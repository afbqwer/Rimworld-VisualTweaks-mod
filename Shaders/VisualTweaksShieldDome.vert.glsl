#version 450

// One quad per projectile-interceptor shield, drawn from the comp's own PostDraw
// replacement at the shield's position and altitude.
//
// Only world xz leaves this stage. The fragment shader measures the ring against
// the shield's *radius* uniform instead of against the quad's 0..1 UVs, so nothing
// here depends on how plane10 happens to orient its texture coordinates — which is
// one whole class of bug (a mirrored interception wedge) removed rather than
// guarded against.

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
