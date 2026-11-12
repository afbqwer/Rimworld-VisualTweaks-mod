using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Takes over <see cref="CompProjectileInterceptor.PostDraw"/> and draws the shield
    /// with the mod's own shader.
    ///
    /// The prefix returns false, so vanilla's body never runs: this is a replacement,
    /// not an overlay. Drawing the new dome *on top* of the old one would leave the
    /// soft stock gradient underneath, which is exactly the part being removed.
    ///
    /// What is deliberately reused instead of reimplemented:
    ///
    /// <c>GetCurrentAlpha()</c> — the max of idle, selected, recently-intercepted,
    /// recently-activated and <c>Props.minAlpha</c>. Copying that formula would fork
    /// vanilla's visibility rules, and this mod already patches one of its terms
    /// (<see cref="ShieldVisibilityPatches"/>); calling the real method keeps that
    /// patch, and any balance change to the others, live.
    ///
    /// What is *not* reused is the interception bearing. Vanilla stores exactly one
    /// (<c>lastInterceptAngle</c> + <c>lastInterceptTicks</c>), so several hits in one
    /// tick collapse to the last one processed — a bug the drawing side cannot work
    /// around, because by then the data is gone. The second patch here records the
    /// bearing at the same point vanilla decides its own, and
    /// <see cref="ShieldImpactLog"/> keeps a short list; the renderer draws one flare
    /// per direction. That also removes this class's old dependency on the private
    /// field, and with it the one place a vanilla rename could have silently flipped
    /// the flare to the wrong side.
    /// </summary>
    internal static class ShieldDomePatches
    {
        private const string AlphaMethodName = "GetCurrentAlpha";

        private static Func<CompProjectileInterceptor, float>? alphaOf;
        private static bool fatal;

        internal static void Apply(Harmony harmony)
        {
            alphaOf = BindFloatGetter(AlphaMethodName);
            if (alphaOf == null)
            {
                // Without the alpha terms there is no honest way to know whether the
                // shield should be visible at all, so do not install either patch: an
                // always-on dome on every hidden shield would be far worse than the
                // stock look, and a flare log with nothing to draw is pure overhead.
                Log.Warning("[Visual Tweaks] Shield dome needs CompProjectileInterceptor."
                    + AlphaMethodName + "; patch not applied.");
                return;
            }

            PatchPostDraw(harmony);
            PatchCheckIntercept(harmony);
        }

        private static void PatchPostDraw(Harmony harmony)
        {
            MethodInfo? target = AccessTools.Method(
                typeof(CompProjectileInterceptor),
                nameof(CompProjectileInterceptor.PostDraw));
            MethodInfo? prefix = AccessTools.Method(
                typeof(ShieldDomePatches),
                nameof(PostDrawPrefix));

            if (target == null || prefix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch CompProjectileInterceptor.PostDraw.");
                return;
            }

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                VisualTweaksLog.Info("Patched CompProjectileInterceptor.PostDraw (shield dome).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch shield PostDraw: " + ex);
            }
        }

        private static void PatchCheckIntercept(Harmony harmony)
        {
            MethodInfo? target = AccessTools.Method(
                typeof(CompProjectileInterceptor),
                nameof(CompProjectileInterceptor.CheckIntercept));
            MethodInfo? postfix = AccessTools.Method(
                typeof(ShieldDomePatches),
                nameof(CheckInterceptPostfix));

            if (target == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch CompProjectileInterceptor.CheckIntercept"
                    + " for impact bearings; flare directions will be missing.");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched CompProjectileInterceptor.CheckIntercept (shield bearings).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch CheckIntercept bearings: " + ex);
            }
        }

        /// <summary>
        /// Priority.First because Harmony stops at the first prefix that returns false.
        /// Another mod that takes over PostDraw the same way would otherwise skip the
        /// original before this runs, and then neither drawing would appear.
        /// </summary>
        [HarmonyPriority(Priority.First)]
        private static bool PostDrawPrefix(CompProjectileInterceptor __instance)
        {
            // Returning true means "run vanilla". Every early exit below is a situation
            // where the stock drawing is the correct answer.
            if (fatal || !ShieldDomeBridge.Active || __instance == null)
            {
                return true;
            }

            try
            {
                ThingWithComps? parent = __instance.parent;
                if (parent == null || !parent.Spawned)
                {
                    return true;
                }

                Func<CompProjectileInterceptor, float>? alphaOfLocal = alphaOf;
                if (alphaOfLocal == null)
                {
                    return true;
                }

                // The dome's visibility and a flare's existence are independent: an
                // unselected player shield is fully transparent but still shows where
                // it was just hit, which is what vanilla does too. The renderer decides
                // whether either has anything to say.
                if (ShieldDomeRenderer.Draw(__instance, alphaOfLocal(__instance)))
                {
                    return false;
                }

                // Nothing drawn — no material yet, shader gone, radius unusable. Let
                // vanilla paint rather than leave a hole in the shield.
                return true;
            }
            catch (Exception ex)
            {
                // A throwing prefix would be retried every frame by every shield on the
                // map. Latch instead, and let vanilla draw from here on.
                fatal = true;
                Log.Error("[Visual Tweaks] Shield dome draw failed, stock drawing restored: "
                    + ex);
                return true;
            }
        }

        /// <summary>
        /// Parameter names must match the target's, including <c>lastExactPos</c> and
        /// <c>projectile</c>: Harmony binds by name, and a rename here silently yields a
        /// zero vector — i.e. every flare pointing along +x — or a zero damage.
        /// </summary>
        private static void CheckInterceptPostfix(
            CompProjectileInterceptor __instance,
            Projectile projectile,
            Vector3 lastExactPos,
            bool __result)
        {
            if (!__result || __instance == null || projectile == null
                || !ShieldDomeBridge.Active)
            {
                return;
            }

            ThingWithComps? parent = __instance.parent;
            if (parent == null)
            {
                return;
            }

            // Same expression vanilla uses for its own lastInterceptAngle, so a future
            // change to that convention moves both or neither.
            float angle = lastExactPos.AngleToFlat(parent.TrueCenter());

            // The damage the shield ate is the only measure of how hard the hit was:
            // the impact itself carries no weight, and the projectile is about to be
            // destroyed without ever landing.
            ShieldImpactLog.Notify(__instance, angle, projectile.DamageAmount);
        }

        /// <summary>
        /// Binds a private parameterless instance method returning float as an open
        /// delegate. One binding at startup means the per-frame cost is a delegate call,
        /// not reflection.
        /// </summary>
        private static Func<CompProjectileInterceptor, float>? BindFloatGetter(string name)
        {
            MethodInfo? method = AccessTools.Method(typeof(CompProjectileInterceptor), name);
            if (method == null
                || method.ReturnType != typeof(float)
                || method.GetParameters().Length != 0)
            {
                Log.Warning("[Visual Tweaks] CompProjectileInterceptor." + name
                    + " not found with the expected signature; shield dome disabled.");
                return null;
            }

            try
            {
                return (Func<CompProjectileInterceptor, float>)Delegate.CreateDelegate(
                    typeof(Func<CompProjectileInterceptor, float>),
                    method);
            }
            catch (Exception ex)
            {
                Log.Warning("[Visual Tweaks] Cannot bind CompProjectileInterceptor." + name
                    + ": " + ex.Message);
                return null;
            }
        }
    }
}
