#ifndef TEXEL_SHADING_INCLUDE
#include "UnityCG.cginc"
#define MAXIMAL_OBJECTS_PER_VIEW 512
#define MAX_PRIMITIVES_PER_OBJECT 8192
#define PRIMITIVE_CLUSTER_SIZE 16.0
#define COMPUTE_COVERAGE_TILE_SIZE 8
#define ATLAS_TILE_SIZE 128
#define ATLAS_OBJECT_SIZEEXPONENT_MIN 7 // 7^12 = ATLAS_TILE_SIZE, the minmal space an object can be in the atlas
#define ATLAS_OBJECT_SIZEEXPONENT_MAX 12 // 2^12 = 4096, the maximal size an object can be in the atlas
#define SINGLE_ROW_THREAD_SIZE 64
#define BLOCK_THREAD_SIZE 8
float g_AtlasSizeExponent;
// --------common functions--------

sampler2D g_Dither;
float3 ScreenSpaceDither(float2 vScreenPos, float targetRange)
{
	return 0;
#if 0
	return (tex2D(g_Dither, vScreenPos / 32.0) - 0.5 )/ targetRange;
#else
	// Iestyn's RGB dither (7 asm instructions) from Portal 2 X360, slightly modified for VR
//float3 vDither = float3( dot( float2( 171.0, 231.0 ), vScreenPos.xy + iTime ) );
	float3 vDither = (dot(float2(171.0, 231.0), vScreenPos.xy + _Time.y));
	vDither.rgb = frac(vDither.rgb / float3(103.0, 71.0, 97.0));
	return vDither.rgb / targetRange; //note: looks better without 0.375...

									  //note: not sure why the 0.5-offset is there...
									  //vDither.rgb = fract( vDither.rgb / float3( 103.0, 71.0, 97.0 ) ) - float3( 0.5, 0.5, 0.5 );
									  //return (vDither.rgb / 255.0) * 0.375;
#endif

}

// https://software.intel.com/en-us/node/503873
float3 RGB_YCoCg(float3 c)
{
	// Y = R/4 + G/2 + B/4
	// Co = R/2 - B/2
	// Cg = -R/4 + G/2 - B/4
	return float3(
		c.x / 4.0 + c.y / 2.0 + c.z / 4.0,
		c.x / 2.0 - c.z / 2.0,
		-c.x / 4.0 + c.y / 2.0 - c.z / 4.0
		);
}

// https://software.intel.com/en-us/node/503873
float3 YCoCg_RGB(float3 c)
{
	// R = Y + Co - Cg
	// G = Y + Cg
	// B = Y - Co - Cg
	return float3(
		c.x + c.y - c.z,
		c.x + c.z,
		c.x - c.y - c.z
		);
}

float3 ditherLottes(float3 color, float2 vScreenPos, float quantizationSteps)
{
	// This is used to limit the addition of grain around black to avoid increasing the black level.
	// This should be a pre-computed constant.
	// At zero, grain amplitude is limited such that the largest negative grain value would still quantize to zero.
	// Showing the example for sRGB, the ConvertSrgbToLinear() does sRGB to linear conversion.
	float grainBlackLimit = 0.5 * GammaToLinearSpaceExact(1.0 / (quantizationSteps - 1.0));

	// This should also be a pre-computed constant.
	// With the exception of around the blacks, a constant linear amount of grain is added to the image.
	// Technically with low amounts of quantization steps, it would also be good to limit around white as well.
	// Given the primary usage case is high number of quantization steps,
	// limiting around whites is not perceptually important.
	// The largest linear distance between steps is always the highest output value.
	// This sets the constant linear amount of grain to fully dither the highest output value.
	// This does result in a higher-than-required amount of grain in the darks.
	// Using 0.75 leaves overlap to ensure the grain does not disappear at the linear mid-point between steps.
	float grainAmount = 0.75 * (GammaToLinearSpaceExact(1.0 / (quantizationSteps - 1.0)) - 1.0);

	// Point-sampled grain texture scaled to {-1.0 to 1.0}.
	// Note the grain is sampled without a sRGB-to-linear conversion.
	// Grain is a standard RGBA UNORM format (not sRGB labeled).
	float3 grain = tex2D(g_Dither, vScreenPos / 256.0) * 2.0 - 1.0;

	// Apply grain to linear color.
	return grain * min(color + grainBlackLimit, grainAmount) + color;
}


uint ToUINT_2(float value)
{
	return value * 3.0;
}

float FromUINT_2(uint value)
{
	return float(value & 0x00000003) / 3.0;
}

uint ToUINT_3(float value)
{
	return value* 7.0;
}

float FromUINT_3(uint value)
{
	return float(value & 0x00000007) / 7.0;
}


uint ToUINT_4(float value)
{
	return value * 15.0 ;
}

float FromUINT_4(uint value)
{
	return float(value & 0x0000000F) / 15.0;
}

uint ToUINT_5(float value)
{
	return uint(value * 31.0);
}

float FromUINT_5(uint value)
{
	return (value & 0x0000001F) / 31.0;
}

uint ToUINT_6(float value)
{
	return uint(value * 63.0);
}

float FromUINT_6(uint value)
{
	return (value & 0x0000003F) / 63.0;
}

uint ToUINT_7(float value)
{
	return uint(value * 127.0);
}

float FromUINT_7(uint value)
{
	return float(value & 0x0000007F) / 127.0;
}

uint ToUINT_8(float value)
{
	return uint(value * 255.0);
}

float FromUINT_8(uint value)
{
	return float(value & 0x000000FF) / 255.0;
}

// Returns ±1
float2 signNotZero(float2 v) {
	return float2((v.x >= 0.0) ? +1.0 : -1.0, (v.y >= 0.0) ? +1.0 : -1.0);
}
// Assume normalized input. Output is on [-1, 1] for each component.
float2 float32x3_to_oct(in float3 v) {
	// Project the sphere onto the octahedron, and then onto the xy plane
	float2 p = v.xy * (1.0 / (abs(v.x) + abs(v.y) + abs(v.z)));
	// Reflect the folds of the lower hemisphere over the diagonals
	return (v.z <= 0.0) ? ((1.0 - abs(p.yx)) * signNotZero(p)) : p;
}

float3 oct_to_float32x3(float2 e) {
	float3 v = float3(e.xy, 1.0 - abs(e.x) - abs(e.y));
	if (v.z < 0) v.xy = (1.0 - abs(v.yx)) * signNotZero(v.xy);
	return normalize(v);
}


// Assume normalized input on +Z hemisphere.
// Output is on [-1, 1].
float2 float32x3_to_hemioct(in float3 v) {
	// Project the hemisphere onto the hemi-octahedron,
	// and then into the xy plane
	float2 p = v.xy * (1.0 / (abs(v.x) + abs(v.y) + v.z));
	// Rotate and scale the center diamond to the unit square
	return float2(p.x + p.y, p.x - p.y);
}
float3 hemioct_to_float32x3(float2 e) {
	// Rotate and scale the unit square back to the center diamond
	float2 temp = float2(e.x + e.y, e.x - e.y) * 0.5;
	float3 v = float3(temp, 1.0 - abs(temp.x) - abs(temp.y));
	return normalize(v);
}

float3 NeutralTonemapping(in float3 color)
{
	float luma = max(color.r, max(color.g, color.b));
	return color / (1 + luma);
}

float3 InverseNeutralTonemapping(in float3 color)
{
	float luma = saturate(max(color.r, max(color.g, color.b)));
	return color.rgb / (1 - luma);
}

bool isSampleA(uint2 pixelPos)
{
	return step(0.5, ((pixelPos.x + pixelPos.y) * 0.5) % 1.0);
}

uint EncodeVisibilityBuffer(
	float2 vertexPos, 
	bool isSampleA,
	float3 albedo, 
	float3 normal, 
	float3 specColor, 
	float smoothness,
	float occlusion )
{
	half3 dither4 = ScreenSpaceDither(vertexPos, 4.0); 
	half3 dither8 = ScreenSpaceDither(vertexPos, 8.0);// 3
	half3 dither16 = ScreenSpaceDither(vertexPos, 16.0);// 4
	half3 dither32 = ScreenSpaceDither(vertexPos, 32.0);// 5
	half3 dither64 = ScreenSpaceDither(vertexPos, 64.0);// 6
	half3 dither128 = ScreenSpaceDither(vertexPos, 128.0);// 7
	half3 dither256 = ScreenSpaceDither(vertexPos, 256.0);// 8
	albedo = sqrt(albedo);// do gamma compression, increases resolution of darker values
	//albedo = RGB_YCoCg(albedo); 
	//albedo.gb += 0.5;
//	albedo += dither16;
//	normal = normal * 0.5 + 0.5;
	normal.xy = float32x3_to_oct(normal) * 0.5 + 0.5;
	normal += dither256;
	specColor = sqrt(specColor);
	specColor += dither16;

	//smoothness *= smoothness;
	smoothness += dither16;

	occlusion = sqrt(occlusion);
	//occlusion += dither16;
	
	return
		ToUINT_4(isSampleA ? albedo.r : specColor.r) |
		ToUINT_4(isSampleA ? albedo.g : albedo.b) << 4 |

		ToUINT_8(normal.x) << 8 |
		ToUINT_8(normal.y) << 16 |

		ToUINT_4(isSampleA ? occlusion : smoothness) << 24 |
		ToUINT_4(isSampleA ? specColor.g : specColor.b) << 28;
}


//#define FROM_A(isSampleA, encoded)
void DecodeVisibilityBuffer(
	uint encodedValue,
	uint encodedValueB, 
	bool isSampleA,
	out float3 albedo, 
	out float3 normal, 
	out float3 specular, 
	out float smoothness, 
	out float occlusion)
{
	albedo.r = FromUINT_4((isSampleA ? encodedValue : encodedValueB));
	albedo.g = FromUINT_4((isSampleA ? encodedValue : encodedValueB) >> 4);
	albedo.b = FromUINT_4((isSampleA ? encodedValueB : encodedValue) >> 4);
	albedo *= albedo;
	//albedo.gb -= 0.47;
	//albedo = YCoCg_RGB(albedo);

	normal.x = FromUINT_8(encodedValue >> 8);
	normal.y = FromUINT_8(encodedValue >> 16);
	normal = oct_to_float32x3(normal.xy * 2.0 - 1.0);

	smoothness = FromUINT_4((isSampleA ? encodedValueB : encodedValue) >> 24);
	//smoothness = sqrt(smoothness);
	occlusion = FromUINT_4((isSampleA ? encodedValue : encodedValueB) >> 24);
	occlusion *= occlusion;

	specular.r = FromUINT_4((isSampleA ? encodedValueB : encodedValue));
	specular.g = FromUINT_4((isSampleA ? encodedValue : encodedValueB) >> 28);
	specular.b = FromUINT_4((isSampleA ? encodedValueB : encodedValue) >> 28);
	specular *= specular;
}

struct ObjectToAtlasProperties
{
	uint objectID; // the original object ID, used to trace back the object after sorting
	uint desiredAtlasSpace_axis; // the length of the texture inside the atlas
	float4 atlas_ST; // scale and offset to transform uv coords into atlas space
};

//--------utility--------

//source: https://fgiesen.wordpress.com/2009/12/13/decoding-morton-codes/
// "Insert" a 0 bit after each of the 16 low bits of x
uint Part1By1(uint x)
{
	x &= 0x0000ffff;                  // x = ---- ---- ---- ---- fedc ba98 7654 3210
	x = (x ^ (x << 8)) & 0x00ff00ff; // x = ---- ---- fedc ba98 ---- ---- 7654 3210
	x = (x ^ (x << 4)) & 0x0f0f0f0f; // x = ---- fedc ---- ba98 ---- 7654 ---- 3210
	x = (x ^ (x << 2)) & 0x33333333; // x = --fe --dc --ba --98 --76 --54 --32 --10
	x = (x ^ (x << 1)) & 0x55555555; // x = -f-e -d-c -b-a -9-8 -7-6 -5-4 -3-2 -1-0
	return x;
}

// Inverse of Part1By1 - "delete" all odd-indexed bits
uint Compact1By1(uint x)
{
	x &= 0x55555555;                  // x = -f-e -d-c -b-a -9-8 -7-6 -5-4 -3-2 -1-0
	x = (x ^ (x >> 1)) & 0x33333333; // x = --fe --dc --ba --98 --76 --54 --32 --10
	x = (x ^ (x >> 2)) & 0x0f0f0f0f; // x = ---- fedc ---- ba98 ---- 7654 ---- 3210
	x = (x ^ (x >> 4)) & 0x00ff00ff; // x = ---- ---- fedc ba98 ---- ---- 7654 3210
	x = (x ^ (x >> 8)) & 0x0000ffff; // x = ---- ---- ---- ---- fedc ba98 7654 3210
	return x;
}


uint DecodeMorton2X(uint code) { return Compact1By1(code >> 0); }
uint DecodeMorton2Y(uint code) { return Compact1By1(code >> 1); }

#define TEXEL_SHADING_INCLUDE
#endif
