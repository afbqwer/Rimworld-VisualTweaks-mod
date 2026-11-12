using System;
using RimWorld;
using UnityEngine;
using Verse;
using DrawProperty = Waylith.Rendering.DrawProperty;
using DrawPropertyBlock = Waylith.Rendering.DrawPropertyBlock;
using NVector4 = System.Numerics.Vector4;

namespace VisualTweaks
{
    /// <summary>
    /// Draws the custom dome for personal shield belts, replacing
    /// <c>CompShield.Draw()</c>.
    ///
    /// What is ported from <see cref="ShieldDomeRenderer"/> is the same three surface
    /// types, the same ridge and face/line weights, a material built from the same
    /// code, and the same dissolve / panel-loss break animations. The surface choice
    /// itself is independent — a belt can draw crisp while the dome draws hex — and so
    /// are the lattice density, opacity and hit reaction.
    ///
    /// The hit reaction is a belt-specific choice: vanilla's jitter, the surface
    /// brightening in the struck sector, or the lattice wave. The flare is not offered
    /// — a painted wedge needs a readable direction, and at one character wide it is a
    /// few pixels pointed at nothing. Brightening and the wave reuse the dome shader's
    /// modes 4 and 3 unchanged; only the slider values differ, because the same
    /// numbers do not read the same way at this size.
    ///
    /// What has to differ is geometry and visibility, because a belt is not a
    /// projector:
    ///
    /// - The radius is not a def constant. Vanilla lerps the bubble's *diameter*
    ///   from <c>minDrawSize</c> to <c>maxDrawSize</c> with the stored energy, so the
    ///   dome grows as the belt recharges. Reproducing that keeps the bubble's size
    ///   honest during the recharge cycle.
    /// - Vanilla only paints a belt when <c>ShouldDisplay</c> says the situation is
    ///   relevant (drafted, hostile, recently hit, ...). That property is protected,
    ///   so it is bound once as a delegate and called rather than copied; a future
    ///   change to the rule then moves both drawings or neither.
    /// - Alpha is a player setting, not 1.0. A belt has no idle pulse or "selected"
    ///   brightness ramp to borrow, and drawing it at full shader alpha made it far
    ///   more solid than any nearby interceptor dome, whose own alpha comes from
    ///   GetCurrentAlpha() and sits mostly between 0.2 and 0.62. The default tracks
    ///   that range; the two kinds remain independently adjustable.
    ///
    /// Small size is not special-cased. Every shape constant in the shader is either
    /// radius-relative (glow, stock-like rim, dissolve noise) or expressed in pixels
    /// (ridge, hex line width), so a 1.2-cell bubble gets the same drawn weight as a
    /// 25-cell dome; only its lattice density is chosen separately. Those lines fade
    /// on their own when a cell drops below the shader's 5-pixel legibility floor.
    ///
    /// One material for belts, one draw per belt plus at most one additive hit quad.
    /// The material is separate from the dome's only because the hex atlas is a
    /// material texture and the two atlases have different densities and resolutions;
    /// every look constant is still read from the same global settings. Everything
    /// that differs per belt (centre, radius, alpha, break progress, hit bearing)
    /// travels as a draw property.
    /// </summary>
    internal static class PersonalShieldRenderer
    {
        private static readonly float Altitude =
            Altitudes.AltitudeFor(AltitudeLayer.MoteOverhead);

        private static readonly int ParamsId = Shader.PropertyToID("_Params");
        private static readonly int Params3Id = Shader.PropertyToID("_Params3");
        private static readonly int Params4Id = Shader.PropertyToID("_Params4");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        // Reused every frame. DrawPropertyBlock wraps the span without allocating,
        // and the engine copies it into its arena during the DrawMesh call.
        private static readonly DrawProperty[] properties = new DrawProperty[4];

        // A second material, not the dome's: the baked hex atlas is a material
        // texture, and a belt's lattice has its own density and resolution. All other
        // look constants still come from the same settings.
        private static readonly ShieldDomeMaterial Surface =
            new ShieldDomeMaterial(ShieldHexTarget.Personal);

        /// <summary>
        /// Returns true when a dome was submitted, so the patch can skip vanilla's
        /// drawing. False means "nothing to draw here" and the stock method runs; its
        /// own conditions decide whether that produces a bubble.
        /// </summary>
        internal static bool Draw(
            CompShield comp,
            Func<CompShield, bool> shouldDisplay)
        {
            if (!ShieldDomeBridge.ShouldReplace(comp))
            {
                return false;
            }

            Pawn? pawn = comp.parent is Apparel apparel
                ? apparel.Wearer
                : comp.parent as Pawn;
            // The break animation is allowed to outlive the hit, but not the pawn:
            // a corpse keeps no bubble, which is also what ShouldDisplay says.
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Drawer == null)
            {
                return false;
            }

            Material? mat = Surface.Ensure();
            if (mat == null)
            {
                return false;
            }

            // Shared ridge/face/line weights, but this material resolves the belt's
            // own surface style and lattice independently of the dome's.
            Surface.ApplyLook();

            int breakStyle = VisualTweaksSettings.CurrentShieldBreakStyleFor(
                ShieldHexTarget.Personal);
            int hitStyle = VisualTweaksSettings.personalShieldHitStyle;
            bool customBreak = breakStyle
                != VisualTweaksSettings.ShieldBreakVanilla;
            float dissolveMode = breakStyle
                == VisualTweaksSettings.ShieldBreakHexDissipate
                && Surface.HexActive
                ? 5f
                : 2f;

            PersonalShieldDissolveLog.State? dissolve = null;
            float dissolveProgress = 0f;
            if (customBreak)
            {
                dissolve = PersonalShieldDissolveLog.Progress(
                    comp, out dissolveProgress);
            }

            // A belt that recharges inside the animation is a working shield again.
            // Do not keep painting a torn dome over it. Active alone is not enough:
            // the state can still read Active on the frame the break started, so wait
            // until a frame has actually seen it down.
            if (dissolve != null)
            {
                if (comp.ShieldState == ShieldState.Active)
                {
                    if (dissolve.SawInactive)
                    {
                        PersonalShieldDissolveLog.Cancel(comp);
                        dissolve = null;
                    }
                }
                else
                {
                    dissolve.SawInactive = true;
                }
            }

            if (dissolve == null)
            {
                // Same order as vanilla's Draw(): state first, then the visibility
                // rule. Calling ShouldDisplay for an inactive belt would run code
                // vanilla never runs there — for example a wild animal's shield can
                // have no faction, which that property dereferences.
                if (comp.ShieldState != ShieldState.Active
                    || !shouldDisplay(comp))
                {
                    return false;
                }
            }

            // Only the jitter effect moves the bubble. Brighten and the lattice wave
            // are additive quads drawn over the same centred surface, so they must not
            // also displace it. The dissolve owns the frame when one is playing, so no
            // hit reaction is layered on top of it either.
            Vector3 jitterOffset = Vector3.zero;
            float jitterShrink = 0f;
            if (dissolve == null
                && hitStyle == VisualTweaksSettings.PersonalShieldHitJitter)
            {
                TryGetJitter(comp, out jitterOffset, out jitterShrink);
            }

            // Vanilla's own sizing rule: the drawn diameter grows with the stored
            // energy, from minDrawSize to maxDrawSize, and it uses the raw energy on
            // purpose (an energy stat above 1 is allowed to overshoot just as it does
            // in vanilla). The shader wants a radius, so halve it.
            float size = Mathf.Lerp(
                comp.Props.minDrawSize,
                comp.Props.maxDrawSize,
                comp.Energy) - jitterShrink;
            float radius = Mathf.Max(size * 0.5f, 0.05f);

            Vector3 center = pawn.Drawer.DrawPos + jitterOffset;

            // Crisp and panel silhouettes sit exactly on r == 1, so their quad is
            // exactly the diameter. The stock-like style's soft tail is meant to fade
            // past the rim, so it gets vanilla's own ForceField margin for that tail.
            float quadMargin = VisualTweaksSettings.IsVanillaShieldDome(
                ShieldHexTarget.Personal)
                ? ShieldDomeRenderer.VanillaQuadMargin
                : 1f;

            Matrix4x4 matrix = default;
            matrix.SetTRS(
                new Vector3(center.x, Altitude, center.z),
                Quaternion.identity,
                new Vector3(
                    radius * 2f * quadMargin,
                    1f,
                    radius * 2f * quadMargin));

            NVector4 tint = new NVector4(1f, 1f, 1f, 1f);
            NVector4 centerParam = new NVector4(center.x, center.z, 0f, 0f);
            float time = Time.realtimeSinceStartup;

            if (dissolve != null)
            {
                // Mode 2 or 5. Both break animations share the same clock; _Params3
                // switches meaning from a hit bearing to origin / progress / seed.
                properties[0] = DrawProperty.Vector(
                    ParamsId,
                    new NVector4(
                        dissolveMode,
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
                return true;
            }

            // Additive light first, surface second — the same order the projector
            // dome uses, so the rim is the last thing written. Jitter has already
            // moved the surface itself, so that branch is a no-op here.
            DrawImpact(comp, mat, matrix, tint, centerParam, radius, time, hitStyle);

            // The same role GetCurrentAlpha() plays for an interceptor dome. It is
            // independent of that dome's value on purpose: a belt is always shown at
            // one steady level, and the default sits in the middle of the range a
            // selected dome pulses through.
            float alpha = Mathf.Clamp(
                VisualTweaksSettings.personalShieldOpacity,
                VisualTweaksSettings.MinPersonalShieldOpacity,
                VisualTweaksSettings.MaxPersonalShieldOpacity);

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
            return true;
        }

        /// <summary>
        /// Draws the selected hit reaction as one additive quad, in mode 4 (the
        /// surface brightens in the struck sector) or mode 3 (the lattice wave). Both
        /// modes already exist in the dome shader unchanged; this only supplies the
        /// hit's bearing, its progress through the configured duration, and the
        /// belt's own strength values.
        ///
        /// Hex wave falls back to brightening when the belt's lattice could not be
        /// baked: mode 3 would sample a black texture and draw nothing, which is
        /// worse than a reaction that does not need an asset. The two effects share
        /// one clock so the player tunes a single duration.
        /// </summary>
        private static bool DrawImpact(
            CompShield comp,
            Material material,
            Matrix4x4 matrix,
            NVector4 tint,
            NVector4 centerParam,
            float radius,
            float time,
            int hitStyle)
        {
            // Jitter moves the surface instead of drawing a quad, and an unknown
            // style is safer as no extra draw than as a wrong one.
            if (hitStyle != VisualTweaksSettings.PersonalShieldHitBrighten
                && hitStyle != VisualTweaksSettings.PersonalShieldHitHexWave)
            {
                return false;
            }

            TickManager? tickManager = Find.TickManager;
            if (tickManager == null
                || !PersonalShieldHitLog.TryGet(comp, out int hitTick, out float angle))
            {
                return false;
            }

            int duration = Mathf.Max(
                1,
                Mathf.RoundToInt(VisualTweaksSettings.personalShieldJitterTicks));
            int age = tickManager.TicksGame - hitTick;
            if (age < 0 || age >= duration)
            {
                return false;
            }

            float progress = (float)age / duration;
            float life = 1f - progress;

            // The stored bearing runs along the shot, matching vanilla's
            // impactAngleVect. The light belongs on the side the round came from, so
            // the shader's direction is its negation — the same convention the dome
            // uses for its impacts.
            Vector3 toward = Vector3Utility.FromAngleFlat(angle);
            float dx = -toward.x;
            float dz = -toward.z;

            bool hexSpread = hitStyle == VisualTweaksSettings.PersonalShieldHitHexWave
                && ShieldHexMap.Available(ShieldHexTarget.Personal);

            if (hexSpread)
            {
                float strength = Mathf.Clamp(
                    VisualTweaksSettings.personalShieldHexImpactStrength,
                    VisualTweaksSettings.MinPersonalShieldHexImpactStrength,
                    VisualTweaksSettings.MaxPersonalShieldHexImpactStrength);
                if (strength < 0.004f)
                {
                    return false;
                }

                float share = Mathf.Clamp(
                    VisualTweaksSettings.personalShieldHexImpactShare,
                    VisualTweaksSettings.MinPersonalShieldHexImpactShare,
                    VisualTweaksSettings.MaxPersonalShieldHexImpactShare);

                properties[0] = DrawProperty.Vector(
                    ParamsId, new NVector4(3f, strength, time, radius));
                properties[1] = DrawProperty.Vector(
                    Params3Id, new NVector4(dx, dz, progress, share));
            }
            else
            {
                float alpha = Mathf.Clamp(
                    VisualTweaksSettings.personalShieldBrightenStrength,
                    VisualTweaksSettings.MinPersonalShieldBrightenStrength,
                    VisualTweaksSettings.MaxPersonalShieldBrightenStrength)
                    * life * life;
                if (alpha < 0.004f)
                {
                    return false;
                }

                float cosHalf = Mathf.Cos(
                    0.5f * Mathf.Clamp(
                        VisualTweaksSettings.personalShieldBrightenAngle,
                        VisualTweaksSettings.MinPersonalShieldBrightenAngle,
                        VisualTweaksSettings.MaxPersonalShieldBrightenAngle)
                    * Mathf.Deg2Rad);

                properties[0] = DrawProperty.Vector(
                    ParamsId, new NVector4(4f, alpha, time, radius));
                properties[1] = DrawProperty.Vector(
                    Params3Id, new NVector4(dx, dz, cosHalf, 0f));
            }

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
            return true;
        }

        /// <summary>
        /// The offset and size reduction for the current hit, or zero when no hit is
        /// live. Reproduces vanilla's formula — full strength on the hit tick, then a
        /// linear fade to zero — with the amplitude and duration made configurable.
        ///
        /// The offset uses <c>HorizontalVectorFromAngle</c>, the exact helper vanilla
        /// uses for <c>impactAngleVect</c>, so the push follows the same convention as
        /// the stock bubble rather than an intuitively reversed one.
        /// </summary>
        private static bool TryGetJitter(
            CompShield comp,
            out Vector3 offset,
            out float shrink)
        {
            offset = Vector3.zero;
            shrink = 0f;

            if (VisualTweaksSettings.personalShieldHitStyle
                != VisualTweaksSettings.PersonalShieldHitJitter)
            {
                return false;
            }

            TickManager? tickManager = Find.TickManager;
            if (tickManager == null
                || !PersonalShieldHitLog.TryGet(comp, out int hitTick, out float angle))
            {
                return false;
            }

            int duration = Mathf.Max(
                1,
                Mathf.RoundToInt(VisualTweaksSettings.personalShieldJitterTicks));
            int age = tickManager.TicksGame - hitTick;
            if (age < 0 || age >= duration)
            {
                return false;
            }

            float fade = (float)(duration - age) / duration;
            float amount = VisualTweaksSettings.personalShieldJitterDistance * fade;
            if (amount <= 0f)
            {
                return false;
            }

            offset = Vector3Utility.HorizontalVectorFromAngle(angle) * amount;
            shrink = amount;
            return true;
        }
    }
}
