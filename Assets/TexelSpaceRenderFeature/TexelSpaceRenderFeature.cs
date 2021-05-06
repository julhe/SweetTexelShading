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
        public const int SCREEN_MAX_X = 3840, 
            SCREEN_MAX_Y = 2100, 
            COMPUTE_COVERAGE_TILE_SIZE = 8, 
            MAX_PRIMITIVES_PER_OBJECT = 65536 / PRIMITIVE_CLUSTER_SIZE,
            PRIMITIVE_CLUSTER_SIZE = 8, 
            kCameraDepthBufferBits = 32, 
            MAX_LIGHTS = 48;

    #endregion

    [Range(8, 13)] public int AtlasSizeExponent = 10;
    public ComputeShader TssComputeShader;
    
    RenderTexture VistaAtlasA;

    static void RenderTextureCreateOrChange(ref RenderTexture rt, int sizeExponent) {
        int sizeXy = 1 << sizeExponent;

        bool needsCreation = rt == null;
        if (rt != null && rt.width != sizeXy) {
            rt.Release();
            needsCreation = true;
        }

        if (!needsCreation) {
            return;
        }

        rt = new RenderTexture(sizeXy, sizeXy, 1, DefaultFormat.LDR) {useMipMap = true};
        rt.Create();
    }
    public override void Create() {
        instance = this;

        texelSpaceShadingPass = new TexelSpaceShadingPass {
            renderPassEvent = RenderPassEvent.BeforeRenderingOpaques
        };

        visibilityPass = new VisibilityPass(TssComputeShader) {
            renderPassEvent = RenderPassEvent.BeforeRendering
        };
        RenderTextureCreateOrChange(ref VistaAtlasA, AtlasSizeExponent);
        
        VisibilityRt.Init("_VisibilityRt");
    }

    static RenderTargetHandle VisibilityRt;
    // Passes:
    // 1: (Render) Visibility
    // 2: (Compute) Visibility Compute
    // 3: (Compute) Atlas Packing
    // 4: (Render) TexelSpace Render
    // 5: (Render, URP) Present 
    VisibilityPass visibilityPass;
    TexelSpaceShadingPass texelSpaceShadingPass;
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {

        // Visiblity Pass
        // =============================================================================================================
        for (int i = 0; i < visibleObjects.Count; i++) {
            visibleObjects[i].SetAtlasProperties(i + 1); //objectID 0 is reserved for "undefined"
        }
        visibleObjects.Clear();

        renderer.EnqueuePass(visibilityPass);
        
        // Shading Pass
        // =============================================================================================================
        RenderTextureCreateOrChange(ref VistaAtlasA, AtlasSizeExponent);
        texelSpaceShadingPass.TargetAtlas = VistaAtlasA;
        renderer.EnqueuePass(texelSpaceShadingPass);
        
    }

    class VisibilityPass : ScriptableRenderPass {
        ShaderTagId visibilityPass = new ShaderTagId("Visibility Pass");

        ComputeShader m_ResolveCS;
        public int g_PrimitiveVisibilityID = Shader.PropertyToID("g_PrimitiveVisibility");
        public int g_VisibilityBufferID = Shader.PropertyToID("g_VisibilityBuffer");

        public int VisibleObjects, AtlasAxisSize;

        ComputeBuffer
            g_PrimitiveVisibility,
            g_ObjectToAtlasProperties,
            g_prev_ObjectToAtlasProperties,
            g_Object_MipmapLevelA,
            g_Object_MipmapLevelB,
            g_ObjectMipMapCounterValue;

        int m_cs_ExtractVisibility, m_cs_MipMapFinalize, m_cs_DebugVisibilityBuffer, m_cs_AtlasPacking, m_cs_CopyDataToPreFrameBuffer;
        public VisibilityPass(ComputeShader m_ResolveCS) {
            this.m_ResolveCS = m_ResolveCS;
            m_cs_ExtractVisibility = m_ResolveCS.FindKernel("ExtractCoverage");
            m_cs_DebugVisibilityBuffer = m_ResolveCS.FindKernel("DebugShowVertexID");
            m_cs_AtlasPacking = m_ResolveCS.FindKernel("AtlasPacking");
            m_cs_CopyDataToPreFrameBuffer = m_ResolveCS.FindKernel("CopyDataToPreFrameBuffer");
            m_cs_MipMapFinalize = m_ResolveCS.FindKernel("MipMapFinalize");
            
            g_PrimitiveVisibility =
                new ComputeBuffer(MaximalObjectsPerView * (MAX_PRIMITIVES_PER_OBJECT / 32), sizeof(int));
            
            g_ObjectToAtlasProperties =
                new ComputeBuffer(MaximalObjectsPerView, sizeof(uint) + sizeof(uint) + sizeof(float) * 4);
            g_prev_ObjectToAtlasProperties =
                new ComputeBuffer(g_ObjectToAtlasProperties.count, g_ObjectToAtlasProperties.stride);
            
            // this value can get pretty large, so 
            int g_Object_MipmapLevelA_size =
                SCREEN_MAX_X / COMPUTE_COVERAGE_TILE_SIZE * (SCREEN_MAX_Y / COMPUTE_COVERAGE_TILE_SIZE) *
                MaximalObjectsPerView / 32;
            g_Object_MipmapLevelA =
                new ComputeBuffer(SCREEN_MAX_X * SCREEN_MAX_Y, sizeof(int),
                    ComputeBufferType.Append); //TODO: better heristic for value
            g_Object_MipmapLevelB = new ComputeBuffer(g_Object_MipmapLevelA.count, g_Object_MipmapLevelA.stride,
                ComputeBufferType.Append);
            g_ObjectMipMapCounterValue = new ComputeBuffer(1, sizeof(int), ComputeBufferType.IndirectArguments);
            
            
        }
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
            base.Configure(cmd, cameraTextureDescriptor);
            
            // assume XR is go always single pass
            // create vista RT
            var textureDescriptor = cameraTextureDescriptor;
            textureDescriptor.colorFormat = RenderTextureFormat.RInt;

            cmd.GetTemporaryRT(VisibilityRt.id, textureDescriptor);
            ConfigureTarget(VisibilityRt.Identifier());
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            var drawingSettings = CreateDrawingSettings(visibilityPass, ref renderingData, SortingCriteria.OptimizeStateChanges);
            FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.all);
            context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filterSettings);
            
            
            // VISIBLITY DISSOLVE PASS
            // maps the previous rendered data into usable buffers
            var cmd = CommandBufferPool.Get("Visibilty Disolve");
            cmd.SetComputeTextureParam(m_ResolveCS, m_cs_ExtractVisibility, g_VisibilityBufferID,
                VisibilityRt.id);
            cmd.SetComputeBufferParam(m_ResolveCS, m_cs_ExtractVisibility, g_PrimitiveVisibilityID,
                g_PrimitiveVisibility);
            cmd.SetComputeBufferParam(m_ResolveCS, m_cs_ExtractVisibility, "g_ObjectToAtlasProperties",
                g_ObjectToAtlasProperties);

            cmd.SetComputeBufferParam(m_ResolveCS, m_cs_ExtractVisibility, "g_ObjectMipMap_append",
                g_Object_MipmapLevelA);
            int SCREEN_X = 32, SCREEN_Y = 32; //TODO:
            cmd.DispatchCompute(m_ResolveCS, m_cs_ExtractVisibility, SCREEN_X / COMPUTE_COVERAGE_TILE_SIZE,
                SCREEN_Y / COMPUTE_COVERAGE_TILE_SIZE, 1);
            cmd.CopyCounterValue(g_Object_MipmapLevelA, g_ObjectMipMapCounterValue, 0);
            cmd.SetComputeBufferParam(m_ResolveCS, m_cs_MipMapFinalize, "g_ObjectMipMap_consume",
                g_Object_MipmapLevelA);
            cmd.SetComputeBufferParam(m_ResolveCS, m_cs_MipMapFinalize, "g_ObjectToAtlasProperties",
                g_ObjectToAtlasProperties);
            cmd.SetComputeBufferParam(m_ResolveCS, m_cs_MipMapFinalize, "g_ObjectMipMapCounterValue",
                g_ObjectMipMapCounterValue);
            cmd.DispatchCompute(m_ResolveCS, m_cs_MipMapFinalize, 1, 1, 1);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd) {
            base.OnCameraCleanup(cmd);
            cmd.ReleaseTemporaryRT(VisibilityRt.id);
            
        }
    }
    
    class TexelSpaceShadingPass : ScriptableRenderPass {
        public RenderTexture TargetAtlas;
        
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
    
#region TexelSpaceRenderHelperInterface
    public static TexelSpaceRenderFeature instance;
    public List<TexelSpaceRenderHelper> visibleObjects = new List<TexelSpaceRenderHelper>();

    public void AddObject(TexelSpaceRenderHelper texelSpaceRenderHelper) {
        visibleObjects.Add(texelSpaceRenderHelper);
    }
#endregion
    
    void LogTrace(object obj) {
    #if LOG_TRACE
        Debug.Log(obj, this);
    #endif
    }
}
