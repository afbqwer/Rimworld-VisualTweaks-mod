using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Adds the per-shield style gizmo and cleans up the per-object record when its
    /// owner is destroyed.
    ///
    /// The postfix sits on <see cref="ThingWithComps.GetGizmos"/> rather than on
    /// Pawn and Building separately: both eventually call that base method, so one
    /// patch covers every gizmo-producing host without replacing the eager pawn
    /// gizmo lists other mods may build.
    ///
    /// The gizmo only exists for a single selection, and only on the object that is
    /// selected. A multi-selection shows nothing: the bar is already crowded with
    /// the pawns' own commands and the player opens this window to style one shield,
    /// not to bulk-edit a drag box.
    ///
    /// That decision is made per call from the live selector and never cached. A
    /// frame stamp that let only the first caller of a frame answer was tried and
    /// taken back out: the gizmo bar rebuilds its list more than once inside one
    /// frame (a layout pass with the old selection, then another once the click has
    /// changed it), so the rebuild that gets drawn found the frame's answer already
    /// spent -- the button arrived a frame late, or blinked when another caller had
    /// taken the slot. One selector read is a few nanoseconds and removes both.
    /// </summary>
    internal static class ShieldOverridePatches
    {
        private const string GizmoIconPath = "UI/Commands/ChangeColor";
        private static Texture2D? gizmoIcon;
        private static bool gizmoIconResolved;

        internal static void Apply(Harmony harmony)
        {
            MethodInfo? target = AccessTools.Method(
                typeof(ThingWithComps),
                nameof(ThingWithComps.GetGizmos));
            MethodInfo? postfix = AccessTools.Method(
                typeof(ShieldOverridePatches),
                nameof(GetGizmosPostfix));

            if (target == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch ThingWithComps.GetGizmos;"
                    + " per-shield style gizmo unavailable.");
            }
            else
            {
                try
                {
                    harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                    VisualTweaksLog.Info("Patched ThingWithComps.GetGizmos"
                        + " (per-shield style gizmo).");
                }
                catch (Exception ex)
                {
                    Log.Error("[Visual Tweaks] Failed to patch per-shield style gizmo: "
                        + ex);
                }
            }

            PatchDestroyCleanup(harmony);
        }

        private static void PatchDestroyCleanup(Harmony harmony)
        {
            MethodInfo? target = AccessTools.Method(
                typeof(Thing),
                nameof(Thing.Destroy));
            MethodInfo? postfix = AccessTools.Method(
                typeof(ShieldOverridePatches),
                nameof(DestroyPostfix));

            if (target == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch Thing.Destroy; per-shield"
                    + " overrides of destroyed things will remain in the save.");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch Thing.Destroy cleanup: " + ex);
            }
        }

        private static void GetGizmosPostfix(
            ThingWithComps __instance,
            ref IEnumerable<Gizmo> __result)
        {
            if (!VisualTweaksSettings.shieldOverrideGizmo
                || __instance == null
                || __result == null)
            {
                return;
            }

            // Single selection only, and only on the selected object itself. This is
            // one selector read plus a reference compare per call -- cheaper than the
            // scan it skips, and stateless, so every rebuild of the gizmo bar in a
            // frame gets the same answer. It also drops the forwarded asks for free:
            // Corpse.GetGizmos hands its inner pawn to this method, and that pawn is
            // not what the player selected.
            if (!ReferenceEquals(Find.Selector?.SingleSelectedThing, __instance))
            {
                return;
            }

            try
            {
                if (TryGetShieldTarget(__instance, out ShieldHexTarget target))
                {
                    __result = Append(__result, CreateGizmo(__instance, target));
                }
            }
            catch (Exception ex)
            {
                // A broken shield comp in another mod must not take the whole gizmo
                // bar down with it.
                Log.ErrorOnce(
                    "[Visual Tweaks] Per-shield style gizmo failed: " + ex,
                    typeof(ShieldOverridePatches).GetHashCode());
            }
        }

        private static void DestroyPostfix(Thing __instance)
        {
            try
            {
                ShieldOverrideGameComponent.Remove(__instance);
            }
            catch (Exception ex)
            {
                // Cleanup is a convenience, never a reason to fail a Destroy.
                Log.ErrorOnce(
                    "[Visual Tweaks] Per-shield override cleanup failed: " + ex,
                    typeof(ShieldOverridePatches).GetHashCode() ^ 0x2D);
            }
        }

        private static Texture2D? GizmoIcon()
        {
            if (!gizmoIconResolved)
            {
                gizmoIconResolved = true;
                gizmoIcon = ContentFinder<Texture2D>.Get(GizmoIconPath, false);
            }

            return gizmoIcon;
        }

        private static IEnumerable<Gizmo> Append(
            IEnumerable<Gizmo> gizmos,
            Gizmo extra)
        {
            foreach (Gizmo gizmo in gizmos)
            {
                yield return gizmo;
            }

            yield return extra;
        }

        private static Gizmo CreateGizmo(
            ThingWithComps thing,
            ShieldHexTarget target)
        {
            return new Command_Action
            {
                defaultLabel = "VisualTweaks_ShieldOverride_GizmoLabel".Translate(),
                defaultDesc = "VisualTweaks_ShieldOverride_GizmoTip".Translate(),
                icon = GizmoIcon(),
                // The captured thing is the only thing this gizmo can ever belong to,
                // because it is only created for the current single selection.
                action = delegate { OpenFor(thing, target); },
            };
        }

        private static void OpenFor(ThingWithComps thing, ShieldHexTarget target)
        {
            Find.WindowStack?.Add(new Window_ShieldStyleOverride(
                new List<ShieldHost> { new ShieldHost(thing, target) }));
        }

        /// <summary>
        /// Only pawns and buildings get the gizmo; a belt or a shield-building's
        /// comp never becomes the host, so two entries can never edit one shield.
        /// Personal shields are checked first on a building and large shields first
        /// on a pawn, so the family that owns the drawn surface wins in the rare
        /// case an object has both.
        ///
        /// Reached at most once per frame, but the order of the branches is still the
        /// cost: the worn-apparel loop is a handful of comp lookups, and the VEF
        /// hediff walk is the expensive one, so it stays last and only runs for a
        /// pawn that has neither a belt nor a bubble. A pawn with VEF abilities and
        /// nothing else now reaches that walk instead of short-circuiting on the
        /// ability carrier's bubble base class, which is the price of not offering
        /// the gizmo where there is no shield -- one hediff scan per gizmo rebuild.
        /// </summary>
        private static bool TryGetShieldTarget(
            ThingWithComps thing,
            out ShieldHexTarget target)
        {
            target = ShieldHexTarget.Large;
            bool pawnLike = thing is Pawn;
            bool buildingLike = thing is Building;
            if (!pawnLike && !buildingLike)
            {
                return false;
            }

            if (pawnLike)
            {
                if (HasPersonalShield(thing))
                {
                    target = ShieldHexTarget.Personal;
                    return true;
                }

                if (HasLargeShield(thing))
                {
                    target = ShieldHexTarget.Large;
                    return true;
                }
            }
            else
            {
                if (HasLargeShield(thing))
                {
                    target = ShieldHexTarget.Large;
                    return true;
                }

                if (HasPersonalShield(thing))
                {
                    target = ShieldHexTarget.Personal;
                    return true;
                }
            }

            return false;
        }

        private static bool HasLargeShield(ThingWithComps thing)
        {
            if (thing.GetComp<CompProjectileInterceptor>() != null)
            {
                return true;
            }

            if (VefRepulsorShieldCompat.Available)
            {
                List<ThingComp> comps = thing.AllComps;
                for (int i = 0; i < comps.Count; i++)
                {
                    if (VefRepulsorShieldCompat.IsRepulsor(comps[i]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasPersonalShield(ThingWithComps thing)
        {
            if (thing.GetComp<CompShield>() != null)
            {
                return true;
            }

            if (VefShieldBubbleCompat.Available)
            {
                List<ThingComp> comps = thing.AllComps;
                for (int i = 0; i < comps.Count; i++)
                {
                    // IsShieldBubble, not IsBubble: VEF's CompAbilities derives from
                    // CompShieldBubble to reuse its charge machinery for ability
                    // barriers, so a type check alone would offer this gizmo on every
                    // pawn that has learned an ability.
                    if (VefShieldBubbleCompat.IsShieldBubble(comps[i]))
                    {
                        return true;
                    }
                }
            }

            if (thing is Pawn pawn)
            {
                if (pawn.apparel != null)
                {
                    List<Apparel> worn = pawn.apparel.WornApparel;
                    for (int i = 0; i < worn.Count; i++)
                    {
                        Apparel apparel = worn[i];
                        if (apparel.GetComp<CompShield>() != null)
                        {
                            return true;
                        }

                        if (VefShieldBubbleCompat.Available)
                        {
                            List<ThingComp> comps = apparel.AllComps;
                            for (int j = 0; j < comps.Count; j++)
                            {
                                if (VefShieldBubbleCompat.IsShieldBubble(comps[j]))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }

                if (VefHediffShieldCompat.PawnHasShield(pawn))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
