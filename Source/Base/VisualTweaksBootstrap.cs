using System;
using HarmonyLib;
using Verse;

namespace VisualTweaks
{
    [StaticConstructorOnStartup]
    internal static class VisualTweaksBootstrap
    {
        static VisualTweaksBootstrap()
        {
            try
            {
                GasFieldBridge.Initialize();
                ExplosionRippleBridge.Initialize();
                HeatHazeBridge.Initialize();
                ColdMistBridge.Initialize();
                VacuumLeakBridge.Initialize();
                ShieldDomeBridge.Initialize();
                int culledDefsFixed = ShieldCullFix.Apply();

                Harmony harmony = new Harmony("local.visualtweaks");
                GasFieldPatches.Apply(harmony);
                ExplosionRipplePatches.Apply(harmony);
                ShieldRipplePatches.Apply(harmony);
                ShieldVisibilityPatches.Apply(harmony);
                ShieldDomePatches.Apply(harmony);
                VacuumBreachPatches.Apply(harmony);

                VisualTweaksLog.Info("base initialized (gas field renderer "
                    + (GasFieldBridge.ShaderReady ? "ready" : "disabled")
                    + ", explosion shockwave "
                    + (ExplosionRippleBridge.ShaderReady ? "ready" : "disabled")
                    + ", heat haze "
                    + (HeatHazeBridge.ShaderReady ? "ready" : "disabled")
                    + ", cold mist "
                    + (ColdMistBridge.ShaderReady ? "ready" : "disabled")
                    + ", vacuum leak "
                    + (VacuumLeakBridge.ShaderReady ? "ready" : "disabled")
                    + ", shield dome "
                    + (ShieldDomeBridge.ShaderReady ? "ready" : "disabled")
                    + ", off-screen shield culling fixed on "
                    + culledDefsFixed + " def(s)).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Base initialization failed: " + ex);
            }
        }
    }
}
