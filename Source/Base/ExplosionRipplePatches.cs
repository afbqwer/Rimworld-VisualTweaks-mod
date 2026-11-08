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
            MethodInfo postfix = AccessTools.Method(
                typeof(ExplosionRipplePatches),
                nameof(StartExplosionPostfix));

            if (target == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch Explosion.StartExplosion.");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                Log.Message("[Visual Tweaks] Patched Explosion.StartExplosion.");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch Explosion.StartExplosion: " + ex);
            }
        }

        private static void StartExplosionPostfix(Explosion __instance)
        {
            if (!ExplosionRippleBridge.Active || __instance == null || __instance.Map == null)
            {
                return;
            }

            // No damage-type filter: the user asked for every visual explosion. The
            // radius guard only avoids a degenerate zero-size quad.
            if (!__instance.doVisualEffects || !(__instance.radius > 0.01f))
            {
                return;
            }

            OverlayMapComponent component =
                __instance.Map.GetComponent<OverlayMapComponent>();
            component?.NotifyExplosion(__instance);
        }
    }
}
