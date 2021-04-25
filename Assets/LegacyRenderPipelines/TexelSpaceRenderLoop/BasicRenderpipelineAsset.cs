using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class BasicRenderpipelineAsset : RenderPipelineAsset
{
    private static readonly string m_PipelineFolder = "Assets/TexeSpaceRenderLoop";
    private static readonly string m_AssetName = "BasicRenderpipeline.asset";

    public ComputeShader resolveShader;
    public Shader resolveBlitShader;
    [Range(3, 14)]
    public int maximalAtlasSizeExponent;
    public int maximalAtlasSizePixel
    {
        get { return Mathf.NextPowerOfTwo(Mathf.RoundToInt(Mathf.Pow(2f, maximalAtlasSizeExponent))); }
    }

    public float atlasResolutionScale = 1024f;
    public float visibilityPassDownscale = 1f;
    public float atlasRefreshFps = 30;
    public int MSSALevel;
    public bool clearAtlasOnRefresh = false;
    public bool TexelSpaceBackfaceCulling = true;
    public TexelSpaceDebugMode debugPass = TexelSpaceDebugMode.None;

    public float memoryConsumption;
    public Texture2D[] dither;

    protected override RenderPipeline CreatePipeline() {
        dither = Resources.LoadAll<Texture2D>("Noise16pxLDR");
        return new BasicRenderpipeline(this);
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem("RenderPipeline/BasicRenderpipeline/Create Pipeline Asset", false, 15)]
    static void CreateLightweightPipeline()
    {
        var instance = ScriptableObject.CreateInstance<BasicRenderpipelineAsset>();

        string[] paths = m_PipelineFolder.Split('/');
        string currentPath = paths[0];
        for (int i = 1; i < paths.Length; ++i)
        {
            string folder = currentPath + "/" + paths[i];
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                UnityEditor.AssetDatabase.CreateFolder(currentPath, paths[i]);

            currentPath = folder;
        }

        UnityEditor.AssetDatabase.CreateAsset(instance, m_PipelineFolder + "/" + m_AssetName);
    }
#endif
}
