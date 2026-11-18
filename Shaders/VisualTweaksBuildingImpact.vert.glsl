#version 450

// One instanced quad per building-impact burst (debris chips + dust/smoke).
//
// Mirrors the spark layout: the instance matrix carries only the world
// transform, and the per-burst parameters ride in the _ImpactParams storage
// buffer (four vec4 per instance, written by BuildingImpactRenderer). The
// non-instanced variant is kept for the small-count path and reads the
// material uniforms instead.
//
// The storage buffer is declared in the fragment shader as well even though it
// does not read it: Waylith builds the material descriptor set from the
// fragment source, so without those declarations the bindings would not exist.
// This mirrors Mote.vert / Mote.frag in the vanilla shaders.

#ifdef INSTANCED
#include "InstanceTransforms.glsl"

layout(std430, set = 2, binding = 3) readonly buffer _ImpactParams
{
    vec4 impactParams[];
};
#endif

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vParams;  // t, intensity, seed, debris count
layout(location = 2) out vec4 vParams2; // travel dir x/z, bias, smoke amount
layout(location = 3) out vec4 vParams3; // material colour rgb, face clip flag
layout(location = 4) out vec4 vParams4; // struck face normal x/z, -, -

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

// Only used by the non-instanced fallback. The declaration must match the
// fragment block exactly so both stages agree on the material set layout.
// _Params3 is a material-wide constant in both paths; _Params4 / _Params5
// carry the third and fourth per-instance vec4 in the fallback path.
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Params;
    vec4 _Params2;
    vec4 _Params3;
    vec4 _Params4;
    vec4 _Params5;
} u;

void main()
{
#ifdef INSTANCED
    vec4 world = instanceTransforms[gl_InstanceIndex] * vec4(inPosition, 1.0);
    vParams = impactParams[gl_InstanceIndex * 4];
    vParams2 = impactParams[gl_InstanceIndex * 4 + 1];
    vParams3 = impactParams[gl_InstanceIndex * 4 + 2];
    vParams4 = impactParams[gl_InstanceIndex * 4 + 3];
#else
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    vParams = u._Params;
    vParams2 = u._Params2;
    vParams3 = u._Params4;
    vParams4 = u._Params5;
#endif

    gl_Position = uVertex.ViewProj * world;
    vUv = inUv0.xy;
}