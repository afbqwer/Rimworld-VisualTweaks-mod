using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Per-shield "it came back up" animation state.
    ///
    /// The break animation is started by the methods that end a shield; this one
    /// watches the opposite edge. The state is keyed by the comp object because the
    /// families this mod draws (vanilla interceptors, VEF repulsors, vanilla belts
    /// and VEF's personal shield comps) do not share a base type that exposes an
    /// "available" flag.
    ///
    /// Observed from the draw path instead of a tick patch on purpose. Every draw
    /// entry point already resolves the exact state the renderer uses — Active for a
    /// projector, ShieldState plus energy for a belt — so a tick patch would have to
    /// duplicate those rules per comp type, including VEF's reflected ones, and keep
    /// them in sync. A transition missed while the shield was off screen only costs
    /// the animation, never the shield; the dome is simply already there when the
    /// camera returns.
    ///
    /// The first observation never animates. That covers loading a save, a newly
    /// constructed generator, equipping a belt and enabling the replacement: all of
    /// them arrive with an already-active shield and should not flash.
    ///
    /// Not scribed for the same reason as the dissolve logs: the animation is at most
    /// a second and rebasing its start tick against a clock that may have advanced by
    /// thousands of ticks has no value.
    /// </summary>
    internal static class ShieldRestoreLog
    {
        /// <summary>The restore animation lasts one second at normal game speed.</summary>
        internal const int DurationTicks = 60;

        /// <summary>
        /// How small a Grow animation starts. Not zero: a quad scaled to nothing is
        /// invisible for the first frames anyway because the alpha starts at zero, and
        /// the shader's radius-relative features need a usable radius.
        /// </summary>
        internal const float GrowStartScale = 0.15f;

        internal sealed class State
        {
            public bool Initialized;
            public bool WasPresent;
            public int StartTick = -1;
            public int DurationTicks = ShieldRestoreLog.DurationTicks;
        }

        private static readonly ConditionalWeakTable<object, State> States =
            new ConditionalWeakTable<object, State>();

        /// <summary>
        /// Records whether the shield is currently available. Starts a restore when it
        /// changes from false to true, and drops any running restore when it goes down
        /// (a break, cooldown, EMP stun or shutdown owns the surface from then on).
        /// Idempotent, so the renderer can call it once per draw without checking.
        /// </summary>
        internal static void Observe(object comp, bool present)
        {
            if (comp == null)
            {
                return;
            }

            TickManager? tickManager = Find.TickManager;
            if (tickManager == null)
            {
                return;
            }

            State state = States.GetOrCreateValue(comp);
            if (!state.Initialized)
            {
                state.Initialized = true;
                state.WasPresent = present;
                return;
            }

            if (present == state.WasPresent)
            {
                return;
            }

            state.WasPresent = present;
            if (!present)
            {
                state.StartTick = -1;
                return;
            }

            state.StartTick = tickManager.TicksGame;
            state.DurationTicks = DurationTicks;
        }

        /// <summary>
        /// Progress in 0..1 while a restore is playing, or null when there is none.
        /// TicksGame drives it, so it freezes on pause and follows game speed like
        /// every other shield animation.
        /// </summary>
        internal static float? Progress(object comp)
        {
            TickManager? tickManager = Find.TickManager;
            if (comp == null
                || tickManager == null
                || !States.TryGetValue(comp, out State? state)
                || state == null
                || state.StartTick < 0)
            {
                return null;
            }

            int age = tickManager.TicksGame - state.StartTick;
            if (age < 0 || age >= state.DurationTicks)
            {
                return null;
            }

            return state.DurationTicks <= 0
                ? 1f
                : (float)age / state.DurationTicks;
        }

        /// <summary>Eased 0..1, shared by both families so Fade and Grow keep pace.</summary>
        internal static float Ease(float progress)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progress));
        }
    }
}
