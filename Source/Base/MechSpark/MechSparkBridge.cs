using RimWorld;
using Verse;
using Waylith.Rendering;

namespace VisualTweaks
{
    /// <summary>
    /// Resolves and validates the metal-hit spark shader. Kept separate from
    /// the renderer so a missing shader only disables this feature instead of
    /// breaking the shockwave or the shield dome.
    ///
    /// Unlike the shockwave there is no scene grab to validate: sparks are additive
    /// light, not refraction, so the shader needs nothing beyond its own uniforms.
    /// </summary>
    public static class MechSparkBridge
    {
        private const string ShaderDefName = "VisualTweaksMechSpark";
        private const string VertexShaderFile = "VisualTweaksMechSpark.vert.glsl";
        private const string FragmentShaderFile = "VisualTweaksMechSpark.frag.glsl";

        private static bool initialized;
        private static bool shaderReady;
        private static bool runtimeDisabled;
        private static ShaderDef? shaderDef;

        internal static ShaderDef? SparkShaderDef => shaderDef;

        public static bool ShaderReady => shaderReady;

        /// <summary>
        /// True when sparks should draw. Screenshot mode is excluded for the same
        /// reason as the other overlays: screenshots should show the stock visuals.
        /// </summary>
        public static bool Active =>
            initialized
            && shaderReady
            && !runtimeDisabled
            && VisualTweaksSettings.mechSparkEnabled
            && !ScreenshotModeGate.Active;

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
                    + "' not found; metal-hit sparks disabled.");
                return;
            }

            if (!ShaderSourceLoader.TryResolveShaderPath(VertexShaderFile, out _)
                || !ShaderSourceLoader.TryResolveShaderPath(FragmentShaderFile, out _))
            {
                Log.Warning("[Visual Tweaks] Metal-hit spark shader sources not found; disabled.");
                return;
            }

            if (!ShaderRegistry.TryResolve(ShaderDefName, out Shader? engineShader)
                || engineShader == null
                || engineShader.HasErrors)
            {
                Log.Warning("[Visual Tweaks] Metal-hit spark shader '" + ShaderDefName
                    + "' failed to compile or parse; disabled.");
                return;
            }

            shaderReady = true;
        }

        internal static void DisableRuntime(string reason)
        {
            if (runtimeDisabled)
            {
                return;
            }

            runtimeDisabled = true;
            Log.Error("[Visual Tweaks] Metal-hit sparks disabled at runtime: " + reason);
        }
    }
}