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

				#define UNITY_BRDF_PBS BRDF1_Unity_PBS
			#include "UnityCG.cginc"
			#include "TexelShading.cginc" 
			#include "UnityPBSLighting.cginc"
			#include "AutoLight.cginc"

		//	#define UNITY_SHOULD_SAMPLE_SH

		
			#define UNITY_PASS_FORWARDBASE
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
			float4 frag (v2f i) : SV_Target
			{
				uint2 pixelPos = uint2(i.uv * _ScreenParams.xy);
				bool sampA = isSampleA(pixelPos);
				uint2 input = _MainTex[pixelPos];
				uint2 inputB = _MainTex[pixelPos + uint2(1,0)];
				float3 albedo;
				float3 normal, emission, specular;
				float smoothness, occlusion;
				DecodeVisibilityBuffer(
					input,
					inputB,
					sampA,
					/*out*/ albedo, 
					/*out*/ normal, 
					/*out*/ specular, 
					/*out*/ smoothness,
					/*out*/ occlusion);

				//float3 normalWS = mul(cam_viewToWorld, float4(normal, 0));
				//normalWS = normalize(normalWS);
				// this is unity's regular standard shader

				SurfaceOutputStandard s = (SurfaceOutputStandard)0;
				s.Albedo = albedo;
				s.Normal = normal;
				s.Smoothness = smoothness;
				s.Metallic = 0;
				s.Occlusion = occlusion;// tex2D(_OcclusionMap, i.pack0);
				s.Emission = 0;// tex2D(_EmissionMap, i.pack0) * _EmissionColor;
				//

				float depth = SAMPLE_DEPTH_TEXTURE(g_Depth, i.uv);
				
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
				UNITY_LIGHT_ATTENUATION(atten, i, worldPos)

					// Setup lighting environment
					UnityGI gi;
				UNITY_INITIALIZE_OUTPUT(UnityGI, gi);
				gi.indirect.diffuse = 0;// ShadeSH9(float4(s.Normal, 1));
				gi.indirect.specular = 0;
				gi.light.color = _LightColor0.rgb;
				gi.light.dir = lightDir;
				// Call GI (lightmaps/SH/reflections) lighting function
				UnityGIInput giInput;
				UNITY_INITIALIZE_OUTPUT(UnityGIInput, giInput);
				giInput.light = gi.light;
				giInput.worldPos = worldPos;
				giInput.worldViewDir = worldViewDir;
				giInput.atten = atten;
#if defined(LIGHTMAP_ON) || defined(DYNAMICLIGHTMAP_ON)
				giInput.lightmapUV = i.lmap;
#else
				giInput.lightmapUV = 0.0;
#endif
#if UNITY_SHOULD_SAMPLE_SH && !UNITY_SAMPLE_FULL_SH_PER_PIXEL
				giInput.ambient = i.sh;
#else
				giInput.ambient.rgb = 0.0;
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

				LightingStandard_GI(s, giInput, gi);
				gi.indirect.diffuse = 0.33;
				float3 outColor = LightingStandard(
					s,
					worldViewDir,
					gi);

			//	BRDF1_Unity_PBS(s.Albedo, s.Specular, )
				//outColor += env * s.Occlusion * 0.67;

				//return sampA;
				//outColor = 
				float3 lit = dot(normal, lightDir);
				return float4(outColor, 1);
			}
			ENDCG
		}
	}
}
