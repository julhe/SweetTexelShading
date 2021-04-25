using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
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

    [Range(8, 13)] public int AtlasSizeExponent = 10;
    RenderTexture VistaAtlasA;

    static void RenderTextureCreateOrChange(ref RenderTexture rt, int sizeExponent) {
        int sizeXy = 1 << sizeExponent;

        bool needsCreation = rt == null;
        if (rt != null && rt.width != sizeXy) {
            rt.Release();
            needsCreation = true;
        }
        
        if (needsCreation) {
            rt = new RenderTexture(sizeXy, sizeXy, 1, DefaultFormat.LDR);
            rt.useMipMap = true;
            rt.Create();
        }
    }
    public override void Create() {
        instance = this;

        texelSpaceShadingPass = new TexelSpaceShadingPass {
            renderPassEvent = RenderPassEvent.BeforeRenderingOpaques
        };
        
        RenderTextureCreateOrChange(ref VistaAtlasA, AtlasSizeExponent);

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
        RenderTextureCreateOrChange(ref VistaAtlasA, AtlasSizeExponent);
        texelSpaceShadingPass.TargetAtlas = VistaAtlasA;
        renderer.EnqueuePass(texelSpaceShadingPass);
        
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
        public RenderTexture TargetAtlas;
        ShaderTagId vistaPass = new ShaderTagId("Vista Pass");
        ShaderTagId texelSpacePass = new ShaderTagId("Texel Space Pass");
        
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
            ConfigureTarget(TargetAtlas);
            ConfigureClear(ClearFlag.Color, Color.clear);
            cmd.SetGlobalTexture("g_VistaAtlas", TargetAtlas);
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            #region Clear Atlas
                // var cmd = CommandBufferPool.Get();
                // cmd.ClearRenderTarget(true, true, Color.clear);
                // context.ExecuteCommandBuffer(cmd);
                // CommandBufferPool.Release(cmd);
            #endregion
            #region Render objects to atlas (Texel Shade Pass)
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
