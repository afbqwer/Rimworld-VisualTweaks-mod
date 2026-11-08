using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// The one place that decides how fast an effect whose animation follows the colony
    /// advances its shader clock.
    ///
    /// These effects used to integrate <c>Time.deltaTime</c> directly, which froze them
    /// on pause but left them churning at 1x while the world ran at 15x. Scaling by the
    /// game *speed tier* rather than by <c>TickManager.TickRateMultiplier</c> is
    /// deliberate: vanilla's multipliers are 3x, 6x and 15x (and 12x/18x/150x/20000x in
    /// the special cases), and at those rates a noise pattern becomes a different field
    /// every frame — that reads as flicker, not as faster vapour. The ramp below makes
    /// every tier measurably faster than the last while the pattern still looks like a
    /// pattern.
    ///
    /// Shared instead of copied per renderer because the numbers are a single tuning
    /// decision: if heat haze and cold mist drift apart, two overlays that sit on the
    /// same cells animate at different speeds at the same game speed.
    /// </summary>
    internal static class OverlayClock
    {
        // Whole tuning surface. Each tier is roughly 40-60% faster than the one below it,
        // capped at 3x so the top speed is a visible churn rather than strobing noise.
        private const float NormalFactor = 1f;
        private const float FastFactor = 1.6f;
        private const float SuperfastFactor = 2.2f;
        private const float UltrafastFactor = 3f;

        // A hitch longer than this must not jump the pattern by a visible amount.
        private const float MaxStep = 0.1f;

        // Shader clocks are floats; wrapping keeps precision usable over a long session.
        private const float Wrap = 100000f;

        /// <summary>
        /// Adds this frame's game-speed-scaled delta to <paramref name="clock"/>. A no-op
        /// while paused, so an effect that freezes with the game keeps doing so.
        ///
        /// The phase stays continuous across a speed change: only the rate it grows at
        /// changes, so toggling speed never snaps the pattern.
        /// </summary>
        internal static void Advance(ref float clock, bool paused, TimeSpeed speed)
        {
            if (paused)
            {
                return;
            }

            float dt = Time.deltaTime;
            if (!(dt > 0f))
            {
                return;
            }

            // Clamping the real delta before scaling keeps the *visual* jump the same at
            // every speed: everything else on screen also moved by dt * factor during
            // that frame.
            if (dt > MaxStep)
            {
                dt = MaxStep;
            }

            clock += dt * SpeedFactor(speed);
            if (clock > Wrap)
            {
                clock = 0f;
            }
        }

        private static float SpeedFactor(TimeSpeed speed)
        {
            switch (speed)
            {
                case TimeSpeed.Paused:
                    return 0f;
                case TimeSpeed.Fast:
                    return FastFactor;
                case TimeSpeed.Superfast:
                    return SuperfastFactor;
                case TimeSpeed.Ultrafast:
                    return UltrafastFactor;
                default:
                    // Normal, and any tier a future version adds: 1x is the safe answer
                    // for a speed we have no hand-tuned factor for.
                    return NormalFactor;
            }
        }
    }
}
