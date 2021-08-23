using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class AutoTexelSpaceRenderHelper : MonoBehaviour {
	public bool AutoUpdate;
	void Update() {
		if (!AutoUpdate) {
			return;
		}
		
		foreach (var meshRenderer in GetComponentsInChildren<MeshRenderer>()) {

			if (!meshRenderer.TryGetComponent(out TexelSpaceRenderObject texelSpaceRenderHelper)) {
				var renderHelper = meshRenderer.gameObject.AddComponent<TexelSpaceRenderObject>();
			}
		}
		

	}
}
