using System;
using HarmonyLib;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// AO compatibility entry point. This assembly is only mounted (and therefore only
    /// loaded) when Atmosphere Overhaul is active, so everything AO specific lives here:
    /// the wind overlay layer, mist data for the gas field, and suppressing AO's own
    /// sprite renderers.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class AOWindOverlayAOBootstrap
    {
        static AOWindOverlayAOBootstrap()
        {
            try
            {
                AOWindBridge.Initialize();

                if (!AOWindBridge.Available)
                {
                    Log.Warning("[Visual Tweaks] Atmosphere Overhaul was not found or its API changed; AO features disabled.");
                    return;
                }

                GasFieldBridge.RegisterExtension(new AOGasFieldExtension());

                // The base map component creates one layer per map from this factory, so
                // the wind overlay needs no MapComponent of its own.
                AOIntegration.CreateWindLayer = CreateWindLayer;
                AOIntegration.StreamlineAvailable = true;
                AOIntegration.PerCellTemperatureAvailable = true;

                Harmony harmony = new Harmony("local.visualtweaks.ao");
                AOWindPatches.Apply(harmony);
                AOGasFieldAOPatches.Apply(harmony);

                Log.Message("[Visual Tweaks] AO compatibility initialized.");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] AO initialization failed: " + ex);
            }
        }

        private static IOverlayLayer CreateWindLayer()
        {
            return new AOWindOverlayLayer();
        }
    }
}
