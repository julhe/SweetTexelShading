// Upgrade NOTE: replaced '_Object2World' with 'unity_ObjectToWorld'

Shader "Compact Deferred"
{
	Properties
	{
		
		_MainTex ("Texture", 2D) = "white" {}
		_Color("Color", Color) = (1,1,1)
		_Smoothness ("Smoothness", Range(0,1)) = 0.5
		_BumpMap("NormalMap", 2D) = "bump" {}
		_OcclusionMap("Occlusion", 2D) = "white" {}
		_EmissionMap("Emission", 2D) = "white" {}
		[HDR] _EmissionColor("_EmissionColor", Color) = (0,0,0,0)
		[Toggle(_METALLIC_WORKFLOW)] _METALLIC_WORKFLOW("Metallic Workflow", int) = 0
		[Header(Specular)]
		[Toggle(_ROUGHNESS_MAPS)] _ROUGHNESS_MAPS("Convert To Smoothness", int) = 0
		_SpecGlossMap("Specular Gloss", 2D) = "white" {}
		_Specular("Specular", Color) = (0.5,0.5,0.5)
		[Header(Metallic)]
		_Metallic("Metallic", Range(0,1)) = 0
		_MetallicMap("Metallic", 2D) = "white" {}
	}
	SubShader
	{
		Tags { "RenderType"="Opaque" "RenderPipeline" = "BasicRenderpipeline"}
		LOD 100
		CGINCLUDE

		#include "CompactDeferred.cginc" 

		#pragma enable_d3d11_debug_symbols
		StructuredBuffer<ObjectToAtlasProperties> g_ObjectToAtlasProperties;
		StructuredBuffer<ObjectToAtlasProperties> g_prev_ObjectToAtlasProperties;
		StructuredBuffer<uint> _ObjectID_b, _prev_ObjectID_b; // wrap the objectID inside a buffer, since ints cant be set over a materialproperty block
	
		float g_AtlasResolutionScale;
		sampler2D g_prev_VistaAtlas;
		ENDCG

		Pass
		{

			Tags{ "LightMode" = "GBuffer" }

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 5.0
			#include "UnityCG.cginc"
			#pragma multi_compile __ _METALLIC_WORKFLOW
			#pragma multi_compile __ _ROUGHNESS_MAPS
			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
				float3 normal : NORMAL;
			};

			struct v2f
			{
				float4 vertex : SV_POSITION;
				float3 worldPos : COLOR;
				float2 pack0 : TEXCOORD0; // _MainTex
				float4 tSpace0 : TEXCOORD1;
				float4 tSpace1 : TEXCOORD2;
				float4 tSpace2 : TEXCOORD3;
			};
			float4 _MainTex_ST, _Color;
			v2f vert(appdata_full v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
			
				o.pack0.xy = TRANSFORM_TEX(v.texcoord, _MainTex);

				float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
				fixed3 worldNormal = UnityObjectToWorldNormal(v.normal);
				fixed3 worldTangent = UnityObjectToWorldDir(v.tangent.xyz);
				fixed tangentSign = v.tangent.w * unity_WorldTransformParams.w;
				fixed3 worldBinormal = cross(worldNormal, worldTangent) * tangentSign;
				o.tSpace0 = float4(worldTangent.x, worldBinormal.x, worldNormal.x, worldPos.x);
				o.tSpace1 = float4(worldTangent.y, worldBinormal.y, worldNormal.y, worldPos.y);
				o.tSpace2 = float4(worldTangent.z, worldBinormal.z, worldNormal.z, worldPos.z);

				return o;
			}
	
			float4x4 cam_worldToView, cam_viewToWorld;
			sampler2D _MainTex, _BumpMap, _SpecGlossMap, _EmissionMap, _OcclusionMap, _MetallicMap;
			RWStructuredBuffer<ObjectToAtlasProperties> g_ObjectToAtlasPropertiesRW;
			float _Smoothness, _Metallic;
			float3 _EmissionColor;
			struct f2r
			{
				uint4 gbuffer : SV_Target0;
			};
			f2r frag(v2f i, uint primID : SV_PrimitiveID)
			{
				float4 albedo = tex2D(_MainTex, i.pack0) * _Color;

				half3 tnormal = UnpackNormal(tex2D(_BumpMap, i.pack0));
				// transform normal from tangent to world space
				half3 worldNormal;
				worldNormal.x = dot(i.tSpace0, tnormal);
				worldNormal.y = dot(i.tSpace1, tnormal);
				worldNormal.z = dot(i.tSpace2, tnormal);

				// transform worldNormal to viewspace
				// doubles the precision in comparision to worldspace
				float3 viewSpaceNormal = mul(cam_worldToView, worldNormal);

				viewSpaceNormal = normalize(viewSpaceNormal);

				float metallic, Smoothness;
#if _METALLIC_WORKFLOW
				metallic = tex2D(_MetallicMap, i.pack0) * _Metallic;
				Smoothness = tex2D(_SpecGlossMap, i.pack0).r;
#else
				float4 specGloss = tex2D(_SpecGlossMap, i.pack0);
				Smoothness = specGloss.a;
				float specGlossAvg = max(specGloss.r, max(specGloss.g, specGloss.b));
				float albedoAvg = max(albedo.r, max(albedo.g, albedo.b));
				metallic = saturate((specGlossAvg - albedoAvg) / (unity_ColorSpaceDielectricSpec.r - specGlossAvg));
				metallic = smoothstep(unity_ColorSpaceDielectricSpec.r, 1, specGlossAvg);
#endif
#if _ROUGHNESS_MAPS
				Smoothness = 1.0 - Smoothness;
#endif
				Smoothness *= _Smoothness;
				float occlusion = tex2D(_OcclusionMap, i.pack0);
				float3 Emission = tex2D(_EmissionMap, i.pack0) * _EmissionColor;

				Emission += ShadeSH9(float4(worldNormal,1)) * albedo.rgb * occlusion;
				uint2 pixelPos = uint2(i.vertex.xy );
				bool checkerboard = isSampleA(pixelPos);

				
				clip(albedo.a - 0.5);
				f2r output;
				output.gbuffer = 0;
				output.gbuffer.xy = EncodeVisibilityBuffer(
					i.vertex.xy,
					checkerboard,
					albedo.rgb, 
					worldNormal, 
					metallic,
					Smoothness,
					occlusion,
					Emission);

				return output;
			}
			ENDCG
		}

		Pass
		{

			Tags{ "LightMode" = "PosPrepass" }

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 5.0
			#include "UnityCG.cginc"

			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
				float3 normal : NORMAL;
			};

			struct v2f
			{
				float4 vertex : SV_POSITION;
				float3 worldPos : COLOR;
			};

			float4 _MainTex_ST;
			v2f vert(appdata_full v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
				return o;
			}

		float4 frag(v2f i) : SV_Target
		{
			return float4(i.worldPos, 0);
		}
			ENDCG
		}

		Pass
		{

			Tags{"LightMode" = "Texel Space Pass"}
			Cull Off
			ZWrite Off
			ZTest Off
			CGPROGRAM
			#pragma vertex vert_surf
			#pragma geometry geom
			#pragma fragment frag
			// make fog work
			#pragma multi_compile_fog
			#pragma multi_compile_fwdbase
			#include "UnityCG.cginc"
			#include "UnityPBSLighting.cginc"
			#include "AutoLight.cginc"
#define UNITY_PASS_FORWARDBASE
			// vertex-to-fragment interpolation data
			// no lightmaps:
#ifndef LIGHTMAP_ON
			struct v2f_surf {
				UNITY_POSITION(pos);
				float2 pack0 : TEXCOORD0; // _MainTex
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
				float2 pack0 : TEXCOORD0; // _MainTex
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
			float4 _MainTex_ST;

			float3 _Specular;
			sampler2D _MainTex, _BumpMap;

			
			
			// vertex shader
			v2f_surf vert_surf(appdata_full v) {
				UNITY_SETUP_INSTANCE_ID(v);
				v2f_surf o;
				UNITY_INITIALIZE_OUTPUT(v2f_surf, o);
				UNITY_TRANSFER_INSTANCE_ID(v, o);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

				float2 atlasCoord = v.texcoord1;
				float4 atlasScaleOffset = g_ObjectToAtlasProperties[_ObjectID_b[0]].atlas_ST;
				atlasCoord = (atlasCoord * atlasScaleOffset.xy) + atlasScaleOffset.zw;
				atlasCoord.y = 1 - atlasCoord.y;
				o.pos = float4(atlasCoord * 2 - 1, 0, 1);
				// 
				o.pack0.xy = TRANSFORM_TEX(v.texcoord, _MainTex);
				float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
				fixed3 worldNormal = UnityObjectToWorldNormal(v.normal);
				fixed3 worldTangent = UnityObjectToWorldDir(v.tangent.xyz);
				fixed tangentSign = v.tangent.w * unity_WorldTransformParams.w;
				fixed3 worldBinormal = cross(worldNormal, worldTangent) * tangentSign;
				o.tSpace0 = float4(worldTangent.x, worldBinormal.x, worldNormal.x, worldPos.x);
				o.tSpace1 = float4(worldTangent.y, worldBinormal.y, worldNormal.y, worldPos.y);
				o.tSpace2 = float4(worldTangent.z, worldBinormal.z, worldNormal.z, worldPos.z);
#ifdef DYNAMICLIGHTMAP_ON
				o.lmap.zw = v.texcoord2.xy * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
#endif
#ifdef LIGHTMAP_ON
				o.lmap.xy = v.texcoord1.xy * unity_LightmapST.xy + unity_LightmapST.zw;
#endif

				// SH/ambient and vertex lights
#ifndef LIGHTMAP_ON
#if UNITY_SHOULD_SAMPLE_SH && !UNITY_SAMPLE_FULL_SH_PER_PIXEL
				o.sh = 0;
				// Approximated illumination from non-important point lights
#ifdef VERTEXLIGHT_ON
				o.sh += Shade4PointLights(
					unity_4LightPosX0, unity_4LightPosY0, unity_4LightPosZ0,
					unity_LightColor[0].rgb, unity_LightColor[1].rgb, unity_LightColor[2].rgb, unity_LightColor[3].rgb,
					unity_4LightAtten0, worldPos, worldNormal);
#endif
				o.sh = ShadeSHPerVertex(worldNormal, o.sh);
#endif
#endif // !LIGHTMAP_ON

				UNITY_TRANSFER_SHADOW(o, v.texcoord1.xy); // pass shadow coordinates to pixel shader
				UNITY_TRANSFER_FOG(o, o.pos); // pass fog coordinates to pixel shader


				return o;
			}


#define FULLSCREEN_TRIANGLE_CULLING
			float3 g_CameraPositionWS;
			Buffer<uint> g_PrimitiveVisibility;


			[maxvertexcount(3)]
			void geom(triangle v2f_surf p[3], inout TriangleStream<v2f_surf> triStream, in uint primID : SV_PrimitiveID)
			{
//#ifdef FULLSCREEN_TRIANGLE_CULLING
//				
//				uint baseIndex, subIndex;
//				GetVisiblityIDIndicies(_ObjectID_b[0], primID, /*out*/ baseIndex, /*out*/ subIndex);
//
//				float visiblity = g_PrimitiveVisibility[baseIndex];// &(subIndex >> 1));
//#else
//				float3 averagePos = (p[0].worldPos + p[1].worldPos + p[2].worldPos) / 3.0;
//				float3 viewDir = normalize((averagePos) -g_CameraPositionWS);
//				float3 averageNormal = normalize(p[0].normal + p[1].normal + p[2].normal);
//
//				float visiblity = dot(averageNormal, viewDir);
//#endif
//				if (visiblity > 0)
//				{
//					v2f_surf p0 = p[0];
//					v2f_surf p1 = p[1];
//					v2f_surf p2 = p[2];
//					// do conservative raserization 
//					// source: https://github.com/otaku690/SparseVoxelOctree/blob/master/WIN/SVO/shader/voxelize.geom.glsl
//					//Next we enlarge the triangle to enable conservative rasterization
//					float4 AABB;
//					float2 hPixel = float2(1.0 / _ScreenParams.x, 1.0 / _ScreenParams.y);
//					float pl = 1.4142135637309 / ( min(_ScreenParams.x, _ScreenParams.y));
//
//					//calculate AABB of this triangle
//					AABB.xy = p0.pos.xy;
//					AABB.zw = p0.pos.xy;
//
//					AABB.xy = min(p1.pos.xy, AABB.xy);
//					AABB.zw = max(p1.pos.xy, AABB.zw);
//
//					AABB.xy = min(p2.pos.xy, AABB.xy);
//					AABB.zw = max(p2.pos.xy, AABB.zw);
//
//					//Enlarge half-pixel
//					AABB.xy -= hPixel;
//					AABB.zw += hPixel;
//
//					//find 3 triangle edge plane
//					float3 e0 = float3(p1.pos.xy - p0.pos.xy, 0);
//					float3 e1 = float3(p2.pos.xy - p1.pos.xy, 0);
//					float3 e2 = float3(p0.pos.xy - p2.pos.xy, 0);
//					float3 n0 = cross(e0, float3(0, 0, 1));
//					float3 n1 = cross(e1, float3(0, 0, 1));
//					float3 n2 = cross(e2, float3(0, 0, 1));
//
//					//dilate the triangle
//					// julian: I can't figure out why the dilate-offset sometimes produces insane distorted triangels
//					// so I normalize the offset, which works pretty well so far
//					p0.pos.xy += pl*normalize((e2.xy / dot(e2.xy, n0.xy)) + (e0.xy / dot(e0.xy, n2.xy)));
//					p1.pos.xy += pl*normalize((e0.xy / dot(e0.xy, n1.xy)) + (e1.xy / dot(e1.xy, n0.xy)));
//					p2.pos.xy += pl*normalize((e1.xy / dot(e1.xy, n2.xy)) + (e2.xy / dot(e2.xy, n1.xy)));
//
//					triStream.Append(p0);
//					triStream.Append(p1);
//					triStream.Append(p2);
//				}
//
//				triStream.RestartStrip();
			}

			float3 g_LightDir, _EmissionColor;
			float _Smoothness;
			sampler2D _SpecGlossMap, _OcclusionMap, _EmissionMap;
			half4 frag (v2f_surf i) : SV_Target
			{
				// this is unity's regular standard shader

				SurfaceOutputStandardSpecular s = (SurfaceOutputStandardSpecular)0;
				s.Albedo = tex2D(_MainTex, i.pack0);
				// sample the normal map, and decode from the Unity encoding
				half3 tnormal = UnpackNormal(tex2D(_BumpMap, i.pack0));
				// transform normal from tangent to world space
				half3 worldNormal;
				worldNormal.x = dot(i.tSpace0, tnormal);
				worldNormal.y = dot(i.tSpace1, tnormal);
				worldNormal.z = dot(i.tSpace2, tnormal);
				s.Normal = worldNormal;
				float4 specGloss = tex2D(_SpecGlossMap, i.pack0);
				s.Smoothness = specGloss.a * _Smoothness;
				s.Specular = specGloss.rgb * _Specular;
				s.Occlusion = tex2D(_OcclusionMap, i.pack0);
				s.Emission = tex2D(_EmissionMap, i.pack0) * _EmissionColor;
				//

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

				outColor += s.Emission;
				return float4(metallic.xxx, 1);
			}
			ENDCG
		}


	}
}
