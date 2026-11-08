using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace VisualTweaks
{
    internal static class AOWindPatches
    {
        private static bool drawWindStreamlinesLogged;
        private static bool stepParticlesLogged;
        private static bool drawParticlesLogged;
        private static bool drawParticleLogged;

        internal static void Apply(Harmony harmony)
        {
            // Primary switch: stop AO before it even creates its streamline renderer.
            Patch(
                harmony,
                AOWindBridge.MapComponentType,
                "DrawWindStreamlines",
                nameof(SkipDrawWindStreamlines));

            // Defensive fallbacks in case the primary method was inlined by the runtime.
            // The particle methods contain loops and are very unlikely to be inlined.
            Patch(
                harmony,
                AOWindBridge.StreamlineRendererType,
                "StepParticles",
                nameof(SkipStepParticles));
            Patch(
                harmony,
                AOWindBridge.StreamlineRendererType,
                "DrawParticles",
                nameof(SkipDrawParticles));
            Patch(
                harmony,
                AOWindBridge.StreamlineRendererType,
                "DrawParticle",
                nameof(SkipDrawParticle));
        }

        private static void Patch(Harmony harmony, Type? type, string methodName, string prefixName)
        {
            if (type == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch " + methodName + ": type not found.");
                return;
            }

            MethodInfo target = AccessTools.Method(type, methodName);
            MethodInfo prefix = AccessTools.Method(typeof(AOWindPatches), prefixName);

            if (target == null || prefix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch " + type.FullName + "." + methodName + ".");
                return;
            }

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                Log.Message("[Visual Tweaks] Patched " + type.Name + "." + methodName + ".");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch " + type.FullName + "." + methodName + ": " + ex);
            }
        }

        private static bool SkipDrawWindStreamlines()
        {
            if (!VisualTweaksSettings.ReplacementEnabled)
            {
                return true;
            }

            if (!drawWindStreamlinesLogged)
            {
                drawWindStreamlinesLogged = true;
                Log.Message("[Visual Tweaks] AO DrawWindStreamlines suppressed.");
            }

            return false;
        }

        private static bool SkipStepParticles()
        {
            if (!VisualTweaksSettings.ReplacementEnabled)
            {
                return true;
            }

            if (!stepParticlesLogged)
            {
                stepParticlesLogged = true;
                Log.Message("[Visual Tweaks] AO StepParticles suppressed (fallback active).");
            }

            return false;
        }

        private static bool SkipDrawParticles()
        {
            if (!VisualTweaksSettings.ReplacementEnabled)
            {
                return true;
            }

            if (!drawParticlesLogged)
            {
                drawParticlesLogged = true;
                Log.Message("[Visual Tweaks] AO DrawParticles suppressed (fallback active).");
            }

            return false;
        }

        private static bool SkipDrawParticle()
        {
            if (!VisualTweaksSettings.ReplacementEnabled)
            {
                return true;
            }

            if (!drawParticleLogged)
            {
                drawParticleLogged = true;
                Log.Message("[Visual Tweaks] AO DrawParticle suppressed (fallback active).");
            }

            return false;
        }
    }
}
