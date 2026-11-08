#version 450

// Low-temperature room mist.
//
// One view-sized quad blends a floating vapour layer over cold *rooms*. The
// strength comes from a per-cell mask baked by ColdMistRenderer from vanilla
// room data (no AO involvement):
//   R = room temperature normalised over ColdTempNormMin..ColdTempNormMax
//       (-100 .. +40 C, so roughly 0.55 C per code)
//   G = unused
//   B = unused
//   A = 255 inside a room that does not use the outdoor temperature, 0 elsewhere
//
// The mask texture spans the whole map at one texel per cell, so the mapping is a
// plain scale with no window offset:
//   uv = vWorld * _Window.xy
//
// Alpha carries the indoor test. A cleared texel is (0,0,0,0), which would read as
// -100 C if it were trusted, so the shader multiplies the cold ramp by mask.a.
// The bilinear filter then feathers both the temperature and the indoor test, and
// the mist fades out over a cell or so at a wall instead of ending on a hard edge.
//
// Unlike the heat haze this shader never touches a scene grab: mist is a
// translucent haze blended over the scene, not a refraction of it. That also
// means both thresholds can move without a rebake, exactly like the heat mask.

layout(location = 0) in vec2 vWorld;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Params;  // @default = vec4(0.0, 0.1, 1.0, 2.0)               // time, opacity, noise scale, speed
    vec4 _Params2; // @default = vec4(0.8, 0.425, 0.1, 0.0)             // start temp, full temp, coverage, unused
    vec4 _Window;  // @default = vec4(0.00390625, 0.00390625, 0.0, 0.0) // 1/mapW, 1/mapH, unused
} u;

layout(set = 2, binding = 1) uniform sampler2D _ColdMask; // @default = black

// Hoskins hash: stable at map coordinates in the hundreds, unlike the naive
// fract(p * large) hashes that collapse into a grid (see the shader notes §5.2).
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

// Three octaves, each rotated and each carrying its own slowly revolving offset.
// The offsets are tiny on purpose and point in different directions, so the
// octaves churn against each other instead of the whole field translating as one
// layer. That is the difference between cold air moving and a cloud drifting
// past. Sum of amplitudes 0.875, rescaled to keep the 0..1 range and a mean near
// 0.5 (the coverage thresholds below are tuned against that mean).
float vapour(vec2 p, float t)
{
    const mat2 rotate = mat2(0.80, 0.60, -0.60, 0.80);

    float sum = 0.5 * gradientNoise(p + vec2(t * 0.032, t * -0.020));
    p = rotate * p * 2.03;
    sum += 0.25 * gradientNoise(p + vec2(t * -0.050, t * 0.038));
    p = rotate * p * 2.03;
    sum += 0.125 * gradientNoise(p + vec2(t * 0.075, t * 0.060));

    return sum * (1.0 / 0.875);
}

void main()
{
    vec2 uv = vWorld * u._Window.xy;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
    {
        discard;
    }

    vec4 mask = texture(_ColdMask, uv);

    // Descending ramp: mask.r is the raw normalised temperature, so "full" is
    // colder than "start" and therefore the lower edge of the smoothstep. Both
    // bounds arrive already normalised, so moving the sliders never forces a
    // rebake.
    float cold = 1.0 - smoothstep(u._Params2.y, u._Params2.x, mask.r);
    cold *= mask.a;
    if (cold <= 0.002)
    {
        discard;
    }

    // ---- animation ---------------------------------------------------------
    //
    // There is no global drift anywhere in this block. Translating one noise
    // field over time reads as a solid layer sliding past at any speed, because
    // the eye locks onto the individual blobs; the motion has to come from the
    // field evolving in place instead.

    float scale = max(u._Params.z, 0.05);
    float speed = u._Params.w;
    float t = u._Params.x * speed;

    // A slight diagonal grain, stretched 1.6:1. Isotropic noise reads as cotton
    // wool; a stretched field reads as a breath of cold air.
    const mat2 grain = mat2(0.906, 0.423, -0.423, 0.906); // 25 degrees
    vec2 cell = grain * vWorld;
    float freq = 0.70 * scale;
    vec2 p = cell * vec2(freq, freq * 0.62);

    // Domain warp: the edges curl instead of following the noise lattice, and
    // because the warp field itself keeps moving, the curls change shape rather
    // than travelling.
    float warp = gradientNoise(
        p * 0.45 + vec2(7.1, -3.3) + vec2(t * 0.017, t * -0.011)) - 0.5;
    p += vec2(warp * 1.9, warp * -1.3);

    float cloud = vapour(p, t);

    // Fine detail added to the threshold rather than to the alpha: this is what
    // gives the silhouette its ragged vapour edge. It is skewed by the warp so it
    // breaks up instead of forming the concentric rings that a ridged transform
    // (or a plain high-frequency layer) would produce.
    float detail = gradientNoise(
        p * 3.0 + vec2(warp * 2.7, -warp * 2.1) + vec2(t * 0.030, t * -0.045)) - 0.5;
    cloud += detail * 0.16;

    // Broad life field on a circular orbit: about one pass every four and a half
    // seconds at the default speed of 2, and never further than a quarter of its
    // own feature size, so banks of vapour thicken and dissolve where they sit
    // instead of scrolling by. This is the term that carries the "floating" read.
    vec2 orbit = vec2(cos(t * 0.70), sin(t * 0.70)) * 0.25;
    float life = gradientNoise(cell * (0.22 * freq) + orbit) - 0.5;
    cloud += life * 0.46;

    // A wide threshold band spanning almost the whole distribution, so density
    // varies continuously instead of snapping between "clear" and "solid" — that
    // snapping is what makes procedural fog look like cotton wool.
    //
    // Coverage slides the band across a deliberately large range: at 0 the lower
    // edge sits above the 96th percentile of the field, so the room is essentially
    // clear, and only at 1 does the band drop far enough to fill it. The old span
    // was narrow enough that even 0 fogged half the room, which made the slider
    // useless over its useful half.
    float lo = mix(0.78, 0.05, u._Params2.z);
    float hi = mix(1.02, 0.55, u._Params2.z);
    float shape = smoothstep(lo, hi, cloud);

    // A faint floor so a cold room is never completely bare; without it a small
    // room can land entirely inside a clear patch and read as a broken effect.
    shape = max(shape, 0.04);

    float alpha = shape * cold * u._Params.y;
    if (alpha <= 0.003)
    {
        discard;
    }

    // Thin edges stay near white so the haze reads as vapour; denser parts go a
    // little cooler and greyer, which keeps a thick bank from looking like a
    // light source.
    vec3 thin = vec3(0.84, 0.89, 0.97);
    vec3 thick = vec3(0.68, 0.77, 0.90);

    outColor = vec4(mix(thin, thick, shape), alpha);
}
