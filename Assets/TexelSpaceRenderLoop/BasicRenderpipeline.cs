using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    static readonly ShaderPassName m_TexelSpacePass = new ShaderPassName("Texel Space Pass");
    static readonly ShaderPassName m_VistaPass = new ShaderPassName("Vista Pass");
    static readonly ShaderPassName m_CoveragePass = new ShaderPassName("Coverage Pass");

    public static Camera CURRENT_CAMERA { get; private set; }
    public static int SCREEN_X, SCREEN_Y;

    RenderTexture g_VistaAtlas_A, g_VistaAtlas_B;
    bool target_atlasA;
    RenderTargetIdentifier g_ScreenVertexID_RT, g_CameraTarget_RT;
    Vector2Int g_ScreenVertexID_dimension;
    const int kCameraDepthBufferBits = 32;

    CameraComparer m_CameraComparer = new CameraComparer();
    TexelSpaceRenderHelperComparer m_RenderHelperComparer = new TexelSpaceRenderHelperComparer();
    int m_cs_CoverageToBuffer, g_ScreenVertexID, g_vertexIDVisiblity, m_cs_DebugCoverage, m_cs_AtlasPacking, m_cs_CopyDataToPreFrameBuffer, g_dummyRT;
    ComputeShader m_ResolveCS;
    ComputeBuffer g_vertexIDVisiblity_B, g_ObjectToAtlasProperties, g_prev_ObjectToAtlasProperties;
    struct ObjectToAtlasProperties
    {
        public uint objectID;
        public uint desiredAtlasSpace_axis;
        public Vector4 atlas_ST;
    }


    int[] g_vertexIDVisiblity_B_init;
    ObjectToAtlasProperties[] g_ObjectToAtlasProperties_init;
    BasicRenderpipelineAsset m_asset;
    float timeSinceLastRender = 0f;
    Material fullscreenBlitMat;
    public BasicRenderpipeline(BasicRenderpipelineAsset asset)
    {
        m_asset = asset;
        Shader.globalRenderPipeline = PIPELINE_NAME;
        fullscreenBlitMat = new Material(m_asset.resolveBlitShader);
        fullscreenBlitMat.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
        m_ResolveCS = asset.resolveShader;

        g_dummyRT = Shader.PropertyToID("g_dummyRT");
        m_cs_CoverageToBuffer = m_ResolveCS.FindKernel("ExtractCoverage");
        m_cs_DebugCoverage = m_ResolveCS.FindKernel("DebugShowVertexID");
        m_cs_AtlasPacking = m_ResolveCS.FindKernel("AtlasPacking");
        m_cs_CopyDataToPreFrameBuffer = m_ResolveCS.FindKernel("CopyDataToPreFrameBuffer");

        g_ScreenVertexID = Shader.PropertyToID("g_ScreenVertexID");
        g_vertexIDVisiblity = Shader.PropertyToID("g_VertexIDVisiblity");

        g_ScreenVertexID_RT = new RenderTargetIdentifier(g_ScreenVertexID);

        g_vertexIDVisiblity_B = new ComputeBuffer((32 * MAX_PRIMITIVES_PER_OBJECT), sizeof(int));
        g_vertexIDVisiblity_B_init = Enumerable.Repeat(0, g_vertexIDVisiblity_B.count).ToArray();

        g_ObjectToAtlasProperties = new ComputeBuffer(MAXIMAL_OBJECTS_PER_VIEW, sizeof(uint) + sizeof(uint) + sizeof(float) * 4);
        g_prev_ObjectToAtlasProperties = new ComputeBuffer(g_ObjectToAtlasProperties.count, g_ObjectToAtlasProperties.stride);

        g_ObjectToAtlasProperties_init = Enumerable.Repeat(new ObjectToAtlasProperties() { atlas_ST = Vector4.zero, desiredAtlasSpace_axis = 0, objectID = 0 }, g_ObjectToAtlasProperties.count).ToArray();
        g_ObjectToAtlasProperties.SetData(g_ObjectToAtlasProperties_init);
        g_prev_ObjectToAtlasProperties.SetData(g_ObjectToAtlasProperties_init);
    }

    public override void Dispose()
    {
        base.Dispose();
        instance = null;
        Shader.globalRenderPipeline = "";
        g_vertexIDVisiblity_B.Release();
        g_ObjectToAtlasProperties.Release();
        g_prev_ObjectToAtlasProperties.Release();
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

            g_VistaAtlas_A = new RenderTexture(targetAtlasSize, targetAtlasSize, 0, RenderTextureFormat.RGB111110Float, RenderTextureReadWrite.Linear);
            
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

    void ValidateScreenDependentBuffers()
    {
       
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

        g_vertexIDVisiblity_B.SetData(g_vertexIDVisiblity_B_init);
        foreach (Camera camera in cameras)
        {
            CURRENT_CAMERA = camera;
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
                //Debug.Log(DateTime.Now.ToString("hh.mm.ss.ffffff") + "render" + timeSinceLastRender.ToString());
                target_atlasA = !target_atlasA;
                CopyDataToPreFrameBuffer();
                SetupRenderBuffers();
                RenderTexelCoverage(); // demiter pixel coverage 
                PackAtlas(); // Pack new Atlas
                RenderTexelShading(); // render texel space
                
                ReleaseBuffers();
            }
            visibleObjects.Clear();

            RenderVista(); // render final objects

            if (m_asset.debugPass == TexelSpacePass.None)
            {
                context.DrawSkybox(CURRENT_CAMERA);
            }

            //context.DrawSkybox(m_CurrCamera);
        
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
        g_ScreenVertexID_dimension = new Vector2Int(
            Mathf.CeilToInt(screen_x / m_asset.visibilityPassDownscale),
            Mathf.CeilToInt(screen_y / m_asset.visibilityPassDownscale));

        cmd.GetTemporaryRT(g_ScreenVertexID, g_ScreenVertexID_dimension.x, g_ScreenVertexID_dimension.y, 16, FilterMode.Point, RenderTextureFormat.RInt, RenderTextureReadWrite.Linear, 1);

        cmd.SetRenderTarget(g_ScreenVertexID_RT);
        cmd.ClearRenderTarget(true, true, Color.clear);

        cmd.SetRenderTarget(target_atlasA ? g_VistaAtlas_A : g_VistaAtlas_B);
        if(m_asset.clearAtlasOnRefresh)
        {
            cmd.ClearRenderTarget(true, true, Color.black);
        }
          
        cmd.SetGlobalTexture("g_VistaAtlas", target_atlasA ? g_VistaAtlas_A : g_VistaAtlas_B);
        cmd.SetGlobalTexture("g_prev_VistaAtlas", target_atlasA ? g_VistaAtlas_B : g_VistaAtlas_A);

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


    void RenderTexelCoverage()
    {

        int screen_x = CURRENT_CAMERA.pixelWidth;
        int screen_y = CURRENT_CAMERA.pixelHeight;
        // COVERAGE RENDER PASS
        // renders the current view as: objectID, primitveID and mipmap level
        CommandBuffer cmd = CommandBufferPool.Get("RenderTexelCoverage");
        cmd.SetRenderTarget(g_ScreenVertexID);
        cmd.SetGlobalBuffer("g_ObjectToAtlasPropertiesRW", g_ObjectToAtlasProperties);
        cmd.SetRandomWriteTarget(1, g_ObjectToAtlasProperties);

        //g_vertexIDVisiblity_B.SetData(g_vertexIDVisiblity_B_init);
        m_context.ExecuteCommandBuffer(cmd);

        RenderOpaque(m_CoveragePass, SortFlags.OptimizeStateChanges);

        cmd.Clear();
        cmd.ClearRandomWriteTargets();



        //cmd.SetGlobalBuffer("g_ObjectToAtlasProperties", g_ObjectToAtlasProperties);
        //cmd.SetGlobalBuffer("g_VertexIDVisiblity", g_vertexIDVisiblity_B);
        //cmd.SetRandomWriteTarget(1, g_ObjectToAtlasProperties);
        //cmd.SetRandomWriteTarget(2, g_vertexIDVisiblity_B);
        //cmd.GetTemporaryRT(g_dummyRT, screen_x, screen_y, 0);

        //cmd.Blit(g_ScreenVertexID_RT, g_dummyRT, fullscreenBlitMat);
        //cmd.ClearRandomWriteTargets();

        //if (m_asset.debugPass == TexelSpacePass.TexelScreenCoverage)
        //{
        //    cmd.Blit(g_dummyRT, BuiltinRenderTextureType.CameraTarget);
        //}

        // cmd.ReleaseTemporaryRT(g_dummyRT);

        // COVERAGE DISSOLVE PASS
        // maps the previous rendered data into usable buffers
        cmd.SetComputeTextureParam(m_ResolveCS, m_cs_CoverageToBuffer, g_ScreenVertexID, g_ScreenVertexID_RT);
        cmd.SetComputeBufferParam(m_ResolveCS, m_cs_CoverageToBuffer, "g_VertexIDVisiblity", g_vertexIDVisiblity_B);
        cmd.SetComputeBufferParam(m_ResolveCS, m_cs_CoverageToBuffer, "g_ObjectToAtlasProperties", g_ObjectToAtlasProperties);

        cmd.DispatchCompute(m_ResolveCS, m_cs_CoverageToBuffer, screen_x / COMPUTE_COVERAGE_TILE_SIZE, screen_y / COMPUTE_COVERAGE_TILE_SIZE, 1);

        m_context.ExecuteCommandBuffer(cmd);

        cmd.Clear();
        if (m_asset.debugPass == TexelSpacePass.TexelScreenCoverage)
        {
            int debugView = Shader.PropertyToID("g_DebugTexture");
            cmd.GetTemporaryRT(debugView, screen_x, screen_y, 16, FilterMode.Point, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, 1, true);
            cmd.SetComputeTextureParam(m_ResolveCS, m_cs_DebugCoverage, g_ScreenVertexID, g_ScreenVertexID_RT);
            cmd.SetComputeTextureParam(m_ResolveCS, m_cs_DebugCoverage, "g_DebugTexture", debugView);
            cmd.SetComputeBufferParam(m_ResolveCS, m_cs_DebugCoverage, "g_VertexIDVisiblity", g_vertexIDVisiblity_B);
            cmd.SetComputeBufferParam(m_ResolveCS, m_cs_DebugCoverage, "g_ObjectToAtlasPropertiesR", g_ObjectToAtlasProperties);
            cmd.DispatchCompute(
                m_ResolveCS,
                m_cs_DebugCoverage,
                screen_x / 8,
                screen_y / 8,
                1);

            cmd.Blit(debugView, BuiltinRenderTextureType.CameraTarget);
            cmd.ReleaseTemporaryRT(debugView);
            m_context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
        }

        CommandBufferPool.Release(cmd);
    }

    const RendererConfiguration rendererConfiguration_shading = 
        RendererConfiguration.PerObjectLightIndices8 | RendererConfiguration.PerObjectLightmaps | RendererConfiguration.PerObjectLightProbe | RendererConfiguration.PerObjectReflectionProbes | RendererConfiguration.ProvideLightIndices;
    void RenderTexelShading()
    {
        CommandBuffer cmd = CommandBufferPool.Get("RenderTexelShading");
        cmd.SetRenderTarget(target_atlasA ? g_VistaAtlas_A : g_VistaAtlas_B);
        cmd.SetGlobalBuffer(g_vertexIDVisiblity, g_vertexIDVisiblity_B);
        cmd.SetGlobalBuffer("g_ObjectToAtlasProperties", g_ObjectToAtlasProperties);
        cmd.SetGlobalBuffer("g_prev_ObjectToAtlasProperties", g_prev_ObjectToAtlasProperties);
        m_context.ExecuteCommandBuffer(cmd);

        RenderOpaque(m_TexelSpacePass, SortFlags.OptimizeStateChanges, rendererConfiguration_shading);

        cmd.Clear();
        if (m_asset.debugPass == TexelSpacePass.TexelSpaceShading)
        {
            cmd.Blit(g_VistaAtlas_A, BuiltinRenderTextureType.CameraTarget);
            m_context.ExecuteCommandBuffer(cmd);
        }
        CommandBufferPool.Release(cmd);

    }

    void CopyDataToPreFrameBuffer()
    {
        CommandBuffer cmd = CommandBufferPool.Get("CopyDataToPreFrameBuffer");

        cmd.SetComputeBufferParam(m_ResolveCS, m_cs_CopyDataToPreFrameBuffer, "g_ObjectToAtlasProperties", g_ObjectToAtlasProperties);
        cmd.SetComputeBufferParam(m_ResolveCS, m_cs_CopyDataToPreFrameBuffer, "g_prev_ObjectToAtlasProperties", g_prev_ObjectToAtlasProperties);
        cmd.SetRandomWriteTarget(0, g_ObjectToAtlasProperties);
        cmd.SetRandomWriteTarget(1, g_prev_ObjectToAtlasProperties);
        cmd.DispatchCompute(m_ResolveCS, m_cs_CopyDataToPreFrameBuffer, 1, 1, 1);
        cmd.ClearRandomWriteTargets();
        m_context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    void RenderVista()
    {
        CommandBuffer cmd = CommandBufferPool.Get("Render Vista");

        // setup globals
        cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);

        m_context.ExecuteCommandBuffer(cmd); // apply globals

        cmd.Clear();
        if (m_asset.debugPass == TexelSpacePass.None)
        {
            RenderOpaque(m_VistaPass, SortFlags.CommonOpaque);  // render
        }

        m_context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);


    }
    //TODO: BROKEN
    void RenderForwardFallback()
    {
        CommandBuffer cmd = CommandBufferPool.Get("Render ForwardFallback");

        // setup globals
        cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        m_context.ExecuteCommandBuffer(cmd); // apply globals

        cmd.Clear();
        if (m_asset.debugPass == TexelSpacePass.None)
        {
            RenderOpaque(new ShaderPassName(), SortFlags.CommonOpaque);  // render
        }

        CommandBufferPool.Release(cmd);

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
        cmd.ReleaseTemporaryRT(g_vertexIDVisiblity);
        m_context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    // Atlas packing - should be moved on gpu some day
    List<TexelSpaceRenderHelper> visibleObjects = new List<TexelSpaceRenderHelper>();
    public void AddObject(TexelSpaceRenderHelper texelSpaceRenderHelper)
    {
        visibleObjects.Add(texelSpaceRenderHelper);
    }

    public const int ATLAS_TILE_SIZE = 128;
    int atlasAxisSize;

    //TODO: reuse depthbuffer from visibility pass for vista pass
    //TODO: check if atlas is acutally large enough
    void PackAtlas()
    {
        CommandBuffer cmd = CommandBufferPool.Get("PackAtlas");
        atlasAxisSize = m_asset.maximalAtlasSizePixel;


        for (int i = 0; i < visibleObjects.Count; i++)
        {
            visibleObjects[i].SetAtlasProperties(Vector4.zero, i);
            g_ObjectToAtlasProperties_init[i].desiredAtlasSpace_axis = (uint)visibleObjects[i].atlasSize;
        }
        //g_ObjectToAtlasProperties.SetData(g_ObjectToAtlasProperties_data);
        cmd.SetComputeIntParam(m_ResolveCS, "g_totalObjectsInView", visibleObjects.Count);
        cmd.SetComputeIntParam(m_ResolveCS, "g_atlasAxisSize", atlasAxisSize);
        cmd.SetComputeIntParam(m_ResolveCS, "atlas_sliceCount", 0);

        cmd.SetComputeBufferParam(m_ResolveCS, m_cs_AtlasPacking, "g_ObjectToAtlasProperties", g_ObjectToAtlasProperties);  
        cmd.SetComputeBufferParam(m_ResolveCS, m_cs_AtlasPacking, "g_prev_ObjectToAtlasProperties", g_prev_ObjectToAtlasProperties);

        cmd.DispatchCompute(m_ResolveCS, m_cs_AtlasPacking, 1, 1, 1);

#if CPU_ATLAS_PACKING
        int atlasTotalTiles = atlasAxisSize / ATLAS_TILE_SIZE;
        atlasTotalTiles *= atlasTotalTiles;
        int atlasCurrentlyUsedTiles = 0;

        int objectIndexCounter = 0;
        atlas_debug.Clear();
        foreach (TexelSpaceRenderHelper visibleObject in visibleObjects)
        {
            int objectTilesAxis = visibleObject.atlasSize / ATLAS_TILE_SIZE;
            int objectTilesTotal = objectTilesAxis * objectTilesAxis;

            Vector4 atlasRect = GetTextureBound(atlasCurrentlyUsedTiles, objectTilesAxis);
            visibleObject.SetAtlasProperties(GetUVToAtlasScaleOffset(atlasRect), objectIndexCounter);
     
            g_atlasUVMappings_data[objectIndexCounter] = GetUVToAtlasScaleOffset(atlasRect);

            atlasCurrentlyUsedTiles += objectTilesTotal;
            objectIndexCounter++;
        }
        //g_atlasUVMappings.SetData(g_atlasUVMappings_data);
        visibleObjects.Clear();
#endif
        visibleObjects.Clear();
        //Debug.Assert(HardValidateAtlas(atlas_debug), "One (or more) textures overlap in the atlas!");
        m_context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

}

public enum TexelSpacePass
{
    None = 0,
    TexelScreenCoverage,
    TexelSpaceShading,
    PresentTexel
}

public class CameraComparer : IComparer<Camera>
{
    public int Compare(Camera lhs, Camera rhs)
    {
        return (int)(rhs.depth - lhs.depth);
    }
}