#version 450

// Volumetric-ish gas shading for a top-down map.
//
// The per-cell scalar fields live in two textures (each cross-faded between the
// previous and current simulation publish so the 10 Hz solver cadence does not
// read as stepping):
//   _GasTex*      : R blind smoke, G tox gas, B rot stink, A deadlife dust (0..1)
//   _MistTex*     : R mist density, G open-cell mask
//   _GasTexWide*  : the gas field box-averaged over 8x8 cells, used only where the
//                   averaged value is already dense (see main())
//   _WindTex      : local flow history over the whole map: rg = accumulated
//                   displacement (cells), ba = realized velocity (cells/s). Only the
//                   local-flow noise mode samples it.
//
// What makes it look like fog instead of stacked sprites:
//   * alpha is an optical depth (1 - exp(-density)) rather than a linear opacity;
//   * the silhouette comes from animated gradient noise, not from one repeated sprite;
//   * a low-frequency patch mask breaks up uniform coverage;
//   * boundaries feather through noise instead of ending on cell edges.

layout(location = 0) in vec2 vWorld;
layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _SmokeColor; // @default = vec4(0.784, 0.784, 0.784, 1.0)  // vanilla 200,200,200
    vec4 _ToxColor;   // @default = vec4(0.706, 0.839, 0.094, 1.0)  // vanilla 180,214,24
    vec4 _RotColor;   // @default = vec4(0.839, 0.353, 0.094, 1.0)  // vanilla 214,90,24
    vec4 _DustColor;  // @default = vec4(0.02, 0.08, 0.32, 1.0)    // vanilla 0.01,0.05,0.2, lifted
    vec4 _MistColor;  // @default = vec4(0.87, 0.91, 0.96, 1.0)
    vec4 _Params;     // @default = vec4(1.0, 2.6, 1.0, 0.0)  // fade t, optical gain, opacity, shader clock (unused here)
    vec4 _Params2;    // @default = vec4(1.0, 1.0, 0.0, 0.0)  // texture w, texture h, ambient drift velocity x/y (noise units per second)
    vec4 _Params3;    // @default = vec4(1.1, 0.35, 1.0, 0.0) // noise scale, warp, detail, unused
    vec4 _Params4;    // @default = vec4(0.5, 1.0, 0.0, 0.5)   // blur, faint boost, cloudy mode, sharpen
    vec4 _Params6;    // @default = vec4(1.0, 1.0, 0.0, 0.0)   // vWorld -> uv, current texture
    vec4 _Params7;    // @default = vec4(1.0, 1.0, 0.0, 0.0)   // vWorld -> uv, previous gas
    vec4 _Params8;    // @default = vec4(1.0, 1.0, 0.0, 0.0)   // vWorld -> uv, previous mist
    vec4 _Params9;    // @default = vec4(1.0, 1.0, 0.0, 1.0)   // noise mode, animation, seconds since noise bake, draw deadlife dust
    vec4 _Params10;   // @default = vec4(1.0, 1.0, 0.0, 0.0)   // vWorld -> uv, flow grid
    vec4 _Params11;   // @default = vec4(0.0, 0.0, 0.0, 0.0)   // ambient drift: xy = accumulated displacement (noise units)
} u;

layout(set = 2, binding = 1) uniform sampler2D _GasTexCur;   // @default = black
layout(set = 2, binding = 2) uniform sampler2D _GasTexPrev;  // @default = black
layout(set = 2, binding = 3) uniform sampler2D _MistTexCur;  // @default = black
layout(set = 2, binding = 4) uniform sampler2D _MistTexPrev; // @default = black
layout(set = 2, binding = 5) uniform sampler2D _GasTexWideCur;  // @default = black
layout(set = 2, binding = 6) uniform sampler2D _GasTexWidePrev; // @default = black
// RGBA32F: rg = accumulated displacement in cells, ba = smoothed velocity in cells/s.
layout(set = 2, binding = 7) uniform sampler2D _WindTex;       // @default = black

// Hoskins hash: stays stable at map coordinates (hundreds of cells), unlike the
// naive fract(p * large) hashes that collapse into a grid (see the shader notes §5.2).
float hash21(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

// Gradient (Perlin) noise. Value noise interpolates the four lattice corner *values*,
// so its level sets run into straight axis-aligned segments: over a large uniform gas
// area that reads as rectangular tiles stitched together. Gradient noise interpolates
// corner *slopes*, which keeps the contours round.
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

    // Gradient noise has a narrower value histogram than value noise, so the gain is
    // raised to keep the cloud contrast the previous look had.
    return clamp(0.5 + 0.85 * mix(mix(a, b, u.x), mix(c, d, u.x), u.y), 0.0, 1.0);
}

float fbm(vec2 p, int octaves)
{
    float sum = 0.0;
    float amp = 0.5;
    float norm = 0.0;

    // Constant upper bound + break, matching the stock shader style so drivers
    // take the same path regardless of the octave count.
    for (int i = 0; i < 4; i++)
    {
        if (i >= octaves)
        {
            break;
        }

        sum += amp * gradientNoise(p);
        norm += amp;
        // Rotate each octave: stacked axis-aligned octaves are what make fbm read as
        // a square lattice (and as seams once the gas covers a large area).
        p = vec2(p.x * 0.8 - p.y * 0.6, p.x * 0.6 + p.y * 0.8) * 2.03 + vec2(17.31, -9.73);
        amp *= 0.5;
    }

    return sum / max(norm, 1e-4);
}

// Bilinear sample of the flow texture, done by hand with texelFetch.
//
// The texture is RGBA32F and the engine does not guarantee that 32-bit float formats
// accept a linear sampler. Four texel fetches per pixel are negligible next to the fbm
// chain, and they remove the risk of the whole field turning black on a driver that
// only supports nearest sampling for float images. The texture spans the whole map at
// FlowCellSize cells per texel, so its size comes from textureSize.
vec4 sampleFlow(sampler2D tex, vec2 uv)
{
    vec2 size = vec2(textureSize(tex, 0));
    vec2 p = clamp(uv, vec2(0.0), vec2(0.999999)) * size - 0.5;
    vec2 i = floor(p);
    vec2 f = p - i;
    vec2 hi = size - 1.0;
    ivec2 c0 = ivec2(clamp(i, vec2(0.0), hi));
    ivec2 c1 = ivec2(clamp(i + vec2(1.0, 0.0), vec2(0.0), hi));
    ivec2 c2 = ivec2(clamp(i + vec2(0.0, 1.0), vec2(0.0), hi));
    ivec2 c3 = ivec2(clamp(i + vec2(1.0, 1.0), vec2(0.0), hi));
    vec4 a = texelFetch(tex, c0, 0);
    vec4 b = texelFetch(tex, c1, 0);
    vec4 c = texelFetch(tex, c2, 0);
    vec4 d = texelFetch(tex, c3, 0);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// 3x3 binomial blur (weights 1-2-1). Bilinear taps return interpolation floats, so
// even the 8-bit texture produces sub-step gradients across a cell boundary.
vec4 reconstructGas(sampler2D tex, vec2 uv, vec2 offset)
{
    vec4 sum = texture(tex, uv) * 4.0;
    sum += (texture(tex, uv + vec2(offset.x, 0.0))
        + texture(tex, uv - vec2(offset.x, 0.0))
        + texture(tex, uv + vec2(0.0, offset.y))
        + texture(tex, uv - vec2(0.0, offset.y))) * 2.0;
    sum += texture(tex, uv + offset)
        + texture(tex, uv - offset)
        + texture(tex, uv + vec2(offset.x, -offset.y))
        + texture(tex, uv + vec2(-offset.x, offset.y));
    return sum * 0.0625;
}

// Squeeze the bilinear weight inside each cell toward its centre. The sampled plateau
// value is untouched (so one 1/255 cell stays visible), but the fixed ~1-cell bilinear
// ramp shrinks to roughly 1/k cells: k = 1 is the stock filter, k = 5 is about 0.2 cells.
// This is what lets the blur slider reach a genuinely hard edge at 0 without a second
// texture fetch.
// Smooth maximum. max() is continuous but its derivative jumps where the two inputs
// cross, and that corner reads as a drawn line: the real field falls off in one cell
// while an 8x8-averaged copy falls off over eight, so the merge picks each one in turn
// and the iso-contour has a kink. This lifts the corner by at most k/4 and is identical
// to max() everywhere else. The lift is scaled by the magnitude, so two empty cells
// still return exactly zero.
vec4 softMax(vec4 a, vec4 b, float k)
{
    vec4 m = max(a, b);
    vec4 t = max(vec4(k) - abs(a - b), vec4(0.0)) / k;
    // The lift peaks where the inputs are equal, which is exactly a uniform sheet: without
    // this weight a 1/255 layer would gain ~15% simply for being flat. Thin gas has no
    // crease to smooth (the halo is gated off there), so the lift fades in only once the
    // field is actually dense.
    vec4 w = smoothstep(vec4(0.08), vec4(0.30), m);
    return m + t * t * (k * 0.25) * w;
}

vec2 sharpenUv(vec2 uv, vec2 size, float sharp)
{
    vec2 cell = uv * size - 0.5;
    vec2 f = fract(cell);
    // 3, not 4: at full sharpening the sample lands on the cell boundary, where bilinear
    // filtering returns the mean of the two cells and draws a half-density outline.
    float k = 1.0 + 3.0 * sharp;
    f = clamp((f - 0.5) * k + 0.5, 0.0, 1.0);
    return (floor(cell) + f + 0.5) / size;
}

void main()
{
    float fade = u._Params.x;
    float blur = u._Params4.x;
    float faint = u._Params4.y;
    float sharpen = u._Params4.w;

    // The density textures only cover the baked window (view + margin), not the whole
    // map: scale/offset come from the renderer so the per-cell texture stays cheap.
    vec2 uv = vWorld * u._Params6.xy + u._Params6.zw;
    // The previous texture can have been baked from a different window (the window moves
    // in 8-16 cell steps while the camera pans), so it needs its own mapping. Without it,
    // mixing the two would smear the old window's gas across the new one for a whole
    // refresh interval — visible as the field jumping and drifting back.
    vec2 uvPrev = vWorld * u._Params7.xy + u._Params7.zw;
    // The mist pair is uploaded independently of the gas pair, so its previous half can
    // hold a different window. Mapping it with the gas one shifts the fog sideways for a
    // whole refresh interval.
    vec2 uvPrevMist = vWorld * u._Params8.xy + u._Params8.zw;

    // A previous texture is only meaningful inside its own baked window. The ring
    // outside that window is cleared to zero on upload, but Clamp turns uv < 0 into
    // the window's own left edge column (and, when the window fills the capacity,
    // uv > 1 into its edge row) — real gas, not the ring. Without this mask a camera
    // step larger than the window slack (a long first frame, a teleport, a fast
    // zoom) smears the old edge across the newly exposed strip until fade reaches 1.
    vec2 prevValid = step(vec2(0.0), uvPrev) * step(uvPrev, vec2(1.0));
    vec2 prevValidMist = step(vec2(0.0), uvPrevMist) * step(uvPrevMist, vec2(1.0));
    // Independent of blur: scaling it by (1 - blur) silently halved the slider at its
    // default position, which is why 1.0 never read as sharp. It now acts in two places —
    // it squeezes the bilinear ramp toward the cell centre (sharpenUv) and pulls the 3x3
    // taps back together — so blur = 1 + sharpen = 1 still resolves a hard rim.
    float sharp = clamp(sharpen, 0.0, 1.0);
    vec2 suv = sharp > 0.002 ? sharpenUv(uv, u._Params2.xy, sharp) : uv;
    vec2 suvPrev = sharp > 0.002 ? sharpenUv(uvPrev, u._Params2.xy, sharp) : uvPrev;
    vec2 suvPrevMist = sharp > 0.002
        ? sharpenUv(uvPrevMist, u._Params2.xy, sharp)
        : uvPrevMist;
    vec2 texel = 1.0 / max(u._Params2.xy, vec2(1.0));
    // Keep every tap inside one cell: with wider spacing the taps stop overlapping and
    // a hard rim turns into a three-band staircase.
    vec2 offset = texel * (0.35 + 0.65 * blur) * (1.0 - 0.85 * sharp);

    // Blur is a linear filter, so it is applied to both cross-fade endpoints and the
    // results are mixed afterwards.
    vec4 cur = texture(_GasTexCur, suv);
    vec4 prev = texture(_GasTexPrev, suvPrev);
    if (blur > 0.002)
    {
        cur = reconstructGas(_GasTexCur, suv, offset);
        prev = reconstructGas(_GasTexPrev, suvPrev, offset);
    }

    // The taps above may clamp at the previous window's edge; masking the result
    // keeps a fragment that was never covered by that window from contributing.
    prev *= prevValid.xyxy;
    vec4 gas = mix(prev, cur, fade);
    vec2 mistPrev = texture(_MistTexPrev, suvPrevMist).rg * prevValidMist;
    vec2 mist = mix(mistPrev, texture(_MistTexCur, suv).rg, fade);

    // Dense-gated wide diffusion. A blast fills the grid to 255 from its centre outward,
    // so its rim is a 255 -> 0 step inside one cell; a 3x3 kernel cannot feather that
    // (the taps stop overlapping and the rim turns into a staircase). The wide texture is
    // an 8x8 box average of the same field, so the shader only has to add it back where
    // the averaged value is already dense: a wide kernel can never lift a thin plateau
    // (averaging a 1/255 sheet still returns 1/255), which is what keeps sparse gas
    // untouched. max() preserves the original silhouette — the halo only adds.
    vec4 wide = mix(
        texture(_GasTexWidePrev, uvPrev) * prevValid.xyxy,
        texture(_GasTexWideCur, uv),
        fade);
    float halo = smoothstep(0.0, 0.5, blur);
    // The gate decides where the halo starts. A narrow band there is an edge of its own,
    // so the ramp spans most of the dense range instead of stopping at 0.08: the halo
    // thins out over the same distance the blur spreads it.
    wide *= smoothstep(vec4(0.02), vec4(0.40), wide) * halo;
    gas = softMax(gas, wide, 0.12);

    // Deadlife dust can be handed back to vanilla's sprite layer, which keeps its grainy
    // texture and its per-cell silhouette. The field then drops the channel completely:
    // zeroing it here covers the wide halo too, and makes a dust-only cell reach the
    // discard below instead of painting a second, smoother copy of the same cloud.
    if (u._Params9.w < 0.5)
    {
        gas.a = 0.0;
    }

    float mistDensity = mist.x * mist.y;
    float gasSum = gas.r + gas.g + gas.b + gas.a;
    float total = gasSum + mistDensity;

    // Keep the blurred tails: cutting at 1/255 is what turned every soft edge into
    // a hard one, because a 1/255 sheet has no density between 0 and the plateau.
    if (total < 0.0002)
    {
        discard;
    }

    // Detail-noise motion. The density is advected by AO either way; this only decides
    // how the sub-cell pattern moves inside it.
    //
    //   0  ambient drift: the pattern follows one global drift vector, accumulated on the
    //      CPU so a wind change bends the path instead of stepping it. Kept as the
    //      comparison baseline; the direction now matches the density instead of sliding
    //      upwind.
    //   1  local flow: the pattern is displaced by the *integrated* per-cell flow the
    //      density is actually advected by. The displacement is accumulated on the CPU
    //      and only extended here by the last publish's velocity, so a solver rewrite of
    //      the wind (or an explosion) changes the velocity without teleporting the
    //      pattern. This replaced a two-phase layer hand-over: any "phase * period"
    //      formulation re-derives position from the current wind, which is what made the
    //      hand-over look wrong when the wind field was updated unevenly.
    int noiseMode = int(u._Params9.x + 0.5);
    float animation = u._Params9.y;
    float freq = u._Params3.x;
    // How much of the current interval has already elapsed since the last noise bake.
    // Computed on the CPU (in double), already clamped to the publish period.
    float sincePublish = clamp(u._Params9.z, 0.0, 1.0);

    vec2 off0;

    if (noiseMode == 1)
    {
        vec2 windUv = vWorld * u._Params10.xy + u._Params10.zw;
        vec4 flow = sampleFlow(_WindTex, windUv);

        // rg = displacement so far (cells), ba = velocity (cells/s). The sign is
        // negative because a feature at a fixed noise coordinate p sits at
        // world = (p - off) / freq; to carry it downwind, off has to move upwind.
        vec2 displacement = flow.rg + flow.ba * (sincePublish * animation);
        off0 = -displacement * freq;
    }
    else
    {
        // Global ambient drift, kept as the comparison baseline. The original shader
        // added the vector directly, which moved the pattern upwind; the sign now
        // matches the local-flow and density motion.
        //
        // The displacement is integrated on the CPU because the wind vector changes
        // (weather transitions, gusts). Evaluating wind * absolute time here teleported
        // the whole pattern whenever that happened, since the jump grew with the seconds
        // since the renderer started. _Params11.xy is the accumulated offset,
        // _Params2.zw the drift velocity that this frame's elapsed time extends.
        off0 = -(u._Params11.xy + u._Params2.zw * sincePublish);
    }

    vec2 p0 = vWorld * freq + off0;

    vec2 warp = vec2(fbm(p0 * 0.42 + 11.7, 2), fbm(p0 * 0.42 - 5.3, 2)) - 0.5;

    float n = fbm(p0 + warp * u._Params3.y, 3);
    float medium = fbm(vWorld * 0.22 + off0 * 0.3 + vec2(5.1, -2.7), 2);
    n = mix(0.5, n, u._Params3.z);

    // Feature size around ten cells: without it, the field reads as an even soup.
    float coverage = fbm(vWorld * 0.085 + 3.7, 2);
    medium = medium * (0.75 + 0.5 * coverage);

    vec3 gasColor = (gas.r * u._SmokeColor.rgb
        + gas.g * u._ToxColor.rgb
        + gas.b * u._RotColor.rgb
        + gas.a * u._DustColor.rgb) / max(gasSum, 1e-4);
    vec3 color = (gasColor * gasSum + u._MistColor.rgb * mistDensity) / max(total, 1e-4);

    float density01 = max(max(gas.r, gas.g), max(gas.b, gas.a));
    density01 = max(density01, mistDensity);

    // One monotone optical-depth curve over the whole density range. The previous
    // max(body, thin) pair saturated around 0.3 (the near-linear thin term reached
    // 0.7 there), so a 255 -> 0 rim showed almost no alpha gradient across it no
    // matter how wide the density ramp was. A sub-linear power keeps one 1/255 cell
    // visible (about 0.05 alpha) while still leaving contrast above it.
    float bodyShape = (0.55 + 0.45 * medium) * (0.9 + 0.2 * n);
    float thinMix = 1.0 - smoothstep(0.08, 0.35, total);
    float gain = 1.0 + (faint - 1.0) * thinMix;
    float optical = u._Params.y * pow(total, 0.68) * bodyShape * gain;
    float alpha = 1.0 - exp(-optical);

    // Cloudy variant: modulate the continuous opacity with a billowing texture so it
    // reads as AO smoke without per-cell grain.
    if (u._Params4.z > 0.5)
    {
        float texture = smoothstep(0.28, 0.82, 0.7 * medium + 0.3 * n);
        alpha *= 0.45 + 0.75 * texture;
    }

    alpha = clamp(alpha * u._Params.z, 0.0, 1.0);

    // Denser cores read a little heavier instead of just more opaque.
    color = mix(color, color * 0.84, clamp(density01 * 1.5, 0.0, 1.0) * 0.35);

    outColor = vec4(color, alpha);
}
