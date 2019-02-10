// Upgrade NOTE: replaced '_Object2World' with 'unity_ObjectToWorld'

Shader "Compact Deferred / Simple Forward"
{
	Properties
	{
		
		_MainTex ("Texture", 2D) = "white" {}
		[Toggle(_ALPHA_CLIP)] _ALPHA_CLIP("Alpha Cutout", int) = 0
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
		Tags { "RenderType"="Opaque"}
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

			Tags{ "RenderPipeline" = "BasicRenderpipeline" "LightMode" = "GBuffer" }

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 5.0
			#include "UnityCG.cginc"
			#pragma multi_compile __ _METALLIC_WORKFLOW
			#pragma multi_compile __ _ROUGHNESS_MAPS
			#pragma multi_compile __ _ALPHA_CLIP
			#pragma multi_compile __ _FULL_GBUFFER
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

			GBuffers frag(v2f i, uint primID : SV_PrimitiveID)
			{
				SurfaceOutputStandard s = (SurfaceOutputStandard) 0;
				float4 albedo = tex2D(_MainTex, i.pack0) * _Color;
#ifdef _ALPHA_CLIP
				clip(albedo.a - 0.5);
#endif
				s.Albedo = albedo;
				half3 tnormal = UnpackNormal(tex2D(_BumpMap, i.pack0));
				// transform normal from tangent to world space
				half3 worldNormal;
				worldNormal.x = dot(i.tSpace0, tnormal);
				worldNormal.y = dot(i.tSpace1, tnormal);
				worldNormal.z = dot(i.tSpace2, tnormal);

				s.Normal = worldNormal;
				// transform worldNormal to viewspace
				// doubles the precision in comparision to worldspace
				float3 viewSpaceNormal = mul(cam_worldToView, worldNormal);

				viewSpaceNormal = normalize(viewSpaceNormal);

#if _METALLIC_WORKFLOW
				s.Metallic = tex2D(_MetallicMap, i.pack0) * _Metallic;
				s.Smoothness = tex2D(_SpecGlossMap, i.pack0).r;
#else
				float4 specGloss = tex2D(_SpecGlossMap, i.pack0);
				s.Smoothness = specGloss.a;
				float specGlossAvg = max(specGloss.r, max(specGloss.g, specGloss.b));
				float albedoAvg = max(albedo.r, max(albedo.g, albedo.b));
				s.Metallic = saturate((specGlossAvg - albedoAvg) / (unity_ColorSpaceDielectricSpec.r - specGlossAvg));
				s.Metallic = smoothstep(unity_ColorSpaceDielectricSpec.r, 1, specGlossAvg);
#endif
#if _ROUGHNESS_MAPS
				s.Smoothness = 1.0 - s.Smoothness;
#endif
				s.Smoothness *= _Smoothness;
				s.Occlusion = tex2D(_OcclusionMap, i.pack0);
				s.Emission = tex2D(_EmissionMap, i.pack0) * _EmissionColor;

				//s.Emission += ShadeSH9(float4(worldNormal,1)) * s.Albedo.rgb * s.Occlusion;

				GBuffers output = PackGBuffer(i.vertex.xy, s);
				/*
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
					*/
				return output;
			}
			ENDCG
		}

		UsePass "Unlit/SimpleForward/SIMPLE_FORWARD_LIT"
		UsePass "Unlit/SimpleForward/SIMPLE_FORWARD_DEPTHONLY"
	}
}
