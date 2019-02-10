using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class GGXLutGen : MonoBehaviour
{
    public Texture3D lut;
    public bool start = false;

    public float coordPow = 1f;
    public float roughnessPow = 1f;
    public Vector3Int lutSize = Vector3Int.one * 16;
    // Update is called once per frame
    void Update () {
	    if (start)
	    {
	        start = false;
	        Generate();
	    }
	}

    void Generate()
    {
        lut = new Texture3D(lutSize.x, lutSize.y, lutSize.z, TextureFormat.RHalf, true);
        lut.wrapMode = TextureWrapMode.Clamp;
        lut.filterMode = FilterMode.Trilinear;
        int mipmaps = Mathf.RoundToInt(Mathf.Log(lut.width, 2f)) ;
        //note: the last mipmap level is skiped (1x1)
        for (int i = 0; i < mipmaps; i++)
        {
            float roughness = i / (float) (mipmaps);

            int size = Mathf.RoundToInt(Mathf.Pow(2f, (mipmaps - i)));
            lut.SetPixels(GenerateBlock(Vector3Int.one * size,  roughness), i);
        }
        lut.Apply(false);
        Shader.SetGlobalTexture("_ggxLUT", lut);
        Shader.SetGlobalFloat("_ggxLUT_coordPow", 1f / coordPow );
        Shader.SetGlobalFloat("_ggxLUT_roughnessPow", 1f / roughnessPow);
    }

    Color[] GenerateBlock(Vector3Int size, float roughness)
    {
        Color[] block = new Color[size.x * size.y * size.z];
        roughness = Mathf.Max(Mathf.Pow(roughness, roughnessPow), 0.03f);
        for (int i = 0; i < block.Length; i++)
        {
            int coordX = i % size.x;
            int coordY = (i / size.x) % size.y;
            int coordZ = i / (size.x * size.y);

            float x = coordX / (float) (size.x - 1);
            float y = coordY / (float) (size.y - 1);
            float z = coordZ / (float) (size.z - 1);

            x = Mathf.Pow(x, 1);
            y = Mathf.Pow(y, 1);
            z = Mathf.Pow(z, coordPow);


            float visibility = SmithJointGGXVisibilityTerm(x, y, roughness );
            float ggx = GGXTerm(z, roughness);

            // this code was used to vertify the axis order
            // Vector3 direction = new Vector3(x * 2.0f - 1.0f,y * 2.0f - 1.0f, z * 2.0f - 1.0f).normalized;
            //block[i] = new Color(direction.x, direction.y, direction.z);
            float output = ggx * visibility;//GGXTerm(z, roughness);
            block[i] = Color.white * output ;

       
        }
        return block;
    }

    float SmithJointGGXVisibilityTerm(float NdotL, float NdotV, float roughness)
    {
        float a = roughness;
        float lambdaV = NdotL * (NdotV * (1 - a) + a);
        float lambdaL = NdotV * (NdotL * (1 - a) + a);

        return 0.5f / (lambdaV + lambdaL + 1e-5f);
    }

    float GGXTerm(float NdotH, float roughness)
    {
        float a2 = roughness * roughness;
        float d = (NdotH * a2 - NdotH) * NdotH + 1.0f; // 2 mad
        return (1.0f / Mathf.PI) * a2 / (d * d + 1e-7f); // This function is not intended to be running on Mobile,
        // therefore epsilon is smaller than what can be represented by float
    }
}
