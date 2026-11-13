using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Hooks the two places a projectile interceptor reports a successful stop.
    ///
    /// Both methods return true only when the shield actually ate the shot, so the
    /// postfix reads <c>__result</c> instead of duplicating the whole gate (active,
    /// cooldown, faction, EMP, hit points). That is the difference between a patch
    /// that keeps working after a vanilla balance change and one that quietly starts
    /// spawning ripples for bullets the shield let through.
    ///
    /// CheckIntercept covers ground and air projectiles (the projectile's own step
    /// calls it). CheckBombardmentIntercept covers orbital/mortar-style bombardment,
    /// which never becomes a Projectile and would otherwise never ripple — that is the
    /// case a gravship shield exists for.
    /// </summary>
    internal static class ShieldRipplePatches
    {
        /// <summary>
        /// Bombardment projectiles carry no damage number of their own — the shell's
        /// effect is an explosion whose only dial is a radius range. This converts that
        /// radius into the same damage units the ripple response is tuned around, so a
        /// 6-8 cell bombardment lands at roughly two and a half times a bullet instead of
        /// needing a second code path. 5 damage per cell of blast radius is calibrated
        /// against the reference: vanilla shells run 6-8 cells, and 5*7 = 35 is a heavy
        /// hit next to a 12-damage bullet without being off the top of the curve.
        /// </summary>
        private const float BombardmentDamagePerRadius = 5f;

        internal static void Apply(Harmony harmony)
        {
            PatchIntercept(harmony);
            PatchBombardment(harmony);
        }

        private static void PatchIntercept(Harmony harmony)
        {
            MethodInfo? target = AccessTools.Method(
                typeof(CompProjectileInterceptor),
                nameof(CompProjectileInterceptor.CheckIntercept));
            MethodInfo? postfix = AccessTools.Method(
                typeof(ShieldRipplePatches),
                nameof(CheckInterceptPostfix));

            if (target == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch CompProjectileInterceptor.CheckIntercept.");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched CompProjectileInterceptor.CheckIntercept.");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch CheckIntercept: " + ex);
            }
        }

        private static void PatchBombardment(Harmony harmony)
        {
            MethodInfo? target = AccessTools.Method(
                typeof(CompProjectileInterceptor),
                nameof(CompProjectileInterceptor.CheckBombardmentIntercept));
            MethodInfo? postfix = AccessTools.Method(
                typeof(ShieldRipplePatches),
                nameof(CheckBombardmentInterceptPostfix));

            if (target == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch CheckBombardmentIntercept.");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched CompProjectileInterceptor.CheckBombardmentIntercept.");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch CheckBombardmentIntercept: " + ex);
            }
        }

        /// <summary>
        /// Parameter names must match the target's, including <c>newExactPos</c> and
        /// <c>projectile</c>: Harmony binds by name, and a rename here silently yields a
        /// zero vector or a null projectile.
        /// </summary>
        private static void CheckInterceptPostfix(
            CompProjectileInterceptor __instance,
            Projectile projectile,
            bool __result,
            Vector3 newExactPos)
        {
            if (!__result || __instance == null || projectile == null)
            {
                return;
            }

            // The shield already ate the shot, so the damage that would have landed is
            // the only honest measure of how hard it hit.
            NotifyIntercept(__instance, newExactPos, projectile.DamageAmount);
        }

        /// <summary>
        /// Shared by the vanilla patch and the Combat Extended bridge, which reaches
        /// this point from ProjectileCE.CheckIntercept instead.
        /// </summary>
        internal static void NotifyIntercept(
            CompProjectileInterceptor? interceptor,
            Vector3 hitPos,
            float damage)
        {
            if (interceptor == null || !ShieldRippleLayer.Active)
            {
                return;
            }

            Map? map = interceptor.parent?.Map;
            if (map == null)
            {
                return;
            }

            map.GetComponent<OverlayMapComponent>()?.NotifyShieldHit(
                interceptor,
                hitPos,
                damage);
        }

        private static void CheckBombardmentInterceptPostfix(
            CompProjectileInterceptor __instance,
            Bombardment bombardment,
            bool __result,
            Bombardment.BombardmentProjectile projectile)
        {
            if (!__result || __instance == null || projectile == null
                || !ShieldRippleLayer.Active)
            {
                return;
            }

            Map? map = __instance.parent?.Map;
            if (map == null)
            {
                return;
            }

            float damage = bombardment?.explosionRadiusRange.Average
                * BombardmentDamagePerRadius ?? 0f;

            // Bombardment only knows the target cell, not a crossing point; the layer
            // projects it onto the shield rim like every other impact.
            map.GetComponent<OverlayMapComponent>()?.NotifyShieldHit(
                __instance,
                projectile.targetCell.ToVector3Shifted(),
                damage);
        }
    }
}
