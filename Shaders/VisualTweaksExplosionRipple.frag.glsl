#version 450

// Instanced shockwave rendering needs the storage buffers declared in this
// stage too: Waylith builds the material descriptor set from the fragment
// source, so without these declarations binding 2 / 3 would not exist. The
// fragment itself only reads the interpolated vParams/vParams2 varyings; this
// mirrors Mote.frag in the vanilla shaders.
#ifdef INSTANCED
#include "InstanceTransforms.glsl"

layout(std430, set = 2, binding = 3) readonly buffer _RippleParams
{
    vec4 rippleParams[];
};
#endif

// Screen-space refraction shockwave.
//
// One quad per explosion, centred on the blast and scaled to the visual radius.
// The shader builds a wide compression band in local UV space and resamples the
// scene grab through it. The band is deliberately broad and smooth, so the
// result reads as a lens of compressed air sweeping outward rather than a
// painted ring. This follows the GraphicsLib / Starsector ripple profile: the
// displacement peaks on the wave front and falls away to zero on both sides.
//
// The scene grab is declared in VisualTweaksShaders.xml:
//   <sceneGrab><name>VisualTweaksExplosionRipple</name>...</sceneGrab>
// which binds the frame colour to _GrabVisualTweaksExplosionRipple. The grab is
// taken at queue 3178, after motes and explosion flashes (3151) and before the
// darkness layer (3180), so the shockwave also bends the blast's own fireball.
//
// Output is opaque inside the band: the refracted scene replaces the frame there
// and fades continuously to the untouched scene at the band edge. Alpha blending
// is therefore never used to fade the effect (partial alpha would leave a ghost
// image); the displacement itself goes to zero at the boundary.
//
// Per-wave data arrives through the vertex stage:
//   vParams.x   age 0..1
//   vParams.y   wave front position, 0..1 across the quad radius
//   vParams.z   band width (sigma, normalised)
//   vParams.w   intensity
//   vParams2.x  trailing rarefaction length (normalised)
//   vParams2.y  random seed
//   vParams2.z  affected angle minimum (degrees)
//   vParams2.w  affected angle maximum (degrees); negative disables the mask
//
// Material constants (shared by every wave, set once on the material):
//   _Params3.x  radial noise amplitude
//   _Params3.y  angular noise frequency
//   _Params3.z  angular softness (degrees)
//   _Params4.x  refraction strength (fraction of the visual radius)
//   _Params4.y  brightness gain on the compression crest
//
// _Params / _Params2 are only read by the non-instanced small-count fallback
// path in the vertex shader; the declarations must stay in sync between stages.

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vParams;
layout(location = 2) in vec4 vParams2;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Params;  // @default = vec4(0.0, 0.0, 0.15, 1.0)
    vec4 _Params2; // @default = vec4(0.3, 0.0, 0.0, -1.0)
    vec4 _Params3; // @default = vec4(0.02, 3.2, 8.0, 0.0)
    vec4 _Params4; // @default = vec4(0.18, 0.06, 0.0, 0.0)
} u;

layout(set = 2, binding = 1) uniform sampler2D _GrabVisualTweaksExplosionRipple;

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
    vec2 centered = vUv * 2.0 - 1.0;
    float r = length(centered);

    // Screen-space size of one pixel expressed in r units. Reused for the band
    // minimum width below and for the refraction radius later.
    float pixelR = max(length(vec2(dFdx(r), dFdy(r))), 1e-6);
    float pixelSigma = pixelR * 1.5;
    float age = vParams.x;

    // Sample the noise on a circle so the angular pattern stays continuous
    // across the atan seam. The radial warp only bends the front slightly; the
    // broad band itself is what sells the effect.
    float angle = atan(-centered.y, centered.x);
    vec2 circle = vec2(cos(angle), sin(angle)) * u._Params3.y
        + vec2(
            vParams2.y * 13.1 + age * 0.6,
            vParams2.y * 7.7 + age * 0.4);
    float noise = fbm2(circle);
    float warp = (noise - 0.5) * u._Params3.x;

    float front = vParams.y + warp;
    float d = r - front;

    // The shell thins as it decays. The trailing (inner) edge starts closing
    // first, then the leading edge follows, so the band collapses to close to
    // zero width before the wave is removed instead of vanishing while still
    // wide. A pixel-sized floor avoids aliasing at the very end; the strength
    // fade below takes it out before that line becomes visible.
    float baseWidth = max(vParams.z, 0.02);
    float collapseInner = smoothstep(0.55, 1.0, age);
    float collapseOuter = smoothstep(0.68, 1.0, age);
    float sigmaInner = max(
        baseWidth * 0.85 * (1.0 - collapseInner),
        pixelSigma);
    float sigmaOuter = max(
        baseWidth * 1.15 * (1.0 - collapseOuter),
        pixelSigma);
    float sigma = d < 0.0 ? sigmaInner : sigmaOuter;
    float crest = exp(-(d * d) / (sigma * sigma));

    // Weak rarefaction trailing behind the front: after the compression passes
    // the air pulls back a little, which keeps the lens from looking like a
    // simple magnifier. It disappears together with the trailing edge.
    float tail = max(vParams2.x, 1e-3);
    float wake = d < 0.0 ? exp(d / tail) : 0.0;
    float strength = crest - wake * (0.22 * (1.0 - collapseInner));

    // Fade the quad edge and keep the very centre free of distortion (the
    // displacement is zero there anyway, and a soft core avoids a hard seam on
    // the first frames).
    float outer = 1.0 - smoothstep(0.94, 1.06, r);
    float inner = smoothstep(0.0, 0.015, r);
    strength *= outer * inner;

    // Once the band has collapsed, fade the residual line out instead of
    // cutting it at the end of the wave's lifetime.
    strength *= 1.0 - smoothstep(0.85, 1.0, age);

    float mask = 1.0;
    if (vParams2.w >= 0.0)
    {
        float a = mod(degrees(angle) + 360.0, 360.0);
        float lo = mod(vParams2.z + 360.0, 360.0);
        float hi = mod(vParams2.w + 360.0, 360.0);
        float range = mod(hi - lo + 360.0, 360.0);
        if (range < 0.5)
        {
            range = 360.0;
        }

        float soft = max(u._Params3.z, 0.5);
        // Signed distance from the minimum angle, in -180..180, so the mask
        // wraps correctly through 0/360.
        float rel = mod(a - lo + 540.0, 360.0) - 180.0;
        mask = smoothstep(-soft, soft, rel)
            * (1.0 - smoothstep(range - soft, range + soft, rel));
    }

    float s = strength * vParams.w * mask;
    if (abs(s) <= 0.002)
    {
        discard;
    }

    vec2 grabSize = vec2(textureSize(_GrabVisualTweaksExplosionRipple, 0));
    vec2 grabUv = gl_FragCoord.xy / max(grabSize, vec2(1.0));

    // Compressed air acts as a positive lens: sample towards the centre so the
    // content behind the crest is magnified. Scaling by the on-screen radius
    // (screen-space derivative of r) keeps the look identical at any zoom: a
    // small blast bends by a few pixels, a large one by more, both by the same
    // fraction of their own radius. The offset follows the local quad direction,
    // so the ground perspective ellipse is preserved.
    float radiusPx = min(1.0 / pixelR, 4096.0);
    vec2 offset = centered
        * (u._Params4.x * s * radiusPx)
        / max(grabSize, vec2(1.0));

    vec2 sampleUv = clamp(
        grabUv - vec2(offset.x, -offset.y),
        vec2(0.0),
        vec2(1.0));
    vec3 scene = texture(_GrabVisualTweaksExplosionRipple, sampleUv).rgb;

    // A faint brightness lift on the compression crest reads as air glinting in
    // the blast light.
    scene *= 1.0 + u._Params4.y * clamp(s, 0.0, 1.5);

    outColor = vec4(clamp(scene, 0.0, 1.0), 1.0);
}
