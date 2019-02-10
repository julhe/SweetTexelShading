Shader "Unlit/SimpleForward"
{
	Properties
	{
				_MainTex("Texture", 2D) = "white" {}
		_Smoothness("Smoothness", Range(0,1)) = 0.5
		_Specular("_Specular", Color) = (0.5,0.5,0.5)
		_BumpMap("NormalMap", 2D) = "bump" {}
		_SpecGlossMap("Metallic", 2D) = "white" {}
		_OcclusionMap("Occlusion", 2D) = "white" {}
		_EmissionMap("Emission", 2D) = "black" {}
		[HDR] _EmissionColor("_EmissionColor", Color) = (0,0,0,0)
	}
	SubShader
	{
		
		LOD 100
		CGINCLUDE


		ENDCG
		Pass
		{
			Name "SIMPLE_FORWARD_LIT"
			//ZWrite Off
			Tags { "RenderType" = "Opaque"  "RenderPipeline" = "SimpleForward" "LightMode" = "SimpleForward" }
			CGPROGRAM

			#define UNITY_PASS_FORWARDBASE
			#pragma enable_d3d11_debug_symbols
			#pragma vertex vert
			#pragma fragment frag
			// make fog work
			#pragma multi_compile_fog
			#pragma target 6.0
			

			#include "UnityCG.cginc"
			#include "UnityPBSLighting.cginc"
			#include "AutoLight.cginc"
			#include "Assets/ClusteredLightning/ClusteredLightning.cginc"
			#pragma multi_compile_fwdbase

			float3 LightFullBRDF(SurfaceOutputStandardSpecular s, UnityLight light, float oneMinusReflectivity, float3 worldViewDir)
			{
				float perceptualRoughness = SmoothnessToPerceptualRoughness(s.Smoothness);
				float3 halfDir = Unity_SafeNormalize(float3(light.dir) + worldViewDir);

				half nv = saturate(dot(s.Normal, worldViewDir)); // TODO: this saturate should no be necessary here
				half nl = saturate(dot(s.Normal, light.dir));
				half nh = saturate(dot(s.Normal, halfDir));
				half lh = saturate(dot(light.dir, halfDir));

				half diffuseTerm = DisneyDiffuse(nv, nl, lh, perceptualRoughness) * nl;

				half roughness = PerceptualRoughnessToRoughness(perceptualRoughness);
				roughness = max(roughness, 0.002);
				half V = SmithJointGGXVisibilityTerm(nl, nv, roughness);
				half D = GGXTerm(nh, roughness);

				half specularTerm = V * D * UNITY_PI; // Torrance-Sparrow model, Fresnel is applied later
				specularTerm = max(0, specularTerm * nl);

				half grazingTerm = saturate(s.Smoothness + (1 - oneMinusReflectivity));
				half3 color = 
					s.Albedo * (light.color * diffuseTerm)
					+ specularTerm * light.color * FresnelTerm(s.Specular, lh);

				return color;
			}
			// vertex-to-fragment interpolation data
			// no lightmaps:
#ifndef LIGHTMAP_ON
			struct v2f_surf {
				UNITY_POSITION(pos);
				float4 pack0 : TEXCOORD0; // _MainTex
				float4 tSpace0 : TEXCOORD1;
				float4 tSpace1 : TEXCOORD2;
				float4 tSpace2 : TEXCOORD3;
#if UNITY_SHOULD_SAMPLE_SH
				half3 sh : TEXCOORD4; // SH
#endif
				UNITY_SHADOW_COORDS(5)
					UNITY_FOG_COORDS(6)
#if SHADER_TARGET >= 30
					float4 lmap : TEXCOORD7;
#endif
				UNITY_VERTEX_INPUT_INSTANCE_ID
				UNITY_VERTEX_OUTPUT_STEREO
			};
#endif
		// with lightmaps:
#ifdef LIGHTMAP_ON
			struct v2f_surf {
				UNITY_POSITION(pos);
				float4 pack0 : TEXCOORD0; // _MainTex
				float4 tSpace0 : TEXCOORD1;
				float4 tSpace1 : TEXCOORD2;
				float4 tSpace2 : TEXCOORD3;
				float4 lmap : TEXCOORD4;

				UNITY_SHADOW_COORDS(5)
					UNITY_FOG_COORDS(6)
					UNITY_VERTEX_INPUT_INSTANCE_ID
					UNITY_VERTEX_OUTPUT_STEREO
			};
#endif

			sampler2D _BumpMap, _SpecGlossMap, _OcclusionMap, _EmissionMap;
			float _Smoothness;
			float3 _Specular, _EmissionColor;
			sampler2D _MainTex;
			float4 _MainTex_ST;
			
			v2f_surf vert (appdata_full v)
			{
				v2f_surf o = (v2f_surf) 0;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.pack0 = TRANSFORM_TEX(v.texcoord, _MainTex).xyxy;

				float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
				fixed3 worldNormal = UnityObjectToWorldNormal(v.normal);
				fixed3 worldTangent = UnityObjectToWorldDir(v.tangent.xyz);
				fixed tangentSign = v.tangent.w * unity_WorldTransformParams.w;
				fixed3 worldBinormal = cross(worldNormal, worldTangent) * tangentSign;
				o.tSpace0 = float4(worldTangent.x, worldBinormal.x, worldNormal.x, worldPos.x);
				o.tSpace1 = float4(worldTangent.y, worldBinormal.y, worldNormal.y, worldPos.y);
				o.tSpace2 = float4(worldTangent.z, worldBinormal.z, worldNormal.z, worldPos.z);

				UNITY_TRANSFER_FOG(o,o.vertex);
				return o;
			}

			half4 frag (v2f_surf i) : SV_Target
			{

				SurfaceOutputStandardSpecular s = (SurfaceOutputStandardSpecular)0;
				s.Albedo = tex2D(_MainTex, i.pack0.xy);
				// sample the normal map, and decode from the Unity encoding
				half3 tnormal = UnpackNormal(tex2D(_BumpMap, i.pack0.xy));
				// transform normal from tangent to world space
				half3 worldNormal;
				worldNormal.x = dot(i.tSpace0, tnormal);
				worldNormal.y = dot(i.tSpace1, tnormal);
				worldNormal.z = dot(i.tSpace2, tnormal);
				s.Normal = worldNormal;
				float4 specGloss = tex2D(_SpecGlossMap, i.pack0.xy);
				s.Smoothness = specGloss.a * _Smoothness;
				s.Specular = specGloss.rgb * _Specular;
				s.Occlusion = tex2D(_OcclusionMap, i.pack0.xy);
				s.Emission = tex2D(_EmissionMap, i.pack0.xy) * _EmissionColor;

				float3 worldPos = float3(i.tSpace0.w, i.tSpace1.w, i.tSpace2.w);
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
				gi.indirect.diffuse = 0;
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
				LightingStandardSpecular_GI(s, giInput, gi);
				float3 outColor = LightingStandardSpecular(
					s,
					worldViewDir,
					gi);
				//return  i.pos.z;// / _ZBufferParams.x;
				outColor += s.Emission;
			//	return float4(outColor, 1);
				half oneMinusReflectivity;
				half3 specColor;
				s.Albedo = EnergyConservationBetweenDiffuseAndSpecular(
					s.Albedo, 
					s.Specular, 
					/*out*/ oneMinusReflectivity);

				//return float4(i.pos.xy * g_pixelPosToFroxelCoord.xy, 0, 0);
				//gi.indirect.diffuse = 0;
				//gi.indirect.specular = 0;
				//float oneMinRefWave = WaveActiveMin(oneMinRefWave);
				outColor += ShadeBRDFLights(
					s.Albedo,
					s.Normal,
					s.Specular,
					s.Smoothness,
					oneMinusReflectivity,
					gi,
					worldViewDir,
					worldPos,
					i.pos);

				return float4(outColor, 1);
			}
			ENDCG
		}

		Pass
		{
			Name "SIMPLE_FORWARD_DEPTHONLY"
			Tags { "RenderType" = "Opaque"  "RenderPipeline" = "SimpleForward" "LightMode" = "DepthOnly" }

			//ColorMask 0
			CGPROGRAM

			#pragma enable_d3d11_debug_symbols
			#pragma vertex vert
			#pragma fragment frag
				#include "UnityCG.cginc"
			struct surf_depth
			{
				UNITY_POSITION(pos);
			};
			surf_depth vert(appdata_full v)
			{
				surf_depth o;
				o.pos = UnityObjectToClipPos(v.vertex);
				return o;
			}

			half4 frag(surf_depth i) : SV_Target
			{
				return 0;
			}
			ENDCG
		}
	}
}
