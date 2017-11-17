using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class TexelSpaceRenderHelper : MonoBehaviour
{

    public float screenArea;
    MeshRenderer meshRenderer;
    public int atlasSize = 16;
    public bool lockAtlasSize = false;
    public int objectID;
    public Vector4 atlasRect;
    ComputeBuffer objectID_b, prev_objectID_b;
    uint[] objectID_b_data = new uint[1];
    uint[] prev_objectID_b_data = new uint[1];
    // Update is called once per frame
    void Update()
    {

        meshRenderer = GetComponent<MeshRenderer>();
    }

    private void OnWillRenderObject()
    {

        Camera activeCam = BasicRenderpipeline.CURRENT_CAMERA;
        if (activeCam == null)
            return;

        if(objectID_b == null)
        {
            objectID_b = new ComputeBuffer(1, sizeof(int));
            prev_objectID_b = new ComputeBuffer(1, sizeof(int));
        }
        //Bounds bounds = GetComponent<MeshRenderer>().bounds;

        //Vector3 center_ScreenSpace = activeCam.WorldToScreenPoint(bounds.center);
        //Vector3 max_ScreenSpace = activeCam.WorldToScreenPoint(bounds.max);
        //diameter = Vector3.Distance(center_ScreenSpace, max_ScreenSpace) * 2f;

        //screenArea = projectSphere(getBoundingSphere(), activeCam.worldToCameraMatrix, BasicRenderpipeline.CURRENT_CAMERA_FOCAL_LENGTH);
        //if (!lockAtlasSize)
        //    atlasSize = Mathf.Clamp(Mathf.NextPowerOfTwo((int)(screenArea /2f)), BasicRenderpipeline.ATLAS_TILE_SIZE, 1024);

        if (BasicRenderpipeline.instance != null)
        {
            BasicRenderpipeline.instance.AddObject(this);
        }

    }

    public void SetAtlasProperties(Vector4 atlasPackingRect, int objectID)
    {
        MaterialPropertyBlock matProbBlock = new MaterialPropertyBlock();

        prev_objectID_b_data[0] = (uint) this.objectID;
        prev_objectID_b.SetData(prev_objectID_b_data);
        this.objectID = objectID;
        objectID_b_data[0] = (uint) objectID;
        objectID_b.SetData(objectID_b_data);

        this.atlasRect = atlasPackingRect;
        matProbBlock.SetVector("_AtlasScaleOffset", atlasPackingRect);
        matProbBlock.SetBuffer("_ObjectID_b", objectID_b);
        matProbBlock.SetBuffer("_prev_ObjectID_b", prev_objectID_b);
        if (meshRenderer != null)
        {
            meshRenderer.SetPropertyBlock(matProbBlock);
        }


    }

    Vector4 getBoundingSphere()
    {
        Bounds bounds = GetComponent<MeshRenderer>().bounds;
        float radius = Vector3.Distance(bounds.min, bounds.max) / 4f;
        Vector4 result = (bounds.center);
        result.w = radius;
        return result;
    }
    private void OnDrawGizmosSelected()
    {
       // Bounds bounds = GetComponent<MeshRenderer>().bounds;
        //Vector4 sphere = getBoundingSphere();
        //Gizmos.DrawWireSphere(sphere, sphere.w);
        //Gizmos.DrawWireCube(bounds.center, bounds.size);
    }

    // source: http://www.iquilezles.org/www/articles/sphereproj/sphereproj.htm
    static float projectSphere(Vector4 sph,  // sphere in world space
                     Matrix4x4 cam,  // camera matrix (world to camera)
                     float fl) // projection (focal length)
    {
        // transform to camera space
        Vector3 o = cam * sph;

        float r2 = sph.w * sph.w;
        float z2 = o.z * o.z;
        float l2 = Vector3.Dot(o, o);

        return -3.14159f * fl * fl * r2 * Mathf.Sqrt(Mathf.Abs((l2 - r2) / (r2 - z2))) / (r2 - z2);
    }
}

public class TexelSpaceRenderHelperComparer : IComparer<TexelSpaceRenderHelper>
{
    public int Compare(TexelSpaceRenderHelper lhs, TexelSpaceRenderHelper rhs)
    {
        return (int)(rhs.atlasSize - lhs.atlasSize);
    }
}