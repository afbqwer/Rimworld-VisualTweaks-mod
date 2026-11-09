using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Suppresses AO's sprite-stack mist renderers while the base mod's field renderer is
    /// active. Every prefix falls through (returns true) as soon as the renderer reports
    /// inactive, so disabling the setting or losing the shader restores AO's original look
    /// immediately. The vanilla/AO gas section patch lives in the base assembly.
    /// </summary>
    internal static class AOGasFieldAOPatches
    {
        private static bool loggedMist;
        private static bool loggedPatchMist;

        internal static void Apply(Harmony harmony)
        {
            Patch(
                harmony,
                AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.WaterMistSectionRenderer"),
                "Draw",
                nameof(SkipWaterMistDraw));

            Patch(
                harmony,
                AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.TemporalPatchMistRenderer"),
                "Draw",
                nameof(SkipTemporalPatchMistDraw));
        }

        private static void Patch(
            Harmony harmony,
            Type type,
            string methodName,
            string prefixName)
        {
            if (type == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch AO gas layer " + methodName + ": type not found.");
                return;
            }

            MethodInfo target = AccessTools.Method(type, methodName);
            MethodInfo prefix = AccessTools.Method(typeof(AOGasFieldAOPatches), prefixName);

            if (target == null || prefix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch " + type.FullName + "." + methodName + ".");
                return;
            }

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                VisualTweaksLog.Info("Patched " + type.Name + "." + methodName + ".");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch " + type.FullName + "." + methodName + ": " + ex);
            }
        }

        private static bool SkipWaterMistDraw()
        {
            if (!GasFieldBridge.Active)
            {
                return true;
            }

            VisualTweaksLog.TraceOnce(
                ref loggedMist,
                "AO water mist sprites suppressed.");

            return false;
        }

        private static bool SkipTemporalPatchMistDraw()
        {
            if (!GasFieldBridge.Active)
            {
                return true;
            }

            VisualTweaksLog.TraceOnce(
                ref loggedPatchMist,
                "AO temporal patch mist sprites suppressed.");

            return false;
        }
    }
}
