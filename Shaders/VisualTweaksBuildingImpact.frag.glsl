#version 450

// Instanced rendering needs the storage buffer declared in this stage too:
// Waylith builds the material descriptor set from the fragment source, so
// without this declaration binding 3 would not exist. The fragment itself only
// reads the interpolated varyings; this mirrors Mote.frag in vanilla shaders.
#ifdef INSTANCED
#include "InstanceTransforms.glsl"

layout(std430, set = 2, binding = 3) readonly buffer _ImpactParams
{
    vec4 impactParams[];
};
#endif

// Fully procedural building-impact debris and dust. No texture is sampled
// anywhere: chips are analytic rotating boxes, smoke is hash-based fbm noise.
// Triggered by Thing.TakeDamage when damage lands on a building (walls, doors,
// furniture, mineable rock...), tinted by the building's material colour.
//
// One quad per hit. Two phases share the burst life t (0..1):
//   Debris — the first _Params3.z fraction of t. N chips (N arrives per
//       instance) fan out toward -travelDir (back toward the shooter for
//       ranged hits, outward for explosions), ease-out launch plus a downward
//       screen drift that reads as gravity in top-down view. Each chip is a
//       small box SDF spinning around its own centre; a facet term darkens and
//       brightens it as it tumbles, which reads as 3D without any lighting.
//   Smoke — spans the whole t. A few fbm puffs expand from the impact point,
//       drift slightly up-screen (buoyancy) and erode away as the noise
//       threshold rises with age. A dense central dust kernel covers the first
//       moments so the impact reads before the puffs have grown.
//
// Per-burst data arrives through the vertex stage:
//   vParams.x   age 0..1 (whole burst, smoke included)
//   vParams.y   intensity
//   vParams.z   random seed
//   vParams.w   debris count (clamped to 1..24 here)
//   vParams2.x  travel direction x (world x, unit)
//   vParams2.y  travel direction z (world z, unit)
//   vParams2.z  backward bias share 0..1; 0 leaves the spray isotropic
//   vParams2.w  smoke amount (0 disables the smoke phase)
//   vParams3.rgb material colour (stuff tint of the struck building)
//   vParams3.w  face clip flag: 1 = the burst sits on a struck face, 0 = no
//               usable face (skips the wall constraint)
//   vParams4.xy outward normal of the struck face (world xz, unit)
//
// The wall is solid, so nothing may be drawn on the far side of the struck
// face: a chip passing over the wall would read as passing through it. Two
// guards enforce that:
//   - chip directions are constrained to the outside half-space
//     (dot(dir, normal) >= ~0.08), so the fan cannot aim into the wall;
//   - every fragment is clipped against the face plane, hard for chips
//     (they are solid and disappear cleanly at the surface) and soft for
//     smoke (dust clings and fades at the wall).
//
// Material constants (shared by every burst, set once on the material):
//   _Params3.x  gravity drift (local units over the debris phase)
//   _Params3.y  maximum debris travel (local units)
//   _Params3.z  debris life fraction (debris dies at this share of t)
//   _Params3.w  smoke rise (local units over the whole life)
//
// _Params / _Params2 / _Params4 / _Params5 are only read by the non-instanced
// small-count fallback path in the vertex shader; the declarations must stay
// in sync between stages.
//
// Output is standard alpha blending (the ShaderDef copies the mote queue but
// blends like matter, not light): chips composite OVER the smoke, rgb is
// un-premultiplied at the end, alpha the total coverage.

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vParams;
layout(location = 2) in vec4 vParams2;
layout(location = 3) in vec4 vParams3;
layout(location = 4) in vec4 vParams4;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Params;  // @default = vec4(0.0, 1.0, 0.0, 10.0)
    vec4 _Params2; // @default = vec4(0.0, 0.0, 0.0, 1.0)
    vec4 _Params3; // @default = vec4(0.30, 0.75, 0.40, 0.18)
    vec4 _Params4; // @default = vec4(0.55, 0.53, 0.50, 0.0)
    vec4 _Params5; // @default = vec4(0.0, -1.0, 0.0, 0.0)
} u;

float hash21(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float vnoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p)
{
    return 0.55 * vnoise(p)
        + 0.30 * vnoise(p * 2.1 + 7.3)
        + 0.15 * vnoise(p * 4.3 + 3.1);
}

// Standard "src over dst" on un-premultiplied colour + coverage.
void over(inout vec3 col, inout float a, vec3 srcCol, float srcA)
{
    col = srcCol * srcA + col * (1.0 - srcA);
    a = srcA + a * (1.0 - srcA);
}

void main()
{
    vec2 centered = vUv * 2.0 - 1.0;
    float r = length(centered);

    // Screen-space size of one pixel in local units, so chip edges stay crisp
    // at any zoom instead of aliasing when zoomed out.
    float pixelR = max(length(vec2(dFdx(r), dFdy(r))), 1e-6);

    float t = vParams.x;
    float intensity = vParams.y;
    float seed = vParams.z;
    int count = clamp(int(vParams.w + 0.5), 1, 24);

    float gravity = u._Params3.x;
    float maxTravel = u._Params3.y;
    float debrisFrac = max(u._Params3.z, 0.05);
    float smokeRise = u._Params3.w;
    float smoke = vParams2.w;

    vec3 matColor = vParams3.rgb;
    // Dust is the material colour pulled toward a neutral grey and lifted a
    // little: pulverised stone/wood reads lighter than the solid surface.
    vec3 dustColor = mix(matColor, vec3(0.66, 0.64, 0.60), 0.6) * 1.05 + 0.06;

    // Wall constraint. The quad centre sits exactly on the struck face, so the
    // signed distance to that plane is dot(centered, normal): positive outside.
    // Chips are cut hard at the surface (solid matter), smoke fades softly
    // (dust clings to the wall).
    vec2 faceNormal = vParams4.xy;
    bool hasFace = vParams3.w > 0.5;
    float side = dot(centered, faceNormal);
    // Chips are fully visible right at the surface and hard-cut a hair inside
    // it (their launch is constrained outward, so this only catches lateral
    // drift); smoke fades across a wider margin.
    float faceClipChip = hasFace ? smoothstep(-0.012, 0.0, side) : 1.0;
    float faceClipSmoke = hasFace ? smoothstep(-0.07, 0.12, side) : 1.0;

    vec3 col = vec3(0.0);
    float alpha = 0.0;

    // ---- Smoke phase (drawn first: chips composite on top of it) ----
    if (smoke > 0.001)
    {
        for (int i = 0; i < 4; i++)
        {
            float fi = float(i);
            vec2 hb = vec2(seed * 53.1 + fi * 9.7, seed * 29.7 + fi * 5.3);
            float h1 = hash21(hb + vec2(1.7, 3.1));
            float h2 = hash21(hb + vec2(8.3, 6.9));
            float h3 = hash21(hb + vec2(4.2, 2.8));
            float h4 = hash21(hb + vec2(7.5, 9.2));

            // Staggered start, every puff lives to the end of the burst.
            float start = 0.08 * h4;
            float us = clamp((t - start) / max(1.0 - start, 1e-4), 0.0, 1.0);
            if (us <= 0.0)
            {
                continue;
            }

            // Puffs born near the impact, leaning slightly into the spray;
            // buoyancy lifts them up-screen and a small hash wind adds drift.
            vec2 lean = -vParams2.xy * vParams2.z * 0.12 * h2;
            vec2 wind = vec2(h1 - 0.5, h3 - 0.5) * 0.14;
            vec2 puffPos = lean
                + vec2((h1 - 0.5) * 0.16, (h3 - 0.5) * 0.16)
                + (wind + vec2(0.0, smokeRise)) * us;

            float radius = mix(0.10, 0.42, 1.0 - (1.0 - us) * (1.0 - us))
                * mix(0.75, 1.25, h2);
            vec2 q = (centered - puffPos) / radius;
            float dist = length(q);
            if (dist > 1.15)
            {
                continue;
            }

            // Noise scrolls slowly upward as the puff rises; the erosion
            // threshold climbs with age so the puff breaks apart instead of
            // fading as a uniform disc.
            float n = fbm(q * 2.2 + seed * 17.0 + fi * 7.0 + vec2(0.0, -us * 0.7));
            float shape = 1.0 - smoothstep(0.45, 1.0, dist);
            float density = shape * smoothstep(0.32, 0.72, n + 0.28 * (1.0 - us));

            float env = smoothstep(0.0, 0.12, us) * (1.0 - smoothstep(0.55, 1.0, us));
            float a = density * env * 0.55 * smoke * intensity * faceClipSmoke;
            if (a <= 0.003)
            {
                continue;
            }

            // Young dust is dense and darker; it lightens as it thins.
            vec3 puffCol = dustColor * mix(0.72, 1.05, us);
            over(col, alpha, puffCol, clamp(a, 0.0, 1.0));
        }

        // Dense central dust kernel: brief, small, covers the instant of impact.
        float kernelSigma = 0.12 * (1.0 + t * 3.0);
        float kr = r / kernelSigma;
        float kernel = exp(-kr * kr)
            * (1.0 - smoothstep(0.0, 0.28, t))
            * 0.5 * smoke * intensity * faceClipSmoke;
        if (kernel > 0.003)
        {
            over(col, alpha, dustColor * 0.8, clamp(kernel, 0.0, 1.0));
        }
    }

    // ---- Debris phase ----
    float dt = t / debrisFrac;
    if (dt < 1.0)
    {
        for (int i = 0; i < count; i++)
        {
            float fi = float(i);
            vec2 hb = vec2(seed * 91.7 + fi * 12.9, seed * 47.3 + fi * 7.1);
            float h1 = hash21(hb + vec2(1.3, 7.7));
            float h2 = hash21(hb + vec2(9.1, 2.6));
            float h3 = hash21(hb + vec2(4.8, 6.2));
            float h4 = hash21(hb + vec2(8.4, 1.9));
            float h5 = hash21(hb + vec2(3.2, 9.4));
            float h6 = hash21(hb + vec2(6.6, 4.3));

            // Staggered launch and per-chip lifetime inside the debris phase.
            float start = 0.08 * h4;
            float end = mix(0.6, 1.0, h5);
            if (dt < start || dt >= end)
            {
                continue;
            }
            float uu = clamp((dt - start) / max(end - start, 1e-4), 0.0, 1.0);

            float angle = 6.2831853 * h1;
            vec2 radial = vec2(cos(angle), sin(angle));
            // Fan toward -travelDir; zero direction leaves it isotropic.
            float bias = vParams2.z * h2;
            vec2 dir = normalize(radial * (1.0 - bias) - vParams2.xy * bias);

            // Never aim into the wall: project any inward component out and
            // keep a small outward lean. The face clip below is the hard
            // backstop for lateral drift that still crosses the plane.
            if (hasFace)
            {
                float dn = dot(dir, faceNormal);
                dir = normalize(dir - faceNormal * min(dn - 0.08, 0.0));
            }

            float speed = mix(0.5, 1.0, h3) * maxTravel;
            float ease = 1.0 - (1.0 - uu) * (1.0 - uu);
            vec2 pos = dir * speed * ease;
            pos.y -= gravity * uu * uu;

            // Tumbling: the chip spins around its own centre while it flies.
            float spin = 6.2831853 * h6
                + uu * mix(5.0, 11.0, h1) * (h4 > 0.5 ? 1.0 : -1.0);
            vec2 rel = centered - pos;
            vec2 q = vec2(
                rel.x * cos(spin) - rel.y * sin(spin),
                rel.x * sin(spin) + rel.y * cos(spin));

            // Irregular box: per-chip aspect keeps the fragments from reading
            // as a cloud of identical squares. Chips shrink slightly as they
            // "fall away" from the camera at the end of their life.
            float halfSize = mix(0.03, 0.075, h2) * (1.0 - 0.3 * uu);
            vec2 halfVec = halfSize * vec2(1.0, mix(0.45, 1.0, h4));
            vec2 dq = abs(q) - halfVec;
            float sd = length(max(dq, 0.0)) + min(max(dq.x, dq.y), 0.0);
            float aa = pixelR * 1.2;
            float cov = 1.0 - smoothstep(-aa, aa, sd);
            if (cov <= 0.003)
            {
                continue;
            }

            // Facet shading: brightness swings as the chip tumbles, one face
            // catching light, the next in shadow. A dust coating pulls every
            // chip slightly toward the dust colour.
            float facet = 0.5 + 0.5 * sin(spin * 2.0 + h3 * 6.2831853);
            vec3 chipCol = mix(matColor * mix(0.45, 1.2, facet), dustColor, 0.22);

            float fade = smoothstep(0.0, 0.04, uu) * (1.0 - smoothstep(0.75, 1.0, uu));
            float a = cov * fade * clamp(intensity, 0.0, 1.0) * faceClipChip;
            over(col, alpha, chipCol, a);
        }
    }

    // Fade before the quad boundary so nothing is hard-clipped.
    float edge = 1.0 - smoothstep(0.88, 1.0, r);
    alpha *= edge;

    if (alpha <= 0.004)
    {
        discard;
    }

    outColor = vec4(col / alpha, clamp(alpha, 0.0, 1.0));
}