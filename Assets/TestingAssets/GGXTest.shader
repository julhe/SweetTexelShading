Shader "Unlit/GGXTest"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
	perceptualRoughness("perceptualRoughness", Range(0,1)) = 0
		[Toggle(_SHOW_REF)] _SHOW_REF("Show Reference", int) = 0
		_Alpha("Alpha", float) = 0
		_Beta("Beta", float) = 0
	}
	SubShader
	{
		Tags { "RenderType"="Opaque" }
		LOD 100

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			// make fog work
			#pragma multi_compile_fog
			#pragma multi_compile _ _SHOW_REF
			#include "UnityCG.cginc"
#include "Lighting.cginc"

			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
				float3 normal : NORMAL;
			};

			struct v2f
			{
				float2 uv : TEXCOORD0;
				UNITY_FOG_COORDS(1)
					float3 normalWS : NORMAL;
				float3 normalVS : NORMAL1;
				float3 worldPos : TEXCOORD2;
				float4 vertex : SV_POSITION;
			};

			sampler2D _MainTex;
			float4 _MainTex_ST;
			float _Alpha, _Beta;
			v2f vert (appdata v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.uv, _MainTex);
				o.normalWS = mul(unity_ObjectToWorld, v.normal);
				o.normalVS = mul(UNITY_MATRIX_MV, v.normal);
				o.worldPos = mul(unity_ObjectToWorld, v.vertex);
				UNITY_TRANSFER_FOG(o,o.vertex);
				return o;
			}
#define truncate(x, steps) (floor(x * steps) / steps)
			sampler3D _ggxLUT;
			float perceptualRoughness, _ggxLUT_coordPow, _ggxLUT_roughnessPow;
			fixed4 frag (v2f i) : SV_Target
			{
				float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
				float3 lightDir = -normalize(_WorldSpaceLightPos0);
				float3 halfVector = normalize(lightDir + viewDir);
				i.normalWS = normalize(i.normalWS);
				float NdotL = saturate(dot(i.normalWS, lightDir));
				float NdotV = saturate(dot(i.normalWS, viewDir));
				float LdotH = saturate(dot(lightDir, halfVector));
				float NdotH = saturate(dot(i.normalWS, halfVector));
				//return float4(viewDir, 1);
				//return float4((tex3D(_ggxLUT, viewDir * 0.5 + 0.5).xyz) - viewDir, 0);

				//perceptualRoughness += fwidth(NdotH);
				float roughness = PerceptualRoughnessToRoughness(perceptualRoughness);
				//float NdotV = dot(i.normalWS, viewDir);
				//-----------------------------------------

				float perceptualRoughness = roughness;
				half fd90 = 0.5 + 2 * LdotH * LdotH * perceptualRoughness;
				// Two schlick fresnel term
				half lightScatter = (1 + (fd90 - 1) * Pow5(1 - NdotL));
				half viewScatter = (1 + (fd90 - 1) * Pow5(1 - NdotV));

				half diffuse = lightScatter * viewScatter * NdotL;
				//---------------------------------------
				half a = roughness;
				half lambdaV = NdotL * (NdotV * (1 - a) + a);
				half lambdaL = NdotV * (NdotL * (1 - a) + a);
				half visiblity = 0.5f / (lambdaV + lambdaL + 1e-5f);
				//--------------------

				float a2 = roughness * roughness;
				float d = (NdotH * a2 - NdotH) * NdotH + 1.0f; // 2 mad
				float ggx = GGXTerm(NdotH, roughness); // This function is not intended to be running on Mobile,
															// therefore epsilon is smaller than what can be represented by half
		
#ifdef _SHOW_REF
				float2 uv = float2(NdotL, NdotH);
			//	return uv.xyxy;
				//return pow(NdotL * NdotV * LdotH * NdotH,1);
				return tex2D(_MainTex, uv);
				//return float4(i.uv, 0, 0);
				//return SmithJointGGXVisibilityTerm(i.uv.x, i.uv.y, roughness);
				//return truncate(pow(NdotH, _ggxLUT_coordPow), 8);
				//return GGXTerm(pow(i.uv.x, _Alpha), pow(i.uv.y, _Beta));
				//return float4(NdotH, NdotH, NdotH, perceptualRoughness * 4);
				return ggx * visiblity * NdotL;
#else
				//return GGXTerm(pow(truncate(i.uv.x, 32), _Alpha), truncate(pow(i.uv.y, _Beta), 16));
				float4 params;
				params.x = pow(NdotL, 1);
				params.y = pow(NdotV, 1);
				params.z = pow(NdotH, _ggxLUT_coordPow);
				params.w = pow(roughness, _ggxLUT_roughnessPow) * 4;
				return tex3Dlod(_ggxLUT, params).x * NdotL;
#endif
				
				//return abs(NdotL - NdotV) ;
				//return 0.5f / (lambdaV + lambdaL + 1e-5f);

			}
			ENDCG
		}
	}
}
