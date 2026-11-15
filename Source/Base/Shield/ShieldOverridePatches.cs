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
    /// Only the first caller of a frame answers. Vanilla asks every selected object
    /// for its gizmos once per frame and then merges the identical commands back
    /// into one button, so every later answer was built only to be discarded -- and
    /// each one cost a full shield scan. One frame stamp makes the first caller
    /// answer for the whole selection, which keeps that scan at a single object per
    /// frame however large the selection is. A selection whose first object has no
    /// shield shows no gizmo; the player selects a shielded object directly.
    ///
    /// Two things about that stamp are load-bearing. Callers that cannot own a
    /// shield are rejected before it, because anything that asks first would
    /// otherwise take the frame's single answer (a corpse's inner pawn is the
    /// vanilla case). And a repeat caller of the same object in the same frame
    /// replays the stored answer, because the gizmo bar can rebuild its list more
    /// than once per frame and a dropped second answer is a button that blinks.
    /// </summary>
    internal static class ShieldOverridePatches
    {
        private const string GizmoIconPath = "UI/Commands/ChangeColor";
        private static Texture2D? gizmoIcon;
        private static bool gizmoIconResolved;

        /// <summary>
        /// The frame this postfix last answered, and the answer itself. Same clock
        /// <c>GizmoGridDrawer</c> caches on. Everything after the first call in a
        /// frame costs an int compare plus, for the same object, one reference
        /// compare.
        /// </summary>
        private static int answeredFrame = -1;
        private static ThingWithComps? answeredThing;
        private static ShieldHexTarget answeredTarget;
        private static bool answeredShielded;

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

            // Cheapest rejection first, and ahead of the frame stamp: an object that
            // is neither a pawn nor a building can never own a shield, so it must not
            // consume the frame's single answer. A corpse's inner pawn is the vanilla
            // case -- Corpse.GetGizmos forwards to it -- and without this it takes the
            // slot from the thing the player actually selected, which reads as a
            // blinking button.
            if (__instance is not Pawn && __instance is not Building)
            {
                return;
            }

            if (__instance is Pawn { Dead: true })
            {
                return;
            }

            // Every selected object is asked once per frame. Answering them all
            // means one shield scan each -- the VEF hediff walk is the expensive
            // one -- for commands vanilla then merges down to a single button. One
            // frame stamp replaces that with an int compare for every caller after
            // the first, and the stamp is taken before the scan so a first object
            // without a shield stops the rest of the frame too.
            int frame = Time.frameCount;
            if (frame == answeredFrame)
            {
                // The gizmo bar may ask about the same object again in the same frame
                // (it rebuilds when the selection changes between OnGUI passes, and a
                // mod can select something while another pane draws). Replaying the
                // stored answer keeps the button steady without a second scan; the
                // command is rebuilt rather than shared because vanilla's merge mutates
                // the commands it is given.
                if (answeredShielded && ReferenceEquals(answeredThing, __instance))
                {
                    __result = Append(__result, CreateGizmo(__instance, answeredTarget));
                }

                return;
            }

            answeredFrame = frame;
            answeredThing = __instance;
            answeredShielded = false;

            try
            {
                if (TryGetShieldTarget(__instance, out ShieldHexTarget target))
                {
                    answeredTarget = target;
                    answeredShielded = true;
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
                // The action reads the live selection rather than the captured
                // thing: the gizmo is only ever built for the first selected object,
                // but every selected shield is edited together by the one window
                // that opens.
                action = delegate { OpenForSelection(thing, target); },
            };
        }

        private static void OpenForSelection(
            ThingWithComps fallback,
            ShieldHexTarget fallbackTarget)
        {
            List<ShieldHost> hosts = CollectSelectedShieldHosts();

            if (hosts.Count == 0)
            {
                // Selection changed between drawing the gizmo and clicking it.
                hosts.Add(new ShieldHost(fallback, fallbackTarget));
            }

            Find.WindowStack?.Add(new Window_ShieldStyleOverride(hosts));
        }

        private static List<ShieldHost> CollectSelectedShieldHosts()
        {
            List<ShieldHost> hosts = new List<ShieldHost>();
            List<object>? selected = Find.Selector?.SelectedObjects;
            if (selected == null)
            {
                return hosts;
            }

            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i] is not ThingWithComps candidate)
                {
                    continue;
                }

                if (TryGetShieldTarget(candidate, out ShieldHexTarget target))
                {
                    hosts.Add(new ShieldHost(candidate, target));
                }
            }

            return hosts;
        }

        /// <summary>
        /// Only pawns and buildings get the gizmo; a belt or a shield-building's
        /// comp never becomes the host, so two entries can never edit one shield.
        /// Personal shields are checked first on a building and large shields first
        /// on a pawn, so the family that owns the drawn surface wins in the rare
        /// case an object has both.
        ///
        /// Runs for at most one object per frame, so the scans below are a
        /// per-frame cost of one object and not a per-selection cost.
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
