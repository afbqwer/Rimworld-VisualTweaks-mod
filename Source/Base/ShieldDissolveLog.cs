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
    /// patched in ShieldDomePatches.
    ///
    /// Keyed by the comp through a ConditionalWeakTable, like the impact log, so a
    /// destroyed generator takes its state with it. Not scribed: a dissolve that
    /// survived a save/load would need its start tick re-based against a clock that
    /// may have advanced by tens of thousands of ticks, for at most 1.5 seconds of
    /// an animation nobody is watching. Empty after load is correct.
    /// </summary>
    internal static class ShieldDissolveLog
    {
        internal sealed class State
        {
            public int StartTick;
            public int DurationTicks;

            /// <summary>Unit direction in world xz toward the point that failed.</summary>
            public float OriginX;
            public float OriginZ;

            /// <summary>Offsets the noise field so two breaks never look identical.</summary>
            public float Seed;

            /// <summary>
            /// True once at least one frame has been seen with Active == false.
            /// Deactivate() does not flip Active in the same tick — it arms the charge
            /// cycle, which only takes effect on the next one — so on the frame the
            /// dissolve starts the shield can still report itself as active. Watching
            /// Active alone would cancel the animation before its first frame.
            /// </summary>
            public bool SawInactive;
        }

        private static readonly ConditionalWeakTable<CompProjectileInterceptor, State>
            States = new ConditionalWeakTable<CompProjectileInterceptor, State>();

        /// <summary>
        /// Starts (or restarts) a dissolve. Called from the four shutdown entry points,
        /// and only when the custom dome is actually installed.
        /// </summary>
        internal static void Begin(CompProjectileInterceptor comp)
        {
            TickManager? tickManager = Find.TickManager;
            ThingWithComps? parent = comp.parent;
            if (tickManager == null || parent == null)
            {
                return;
            }

            int now = tickManager.TicksGame;

            // Fallback origin: the most recent recorded hit. Both break methods run
            // from inside CheckIntercept, and RefreshOrigin below will replace this
            // with the fatal shot's own bearing before the frame is drawn.
            float ox = 1f;
            float oz = 0f;
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
                // Deterministic, so a given building always breaks from the same side
                // when there was no projectile to blame (lord destroyed, switched off).
                float a = (parent.thingIDNumber * 0.6180339887f % 1f) * Mathf.PI * 2f;
                ox = Mathf.Cos(a);
                oz = Mathf.Sin(a);
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
            state.SawInactive = false;
        }

        /// <summary>
        /// Called from CheckIntercept's postfix. BreakShield* runs *inside* that method,
        /// before the postfix, so at Begin() time the fatal shot is not in the impact
        /// log yet. This lets it correct the tear's origin without giving the break
        /// patches a dependency on private fields or on CheckIntercept's parameters.
        /// Only touches a dissolve that started on this exact tick.
        /// </summary>
        internal static void RefreshOrigin(
            CompProjectileInterceptor comp,
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
        /// </summary>
        internal static State? Progress(CompProjectileInterceptor comp, out float progress)
        {
            progress = 0f;
            TickManager? tickManager = Find.TickManager;
            if (tickManager == null
                || !States.TryGetValue(comp, out State? state)
                || state == null)
            {
                return null;
            }

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
        /// Ends a dissolve early. Used when a shield comes back before the animation
        /// has played out — a recharging generator must not keep showing a dome that
        /// is still falling apart.
        /// </summary>
        internal static void Cancel(CompProjectileInterceptor comp)
        {
            States.Remove(comp);
        }
    }
}
