using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Recent interception bearings per shield, newest last.
    ///
    /// Vanilla keeps one <c>lastInterceptAngle</c> and one <c>lastInterceptTicks</c>,
    /// so a burst that resolves two directions in the same tick — or over a few
    /// ticks, which is every raid — leaves only whichever bullet was processed last.
    /// The earlier directions are overwritten before anything could draw them. That
    /// is not a rendering problem and cannot be fixed on the drawing side: the data
    /// is gone by the time PostDraw runs.
    ///
    /// So the patch records the bearing itself, at the exact point vanilla decides
    /// its own, and this keeps a short list instead of a single slot.
    ///
    /// Deliberately not scribed. A fade that survived a save/load would need its
    /// ticks re-based against a tick manager that may have advanced tens of thousands
    /// of ticks, and the payoff is at most 40 ticks of a flare the player has already
    /// stopped looking at. Empty after load is the correct state.
    ///
    /// Keyed by the comp through a <see cref="ConditionalWeakTable{TKey,TValue}"/>,
    /// so a shield that is destroyed or despawned for good takes its list with it and
    /// there is no per-map teardown to get wrong.
    /// </summary>
    internal static class ShieldImpactLog
    {
        /// <summary>
        /// Matches vanilla's own cone fade (GetCurrentConeAlpha_RecentlyIntercepted
        /// divides by 40). Keeping the same number means the flare is gone on the
        /// tick the stock one would have been, so switching this setting on and off
        /// does not change how long a hit stays readable.
        /// </summary>
        internal const int LifetimeTicks = 40;

        /// <summary>
        /// Hard cap. Eight simultaneous directions already covers a surrounded shield;
        /// beyond that the flares overlap into a full ring and say nothing.
        /// </summary>
        private const int MaxPerShield = 8;

        /// <summary>
        /// A burst from one shooter at one bearing refreshes the newest entry instead
        /// of appending, so a charge lance volley reads as sustained fire rather than
        /// as eight overlapping copies of the same flare.
        /// </summary>
        private const int MergeWindowTicks = 8;

        /// <summary>
        /// Wide enough to merge adjacent shooters standing shoulder to shoulder,
        /// narrow enough that two genuinely different directions stay apart.
        /// </summary>
        private const float MergeAngleDegrees = 10f;

        private static readonly ConditionalWeakTable<CompProjectileInterceptor, List<Impact>>
            Impacts = new ConditionalWeakTable<CompProjectileInterceptor, List<Impact>>();

        internal struct Impact
        {
            /// <summary>Same convention as vanilla: bearing from the projectile toward the shield.</summary>
            public float Angle;

            public int Tick;
        }

        internal static void Notify(CompProjectileInterceptor comp, float angle)
        {
            TickManager? tickManager = Find.TickManager;
            if (tickManager == null)
            {
                return;
            }

            int now = tickManager.TicksGame;
            List<Impact> list = Impacts.GetOrCreateValue(comp);

            // Newest first: a burst arrives in ascending tick order, so the first
            // recent-enough entry with a matching bearing is the one to refresh.
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Impact existing = list[i];
                if (now - existing.Tick > MergeWindowTicks)
                {
                    break;
                }

                if (Mathf.Abs(Mathf.DeltaAngle(existing.Angle, angle)) <= MergeAngleDegrees)
                {
                    list[i] = new Impact { Angle = angle, Tick = now };
                    return;
                }
            }

            Prune(list, now);

            if (list.Count >= MaxPerShield)
            {
                list.RemoveAt(0);
            }

            list.Add(new Impact { Angle = angle, Tick = now });
        }

        /// <summary>
        /// The live list, or null if this shield has never been hit. Callers must not
        /// mutate it — entries are dropped by <see cref="Notify"/>, and a stale one is
        /// filtered out by age at draw time anyway.
        /// </summary>
        internal static List<Impact>? Peek(CompProjectileInterceptor comp)
        {
            return Impacts.TryGetValue(comp, out List<Impact>? list) ? list : null;
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
