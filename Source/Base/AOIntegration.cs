using System;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Handshake published by the AO compatibility assembly. The base assembly is loaded
    /// in both cases, so it can only ask these flags instead of referencing AO types.
    /// </summary>
    public static class AOIntegration
    {
        /// <summary>True once AO was found and its streamline overlay is usable.</summary>
        public static bool StreamlineAvailable { get; set; }

        /// <summary>
        /// True when AO's per-cell temperature field is the authoritative source.
        /// AO patches GenTemperature.GetTemperatureForCell to return its snapshot;
        /// without AO that method falls back to vanilla room temperature and returns
        /// 21 C for open outdoor cells, so the heat mask has to use a different path.
        /// </summary>
        public static bool PerCellTemperatureAvailable { get; set; }

        /// <summary>
        /// Creates the wind overlay layer for one map, or null while AO is absent.
        /// The implementation lives in the AO assembly because it needs AO's wind
        /// snapshots and the streamline renderer; the base map component asks for it once
        /// per map and draws nothing extra when this is null.
        /// </summary>
        public static Func<IOverlayLayer>? CreateWindLayer { get; set; }
    }
}
