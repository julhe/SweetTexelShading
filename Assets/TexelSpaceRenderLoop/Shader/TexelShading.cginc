#ifndef TEXEL_SHADING_INCLUDE

#define MAXIMAL_OBJECTS_PER_VIEW 512
#define MAX_PRIMITIVES_PER_OBJECT 8192
#define PRIMITIVE_CLUSTER_SIZE 8.0
#define COMPUTE_COVERAGE_TILE_SIZE 8
#define ATLAS_TILE_SIZE 128
#define ATLAS_OBJECT_SIZEEXPONENT_MIN 7 // 7^12 = ATLAS_TILE_SIZE, the minmal space an object can be in the atlas
#define ATLAS_OBJECT_SIZEEXPONENT_MAX 12 // 2^12 = 4096, the maximal size an object can be in the atlas
#define SINGLE_ROW_THREAD_SIZE 64
#define BLOCK_THREAD_SIZE 8
float g_AtlasSizeExponent;
// --------common functions--------

// Current VisiblityBufferLayout with RInt
// r: 11bit objectID | 16bit primitiveID | 5bit mipmap Level
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

void GetVisiblityIDIndicies(uint objectID, uint primitiveID, out uint baseIndex, out uint subIndex)
{
	uint index = objectID * MAX_PRIMITIVES_PER_OBJECT + floor(primitiveID / (float)PRIMITIVE_CLUSTER_SIZE);
	baseIndex = floor(index / 32.0);
	subIndex = index % 32;

	//baseIndex = index;
}

struct ObjectToAtlasProperties
{
	uint objectID; // the original object ID, used to trace back the object after sorting
	uint sizeExponent; // the length of the texture inside the atlas
	float4 atlas_ST; // scale and offset to transform uv coords into atlas space
};

float max3(float x, float y, float z)
{
    return max(x, max(y, z));
}

sampler2D g_Dither;
float3 ScreenSpaceDither(float2 vScreenPos, float targetRange)
{
	//return 0;
#if 1
    float3 blueNoise = tex2D(g_Dither, vScreenPos / 32.0);
    blueNoise /= targetRange;
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

#define TONEMAP_VISTA
float3 SimpleTonemap(float3 c)
{
    #ifdef TONEMAP_VISTA
        return c * rcp(max3(c.r, c.g, c.b) + 1.0);
    #else
        return c;
    #endif
}
float3 SimpleTonemapInverse(float3 c)
{
    #ifdef TONEMAP_VISTA
        return c * rcp(1.0- max3(c.r, c.g, c.b));
    #else
        return c;
    #endif
}

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
