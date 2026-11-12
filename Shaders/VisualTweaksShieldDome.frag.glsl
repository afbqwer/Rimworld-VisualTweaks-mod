#version 450

// Crisp-edged projectile-interceptor shield.
//
// This replaces CompProjectileInterceptor.PostDraw, which paints Other/ForceField
// through ShaderDatabase.MoteGlow: a soft radial gradient texture, so the rim is a
// smear several pixels wide and the glow continues past the radius the game
// actually tests against (Props.radius in CheckIntercept). The gameplay radius and
// the painted radius disagree, and the eye reads the painted one.
//
// Everything below is analytic, so the silhouette sits exactly on r == 1.0.
//
// ---- how the silhouette is built ------------------------------------------
//
// Three concentric terms, each answering a different question:
//
//   1. RIDGE — how thick the edge reads. A soft band a few pixels wide, centred
//      just inside the rim. One narrow line reads as vector art at any size; a
//      band reads as material. Width is in *pixels* (see _Params5.x), converted
//      through the same derivative the antialiasing uses, so a 3-cell shield and a
//      25-cell one carry the same drawn weight.
//   2. CORE — what makes it look lit. A gaussian hot line sitting inside the
//      ridge, trending towards white rather than the def's tint. Its tails are why
//      it reads as light instead of as a stroke.
//   3. GLOW — why it reads as a dome and not a disc. pow(r, n) rises toward the
//      silhouette, which is where a sphere seen head-on is brightest. Without
//      this the shape stays a circle no matter how good the edge is.
//
// On top of those: a trace of body (r^2) so the interior is not empty, angular
// drift so nothing is perfectly uniform, and a one-pixel inner feather at r == 1
// so the silhouette antialiases without ever growing past the intercept radius.
//
// ---- the interception flare ----------------------------------------------
//
// Mode 1 draws one flare per recorded impact, not one per shield. Vanilla keeps a
// single lastInterceptAngle and a single lastInterceptTicks, so a burst from two
// directions collapses into whichever bullet resolved last — the older directions
// are simply lost. The flare is therefore driven by a list of impacts supplied by
// ShieldImpactLog, one quad each, additive, so overlapping hits accumulate the way
// several lights on one surface should.
//
// ---- the dissolve --------------------------------------------------------
//
// Mode 2 is what plays when the shield breaks. Vanilla's Shield_Break effecter
// flashes a texture and plays a sound, but the dome itself is gone on the very
// next frame: GetCurrentAlpha() collapses to zero because Active is false. A
// dissolve cannot be driven by that alpha, so this mode carries its own progress
// and its own brightness, supplied by ShieldDissolveLog from the four places the
// comp actually shuts down.
//
// The surface is erased by a threshold crossing a scalar field. The field is the
// distance from the point of failure, perturbed by low-frequency noise, so the
// tear front splits into fingers instead of expanding as a clean circle. A hot
// line rides that front, and a finer noise ridge adds cracks to the material
// just behind it. Coordinates are normalised by the radius (p = d / radius), so
// a 2-cell bubble and a 25-cell gravship dome break into the same number of
// pieces rather than the large one simply turning into sand.
//
// Everything is still clipped by `edge` at r == 1: even while falling apart the
// shield never paints outside the circle it blocks.

layout(location = 0) in vec2 vWorld;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Params;  // @default = vec4(0.0, 1.0, 0.0, 5.0)      // mode, alpha, seconds, world radius (cells)
    vec4 _Params2; // @default = vec4(0.45, 0.35, 0.30, 0.05)  // ridge, core, glow, body weights
    vec4 _Params3; // @default = vec4(1.0, 0.0, 0.94, 0.2)     // flare dir x/z, cos(half angle), inner radius
                   //                                          // dissolve: break origin x/z, progress, seed
    vec4 _Params4; // @default = vec4(0.0, 0.0, 0.0, 0.0)      // centre x/z in world cells, unused
    vec4 _Params5; // @default = vec4(2.8, 1.2, 5.0, 0.3)      // ridge half px, core sigma px, glow exponent, drift
    vec4 _Color;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
} u;

// Deterministic value noise. No texture, so the pattern cannot be stretched or
// tiled by a different plane10 UV orientation, and the seed is a uniform the CPU
// can change per break.
float Hash21(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float ValueNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = Hash21(i);
    float b = Hash21(i + vec2(1.0, 0.0));
    float c = Hash21(i + vec2(0.0, 1.0));
    float d = Hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// Four octaves, normalised back to 0..1. 0.5 + 0.25 + 0.125 + 0.0625 = 0.9375.
float Fbm(vec2 p)
{
    float v = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 4; i++)
    {
        v += amp * ValueNoise(p);
        p = p * 2.03 + vec2(17.1, 9.7);
        amp *= 0.5;
    }
    return v / 0.9375;
}

// The intact dome, as (tint.rgb, unmultiplied alpha). Shared by mode 0 and mode 2
// so a dissolving shield is literally the same surface with pieces removed, not a
// second effect that happens to look similar.
vec4 DomeColor(float r, float aa, float ang, float t)
{
    float rc = clamp(r, 0.0, 1.0);

    float ridgeHalf = max(u._Params5.x * aa, aa);
    float ridgeX = (r - (1.0 - ridgeHalf * 0.5)) / ridgeHalf;
    float ridge = 1.0 - smoothstep(0.42, 1.0, abs(ridgeX));

    float cs = max(u._Params5.y * aa, aa * 0.5);
    float cx = (r - (1.0 - cs * 1.2)) / cs;
    float core = exp(-cx * cx);

    float glow = pow(rc, max(u._Params5.z, 1.0));
    float body = rc * rc;

    float wave = sin(ang * 2.0 + t * 0.45) * sin(ang * 3.0 - t * 0.27);
    float drift = 1.0 - u._Params5.w * (0.5 - 0.5 * wave);
    float coreDrift = 0.82 + 0.18 * sin(ang * 5.0 - t * 0.8);

    float ridgeTerm = u._Params2.x * ridge;
    float coreTerm = u._Params2.y * core * coreDrift;
    float glowTerm = u._Params2.z * glow;
    float bodyTerm = u._Params2.w * body;
    float sum = ridgeTerm + coreTerm + glowTerm + bodyTerm;

    vec3 halo = u._Color.rgb;
    vec3 coreTint = mix(halo, vec3(1.0), 0.65);
    vec3 tint = (halo * (ridgeTerm + glowTerm + bodyTerm) + coreTint * coreTerm)
        / max(sum, 1e-4);

    return vec4(tint, sum * drift);
}

void main()
{
    vec2 d = vWorld - u._Params4.xy;
    float radius = max(u._Params.w, 0.01);

    // 1.0 exactly on the collision rim — the number the whole effect is built
    // around, and the reason the quad is scaled to 2 * radius rather than to
    // vanilla's 2 * radius * 1.1601562 texture-margin factor.
    float r = length(d) / radius;

    // Unit direction from the centre. Guarded because the pixel under the exact
    // centre has none, and atan(0, 0) is undefined — the drift below would
    // propagate a NaN into the fragment before it could be discarded.
    vec2 n = d / max(length(d), 1e-4);

    // One pixel, expressed in units of r. Computed unconditionally, before any
    // branching, so the derivative is taken in uniform control flow. Every width
    // in this shader is a multiple of it.
    float aa = max(fwidth(r), 1e-4);

    // The silhouette: a one-pixel fade placed *inside* r == 1, so the edge
    // antialiases without the drawn shape ever growing past what the shield
    // actually blocks.
    float edge = 1.0 - smoothstep(1.0 - aa, 1.0, r);

    float t = u._Params.z;
    float ang = atan(n.y, n.x);
    float mode = u._Params.x;

    if (mode < 0.5)
    {
        // ---- intact dome --------------------------------------------------
        vec4 dome = DomeColor(r, aa, ang, t);
        outColor = vec4(dome.rgb, dome.a * u._Params.y * edge);
    }
    else if (mode < 1.5)
    {
        // ---- interception flare -------------------------------------------
        //
        // One quad per impact. Angular profile and radial falloff are both soft,
        // so two flares from neighbouring directions blend into a brighter patch
        // instead of stacking into an opaque slice.
        float cosHalf = u._Params3.z;

        // 0 at the side of the flare, 1 on its axis. Feathering the top of this
        // range (below) is what keeps overlapping flares from showing a seam.
        float axis = clamp(
            (dot(n, u._Params3.xy) - cosHalf) / max(1.0 - cosHalf, 1e-3),
            0.0,
            1.0);
        float profile = smoothstep(0.0, 0.8, axis);

        // Linear-with-a-floor radial ramp rather than a squared one: a squared
        // ramp keeps only the last few pixels bright, and on a small shield those
        // are the same few pixels the dome's own ridge occupies, so the flare
        // disappears into the rim it is supposed to disturb.
        float radial = smoothstep(u._Params3.w, 1.0, r);
        float flare = radial * (0.55 + 0.45 * radial);

        vec3 halo = u._Color.rgb;
        vec3 coreTint = mix(halo, vec3(1.0), 0.65);

        outColor = vec4(
            mix(halo, coreTint, flare * 0.6),
            profile * flare * u._Params.y * edge);
    }
    else
    {
        // ---- dissolve -----------------------------------------------------
        vec2 od = u._Params3.xy;
        vec2 originDir = od / max(length(od), 1e-4);
        float progress = clamp(u._Params3.z, 0.0, 1.0);
        float seed = u._Params3.w;

        // Normalised disc coordinates. Radius-relative, so the number of pieces
        // a shield breaks into is a design constant rather than a side effect of
        // how big the building happens to be.
        vec2 p = d / radius;

        // Tear front: distance from the point that failed, with a low-frequency
        // perturbation. The perturbation is what makes the front split into
        // fingers instead of sweeping across as a clean circle.
        float noise = Fbm(p * 3.5 + vec2(seed, seed * 1.37));
        float spread = length(p - originDir) * 0.5;
        float field = clamp(spread + (noise - 0.5) * 0.42, 0.0, 1.0);

        // Threshold sweeps from just below the field's minimum to just above its
        // maximum, so progress 0 leaves the whole surface and progress 1 leaves
        // none of it. The feather is the field's own screen derivative: one pixel
        // of tear edge at any zoom.
        float threshold = progress * 1.15 - 0.15;
        float fw = max(fwidth(field), 0.006);
        float remaining = smoothstep(threshold - fw, threshold + fw, field);

        // White-hot line riding the tear. Only on the surviving side — the dead
        // side must stay empty or the dissolve reads as a bright overlay rather
        // than as material being removed.
        float front = exp(-pow((field - threshold) / (fw * 3.0 + 0.022), 2.0))
            * remaining;

        // Cracks: ridges of a finer noise, visible only in the strip of surface
        // that is about to go. Zero at progress 0 so the shield does not come
        // pre-cracked.
        float crackNoise = ValueNoise(p * 19.0 + vec2(seed * 2.1, seed * 0.7));
        float crackRidge = 1.0 - smoothstep(0.0, 0.10, abs(crackNoise - 0.5));
        float nearFront =
            (1.0 - smoothstep(0.0, 0.26, max(field - threshold, 0.0))) * remaining;
        float crack = crackRidge * nearFront * smoothstep(0.0, 0.06, progress);

        vec4 dome = DomeColor(r, aa, ang, t);
        float baseA = dome.a * remaining;
        float frontA = front * 0.75;
        float crackA = crack * 0.30;

        // A short energy release at the instant of failure. Decays over the first
        // tenth of the animation, so the break reads as a flash rather than as
        // the whole dissolve being uniformly brighter.
        float surge = 1.0 + 0.55 * exp(-progress * 9.0);

        // The last surviving sliver would otherwise wink out the instant the
        // threshold crosses it. Fading the final stretch makes the end of the
        // animation coincide with the end of the clock, with no dead tail.
        float endFade = 1.0 - smoothstep(0.85, 1.0, progress);

        vec3 hot = mix(u._Color.rgb, vec3(1.0), 0.88);
        float total = baseA + frontA + crackA;
        vec3 tint = (dome.rgb * baseA + hot * (frontA + crackA))
            / max(total, 1e-4);

        outColor = vec4(tint, total * u._Params.y * edge * surge * endFade);
    }
}
