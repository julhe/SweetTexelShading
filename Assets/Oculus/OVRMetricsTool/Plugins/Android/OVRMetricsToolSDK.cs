/************************************************************************************
Copyright : Copyright (c) Facebook Technologies, LLC and its affiliates. All rights reserved.

Licensed under the Oculus Utilities SDK License Version 1.31 (the "License"); you may not use
the Utilities SDK except in compliance with the License, which is provided at the time of installation
or download, or which otherwise accompanies this software in either electronic or hard copy form.

You may obtain a copy of the License at
https://developer.oculus.com/licenses/utilities-1.31

Unless required by applicable law or agreed to in writing, the Utilities SDK distributed
under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF
ANY KIND, either express or implied. See the License for the specific language governing
permissions and limitations under the License.
************************************************************************************/
#if UNITY_ANDROID && !UNITY_EDITOR
#define JNI_AVAILABLE
#endif

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class OVRMetricsToolSDK : MonoBehaviour
{
    private static AndroidJavaClass _MetricsService = null;
    private static AndroidJavaObject _Context = null;

    private static bool _IsBound = false;
    private static OVRMetricsToolSDK _Instance;

    public static OVRMetricsToolSDK Instance
    {
        get
        {
            if (_Instance == null)
            {
                var go = new GameObject("OVRMetricsToolSDK") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);

                InitJava();
                _Instance = go.AddComponent<OVRMetricsToolSDK>();
            }
            return _Instance;
        }
    }

    [System.Diagnostics.Conditional("JNI_AVAILABLE")]
    private static void InitJava()
    {
        if (_MetricsService == null)
        {
            AndroidJavaClass unityPlayerClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            _Context = unityPlayerClass.GetStatic<AndroidJavaObject>("currentActivity");

            _MetricsService = new AndroidJavaClass("com.oculus.metrics.MetricsService");
        }
    }

    private void Awake()
    {
        Bind();
    }

    private void OnDestroy()
    {
        Shutdown();
    }

    private void OnApplicationPause(bool pause)
    {
        // We need to shutdown on pause to force OVR Metrics Tool into an unbound state.
        if (pause)
        {
            Shutdown();
        }
        else
        {
            Bind();
        }
    }

    private void Bind()
    {
        if (_IsBound) {
            return;
        }
        if (_MetricsService != null)
        {
            _MetricsService.CallStatic("bind", _Context);
            _IsBound = true;
        }
    }

    private void Shutdown()
    {
        if (_MetricsService != null)
        {
            _MetricsService.CallStatic("shutdown", _Context);
            _IsBound = false;
        }
    }

    public bool AppendCsvDebugString(string debugString)
    {
        if (_MetricsService != null && _IsBound)
        {
            if (_MetricsService.CallStatic<bool>("appendCsvDebugString", _Context, debugString))
            {
                return true;
            }
            _IsBound = false;
        }
        return false;
    }

    public bool SetOverlayDebugString(string debugString)
    {
        if (_MetricsService != null && _IsBound)
        {
            if (_MetricsService.CallStatic<bool>("setOverlayDebugString", _Context, debugString))
            {
                return true;
            }
            _IsBound = false;
        }
        return false;
    }
}
