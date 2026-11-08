#version 450

// Instanced leak rendering needs the storage buffers declared in this stage
// too: Waylith builds the material descriptor set from the fragment source,
// so without these declarations binding 2 / 3 would not exist. The fragment
// itself only reads the interpolated varyings.
#ifdef INSTANCED
#include "InstanceTransforms.glsl"

layout(std430, set = 2, binding = 3) readonly buffer _LeakParams
{
    vec4 leakParams[];
};
#endif

// Vacuum leak airflow: a pale, cold stream of escaping air.
//
// Each instance is ONE strip from inside the room, through the opening, and out
// into space. Local +Z (UV v increasing) points along the flow, so the pattern
// scrolls one way through the whole stream. The opening sits at v = GapV; the
// signed distance from it decides which side a fragment is on. Because width,
// fade and noise are all continuous functions of that one coordinate, there is
// no seam where the room half meets the space half.
//
// Per-instance data arrives through the vertex stage:
//   vParams.x  strength (low-passed intensity, or the breach envelope)
//   vParams.y  gapV: uv v of the opening inside the strip
//   vParams.z  scroll phase (UV units of the wet length, integrated on the CPU
//              so speed changes never jump the pattern)
//   vParams.w  narrow: gap width / quad width, the mask width at the opening
//   vParams2.x random seed
//   vParams2.y strip length in cells (wet length + dry length), or the suction
//              disc diameter for a ceiling hole
//   vParams2.z mode: 0 = wall/port strip, 1 = ceiling suction disc
//
// Material constants (set once per frame on the shared material):
//   _Params.x  global intensity multiplier (settings slider)
//   _Params.y  peak alpha
//   _Params.z  refraction enabled (0 or 1)
//   _Params.w  refraction amplitude in pixels at full coverage
//
// The scene grab is declared in VisualTweaksShaders.xml and captured at the same
// queue, so it holds everything the stream may bend but never the stream. The
// output composites the refracted frame with the pale stream colour using the
// same weight an alpha blend would have used, and writes alpha 1: the displaced
// sample fades to the untouched frame at the silhouette, so no seam appears.
// Everything else is procedural: no textures, no asset dependencies.

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vParams;
layout(location = 2) in vec4 vParams2;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Params;  // @default = vec4(1.0, 0.22, 0.0, 0.0)
    vec4 _Params2; // @default = vec4(1.0, 0.5556, 0.0, 0.3846)
    vec4 _Params3; // @default = vec4(0.0, 4.5, 0.0, 0.0)
} u;

layout(set = 2, binding = 1) uniform sampler2D _GrabVisualTweaksVacuumLeak;

float hash21(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

vec2 noiseGradient(vec2 p)
{
    float angle = hash21(p) * 6.2831853;
    return vec2(cos(angle), sin(angle));
}

float gradientNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = p - i;
    vec2 u = f * f * (3.0 - 2.0 * f);

    float a = dot(noiseGradient(i), f);
    float b = dot(noiseGradient(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0));
    float c = dot(noiseGradient(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0));
    float d = dot(noiseGradient(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0));

    return clamp(0.5 + 0.85 * mix(mix(a, b, u.x), mix(c, d, u.x), u.y), 0.0, 1.0);
}

float fbm2(vec2 p)
{
    return 0.65 * gradientNoise(p)
        + 0.35 * gradientNoise(p * 2.03 + vec2(17.31, -9.73));
}

// Refracted scene behind the stream. The wobble rides the same noise as the
// streaks, so the bend travels with the flow; it fades with coverage and with
// the silhouette, which keeps the displaced sample equal to the frame at the
// edges. With refraction disabled the offset is zero and the result is the
// untouched frame, so the composite below is exactly the old alpha blend.
vec3 sampleRefracted(vec2 noiseCoord, float coverage, float strength)
{
    vec2 grabSize = max(vec2(textureSize(_GrabVisualTweaksVacuumLeak, 0)), vec2(1.0));
    vec2 grabUv = gl_FragCoord.xy / grabSize;
    vec2 wobble = vec2(
        fbm2(noiseCoord * 1.7 + vec2(3.1, 7.7)),
        fbm2(noiseCoord * 1.7 + vec2(9.3, 1.9))) - 0.5;
    float distortPx = u._Params.w * u._Params.z
        * clamp(u._Params.x * 0.5, 0.0, 1.0)
        * strength * coverage;
    vec2 offset = wobble * (2.0 * distortPx) / grabSize;
    return texture(
        _GrabVisualTweaksVacuumLeak,
        clamp(grabUv + offset, vec2(0.0), vec2(1.0))).rgb;
}

void main()
{
    float strength = vParams.x;
    float gapV = vParams.y;
    float phase = vParams.z;
    float narrow = vParams.w;
    float seed = vParams2.x;
    float lenCells = max(vParams2.y, 0.001);
    float mode = vParams2.z;

    // Ceiling hole: the roof is gone, so the air leaves upward and from the map
    // camera it reads as radial inflow. The quad is clipped to a disc; noise is
    // sampled in Cartesian coordinates and translated outward along the radius,
    // which makes the features stream inward without an angular seam at +/-pi.
    if (mode > 0.5)
    {
        vec2 centered = vUv - 0.5;
        float halfSize = max(lenCells * 0.5, 0.001);
        float rCells = length(centered) * lenCells;
        float rn = rCells / halfSize;          // 0 at the hole centre, 1 at the rim
        if (rn >= 1.0)
        {
            discard;
        }

        vec2 dir = rCells > 1e-4 ? centered / max(length(centered), 1e-5) : vec2(1.0, 0.0);
        float hole = clamp(narrow, 0.02, 0.95);

        // Nothing is drawn inside the opening (air is already gone) and the disc
        // feathers out before the quad edge; the room keeps a soft halo of inflow.
        float inner = smoothstep(hole * 0.75, min(hole + 0.18, 0.95), rn);
        float outer = 1.0 - smoothstep(0.6, 1.0, rn);

        // Sliding the sample point outward by phase scrolls the pattern inward.
        vec2 q = centered * (lenCells * 2.2)
            + dir * (phase * 1.8)
            + vec2(seed * 11.0, seed * 7.0);
        float streak = smoothstep(0.40, 0.75, fbm2(q));

        // A periodic petal term adds the inward spiral that pure noise lacks.
        // It uses an integer multiple of the angle, so it wraps without a seam.
        float ang = atan(centered.y, centered.x);
        float petal = 0.5 + 0.5 * sin(ang * 3.0 + rn * 5.0 + phase * 3.5 + seed * 6.2831853);

        float filaments = 0.30 + 0.70 * mix(streak, petal, 0.35);
        float gain = mix(1.35, 0.55, rn);
        float rim = exp(-((rn - hole) * (rn - hole)) / 0.0049);

        float coverage = inner * outer * gain * filaments + outer * rim * 0.45;
        float a = strength * u._Params.x * u._Params.y * coverage;

        if (a < 0.003)
        {
            discard;
        }

        vec3 scene = sampleRefracted(q, coverage, strength);
        outColor = vec4(
            mix(scene, vec3(0.78, 0.87, 0.96), clamp(a, 0.0, 1.0)),
            1.0);
        return;
    }

    // Signed distance from the opening along the flow, in cells, and the
    // normalised distance to the far end of whichever side we are on. Both are
    // continuous through the opening, which is what removes the old seam.
    float s = (vUv.y - gapV) * lenCells;
    float wetLen = max(gapV * lenCells, 0.001);
    float dryLen = max((1.0 - gapV) * lenCells, 0.001);
    float g = s < 0.0 ? -s / wetLen : s / dryLen;

    // Filaments: noise sampled fast across the flow and slow along it, so the
    // blobs stretch into streaks. The along coordinate is measured from the
    // opening, so the pattern runs continuously from the room, through the gap,
    // and out into space; scrolling it by phase carries the streaks downstream.
    // The per-port seed decorrelates neighbouring streams.
    float along = s / wetLen;
    vec2 np = vec2(
        vUv.x * 6.0 + seed * 17.0,
        (along - phase) * 1.6 + seed * 7.0);
    float n = fbm2(np);
    float streak = smoothstep(0.42, 0.78, n);

    // Cross-section half-width in UV space, as a fraction of the quad: one
    // waist at the opening, then a flare into the room and a wider bloom out
    // into vacuum.
    float waist = 0.5 * narrow;
    float maxHalf = s < 0.0 ? max(waist, 0.31) : max(waist, 0.32);
    float halfWidth = mix(waist, maxHalf, smoothstep(0.0, 0.85, g));

    // The falloff is measured in units of the local half width, not as a fixed
    // slab of UV. A fixed slab (the old 0.07) is only ~0.26 cells wide, so at a
    // zoomed-out camera the silhouette was a four-pixel step: a bright band with
    // a knife edge. Proportional to the width, the opening and the flare fade
    // over the same fraction of their own silhouette.
    //
    // 50% alpha lands exactly on t = 1, so widening the ramp never widens the
    // opening: the stream still passes through the hole at the hole's own width
    // and only its toes reach further out.
    //
    // outer is capped so the mask still reaches zero before the mesh edge at
    // 0.5 — a band that runs past it slices the strip with a hard straight cut,
    // which is the artifact this change exists to remove.
    float across = abs(vUv.x - 0.5);
    float t = across / max(halfWidth, 1e-4);
    // The silhouette wavers on the same noise that drives the streaks, so the
    // boundary is a turbulent fringe rather than a mathematically clean curve.
    t += (n - 0.5) * 0.30;
    float outer = clamp(0.5 / max(halfWidth, 1e-4) - 0.18, 1.16, 1.65);
    float edge = 1.0 - smoothstep(2.0 - outer, outer, t);

    // Dissolve at both far ends; the opening in the middle stays open because
    // the flow continues through it.
    float endFade = 1.0 - smoothstep(0.62, 1.0, g);

    // The plume past the opening thins with distance, but starts at exactly the
    // wet side's gain: brightness cannot step at the opening.
    float sideGain = s < 0.0 ? 1.0 : 1.0 / (1.0 + 1.1 * g);

    // A faint smooth cone under the brighter filaments: the stream reads as
    // moving air even between streaks, not as a handful of dots. The same
    // coverage also gates the refraction.
    float coverage = edge * endFade * sideGain * (0.3 + 0.7 * streak);
    float a = strength * u._Params.x * u._Params.y * coverage;

    if (a < 0.003)
    {
        discard;
    }

    // Composite over the refracted frame with the weight the old alpha blend
    // used: the pale colour still reads as the stream, the scene behind it
    // bends. Alpha is 1 because the frame itself is already in the sample.
    vec3 scene = sampleRefracted(np, coverage, strength);
    outColor = vec4(
        mix(scene, vec3(0.78, 0.87, 0.96), clamp(a, 0.0, 1.0)),
        1.0);
}