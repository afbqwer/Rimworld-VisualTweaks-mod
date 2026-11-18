#version 450

// One instanced quad per bullet impact on a mechanoid.
//
// Mirrors the shockwave layout: the instance matrix carries only the world
// transform, and the per-burst parameters ride in the _SparkParams storage
// buffer (two vec4 per instance, written by MechSparkRenderer). The
// non-instanced variant is kept for the small-count path and reads the
// material uniforms instead.
//
// The storage buffer is declared in the fragment shader as well even though it
// does not read it: Waylith builds the material descriptor set from the
// fragment source, so without those declarations the bindings would not exist.
// This mirrors Mote.vert / Mote.frag in the vanilla shaders.

#ifdef INSTANCED
#include "InstanceTransforms.glsl"

layout(std430, set = 2, binding = 3) readonly buffer _SparkParams
{
    vec4 sparkParams[];
};
#endif

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vParams;  // t, intensity, seed, spark count
layout(location = 2) out vec4 vParams2; // travel dir x/z, direction bias, unused

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

// Only used by the non-instanced fallback. The declaration must match the
// fragment block exactly so both stages agree on the material set layout.
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Params;
    vec4 _Params2;
    vec4 _Params3;
} u;

void main()
{
#ifdef INSTANCED
    vec4 world = instanceTransforms[gl_InstanceIndex] * vec4(inPosition, 1.0);
    vParams = sparkParams[gl_InstanceIndex * 2];
    vParams2 = sparkParams[gl_InstanceIndex * 2 + 1];
#else
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    vParams = u._Params;
    vParams2 = u._Params2;
#endif

    gl_Position = uVertex.ViewProj * world;
    vUv = inUv0.xy;
}