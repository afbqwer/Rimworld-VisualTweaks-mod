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
    /// Draws the custom dome for personal shields: vanilla shield belts
    /// (<c>CompShield.Draw()</c>), VEF's pawn shield bubbles
    /// (<c>CompShieldBubble</c>) and VEF's hediff-granted shields
    /// (<c>HediffComp_Shield</c>).
    ///
    /// What is ported from <see cref="ShieldDomeRenderer"/> is the same three surface
    /// types, the same ridge and face/line weights, a material built from the same
    /// code, and the same dissolve / panel-loss break animations. The stock-like type
    /// resolves to the matching stock profile for the target: the ForceField ring for
    /// a dome's Vanilla choice, the ShieldBubble bubble for a belt (also selectable on
    /// a dome as Bubble), because vanilla uses different textures for the two. The
    /// surface choice itself is independent — a belt can draw crisp while the dome
    /// draws hex — and so are the lattice density, opacity and hit reaction.
    ///
    /// The hit reaction is a personal-specific choice: vanilla's jitter, the surface
    /// brightening in the struck sector, or the lattice wave. The flare is not offered
    /// — a painted wedge needs a readable direction, and at one character wide it is a
    /// few pixels pointed at nothing. Brightening and the wave reuse the dome shader's
    /// modes 4 and 3 unchanged; only the slider values differ, because the same
    /// numbers do not read the same way at this size.
    ///
    /// What has to differ is geometry and visibility, because a personal shield is not
    /// a projector:
    ///
    /// - The radius is not a def constant. Vanilla lerps the bubble's *diameter*
    ///   from <c>minDrawSize</c> to <c>maxDrawSize</c> with the stored energy, so the
    ///   dome grows as the belt recharges. VEF's bubble uses its own def's
    ///   <c>minShieldSize</c>/<c>maxShieldSize</c> and raw energy the same way.
    ///   Reproducing that keeps the bubble's size honest during the recharge cycle.
    /// - Both comps only paint when their own rule says the situation is relevant
    ///   (drafted, hostile, recently hit, ...). Vanilla's is a protected property, so
    ///   it is bound once as a delegate and called rather than copied; VEF's lives in
    ///   its draw method, so the adapter reproduces that one.
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
    /// One material for personal shields, one draw per shield plus at most one
    /// additive hit quad. The material is separate from the dome's only because the
    /// hex atlas is a material texture and the two atlases have different densities
    /// and resolutions; every look constant is still read from the same global
    /// settings. Everything that differs per shield (centre, radius, tint, alpha,
    /// break progress, hit bearing) travels as a draw property.
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
        ///
        /// The vanilla belt adapter. It resolves the pawn, checks the stock
        /// visibility rule and reads vanilla's energy-driven size; the shader path
        /// below is shared with VEF's shield-bubble comp, which supplies the same
        /// numbers from its own def.
        /// </summary>
        internal static bool Draw(
            CompShield comp,
            Func<CompShield, bool> shouldDisplay)
        {
            // The caller (PersonalShieldPatches.DrawFor) has already applied
            // ShieldDomeBridge.ShouldReplace and latched any fatal draw failure.
            // Checking the gate again here would only repeat the settings and
            // screenshot checks on every belt every frame.
            Pawn? pawn = comp.parent is Apparel apparel
                ? apparel.Wearer
                : comp.parent as Pawn;
            // The break animation is allowed to outlive the hit, but not the pawn:
            // a corpse keeps no bubble, which is also what ShouldDisplay says.
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Drawer == null)
            {
                return false;
            }

            // Same order as vanilla's Draw(): state first, then the visibility rule.
            // The short-circuit matters — ShouldDisplay dereferences state that only
            // makes sense for an active shield, which is why vanilla never calls it
            // for an inactive one.
            bool active = comp.ShieldState == ShieldState.Active;
            bool visible = active && shouldDisplay(comp);

            // Vanilla's own sizing rule: the drawn diameter grows with the stored
            // energy, from minDrawSize to maxDrawSize, and it uses the raw energy on
            // purpose (an energy stat above 1 is allowed to overshoot just as it does
            // in vanilla).
            float size = Mathf.Lerp(
                comp.Props.minDrawSize,
                comp.Props.maxDrawSize,
                comp.Energy);

            return DrawCore(comp, pawn.Drawer.DrawPos, size, active, visible, Color.white);
        }

        /// <summary>
        /// The VEF shield-bubble adapter. The caller has already resolved the pawn,
        /// the comp's "is it up" state and the bubble's own visibility rule, because
        /// those live on a comp type this assembly cannot reference. VEF sizes its
        /// bubble from the def's min/max shield size and the raw energy — not from a
        /// normalized charge — so the dome occupies exactly the footprint the stock
        /// billboard would.
        ///
        /// <paramref name="active"/> and <paramref name="visible"/> are separate on
        /// purpose: a dissolve must be cancelled as soon as the shield recharges, even
        /// if the stock visibility rule still hides it.
        /// </summary>
        internal static bool Draw(
            ThingComp comp,
            Pawn pawn,
            float drawnSize,
            bool active,
            bool visible,
            Color color)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Drawer == null)
            {
                return false;
            }

            return DrawCore(comp, pawn.Drawer.DrawPos, drawnSize, active, visible, color);
        }

        /// <summary>
        /// The VEF hediff-shield adapter. Its comp is not a ThingComp and it is drawn
        /// from <c>Pawn.DrawAt</c>, so the caller supplies the exact draw position and
        /// the def's own graphic offset instead of a pawn whose Drawer is consulted.
        /// </summary>
        internal static bool Draw(
            object comp,
            Vector3 center,
            float drawnSize,
            bool active,
            bool visible,
            Color color)
        {
            return DrawCore(comp, center, drawnSize, active, visible, color);
        }

        private static bool DrawCore(
            object comp,
            Vector3 baseCenter,
            float drawnSize,
            bool active,
            bool visible,
            Color baseColor)
        {
            // The up edge owns the restore animation, for every adapter that reaches
            // this core: vanilla belts, VEF bubbles and VEF hediff shields. The
            // adapters already distinguish "available" from "visible", so the
            // animation follows the shield state rather than whether the stock rule
            // happens to paint it right now.
            int restoreTicks = Mathf.RoundToInt(
                VisualTweaksSettings.personalShieldRestoreTicks);
            ShieldRestoreLog.Observe(comp, active, restoreTicks);

            ShieldBreakStyle breakStyle =
                VisualTweaksSettings.CurrentShieldBreakStyleFor(
                    ShieldHexTarget.Personal);
            PersonalShieldHitStyle hitStyle =
                VisualTweaksSettings.personalShieldHitStyle;
            ShieldRestoreStyle restoreStyle =
                VisualTweaksSettings.personalShieldRestoreStyle;
            bool customBreak = breakStyle != ShieldBreakStyle.Vanilla;
            float dissolveMode = breakStyle
                == ShieldBreakStyle.HexDissipate
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

            int now = Find.TickManager?.TicksGame ?? 0;

            // A break is only ever *drawn* while the shield is down. On the tick a
            // break begins the state can still read Active for the rest of that tick;
            // that tick is skipped, not canceled, and the tear starts on the next one.
            //
            // On any later tick, active means the belt is back. If it returned within
            // InstantRecoveryTicks the whole thing happened too fast to read — several
            // mods break and restore a shield in one motion — so neither the tear nor
            // the restore may play. Beyond that window the belt really was down: end
            // the tear and fade it back in.
            if (dissolve != null && active)
            {
                int age = now - dissolve.StartTick;
                if (age > 0)
                {
                    PersonalShieldDissolveLog.Cancel(comp);
                    dissolve = null;

                    if (age > ShieldRestoreLog.InstantRecoveryTicks
                        && restoreStyle != ShieldRestoreStyle.None)
                    {
                        ShieldRestoreLog.Begin(comp, restoreTicks);
                    }
                    else
                    {
                        // Observe() may have stamped this frame's up edge already.
                        // An instant break-and-restore must leave no trace.
                        ShieldRestoreLog.Cancel(comp);
                    }
                }
            }

            // A disable and re-enable between two draws gives Observe() no false
            // sample, so its up edge cannot fire. The finished break is the proof that
            // the belt did go down; use it as the reactivation signal.
            if (dissolve == null
                && active
                && customBreak
                && restoreStyle != ShieldRestoreStyle.None
                && PersonalShieldDissolveLog.TryConsumeFinished(comp))
            {
                ShieldRestoreLog.Begin(comp, restoreTicks);
            }

            // A break surface is submitted only while the belt is actually down, and
            // only after it has outlived the instant-recovery window. Holding the first
            // few frames back is what makes a sub-frame break invisible: nothing has
            // been painted yet when the reactivation cancels it above.
            bool breaking = dissolve != null
                && !active
                && now - dissolve.StartTick > ShieldRestoreLog.InstantRecoveryTicks;

            if (breaking && dissolve != null)
            {
                // The delay is not part of the animation: remap the clock so the
                // first visible frame is the untouched surface (progress 0) and the
                // tear still finishes on the original end tick.
                int visibleSpan = Mathf.Max(
                    1,
                    dissolve.DurationTicks - ShieldRestoreLog.InstantRecoveryTicks);
                dissolveProgress = Mathf.Clamp01(
                    (float)(now - dissolve.StartTick
                        - ShieldRestoreLog.InstantRecoveryTicks)
                    / visibleSpan);
            }

            // ------------------------------------------------------------------
            // Restore.
            //
            // The belt came back up: fade the surface in, and for Grow also inflate
            // it from a fraction of its current energy-driven size. This multiplies
            // the player's opacity rather than replacing it, so the belt never gets
            // brighter than its normal appearance. A break in flight owns the frame.
            // ------------------------------------------------------------------
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
            float restoreScale = 1f;
            if (restoreProgress >= 0f)
            {
                float eased = ShieldRestoreLog.Ease(restoreProgress);
                restoreAlpha = eased;
                if (restoreStyle == ShieldRestoreStyle.Grow)
                {
                    restoreScale = Mathf.Lerp(
                        ShieldRestoreLog.GrowStartScale,
                        1f,
                        eased);
                }
            }

            bool restoring = dissolve == null && restoreProgress >= 0f;

            // Nothing is submitted for a shield that is neither breaking nor active
            // and visible, so the material and its look uniforms are resolved only
            // after that decision.
            if (!breaking && !visible)
            {
                return false;
            }

            Material? mat = Surface.Ensure();
            if (mat == null)
            {
                return false;
            }

            // Shared ridge/face/line weights, but this material resolves the personal
            // surface style and lattice independently of the dome's.
            Surface.ApplyLook();

            // Only the jitter effect moves the bubble. Brighten and the lattice wave
            // are additive quads drawn over the same centred surface, so they must not
            // also displace it. The dissolve owns the frame when one is playing, so no
            // hit reaction is layered on top of it either; a restoring bubble does the
            // same, because a full-strength reaction on a surface still fading in
            // reads as a second object.
            Vector3 jitterOffset = Vector3.zero;
            float jitterShrink = 0f;
            if (dissolve == null
                && !restoring
                && hitStyle == PersonalShieldHitStyle.Jitter)
            {
                TryGetJitter(comp, out jitterOffset, out jitterShrink);
            }

            float size = drawnSize - jitterShrink;
            float radius = Mathf.Max(size * 0.5f, 0.05f);
            float drawnRadius = radius * restoreScale;

            Vector3 center = baseCenter + jitterOffset;

            // Crisp and panel silhouettes sit exactly on r == 1, so their quad is
            // exactly the diameter. The stock-like style's soft tail is meant to fade
            // past the rim, so it gets vanilla's own ForceField margin for that tail.
            float quadMargin =
                VisualTweaksSettings.personalShieldDomeStyle
                        == ShieldDomeStyle.Vanilla
                    || VisualTweaksSettings.personalShieldDomeStyle
                        == ShieldDomeStyle.HexBubble
                    ? ShieldDomeRenderer.VanillaQuadMargin
                    : 1f;

            Matrix4x4 matrix = default;
            matrix.SetTRS(
                new Vector3(center.x, Altitude, center.z),
                Quaternion.identity,
                new Vector3(
                    drawnRadius * 2f * quadMargin,
                    1f,
                    drawnRadius * 2f * quadMargin));

            // Vanilla belts have always drawn white here; VEF lets its def pick the
            // bubble colour, so the adapter passes it through instead of hard-coding.
            NVector4 tint = new NVector4(
                baseColor.r,
                baseColor.g,
                baseColor.b,
                1f);
            bool hexBubble = ShieldHexBubble.Active(ShieldHexTarget.Personal);

            // The belt's hit clock is its own jitter duration (vanilla's 8 ticks),
            // shared with the brighten / hex-spread quads. The net hold rides in
            // _Params4.z so no extra uniform or draw call is needed.
            float hitHold = 0f;
            if (hexBubble
                && PersonalShieldHitLog.TryGet(comp, out int hitTick, out _))
            {
                TickManager? tickManager = Find.TickManager;
                if (tickManager != null)
                {
                    int hitDuration = Mathf.Max(
                        1,
                        Mathf.RoundToInt(
                            VisualTweaksSettings.personalShieldJitterTicks));
                    int hitAge = tickManager.TicksGame - hitTick;
                    if (hitAge >= 0 && hitAge < hitDuration)
                    {
                        hitHold = ShieldHexBubble.HitHold(
                            (float)hitAge / hitDuration);
                    }
                }
            }

            NVector4 centerParam = new NVector4(center.x, center.z, hitHold, 0f);
            float time = Time.realtimeSinceStartup;

            if (breaking && dissolve != null)
            {
                // Mode 2 or 5. Both break animations share the same clock; _Params3
                // switches meaning from a hit bearing to origin / progress / seed.
                properties[0] = DrawProperty.Vector(
                    ParamsId,
                    new NVector4(
                        dissolveMode,
                        VisualTweaksSettings.personalShieldDissolveBrightness,
                        time,
                        radius));
                properties[1] = DrawProperty.Vector(
                    Params3Id,
                    new NVector4(
                        dissolve.OriginX,
                        dissolve.OriginZ,
                        dissolveProgress,
                        dissolve.Seed));

                // The break owns the frame: fade the net in, then keep it fully
                // visible until the surface is gone. Same curve as the dome.
                NVector4 breakCenter = new NVector4(
                    centerParam.X,
                    centerParam.Y,
                    hexBubble ? ShieldHexBubble.BreakHold(dissolveProgress) : 0f,
                    0f);
                properties[2] = DrawProperty.Vector(Params4Id, breakCenter);
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
            // moved the surface itself, so that branch is a no-op here. A restoring
            // surface suppresses the reaction for the same reason as the dome.
            if (dissolve == null && !restoring)
            {
                DrawImpact(
                    comp,
                    mat,
                    matrix,
                    tint,
                    centerParam,
                    drawnRadius,
                    time,
                    hitStyle);
            }

            // The same role GetCurrentAlpha() plays for an interceptor dome. It is
            // independent of that dome's value on purpose: a belt is always shown at
            // one steady level, and the default sits in the middle of the range a
            // selected dome pulses through.
            float alpha = Mathf.Clamp(
                VisualTweaksSettings.personalShieldOpacity,
                VisualTweaksSettings.MinPersonalShieldOpacity,
                VisualTweaksSettings.MaxPersonalShieldOpacity);
            if (hexBubble)
            {
                // A belt has no vanilla pulse of its own, so "pulse on" adds a
                // relative one; "pulse off" uses the same fixed, lower alpha as
                // the projector dome, with no sin term at all.
                alpha = ShieldHexBubble.Alpha(
                    alpha,
                    time,
                    VisualTweaksSettings.hexBubblePulse,
                    rawAlphaPulses: false);
            }

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
            object comp,
            Material material,
            Matrix4x4 matrix,
            NVector4 tint,
            NVector4 centerParam,
            float radius,
            float time,
            PersonalShieldHitStyle hitStyle)
        {
            // Jitter moves the surface instead of drawing a quad, and an unknown
            // style is safer as no extra draw than as a wrong one.
            if (hitStyle != PersonalShieldHitStyle.Brighten
                && hitStyle != PersonalShieldHitStyle.HexWave)
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

            // The stored bearing is a DamageInfo.Angle, i.e. Unity's Y-euler
            // convention (the same one vanilla's impactAngleVect uses), not the
            // AngleToFlat convention the interceptor logs. HorizontalVectorFromAngle
            // is the matching conversion; using FromAngleFlat here rotated every
            // sector 90 degrees clockwise. The light belongs on the side the round
            // came from, so the shot's own vector is negated.
            Vector3 toward = Vector3Utility.HorizontalVectorFromAngle(angle);
            float dx = -toward.x;
            float dz = -toward.z;

            bool hexSpread = hitStyle == PersonalShieldHitStyle.HexWave
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
            object comp,
            out Vector3 offset,
            out float shrink)
        {
            offset = Vector3.zero;
            shrink = 0f;

            if (VisualTweaksSettings.personalShieldHitStyle
                != PersonalShieldHitStyle.Jitter)
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
