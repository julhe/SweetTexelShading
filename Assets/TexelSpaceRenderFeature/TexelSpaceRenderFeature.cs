using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering.Universal;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class TexelSpaceRenderFeature : ScriptableRendererFeature {

    #region Configuration
        const int MaximalObjectsPerView = 512;
    #endregion
    #region TexelSpaceRenderHelperInterface
        public static TexelSpaceRenderFeature instance;
        public List<TexelSpaceRenderHelper> visibleObjects = new List<TexelSpaceRenderHelper>();

        public void AddObject(TexelSpaceRenderHelper texelSpaceRenderHelper) {
            visibleObjects.Add(texelSpaceRenderHelper);
        }
    #endregion

    [Range(8, 12)] public int VistaAtlasExponent = 10;
    RenderTexture VistaAtlasA;

    void RenderTextureCreateOrChange(ref RenderTexture rt, int sizeExponent) {
        int sizeXy = 1 << sizeExponent;
        Debug.Log(sizeXy);
        if (rt == null) {
            
       
        }
    }
    public override void Create() {
        instance = this;

        texelSpaceShadingPass = new TexelSpaceShadingPass();
        visibilityPass = new VisibilityPass();
        RenderTextureCreateOrChange(ref VistaAtlasA, VistaAtlasExponent);

    }
    
    static RenderTargetHandle VisibilityRt = new RenderTargetHandle("_VisibilityRt");
    // Passes:
    // 1: (Render) Visibility
    // 2: (Compute) Visibility Compute
    // 3: (Compute) Atlas Packing
    // 4: (Render) TexelSpace Render
    // 5: (Render, URP) Present 
    VisibilityPass visibilityPass;
    TexelSpaceShadingPass texelSpaceShadingPass;
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
        texelSpaceShadingPass.renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
        renderer.EnqueuePass(texelSpaceShadingPass);
       // visibilityPass.renderPassEvent = RenderPassEvent.BeforeRendering;
      //  renderer.EnqueuePass(visibilityPass);
      visibleObjects.Clear();
    }

    class VisibilityPass : ScriptableRenderPass {
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
            base.Configure(cmd, cameraTextureDescriptor);

            // assume XR is go always single pass
            
            // create vista RT
            var textureDescriptor = cameraTextureDescriptor;
            textureDescriptor.colorFormat = RenderTextureFormat.RInt;
 
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
           
        }

        public override void OnCameraCleanup(CommandBuffer cmd) {
            base.OnCameraCleanup(cmd);
            
        }
    }
    
    class TexelSpaceShadingPass : ScriptableRenderPass {
        public RenderTexture VistaAtlas;
        ShaderTagId vistaPass = new ShaderTagId("Vista Pass");
        ShaderTagId texelSpacePass = new ShaderTagId("Texel Space Pass");
        
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
            base.Configure(cmd, cameraTextureDescriptor);
            
            if (VistaAtlas == null || !VistaAtlas.IsCreated()) {
                // TODO: custom resolution
                VistaAtlas = new RenderTexture(2048, 2048, 1, RenderTextureFormat.ARGB32);
                VistaAtlas.useMipMap = true;
                VistaAtlas.Create();
            }
            ConfigureTarget(VistaAtlas);
            cmd.SetGlobalTexture("g_prev_VistaAtlas", VistaAtlas);
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            #region Clear RT
                // var cmd = CommandBufferPool.Get();
                // cmd.ClearRenderTarget(true, true, Color.black);
                // context.ExecuteCommandBuffer(cmd);
                // CommandBufferPool.Release(cmd);
            #endregion
            #region render atlas
                var drawingSettings = CreateDrawingSettings(texelSpacePass, ref renderingData, SortingCriteria.OptimizeStateChanges);
                FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.all);
                context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filterSettings);
            #endregion
        }
    }

    void LogTrace(object obj) {
    #if LOG_TRACE
        Debug.Log(obj, this);
    #endif
    }
}
