﻿Shader "Hidden/EvaluateCoverage"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
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
			#include "UnityCG.cginc"



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
			SamplerState sampler_MainTex;
			struct ObjectToAtlasProperties
			{
				uint objectID; // the original object ID, used to trace back the object after sorting
				uint desiredAtlasSpace_axis; // the length of the texture inside the atlas
				float4 atlas_ST; // scale and offset to transform uv coords into atlas space
			};

			// Current VisiblityBufferLayout with RInt
			// r: 11bit objectID | 16bit primitiveID | 5 mipmap Level
			uint EncodeVisibilityBuffer(uint objectID, uint primitiveID, uint mipmapLevel)
			{
				return objectID | primitiveID << 11 | mipmapLevel << 27;
			}

			void DecodeVisibilityBuffer(uint encodedValue, out uint objectID, out uint primitiveID, out uint mipmapLevel)
			{
				objectID = encodedValue & 0x7FF; //objectID
				primitiveID = (encodedValue >> 11) & 0xFFFF; //primitiveID
				mipmapLevel = (encodedValue >> 27) & 0x1F; //mipmapLevel
			}

			#define MAX_PRIMITIVES_PER_OBJECT 8192
			#define PRIMITIVE_CLUSTER_SIZE 8
			void GetVisiblityIDIndicies(uint objectID, uint primitiveID, out uint baseIndex, out uint subIndex)
			{
				uint index = objectID * MAX_PRIMITIVES_PER_OBJECT + floor(primitiveID / (float)PRIMITIVE_CLUSTER_SIZE);
				baseIndex = index / 32;
				subIndex = index % 32;

				baseIndex = index;
			}

			RWStructuredBuffer<ObjectToAtlasProperties> g_ObjectToAtlasProperties;
			RWBuffer<int> g_VertexIDVisiblity;
		

			float4 globalTest;
			fixed4 frag (v2f i) : SV_Target
			{

				uint4 input = _MainTex[uint2(i.uv * _ScreenParams.xy)];// (sampler_MainTex, i.uv);
				uint objectID, primitiveID, mipmapLevel;
				DecodeVisibilityBuffer(
					input.r,
					/*out*/ objectID,
					/*out*/ primitiveID,
					/*out*/ mipmapLevel);

				// "unlock" primitive for texel shading
				uint baseIndex, subIndex;
				GetVisiblityIDIndicies(objectID, primitiveID, /*out*/ baseIndex, /*out*/ subIndex);
				g_VertexIDVisiblity[baseIndex] = 1;

				// compute maximal lod level on-the-fly
				// (doing it in compute shader is really painfull)
				// (I <3 the fact that this is possible!)
				//InterlockedMax(g_ObjectToAtlasProperties[objectID].desiredAtlasSpace_axis, mipmapLevel);

				float f = g_ObjectToAtlasProperties[objectID].objectID;// g_ObjectToAtlasProperties[objectID].desiredAtlasSpace_axis;

				half4 debugColor = half4(sin(f / 2), sin(f / 512), sin(f / 16384), 0) * 0.5 + 0.5;
				return debugColor;
			}
			ENDCG
		}
	}
}
