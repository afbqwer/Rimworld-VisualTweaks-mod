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
        /// True when the custom dome should replace vanilla's drawing. Screenshot mode
        /// is excluded so a screenshot still shows the stock visuals, matching every
        /// other effect in this mod.
        /// </summary>
        internal static bool Active =>
            initialized
            && shaderReady
            && !runtimeDisabled
            && VisualTweaksSettings.crispShieldDome
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
