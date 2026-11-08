#version 450

// High-temperature air shimmer.
//
// A single view-sized quad resamples the scene grab through a noisy refraction
// field. The strength comes from a per-cell temperature mask baked by
// HeatHazeRenderer:
//   R = normalised cell temperature (0..1, 0 C .. 240 C)
//   G = direct fire hotspot (0..1)
//   B = unused
//   A = 255
//
// The mask is sampled with the same window mapping that GasFieldRenderer uses:
//   uv = vWorld * _Window.xy + _Window.zw
// with the texture capacity in the denominator, so the mask stays anchored to
// the map while the camera pans.
//
// Distortion is applied in screen space (grab UV) but generated from world-space
// noise, so the pattern belongs to the air over the terrain rather than to the
// screen. The amplitude is expressed in screen pixels, which keeps the effect
// readable at every zoom level.
//
// The scene grab is declared in VisualTweaksShaders.xml:
//   <sceneGrab><name>VisualTweaksHeatHaze</name><queue>3174</queue></sceneGrab>
// Queue 3174 sits after the normal map visuals and before fog of war (3175), so
// fog and darkness cover the shimmer exactly like they cover terrain.

layout(location = 0) in vec2 vWorld;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Params;  // @default = vec4(0.0, 1.0, 1.0, 1.0)      // time, intensity, noise scale, speed (1 = old 2x baseline)
    vec4 _Params2; // @default = vec4(0.1458333, 0.3333333, 1.0, 2.5) // start temp, full temp, fire boost, amplitude px
    vec4 _Window;  // @default = vec4(0.001953125, 0.001953125, 0.0, 0.0) // 1/w, 1/h, -originX/w, -originZ/h
} u;

layout(set = 2, binding = 1) uniform sampler2D _GrabVisualTweaksHeatHaze;
layout(set = 2, binding = 2) uniform sampler2D _HeatMask; // @default = black

// Hoskins hash: stable at map coordinates in the hundreds, unlike the naive
// fract(p * large) hashes that collapse into a grid.
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

void main()
{
    vec2 uv = vWorld * u._Window.xy + u._Window.zw;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
    {
        discard;
    }

    vec4 mask = texture(_HeatMask, uv);

    // Temperature is normalised to 0..240 C on the CPU. The thresholds are also
    // normalised, so changing the slider does not require a rebake.
    float full = max(u._Params2.y, u._Params2.x + 0.001);
    float heat = smoothstep(u._Params2.x, full, mask.r);
    heat = max(heat, mask.g * u._Params2.z);

    if (heat <= 0.002)
    {
        discard;
    }

    float scale = max(u._Params.z, 0.05);
    float speed = u._Params.w;
    float time = u._Params.x;

    // World-anchored noise, slowly drifting upward. One low-frequency warp
    // breaks the lattice pattern and keeps the motion from looking like a
    // scrolling texture. The constants include the 2x baseline: speed 1 matches
    // the previous fixed drift, and the slider spans 0.1x..2x of that.
    vec2 p = vWorld * (1.2 * scale) + vec2(time * 0.14, time * 0.84) * speed;
    float warp = gradientNoise(p * 0.37 + vec2(7.1, -3.3)) - 0.5;
    p += vec2(warp * 1.3, warp * -0.9);

    float nx = fbm2(p);
    float ny = fbm2(p + vec2(19.7, -13.1));
    vec2 noise = vec2(nx, ny) - 0.5;

    vec2 grabSize = vec2(textureSize(_GrabVisualTweaksHeatHaze, 0));
    vec2 grabUv = gl_FragCoord.xy / max(grabSize, vec2(1.0));

    // Amplitude is in screen pixels, scaled by how hot this cell is and by the
    // user's intensity slider. The noise is centred on 0, so the 2x factor makes
    // the slider correspond to the peak-to-peak displacement.
    float amplitude = u._Params2.w * u._Params.y * heat;
    vec2 offset = noise * (2.0 * amplitude) / max(grabSize, vec2(1.0));

    // A small upward bias reads as rising air without adding a directional
    // streak to the noise itself.
    offset.y += amplitude * 0.12 / max(grabSize.y, 1.0);

    vec3 scene = texture(
        _GrabVisualTweaksHeatHaze,
        clamp(grabUv + offset, vec2(0.0), vec2(1.0))).rgb;

    // Feather only the very faint edge. Above ~8% heat the refracted scene fully
    // replaces the original; at lower values the offset is small, so blending the
    // two is invisible and avoids a hard cut on the mask boundary.
    outColor = vec4(scene, smoothstep(0.0, 0.08, heat));
}
