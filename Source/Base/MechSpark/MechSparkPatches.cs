using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Hooks Bullet.Impact to spray procedural sparks when a shot lands on a
    /// mechanoid. The base Projectile.Impact cannot be patched instead: Harmony
    /// does not propagate a base-method patch into overrides, and Bullet.Impact
    /// is where every vanilla and most modded firearms end up.
    ///
    /// The capture happens in the prefix because Impact destroys the projectile:
    /// after Destroy, Map is null and the exact position is no longer reachable
    /// through the spawned-thing lists. The postfix only forwards the captured
    /// state, so a prefix from another mod that skips the original also skips
    /// the sparks.
    /// </summary>
    internal static class MechSparkPatches
    {
        internal static void Apply(Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(Bullet), "Impact");
            MethodInfo prefix = AccessTools.Method(
                typeof(MechSparkPatches),
                nameof(ImpactPrefix));
            MethodInfo postfix = AccessTools.Method(
                typeof(MechSparkPatches),
                nameof(ImpactPostfix));

            if (target == null || prefix == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch Bullet.Impact.");
                return;
            }

            try
            {
                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched Bullet.Impact.");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch Bullet.Impact: " + ex);
            }
        }

        private static void ImpactPrefix(
            Bullet __instance,
            Thing hitThing,
            bool blockedByShield,
            out SparkHitState __state)
        {
            __state = default;

            if (!MechSparkBridge.Active || blockedByShield || __instance == null)
            {
                return;
            }

            // Living mechanoids only: a corpse or a mechanoid building has no
            // race props, and a shot stopped by a shield never touched metal.
            if (hitThing?.def?.race?.IsMechanoid != true)
            {
                return;
            }

            Map? map = __instance.Map;
            if (map == null)
            {
                return;
            }

            __state = new SparkHitState
            {
                Map = map,
                Position = __instance.ExactPosition,
                // ExactRotation looks along (destination - origin), so its forward
                // is the shot's horizontal travel direction.
                TravelDir = __instance.ExactRotation * Vector3.forward,
                Damage = __instance.DamageAmount,
                Valid = true,
            };
        }

        private static void ImpactPostfix(SparkHitState __state)
        {
            if (!__state.Valid || __state.Map == null)
            {
                return;
            }

            OverlayMapComponent component =
                __state.Map.GetComponent<OverlayMapComponent>();
            component?.NotifyMechSpark(__state.Position, __state.TravelDir, __state.Damage);
        }

        private struct SparkHitState
        {
            public Map? Map;
            public Vector3 Position;
            public Vector3 TravelDir;
            public float Damage;
            public bool Valid;
        }
    }
}