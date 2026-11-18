#version 450

// Instanced spark rendering needs the storage buffer declared in this stage
// too: Waylith builds the material descriptor set from the fragment source, so
// without this declaration binding 3 would not exist. The fragment itself only
// reads the interpolated vParams/vParams2 varyings; this mirrors Mote.frag in
// the vanilla shaders.
#ifdef INSTANCED
#include "InstanceTransforms.glsl"

layout(std430, set = 2, binding = 3) readonly buffer _SparkParams
{
    vec4 sparkParams[];
};
#endif

// Fully procedural bullet-impact sparks for mechanoids. No texture is sampled
// anywhere: every spark is an analytic streak built in this shader.
//
// One quad per impact, centred on the hit point. The fragment loop walks N
// sparks (N arrives per instance); each one derives its own direction, speed,
// start delay and lifetime from a hash of (burst seed, spark index), so the
// whole burst is deterministic per hit and costs no CPU particle state.
//
// Spark model, in local quad units (r == 1 at the quad edge):
//   - direction: radial, pulled toward the shot's backward direction by a
//     per-spark share of vParams2.z, so metal spray fans away from the shooter
//   - motion: ease-out travel (fast launch, decelerating) plus a small drift
//     toward the bottom of the screen that reads as gravity in top-down view
//   - shape: the distance to the segment [previous position, position] gives a
//     motion streak; the head is brighter than the tail and the width tapers
//     as the spark cools
//   - heat: white-hot at launch, orange mid-flight, deep red before death
// A short central flash covers the first ~0.2 of the burst so the impact itself
// reads even before the streaks have travelled.
//
// Per-burst data arrives through the vertex stage:
//   vParams.x   age 0..1
//   vParams.y   intensity
//   vParams.z   random seed
//   vParams.w   spark count (clamped to 1..32 here)
//   vParams2.x  bullet travel direction x (world x, unit)
//   vParams2.y  bullet travel direction z (world z, unit)
//   vParams2.z  backward bias share 0..1; 0 leaves the spray isotropic
//
// Material constants (shared by every burst, set once on the material):
//   _Params3.x  gravity drift (local units over the full life)
//   _Params3.y  trail length (fraction of the spark's own progress)
//   _Params3.z  flash sigma (local units)
//   _Params3.w  maximum spark travel (local units)
//
// _Params / _Params2 are only read by the non-instanced small-count fallback
// path in the vertex shader; the declarations must stay in sync between stages.
//
// Output is additive (the ShaderDef copies vanilla MoteGlow's queue and blend):
// rgb carries the weighted heat colour, alpha the total brightness.

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vParams;
layout(location = 2) in vec4 vParams2;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Params;  // @default = vec4(0.0, 1.0, 0.0, 16.0)
    vec4 _Params2; // @default = vec4(0.0, 0.0, 0.0, 0.0)
    vec4 _Params3; // @default = vec4(0.22, 0.16, 0.14, 0.8)
} u;

float hash21(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

// White-hot -> orange -> deep red over the spark's own life.
vec3 heatColor(float u)
{
    vec3 hot = vec3(1.0, 0.97, 0.82);
    vec3 mid = vec3(1.0, 0.55, 0.12);
    vec3 cool = vec3(0.82, 0.16, 0.02);
    return u < 0.5
        ? mix(hot, mid, u * 2.0)
        : mix(mid, cool, (u - 0.5) * 2.0);
}

void main()
{
    vec2 centered = vUv * 2.0 - 1.0;
    float r = length(centered);

    // Screen-space size of one pixel in local units, so streak width stays
    // crisp at any zoom instead of aliasing when zoomed out.
    float pixelR = max(length(vec2(dFdx(r), dFdy(r))), 1e-6);

    float t = vParams.x;
    float intensity = vParams.y;
    float seed = vParams.z;
    int count = clamp(int(vParams.w + 0.5), 1, 32);

    float gravity = u._Params3.x;
    float trail = max(u._Params3.y, 0.02);
    float maxTravel = u._Params3.w;

    vec3 acc = vec3(0.0);
    float total = 0.0;

    for (int i = 0; i < count; i++)
    {
        float fi = float(i);
        vec2 hashBase = vec2(seed * 91.7 + fi * 12.9, seed * 47.3 + fi * 7.1);
        float h1 = hash21(hashBase + vec2(1.3, 7.7));
        float h2 = hash21(hashBase + vec2(9.1, 2.6));
        float h3 = hash21(hashBase + vec2(4.8, 6.2));
        float h4 = hash21(hashBase + vec2(8.4, 1.9));
        float h5 = hash21(hashBase + vec2(3.2, 9.4));
        float h6 = hash21(hashBase + vec2(6.6, 4.3));

        // Staggered ignition and per-spark lifetime: the burst crackles rather
        // than switching on and off as one block.
        float start = 0.1 * h4;
        float end = mix(0.55, 1.0, h5);
        if (t < start || t >= end)
        {
            continue;
        }
        float u = clamp((t - start) / max(end - start, 1e-4), 0.0, 1.0);

        float angle = 6.2831853 * h1;
        vec2 radial = vec2(cos(angle), sin(angle));
        // Pull toward the backward shot direction; vParams2.xy is zero when no
        // direction was captured, which leaves the spray isotropic.
        float bias = vParams2.z * h2;
        vec2 dir = normalize(radial * (1.0 - bias) - vParams2.xy * bias);

        float speed = mix(0.45, 1.0, h3) * maxTravel;
        float ease = 1.0 - (1.0 - u) * (1.0 - u);
        vec2 pos = dir * speed * ease;
        pos.y -= gravity * u * u;

        float prevU = max(u - trail, 0.0);
        float prevEase = 1.0 - (1.0 - prevU) * (1.0 - prevU);
        vec2 prevPos = dir * speed * prevEase;
        prevPos.y -= gravity * prevU * prevU;

        // Distance to the motion segment = the streak. A degenerate segment
        // (spark just ignited) collapses to a point, which is the correct look.
        vec2 pa = centered - prevPos;
        vec2 ba = pos - prevPos;
        float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-8), 0.0, 1.0);
        float d = length(pa - ba * h);

        float sigma = max(pixelR * 1.3, 0.006) * mix(1.0, 0.55, u);
        float core = exp(-(d * d) / (sigma * sigma));

        float fade = smoothstep(0.0, 0.05, u) * pow(1.0 - u, 1.5);
        float b = core * mix(0.35, 1.0, h) * fade * mix(0.6, 1.0, h6) * intensity;

        acc += heatColor(u) * b;
        total += b;
    }

    // Central impact flash: bright, hot and brief, widening slightly as it
    // decays so it reads as a splash of light rather than a fixed dot.
    float flashSigma = max(u._Params3.z, 0.02) * (1.0 + t * 2.0);
    float fr = r / flashSigma;
    float flash = exp(-fr * fr)
        * (1.0 - smoothstep(0.0, 0.22, t))
        * 1.2 * intensity;
    acc += vec3(1.0, 0.92, 0.72) * flash;
    total += flash;

    // Fade before the quad boundary so a long spark is never hard-clipped.
    float edge = 1.0 - smoothstep(0.88, 1.0, r);
    acc *= edge;
    total *= edge;

    if (total <= 0.004)
    {
        discard;
    }

    outColor = vec4(acc / total, clamp(total, 0.0, 1.0));
}