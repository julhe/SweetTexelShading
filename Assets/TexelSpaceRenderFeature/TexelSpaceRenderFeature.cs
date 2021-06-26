using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Random = UnityEngine.Random;
//TODO: use [DisallowMultipleRendererFeature], but its currently internal -> bug report to unity!
public class TexelSpaceRenderFeature : ScriptableRendererFeature {
	//NOTES: -Changing the RenderingLayerMask from the RenderFeature doesn't work. Probably because object culling happened before.
	//		 -It also seems not to be possible to run a new culling inside the pass.
	//		 - Potential Bug: Object Foo gets placed into atlas -> get's out of view -> doesn't get rendered when it frame index is rendering -> comes back into view -> is black.
	
	[Range(8, 13)] public int AtlasSizeExponent = 10;
	[Range(-2f, 2f)] public float ShadingExponentBias = 0f;
	[Range(1, 32)] public uint AtlasTimeSlicing;
	int MaxResolution => AtlasSizeAxis - 1;
	
	[Header("Debug"), Range(-1, 10)]
	public int OverrideTimeSliceIndex = -1;
	[Range(0,1)] public float DebugViewOverlay = 0f;
	public bool ClearAtlasWithRed;
	public bool AlwaysClearAtlas;
	public bool Pause;
	[Header("Debug Outputs")] 
	public uint CurrentTimeSliceIndex;
	[BitMaskPropertyAttribute]
	public int InAtlasRenderedLayerMask, ToBeRenderedThisFrameMask, FallbackRenderLayerMask;

	List<TexelSpaceRenderObject> objectsInCurrentAtlas = new List<TexelSpaceRenderObject>();

	uint renderedFrames;
	
	TexelSpaceShadingPass texelSpaceShadingPass;
	PresentPass presentPass;
	RenderTexture vistaAtlas;
	int AtlasSizeAxis => 1 << AtlasSizeExponent;
	const uint NotInAtlasRenderMask = 1 << 30;

	public override void Create() {
		
		Debug.Assert(instance == null || instance == this, $"A instance of the {nameof(TexelSpaceRenderFeature)} is already active. There should be only one {nameof(TexelSpaceRenderFeature)} per project.");
		instance = this;
		
		texelSpaceShadingPass ??= new TexelSpaceShadingPass {
			renderPassEvent = RenderPassEvent.BeforeRenderingOpaques,
			Parent = this,
		};

		presentPass ??= new PresentPass {
			Parent = this,
			renderPassEvent = RenderPassEvent.AfterRenderingOpaques
		};

		RenderTextureCreateOrChange(ref vistaAtlas, AtlasSizeExponent);
	}

	int lastRenderedFrameByUnity = -1;
	uint lastRenderedFrameByCounter = 0;
	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {

		RenderTextureCreateOrChange(ref vistaAtlas, AtlasSizeExponent);

		lastRenderedFrameByUnity = Time.renderedFrameCount;
		if (!Pause) {
			unchecked {
				renderedFrames++;
			}
		}

		uint timeSlicedFrameIndex = renderedFrames % Math.Max(AtlasTimeSlicing, 1);
		
	#if UNITY_EDITOR
		if (OverrideTimeSliceIndex != -1) {
			timeSlicedFrameIndex = (uint) OverrideTimeSliceIndex;
		}
	#endif
		CurrentTimeSliceIndex = timeSlicedFrameIndex;
		bool shouldGenerateNewAtlas = timeSlicedFrameIndex == 0;
		texelSpaceShadingPass.ShouldClearAltas = true;
		if (shouldGenerateNewAtlas) {
			// Estimate the size of the objects in the atlas
			// =============================================================================================================
			objectsInCurrentAtlas.Clear();
			int atlasTexelSize = AtlasSizeAxis * AtlasSizeAxis;

			int atlasTilesTotal = AtlasSizeAxis / (int) AtlasTileSize;
			atlasTilesTotal *= atlasTilesTotal;
			int atlasTilesOccupied = 0;

			
			for (int i = 0; i < visibleObjects.Count; i++) {
				float estimatedShadinDensityExponentInv = visibleObjects[i].GetEstimatedMipMapLevel(
					renderingData.cameraData.camera,
					atlasTexelSize);

				bool addToAtlas = estimatedShadinDensityExponentInv >= 0;

				// GetEstmatedMipMapLevel calculates the mipmap starting from 0
				int estimatedShadingDensityExponent = Mathf.RoundToInt(ShadingExponentBias + (AtlasSizeExponent - estimatedShadinDensityExponentInv));
				
				if (estimatedShadingDensityExponent < MinAtlasObjectSizeExponent ||
				    estimatedShadingDensityExponent > MaxResolution) {
					addToAtlas = false;
				}

				int objectTilesPerAxis = (1 << estimatedShadingDensityExponent) / (int) AtlasTileSize;
				int objectTilesTotal = objectTilesPerAxis * objectTilesPerAxis;
				if (atlasTilesOccupied + objectTilesTotal > atlasTilesTotal) {
					// if the atlas is full, don't add any more objects
					addToAtlas = false;
				}
				else {
					atlasTilesOccupied += objectTilesTotal;
				}

				visibleObjects[i].DesiredShadingDensityExponent = estimatedShadingDensityExponent;
				if (addToAtlas) {
					objectsInCurrentAtlas.Add(visibleObjects[i]);
				}
				else {
					visibleObjects[i].SetAtlasProperties(-1, NotInAtlasRenderMask);
				}
			}

			//TODO: make garbage free
			objectsInCurrentAtlas =
				objectsInCurrentAtlas.OrderBy(x => -x.DesiredShadingDensityExponent).ToList();
			
			// Pack the atlas from the previous calculated objects
			// =============================================================================================================
			int atlasCursor = 0; //the current place where we are inserting into the atlas
			for (int i = 0; i < objectsInCurrentAtlas.Count; i++) {
				TexelSpaceRenderObject objectInAtlas = objectsInCurrentAtlas[i];
				int occupiedTilesPerAxis =
					(1 << objectInAtlas.DesiredShadingDensityExponent) / (int) AtlasTileSize;
				int occupiedTilesTotal = occupiedTilesPerAxis * occupiedTilesPerAxis;
				Vector4 atlasRect = GetTextureRect((uint) atlasCursor, (uint) occupiedTilesPerAxis);
				bool isValidRect = atlasRect.x < atlasRect.z && atlasRect.y < atlasRect.w;
				
				if (!isValidRect) {
					Debug.LogError("Malformed atlas rect.");
				}
				Vector4 textureRectInAtlas = GetUVToAtlasScaleOffset(atlasRect) / AtlasSizeAxis;

				atlasCursor += occupiedTilesTotal;
				//Debug.Assert(textureRectInAtlas.x * textureRectInAtlas.y > 0, "Allocating a object in the atlas with zero space");
		
				objectInAtlas.SetAtlasScaleOffset(textureRectInAtlas);

				// generate a layermask to distribute the gpu-workload among multiple frames
				//TODO: take the shading density into account to balance the gpu load between time slices
				// use AtlasTimeSlcing - 1, because the frameIndex can never reach AtlatTimeSlicing due to Modulo Operator
				int maxLayerMask = Math.Max((int) AtlasTimeSlicing - 1, 0);

				objectInAtlas.TimeSliceIndex = Random.Range(0, maxLayerMask); 
				objectInAtlas.SetAtlasProperties(i, (uint) 1 << objectInAtlas.TimeSliceIndex);
			}

			// NOTE: Since the URP did the Culling already, the RenderingLayers are already locked in.
			// So we can't render anything in the frame where we packed the atlas.
			// (It doesn't seem to be possible to run the culling a second time)
			
			// TODO: clear the atlas after the present pass
			presentPass.FallbackRenderLayerMask = uint.MaxValue;
			presentPass.FromAtlasRenderLayerMask = 0;
			texelSpaceShadingPass.RenderLayerMask = 0;

			FallbackRenderLayerMask = unchecked((int) presentPass.FallbackRenderLayerMask);
			ToBeRenderedThisFrameMask = 0;
			InAtlasRenderedLayerMask = 0;
			
		}
		else {
			texelSpaceShadingPass.ShouldClearAltas = false;
			texelSpaceShadingPass.TargetAtlas = vistaAtlas;
			
			InAtlasRenderedLayerMask = 0;
			
			
			for (int i = 0; i < timeSlicedFrameIndex - 1; i++) {
				InAtlasRenderedLayerMask |= (1 << i);
			}
			
			ToBeRenderedThisFrameMask = 1 << (int) (timeSlicedFrameIndex - 1); // a frame index of 0 == initialisation phase (see NOTE above)
			presentPass.FromAtlasRenderLayerMask =
				unchecked((uint) (InAtlasRenderedLayerMask | ToBeRenderedThisFrameMask));
			
			FallbackRenderLayerMask = unchecked((int)  ~presentPass.FromAtlasRenderLayerMask);
			presentPass.FallbackRenderLayerMask =  unchecked((uint) FallbackRenderLayerMask);

			texelSpaceShadingPass.RenderLayerMask = unchecked((uint) ToBeRenderedThisFrameMask);
		}

		// Enqueue Passes
		// =============================================================================================================
		renderer.EnqueuePass(texelSpaceShadingPass);
		renderer.EnqueuePass(presentPass);

		visibleObjects.Clear();
	}

	static void LogTrace(object obj) {
		Debug.Log(obj);
	}


	static CullingResults DoExtraCull(ScriptableRenderContext context, ref RenderingData renderingData) {
		// NOTE/TODO: it
		var cull = renderingData.cameraData.camera.TryGetCullingParameters(
			renderingData.cameraData.xrRendering,
			out ScriptableCullingParameters scriptableCullingParameters);
		scriptableCullingParameters.cullingOptions |= CullingOptions.ForceEvenIfCameraIsNotActive;
		var newCull = context.Cull(ref scriptableCullingParameters);
		return newCull;
	}


	class TexelSpaceShadingPass : ScriptableRenderPass {
		public TexelSpaceRenderFeature Parent;

		readonly ShaderTagId lightModeTexelSpacePass = new ShaderTagId("Texel Space Pass");
		public uint RenderLayerMask;
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
			var extraCull = DoExtraCull(context, ref renderingData);
			CommandBuffer cmd = CommandBufferPool.Get("Texel-Shading Pass Pre");
			cmd.SetGlobalFloat("_Tss_DebugView", Parent.DebugViewOverlay);
			context.ExecuteCommandBuffer(cmd);
			CommandBufferPool.Release(cmd);
			
			// render the objects into the atlas
			DrawingSettings drawingSettings =
				CreateDrawingSettings(lightModeTexelSpacePass, ref renderingData, SortingCriteria.None);
			drawingSettings.perObjectData = PerObjectData.ReflectionProbes | PerObjectData.Lightmaps | PerObjectData.LightProbe | PerObjectData.LightData | PerObjectData.OcclusionProbe | PerObjectData.ShadowMask;

			FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.all) {
				renderingLayerMask = RenderLayerMask
			};

			context.DrawRenderers(extraCull, ref drawingSettings, ref filterSettings);
		}
	}
	// In-Atlas pass
	//==========================================================================================
	class PresentPass : ScriptableRenderPass {
		public TexelSpaceRenderFeature Parent;
		readonly ShaderTagId lightModePresentPass = new ShaderTagId("TSS Present-Pass");
		readonly ShaderTagId forwardFallback = new ShaderTagId("Forward Fallback");
		public uint FromAtlasRenderLayerMask, FallbackRenderLayerMask;
		public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
			cmd.SetGlobalTexture("g_VistaAtlas", Parent.vistaAtlas);
		}
		
		public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
			
			{
				CommandBuffer cmd = CommandBufferPool.Get("Texel-Shading Pass Post");
				cmd.SetGlobalFloat("_Tss_DebugView", Parent.DebugViewOverlay);
				context.ExecuteCommandBuffer(cmd);
				CommandBufferPool.Release(cmd);
				DrawingSettings drawingSettings =
					CreateDrawingSettings(lightModePresentPass, ref renderingData, SortingCriteria.CommonOpaque);

				FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.all) {
					renderingLayerMask = FromAtlasRenderLayerMask
				};

				context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filterSettings);
			}
			
			{
				DrawingSettings drawingSettings =
					CreateDrawingSettings(forwardFallback, ref renderingData, SortingCriteria.CommonOpaque);

				FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.all) {
					renderingLayerMask = FallbackRenderLayerMask
						
				};

				context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filterSettings);

			}
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
	readonly List<TexelSpaceRenderObject> visibleObjects = new List<TexelSpaceRenderObject>();

	public void AddObject(TexelSpaceRenderObject texelSpaceRenderObject) {
		if (isActive) {
			visibleObjects.Add(texelSpaceRenderObject);
		}
	}

#endregion

#region CPUAtlasPacking

	const int MinAtlasObjectSizeExponent = 6;
	const uint AtlasTileSize = 1 << MinAtlasObjectSizeExponent;

	uint2 GetTilePosition(uint index) {
		return new uint2(DecodeMorton2X(index), DecodeMorton2Y(index));
	}

	float4 GetTextureRect(uint index, uint tilesPerAxis) {
		float2 atlasPosition_tileSpace = GetTilePosition(index);
		float2 min = atlasPosition_tileSpace * AtlasTileSize;
		float2 max = min + tilesPerAxis * AtlasTileSize;

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

		bool needsInitialisation = rt == null;
		if (rt != null && rt.width != sizeXy) {
			rt.Release();
			needsInitialisation = true;
		}

		if (!needsInitialisation) {
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