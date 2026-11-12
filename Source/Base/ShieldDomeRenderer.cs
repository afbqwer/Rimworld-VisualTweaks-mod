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
    /// Draws the custom shield dome, and one flare per recorded impact, in place of
    /// vanilla's two quads.
    ///
    /// Called from the <c>PostDraw</c> prefix, i.e. from inside the thing draw pass at
    /// the shield's own position. That is deliberate: it inherits vanilla's visibility
    /// gating (spawned, dynamic-draw list, altitude layer) instead of rebuilding it, and
    /// it keeps the shield's place in the transparent queue without a second
    /// registration path to keep in sync.
    ///
    /// One material, one draw for the dome plus one per live flare. Everything that
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
        // Ridge is the edge's thickness and it is a real width (RidgeHalfPixels), not
        // a brightness — pushing RidgeWeight makes the band hotter, pushing
        // RidgeHalfPixels makes it wider. The first complaint about this shader was
        // that the outline was too thin, and those are two different fixes.
        // ------------------------------------------------------------------
        private const float RidgeWeight = 0.52f;
        private const float CoreWeight = 0.30f;
        private const float GlowWeight = 0.30f;
        private const float BodyWeight = 0.05f;

        /// <summary>
        /// Half-width of the ridge in screen pixels. With the shader's 0.42 plateau
        /// this is a band roughly 5-6 px thick: a ~3 px flat core with a shoulder on
        /// each side. The first version of this shader drew a 1.5 px gaussian, which
        /// is why the outline read as vector art.
        /// </summary>
        private const float RidgeHalfPixels = 3.6f;

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
        /// Peak alpha of one flare. Vanilla's cone reaches 0.82, which is most of the
        /// reason it reads as an opaque slice dropped on the dome — at that opacity it
        /// stops looking like light and starts looking like a decal. 0.30 is a bright
        /// patch on an additive surface, and several of them can overlap before the
        /// result saturates.
        /// </summary>
        private const float MaxFlareAlpha = 0.30f;

        /// <summary>
        /// Half-width as a cosine. 0.9135 is about 24 degrees each side.
        /// </summary>
        private const float FlareCosHalfAngle = 0.9135f;

        /// <summary>
        /// Where the flare starts, as a fraction of the radius. Not zero: a flare that
        /// reaches the centre draws over whatever is standing under the shield, and the
        /// cue only has to point, not cover.
        /// </summary>
        private const float FlareInnerRadius = 0.22f;

        /// <summary>Vanilla's own colour for an inactive shield that is selected.</summary>
        private static readonly Color InactiveColor = new Color(0.2f, 0.2f, 0.2f);

        private static readonly float Altitude =
            Altitudes.AltitudeFor(AltitudeLayer.MoteOverhead);

        private static readonly int ParamsId = Shader.PropertyToID("_Params");
        private static readonly int Params2Id = Shader.PropertyToID("_Params2");
        private static readonly int Params3Id = Shader.PropertyToID("_Params3");
        private static readonly int Params4Id = Shader.PropertyToID("_Params4");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        // Reused every frame; DrawPropertyBlock wraps these arrays without allocating,
        // and the engine copies the span into its arena during the DrawMesh call, so
        // one array can back every draw in the loop.
        private static readonly DrawProperty[] properties = new DrawProperty[4];

        private static Material? material;
        private static bool materialFailed;

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

            Vector3 center = parent.DrawPos;

            // 2 * radius, with no texture-margin factor: the shader puts the
            // silhouette on r == 1.0, so the quad edge and the gameplay radius are the
            // same circle.
            Matrix4x4 matrix = default;
            matrix.SetTRS(
                new Vector3(center.x, Altitude, center.z),
                Quaternion.identity,
                new Vector3(radius * 2f, 1f, radius * 2f));

            // Vanilla paints a selected-but-inactive shield dark grey so "this is off"
            // is visible at a glance. Same rule, same colour.
            Color color = !comp.Active && Find.Selector.IsSelected(parent)
                ? InactiveColor
                : comp.Props.color;

            NVector4 tint = new NVector4(color.r, color.g, color.b, 1f);
            NVector4 centerParam = new NVector4(center.x, center.z, 0f, 0f);
            float time = Time.realtimeSinceStartup;
            bool drew = false;

            try
            {
                // Flares first: they are additive, so order does not change the
                // picture, but submitting them under the dome keeps the rim the last
                // thing written to the surface.
                drew |= DrawFlares(comp, mat, matrix, tint, centerParam, radius, time);

                if (alpha > 0f)
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

        private static bool DrawFlares(
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

                // Squared falloff: the flare snaps on at full strength and gives up
                // most of its brightness in the first third, which is how a flash
                // behaves. A linear fade reads as a slow dimming slice.
                float life = 1f - (float)age / ShieldImpactLog.LifetimeTicks;
                float flareAlpha = MaxFlareAlpha * life * life;
                if (flareAlpha < 0.004f)
                {
                    continue;
                }

                // The stored angle is the bearing from the projectile towards the
                // shield centre, so the flare has to look the other way to point at
                // the shooter.
                Vector3 toward = Vector3Utility.FromAngleFlat(impacts[i].Angle);
                NVector4 direction = new NVector4(
                    -toward.x,
                    -toward.z,
                    FlareCosHalfAngle,
                    FlareInnerRadius);

                properties[0] = DrawProperty.Vector(
                    ParamsId, new NVector4(1f, flareAlpha, time, radius));
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

                // Look constants, identical for every shield and every draw, so they
                // belong on the material rather than in each draw's property list.
                created.SetVector(
                    Params2Id,
                    new Vector4(RidgeWeight, CoreWeight, GlowWeight, BodyWeight));

                created.SetVector(
                    Shader.PropertyToID("_Params5"),
                    new Vector4(RidgeHalfPixels, CoreSigmaPixels, GlowExponent, DriftWeight));

                material = created;
            }
            catch (Exception ex)
            {
                materialFailed = true;
                Log.Error("[Visual Tweaks] Shield dome material could not be created;"
                    + " stock drawing kept: " + ex);
            }

            return material;
        }
    }
}
