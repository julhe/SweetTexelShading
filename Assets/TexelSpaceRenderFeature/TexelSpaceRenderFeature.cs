using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Random = UnityEngine.Random;

public class TexelSpaceRenderFeature : ScriptableRendererFeature {
	//NOTES: -Changing the RenderingLayerMask from the RenderFeature doesn't work. Probably because object culling happened before.
	
	[Range(8, 13)] public int AtlasSizeExponent = 10;
	[Range(1, 32)] public uint AtlasTimeSlicing;
	[Range(1, 10)] public int MinResolution = 2, MaxResolution = 12;
	
	[Header("Debug"), Range(-1, 10)]
	public int OverrideTimeSliceIndex = -1;

	[Range(0,1)] public float DebugViewOverlay = 0f;
	public bool ClearAtlasWithRed;
	public bool AlwaysClearAtlas;

	List<TexelSpaceRenderHelper> objectsInCurrentAtlas = new List<TexelSpaceRenderHelper>();

	byte renderedFrames;
	
	TexelSpaceShadingPass texelSpaceShadingPass;
	PresentPass presentPass;
	FallbackPass fallbackPass; 
	RenderTexture vistaAtlas;
	public int AtlasSizeAxis => 1 << AtlasSizeExponent;

	public override void Create() {
		instance = this;
		

		texelSpaceShadingPass = new TexelSpaceShadingPass {
			renderPassEvent = RenderPassEvent.BeforeRenderingOpaques,
			Parent = this,
		};

		presentPass = new PresentPass {
			Parent = this,
			renderPassEvent = RenderPassEvent.AfterRenderingOpaques
		};

		fallbackPass = new FallbackPass() {
			renderPassEvent = RenderPassEvent.AfterRenderingOpaques
		};

		RenderTextureCreateOrChange(ref vistaAtlas, AtlasSizeExponent);
	}

	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {

		RenderTextureCreateOrChange(ref vistaAtlas, AtlasSizeExponent);
		
		unchecked {
			renderedFrames++;
		}

		uint timeSlicedFrameIndex = renderedFrames % Math.Max(AtlasTimeSlicing + 1, 1);

	#if UNITY_EDITOR
		if (OverrideTimeSliceIndex != -1) {
			timeSlicedFrameIndex = (uint) OverrideTimeSliceIndex;
		}
	#endif
		
		bool shouldGenerateNewAtlas = timeSlicedFrameIndex == 0;
		texelSpaceShadingPass.ShouldClearAltas = true;
		if (shouldGenerateNewAtlas) {
			// Estimate the size of the objects in the atlas
			// =============================================================================================================
			objectsInCurrentAtlas.Clear();
			int atlasTexelSize = AtlasSizeAxis * AtlasSizeAxis;

			int atlasTilesTotal = AtlasSizeAxis / (int) ATLAS_TILE_SIZE;
			atlasTilesTotal *= atlasTilesTotal;
			int atlasTilesOccupied = 0;

			//TODO: don't add objects which wouldn't fit into the atlas.
			for (int i = 0; i < visibleObjects.Count; i++) {
				int estimatedShadinDensityExponent = visibleObjects[i].GetEstimatedMipMapLevel(
					renderingData.cameraData.camera,
					atlasTexelSize);
				// GetEstmatedMipMapLevel calculates the mipmap starting from 0
				estimatedShadinDensityExponent =
					AtlasSizeExponent - Mathf.Max(estimatedShadinDensityExponent, 0);
				//estimatedMipmap = Mathf.Clamp(estimatedMipmap, MinResolution, MaxResolution);
				if (estimatedShadinDensityExponent < MinResolution ||
				    estimatedShadinDensityExponent > MaxResolution) {
					continue;
				}

				int objectTilesPerAxis = (1 << estimatedShadinDensityExponent) / (int) ATLAS_TILE_SIZE;
				int objectTilesTotal = objectTilesPerAxis * objectTilesPerAxis;
				if (atlasTilesOccupied + objectTilesTotal > atlasTilesTotal) {
					// if the atlas is full, don't add any more objects
					continue;
				}

				atlasTilesOccupied += objectTilesTotal;

				visibleObjects[i].DesiredShadingDensityExponent = estimatedShadinDensityExponent;
				objectsInCurrentAtlas.Add(visibleObjects[i]);
			}

			objectsInCurrentAtlas =
				objectsInCurrentAtlas.OrderBy(x => x.DesiredShadingDensityExponent).ToList();
			// Pack the atlas from the previous calculated objects
			// =============================================================================================================
			int atlasCursor = 0; //the current place where we are inserting into the atlas
			for (int i = 0; i < objectsInCurrentAtlas.Count; i++) {
				TexelSpaceRenderHelper objectInAtlas = objectsInCurrentAtlas[i];
				int occupiedTilesPerAxis =
					(1 << objectInAtlas.DesiredShadingDensityExponent) / (int) ATLAS_TILE_SIZE;
				int occupiedTilesTotal = occupiedTilesPerAxis * occupiedTilesPerAxis;
				Vector4 atlasRect = GetTextureRect((uint) atlasCursor, (uint) occupiedTilesPerAxis);
				Vector4 textureRectInAtlas = GetUVToAtlasScaleOffset(atlasRect) / AtlasSizeAxis;

				atlasCursor += occupiedTilesTotal;
				objectInAtlas.SetAtlasScaleOffset(textureRectInAtlas);

				// generate a layermask to distribute the gpu-workload among multiple frames
				//TODO: take the shading density into account to balance the gpu load between time slices
				int maxLayerMask = Math.Max((int) AtlasTimeSlicing - 1, 0);
				objectInAtlas.TimeSliceIndex = Random.Range(0, maxLayerMask);
				objectInAtlas.SetAtlasProperties(i);
			}
		}
		else {
			texelSpaceShadingPass.ShouldClearAltas = false;
		}

		foreach (TexelSpaceRenderHelper texelSpaceRenderHelper in visibleObjects) {
			if (objectsInCurrentAtlas.Contains(texelSpaceRenderHelper)) {
				bool willBeRenderedThisFrame = texelSpaceRenderHelper.TimeSliceIndex == timeSlicedFrameIndex;
				bool isRenderedInAtlas = texelSpaceRenderHelper.TimeSliceIndex < timeSlicedFrameIndex;
				// every object that hasn't been rendered into the atlas yet or isn't part of it, should use the fallback
				if (willBeRenderedThisFrame) {
					texelSpaceRenderHelper.SetTexelSpaceObjectState(TexelSpaceObjectState
						.InAtlasRenderingThisFrame);
				}
				else {
					texelSpaceRenderHelper.SetTexelSpaceObjectState(isRenderedInAtlas
						? TexelSpaceObjectState.InAtlasIsRendered
						: TexelSpaceObjectState.InAtlasNotYetRendered);
				}
			}
			else {
				// object came into visibility while the atlas was still rendering or has a too high shading density. 
				texelSpaceRenderHelper.SetTexelSpaceObjectState(TexelSpaceObjectState.NotInAtlas);
			}
		}
		

		// Enqueue Shading Pass
		// =============================================================================================================

		texelSpaceShadingPass.TargetAtlas = vistaAtlas;
		renderer.EnqueuePass(texelSpaceShadingPass);

		renderer.EnqueuePass(presentPass);
		renderer.EnqueuePass(fallbackPass);

		visibleObjects.Clear();
	}

	static void LogTrace(object obj) {
		Debug.Log(obj);
	}




	class TexelSpaceShadingPass : ScriptableRenderPass {
		public TexelSpaceRenderFeature Parent;

		readonly ShaderTagId lightModeTexelSpacePass = new ShaderTagId("Texel Space Pass");
		public bool ShouldClearAltas;
		public RenderTexture TargetAtlas;
	

		public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
			ConfigureTarget(TargetAtlas);
			ShouldClearAltas |= Parent.AlwaysClearAtlas;
			ConfigureClear(
				ShouldClearAltas ? ClearFlag.Color : ClearFlag.None, 
				Parent.ClearAtlasWithRed ? Color.red : Color.clear);

			cmd.SetGlobalTexture("g_VistaAtlas", TargetAtlas);

		}

		public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
			CommandBuffer cmd = CommandBufferPool.Get("Texel-Shading Pass Pre");
			cmd.SetGlobalFloat("_Tss_DebugView", Parent.DebugViewOverlay);
			context.ExecuteCommandBuffer(cmd);
			CommandBufferPool.Release(cmd);
			
			// render the objects into the atlas
			DrawingSettings drawingSettings =
				CreateDrawingSettings(lightModeTexelSpacePass, ref renderingData, SortingCriteria.None);

			FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.all) {
				renderingLayerMask = TexelSpaceObjectState.InAtlasRenderingThisFrame.ToRenderingLayerMask()
			};

			context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filterSettings);
		}
	}
	// In-Atlas pass
	//==========================================================================================
	class PresentPass : ScriptableRenderPass {
		public TexelSpaceRenderFeature Parent;
		readonly ShaderTagId lightModePresentPass = new ShaderTagId("TSS Present-Pass");
		public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
			cmd.SetGlobalTexture("g_VistaAtlas", Parent.vistaAtlas);
		}
		
		public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {

			CommandBuffer cmd = CommandBufferPool.Get("Texel-Shading Pass Post");
			context.ExecuteCommandBuffer(cmd);
			CommandBufferPool.Release(cmd);
			DrawingSettings drawingSettings =
				CreateDrawingSettings(lightModePresentPass, ref renderingData, SortingCriteria.CommonOpaque);
		
			FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.all) {
				renderingLayerMask = TexelSpaceObjectState.InAtlasIsRendered.ToRenderingLayerMask()
			};
		
			context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filterSettings);
		}
	}

	// Fallback-Forward pass
	//==========================================================================================
	class FallbackPass : ScriptableRenderPass {
		readonly ShaderTagId forwardFallback = new ShaderTagId("Forward Fallback");

			public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {

			DrawingSettings drawingSettings =
				CreateDrawingSettings(forwardFallback, ref renderingData, SortingCriteria.CommonOpaque);

			FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.all) {
				renderingLayerMask =
					TexelSpaceObjectState.InAtlasNotYetRendered.ToRenderingLayerMask()
					| TexelSpaceObjectState.NotInAtlas.ToRenderingLayerMask()
					| TexelSpaceObjectState.InAtlasRenderingThisFrame.ToRenderingLayerMask()
			};

			context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filterSettings);
		
		}
	}

	protected override void Dispose(bool disposing) {
		base.Dispose(disposing);
		if (vistaAtlas) {
			vistaAtlas.Release();
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
	readonly List<TexelSpaceRenderHelper> visibleObjects = new List<TexelSpaceRenderHelper>();

	public void AddObject(TexelSpaceRenderHelper texelSpaceRenderHelper) {
		if (isActive) {
			visibleObjects.Add(texelSpaceRenderHelper);
		}
	}

#endregion

#region CPUAtlasPacking

	const uint ATLAS_TILE_SIZE = 128;

	uint2 GetTilePosition(uint index) {
		return new uint2(DecodeMorton2X(index), DecodeMorton2Y(index));
	}

	float4 GetTextureRect(uint index, uint tilesPerAxis) {
		float2 atlasPosition_tileSpace = GetTilePosition(index);
		float2 min = atlasPosition_tileSpace * ATLAS_TILE_SIZE;
		float2 max = min + tilesPerAxis * ATLAS_TILE_SIZE;

		return new float4(min, max);
	}

	float4 GetUVToAtlasScaleOffset(float4 atlasPixelSpace) {
		return new float4(atlasPixelSpace.zw - atlasPixelSpace.xy, atlasPixelSpace.xy);
	}

	//source: https://fgiesen.wordpress.com/2009/12/13/decoding-morton-codes/
// "Insert" a 0 bit after each of the 16 low bits of x
	uint Part1By1(uint x) {
		x &= 0x0000ffff; // x = ---- ---- ---- ---- fedc ba98 7654 3210
		x = (x ^ (x << 8)) & 0x00ff00ff; // x = ---- ---- fedc ba98 ---- ---- 7654 3210
		x = (x ^ (x << 4)) & 0x0f0f0f0f; // x = ---- fedc ---- ba98 ---- 7654 ---- 3210
		x = (x ^ (x << 2)) & 0x33333333; // x = --fe --dc --ba --98 --76 --54 --32 --10
		x = (x ^ (x << 1)) & 0x55555555; // x = -f-e -d-c -b-a -9-8 -7-6 -5-4 -3-2 -1-0
		return x;
	}

// Inverse of Part1By1 - "delete" all odd-indexed bits
	uint Compact1By1(uint x) {
		x &= 0x55555555; // x = -f-e -d-c -b-a -9-8 -7-6 -5-4 -3-2 -1-0
		x = (x ^ (x >> 1)) & 0x33333333; // x = --fe --dc --ba --98 --76 --54 --32 --10
		x = (x ^ (x >> 2)) & 0x0f0f0f0f; // x = ---- fedc ---- ba98 ---- 7654 ---- 3210
		x = (x ^ (x >> 4)) & 0x00ff00ff; // x = ---- ---- fedc ba98 ---- ---- 7654 3210
		x = (x ^ (x >> 8)) & 0x0000ffff; // x = ---- ---- ---- ---- fedc ba98 7654 3210
		return x;
	}

	uint DecodeMorton2X(uint code) {
		return Compact1By1(code >> 0);
	}

	uint DecodeMorton2Y(uint code) {
		return Compact1By1(code >> 1);
	}

#endregion
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

		rt = new RenderTexture(
			sizeXy,
			sizeXy,
			1,
			DefaultFormat.LDR) {
			useMipMap = false,
			wrapMode = TextureWrapMode.Clamp,
			hideFlags = HideFlags.DontSave
		};
		rt.Create();
	}
}

public enum TexelSpaceObjectState {
	NotInAtlas,
	InAtlasNotYetRendered,
	InAtlasRenderingThisFrame,
	InAtlasIsRendered
}

public static class StaticUtilities {
	public static uint ToRenderingLayerMask(this TexelSpaceObjectState texelSpaceObjectState) {
		return texelSpaceObjectState switch {
			TexelSpaceObjectState.NotInAtlas => 1 << 0,
			TexelSpaceObjectState.InAtlasNotYetRendered => 1 << 1,
			TexelSpaceObjectState.InAtlasRenderingThisFrame => 1 << 2,
			TexelSpaceObjectState.InAtlasIsRendered => 1 << 3,
			_ => throw new ArgumentOutOfRangeException(nameof(texelSpaceObjectState), texelSpaceObjectState, null)
		};
	}
}