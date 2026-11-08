using RimWorld;
using Verse;
using Waylith.Rendering;

namespace VisualTweaks
{
    /// <summary>
    /// Resolves and validates the heat-haze shader. Kept separate from the renderer
    /// so a missing shader only disables this feature instead of touching the gas
    /// field, the wind overlay or the explosion shockwave.
    /// </summary>
    public static class HeatHazeBridge
    {
        private const string ShaderDefName = "VisualTweaksHeatHaze";
        private const string VertexShaderFile = "VisualTweaksHeatHaze.vert.glsl";
        private const string FragmentShaderFile = "VisualTweaksHeatHaze.frag.glsl";

        private static bool initialized;
        private static bool shaderReady;
        private static bool runtimeDisabled;
        private static ShaderDef? shaderDef;

        internal static ShaderDef? HeatHazeShaderDef => shaderDef;

        public static bool ShaderReady => shaderReady;

        /// <summary>
        /// True when the heat haze should draw. Screenshot mode is excluded for the
        /// same reason as the gas field and the shockwave: screenshots should show
        /// the stock visuals.
        /// </summary>
        public static bool Active =>
            initialized
            && shaderReady
            && !runtimeDisabled
            && VisualTweaksSettings.heatHazeEnabled
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
                    + "' not found; heat haze disabled.");
                return;
            }

            if (!ShaderSourceLoader.TryResolveShaderPath(VertexShaderFile, out _)
                || !ShaderSourceLoader.TryResolveShaderPath(FragmentShaderFile, out _))
            {
                Log.Warning("[Visual Tweaks] Heat haze shader sources not found; disabled.");
                return;
            }

            if (!ShaderRegistry.TryResolve(ShaderDefName, out Shader? engineShader)
                || engineShader == null
                || engineShader.HasErrors)
            {
                Log.Warning("[Visual Tweaks] Heat haze shader '" + ShaderDefName
                    + "' failed to compile or parse; disabled.");
                return;
            }

            // Refraction is meaningless without the scene grab. Catch a missing or
            // misnamed <sceneGrab> here instead of at the first hot cell.
            if (engineShader.SceneGrab == null)
            {
                Log.Warning("[Visual Tweaks] Heat haze shader '" + ShaderDefName
                    + "' has no scene grab; disabled.");
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
            Log.Error("[Visual Tweaks] Heat haze disabled at runtime: " + reason);
        }
    }
}
