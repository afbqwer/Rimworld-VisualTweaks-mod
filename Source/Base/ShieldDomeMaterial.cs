using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// One shield-shaped material and the look uniforms it carries.
    ///
    /// There is one instance per <see cref="ShieldHexTarget"/>, because the baked
    /// hex atlas is a material texture: a belt and a projector dome must be able to
    /// sample different lattices in the same frame. Everything else about the
    /// material is identical and comes from the same global settings, so the two
    /// surfaces can never disagree about style, ridge weight or face shading — only
    /// about which atlas they bind.
    ///
    /// Per-draw values (alpha, radius, centre, break progress) deliberately do not
    /// live here. They travel as a DrawPropertyBlock, which lets both materials be
    /// created once and reused for every shield of their kind.
    /// </summary>
    internal sealed class ShieldDomeMaterial
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

        private static readonly int Params2Id = Shader.PropertyToID("_Params2");
        private static readonly int Params5Id = Shader.PropertyToID("_Params5");
        private static readonly int Params6Id = Shader.PropertyToID("_Params6");

        private readonly ShieldHexTarget target;

        private Material? material;
        private bool materialFailed;

        // What this material currently carries, so a slider drag uploads a few
        // vectors instead of a few vectors per shield per frame.
        private float appliedRidgeWidth = float.NaN;
        private float appliedRidgeBrightness = float.NaN;
        private float appliedDomeStyle = float.NaN;
        private float appliedHexFace = float.NaN;
        private float appliedHexLine = float.NaN;
        private int appliedHexDensity = -1;

        internal ShieldDomeMaterial(ShieldHexTarget target)
        {
            this.target = target;
        }

        /// <summary>
        /// True when the panel style is what this target actually shows: the style is
        /// selected and the target's own lattice texture exists. Both the style
        /// uniform and the choice of hit reaction / break go through this, so they
        /// can never disagree — answering a hit with tiles while the surface draws
        /// crisp would flash panels that are not on screen.
        /// </summary>
        internal bool HexActive =>
            VisualTweaksSettings.HexShieldDome && ShieldHexMap.Available(target);

        /// <summary>
        /// The material, created on first use. Null means "draw nothing"; every
        /// caller hands that frame back to vanilla rather than leaving a hole in the
        /// shield.
        /// </summary>
        internal Material? Ensure()
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
                material = new Material((Shader)def)
                {
                    name = "VisualTweaksShieldDomeMat_" + target,
                    renderQueue = RenderQueue,
                };

                // Force the first upload even if the settings happen to equal the
                // defaults these fields were initialised with.
                Invalidate();
                ApplyLook();
            }
            catch (Exception ex)
            {
                materialFailed = true;

                // Drop the half-built material: the caller treats null as "do not
                // draw" and hands the frame back to vanilla, which is the right answer
                // for a material whose look constants were never uploaded.
                material = null;
                Log.Error("[Visual Tweaks] Shield dome material (" + target
                    + ") could not be created; stock drawing kept: " + ex);
            }

            return material;
        }

        /// <summary>
        /// Uploads the values the player can move. Called every frame, but only
        /// writes when one actually changed: the settings are read live, and the
        /// alternative — a per-draw property for each look constant — would put extra
        /// vectors on every shield every frame for numbers that change once a session.
        ///
        /// The hex bind happens before the early-out because a rebake can replace the
        /// texture object without changing any of the uniforms compared below.
        /// </summary>
        internal void ApplyLook()
        {
            Material? mat = material;
            if (mat == null)
            {
                return;
            }

            float width = VisualTweaksSettings.shieldRidgeWidth;
            float brightness = VisualTweaksSettings.shieldRidgeBrightness;
            float face = VisualTweaksSettings.shieldHexFaceStrength;
            float line = VisualTweaksSettings.shieldHexLineStrength;

            // The spherical lattice is baked once per target and shared by every
            // shield of that target. Bind is cheap to call every frame: it only
            // re-bakes when the baked density changes and only calls SetTexture for
            // a new material or a new texture.
            //
            // The APPLIED values, not the sliders: the sliders are drafts that only
            // reach the texture when the player presses the bake button, and the
            // shader's gradient constant has to describe the texture that actually
            // exists, not the one being previewed in a UI.
            int density = ShieldHexMap.AppliedSubdivisions(target);
            bool wantHex = VisualTweaksSettings.HexShieldDome;
            if (wantHex)
            {
                ShieldHexMap.Bind(mat, target, density);
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

        private void Invalidate()
        {
            appliedRidgeWidth = float.NaN;
            appliedRidgeBrightness = float.NaN;
            appliedDomeStyle = float.NaN;
            appliedHexFace = float.NaN;
            appliedHexLine = float.NaN;
            appliedHexDensity = -1;
        }
    }
}
