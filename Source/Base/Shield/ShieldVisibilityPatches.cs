using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
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
    ///
    /// Vanilla drives both of its alpha pulses — the idle breathing here and the
    /// selected / drawWithNoSelection pulse — from <c>Time.realtimeSinceStartup</c>.
    /// That is why a centurion, whose def sets minIdleAlpha 0.2 and
    /// drawWithNoSelection true, keeps breathing through a pause: its alpha never
    /// falls below MinAlpha, so the postfix leaves the vanilla value alone, and the
    /// selected pulse (0.2..0.62) is the one actually visible. Both methods are
    /// therefore transpiled to read <see cref="ShieldAnimationClock"/>, which keeps
    /// the pause option authoritative without reimplementing vanilla's alpha rules.
    /// </summary>
    internal static class ShieldVisibilityPatches
    {
        private const string IdleAlphaMethodName = "GetCurrentAlpha_Idle";

        private const string SelectedAlphaMethodName = "GetCurrentAlpha_Selected";

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
            MethodInfo? idle = AccessTools.Method(
                typeof(CompProjectileInterceptor),
                IdleAlphaMethodName);
            MethodInfo? postfix = AccessTools.Method(
                typeof(ShieldVisibilityPatches),
                nameof(GetCurrentAlphaIdlePostfix));
            MethodInfo? transpiler = AccessTools.Method(
                typeof(ShieldVisibilityPatches),
                nameof(UseShieldClock));

            if (idle == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch CompProjectileInterceptor."
                    + IdleAlphaMethodName + ".");
                return;
            }

            // Apply the two jobs separately: a transpiler that cannot match must not
            // take the idle-visibility postfix down with it, and vice versa.
            try
            {
                harmony.Patch(idle, postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched CompProjectileInterceptor." + IdleAlphaMethodName + ".");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch shield idle alpha: " + ex);
            }

            if (transpiler == null)
            {
                Log.Warning("[Visual Tweaks] Shield alpha clock transpiler not found;"
                    + " vanilla breathing will keep running while paused.");
                return;
            }

            PatchPulseClock(harmony, idle, transpiler);
            PatchPulseClock(
                harmony,
                AccessTools.Method(
                    typeof(CompProjectileInterceptor),
                    SelectedAlphaMethodName),
                transpiler);
        }

        private static void PatchPulseClock(
            Harmony harmony,
            MethodInfo? target,
            MethodInfo transpiler)
        {
            if (target == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch a shield alpha pulse;"
                    + " its breathing will keep running while paused.");
                return;
            }

            try
            {
                harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
                VisualTweaksLog.Info(
                    "Patched CompProjectileInterceptor." + target.Name + " clock.");
            }
            catch (Exception ex)
            {
                // Losing this patch only leaves vanilla's pulse on while paused; the
                // shader clock and the idle fill still work, so do not abort startup.
                Log.Error("[Visual Tweaks] Failed to patch " + target.Name
                    + " shield alpha clock: " + ex);
            }
        }

        /// <summary>
        /// Swaps vanilla's <c>Time.realtimeSinceStartup</c> reads in one alpha pulse for
        /// the shared shield clock. The stack shape is unchanged (float in, float out),
        /// and the clock returns the same value as before unless the pause option is on.
        ///
        /// The replacement is matched by declaring type and name rather than by
        /// comparing against a captured MethodInfo: Harmony may hand us the same call
        /// through a different MethodInfo instance, but the operand is always the
        /// UnityEngine property getter.
        /// </summary>
        private static IEnumerable<CodeInstruction> UseShieldClock(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo? clock = AccessTools.PropertyGetter(
                typeof(ShieldAnimationClock),
                nameof(ShieldAnimationClock.Time));
            if (clock == null)
            {
                Log.Warning("[Visual Tweaks] ShieldAnimationClock.Time getter not found;"
                    + " shield alpha pulses will keep running while paused.");
                foreach (CodeInstruction instruction in instructions)
                {
                    yield return instruction;
                }

                yield break;
            }

            bool replaced = false;
            foreach (CodeInstruction instruction in instructions)
            {
                // The operand is the property *getter*, so its name is
                // "get_realtimeSinceStartup", not "realtimeSinceStartup".
                if (instruction.operand is MethodInfo called
                    && called.DeclaringType == typeof(Time)
                    && called.Name == "get_realtimeSinceStartup")
                {
                    yield return new CodeInstruction(OpCodes.Call, clock);
                    replaced = true;
                    continue;
                }

                yield return instruction;
            }

            if (!replaced)
            {
                Log.Warning("[Visual Tweaks] No Time.realtimeSinceStartup call found in a"
                    + " shield alpha pulse; its pause freeze will not apply.");
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
