using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;

public class SimpleForward : RenderPipeline
{
    CameraComparer m_CameraComparer = new CameraComparer();
    public static Camera CURRENT_CAMERA { get; private set; }
    private const string RENDERPIPELINE_NAME = "SimpleForward";
    static readonly ShaderPassName ForwardOpaquePass = new ShaderPassName("SimpleForward");
    static readonly ShaderPassName DepthOnlyPass = new ShaderPassName("DepthOnly");
    CullResults m_CullResults;
    private ScriptableRenderContext context;
    private SimpleForwardAsset asset;
    private ClusteredLightning clusteredLightning;
    private RenderTexture g_FroxelToIndexOffset;
    public SimpleForward(SimpleForwardAsset asset)
    {
        this.asset = asset;
        Shader.globalRenderPipeline = RENDERPIPELINE_NAME;
        clusteredLightning = new ClusteredLightning(asset.clusteredLightning);
    }

    public override void Render(ScriptableRenderContext context, Camera[] cameras)
    {
        base.Render(context, cameras);
        this.context = context;
        bool stereoEnabled = XRSettings.isDeviceActive;
        Array.Sort(cameras, m_CameraComparer);
        clusteredLightning.SetParameters(asset.froxelsX, asset.froxelsY, asset.froxelsZ);
        foreach (Camera camera in cameras)
        {
            CURRENT_CAMERA = camera;
            ScriptableCullingParameters cullingParameters;
            if (!CullResults.GetCullingParameters(CURRENT_CAMERA, stereoEnabled, out cullingParameters))
                continue;
#if UNITY_EDITOR
            // Emit scene view UI
            if (camera.cameraType == CameraType.SceneView)
                ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
#endif

            CullResults.Cull(ref cullingParameters, context, ref m_CullResults);
            SetupGlobals();
            if (asset.DepthPrepass)
            {
                RenderOpaque(DepthOnlyPass, SortFlags.QuantizedFrontToBack);
            }
            RenderOpaque(ForwardOpaquePass, SortFlags.OptimizeStateChanges, RendererConfiguration.PerObjectLightProbe | RendererConfiguration.ProvideReflectionProbeIndices);
            context.DrawSkybox(CURRENT_CAMERA);
        }
        context.Submit();
    }

    public void SetupGlobals()
    {
        context.SetupCameraProperties(CURRENT_CAMERA);
        CommandBuffer cmd = CommandBufferPool.Get("SetupGlobals");

        clusteredLightning.SetupClusteredLightning(ref cmd, m_CullResults, CURRENT_CAMERA, asset.NearCluster);
      
        cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        cmd.ClearRenderTarget(true, true, Color.clear);

        context.ExecuteCommandBuffer(cmd);     
        CommandBufferPool.Release(cmd);
    }


    private FilterRenderersSettings filterRenderers;

    void RenderOpaque(ShaderPassName passName, SortFlags sortFlags, RendererConfiguration rendererConfiguration = RendererConfiguration.None)
    {
        var opaqueDrawSettings = new DrawRendererSettings(CURRENT_CAMERA, passName);
        opaqueDrawSettings.sorting.flags = sortFlags;
        opaqueDrawSettings.rendererConfiguration = rendererConfiguration;

        var opaqueFilterSettings = new FilterRenderersSettings(true)
        {
            renderQueueRange = RenderQueueRange.all
        };

        context.DrawRenderers(m_CullResults.visibleRenderers, ref opaqueDrawSettings, opaqueFilterSettings);
    }
    public override void Dispose()
    {
        base.Dispose();
        clusteredLightning.Dispose();
    }
}
