// Upgrade NOTE: replaced '_Object2World' with 'unity_ObjectToWorld'

Shader "TexelShading/Standard"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
		_Smoothness ("Smoothness", Range(0,1)) = 0.5
		_Specular ("_Specular", Color) = (0.5,0.5,0.5)
		_BumpMap("NormalMap", 2D) = "bump" {}
		_SpecGlossMap("Metallic", 2D) = "white" {}
		_OcclusionMap("Occlusion", 2D) = "white" {}
		_EmissionMap("Emission", 2D) = "black" {}
		[HDR] _EmissionColor("_EmissionColor", Color) = (0,0,0,0)
	}
	SubShader
	{
		Tags { "RenderType"="Opaque" "RenderPipeline" = "BasicRenderpipeline"}
		LOD 100
		CGINCLUDE

		#include "TexelShading.cginc" 

		#pragma enable_d3d11_debug_symbols
		StructuredBuffer<ObjectToAtlasProperties> g_ObjectToAtlasProperties;
		StructuredBuffer<ObjectToAtlasProperties> g_prev_ObjectToAtlasProperties;
		StructuredBuffer<uint> _ObjectID_b, _prev_ObjectID_b; // wrap the objectID inside a buffer, since ints cant be set over a materialproperty block
	
		float g_AtlasResolutionScale;
		sampler2D g_prev_VistaAtlas;
		ENDCG
		Pass
		{

			Tags{ "LightMode" = "Visibility Pass" }
			
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 5.0
			#include "UnityCG.cginc"

				struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD1;
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
			RWStructuredBuffer<ObjectToAtlasProperties> g_ObjectToAtlasPropertiesRW;
			uint4 frag(v2f i, uint primID : SV_PrimitiveID) : SV_Target
			{
				float2 dx = ddx(i.uv * g_AtlasResolutionScale);
				float2 dy = ddy(i.uv * g_AtlasResolutionScale);

				// classic mipmap-level calculation
				float rawMipMapLevel = max(max(dot(dx, dx), dot(dy, dy)), 1);
				rawMipMapLevel = min(log2(rawMipMapLevel) * 1, g_AtlasSizeExponent);

				uint clusterID = floor(i.uv.x * 8 *8 + i.uv.y * 8);
				uint mipMapLevel = floor(g_AtlasSizeExponent - rawMipMapLevel);
				uint objectID = _ObjectID_b[0];

				// compute maximal lod level per object on-the-fly, which is very slow, but works so far.
				// note: it's still possible that a part with a high mipmap level is occluded later! 
				// InterlockedMax(g_ObjectToAtlasPropertiesRW[objectID].desiredAtlasSpace_axis, mipMapLevel);
				

				return EncodeVisibilityBuffer(objectID, primID, mipMapLevel);
			}
			ENDCG
		}

		Pass
			{
			// aka PresentPass
			Tags{ "LightMode" = "Vista Pass" }
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
				//return float4(g_ObjectToAtlasProperties[_ObjectID_b[0]].atlas_ST.xy, 0, 0);
				float4 atlasA = tex2D(g_prev_VistaAtlas, i.uvPrev);
				atlasA.rgb /= atlasA.a;
				atlasA.rgb = SimpleTonemapInverse(atlasA.rgb);

				float4 atlasB = tex2D(g_VistaAtlas, i.uv);		
				atlasB.rgb /= atlasB.a;
				atlasB.rgb = SimpleTonemapInverse(atlasB.rgb);

				return lerp(atlasA, atlasB, g_atlasMorph);
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
			#pragma multi_compile _ TRIANGLE_CULLING
			#include "UnityCG.cginc"
			#include "UnityPBSLighting.cginc"
			#include "AutoLight.cginc"

#define UNITY_PASS_FORWARDBASE
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
				o.pack0.zw = v.texcoord1;

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


//#define FULLSCREEN_TRIANGLE_CULLING
			float3 g_CameraPositionWS;
			StructuredBuffer<uint> g_PrimitiveVisibility;


			[maxvertexcount(3)]
			void geom(triangle v2f_surf p[3], inout TriangleStream<v2f_surf> triStream, in uint primID : SV_PrimitiveID)
			{
				float visiblity = 1;
#ifdef TRIANGLE_CULLING
				
#ifdef FULLSCREEN_TRIANGLE_CULLING
				
				uint baseIndex, subIndex;
				GetVisiblityIDIndicies(_ObjectID_b[0], primID, /*out*/ baseIndex, /*out*/ subIndex);

				uint visiblity = g_PrimitiveVisibility[baseIndex] & (1 << subIndex);

#else
				// backface culling
				float3 faceCenterWS =
					float3(p[0].tSpace0.w, p[0].tSpace1.w, p[0].tSpace2.w) +
					float3(p[1].tSpace0.w, p[1].tSpace1.w, p[1].tSpace2.w) +
					float3(p[2].tSpace0.w, p[2].tSpace1.w, p[2].tSpace2.w);
				faceCenterWS /= 3.0;
				float3 viewDirWS = normalize(_WorldSpaceCameraPos - faceCenterWS);
				float3 faceNormalWS = float3(p[0].tSpace0.z, p[0].tSpace1.z, p[0].tSpace2.z);
				//float3 averageNormal = normalize(p[0].tSpace0 + p[1].tSpace0 + p[2].tSpace0);

				visiblity = dot(faceNormalWS, viewDirWS);
#endif
#endif
				if (visiblity > 0)
				{
					v2f_surf p0 = p[0];
					v2f_surf p1 = p[1];
					v2f_surf p2 = p[2];

#ifdef DIALATE_TRIANGLES
					// do conservative raserization 
					// source: https://github.com/otaku690/SparseVoxelOctree/blob/master/WIN/SVO/shader/voxelize.geom.glsl
					//Next we enlarge the triangle to enable conservative rasterization
					float4 AABB;
					float2 hPixel = float2(1.0 / _ScreenParams.x, 1.0 / _ScreenParams.y);
					float pl = 1.4142135637309 / ( max(_ScreenParams.x, _ScreenParams.y));

					//calculate AABB of this triangle
					AABB.xy = p0.pos.xy;
					AABB.zw = p0.pos.xy;

					AABB.xy = min(p1.pos.xy, AABB.xy);
					AABB.zw = max(p1.pos.xy, AABB.zw);

					AABB.xy = min(p2.pos.xy, AABB.xy);
					AABB.zw = max(p2.pos.xy, AABB.zw);

					//Enlarge half-pixel
					AABB.xy -= hPixel;
					AABB.zw += hPixel;

					//find 3 triangle edge plane
					float3 e0 = float3(p1.pos.xy - p0.pos.xy, 0);
					float3 e1 = float3(p2.pos.xy - p1.pos.xy, 0);
					float3 e2 = float3(p0.pos.xy - p2.pos.xy, 0);
					float3 n0 = cross(e0, float3(0, 0, 1));
					float3 n1 = cross(e1, float3(0, 0, 1));
					float3 n2 = cross(e2, float3(0, 0, 1));

					//dilate the triangle
					// julian: I can't figure out why the dilate-offset sometimes produces insane distorted triangels
					// so I normalize the offset, which works pretty well so far
					p0.pos.xy += pl*normalize((e2.xy / dot(e2.xy, n0.xy)) + (e0.xy / dot(e0.xy, n2.xy)));
					p1.pos.xy += pl*normalize((e0.xy / dot(e0.xy, n1.xy)) + (e1.xy / dot(e1.xy, n0.xy)));
					p2.pos.xy += pl*normalize((e1.xy / dot(e1.xy, n2.xy)) + (e2.xy / dot(e2.xy, n1.xy)));
#endif
					triStream.Append(p0);
					triStream.Append(p1);
					triStream.Append(p2);
				}


				triStream.RestartStrip();
			}

#define MAX_LIGHTS 48
			float4 g_LightsOriginRange[MAX_LIGHTS];
			float4 g_LightColorAngle[MAX_LIGHTS];
			int g_LightsCount;

			float3 g_LightDir, _EmissionColor;
			float _Smoothness;
			sampler2D _SpecGlossMap, _OcclusionMap, _EmissionMap;
			half4 frag (v2f_surf i) : SV_Target
			{

				//return 1;
				//float2 clusterID = floor(i.pack0.zw * 16) / 16.0;
				//float clusterIDScalar = clusterID.x * 16.0 + clusterID.y;
				//float3 colorCode = float3(clusterID, 0);
				//return float4(colorCode, 1);

				// this is unity's regular standard shader
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

				half oneMinusReflectivity;
				half3 specColor;
				s.Albedo = EnergyConservationBetweenDiffuseAndSpecular(s.Albedo, s.Specular, /*out*/ oneMinusReflectivity);

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
						s.Specular,
						oneMinusReflectivity,
						s.Smoothness,
						s.Normal,
						worldViewDir,
						gi.light,
						gi.indirect) * lightAtten;
				}

		
			//	outColor += ScreenSpaceDither(i.pos, 1024.0);
				outColor = max(outColor, 0);
				return float4(SimpleTonemap(outColor), 1);
			}
			ENDCG
		}


	}
}
