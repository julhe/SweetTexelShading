﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class CompactDeferredAsset : RenderPipelineAsset
{
    private static readonly string m_PipelineFolder = "Assets/CompactDeferred";
    private static readonly string m_AssetName = "CompactDeferred.asset";
    private static readonly string m_noisePath = "CompactDeferred.asset";
    [Range(1, 64)]
    public int froxelsX = 5, froxelsY = 3, froxelsZ = 7;

    public float NearCluster = 1f;
    public ComputeShader clusteredLightning;

    public Shader resolveBlitShader;
    public bool UncompressedGBuffer = false;
    [Range(0.5f, 2f)] public float RenderScale = 1f;

    public TexelSpaceDebugMode debugPass = TexelSpaceDebugMode.None;
    public float memoryConsumption;
    public Texture2D[] dither;
    public Texture2D BlueYellowRedGradient;

    protected override RenderPipeline CreatePipeline() {
        dither = Resources.LoadAll<Texture2D>("Noise16pxLDR");
        return new CompactDeferred(this);
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem("RenderPipeline/Compact Deferred/Create Pipeline Asset", false, 15)]
    static void CreateLightweightPipeline()
    {
        var instance = ScriptableObject.CreateInstance<CompactDeferredAsset>();

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
