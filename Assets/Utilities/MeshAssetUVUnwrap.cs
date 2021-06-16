using System;
using System.Collections;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
/// <summary>
/// Hack for Snappy assets that are not shiped with Lightmap UVs...
/// </summary>
[ExecuteInEditMode]
public class MeshAssetUVUnwrap : MonoBehaviour
{
#if UNITY_EDITOR
	public Mesh[] Meshes;

	public bool Run;
	public void Update() {
		if (Run) {
			Run = false;

			foreach (var meshFilter in GetComponentsInChildren<MeshFilter>()) {
				var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GetAssetPath(meshFilter.sharedMesh));
				
				Unwrapping.GenerateSecondaryUVSet(mesh);
			}
			AssetDatabase.SaveAssets();
		}
	}
#endif
}
