using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
	//		 - Can't also use CommandBuffer.DrawRenderer, because some shader constants aren't set properly.
	[Range(8, 13)] public int AtlasSizeExponent = 10;
	[Range(-4f, 4f)] public float ShadingExponentBias = 0f;
	public bool ShadingCullBackfacingVertices ;
	[Range(1, 32)] public uint AtlasShadingTimeSlicing;
	public bool ForceFallbackToForward;

	int MaxResolution => AtlasSizeAxis - 1;
	
	[Header("Debug"), Range(-1, 31)]
	public int OverrideTimeSliceIndex = -1;
	[Range(0,1)] public float DebugViewOverlay = 0f;
	public bool ClearAtlasWithRed;
	public bool AlwaysClearAtlas;
	public bool AllowClearAtlas = true;
	public bool Pause;
	public bool ReportAtlasInConsole;
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

	static TexelSpaceRenderObject.TexelSpaceRenderObjectShadingExponentComparer
		TexelSpaceRenderObjectShadingExponentComparer =
			new TexelSpaceRenderObject.TexelSpaceRenderObjectShadingExponentComparer();
	public override void Create() {
		Debug.Assert(Instance == null || Instance == this, $"A instance of the {nameof(TexelSpaceRenderFeature)} is already active. There should be only one {nameof(TexelSpaceRenderFeature)} per project.");
		Instance = this;
		
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



	uint framesSinceLastAtlasRebuild;
	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
		ReportEnabled = ReportAtlasInConsole;
		RenderTextureCreateOrChange(ref vistaAtlas, AtlasSizeExponent);
		
		if (!Pause) {
			unchecked {
				renderedFrames++;
			}
		}

		if (Application.isPlaying) {
			renderedFrames = (uint) Time.renderedFrameCount;
		}
		uint timeSlicedFrameIndex = renderedFrames % Math.Max(AtlasShadingTimeSlicing, 1);
		
	#if UNITY_EDITOR
		if (OverrideTimeSliceIndex != -1) {
			timeSlicedFrameIndex = (uint) OverrideTimeSliceIndex;
		}
	#endif
		CurrentTimeSliceIndex = timeSlicedFrameIndex;
		bool shouldGenerateNewAtlas = timeSlicedFrameIndex == 0;
		texelSpaceShadingPass.ShouldClearAltas = true;
		if (shouldGenerateNewAtlas) {
			framesSinceLastAtlasRebuild = 0;
			// Estimate the size of the objects in the atlas
			// =============================================================================================================
			objectsInCurrentAtlas.Clear();
			int atlasTexelSize = AtlasSizeAxis * AtlasSizeAxis;

			int atlasTilesTotal = AtlasSizeAxis / (int) AtlasTileSize;
			atlasTilesTotal *= atlasTilesTotal;
			int atlasTilesOccupied = 0;
			

			int statsRejectedDueSize = 0, statsRejectedDueAtlasFull = 0;
			ReportBegin();
			ReportAppendLine("Texel Space Rendering - Atlas Report");
			ReportAppendLine("=======================================");
			ReportAppendLine("Pack Atlas");
			ReportAppendLine("=======================================");
			for (int i = 0; i < visibleObjects.Count; i++) {
				if (visibleObjects[i] == null) {
					// remove nulls from list
					visibleObjects.RemoveAt(i);
					i--;
					continue; 
				}

				var currentObject = visibleObjects[i];
				currentObject.RejectedDueSize = false;
				currentObject.RejectedDueAtlasFull = false;
				float estimatedShadinDensityExponentInv = currentObject.GetEstimatedMipMapLevel(
					renderingData.cameraData.camera,
					atlasTexelSize);

				bool addToAtlas = estimatedShadinDensityExponentInv >= 0 && currentObject.enabled;

				// GetEstmatedMipMapLevel calculates the mipmap starting from 0
				int estimatedShadingDensityExponent = Mathf.RoundToInt(ShadingExponentBias + (AtlasSizeExponent - estimatedShadinDensityExponentInv));
				
				//check if the object is even fit for the atlas
				if (estimatedShadingDensityExponent < MinAtlasObjectSizeExponent ||
				    estimatedShadingDensityExponent > MaxResolution) {
					currentObject.RejectedDueSize = true;
					statsRejectedDueSize++;
					addToAtlas = false;
				}
				else {
					int objectTilesPerAxis = (1 << estimatedShadingDensityExponent) / (int) AtlasTileSize;
					int objectTilesTotal = objectTilesPerAxis * objectTilesPerAxis;
					if (atlasTilesOccupied + objectTilesTotal > atlasTilesTotal) {
						// if the atlas is full, don't add any more objects
						statsRejectedDueAtlasFull++;
						currentObject.RejectedDueAtlasFull = true;
						ReportAppendFormat("Atlas full (tiles = {0}/{1}, object tiles = {2}).\t\tObject {3}\n", atlasTilesOccupied,
							atlasTilesTotal, objectTilesTotal, currentObject.name);
					
					
						addToAtlas = false;
					}
					else {
						atlasTilesOccupied += objectTilesTotal;
						ReportAppendFormat("Atlas has space ( tiles = {0}/{1}, object tiles = {2}).\t\tObject: {3}\n", atlasTilesOccupied,
							atlasTilesTotal, objectTilesTotal, currentObject.name);
					}
				}

				

				currentObject.DesiredShadingDensityExponent = estimatedShadingDensityExponent;
				if (addToAtlas) {
					objectsInCurrentAtlas.Add(currentObject);
				}
				else {
					currentObject.SetAtlasProperties(-1, NotInAtlasRenderMask);
				}
			}
			ReportAppendLine("=======================================");
			ReportAppendLine("Atlas Object Summary");
			ReportAppendLine("=======================================");
			foreach (var objectsInCurrentAtla in objectsInCurrentAtlas) {
				ReportAppendFormat("{0}.\t\t shading density {1} \n", objectsInCurrentAtla.name, 1 << objectsInCurrentAtla.DesiredShadingDensityExponent);
			}
			ReportAppendLine("=======================================");
			ReportAppendLine("Atlas Stats");
			ReportAppendLine("=======================================");
			ReportAppendFormat("\n\ninAtlas = {0}, rejectDueSize = {1}, rejectDueAtlasFull = {2}", objectsInCurrentAtlas.Count,
				statsRejectedDueSize, statsRejectedDueAtlasFull);
			
			ReportEnd();

			// sort objects by their desired shading density. this is very important to make the packing work with Z-Order curve.
			objectsInCurrentAtlas.Sort(TexelSpaceRenderObjectShadingExponentComparer);

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
					Debug.LogError("Malformed atlas rect.", objectInAtlas);
				}
				Vector4 textureRectInAtlas = GetUVToAtlasScaleOffset(atlasRect) / AtlasSizeAxis;

				atlasCursor += occupiedTilesTotal;
				//Debug.Assert(textureRectInAtlas.x * textureRectInAtlas.y > 0, "Allocating a object in the atlas with zero space");
		
				objectInAtlas.SetAtlasScaleOffset(textureRectInAtlas);

				// generate a layermask to distribute the gpu-workload among multiple frames
				//TODO: take the shading density into account to balance the gpu load between time slices
				// use AtlasTimeSlcing - 1, because the frameIndex can never reach AtlatTimeSlicing due to Modulo Operator
				int maxLayerMask = Math.Max((int) AtlasShadingTimeSlicing - 1, 0);

				objectInAtlas.TimeSliceIndex = Random.Range(0, maxLayerMask); 
				objectInAtlas.SetAtlasProperties(i, (uint) 1 << objectInAtlas.TimeSliceIndex);
			}

			// NOTE: Since the URP did the Culling already, the RenderingLayers are already locked in.
			// So we can't render anything in the frame where we packed the atlas.
			// (It doesn't seem to be possible to run the culling a second time)
			
			// TODO: clear the atlas after the present pass


			EnableFallbackToForward();
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
			
			FallbackRenderLayerMask = unchecked((int)  ~presentPass.FromAtlasRenderLayerMask | ToBeRenderedThisFrameMask );
			presentPass.FallbackRenderLayerMask =  unchecked((uint) FallbackRenderLayerMask);

			texelSpaceShadingPass.RenderLayerMask = unchecked((uint) ToBeRenderedThisFrameMask);
			framesSinceLastAtlasRebuild++;
		}

		if (ForceFallbackToForward) {
			EnableFallbackToForward();
		}
		// Enqueue Passes
		// =============================================================================================================
		renderer.EnqueuePass(texelSpaceShadingPass);
		renderer.EnqueuePass(presentPass);

		visibleObjects.Clear();
	}

	void EnableFallbackToForward() {
		presentPass.FallbackRenderLayerMask = uint.MaxValue;
		presentPass.FromAtlasRenderLayerMask = 0;
		texelSpaceShadingPass.RenderLayerMask = 0;
		
		FallbackRenderLayerMask = unchecked((int) presentPass.FallbackRenderLayerMask);
		ToBeRenderedThisFrameMask = 0;
		InAtlasRenderedLayerMask = 0;
	}

	static void LogTrace(object obj) {
		Debug.Log(obj);
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
			ShouldClearAltas &= Parent.AllowClearAtlas;
			ConfigureClear(
				ShouldClearAltas ? ClearFlag.Color : ClearFlag.None, 
				Parent.ClearAtlasWithRed ? new Color(1f, 0f, 0f, 0f): Color.clear);

			cmd.SetGlobalTexture("g_VistaAtlas", TargetAtlas);
			if (Parent.ShadingCullBackfacingVertices) {
				Shader.EnableKeyword("TSS_CULL_VERTICES");
			}
			else {
				Shader.DisableKeyword("TSS_CULL_VERTICES");
			}
		}

		public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
			CommandBuffer cmd = CommandBufferPool.Get("Texel-Shading Pass Pre");
			cmd.SetGlobalFloat("_Tss_DebugView", Parent.DebugViewOverlay);

			// see notes at start of this file
			#if TSS_MANUAL_RENDERING
			foreach (TexelSpaceRenderObject parentObjectsInCurrentAtla in Parent.objectsInCurrentAtlas) {
				cmd.DrawRenderer(parentObjectsInCurrentAtla.meshRenderer,
					parentObjectsInCurrentAtla.meshRenderer.sharedMaterial);
			}
			#endif
			context.ExecuteCommandBuffer(cmd);
			CommandBufferPool.Release(cmd);
			
			// render the objects into the atlas
			DrawingSettings drawingSettings =
				CreateDrawingSettings(lightModeTexelSpacePass, ref renderingData, SortingCriteria.None);
			drawingSettings.perObjectData
				= PerObjectData.ReflectionProbes | PerObjectData.Lightmaps | PerObjectData.LightProbe | PerObjectData.LightData | PerObjectData.OcclusionProbe | PerObjectData.ShadowMask | PerObjectData.LightIndices;

			FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.all) {
				renderingLayerMask = RenderLayerMask
			};
			#if !TSS_MANUAL_RENDERING
			context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filterSettings);
			#endif
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

	class OverlayPass : ScriptableRenderPass {
		public TexelSpaceRenderFeature Parent;

		public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
			CommandBuffer cmd = CommandBufferPool.Get("Texel-Shading Debug Overlay");
			
			context.ExecuteCommandBuffer(cmd);
			CommandBufferPool.Release(cmd);
		}
	}
#region ReportFunctions
	// Optional report functions to debug the contents of the atlas via debug messages.
	static readonly StringBuilder ReportStringBuilder = new StringBuilder();
	static bool ReportEnabled = false;
	static void ReportBegin() {
		if(!ReportEnabled)
			return;
		
		ReportStringBuilder.Clear();
	}
	static void ReportAppendLine(string line) {
		if(!ReportEnabled)
			return;
		
		ReportStringBuilder.AppendLine(line);
	}
	
	static void ReportAppendFormat(string line, params object[] args) {
		if(!ReportEnabled)
			return;
		
		ReportStringBuilder.AppendFormat(line, args);
	}

	static void ReportEnd() {
		if(!ReportEnabled)
			return;
		
		Debug.Log(ReportStringBuilder.ToString());
	}
#endregion

#region TexelSpaceRenderHelperInterface

	public static TexelSpaceRenderFeature Instance;
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
		float2 atlasPositionTileSpace = GetTilePosition(index);
		float2 min = atlasPositionTileSpace * AtlasTileSize;
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

public class SimpleRenderFeature : ScriptableRendererFeature {
	public override void Create() {
		// wird
	}

	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
		// wird jedes einzelbild aufgerufen
	}
}