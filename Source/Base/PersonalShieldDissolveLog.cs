using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Per-belt dissolve state, the personal-shield analogue of
    /// <see cref="ShieldDissolveLog"/>.
    ///
    /// A broken belt stops drawing on the same frame it breaks: vanilla's
    /// <c>CompShield.Draw()</c> requires <c>ShieldState.Active</c>, and <c>Break()</c>
    /// immediately moves the comp to Resetting. The custom break therefore has to
    /// carry its own clock, brightness and origin, exactly like the dome's.
    ///
    /// Started from <c>PostPreApplyDamage</c>, which is the public method that both
    /// break paths go through, and not from the private <c>Break()</c>: watching the
    /// shield's state fall out of Active during that call is the same signal without
    /// depending on a method name that vanilla is free to rename.
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

            /// <summary>
            /// True once a frame has been seen with the shield not Active. A comp that
            /// recharges before the animation ends must not keep painting a torn dome
            /// over itself, but on the first frame after a break the state can still
            /// report Active for the rest of the tick.
            /// </summary>
            public bool SawInactive;
        }

        private static readonly ConditionalWeakTable<CompShield, State> States =
            new ConditionalWeakTable<CompShield, State>();

        /// <summary>
        /// Starts (or restarts) a dissolve. The angle is the damage direction — from
        /// the attacker towards the belt — so the tear opens on the side that was hit.
        /// Null means no useful bearing (a dev break, for instance); a deterministic
        /// side is chosen from the thing id instead.
        /// </summary>
        internal static void Begin(CompShield comp, float? damageAngle)
        {
            TickManager? tickManager = Find.TickManager;
            ThingWithComps? parent = comp?.parent;
            if (comp == null || tickManager == null || parent == null)
            {
                return;
            }

            int now = tickManager.TicksGame;
            float ox;
            float oz;

            if (damageAngle.HasValue)
            {
                // The same convention as the dome: the stored angle points along the
                // shot, so the failure point is the opposite vector.
                Vector3 toward = Vector3Utility.FromAngleFlat(damageAngle.Value);
                ox = -toward.x;
                oz = -toward.z;
            }
            else
            {
                float a = (parent.thingIDNumber * 0.6180339887f % 1f) * Mathf.PI * 2f;
                ox = Mathf.Cos(a);
                oz = Mathf.Sin(a);
            }

            State state = States.GetOrCreateValue(comp);
            state.StartTick = now;
            state.DurationTicks = Mathf.Max(
                1,
                Mathf.RoundToInt(VisualTweaksSettings.shieldDissolveTicks));
            state.OriginX = ox;
            state.OriginZ = oz;
            // Small on purpose: the shader adds this to noise lattice coordinates and
            // works in float32. Feeding raw ticks made the hash collapse into square
            // blocks after a few hours of play. See ShieldDissolveLog for the details.
            int hash = Gen.HashCombineInt(parent.thingIDNumber, now);
            state.Seed = (hash & 0xFFFF) / 65535f * 4f;
            state.SawInactive = false;
        }

        /// <summary>
        /// The live dissolve, or null once it has finished. Progress is in 0..1 and
        /// advances on TicksGame, so the animation pauses and speeds up with the game
        /// clock like a vanilla mote.
        /// </summary>
        internal static State? Progress(CompShield comp, out float progress)
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

        /// <summary>Ends a dissolve early, when the belt is back before it finishes.</summary>
        internal static void Cancel(CompShield comp)
        {
            if (comp != null)
            {
                States.Remove(comp);
            }
        }
    }
}
