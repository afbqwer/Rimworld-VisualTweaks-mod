using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using DrawProperty = Waylith.Rendering.DrawProperty;
using DrawPropertyBlock = Waylith.Rendering.DrawPropertyBlock;
using NVector4 = System.Numerics.Vector4;

namespace VisualTweaks
{
    /// <summary>
    /// Draws the custom shield dome, and one impact quad per recorded impact, in
    /// place of vanilla's two quads.
    ///
    /// Called from the <c>PostDraw</c> prefix, i.e. from inside the thing draw pass at
    /// the shield's own position. That is deliberate: it inherits vanilla's visibility
    /// gating (spawned, dynamic-draw list, altitude layer) instead of rebuilding it, and
    /// it keeps the shield's place in the transparent queue without a second
    /// registration path to keep in sync.
    ///
    /// One material, one draw for the dome plus one per live impact. Everything that
    /// differs per draw (alpha, radius, colour, bearing) travels as a draw property;
    /// only the look constants live on the material, so the number of shields changes
    /// the number of draw calls and nothing else.
    ///
    /// No instancing. Vanilla already submits one quad per shield per frame and shields
    /// are counted in ones and twos per map; batching would cost a compute buffer, a
    /// per-instance parameter layout and a second code path for a saving that cannot be
    /// measured here. The ripple renderer batches because hits arrive in bursts of
    /// dozens, which is a different problem.
    /// </summary>
    internal static class ShieldDomeRenderer
    {
        /// <summary>
        /// Copied from the ShaderDef so the material does not depend on it sticking.
        /// 3151 is vanilla's MoteGlow queue; the shield must not draw over fog of war.
        /// </summary>
        private const int RenderQueue = 3151;

        // ------------------------------------------------------------------
        // Dome look.
        //
        // Four weights and four shape numbers. The split is deliberate: the weights
        // say *how much* each term contributes, the shape numbers say *what shape* it
        // has. Tuning one should not silently move the other.
        //
        // Two of them are player settings (ridge weight and ridge half-width) and are
        // applied in ApplyLook; the rest are constants, because a slider for "fresnel
        // exponent" is a slider nobody can reason about. Everything exposed is read
        // every frame, so dragging a slider on a paused, selected shield shows the
        // change immediately.
        // ------------------------------------------------------------------
        private const float CoreWeight = 0.30f;
        private const float GlowWeight = 0.30f;
        private const float BodyWeight = 0.05f;

        /// <summary>Gaussian sigma of the hot core, in screen pixels.</summary>
        private const float CoreSigmaPixels = 1.2f;

        /// <summary>
        /// Fresnel exponent. 3 reads like a flat pane of light, 10 like a tight
        /// bubble; 5 keeps a broad glow without eating the interior.
        /// </summary>
        private const float GlowExponent = 5f;

        /// <summary>Angular brightness drift on the rim. 0 freezes it.</summary>
        private const float DriftWeight = 0.30f;

        // ------------------------------------------------------------------
        // Flare look.
        // ------------------------------------------------------------------

        /// <summary>
        /// How far the flare reaches inward, as a fraction of the radius — a radial
        /// quantity, deliberately independent of the angular width.
        ///
        /// Deriving depth from the width was a mistake with a specific failure: depth
        /// then equals width times tan(half-angle), so the narrower the flare the shorter
        /// it gets, and on a large shield (5 degrees) it collapsed into a notch at the
        /// rim. Width answers "how wide is the beam", depth answers "how far into the
        /// dome does it reach"; nothing ties the two.
        ///
        /// There is no ceiling. One was tried — 6 cells, to stop the 25-cell gravship
        /// generator from laying a long wedge over everything standing under it — and it
        /// caused the mirror-image complaint: 6 cells is 24% of a 25-cell radius, so the
        /// streak sat at the rim looking like a nick. Proportion is the thing that reads
        /// consistently at every size, so the fraction is the whole rule; only the floor
        /// survives, because a 2-cell shield would otherwise get a sub-cell stub.
        /// </summary>
        private const float FlareDepthShare = 0.78f;

        private const float FlareMinDepthCells = 2.5f;

        /// <summary>
        /// Damage that maps to a damage factor of exactly 1.0 — a vanilla bullet in the
        /// 10-15 band, same reference the shield ripple uses.
        /// </summary>
        private const float FlareReferenceDamage = 12f;

        /// <summary>
        /// The two clamps that make the damage response safe. Without them a 200-damage
        /// charge blast is 17x a pistol round, which saturates the whole dome white and
        /// stops reading as a direction; and a 3-damage spit round rounds to nothing.
        /// The response is sub-linear via the influence exponent, then clamped.
        /// </summary>
        private const float FlareMinDamageFactor = 0.55f;

        private const float FlareMaxDamageFactor = 1.8f;

        // ------------------------------------------------------------------
        // Panel-style hit reaction.
        // ------------------------------------------------------------------


        /// <summary>Vanilla's own colour for an inactive shield that is selected.</summary>
        private static readonly Color InactiveColor = new Color(0.2f, 0.2f, 0.2f);

        /// <summary>
        /// The quad margin vanilla uses for Other/ForceField. Its soft rim is drawn
        /// past the radius the game blocks at, and the stock quad is enlarged by this
        /// factor to give that glow somewhere to land. The stock-like style needs the
        /// same room for the same reason; the crisp and panel styles must NOT use it,
        /// because their whole contract is that the silhouette sits on r == 1.
        /// </summary>
        private const float VanillaQuadMargin = 1.1601562f;

        private static readonly float Altitude =
            Altitudes.AltitudeFor(AltitudeLayer.MoteOverhead);

        private static readonly int ParamsId = Shader.PropertyToID("_Params");
        private static readonly int Params2Id = Shader.PropertyToID("_Params2");
        private static readonly int Params3Id = Shader.PropertyToID("_Params3");
        private static readonly int Params4Id = Shader.PropertyToID("_Params4");
        private static readonly int Params5Id = Shader.PropertyToID("_Params5");
        private static readonly int Params6Id = Shader.PropertyToID("_Params6");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        // Reused every frame; DrawPropertyBlock wraps these arrays without allocating,
        // and the engine copies the span into its arena during the DrawMesh call, so
        // one array can back every draw in the loop.
        private static readonly DrawProperty[] properties = new DrawProperty[4];

        private static Material? material;
        private static bool materialFailed;

        // What the material currently carries, so a slider drag uploads a few
        // vectors instead of a few vectors per shield per frame.
        private static float appliedRidgeWidth = float.NaN;
        private static float appliedRidgeBrightness = float.NaN;
        private static float appliedDomeStyle = float.NaN;
        private static float appliedHexFace = float.NaN;
        private static float appliedHexLine = float.NaN;
        private static int appliedHexDensity = -1;

        /// <summary>
        /// True when the panel style is what the player actually sees: the style is
        /// selected and the lattice texture exists. Both the style uniform and the
        /// choice of hit reaction go through this, so the two can never disagree —
        /// answering a hit with tiles while the dome draws the crisp style would flash
        /// panels that are not on screen.
        /// </summary>
        private static bool HexActive =>
            VisualTweaksSettings.HexShieldDome && ShieldHexMap.Available;

        /// <summary>
        /// True when the stock-like style is chosen. Unlike <see cref="HexActive"/>
        /// it has no external asset to fail, so it needs no availability check.
        /// </summary>
        private static bool VanillaActive => VisualTweaksSettings.VanillaShieldDome;

        /// <summary>
        /// Returns false when nothing was drawn, so the caller can hand the frame back
        /// to vanilla instead of skipping it. Every early exit here is a reason the
        /// stock drawing is the better answer, and the calling patch treats false as
        /// exactly that — "I did not draw, so you draw".
        /// </summary>
        internal static bool Draw(CompProjectileInterceptor comp, float alpha)
        {
            if (!ShieldDomeBridge.ShaderReady || materialFailed)
            {
                return false;
            }

            ThingWithComps? parent = comp.parent;
            if (parent == null || !parent.Spawned)
            {
                return false;
            }

            float radius = comp.Props.radius;
            if (radius <= 0.01f)
            {
                return false;
            }

            Material? mat = EnsureMaterial();
            if (mat == null)
            {
                return false;
            }

            ApplyLook(mat);

            Vector3 center = parent.DrawPos;

            // 2 * radius normally, with no texture-margin factor: the shader puts the
            // silhouette on r == 1.0, so the quad edge and the gameplay radius are the
            // same circle. The stock-like style is the exception — its soft rim is
            // supposed to fade through r == 1 the way vanilla's does, so it gets the
            // same 1.1601562 margin vanilla uses for Other/ForceField.
            float quadMargin = VanillaActive ? VanillaQuadMargin : 1f;
            Matrix4x4 matrix = default;
            matrix.SetTRS(
                new Vector3(center.x, Altitude, center.z),
                Quaternion.identity,
                new Vector3(
                    radius * 2f * quadMargin,
                    1f,
                    radius * 2f * quadMargin));

            // ------------------------------------------------------------------
            // Dissolve.
            //
            // A broken shield has no alpha of its own: vanilla's GetCurrentAlpha()
            // falls to zero with Active, so the surface has to carry its own clock
            // and its own brightness for the few ticks it takes to fall apart. The
            // patch starts that clock at the four real shutdown points; here it is
            // only read. Progress advances on TicksGame, so it freezes on pause and
            // speeds up with game speed like a vanilla mote.
            // ------------------------------------------------------------------
            ShieldDissolveLog.State? dissolve = null;
            float dissolveProgress = 0f;
            if (VisualTweaksSettings.shieldDissolveEnabled)
            {
                dissolve = ShieldDissolveLog.Progress(comp, out dissolveProgress);
            }

            // A generator that recharges mid-animation is a working shield again.
            // Do not keep painting a dome that is still disintegrating over it.
            // Active alone is not enough here: Deactivate() leaves it true for the
            // rest of the tick, so wait until we have actually seen the shield go
            // down before treating a true value as a reactivation.
            if (dissolve != null)
            {
                if (comp.Active)
                {
                    if (dissolve.SawInactive)
                    {
                        ShieldDissolveLog.Cancel(comp);
                        dissolve = null;
                    }
                }
                else
                {
                    dissolve.SawInactive = true;
                }
            }

            // Vanilla paints a selected-but-inactive shield dark grey so "this is off"
            // is visible at a glance. A shield that is actively tearing itself apart is
            // emitting light, not sitting idle, so it keeps the def's colour.
            Color color = dissolve == null
                && !comp.Active
                && Find.Selector.IsSelected(parent)
                ? InactiveColor
                : comp.Props.color;

            NVector4 tint = new NVector4(color.r, color.g, color.b, 1f);
            NVector4 centerParam = new NVector4(center.x, center.z, 0f, 0f);
            float time = Time.realtimeSinceStartup;
            bool drew = false;

            try
            {
                // Hit reactions first: they are additive, so order does not change
                // the picture, but submitting them under the dome keeps the rim the
                // last thing written to the surface. A burst that was still fading
                // when the shield broke should keep fading, not vanish with it.
                //
                // Which reaction is a player choice now, but not every surface can
                // draw every one: the lattice wave needs the baked hex map, while the
                // flare and the sector brightening are analytic. Each dome style keeps
                // its own choice in the settings. A hex wave selected while the map is
                // unavailable falls back to the flare, matching the dome's own fallback
                // to the crisp surface in that case.
                int impactStyle = VisualTweaksSettings.CurrentShieldImpactStyle;
                if (impactStyle == VisualTweaksSettings.ShieldImpactHexWave && !HexActive)
                {
                    impactStyle = VisualTweaksSettings.ShieldImpactFlare;
                }

                if (impactStyle == VisualTweaksSettings.ShieldImpactHexWave)
                {
                    drew |= DrawHexImpacts(
                        comp, mat, matrix, tint, centerParam, radius, time);
                }
                else
                {
                    drew |= DrawFlares(
                        comp,
                        mat,
                        matrix,
                        tint,
                        centerParam,
                        radius,
                        time,
                        impactStyle == VisualTweaksSettings.ShieldImpactBrighten);
                }

                if (dissolve != null)
                {
                    // Mode 2. _Params3 switches meaning from an impact's direction to
                    // the tear's origin / progress / seed; no extra uniform is needed.
                    properties[0] = DrawProperty.Vector(
                        ParamsId,
                        new NVector4(
                            2f,
                            VisualTweaksSettings.shieldDissolveBrightness,
                            time,
                            radius));
                    properties[1] = DrawProperty.Vector(
                        Params3Id,
                        new NVector4(
                            dissolve.OriginX,
                            dissolve.OriginZ,
                            dissolveProgress,
                            dissolve.Seed));
                    properties[2] = DrawProperty.Vector(Params4Id, centerParam);
                    properties[3] = DrawProperty.Color(ColorId, tint);

                    Graphics.DrawMesh(
                        MeshPool.plane10,
                        matrix,
                        mat,
                        0,
                        null,
                        0,
                        new DrawPropertyBlock(properties));
                    drew = true;
                }
                else if (alpha > 0f)
                {
                    properties[0] = DrawProperty.Vector(
                        ParamsId, new NVector4(0f, alpha, time, radius));
                    properties[1] = DrawProperty.Vector(Params4Id, centerParam);
                    properties[2] = DrawProperty.Color(ColorId, tint);

                    Graphics.DrawMesh(
                        MeshPool.plane10,
                        matrix,
                        mat,
                        0,
                        null,
                        0,
                        new DrawPropertyBlock(properties.AsSpan(0, 3)));
                    drew = true;
                }
            }
            catch (Exception ex)
            {
                // Hands the frame back to vanilla permanently: the bridge gate is read
                // by the patch before it decides to skip the original.
                ShieldDomeBridge.DisableRuntime(ex.ToString());
                return false;
            }

            return drew;
        }

        /// <summary>
        /// Mode 3: the panel style's hit reaction. One quad per impact, exactly like the
        /// flare it replaces — the shader turns each into a wave across the lattice using
        /// the per-cell identity already baked into the hex map.
        ///
        /// Everything the shader needs is already a uniform: the direction arrives the
        /// same way as for a flare, and the age is normalised to 0..1 here so the
        /// lifetime can be tuned on the CPU without touching the shader.
        /// </summary>
        private static bool DrawHexImpacts(
            CompProjectileInterceptor comp,
            Material material,
            Matrix4x4 matrix,
            NVector4 tint,
            NVector4 centerParam,
            float radius,
            float time)
        {
            List<ShieldImpactLog.Impact>? impacts = ShieldImpactLog.Peek(comp);
            TickManager? tickManager = Find.TickManager;
            if (impacts == null || impacts.Count == 0 || tickManager == null)
            {
                return false;
            }

            int now = tickManager.TicksGame;
            bool drew = false;

            for (int i = impacts.Count - 1; i >= 0; i--)
            {
                int age = now - impacts[i].Tick;
                if (age < 0 || age >= ShieldImpactLog.LifetimeTicks)
                {
                    continue;
                }

                // Its own strength, not the flare's: the two effects have no common
                // scale, and a shared slider meant a value tuned for a wedge had to be
                // multiplied by a fudge factor to look right on a lit tile.
                float strength = Mathf.Clamp01(
                    VisualTweaksSettings.shieldHexImpactStrength
                    * DamageFactor(impacts[i].Damage));
                if (strength < 0.004f)
                {
                    continue;
                }

                float progress = (float)age / ShieldImpactLog.LifetimeTicks;

                // Same convention as the flare: the stored angle points from the
                // projectile towards the shield, so the reaction has to look the other
                // way to sit where the round actually hit.
                Vector3 toward = Vector3Utility.FromAngleFlat(impacts[i].Angle);
                NVector4 direction = new NVector4(
                    -toward.x,
                    -toward.z,
                    progress,
                    VisualTweaksSettings.shieldHexImpactShare);

                properties[0] = DrawProperty.Vector(
                    ParamsId, new NVector4(3f, strength, time, radius));
                properties[1] = DrawProperty.Vector(Params3Id, direction);
                properties[2] = DrawProperty.Vector(Params4Id, centerParam);
                properties[3] = DrawProperty.Color(ColorId, tint);

                Graphics.DrawMesh(
                    MeshPool.plane10,
                    matrix,
                    material,
                    0,
                    null,
                    0,
                    new DrawPropertyBlock(properties));

                drew = true;
            }

            return drew;
        }

        /// <summary>
        /// Draws one quad per live impact. Mode 1 is the painted flare; mode 4 brightens
        /// the dome itself in the impact's angular sector. The geometry, the per-impact
        /// alpha and the angular width are identical, so the two reactions spend the
        /// same amount of light — only the shader's interpretation differs. Both are
        /// available to the crisp and stock-like surfaces; the panel shell uses this
        /// path only when the player selects Brighten there.
        /// </summary>
        private static bool DrawFlares(
            CompProjectileInterceptor comp,
            Material material,
            Matrix4x4 matrix,
            NVector4 tint,
            NVector4 centerParam,
            float radius,
            float time,
            bool brighten)
        {
            List<ShieldImpactLog.Impact>? impacts = ShieldImpactLog.Peek(comp);
            TickManager? tickManager = Find.TickManager;
            if (impacts == null || impacts.Count == 0 || tickManager == null)
            {
                return false;
            }

            int now = tickManager.TicksGame;
            bool drew = false;

            // Depth depends only on the shield, so it is resolved once. Width does not:
            // it follows each impact's damage.
            float depth = Mathf.Max(radius * FlareDepthShare, FlareMinDepthCells);
            float innerRadius = Mathf.Clamp(1f - depth / radius, 0.05f, 0.9f);

            for (int i = impacts.Count - 1; i >= 0; i--)
            {
                int age = now - impacts[i].Tick;
                if (age < 0 || age >= ShieldImpactLog.LifetimeTicks)
                {
                    continue;
                }

                // Squared falloff: the flare snaps on at full strength and gives up
                // most of its brightness in the first third, which is how a flash
                // behaves. A linear fade reads as a slow dimming slice.
                float life = 1f - (float)age / ShieldImpactLog.LifetimeTicks;
                float flareAlpha = VisualTweaksSettings.shieldFlareAlpha
                    * DamageFactor(impacts[i].Damage)
                    * life
                    * life;
                if (flareAlpha < 0.004f)
                {
                    continue;
                }

                // Width from the impact's damage, through the same curve the rim ripple
                // uses but with the flare's own tuning and its own size slider. Resolved
                // per impact, so a heavy hit is visibly wider than a pistol round while
                // both stay inside the clamps.
                float footprint = ShieldImpactFootprint.FlareCells(
                    impacts[i].Damage,
                    radius);
                float cosHalfAngle = Mathf.Cos(Mathf.Atan2(footprint, radius));

                // The stored angle is the bearing from the projectile towards the
                // shield centre, so the flare has to look the other way to point at
                // the shooter.
                Vector3 toward = Vector3Utility.FromAngleFlat(impacts[i].Angle);
                NVector4 direction = new NVector4(
                    -toward.x,
                    -toward.z,
                    cosHalfAngle,
                    innerRadius);

                properties[0] = DrawProperty.Vector(
                    ParamsId,
                    new NVector4(
                        brighten ? 4f : 1f,
                        flareAlpha,
                        time,
                        radius));
                properties[1] = DrawProperty.Vector(Params3Id, direction);
                properties[2] = DrawProperty.Vector(Params4Id, centerParam);
                properties[3] = DrawProperty.Color(ColorId, tint);

                Graphics.DrawMesh(
                    MeshPool.plane10,
                    matrix,
                    material,
                    0,
                    null,
                    0,
                    new DrawPropertyBlock(properties));

                drew = true;
            }

            return drew;
        }

        /// <summary>
        /// How much harder a hit looks, from the damage it would have dealt.
        ///
        /// Sub-linear on purpose: damage spans two orders of magnitude, so a linear
        /// response would make small arms invisible and turn one charge blast into a
        /// screen-filling wedge. The exponent is the player's "damage influence" slider
        /// — 0 makes every hit identical, 1 is linear before the clamps — and the
        /// clamps are absolute: 0.55x to 1.8x, whatever the weapon.
        /// </summary>
        private static float DamageFactor(float damage)
        {
            float influence = Mathf.Clamp01(VisualTweaksSettings.shieldFlareDamageInfluence);
            if (influence <= 0f)
            {
                return 1f;
            }

            // Floor at 1 damage so a zero-damage projectile (EMP carries none of its
            // own) cannot collapse the response or produce pow(0, x).
            float ratio = Mathf.Max(damage, 1f) / FlareReferenceDamage;
            return Mathf.Clamp(
                Mathf.Pow(ratio, influence),
                FlareMinDamageFactor,
                FlareMaxDamageFactor);
        }

        /// <summary>
        /// Uploads the two vectors the player can move. Called every frame, but only
        /// writes when a value actually changed: the settings are read live, and the
        /// alternative — a per-draw property for each look constant — would put two
        /// extra vectors on every shield every frame for numbers that change once a
        /// session.
        /// </summary>
        private static void ApplyLook(Material mat)
        {
            float width = VisualTweaksSettings.shieldRidgeWidth;
            float brightness = VisualTweaksSettings.shieldRidgeBrightness;
            float face = VisualTweaksSettings.shieldHexFaceStrength;
            float line = VisualTweaksSettings.shieldHexLineStrength;

            // The spherical lattice is baked once and shared by every shield. Bind is
            // cheap to call every frame: it only re-bakes when the baked density
            // changes and only calls SetTexture for a new material.
            //
            // The APPLIED density, not the slider: the slider is a draft that only
            // reaches the texture when the player presses the rebake button, and the
            // shader's gradient constant has to describe the texture that actually
            // exists, not the one being previewed in a UI.
            int density = VisualTweaksSettings.shieldHexDensityApplied;
            bool wantHex = VisualTweaksSettings.HexShieldDome;
            if (wantHex)
            {
                ShieldHexMap.Bind(mat, density);
            }

            // A failed bake must not produce a black disc. Falling back to the
            // smooth dome keeps the shield readable and matches the shader bridge's
            // "lose the effect, never the feature" rule. The stock-like style has no
            // asset to fail, so it is passed through unconditionally.
            float style = VisualTweaksSettings.VanillaShieldDome
                ? 2f
                : HexActive ? 1f : 0f;

            if (width == appliedRidgeWidth
                && brightness == appliedRidgeBrightness
                && style == appliedDomeStyle
                && face == appliedHexFace
                && line == appliedHexLine
                && density == appliedHexDensity)
            {
                return;
            }

            appliedRidgeWidth = width;
            appliedRidgeBrightness = brightness;
            appliedDomeStyle = style;
            appliedHexFace = face;
            appliedHexLine = line;
            appliedHexDensity = density;

            // _Params2 = (ridge, core, glow, body) weights.
            mat.SetVector(
                Params2Id,
                new Vector4(brightness, CoreWeight, GlowWeight, BodyWeight));

            // _Params5 = (ridge half px, core sigma px, glow exponent, drift).
            mat.SetVector(
                Params5Id,
                new Vector4(width, CoreSigmaPixels, GlowExponent, DriftWeight));

            // _Params6.w is 1/eRef, the reciprocal of the angular distance that
            // maps to edgeDistance 1.0. The shader multiplies it by 1/z (the
            // orthographic Jacobian) to get the exact field gradient, so line
            // width is 2.2 px at every zoom and every shield size.
            mat.SetVector(
                Params6Id,
                new Vector4(
                    style,
                    face,
                    line,
                    density / ShieldHexMap.ERefPerSubdivision));
        }

        private static Material? EnsureMaterial()
        {
            if (material != null || materialFailed)
            {
                return material;
            }

            ShaderDef? def = ShieldDomeBridge.DomeShaderDef;
            if (def == null)
            {
                materialFailed = true;
                return null;
            }

            try
            {
                Material created = new Material((Shader)def)
                {
                    name = "VisualTweaksShieldDomeMat",
                    renderQueue = RenderQueue,
                };

                material = created;

                // Force the first upload even if the settings happen to equal the
                // defaults these fields were initialised with.
                appliedRidgeWidth = float.NaN;
                appliedRidgeBrightness = float.NaN;
                appliedDomeStyle = float.NaN;
                appliedHexFace = float.NaN;
                appliedHexLine = float.NaN;
                appliedHexDensity = -1;
                ApplyLook(created);
            }
            catch (Exception ex)
            {
                materialFailed = true;

                // Drop the half-built material: the caller treats null as "do not
                // draw" and hands the frame back to vanilla, which is the right answer
                // for a material whose look constants were never uploaded.
                material = null;
                Log.Error("[Visual Tweaks] Shield dome material could not be created;"
                    + " stock drawing kept: " + ex);
            }

            return material;
        }
    }
}
