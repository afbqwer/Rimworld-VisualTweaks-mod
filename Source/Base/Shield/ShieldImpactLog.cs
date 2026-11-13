using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Recent interceptions per shield, newest last: bearing and damage.
    ///
    /// Vanilla keeps one <c>lastInterceptAngle</c> and one <c>lastInterceptTicks</c>,
    /// so a burst that resolves two directions in the same tick — or over a few ticks,
    /// which is every raid — leaves only whichever bullet was processed last. The
    /// earlier directions are overwritten before anything could draw them. That is not
    /// a rendering problem and cannot be fixed on the drawing side: the data is gone by
    /// the time PostDraw runs. So the patch records it here, at the exact point vanilla
    /// decides its own.
    ///
    /// Entries are never merged. An earlier version folded a burst within 8 ticks and
    /// 10 degrees into one entry and refreshed its angle — which is cheaper and keeps
    /// the flare count down, but it makes the drawn direction jump as each new shot
    /// overwrites the angle. Two shooters standing a few degrees apart produced a flare
    /// that visibly twitched between their bearings, and a burst looked like it was
    /// being dragged around. An entry whose angle is fixed for its whole life cannot
    /// twitch; keeping them apart costs at most <see cref="MaxPerShield"/> quads.
    ///
    /// Deliberately not scribed. A fade that survived a save/load would need its ticks
    /// re-based against a tick manager that may have advanced tens of thousands of
    /// ticks, and the payoff is at most a couple of seconds of a flare the player has
    /// already stopped looking at. Empty after load is the correct state.
    ///
    /// Keyed by the comp through a <see cref="ConditionalWeakTable{TKey,TValue}"/>, so
    /// a shield that is destroyed takes its list with it and there is no per-map
    /// teardown to get wrong.
    ///
    /// The key is the base <see cref="ThingComp"/> rather than the vanilla interceptor:
    /// the VEF repulsor drives its shield from a completely different comp type
    /// (CompShieldField) and feeds the same impact list. A comp of either kind is a
    /// distinct key, so the two families cannot read each other's bearings.
    /// </summary>
    internal static class ShieldImpactLog
    {
        /// <summary>
        /// How long one interception stays drawn. The default 40 matches vanilla's own
        /// cone fade (<c>GetCurrentConeAlpha_RecentlyIntercepted</c> divides by 40), so
        /// at the default the flare is gone on the tick the stock one would have been.
        /// The player can move it; a longer wave then reads as a slower spread rather
        /// than as a second event.
        /// </summary>
        internal static int LifetimeTicks =>
            Mathf.Max(1, Mathf.RoundToInt(VisualTweaksSettings.shieldImpactTicks));

        /// <summary>
        /// Hard cap, oldest dropped first. Eight simultaneous directions already covers
        /// a surrounded shield; the ninth adds draw calls to a spot that is already
        /// saturated white.
        /// </summary>
        private const int MaxPerShield = 8;

        private static readonly ConditionalWeakTable<ThingComp, List<Impact>>
            Impacts = new ConditionalWeakTable<ThingComp, List<Impact>>();

        internal struct Impact
        {
            /// <summary>
            /// Same convention as vanilla's <c>lastInterceptAngle</c>: the
            /// <c>AngleToFlat</c> bearing from the projectile toward the shield.
            /// Callers whose angle is a Unity Y-euler value (VEF's repulsor, for
            /// instance) must convert by subtracting 90 degrees before notifying.
            /// </summary>
            public float Angle;

            /// <summary>
            /// Damage the projectile would have dealt. The only honest measure of how
            /// hard the shield was hit: the impact itself carries no weight, and the
            /// shield ate it, so no other number exists.
            /// </summary>
            public float Damage;

            public int Tick;
        }

        internal static void Notify(ThingComp comp, float angle, float damage)
        {
            TickManager? tickManager = Find.TickManager;
            if (tickManager == null)
            {
                return;
            }

            int now = tickManager.TicksGame;
            List<Impact> list = Impacts.GetOrCreateValue(comp);

            Prune(list, now);

            if (list.Count >= MaxPerShield)
            {
                list.RemoveAt(0);
            }

            list.Add(new Impact { Angle = angle, Damage = damage, Tick = now });
        }

        /// <summary>
        /// The live list, or null if this shield has never been hit. Callers must not
        /// mutate it — entries are dropped by <see cref="Notify"/>, and a stale one is
        /// filtered out by age at draw time anyway.
        /// </summary>
        internal static List<Impact>? Peek(ThingComp comp)
        {
            return Impacts.TryGetValue(comp, out List<Impact>? list) ? list : null;
        }

        /// <summary>
        /// True while at least one recorded impact is still inside its lifetime —
        /// the same age test the draw loops apply. Lets a renderer skip resolving
        /// the material and its look uniforms on a shield that has not been hit
        /// recently. At most <see cref="MaxPerShield"/> entries, and the lookup is
        /// only reached when the surface is already invisible.
        /// </summary>
        internal static bool HasLive(ThingComp comp)
        {
            TickManager? tickManager = Find.TickManager;
            if (tickManager == null
                || !Impacts.TryGetValue(comp, out List<Impact>? list))
            {
                return false;
            }

            int now = tickManager.TicksGame;
            for (int i = 0; i < list.Count; i++)
            {
                int age = now - list[i].Tick;
                if (age >= 0 && age < LifetimeTicks)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Prune(List<Impact> list, int now)
        {
            int drop = 0;
            while (drop < list.Count && now - list[drop].Tick >= LifetimeTicks)
            {
                drop++;
            }

            if (drop > 0)
            {
                list.RemoveRange(0, drop);
            }
        }
    }
}
