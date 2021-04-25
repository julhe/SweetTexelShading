using System.Collections;
using System.Collections.Generic;
using UnityEngine;
[ExecuteInEditMode]
public class PlaceRandomLights : MonoBehaviour
{

    public Bounds bounds;
    public int count = 32;
    public float minRange = 2f, maxRange = 4f;
    public bool start = false;
	// Update is called once per frame
	void Update () {
	    if (start)
	    {
	        start = false;

            foreach (Transform child in transform)
	        {
	            DestroyImmediate(child.gameObject);
	        }

           
	        for (int i = 0; i < count; i++)
	        {
	            Vector3 position;
	            position.x = Mathf.Lerp(bounds.min.x, bounds.max.x, Random.value);
	            position.y = Mathf.Lerp(bounds.min.y, bounds.max.y, Random.value);
	            position.z = Mathf.Lerp(bounds.min.z, bounds.max.z, Random.value);

	            Color color = Random.ColorHSV(0f, 1f, 0f, 1f, 0.5f, 1f);

                GameObject lightGO = new GameObject("light "+ i);
                lightGO.transform.SetParent(transform);
	            lightGO.transform.position = position;
	            var light = lightGO.AddComponent<Light>();
	            light.color = color;
	            light.range = Random.Range(minRange, maxRange);
	        }
	    }
	}

    private void OnDrawGizmosSelected()
    {
        Gizmos.DrawWireCube(bounds.center, bounds.size);
    }


}
