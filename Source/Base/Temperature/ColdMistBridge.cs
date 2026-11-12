using RimWorld;
using Verse;
using Waylith.Rendering;

namespace VisualTweaks
{
    /// <summary>
    /// Resolves and validates the cold-mist shader. Kept separate from the renderer
    /// so a missing shader only disables this feature instead of touching the heat
    /// haze, the gas field, the wind overlay or the explosion shockwave.
    /// </summary>
    public static class ColdMistBridge
    {
        private const string ShaderDefName = "VisualTweaksColdMist";
        private const string VertexShaderFile = "VisualTweaksColdMist.vert.glsl";
        private const string FragmentShaderFile = "VisualTweaksColdMist.frag.glsl";

        private static bool initialized;
        private static bool shaderReady;
        private static bool runtimeDisabled;
        private static ShaderDef? shaderDef;

        internal static ShaderDef? ColdMistShaderDef => shaderDef;

        public static bool ShaderReady => shaderReady;

        /// <summary>
        /// True when the mist should draw. Screenshot mode is excluded for the same
        /// reason as every other effect here: screenshots should show the stock
        /// visuals.
        /// </summary>
        public static bool Active =>
            initialized
            && shaderReady
            && !runtimeDisabled
            && VisualTweaksSettings.coldMistEnabled
            && !ScreenshotModeActive;

        private static bool ScreenshotModeActive
        {
            get
            {
                ScreenshotModeHandler handler = Find.ScreenshotModeHandler;
                return handler != null && handler.Active;
            }
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
                    + "' not found; cold mist disabled.");
                return;
            }

            if (!ShaderSourceLoader.TryResolveShaderPath(VertexShaderFile, out _)
                || !ShaderSourceLoader.TryResolveShaderPath(FragmentShaderFile, out _))
            {
                Log.Warning("[Visual Tweaks] Cold mist shader sources not found; disabled.");
                return;
            }

            if (!ShaderRegistry.TryResolve(ShaderDefName, out Shader? engineShader)
                || engineShader == null
                || engineShader.HasErrors)
            {
                Log.Warning("[Visual Tweaks] Cold mist shader '" + ShaderDefName
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
            Log.Error("[Visual Tweaks] Cold mist disabled at runtime: " + reason);
        }
    }
}
