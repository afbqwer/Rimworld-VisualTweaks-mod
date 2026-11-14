using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Per-shield dissolve state.
    ///
    /// Vanilla has no equivalent. Its Shield_Break effecter flashes a texture and
    /// plays a sound, but the dome itself disappears on the next frame because
    /// GetCurrentAlpha() collapses to zero as soon as Active is false. A dissolve
    /// therefore cannot be driven from that alpha: it needs its own clock, its own
    /// brightness and its own knowledge of where the failure started.
    ///
    /// Deliberately NOT driven by an Active edge. Active is false during cooldown,
    /// charging, stuns, shutdown, zero hit points, dormancy and the tail of a timed
    /// activation — in other words it flips to false after almost every ordinary
    /// interception. Watching it would fire a "break" animation on every bullet.
    /// The only honest signals are the four methods that actually end a shield,
    /// patched in ShieldDomePatches, plus the destruction of the host building.
    /// Destruction has no PostDraw left to draw through, so that path hands this same
    /// state to the map overlay instead of starting a separate animation; see
    /// <see cref="ShieldBreakLayer"/>. Destruction also starts its break from the
    /// centre of the disc rather than from a point on the rim — see
    /// <see cref="Begin"/>.
    ///
    /// Keyed by the comp through a ConditionalWeakTable, like the impact log. A
    /// destroyed generator's state is handed to the overlay queue before the comp goes
    /// away, so that one animation can outlive the thing; once the queue drops it, the
    /// weak-table entry follows. Not scribed: a dissolve that
    /// survived a save/load would need its start tick re-based against a clock that
    /// may have advanced by tens of thousands of ticks, for at most 1.5 seconds of
    /// an animation nobody is watching. Empty after load is correct.
    ///
    /// The key is the base <see cref="ThingComp"/>: the VEF repulsor ends its shield
    /// through CompShieldField, not CompProjectileInterceptor, and both feed this log.
    /// </summary>
    internal static class ShieldDissolveLog
    {
        internal sealed class State
        {
            public int StartTick;
            public int DurationTicks;

            /// <summary>
            /// Where the failure started, as a point in the shield's disc: a unit
            /// vector for a wound on the rim, (0,0) for the centre. The shader reads
            /// the zero case as "distance from the middle", so a centred break
            /// travels outward from the middle of the dome instead of tearing in
            /// from one side.
            /// </summary>
            public float OriginX;
            public float OriginZ;

            /// <summary>Offsets the noise field so two breaks never look identical.</summary>
            public float Seed;
        }

        private static readonly ConditionalWeakTable<ThingComp, State>
            States = new ConditionalWeakTable<ThingComp, State>();

        /// <summary>
        /// Starts (or restarts) a dissolve. Called from the four shutdown entry points
        /// and from host destruction, and only when the custom dome is actually
        /// installed. Returns the new state so the destruction path can hand it to the
        /// overlay draw queue; the ordinary shutdown paths ignore the result.
        ///
        /// <paramref name="fromCentre"/> makes both break animations start in the
        /// middle of the disc and travel outward, instead of tearing in from a point
        /// on the rim. This is what destruction uses: nothing was hit and the shield
        /// itself is what went away, so a wound at an arbitrary bearing would point at
        /// a cause that does not exist.
        /// </summary>
        internal static State? Begin(ThingComp comp, bool fromCentre = false)
        {
            TickManager? tickManager = Find.TickManager;
            ThingWithComps? parent = comp.parent;
            if (tickManager == null || parent == null)
            {
                return null;
            }

            int now = tickManager.TicksGame;

            // Origin: (0,0) is the centre, a unit vector is a point on the rim.
            float ox = 0f;
            float oz = 0f;
            if (!fromCentre)
            {
                // Fallback origin: the most recent recorded hit. Both break methods
                // run from inside CheckIntercept, and RefreshOrigin below will
                // replace this with the fatal shot's own bearing before the frame is
                // drawn.
                ox = 1f;
                System.Collections.Generic.List<ShieldImpactLog.Impact>? impacts =
                    ShieldImpactLog.Peek(comp);
                if (impacts != null && impacts.Count > 0)
                {
                    Vector3 toward = Vector3Utility.FromAngleFlat(
                        impacts[impacts.Count - 1].Angle);
                    ox = -toward.x;
                    oz = -toward.z;
                }
                else
                {
                    // Deterministic, so a given building always breaks from the same
                    // side when there was no projectile to blame (lord destroyed,
                    // switched off).
                    float a = (parent.thingIDNumber * 0.6180339887f % 1f)
                        * Mathf.PI * 2f;
                    ox = Mathf.Cos(a);
                    oz = Mathf.Sin(a);
                }
            }

            State state = States.GetOrCreateValue(comp);
            state.StartTick = now;
            state.DurationTicks = Mathf.Max(
                1,
                Mathf.RoundToInt(VisualTweaksSettings.largeShieldDissolveTicks));
            state.OriginX = ox;
            state.OriginZ = oz;
            // Seed must stay small. It is added to the noise lattice coordinates
            // in the shader, and the GPU works in float32: once a lattice index
            // reaches a few thousand, fract(index * 456.21) loses its fractional
            // bits and neighbouring cells hash to the same value. The result is
            // large square blocks in-game while the same formula looks perfectly
            // organic in a float64 web preview. Feeding raw TicksGame here was the
            // cause — a few hours of play is already several thousand.
            //
            // HashCombineInt gives a different pattern per break without ever
            // producing a large number, and the shader's hash is independently
            // hardened so a future large seed cannot reintroduce the bug.
            int hash = Gen.HashCombineInt(parent.thingIDNumber, now);
            state.Seed = (hash & 0xFFFF) / 65535f * 4f;
            return state;
        }

        /// <summary>
        /// Called from CheckIntercept's postfix. BreakShield* runs *inside* that method,
        /// before the postfix, so at Begin() time the fatal shot is not in the impact
        /// log yet. This lets it correct the tear's origin without giving the break
        /// patches a dependency on private fields or on CheckIntercept's parameters.
        /// Only touches a dissolve that started on this exact tick.
        /// </summary>
        internal static void RefreshOrigin(
            ThingComp comp,
            float interceptAngle,
            int now)
        {
            if (!States.TryGetValue(comp, out State? state)
                || state == null
                || state.StartTick != now)
            {
                return;
            }

            // The logged angle is the bearing from the projectile toward the centre;
            // the tear starts at the projectile's side, which is the opposite vector.
            Vector3 toward = Vector3Utility.FromAngleFlat(interceptAngle);
            state.OriginX = -toward.x;
            state.OriginZ = -toward.z;
        }

        /// <summary>
        /// The live dissolve, or null once it has finished. Progress is in 0..1 and
        /// advances on TicksGame, so the animation freezes with the rest of the world
        /// when the game is paused and speeds up with the game's own time controls —
        /// exactly like a vanilla mote.
        ///
        /// <paramref name="existing"/> is whatever state the table holds, running or
        /// expired, so <see cref="TryConsumeFinished"/> can answer from the same
        /// lookup instead of peeking a second time.
        /// </summary>
        internal static State? Progress(
            ThingComp comp,
            out float progress,
            out State? existing)
        {
            progress = 0f;
            existing = null;
            TickManager? tickManager = Find.TickManager;
            if (tickManager == null
                || !States.TryGetValue(comp, out State? state)
                || state == null)
            {
                return null;
            }

            existing = state;

            int age = tickManager.TicksGame - state.StartTick;
            if (age < 0 || age >= state.DurationTicks)
            {
                progress = 1f;
                return null;
            }

            progress = (float)age / state.DurationTicks;
            return state;
        }

        /// <summary>
        /// Removes a break that has finished and reports whether the shield coming
        /// back should still be animated.
        ///
        /// The normal up edge is observed by <see cref="ShieldRestoreLog"/>. That fails
        /// when a shield is disabled and re-enabled between two draws (or inside one
        /// tick): no frame ever sees it down, so no edge exists to observe. The break
        /// state is the evidence that it did go down, so a break that has just run out
        /// becomes the "it is back" signal. Only a break that ended within one duration
        /// counts; an old state left over while the shield stayed down must not fade it
        /// in when it finally returns — Observe handles that case through the down edge.
        ///
        /// Takes the state <see cref="Progress"/> already fetched; null means the table
        /// held nothing, which is the same answer the lookup used to give.
        /// </summary>
        internal static bool TryConsumeFinished(ThingComp comp, State? state)
        {
            TickManager? tickManager = Find.TickManager;
            if (tickManager == null || comp == null || state == null)
            {
                return false;
            }

            int end = state.StartTick + state.DurationTicks;
            if (tickManager.TicksGame < end)
            {
                return false;
            }

            States.Remove(comp);
            return tickManager.TicksGame - end <= state.DurationTicks;
        }

        /// <summary>
        /// Ends a dissolve early. Used when a shield comes back before the animation
        /// has played out — a recharging generator must not keep showing a dome that
        /// is still falling apart.
        /// </summary>
        internal static void Cancel(ThingComp comp)
        {
            States.Remove(comp);
        }
    }
}
