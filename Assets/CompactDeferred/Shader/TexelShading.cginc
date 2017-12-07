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
	// Iestyn's RGB dither (7 asm instructions) from Portal 2 X360, slightly modified for VR
	//float3 vDither = float3( dot( float2( 171.0, 231.0 ), vScreenPos.xy + iTime ) );
	float3 vDither = (dot(float2(171.0, 231.0), vScreenPos.xy + _Time.y));
	vDither.rgb = frac(vDither.rgb / float3(103.0, 71.0, 97.0));
	return vDither.rgb / targetRange; //note: looks better without 0.375...

									  //note: not sure why the 0.5-offset is there...
									  //vDither.rgb = fract( vDither.rgb / float3( 103.0, 71.0, 97.0 ) ) - float3( 0.5, 0.5, 0.5 );
									  //return (vDither.rgb / 255.0) * 0.375;
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
	return value* 15.0 ;
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


uint EncodeVisibilityBuffer(float2 vertexPos, float3 albedo, float3 normal, float metallic, float smoothness)
{
	albedo = sqrt(albedo); // do gamma compression, increases resolution of darker values
	albedo += ScreenSpaceDither(vertexPos, 16.0);

	normal.xy = float32x3_to_oct(normal) * 0.5 + 0.5;
	normal += ScreenSpaceDither(vertexPos, 64.0);

	metallic += ScreenSpaceDither(vertexPos, 16.0);
	smoothness += ScreenSpaceDither(vertexPos, 16.0);

	return
		ToUINT_4(albedo.r) |
		ToUINT_4(albedo.g) << 4 |
		ToUINT_4(albedo.b) << 8 |
		ToUINT_6(normal.x) << 12 |
		ToUINT_6(normal.y) << 18 |
 
		ToUINT_4(metallic) << 24 |
		ToUINT_4(smoothness) << 28;
}


void DecodeVisibilityBuffer(uint encodedValue, out float3 albedo, out float3 normal, out float metallic, out float smoothness)
{
	albedo.r = FromUINT_4(encodedValue);
	albedo.g = FromUINT_4(encodedValue >> 4);
	albedo.b = FromUINT_4(encodedValue >> 8);
	albedo = (albedo * albedo);

	normal.x = FromUINT_6(encodedValue >> 12);
	normal.y = FromUINT_6(encodedValue >> 18);
	normal = oct_to_float32x3(normal.xy * 2.0 - 1.0);

	metallic = FromUINT_4(encodedValue >> 24);
	smoothness = FromUINT_4(encodedValue >> 28);
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
