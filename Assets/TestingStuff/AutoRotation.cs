using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class AutoRotation : MonoBehaviour {

	// Use this for initialization
	void Start () {
		
	}

    public Vector3 rotation;

    public float Speed;
	// Update is called once per frame
	void Update ()
	{
	    Vector3 euler = rotation * Speed * Time.deltaTime;
	    transform.Rotate(euler);
	}
}
