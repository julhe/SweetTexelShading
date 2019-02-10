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
    private static CullResults CURRENT_CULLRESULTS;
    public static int SCREEN_X, SCREEN_Y;

    private ClusteredLightning clusteredLightning;
    RenderTexture g_VistaAtlas_A, g_VistaAtlas_B;
    bool target_atlasA;
    RenderTargetIdentifier g_Buffer0RT, g_Buffer1RT, g_Buffer2RT, g_Buffer3RT, g_CameraTarget_RT, g_PosBufferRT;
    Vector2Int g_visibilityBuffer_dimension;
    const int kCameraDepthBufferBits = 32;

    CameraComparer m_CameraComparer = new CameraComparer();
    int m_cs_ExtractVisibility, g_GBuffer0, g_GBuffer1, g_GBuffer2, g_GBuffer3, g_Depth, g_dummyRT, g_intermediate;

    CompactDeferredAsset m_asset;
    float timeSinceLastRender = 0f;

    private int frameCounter = 0;
    public CompactDeferred(CompactDeferredAsset asset)
    {
        m_asset = asset;
        Shader.globalRenderPipeline = PIPELINE_NAME;


        g_dummyRT = Shader.PropertyToID("g_dummyRT");
        //m_cs_ExtractVisibility = m_ResolveCS.FindKernel("ExtractCoverage");
        //m_cs_DebugVisibilityBuffer = m_ResolveCS.FindKernel("DebugShowVertexID");
        //m_cs_AtlasPacking = m_ResolveCS.FindKernel("AtlasPacking");
        //m_cs_CopyDataToPreFrameBuffer = m_ResolveCS.FindKernel("CopyDataToPreFrameBuffer");

        g_GBuffer0 = Shader.PropertyToID("g_GBuffer0");
        g_GBuffer1 = Shader.PropertyToID("g_GBuffer1");
        g_GBuffer2 = Shader.PropertyToID("g_GBuffer2");
        g_GBuffer3 = Shader.PropertyToID("g_GBuffer3");
        //g_PosBuffer = Shader.PropertyToID("g_PosBuffer");
        g_Depth = Shader.PropertyToID("g_Depth");

        g_Buffer0RT = new RenderTargetIdentifier(g_GBuffer0);
        g_Buffer1RT = new RenderTargetIdentifier(g_GBuffer1);
        g_Buffer2RT = new RenderTargetIdentifier(g_GBuffer2);
        g_Buffer3RT = new RenderTargetIdentifier(g_GBuffer3);

        clusteredLightning = new ClusteredLightning(m_asset.clusteredLightning);
    }

    public override void Dispose()
    {
        base.Dispose();
        clusteredLightning.Dispose();
        instance = null;
        Shader.globalRenderPipeline = "";

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
        if (fullscreenMat == null)
        {
            fullscreenMat = new Material(m_asset.resolveBlitShader);

        }
        m_asset.memoryConsumption = 0f;

        instance = this;
        m_context = context;
        bool stereoEnabled = XRSettings.isDeviceActive;
        // Sort cameras array by camera depth
        Array.Sort(cameras, m_CameraComparer);
        clusteredLightning.SetParameters(m_asset.froxelsX, m_asset.froxelsY, m_asset.froxelsZ);

        foreach (Camera camera in cameras)
        {
            CURRENT_CAMERA = camera;
            ScriptableCullingParameters cullingParameters;
            if (!CullResults.GetCullingParameters(CURRENT_CAMERA, stereoEnabled, out cullingParameters))
                continue;

            CullResults.Cull(ref cullingParameters, context, ref m_CullResults);
            context.SetupCameraProperties(CURRENT_CAMERA, stereoEnabled);
            CURRENT_CULLRESULTS = m_CullResults;

            CommandBuffer cmd = CommandBufferPool.Get("Globals");
            cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            cmd.SetGlobalTexture("_BlueYellowRedGrad", m_asset.BlueYellowRedGradient);
            m_context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            m_context.DrawSkybox(CURRENT_CAMERA);

            RenderGBuffer();
            Shade();

#if UNITY_EDITOR
            // Emit scene view UI
            if (camera.cameraType == CameraType.SceneView)
                ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
#endif

        }


        timeSinceLastRender += Time.deltaTime;
        frameCounter++;
        context.Submit();
      //  m_asset.memoryConsumption += g_VistaAtlas_A.width * g_VistaAtlas_A.height * (g_VistaAtlas_A.format == RenderTextureFormat.DefaultHDR ? 8 : 4) * 2;
     //   m_asset.memoryConsumption /= 1024 * 1024;
    }

    RenderTargetIdentifier[] gBuffer;
    void RenderGBuffer()
    {
        CommandBuffer cmd = CommandBufferPool.Get("GBuffer");
        int screenX = CURRENT_CAMERA.pixelWidth;
        int screenY = CURRENT_CAMERA.pixelHeight;
        cmd.SetGlobalTexture("g_Dither", m_asset.dither[frameCounter % m_asset.dither.Length]);
        cmd.SetGlobalMatrix("cam_viewToWorld", CURRENT_CAMERA.cameraToWorldMatrix);
        cmd.SetGlobalMatrix("cam_worldToView", CURRENT_CAMERA.worldToCameraMatrix);


        int renderScale_X = Mathf.RoundToInt(m_asset.RenderScale * screenX);
        int renderScale_Y = Mathf.RoundToInt(m_asset.RenderScale * screenY);
        //NOTE: difference from 64Bit integer target to 32bit+32Bit fixed target is only ~0.2ms
        if (m_asset.UncompressedGBuffer)
        {
            cmd.GetTemporaryRT(g_GBuffer0, renderScale_X, renderScale_Y, 0, FilterMode.Point, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cmd.GetTemporaryRT(g_GBuffer1, renderScale_X, renderScale_Y, 0, FilterMode.Point, RenderTextureFormat.ARGB2101010, RenderTextureReadWrite.Linear);
            cmd.GetTemporaryRT(g_GBuffer2, renderScale_X, renderScale_Y, 0, FilterMode.Point, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            cmd.GetTemporaryRT(g_GBuffer3, renderScale_X, renderScale_Y, 0, FilterMode.Point, RenderTextureFormat.RGB111110Float, RenderTextureReadWrite.Linear);

            gBuffer = new RenderTargetIdentifier[4];
            gBuffer[0] = g_GBuffer0;
            gBuffer[1] = g_GBuffer1;
            gBuffer[2] = g_GBuffer2;
            gBuffer[3] = g_GBuffer3;
            cmd.EnableShaderKeyword("_FULL_GBUFFER");
        }
        else
        {
            cmd.GetTemporaryRT(g_GBuffer0, renderScale_X, renderScale_Y, 0, FilterMode.Point, RenderTextureFormat.ARGB2101010, RenderTextureReadWrite.Linear);
            cmd.GetTemporaryRT(g_GBuffer1, renderScale_X, renderScale_Y, 0, FilterMode.Point, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            gBuffer = new RenderTargetIdentifier[2];
            gBuffer[0] = g_GBuffer0;
            gBuffer[1] = g_GBuffer1;
            cmd.DisableShaderKeyword("_FULL_GBUFFER");
        }
        // accquire depth rendertarget
        cmd.GetTemporaryRT(g_Depth, renderScale_X, renderScale_Y, 32, FilterMode.Point, RenderTextureFormat.Depth);
        cmd.SetRenderTarget(gBuffer, g_Depth);

        cmd.ClearRenderTarget(true, true, Color.clear);
        m_context.ExecuteCommandBuffer(cmd);

        RenderOpaque(m_GBufferPass, SortFlags.CommonOpaque, RendererConfiguration.PerObjectLightProbe);
        CommandBufferPool.Release(cmd);
    }

    private const int MAX_LIGHTS = 32;
    void Shade()
    {

        CommandBuffer cmd = CommandBufferPool.Get("Shade");
        clusteredLightning.SetupClusteredLightning(ref cmd, m_CullResults, CURRENT_CAMERA, m_asset.NearCluster);

        // shade
        cmd.SetGlobalTexture("g_Depth", g_Depth);

        var p = GL.GetGPUProjectionMatrix(CURRENT_CAMERA.projectionMatrix, false);// Unity flips its 'Y' vector depending on if its in VR, Editor view or game view etc... (facepalm)
        //p[2, 3] = p[3, 2] = 0.0f;
        //p[3, 3] = 1.0f;
        var clipToWorld = (p * CURRENT_CAMERA.worldToCameraMatrix).inverse;
        cmd.SetGlobalMatrix("camera_clipToWorld", clipToWorld);

        // cmd.SetGlobalVector("_WorldSpaceCameraPos", CURRENT_CAMERA.transform.position);
        cmd.SetGlobalTexture("g_gBuffer0", g_GBuffer0);
        cmd.SetGlobalTexture("g_gBuffer1", g_GBuffer1);
        cmd.SetGlobalTexture("g_gBuffer2", g_GBuffer2);
        cmd.SetGlobalTexture("g_gBuffer3", g_GBuffer3);
        cmd.Blit(g_GBuffer0, BuiltinRenderTextureType.CameraTarget, fullscreenMat);

     //   cmd.ReleaseTemporaryRT(g_PosBuffer);
        cmd.ReleaseTemporaryRT(g_GBuffer0);
        cmd.ReleaseTemporaryRT(g_GBuffer1);
        cmd.ReleaseTemporaryRT(g_GBuffer2);
        cmd.ReleaseTemporaryRT(g_GBuffer3);
        cmd.ReleaseTemporaryRT(g_Depth);

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
        cmd.ReleaseTemporaryRT(g_Depth);
        m_context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }
}

