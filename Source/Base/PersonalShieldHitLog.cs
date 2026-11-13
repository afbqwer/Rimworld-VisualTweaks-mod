using System.Runtime.CompilerServices;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// The last absorbed hit on a personal shield.
    ///
    /// Vanilla keeps exactly two private fields for this — the bearing and the
    /// tick — and its <c>AbsorbedDamage</c> writes them for every non-breaking
    /// absorption. The custom renderer cannot read them without binding private
    /// state, and the write itself lives in a method the replacement does not call,
    /// so the same two values are recorded here from the public damage path instead.
    ///
    /// Vanilla belts, VEF shield bubbles and VEF hediff shields all feed this log.
    /// The key is <see cref="object"/> because those three are not even the same base
    /// type (the hediff comp is not a ThingComp); one entry per comp keeps a burst
    /// collapsing to the last hit processed, which is also what the stock bubble does.
    ///
    /// Keyed through a ConditionalWeakTable so a discarded belt takes it along, and
    /// not scribed: the reaction is a few ticks long and meaningless after a load.
    /// </summary>
    internal static class PersonalShieldHitLog
    {
        internal sealed class Hit
        {
            public int Tick;
            public float Angle;
        }

        private static readonly ConditionalWeakTable<object, Hit> Hits =
            new ConditionalWeakTable<object, Hit>();

        /// <summary>
        /// Records an absorbed hit. The angle is the damage direction — the same
        /// convention vanilla uses for <c>impactAngleVect</c>, i.e. along the shot.
        /// </summary>
        internal static void Notify(object comp, float angle)
        {
            TickManager? tickManager = Find.TickManager;
            if (comp == null || tickManager == null)
            {
                return;
            }

            Hit hit = Hits.GetOrCreateValue(comp);
            hit.Tick = tickManager.TicksGame;
            hit.Angle = angle;
        }

        internal static bool TryGet(
            object comp,
            out int tick,
            out float angle)
        {
            tick = -1;
            angle = 0f;

            if (comp == null
                || !Hits.TryGetValue(comp, out Hit? hit)
                || hit == null)
            {
                return false;
            }

            tick = hit.Tick;
            angle = hit.Angle;
            return true;
        }
    }
}
