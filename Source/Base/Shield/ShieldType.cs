using System.Collections.Generic;
using RimWorld;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// One shield a host actually carries: the family it draws in and the def that
    /// owns it.
    /// </summary>
    internal readonly struct ShieldSource
    {
        internal readonly ShieldHexTarget Target;
        internal readonly Def Def;

        internal ShieldSource(ShieldHexTarget target, Def def)
        {
            Target = target;
            Def = def;
        }

        internal string DefName => Def.defName;

        internal string Label => Def.LabelCap.ToString();

        internal bool IsPersonal => Target == ShieldHexTarget.Personal;
    }

    /// <summary>
    /// Enumerates the shields a thing carries, and names each one by the def that
    /// draws it.
    ///
    /// The type layer is keyed by that def, not by the thing carrying it: a shield
    /// belt is <c>Apparel_ShieldBelt</c> whoever wears it, a mech's built-in shield is
    /// the mech's race def, a projector dome is the building def, and a VEF hediff
    /// shield is the hediff def. That is the granularity a player means by "this kind
    /// of shield", and it stays useful across a colony instead of being another
    /// per-pawn entry.
    ///
    /// A host can carry more than one -- a shield belt plus a hediff shield is the
    /// common case in psycast modpacks -- and none of them can be selected on its own,
    /// because the player only ever selects the pawn. The window therefore lists what
    /// this walk found and lets the player pick which type to edit.
    ///
    /// The renderers do not use this walk: each passes the def of the comp it is
    /// drawing, which stays exact even when a host carries two shields of one family.
    /// </summary>
    internal static class ShieldType
    {
        /// <summary>
        /// Appends every shield on <paramref name="host"/> that this mod can style,
        /// deduplicated by family and def. Personal shields come first, matching the
        /// order the style window groups families in.
        /// </summary>
        internal static void CollectSources(Thing? host, List<ShieldSource> into)
        {
            if (!(host is ThingWithComps thing) || thing.def == null)
            {
                return;
            }

            // A comp on the thing itself belongs to the thing's own def: a mech race
            // def that ships CompShield, or a modded building that does the same.
            if (thing.GetComp<CompShield>() != null)
            {
                Add(into, ShieldHexTarget.Personal, thing.def);
            }

            if (VefShieldBubbleCompat.Available)
            {
                List<ThingComp> comps = thing.AllComps;
                for (int i = 0; i < comps.Count; i++)
                {
                    if (VefShieldBubbleCompat.IsBubble(comps[i]))
                    {
                        Add(into, ShieldHexTarget.Personal, thing.def);
                        break;
                    }
                }
            }

            if (thing is Pawn pawn)
            {
                CollectWorn(pawn, into);
            }

            if (thing.GetComp<CompProjectileInterceptor>() != null)
            {
                Add(into, ShieldHexTarget.Large, thing.def);
            }

            if (VefRepulsorShieldCompat.Available)
            {
                List<ThingComp> comps = thing.AllComps;
                for (int i = 0; i < comps.Count; i++)
                {
                    if (VefRepulsorShieldCompat.IsRepulsor(comps[i]))
                    {
                        Add(into, ShieldHexTarget.Large, thing.def);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// A worn belt owns its own comp, so its def -- not the wearer's -- is the
        /// type. This is the common case for colonists, and usually the only entry the
        /// walk produces.
        /// </summary>
        private static void CollectWorn(Pawn pawn, List<ShieldSource> into)
        {
            if (pawn.apparel != null)
            {
                List<Apparel> worn = pawn.apparel.WornApparel;
                for (int i = 0; i < worn.Count; i++)
                {
                    Apparel apparel = worn[i];
                    if (apparel.def == null)
                    {
                        continue;
                    }

                    if (apparel.GetComp<CompShield>() != null)
                    {
                        Add(into, ShieldHexTarget.Personal, apparel.def);
                    }

                    if (VefShieldBubbleCompat.Available)
                    {
                        List<ThingComp> comps = apparel.AllComps;
                        for (int j = 0; j < comps.Count; j++)
                        {
                            if (VefShieldBubbleCompat.IsBubble(comps[j]))
                            {
                                Add(into, ShieldHexTarget.Personal, apparel.def);
                                break;
                            }
                        }
                    }
                }
            }

            HediffDef? hediff = VefHediffShieldCompat.ShieldHediffDefOf(pawn);
            if (hediff != null)
            {
                Add(into, ShieldHexTarget.Personal, hediff);
            }
        }

        private static void Add(
            List<ShieldSource> into,
            ShieldHexTarget target,
            Def def)
        {
            for (int i = 0; i < into.Count; i++)
            {
                if (into[i].Target == target && ReferenceEquals(into[i].Def, def))
                {
                    return;
                }
            }

            into.Add(new ShieldSource(target, def));
        }
    }
}
