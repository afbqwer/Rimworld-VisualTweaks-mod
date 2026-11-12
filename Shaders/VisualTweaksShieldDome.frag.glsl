#version 450

// Projectile-interceptor shield, in three surface types: crisp ring, hexagonal
// panel shell, and stock-like. Stock-like has two profiles, because vanilla's
// projector dome and personal belt use different textures; a dome can choose
// either (the belt's bubble is offered as a fourth style). See the two stock-like
// surfaces section below.
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
// several lights on one surface should. The flare always ends inside the visual rim,
// even on the soft stock-like dome, so it cannot continue into the texture margin.
//
// The panel style can answer the same impact with mode 3 instead: the panels around
// the hit light white and the light walks outward ring by ring. Same data, same one
// quad per impact; only the shape of the answer differs, because a group of tiles
// cannot plausibly be struck by a painted wedge. Mode 4 is the third answer, a
// sector of the surface itself brightening, and it is available to every style.
//
// ---- the dissolve --------------------------------------------------------
//
// Modes 2 and 5 are the optional break animations. Vanilla's Shield_Break effecter
// flashes a texture and plays a sound, but the dome itself is gone on the very next
// frame: GetCurrentAlpha() collapses to zero because Active is false. A dissolve
// cannot be driven by that alpha, so these modes carry their own progress and their
// own brightness, supplied by ShieldDissolveLog from the four places the comp
// actually shuts down. The third break option is simply not drawing either mode,
// which leaves vanilla's one-frame disappearance intact.
//
// Mode 2 is the continuous tear: the surface is erased by a threshold crossing a
// scalar field. The field is the distance from the point of failure, perturbed by
// low-frequency noise, so the front splits into fingers instead of expanding as a
// clean circle. A hot line rides that front, and a finer noise ridge adds cracks to
// the material just behind it. Coordinates are normalised by the radius
// (p = d / radius), so a 2-cell bubble and a 25-cell gravship dome break into the
// same number of pieces rather than the large one simply turning into sand.
//
// Mode 5 is the panel shell's alternative: whole panels are lost instead of the
// surface being cut, because a set of discrete objects fails that way. It is chosen
// independently of the surface style, just like the hit animation.
//
// The square grid seen in the first in-game screenshot was a float32 precision
// failure in the lattice hash, not a property of this pattern — a float64 web
// preview looks correct. Hash21 below is hardened and the seed is kept small;
// the dissolve algorithm itself is untouched.
//
// Everything is still clipped by `edge` at r == 1 in the crisp and panel styles:
// even while falling apart, those shields never paint outside the circle they
// block. (The stock-like style is the deliberate exception described further down.)
//
// ---- the hexagonal shell (second style) ----------------------------------
//
// A hexagon grid in screen space is a flat honeycomb pasted onto a circle; the
// cells do not shrink near the silhouette, so it never reads as a dome. The
// alternative treats the disc as the orthographic projection of a hemisphere and
// lays a *spherical* tiling on it. Euler's formula makes twelve pentagons
// mandatory on any closed hexagon tiling — they are where the curvature goes, and
// ShieldHexMap bakes exactly that lattice (a subdivided icosahedron's Voronoi
// diagram) into one texture. Per fragment the cost is one texture sample plus a
// handful of arithmetic, instead of 42..162 dot products.
//
// Each cell is shaded flat from its own face normal, so the shell reads as panels
// rather than as a smooth sphere with a net drawn over it. The silhouette ridge is
// still the same DomeColor() term, so this style keeps the crisp edge and the exact
// intercept radius.
//
// A panel shell also has to *react* in panels, which is mode 3 and the panel-loss
// break in mode 5 below. Both need one thing the smooth style does not have: a per-cell
// identity. The baked map already carries it — RGB is the winning site's direction,
// constant inside a cell — so "which panel is this" costs nothing. Angular distance
// from the hit to that direction, divided by the cell centre spacing, counts hops
// across the lattice, and that is the unit both effects travel in. Because the map is
// the same texture the surface is drawn from, a wave can only ever light panels that
// exist, and it stays correct when the density changes.
//
// ---- the stock-like style (third style) ----------------------------------
//
// Vanilla paints a projector dome with Other/ForceField and a personal belt with
// Other/ShieldBubble: two different textures. The dome is one wide radial blur
// with a rim soft enough that its glow continues past the radius the game blocks
// at (which is why vanilla scales that quad by 1.1601562). The belt is a broader
// gradient that fills most of the disc before a bright edge appears, and then
// stops just outside it. Both are reproduced analytically below—VanillaDomeColor
// for the ForceField profile, StockBubbleDomeColor for the ShieldBubble one—never
// by sampling the game's textures.
//
// Neither is a variation of the crisp ridge. The crisp style asks "how thick is
// the edge"; these ask "how soft is the falloff". The dome keeps its middle
// nearly clear, with a small base fill so a selected shield is not a hollow ring;
// the belt keeps a visible haze so it reads as a filled sphere. Both express
// every dimension as a fraction of the radius rather than a screen-space
// derivative, so the same shield looks the same at any zoom. The stock surfaces
// are the only ones allowed past r == 1, and the renderer enlarges the quad for
// that.
//
// The break reuses the smooth style's noise-driven tear deliberately: there is no
// per-panel identity here, because there are no panels.
//
// Hit reactions are chosen independently of the surface (see the renderer): the
// painted flare (mode 1), the sector brightening (mode 4), or the panel wave (mode 3,
// which only means anything on the shell). Vanilla's cone is an additive *sector of
// the same dome*, so the stock-like style defaults to mode 4; the flare stays
// available for every surface and ends inside the visual rim so it cannot float
// outside a soft edge.

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
    vec4 _Params4; // @default = vec4(0.0, 0.0, 0.0, 0.0)      // centre x/z in world cells, unused
    vec4 _Params5; // @default = vec4(2.8, 1.2, 5.0, 0.3)      // ridge half px, core sigma px, glow exponent, drift
    vec4 _Params6; // @default = vec4(0.0, 0.22, 0.75, 9.0)    // style (0 crisp, 1 hex, 2 stock dome, 3 stock belt), hex face, hex line, SDF gradient floor (1.8*k)
    vec4 _Color;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
} u;

// RGB = winning site's direction (the flat face normal, constant inside a cell).
// A   = edgeDistance: 0 on a cell wall, 1 at its centre. No pentagon flag: the
//       twelve five-sided cells are just what a spherical lattice produces and are
//       deliberately not drawn any differently from the hexagons.
layout(set = 2, binding = 1) uniform sampler2D _HexMap; // @default = black

// Where to read the baked lattice for a disc position p.
//
// The bake writes row 0 at p.y == -1 (Unity's SetPixels lays rows out bottom-up), but
// the engine samples v with 0 at the TOP of the stored rows. So p.y has to be flipped
// before the lookup. This is invisible in the dome itself — a mirrored Voronoi is still
// a Voronoi, and the per-face lighting does not care — and shows up only where a value
// read from the map is compared against a world-space direction: the hit flash and the
// break origin then land on the opposite (vertically mirrored) side of the shield.
vec2 HexUv(vec2 p)
{
    return vec2(p.x, -p.y) * 0.5 + 0.5;
}

// Deterministic value noise. No texture, so the pattern cannot be stretched or
// tiled by a different plane10 UV orientation, and the seed is a uniform the CPU
// can change per break.
//
// float32-safe on purpose. The obvious one-liner
//     p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32);
// dies on the GPU: value noise feeds it *integer lattice indices*, and once those
// reach a few thousand, p * 456.21 is large enough that fract() has no fractional
// bits left to keep. Adjacent cells then hash to the same value and the noise
// collapses into square blocks. A float64 web preview cannot reproduce this.
// Wrapping to a small period before a small multiplier fixes it at the source:
// at |p| < 997, p * 0.1031 stays below 103, where float32 still has plenty of
// fractional bits. The period is deliberately larger than any lattice index this
// shader can reach (Fbm's fourth octave peaks around 190), so no octave wraps.
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

// The spherical cell pattern. Returns (flat face shade, cell line).
//
// Nothing here is a grid in screen space: the sampled lattice was projected onto
// the hemisphere before it was baked, so cells naturally compress and bend as they
// approach the silhouette. That compression is the pseudo-3D cue; the flat per-face
// shading is the other half.
vec2 HexSurface(vec2 p, float aa, float t)
{
    vec4 baked = texture(_HexMap, HexUv(p));

    // Constant inside a cell, so it doubles as a face normal and a per-cell id.
    vec3 face = baked.rgb * 2.0 - 1.0;

    // True Voronoi SDF: 0 on a cell wall, 1 at the cell centre.
    float e = baked.a;

    // Surface normal of the hemisphere this disc is standing in for. nz -> 0 at
    // the silhouette, exactly where a real dome turns away from the viewer.
    float nz = sqrt(max(1.0 - dot(p, p), 0.0));

    vec3 light = normalize(vec3(
        0.42 * cos(t * 0.13),
        0.42 * sin(t * 0.17),
        0.80));
    float diffuse = clamp(dot(face, light), 0.0, 1.0);
    float fresnel = 1.0 - nz;

    // Deliberately low contrast between neighbouring faces. A faint cell line
    // next to two differently-shaded panels reads as *two* bright edges welded
    // together; the line has to be unambiguously the brightest thing at a wall.
    // Hash21 is the float32-safe one above; face.xy stays inside [-1, 1].
    float jitter = Hash21(face.xy * 37.0) - 0.5;
    float shade = 0.42 + 0.40 * diffuse + 0.28 * fresnel + 0.05 * jitter;

    // The field gradient, analytically — NOT fwidth(e).
    //
    // e = clamp(angularDistance / eRef, 0, 1), and the distance is measured on
    // the unit hemisphere, so |grad_p e| = 1/(eRef * nz) exactly, where nz is the
    // Jacobian of the orthographic projection. That term is why a cell near the
    // silhouette carries a much steeper field than one at the centre.
    //
    // fwidth(e) was used here and failed in two opposite ways at once, which made
    // the drawn width depend on zoom: it collapses in the V-shaped valley along a
    // wall (neighbours sit on opposite slopes, so the derivative cancels), and it
    // explodes under minification, when one pixel spans several cells and e jumps
    // between 0 and 1. A constant floor cannot fix both, because the true
    // gradient varies by 1/nz across the dome. This does.
    //
    // _Params6.w is 1/eRef. 2.2 is the line width in screen pixels.
    float eGrad = u._Params6.w / max(nz, 0.12);
    float slope = aa * eGrad;

    // One cell spans 2*eRef in r here, i.e. 2*nz/(eRef*aa) pixels.
    float cellPixels = 2.0 * nz / max(u._Params6.w * aa, 1e-6);

    // Width as a SHARE of the cell, not a fixed pixel count.
    //
    // A constant 2.2 px was right physically and wrong visually: zoomed in, cells
    // grow and a 2.2 px line covers ~2% of a cell (reads as a hairline); zoomed
    // out, cells shrink and the same line covers ~15% (reads as thick grout).
    // Scaling with the cell keeps the *visual weight* constant, which is what a
    // lattice of panels should do.
    //
    // Clamped in absolute pixels at both ends. Below 1 px the line only aliases;
    // above 6 px a single cell stops reading as a panel and starts reading as a
    // frame. Inside the clamps the share is exact, so the two ends of the zoom
    // range look like the same drawing.
    float lineWidthPx = clamp(cellPixels * 0.055, 1.0, 6.0);
    float line = 1.0 - smoothstep(0.0, lineWidthPx * slope, e);

    // Below ~5 px per cell the lattice aliases into moire, so the lines fade while
    // the panel shading stays — a distant dome reads as a shell, not as a grid.
    line *= smoothstep(2.0, 5.0, cellPixels);

    return vec2(clamp(shade, 0.0, 1.5), line);
}

// ---- the two stock-like surfaces -----------------------------------------
//
// "Stock-like" is not one profile. Vanilla uses different textures for a
// projector dome (Other/ForceField) and a personal belt (Other/ShieldBubble),
// and the difference is visible: the dome is a narrow ring on a broad but faint
// haze, while the belt is a much wider gradient that only becomes a bright edge
// near the silhouette, so its interior reads as a filled sphere rather than a
// clear disc. Both are rebuilt analytically here; nothing is sampled from the
// game.
//
// _Params6.x selects the profile: 2 is the ForceField stock-like, 3 the
// ShieldBubble stock-like. A belt's regular stock choice is 3; a dome can pick
// either.

// The ShieldBubble profile: the belt's stock surface, also selectable on a dome.
//
// The reference is not a thin ring with haze behind it: its brightness climbs
// steadily from the centre, and the last tenth of the radius adds a bright edge
// on top of that climb. So the profile has two terms — a broad r^3.5 body that
// fills the sphere, and a gaussian rim on top — unlike the dome's rim plus r^3.5
// haze, where the body term is the faint one. The rim's inner shoulder is also
// twice the dome's (sigma 0.090R vs 0.045R), and the whole profile stops just
// past r == 1 instead of trailing through the quad margin.
vec4 StockBubbleDomeColor(float r)
{
    float rr = clamp(r, 0.0, 1.2);
    float d = rr - 1.0;

    // Short outer tail on purpose: the reference has no wide bloom outside its
    // bright edge. The inner sigma is where the body and rim blend together.
    float sigma = d < 0.0 ? 0.090 : 0.030;
    float rim = 0.55 * exp(-(d * d) / (2.0 * sigma * sigma));

    // The filled-sphere term. r^3.5 keeps the middle translucent while rising
    // steadily toward the edge; a pure gaussian rim left the 0.7..0.9R band too
    // dark and then jumped, which read as a ring rather than a bubble.
    float body = 0.35 * pow(clamp(r, 0.0, 1.0), 3.5);

    // A low gray floor. A belt bubble is translucent but never clear.
    float haze = 0.030 + 0.030 * pow(clamp(r, 0.0, 1.0), 1.5);

    // The stock bubble stops just past its bright edge. Unlike the dome's
    // ForceField there is no long outer bloom to fade through the quad margin:
    // 1.045 is where the reference has fallen to a few percent of the peak.
    float outer = 1.0 - smoothstep(1.0, 1.045, r);

    // The stock texture is white and takes the def colour directly, exactly like
    // the dome's: no white-hot core, and no separate tint for the edge.
    return vec4(u._Color.rgb, (rim + body + haze) * outer);
}

// The stock-like dome, as (tint.rgb, unmultiplied alpha).
//
// A narrow feathered highlight sits on the rim, with a long gaussian tail on both
// sides so the light appears to spill off the surface instead of being drawn on
// it. Inside that highlight the disc carries a broad gradient: the stock texture
// is nearly clear at the centre and fills in steadily toward the edge. Every width
// here is a fraction of the radius, not a pixel count, because the stock texture
// is stretched with the shield.
//
// There is no noise and no derivative-based width: the stock look is static and
// smooth, and both properties are what make it read as a soft glow rather than as
// material. The only motion it gets is the alpha pulse supplied by the caller.
vec4 VanillaDomeColor(float r)
{
    // The ShieldBubble profile is a different texture and a different shape; see
    // StockBubbleDomeColor above.
    if (u._Params6.x > 2.5)
    {
        return StockBubbleDomeColor(r);
    }

    // Slightly past the rim: the outer tail is meant to fade through r == 1.
    float rr = clamp(r, 0.0, 1.2);

    // One gaussian band, not two smoothsteps. Its tails are what the stock
    // texture's blur has and what a pair of smoothsteps does not: a slope that
    // keeps easing for a long way on both sides instead of starting at a visible
    // corner. The peak sits on the intercept radius, the inner shoulder is the
    // much wider of the two (the stock ring fades a long way inward before the
    // disc is clear), and both sigmas are fractions of the radius, so the band
    // keeps its proportion at every shield size and zoom.
    //
    // These numbers were fitted against the radial brightness profile of the
    // reference screenshot by Tools/shield_vanilla_compare.py: a very narrow
    // highlight (inner sigma 0.045R, outer 0.018R) on top of a long interior
    // gradient. A symmetric, wider ring looks plausible on its own but reads as a
    // glowing tube next to the stock shield, which is a narrow edge with a broad
    // body.
    float d = rr - 1.0;
    float sigma = d < 0.0 ? 0.045 : 0.018;
    float rim = 0.72 * exp(-(d * d) / (2.0 * sigma * sigma));

    // Interior haze: a base fill plus a rise toward the rim. Fitted to the same
    // profile (0.03 + 0.19 r^3.5): the stock disc is almost clear at the centre
    // and fills in steadily before the rim. The term clamps at r == 1, so beyond
    // the rim it becomes a flat glow faded out only by `edge` — the outer bloom.
    float haze = 0.03 + 0.19 * pow(clamp(r, 0.0, 1.0), 3.5);

    // The stock material applies the def colour straight to its white texture:
    // no white-hot core, and the haze and the rim share one tint. Doing anything
    // else here would make a modded shield's colour drift from vanilla's.
    return vec4(u._Color.rgb, rim + haze);
}

// The intact dome, as (tint.rgb, unmultiplied alpha). Shared by mode 0 and mode 2
// so a dissolving shield is literally the same surface with pieces removed, not a
// second effect that happens to look similar. The style switch lives here for the
// same reason: breaking a panel shell tears the same panels it was showing.
vec4 DomeColor(float r, vec2 p, float aa, float ang, float t)
{
    // The stock-like surface shares none of the ridge/core/glow machinery below
    // and must not compute a derivative-based width, so it answers first.
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

    // Only the interior fill changes between crisp and hexagonal. Ridge, core and
    // fresnel glow are identical, so the silhouette stays as crisp and as exactly
    // on the intercept radius whichever of those two is chosen. (The stock-like
    // style returned above and uses none of them.)
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

    float sum = ridgeTerm + coreTerm + glowTerm + bodyTerm;

    vec3 halo = u._Color.rgb;
    vec3 coreTint = mix(halo, vec3(1.0), 0.65);
    vec3 lineTint = mix(halo, vec3(1.0), 0.85);
    vec3 tint = (halo * (ridgeTerm + glowTerm + max(bodyTerm - hexLine, 0.0))
            + coreTint * coreTerm + lineTint * hexLine)
        / max(sum, 1e-4);

    return vec4(tint, sum * drift);
}

void main()
{
    vec2 d = vWorld - u._Params4.xy;
    float radius = max(u._Params.w, 0.01);

    // 1.0 exactly on the collision rim — the number the whole effect is built
    // around. The crisp and panel styles therefore scale their quad to exactly
    // 2 * radius; only the stock-like style adds vanilla's 1.1601562 margin, so
    // its soft tail has room to fade instead of being sliced off.
    float r = length(d) / radius;

    // Normalised disc coordinates, used by both the hexagonal style and the
    // dissolve. Radius-relative, so a 2-cell bubble and a 25-cell gravship dome
    // show the same number of panels and break into the same number of pieces.
    vec2 p = d / radius;

    // Unit direction from the centre. Guarded because the pixel under the exact
    // centre has none, and atan(0, 0) is undefined — the drift below would
    // propagate a NaN into the fragment before it could be discarded.
    vec2 n = d / max(length(d), 1e-4);

    // One pixel, expressed in units of r. Computed unconditionally, before any
    // branching, so the derivative is taken in uniform control flow. Every width
    // in this shader is a multiple of it.
    float aa = max(fwidth(r), 1e-4);

    // The silhouette. The crisp and panel styles use a one-pixel fade placed
    // *inside* r == 1, so the edge antialiases without the drawn shape ever
    // growing past what the shield actually blocks.
    //
    // The stock-like style is the one exception, and on purpose: its whole point is
    // a glow that continues past the collision radius the way vanilla's texture
    // does. Its falloff is measured in fractions of the radius and is deliberately
    // longer than the rim's own outer tail: the rim has already reached zero by the
    // time this fades, so it only tapers the haze that clamps past r == 1. It ends
    // before the enlarged quad edge, leaving no hard square behind. The renderer
    // scales that quad by vanilla's own 1.1601562 margin for this style only.
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
        // One quad per impact. Angular profile and radial falloff are both soft,
        // so two flares from neighbouring directions blend into a brighter patch
        // instead of stacking into an opaque slice. The stock-like style does not
        // use this branch; it uses mode 4, where the dome itself brightens.
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

        // Clip the flare to the visual rim, not to the dome's own edge. The
        // stock-like surface deliberately lets `edge` continue past r == 1, but a
        // wedge whose radial ramp is already saturated there would keep its full
        // strength through that margin — the detached band this style used to show.
        //
        // The stock-like rim is a soft band whose visible edge reaches a few percent
        // past the peak, so its flare fades through that band instead of being cut
        // exactly at 1.0 (which would slice a chord across the bright ring). 1.05R
        // is the outer half of the rim in the reference profile. The crisp and panel
        // surfaces still end at r == 1 with one pixel of AA, because that is their
        // silhouette contract.
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

        // Continuous dissolve: the surface tears along a noise-driven front. This is
        // the "Dissolve" break option on every surface. The panel shell has a second
        // option (mode 5) that loses whole panels; choosing this one on the shell
        // cuts the same soft hole a continuous surface would, which is exactly the
        // difference the player is choosing between.
        //
        // Tear front: distance from the point that failed, perturbed by
        // low-frequency noise so it splits into fingers instead of sweeping across
        // as a clean circle.
        float noise = Fbm(p * 3.5 + vec2(seed, seed * 1.37));
        float spread = length(p - originDir) * 0.5;
        float field = clamp(spread + (noise - 0.5) * 0.42, 0.0, 1.0);

        float threshold = progress * 1.15 - 0.15;

        // One pixel of tear edge at any zoom.
        float fw = max(fwidth(field), 0.006);
        float remaining = smoothstep(threshold - fw, threshold + fw, field);

        // White-hot line riding the tear. Only on the surviving side — the dead
        // side must stay empty or the dissolve reads as a bright overlay rather
        // than as material being removed.
        float front = exp(-pow((field - threshold) / (fw * 3.0 + 0.022), 2.0))
            * remaining;

        // Cracks: ridges of a finer noise over the strip of surface that is
        // about to go. Zero at progress 0 so the shield does not come pre-cracked.
        //
        // This is the original look, restored. The nested square rings in the
        // first in-game screenshot were NOT this pattern: they were a float32
        // hash collapse in Hash21 feeding the same lattice index to many cells.
        // A float64 web preview could not show it.
        float crackNoise = ValueNoise(p * 19.0 + vec2(seed * 2.1, seed * 0.7));
        float crackRidge = 1.0 - smoothstep(0.0, 0.10, abs(crackNoise - 0.5));
        float nearFront =
            (1.0 - smoothstep(0.0, 0.26, field - threshold)) * remaining;
        float crack = crackRidge * nearFront * smoothstep(0.0, 0.06, progress);

        // Only `remaining` scales the surface. An earlier version also hollowed each
        // panel toward its seams (0.55 at the walls), which dimmed the whole shield
        // that was still standing - the surviving half looked like it had lost power
        // rather than like the other half was coming apart. The seams are already
        // readable from the dome's own line term.
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
    else if (mode < 3.5)
    {
        // ---- hexagonal impact flash ---------------------------------------
        //
        // Mode 3, the panel style's answer to a hit. The smooth style paints a wedge
        // on the surface because a surface is what it is; a set of tiles is struck at
        // the tiles, so the ones around the impact light up white and the light then
        // walks outward ring by ring, fading as it goes.
        //
        // The quantisation is free: the hex map already carries a per-cell id, so the
        // flash is automatically made of panels without any mask, and it can only
        // light panels that exist. One quad per impact, same count as the flare it
        // replaces.
        vec2 od = u._Params3.xy;
        vec2 originDir = od / max(length(od), 1e-4);
        float progress = clamp(u._Params3.z, 0.0, 1.0);

        // _Params3.w is a SHARE of the run from a point to the far rim, not a hop
        // count, and it is converted to hops here. A hop is one panel spacing, so a
        // fixed hop count is not a fixed visual size: at k=12 a panel is 2.4x smaller
        // than at k=5, and four hops covered a fifth of the area - a dense shield
        // barely reacted to being hit while a sparse one flashed across half the dome.
        // Anchoring the reach to the dome instead makes the flash the same size on
        // screen at every density, with more (smaller) panels taking part.
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

        // The front is a position in hops, so it covers the same number of panels per
        // second on every shield.
        //
        // The offset is what makes the hit visible on the first frame. 0.8 hops is the
        // floor: the panel nearest the impact sits up to ~0.5 hops away (half a cell
        // spacing) plus up to 0.15 of stagger, so anything smaller left progress 0
        // completely dark and the flash faded in instead of striking.
        //
        // It also grows with the reach, because one panel is not a fixed size: on a
        // dense lattice the struck panel alone is a speck, so the opening flash takes a
        // small share of the reach with it. max() rather than a fixed share keeps the
        // reference density exactly as it was - there 0.8 hops is already the larger
        // term.
        float front = max(0.8, reach * 0.12) + progress * reach;
        float since = front - order;

        // Brightness falls with hops BEHIND the front, which is the same thing as
        // falling with the time since that panel lit. That equivalence is the whole
        // effect: it makes a travelling wave with a fading tail.
        //
        // Without the distance term (a plain exp on time, 0.4, was tried) every panel
        // in range sat at full brightness as the front passed, so the result was a
        // bright disc that kept growing instead of a ring that faded: the outer edge
        // never looked dimmer than the middle because it was not dimmer.
        //
        // Measured in units of the reach, not in hops, for the same reason the reach
        // itself is: 3.2 is 0.8 per hop at the density this was tuned at, and keeping
        // it in reach units means the ring is the same angular width at k=2 and k=12.
        // In hops it would have been a fat band on a sparse shield and a hairline on a
        // dense one.
        float behind = since / max(reach, 1e-4);
        float lit = smoothstep(0.0, 0.025, behind) * exp(-max(behind, 0.0) * 3.2);

        // The last ring would otherwise still be at full strength on the final frame
        // and vanish with the draw. Fade the whole effect out instead.
        lit *= 1.0 - smoothstep(0.70, 1.0, progress);

        // Panels, not pixels: the interior lights and the seams stay dim, so the
        // flash is visibly made of tiles.
        float panel = 0.6 + 0.4 * smoothstep(0.0, 0.25, e);

        vec3 hot = mix(u._Color.rgb, vec3(1.0), 0.9);
        outColor = vec4(hot, lit * panel * u._Params.y * edge);
    }
    else if (mode < 4.5)
    {
        // ---- brighten impact: the dome itself lights up in a sector -------
        //
        // Mode 4, selectable for every surface. Vanilla's ForceFieldCone is not a
        // separate wedge laid over the shield: it is an additive sector of the same
        // soft surface. What the eye sees is the shield inside that sector becoming
        // more opaque, with the sector's outer edge following the chosen surface's
        // silhouette instead of crossing it. The stock-like style defaults to this;
        // the crisp and panel surfaces can select it as well.
        //
        // So the extra light here is the dome's own shade and silhouette, masked to
        // the impact's angular sector. Rim and haze brighten together because they
        // are the same surface. Nothing about the radial profile is re-drawn: it is
        // the dome's alpha multiplied by the sector, including the stock dome's own
        // soft `edge`. That is what keeps the hit attached to the visual rim — an
        // independent radial ramp (the old flare) reached outward at full strength
        // and read as a wedge hovering outside the soft edge.
        float cosHalf = u._Params3.z;
        float axis = clamp(
            (dot(n, u._Params3.xy) - cosHalf) / max(1.0 - cosHalf, 1e-3),
            0.0,
            1.0);

        // Mostly flat inside the sector, feathered only near its two sides. A long
        // ramp (mode 1's 0.8) makes a beam with a bright axis; vanilla's cone reads
        // as a region that lit up, not as a searchlight.
        float sector = smoothstep(0.0, 0.45, axis);

        vec4 dome = DomeColor(r, p, aa, ang, t);

        // Radial contraction. Adding the dome's full soft edge doubles the outer
        // half of the rim as well, which makes the struck sector of the stock-like
        // shield look thicker than the rest of it. Vanilla's cone does not widen the
        // band: its extra light is concentrated on the rim and stops at the visual
        // peak. So the stock-like surface fades the extra out over 1.0..1.04R; the
        // crisp and panel surfaces already have a one-pixel edge and use it as is.
        float brightenEdge;
        if (u._Params6.x > 1.5)
        {
            brightenEdge = 1.0 - smoothstep(1.0, 1.04, r);
        }
        else
        {
            brightenEdge = edge;
        }

        // The whole sector brightens, but the extra light thins toward the centre
        // and reaches zero only at the centre. A constant full-strength wedge reads
        // as a separate object; cutting it off at 0.5R was the opposite problem — it
        // left almost nothing visible. The full-radius ramp keeps the interior
        // readable while still leaving the centre untouched.
        float innerFade = smoothstep(0.0, 1.0, r);

        // Additive, exactly like the dome: the same surface written a second time
        // inside the sector. Strong hits and overlapping directions accumulate.
        outColor = vec4(
            dome.rgb,
            dome.a * sector * innerFade * u._Params.y * brightenEdge);
    }
    else
    {
        // ---- hex break: lose panels ---------------------------------------
        //
        // Mode 5, the panel shell's own break option. A continuous surface tears
        // along a noise front (mode 2); a shell of discrete objects instead *loses
        // panels*: each is entirely there or entirely gone, and the wave walks
        // outward through the lattice. Running the noise front over the panels
        // would cut a soft hole through them, which reads as a rendering mistake
        // rather than as structural failure.
        vec2 od = u._Params3.xy;
        vec2 originDir = od / max(length(od), 1e-4);
        float progress = clamp(u._Params3.z, 0.0, 1.0);

        vec4 dome = DomeColor(r, p, aa, ang, t);
        vec4 baked = texture(_HexMap, HexUv(p));
        vec3 face = baked.rgb * 2.0 - 1.0;

        // Distance in panels. The baked lattice is uniform in solid angle, so
        // angular distance divided by the centre spacing counts hops; that is what
        // makes the wave cross the same number of panels per second on a 3-cell
        // bubble and on a 25-cell gravship dome.
        float cellStep = 2.0 / max(u._Params6.w, 1e-4);
        vec3 originOnSphere = vec3(
            originDir,
            sqrt(max(1.0 - dot(originDir, originDir), 0.0)));
        float hops = acos(clamp(dot(face, originOnSphere), -1.0, 1.0)) / cellStep;

        // Per-panel stagger so the front is a ragged collection of tiles instead
        // of a perfect ring. Hash21 of the face is the same per-cell id the
        // shading jitter uses, so a panel keeps one identity while it dies.
        float order = hops + Hash21(face.xy * 53.0) * 0.45;

        // Enough hops to cross the hemisphere at this density, plus a margin so
        // progress 1 really does leave nothing. Scales with k: a denser lattice
        // has more panels between the break point and the far rim.
        float maxHops = 3.14159265 * u._Params6.w * 0.5;
        float span = maxHops + 1.5;

        // When this panel lets go, as a fraction of the whole animation. 0.90 is
        // not arbitrary: the fade below lasts 0.09, so the last panel finishes just
        // as the clock runs out instead of being cut off mid-transition.
        float start = (order + 0.6) / (span + 1.2) * 0.90;

        // dt is the panel's own clock: negative while intact, 0 at the moment it
        // starts to give way, 1 once it is gone. The 9% fade is the same width the
        // continuous tear uses: about two panels in transition at any moment.
        float dt = (progress - start) * (1.0 / 0.09);
        float remaining = 1.0 - smoothstep(0.0, 1.0, dt);

        // Panels do brighten as they fail, but only a little and only briefly: a
        // strong surge turns the transition band into a white stripe travelling
        // across the shield, and a wide one brightens a whole region at once.
        float dying = exp(-dt * dt * 6.0) * remaining * 0.3;

        // No cracks: a crack belongs to a continuous surface. Here the panel
        // seams already are the cracks.
        float baseA = dome.a * remaining;
        float frontA = dying * 0.75;
        float surge = 1.0 + 0.55 * exp(-progress * 9.0);
        float endFade = 1.0 - smoothstep(0.85, 1.0, progress);

        vec3 hot = mix(u._Color.rgb, vec3(1.0), 0.88);
        float total = baseA + frontA;
        vec3 tint = (dome.rgb * baseA + hot * frontA) / max(total, 1e-4);

        outColor = vec4(tint, total * u._Params.y * edge * surge * endFade);
    }
}
