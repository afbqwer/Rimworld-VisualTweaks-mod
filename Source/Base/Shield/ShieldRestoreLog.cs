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
    /// The one edge this cannot see is a disable and re-enable that both land between
    /// two draws, or inside one tick. The break log still recorded the break, so the
    /// renderer calls <see cref="Begin"/> when that break plays out, and the restore
    /// happens even though no frame ever saw the shield down.
    ///
    /// The first observation never animates. That covers loading a save, a newly
    /// constructed generator, equipping a belt and enabling the replacement: all of
    /// them arrive with an already-active shield and should not flash.
    ///
    /// Not scribed for the same reason as the dissolve logs: the animation is only a
    /// few seconds and rebasing its start tick against a clock that may have advanced
    /// by thousands of ticks has no value.
    /// </summary>
    internal static class ShieldRestoreLog
    {
        /// <summary>
        /// How long a break must last before the shield coming back counts as a real
        /// event. Several mods break their shield and restore it in one motion; playing
        /// a tear and then a fade for something the player never saw down reads as a
        /// flicker. A shield that is back within this window is treated as never having
        /// left, and both animations are dropped.
        ///
        /// 1 ticks is about a sixth of a second at normal speed — long enough to cover
        /// a disable and re-enable that straddle a tick or two, short enough that a
        /// genuinely visible break still gets its full feedback.
        /// </summary>
        internal const int InstantRecoveryTicks = 1;

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
            public int DurationTicks = 1;
        }

        private static readonly ConditionalWeakTable<object, State> States =
            new ConditionalWeakTable<object, State>();

        /// <summary>
        /// Records whether the shield is currently available. Starts a restore when it
        /// changes from false to true, and drops any running restore when it goes down
        /// (a break, cooldown, EMP stun or shutdown owns the surface from then on).
        /// Idempotent, so the renderer can call it once per draw without checking.
        /// The duration is captured at the start of each restore, so moving the slider
        /// mid-animation affects the next one rather than restarting this one.
        /// </summary>
        internal static void Observe(object comp, bool present, int durationTicks)
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
            state.DurationTicks = Mathf.Max(1, durationTicks);
        }

        /// <summary>
        /// Starts a restore without waiting for an observed up edge.
        ///
        /// Observe() only sees the transitions the draw path happens to sample. A
        /// disable and re-enable that both land between two draws — or inside the same
        /// tick — never produces a frame with <c>present == false</c>, so WasPresent
        /// stays true and the restore would silently not play. The break log still knows
        /// the shield went down, so the renderer hands that event here when the break
        /// finishes. Idempotent with Observe: both stamp the same tick, so a call in
        /// the same frame as a real up edge cannot restart the animation.
        /// </summary>
        internal static void Begin(object comp, int durationTicks)
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
            state.Initialized = true;
            state.WasPresent = true;
            state.StartTick = tickManager.TicksGame;
            state.DurationTicks = Mathf.Max(1, durationTicks);
        }

        /// <summary>
        /// Drops a restore without starting a new one. Used when a break turns out to
        /// have been an instant break-and-restore: Observe() may already have stamped
        /// the up edge on this frame, and that edge must leave no animation behind.
        /// Keeps WasPresent set so the next frame does not mistake the same shield for
        /// a fresh activation.
        /// </summary>
        internal static void Cancel(object comp)
        {
            if (comp == null)
            {
                return;
            }

            if (States.TryGetValue(comp, out State? state) && state != null)
            {
                state.StartTick = -1;
                state.WasPresent = true;
            }
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
