using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class TexelSpaceRenderFeature : ScriptableRendererFeature {
	[Range(8, 13)] public int AtlasSizeExponent = 10;
	public float AtlasResolutionScale = 1024f;
	[Range(0.125f, 1f)] public float VisiblityPassScale = 1f;
	public ComputeShader TssComputeShader;
	TexelSpaceShadingPass texelSpaceShadingPass;


	// Passes:
	// 1: (Render) Visibility
	// 2: (Compute) Visibility Compute
	// 3: (Compute) Atlas Packing
	// 4: (Render) TexelSpace Render
	// 5: (Render, URP) Present 
	VisibilityPass visibilityPass;

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

		visibilityPass = new VisibilityPass {
			renderPassEvent = RenderPassEvent.BeforeRenderingPrepasses
		};
		RenderTextureCreateOrChange(ref VistaAtlasA, AtlasSizeExponent);
	}

	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
		visibilityPass.ComputeShader = TssComputeShader;
		visibilityPass.AtlasResolutionScale = AtlasResolutionScale;
		visibilityPass.AtlasSizeExponent = AtlasSizeExponent;
		visibilityPass.AtlasAxisSize = 1 << AtlasSizeExponent;
		visibilityPass.VisiblityPassScale = VisiblityPassScale;
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

	void LogTrace(object obj) {
	#if LOG_TRACE
        Debug.Log(obj, this);
	#endif
	}

	class VisibilityPass : ScriptableRenderPass {
		public float AtlasResolutionScale, VisiblityPassScale = 1f;
		public int AtlasSizeExponent = 10;

		ComputeBuffer
			g_PrimitiveVisibility,
			g_ObjectToAtlasProperties,
			g_prev_ObjectToAtlasProperties,
			g_Object_MipmapLevelA,
			g_Object_MipmapLevelB,
			g_ObjectMipMapCounterValue;

		public readonly int g_PrimitiveVisibilityID = Shader.PropertyToID("g_PrimitiveVisibility");
		Vector2Int g_visibilityBuffer_dimension;
		RenderTargetIdentifier g_visibilityBuffer_RT;
		public readonly int g_VisibilityBufferID = Shader.PropertyToID("g_VisibilityBuffer");
		public bool Initialized;

		static class ComputeKernelId {
			public static int ExtractVisibility,
				MipMapFinalize,
				DebugVisibilityBuffer,
				AtlasPacking,
				CopyDataToPreFrameBuffer,
				InitalizePrimitiveVisiblity;
		}


		public ComputeShader ComputeShader;
		readonly ShaderTagId visibilityPass = new ShaderTagId("Visibility Pass");

		public int VisibleObjects, AtlasAxisSize;

		public bool IsReady => ComputeShader != null;

		public void Initialize() {
			if (!ComputeShader) {
				Debug.LogError("Compute Shader not set.");
				return;
			}
			if (Initialized) {
				return;
			}
			ComputeKernelId.ExtractVisibility = ComputeShader.FindKernel("ExtractCoverage");
			ComputeKernelId.DebugVisibilityBuffer = ComputeShader.FindKernel("DebugShowVertexID");
			ComputeKernelId.AtlasPacking = ComputeShader.FindKernel("AtlasPacking");
			ComputeKernelId.CopyDataToPreFrameBuffer = ComputeShader.FindKernel("CopyDataToPreFrameBuffer");
			ComputeKernelId.MipMapFinalize = ComputeShader.FindKernel("MipMapFinalize");
			ComputeKernelId.InitalizePrimitiveVisiblity = ComputeShader.FindKernel("InitalizePrimitiveVisiblity");

			g_PrimitiveVisibility = new ComputeBuffer(
				MaximalObjectsPerView * (MAX_PRIMITIVES_PER_OBJECT / 32), 
				sizeof(int));

			g_ObjectToAtlasProperties = new ComputeBuffer(
				MaximalObjectsPerView, 
				sizeof(uint) + sizeof(uint) + sizeof(float) * 4);
			
			g_prev_ObjectToAtlasProperties = new ComputeBuffer(
				g_ObjectToAtlasProperties.count,
				g_ObjectToAtlasProperties.stride);

			// this value can get pretty large, so 
			int g_Object_MipmapLevelA_size =
				SCREEN_MAX_X / COMPUTE_COVERAGE_TILE_SIZE 
				* (SCREEN_MAX_Y / COMPUTE_COVERAGE_TILE_SIZE)
				* MaximalObjectsPerView / 32;
			
			g_Object_MipmapLevelA =	new ComputeBuffer(
				SCREEN_MAX_X * SCREEN_MAX_Y, 
				sizeof(int),
					ComputeBufferType.Append); //TODO: better heuristics for value
			
			g_Object_MipmapLevelB = new ComputeBuffer(
				g_Object_MipmapLevelA.count, 
				g_Object_MipmapLevelA.stride,
				ComputeBufferType.Append);
			
			g_ObjectMipMapCounterValue = new ComputeBuffer(
				1, 
				sizeof(int),
				ComputeBufferType.IndirectArguments);

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

			g_visibilityBuffer_dimension = new Vector2Int(
				Mathf.CeilToInt(textureDescriptor.width / VisiblityPassScale),
				Mathf.CeilToInt(textureDescriptor.height / VisiblityPassScale));

			textureDescriptor.width = g_visibilityBuffer_dimension.x;
			textureDescriptor.height = g_visibilityBuffer_dimension.y;

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

			{
				// =====================================================================================================
				// SetupShaderGlobals
				// =====================================================================================================
				CommandBuffer cmd = CommandBufferPool.Get("Visibility-Pass Pre");
				cmd.SetGlobalFloat("g_AtlasResolutionScale",
					AtlasResolutionScale / VisiblityPassScale);
				// float lerpFactor =
				//     Mathf.Clamp01((float) timeSinceLastRender /
				//                   (1f / m_asset.atlasRefreshFps)); //TODO: clamp should't been neccesary

				cmd.SetGlobalFloat("g_atlasMorph", 0.5f);
				cmd.SetGlobalFloat("g_AtlasSizeExponent", AtlasSizeExponent);
				// =====================================================================================================
				// CopyDataToPreFrameBuffer
				// =====================================================================================================
				cmd.SetComputeBufferParam(
					ComputeShader, 
					ComputeKernelId.CopyDataToPreFrameBuffer, 
					"g_ObjectToAtlasProperties",
					g_ObjectToAtlasProperties);
				
				cmd.SetComputeBufferParam(
					ComputeShader, 
					ComputeKernelId.CopyDataToPreFrameBuffer, 
					"g_prev_ObjectToAtlasProperties",
					g_prev_ObjectToAtlasProperties);
				
				ComputeShader.GetKernelThreadGroupSizes(
					ComputeKernelId.CopyDataToPreFrameBuffer, 
					out uint threadsX, 
					out uint _,
					out uint _);
				
				cmd.DispatchCompute(
					ComputeShader, 
					ComputeKernelId.CopyDataToPreFrameBuffer,
					Mathf.CeilToInt(MaximalObjectsPerView / (float) 64.0), 1, 1);

				cmd.SetComputeBufferParam(
					ComputeShader, 
					ComputeKernelId.InitalizePrimitiveVisiblity, 
					g_PrimitiveVisibilityID,
					g_PrimitiveVisibility);
				
				cmd.DispatchCompute(
					ComputeShader, 
					ComputeKernelId.InitalizePrimitiveVisiblity,
					Mathf.CeilToInt(g_PrimitiveVisibility.count / (float) threadsX), 
					1, 
					1);
				
				context.ExecuteCommandBuffer(cmd);
				CommandBufferPool.Release(cmd);
			}

			// Render Visiblity Pass
			DrawingSettings drawingSettings =
				CreateDrawingSettings(visibilityPass, ref renderingData, SortingCriteria.OptimizeStateChanges);
			FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.opaque);
			context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filterSettings);
			{
				// =====================================================================================================
				// VISIBLITY DISSOLVE PASS
				// maps the previous rendered data into usable buffers
				// =====================================================================================================
				CommandBuffer cmd = CommandBufferPool.Get("Visibility-Pass Post");
				cmd.SetComputeTextureParam(
					ComputeShader, 
					ComputeKernelId.ExtractVisibility, 
					g_VisibilityBufferID,
					g_visibilityBuffer_RT);
				
				cmd.SetComputeBufferParam(
					ComputeShader, 
					ComputeKernelId.ExtractVisibility, 
					g_PrimitiveVisibilityID,
					g_PrimitiveVisibility);
				
				cmd.SetComputeBufferParam(
					ComputeShader, 
					ComputeKernelId.ExtractVisibility, 
					"g_ObjectToAtlasProperties",
					g_ObjectToAtlasProperties);
				
				cmd.SetComputeBufferParam(
					ComputeShader, 
					ComputeKernelId.ExtractVisibility, 
					"g_ObjectMipMap_append",
					g_Object_MipmapLevelA);
				
				cmd.DispatchCompute(
					ComputeShader,
					ComputeKernelId.ExtractVisibility,
					screenXpx / COMPUTE_COVERAGE_TILE_SIZE,
					screenYpx / COMPUTE_COVERAGE_TILE_SIZE,
					1);
				
				cmd.CopyCounterValue(g_Object_MipmapLevelA, g_ObjectMipMapCounterValue, 0);
				
				cmd.SetComputeBufferParam(
					ComputeShader, 
					ComputeKernelId.MipMapFinalize, 
					"g_ObjectMipMap_consume",
					g_Object_MipmapLevelA);
				
				cmd.SetComputeBufferParam(
					ComputeShader, 
					ComputeKernelId.MipMapFinalize, 
					"g_ObjectToAtlasProperties",
					g_ObjectToAtlasProperties);
				
				cmd.SetComputeBufferParam(
					ComputeShader, 
					ComputeKernelId.MipMapFinalize, 
					"g_ObjectMipMapCounterValue",
					g_ObjectMipMapCounterValue);
				
				cmd.DispatchCompute(ComputeShader, ComputeKernelId.MipMapFinalize, 1, 1, 1);

				// =====================================================================================================
				// PackAtlas
				// =====================================================================================================

				// LogTrace("PackAtlas...");

				cmd.SetComputeIntParam(ComputeShader, "g_totalObjectsInView", VisibleObjects + 1);
				cmd.SetComputeIntParam(ComputeShader, "g_atlasAxisSize", AtlasAxisSize);

				cmd.SetComputeBufferParam(ComputeShader, ComputeKernelId.AtlasPacking, "g_ObjectToAtlasProperties",
					g_ObjectToAtlasProperties);

				cmd.DispatchCompute(ComputeShader, ComputeKernelId.AtlasPacking, 1, 1, 1);

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

		static class CsKernels {
			public static int ExtractVisiblity;
		}
	}

	class TexelSpaceShadingPass : ScriptableRenderPass {
		public RenderTexture TargetAtlas;

		readonly ShaderTagId texelSpacePass = new ShaderTagId("Texel Space Pass");

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

			DrawingSettings drawingSettings =
				CreateDrawingSettings(texelSpacePass, ref renderingData, SortingCriteria.OptimizeStateChanges);
			FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.all);
			context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filterSettings);

		#endregion
		}
	}

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

#region TexelSpaceRenderHelperInterface

	public static TexelSpaceRenderFeature instance;
	public List<TexelSpaceRenderHelper> visibleObjects = new List<TexelSpaceRenderHelper>();

	public void AddObject(TexelSpaceRenderHelper texelSpaceRenderHelper) {
		visibleObjects.Add(texelSpaceRenderHelper);
	}

#endregion
}