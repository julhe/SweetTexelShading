#ifndef TEXEL_SHADING_INCLUDE

#define MAXIMAL_OBJECTS_PER_VIEW 512
#define MAX_PRIMITIVES_PER_OBJECT 16384
#define PRIMITIVE_CLUSTER_SIZE 1.0
#define COMPUTE_COVERAGE_TILE_SIZE 8
#define ATLAS_TILE_SIZE 1
#define ATLAS_OBJECT_SIZEEXPONENT_MIN 1 // 2^3 = 4 ATLAS_TILE_SIZE, the minmal space an triangle can be in the atlas
#define ATLAS_OBJECT_SIZEEXPONENT_MAX 8 // 2^6 = 16, the maximal size an triangle can be in the atlas
#define SINGLE_ROW_THREAD_SIZE 64
#define BLOCK_THREAD_SIZE 8
float g_AtlasSizeExponent, g_atlasAxisSize;
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
	
	uint index = objectID * MAX_PRIMITIVES_PER_OBJECT + primitiveID; //todo: use indiviall offset insteat of MAX_PRIMITIVES_PER_OBJECT
	baseIndex = index / 32;
	subIndex = index % 32;

	baseIndex = index;
}

struct ObjectToAtlasProperties
{
	uint objectID; // the original object ID, used to trace back the object after sorting
	uint sizeExponent; // the length of the texture inside the atlas
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

// I use the morton-code / z-shape for atlas packing
// this makes the packing very much straight forward and the complexity for insertion is always O(1).
// But this only works, as long as the textures sizes are power-of-two (256, 512, 1024,...) and the largest textures are getting inserted first
uint2 GetTilePosition(uint index)
{
	return uint2(DecodeMorton2X(index), DecodeMorton2Y(index));
}

float4 GetTextureRect(uint index, uint tilesPerAxis)
{
	float2 atlasPosition_tileSpace = GetTilePosition(index);
	float2 min = atlasPosition_tileSpace * ATLAS_TILE_SIZE;
	float2 max = min + tilesPerAxis * ATLAS_TILE_SIZE;

	return float4(min, max);
}

float4 GetUVToAtlasScaleOffset(float4 atlasPixelSpace)
{
	return float4(atlasPixelSpace.zw - atlasPixelSpace.xy, atlasPixelSpace.xy);
}

float4 GetUVToAtlasScaleOffset(uint startIndex, uint size)
{
    return GetUVToAtlasScaleOffset(GetTextureRect(startIndex, size)) / g_atlasAxisSize;
}

void MapTriangleTo01(inout float2 a, inout float2 b, inout float2 c)
{
    // map from [-1,1] to [0,1]
    a = mad(a, 0.5, 0.5);
    b = mad(b, 0.5, 0.5);
    c = mad(c, 0.5, 0.5);
}

void MapTriangleToFull(inout float2 a, inout float2 b, inout float2 c)
{
	// map from [0,1] to [-1,1]
    a = mad(a, 2.0, -1.0);
    b = mad(b, 2.0, -1.0);
    c = mad(c, 2.0, -1.0);
}

void MapTriangleToAtlas(inout float2 a, inout float2 b, inout float2 c, int index, int size)
{	

	//normalize triangle size to fill [0,1]
    //float2 minPos = min(a, min(b, c));
    //float2 maxPos = max(a, max(b, c));

    //float2 offset = -minPos;
    //float2 scale = rcp(maxPos + offset);

    //a = mad(a, scale, offset);
    //b = mad(b, scale, offset);
    //c = mad(c, scale, offset);

    a = 0.0;
    b = float2(0.0, 1.0);
    c = float2(1.0, 0.0);
    //obtain spacein
    //atlas

    float4 atlasST = GetUVToAtlasScaleOffset(index, pow(2, size));

    a = mad(a, atlasST.xy, atlasST.zw);
    b = mad(b, atlasST.xy, atlasST.zw);
    c = mad(c, atlasST.xy, atlasST.zw);
}

float4 debugColorBand(int value)
{
    return half4(sin(value), sin(value / 63.0), sin(value / 511.0), 0) * 0.5 + 0.5;
}
#define TEXEL_SHADING_INCLUDE
#endif
