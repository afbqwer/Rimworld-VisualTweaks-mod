using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Per-shield "it went away without breaking" animation state.
    ///
    /// The break animation has methods to hang on: four shutdown entry points and
    /// host destruction. A shield that merely goes away has none. A charging cycle
    /// starting, a timed activation running out, a stun ending into dormancy, VEF
    /// switching a repulsor off between raids — every one of those is a condition
    /// inside a property that vanilla only ever reads while drawing. Nothing is
    /// called at the moment it changes.
    ///
    /// <c>Active</c> is not a usable signal either. It is false during cooldown,
    /// charging, stuns, shutdown, zero hit points, dormancy and the tail of a timed
    /// activation, so an edge detector on it would fire on almost every ordinary
    /// interception. That is why the break log is method-keyed and why this one is
    /// keyed on the draw path instead: <see cref="ShieldRestoreLog"/> already
    /// observes the up edge there, and the down edge is the same observation run
    /// backwards.
    ///
    /// Two things are captured at the fall and cannot be read afterwards:
    ///
    ///   * the brightness the surface had on its last *shown* frame — vanilla's
    ///     alpha is already zero by the time the state is false, so a fade driven
    ///     from the current value would start at nothing;
    ///   * the drawn size, because some families size the surface from state that
    ///     has just changed (a belt's energy-driven bubble).
    ///
    /// A vanish only starts for a surface that was actually on screen. A player's
    /// shield that is neither selected nor lit by a recent hit has alpha zero and is
    /// invisible; fading it would light up a dome nobody could see.
    ///
    /// Not scribed, for the same reason as the other shield logs: the animation is
    /// under a second and re-basing its start tick across a load has no value.
    /// </summary>
    internal static class ShieldVanishLog
    {
        /// <summary>
        /// How small a Shrink animation ends. Not zero: the quad is invisible at the
        /// end anyway because the alpha has reached the surface's own steady value,
        /// and the shader's radius-relative features need a usable radius.
        /// </summary>
        internal const float ShrinkEndScale = 0.15f;

        /// <summary>
        /// Below this the surface was not on screen, so there is nothing to fade.
        /// Matches the floor the visibility patch uses for its own alpha.
        /// </summary>
        private const float VisibilityEpsilon = 0.001f;

        internal sealed class State
        {
            public bool Initialized;
            public bool WasPresent;
            public bool WasShown;
            public float LastAlpha;
            public float LastSize;

            public int StartTick = -1;
            public int DurationTicks = 1;

            /// <summary>Brightness on the last shown frame; the fade starts here.</summary>
            public float StartAlpha;

            /// <summary>Drawn size on the last shown frame; Shrink starts here.</summary>
            public float StartSize;
        }

        private static readonly ConditionalWeakTable<object, State> States =
            new ConditionalWeakTable<object, State>();

        /// <summary>
        /// Records the current state and starts a vanish on a down edge.
        ///
        /// <paramref name="present"/> is the shield's own "it is up" answer (Active
        /// for an interceptor, ShieldState for a belt) and decides the edge.
        /// <paramref name="shown"/> is whether the last frame actually painted the
        /// surface; only a shown shield earns an animation.
        /// <paramref name="allowBegin"/> is false while a break is in flight or the
        /// style is off: the edge is still recorded so a later switch cannot fire on
        /// a stale one, but nothing is started.
        ///
        /// <paramref name="instantReturn"/> reports a shield that came back within
        /// <see cref="ShieldRestoreLog.InstantRecoveryTicks"/> of its own fall. The
        /// caller drops the fade-in it may have just started, so a sub-frame
        /// disappearance leaves no trace — the same rule the break path uses for a
        /// shield that breaks and restores in one motion.
        /// </summary>
        internal static State? Observe(
            object comp,
            bool present,
            bool shown,
            float alpha,
            float size,
            int durationTicks,
            bool allowBegin,
            out bool instantReturn)
        {
            instantReturn = false;

            TickManager? tickManager = Find.TickManager;
            if (comp == null || tickManager == null)
            {
                return null;
            }

            int now = tickManager.TicksGame;
            State state = States.GetOrCreateValue(comp);

            if (!state.Initialized)
            {
                state.Initialized = true;
                state.WasPresent = present;
                state.WasShown = shown;
                state.LastAlpha = alpha;
                state.LastSize = size;
                return null;
            }

            if (present)
            {
                // The up edge. A vanish that only just started never painted anything
                // different from the frame before it, so this is a blip: report it and
                // let the caller take the fade-in down with it.
                if (!state.WasPresent && state.StartTick >= 0)
                {
                    instantReturn = now - state.StartTick
                        <= ShieldRestoreLog.InstantRecoveryTicks;
                }

                state.WasPresent = true;
                state.WasShown = shown;
                state.LastAlpha = alpha;
                state.LastSize = size;
                state.StartTick = -1;
                return null;
            }

            bool fell = state.WasPresent;
            state.WasPresent = false;

            if (fell
                && allowBegin
                && state.WasShown
                && state.LastAlpha > VisibilityEpsilon
                && durationTicks > 0)
            {
                state.StartTick = now;
                state.DurationTicks = Mathf.Max(1, durationTicks);
                state.StartAlpha = state.LastAlpha;
                state.StartSize = state.LastSize;
            }

            // The current frame is down, so it is not shown; keeping that flag honest
            // is what stops the next fall from animating a surface nobody saw.
            state.WasShown = shown;

            return Progress(comp, out _) ? state : null;
        }

        /// <summary>
        /// True while a vanish is playing, with progress in 0..1. TicksGame drives it,
        /// so it freezes on pause and follows game speed like every other shield
        /// animation. Expired states are dropped here rather than kept as evidence:
        /// an old vanish must never be mistaken for one that is still running.
        /// </summary>
        internal static bool Progress(object comp, out float progress)
        {
            progress = 0f;
            TickManager? tickManager = Find.TickManager;
            if (comp == null
                || tickManager == null
                || !States.TryGetValue(comp, out State? state)
                || state == null
                || state.StartTick < 0)
            {
                return false;
            }

            int age = tickManager.TicksGame - state.StartTick;
            if (age < 0 || age >= state.DurationTicks)
            {
                state.StartTick = -1;
                return false;
            }

            progress = (float)age / state.DurationTicks;
            return true;
        }

        /// <summary>
        /// Drops a running vanish. Used when a break starts underneath one: the tear
        /// is the louder event and must not be stacked on a fade.
        /// </summary>
        internal static void Cancel(object comp)
        {
            if (comp != null
                && States.TryGetValue(comp, out State? state)
                && state != null)
            {
                state.StartTick = -1;
            }
        }

        /// <summary>Eased 0..1, shared with the restore animation so the two match pace.</summary>
        internal static float Ease(float progress)
        {
            return ShieldRestoreLog.Ease(progress);
        }
    }
}
