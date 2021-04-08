﻿using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
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

	public static BasicRenderpipeline instance;
	static readonly ShaderTagId m_TexelSpacePass = new ShaderTagId("Texel Space Pass");
	static readonly ShaderTagId m_VistaPass = new ShaderTagId("Vista Pass");
	static readonly ShaderTagId m_VisibilityPass = new ShaderTagId("Visibility Pass");
	public static int SCREEN_X, SCREEN_Y;

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
	ScriptableRenderContext m_context;

	CullingResults m_CullResults;

	ShadingCluster[]
		shadingClusters =
			new ShadingCluster[MAXIMAL_OBJECTS_PER_VIEW * 256]; // objectID is implicid trough first 10bit of the index

	int skipedFrames = 0;
	bool target_atlasA;
	float timeSinceLastRender;

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

	public static Camera CURRENT_CAMERA { get; private set; }
	//
	// public void Dispose()
	// {
	//     instance = null;
	//     Shader.globalRenderPipeline = "";
	//     g_PrimitiveVisibility.Release();
	//     g_ObjectToAtlasProperties.Release();
	//     g_prev_ObjectToAtlasProperties.Release();
	//     if(g_VistaAtlas_A != null)
	//         g_VistaAtlas_A.Release();
	//     if(g_VistaAtlas_B != null)
	//         g_VistaAtlas_B.Release();
	//
	//    
	// }


	void ValidateAtlasTextureParameters() {
		int targetAtlasSize = m_asset.maximalAtlasSizePixel;
		if (g_VistaAtlas_A == null || g_VistaAtlas_A.width != targetAtlasSize) {
			CommandBuffer cmd = CommandBufferPool.Get("SetupAtlas");
			if (g_VistaAtlas_A != null) {
				g_VistaAtlas_A.Release();
				g_VistaAtlas_B.Release();
			}

			g_VistaAtlas_A = new RenderTexture(targetAtlasSize, targetAtlasSize, 0, RenderTextureFormat.ARGB2101010,
				RenderTextureReadWrite.sRGB);

			g_VistaAtlas_A.Create();
			g_VistaAtlas_B = new RenderTexture(g_VistaAtlas_A);
			g_VistaAtlas_B.Create();

			cmd.SetRenderTarget(g_VistaAtlas_A);
			cmd.ClearRenderTarget(true, true, Color.black);

			cmd.SetRenderTarget(g_VistaAtlas_B);
			cmd.ClearRenderTarget(true, true, Color.black);

			m_context.ExecuteCommandBuffer(cmd);
			CommandBufferPool.Release(cmd);
		}
	}

	protected override void Render(ScriptableRenderContext context, Camera[] cameras) {
		m_asset.memoryConsumption = 0f;
		instance = this;
		m_context = context;
		bool stereoEnabled = XRSettings.isDeviceActive;
		// Sort cameras array by camera depth
		Array.Sort(cameras, m_CameraComparer);

		// SetupShaderGlobals
		// =====================================================================================================
		LogVerbose("SetupShaderGlobals...");
		CommandBuffer cmd5 = CommandBufferPool.Get("SetupShaderGlobals");
		cmd5.SetGlobalFloat("g_AtlasResolutionScale", m_asset.atlasResolutionScale / m_asset.visibilityPassDownscale);
		float lerpFactor =
			Mathf.Clamp01(timeSinceLastRender / (1f / m_asset.atlasRefreshFps)); //TODO: clamp should't been neccesary

		cmd5.SetGlobalFloat("g_atlasMorph", lerpFactor);
		cmd5.SetGlobalTexture("g_Dither", m_asset.dither[0]);
		if (m_asset.TexelSpaceBackfaceCulling) {
			cmd5.EnableShaderKeyword("TRIANGLE_CULLING");
		}
		else {
			cmd5.DisableShaderKeyword("TRIANGLE_CULLING");
		}

		m_context.ExecuteCommandBuffer(cmd5);
		CommandBufferPool.Release(cmd5);
		bool shouldUpdateAtlas = timeSinceLastRender > 1f / m_asset.atlasRefreshFps;

		//g_PrimitiveVisibility.SetData(g_PrimitiveVisibility_init);
		foreach (Camera camera in cameras) {
			CURRENT_CAMERA = camera;
			SCREEN_X = CURRENT_CAMERA.pixelWidth;
			SCREEN_Y = CURRENT_CAMERA.pixelHeight;

			SortingSettings cameraSortSettings = new SortingSettings(camera);
			ScriptableCullingParameters cullingParameters;

			if (!camera.TryGetCullingParameters(stereoEnabled, out cullingParameters)) {
				continue;
			}

		#if UNITY_EDITOR
			// Emit scene view UI
			if (camera.cameraType == CameraType.SceneView) {
				ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
			}
		#endif
			m_CullResults = context.Cull(ref cullingParameters);
			//CullingResults.Cull(ref cullingParameters, context, ref m_CullResults);

			context.SetupCameraProperties(CURRENT_CAMERA, stereoEnabled);

			ValidateAtlasTextureParameters();

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
			m_context.ExecuteCommandBuffer(createCameraRT);

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
					Mathf.CeilToInt(g_PrimitiveVisibility.count / threadsX), 1, 1);
				m_context.ExecuteCommandBuffer(cmd);
				CommandBufferPool.Release(cmd);
				// =====================================================================================================
				// SetupRenderBuffers
				// =====================================================================================================
				LogVerbose("SetupRenderBuffers...");
				CommandBuffer cmd1 = CommandBufferPool.Get("SetupBuffers");
				int screen_x = CURRENT_CAMERA.pixelWidth;
				int screen_y = CURRENT_CAMERA.pixelHeight;
				g_visibilityBuffer_dimension = new Vector2Int(
					Mathf.CeilToInt(screen_x / m_asset.visibilityPassDownscale),
					Mathf.CeilToInt(screen_y / m_asset.visibilityPassDownscale));

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
				m_context.ExecuteCommandBuffer(cmd1);
				CommandBufferPool.Release(cmd1);
				// =====================================================================================================
				// RenderVisiblityPass
				// =====================================================================================================
				// VISIBLITY RENDER PASS
				// renders the current view as: objectID, primitveID and mipmap level
				g_Object_MipmapLevelA.SetCounterValue(0);
				CommandBuffer cmd2 = CommandBufferPool.Get("RenderTexelCoverage");
				cmd2.SetRenderTarget(g_VisibilityBufferID);
				//cmd.SetGlobalBuffer("g_ObjectToAtlasPropertiesRW", g_ObjectToAtlasProperties);
				//cmd.SetRandomWriteTarget(1, g_ObjectToAtlasProperties);

				//g_vertexIDVisiblity_B.SetData(g_vertexIDVisiblity_B_init);
				m_context.ExecuteCommandBuffer(cmd2);

				cameraSortSettings.criteria = SortingCriteria.OptimizeStateChanges;
				RenderOpaque(m_VisibilityPass, cameraSortSettings);

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

				m_context.ExecuteCommandBuffer(cmd2);

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

						m_context.ExecuteCommandBuffer(cmd2);
						cmd2.Clear();

						break;
				}

				CommandBufferPool.Release(cmd2);
				// =====================================================================================================
				// PackAtlas
				// =====================================================================================================
				LogVerbose("PackAtlas...");
				CommandBuffer cmd3 = CommandBufferPool.Get("PackAtlas");
				atlasAxisSize = m_asset.maximalAtlasSizePixel;

				for (int i = 0; i < visibleObjects.Count; i++) {
					visibleObjects[i].SetAtlasProperties(i + 1); //objectID 0 is reserved for "undefined"
				}

				//g_ObjectToAtlasProperties.SetData(g_ObjectToAtlasProperties_data);
				cmd3.SetComputeIntParam(m_ResolveCS, "g_totalObjectsInView", visibleObjects.Count + 1);
				cmd3.SetComputeIntParam(m_ResolveCS, "g_atlasAxisSize", atlasAxisSize);

				cmd3.SetComputeBufferParam(m_ResolveCS, m_cs_AtlasPacking, "g_ObjectToAtlasProperties",
					g_ObjectToAtlasProperties);

				cmd3.DispatchCompute(m_ResolveCS, m_cs_AtlasPacking, 1, 1, 1);

				visibleObjects.Clear();
				m_context.ExecuteCommandBuffer(cmd3);
				CommandBufferPool.Release(cmd3);
				// =====================================================================================================
				// RenderTexelShading
				// =====================================================================================================
				CommandBuffer cmd4 = CommandBufferPool.Get("RenderTexelShading");
				LogVerbose("setup light array...");
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

					Vector4 lightOriginRange;
					// if it's a directional light, just treat it as a point light and place it very far away
					lightOriginRange = light.lightType == LightType.Directional
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
				m_context.ExecuteCommandBuffer(cmd4);

				RenderOpaque(m_TexelSpacePass, cameraSortSettings);

				cmd4.Clear();
				if (m_asset.debugPass == TexelSpaceDebugMode.TexelShadingPass) {
					cmd4.Blit(g_VistaAtlas_A, BuiltinRenderTextureType.CameraTarget);
					m_context.ExecuteCommandBuffer(cmd4);
				}

				CommandBufferPool.Release(cmd4);

				LogVerbose("ReleaseBuffers...");
				CommandBuffer cmd6 = CommandBufferPool.Get("ReleaseBuffers");
				cmd6.ReleaseTemporaryRT(g_PrimitiveVisibilityID);
				m_context.ExecuteCommandBuffer(cmd6);
				CommandBufferPool.Release(cmd6);
			}

			visibleObjects.Clear();

			// ===================================================================================================== 
			// Render Vista + Finalize
			// =====================================================================================================

			CommandBuffer cmdVista = CommandBufferPool.Get("Render Vista");
			cmdVista.SetRenderTarget(g_CameraTarget);
			m_context.ExecuteCommandBuffer(cmdVista);
			cmdVista.Clear();

			if (m_asset.debugPass == TexelSpaceDebugMode.None) {
				cameraSortSettings.criteria = SortingCriteria.OptimizeStateChanges;
				RenderOpaque(m_VistaPass, cameraSortSettings); // render vista
				m_context.DrawSkybox(CURRENT_CAMERA);
			}

			if (m_asset.debugPass == TexelSpaceDebugMode.TexelShadingPass) {
				cmdVista.Blit(g_VistaAtlas_A, g_CameraTarget);
				m_context.ExecuteCommandBuffer(cmdVista);
				cmdVista.Clear();
			}

			cmdVista.Blit(g_CameraTarget, BuiltinRenderTextureType.CameraTarget);
			cmdVista.ReleaseTemporaryRT(g_CameraTarget);
			m_context.ExecuteCommandBuffer(cmdVista);
			CommandBufferPool.Release(cmdVista);
		}

		if (shouldUpdateAtlas) {
			timeSinceLastRender = 0f;
		}

		timeSinceLastRender += Time.deltaTime;

		context.Submit();
		m_asset.memoryConsumption += g_VistaAtlas_A.width * g_VistaAtlas_A.height *
		                             (g_VistaAtlas_A.format == RenderTextureFormat.DefaultHDR ? 8 : 4) * 2;
		m_asset.memoryConsumption /= 1024 * 1024;
	}


	void RenderOpaque(ShaderTagId shaderTagId, SortingSettings sortingSettings) {
		LogVerbose("RenderOpaque...");
		DrawingSettings drawSettings = new DrawingSettings(shaderTagId, sortingSettings);
		FilteringSettings filteringSettings = new FilteringSettings(RenderQueueRange.opaque);
		drawSettings.perObjectData = rendererConfiguration_shading;
		m_context.DrawRenderers(m_CullResults, ref drawSettings, ref filteringSettings);
	}

	public void AddObject(TexelSpaceRenderHelper texelSpaceRenderHelper) {
		visibleObjects.Add(texelSpaceRenderHelper);
	}

	//TODO: reuse depthbuffer from visibility pass for vista pass
	//TODO: check if atlas is acutally large enough

	void LogVerbose(object obj) {
	#if LOG_VERBOSE
        Debug.Log(obj);
	#endif
	}

	struct ObjectToAtlasProperties {
		public uint objectID;
		public uint desiredAtlasSpace_axis;
		public Vector4 atlas_ST;
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