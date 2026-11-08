using System.Diagnostics.CodeAnalysis;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Optional data source registered by the AO compatibility assembly. The base mod
    /// renders vanilla gases on its own; every member here is AO-only extra data (mist,
    /// per-cell wind, solver cadence) that must not be a hard dependency.
    /// </summary>
    public interface IGasFieldExtension
    {
        /// <summary>True when the per-cell wind snapshot is available (AO only).</summary>
        bool SupportsLocalWind { get; }

        /// <summary>How many ticks the AO solver runs between published snapshots.</summary>
        int SimulationIntervalTicks { get; }

        bool TryGetWind(
            Map map,
            [NotNullWhen(true)] out float[]? velocityX,
            [NotNullWhen(true)] out float[]? velocityZ,
            [NotNullWhen(true)] out float[]? obstacleMask);

        int CompletedSteps(Map map);

        bool TryGetMistData(
            Map map,
            [NotNullWhen(true)] out float[]? mist,
            [NotNullWhen(true)] out float[]? obstacleMask);

        bool TryGetMistRenderSettings(
            Map map,
            out float maxDensity,
            out float renderZero,
            out float renderFull);

        void ApplyTemporalPatchMist(Map map, float[] mist);

        /// <summary>Normalized ambient wind direction the noise pattern drifts along.</summary>
        bool TryGetAmbientWind(Map map, out float windX, out float windZ);
    }
}
