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
    /// What is ported from <see cref="ShieldDomeRenderer"/> is everything that is not
    /// the hit reaction: the same three surfaces (crisp, hexagonal, stock-like), the
    /// same ridge and lattice settings, a material built from the same code, and the
    /// same dissolve / panel-loss break animations. Impacts are deliberately absent:
    /// a belt is one character wide, and a flare, a sector brightening or a lattice
    /// wave at that size is a few pixels of noise rather than a readable direction.
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
    /// - Alpha is 1.0 whenever the dome is shown. A belt has no idle pulse and no
    ///   "selected" brightness ramp to borrow — vanilla draws its bubble at full
    ///   texture alpha — so the shader's own profile is the whole appearance.
    ///
    /// Small size is not special-cased. Every shape constant in the shader is either
    /// radius-relative (glow, stock-like rim, dissolve noise) or expressed in pixels
    /// (ridge, hex line width), so a 1.2-cell bubble gets the same drawn weight and
    /// the same panel count as a 25-cell dome. The lattice lines fade on their own
    /// when a cell drops below the shader's 5-pixel legibility floor.
    ///
    /// One material for belts, one draw per belt. The material is separate from the
    /// dome's only because the hex atlas is a material texture and the two atlases
    /// have different densities and resolutions; every look constant is still read
    /// from the same global settings. Everything that differs per belt (centre,
    /// radius, alpha, break progress) travels as a draw property.
    /// </summary>
    internal static class PersonalShieldRenderer
    {
        /// <summary>
        /// A displayed belt is always fully shown. Unlike an interceptor dome there
        /// is no selection or idle pulse to modulate, and vanilla's own bubble is
        /// drawn at the texture's full alpha.
        /// </summary>
        private const float DomeAlpha = 1f;

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
            if (!ShieldDomeBridge.Active || comp == null)
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

            // Same style and ridge settings as the big dome; only the atlas differs.
            Surface.ApplyLook();

            int breakStyle = VisualTweaksSettings.CurrentShieldBreakStyle;
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

            // Vanilla's own sizing rule: the drawn diameter grows with the stored
            // energy, from minDrawSize to maxDrawSize, and it uses the raw energy on
            // purpose (an energy stat above 1 is allowed to overshoot just as it does
            // in vanilla). The shader wants a radius, so halve it.
            float size = Mathf.Lerp(
                comp.Props.minDrawSize,
                comp.Props.maxDrawSize,
                comp.Energy);
            float radius = Mathf.Max(size * 0.5f, 0.05f);

            Vector3 center = pawn.Drawer.DrawPos;

            // Crisp and panel silhouettes sit exactly on r == 1, so their quad is
            // exactly the diameter. The stock-like style's soft tail is meant to fade
            // past the rim, so it gets vanilla's own ForceField margin for that tail.
            float quadMargin = ShieldDomeRenderer.VanillaActive
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

            properties[0] = DrawProperty.Vector(
                ParamsId, new NVector4(0f, DomeAlpha, time, radius));
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
    }
}
