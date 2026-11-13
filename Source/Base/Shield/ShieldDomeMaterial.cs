using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// One shield-shaped material and the look uniforms it carries.
    ///
    /// There is one instance per (<see cref="ShieldHexTarget"/>, surface) pair,
    /// because both the chosen surface style and the baked hex atlas are material
    /// state: a belt may draw crisp while the dome draws hex, and a single shield
    /// may override its family's surface through the style window. Ridge weight and
    /// the face/line weights remain shared, so the materials cannot drift apart
    /// there.
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
        private static readonly int Params7Id = Shader.PropertyToID("_Params7");
        private static readonly int Params8Id = Shader.PropertyToID("_Params8");
        private static readonly int Params9Id = Shader.PropertyToID("_Params9");

        private readonly ShieldHexTarget target;

        /// <summary>
        /// The surface this material is fixed to. There is one instance per
        /// (target, surface) pair instead of one per target, because the surface is
        /// a material uniform and individual shields may now override it.
        /// </summary>
        private readonly ShieldDomeStyle surfaceStyle;

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
        private int appliedHexBindFor = -1;
        private Vector4 appliedScan7 = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        private Vector4 appliedScan8 = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        private Vector4 appliedAperture = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);

        internal ShieldDomeMaterial(ShieldHexTarget target, ShieldDomeStyle surfaceStyle)
        {
            this.target = target;
            this.surfaceStyle = surfaceStyle;
        }

        internal ShieldDomeStyle SurfaceStyle => surfaceStyle;

        /// <summary>
        /// True when a spherical lattice is really being drawn: either the panel
        /// shell or the hex bubble, with this target's baked texture available.
        /// Every per-cell effect goes through this gate — the lattice hit wave
        /// (mode 3), the panel-loss break (mode 5) and the crisp fallback — so a
        /// shield is never answered by tiles it is not showing. The bubble's
        /// transparent net samples the same cell ids, so those two effects are as
        /// valid on it as on the shell. A belt's hit reaction is the one exception:
        /// Hex spread may be chosen on a non-lattice surface and checks the texture
        /// itself, because the surface it lights is the animation's whole point.
        /// </summary>
        internal bool HexActive =>
            ShieldStyle.IsHexSurface(surfaceStyle)
            && ShieldHexMap.Available(target);

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

                // A failed first upload must not leave the half-built Unity object
                // orphaned. Destroy it before clearing the field: callers treat null
                // as "do not draw" and hand the frame back to vanilla, which is the
                // right answer for a material whose look constants were never uploaded.
                if (material != null)
                {
                    UnityEngine.Object.Destroy(material);
                }

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

            bool personal = target == ShieldHexTarget.Personal;
            float width = personal
                ? VisualTweaksSettings.personalShieldRidgeWidth
                : VisualTweaksSettings.largeShieldRidgeWidth;
            float brightness = personal
                ? VisualTweaksSettings.personalShieldRidgeBrightness
                : VisualTweaksSettings.largeShieldRidgeBrightness;
            float face = personal
                ? VisualTweaksSettings.personalShieldHexFaceStrength
                : VisualTweaksSettings.largeShieldHexFaceStrength;
            float line = personal
                ? VisualTweaksSettings.personalShieldHexLineStrength
                : VisualTweaksSettings.largeShieldHexLineStrength;
            float centerClear = personal
                ? VisualTweaksSettings.personalShieldCenterClear
                : VisualTweaksSettings.largeShieldCenterClear;

            // The spherical lattice is baked once per target and shared by every
            // shield of that target. Bind is cheap to call every frame: it only
            // re-bakes when the baked density changes and only calls SetTexture for
            // a new material or a new texture.
            //
            // A belt also binds it when its hit effect is the lattice wave, even if
            // the surface it draws is crisp or stock-like: mode 3 reads per-cell ids
            // from the map and would otherwise sample a black texture.
            //
            // The APPLIED values, not the sliders: the sliders are drafts that only
            // reach the texture when the player presses the bake button, and the
            // shader's gradient constant has to describe the texture that actually
            // exists, not the one being previewed in a UI.
            int density = ShieldHexMap.AppliedSubdivisions(target);
            ShieldDomeStyle chosen = surfaceStyle;
            bool hexBubble = chosen == ShieldDomeStyle.HexBubble;

            // A belt binds the lattice when one of its hit reactions may sample it,
            // even while the surface is crisp or stock-like. The atlas is baked up
            // front when the global hit choice is the lattice wave, when a saved
            // override asks for it, or when the style window selects it; after that
            // it simply stays available and every belt material can bind it. A large
            // dome only binds when its surface can actually sample it.
            bool wantHex =
                ShieldStyle.IsHexSurface(chosen)
                || (target == ShieldHexTarget.Personal
                    && (VisualTweaksSettings.personalShieldHitStyle
                            == PersonalShieldHitStyle.HexWave
                        || ShieldHexMap.Available(target)));
            if (wantHex && appliedHexBindFor != density)
            {
                ShieldHexMap.Bind(mat, target, density);
                appliedHexBindFor = ShieldHexMap.Available(target) ? density : -1;
            }

            // A failed bake must not produce a black disc. Falling back to the
            // smooth dome keeps the shield readable and matches the shader bridge's
            // "lose the effect, never the feature" rule. The stock-like style has no
            // asset to fail, so it is passed through unconditionally.
            //
            // Stock-like is 2 for the ForceField profile and 3 for the ShieldBubble
            // profile. A belt's regular Vanilla choice is the bubble; a dome can pick
            // either profile. HexBubble is 4. Hex falls back to crisp when its atlas
            // failed, and HexBubble falls back to the plain bubble for the same
            // reason: with a black atlas every texel reads as a cell wall and the
            // whole disc would light up as one solid net.
            float style;
            if (chosen == ShieldDomeStyle.Vanilla)
            {
                style = target == ShieldHexTarget.Personal ? 3f : 2f;
            }
            else if (chosen == ShieldDomeStyle.Bubble)
            {
                style = 3f;
            }
            else if (hexBubble)
            {
                style = ShieldHexMap.Available(target) ? 4f : 3f;
            }
            else
            {
                style = HexActive ? 1f : 0f;
            }

            // The hex face/line values are shared with the panel-shell style, but
            // a transparent net has no panels: it needs no face term and a
            // stronger line for the same drawn weight. The gain lives here, not
            // in the settings, so both styles keep one saved slider.
            float faceValue = hexBubble ? 0f : face;
            float lineValue = hexBubble
                ? Mathf.Min(
                    line * ShieldHexBubble.LineGain,
                    VisualTweaksSettings.MaxShieldHexLine)
                : line;

            Vector4 scan7 = new Vector4(
                VisualTweaksSettings.hexBubbleScanDown ? 1f : -1f,
                VisualTweaksSettings.hexBubbleScanPeriod,
                VisualTweaksSettings.hexBubbleFeather,
                VisualTweaksSettings.hexBubbleBaseline);
            Vector4 scan8 = new Vector4(
                VisualTweaksSettings.hexBubbleStripes,
                ShieldHexBubble.BandHalfWidth,
                ShieldHexBubble.LeadStrength,
                0f);

            // The centre aperture is a property of the two lattice surfaces. The
            // same value is uploaded whether or not the current surface reads it,
            // because the material is also reused across shields of the same
            // family and surface. Both families keep their own clear radius: a
            // 1.2-cell belt and a 25-cell dome need very different clearances.
            Vector4 aperture9 = new Vector4(centerClear, 0f, 0f, 0f);

            if (width == appliedRidgeWidth
                && brightness == appliedRidgeBrightness
                && style == appliedDomeStyle
                && faceValue == appliedHexFace
                && lineValue == appliedHexLine
                && density == appliedHexDensity
                && scan7 == appliedScan7
                && scan8 == appliedScan8
                && aperture9 == appliedAperture)
            {
                return;
            }

            appliedRidgeWidth = width;
            appliedRidgeBrightness = brightness;
            appliedDomeStyle = style;
            appliedHexFace = faceValue;
            appliedHexLine = lineValue;
            appliedHexDensity = density;
            appliedScan7 = scan7;
            appliedScan8 = scan8;
            appliedAperture = aperture9;

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
                    faceValue,
                    lineValue,
                    density / ShieldHexMap.ERefPerSubdivision));

            // Material-level scan settings. The phase itself is derived in the
            // shader from _Params.z (seconds), so switching a shield to this
            // style costs no per-frame CPU work. _Params4.z, the per-draw hold,
            // is the only value that changes every frame.
            mat.SetVector(Params7Id, scan7);
            mat.SetVector(Params8Id, scan8);

            // _Params9 = (clear radius as a share of R, 0, 0, 0). 0 is the off
            // switch: the shader's CenterAperture returns 1 immediately, so the
            // two lattice styles behave exactly as before.
            mat.SetVector(Params9Id, aperture9);
        }

        private void Invalidate()
        {
            appliedRidgeWidth = float.NaN;
            appliedRidgeBrightness = float.NaN;
            appliedDomeStyle = float.NaN;
            appliedHexFace = float.NaN;
            appliedHexLine = float.NaN;
            appliedHexDensity = -1;
            appliedHexBindFor = -1;
            appliedScan7 = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
            appliedScan8 = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
            appliedAperture = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        }
    }

    /// <summary>
    /// One material per (target, surface) pair. The surface is a material uniform,
    /// so a shield that overrides it cannot share a material with one that does
    /// not. Ten instances at most: two families times five surfaces.
    /// </summary>
    internal static class ShieldMaterials
    {
        private static readonly System.Collections.Generic.Dictionary<
            int,
            ShieldDomeMaterial> cache =
            new System.Collections.Generic.Dictionary<int, ShieldDomeMaterial>();

        internal static ShieldDomeMaterial Get(
            ShieldHexTarget target,
            ShieldDomeStyle style)
        {
            int key = ((int)target << 8) | (int)style;
            if (!cache.TryGetValue(key, out ShieldDomeMaterial? material))
            {
                material = new ShieldDomeMaterial(target, style);
                cache[key] = material;
            }

            return material;
        }
    }
}
