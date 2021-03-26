using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.PlayerLoop;
using UnityEngine.Rendering;

public class ClusteredLightning
{
    #region Configuration
    private const int MAX_LIGHTS_PER_VIEW = 2048, MAX_LIGTHS_PER_FROXEL = 1024;
    #endregion
    private ComputeShader clusteredLightning;
    public static Vector3[] debugFrustumPositions;
    public ClusteredLightning(ComputeShader clusteredLightning)
    {
        this.clusteredLightning = clusteredLightning;
    }
    struct LightInfo
    {
        public Vector3 origin;
        public float radius, angle;
        public Vector3 color;

        public LightInfo(Light light = null)
        {
            if (light == null)
            {
                origin = Vector3.zero;
                radius = 0f;
                angle = 0f;
                color = Vector3.zero;
            }
            else
            {
                if (light.type == LightType.Directional)
                {
                    // if it's a directional light, just treat it as a point light and place it very far away
                    origin = -light.transform.forward * 99999f;
                    radius = 99999999f;
                    angle = 1f;
                }
                else
                {
                    origin = light.transform.position;
                    radius = light.range;
                    angle = Mathf.Cos(light.spotAngle);
                }
                Vector4 colorTmp = light.color * light.intensity;
                color = colorTmp;
            }

        }
    }

    private Vector3Int froxelCount = Vector3Int.one * -1, froxelCountMinusOne;
    private RenderTexture g_FroxelToIndexOffset;
    private Texture3D  g_FrustumCorners;
    private int[] g_LightIndexBuffer_defaultData, g_LightIndexBuffer_readbackData;
    private ComputeBuffer g_LightIndexBuffer, g_LightIndexCounter, g_Lights;
    public void SetParameters(int x, int y, int z)
    {
        if (x == froxelCount.x && y == froxelCount.y && z == froxelCount.z)
        {
            return;
        }
        Dispose();

        g_FrustumCorners = new Texture3D(2, 2, 2,TextureFormat.RGBAFloat, false );
        g_FrustumCorners.filterMode = FilterMode.Bilinear;
        g_FrustumCorners.wrapMode = TextureWrapMode.Clamp;
        g_FrustumCorners.Apply();

        g_FroxelToIndexOffset = new RenderTexture(x, y, 0, RenderTextureFormat.RGInt);
        g_FroxelToIndexOffset.enableRandomWrite = true;
        g_FroxelToIndexOffset.dimension = TextureDimension.Tex3D;
        g_FroxelToIndexOffset.volumeDepth = z;
        g_FroxelToIndexOffset.Create();
        froxelCount = new Vector3Int(x,y,z);
        froxelCountMinusOne = new Vector3Int(
            Mathf.Max(x-1, 1),
            Mathf.Max(y - 1, 1),
            Mathf.Max(z - 1, 1));
        g_LightIndexBuffer = new ComputeBuffer(x * y * z * MAX_LIGTHS_PER_FROXEL, sizeof(int));
        g_LightIndexBuffer_defaultData = new int[g_LightIndexBuffer.count];
        g_LightIndexBuffer.SetData(g_LightIndexBuffer_defaultData);
        g_LightIndexCounter = new ComputeBuffer(1, sizeof(int));
        g_LightIndexBuffer_readbackData = new int[g_LightIndexCounter.count];
        g_Lights = new ComputeBuffer(MAX_LIGHTS_PER_VIEW, sizeof(float) * 8);
    }


    LightInfo[] g_LightsData = new LightInfo[MAX_LIGHTS_PER_VIEW];
    private Vector4 zLinearParams;
    private Matrix4x4 clipToWorldSpace;
    public void SetupClusteredLightning(
        ref CommandBuffer cmd, 
        CullingResults m_CullResults, 
        Camera currentCamera,
        float nearCluster)
    {
        var visibleLights = m_CullResults.visibleLights;
        for (int i = 0; i < MAX_LIGHTS_PER_VIEW; i++)
        {
            g_LightsData[i] = new LightInfo(i >= visibleLights.Length ? null : visibleLights[i].light);
        }
        g_Lights.SetData(g_LightsData);
        
        // non-compute globals
        cmd.SetGlobalBuffer("g_Lights", g_Lights);
        cmd.SetGlobalInt("g_LightsCount", Mathf.Min(MAX_LIGHTS_PER_VIEW, visibleLights.Length));

        
        //TODO: use actual rendertarget resolution
        var g_pixelPosToFroxelCoord = new Vector3(
            (1f / currentCamera.pixelWidth) * froxelCount.x,
            (1f / currentCamera.pixelHeight) * froxelCount.y,
            froxelCount.z);

        // shared values
        float zFar = currentCamera.farClipPlane, zNear = currentCamera.nearClipPlane;

        Matrix4x4 projGPU = GL.GetGPUProjectionMatrix(currentCamera.projectionMatrix, true);
        Matrix4x4 worldToClipSpace = (projGPU * currentCamera.worldToCameraMatrix);
        clipToWorldSpace = (worldToClipSpace).inverse;
        Matrix4x4 projInv = projGPU.inverse;
        zLinearParams =
            new Vector4(
                zFar - zNear,
                zNear,
                projInv.m33,
                projInv.m32
            );

        UpdateFrustumLookUpTexture();

        Vector4 g_FroxelDepthSpacingParams = new Vector4();
        float zNearDivzFar = (zNear * nearCluster) / zFar;
        g_FroxelDepthSpacingParams.x = (froxelCount.z -1f) / -Mathf.Log(zNearDivzFar, 2f);
        g_FroxelDepthSpacingParams.y = (float) froxelCount.z;

        // globals
        cmd.SetGlobalVector("g_pixelPosToFroxelCoord", g_pixelPosToFroxelCoord);
        Vector3 froxelCountFloat = froxelCount;
        cmd.SetGlobalVector("g_TotalFoxelsPerAxis", froxelCountFloat);
        cmd.SetGlobalBuffer("g_LightIndexBuffer", g_LightIndexBuffer);
        cmd.SetGlobalTexture("g_FroxelToIndexOffset", g_FroxelToIndexOffset);
        cmd.SetGlobalVector("g_FroxelDepthSpacingParams", g_FroxelDepthSpacingParams);
        // compute params
        int mainKernel = clusteredLightning.FindKernel("CSMain");
        cmd.SetComputeTextureParam(clusteredLightning, mainKernel, "g_FroxelToIndexOffset_rw", g_FroxelToIndexOffset);
        cmd.SetComputeBufferParam(clusteredLightning, mainKernel, "g_LightIndexBuffer_rw", g_LightIndexBuffer);
        g_LightIndexCounter.GetData(g_LightIndexBuffer_readbackData);
        Debug.Log("total items in all clusters: " + g_LightIndexBuffer_readbackData[0]);
        int totalClusters = froxelCount.x * froxelCount.y * froxelCount.z;
        Debug.Log("avg. item count: " + (g_LightIndexBuffer_readbackData[0]) / (float)totalClusters);
        g_LightIndexCounter.SetData(new int[]{0});
        cmd.SetComputeBufferParam(clusteredLightning, mainKernel, "g_LightIndexCounter", g_LightIndexCounter);
        cmd.SetComputeVectorParam(clusteredLightning, "g_cameraCenterWs", currentCamera.transform.position);


        cmd.SetComputeTextureParam(clusteredLightning, mainKernel, "_FrustumCorners", g_FrustumCorners);  

        cmd.SetComputeVectorParam(clusteredLightning, "_ZLinearParams", zLinearParams);
        cmd.SetComputeIntParam(clusteredLightning, "g_TotalFoxelsPerAxisX", froxelCount.x);
        cmd.SetComputeIntParam(clusteredLightning, "g_TotalFoxelsPerAxisY", froxelCount.y);
        cmd.SetComputeIntParam(clusteredLightning, "g_TotalFoxelsPerAxisZ", froxelCount.z);
        cmd.SetComputeIntParam(clusteredLightning, "g_LightsCount", Mathf.Min(MAX_LIGHTS_PER_VIEW, visibleLights.Length));
        cmd.SetComputeBufferParam(clusteredLightning, mainKernel, "g_Lights", g_Lights);
        cmd.SetComputeVectorParam(clusteredLightning, "g_FroxelDepthSpacingParams", g_FroxelDepthSpacingParams);
        cmd.SetComputeMatrixParam(clusteredLightning, "clipToWorldSpace", clipToWorldSpace);
        cmd.SetComputeMatrixParam(clusteredLightning, "worldToClipSpace", worldToClipSpace);
        {

            string s = "";
            Vector3 froxelsPerAxisInv = froxelCount;
            froxelsPerAxisInv.x = 1f / froxelCount.x;
            froxelsPerAxisInv.y = 1f / froxelCount.y;
            froxelsPerAxisInv.z = 1f / Mathf.Max(froxelCount.z - 1f, 1f);
            for (int j = 0; j < froxelCount.z; j++)
            {
                Vector3 p = new Vector3(froxelCount.x / 2f, froxelCount.y / 2f,j);
                Vector3[] screenSpace = new Vector3[8];
                // Near
                // Top left point
                screenSpace[0] = p;
                // Top right point
                screenSpace[1] = new Vector3(p.x + 1, p.y, p.z);
                // Bottom left point
                screenSpace[2] = new Vector3(p.x, p.y + 1, p.z);
                // Bottom right point
                screenSpace[3] = new Vector3(p.x + 1, p.y + 1, p.z);

                // Far
                Vector3 depthStep = new Vector3(0, 0, 1);
                // Top left point
                screenSpace[4] = screenSpace[0] + depthStep;
                // Top right point
                screenSpace[5] = screenSpace[1] + depthStep;
                // Bottom left point
                screenSpace[6] = screenSpace[2] + depthStep;
                // Bottom right point
                screenSpace[7] = screenSpace[3] + depthStep;

                Vector4[] worldSpace = new Vector4[8];

                //// Now convert the screen space points to view space
                //for (int i = 0; i < 8; i++)
                //{

                int i = 0;
                    screenSpace[i].Scale(froxelsPerAxisInv);
                //Debug.Log(screenSpace[i].z);
                    Vector3 screenSpaceFull = new Vector3(
                        0f, 
                        0f, 
                        ((screenSpace[i].z) * (zFar - zNear)) +zNear  );
                //screenSpaceFull.z = Mathf.Lerp(zNear, zFar, screenSpace[i].z);
                float clipZ = ((1.0f / screenSpaceFull.z) - projInv.m33) / projInv.m32;
                //float zBufferParamsYInv = 1f / _ZBufferParams.y;
                //screenSpaceFull.z = Mathf.Lerp(zBufferParamsYInv, 1f + zBufferParamsYInv, screenSpaceFull.z);

               // float clipZ = 1.0f / (projInv.m32 * screenSpaceFull.z + projInv.m33);
                ////float nonLinearDepth = (zFar + zNear - 2.0f * zNear * zFar / screenSpaceFull.z) / (zFar - zNear);
                screenSpaceFull.z = Mathf.Clamp01(clipZ);


                worldSpace[i] = clipToWorldSpace.MultiplyPoint(screenSpaceFull);
                //}

                s += worldSpace[0].z.ToString() + " from " + screenSpaceFull.z + "\n";
            }
            //Debug.Log(s);
            //Debug.Log(clipToWorldSpace);
            //Debug.Log(clipToWorldSpace.m32);
            //Debug.Log(clipToWorldSpace.m33);
            // Debug.Log(clipToWorldSpace.inverse.MultiplyPoint(new Vector3(0f, 0f, zNear)));
            //  Debug.Log(clipToWorldSpace.inverse.MultiplyPoint(new Vector3(0f, 0f, zFar)));
        }

        {
            if (visibleLights.Length > 0)
            {
                var vLight = visibleLights[0];
                Vector3 vLightPos = vLight.light.transform.position;
                Vector3 viewport = worldToClipSpace.MultiplyPoint(vLight.light.transform.position);
                viewport.z = 1.0f / (viewport.z * zLinearParams.w + zLinearParams.z); // compensate non-linear z-distribution 
                viewport.z = (viewport.z - zLinearParams.y) / zLinearParams.x; // [zNear, zFar] to [0,1]
                //viewport.z = 0.5f;
                viewport.z = (viewport.z * zLinearParams.x) + zLinearParams.y; // [0,1] to [zNear, zFar]
                //TODO: use rcp(), precompute rcp(_ZLinearParams.w)
                viewport.z = ((1.0f / viewport.z) - zLinearParams.z) / zLinearParams.w; // compensate non-linear z-distribution 

                Vector3 wordPos = clipToWorldSpace.MultiplyPoint(viewport);
             //   Debug.Log(Vector3.Distance(wordPos, vLight.light.transform.position));

            }
        }
      
        cmd.DispatchCompute(clusteredLightning, mainKernel, froxelCount.x, froxelCount.y, froxelCount.z);

    }

    public Vector3 ViewportToWorld(Vector3 viewportCoord)
    {
        //viewportCoord.z = pow(viewportCoord.z, 8.0);
        viewportCoord.z = (viewportCoord.z * zLinearParams.x) + zLinearParams.y; // [0,1] to [zNear, zFar]
        float clipZ = ((1.0f / viewportCoord.z) - zLinearParams.z) / zLinearParams.w; // compensate non-linear z-distribution 
        viewportCoord.z = clipZ;
        Vector3 worldSpace = clipToWorldSpace.MultiplyPoint(viewportCoord);
        return worldSpace;
    }
    public void UpdateFrustumLookUpTexture()
    {
        Vector3 cell_viewport = new Vector3(-1f, -1f, 0f);
        Vector3 cell_viewportNext = new Vector3(1f, 1f, 1f);
        Vector4 left_bottom_near = ViewportToWorld(new Vector3(cell_viewport.x, cell_viewport.y, cell_viewport.z));
        Vector4 left_bottom_far = ViewportToWorld(new Vector3(cell_viewport.x, cell_viewport.y, cell_viewportNext.z));
        Vector4 left_top_near = ViewportToWorld(new Vector3(cell_viewport.x, cell_viewportNext.y, cell_viewport.z));
        Vector4 left_top_far = ViewportToWorld(new Vector3(cell_viewport.x, cell_viewportNext.y, cell_viewportNext.z));

        Vector4 right_bottom_near = ViewportToWorld(new Vector3(cell_viewportNext.x, cell_viewport.y, cell_viewport.z));
        Vector4 right_bottom_far = ViewportToWorld(new Vector3(cell_viewportNext.x, cell_viewport.y, cell_viewportNext.z));
        Vector4 right_top_near = ViewportToWorld(new Vector3(cell_viewportNext.x, cell_viewportNext.y, cell_viewport.z));
        Vector4 right_top_far = ViewportToWorld(new Vector3(cell_viewportNext.x, cell_viewportNext.y, cell_viewportNext.z));

        Color[] frustumCorners = new Color[8];
        frustumCorners[0] = left_bottom_near;
        frustumCorners[1] = right_bottom_near;
        frustumCorners[2] = left_top_near;
        frustumCorners[3] = right_top_near;

        frustumCorners[4] = left_bottom_far;
        frustumCorners[5] = right_bottom_far;
        frustumCorners[6] = left_top_far;
        frustumCorners[7] = right_top_far;

        //frustumCorners[0] = new Vector4(0, 0, 0, 0);
        //frustumCorners[1] = new Vector4(1, 0, 0, 0);
        //frustumCorners[2] = new Vector4(0, 1, 0, 0); ;
        //frustumCorners[3] = new Vector4(1, 1, 0, 0); ;

        //frustumCorners[4] = new Vector4(0, 0, 1, 0); ;
        //frustumCorners[5] = new Vector4(1, 0, 1, 0); ;
        //frustumCorners[6] = new Vector4(0, 1, 1, 0); ;
        //frustumCorners[7] = new Vector4(1, 1, 1, 0); ;

        g_FrustumCorners.SetPixels(frustumCorners);
        g_FrustumCorners.Apply();
    }
    public void Dispose()
    {
        if (g_FroxelToIndexOffset != null)
        {
            g_FroxelToIndexOffset.Release();
            g_FroxelToIndexOffset = null;
        }

        if (g_LightIndexBuffer != null)
        {
            g_LightIndexBuffer.Dispose();
            g_LightIndexBuffer = null;
        }

        if (g_LightIndexCounter != null)
        {
            g_LightIndexCounter.Dispose();
            g_LightIndexCounter = null;
        }

        if (g_Lights != null)
        {
            g_Lights.Dispose();
            g_Lights = null;
        }

        if (g_FrustumCorners != null)
        {
            //TODO: memory leak?
        }
    }

}
