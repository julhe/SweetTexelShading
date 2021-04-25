﻿Shader "Hidden/DeferredShadingFullscreen"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}

		//g_PosBuffer("Pos", 2D) = "black" {}
	}
	SubShader
	{
		// No culling or depth
		Cull Off ZWrite Off ZTest Always

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 5.0
			#pragma enable_d3d11_debug_symbols
			#include "Assets/ClusteredLightning/ClusteredLightning.cginc"
//#define SUPERSAMPLE_MODE
#define UNITY_PASS_FORWARDBASE
			#define UNITY_BRDF_PBS BRDF1_Unity_PBS
			#include "UnityCG.cginc"
			#include "CompactDeferred.cginc" 
			#include "UnityPBSLighting.cginc"
			#include "AutoLight.cginc"
			#pragma multi_compile __ _FULL_GBUFFER
			//#define UNITY_SAMPLE_FULL_SH_PER_PIXEL

		
			
			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct v2f
			{
				float2 uv : TEXCOORD0;
				float4 vertex : SV_POSITION;
			};

			v2f vert (appdata v)
			{
				v2f o;
				
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.uv = v.uv;
				return o;
			}
			
			Texture2D<uint4> _MainTex;

			float4x4 camera_clipToWorld, cam_viewToWorld;
			//samplerCUBE unity_SpecCube0;
			UNITY_DECLARE_DEPTH_TEXTURE(g_Depth);
			float4 globalTest;

			float4 frag (v2f i) : SV_Target
			{
				uint2 pixelPos = uint2(i.uv * _ScreenParams.xy);
//#ifdef SUPERSAMPLE_MODE
//				pixelPos *= uint2(2, 2);
//#endif
//
//#if FULL_GBUFFER
//
//#else
//
//	#ifdef SUPERSAMPLE_MODE
//				
//					float3 albedoSS;
//					float3 normalSS, emissionSS;
//					float smoothnessSS, occlusionSS, metallicSS;
//					uint2 input = _MainTex[pixelPos].xy;
//					uint2 inputB = _MainTex[pixelPos + uint2(1, 0)].xy;
//					DecodeVisibilityBuffer(
//						input,
//						inputB,
//						sampA,
//						/*out*/ albedo,
//						/*out*/ normal,
//						/*out*/ metallic,
//						/*out*/ smoothness,
//						/*out*/ occlusion,
//						/*out*/ emission);
//
//				
//					input = _MainTex[pixelPos + uint2(1,1)];
//					inputB = _MainTex[pixelPos + uint2(0, 1)];
//					DecodeVisibilityBuffer(
//						input,
//						inputB,
//						sampA,
//						/*out*/ albedoSS,
//						/*out*/ normalSS,
//						/*out*/ metallicSS,
//						/*out*/ smoothnessSS,
//						/*out*/ occlusionSS,
//						/*out*/ emissionSS);
//
//					albedo = (albedo + albedoSS) / 2.0;
//					normal = (normal + normalSS) / 2.0;
//					metallic = (metallic + metallicSS) / 2.0;
//					smoothness = (smoothness + smoothnessSS) / 2.0;
//					occlusion = (occlusion + occlusionSS) / 2.0;
//	#else
//					//uint2 input = _MainTex[pixelPos].xy;
//					//uint2 inputB = _MainTex[pixelPos + uint2(1,0)].xy;
//					//DecodeVisibilityBuffer(
//					//	input,
//					//	inputB,
//					//	sampA,
//					//	/*out*/ albedo,
//					//	/*out*/ normal,
//					//	/*out*/ metallic,
//					//	/*out*/ smoothness,
//					//	/*out*/ occlusion,
//					//	/*out*/ emission);
//
//					float4 gbuffer0a = g_gBuffer0[pixelPos];
//					float4 gbuffer1a = g_gBuffer1[pixelPos];
//					float4 gbuffer0b = g_gBuffer0[pixelPos + uint2(1, 0)];
//					float4 gbuffer1b = g_gBuffer1[pixelPos + uint2(1, 0)];
//					UnpackGBuffer(
//						gbuffer0a,
//						gbuffer1a,
//						gbuffer0b,
//						gbuffer1b,
//						sampA,
//						/*out*/ albedo,
//						/*out*/ normal,
//						/*out*/ metallic,
//						/*out*/ smoothness,
//						/*out*/ occlusion,
//						/*out*/ emission);
//	#endif
//
//#endif

				//float3 normalWS = mul(cam_viewToWorld, float4(normal, 0));
				//normalWS = normalize(normalWS);
				// this is unity's regular standard shader

				SurfaceOutputStandard s = UnpackGBuffer(pixelPos);

				float depth = SAMPLE_DEPTH_TEXTURE(g_Depth, i.uv);
				if (depth < 0.000001)
					discard;

				float2 viewportPos = i.uv * 2.0 - 1.0;
				float4 worldPos = mul(camera_clipToWorld, float4(viewportPos.x, viewportPos.y, depth, 1));// float3(i.tSpace0.w, i.tSpace1.w, i.tSpace2.w);
				worldPos.xyz /= worldPos.w;

				float3 ddxWorldPos = ddx(worldPos.xyz);
				float3 ddyWorldPos = ddy(worldPos.xyz);

				float3 analyticNormal = -normalize(cross(ddxWorldPos, ddyWorldPos));

				//s.Normal = analyticNormal;
#ifndef USING_DIRECTIONAL_LIGHT
				fixed3 lightDir = normalize(UnityWorldSpaceLightDir(worldPos));
#else
				fixed3 lightDir = _WorldSpaceLightPos0.xyz;
#endif
				fixed3 worldViewDir = normalize(UnityWorldSpaceViewDir(worldPos));
			
				// compute lighting & shadowing factor
				//UNITY_LIGHT_ATTENUATION(atten, i, worldPos)
				float  atten = 1;
					// Setup lighting environment
					UnityGI gi;
				UNITY_INITIALIZE_OUTPUT(UnityGI, gi);
				gi.indirect.diffuse = 0;// 
				gi.indirect.specular = 0;
				gi.light.color = 0;// _LightColor0.rgb;
				gi.light.dir = 0;// lightDir;
				// Call GI (lightmaps/SH/reflections) lighting function
				UnityGIInput giInput;
				UNITY_INITIALIZE_OUTPUT(UnityGIInput, giInput);
				giInput.light = gi.light;
				giInput.worldPos = worldPos;
				giInput.worldViewDir = worldViewDir;
				giInput.atten = 0;// atten;
#if defined(LIGHTMAP_ON) || defined(DYNAMICLIGHTMAP_ON)
				giInput.lightmapUV = 0;// i.lmap;
#else
				giInput.lightmapUV = 0.0;
#endif
#if UNITY_SHOULD_SAMPLE_SH && !UNITY_SAMPLE_FULL_SH_PER_PIXEL
				giInput.ambient = i.sh;
#else
				giInput.ambient.rgb = 0;
#endif
				giInput.probeHDR[0] = unity_SpecCube0_HDR;
				giInput.probeHDR[1] = unity_SpecCube1_HDR;
#if defined(UNITY_SPECCUBE_BLENDING) || defined(UNITY_SPECCUBE_BOX_PROJECTION)
				giInput.boxMin[0] = unity_SpecCube0_BoxMin; // .w holds lerp value for blending
#endif
#ifdef UNITY_SPECCUBE_BOX_PROJECTION
				giInput.boxMax[0] = unity_SpecCube0_BoxMax;
				giInput.probePosition[0] = unity_SpecCube0_ProbePosition;
				giInput.boxMax[1] = unity_SpecCube1_BoxMax;
				giInput.boxMin[1] = unity_SpecCube1_BoxMin;
				giInput.probePosition[1] = unity_SpecCube1_ProbePosition;
#endif
				//Unity_GlossyEnvironmentData g = UnityGlossyEnvironmentSetup(s.Smoothness, worldViewDir, s.Normal, s.Specular);

				// mainlight
				LightingStandard_GI(s, giInput, gi);

				float3 outColor = LightingStandard(
					s,
					worldViewDir,
					gi);

				outColor += s.Emission;

				half oneMinusReflectivity;
				half3 specColor;
				s.Albedo = DiffuseAndSpecularFromMetallic(s.Albedo, s.Metallic, /*out*/ specColor, /*out*/ oneMinusReflectivity);

				i.vertex.z = (depth);
				outColor += ShadeBRDFLights(
					s.Albedo,
					s.Normal,
					specColor,
					s.Smoothness,
					oneMinusReflectivity,
					gi,
					worldViewDir,
					worldPos,
					i.vertex);
		
			//	float3 lit = dot(normal, lightDir) * _LightColor0 ;
				return float4(outColor, 1);
			}
			ENDCG
		}
	}
}
