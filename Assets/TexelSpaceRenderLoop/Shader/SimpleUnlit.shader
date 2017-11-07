// Upgrade NOTE: replaced '_Object2World' with 'unity_ObjectToWorld'

Shader "Unlit/SimpleUnlit"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
		_Smoothness ("Smoothness", Range(0,1)) = 0.5
		_Metallic ("Metallic", Range(0,1)) = 0.5
		_BumpMap("NormalMap", 2D) = "bump" {}
		_SpecGlossMap("Metallic", 2D) = "white" {}
		_OcclusionMap("Occlusion", 2D) = "white" {}
	}
	SubShader
	{
		Tags { "RenderType"="Opaque" "RenderPipeline" = "BasicRenderpipeline"}
		LOD 100
		CGINCLUDE
		// keep in sync with compute shader, since includes are broken!!!
		uint EncodeVisibilityBuffer(uint objectID, uint primitiveID, uint mipmapLevel)
		{
			return objectID | primitiveID << 11 | mipmapLevel << 27;
		}

		struct ObjectToAtlasProperties
		{
			uint objectID;
			uint desiredAtlasSpace_axis; // the length of the texture inside the atlas
			float4 atlas_ST; // scale and offset to transform uv coords into atlas space
		};


		#pragma enable_d3d11_debug_symbols
		StructuredBuffer<ObjectToAtlasProperties> g_ObjectToAtlasProperties;
		StructuredBuffer<ObjectToAtlasProperties> g_prev_ObjectToAtlasProperties;
		StructuredBuffer<uint> _ObjectID_b, _prev_ObjectID_b; // wrap the objectID inside a buffer, since ints cant be set over a materialproperty block
		float g_AtlasResolutionScale;
		sampler2D g_prev_VistaAtlas;
		ENDCG
		Pass
		{

			Tags{ "LightMode" = "Coverage Pass" }
			
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag

			#include "UnityCG.cginc"

				struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct v2f
			{
				float4 vertex : SV_POSITION;
				float3 worldPos : COLOR;
				float2 uv : TEXCOORD0;
			};

			v2f vert(appdata v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
				o.uv = v.uv;
				return o;
			}

			uint4 frag(v2f i, uint primID : SV_PrimitiveID) : SV_Target
			{
				//TODO: use worldsize instead
				const float SUB_TEXTURE_SIZE = 8192;
				const float scale = g_AtlasResolutionScale;
				float3 dx = ddx(i.worldPos * scale);
				float3 dy = ddy(i.worldPos * scale);
				float d = max(max(dot(dx, dx), dot(dy, dy)), 1);
				// 2 ^ 13 = 8192 
				d = (log2(d) * 0.5);
				uint mipMapLevel = 13 - floor(d); //TODO: adjust 13 with SUB_TEXTURE_SIZE
				
				return EncodeVisibilityBuffer(_ObjectID_b[0], primID, mipMapLevel);
			}
			ENDCG
		}

		Pass
			{

			Tags{ "LightMode" = "Vista Pass" }
			Cull Off
			CGPROGRAM
				
			#pragma vertex vert
			#pragma fragment frag

			#include "UnityCG.cginc"

			struct appdata
			{
				float4 pos : POSITION;
				float2 uv : TEXCOORD1;
			};

			struct v2f
			{
				float2 uv : TEXCOORD0;
				float2 uvPrev : TEXCOORD1;

				float4 pos : SV_POSITION;
			};

			sampler2D g_VistaAtlas;
			float4 g_VistaAtlas_ST;
			v2f vert(appdata v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.pos);
				o.uv = v.uv;
				o.uvPrev = o.uv;
				float4 atlasScaleOffset = g_ObjectToAtlasProperties[_ObjectID_b[0]].atlas_ST;
				o.uv = (v.uv * atlasScaleOffset.xy) + atlasScaleOffset.zw;
				float4 prev_atlasScaleOffset = g_prev_ObjectToAtlasProperties[_prev_ObjectID_b[0]].atlas_ST;
				o.uvPrev = (v.uv * prev_atlasScaleOffset.xy) + prev_atlasScaleOffset.zw;

				return o;
			}

			float g_atlasMorph;
			half4 frag(v2f i) : SV_Target
			{
				float3 atlasA = tex2D(g_prev_VistaAtlas, i.uvPrev).rgb;
				float3 atlasB = tex2D(g_VistaAtlas, i.uv).rgb;
				return float4(lerp(atlasA, atlasB, g_atlasMorph), 1);
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
			Buffer<float> g_VertexIDVisiblity;
			
			[maxvertexcount(3)]
			void geom(triangle v2f_surf p[3], inout TriangleStream<v2f_surf> triStream, in uint primID : SV_PrimitiveID)
			{
#ifdef FULLSCREEN_TRIANGLE_CULLING
				
				float visiblity = 1;// g_VertexIDVisiblity[EncodeVisibilityBuffer(_ObjectID_b[0], primID, 0)];
#else
				float3 averagePos = (p[0].worldPos + p[1].worldPos + p[2].worldPos) / 3.0;
				float3 viewDir = normalize((averagePos) -g_CameraPositionWS);
				float3 averageNormal = normalize(p[0].normal + p[1].normal + p[2].normal);

				float visiblity = dot(averageNormal, viewDir);
#endif
				if (visiblity > 0)
				{
					v2f_surf p0 = p[0];
					v2f_surf p1 = p[1];
					v2f_surf p2 = p[2];

					triStream.Append(p0);
					triStream.Append(p1);
					triStream.Append(p2);
				}

				//triStream.RestartStrip();
			}

			float3 g_LightDir;
			float _Smoothness, _Metallic;
			sampler2D _SpecGlossMap, _OcclusionMap;
			half4 frag (v2f_surf i) : SV_Target
			{

				// surface
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
				s.Smoothness = specGloss.a;
				s.Specular = specGloss.rgb;
				s.Occlusion = tex2D(_OcclusionMap, i.pack0);

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

				return float4(outColor, 1);
			}
			ENDCG
		}


	}
}
