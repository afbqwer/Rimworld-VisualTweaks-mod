using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Sparks when damage is absorbed by metal — metal apparel (useDeflectMetalEffect
    /// or a metal stuff) or a mechanoid's natural metal body. This replaces the old
    /// Bullet.Impact hook: ArmorUtility is the shared armor step every damage type
    /// flows through, so melee and explosions spark too, and the judgment is the
    /// vanilla metalArmor flag rather than IsMechanoid alone.
    ///
    /// Two cooperating hooks:
    ///   GetPostArmorDamage prefix — opens a "damage event", captures the incoming
    ///                               amount and resets the once-per-event guard.
    ///   ApplyArmor postfix        — reads the real out metalArmor flag; the first
    ///                               metal layer of the event spawns one burst.
    ///
    /// Why read ApplyArmor's flag instead of GetPostArmorDamage's out flags: those
    /// (deflected/diminishedByMetalArmor) are only set when the armor actually reduced
    /// this hit. A zero-armor mechanoid, or a high-AP round that punches straight
    /// through, leaves them false even though metalArmor was true. ApplyArmor's flag
    /// is the honest "this layer is metal" signal regardless of the armor roll.
    ///
    /// ApplyArmor is private and only ever called from GetPostArmorDamage (once per
    /// covering apparel plus once for natural armor), all synchronously on the main
    /// thread, so the bracket yields exactly one burst per damage event. The static
    /// event state is safe because the apparel.TakeDamage inside ApplyArmor cannot
    /// reenter GetPostArmorDamage — apparel is not a Pawn.
    ///
    /// Position is the pawn's centre and the spray is isotropic: the armor step has
    /// neither an exact impact point nor a travel direction to hand over.
    /// </summary>
    internal static class MechSparkPatches
    {
        // Per damage-event context, opened by the GetPostArmorDamage prefix and
        // consumed by the first metal ApplyArmor postfix. Main-thread only.
        private static float eventAmount;
        private static bool sparked;

        internal static void Apply(Harmony harmony)
        {
            MethodInfo armorEntry = AccessTools.Method(
                typeof(ArmorUtility),
                nameof(ArmorUtility.GetPostArmorDamage));
            MethodInfo applyArmor = AccessTools.Method(typeof(ArmorUtility), "ApplyArmor");
            MethodInfo entryPrefix = AccessTools.Method(
                typeof(MechSparkPatches),
                nameof(GetPostArmorDamagePrefix));
            MethodInfo armorPostfix = AccessTools.Method(
                typeof(MechSparkPatches),
                nameof(ApplyArmorPostfix));

            if (armorEntry == null || applyArmor == null
                || entryPrefix == null || armorPostfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch ArmorUtility for metal sparks.");
                return;
            }

            try
            {
                harmony.Patch(armorEntry, prefix: new HarmonyMethod(entryPrefix));
                harmony.Patch(applyArmor, postfix: new HarmonyMethod(armorPostfix));
                VisualTweaksLog.Info("Patched ArmorUtility.GetPostArmorDamage + ApplyArmor.");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch ArmorUtility: " + ex);
            }
        }

        private static void GetPostArmorDamagePrefix(float amount)
        {
            eventAmount = amount;
            sparked = false;
        }

        private static void ApplyArmorPostfix(Pawn pawn, bool metalArmor)
        {
            if (!MechSparkBridge.Active || !metalArmor || sparked)
            {
                return;
            }

            if (pawn?.Map == null)
            {
                return;
            }

            sparked = true;
            OverlayMapComponent component = pawn.Map.GetComponent<OverlayMapComponent>();
            // Zero travel direction -> the layer emits an isotropic burst.
            component?.NotifyMechSpark(pawn.TrueCenter(), Vector3.zero, eventAmount);
        }
    }
}