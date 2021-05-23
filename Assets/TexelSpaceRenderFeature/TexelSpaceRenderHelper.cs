using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class TexelSpaceRenderHelper : MonoBehaviour
{

    MeshRenderer meshRenderer;
    public int objectID, previousID;
    static readonly int ObjectIDB = Shader.PropertyToID("_ObjectID_b");
    static readonly int PrevObjectIDB = Shader.PropertyToID("_prev_ObjectID_b");

    // Update is called once per frame
    void Update()
    {
        meshRenderer = GetComponent<MeshRenderer>();
    }

    private void OnWillRenderObject()
    {

        //Bounds bounds = GetComponent<MeshRenderer>().bounds;

        //Vector3 center_ScreenSpace = activeCam.WorldToScreenPoint(bounds.center);
        //Vector3 max_ScreenSpace = activeCam.WorldToScreenPoint(bounds.max);
        //diameter = Vector3.Distance(center_ScreenSpace, max_ScreenSpace) * 2f;

        //screenArea = projectSphere(getBoundingSphere(), activeCam.worldToCameraMatrix, BasicRenderpipeline.CURRENT_CAMERA_FOCAL_LENGTH);
        //if (!lockAtlasSize)
        //    atlasSize = Mathf.Clamp(Mathf.NextPowerOfTwo((int)(screenArea /2f)), BasicRenderpipeline.ATLAS_TILE_SIZE, 1024);

        if (TexelSpaceRenderFeature.instance != null) {
            TexelSpaceRenderFeature.instance.AddObject(this);
        }
        else {
            Debug.LogWarning($"No {nameof(TexelSpaceRenderFeature)} found.");
        }
    }

    public void SetAtlasProperties(int newObjectID)
    {
        MaterialPropertyBlock matProbBlock = new MaterialPropertyBlock();

        previousID = objectID;
        objectID = newObjectID;

        matProbBlock.SetInt(ObjectIDB,objectID);
        matProbBlock.SetInt(PrevObjectIDB, newObjectID);
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
