#version 450

// One quad covering the baked cold-mask window. Positions are written in world
// cell coordinates so the fragment stage gets a stable map-space position for
// noise sampling and for mapping into the mask texture.

layout(location = 0) in vec3 inPosition; // @vertex Position required

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec2 vWorld;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

void main()
{
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    gl_Position = uVertex.ViewProj * world;
    vWorld = world.xz;
}
