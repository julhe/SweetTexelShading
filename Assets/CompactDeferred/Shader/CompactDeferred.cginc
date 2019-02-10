#ifndef TEXEL_SHADING_INCLUDE
#include "UnityCG.cginc"
#include "UnityPBSLighting.cginc"
#include "CompactDeferred Utility.cginc"
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
	//return 0;
#if 1
	float3 blueNoise = tex2D(g_Dither, vScreenPos / 32.0) ;
	blueNoise /= targetRange ;
	return blueNoise;
	//blueNoise = mad(blueNoise, 2.0f, -1.0f);
	//blueNoise = sign(blueNoise)*(1.0f - sqrt(1.0f - abs(blueNoise)));
	//blueNoise /= targetRange * 2;
	//return blueNoise;

#else
	// Iestyn's RGB dither (7 asm instructions) from Portal 2 X360, slightly modified for VR
//float3 vDither = float3( dot( float2( 171.0, 231.0 ), vScreenPos.xy + iTime ) );
	float3 vDither = (dot(float2(171.0, 231.0), vScreenPos.xy + _Time.y * 0));
	vDither.rgb = frac(vDither.rgb / float3(103.0, 71.0, 97.0)) ;
	return vDither.rgb / targetRange; //note: looks better without 0.375...

									  //note: not sure why the 0.5-offset is there...
									  //vDither.rgb = fract( vDither.rgb / float3( 103.0, 71.0, 97.0 ) ) - float3( 0.5, 0.5, 0.5 );
									  //return (vDither.rgb / 255.0) * 0.375;
#endif

}

// https://software.intel.com/en-us/node/503873
float3 RGB_YCoCg(float3 c)
{
    float3x3 RGB_To_YCoCg =
    {
        +0.25, 0.5, +0.25,
        +0.50, 0.0, -0.50,
        -0.25, 0.5, -0.25,
    };
	// Y = R/4 + G/2 + B/4
	// Co = R/2 - B/2
	// Cg = -R/4 + G/2 - B/4
    return mul(RGB_To_YCoCg, c);
	//return float3(
	//	c.x / 4.0 + c.y / 2.0 + c.z / 4.0,
	//	c.x / 2.0 - c.z / 2.0,
	//	-c.x / 4.0 + c.y / 2.0 - c.z / 4.0
	//	);
}

// https://software.intel.com/en-us/node/503873
float3 YCoCg_RGB(float3 c)
{

    float3x3 YCoCg_To_RGB =
    {
        +1, 1, -1,
        +1, 0,  1,
        +1, -1, -1,
    };
    return mul(YCoCg_To_RGB, c);
	// R = Y + Co - Cg
	// G = Y + Cg
	// B = Y - Co - Cg
//	return float3(
//		c.x + c.y - c.z,
//		c.x + c.z,
//		c.x - c.y - c.z
//		);
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


// Returns ±1
float2 signNotZero(float2 v) {
	return float2((v.x >= 0.0) ? +1.0 : -1.0, (v.y >= 0.0) ? +1.0 : -1.0);
}
// Assume normalized input. Output is on [-1, 1] for each component.
float2 float32x3_to_oct(in float3 v) {
	// Project the sphere onto the octahedron, and then onto the xy plane
    float2 p = v.xy * rcp( dot( abs(v), 1) );
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
	float2 p = v.xy * rcp( (abs(v.x) + abs(v.y) + v.z));
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

struct GBuffers
{
    //uint4 gbuffer : SV_Target0;
    float4 gbuffer0 : SV_Target0;
    float4 gbuffer1 : SV_Target1;
#ifdef _FULL_GBUFFER
	float4 gbuffer2 : SV_Target2;
	float4 gbuffer3 : SV_Target3;
#endif
};

bool isSampleA(uint2 pixelPos)
{
	return step(0.5, ((pixelPos.x + pixelPos.y) * 0.5) % 1.0);
}

#ifdef SINGLE_CHANNEL_GBUFFER
#define GBUFFER_FORMAT uint
#else
#define GBUFFER_FORMAT uint2
#endif

Texture2D<float4> g_gBuffer0, g_gBuffer1, g_gBuffer2, g_gBuffer3;
GBuffers PackGBuffer(
	float2 vertexPos, 
    SurfaceOutputStandard s
)
{
    half3 dither4 = ScreenSpaceDither(vertexPos, 4.0);
    half3 dither8 = ScreenSpaceDither(vertexPos, 8.0); // 3
    half3 dither16 = ScreenSpaceDither(vertexPos, 16.0); // 4
    half3 dither32 = ScreenSpaceDither(vertexPos, 32.0); // 5
    half3 dither64 = ScreenSpaceDither(vertexPos, 64.0); // 6
    half3 dither128 = ScreenSpaceDither(vertexPos, 128.0); // 7
    half3 dither256 = ScreenSpaceDither(vertexPos, 256.0); // 8
    half3 dither512 = ScreenSpaceDither(vertexPos, 512.0); // 9
    half3 dither1024 = ScreenSpaceDither(vertexPos, 1024.0); // 10
    GBuffers output;

    #ifdef _FULL_GBUFFER

        output.gbuffer0.rgb = s.Albedo;
        output.gbuffer0.a = s.Occlusion;
        output.gbuffer1.rgb =  s.Normal * 0.5 + 0.5;
        output.gbuffer2.r =  s.Metallic;
        output.gbuffer2.g = s.Smoothness;
        output.gbuffer3.rgb =  s.Emission;
    #else

        bool isSmpA = isSampleA(uint2(vertexPos));

        // World-Space Normals
        s.Normal.xy = float32x3_to_oct(s.Normal) * 0.5 + 0.5;
       // normal.xy += dither1024;
        output.gbuffer0.xy = s.Normal;

         // Occlusion 
        s.Occlusion = sqrt(s.Occlusion);
        s.Occlusion += dither4;
        output.gbuffer0.w = s.Occlusion;

        // Smoothness / Metallic
        s.Metallic *= s.Metallic;
        output.gbuffer1.z = isSmpA ? s.Metallic : s.Smoothness;

        // Albedo
        s.Albedo = sqrt(s.Albedo); // do gamma compression, reduces banding in darker areas
        s.Albedo = RGB_YCoCg(s.Albedo);
        s.Albedo.gb += 0.5; // move CoCg to from [-0.5, 0.5] to [0,1] range (TODO: put this into YCoCg conversion)
        s.Albedo += dither256;

        output.gbuffer1.x = s.Albedo.x;
        output.gbuffer1.y = isSmpA ? s.Albedo.y : s.Albedo.z;
    
        // Emission
        s.Emission = saturate(s.Emission); //TODO: do FastTonemap to go beyond 1.0
        s.Emission = sqrt(s.Emission);
        s.Emission = RGB_YCoCg(s.Emission);
    
        s.Emission.gb += 0.5; // move CoCg to [0,1] range (can be simpified)
        s.Emission += dither256;

        output.gbuffer0.z = s.Emission.x;
        output.gbuffer1.w = isSmpA ? s.Emission.y : s.Emission.z;
    #endif
    return output;
}

SurfaceOutputStandard UnpackGBuffer(uint2 pixelPosition)
{
    SurfaceOutputStandard s = (SurfaceOutputStandard) 0;
    #ifdef _FULL_GBUFFER
        float4 gbuffer0 = g_gBuffer0[pixelPosition];
        float4 gbuffer1 = g_gBuffer1[pixelPosition];
        float4 gbuffer2 = g_gBuffer2[pixelPosition];
        float4 gbuffer3 = g_gBuffer3[pixelPosition];

        s.Albedo = gbuffer0.rgb;
        s.Occlusion = gbuffer0.a;

        s.Normal = normalize(gbuffer1.rgb * 2.0 - 1.0);
        
        s.Metallic = gbuffer2.r;
        s.Smoothness = gbuffer2.g;
       
        s.Emission = gbuffer3.rgb;
    #else

        bool isSmpA = isSampleA(pixelPosition);
        float4 gbuffer0a = g_gBuffer0[pixelPosition];
        float4 gbuffer1a = g_gBuffer1[pixelPosition];
        float4 gbuffer0b = g_gBuffer0[pixelPosition + uint2(1, 0)];
        float4 gbuffer1b = g_gBuffer1[pixelPosition + uint2(1, 0)];

        s.Normal.xy = gbuffer0a.xy;
        s.Normal = oct_to_float32x3(s.Normal.xy * 2.0 - 1.0);

        s.Albedo.r = gbuffer1a.x;
        s.Albedo.g = isSmpA ? gbuffer1a.y : gbuffer1b.y;
        s.Albedo.b = isSmpA ? gbuffer1b.y : gbuffer1a.y;
        s.Albedo.gb -= 0.5; // map CoCg back to [-0.5, 0.5]
        s.Albedo = YCoCg_RGB(s.Albedo);
        s.Albedo *= s.Albedo; // reverse gamma compression

        s.Occlusion = gbuffer0a.w * gbuffer0a.w;

        s.Metallic = isSmpA ? gbuffer1a.z : gbuffer1b.z;
        s.Metallic = sqrt(s.Metallic);

        s.Smoothness = isSmpA ? gbuffer1b.z : gbuffer1a.z;

        s.Emission.r = gbuffer0a.z;
        s.Emission.g = (isSmpA ? gbuffer1a.w : gbuffer1b.w);
        s.Emission.b = (isSmpA ? gbuffer1b.w : gbuffer1a.w);
    
        s.Emission.gb -= 0.5; // map CoCg back to [-0.5, 0.5]
        s.Emission = YCoCg_RGB(s.Emission);
        s.Emission *= s.Emission; // reverse gamma compression
    #endif

    return s;
}


GBUFFER_FORMAT EncodeVisibilityBuffer(
	float2 vertexPos, 
	bool isSampleA,
	float3 albedo, 
	float3 normal, 
	float metallic,
	float smoothness,
	float occlusion,
    float3 emission)
{
    half3 dither4 = ScreenSpaceDither(vertexPos, 4.0);
    half3 dither8 = ScreenSpaceDither(vertexPos, 8.0); // 3
    half3 dither16 = ScreenSpaceDither(vertexPos, 16.0); // 4
    half3 dither32 = ScreenSpaceDither(vertexPos, 32.0); // 5
    half3 dither64 = ScreenSpaceDither(vertexPos, 64.0); // 6
    half3 dither128 = ScreenSpaceDither(vertexPos, 128.0); // 7
    half3 dither256 = ScreenSpaceDither(vertexPos, 256.0); // 8
    half3 dither512 = ScreenSpaceDither(vertexPos, 512.0); // 9
    half3 dither1024 = ScreenSpaceDither(vertexPos, 1024.0); // 10
#ifdef SINGLE_CHANNEL_GBUFFER
	albedo = RGB_YCoCg(albedo); 
	albedo.gb += 0.5; // move CoCg to [0,1] range (can be simpified)
    albedo.x = sqrt(albedo.x); // do gamma compression, increases resolution of darker values
	albedo.rgb += dither16;

	normal.xy = float32x3_to_oct(normal) * 0.5 + 0.5;
	normal += dither256;

	//specColor = sqrt(specColor);
	//specColor += dither64;

	smoothness *= smoothness;
	smoothness += dither64;

	occlusion = sqrt(occlusion);
	occlusion += dither64;
	
    emission = saturate(emission);
    emission = RGB_YCoCg(emission);
    emission.gb += 0.5; // move CoCg to [0,1] range (can be simpified)
    emission.x = sqrt(emission.x); // do gamma compression, increases resolution of darker values
    emission.rgb += dither16;
    metallic += dither16;
    
    return
		ToUINT_8(isSampleA ? normal.x : normal.y) |
		ToUINT_4(albedo.x) << 8 |
		ToUINT_4(isSampleA ? albedo.y : albedo.z) << 12 |
		ToUINT_4(isSampleA ? occlusion : metallic) << 16 |
        ToUINT_4(emission.x) << 20 |
		ToUINT_4(isSampleA ? emission.y : emission.z) << 24 |
        ToUINT_4(smoothness.x) << 28;
#else
    uint2 gbuffer = 0;

    // World-Space Normals
    normal.xy = float32x3_to_oct(normal) * 0.5 + 0.5;
    normal.xy += dither1024;
    gbuffer.x |= ToUINT_10(normal.x) | ToUINT_10(normal.y) << 10;


     // Occlusion / Metallic
    occlusion = sqrt(occlusion);
    metallic *= metallic;
    metallic += dither64;
    gbuffer.x |= ToUINT_6(isSampleA ? occlusion : metallic) << 20;

    // Smoothness
    smoothness += dither64;
    gbuffer.x |= ToUINT_6(smoothness) << 26;

    // Albedo
    albedo = sqrt(albedo); // do gamma compression, reduces banding in darker areas
    albedo = RGB_YCoCg(albedo);
    albedo.gb += 0.5; // move CoCg to from [-0.5, 0.5] to [0,1] range (TODO: put this into YCoCg conversion)
    albedo += dither256;

    gbuffer.y |= ToUINT_8(albedo.x);
    gbuffer.y |= ToUINT_8(isSampleA ? albedo.y : albedo.z) << 8;
    
    // Emission
    emission = saturate(emission); //TODO: do FastTonemap to go beyond 1.0
    emission = sqrt(emission);
    emission = RGB_YCoCg(emission);
    
    emission.gb += 0.5; // move CoCg to [0,1] range (can be simpified)
    emission += dither256;

    gbuffer.y |= ToUINT_8(emission.x) << 16;
    gbuffer.y |= ToUINT_8(isSampleA ? emission.y : emission.z) << 24;

    return gbuffer;
#endif
}


//#define FROM_A(isSampleA, encoded)
void DecodeVisibilityBuffer(
	GBUFFER_FORMAT encodedValue,
	GBUFFER_FORMAT encodedValueB,
	bool isSampleA,
	out float3 albedo, 
	out float3 normal, 
	out float metallic, 
	out float smoothness, 
	out float occlusion,
    out float3 emission)
{
#ifdef SINGLE_CHANNEL_GBUFFER
    albedo.r = FromUINT_4(encodedValue >> 8);
     #ifdef  SUPERSAMPLE_MODE   
        albedo.r += FromUINT_4(encodedValueB >> 8);
        albedo /= 2.0;
    #endif

	albedo.g = FromUINT_4((isSampleA ? encodedValue : encodedValueB) >> 12);
	albedo.b = FromUINT_4((isSampleA ? encodedValueB : encodedValue) >> 12);
    albedo.x *= albedo.x;
    albedo.gb -= 0.5;//
   // +(0.125 / 16.0);
	albedo = YCoCg_RGB(albedo);


	normal.x = FromUINT_8((isSampleA ? encodedValue : encodedValueB));
	normal.y = FromUINT_8((isSampleA ? encodedValueB : encodedValue));
	normal = oct_to_float32x3(normal.xy * 2.0 - 1.0);

    smoothness = FromUINT_4(encodedValue >> 28);
	smoothness = sqrt(smoothness);
	occlusion = FromUINT_4((isSampleA ? encodedValue : encodedValueB) >> 16);
	occlusion *= occlusion;

    metallic = FromUINT_4((isSampleA ? encodedValueB : encodedValue) >> 16);;

    emission.r = FromUINT_4(encodedValue >> 20);
    emission.g = FromUINT_4((isSampleA ? encodedValue : encodedValueB) >> 24);
    emission.b = FromUINT_4((isSampleA ? encodedValueB : encodedValue) >> 24);
    emission.x *= emission.x;
    emission.gb -= 0.5; 

    emission = YCoCg_RGB(emission);
    emission *= step((2.0 / 16.0), emission.x);
#else

    normal.x = FromUINT_10(encodedValue.x);
    normal.y = FromUINT_10(encodedValue.x >> 10) ;
    normal = oct_to_float32x3(normal.xy * 2.0 - 1.0);

    albedo.r = FromUINT_8(encodedValue.y);
    albedo.g = FromUINT_8((isSampleA ? encodedValue.y : encodedValueB.y) >> 8);
    albedo.b = FromUINT_8((isSampleA ? encodedValueB.y : encodedValue.y) >> 8);
    albedo.gb -= 0.5;
    albedo = YCoCg_RGB(albedo);
    albedo *= albedo;

    metallic = FromUINT_6((isSampleA ? encodedValueB.x : encodedValue.x) >> 20);
    metallic = sqrt(metallic);
    occlusion = FromUINT_6((isSampleA ? encodedValue.x : encodedValueB.x) >> 20);
    occlusion *= occlusion;

    smoothness = FromUINT_6(encodedValue.x >> 26);

    emission.r = FromUINT_8(encodedValue.y >> 16);
    emission.g = FromUINT_8((isSampleA ? encodedValue.y : encodedValueB.y) >> 24);
    emission.b = FromUINT_8((isSampleA ? encodedValueB.y : encodedValue.y) >> 24);
    
    emission.gb -= 0.5;
    emission = YCoCg_RGB(emission);
    emission *= emission;
    #endif
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
