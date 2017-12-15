using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;


public class CompactDeferred : RenderPipeline
{
    public static CompactDeferred instance;
    const string PIPELINE_NAME = "BasicRenderpipeline";
    const int MAXIMAL_OBJECTS_PER_VIEW = 512;
    const int SCREEN_MAX_X = 2048, SCREEN_MAX_Y = 2048;
    const int COMPUTE_COVERAGE_TILE_SIZE = 8;
    const int MAX_PRIMITIVES_PER_OBJECT = 8192;
    const int PRIMITIVE_CLUSTER_SIZE = 8;
    static readonly ShaderPassName m_TexelSpacePass = new ShaderPassName("Texel Space Pass");
    static readonly ShaderPassName m_VistaPass = new ShaderPassName("Vista Pass");
    static readonly ShaderPassName m_VisibilityPass = new ShaderPassName("Visibility Pass");
    static readonly ShaderPassName m_GBufferPass = new ShaderPassName("GBuffer");
    static readonly ShaderPassName m_PosPass = new ShaderPassName("PosPrepass");
    public static Camera CURRENT_CAMERA { get; private set; }
    public static int SCREEN_X, SCREEN_Y;

    RenderTexture g_VistaAtlas_A, g_VistaAtlas_B;
    bool target_atlasA;
    RenderTargetIdentifier g_BufferRT, g_CameraTarget_RT, g_PosBufferRT;
    Vector2Int g_visibilityBuffer_dimension;
    const int kCameraDepthBufferBits = 32;

    CameraComparer m_CameraComparer = new CameraComparer();
    int m_cs_ExtractVisibility, g_GBuffer, g_PosBuffer, g_PrimitiveVisibilityID, m_cs_DebugVisibilityBuffer, m_cs_AtlasPacking, m_cs_CopyDataToPreFrameBuffer, g_dummyRT;
    ComputeShader m_ResolveCS;
    ComputeBuffer g_PrimitiveVisibility, g_ObjectToAtlasProperties, g_prev_ObjectToAtlasProperties;
    struct ObjectToAtlasProperties
    {
        public uint objectID;
        public uint desiredAtlasSpace_axis;
        public Vector4 atlas_ST;
    }


    int[] g_PrimitiveVisibility_init;
    ObjectToAtlasProperties[] g_ObjectToAtlasProperties_init;
    CompactDeferredAsset m_asset;
    float timeSinceLastRender = 0f;

    public CompactDeferred(CompactDeferredAsset asset)
    {
        m_asset = asset;
        Shader.globalRenderPipeline = PIPELINE_NAME;
        m_ResolveCS = asset.resolveShader;
        fullscreenMat = new Material(m_asset.resolveBlitShader);

        g_dummyRT = Shader.PropertyToID("g_dummyRT");
        //m_cs_ExtractVisibility = m_ResolveCS.FindKernel("ExtractCoverage");
        //m_cs_DebugVisibilityBuffer = m_ResolveCS.FindKernel("DebugShowVertexID");
        //m_cs_AtlasPacking = m_ResolveCS.FindKernel("AtlasPacking");
        //m_cs_CopyDataToPreFrameBuffer = m_ResolveCS.FindKernel("CopyDataToPreFrameBuffer");

        g_GBuffer = Shader.PropertyToID("g_GBuffer");
        g_PosBuffer = Shader.PropertyToID("g_PosBuffer");
        g_PrimitiveVisibilityID = Shader.PropertyToID("g_PrimitiveVisibility");

        g_BufferRT = new RenderTargetIdentifier(g_GBuffer);
        g_PosBufferRT = new RenderTargetIdentifier(g_PosBuffer);

        g_PrimitiveVisibility = new ComputeBuffer((32 * MAX_PRIMITIVES_PER_OBJECT), sizeof(int));
        g_PrimitiveVisibility_init = Enumerable.Repeat(0, g_PrimitiveVisibility.count).ToArray();

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
        g_PrimitiveVisibility.Release();
        g_ObjectToAtlasProperties.Release();
        g_prev_ObjectToAtlasProperties.Release();
        if (g_VistaAtlas_A != null)
            g_VistaAtlas_A.Release();
        if (g_VistaAtlas_B != null)
            g_VistaAtlas_B.Release();


    }

    CullResults m_CullResults;
    ScriptableRenderContext m_context;
    Material fullscreenMat;
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

        bool shouldUpdateAtlas = timeSinceLastRender > (1f / m_asset.atlasRefreshFps);

        g_PrimitiveVisibility.SetData(g_PrimitiveVisibility_init);
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
            RenderGBuffer();
            Shade();
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

    void ClearCameraTarget(Color color)
    {
        CommandBuffer cmd = CommandBufferPool.Get("ClearCameraTarget");

        cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        cmd.ClearRenderTarget(true, true, color);
        m_context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    void RenderGBuffer()
    {
        CommandBuffer cmd = CommandBufferPool.Get("Lit");
        int screen_x = CURRENT_CAMERA.pixelWidth;
        int screen_y = CURRENT_CAMERA.pixelHeight;
        cmd.SetGlobalTexture("g_Dither", m_asset.dither);
        cmd.GetTemporaryRT(g_GBuffer, screen_x, screen_y, 32, FilterMode.Point, RenderTextureFormat.RInt);
        cmd.SetRenderTarget(g_GBuffer);
        cmd.ClearRenderTarget(true, true, Color.clear);
        m_context.ExecuteCommandBuffer(cmd);

        RenderOpaque(m_GBufferPass, SortFlags.CommonOpaque);
        CommandBufferPool.Release(cmd);
    }

    void Shade()
    {
        CommandBuffer cmd = CommandBufferPool.Get("Shade");
        // shade
        cmd.SetGlobalTexture(g_PosBuffer, g_PosBufferRT);

        cmd.Blit(g_BufferRT, BuiltinRenderTextureType.CameraTarget, fullscreenMat);
        cmd.ReleaseTemporaryRT(g_PosBuffer);
        cmd.ReleaseTemporaryRT(g_GBuffer);

        m_context.ExecuteCommandBuffer(cmd);

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
        cmd.ReleaseTemporaryRT(g_PrimitiveVisibilityID);
        m_context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }
}

