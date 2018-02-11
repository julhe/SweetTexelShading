using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Graphs;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;

class ComputeBufferWithData<T>
{
    public T[] data;
    public ComputeBuffer buffer;
}

public class BasicRenderpipeline : RenderPipeline
{
    public static BasicRenderpipeline instance;
    const string PIPELINE_NAME = "BasicRenderpipeline";
    const int MAXIMAL_OBJECTS_PER_VIEW = 512;
    const int SCREEN_MAX_X = 2048, SCREEN_MAX_Y = 2048;
    const int COMPUTE_COVERAGE_TILE_SIZE = 8;
    const int MAX_PRIMITIVES_PER_OBJECT = 8192;
    const int PRIMITIVE_CLUSTER_SIZE = 8;
    private const RenderTextureFormat ATLAS_TEXTUREFORMAT = RenderTextureFormat.ARGB32;
    static readonly ShaderPassName m_TexelSpacePass = new ShaderPassName("Texel Space Pass");
    static readonly ShaderPassName m_VistaPass = new ShaderPassName("Vista Pass");
    static readonly ShaderPassName m_VisibilityPass = new ShaderPassName("Visibility Pass");

    public static Camera CURRENT_CAMERA { get; private set; }
    public static int SCREEN_X, SCREEN_Y;

    RenderTexture g_VistaAtlas_A, g_VistaAtlas_B;
    bool target_atlasA;
    RenderTargetIdentifier g_visibilityBuffer_RT, g_CameraTarget_RT;
    Vector2Int g_visibilityBuffer_dimension;
    const int kCameraDepthBufferBits = 32;

    CameraComparer m_CameraComparer = new CameraComparer();
    TexelSpaceRenderHelperComparer m_RenderHelperComparer = new TexelSpaceRenderHelperComparer();
    readonly int m_cs_ExtractVisibility, g_VisibilityBufferID, g_PrimitiveVisibilityID, 
        m_cs_DebugVisibilityBuffer, m_cs_AtlasPacking, m_cs_CopyDataToPreFrameBuffer, g_CameraTarget, m_cs_MipMapFinalize;
    readonly ComputeShader m_ResolveCS;
    readonly ComputeBuffer g_PrimitiveVisibility, g_ObjectMipMapCounterValue;
    readonly ComputeBuffer g_PrimitiveMipMapLevel, g_PrimitiveLocation;
    struct ObjectToAtlasProperties
    {
        public uint objectID;
        public uint desiredAtlasSpace_axis;
        public Vector4 atlas_ST;
    }

    struct ShadingCluster
    {
        public int clusterID;
        public int mipMapLevel;
        public Vector4 atlasScaleOffset;
    }

    ShadingCluster[] shadingClusters = new ShadingCluster[MAXIMAL_OBJECTS_PER_VIEW * 256]; // objectID is implicid trough first 10bit of the index

    int[] g_PrimitiveVisibility_init;
    ObjectToAtlasProperties[] g_ObjectToAtlasProperties_init;
    BasicRenderpipelineAsset m_asset;
    float timeSinceLastRender = 0f;

    public BasicRenderpipeline(BasicRenderpipelineAsset asset)
    {
        m_asset = asset;
        Shader.globalRenderPipeline = PIPELINE_NAME;
        m_ResolveCS = asset.resolveShader;

        m_cs_ExtractVisibility = m_ResolveCS.FindKernel("ExtractCoverage");
        m_cs_DebugVisibilityBuffer = m_ResolveCS.FindKernel("DebugShowVertexID");
        m_cs_AtlasPacking = m_ResolveCS.FindKernel("AtlasPacking");
        m_cs_CopyDataToPreFrameBuffer = m_ResolveCS.FindKernel("CopyDataToPreFrameBuffer");
        m_cs_MipMapFinalize = m_ResolveCS.FindKernel("MipMapFinalize");

        g_VisibilityBufferID = Shader.PropertyToID("g_VisibilityBuffer");
        g_PrimitiveVisibilityID = Shader.PropertyToID("g_PrimitiveVisibility");
        g_CameraTarget = Shader.PropertyToID("g_CameraTarget");

        g_visibilityBuffer_RT = new RenderTargetIdentifier(g_VisibilityBufferID);

        g_PrimitiveVisibility = new ComputeBuffer((50 * MAX_PRIMITIVES_PER_OBJECT), sizeof(int));
        g_PrimitiveVisibility_init = Enumerable.Repeat(0, g_PrimitiveVisibility.count).ToArray();


        // this value can get pretty large, so 
        int g_Object_MipmapLevelA_size =
            ((SCREEN_MAX_X / COMPUTE_COVERAGE_TILE_SIZE) * (SCREEN_Y / COMPUTE_COVERAGE_TILE_SIZE) * MAXIMAL_OBJECTS_PER_VIEW) / 32;
        g_ObjectMipMapCounterValue = new ComputeBuffer(1, sizeof(int), ComputeBufferType.IndirectArguments);

        g_PrimitiveMipMapLevel = new ComputeBuffer(MAX_PRIMITIVES_PER_OBJECT * 50, sizeof(int));
        g_PrimitiveMipMapLevel.SetData(Enumerable.Repeat(0, g_PrimitiveMipMapLevel.count).ToArray());

        g_PrimitiveLocation = new ComputeBuffer(g_PrimitiveMipMapLevel.count, sizeof(int));
    }

    public override void Dispose()
    {
        base.Dispose();
        instance = null;
        Shader.globalRenderPipeline = "";
        g_PrimitiveVisibility.Release();

        if(g_VistaAtlas_A != null)
            g_VistaAtlas_A.Release();
        if(g_VistaAtlas_B != null)
            g_VistaAtlas_B.Release();

       
    }

    void ValidateAtlasTextureParameters()
    {
        int targetAtlasSize = m_asset.maximalAtlasSizePixel;
        if (g_VistaAtlas_A == null || g_VistaAtlas_A.width != targetAtlasSize)
        {
            CommandBuffer cmd = CommandBufferPool.Get("SetupAtlas");
            if (g_VistaAtlas_A != null)
            {
                g_VistaAtlas_A.Release();
                g_VistaAtlas_B.Release();
            }

            g_VistaAtlas_A = new RenderTexture(targetAtlasSize, targetAtlasSize, 0, ATLAS_TEXTUREFORMAT);
            
            g_VistaAtlas_A.Create();
            g_VistaAtlas_B = new RenderTexture(g_VistaAtlas_A);
            g_VistaAtlas_B.Create();

            cmd.SetRenderTarget(g_VistaAtlas_A);
            cmd.ClearRenderTarget(true, true, Color.grey);

            cmd.SetRenderTarget(g_VistaAtlas_B);
            cmd.ClearRenderTarget(true, true, Color.grey);

            m_context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    CullResults m_CullResults;
    ScriptableRenderContext m_context;

    int skipedFrames = 0;
    public override void Render(ScriptableRenderContext context, Camera[] cameras)
    {
        base.Render(context, cameras);
   
        m_asset.memoryConsumption = 0f;
        instance = this;
        m_context = context;
        bool stereoEnabled = XRSettings.isDeviceActive;
        // Sort cameras array by camera depth
        Array.Sort(cameras, m_CameraComparer);


        SetupShaderGlobals();
        bool shouldUpdateAtlas =  timeSinceLastRender > (1f / m_asset.atlasRefreshFps);

        g_PrimitiveVisibility.SetData(g_PrimitiveVisibility_init);
        foreach (Camera camera in cameras)
        {
            CURRENT_CAMERA = camera;
            SCREEN_X = CURRENT_CAMERA.pixelWidth;
            SCREEN_Y = CURRENT_CAMERA.pixelHeight;

            ScriptableCullingParameters cullingParameters;
            if (!CullResults.GetCullingParameters(CURRENT_CAMERA, stereoEnabled, out cullingParameters))
                continue;
            ClearCameraTarget(Color.clear);
#if UNITY_EDITOR
            // Emit scene view UI
            if (camera.cameraType == CameraType.SceneView)
                ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
#endif

            CullResults.Cull(ref cullingParameters, context, ref m_CullResults);

            context.SetupCameraProperties(CURRENT_CAMERA, stereoEnabled);
        
            ValidateAtlasTextureParameters(); 

            // TODO: reuse uv output to skip rendering objects a third time in VistaPass
            if (shouldUpdateAtlas)
            {
                totalTriangles = 0;
                //Debug.Log(DateTime.Now.ToString("hh.mm.ss.ffffff") + "render" + timeSinceLastRender.ToString());
                target_atlasA = !target_atlasA;
                CopyDataToPreFrameBuffer();
                SetupRenderBuffers();
                RenderVisiblityPass(); // demiter pixel coverage 
                PackAtlas(); // Pack new Atlas
                RenderTexelShading(); // render texel space
                
                ReleaseBuffers();
            }
            visibleObjects.Clear();

            RenderVista(); // render final objects
        }
        if (shouldUpdateAtlas)
        {
            timeSinceLastRender = 0f;
        }

        timeSinceLastRender += Time.deltaTime;

        context.Submit();
        m_asset.memoryConsumption += g_VistaAtlas_A.width * g_VistaAtlas_A.height * (g_VistaAtlas_A.format == RenderTextureFormat.DefaultHDR ? 8 : 4) * 2;
        m_asset.memoryConsumption /= 1024 * 1024;
    }
    void SetupRenderBuffers()
    {
        CommandBuffer cmd = CommandBufferPool.Get("SetupBuffers");
        int screen_x = CURRENT_CAMERA.pixelWidth;
        int screen_y = CURRENT_CAMERA.pixelHeight;
        g_visibilityBuffer_dimension = new Vector2Int(
            Mathf.CeilToInt(screen_x / m_asset.visibilityPassDownscale),
            Mathf.CeilToInt(screen_y / m_asset.visibilityPassDownscale));

        cmd.GetTemporaryRT(g_VisibilityBufferID, g_visibilityBuffer_dimension.x, g_visibilityBuffer_dimension.y, 32, FilterMode.Point, RenderTextureFormat.RInt, RenderTextureReadWrite.Linear, 1);

        cmd.SetRenderTarget(g_visibilityBuffer_RT);
        cmd.ClearRenderTarget(true, true, Color.clear);

        cmd.SetRenderTarget(target_atlasA ? g_VistaAtlas_A : g_VistaAtlas_B);
        if(m_asset.clearAtlasOnRefresh)
        {
            cmd.ClearRenderTarget(true, true, Color.clear);
        }
          
        cmd.SetGlobalTexture("g_VistaAtlas", target_atlasA ? g_VistaAtlas_A : g_VistaAtlas_B);
        cmd.SetGlobalTexture("g_prev_VistaAtlas", target_atlasA ? g_VistaAtlas_B : g_VistaAtlas_A);
        cmd.SetGlobalFloat("g_AtlasSizeExponent", m_asset.maximalAtlasSizeExponent);
        cmd.SetGlobalBuffer("g_PrimitiveVisibility", g_PrimitiveVisibility);
        cmd.SetGlobalBuffer("g_PrimitiveLocation", g_PrimitiveLocation);


        cmd.SetGlobalFloat("g_atlasAxisSize", atlasAxisSize);
        m_context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    void ClearCameraTarget(Color color)
    {
        CommandBuffer cmd = CommandBufferPool.Get("ClearCameraTarget");

        cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        cmd.ClearRenderTarget(true, true, color);
        m_context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    void SetupShaderGlobals()
    {
        CommandBuffer cmd = CommandBufferPool.Get("SetupShaderGlobals");
        cmd.SetGlobalFloat("g_AtlasResolutionScale", m_asset.atlasResolutionScale / m_asset.visibilityPassDownscale);
        float lerpFactor = Mathf.Clamp01(timeSinceLastRender / (1f / m_asset.atlasRefreshFps)); //TODO: clamp should't been neccesary

        cmd.SetGlobalFloat("g_atlasMorph", lerpFactor);

        m_context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }


    void RenderVisiblityPass()
    {
        // VISIBLITY RENDER PASS
        // renders the current view as: objectID, primitveID and mipmap level
        CommandBuffer cmd = CommandBufferPool.Get("RenderTexelCoverage");
        cmd.SetRenderTarget(g_VisibilityBufferID);
        cmd.SetGlobalBuffer("g_PrimitiveMipMapLevel", g_PrimitiveMipMapLevel);
        cmd.SetRandomWriteTarget(1, g_PrimitiveMipMapLevel);
        //g_vertexIDVisiblity_B.SetData(g_vertexIDVisiblity_B_init);
        m_context.ExecuteCommandBuffer(cmd);

        RenderOpaque(m_VisibilityPass, SortFlags.OptimizeStateChanges);

        cmd.Clear();
        cmd.ClearRandomWriteTargets();

        // VISIBLITY DISSOLVE PASS
        // maps the previous rendered data into usable buffers
        cmd.SetComputeTextureParam(m_ResolveCS, m_cs_ExtractVisibility, g_VisibilityBufferID, g_visibilityBuffer_RT);
        cmd.SetComputeBufferParam(m_ResolveCS, m_cs_ExtractVisibility, g_PrimitiveVisibilityID, g_PrimitiveVisibility);
        cmd.SetComputeBufferParam(m_ResolveCS, m_cs_ExtractVisibility, "g_PrimitiveMipMapLevel", g_PrimitiveMipMapLevel);
        //cmd.SetComputeBufferParam(m_ResolveCS, m_cs_ExtractVisibility, "g_ObjectToAtlasProperties", g_ObjectToAtlasProperties);
        // g_Object_MipmapLevelA.SetCounterValue(0);
        //cmd.SetComputeBufferParam(m_ResolveCS, m_cs_ExtractVisibility, "g_ObjectMipMap_append", g_Object_MipmapLevelA);
        cmd.DispatchCompute(m_ResolveCS, m_cs_ExtractVisibility, SCREEN_X / COMPUTE_COVERAGE_TILE_SIZE, SCREEN_Y / COMPUTE_COVERAGE_TILE_SIZE, 1);

       // cmd.CopyCounterValue(g_Object_MipmapLevelA, g_ObjectMipMapCounterValue, 0);
        //cmd.SetComputeBufferParam(m_ResolveCS, m_cs_MipMapFinalize, "g_ObjectMipMap_consume", g_Object_MipmapLevelA);
        //cmd.SetComputeBufferParam(m_ResolveCS, m_cs_MipMapFinalize, "g_ObjectToAtlasProperties", g_ObjectToAtlasProperties);
        //cmd.SetComputeBufferParam(m_ResolveCS, m_cs_MipMapFinalize, "g_ObjectMipMapCounterValue", g_ObjectMipMapCounterValue);
       // cmd.DispatchCompute(m_ResolveCS, m_cs_MipMapFinalize, 1, 1, 1);

        m_context.ExecuteCommandBuffer(cmd);

        cmd.Clear();
        // optional debug pass
        switch (m_asset.debugPass)
        {
            case TexelSpaceDebugMode.VisibilityPassObjectID:
            case TexelSpaceDebugMode.VisibilityPassPrimitivID:
            case TexelSpaceDebugMode.VisibilityPassMipMapPerObject:
            case TexelSpaceDebugMode.VisibilityPassMipMapPerPixel:


                int debugView = Shader.PropertyToID("g_DebugTexture");
                cmd.GetTemporaryRT(debugView, SCREEN_X, SCREEN_Y, 16, FilterMode.Point, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, 1, true);
                cmd.SetComputeTextureParam(m_ResolveCS, m_cs_DebugVisibilityBuffer, g_VisibilityBufferID, g_visibilityBuffer_RT);
                cmd.SetComputeTextureParam(m_ResolveCS, m_cs_DebugVisibilityBuffer, "g_DebugTexture", debugView);
                cmd.SetComputeBufferParam(m_ResolveCS, m_cs_DebugVisibilityBuffer, "g_PrimitiveMipMapLevel", g_PrimitiveMipMapLevel);
                cmd.SetComputeIntParam(m_ResolveCS, "g_DebugPassID", (int)m_asset.debugPass);
                cmd.DispatchCompute(
                    m_ResolveCS,
                    m_cs_DebugVisibilityBuffer,
                    SCREEN_X / 8,
                    SCREEN_Y / 8,
                    1);

                cmd.Blit(debugView, BuiltinRenderTextureType.CameraTarget);
                cmd.ReleaseTemporaryRT(debugView);

                m_context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                break;
            default:
                break;
        }
        CommandBufferPool.Release(cmd);
    }

    const RendererConfiguration rendererConfiguration_shading = 
        RendererConfiguration.PerObjectLightIndices8 | RendererConfiguration.PerObjectLightmaps | RendererConfiguration.PerObjectLightProbe | RendererConfiguration.PerObjectReflectionProbes | RendererConfiguration.ProvideLightIndices;
    void RenderTexelShading()
    {
        CommandBuffer cmd = CommandBufferPool.Get("RenderTexelShading");
        cmd.SetRenderTarget(target_atlasA ? g_VistaAtlas_A : g_VistaAtlas_B);
        cmd.SetGlobalBuffer(g_PrimitiveVisibilityID, g_PrimitiveVisibility);
       
       // cmd.SetGlobalBuffer("g_prev_ObjectToAtlasProperties", g_prev_ObjectToAtlasProperties);
        m_context.ExecuteCommandBuffer(cmd);

        RenderOpaque(m_TexelSpacePass, SortFlags.OptimizeStateChanges, rendererConfiguration_shading);

        cmd.Clear();
        if (m_asset.debugPass == TexelSpaceDebugMode.TexelShadingPass)
        {
            cmd.Blit(g_VistaAtlas_A, BuiltinRenderTextureType.CameraTarget);
            m_context.ExecuteCommandBuffer(cmd);
        }
        CommandBufferPool.Release(cmd);

    }

    void CopyDataToPreFrameBuffer()
    {
        CommandBuffer cmd = CommandBufferPool.Get("CopyDataToPreFrameBuffer");

        //cmd.SetComputeBufferParam(m_ResolveCS, m_cs_CopyDataToPreFrameBuffer, "g_ObjectToAtlasProperties", g_ObjectToAtlasProperties);
        //cmd.SetComputeBufferParam(m_ResolveCS, m_cs_CopyDataToPreFrameBuffer, "g_prev_ObjectToAtlasProperties", g_prev_ObjectToAtlasProperties);
        int singleComputeRow = COMPUTE_COVERAGE_TILE_SIZE * COMPUTE_COVERAGE_TILE_SIZE;
        cmd.DispatchCompute(m_ResolveCS, m_cs_CopyDataToPreFrameBuffer, Mathf.CeilToInt(MAXIMAL_OBJECTS_PER_VIEW / singleComputeRow), 1, 1);

        m_context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    void RenderVista()
    {
        if (m_asset.debugPass == TexelSpaceDebugMode.None)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Render Vista");
            cmd.GetTemporaryRT(g_CameraTarget, SCREEN_X, SCREEN_Y, 24, FilterMode.Bilinear, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, Mathf.NextPowerOfTwo(m_asset.MSSALevel));
            cmd.SetRenderTarget(g_CameraTarget);
            cmd.ClearRenderTarget(true, true, Color.clear);
            m_context.ExecuteCommandBuffer(cmd);

            RenderOpaque(m_VistaPass, SortFlags.CommonOpaque);  // render
            m_context.DrawSkybox(CURRENT_CAMERA);

            cmd.Clear();
            cmd.Blit(g_CameraTarget, BuiltinRenderTextureType.CameraTarget);
            cmd.ReleaseTemporaryRT(g_CameraTarget);
            m_context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    void RenderOpaque(ShaderPassName passName, SortFlags sortFlags, RendererConfiguration rendererConfiguration = RendererConfiguration.None)
    {
        var opaqueDrawSettings = new DrawRendererSettings(CURRENT_CAMERA, passName);
        opaqueDrawSettings.sorting.flags = sortFlags;
        opaqueDrawSettings.rendererConfiguration = rendererConfiguration;
        
        var opaqueFilterSettings = new FilterRenderersSettings(true)
        {
            renderQueueRange = RenderQueueRange.all
        };

        m_context.DrawRenderers(m_CullResults.visibleRenderers, ref opaqueDrawSettings, opaqueFilterSettings);
    }
    void ReleaseBuffers()
    {
        CommandBuffer cmd = CommandBufferPool.Get("ReleaseBuffers");
        cmd.ReleaseTemporaryRT(g_PrimitiveVisibilityID);
        m_context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    // Atlas packing 
    List<TexelSpaceRenderHelper> visibleObjects = new List<TexelSpaceRenderHelper>();
    public void AddObject(TexelSpaceRenderHelper texelSpaceRenderHelper)
    {
        visibleObjects.Add(texelSpaceRenderHelper);
    }

    public const int ATLAS_TILE_SIZE = 128;
    int atlasAxisSize;

    //TODO: reuse depthbuffer from visibility pass for vista pass
    //TODO: check if atlas is acutally large enough
    private int totalTriangles = 0;
    private int[] objectTriangleOffsets = new int[MAXIMAL_OBJECTS_PER_VIEW];
    void PackAtlas()
    {
        CommandBuffer cmd = CommandBufferPool.Get("PackAtlas");
        atlasAxisSize = m_asset.maximalAtlasSizePixel;

        for (int i = 0; i < visibleObjects.Count && i < objectTriangleOffsets.Length + 1; i++)
        {
            int triangleCount;
            visibleObjects[i].SetAtlasProperties(i + 1, out triangleCount); //objectID 0 is reserved for "undefined"
            objectTriangleOffsets[i + 1] = totalTriangles;

            totalTriangles += triangleCount;
        }
        //g_ObjectToAtlasProperties.SetData(g_ObjectToAtlasProperties_data);
        cmd.SetComputeIntParam(m_ResolveCS, "g_totalObjectsInView", visibleObjects.Count + 1);
        cmd.SetComputeIntParam(m_ResolveCS, "g_atlasAxisSize", atlasAxisSize);
        cmd.SetComputeIntParam(m_ResolveCS, "g_totalTriangles", totalTriangles);
        cmd.SetComputeBufferParam(m_ResolveCS, m_cs_AtlasPacking, "g_PrimitiveLocation", g_PrimitiveLocation);
        cmd.SetComputeBufferParam(m_ResolveCS, m_cs_AtlasPacking, "g_PrimitiveMipMapLevel", g_PrimitiveMipMapLevel);
        cmd.SetComputeBufferParam(m_ResolveCS, m_cs_AtlasPacking, "g_PrimitiveVisibility", g_PrimitiveVisibility);

        //cmd.SetComputeBufferParam(m_ResolveCS, m_cs_AtlasPacking, "g_ObjectToAtlasProperties", g_ObjectToAtlasProperties);  

        cmd.DispatchCompute(m_ResolveCS, m_cs_AtlasPacking, 1, 1, 1);

        visibleObjects.Clear();
        m_context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }
}

public enum TexelSpaceDebugMode
{
    None = 0,
    VisibilityPassObjectID,
    VisibilityPassPrimitivID,
    VisibilityPassMipMapPerObject,
    VisibilityPassMipMapPerPixel,
    TexelShadingPass,
}

public class CameraComparer : IComparer<Camera>
{
    public int Compare(Camera lhs, Camera rhs)
    {
        return (int)(rhs.depth - lhs.depth);
    }
}