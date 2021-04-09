using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;

public class SimpleForward : RenderPipeline
{
    CameraComparer m_CameraComparer = new CameraComparer();
    public static Camera CURRENT_CAMERA { get; private set; }
    private const string RENDERPIPELINE_NAME = "SimpleForward";
    static readonly ShaderTagId ForwardOpaquePass = new ShaderTagId("SimpleForward");
    static readonly ShaderTagId DepthOnlyPass = new ShaderTagId("DepthOnly");
    CullingResults m_CullResults;
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

    protected override void Render(ScriptableRenderContext context, Camera[] cameras)
    {
        context = context;
        bool stereoEnabled = XRSettings.isDeviceActive;
        Array.Sort(cameras, m_CameraComparer);
        clusteredLightning.SetParameters(asset.froxelsX, asset.froxelsY, asset.froxelsZ);
        foreach (Camera camera in cameras)
        {
            CURRENT_CAMERA = camera;
            SortingSettings cameraSortSettings = new SortingSettings(camera);
            ScriptableCullingParameters cullingParameters;

            if (!camera.TryGetCullingParameters(stereoEnabled, out cullingParameters)) {
                continue;
            }
            
#if UNITY_EDITOR
            // Emit scene view UI
            if (camera.cameraType == CameraType.SceneView)
                ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
#endif

            context.Cull(ref cullingParameters);
            SetupGlobals();
            if (asset.DepthPrepass)
            {
                RenderOpaque(DepthOnlyPass, new SortingSettings(camera) { criteria = SortingCriteria.OptimizeStateChanges});
            }
            RenderOpaque(ForwardOpaquePass, new SortingSettings(camera) { criteria = SortingCriteria.OptimizeStateChanges});
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


    const PerObjectData rendererConfiguration_shading =
        PerObjectData.LightIndices |
        PerObjectData.Lightmaps |
        PerObjectData.LightProbe |
        PerObjectData.ReflectionProbes |
        PerObjectData.LightData;
    void RenderOpaque(ShaderTagId shaderTagId, SortingSettings sortingSettings) {
        LogVerbose("RenderOpaque...");
        DrawingSettings drawSettings = new DrawingSettings(shaderTagId, sortingSettings);
        FilteringSettings filteringSettings = new FilteringSettings(RenderQueueRange.opaque);
        drawSettings.perObjectData = rendererConfiguration_shading;
        context.DrawRenderers(m_CullResults, ref drawSettings, ref filteringSettings);
    }
    
    void LogVerbose(object obj) {
    #if LOG_VERBOSE
        Debug.Log(obj);
    #endif
    }
    //
    // public override void Dispose()
    // {
    //     base.Dispose();
    //     clusteredLightning.Dispose();
    // }
}
