using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class SimpleForwardAsset : RenderPipelineAsset {
    private static readonly string m_PipelineFolder = "Assets/SimpleForward";
    private static readonly string m_AssetName = "SimpleForward.asset";
    protected override RenderPipeline CreatePipeline() {
        return new SimpleForward(this);
    }

    [Range(1, 32)]
    public int froxelsX = 5, froxelsY = 3, froxelsZ = 7;
    public float NearCluster = 1f;
    public ComputeShader clusteredLightning;
    public bool DepthPrepass = false;
#if UNITY_EDITOR
    [UnityEditor.MenuItem("RenderPipeline/SimpleForward/Create Pipeline Asset", false, 15)]
    static void CreateLightweightPipeline()
    {
        var instance = ScriptableObject.CreateInstance<SimpleForwardAsset>();

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
