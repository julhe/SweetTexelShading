using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Debug = UnityEngine.Debug;

public class TexelSpaceRenderFeature : ScriptableRendererFeature {
	[Range(8, 13)] public int AtlasSizeExponent = 10;
	public float AtlasResolutionScale = 1024f;
	[Range(0.125f, 1f)] public float VisiblityPassScale = 1f;
	public ComputeShader TssComputeShader;

	public VisibilitySource VisiblityMode;
	TexelSpaceShadingPass texelSpaceShadingPass;


	public enum VisibilitySource {
		GpuExtraPass,
		GpuWithVistaPass,
		CpuHeuristic,
	}
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

	ComputeBuffer g_ObjectToAtlasProperties;
	public override void Create() {
		instance = this;
		
		g_ObjectToAtlasProperties = new ComputeBuffer(
			MaximalObjectsPerView, 
			sizeof(uint) + sizeof(uint) + sizeof(float) * 4);

		
		texelSpaceShadingPass = new TexelSpaceShadingPass {
			renderPassEvent = RenderPassEvent.BeforeRenderingOpaques
		};

		visibilityPass = new VisibilityPass {
			renderPassEvent = RenderPassEvent.BeforeRenderingPrepasses
		};
		RenderTextureCreateOrChange(ref VistaAtlasA, AtlasSizeExponent);
	}


	struct ObjectInAtlas {
		public int desiredExponent, originalIndex;
	}
	List<ObjectInAtlas> visibleObjectDesiredMipMap = new List<ObjectInAtlas>();
	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
		visibilityPass.ComputeShader = TssComputeShader;
		visibilityPass.AtlasResolutionScale = AtlasResolutionScale;
		visibilityPass.AtlasSizeExponent = AtlasSizeExponent;
		visibilityPass.AtlasAxisSize = 1 << AtlasSizeExponent;
		visibilityPass.VisiblityPassScale = VisiblityPassScale;
		visibilityPass.VisiblityComputeOnTheFly = VisiblityMode;
		visibilityPass.g_ObjectToAtlasProperties = g_ObjectToAtlasProperties;
		visibilityPass.Initialize();
		if (visibilityPass.IsReady) {
			if (VisiblityMode != VisibilitySource.CpuHeuristic) {
				// Visiblity Pass
				// =============================================================================================================
				for (int i = 0; i < visibleObjects.Count; i++) {
					visibleObjects[i].SetAtlasProperties(i + 1); //objectID 0 is reserved for "undefined"
				}

				visibilityPass.VisibleObjects = visibleObjects.Count;
				renderer.EnqueuePass(visibilityPass);
			}
			else {
				// generate atlas rects on cpu
				visibleObjectDesiredMipMap.Clear();
				int atlasAxisSize = 1 << AtlasSizeExponent;
				int atlasTexelSize = atlasAxisSize * atlasAxisSize;

				for (int i = 0; i < visibleObjects.Count; i++) {
					int estimatedMipmap = visibleObjects[i].GetEstmatedMipMapLevel(
						renderingData.cameraData.camera,
						atlasTexelSize);
					// GetEstmatedMipMapLevel calculates the mipmap starting from 0
					estimatedMipmap = AtlasSizeExponent - Mathf.Max(estimatedMipmap, 0);

					estimatedMipmap = Mathf.Min(8, estimatedMipmap);
					visibleObjectDesiredMipMap.Add(new ObjectInAtlas(){desiredExponent = estimatedMipmap, originalIndex = i});
				}

				visibleObjectDesiredMipMap = visibleObjectDesiredMipMap.OrderBy(x => x.desiredExponent).ToList();
				int atlasCursor = 0; //the current place where we are inserting into the atlas
				foreach (ObjectInAtlas objectInAtlas in visibleObjectDesiredMipMap) {
					int objectTilesAxis = (1 << objectInAtlas.desiredExponent) / (int) ATLAS_TILE_SIZE;
					int objectTilesTotal = objectTilesAxis * objectTilesAxis;
					Vector4 atlasRect = GetTextureRect((uint) atlasCursor, (uint) objectTilesAxis);
					Vector4 textureRectInAtlas = GetUVToAtlasScaleOffset(atlasRect) / (float) atlasAxisSize;

					atlasCursor += objectTilesTotal;
					visibleObjects[objectInAtlas.originalIndex].SetAtlasScaleOffset(textureRectInAtlas);
				}

				
				
			}


			// Shading Pass
			// =============================================================================================================
			
			texelSpaceShadingPass.g_ObjectToAtlasProperties = g_ObjectToAtlasProperties;
			texelSpaceShadingPass.VisiblityComputeOnTheFly = VisiblityMode;
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
		public float AtlasResolutionScale, VisiblityPassScale = 1f;
		public int AtlasSizeExponent = 10;
		public VisibilitySource VisiblityComputeOnTheFly;

		public ComputeBuffer
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
				InitalizePrimitiveVisiblity,
				ResetSizeExponent;
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
			ComputeKernelId.ResetSizeExponent = ComputeShader.FindKernel("ResetSizeExponent");

			g_PrimitiveVisibility = new ComputeBuffer(
				MaximalObjectsPerView * (MAX_PRIMITIVES_PER_OBJECT / 32), 
				sizeof(int));

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
			if (VisiblityComputeOnTheFly == VisibilitySource.GpuWithVistaPass) {
				// we wrote to g_ObjectToAtlasProperties in the last frame by pixel shader, so it's clean up time!
				cmd.ClearRandomWriteTargets();
			}
			else
			{
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
				// Copy "AtlasData" to "Prev AtlasData" (TODO: just swap the reference them!)
				// =====================================================================================================
				if (VisiblityComputeOnTheFly == VisibilitySource.GpuExtraPass) {
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
				}

				cmd.SetComputeBufferParam(
					ComputeShader, 
					ComputeKernelId.InitalizePrimitiveVisiblity, 
					g_PrimitiveVisibilityID,
					g_PrimitiveVisibility);
				
				cmd.DispatchCompute(
					ComputeShader, 
					ComputeKernelId.InitalizePrimitiveVisiblity,
					Mathf.CeilToInt(g_PrimitiveVisibility.count / (float) 64), 
					1, 
					1);
				
				if (VisiblityComputeOnTheFly == VisibilitySource.GpuExtraPass) {
					Shader.EnableKeyword("TSS_VisiblityOnTheFly");
					//cmd.SetGlobalBuffer("g_ObjectToAtlasPropertiesRW", g_ObjectToAtlasProperties);
				}
				else {
					Shader.DisableKeyword("TSS_VisiblityOnTheFly");
				}
				
				LogTrace("Visibility Pass: Execute Cmd: " + cmd.name);
				context.ExecuteCommandBuffer(cmd);
				CommandBufferPool.Release(cmd);
			}

			if (VisiblityComputeOnTheFly == VisibilitySource.GpuExtraPass) {
				// Render Visiblity Pass
				DrawingSettings drawingSettings =
					CreateDrawingSettings(visibilityPass, ref renderingData, SortingCriteria.OptimizeStateChanges);
				FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.opaque);
				LogTrace("Visibility Pass: DrawRenderers");
				context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filterSettings);
			}

			{
				// =====================================================================================================
				// VISIBLITY DISSOLVE PASS
				// maps the previous rendered data into usable buffers
				// =====================================================================================================
				CommandBuffer cmd = CommandBufferPool.Get("Visibility-Pass Post");
				if (VisiblityComputeOnTheFly == VisibilitySource.GpuWithVistaPass) {
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
				
					cmd.DispatchCompute(
						ComputeShader, 
						ComputeKernelId.MipMapFinalize, 
						1, 
						1, 
						1);
				}
				// =====================================================================================================
				// PackAtlas
				// =====================================================================================================

				cmd.SetComputeIntParam(ComputeShader, "g_totalObjectsInView", VisibleObjects + 1);
				cmd.SetComputeIntParam(ComputeShader, "g_atlasAxisSize", AtlasAxisSize);

				cmd.SetComputeBufferParam(
					ComputeShader, 
					ComputeKernelId.AtlasPacking, 
					"g_ObjectToAtlasProperties",
					g_ObjectToAtlasProperties);

				cmd.DispatchCompute(
					ComputeShader, 
					ComputeKernelId.AtlasPacking, 
					1, 1, 1);
				
				if (VisiblityComputeOnTheFly == VisibilitySource.GpuWithVistaPass) {
					// reset the size exponents, since they gonna be written soon by the vista pass
					cmd.SetComputeBufferParam(
						ComputeShader, 
						ComputeKernelId.ResetSizeExponent, 
						"g_ObjectToAtlasProperties", 
						g_ObjectToAtlasProperties);
					
					cmd.DispatchCompute(
						ComputeShader, 
						ComputeKernelId.ResetSizeExponent, 
						g_ObjectToAtlasProperties.count / Mathf.CeilToInt(MaximalObjectsPerView / (float) 64.0), 1, 1);
				}
				
				cmd.SetGlobalBuffer(g_PrimitiveVisibilityID, g_PrimitiveVisibility);
				//cmd.SetGlobalBuffer("g_prev_ObjectToAtlasProperties", g_prev_ObjectToAtlasProperties);
				LogTrace("Visibility Pass: execute Cmd " + cmd.name);
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
		public ComputeBuffer g_ObjectToAtlasProperties;
		public VisibilitySource VisiblityComputeOnTheFly;

		readonly ShaderTagId texelSpacePass = new ShaderTagId("Texel Space Pass");

		public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
			ConfigureTarget(TargetAtlas);
			ConfigureClear(ClearFlag.Color, Color.clear);
			// TODO:
			//cmd.SetGlobalTexture("g_prev_VistaAtlas", target_atlasA ? g_VistaAtlas_B : g_VistaAtlas_A);
			cmd.SetGlobalTexture("g_VistaAtlas", TargetAtlas);
			cmd.SetGlobalBuffer("g_ObjectToAtlasProperties", g_ObjectToAtlasProperties);
		}

		public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {

		#region Render objects to atlas (Texel Shade Pass)

			DrawingSettings drawingSettings =
				CreateDrawingSettings(texelSpacePass, ref renderingData, SortingCriteria.OptimizeStateChanges);
			FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.all);
			context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filterSettings);

			if (VisiblityComputeOnTheFly == VisibilitySource.GpuWithVistaPass) {
				// soon, the vista pass will be rendern, so we setup the g_ObjectToAtlasProperties
				CommandBuffer cmd = CommandBufferPool.Get("Texel-Shading Pass Post");
				cmd.SetGlobalBuffer("g_ObjectToAtlasPropertiesRW", g_ObjectToAtlasProperties);
				cmd.SetRandomWriteTarget(1, g_ObjectToAtlasProperties);
				context.ExecuteCommandBuffer(cmd);
				CommandBufferPool.Release(cmd);
			}

		#endregion
		}
		
		
	}

	static void LogTrace(System.Object obj) {
		Debug.Log(obj);
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

#region CPUAtlasPacking

	const uint ATLAS_TILE_SIZE = 128;
	uint2 GetTilePosition(uint index)
	{
		return new uint2(DecodeMorton2X(index), DecodeMorton2Y(index));
	}

	float4 GetTextureRect(uint index, uint tilesPerAxis)
	{
		float2 atlasPosition_tileSpace = GetTilePosition(index);
		float2 min = atlasPosition_tileSpace * ATLAS_TILE_SIZE;
		float2 max = min + tilesPerAxis * ATLAS_TILE_SIZE;

		return new float4(min, max);
	}

	float4 GetUVToAtlasScaleOffset(float4 atlasPixelSpace)
	{
		return new float4(atlasPixelSpace.zw - atlasPixelSpace.xy, atlasPixelSpace.xy);
	}
	
	//source: https://fgiesen.wordpress.com/2009/12/13/decoding-morton-codes/
// "Insert" a 0 bit after each of the 16 low bits of x
	uint Part1By1(uint x)
	{
		x &= 0x0000ffff;                  // x = ---- ---- ---- ---- fedc ba98 7654 3210
		x = (x ^ (x << 8)) & 0x00ff00ff; // x = ---- ---- fedc ba98 ---- ---- 7654 3210
		x = (x ^ (x << 4)) & 0x0f0f0f0f; // x = ---- fedc ---- ba98 ---- 7654 ---- 3210
		x = (x ^ (x << 2)) & 0x33333333; // x = --fe --dc --ba --98 --76 --54 --32 --10
		x = (x ^ (x << 1)) & 0x55555555; // x = -f-e -d-c -b-a -9-8 -7-6 -5-4 -3-2 -1-0
		return x;
	}

// Inverse of Part1By1 - "delete" all odd-indexed bits
	uint Compact1By1(uint x)
	{
		x &= 0x55555555;                  // x = -f-e -d-c -b-a -9-8 -7-6 -5-4 -3-2 -1-0
		x = (x ^ (x >> 1)) & 0x33333333; // x = --fe --dc --ba --98 --76 --54 --32 --10
		x = (x ^ (x >> 2)) & 0x0f0f0f0f; // x = ---- fedc ---- ba98 ---- 7654 ---- 3210
		x = (x ^ (x >> 4)) & 0x00ff00ff; // x = ---- ---- fedc ba98 ---- ---- 7654 3210
		x = (x ^ (x >> 8)) & 0x0000ffff; // x = ---- ---- ---- ---- fedc ba98 7654 3210
		return x;
	}


	uint DecodeMorton2X(uint code) { return Compact1By1(code >> 0); }
	uint DecodeMorton2Y(uint code) { return Compact1By1(code >> 1); }

#endregion
}