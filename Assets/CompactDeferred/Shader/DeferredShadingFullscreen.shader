Shader "Hidden/DeferredShadingFullscreen"
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

//#define SUPERSAMPLE_MODE
#define UNITY_PASS_FORWARDBASE
			#define UNITY_BRDF_PBS BRDF1_Unity_PBS
			#include "UnityCG.cginc"
			#include "CompactDeferred.cginc" 
			#include "UnityPBSLighting.cginc"
			#include "AutoLight.cginc"

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
			sampler2D g_PosBuffer;
			//samplerCUBE unity_SpecCube0;
			UNITY_DECLARE_DEPTH_TEXTURE(g_Depth);
			float4 globalTest;
#define MAX_LIGHTS 32
			float4 g_LightsOriginRange[MAX_LIGHTS];
			float4 g_LightColorAngle[MAX_LIGHTS];
			int g_LightsCount;
			float4 frag (v2f i) : SV_Target
			{
				uint2 pixelPos = uint2(i.uv * _ScreenParams.xy);
#ifdef SUPERSAMPLE_MODE
				pixelPos *= uint2(2, 2);
#endif
				bool sampA = isSampleA(pixelPos);
				float3 albedo;
				float3 normal,  specular, emission;
				float smoothness, occlusion, metallic;			

#ifdef SUPERSAMPLE_MODE
				
				float3 albedoSS;
				float3 normalSS, emissionSS;
				float smoothnessSS, occlusionSS, metallicSS;
				uint2 input = _MainTex[pixelPos].xy;
				uint2 inputB = _MainTex[pixelPos + uint2(1, 0)].xy;
				DecodeVisibilityBuffer(
					input,
					inputB,
					sampA,
					/*out*/ albedo,
					/*out*/ normal,
					/*out*/ metallic,
					/*out*/ smoothness,
					/*out*/ occlusion,
					/*out*/ emission);

				
				input = _MainTex[pixelPos + uint2(1,1)];
				inputB = _MainTex[pixelPos + uint2(0, 1)];
				DecodeVisibilityBuffer(
					input,
					inputB,
					sampA,
					/*out*/ albedoSS,
					/*out*/ normalSS,
					/*out*/ metallicSS,
					/*out*/ smoothnessSS,
					/*out*/ occlusionSS,
					/*out*/ emissionSS);

				albedo = (albedo + albedoSS) / 2.0;
				normal = (normal + normalSS) / 2.0;
				metallic = (metallic + metallicSS) / 2.0;
				smoothness = (smoothness + smoothnessSS) / 2.0;
				occlusion = (occlusion + occlusionSS) / 2.0;
#else
				uint2 input = _MainTex[pixelPos].xy;
				uint2 inputB = _MainTex[pixelPos + uint2(1,0)].xy;
				DecodeVisibilityBuffer(
					input,
					inputB,
					sampA,
					/*out*/ albedo,
					/*out*/ normal,
					/*out*/ metallic,
					/*out*/ smoothness,
					/*out*/ occlusion,
					/*out*/ emission);
#endif



				//float3 normalWS = mul(cam_viewToWorld, float4(normal, 0));
				//normalWS = normalize(normalWS);
				// this is unity's regular standard shader

				SurfaceOutputStandard s = (SurfaceOutputStandard)0;
				s.Albedo = albedo;
				s.Normal = normal;
				s.Smoothness = smoothness;
				s.Metallic = metallic;
				s.Occlusion = occlusion;// tex2D(_OcclusionMap, i.pack0);
				s.Emission = emission;// tex2D(_EmissionMap, i.pack0) * _EmissionColor;
				//

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

				gi.indirect.diffuse = 0;
				gi.indirect.specular = 0;
				[loop]
				for (int i = 0; i < g_LightsCount; i++)
				{

					float3 lightOrigin = g_LightsOriginRange[i].xyz;
					float lightRange = g_LightsOriginRange[i].w;

					float3 lightRay = lightOrigin - worldPos;
					float lightRayLengthSqr = dot(lightRay, lightRay);
					gi.light.color = g_LightColorAngle[i];
					float lightAtten = saturate(lightRange - length(lightRay));

					
					if (lightAtten < 0.001)
						continue;

					gi.light.dir = normalize(lightRay);
					giInput.light = gi.light;

					outColor += UNITY_BRDF_PBS(
						s.Albedo,
						specColor,
						oneMinusReflectivity, 
						s.Smoothness,
						s.Normal,
						worldViewDir,
						gi.light,
						gi.indirect) * lightAtten ;
				}
				
		
				float3 lit = dot(normal, lightDir) * _LightColor0 ;
				return float4(outColor, 1);
			}
			ENDCG
		}
	}
}
