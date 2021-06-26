using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;


public class SwitchShader : MonoBehaviour {
    public Shader A, B;

    public bool current;
    public Material[] Materials;

    public UnityEvent OnA, OnB;
    public void SwapMaterials() {
        current = !current;
        SetMaterials(current);
        if (current) {
            OnA.Invoke();
        }
        else {
            OnB.Invoke();
        }

    }
    public void SetMaterials(bool useA) {
        foreach (Material material in Materials) {
            material.shader = useA ? A : B;
        }
    }
    
    
}
