# SweetTexelShading
Texel-Shading with Unity's ScriptableRenderPipeline

# What is this?
Texel shading means that the shading happens on the objects texture before its actually rendered in 3D. 

# Why?
  VR and 4K demand high on fillrate and memory.
  Shader Aliasing is still a issue in current.

Texel shading could potentially solve both problems by allowing to render shading on a lower frame rate than the scene is rendered

# How?
Scene is rendered into the coverage buffer.
Information about what object is visible at which distance is collected.
Objects are rendered in texture space.
