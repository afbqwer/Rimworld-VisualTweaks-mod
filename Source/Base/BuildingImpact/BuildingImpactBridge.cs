using RimWorld;
using Verse;
using Waylith.Rendering;

namespace VisualTweaks
{
    /// <summary>
    /// Resolves and validates the building-impact debris shader. Kept separate
    /// from the renderer so a missing shader only disables this feature instead
    /// of breaking the other overlays. Same shape as MechSparkBridge: debris and
    /// dust are self-contained alpha-blended matter, no scene grab to validate.
    /// </summary>
    public static class BuildingImpactBridge
    {
        private const string ShaderDefName = "VisualTweaksBuildingImpact";
        private const string VertexShaderFile = "VisualTweaksBuildingImpact.vert.glsl";
        private const string FragmentShaderFile = "VisualTweaksBuildingImpact.frag.glsl";

        private static bool initialized;
        private static bool shaderReady;
        private static bool runtimeDisabled;
        private static ShaderDef? shaderDef;

        internal static ShaderDef? ImpactShaderDef => shaderDef;

        public static bool ShaderReady => shaderReady;

        /// <summary>
        /// True when impact debris should draw. Screenshot mode is excluded for
        /// the same reason as the other overlays: screenshots show stock visuals.
        /// </summary>
        public static bool Active =>
            initialized
            && shaderReady
            && !runtimeDisabled
            && VisualTweaksSettings.buildingImpactEnabled
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
                    + "' not found; building impact debris disabled.");
                return;
            }

            if (!ShaderSourceLoader.TryResolveShaderPath(VertexShaderFile, out _)
                || !ShaderSourceLoader.TryResolveShaderPath(FragmentShaderFile, out _))
            {
                Log.Warning("[Visual Tweaks] Building impact shader sources not found; disabled.");
                return;
            }

            if (!ShaderRegistry.TryResolve(ShaderDefName, out Shader? engineShader)
                || engineShader == null
                || engineShader.HasErrors)
            {
                Log.Warning("[Visual Tweaks] Building impact shader '" + ShaderDefName
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
            Log.Error("[Visual Tweaks] Building impact debris disabled at runtime: " + reason);
        }
    }
}