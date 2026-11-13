using UnityEngine;

namespace VisualTweaks
{
    /// <summary>
    /// The constants and the two time curves shared by the HexBubble surface's
    /// CPU side. The GPU owns the scan mask itself (one continuous function of p.y
    /// and time); this class owns everything that has to be decided per draw:
    /// how strongly the net is forced visible, and what alpha the surface gets.
    ///
    /// Both renderer families call the same helpers so a belt and a projector
    /// dome cannot drift apart on the timing.
    /// </summary>
    internal static class ShieldHexBubble
    {
        /// <summary>
        /// Half width of the scan band in normalised disc coordinates (the disc's
        /// diameter is 2). Fixed at the value the preview was tuned with, so there
        /// is no setting for it. Uploaded in _Params8.y.
        /// </summary>
        internal const float BandHalfWidth = 0.06f;

        /// <summary>Leading-edge highlight. Tuned to zero in the preview.</summary>
        internal const float LeadStrength = 0f;

        /// <summary>
        /// Fraction of the hit / break animation spent fading the net in (and, for
        /// a hit, out). All of it lives inside the animation's own duration, so
        /// "hold the net opaque for the hit animation" stays literally true.
        /// </summary>
        internal const float RevealShare = 0.25f;

        /// <summary>
        /// Alpha used when the pulse is off. Vanilla's selected dome pulses
        /// 0.20..0.62 (mean 0.41) and an unselected one sits near 0.10; a steady
        /// surface at the peak reads noticeably more solid than the average of a
        /// pulsing one under additive blending, so the fixed value sits below that
        /// mean. See CompProjectileInterceptor.GetCurrentAlpha_Selected in the
        /// decompiled game.
        /// </summary>
        internal const float SteadyAlpha = 0.30f;

        /// <summary>
        /// The hex line setting is shared with the panel-shell style, where the
        /// panels themselves carry most of the brightness. A transparent net has
        /// no panels, so it needs a higher weight for the same drawn line. This is
        /// applied to _Params6.z in ShieldDomeMaterial, never in the settings, so
        /// the two styles keep one slider and one saved value.
        /// </summary>
        internal const float LineGain = 3f;

        /// <summary>True when this shield family draws the hex bubble surface.</summary>
        internal static bool Active(ShieldHexTarget target) =>
            VisualTweaksSettings.ShieldDomeStyleFor(target)
                == ShieldDomeStyle.HexBubble;

        private static float Ease(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        /// <summary>
        /// Net visibility weight during one hit: fade in, fully on, fade out.
        /// The caller supplies progress in 0..1 over the configured impact ticks.
        /// </summary>
        internal static float HitHold(float progress)
        {
            float p = Mathf.Clamp01(progress);
            return Ease(p / RevealShare)
                * (1f - Ease((p - (1f - RevealShare)) / RevealShare));
        }

        /// <summary>
        /// Net visibility weight during a break: fade in and then stay fully on
        /// until the surface is gone. A break has no fade-out — the shield itself
        /// disappears, and a net fading over an empty disc would read as a ghost.
        /// </summary>
        internal static float BreakHold(float progress) =>
            Ease(Mathf.Clamp01(progress) / RevealShare);

        /// <summary>
        /// Surface alpha for a HexBubble draw.
        ///
        /// pulse on  — keep the raw alpha. A projector's raw alpha is vanilla's
        ///             GetCurrentAlpha() and already pulses; a belt's is a
        ///             constant, so give it a relative pulse of the same shape.
        /// pulse off — one fixed, lower value, independent of the raw alpha. That
        ///             independence is the point: no sin term survives anywhere.
        /// </summary>
        internal static float Alpha(
            float rawAlpha,
            float time,
            bool pulse,
            bool rawAlphaPulses)
        {
            if (!pulse)
            {
                return SteadyAlpha;
            }

            return rawAlphaPulses
                ? rawAlpha
                : rawAlpha * (1f + 0.5f * Mathf.Sin(time * 2.2f));
        }
    }
}
