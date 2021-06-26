using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

[ExecuteInEditMode, DisallowMultipleComponent]
public class TexelSpaceRenderObject : MonoBehaviour
{
    MeshRenderer meshRenderer;
    [Header("Debug")] public int objectID;
    public int previousID;

    static readonly int ObjectIDB = Shader.PropertyToID("_ObjectID_b");
    static readonly int PrevObjectIDB = Shader.PropertyToID("_prev_ObjectID_b");
    
    MaterialPropertyBlock matProbBlock;
    [Header("Debug - CPU Heuristic")] public int DesiredShadingDensityExponent;
    public int TimeSliceIndex;
    [BitMaskProperty]
    public uint LayerMaskInMeshRenderer;
    public float MeshUVDistributionMetric;

    public void OnEnable()
    {
        matProbBlock = new MaterialPropertyBlock();
        Mesh mesh = GetComponent<MeshFilter>().sharedMesh;
        Debug.Assert(mesh);
        MeshUVDistributionMetric = mesh.GetUVDistributionMetric(1);

        // If the mesh has a transform scale or uvscale it would need to be applied here

        // Transform scale:
        // Use two scale values as we need an 'area' scale.
        // Use the two largest component to usa a more conservative selection and pick the higher resolution mip
        MeshUVDistributionMetric *= GetLargestAreaScale(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);

        // To determine uv scale for a material use Material.GetTextureScale
        // If there is a uv scale to apply then divide the m_MeshUVDistributionMetric by (uvScale.x * uvScale.y)
        meshRenderer = GetComponent<MeshRenderer>();
    }
    
    // Update is called once per frame
    void Update() {
        if (TexelSpaceRenderFeature.instance == null) {
            return;
        }
        
        if (matProbBlock == null) {
            matProbBlock = new MaterialPropertyBlock();
        }

    }

    void OnWillRenderObject() {

        if (TexelSpaceRenderFeature.instance != null) {
            TexelSpaceRenderFeature.instance.AddObject(this);
        }
        else {
            //TODO: only send this warning every x seconds to avoid Log spam with many objects
            Debug.LogWarning($"No {nameof(TexelSpaceRenderFeature)} found.");
        }

    }

    public bool IsVisible;
    void OnBecameVisible() {
        IsVisible = true;
    }

    void OnBecameInvisible() {
        IsVisible = false;
    }

    public float GetEstimatedMipMapLevel(Camera camera, int texelCount) {
        SetView(camera);
        return CalculateMipmapLevel(GetComponent<Renderer>().bounds, MeshUVDistributionMetric, texelCount);
    }
    
    public void SetAtlasProperties(int newObjectID, uint renderingLayerMask) {
        previousID = objectID;
        objectID = newObjectID;

        matProbBlock.SetInt(ObjectIDB,objectID);
        matProbBlock.SetInt(PrevObjectIDB, newObjectID);
        if (meshRenderer != null) {
            meshRenderer.SetPropertyBlock(matProbBlock);
            meshRenderer.renderingLayerMask = renderingLayerMask;
            LayerMaskInMeshRenderer = meshRenderer.renderingLayerMask;
        }
    }

    // =================================================================================================================
    private Vector3 m_CameraPosition;
    private float m_CameraEyeToScreenDistanceSquared;

    private float m_TexelCount;
    
    public void SetView(Camera camera) {
        float cameraHA = Mathf.Deg2Rad * camera.fieldOfView * 0.5f;
        float screenHH = (float)camera.pixelHeight * 0.5f;
        SetView(camera.transform.position, cameraHA, screenHH, camera.aspect);
    }

    public void SetView(Vector3 cameraPosition, float cameraHalfAngle, float screenHalfHeight, float aspectRatio) {
        m_CameraPosition = cameraPosition;
        m_CameraEyeToScreenDistanceSquared = Mathf.Pow(screenHalfHeight / Mathf.Tan(cameraHalfAngle), 2.0f);

        // Switch to using the horizontal dimension if larger
        if (aspectRatio > 1.0f) {
            // Width is larger than height
            m_CameraEyeToScreenDistanceSquared *= aspectRatio;
        }
    }

    
    private float CalculateMipmapLevel(Bounds bounds, float uvDistributionMetric, float texelCount) {
        // based on  http://web.cse.ohio-state.edu/~crawfis.3/cse781/Readings/MipMapLevels-Blog.html
        // screenArea = worldArea * (ScreenPixels/(D*tan(FOV)))^2
        // mip = 0.5 * log2 (uvArea / screenArea)
        float distanceToCameraSqr = bounds.SqrDistance(m_CameraPosition);
        if (distanceToCameraSqr < 1e-06)
            return -1;

        // uvDistributionMetric is the average of triangle area / uv area (a ratio from world space triangle area to normalised uv area)
        // - triangle area is in world space
        // - uv area is in normalised units (0->1 rather than 0->texture size)

        // m_CameraEyeToScreenDistanceSquared / dSq is the ratio of screen area to world space area

        float v = (texelCount * distanceToCameraSqr) / (uvDistributionMetric * m_CameraEyeToScreenDistanceSquared);
        float desiredMipLevel = 0.5f * Mathf.Log(v, 2);

        return desiredMipLevel;
    }

    // Pick the larger two scales of the 3 components and multiply together
    float GetLargestAreaScale(float x, float y, float z) {
        if (x > y) {
            return y > z ? x * y : x * z;
        }

        return x < z ? y * z : x * y;
    }

    public void SetAtlasScaleOffset(Vector4 scaleOffset) {
        Debug.Assert(scaleOffset != Vector4.zero);
        matProbBlock.SetVector("_VistaAtlasScaleOffset", scaleOffset);
        if (meshRenderer != null)
        {
            meshRenderer.SetPropertyBlock(matProbBlock);
        }
    }
}
