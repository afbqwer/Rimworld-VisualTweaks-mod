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
                PersonalShieldPatches.Apply(harmony);
                CombatExtendedShieldCompat.Apply(harmony);
                CombatExtendedExplosionCompat.Apply(harmony);
                VefRepulsorShieldCompat.Apply(harmony);
                VefShieldBubbleCompat.Apply(harmony);
                VefHediffShieldCompat.Apply(harmony);
                ShieldOverridePatches.Apply(harmony);
                VacuumBreachPatches.Apply(harmony);

                // The hex lattice is baked HERE, not on the first shield that gets
                // drawn. Mod settings are read in the Mod constructor (CreateModClasses,
                // before static constructors), so the enabled flag and the density are
                // already the saved values, and the cost lands on the loading screen
                // instead of on a frame in the middle of a fight. No-ops when the
                // style is off or the shader did not compile.
                //
                // After the compat layers are installed on purpose: whether a VEF
                // shield family is drawing decides whether its atlas is needed, and
                // that is only known once those patches have resolved their types.
                ShieldHexMap.Prewarm();

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
