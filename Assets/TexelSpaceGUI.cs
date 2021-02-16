using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TexelSpaceGUI : MonoBehaviour {
	public BasicRenderpipelineAsset asset;
	void OnGUI() {
		GUILayout.Label($"Frame time: {Time.smoothDeltaTime * 1000.0f}");
		
		GUILayout.Label($"Atlas Size Exponent (n²): {asset.maximalAtlasSizeExponent}");
		asset.maximalAtlasSizeExponent = (int) GUILayout.HorizontalSlider(asset.maximalAtlasSizeExponent, 8, 13);
		
		GUILayout.Label($"Atlas refresh Rate: {asset.atlasRefreshFps}");
		asset.atlasRefreshFps = (int) GUILayout.HorizontalSlider(asset.atlasRefreshFps, 1, 250);

		GUILayout.Label($"Atlas Resolution Scale: {asset.atlasResolutionScale}");
		asset.atlasResolutionScale =  GUILayout.HorizontalSlider(asset.atlasResolutionScale, 2000, 16000);

		GUILayout.Label($"Debug Mode: {asset.debugPass}");
		asset.debugPass = (TexelSpaceDebugMode) GUILayout.HorizontalSlider((int) asset.debugPass, 0, 4);
	}
}
