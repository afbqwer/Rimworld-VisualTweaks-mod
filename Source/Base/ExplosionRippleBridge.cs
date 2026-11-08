using RimWorld;
using Verse;
using Waylith.Rendering;

namespace VisualTweaks
{
    /// <summary>
    /// Resolves and validates the shockwave shader. Kept separate from the renderer so
    /// a missing shader only disables this feature instead of breaking the gas field or
    /// the wind overlay.
    ///
    /// The future screen-space refraction variant can add a second ShaderDef here (with
    /// its own sceneGrab) while the draw loop and per-effect data stay unchanged.
    /// </summary>
    public static class ExplosionRippleBridge
    {
        private const string ShaderDefName = "VisualTweaksExplosionRipple";
        private const string VertexShaderFile = "VisualTweaksExplosionRipple.vert.glsl";
        private const string FragmentShaderFile = "VisualTweaksExplosionRipple.frag.glsl";

        private static bool initialized;
        private static bool shaderReady;
        private static bool runtimeDisabled;
        private static ShaderDef? shaderDef;

        internal static ShaderDef? RippleShaderDef => shaderDef;

        public static bool ShaderReady => shaderReady;

        /// <summary>
        /// True when the shockwave should draw. Screenshot mode is excluded for the same
        /// reason as the gas field: screenshots should show the stock visuals.
        /// </summary>
        public static bool Active =>
            initialized
            && shaderReady
            && !runtimeDisabled
            && VisualTweaksSettings.explosionRippleEnabled
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
                    + "' not found; explosion shockwave disabled.");
                return;
            }

            if (!ShaderSourceLoader.TryResolveShaderPath(VertexShaderFile, out _)
                || !ShaderSourceLoader.TryResolveShaderPath(FragmentShaderFile, out _))
            {
                Log.Warning("[Visual Tweaks] Explosion shockwave shader sources not found; disabled.");
                return;
            }

            if (!ShaderRegistry.TryResolve(ShaderDefName, out Shader? engineShader)
                || engineShader == null
                || engineShader.HasErrors)
            {
                Log.Warning("[Visual Tweaks] Explosion shockwave shader '" + ShaderDefName
                    + "' failed to compile or parse; disabled.");
                return;
            }

            // The refraction effect is useless without a scene grab: it would sample
            // whatever texture happened to be bound (usually black). Catch a missing
            // or misnamed <sceneGrab> here instead of at the first explosion.
            if (engineShader.SceneGrab == null)
            {
                Log.Warning("[Visual Tweaks] Explosion shockwave shader '" + ShaderDefName
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
            Log.Error("[Visual Tweaks] Explosion shockwave disabled at runtime: " + reason);
        }
    }
}
