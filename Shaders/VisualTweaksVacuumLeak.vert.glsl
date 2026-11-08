#version 450

// One instanced quad per leak visual: a single strip running from the wet side
// of a ventilation port, through the opening, into the short plume it sheds on
// the dry side. Both halves share one uv field, so the stream has no seam.
//
// Instanced draws feed the per-instance parameters through the _LeakParams
// storage buffer: two vec4 per instance, written by VacuumLeakRenderer. The
// instance matrix carries the world transform (rotation aligns local +Z with
// the flow direction). The non-instanced variant exists only so the program
// also compiles without INSTANCED; it reads the material uniforms instead.
//
// The storage buffers are declared in the fragment shader as well even though
// it does not read them: Waylith builds the material descriptor set from the
// fragment source, so without those declarations the bindings would not exist.
// This mirrors Mote.vert / Mote.frag and the mod's own ExplosionRipple shaders.

#ifdef INSTANCED
#include "InstanceTransforms.glsl"

layout(std430, set = 2, binding = 3) readonly buffer _LeakParams
{
    vec4 leakParams[];
};
#endif

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vParams;  // strength, gapV, phase, narrow
layout(location = 2) out vec4 vParams2; // seed, lenCells, 0, 0

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

// Must match the fragment block exactly so both stages agree on the set layout.
// _Params2 / _Params3 are only read by the non-instanced fallback.
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Params;  // @default = vec4(1.0, 0.22, 0.0, 0.0)
    vec4 _Params2; // @default = vec4(1.0, 0.5556, 0.0, 0.3846)
    vec4 _Params3; // @default = vec4(0.0, 4.5, 0.0, 0.0)
} u;

void main()
{
#ifdef INSTANCED
    vec4 world = instanceTransforms[gl_InstanceIndex] * vec4(inPosition, 1.0);
    vParams = leakParams[gl_InstanceIndex * 2];
    vParams2 = leakParams[gl_InstanceIndex * 2 + 1];
#else
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    vParams = u._Params2;
    vParams2 = u._Params3;
#endif

    gl_Position = uVertex.ViewProj * world;
    vUv = inUv0.xy;
}