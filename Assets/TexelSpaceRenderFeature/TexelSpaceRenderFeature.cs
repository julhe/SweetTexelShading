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
    public float AtlasResolutionScale = 1024f;
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

        visibilityPass = new VisibilityPass() {
            renderPassEvent = RenderPassEvent.BeforeRenderingPrepasses
        };
        RenderTextureCreateOrChange(ref VistaAtlasA, AtlasSizeExponent);
    }


    // Passes:
    // 1: (Render) Visibility
    // 2: (Compute) Visibility Compute
    // 3: (Compute) Atlas Packing
    // 4: (Render) TexelSpace Render
    // 5: (Render, URP) Present 
    VisibilityPass visibilityPass;
    TexelSpaceShadingPass texelSpaceShadingPass;
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
        visibilityPass.m_ResolveCS = TssComputeShader;
        visibilityPass.AtlasResolutionScale = AtlasResolutionScale;
        visibilityPass.AtlasSizeExponent = AtlasSizeExponent;
        visibilityPass.AtlasAxisSize = 1 << AtlasSizeExponent;
        visibilityPass.Initialize();
        if (visibilityPass.IsReady) {
            // Visiblity Pass
            // =============================================================================================================
            for (int i = 0; i < visibleObjects.Count; i++) {
                visibleObjects[i].SetAtlasProperties(i + 1); //objectID 0 is reserved for "undefined"
            }

            visibilityPass.VisibleObjects = visibleObjects.Count;
            renderer.EnqueuePass(visibilityPass);
        
            // Shading Pass
            // =============================================================================================================
            RenderTextureCreateOrChange(ref VistaAtlasA, AtlasSizeExponent);
            texelSpaceShadingPass.TargetAtlas = VistaAtlasA;
            renderer.EnqueuePass(texelSpaceShadingPass);
        }
        else {
            Debug.LogError($"{nameof(TexelSpaceRenderFeature)} is not ready. TSS will not execute.");
        }
        visibleObjects.Clear();
    }

    class VisibilityPass : ScriptableRenderPass {
        public float AtlasResolutionScale, VisiblityPassDownscale = 1f;
        public int AtlasSizeExponent = 10;
        public bool IsReady => m_ResolveCS != null;
        public bool Initialized = false;
        ShaderTagId visibilityPass = new ShaderTagId("Visibility Pass");

        public ComputeShader m_ResolveCS;
        public int g_PrimitiveVisibilityID = Shader.PropertyToID("g_PrimitiveVisibility");
        public int g_VisibilityBufferID = Shader.PropertyToID("g_VisibilityBuffer");
        RenderTargetIdentifier g_visibilityBuffer_RT;

        public int VisibleObjects, AtlasAxisSize;
        Vector2Int g_visibilityBuffer_dimension;

        static class CsKernels {
            public static int ExtractVisiblity;
        }
        
        ComputeBuffer
            g_PrimitiveVisibility,
            g_ObjectToAtlasProperties,
            g_prev_ObjectToAtlasProperties,
            g_Object_MipmapLevelA,
            g_Object_MipmapLevelB,
            g_ObjectMipMapCounterValue;

        int m_cs_ExtractVisibility, m_cs_MipMapFinalize, m_cs_DebugVisibilityBuffer, m_cs_AtlasPacking, m_cs_CopyDataToPreFrameBuffer, m_cs_InitalizePrimitiveVisiblity;

        public void Initialize() {
            if (!m_ResolveCS) {
                Debug.LogError("Compute Shader not set.");
                return;
            }
            
            if (Initialized) {
                return;
            }

            
            m_cs_ExtractVisibility = m_ResolveCS.FindKernel("ExtractCoverage");
            m_cs_DebugVisibilityBuffer = m_ResolveCS.FindKernel("DebugShowVertexID");
            m_cs_AtlasPacking = m_ResolveCS.FindKernel("AtlasPacking");
            m_cs_CopyDataToPreFrameBuffer = m_ResolveCS.FindKernel("CopyDataToPreFrameBuffer");
            m_cs_MipMapFinalize = m_ResolveCS.FindKernel("MipMapFinalize");
            m_cs_InitalizePrimitiveVisiblity = m_ResolveCS.FindKernel("InitalizePrimitiveVisiblity");
            
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

            g_visibilityBuffer_RT = new RenderTargetIdentifier(g_VisibilityBufferID);
            Initialized = true;
        }
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
            base.Configure(cmd, cameraTextureDescriptor);

         
            // Create the rendertarget for the visiblity information
            RenderTextureDescriptor textureDescriptor = cameraTextureDescriptor;
            textureDescriptor.colorFormat = RenderTextureFormat.RInt;
            textureDescriptor.depthBufferBits = 16;
            textureDescriptor.msaaSamples = 1;
            //NOTE: force mono-scopic rendering. adding support for array textures in the visiblity pass feels wierd...
            // but that might change if we render out visibility in the vista pass anyways?
            textureDescriptor.vrUsage = VRTextureUsage.None;
            textureDescriptor.dimension = TextureDimension.Tex2D;
            if (cameraTextureDescriptor.vrUsage == VRTextureUsage.TwoEyes) {
                textureDescriptor.width *= 2;
            }
            
            cmd.GetTemporaryRT(g_VisibilityBufferID, textureDescriptor);
            ConfigureTarget(g_VisibilityBufferID);
            ConfigureClear(ClearFlag.All, Color.clear);
        }

        //TODO: merge command buffers to single one
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            CameraData cameraData = renderingData.cameraData;
            int screenXpx = cameraData.camera.pixelWidth, screenYpx = cameraData.camera.pixelHeight;
            // SetupShaderGlobals
            // =====================================================================================================
            {
                CommandBuffer cmd5 = CommandBufferPool.Get("SetupShaderGlobals");
                cmd5.SetGlobalFloat("g_AtlasResolutionScale",
                    AtlasResolutionScale / VisiblityPassDownscale); 
                // float lerpFactor =
                //     Mathf.Clamp01((float) timeSinceLastRender /
                //                   (1f / m_asset.atlasRefreshFps)); //TODO: clamp should't been neccesary

                cmd5.SetGlobalFloat("g_atlasMorph", 0.5f);
                // if (m_asset.TexelSpaceBackfaceCulling) {
                //     cmd5.EnableShaderKeyword("TRIANGLE_CULLING");
                // }
                // else {
                //     cmd5.DisableShaderKeyword("TRIANGLE_CULLING");
                // }

                context.ExecuteCommandBuffer(cmd5);
                CommandBufferPool.Release(cmd5);
            }
            
            // =====================================================================================================
            // CopyDataToPreFrameBuffer
            // =====================================================================================================
            // LogVerbose("CopyDataToPreFrameBuffer...");
            {
                CommandBuffer cmd = CommandBufferPool.Get("CopyDataToPreFrameBuffer");
                cmd.SetComputeBufferParam(m_ResolveCS, m_cs_CopyDataToPreFrameBuffer, "g_ObjectToAtlasProperties",
                    g_ObjectToAtlasProperties);
                cmd.SetComputeBufferParam(m_ResolveCS, m_cs_CopyDataToPreFrameBuffer, "g_prev_ObjectToAtlasProperties",
                    g_prev_ObjectToAtlasProperties);
                uint threadsX, threadsY, threadsZ;
                m_ResolveCS.GetKernelThreadGroupSizes(m_cs_CopyDataToPreFrameBuffer, out threadsX, out threadsY,
                    out threadsZ);
                cmd.DispatchCompute(m_ResolveCS, m_cs_CopyDataToPreFrameBuffer,
                    Mathf.CeilToInt(MaximalObjectsPerView / (float) 64.0), 1, 1);

                cmd.SetComputeBufferParam(m_ResolveCS, m_cs_InitalizePrimitiveVisiblity, g_PrimitiveVisibilityID,
                    g_PrimitiveVisibility);
                cmd.DispatchCompute(m_ResolveCS, m_cs_InitalizePrimitiveVisiblity,
                    Mathf.CeilToInt(g_PrimitiveVisibility.count / (float) threadsX), 1, 1);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
            // =====================================================================================================
            // SetupRenderBuffers
            // =====================================================================================================
            {
                //TODO: move this into pass configure?
                
               // LogTrace("SetupRenderBuffers...");
                CommandBuffer cmd1 = CommandBufferPool.Get("SetupBuffers");
                int screenX = cameraData.camera.pixelWidth;
                int screenY = cameraData.camera.pixelHeight;
                g_visibilityBuffer_dimension = new Vector2Int(
                    Mathf.CeilToInt(screenX / VisiblityPassDownscale),
                    Mathf.CeilToInt(screenY / VisiblityPassDownscale));

                // cmd1.GetTemporaryRT(g_VisibilityBufferID, g_visibilityBuffer_dimension.x,
                //     g_visibilityBuffer_dimension.y, 32, FilterMode.Point, RenderTextureFormat.RInt,
                //     RenderTextureReadWrite.Linear, 1);

                // NOTE: RT already set by pass configuration
                // cmd1.SetRenderTarget(g_visibilityBuffer_RT);
                // cmd1.ClearRenderTarget(true, true, Color.clear);
                //
                // cmd1.SetRenderTarget(target_atlasA ? g_VistaAtlas_A : g_VistaAtlas_B);
                // if (m_asset.clearAtlasOnRefresh) {
                //     cmd1.ClearRenderTarget(true, true, Color.clear);
                // }


                cmd1.SetGlobalFloat("g_AtlasSizeExponent", AtlasSizeExponent);
                context.ExecuteCommandBuffer(cmd1);
                CommandBufferPool.Release(cmd1);
            }
            
            // Render Visiblity Pass
            var drawingSettings = CreateDrawingSettings(visibilityPass, ref renderingData, SortingCriteria.OptimizeStateChanges);
            //TODO: only render opaque?
            FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.all);
            context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filterSettings);
            
            // VISIBLITY DISSOLVE PASS
            // maps the previous rendered data into usable buffers
            {
                CommandBuffer cmd = CommandBufferPool.Get("Visibilty Disolve");
                cmd.SetComputeTextureParam(m_ResolveCS, m_cs_ExtractVisibility, g_VisibilityBufferID, g_visibilityBuffer_RT);
                cmd.SetComputeBufferParam(m_ResolveCS, m_cs_ExtractVisibility, g_PrimitiveVisibilityID,
                    g_PrimitiveVisibility);
                cmd.SetComputeBufferParam(m_ResolveCS, m_cs_ExtractVisibility, "g_ObjectToAtlasProperties",
                    g_ObjectToAtlasProperties);

                cmd.SetComputeBufferParam(m_ResolveCS, m_cs_ExtractVisibility, "g_ObjectMipMap_append",
                    g_Object_MipmapLevelA);
                
                cmd.DispatchCompute(
                    m_ResolveCS, 
                    m_cs_ExtractVisibility, 
                    screenXpx / COMPUTE_COVERAGE_TILE_SIZE,
                    screenYpx / COMPUTE_COVERAGE_TILE_SIZE, 
                    1);
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
            
            // =====================================================================================================
            // PackAtlas
            // =====================================================================================================
            {
               // LogTrace("PackAtlas...");
                CommandBuffer cmd3 = CommandBufferPool.Get("PackAtlas");

                cmd3.SetComputeIntParam(m_ResolveCS, "g_totalObjectsInView", VisibleObjects + 1);
                cmd3.SetComputeIntParam(m_ResolveCS, "g_atlasAxisSize", AtlasAxisSize);

                cmd3.SetComputeBufferParam(m_ResolveCS, m_cs_AtlasPacking, "g_ObjectToAtlasProperties",
                    g_ObjectToAtlasProperties);

                cmd3.DispatchCompute(m_ResolveCS, m_cs_AtlasPacking, 1, 1, 1);

                context.ExecuteCommandBuffer(cmd3);
                CommandBufferPool.Release(cmd3);
            }
            
            // =====================================================================================================
            // PackAtlas
            // =====================================================================================================
            {
                CommandBuffer cmd = CommandBufferPool.Get("PackAtlas");
                cmd.SetGlobalBuffer(g_PrimitiveVisibilityID, g_PrimitiveVisibility);
                cmd.SetGlobalBuffer("g_ObjectToAtlasProperties", g_ObjectToAtlasProperties);
                cmd.SetGlobalBuffer("g_prev_ObjectToAtlasProperties", g_prev_ObjectToAtlasProperties);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }


        }

        public override void OnCameraCleanup(CommandBuffer cmd) {
            base.OnCameraCleanup(cmd);
           // cmd.ReleaseTemporaryRT(VisibilityRt.id);
            
        }
    }
    
    class TexelSpaceShadingPass : ScriptableRenderPass {
        public RenderTexture TargetAtlas;
        
        ShaderTagId texelSpacePass = new ShaderTagId("Texel Space Pass");
        
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
            ConfigureTarget(TargetAtlas);
            ConfigureClear(ClearFlag.Color, Color.clear);
            // TODO:
            //cmd.SetGlobalTexture("g_prev_VistaAtlas", target_atlasA ? g_VistaAtlas_B : g_VistaAtlas_A);
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
