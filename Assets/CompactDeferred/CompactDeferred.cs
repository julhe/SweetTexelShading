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

    RenderTexture g_VistaAtlas_A, g_VistaAtlas_B;
    bool target_atlasA;
    RenderTargetIdentifier g_BufferRT, g_CameraTarget_RT, g_PosBufferRT;
    Vector2Int g_visibilityBuffer_dimension;
    const int kCameraDepthBufferBits = 32;

    CameraComparer m_CameraComparer = new CameraComparer();
    int m_cs_ExtractVisibility, g_GBuffer, g_PosBuffer, g_Depth, g_dummyRT, g_intermediate;

    CompactDeferredAsset m_asset;
    float timeSinceLastRender = 0f;

    private int frameCounter = 0;
    public CompactDeferred(CompactDeferredAsset asset)
    {
        m_asset = asset;
        Shader.globalRenderPipeline = PIPELINE_NAME;

        fullscreenMat = new Material(m_asset.resolveBlitShader);

        g_dummyRT = Shader.PropertyToID("g_dummyRT");
        //m_cs_ExtractVisibility = m_ResolveCS.FindKernel("ExtractCoverage");
        //m_cs_DebugVisibilityBuffer = m_ResolveCS.FindKernel("DebugShowVertexID");
        //m_cs_AtlasPacking = m_ResolveCS.FindKernel("AtlasPacking");
        //m_cs_CopyDataToPreFrameBuffer = m_ResolveCS.FindKernel("CopyDataToPreFrameBuffer");

        g_GBuffer = Shader.PropertyToID("g_GBuffer");
        g_PosBuffer = Shader.PropertyToID("g_PosBuffer");
        g_Depth = Shader.PropertyToID("g_Depth");
        
        g_BufferRT = new RenderTargetIdentifier(g_GBuffer);
        g_PosBufferRT = new RenderTargetIdentifier(g_PosBuffer);


    }

    public override void Dispose()
    {
        base.Dispose();
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

        m_asset.memoryConsumption = 0f;

        instance = this;
        m_context = context;
        bool stereoEnabled = XRSettings.isDeviceActive;
        // Sort cameras array by camera depth
        Array.Sort(cameras, m_CameraComparer);


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
            CURRENT_CULLRESULTS = m_CullResults;

            
            RenderGBuffer();
            Shade();
        }


        timeSinceLastRender += Time.deltaTime;
        frameCounter++;
        context.Submit();
      //  m_asset.memoryConsumption += g_VistaAtlas_A.width * g_VistaAtlas_A.height * (g_VistaAtlas_A.format == RenderTextureFormat.DefaultHDR ? 8 : 4) * 2;
     //   m_asset.memoryConsumption /= 1024 * 1024;
    }

    void ClearCameraTarget(Color color)
    {
        CommandBuffer cmd = CommandBufferPool.Get("ClearCameraTarget");
        cmd.SetGlobalMatrix("cam_viewToWorld", CURRENT_CAMERA.cameraToWorldMatrix);
        cmd.SetGlobalMatrix("cam_worldToView", CURRENT_CAMERA.worldToCameraMatrix);

        cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        cmd.ClearRenderTarget(true, true, color);
        m_context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    RenderTargetIdentifier[] gBuffer = new RenderTargetIdentifier[1];
    void RenderGBuffer()
    {
        CommandBuffer cmd = CommandBufferPool.Get("GBuffer");
        int screenX = CURRENT_CAMERA.pixelWidth;
        int screenY = CURRENT_CAMERA.pixelHeight;
        cmd.SetGlobalTexture("g_Dither", m_asset.dither[frameCounter % m_asset.dither.Length]);
      
        int renderScale_X = Mathf.RoundToInt(m_asset.RenderScale * screenX);
        int renderScale_Y = Mathf.RoundToInt(m_asset.RenderScale * screenY);
        cmd.GetTemporaryRT(g_GBuffer, renderScale_X, renderScale_Y, 0, FilterMode.Point, RenderTextureFormat.RInt);
        //cmd.GetTemporaryRT(g_PosBuffer, screenX, screenY, 0, FilterMode.Point, RenderTextureFormat.ARGBFloat);
        cmd.GetTemporaryRT(g_Depth, renderScale_X, renderScale_Y, 32, FilterMode.Point, RenderTextureFormat.Depth);

        gBuffer[0] = g_GBuffer;
      //  gBuffer[1] = g_PosBuffer;
        cmd.SetRenderTarget(gBuffer, g_Depth);

        cmd.ClearRenderTarget(true, true, Color.clear);
        m_context.ExecuteCommandBuffer(cmd);

        RenderOpaque(m_GBufferPass, SortFlags.CommonOpaque);
        CommandBufferPool.Release(cmd);
    }

    void Shade()
    {
        CommandBuffer cmd = CommandBufferPool.Get("Shade");

        {
            var visibleLights = m_CullResults.visibleLights;
            VisibleLight mainLight = new VisibleLight();
            bool mainLightValid = false;
            foreach (var light in visibleLights)
            {
                if (light.lightType == LightType.Directional)
                {
                    mainLightValid = true;
                    mainLight = light;
                }

            }
            if (mainLightValid)
            {
                cmd.SetGlobalVector("_WorldSpaceLightPos0", -mainLight.light.transform.forward);
                cmd.SetGlobalColor("_LightColor0", mainLight.light.color);
            }

            cmd.SetGlobalTexture("unity_SpecCube0", RenderSettings.customReflection);
            if (m_CullResults.visibleReflectionProbes.Count > 0)
            {
     
                cmd.SetGlobalVector("unity_SpecCube0_HDR", m_CullResults.visibleReflectionProbes[0].hdr);
                cmd.SetGlobalVector("unity_SpecCube0_ProbePosition", Vector4.zero);
                
            //    cmd.SetGlobalTexture("unity_SpecCube0", m_CullResults.visibleReflectionProbes[0].texture);
            }
         
           
        }

        // shade
        cmd.SetGlobalTexture("g_PosBuffer", g_PosBuffer);
        cmd.SetGlobalTexture("g_Depth", g_Depth);

        var p = GL.GetGPUProjectionMatrix(CURRENT_CAMERA.projectionMatrix, false);// Unity flips its 'Y' vector depending on if its in VR, Editor view or game view etc... (facepalm)
        //p[2, 3] = p[3, 2] = 0.0f;
        //p[3, 3] = 1.0f;
        var clipToWorld = (p * CURRENT_CAMERA.worldToCameraMatrix).inverse;
        cmd.SetGlobalMatrix("camera_clipToWorld", clipToWorld);

        // cmd.SetGlobalVector("_WorldSpaceCameraPos", CURRENT_CAMERA.transform.position);

        //cmd.GetTemporaryRT(g_intermediate, SCREEN_X, SCREEN_Y, 0, FilterMode.Bilinear);
        cmd.Blit(g_BufferRT, BuiltinRenderTextureType.CameraTarget, fullscreenMat);
     //   cmd.ReleaseTemporaryRT(g_PosBuffer);
        cmd.ReleaseTemporaryRT(g_GBuffer);
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

