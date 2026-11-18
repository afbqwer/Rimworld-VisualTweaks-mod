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
    /// or a metal stuff) or a mechanoid's natural metal body. ArmorUtility is the
    /// shared armor step every damage type flows through, so melee and explosions
    /// spark too, and the judgment is the vanilla metalArmor flag rather than
    /// IsMechanoid alone.
    ///
    /// Four cooperating hooks, in call order:
    ///   DamageWorker_AddInjury.ApplyDamageToPart prefix — captures dinfo.Instigator
    ///       (the shooter / attacker) for a best-effort attack direction. This is the
    ///       only place upstream of the armor step that still holds the DamageInfo.
    ///   GetPostArmorDamage prefix — opens a damage event: captures the ORIGINAL
    ///       incoming amount (the body mutates it by ref afterwards) and resets the
    ///       metal / once-per-event guards.
    ///   ApplyArmor postfix — reads the real out metalArmor flag and latches
    ///       metalPresent. Reading this flag (not the entry's deflected/diminished
    ///       outs) is what catches zero-armor mechanoids and high-AP punch-through:
    ///       those leave the entry outs false even though a metal layer was struck.
    ///   GetPostArmorDamage postfix — the spawn point. By now the deflected /
    ///       diminished outs are final, so the backward bias can be graded by the
    ///       armor outcome, and the event is emitted exactly once.
    ///
    /// Direction is best-effort: the attacker->target vector from the instigator's
    /// position (meaningful for bullets and melee, which both set a real instigator).
    /// DamageInfo.Angle is deliberately NOT used — it defaults to a random 0..359 for
    /// any damage that never calls SetAngle, so it cannot be trusted blindly. When no
    /// usable instigator exists (environmental damage, some explosions) the burst is
    /// isotropic.
    ///
    /// All hooks run synchronously on the main thread; ApplyArmor is only ever called
    /// from GetPostArmorDamage, and the apparel.TakeDamage inside it cannot reenter
    /// the armor step (apparel is not a Pawn), so the static event state is safe.
    /// </summary>
    internal static class MechSparkPatches
    {
        // Per damage-event context, opened by the prefixes and consumed by the
        // GetPostArmorDamage postfix. Main-thread only.
        private static Thing? eventInstigator;
        private static float eventAmount;
        private static bool metalPresent;
        private static bool sparked;

        // Backward bias by armor outcome. A full deflect throws most sparks back
        // along the attack (toward the attacker), a partial diminish fewer, and metal
        // that took the hit outright (zero armor / AP punch-through) sprays isotropically.
        private const float BiasDeflected = 0.8f;
        private const float BiasDiminished = 0.45f;
        private const float BiasPlainMetal = 0f;

        internal static void Apply(Harmony harmony)
        {
            MethodInfo applyPart = AccessTools.Method(
                typeof(DamageWorker_AddInjury), "ApplyDamageToPart");
            MethodInfo applyPartPrefix = AccessTools.Method(
                typeof(MechSparkPatches), nameof(ApplyDamageToPartPrefix));

            MethodInfo armorEntry = AccessTools.Method(
                typeof(ArmorUtility), nameof(ArmorUtility.GetPostArmorDamage));
            MethodInfo entryPrefix = AccessTools.Method(
                typeof(MechSparkPatches), nameof(GetPostArmorDamagePrefix));
            MethodInfo entryPostfix = AccessTools.Method(
                typeof(MechSparkPatches), nameof(GetPostArmorDamagePostfix));

            MethodInfo applyArmor = AccessTools.Method(typeof(ArmorUtility), "ApplyArmor");
            MethodInfo armorPostfix = AccessTools.Method(
                typeof(MechSparkPatches), nameof(ApplyArmorPostfix));

            if (applyPart == null || applyPartPrefix == null
                || armorEntry == null || entryPrefix == null || entryPostfix == null
                || applyArmor == null || armorPostfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch ArmorUtility for metal sparks.");
                return;
            }

            try
            {
                harmony.Patch(applyPart, prefix: new HarmonyMethod(applyPartPrefix));
                harmony.Patch(
                    armorEntry,
                    prefix: new HarmonyMethod(entryPrefix),
                    postfix: new HarmonyMethod(entryPostfix));
                harmony.Patch(applyArmor, postfix: new HarmonyMethod(armorPostfix));
                VisualTweaksLog.Info(
                    "Patched DamageWorker_AddInjury.ApplyDamageToPart + ArmorUtility for metal sparks.");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch ArmorUtility: " + ex);
            }
        }

        private static void ApplyDamageToPartPrefix(DamageInfo dinfo)
        {
            eventInstigator = dinfo.Instigator;
        }

        private static void GetPostArmorDamagePrefix(float amount)
        {
            eventAmount = amount;
            metalPresent = false;
            sparked = false;
        }

        private static void ApplyArmorPostfix(bool metalArmor)
        {
            if (metalArmor)
            {
                metalPresent = true;
            }
        }

        private static void GetPostArmorDamagePostfix(
            Pawn pawn,
            bool deflectedByMetalArmor,
            bool diminishedByMetalArmor)
        {
            if (!MechSparkBridge.Active || !metalPresent || sparked)
            {
                return;
            }

            if (pawn?.Map == null)
            {
                return;
            }

            sparked = true;

            float bias = deflectedByMetalArmor ? BiasDeflected
                : diminishedByMetalArmor ? BiasDiminished
                : BiasPlainMetal;

            // Best-effort attack direction: attacker -> target. Falls back to zero
            // (isotropic) when the instigator is missing, unspawned, off-map, or the
            // target itself.
            Vector3 travelDir = Vector3.zero;
            Thing? instigator = eventInstigator;
            if (instigator != null && instigator != pawn
                && instigator.Spawned && instigator.Map == pawn.Map)
            {
                Vector3 flat = (pawn.TrueCenter() - instigator.TrueCenter()).Yto0();
                if (flat.sqrMagnitude > 0.01f)
                {
                    travelDir = flat.normalized;
                }
            }

            OverlayMapComponent component = pawn.Map.GetComponent<OverlayMapComponent>();
            component?.NotifyMechSpark(pawn.TrueCenter(), travelDir, bias, eventAmount);
        }
    }
}