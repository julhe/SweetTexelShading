#ifndef TEXEL_SHADING_INCLUDE
#define PRIMITIVE_VISIBLITY_ARRAY_SIZE 2048
#define ATLAS_TILE_SIZE 128


// --------common functions--------

// Current VisiblityBufferLayout with RInt
// r: 11bit objectID | 16bit primitiveID | 5 mipmap Level
uint EncodeVisibilityBuffer(uint objectID, uint primitiveID, uint mipmapLevel)
{
    return objectID | primitiveID << 11 | mipmapLevel << 27;
}

uint3 DecodeVisibilityBuffer(uint encodedValue)
{
	return uint3(
		encodedValue & 0x7FF, //objectID
		(encodedValue >> 11) & 0xFFFF, //primitiveID
		(encodedValue >> 27) & 0x1F); //mipmapLevel

}

//TODO: untested
uint GetPrimitiveVisiblity(uint primitiveID, uint visibilityArray[PRIMITIVE_VISIBLITY_ARRAY_SIZE])
{
	uint bitIndex = primitiveID % 32;
	uint arrayIndex = floor(primitiveID / 32);
	return (visibilityArray[arrayIndex] >> bitIndex) & 0x1;
}

struct TexelSpaceObject3
{
	uint primitiveVisibility[PRIMITIVE_VISIBLITY_ARRAY_SIZE];
	uint mipmapLevel;

};

//--------unitility--------

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
#define TEXEL_SHADING_INCLUDE
#endif
