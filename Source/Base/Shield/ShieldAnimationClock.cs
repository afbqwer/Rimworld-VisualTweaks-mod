using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// The one clock every shield surface animation reads.
    ///
    /// The shaders are fed a wall-clock time so the HexBubble scan band, the angular
    /// drift and the break noise keep moving while the colony is paused. That is a
    /// reasonable default for a surface that reads like a HUD, but "pause" means
    /// "freeze everything" to plenty of players, so the setting can stop it. The
    /// accumulator is shared by the dome, the personal belt and the idle breathing
    /// alpha, so the families cannot drift apart on when they stop.
    ///
    /// While frozen the value holds; it resumes from the same phase instead of
    /// jumping to the wall clock, so unpausing does not snap the pattern. Advanced at
    /// most once per rendered frame: several renderers read it, and a per-read
    /// delta would make the clock run faster the more shields are on screen.
    /// </summary>
    internal static class ShieldAnimationClock
    {
        // A hitch must not jump the scan band by a visible distance. The same guard
        // is used by OverlayClock for the effects that follow the colony.
        private const float MaxStep = 0.1f;

        private static bool initialized;
        private static float clock;
        private static int lastFrame = -1;

        /// <summary>
        /// Seconds for the shield shaders. Freezes on pause when the setting is on;
        /// otherwise it tracks real time.
        ///
        /// Reads Verse's per-frame cache (<see cref="RealTime"/>) instead of calling
        /// <c>Time.frameCount</c> / <c>Time.realtimeSinceStartup</c> here: this getter
        /// runs once per shield per frame plus once per idle-alpha query, and after
        /// the first read of a frame it should cost nothing but two static reads.
        /// </summary>
        internal static float Time
        {
            get
            {
                int frame = RealTime.frameCount;

                if (!initialized)
                {
                    initialized = true;
                    lastFrame = frame;
                    clock = RealTime.LastRealTime;
                    return clock;
                }

                if (frame == lastFrame)
                {
                    return clock;
                }

                lastFrame = frame;

                // With the option off, hand back wall time exactly as the shaders
                // used to receive it. Keeping the accumulator in sync here also means
                // re-enabling the option starts from the current phase rather than
                // from whenever it was last read.
                if (!VisualTweaksSettings.shieldPauseWhenPaused)
                {
                    clock = RealTime.LastRealTime;
                    return clock;
                }

                // Current.Game is null outside a loaded game (main menu, teardown).
                // Find.TickManager would dereference it, so resolve the chain here
                // and treat "no game" as "not paused" instead of risking an NRE in a
                // per-frame draw path.
                TickManager? tickManager = Current.Game?.tickManager;
                if (tickManager != null && tickManager.Paused)
                {
                    return clock;
                }

                float dt = RealTime.realDeltaTime;
                if (!(dt > 0f))
                {
                    return clock;
                }

                if (dt > MaxStep)
                {
                    dt = MaxStep;
                }

                clock += dt;
                return clock;
            }
        }
    }
}
