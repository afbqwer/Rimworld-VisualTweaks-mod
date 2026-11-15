using UnityEngine;

namespace VisualTweaks
{
    /// <summary>
    /// The constants and the two time curves shared by the HexBubble surface's
    /// CPU side. The GPU owns the scan mask itself (one continuous function of p.y
    /// and time); this class owns everything that has to be decided per draw:
    /// how strongly the net is forced visible, what alpha the surface gets, and at
    /// which point of its sweep each shield's band sits.
    ///
    /// Both renderer families call the same helpers so a belt and a projector
    /// dome cannot drift apart on the timing.
    /// </summary>
    internal static class ShieldHexBubble
    {
        /// <summary>
        /// Separates the scan phase from the idle-alpha phase in
        /// <see cref="ShieldVisibilityPatches"/>, which hashes the same thing id with
        /// its own salt. Two salts mean the two motions are uncorrelated, so a shield
        /// whose pulse is at its peak is not also halfway through its sweep.
        /// </summary>
        private const uint ScanPhaseSalt = 51380427u;

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
        /// Where in its sweep this shield's scan band sits, in seconds, in
        /// [0, period).
        ///
        /// The shader derives the band position from one shared clock
        /// (<see cref="ShieldAnimationClock.Time"/>), so without this every bubble on
        /// the map sweeps in lockstep — the one thing a scan line must not do, and
        /// most obvious straight after a load, when every shield starts from the same
        /// t. The offset is a hash of the host's thing id, which buys three things at
        /// once: two shields never share a phase by accident, the value is identical
        /// on every load without a line of save data or a load hook, and it stays put
        /// while the clock runs, so a shield does not wander relative to its
        /// neighbours.
        ///
        /// A hash, not the low bits of the id. Ids are handed out by a counter, so
        /// shields built near each other differ by one or two thousand — a masked id
        /// puts them a few hundredths of a period apart, which is a third of a band
        /// width and reads as synchronised. Avalanche is the whole point: the input is
        /// consecutive, so small input changes must produce unrelated outputs.
        ///
        /// <paramref name="period"/> is the configured scan period, i.e. the same
        /// modulus the shader takes t modulo, so the offset is a pure phase shift and
        /// a period change cannot turn into a fraction of a band. Returns 0 — every
        /// shield back on the shared clock, one synchronised sweep — when the player
        /// switches the stagger off.
        /// </summary>
        internal static float ScanPhase(int hostId, float period)
        {
            // The switch is read here rather than at the two call sites: the shifting
            // itself lives here, and a caller that forgot the check would quietly
            // bypass the setting for one shield family.
            if (!VisualTweaksSettings.hexBubblePerShieldScanPhase || !(period > 0f))
            {
                return 0f;
            }

            // A murmur3 finaliser rather than Gen.HashCombineInt, which is the obvious
            // house choice and does not work here: for consecutive ids it leaves the
            // middle bits almost untouched, and three shields built in a row came out
            // within 0.1% of one another. This mixes every bit of the id, so ids 1..10
            // land all over the sweep.
            uint h = (uint)hostId ^ ScanPhaseSalt;
            h ^= h >> 16;
            h *= 0x85EBCA6Bu;
            h ^= h >> 13;
            h *= 0xC2B2AE35u;
            h ^= h >> 16;

            // Top 24 bits: 16.7 million phases is finer than a pixel, and the
            // low bits of a finaliser are the ones that can still show structure.
            return (h >> 8) / 16777216f * period;
        }

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
