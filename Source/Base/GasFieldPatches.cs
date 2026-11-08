using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Suppresses the vanilla per-cell gas sprites while the field renderer is active.
    /// This is the one gas patch that also matters without AO: vanilla's
    /// SectionLayer_Gas regenerates one full-alpha quad per cell. AO's own prefix for the
    /// same method is outranked by Priority.First, so a single patch covers both cases.
    /// AO's mist renderers are suppressed by the AO compatibility assembly instead.
    /// </summary>
    internal static class GasFieldPatches
    {
        private static bool loggedVanillaGas;

        internal static void Apply(Harmony harmony)
        {
            // Priority.First makes this prefix run before AO's own SectionLayer_Gas
            // prefix, so returning false skips both AO's replacement and the vanilla
            // regeneration for that section.
            Patch(
                harmony,
                typeof(SectionLayer_Gas),
                "Regenerate",
                nameof(SkipVanillaGasRegenerate),
                Priority.First);
        }

        private static void Patch(
            Harmony harmony,
            Type type,
            string methodName,
            string prefixName,
            int priority)
        {
            if (type == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch gas layer " + methodName + ": type not found.");
                return;
            }

            MethodInfo target = AccessTools.Method(type, methodName);
            MethodInfo prefix = AccessTools.Method(typeof(GasFieldPatches), prefixName);

            if (target == null || prefix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch " + type.FullName + "." + methodName + ".");
                return;
            }

            try
            {
                HarmonyMethod prefixMethod = new HarmonyMethod(prefix)
                {
                    priority = priority,
                };

                harmony.Patch(target, prefix: prefixMethod);
                Log.Message("[Visual Tweaks] Patched " + type.Name + "." + methodName + ".");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch " + type.FullName + "." + methodName + ": " + ex);
            }
        }

        private static bool SkipVanillaGasRegenerate(SectionLayer_Gas __instance)
        {
            if (!GasFieldBridge.Active)
            {
                return true;
            }

            // AO's prefix clears the section before rebuilding it; skipping the whole
            // method would leave the previous sprite quads alive forever, so the meshes
            // are cleared here instead and the field pass draws them.
            __instance.subMeshes.Clear();

            if (!loggedVanillaGas)
            {
                loggedVanillaGas = true;
                Log.Message("[Visual Tweaks] gas section sprites suppressed.");
            }

            return false;
        }
    }
}
