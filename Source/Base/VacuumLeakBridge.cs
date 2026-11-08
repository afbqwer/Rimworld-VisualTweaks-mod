using RimWorld;
using Verse;
using Waylith.Rendering;

namespace VisualTweaks
{
    /// <summary>
    /// Resolves and validates the vacuum leak shader. Kept separate from the renderer
    /// so a missing shader only disables this feature instead of breaking the rest of
    /// the overlay stack. The shader composites over a scene grab because the optional
    /// refraction needs the frame behind the stream, the same shape as
    /// ExplosionRippleBridge.
    /// </summary>
    public static class VacuumLeakBridge
    {
        private const string ShaderDefName = "VisualTweaksVacuumLeak";
        private const string VertexShaderFile = "VisualTweaksVacuumLeak.vert.glsl";
        private const string FragmentShaderFile = "VisualTweaksVacuumLeak.frag.glsl";

        private static bool initialized;
        private static bool shaderReady;
        private static bool runtimeDisabled;
        private static ShaderDef? shaderDef;

        internal static ShaderDef? LeakShaderDef => shaderDef;

        public static bool ShaderReady => shaderReady;

        /// <summary>
        /// True when the leak visuals should draw. Screenshot mode is excluded for the
        /// same reason as the other effects: screenshots should show the stock visuals.
        /// </summary>
        public static bool Active =>
            initialized
            && shaderReady
            && !runtimeDisabled
            && VisualTweaksSettings.vacuumLeakEnabled
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
                    + "' not found; vacuum leak disabled.");
                return;
            }

            if (!ShaderSourceLoader.TryResolveShaderPath(VertexShaderFile, out _)
                || !ShaderSourceLoader.TryResolveShaderPath(FragmentShaderFile, out _))
            {
                Log.Warning("[Visual Tweaks] Vacuum leak shader sources not found; disabled.");
                return;
            }

            if (!ShaderRegistry.TryResolve(ShaderDefName, out Shader? engineShader)
                || engineShader == null
                || engineShader.HasErrors)
            {
                Log.Warning("[Visual Tweaks] Vacuum leak shader '" + ShaderDefName
                    + "' failed to compile or parse; disabled.");
                return;
            }

            // Without the grab the refraction would sample whatever texture happened
            // to be bound (usually black). Catch a missing or misnamed <sceneGrab>
            // here instead of at the first leak.
            if (engineShader.SceneGrab == null)
            {
                Log.Warning("[Visual Tweaks] Vacuum leak shader '" + ShaderDefName
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
            Log.Error("[Visual Tweaks] Vacuum leak disabled at runtime: " + reason);
        }
    }
}