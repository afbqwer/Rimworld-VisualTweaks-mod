#version 450

// GPU ribbon expansion for the particle style: only the texture silhouette is
// sampled here. The per-particle tint (bucket colour multiplied by the life fade)
// arrives as a vertex colour, and the material colour carries global opacity.

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Params;  // (altitude, halfWidth, tailWidthFactor, headWidthFactor)
    vec4 _Params2; // (mapWidth, mapHeight, activeCount, unused)
    vec4 _Color;   // (1, 1, 1, opacityScale)
} ub;

layout(set = 2, binding = 1) uniform sampler2D _MainTex; // @default = white

// Declared but never read here: the material descriptor set is built from this
// stage, so without these the vertex-stage storage bindings would not exist.
layout(std430, set = 2, binding = 3) readonly buffer _AOParticles
{
    vec4 particleData[];
};

layout(std430, set = 2, binding = 4) readonly buffer _AOWindVX
{
    float windVX[];
};

layout(std430, set = 2, binding = 5) readonly buffer _AOWindVZ
{
    float windVZ[];
};

layout(std430, set = 2, binding = 6) readonly buffer _AOWindMask
{
    float windMask[];
};

void main()
{
    vec4 tex = texture(_MainTex, vUv);
    outColor = vec4(tex.rgb * vColor.rgb, tex.a * vColor.a);
}
