using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Keeps active shields faintly visible without a selection.
    ///
    /// Vanilla deliberately hides them. <c>CompProjectileInterceptor.GetCurrentAlpha_Idle</c>
    /// returns 0 for a player-faction shield, 0 again when the shield is selected, and the
    /// remaining branch lerps from <c>minIdleAlpha</c> — which defaults to -1.7, i.e. a
    /// negative alpha that <c>GetCurrentAlpha</c> clamps away. The net effect is that a
    /// shield is only ever drawn while selected or right after it eats a shot. (The
    /// gravship generator opts back in by setting minIdleAlpha to 0.2 and
    /// drawWithNoSelection to true in its def.)
    ///
    /// Patching the idle term rather than the def is the only option that works for
    /// every shield in the game, including modded ones: alphas are combined with Max, so
    /// returning a small positive value here is invisible whenever the selected branch or
    /// the recently-intercepted flash already produces something brighter.
    ///
    /// The value deliberately ignores minIdleAlpha. Feeding that field into the lerp is
    /// what makes the vanilla property return a negative number in the first place, and
    /// any shield that already sets a visible minIdleAlpha is bright enough on its own.
    /// </summary>
    internal static class ShieldVisibilityPatches
    {
        private const string IdleAlphaMethodName = "GetCurrentAlpha_Idle";

        /// <summary>
        /// Bottom of the breathing range, and also the threshold below which vanilla's
        /// own idle term is treated as "not drawn": the two have to be the same number,
        /// otherwise the shader output jumps at the seam.
        /// </summary>
        private const float MinAlpha = 0.05f;

        /// <summary>
        /// Top of the range. Vanilla's own selected pulse starts at 0.2, so this stays
        /// clearly below "this thing is selected" while still being easy to see.
        /// </summary>
        private const float MaxAlpha = 0.13f;

        /// <summary>
        /// Same hash salt vanilla uses for the idle pulse, so a shield that this patch
        /// makes visible breathes in phase with the enemies' shields rather than drifting
        /// on its own rhythm.
        /// </summary>
        private const int PhaseSalt = 96804938;

        internal static void Apply(Harmony harmony)
        {
            MethodInfo? target = AccessTools.Method(
                typeof(CompProjectileInterceptor),
                IdleAlphaMethodName);
            MethodInfo? postfix = AccessTools.Method(
                typeof(ShieldVisibilityPatches),
                nameof(GetCurrentAlphaIdlePostfix));

            if (target == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch CompProjectileInterceptor."
                    + IdleAlphaMethodName + ".");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched CompProjectileInterceptor." + IdleAlphaMethodName + ".");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch shield idle alpha: " + ex);
            }
        }

        private static void GetCurrentAlphaIdlePostfix(
            CompProjectileInterceptor __instance,
            ref float __result)
        {
            // Fill in anywhere vanilla produced something dimmer than our floor, not
            // only where it produced exactly zero.
            //
            // The stricter "> 0" test looks safer but strobes: an enemy shield's idle
            // term is Lerp(-1.7, 0.11, t), which turns positive for the top few percent
            // of the sine — so it would flip between vanilla's 0.01 spike and our 0.05
            // floor every cycle. Treating "dimmer than the floor" as "not drawn" makes
            // the result continuous. Shields that already set a visible minIdleAlpha
            // (the gravship generator uses 0.2) never fall below the floor and are left
            // exactly as vanilla drew them.
            if (__result >= MinAlpha || __instance == null || !VisualTweaksSettings.alwaysShowShields)
            {
                return;
            }

            if (ScreenshotModeGate.Active)
            {
                return;
            }

            ThingWithComps? parent = __instance.parent;
            if (parent == null || !__instance.Active)
            {
                return;
            }

            float phase = (float)(Gen.HashCombineInt(parent.thingIDNumber, PhaseSalt) % 100)
                + ShieldAnimationClock.Time * __instance.Props.idlePulseSpeed;

            __result = Mathf.Lerp(MinAlpha, MaxAlpha, (Mathf.Sin(phase) + 1f) / 2f);
        }
    }
}
