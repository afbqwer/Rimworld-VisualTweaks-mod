using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Hooks the final explosion spawn. StartExplosion already carries the final radius,
    /// affected angle and doVisualEffects flag, and every vanilla (and most modded)
    /// explosion ends up here through GenExplosion.DoExplosion.
    /// </summary>
    internal static class ExplosionRipplePatches
    {
        internal static void Apply(Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(
                typeof(Explosion),
                nameof(Explosion.StartExplosion));
            MethodInfo prefix = AccessTools.Method(
                typeof(ExplosionRipplePatches),
                nameof(StartExplosionPrefix));
            MethodInfo postfix = AccessTools.Method(
                typeof(ExplosionRipplePatches),
                nameof(StartExplosionPostfix));

            if (target == null || prefix == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch Explosion.StartExplosion.");
                return;
            }

            try
            {
                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched Explosion.StartExplosion.");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch Explosion.StartExplosion: " + ex);
            }
        }

        /// <summary>
        /// Captures the visual flag on the way in, before any other mod's postfix can
        /// touch it. Dynamic Effects Forge ("blues.forge") overrides vanilla explosion
        /// visuals by setting <c>doVisualEffects = false</c> from its own postfix on
        /// this method. Harmony runs postfixes in owner order ("blues.forge" sorts
        /// before "local.visualtweaks"), so reading the field in our postfix saw the
        /// already-cleared value and silently dropped every shockwave while that mod
        /// was installed. The flag only controls a vanilla splash fleck here, so a
        /// value taken before other patches run is the correct one for us.
        /// </summary>
        [HarmonyPriority(Priority.High)]
        private static void StartExplosionPrefix(Explosion __instance, out bool __state)
        {
            __state = __instance != null && __instance.doVisualEffects;
        }

        private static void StartExplosionPostfix(Explosion __instance, bool __state)
        {
            if (!ExplosionRippleBridge.Active || __instance == null || __instance.Map == null)
            {
                return;
            }

            // No damage-type filter: the user asked for every visual explosion. The
            // radius guard only avoids a degenerate zero-size quad.
            if (!__state || !(__instance.radius > 0.01f))
            {
                return;
            }

            OverlayMapComponent component =
                __instance.Map.GetComponent<OverlayMapComponent>();
            component?.NotifyExplosion(__instance);
        }
    }
}
