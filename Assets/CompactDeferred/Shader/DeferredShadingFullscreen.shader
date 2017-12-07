Shader "Hidden/DeferredShadingFullscreen"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
		g_PosBuffer("Pos", 2D) = "black" {}
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

			#define UNITY_PASS_FORWARDBASE
			#include "UnityCG.cginc"
			#include "TexelShading.cginc" 
			#include "UnityPBSLighting.cginc"
			#include "AutoLight.cginc"

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
			sampler2D g_PosBuffer;
			UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
			float4 globalTest;
			float4 frag (v2f i) : SV_Target
			{
				uint2 pixelPos = uint2(i.uv * _ScreenParams.xy);
				uint4 input = _MainTex[pixelPos];// (sampler_MainTex, i.uv);
				float3 albedo;
				float3 normal, emission;
				float smoothness, metallic;
				DecodeVisibilityBuffer(input.x, /*out*/ albedo, /*out*/ normal, /*out*/ metallic, /*out*/ smoothness);
	
				// this is unity's regular standard shader

				SurfaceOutputStandard s = (SurfaceOutputStandard)0;
				s.Albedo = albedo;
				// sample the normal map, and decode from the Unity encoding
				// transform normal from tangent to world space

				s.Normal = normal;
				float4 specGloss = 0.67;// tex2D(_SpecGlossMap, i.pack0);
				s.Smoothness = smoothness;
				s.Metallic = metallic;
				s.Occlusion = 1;// tex2D(_OcclusionMap, i.pack0);
				s.Emission = 0;// tex2D(_EmissionMap, i.pack0) * _EmissionColor;
				//

				float3 worldPos = tex2D(g_PosBuffer, i.uv).rgb;// float3(i.tSpace0.w, i.tSpace1.w, i.tSpace2.w);
#ifndef USING_DIRECTIONAL_LIGHT
				fixed3 lightDir = normalize(UnityWorldSpaceLightDir(worldPos));
#else
				fixed3 lightDir = _WorldSpaceLightPos0.xyz;
#endif
				fixed3 worldViewDir = normalize(float3(0, 1, 1)); //normalize(UnityWorldSpaceViewDir(worldPos));
				lightDir = normalize(float3(1, 1, 1));
				// compute lighting & shadowing factor
				UNITY_LIGHT_ATTENUATION(atten, i, worldPos)

					// Setup lighting environment
					UnityGI gi;
				UNITY_INITIALIZE_OUTPUT(UnityGI, gi);
				gi.indirect.diffuse = 0;
				gi.indirect.specular = 0;
				gi.light.color = 1;// _LightColor0.rgb;
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
				//LightingStandardSpecular_GI(s, giInput, gi);
				float3 outColor = LightingStandard(
					s,
					worldViewDir,
					gi);

				//outColor +=  s.Emission;
				//float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
				float3 lit = dot(normal, lightDir);
				return float4(abs(outColor), 1);
			}
			ENDCG
		}
	}
}
