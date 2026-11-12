using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Bridges Combat Extended's projectile interception into the mod's shield effects.
    ///
    /// CE does not use <see cref="Projectile"/>: its ProjectileCE runs its own
    /// CheckIntercept, which duplicates the vanilla gate and calls BreakShield*
    /// directly. It therefore never calls
    /// <see cref="CompProjectileInterceptor.CheckIntercept"/>, and the two postfixes
    /// this mod hangs on that method (impact bearings and the shockwave ripple) are
    /// dead under CE. The result is a shield that stops shots with almost no visual
    /// feedback from this mod: neither the ripple nor the dome impact ever reaches
    /// the drawing code.
    ///
    /// The patch is installed only when CE is loaded and is bound by reflection, so
    /// the base assembly keeps working without CE and without a compile reference.
    /// CE's method already sets lastInterceptTicks/lastInterceptAngle; this postfix
    /// feeds the same two consumers vanilla's postfix feeds, using CE's own
    /// ExactPosition/LastPos conventions so both paths describe the same hit point.
    /// </summary>
    internal static class CombatExtendedShieldCompat
    {
        private const string ProjectileTypeName = "CombatExtended.ProjectileCE";

        private static PropertyInfo? exactPosition;
        private static PropertyInfo? damageAmount;
        private static FieldInfo? lastPos;

        internal static void Apply(Harmony harmony)
        {
            Type? projectileType = AccessTools.TypeByName(ProjectileTypeName);
            if (projectileType == null)
            {
                // CE absent is the normal case, not a warning.
                return;
            }

            MethodInfo? target = AccessTools.Method(
                projectileType,
                "CheckIntercept",
                new[] { typeof(Thing), typeof(CompProjectileInterceptor), typeof(bool) });
            MethodInfo? postfix = AccessTools.Method(
                typeof(CombatExtendedShieldCompat),
                nameof(CheckInterceptPostfix));

            if (target == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Combat Extended found, but ProjectileCE."
                    + "CheckIntercept has an unexpected signature; shield hit effects"
                    + " stay missing under CE.");
                return;
            }

            exactPosition = AccessTools.Property(projectileType, "ExactPosition");
            damageAmount = AccessTools.Property(projectileType, "DamageAmount");
            lastPos = AccessTools.Field(projectileType, "LastPos");

            if (exactPosition == null || damageAmount == null || lastPos == null)
            {
                Log.Warning("[Visual Tweaks] Combat Extended projectile state not found"
                    + " (ExactPosition/DamageAmount/LastPos); shield hit effects stay"
                    + " missing under CE.");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched CombatExtended.ProjectileCE.CheckIntercept"
                    + " (CE shield hit effects).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch ProjectileCE.CheckIntercept: "
                    + ex);
            }
        }

        /// <summary>
        /// Parameter names must match CE's method: Harmony binds by name, and CE's
        /// interceptor parameter is called <c>interceptorComp</c>, not <c>comp</c>.
        /// <c>__instance</c> is typed as the nearest vanilla base of ProjectileCE so
        /// this assembly needs no reference to CE's type.
        /// </summary>
        private static void CheckInterceptPostfix(
            ThingWithComps __instance,
            Thing interceptorThing,
            CompProjectileInterceptor interceptorComp,
            bool __result)
        {
            // CE calls this for every interceptor near the projectile each tick, but
            // only a successful stop has anything to draw.
            if (!__result
                || __instance == null
                || interceptorThing == null
                || interceptorComp == null)
            {
                return;
            }

            bool ripple = ShieldRippleLayer.Active;
            bool dome = ShieldDomeBridge.ShouldReplace(interceptorComp);
            if (!ripple && !dome)
            {
                return;
            }

            try
            {
                float damage = ReadFloat(damageAmount!, __instance);
                Vector3 hitPos = ReadVector3(exactPosition!, __instance);

                if (ripple)
                {
                    ShieldRipplePatches.NotifyIntercept(interceptorComp, hitPos, damage);
                }

                if (dome)
                {
                    // Same expression CE uses for its own lastInterceptAngle, so the
                    // reaction points the way CE recorded it.
                    Vector3 origin = ReadVector3(lastPos!, __instance);
                    float angle = origin.AngleToFlat(interceptorThing.TrueCenter());
                    ShieldDomePatches.NotifyIntercepted(interceptorComp, angle, damage);
                }
            }
            catch (Exception ex)
            {
                // This runs inside CE's projectile tick. One bad reflection read must
                // not take the shot's collision handling down with it, and ErrorOnce
                // keeps a persistent failure out of the log spam.
                Log.ErrorOnce("[Visual Tweaks] CE shield hit bridge failed: " + ex,
                    typeof(CombatExtendedShieldCompat).GetHashCode());
            }
        }

        private static float ReadFloat(PropertyInfo property, object instance)
        {
            object? value = property.GetValue(instance, null);
            return value is float number ? number : 0f;
        }

        private static Vector3 ReadVector3(PropertyInfo property, object instance)
        {
            object? value = property.GetValue(instance, null);
            return value is Vector3 vector ? vector : Vector3.zero;
        }

        private static Vector3 ReadVector3(FieldInfo field, object instance)
        {
            object? value = field.GetValue(instance);
            return value is Vector3 vector ? vector : Vector3.zero;
        }
    }
}
