using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TranslateOverTime : MonoBehaviour {
    public Vector3 direction;
	// Use this for initialization
	void Start () {
		
	}
	
	// Update is called once per frame
	void Update () {
        transform.position += direction * Time.deltaTime;
	}
}
