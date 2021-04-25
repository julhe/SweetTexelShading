using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

internal class ComputeBufferWithData<T> {
	public ComputeBuffer buffer;
	public T[] data;
}

public class BasicRenderpipeline : RenderPipeline {
	const string PIPELINE_NAME = "BasicRenderpipeline";
	const int MAXIMAL_OBJECTS_PER_VIEW = 512;
	const int SCREEN_MAX_X = 3840, SCREEN_MAX_Y = 2100;
	const int COMPUTE_COVERAGE_TILE_SIZE = 8;
	const int MAX_PRIMITIVES_PER_OBJECT = 65536 / PRIMITIVE_CLUSTER_SIZE;
	const int PRIMITIVE_CLUSTER_SIZE = 8;
	const int kCameraDepthBufferBits = 32;
	const int MAX_LIGHTS = 48;

	//
	const PerObjectData rendererConfiguration_shading =
		PerObjectData.LightIndices |
		PerObjectData.Lightmaps |
		PerObjectData.LightProbe |
		PerObjectData.ReflectionProbes |
		PerObjectData.LightData;

	static class TsRenderPassId {
		// TODO
	}
	public static BasicRenderpipeline instance;
	static readonly ShaderTagId m_TexelSpacePass = new ShaderTagId("Texel Space Pass");
	static readonly ShaderTagId m_VistaPass = new ShaderTagId("Vista Pass");
	static readonly ShaderTagId m_VisibilityPass = new ShaderTagId("Visibility Pass");
	public static int SCREEN_X, SCREEN_Y;

	static class TsResolvePassField {
		// TODO
	}
	readonly ComputeBuffer g_PrimitiveVisibility,
		g_ObjectToAtlasProperties,
		g_prev_ObjectToAtlasProperties,
		g_Object_MipmapLevelA,
		g_Object_MipmapLevelB,
		g_ObjectMipMapCounterValue;

	readonly int m_cs_ExtractVisibility,
		g_VisibilityBufferID,
		g_PrimitiveVisibilityID,
		m_cs_DebugVisibilityBuffer,
		m_cs_AtlasPacking,
		m_cs_InitalizePrimitiveVisiblity,
		m_cs_CopyDataToPreFrameBuffer,
		g_CameraTarget,
		m_cs_MipMapFinalize;

	readonly ComputeShader m_ResolveCS;


	int atlasAxisSize;
	readonly List<Vector4> g_LightColorAngle = new List<Vector4>();

	readonly List<Vector4> g_LightsOriginRange = new List<Vector4>();
	Vector2Int g_visibilityBuffer_dimension;
	readonly RenderTargetIdentifier g_visibilityBuffer_RT;
	RenderTargetIdentifier g_CameraTarget_RT;

	RenderTexture g_VistaAtlas_A, g_VistaAtlas_B;

	readonly BasicRenderpipelineAsset m_asset;

	readonly CameraComparer m_CameraComparer = new CameraComparer();

	CullingResults m_CullResults;

	ShadingCluster[] shadingClusters =
			new ShadingCluster[MAXIMAL_OBJECTS_PER_VIEW * 256]; // objectID is implicid trough first 10bit of the index

	int skipedFrames = 0;
	bool target_atlasA;
	double timeSinceLastRender;

	// Atlas packing 
	readonly List<TexelSpaceRenderHelper> visibleObjects = new List<TexelSpaceRenderHelper>();

	public BasicRenderpipeline(BasicRenderpipelineAsset asset) {
		m_asset = asset;
		Shader.globalRenderPipeline = PIPELINE_NAME;
		m_ResolveCS = asset.resolveShader;
		QualitySettings.antiAliasing = 1;
		m_cs_ExtractVisibility = m_ResolveCS.FindKernel("ExtractCoverage");
		m_cs_DebugVisibilityBuffer = m_ResolveCS.FindKernel("DebugShowVertexID");
		m_cs_AtlasPacking = m_ResolveCS.FindKernel("AtlasPacking");
		m_cs_CopyDataToPreFrameBuffer = m_ResolveCS.FindKernel("CopyDataToPreFrameBuffer");
		m_cs_MipMapFinalize = m_ResolveCS.FindKernel("MipMapFinalize");
		m_cs_InitalizePrimitiveVisiblity = m_ResolveCS.FindKernel("InitalizePrimitiveVisiblity");
		g_VisibilityBufferID = Shader.PropertyToID("g_VisibilityBuffer");
		g_PrimitiveVisibilityID = Shader.PropertyToID("g_PrimitiveVisibility");
		g_CameraTarget = Shader.PropertyToID("g_CameraTarget");

		g_visibilityBuffer_RT = new RenderTargetIdentifier(g_VisibilityBufferID);

		g_PrimitiveVisibility =
			new ComputeBuffer(MAXIMAL_OBJECTS_PER_VIEW * (MAX_PRIMITIVES_PER_OBJECT / 32), sizeof(int));

		g_ObjectToAtlasProperties =
			new ComputeBuffer(MAXIMAL_OBJECTS_PER_VIEW, sizeof(uint) + sizeof(uint) + sizeof(float) * 4);
		g_prev_ObjectToAtlasProperties =
			new ComputeBuffer(g_ObjectToAtlasProperties.count, g_ObjectToAtlasProperties.stride);

		// this value can get pretty large, so 
		int g_Object_MipmapLevelA_size =
			SCREEN_MAX_X / COMPUTE_COVERAGE_TILE_SIZE * (SCREEN_Y / COMPUTE_COVERAGE_TILE_SIZE) *
			MAXIMAL_OBJECTS_PER_VIEW / 32;
		g_Object_MipmapLevelA =
			new ComputeBuffer(SCREEN_MAX_X * SCREEN_MAX_Y, sizeof(int),
				ComputeBufferType.Append); //TODO: better heristic for value
		g_Object_MipmapLevelB = new ComputeBuffer(g_Object_MipmapLevelA.count, g_Object_MipmapLevelA.stride,
			ComputeBufferType.Append);
		g_ObjectMipMapCounterValue = new ComputeBuffer(1, sizeof(int), ComputeBufferType.IndirectArguments);
	}

	#region XR SDK
		// XR SDK display interface
		static List<XRDisplaySubsystem> displayList = new List<XRDisplaySubsystem>();
		XRDisplaySubsystem              display = null;
		// XRSDK does not support msaa per XR display. All displays share the same msaa level.
		static  int                     msaaLevel = 1;
		
		// With XR SDK: disable legacy VR system before rendering first frame
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
		internal static void XRSystemInit()
		{
			if (GraphicsSettings.currentRenderPipeline == null)
				return;

			SubsystemManager.GetSubsystems(displayList);

			// XRTODO: refactor with RefreshXrSdk()
			for (int i = 0; i < displayList.Count; i++)
			{
				displayList[i].disableLegacyRenderer = true;
				displayList[i].textureLayout = XRDisplaySubsystem.TextureLayout.Texture2DArray;
				displayList[i].sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear;
			}

		}
		
		Vector4[] stereoEyeIndices = new Vector4[2] { Vector4.zero , Vector4.one };
		
	#endregion

	
	protected override void Render(ScriptableRenderContext context, Camera[] cameras) {
		m_asset.memoryConsumption = 0f;
		instance = this;

		display = displayList.Count > 0 && displayList[0].running ? displayList[0] : null;
		bool xrEnabled = XRSettings.isDeviceActive;

		// Sort cameras array by camera depth
		Array.Sort(cameras, m_CameraComparer);

		// SetupShaderGlobals
		// =====================================================================================================
		LogTrace("SetupShaderGlobals...");
		{
			CommandBuffer cmd5 = CommandBufferPool.Get("SetupShaderGlobals");
			cmd5.SetGlobalFloat("g_AtlasResolutionScale",
				m_asset.atlasResolutionScale / m_asset.visibilityPassDownscale);
			float lerpFactor =
				Mathf.Clamp01((float) timeSinceLastRender /
				              (1f / m_asset.atlasRefreshFps)); //TODO: clamp should't been neccesary

			cmd5.SetGlobalFloat("g_atlasMorph", lerpFactor);
			cmd5.SetGlobalTexture("g_Dither", m_asset.dither[0]);
			if (m_asset.TexelSpaceBackfaceCulling) {
				cmd5.EnableShaderKeyword("TRIANGLE_CULLING");
			}
			else {
				cmd5.DisableShaderKeyword("TRIANGLE_CULLING");
			}

			context.ExecuteCommandBuffer(cmd5);
			CommandBufferPool.Release(cmd5);
		}
		bool shouldUpdateAtlas = timeSinceLastRender > 1f / m_asset.atlasRefreshFps;
		
		foreach (Camera camera in cameras) { 
			//XR

			SCREEN_X = camera.pixelWidth;
			SCREEN_Y = camera.pixelHeight;

			SortingSettings cameraSortSettings = new SortingSettings(camera);
			ScriptableCullingParameters cullingParameters;
			
			if (!camera.TryGetCullingParameters(xrEnabled, out cullingParameters)) {
				continue;
			}
			
			m_CullResults = context.Cull(ref cullingParameters);

			context.SetupCameraProperties(camera, xrEnabled);

			#region XRtest
			
			{
				
				var cmd = CommandBufferPool.Get("Test");			
				
				if (display != null) //Vr is enabled
				{
				#region setup stero rendering
					// XRTODO: Handle stereo mode selection in URP pipeline asset UI
					display.textureLayout = XRDisplaySubsystem.TextureLayout.Texture2DArray;
					display.zNear = camera.nearClipPlane;
					display.zFar  = camera.farClipPlane;
					display.sRGB  = QualitySettings.activeColorSpace == ColorSpace.Linear;
				
					display.GetRenderPass(0, out XRDisplaySubsystem.XRRenderPass xrRenderPass);
					cmd.SetRenderTarget(xrRenderPass.renderTarget);
					xrRenderPass.GetRenderParameter(camera, 0, out var renderParameter0);
					xrRenderPass.GetRenderParameter(camera, 1, out var renderParameter1);
				#endregion
				#region enable stero rendering
					//enable single pass (see XRPass.cs:344)
					if (SystemInfo.supportsMultiview)
					{
						cmd.EnableShaderKeyword("STEREO_MULTIVIEW_ON");
						cmd.SetGlobalVectorArray("unity_StereoEyeIndices", stereoEyeIndices);
					}
					else
					{
						cmd.EnableShaderKeyword("STEREO_INSTANCING_ON");
						const int viewCount = 2;
						cmd.SetInstanceMultiplier((uint)viewCount);
					}
					cmd.EnableShaderKeyword(ShaderKeywordStrings.UseDrawProcedural);
				#endregion
				}
				else {
					cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
				}
				
				cmd.ClearRenderTarget(true, true, Color.green);
				context.ExecuteCommandBuffer(cmd);
				//RenderOpaque(context, m_VistaPass, cameraSortSettings);
				context.DrawSkybox(camera);
				
				#region Disable stero rendering                    
					if (SystemInfo.supportsMultiview)
					{
						cmd.DisableShaderKeyword("STEREO_MULTIVIEW_ON");
					}
					else
					{
						cmd.DisableShaderKeyword("STEREO_INSTANCING_ON");
						cmd.SetInstanceMultiplier(1);
					}
					cmd.DisableShaderKeyword(ShaderKeywordStrings.UseDrawProcedural);
				#endregion
		
			}
			#endregion
			int targetAtlasSize = m_asset.maximalAtlasSizePixel;
			if (g_VistaAtlas_A == null || g_VistaAtlas_A.width != targetAtlasSize) {
				CommandBuffer cmd5 = CommandBufferPool.Get("(Re)initialize Atlas");
				if (g_VistaAtlas_A != null) {
					g_VistaAtlas_A.Release();
					g_VistaAtlas_B.Release();
				}

				g_VistaAtlas_A = new RenderTexture(
					targetAtlasSize, 
					targetAtlasSize, 
					0, 
					RenderTextureFormat.ARGB2101010,
					RenderTextureReadWrite.sRGB);

				g_VistaAtlas_A.Create();
				g_VistaAtlas_B = new RenderTexture(g_VistaAtlas_A);
				g_VistaAtlas_B.Create();

				cmd5.SetRenderTarget(g_VistaAtlas_A);
				cmd5.ClearRenderTarget(true, true, Color.black);

				cmd5.SetRenderTarget(g_VistaAtlas_B);
				cmd5.ClearRenderTarget(true, true, Color.black);

				context.ExecuteCommandBuffer(cmd5);
				CommandBufferPool.Release(cmd5);
			}

			// TODO: reuse uv output to skip rendering objects a third time in VistaPass

			CommandBuffer createCameraRT = CommandBufferPool.Get("Create Camera RT");
			createCameraRT.GetTemporaryRT(
				g_CameraTarget,
				SCREEN_X,
				SCREEN_Y,
				24,
				FilterMode.Bilinear,
				RenderTextureFormat.ARGB32,
				RenderTextureReadWrite.sRGB,
				Mathf.NextPowerOfTwo(m_asset.MSSALevel));

			createCameraRT.SetRenderTarget(g_CameraTarget);
			createCameraRT.ClearRenderTarget(true, true, Color.clear);
			context.ExecuteCommandBuffer(createCameraRT);

			if (shouldUpdateAtlas) {
				//Debug.Log(DateTime.Now.ToString("hh.mm.ss.ffffff") + "render" + timeSinceLastRender.ToString());
				target_atlasA = !target_atlasA;
				// =====================================================================================================
				// CopyDataToPreFrameBuffer
				// =====================================================================================================
				// LogVerbose("CopyDataToPreFrameBuffer...");
				CommandBuffer cmd = CommandBufferPool.Get("CopyDataToPreFrameBuffer");

				cmd.SetComputeBufferParam(m_ResolveCS, m_cs_CopyDataToPreFrameBuffer, "g_ObjectToAtlasProperties",
					g_ObjectToAtlasProperties);
				cmd.SetComputeBufferParam(m_ResolveCS, m_cs_CopyDataToPreFrameBuffer, "g_prev_ObjectToAtlasProperties",
					g_prev_ObjectToAtlasProperties);
				uint threadsX, threadsY, threadsZ;
				m_ResolveCS.GetKernelThreadGroupSizes(m_cs_CopyDataToPreFrameBuffer, out threadsX, out threadsY,
					out threadsZ);
				cmd.DispatchCompute(m_ResolveCS, m_cs_CopyDataToPreFrameBuffer,
					Mathf.CeilToInt(MAXIMAL_OBJECTS_PER_VIEW / (float) 64.0), 1, 1);

				cmd.SetComputeBufferParam(m_ResolveCS, m_cs_InitalizePrimitiveVisiblity, g_PrimitiveVisibilityID,
					g_PrimitiveVisibility);
				cmd.DispatchCompute(m_ResolveCS, m_cs_InitalizePrimitiveVisiblity,
					Mathf.CeilToInt(g_PrimitiveVisibility.count / (float) threadsX), 1, 1);
				context.ExecuteCommandBuffer(cmd);
				CommandBufferPool.Release(cmd);
				// =====================================================================================================
				// SetupRenderBuffers
				// =====================================================================================================
				LogTrace("SetupRenderBuffers...");
				CommandBuffer cmd1 = CommandBufferPool.Get("SetupBuffers");
				int screenX = camera.pixelWidth;
				int screenY = camera.pixelHeight;
				g_visibilityBuffer_dimension = new Vector2Int(
					Mathf.CeilToInt(screenX / m_asset.visibilityPassDownscale),
					Mathf.CeilToInt(screenY / m_asset.visibilityPassDownscale));

				cmd1.GetTemporaryRT(g_VisibilityBufferID, g_visibilityBuffer_dimension.x,
					g_visibilityBuffer_dimension.y, 32, FilterMode.Point, RenderTextureFormat.RInt,
					RenderTextureReadWrite.Linear, 1);

				cmd1.SetRenderTarget(g_visibilityBuffer_RT);
				cmd1.ClearRenderTarget(true, true, Color.clear);

				cmd1.SetRenderTarget(target_atlasA ? g_VistaAtlas_A : g_VistaAtlas_B);
				if (m_asset.clearAtlasOnRefresh) {
					cmd1.ClearRenderTarget(true, true, Color.clear);
				}

				cmd1.SetGlobalTexture("g_VistaAtlas", target_atlasA ? g_VistaAtlas_A : g_VistaAtlas_B);
				cmd1.SetGlobalTexture("g_prev_VistaAtlas", target_atlasA ? g_VistaAtlas_B : g_VistaAtlas_A);
				cmd1.SetGlobalFloat("g_AtlasSizeExponent", m_asset.maximalAtlasSizeExponent);
				context.ExecuteCommandBuffer(cmd1);
				CommandBufferPool.Release(cmd1);
				// =====================================================================================================
				// Visiblity Pass
				// Figure out what objects (and triangles) are visible and needed to be rendered. 
				// =====================================================================================================
				// renders the current view as: objectID, primitveID and mipmap level
				g_Object_MipmapLevelA.SetCounterValue(0);
				CommandBuffer cmd2 = CommandBufferPool.Get("RenderTexelCoverage");
				cmd2.SetRenderTarget(g_VisibilityBufferID);
				//cmd.SetGlobalBuffer("g_ObjectToAtlasPropertiesRW", g_ObjectToAtlasProperties);
				//cmd.SetRandomWriteTarget(1, g_ObjectToAtlasProperties);

				//g_vertexIDVisiblity_B.SetData(g_vertexIDVisiblity_B_init);
				context.ExecuteCommandBuffer(cmd2);

				cameraSortSettings.criteria = SortingCriteria.OptimizeStateChanges;
				context.StartMultiEye(camera);
				RenderOpaque(context,m_VisibilityPass, cameraSortSettings);
				context.StopMultiEye(camera);

				cmd2.Clear();
				cmd2.ClearRandomWriteTargets();

				// VISIBLITY DISSOLVE PASS
				// maps the previous rendered data into usable buffers
				cmd2.SetComputeTextureParam(m_ResolveCS, m_cs_ExtractVisibility, g_VisibilityBufferID,
					g_visibilityBuffer_RT);
				cmd2.SetComputeBufferParam(m_ResolveCS, m_cs_ExtractVisibility, g_PrimitiveVisibilityID,
					g_PrimitiveVisibility);
				cmd2.SetComputeBufferParam(m_ResolveCS, m_cs_ExtractVisibility, "g_ObjectToAtlasProperties",
					g_ObjectToAtlasProperties);

				cmd2.SetComputeBufferParam(m_ResolveCS, m_cs_ExtractVisibility, "g_ObjectMipMap_append",
					g_Object_MipmapLevelA);
				cmd2.DispatchCompute(m_ResolveCS, m_cs_ExtractVisibility, SCREEN_X / COMPUTE_COVERAGE_TILE_SIZE,
					SCREEN_Y / COMPUTE_COVERAGE_TILE_SIZE, 1);
				cmd2.CopyCounterValue(g_Object_MipmapLevelA, g_ObjectMipMapCounterValue, 0);
				cmd2.SetComputeBufferParam(m_ResolveCS, m_cs_MipMapFinalize, "g_ObjectMipMap_consume",
					g_Object_MipmapLevelA);
				cmd2.SetComputeBufferParam(m_ResolveCS, m_cs_MipMapFinalize, "g_ObjectToAtlasProperties",
					g_ObjectToAtlasProperties);
				cmd2.SetComputeBufferParam(m_ResolveCS, m_cs_MipMapFinalize, "g_ObjectMipMapCounterValue",
					g_ObjectMipMapCounterValue);
				cmd2.DispatchCompute(m_ResolveCS, m_cs_MipMapFinalize, 1, 1, 1);

				context.ExecuteCommandBuffer(cmd2);

				cmd2.Clear();

				// optional debug pass
				switch (m_asset.debugPass) {
					case TexelSpaceDebugMode.VisibilityPassObjectID:
					case TexelSpaceDebugMode.VisibilityPassPrimitivID:
					case TexelSpaceDebugMode.VisibilityPassMipMapPerObject:
					case TexelSpaceDebugMode.VisibilityPassMipMapPerPixel:
						int debugView = Shader.PropertyToID("g_DebugTexture");
						cmd2.GetTemporaryRT(debugView, SCREEN_X, SCREEN_Y, 16, FilterMode.Point,
							RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, 1, true);
						cmd2.SetComputeTextureParam(m_ResolveCS, m_cs_DebugVisibilityBuffer, g_VisibilityBufferID,
							g_visibilityBuffer_RT);
						cmd2.SetComputeTextureParam(m_ResolveCS, m_cs_DebugVisibilityBuffer, "g_DebugTexture",
							debugView);
						cmd2.SetComputeBufferParam(m_ResolveCS, m_cs_DebugVisibilityBuffer,
							"g_ObjectToAtlasPropertiesR", g_ObjectToAtlasProperties);
						cmd2.SetComputeIntParam(m_ResolveCS, "g_DebugPassID", (int) m_asset.debugPass);
						cmd2.DispatchCompute(
							m_ResolveCS,
							m_cs_DebugVisibilityBuffer,
							SCREEN_X / 8,
							SCREEN_Y / 8,
							1);

						cmd2.Blit(debugView, g_CameraTarget);
						cmd2.ReleaseTemporaryRT(debugView);

						context.ExecuteCommandBuffer(cmd2);
						cmd2.Clear();

						break;
				}

				CommandBufferPool.Release(cmd2);
				// =====================================================================================================
				// PackAtlas
				// =====================================================================================================
				LogTrace("PackAtlas...");
				CommandBuffer cmd3 = CommandBufferPool.Get("PackAtlas");
				atlasAxisSize = m_asset.maximalAtlasSizePixel;

				for (int i = 0; i < visibleObjects.Count; i++) {
					visibleObjects[i].SetAtlasProperties(i + 1); //objectID 0 is reserved for "undefined"
				}
				
				cmd3.SetComputeIntParam(m_ResolveCS, "g_totalObjectsInView", visibleObjects.Count + 1);
				cmd3.SetComputeIntParam(m_ResolveCS, "g_atlasAxisSize", atlasAxisSize);

				cmd3.SetComputeBufferParam(m_ResolveCS, m_cs_AtlasPacking, "g_ObjectToAtlasProperties",
					g_ObjectToAtlasProperties);

				cmd3.DispatchCompute(m_ResolveCS, m_cs_AtlasPacking, 1, 1, 1);

				visibleObjects.Clear();
				context.ExecuteCommandBuffer(cmd3);
				CommandBufferPool.Release(cmd3);
				// =====================================================================================================
				// RenderTexelShading
				// =====================================================================================================
				CommandBuffer cmd4 = CommandBufferPool.Get("RenderTexelShading");
				LogTrace("setup light array...");
				NativeArray<VisibleLight> visibleLights = m_CullResults.visibleLights;
				g_LightsOriginRange.Clear();
				g_LightColorAngle.Clear();
				for (int i1 = 0; i1 < MAX_LIGHTS; i1++) {
					if (i1 >= visibleLights.Length) {
						// fill up buffer with zero lights
						g_LightsOriginRange.Add(Vector4.zero);
						g_LightColorAngle.Add(Vector4.zero);
						continue;
					}

					VisibleLight light = visibleLights[i1];

					// if it's a directional light, just treat it as a point light and place it very far away
					Vector4 lightOriginRange = light.lightType == LightType.Directional
						? -light.light.transform.forward * 99999f
						: light.light.transform.position;
					lightOriginRange.w = light.lightType == LightType.Directional ? 99999999f : light.range;
					g_LightsOriginRange.Add(lightOriginRange);

					Vector4 lightColorAngle;
					lightColorAngle = light.light.color * light.light.intensity;
					lightColorAngle.w = light.lightType == LightType.Directional ? Mathf.Cos(light.spotAngle) : 1f;
					g_LightColorAngle.Add(lightColorAngle);
				}

				cmd4.SetGlobalVectorArray("g_LightsOriginRange", g_LightsOriginRange);
				cmd4.SetGlobalVectorArray("g_LightColorAngle", g_LightColorAngle);
				cmd4.SetGlobalInt("g_LightsCount", Mathf.Min(MAX_LIGHTS, visibleLights.Length));

				cmd4.SetRenderTarget(target_atlasA ? g_VistaAtlas_A : g_VistaAtlas_B);
				cmd4.SetGlobalBuffer(g_PrimitiveVisibilityID, g_PrimitiveVisibility);
				cmd4.SetGlobalBuffer("g_ObjectToAtlasProperties", g_ObjectToAtlasProperties);
				cmd4.SetGlobalBuffer("g_prev_ObjectToAtlasProperties", g_prev_ObjectToAtlasProperties);
				context.ExecuteCommandBuffer(cmd4);
				RenderOpaque(context, m_TexelSpacePass, cameraSortSettings);

				cmd4.Clear();
				if (m_asset.debugPass == TexelSpaceDebugMode.TexelShadingPass) {
					cmd4.Blit(g_VistaAtlas_A, BuiltinRenderTextureType.CameraTarget);
					context.ExecuteCommandBuffer(cmd4);
				}

				CommandBufferPool.Release(cmd4);

				LogTrace("ReleaseBuffers...");
				CommandBuffer cmd6 = CommandBufferPool.Get("ReleaseBuffers");
				cmd6.ReleaseTemporaryRT(g_PrimitiveVisibilityID);
				context.ExecuteCommandBuffer(cmd6);
				CommandBufferPool.Release(cmd6);
				
			#if UNITY_EDITOR
				// Emit scene view UI
				if (camera.cameraType == CameraType.SceneView) {
					ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
				}
			#endif
			}

			visibleObjects.Clear();

			// ===================================================================================================== 
			// Render Vista + Finalize
			// =====================================================================================================

			CommandBuffer cmdVista = CommandBufferPool.Get("Render Vista");
			cmdVista.SetRenderTarget(g_CameraTarget);
			context.ExecuteCommandBuffer(cmdVista);
			cmdVista.Clear();

			
			context.StartMultiEye(camera);
			switch (m_asset.debugPass) {
				case TexelSpaceDebugMode.None:
					cameraSortSettings.criteria = SortingCriteria.OptimizeStateChanges;
					RenderOpaque(context, m_VistaPass, cameraSortSettings); // render vista
					context.DrawSkybox(camera);
					break;
				case TexelSpaceDebugMode.VisibilityPassObjectID:
				case TexelSpaceDebugMode.VisibilityPassPrimitivID:
				case TexelSpaceDebugMode.VisibilityPassMipMapPerObject:
				case TexelSpaceDebugMode.VisibilityPassMipMapPerPixel:
					break;
				case TexelSpaceDebugMode.TexelShadingPass:
					cmdVista.Blit(g_VistaAtlas_A, g_CameraTarget);
					context.ExecuteCommandBuffer(cmdVista);
					cmdVista.Clear();
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
			context.StopMultiEye(camera);
			cmdVista.Blit(g_CameraTarget, BuiltinRenderTextureType.CameraTarget);
			cmdVista.ReleaseTemporaryRT(g_CameraTarget);
			context.ExecuteCommandBuffer(cmdVista);
			CommandBufferPool.Release(cmdVista);
		}

		if (shouldUpdateAtlas) {
			timeSinceLastRender = 0f;
		}

		timeSinceLastRender += Time.deltaTime;
		context.Submit();
		if (g_VistaAtlas_A) {
			m_asset.memoryConsumption += g_VistaAtlas_A.width * g_VistaAtlas_A.height *
			                             (g_VistaAtlas_A.format == RenderTextureFormat.DefaultHDR ? 8 : 4) * 2;
			
		}
		
		m_asset.memoryConsumption /= 1024 * 1024;
	}

	void RenderOpaque(ScriptableRenderContext context, ShaderTagId shaderTagId, SortingSettings sortingSettings) {
		LogTrace("RenderOpaque...");
		DrawingSettings drawSettings = new DrawingSettings(shaderTagId, sortingSettings);
		FilteringSettings filteringSettings = new FilteringSettings(RenderQueueRange.opaque);
		drawSettings.perObjectData = rendererConfiguration_shading;
		context.DrawRenderers(m_CullResults, ref drawSettings, ref filteringSettings);
	}

	public void AddObject(TexelSpaceRenderHelper texelSpaceRenderHelper) {
		visibleObjects.Add(texelSpaceRenderHelper);
	}

	//TODO: reuse depthbuffer from visibility pass for vista pass
	//TODO: check if atlas is acutally large enough

	void LogTrace(object obj) {
	#if LOG_TRACE
        Debug.Log(obj);
	#endif
	}
	
	struct ShadingCluster {
		public int clusterID;
		public int mipMapLevel;
		public Vector4 atlasScaleOffset;
	}
}

public enum TexelSpaceDebugMode {
	None = 0,
	VisibilityPassObjectID,
	VisibilityPassPrimitivID,
	VisibilityPassMipMapPerObject,
	VisibilityPassMipMapPerPixel,
	TexelShadingPass
}

public class CameraComparer : IComparer<Camera> {
	public int Compare(Camera lhs, Camera rhs) {
		return (int) (rhs.depth - lhs.depth);
	}
}