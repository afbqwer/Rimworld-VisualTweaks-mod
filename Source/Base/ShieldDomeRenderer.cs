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
        ///
        /// Shared with <see cref="PersonalShieldRenderer"/>: it uses the same
        /// stock-like surface, so it needs the same margin for the same reason.
        /// </summary>
        internal const float VanillaQuadMargin = 1.1601562f;

        // Vanilla's ForceFieldCone is a fixed-width sector: only its alpha fades,
        // its angular extent does not follow damage. The brighten reaction keeps
        // that fixed width (the stock value is 30 degrees, adjustable in settings).
        // The painted flare still uses the damage-driven width, because a wedge that
        // has to point is a different cue.

        private static readonly float Altitude =
            Altitudes.AltitudeFor(AltitudeLayer.MoteOverhead);

        private static readonly int ParamsId = Shader.PropertyToID("_Params");
        private static readonly int Params3Id = Shader.PropertyToID("_Params3");
        private static readonly int Params4Id = Shader.PropertyToID("_Params4");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        // Reused every frame; DrawPropertyBlock wraps these arrays without allocating,
        // and the engine copies the span into its arena during the DrawMesh call, so
        // one array can back every draw in the loop.
        private static readonly DrawProperty[] properties = new DrawProperty[4];

        // The dome's own material. The belt has a second instance in
        // PersonalShieldRenderer so each can bind its own hex atlas.
        private static readonly ShieldDomeMaterial Surface =
            new ShieldDomeMaterial(ShieldHexTarget.Large);

        /// <summary>
        /// True when the panel style is what the player actually sees: the style is
        /// selected and this target's lattice texture exists. Both the style uniform
        /// and the choice of hit reaction go through this, so the two can never
        /// disagree — answering a hit with tiles while the dome draws the crisp style
        /// would flash panels that are not on screen.
        /// </summary>
        internal static bool HexActive => Surface.HexActive;

        /// <summary>
        /// Returns false when nothing was drawn, so the caller can hand the frame back
        /// to vanilla instead of skipping it. Every early exit here is a reason the
        /// stock drawing is the better answer, and the calling patch treats false as
        /// exactly that — "I did not draw, so you draw".
        ///
        /// The vanilla projector adapter. Everything the draw needs is read from the
        /// interceptor here and handed to <see cref="DrawCore"/>; a second adapter
        /// (VefRepulsorShieldCompat) supplies the same numbers from VEF's shield-field
        /// comp, so the shader path itself has no knowledge of either comp type.
        /// </summary>
        internal static bool Draw(CompProjectileInterceptor comp, float alpha)
        {
            if (comp == null)
            {
                return false;
            }

            ThingWithComps? parent = comp.parent;
            if (parent == null)
            {
                return false;
            }

            return DrawCore(
                comp,
                parent,
                comp.Props.radius,
                comp.Props.color,
                alpha,
                comp.Active,
                Find.Selector.IsSelected(parent));
        }

        /// <summary>
        /// The VEF repulsor adapter. A repulsor is a building with its own shield
        /// comp, and VEF's own draw has no alpha of its own: opacity lives in the
        /// def's health colour. The adapter resolves that colour and alpha, and
        /// <paramref name="present"/> says whether the stock draw would be up at all
        /// (active and holding energy), because a dissolve must outlive that state.
        /// </summary>
        internal static bool Draw(
            ThingComp comp,
            Thing host,
            float radius,
            float alpha,
            Color color,
            bool present)
        {
            return DrawCore(comp, host, radius, color, alpha, present, selected: false);
        }

        private static bool DrawCore(
            ThingComp comp,
            Thing host,
            float radius,
            Color baseColor,
            float alpha,
            bool present,
            bool selected)
        {
            if (!ShieldDomeBridge.ShaderReady)
            {
                return false;
            }

            if (host == null || !host.Spawned)
            {
                return false;
            }

            if (radius <= 0.01f)
            {
                return false;
            }

            Vector3 center = host.DrawPos;

            // The up edge owns the restore animation. Observed here rather than from a
            // tick patch because this is the one place that already knows what "up"
            // means for both adapters: Active for an interceptor, active plus charge
            // for a repulsor. See ShieldRestoreLog.
            ShieldRestoreLog.Observe(
                comp,
                present,
                Mathf.RoundToInt(VisualTweaksSettings.largeShieldRestoreTicks));

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
            // The break animation is per surface now. Vanilla means "do not draw a
            // custom break at all", which restores the one-frame disappearance;
            // Dissolve is the continuous tear; HexDissipate is the panel loss and
            // needs the baked lattice, so a missing texture falls back to the tear.
            ShieldBreakStyle breakStyle = VisualTweaksSettings.CurrentShieldBreakStyle;
            bool customBreak = breakStyle != ShieldBreakStyle.Vanilla;
            float dissolveMode = breakStyle
                == ShieldBreakStyle.HexDissipate && HexActive
                ? 5f
                : 2f;

            ShieldDissolveLog.State? dissolve = null;
            float dissolveProgress = 0f;
            if (customBreak)
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
                if (present)
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

            // ------------------------------------------------------------------
            // Restore.
            //
            // The opposite edge of the dissolve: a shield that just became available
            // again fades in, and Grow additionally inflates it from a fraction of
            // its radius. The alpha still comes from vanilla, so this only scales a
            // surface that vanilla would already show — an unselected player shield
            // stays hidden through its own alpha rather than being forced visible.
            // A break in flight owns the frame, so no restore is applied to it.
            // ------------------------------------------------------------------
            ShieldRestoreStyle restoreStyle = VisualTweaksSettings.largeShieldRestoreStyle;
            float restoreProgress = -1f;
            if (dissolve == null && restoreStyle != ShieldRestoreStyle.None)
            {
                float? progress = ShieldRestoreLog.Progress(comp);
                if (progress.HasValue)
                {
                    restoreProgress = progress.Value;
                }
            }

            float restoreAlpha = 1f;
            float drawnRadius = radius;
            if (restoreProgress >= 0f)
            {
                float eased = ShieldRestoreLog.Ease(restoreProgress);
                restoreAlpha = eased;
                if (restoreStyle == ShieldRestoreStyle.Grow)
                {
                    drawnRadius = radius * Mathf.Lerp(
                        ShieldRestoreLog.GrowStartScale,
                        1f,
                        eased);
                }
            }

            // 2 * radius normally, with no texture-margin factor: the shader puts the
            // silhouette on r == 1.0, so the quad edge and the gameplay radius are the
            // same circle. The stock-like style is the exception — its soft rim is
            // supposed to fade through r == 1 the way vanilla's does, so it gets the
            // same 1.1601562 margin vanilla uses for Other/ForceField.
            float quadMargin =
                VisualTweaksSettings.shieldDomeStyle == ShieldDomeStyle.Vanilla
                    || VisualTweaksSettings.shieldDomeStyle == ShieldDomeStyle.Bubble
                    ? VanillaQuadMargin
                    : 1f;
            Matrix4x4 matrix = default;
            matrix.SetTRS(
                new Vector3(center.x, Altitude, center.z),
                Quaternion.identity,
                new Vector3(
                    drawnRadius * 2f * quadMargin,
                    1f,
                    drawnRadius * 2f * quadMargin));

            // Nothing is submitted while the surface is fully transparent, no break
            // is in flight and the last recorded hit has already faded. Deciding that
            // before touching the material keeps a hidden shield down to a few field
            // reads and one lookup per frame instead of an Ensure/ApplyLook pass.
            if (dissolve == null
                && alpha <= 0f
                && !ShieldImpactLog.HasLive(comp))
            {
                return false;
            }

            Material? mat = Surface.Ensure();
            if (mat == null)
            {
                return false;
            }

            Surface.ApplyLook();

            // Vanilla paints a selected-but-inactive shield dark grey so "this is off"
            // is visible at a glance. A shield that is actively tearing itself apart is
            // emitting light, not sitting idle, so it keeps the def's colour. The VEF
            // adapter never asks for this dimming: a repulsor that is off is not drawn
            // by VEF at all, so there is no "selected but idle" surface to recolour.
            Color color = dissolve == null && !present && selected
                ? InactiveColor
                : baseColor;

            NVector4 tint = new NVector4(color.r, color.g, color.b, 1f);
            NVector4 centerParam = new NVector4(center.x, center.z, 0f, 0f);
            float time = Time.realtimeSinceStartup;
            bool drew = false;
            bool restoring = dissolve == null && restoreProgress >= 0f;

            try
            {
                // Hit reactions belong to a shield that is still standing. Once a
                // dissolve is in flight the break animation owns the frame: the hex
                // wave in particular can still be mid-spread when the shield fails,
                // and letting it run over the tear reads as a second, longer animation.
                // The personal belt already behaves this way (its dissolve returns
                // before DrawImpact), so this also keeps the two families aligned.
                // A restoring dome suppresses them for the same reason: a full-strength
                // flare on a surface that is still fading in reads as a second object.
                if (dissolve == null && !restoring)
                {
                    // Which reaction is a player choice now, but not every surface can
                    // draw every one: the lattice wave needs the baked hex map, while the
                    // flare and the sector brightening are analytic. Each dome style keeps
                    // its own choice in the settings. A hex wave selected while the map is
                    // unavailable falls back to the flare, matching the dome's own fallback
                    // to the crisp surface in that case.
                    ShieldImpactStyle impactStyle =
                        VisualTweaksSettings.CurrentShieldImpactStyle;
                    if (impactStyle == ShieldImpactStyle.HexWave && !HexActive)
                    {
                        impactStyle = ShieldImpactStyle.Flare;
                    }

                    if (impactStyle == ShieldImpactStyle.HexWave)
                    {
                        drew |= DrawHexImpacts(
                            comp, mat, matrix, tint, centerParam, drawnRadius, time);
                    }
                    else
                    {
                        drew |= DrawFlares(
                            comp,
                            mat,
                            matrix,
                            tint,
                            centerParam,
                            drawnRadius,
                            time,
                            impactStyle == ShieldImpactStyle.Brighten);
                    }
                }

                if (dissolve != null)
                {
                    // Mode 2 or 5. Both break animations are driven by the same
                    // clock: _Params3 switches meaning from an impact's direction to
                    // the break's origin / progress / seed, so no extra uniform is
                    // needed for the choice between them.
                    properties[0] = DrawProperty.Vector(
                        ParamsId,
                        new NVector4(
                            dissolveMode,
                            VisualTweaksSettings.largeShieldDissolveBrightness,
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
                        ParamsId,
                        new NVector4(
                            0f,
                            alpha * restoreAlpha,
                            time,
                            drawnRadius));
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
            ThingComp comp,
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
            ThingComp comp,
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

                // Brighten is vanilla's fixed cone: 30 degrees regardless of damage.
                // The painted flare keeps the damage-driven width, resolved per impact
                // through the same curve the rim ripple uses, so a heavy hit is visibly
                // wider than a pistol round while both stay inside the clamps.
                float cosHalfAngle;
                if (brighten)
                {
                    cosHalfAngle = Mathf.Cos(
                        0.5f * VisualTweaksSettings.shieldBrightenAngle * Mathf.Deg2Rad);
                }
                else
                {
                    float footprint = ShieldImpactFootprint.FlareCells(
                        impacts[i].Damage,
                        radius);
                    cosHalfAngle = Mathf.Cos(Mathf.Atan2(footprint, radius));
                }

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

    }
}
