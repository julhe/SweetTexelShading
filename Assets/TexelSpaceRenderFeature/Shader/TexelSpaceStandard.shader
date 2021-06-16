// Upgrade NOTE: replaced '_Object2World' with 'unity_ObjectToWorld'

Shader "TexelShading/Standard"
{
	Properties //identdical to the default lit shader
    {
        // Specular vs Metallic workflow
        [HideInInspector] _WorkflowMode("WorkflowMode", Float) = 1.0

        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1,1,1,1)

        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
        _GlossMapScale("Smoothness Scale", Range(0.0, 1.0)) = 1.0
        _SmoothnessTextureChannel("Smoothness texture channel", Float) = 0

        _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _MetallicGlossMap("Metallic", 2D) = "white" {}

        _SpecColor("Specular", Color) = (0.2, 0.2, 0.2)
        _SpecGlossMap("Specular", 2D) = "white" {}

        [ToggleOff] _SpecularHighlights("Specular Highlights", Float) = 1.0
        [ToggleOff] _EnvironmentReflections("Environment Reflections", Float) = 1.0

        _BumpScale("Scale", Float) = 1.0
        _BumpMap("Normal Map", 2D) = "bump" {}

        _Parallax("Scale", Range(0.005, 0.08)) = 0.005
        _ParallaxMap("Height Map", 2D) = "black" {}

        _OcclusionStrength("Strength", Range(0.0, 1.0)) = 1.0
        _OcclusionMap("Occlusion", 2D) = "white" {}

        [HDR] _EmissionColor("Color", Color) = (0,0,0)
        _EmissionMap("Emission", 2D) = "white" {}

        _DetailMask("Detail Mask", 2D) = "white" {}
        _DetailAlbedoMapScale("Scale", Range(0.0, 2.0)) = 1.0
        _DetailAlbedoMap("Detail Albedo x2", 2D) = "linearGrey" {}
        _DetailNormalMapScale("Scale", Range(0.0, 2.0)) = 1.0
        [Normal] _DetailNormalMap("Normal Map", 2D) = "bump" {}

        // SRP batching compatibility for Clear Coat (Not used in Lit)
        [HideInInspector] _ClearCoatMask("_ClearCoatMask", Float) = 0.0
        [HideInInspector] _ClearCoatSmoothness("_ClearCoatSmoothness", Float) = 0.0

        // Blending state
        [HideInInspector] _Surface("__surface", Float) = 0.0
        [HideInInspector] _Blend("__blend", Float) = 0.0
        [HideInInspector] _AlphaClip("__clip", Float) = 0.0
        [HideInInspector] _SrcBlend("__src", Float) = 1.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0
        [HideInInspector] _ZWrite("__zw", Float) = 1.0
        [HideInInspector] _Cull("__cull", Float) = 2.0

        _ReceiveShadows("Receive Shadows", Float) = 1.0
        // Editmode props
        [HideInInspector] _QueueOffset("Queue offset", Float) = 0.0

        // ObsoleteProperties
        [HideInInspector] _MainTex("BaseMap", 2D) = "white" {}
        [HideInInspector] _Color("Base Color", Color) = (1, 1, 1, 1)
        [HideInInspector] _GlossMapScale("Smoothness", Float) = 0.0
        [HideInInspector] _Glossiness("Smoothness", Float) = 0.0
        [HideInInspector] _GlossyReflections("EnvironmentReflections", Float) = 0.0

        [HideInInspector][NoScaleOffset]unity_Lightmaps("unity_Lightmaps", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_LightmapsInd("unity_LightmapsInd", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_ShadowMasks("unity_ShadowMasks", 2DArray) = "" {}
    }
	SubShader
	{
		Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline"}
		LOD 100
		HLSLINCLUDE

		#include "TexelShading.cginc"
		#include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
		#include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"

		#pragma enable_d3d11_debug_symbols

		#define TSS_VISIBLITY_BY_CPU
		StructuredBuffer<ObjectToAtlasProperties> g_ObjectToAtlasProperties;
		StructuredBuffer<ObjectToAtlasProperties> g_prev_ObjectToAtlasProperties;
		RWStructuredBuffer<ObjectToAtlasProperties> g_ObjectToAtlasPropertiesRW;
		uint _ObjectID_b, _prev_ObjectID_b; 
	
		float g_AtlasResolutionScale;
		sampler2D g_prev_VistaAtlas, g_VistaAtlas;
		float4 g_VistaAtlas_ST;
		float3 _CameraForwardDirection; //TODO: can be taken from the camera view matrix?

		float4 _VistaAtlasScaleOffset;
		float4 GetObjectAtlasScaleOffset()
		{
			return _VistaAtlasScaleOffset;
			// return g_ObjectToAtlasProperties[_ObjectID_b].atlas_ST;
		}
		ENDHLSL
		Pass
		{
			Tags{ "LightMode" = "Visibility Pass" }
			HLSLPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 5.0

			//--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

			struct v2f
			{
				float4 positionCS : SV_POSITION;
				float3 positionWS : COLOR;
				float2 uv : TEXCOORD0;
				UNITY_VERTEX_INPUT_INSTANCE_ID
				UNITY_VERTEX_OUTPUT_STEREO
			};

			v2f vert(Attributes input)
			{
				v2f output = (v2f)0;
			    UNITY_SETUP_INSTANCE_ID(input);
			    UNITY_TRANSFER_INSTANCE_ID(input, output);
			    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

				VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
				output.uv = input.lightmapUV;
				output.positionCS = vertexInput.positionCS;
				output.positionWS = vertexInput.positionWS;

				return output;
			}
			
			
			uint4 frag(v2f i, uint primID : SV_PrimitiveID) : SV_Target
			{
				UNITY_SETUP_INSTANCE_ID(i);
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
				float2 dx = ddx(i.uv * g_AtlasResolutionScale);
				float2 dy = ddy(i.uv * g_AtlasResolutionScale);

				// classic mipmap-level calculation
				float rawMipMapLevel = max(max(dot(dx, dx), dot(dy, dy)), 1);
				rawMipMapLevel = min(log2(rawMipMapLevel) * 1, g_AtlasSizeExponent);

				uint clusterID = floor(i.uv.x * 8 * 8 + i.uv.y * 8);
				uint mipMapLevel = floor(g_AtlasSizeExponent - rawMipMapLevel);

				// compute maximal lod level per object on-the-fly, which is very slow, but works so far.
				// note: it's still possible that a part with a high mipmap level is occluded later! 
				// InterlockedMax(g_ObjectToAtlasPropertiesRW[_ObjectID_b].sizeExponent, mipMapLevel);
				return EncodeVisibilityBuffer(_ObjectID_b, primID, mipMapLevel);
			}
			ENDHLSL
		}

		Pass
			{
			// aka PresentPass
			Tags{ "LightMode" = "UniversalForward" }
			HLSLPROGRAM
				
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 5.0

			//--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

			struct v2f
			{
				float2 uv : TEXCOORD0;
				float2 uvPrev : TEXCOORD1;
				float2 uvRaw : TEXCOORD2;
				float4 positionCS : SV_POSITION;
				UNITY_VERTEX_INPUT_INSTANCE_ID
				UNITY_VERTEX_OUTPUT_STEREO
			};

			v2f vert(Attributes input)
			{
				v2f output = (v2f)0;
			    UNITY_SETUP_INSTANCE_ID(input);
			    UNITY_TRANSFER_INSTANCE_ID(input, output);
			    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

				VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
				
				output.positionCS = vertexInput.positionCS;
				output.uvRaw = input.lightmapUV;
				
				const float4 atlasScaleOffset = GetObjectAtlasScaleOffset(); 
				output.uv = input.lightmapUV * atlasScaleOffset.xy + atlasScaleOffset.zw;
				const float4 prev_atlasScaleOffset = g_prev_ObjectToAtlasProperties[_prev_ObjectID_b].atlas_ST;
				output.uvPrev = (input.lightmapUV * prev_atlasScaleOffset.xy) + prev_atlasScaleOffset.zw;

				return output;
			}

			float g_atlasMorph;
			
            float _Tss_DebugView;
			half4 frag(v2f i) : SV_Target
			{
				UNITY_SETUP_INSTANCE_ID(i);
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
				//return float4(i.uv, 0, 1);
				
				float4 atlasA = tex2D(g_prev_VistaAtlas, i.uvPrev);
				//atlasA.rgb /= atlasA.a;
				//atlasA.rgb = SimpleTonemapInverse(atlasA.rgb);

				float4 atlasB = tex2D(g_VistaAtlas, i.uv);		
				atlasB.rgb /= atlasB.a;
				//atlasB.rgb = SimpleTonemapInverse(atlasB.rgb);




				#ifndef TSS_VISIBLITY_BY_CPU
					float4 finalColor = lerp(atlasA, atlasB, g_atlasMorph);
					
					// compute maximal lod level per object on-the-fly, which is very slow, but works so far.
					// note: it's still possible that a part with a high mipmap level is occluded later!

					float2 dx = ddx(i.uvRaw * g_AtlasResolutionScale);
					float2 dy = ddy(i.uvRaw * g_AtlasResolutionScale);

					// classic mipmap-level calculation
					float rawMipMapLevel = max(max(dot(dx, dx), dot(dy, dy)), 1);
					rawMipMapLevel = min(log2(rawMipMapLevel), g_AtlasSizeExponent);
					uint mipMapLevel = floor(g_AtlasSizeExponent - rawMipMapLevel);
					InterlockedMax(g_ObjectToAtlasPropertiesRW[_ObjectID_b].sizeExponent, mipMapLevel);
					g_ObjectToAtlasPropertiesRW[_ObjectID_b].atlas_ST = 0;
				#endif
				//finalColor.rgb = TonemappingACES(finalColor.rgb);

				return lerp(atlasB , half4(0.0, 1, 0.0, 1.0), _Tss_DebugView);
			}
			ENDHLSL
		}

		Pass
		{

			// Shading pass
			// Reuses the URP Code path to render to the atlas.
			// Right now, it only does adjust the output vertex coordinates from the vertex shader.
			
			Tags{"LightMode" = "Texel Space Pass"}
			
			//Blend[_SrcBlend][_DstBlend]
            ZWrite Off
			ZTest Off
            Cull Off

            HLSLPROGRAM
            //#pragma only_renderers gles gles3 glcore
            #pragma target 5.0

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local _PARALLAXMAP
            #pragma shader_feature_local _ _DETAIL_MULX2 _DETAIL_SCALED
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma shader_feature_local_fragment _SPECULAR_SETUP
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF

            // -------------------------------------
            // Universal Pipeline keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fog

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #pragma vertex TsLitPassVertex
            #pragma fragment LitPassFragment

            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"


            Varyings TsLitPassVertex(Attributes input)
			{
				// run the original vertex pass
				Varyings output = LitPassVertex(input);

				// now use the lightmap uv as the output uv
				// clamp uv map to prevent bad uv-unwrapping from messing up the atlas and massively decrease the performance
				float2 atlasCoord = saturate(input.lightmapUV);
				const float4 atlasScaleOffset = GetObjectAtlasScaleOffset();
				atlasCoord = atlasCoord * atlasScaleOffset.xy + atlasScaleOffset.zw;
				//TODO: also for D3D12, etc...
				#if defined(SHADER_API_D3D11) 
					atlasCoord.y = 1 - atlasCoord.y;
				#endif
			
				output.positionCS = float4(atlasCoord * 2.0 - 1.0, 0.0, 1.0);
				return output;
			}

            half4 TssLitPassFragment(Varyings input) : SV_Target{
                UNITY_SETUP_INSTANCE_ID(input);
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            	// might be a bad idea...
            	if(dot(input.normalWS, _CameraForwardDirection) > 0.5)
            	{
            		return 0.0;
            	}
            	return LitPassFragment(input);
            }
            
// //#define FULLSCREEN_TRIANGLE_CULLING
// 			float3 g_CameraPositionWS;
// 			StructuredBuffer<uint> g_PrimitiveVisibility;
//
//
// 			[maxvertexcount(3)]
// 			void geom(triangle v2f_surf p[3], inout TriangleStream<v2f_surf> triStream, in uint primID : SV_PrimitiveID)
// 			{
// 				float visiblity = 1;
// #ifdef TRIANGLE_CULLING
// 				
// #ifdef FULLSCREEN_TRIANGLE_CULLING
// 				
// 				uint baseIndex, subIndex;
// 				GetVisiblityIDIndicies(_ObjectID_b, primID, /*out*/ baseIndex, /*out*/ subIndex);
//
// 				uint visiblity = g_PrimitiveVisibility[baseIndex] & (1 << subIndex);
//
// #else
// 				// backface culling
// 				float3 faceCenterWS =
// 					float3(p[0].tSpace0.w, p[0].tSpace1.w, p[0].tSpace2.w) +
// 					float3(p[1].tSpace0.w, p[1].tSpace1.w, p[1].tSpace2.w) +
// 					float3(p[2].tSpace0.w, p[2].tSpace1.w, p[2].tSpace2.w);
// 				faceCenterWS /= 3.0;
// 				float3 viewDirWS = normalize(_WorldSpaceCameraPos - faceCenterWS);
// 				float3 faceNormalWS = float3(p[0].tSpace0.z, p[0].tSpace1.z, p[0].tSpace2.z);
// 				//float3 averageNormal = normalize(p[0].tSpace0 + p[1].tSpace0 + p[2].tSpace0);
//
// 				visiblity = dot(faceNormalWS, viewDirWS);
// #endif
// #endif
// 				if (visiblity > 0)
// 				{
// 					v2f_surf p0 = p[0];
// 					v2f_surf p1 = p[1];
// 					v2f_surf p2 = p[2];
//
// #ifdef DIALATE_TRIANGLES
// 					// do conservative raserization 
// 					// source: https://github.com/otaku690/SparseVoxelOctree/blob/master/WIN/SVO/shader/voxelize.geom.glsl
// 					//Next we enlarge the triangle to enable conservative rasterization
// 					float4 AABB;
// 					float2 hPixel = float2(1.0 / _ScreenParams.x, 1.0 / _ScreenParams.y);
// 					float pl = 1.4142135637309 / ( max(_ScreenParams.x, _ScreenParams.y));
//
// 					//calculate AABB of this triangle
// 					AABB.xy = p0.pos.xy;
// 					AABB.zw = p0.pos.xy;
//
// 					AABB.xy = min(p1.pos.xy, AABB.xy);
// 					AABB.zw = max(p1.pos.xy, AABB.zw);
//
// 					AABB.xy = min(p2.pos.xy, AABB.xy);
// 					AABB.zw = max(p2.pos.xy, AABB.zw);
//
// 					//Enlarge half-pixel
// 					AABB.xy -= hPixel;
// 					AABB.zw += hPixel;
//
// 					//find 3 triangle edge plane
// 					float3 e0 = float3(p1.pos.xy - p0.pos.xy, 0);
// 					float3 e1 = float3(p2.pos.xy - p1.pos.xy, 0);
// 					float3 e2 = float3(p0.pos.xy - p2.pos.xy, 0);
// 					float3 n0 = cross(e0, float3(0, 0, 1));
// 					float3 n1 = cross(e1, float3(0, 0, 1));
// 					float3 n2 = cross(e2, float3(0, 0, 1));
//
// 					//dilate the triangle
// 					// julian: I can't figure out why the dilate-offset sometimes produces insane distorted triangels
// 					// so I normalize the offset, which works pretty well so far
// 					p0.pos.xy += pl*normalize((e2.xy / dot(e2.xy, n0.xy)) + (e0.xy / dot(e0.xy, n2.xy)));
// 					p1.pos.xy += pl*normalize((e0.xy / dot(e0.xy, n1.xy)) + (e1.xy / dot(e1.xy, n0.xy)));
// 					p2.pos.xy += pl*normalize((e1.xy / dot(e1.xy, n2.xy)) + (e2.xy / dot(e2.xy, n1.xy)));
// #endif
// 					triStream.Append(p0);
// 					triStream.Append(p1);
// 					triStream.Append(p2);
// 				}
//
//
// 				triStream.RestartStrip();
// 			}


			ENDHLSL
		}


	}
	FallBack "Universal Render Pipeline/Lit"
    CustomEditor "UnityEditor.Rendering.Universal.ShaderGUI.LitShader"
}
