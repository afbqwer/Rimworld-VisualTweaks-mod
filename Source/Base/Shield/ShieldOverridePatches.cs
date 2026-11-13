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
    /// </summary>
    internal static class ShieldOverridePatches
    {
        private const string GizmoIconPath = "UI/Commands/ChangeColor";

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
            ShieldOverrideGameComponent.Remove(__instance);
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
                icon = ContentFinder<Texture2D>.Get(GizmoIconPath, false),
                action = delegate
                {
                    Find.WindowStack?.Add(
                        new Window_ShieldStyleOverride(thing, target));
                },
            };
        }

        /// <summary>
        /// Only pawns and buildings get the gizmo; a belt or a shield-building's
        /// comp never becomes the host, so two entries can never edit one shield.
        /// Personal shields are checked first on a building and large shields first
        /// on a pawn, so the family that owns the drawn surface wins in the rare
        /// case an object has both.
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
                    if (VefShieldBubbleCompat.IsBubble(comps[i]))
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
                                if (VefShieldBubbleCompat.IsBubble(comps[j]))
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
