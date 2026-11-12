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

layout(location = 0) in vec2 vWorld;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Params;  // @default = vec4(0.0, 1.0, 0.0, 5.0)      // mode, alpha, seconds, world radius (cells)
    vec4 _Params2; // @default = vec4(0.45, 0.35, 0.30, 0.05)  // ridge, core, glow, body weights
    vec4 _Params3; // @default = vec4(1.0, 0.0, 0.94, 0.2)     // flare dir x/z, cos(half angle), inner radius
    vec4 _Params4; // @default = vec4(0.0, 0.0, 0.0, 0.0)      // centre x/z in world cells, unused
    vec4 _Params5; // @default = vec4(2.8, 1.2, 5.0, 0.3)      // ridge half px, core sigma px, glow exponent, drift
    vec4 _Color;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
} u;

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

    if (u._Params.x < 0.5)
    {
        // ---- dome ---------------------------------------------------------
        float rc = clamp(r, 0.0, 1.0);

        // Ridge: half-width in pixels, centred half a width inside the rim so the
        // outer shoulder lands on the silhouette and is then feathered by `edge`.
        // This is the "too thin" dial — it is a real width, not a brightness.
        //
        // The smoothstep starts at 0.42 rather than 0, which gives the band a flat
        // middle with shoulders instead of a triangular peak. A peak profile loses
        // half its weight a pixel from centre, so it reads as a line no matter how
        // wide the half-width is set; the plateau is what makes the edge read as a
        // band.
        float ridgeHalf = max(u._Params5.x * aa, aa);
        float ridgeX = (r - (1.0 - ridgeHalf * 0.5)) / ridgeHalf;
        float ridge = 1.0 - smoothstep(0.42, 1.0, abs(ridgeX));

        // Core: a gaussian inside the ridge, one and a bit pixels inside the rim.
        float cs = max(u._Params5.y * aa, aa * 0.5);
        float cx = (r - (1.0 - cs * 1.2)) / cs;
        float core = exp(-cx * cx);

        // Glow: the curvature term.
        float glow = pow(rc, max(u._Params5.z, 1.0));

        // Body: a trace of tint so the interior is a volume, not an empty hoop.
        float body = rc * rc;

        // Drift: two slow angular waves, incommensurate so they never settle into
        // a rotating pattern. The core gets a faster, weaker one of its own so the
        // line never looks machined. Brightness only — never radius.
        float wave = sin(ang * 2.0 + t * 0.45) * sin(ang * 3.0 - t * 0.27);
        float drift = 1.0 - u._Params5.w * (0.5 - 0.5 * wave);
        float coreDrift = 0.82 + 0.18 * sin(ang * 5.0 - t * 0.8);

        float ridgeTerm = u._Params2.x * ridge;
        float coreTerm = u._Params2.y * core * coreDrift;
        float glowTerm = u._Params2.z * glow;
        float bodyTerm = u._Params2.w * body;
        float sum = ridgeTerm + coreTerm + glowTerm + bodyTerm;

        // Weighted average rather than three adds: the halo keeps the def's tint,
        // the core goes toward white, and where they overlap the result cannot
        // drift past white.
        vec3 halo = u._Color.rgb;
        vec3 coreTint = mix(halo, vec3(1.0), 0.65);
        vec3 tint = (halo * (ridgeTerm + glowTerm + bodyTerm) + coreTint * coreTerm)
            / max(sum, 1e-4);

        outColor = vec4(tint, sum * drift * u._Params.y * edge);
    }
    else
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

        // Squared radial ramp: the flare belongs to the rim, not to the centre.
        // A wedge that reaches the middle covers whatever is standing under the
        // shield and stops reading as "something hit the surface here".
        float radial = smoothstep(u._Params3.w, 1.0, r);
        float flare = radial * radial;

        vec3 halo = u._Color.rgb;
        vec3 coreTint = mix(halo, vec3(1.0), 0.65);

        outColor = vec4(
            mix(halo, coreTint, flare * 0.6),
            profile * flare * u._Params.y * edge);
    }
}
