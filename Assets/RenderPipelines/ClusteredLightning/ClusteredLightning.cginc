#ifndef CLUSTERED_LIGHTNING
#include "UnityCG.cginc"
#include "UnityPBSLighting.cginc"

struct LightInfo
{
    float3 origin;
    float radius, angle;
    float3 color;
};
#define MAX_LIGHTS_PER_FROXEL 1024
#define MAX_LIGHTS_VIEW 2048
#define ZExponent 1.0
#define DO_CLUSTERED
int g_LightsCount;

Texture3D<uint2> g_FroxelToIndexOffset;
StructuredBuffer<uint> g_LightIndexBuffer;
StructuredBuffer<LightInfo> g_Lights;

Texture2D<half4> _BlueYellowRedGrad;
SamplerState sampler_BlueYellowRedGrad;
int3 g_TotalFoxelsPerAxis;
float3 g_pixelPosToFroxelCoord;
int g_TotalFoxelsPerAxisX, g_TotalFoxelsPerAxisY, g_TotalFoxelsPerAxisZ;
float2 g_FroxelDepthSpacingParams;

int ZCoordFromEyeDepth(float z)
{
    return floor(max(log2(z) * g_FroxelDepthSpacingParams.x + g_FroxelDepthSpacingParams.y, 0));
}

float3 ShadeBRDFLights(
    half3 albedo, 
    half3 normal, 
    half3 specular,
    half smoothness,
    half oneMinusReflectivity, 
    UnityGI gi,
    float3 worldViewDir, 
    float3 worldPos, 
    float4 pixelPos)
{
    gi.indirect.diffuse = 0;
    gi.indirect.specular = 0;
    float3 outColor = 0;
#ifdef DO_CLUSTERED
    // xy positions are in pixelspace. z is in projection space
    uint4 froxelCoord = uint4(floor(pixelPos.xyz * g_pixelPosToFroxelCoord.xyz), 0);
    froxelCoord.z = ZCoordFromEyeDepth(Linear01Depth(pixelPos.z));
    int2 index_offset = g_FroxelToIndexOffset.Load(froxelCoord);
    //uint s_lightIndex = WaveActiveMin(index_offset.x);
	[loop]
    for (int i = index_offset.x; i < index_offset.y; i++)
    {
        int lightIndex = g_LightIndexBuffer[i];
#else
    for (int i = 0; i < g_LightsCount; i++)
    {
        int lightIndex = i;
#endif
        
        float3 lightOrigin = g_Lights[lightIndex].origin;
        float lightRange = g_Lights[lightIndex].radius;

        float3 lightRay = lightOrigin - worldPos;
        float lightRayLengthSqr = dot(lightRay, lightRay);
        gi.light.color = g_Lights[lightIndex].color;
        float lightAttenLinear = 1.0 - saturate(sqrt(lightRayLengthSqr) / lightRange);
        float lightAtten = rcp(lightRayLengthSqr + 1.0) *  lightAttenLinear;
					
        if (lightAtten < 0.00001)
            continue;

        gi.light.dir = normalize(lightRay);

        outColor += BRDF1_Unity_PBS(
						albedo,
						specular,
						oneMinusReflectivity,
						smoothness,
						normal,
						worldViewDir,
						gi.light,
						gi.indirect) * lightAtten;
      
    }
   // return floor(froxelCoord.z) / g_pixelPosToFroxelCoord.z;
    //return (index_offset.z / 10.0) / 5.0;
    int froxelLightCount = float(index_offset.y - index_offset.x);
    float lightCount01 = float(froxelLightCount) / MAX_LIGHTS_PER_FROXEL;
    
    outColor += _BlueYellowRedGrad.Sample(sampler_BlueYellowRedGrad, float2(lightCount01, 0)) * (froxelLightCount > 0);
    //return Linear01Depth(pixelPos.z);
    //return froxelCoord.z / (g_pixelPosToFroxelCoord.z);
    return outColor;
    //outColor = float3(froxelCoord.xyz) / g_TotalFoxelsPerAxis;
  //  outColor.z /= g_pixelPosToFroxelCoord.z;
   // return floor(pixelPos.z * g_pixelPosToFroxelCoord.z) / g_pixelPosToFroxelCoord.z;;
    //return floor(index_offset.z) / g_pixelPosToFroxelCoord.z;
}

float3 distanceSqr(float3 a, float3 b)
{
    float3 ray = a - b;
    return dot(ray, ray);
}

struct Plane
{
    float3 N; // Plane normal.
    float d; // Distance to origin.
};

struct FrustumVertecies
{
    float3 left_bottom_near;
    float3 left_bottom_far;
    float3 left_top_near;
    float3 left_top_far;

    float3 right_bottom_near;
    float3 right_bottom_far;
    float3 right_top_near;
    float3 right_top_far;

};


#define frustumPlaneCount 6
struct Frustum
{
    Plane planes[frustumPlaneCount]; // left, right, top, bottom, front, back frustum planes.
};

// Compute a plane from 3 noncollinear points that form a triangle.
// This equation assumes a right-handed (counter-clockwise winding order) 
// coordinate system to determine the direction of the plane normal.
Plane ComputePlane(float3 p0, float3 p1, float3 p2)
{
    Plane plane;
 
    float3 v0 = p1 - p0;
    float3 v2 = p2 - p0;
 
    plane.N = normalize(cross(v0, v2));
 
    // Compute the distance to the origin using p0.
    plane.d = dot(plane.N, p0);
 
    return plane;
}

#define dot2(x) (dot(x,x))

float udQuad(float3 p, float3 a, float3 b, float3 c, float3 d)
{
    float3 ba = b - a;
    float3 pa = p - a;
    float3 cb = c - b;
    float3 pb = p - b;
    float3 dc = d - c;
    float3 pc = p - c;
    float3 ad = a - d;
    float3 pd = p - d;
    float3 nor = cross(ba, ad);

    return sqrt(
    (sign(dot(cross(ba, nor), pa)) +
     sign(dot(cross(cb, nor), pb)) +
     sign(dot(cross(dc, nor), pc)) +
     sign(dot(cross(ad, nor), pd)) < 3.0)
     ?
     min(min(min(
     dot2(ba * clamp(dot(ba, pa) / dot2(ba), 0.0, 1.0) - pa),
     dot2(cb * clamp(dot(cb, pb) / dot2(cb), 0.0, 1.0) - pb)),
     dot2(dc * clamp(dot(dc, pc) / dot2(dc), 0.0, 1.0) - pc)),
     dot2(ad * clamp(dot(ad, pd) / dot2(ad), 0.0, 1.0) - pd))
     :
     dot(nor, pa) * dot(nor, pa) / dot2(nor));
}

bool SphereDistianceToQuad(float4 sp, float3 a, float3 b, float3 c, float3 d)
{
    float dist = udQuad(sp.xyz, a, b, c, d);
    return dist < sp.w * 2.0;
}

  
 bool intersectRayWithSquare(float3 R1, float3 R2,
                                                 float3 S1, float3 S2, float3 S3)
{
        // 1.
    float3 dS21 = S2 - (S1);
    float3 dS31 = S3 - (S1);
    float3 n = cross(dS21, dS31);

        // 2.
    float3 dR = R1 - (R2);

    float ndotdR = dot(n,dR);

    if (abs(ndotdR) < 1e-6f)
    { // Choose your tolerance
        return false;
    }

    float t = -dot(n, R1 - (S1)) / ndotdR;
    float3 M = R1 + (dR * (t));

        // 3.
    float3 dMS1 = M - (S1);
    float u = dot(dMS1, dS21);
    float v = dot(dMS1, dS31);

        // 4.
    return (u >= 0.0f && u <= dot(dS21, dS21)
             && v >= 0.0f && v <= dot(dS31, dS31));
}

int rayTriangleIntersect(
const float3 orig, const float3 dir,
const float3 v0, const float3 v1, const float3 v2)
{

 
    float t, u, v;
    float3 v0v1 = v1 - v0;
    float3 v0v2 = v2 - v0;
    float3 pvec = cross(dir, v0v2);
    float det = dot(v0v1, pvec);

    // ray and triangle are parallel if det is close to 0
    if (abs(det) < 0.0001) return 0;

    float invDet = rcp(det);

    float3 tvec = orig - v0;
    u = dot(tvec, pvec) * invDet;
    if (u < 0 || u > 1) return 0;

    float3 qvec = cross(tvec, v0v1);
    v = dot(dir, qvec) * invDet;
    if (v < 0 || u + v > 1) return 0;

    t = dot(v0v2, qvec) * invDet;

    return 1;

}

inline float ExponetialDepthDistributionInv(float z)
{
    return exp2(((z * g_TotalFoxelsPerAxisZ) - g_FroxelDepthSpacingParams.y) / g_FroxelDepthSpacingParams.x);
}

float4x4 clipToWorldSpace, worldToClipSpace;
float4 _ZLinearParams;

float3x3 GetRotaionMatrix(float3 forward, float3 up)
{
    float3x3 tr = {                            
                    normalize(cross(up, forward)),  //x
                    up,                             //Y
                    forward};                       //Z

    return tr;
}
float3 ViewportToWorld(float3 viewportCoord, bool useRightEye)
{
    float eyeDepth = (viewportCoord.z * _ZLinearParams.x) + _ZLinearParams.y; // [0,1] to [zNear, zFar]

    //TODO: use rcp(), precompute rcp(_ZLinearParams.w)
    float clipZ = ((1.0f / eyeDepth) - _ZLinearParams.z) / _ZLinearParams.w; // compensate non-linear z-distribution 

    float4 worldSpace = mul(clipToWorldSpace, float4(viewportCoord.xy, clipZ, 1.0));
    worldSpace.xyz /= worldSpace.w;
    return worldSpace;
}

float3 WorldToViewport(float3 worldspaceCoord, bool useRightEye)
{
    float4 viewport = mul(worldToClipSpace, float4(worldspaceCoord, 1.0));
    viewport.xyz /= viewport.w;
 
   // viewport.z = 1.0 - viewport.z;
    //TODO: use rcp(), precompute rcp(_ZLinearParams.w)
    viewport.z = 1.0 / (viewport.z * _ZLinearParams.w + _ZLinearParams.z); // compensate non-linear z-distribution 
    viewport.z = (viewport.z - _ZLinearParams.y) / _ZLinearParams.x; // [zNear, zFar] to [0,1]

    return viewport;
}

// brute force containment test - for accurracy comparison
bool SphereInsideFrustumExact(float4 sphere, FrustumVertecies f, int3 p)
{
     // this code is shared per froxel, and gets optimized by the compiler
    float3 froxelsPerAxisInv = rcp(float3(g_TotalFoxelsPerAxisX, g_TotalFoxelsPerAxisY, g_TotalFoxelsPerAxisZ));

    // outline
    // get viewport coordinates of the cell
    float3 cell_viewport = p * froxelsPerAxisInv;
    float3 cell_viewportNext = (p + 1) * froxelsPerAxisInv;
    if (true /*g_flipYaxis == 1*/)
    {
		// also swap values to let the frustum plane normals be correct
        float tmp = 1.0 - cell_viewport.y;
        cell_viewport.y = 1.0 - cell_viewportNext.y;
        cell_viewportNext.y = tmp;
    }

    // get froxel viewspace bounds
    float3 AABBmin = cell_viewport;
    AABBmin.xy = AABBmin.xy * 2.0 - 1.0;
    AABBmin.z = ExponetialDepthDistributionInv(AABBmin.z);

    float3 AABBmax = cell_viewportNext;
    AABBmax.xy = AABBmax.xy * 2.0 - 1.0;
    AABBmax.z = ExponetialDepthDistributionInv(AABBmax.z);
    //=================

    // bring sphere in viewspace
    float3 sphereCenterViewspace = WorldToViewport(sphere.xyz, false);
    // clamp viewspace center to froxelbound
    float3 clampedSpherePosCS = clamp(sphereCenterViewspace.xyz, AABBmin, AABBmax);
    // bring clamped value in worldspace
    float3 clampedSpherePosWS = ViewportToWorld(clampedSpherePosCS, false);
    // check distance.
    return distance(clampedSpherePosWS, sphere.xyz) <= sphere.w ;
}

bool SphereScreenAABB(float4 sphere, FrustumVertecies f, int3 p) {
         // this code is shared per froxel, and gets optimized by the compiler
    float3 froxelsPerAxisInv = rcp(float3(g_TotalFoxelsPerAxisX, g_TotalFoxelsPerAxisY, g_TotalFoxelsPerAxisZ));

    // outline
    // get viewport coordinates of the cell
    float3 cell_viewport = p * froxelsPerAxisInv;
    float3 cell_viewportNext = (p + 1) * froxelsPerAxisInv;
    if (true /*g_flipYaxis == 1*/)
    {
		// also swap values to let the frustum plane normals be correct
        float tmp = 1.0 - cell_viewport.y;
        cell_viewport.y = 1.0 - cell_viewportNext.y;
        cell_viewportNext.y = tmp;
    }


    float3 AABBminWS = min(f.left_bottom_near, min(f.left_bottom_far, min(f.left_top_near, min(f.left_top_far, min(f.right_bottom_near, min(f.right_bottom_far, min(f.right_top_near, f.right_top_far)))))));
    float3 AABBmaxWS = max(f.left_bottom_near, max(f.left_bottom_far, max(f.left_top_near, max(f.left_top_far, max(f.right_bottom_near, max(f.right_bottom_far, max(f.right_top_near, f.right_top_far)))))));
    float3 AABBcenterWS = lerp(AABBminWS, AABBmaxWS, 0.5);
    float3 AABBextendsWS = AABBmaxWS - AABBminWS;
    float3 vDelta = max(0, abs(AABBcenterWS - sphere.xyz) -AABBextendsWS );
    float fDistSq = dot(vDelta, vDelta);
    return fDistSq <= sphere.w * sphere.w;
}

// src: http://www.iquilezles.org/www/articles/distfunctions/distfunctions.htm
float sdCappedCone( in float3 p, in float h, in float r1, in float r2 )
{
    float2 q = float2( length(p.xz), p.y );
    
    float2 k1 = float2(r2,h);
    float2 k2 = float2(r2-r1,2.0*h);
    float2 ca = float2(q.x-min(q.x,(q.y < 0.0)?r1:r2), abs(q.y)-h);
    float2 cb = q - k1 + k2* saturate(dot(k1-q,k2)/dot2(k2));
    float s = (cb.x < 0.0 && ca.y < 0.0) ? -1.0 : 1.0;
    return s*sqrt( min(dot2(ca),dot2(cb)) );
}

// src: http://www.iquilezles.org/www/articles/distfunctions/distfunctions.htm
float sdRoundCone( in float3 p, in float r1, float r2, float h )
{
    float2 q = float2( length(p.xz), p.y );
    
    float b = (r1-r2)/h;
    float a = sqrt(1.0-b*b);
    float k = dot(q,float2(-b,a));
    
    if( k < 0.0 ) return length(q) - r1;
    if( k > a*h ) return length(q-float2(0.0,h)) - r2;
        
    return dot(q, float2(a,b) ) - r1;
}
// src: http://www.iquilezles.org/www/articles/sphereocc/sphereocc.htm
int sphereVisibility( in float3 ca, in float ra, in float3 cb, float rb, in float3 c )
{
    float aa = dot(ca-c,ca-c);
    float bb = dot(cb-c,cb-c);
    float ab = dot(ca-c,cb-c);
    
    float s = ab*ab + ra*ra*bb + rb*rb*aa - aa*bb; 
    float t = 2.0*ab*ra*rb;

	     if( s + t < 0.0 ) return 1;
	else if( s - t < 0.0 ) return 2;
	                       return 3;
}
// Check to see if a sphere is fully behind (inside the negative halfspace of) a plane.
// Source: Real-time collision detection, Christer Ericson (2005)
bool SphereInsidePlane(float4 sphere, Plane plane)
{
    return step(dot(plane.N, sphere.xyz) - plane.d, -sphere.w);
    //return dot(plane.N, sphere.xyz) - plane.d < -sphere.w;
}

// Check to see of a light is partially contained within the frustum.
bool SphereInsideFrustum(float4 sphere, Frustum frustum, float zNear, float zFar)
{
    bool result = true;
 
    // First check depth
    // Note: Here, the view vector points in the -Z axis so the 
    // far depth value will be approaching -infinity.
    //if (sphere.xyz.z - sphere.r > zNear || sphere.xyz.z + sphere.r < zFar)
    //{
    //    result = false;
    //}
 
    // Then check frustum planes
    float inside = 0;
    [unroll]
    for (int i = 0; i < frustumPlaneCount; i++)
    {
        if (SphereInsidePlane(sphere, frustum.planes[i]))
        {
            return false;
        }
    }
 
    return true;
}

// Sphere intersection
float sphIntersect(in float3 ro, in float3 rd, in float4 sph)
{
    float3 oc = ro - sph.xyz;
    float b = dot(oc, rd);
    float c = dot(oc, oc) - sph.w * sph.w;
    float h = b * b - c;
    if (h < 0.0)
        return -1.0;
    return -b - sqrt(h); 
}

// original function by 
float sdCapsule(float3 p, float3 a, float3 b, float aRadius, float bRadius)
{
    float3 pa = p - a, ba = b - a;
    float h = saturate(dot(pa, ba) / (dot(ba, ba)));
    float radius = aRadius;// lerp(aRadius, bRadius, h);
    return length(pa - ba * h) - radius;
}

#define CLUSTERED_LIGHTNING
#endif
