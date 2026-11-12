using System.Diagnostics.CodeAnalysis;
using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Waylith.Rendering;

namespace VisualTweaks
{
    /// <summary>
    /// Base-mod access to the gas field renderer's inputs. Vanilla gases come straight from
    /// GasGrid here; every Atmosphere Overhaul specific input (mist, per-cell wind, solver
    /// cadence) is supplied by an optional <see cref="IGasFieldExtension"/> registered by
    /// the AO compatibility assembly. With AO absent all of those quietly fall back to
    /// vanilla behaviour.
    /// </summary>
    public static class GasFieldBridge
    {
        private const string ShaderDefName = "VisualTweaksGasField";
        private const string VertexShaderFile = "VisualTweaksGasField.vert.glsl";
        private const string FragmentShaderFile = "VisualTweaksGasField.frag.glsl";

        private static bool initialized;
        private static bool shaderReady;
        private static bool runtimeDisabled;

        private static ShaderDef? shaderDef;
        private static IGasFieldExtension? extension;

        // Vanilla's per-cell gas words. Only used for an exact "is there any gas in this
        // window" test: GasGrid.GetDirect is a method call per cell, and the bake it guards
        // costs ~40x more than a vectorised scan over the same range.
        private static FieldInfo? gasDensityField;
        private static bool gasDensityResolved;

        internal static ShaderDef? FieldShaderDef => shaderDef;

        public static bool ShaderReady => shaderReady;

        /// <summary>
        /// True when the replacement gas/mist field should draw instead of the original
        /// sprite layers. Only a dev-mode reference screenshot hands the frame back to
        /// the stock renderers; a normal F11 keeps the effect.
        /// </summary>
        public static bool Active =>
            initialized
            && shaderReady
            && !runtimeDisabled
            && !ScreenshotModeGate.Active
            && VisualTweaksSettings.useGasField;

        internal static bool SupportsLocalWind =>
            extension != null && extension.SupportsLocalWind;

        /// <summary>
        /// The noise mode actually usable in this environment. Local-flow drift needs AO's
        /// per-cell wind snapshot; without it a save that selected it falls back to the
        /// ambient drift instead of freezing the pattern.
        /// </summary>
        internal static int EffectiveNoiseMode
        {
            get
            {
                int mode = VisualTweaksSettings.gasFieldNoiseMode;
                if (mode != VisualTweaksSettings.GasNoiseLocalFlow || SupportsLocalWind)
                {
                    return mode;
                }

                return VisualTweaksSettings.GasNoiseAmbient;
            }
        }

        /// <summary>
        /// How many ticks AO's solver runs between publishes. Without AO vanilla advects
        /// gas every tick; the renderer only needs the 10 Hz cadence, so the old default
        /// (6 ticks at 60 TPS) is kept.
        /// </summary>
        internal static int SimulationIntervalTicks =>
            extension != null ? extension.SimulationIntervalTicks : 6;

        /// <summary>
        /// Resolves the shader def. Called once from the base bootstrap after defs are
        /// loaded.
        /// </summary>
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
                    + "' not found; gas field renderer disabled.");
                return;
            }

            if (!ShaderSourceLoader.TryResolveShaderPath(VertexShaderFile, out _)
                || !ShaderSourceLoader.TryResolveShaderPath(FragmentShaderFile, out _))
            {
                Log.Warning("[Visual Tweaks] Gas field shader sources not found; renderer disabled.");
                return;
            }

            if (!ShaderRegistry.TryResolve(ShaderDefName, out Waylith.Rendering.Shader? engineShader)
                || engineShader == null
                || engineShader.HasErrors)
            {
                Log.Warning("[Visual Tweaks] Gas field shader '" + ShaderDefName
                    + "' failed to compile or parse; renderer disabled.");
                return;
            }

            shaderReady = true;
        }

        /// <summary>
        /// Called by the AO compatibility assembly once it has confirmed AO is present.
        /// Late registration is fine: every read below is evaluated per frame.
        /// </summary>
        public static void RegisterExtension(IGasFieldExtension gasFieldExtension)
        {
            extension = gasFieldExtension;
        }

        internal static void DisableRuntime(string reason)
        {
            if (runtimeDisabled)
            {
                return;
            }

            runtimeDisabled = true;
            Log.Error("[Visual Tweaks] Gas field renderer disabled at runtime: " + reason);
        }

        internal static bool TryGetMistData(
            Map map,
            [NotNullWhen(true)] out float[]? mist,
            [NotNullWhen(true)] out float[]? obstacleMask)
        {
            mist = null;
            obstacleMask = null;
            return extension != null && extension.TryGetMistData(map, out mist, out obstacleMask);
        }

        /// <summary>
        /// Vanilla's raw gas words, indexed by map cell. Reflection, because there is no
        /// bulk accessor: the point of asking for the array is to scan a whole window with
        /// a vectorised search instead of calling GetDirect per cell.
        /// Returns false when the field cannot be found, so callers can fall back to
        /// "assume there is content" rather than skipping the bake.
        /// </summary>
        internal static bool TryGetGasDensity(Map map, [NotNullWhen(true)] out uint[]? density)
        {
            density = null;

            GasGrid? gasGrid = map?.gasGrid;
            if (gasGrid == null)
            {
                return false;
            }

            if (!gasDensityResolved)
            {
                gasDensityResolved = true;
                gasDensityField = AccessTools.Field(typeof(GasGrid), "gasDensity");
                if (gasDensityField == null)
                {
                    Log.Warning("[Visual Tweaks] GasGrid.gasDensity not found; "
                        + "gas content gating disabled.");
                }
            }

            if (gasDensityField == null)
            {
                return false;
            }

            try
            {
                density = gasDensityField.GetValue(gasGrid) as uint[];
                return density != null;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryGetMistRenderSettings(
            Map map,
            out float maxDensity,
            out float renderZero,
            out float renderFull)
        {
            // AO's tuning defaults; used when the settings object is unavailable.
            maxDensity = 100f;
            renderZero = 1f;
            renderFull = 3f;
            return extension != null
                && extension.TryGetMistRenderSettings(
                    map,
                    out maxDensity,
                    out renderZero,
                    out renderFull);
        }

        /// <summary>
        /// Blends AO's temporal-patch mist into the coarse snapshot. No-op without AO.
        /// </summary>
        internal static void ApplyTemporalPatchMist(Map map, float[] mist)
        {
            extension?.ApplyTemporalPatchMist(map, mist);
        }

        internal static bool TryGetAmbientWind(Map map, out float windX, out float windZ)
        {
            // AO replaces the vanilla wind model, so its answer is authoritative even
            // when it reports calm; only the vanilla fallback may use WindManager.
            if (extension != null)
            {
                return extension.TryGetAmbientWind(map, out windX, out windZ);
            }

            return TryGetVanillaAmbientWind(map, out windX, out windZ);
        }

        internal static bool TryGetWind(
            Map map,
            [NotNullWhen(true)] out float[]? velocityX,
            [NotNullWhen(true)] out float[]? velocityZ,
            [NotNullWhen(true)] out float[]? obstacleMask)
        {
            velocityX = null;
            velocityZ = null;
            obstacleMask = null;
            return extension != null
                && extension.TryGetWind(map, out velocityX, out velocityZ, out obstacleMask);
        }

        internal static int CompletedSteps(Map map)
        {
            return extension != null ? extension.CompletedSteps(map) : 0;
        }

        /// <summary>
        /// Vanilla has no wind direction: WindManager only exposes a scalar speed. The
        /// direction is derived deterministically from the world tile, the absolute game
        /// tick and the current precipitation, exactly like a smooth weather-heading model,
        /// so the noise pattern drifts consistently instead of picking a random angle each
        /// frame.
        /// </summary>
        private static bool TryGetVanillaAmbientWind(Map map, out float windX, out float windZ)
        {
            windX = 0f;
            windZ = 0f;

            if (map == null)
            {
                return false;
            }

            WindManager windManager = map.windManager;
            if (windManager == null)
            {
                return false;
            }

            float speed = windManager.WindSpeed;
            if (speed <= 0.0001f || !float.IsFinite(speed))
            {
                return false;
            }

            float heading = VanillaWindHeading(map);
            windX = Mathf.Cos(heading);
            windZ = Mathf.Sin(heading);
            return true;
        }

        private static float VanillaWindHeading(Map map)
        {
            World world = Find.World;
            if (world == null)
            {
                return 0f;
            }

            int seed = Gen.HashCombineInt(world.info.Seed, map.Tile.tileId);
            float baseHeading = Unit(seed, 1) * (float)Math.PI * 2f;

            int ticks = Find.TickManager != null ? Find.TickManager.TicksAbs : 0;
            float slow = 1.1f * Mathf.Sin(
                (float)Math.PI * 2f * (ticks / 162000f + Unit(seed, 2)));
            float fast = 0.45f * Mathf.Sin(
                (float)Math.PI * 2f * (ticks / 49800f + Unit(seed, 3)));

            WeatherManager weather = map.weatherManager;
            float precipitation = weather != null
                ? Mathf.Clamp01(weather.RainRate + weather.SnowRate)
                : 0f;

            return baseHeading + slow + fast + precipitation * (Unit(seed, 4) - 0.5f) * 1.6f;
        }

        private static float Unit(int seed, int salt)
        {
            uint value = (uint)(seed ^ (salt * -1640531527));
            value ^= value >> 16;
            value *= 2246822507u;
            value ^= value >> 13;
            value *= 3266489909u;
            value ^= value >> 16;
            return (float)(value & 0xFFFFFF) / 16777216f;
        }
    }
}
