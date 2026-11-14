#version 450

// Projectile-interceptor shield, in five surface types: crisp ring, hexagonal
// panel shell, two stock-like profiles (vanilla paints a projector dome with
// Other/ForceField and a personal belt with Other/ShieldBubble), and the hex
// bubble — a ShieldBubble body under a transparent spherical lattice revealed
// by a scan band.
//
// This replaces CompProjectileInterceptor.PostDraw, which paints a soft radial
// glow whose rim smears past Props.radius; the drawn edge and the tested one
// disagree. Apart from one sample of the baked hex lattice, everything here is
// analytic, so the silhouette sits on r == 1.0.
//
// Surface (DomeColor) — three concentric terms:
//   RIDGE  edge weight, in pixels, so a 3-cell and a 25-cell shield carry the
//          same drawn weight
//   CORE   gaussian hot line trending to white: the "lit" look
//   GLOW   pow(r, n): rises toward the silhouette, which is the dome cue
// Plus a body trace, angular drift, and a 1px inner feather that antialiases
// without ever growing past the intercept radius.
//
// Hit reactions — one additive quad per impact, kept in a list by
// ShieldImpactLog, because vanilla keeps only the last angle/ticks and loses a
// burst from two directions:
//   mode 1  painted flare (ends inside the visual rim)
//   mode 3  panels around the hit light up, the light walks outward (hex only)
//   mode 4  a sector of the surface itself brightens (any surface)
//
// Breaks — modes 2 and 5 carry their own progress and brightness, supplied by
// ShieldDissolveLog from the four places the comp shuts down; GetCurrentAlpha()
// collapses to zero on the next frame and cannot drive them:
//   mode 2  continuous noise-driven tear
//   mode 5  whole panels lost, as a ring travelling outward from the wound
// The origin is a point in the disc: a unit vector for a wound, or (0,0) for the
// centre (destroyed shield). Both scale by 1 + |origin| for equal pacing.
//
// Break paint is clipped by BreakEdge: r == 1 for crisp/panel, 1.05R for the
// stock-like profiles (1.0R for the hex bubble), so a hot front cannot float
// outside the surface.
//
// Hex shell — a screen-space grid never reads as a dome, so ShieldHexMap bakes a
// spherical lattice (a subdivided icosahedron's Voronoi diagram) into one
// texture: one sample plus a few arithmetic ops per fragment, and it carries a
// per-cell identity (rgb = face normal, a = distance to the wall) used by the
// panel hit/break effects. It also supplies the twelve pentagons Euler's formula
// requires; they are not drawn differently. Both lattice styles carry a centre
// aperture (CenterAperture) so pawns and the emitter stay readable; events are
// exempt.

layout(location = 0) in vec2 vWorld;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Params;  // @default = vec4(0.0, 1.0, 0.0, 5.0)      // mode, alpha, seconds, world radius (cells)
    vec4 _Params2; // @default = vec4(0.45, 0.35, 0.30, 0.05)  // ridge, core, glow, body weights
    vec4 _Params3; // @default = vec4(1.0, 0.0, 0.94, 0.2)     // flare dir x/z, cos(half angle), inner radius
                   //                                          // stock hit: dir x/z, cos(half angle), unused
                   //                                          // dissolve: break origin x/z, progress, seed
                   //                                          // hex hit: dir x/z, progress, reach share
    vec4 _Params4; // @default = vec4(0.0, 0.0, 0.0, 0.0)      // centre x/z in world cells, HexBubble net hold, hex color flags
    vec4 _Params5; // @default = vec4(2.8, 1.2, 5.0, 0.3)      // ridge half px, core sigma px, glow exponent, drift
    vec4 _Params6; // @default = vec4(0.0, 0.22, 0.75, 9.0)    // style (0 crisp, 1 hex, 2 stock dome, 3 stock belt, 4 hex bubble), hex face, hex line, SDF gradient (1/eRef)
    vec4 _Params7; // @default = vec4(1.0, 2.4, 0.20, 0.12)    // HexBubble: scan dir (+1 down / -1 up), period (s), feather, net baseline
    vec4 _Params8; // @default = vec4(1.0, 0.06, 0.0, 0.0)     // HexBubble: stripe count, band half width, leading edge (fixed 0), reserved
    vec4 _Params9; // @default = vec4(0.50, 0.0, 0.0, 0.0)     // lattice styles: clear radius (share of R); 0 disables it
                   //                                          // panels: break timing jitter (share of the animation); 0 is the switch
    vec4 _Color;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
} u;

// RGB = winning site's direction (the flat face normal, constant inside a cell).
// A   = edgeDistance: 0 on a cell wall, 1 at its centre.
layout(set = 2, binding = 1) uniform sampler2D _HexMap; // @default = black

// Per-draw hex color switches, carried in the free _Params4.w component.
// bit 0: lattice lines take the shield's _Color instead of trending to white.
// bit 1: lit panels in the hit wave / panel-loss break take _Color.
float HexLineFollowsColor()
{
    return mod(u._Params4.w, 2.0);
}

float HexFacesFollowColor()
{
    return step(1.5, u._Params4.w);
}

// Disc position -> lattice uv. p.y must be flipped: the bake writes row 0 at
// p.y == -1, but sampling puts v == 0 at the top of the stored rows. Only
// world-space comparisons (hit flash, break origin) can notice the mirror.
vec2 HexUv(vec2 p)
{
    return vec2(p.x, -p.y) * 0.5 + 0.5;
}

// Deterministic value noise, no texture, seeded per break. Wrapped to a small
// period before a small multiplier so float32 keeps enough fractional bits:
// lattice indices reach ~190 (Fbm's fourth octave), where the usual one-liner
// collapses into square blocks. The period exceeds anything Fbm can reach, so no
// octave wraps.
float Hash21(vec2 p)
{
    p = mod(p, 997.0);
    vec3 q = fract(vec3(p.xyx) * 0.1031);
    q += dot(q, q.yzx + 33.33);
    return fract((q.x + q.y) * q.z);
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

// The spherical cell pattern: (flat face shade, cell line). The lattice was
// projected onto the hemisphere before baking, so cells compress and bend toward
// the silhouette; the flat per-face shading completes the pseudo-3D read.
vec2 HexSurface(vec2 p, float aa, float t)
{
    vec4 baked = texture(_HexMap, HexUv(p));

    // Constant inside a cell: at once a face normal and a per-cell id.
    vec3 face = baked.rgb * 2.0 - 1.0;

    // Voronoi SDF: 0 on a wall, 1 at the centre.
    float e = baked.a;

    // Hemisphere normal; nz -> 0 at the silhouette.
    float nz = sqrt(max(1.0 - dot(p, p), 0.0));

    vec3 light = normalize(vec3(
        0.42 * cos(t * 0.13),
        0.42 * sin(t * 0.17),
        0.80));
    float diffuse = clamp(dot(face, light), 0.0, 1.0);
    float fresnel = 1.0 - nz;

    // Low face-to-face contrast, so the cell line stays the brightest thing at
    // a wall and does not read as two bright edges welded together.
    float jitter = Hash21(face.xy * 37.0) - 0.5;
    float shade = 0.42 + 0.40 * diffuse + 0.28 * fresnel + 0.05 * jitter;

    // Analytic field gradient: |grad e| = 1/(eRef * nz), nz being the Jacobian
    // of the projection. fwidth() was tried and failed both ways — it cancels in
    // the V valley along a wall and explodes under minification — so the drawn
    // width depended on zoom. _Params6.w is 1/eRef.
    float eGrad = u._Params6.w / max(nz, 0.12);
    float slope = aa * eGrad;

    // One cell spans this many pixels.
    float cellPixels = 2.0 * nz / max(u._Params6.w * aa, 1e-6);

    // Width as a share of a cell, clamped in pixels: a fixed pixel count reads as
    // a hairline zoomed in and as thick grout zoomed out. Below 1 px it only
    // aliases; above 6 px a cell stops reading as a panel.
    float lineWidthPx = clamp(cellPixels * 0.055, 1.0, 6.0);
    float line = 1.0 - smoothstep(0.0, lineWidthPx * slope, e);

    // Below ~5 px per cell the lattice aliases into moire, so fade the lines and
    // keep the panel shading.
    line *= smoothstep(2.0, 5.0, cellPixels);

    return vec2(clamp(shade, 0.0, 1.5), line);
}

// ---- the centre aperture (the two lattice styles) -------------------------
//
// A pure alpha weight: the surface is additive, so scaling alpha down just lets
// what is underneath show through. The centre is always clear and a wider clear
// radius simply reaches full brightness later. Ridge, core, hit reactions and
// break fronts are never apertured; only static fills and the net floor are.
float CenterAperture(float r)
{
    float end = clamp(u._Params9.x, 0.0, 1.0);
    if (end <= 0.001)
    {
        return 1.0;
    }

    return smoothstep(0.0, end, r);
}

// ---- the two stock-like surfaces -----------------------------------------
//
// The projector dome (Other/ForceField) is a narrow ring on a broad faint haze;
// the belt (Other/ShieldBubble) is a wider gradient that reads as a filled
// sphere. Both are rebuilt analytically; nothing is sampled from the game.
// _Params6.x selects the profile: 2 is ForceField, 3 is ShieldBubble.

// The ShieldBubble profile: a broad r^3.5 body filling the sphere plus a
// gaussian rim (sigma 0.090R inside, 0.030R outside) that stops just past r == 1.
vec4 StockBubbleDomeColor(float r)
{
    float rr = clamp(r, 0.0, 1.2);
    float d = rr - 1.0;

    // Short outer tail: the reference has no wide bloom outside its bright edge.
    float sigma = d < 0.0 ? 0.090 : 0.030;
    float rim = 0.55 * exp(-(d * d) / (2.0 * sigma * sigma));

    // The filled-sphere term: translucent middle, rising toward the edge.
    float body = 0.35 * pow(clamp(r, 0.0, 1.0), 3.5);

    // A translucent bubble is never clear.
    float haze = 0.030 + 0.030 * pow(clamp(r, 0.0, 1.0), 1.5);

    // No long outer bloom: down to a few percent of the peak by 1.045R.
    float outer = 1.0 - smoothstep(1.0, 1.045, r);

    // White texture, def colour applied directly: no white-hot core.
    return vec4(u._Color.rgb, (rim + body + haze) * outer);
}

// The stock-like dome, as (tint.rgb, unmultiplied alpha).
//
// A narrow gaussian rim with a long tail on both sides over a faint interior
// gradient. Every width is a fraction of the radius, not a pixel count, because
// the stock texture is stretched with the shield. No noise and no derivative
// width: the look is static and smooth, and the only motion is the caller's
// alpha pulse.
vec4 VanillaDomeColor(float r)
{
    // Style 3 has a different texture and shape; see above.
    if (u._Params6.x > 2.5)
    {
        return StockBubbleDomeColor(r);
    }

    // Slightly past the rim, so the outer tail fades through r == 1.
    float rr = clamp(r, 0.0, 1.2);

    // Fitted to the reference radial profile with Tools/shield_vanilla_compare.py:
    // peak on the intercept radius, a much wider inner shoulder, a very narrow
    // outer one. One gaussian, not two smoothsteps — the tails are the point.
    float d = rr - 1.0;
    float sigma = d < 0.0 ? 0.045 : 0.018;
    float rim = 0.72 * exp(-(d * d) / (2.0 * sigma * sigma));

    // Interior haze: 0.03 + 0.19 r^3.5, nearly clear at the centre and filling in
    // toward the rim. Clamps past r == 1, so there it becomes a flat outer bloom.
    float haze = 0.03 + 0.19 * pow(clamp(r, 0.0, 1.0), 3.5);

    // The stock material applies the def colour straight to a white texture:
    // haze and rim share one tint.
    return vec4(u._Color.rgb, rim + haze);
}

// ---- the hex bubble surface ----------------------------------------------
//
// A ShieldBubble body plus a transparent spherical hex net. The net is ONE
// continuous mask scaled by p.y and time, not lit cell by cell, so the cost is a
// sample and a few smoothsteps regardless of lattice density.

// The scan band: a narrow bright line with a much wider feather, repeated up to
// three times at equal spacing along the sweep; stripes take the max, so adding
// one never changes the shape of a band.
float HexBubbleScanMask(float py, float t)
{
    float halfW = u._Params8.y;
    float feather = max(u._Params7.z, 0.005);
    float dir = u._Params7.x >= 0.0 ? 1.0 : -1.0;
    float stripes = clamp(floor(u._Params8.x + 0.5), 1.0, 3.0);
    float period = max(u._Params7.y, 0.05);

    float lo = -1.0 - halfW - feather;
    float span = 2.0 + 2.0 * (halfW + feather);

    // mod first: t grows without bound and dividing first would exhaust the
    // float32 mantissa after a long session and freeze the scan.
    float phase = mod(t, period) / period;
    float head = lo + (dir > 0.0 ? phase : 1.0 - phase) * span;

    float best = 0.0;
    for (int i = 0; i < 3; i++)
    {
        if (float(i) < stripes)
        {
            float c = lo + mod(head - dir * float(i) * span / stripes - lo, span);
            best = max(best,
                1.0 - smoothstep(halfW, halfW + feather, abs(py - c)));
        }
    }

    // No leading-edge term: it was tuned to zero in the preview.
    return best;
}

// (tint.rgb, unmultiplied alpha), the same contract as DomeColor. The net exists
// only on the intact and break surfaces; hit reactions are separate quads.
vec4 HexBubbleDomeColor(float r, vec2 p, float aa, float t)
{
    float aperture = CenterAperture(r);

    // The static body and haze take the aperture; scaling alpha, not rgb, is what
    // additive blending reads.
    vec4 bubble = StockBubbleDomeColor(r);
    bubble.a *= aperture;

    float mode = u._Params.x;
    bool netVisible = mode < 0.5
        || (mode > 1.5 && mode < 2.5)
        || (mode > 4.5 && mode < 5.5);
    if (!netVisible || r >= 1.0)
    {
        return bubble;
    }

    // Fixed light (t = 0) so the lattice cannot introduce motion; only the SDF
    // line (.y) is used.
    float net = u._Params6.z * HexSurface(p, aa, 0.0).y;

    // Only the net's static floor is apertured, so the sweep still crosses the
    // centre. A zero baseline collapses to the scan alone.
    float baseline = u._Params7.w * aperture;
    float mask = baseline
        + (1.0 - baseline) * HexBubbleScanMask(p.y, t);

    // _Params4.z is the CPU-side hold: 1 forces the whole net opaque for an
    // animation, with a short fade so the switch is never a hard cut.
    mask = mix(mask, 1.0, clamp(u._Params4.z, 0.0, 1.0));

    // Clip the net at r == 1; the bubble's body fades out by 1.045R and the quad
    // runs to 1.16R, so a lattice in that margin reads as a larger shield.
    float netA = net * mask * (1.0 - smoothstep(1.0 - 2.0 * aa, 1.0, r));

    float sum = bubble.a + netA;
    vec3 halo = u._Color.rgb;
    vec3 lineTint = mix(
        halo,
        vec3(1.0),
        0.85 * (1.0 - HexLineFollowsColor()));
    vec3 tint = (halo * bubble.a + lineTint * netA) / max(sum, 1e-4);
    return vec4(tint, sum);
}

// The intact dome, as (tint.rgb, unmultiplied alpha). Shared by mode 0 and mode 2
// so a dissolving shield is the same surface with pieces removed.
vec4 DomeColor(float r, vec2 p, float aa, float ang, float t)
{
    // Style 4 and the stock-like styles answer first: they share none of the
    // ridge/core/glow machinery below (and 4 > 1.5 would lose the net).
    if (u._Params6.x > 3.5)
    {
        return HexBubbleDomeColor(r, p, aa, t);
    }

    if (u._Params6.x > 1.5)
    {
        return VanillaDomeColor(r);
    }

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

    // Only the interior fill differs between crisp and hexagonal; ridge, core and
    // glow are identical, so the silhouette stays exact either way.
    float hexLine = 0.0;
    float bodyTerm;
    if (u._Params6.x > 0.5 && u._Params6.x < 1.5)
    {
        vec2 hex = HexSurface(p, aa, t);
        hexLine = u._Params6.z * hex.y;
        bodyTerm = u._Params6.y * hex.x + hexLine;
    }
    else
    {
        bodyTerm = u._Params2.w * body;
    }

    // Only the panel shell's static fill is apertured; ridge and core ARE the
    // silhouette and must not be softened on a small belt.
    float aperture = (u._Params6.x > 0.5 && u._Params6.x < 1.5)
        ? CenterAperture(r)
        : 1.0;
    float glowA = glowTerm * aperture;
    float bodyA = bodyTerm * aperture;
    float hexLineA = hexLine * aperture;

    float sum = ridgeTerm + coreTerm + glowA + bodyA;

    vec3 halo = u._Color.rgb;
    vec3 coreTint = mix(halo, vec3(1.0), 0.65);
    vec3 lineTint = mix(
        halo,
        vec3(1.0),
        0.85 * (1.0 - HexLineFollowsColor()));
    vec3 tint = (halo * (ridgeTerm + glowA + max(bodyA - hexLineA, 0.0))
            + coreTint * coreTerm + lineTint * hexLineA)
        / max(sum, 1e-4);

    return vec4(tint, sum * drift);
}

// Where a break may paint, as opposed to where the idle dome is. A break must
// stay on the surface it is tearing: an unmasked hot front reads as a detached
// arc. Crisp and panel keep r == 1; the stock-like profiles stop at 1.05R; the
// hex bubble at 1.0R, because its idle haze already trails out past 1.0R (to
// 1.045R) inside a quad that runs to 1.16R.
float BreakEdge(float r, float edge)
{
    if (u._Params6.x > 3.5)
    {
        return 1.0 - smoothstep(0.985, 1.015, r);
    }

    if (u._Params6.x > 1.5)
    {
        return 1.0 - smoothstep(1.0, 1.05, r);
    }

    return edge;
}

void main()
{
    vec2 d = vWorld - u._Params4.xy;
    float radius = max(u._Params.w, 0.01);

    // 1.0 exactly on the collision rim.
    float r = length(d) / radius;

    // Normalised disc coordinates, radius-relative, so every shield shows the
    // same number of panels and breaks into the same number of pieces.
    vec2 p = d / radius;

    // Unit direction from the centre; guarded because atan(0, 0) is undefined
    // and would spread a NaN through the drift.
    vec2 n = d / max(length(d), 1e-4);

    // One pixel in units of r, taken before any branching so the derivative is
    // in uniform control flow.
    float aa = max(fwidth(r), 1e-4);

    // Silhouette. Crisp and panel fade inside r == 1 so the drawn shape never
    // grows past the intercept radius. The stock-like styles may glow past it on
    // purpose, ending inside the quad the renderer enlarged by 1.1601562.
    float edge;
    if (u._Params6.x > 1.5)
    {
        edge = 1.0 - smoothstep(1.0, 1.16, r);
    }
    else
    {
        edge = 1.0 - smoothstep(1.0 - aa, 1.0, r);
    }

    float t = u._Params.z;
    float ang = atan(n.y, n.x);
    float mode = u._Params.x;

    if (mode < 0.5)
    {
        // ---- intact dome --------------------------------------------------
        vec4 dome = DomeColor(r, p, aa, ang, t);
        outColor = vec4(dome.rgb, dome.a * u._Params.y * edge);
    }
    else if (mode < 1.5)
    {
        // ---- interception flare (crisp style) -----------------------------
        //
        // One quad per impact; the angular profile and radial falloff are both
        // soft, so neighbouring flares blend into a brighter patch instead of
        // stacking into an opaque slice.
        float cosHalf = u._Params3.z;

        // 0 at the side of the flare, 1 on its axis.
        float axis = clamp(
            (dot(n, u._Params3.xy) - cosHalf) / max(1.0 - cosHalf, 1e-3),
            0.0,
            1.0);
        float profile = smoothstep(0.0, 0.8, axis);

        // Linear ramp with a floor: a squared one keeps only the last few pixels,
        // which are the ones the dome's ridge already occupies.
        float radial = smoothstep(u._Params3.w, 1.0, r);
        float flare = radial * (0.55 + 0.45 * radial);

        // Clip to the visual rim: 1.05R through the stock-like soft band, r == 1
        // with AA for crisp and panel.
        float flareEdge;
        if (u._Params6.x > 1.5)
        {
            flareEdge = 1.0 - smoothstep(1.0, 1.05, r);
        }
        else
        {
            flareEdge = 1.0 - smoothstep(1.0 - aa, 1.0, r);
        }

        vec3 halo = u._Color.rgb;
        vec3 coreTint = mix(halo, vec3(1.0), 0.65);

        outColor = vec4(
            mix(halo, coreTint, flare * 0.6),
            profile * flare * u._Params.y * flareEdge);
    }
    else if (mode < 2.5)
    {
        // ---- dissolve -----------------------------------------------------
        vec2 od = u._Params3.xy;
        vec2 originDir = od / max(length(od), 1e-4);
        float progress = clamp(u._Params3.z, 0.0, 1.0);
        float seed = u._Params3.w;

        vec4 dome = DomeColor(r, p, aa, ang, t);

        // Continuous tear, the "Dissolve" option on every surface. Distance from
        // the failure point, perturbed by low-frequency noise, so it splits into
        // fingers. The origin is a point in the disc, and dividing by
        // 1 + |origin| gives a rim wound and a centred break the same pace.
        float noise = Fbm(p * 3.5 + vec2(seed, seed * 1.37));
        float spread = length(p - originDir) / (1.0 + length(originDir));
        float field = clamp(spread + (noise - 0.5) * 0.42, 0.0, 1.0);

        float threshold = progress * 1.15 - 0.15;

        // One pixel of tear edge at any zoom.
        float fw = max(fwidth(field), 0.006);
        float remaining = smoothstep(threshold - fw, threshold + fw, field);

        // White-hot line on the surviving side only; the dead side must stay
        // empty or the dissolve reads as an overlay.
        float front = exp(-pow((field - threshold) / (fw * 3.0 + 0.022), 2.0))
            * remaining;

        // Cracks: a finer noise ridge on the strip about to go. Zero at progress
        // 0, so the shield does not come pre-cracked.
        float crackNoise = ValueNoise(p * 19.0 + vec2(seed * 2.1, seed * 0.7));
        float crackRidge = 1.0 - smoothstep(0.0, 0.10, abs(crackNoise - 0.5));
        float nearFront =
            (1.0 - smoothstep(0.0, 0.26, field - threshold)) * remaining;
        float crack = crackRidge * nearFront * smoothstep(0.0, 0.06, progress);

        // Only `remaining` scales the surface; hollowing panels toward the seams
        // dimmed shields that were still standing.
        float baseA = dome.a * remaining;
        float frontA = front * 0.75;
        float crackA = crack * 0.30;

        // Short energy release at the instant of failure.
        float surge = 1.0 + 0.55 * exp(-progress * 9.0);

        // Fade the last sliver so the animation ends with the clock.
        float endFade = 1.0 - smoothstep(0.85, 1.0, progress);

        vec3 hot = mix(u._Color.rgb, vec3(1.0), 0.88);
        float total = baseA + frontA + crackA;
        vec3 tint = (dome.rgb * baseA + hot * (frontA + crackA))
            / max(total, 1e-4);

        // Clip to the surface's own silhouette.
        float breakEdge = BreakEdge(r, edge);

        // Surge the tear, not the body: brightening the body lit the stock-like
        // margin into a visible halo.
        float bodyA = baseA * u._Params.y * breakEdge * endFade;
        float hotA = (frontA + crackA) * u._Params.y * breakEdge * surge * endFade;

        outColor = vec4(tint, bodyA + hotA);
    }
    else if (mode < 3.5)
    {
        // ---- hexagonal impact flash ---------------------------------------
        //
        // Mode 3, the panel style's answer to a hit: the tiles around the impact
        // light up and the light walks outward. The hex map's per-cell id
        // quantises the flash into panels for free, and it can only light panels
        // that exist.
        vec2 od = u._Params3.xy;
        vec2 originDir = od / max(length(od), 1e-4);
        float progress = clamp(u._Params3.z, 0.0, 1.0);

        // _Params3.w is a share of the run to the far rim, converted to hops so
        // the flash covers the same screen area at every density instead of
        // barely reacting on a dense shield.
        float maxHops = 3.14159265 * u._Params6.w * 0.5;
        float reach = clamp(u._Params3.w, 0.05, 0.9) * maxHops;

        vec4 baked = texture(_HexMap, HexUv(p));
        vec3 face = baked.rgb * 2.0 - 1.0;
        float e = baked.a;

        float cellStep = 2.0 / max(u._Params6.w, 1e-4);
        vec3 hitDir = vec3(
            originDir,
            sqrt(max(1.0 - dot(originDir, originDir), 0.0)));
        float hops = acos(clamp(dot(face, hitDir), -1.0, 1.0)) / cellStep;

        float order = max(hops + (Hash21(face.xy * 37.0) - 0.5) * 0.3, 0.0);

        // Front position in hops. The 0.8 floor (growing with the reach on a
        // dense lattice) makes the nearest panel light on the first frame instead
        // of the flash fading in.
        float front = max(0.8, reach * 0.12) + progress * reach;
        float since = front - order;

        // Brightness falls with hops behind the front, measured in reach units so
        // the ring has the same angular width at any density.
        float behind = since / max(reach, 1e-4);
        float lit = smoothstep(0.0, 0.025, behind) * exp(-max(behind, 0.0) * 3.2);

        // Fade the last ring instead of letting it vanish with the draw.
        lit *= 1.0 - smoothstep(0.70, 1.0, progress);

        // Panels, not pixels: seams stay dim, so the flash is made of tiles.
        float panel = 0.6 + 0.4 * smoothstep(0.0, 0.25, e);

        vec3 hot = mix(
            u._Color.rgb,
            vec3(1.0),
            0.9 * (1.0 - HexFacesFollowColor()));
        // The hex bubble's idle body trails out past 1.0R (to 1.045R); a lattice
        // flash must not paint outside the shield it belongs to.
        float waveEdge = u._Params6.x > 3.5
            ? 1.0 - smoothstep(0.985, 1.015, r)
            : edge;
        outColor = vec4(hot, lit * panel * u._Params.y * waveEdge);
    }
    else if (mode < 4.5)
    {
        // ---- brighten impact: the dome itself lights up in a sector -------
        //
        // Mode 4, for every surface. Vanilla's ForceFieldCone is an additive
        // sector of the same soft surface, so the extra light here is the dome's
        // own shade masked to the sector — rim and haze brighten together, and
        // the sector follows the chosen silhouette instead of crossing it.
        float cosHalf = u._Params3.z;
        float axis = clamp(
            (dot(n, u._Params3.xy) - cosHalf) / max(1.0 - cosHalf, 1e-3),
            0.0,
            1.0);

        // Mostly flat inside, feathered only near the two sides. A long ramp
        // reads as a searchlight rather than a region that lit up.
        float sector = smoothstep(0.0, 0.45, axis);

        vec4 dome = DomeColor(r, p, aa, ang, t);

        // Contract the stock-like edge to 1.04R so the struck sector does not
        // read thicker than the rest of the band; crisp and panel already have a
        // one-pixel edge.
        float brightenEdge;
        if (u._Params6.x > 1.5)
        {
            brightenEdge = 1.0 - smoothstep(1.0, 1.04, r);
        }
        else
        {
            brightenEdge = edge;
        }

        // Thin the extra light toward the centre: a full-strength wedge reads as
        // a separate object.
        float innerFade = smoothstep(0.0, 1.0, r);

        // Additive, like the dome: the same surface written twice in the sector.
        outColor = vec4(
            dome.rgb,
            dome.a * sector * innerFade * u._Params.y * brightenEdge);
    }
    else
    {
        // ---- hex break: lose panels ---------------------------------------
        //
        // Mode 5, the panel shell's break. A shell of discrete objects fails by
        // losing whole panels; running a noise front over them would cut a soft
        // hole, which reads as a rendering mistake.
        vec2 od = u._Params3.xy;
        vec2 originDir = od / max(length(od), 1e-4);
        float progress = clamp(u._Params3.z, 0.0, 1.0);

        vec4 dome = DomeColor(r, p, aa, ang, t);
        vec4 baked = texture(_HexMap, HexUv(p));
        vec3 face = baked.rgb * 2.0 - 1.0;

        // Distance in panels: the lattice is uniform in solid angle, so angular
        // distance over the centre spacing counts hops.
        float cellStep = 2.0 / max(u._Params6.w, 1e-4);
        vec3 originOnSphere = vec3(
            originDir,
            sqrt(max(1.0 - dot(originDir, originDir), 0.0)));
        float hops = acos(clamp(dot(face, originOnSphere), -1.0, 1.0)) / cellStep;

        // A per-cell hash, with a different multiplier from the shading jitter's
        // so the two are uncorrelated.
        float panelId = Hash21(face.xy * 53.0);

        // The farthest panel is π away for a rim wound but only π/2 from the
        // centre, hence the 1 + |origin| factor (as in mode 2).
        float maxHops = 3.14159265 * u._Params6.w * 0.25
            * (1.0 + length(originDir));
        float span = maxHops + 1.5;

        // Order is always radial: the shell empties as a ring travelling outward
        // from the wound; the stagger only keeps that ring from being a circle.
        float radial = (hops + panelId * 0.45 + 0.6) / (span + 1.2);

        // _Params9.y adds timing, not order: each panel draws its own delay, so
        // the ring arrives as a ragged band. Ordering by noise instead would
        // remove the sweep and read as mode 2. 0.90 leaves room for the 0.09 fade.
        float start = radial * 0.90;
        float jitter = clamp(u._Params9.y, 0.0, 0.5);
        if (jitter > 0.001)
        {
            // A second hash, not panelId, so the delay does not correlate with
            // the sweep. Held back near the wound so the first frame stays intact.
            float ramp = smoothstep(0.0, max(0.12, jitter * 0.6), radial);
            start += (Hash21(face.xy * 91.0) - 0.5) * jitter * ramp;
        }

        // The panel's own clock: negative while intact, 1 once gone.
        float dt = (progress - start) * (1.0 / 0.09);
        float remaining = 1.0 - smoothstep(0.0, 1.0, dt);

        // Panels brighten only a little and briefly as they fail; a strong surge
        // turns the front into a white stripe.
        float dying = exp(-dt * dt * 6.0) * remaining * 0.3;

        // No cracks: the seams already are the cracks.
        float baseA = dome.a * remaining;
        float frontA = dying * 0.75;
        float surge = 1.0 + 0.55 * exp(-progress * 9.0);
        float endFade = 1.0 - smoothstep(0.85, 1.0, progress);

        vec3 hot = mix(
            u._Color.rgb,
            vec3(1.0),
            0.88 * (1.0 - HexFacesFollowColor()));
        float total = baseA + frontA;
        vec3 tint = (dome.rgb * baseA + hot * frontA) / max(total, 1e-4);

        float breakEdge = BreakEdge(r, edge);
        float bodyA = baseA * u._Params.y * breakEdge * endFade;
        float hotA = frontA * u._Params.y * breakEdge * surge * endFade;

        outColor = vec4(tint, bodyA + hotA);
    }
}
