# SweetTexelShading
Texel-Shading with Unity's ScriptableRenderPipeline

# What is this?
A custom rendering pipeline for Unity which implements texel shading.
Texel shading means that all sorts of lightning and effects are rendendered on the objects texture before the object is actually rendered in 3D. 

# Why?
 * VR and 4K demand high on fillrate and memory bandwitdth.
 * Shader Aliasing is still a issue in current Games.

Texel shading could potentially solve both problems by allowing to render shading on a lower frame rate than the scene is rendered.

# How?
 1. Scene is rendered with information about each objects unique ID, triangle ID and mipmap level per Pixel. 
 2. A texture atlas is generated from the information of the prevoius pass
 3. Texel shading
 4. Presenting the results
 
 All of this happens on the GPU so far! :)

