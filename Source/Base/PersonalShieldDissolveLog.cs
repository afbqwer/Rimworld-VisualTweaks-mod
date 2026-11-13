using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Per-personal-shield dissolve state, the personal analogue of
    /// <see cref="ShieldDissolveLog"/>.
    ///
    /// A broken shield stops drawing on the same frame it breaks: vanilla's
    /// <c>CompShield.Draw()</c> requires <c>ShieldState.Active</c>, and <c>Break()</c>
    /// immediately moves the comp to Resetting. VEF's bubble shield behaves the same
    /// way. The custom break therefore has to carry its own clock, brightness and
    /// origin, exactly like the dome's.
    ///
    /// Started from the public damage method of each comp (vanilla's
    /// <c>PostPreApplyDamage</c>, VEF's own), not from the private <c>Break()</c>:
    /// watching the shield's state fall out of Active during that call is the same
    /// signal without depending on a method name that either mod is free to rename.
    ///
    /// The key is <see cref="object"/> because the three families are different comp
    /// types — vanilla belts, VEF bubbles and VEF hediff shields — feeding the same
    /// animation, and the hediff comp is not even a ThingComp.
    ///
    /// Keyed through a ConditionalWeakTable so a discarded belt takes its state with
    /// it, and not scribed: a dissolve surviving a save would need its start tick
    /// rebased against a clock that may have advanced arbitrarily, for at most a
    /// second and a half of animation.
    /// </summary>
    internal static class PersonalShieldDissolveLog
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
        }

        private static readonly ConditionalWeakTable<object, State> States =
            new ConditionalWeakTable<object, State>();

        /// <summary>
        /// Starts (or restarts) a dissolve. The angle is the damage direction — from
        /// the attacker towards the belt — so the tear opens on the side that was hit.
        /// Null means no useful bearing (a dev break, for instance); a deterministic
        /// side is chosen from the thing id instead.
        /// </summary>
        internal static void Begin(object comp, Thing? host, float? damageAngle)
        {
            TickManager? tickManager = Find.TickManager;
            if (comp == null || host == null || tickManager == null)
            {
                return;
            }

            int now = tickManager.TicksGame;
            float ox;
            float oz;

            if (damageAngle.HasValue)
            {
                // The stored angle is a DamageInfo.Angle, so it is in Unity's Y-euler
                // convention, not the AngleToFlat convention the dome's log uses.
                // HorizontalVectorFromAngle gives the shot's own direction; the tear
                // opens on the opposite side, where the round came from.
                Vector3 toward = Vector3Utility.HorizontalVectorFromAngle(
                    damageAngle.Value);
                ox = -toward.x;
                oz = -toward.z;
            }
            else
            {
                float a = (host.thingIDNumber * 0.6180339887f % 1f) * Mathf.PI * 2f;
                ox = Mathf.Cos(a);
                oz = Mathf.Sin(a);
            }

            State state = States.GetOrCreateValue(comp);
            state.StartTick = now;
            state.DurationTicks = Mathf.Max(
                1,
                Mathf.RoundToInt(VisualTweaksSettings.personalShieldDissolveTicks));
            state.OriginX = ox;
            state.OriginZ = oz;
            // Small on purpose: the shader adds this to noise lattice coordinates and
            // works in float32. Feeding raw ticks made the hash collapse into square
            // blocks after a few hours of play. See ShieldDissolveLog for the details.
            int hash = Gen.HashCombineInt(host.thingIDNumber, now);
            state.Seed = (hash & 0xFFFF) / 65535f * 4f;
        }

        /// <summary>
        /// The live dissolve, or null once it has finished. Progress is in 0..1 and
        /// advances on TicksGame, so the animation pauses and speeds up with the game
        /// clock like a vanilla mote.
        /// </summary>
        internal static State? Progress(object comp, out float progress)
        {
            progress = 0f;
            TickManager? tickManager = Find.TickManager;
            if (comp == null
                || tickManager == null
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
        /// Removes a break that has finished and reports whether the shield coming
        /// back should still be animated. Same rule and same reason as
        /// <see cref="ShieldDissolveLog.TryConsumeFinished"/>: a disable and re-enable
        /// between two draws leaves no down edge for
        /// <see cref="ShieldRestoreLog.Observe"/> to see.
        /// </summary>
        internal static bool TryConsumeFinished(object comp)
        {
            TickManager? tickManager = Find.TickManager;
            if (tickManager == null
                || comp == null
                || !States.TryGetValue(comp, out State? state)
                || state == null)
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

        /// <summary>Ends a dissolve early, when the belt is back before it finishes.</summary>
        internal static void Cancel(object comp)
        {
            if (comp != null)
            {
                States.Remove(comp);
            }
        }
    }
}
