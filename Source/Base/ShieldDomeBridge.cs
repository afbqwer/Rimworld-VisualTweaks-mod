using System;
using RimWorld;
using Verse;
using Waylith.Rendering;

namespace VisualTweaks
{
    /// <summary>
    /// Resolves and validates the shield dome shader.
    ///
    /// Failing here must leave the stock shield drawing intact, not a missing shield:
    /// the patch reads <see cref="Active"/> and hands the frame back to vanilla when
    /// this is false. A shader that fails to compile therefore costs the effect, never
    /// the feature.
    /// </summary>
    internal static class ShieldDomeBridge
    {
        private const string ShaderDefName = "VisualTweaksShieldDome";
        private const string VertexShaderFile = "VisualTweaksShieldDome.vert.glsl";
        private const string FragmentShaderFile = "VisualTweaksShieldDome.frag.glsl";

        private static bool initialized;
        private static bool shaderReady;
        private static bool runtimeDisabled;
        private static ShaderDef? shaderDef;

        internal static ShaderDef? DomeShaderDef => shaderDef;

        internal static bool ShaderReady => shaderReady;

        /// <summary>
        /// True when the custom dome should replace vanilla's drawing for large
        /// (interceptor) shields. Screenshot mode is excluded so a screenshot still
        /// shows the stock visuals, matching every other effect in this mod.
        /// </summary>
        internal static bool LargeActive =>
            initialized
            && shaderReady
            && !runtimeDisabled
            && VisualTweaksSettings.replaceLargeShields
            && !ScreenshotModeGate.Active;

        /// <summary>Same as <see cref="LargeActive"/>, for personal shield belts.</summary>
        internal static bool PersonalActive =>
            initialized
            && shaderReady
            && !runtimeDisabled
            && VisualTweaksSettings.replacePersonalShields
            && !ScreenshotModeGate.Active;

        /// <summary>
        /// VEF's shield-field comp (used by the VFES repulsors) draws through its own
        /// billboard instead of CompProjectileInterceptor, so it has its own switch.
        /// It shares the large-shield surface and impact choices: a repulsor is a
        /// projector dome, and giving it a second style family would mean a second
        /// set of sliders for the same look.
        /// </summary>
        internal static bool RepulsorActive =>
            initialized
            && shaderReady
            && !runtimeDisabled
            && VisualTweaksSettings.replaceRepulsorShields
            && !ScreenshotModeGate.Active;

        /// <summary>
        /// True when this particular interceptor should be drawn by the mod. The
        /// vanilla-only switch compares the runtime type exactly, so modded subclasses
        /// keep their own drawing. Vanilla's own CompGravshipShieldGenerator is the one
        /// exception: it inherits the drawing but is still a stock shield.
        /// </summary>
        internal static bool ShouldReplace(CompProjectileInterceptor? comp)
        {
            if (comp == null || !LargeActive)
            {
                return false;
            }

            if (!VisualTweaksSettings.largeShieldsVanillaOnly)
            {
                return true;
            }

            Type type = comp.GetType();
            return type == typeof(CompProjectileInterceptor)
                || type == typeof(CompGravshipShieldGenerator);
        }

        /// <summary>Same rule as the large-shield overload, for CompShield.</summary>
        internal static bool ShouldReplace(CompShield? comp)
        {
            if (comp == null || !PersonalActive)
            {
                return false;
            }

            return !VisualTweaksSettings.personalShieldsVanillaOnly
                || comp.GetType() == typeof(CompShield);
        }

        internal static void Initialize()
        {
            if (initialized)
            {
                return;
            }

            initialized = true;

            shaderDef = DefDatabase<ShaderDef>.GetNamedSilentFail(ShaderDefName);
            if (shaderDef == null)
            {
                Log.Warning("[Visual Tweaks] Shader def '" + ShaderDefName
                    + "' not found; shields keep the stock drawing.");
                return;
            }

            if (!ShaderSourceLoader.TryResolveShaderPath(VertexShaderFile, out _)
                || !ShaderSourceLoader.TryResolveShaderPath(FragmentShaderFile, out _))
            {
                Log.Warning("[Visual Tweaks] Shield dome shader sources not found;"
                    + " shields keep the stock drawing.");
                return;
            }

            if (!ShaderRegistry.TryResolve(ShaderDefName, out Shader? engineShader)
                || engineShader == null
                || engineShader.HasErrors)
            {
                Log.Warning("[Visual Tweaks] Shield dome shader '" + ShaderDefName
                    + "' failed to compile or parse; shields keep the stock drawing.");
                return;
            }

            shaderReady = true;
        }

        /// <summary>
        /// Called by the renderer when a draw throws. One broken material must not spam
        /// the log every frame, and must not leave the game with no shield drawing at
        /// all — disabling here restores vanilla's on the next frame.
        /// </summary>
        internal static void DisableRuntime(string reason)
        {
            if (runtimeDisabled)
            {
                return;
            }

            runtimeDisabled = true;
            Log.Error("[Visual Tweaks] Shield dome disabled at runtime, stock drawing"
                + " restored: " + reason);
        }
    }
}
