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
    ///   GetPostArmorDamage prefix — opens a damage event as a <see cref="SparkEvent"/>
    ///       passed to its own postfix through Harmony's ref __state, capturing the
    ///       ORIGINAL incoming amount (the body mutates it by ref afterwards).
    ///   ApplyArmor postfix — reads the real out metalArmor flag and latches
    ///       MetalPresent on the current event. Reading this flag (not the entry's
    ///       deflected/diminished outs) is what catches zero-armor mechanoids and
    ///       high-AP punch-through: those leave the entry outs false even though a
    ///       metal layer was struck.
    ///   GetPostArmorDamage postfix — the spawn point. By now the deflected /
    ///       diminished outs are final, so the backward bias is graded by the armor
    ///       outcome, and the event is emitted exactly once (a postfix runs once per
    ///       call, so no separate once-guard is needed).
    ///
    /// On __state: Harmony's __state only travels between a prefix and postfix of the
    /// SAME method. It carries the GetPostArmorDamage event (amount + metal flag)
    /// cleanly, fresh per call with no manual reset. It cannot cross methods, so the
    /// two nested bridges stay static: s_instigator feeds in from the OUTER
    /// ApplyDamageToPart, and s_current lets the INNER ApplyArmor reach the event
    /// object that __state also references. Both are main-thread only; ApplyArmor is
    /// only ever called from GetPostArmorDamage, and the apparel.TakeDamage inside it
    /// cannot reenter the armor step (apparel is not a Pawn), so the bridges are safe.
    ///
    /// Direction is resolved per damage source in <see cref="ResolveTravelDir"/>:
    /// explosions use dinfo.Angle (blast centre -> target) to spray outward, ranged
    /// projectiles use dinfo.Angle (exact travel direction) to deflect backward, and
    /// everything else falls back to the instigator's attacker -> target position
    /// vector (isotropic when none is usable). Angle is only trusted for isRanged /
    /// isExplosive defs, which set it explicitly; other damage defaults it to a random
    /// 0..359 and must not use it.
    /// </summary>
    internal static class MechSparkPatches
    {
        // Per damage-event data, created by the GetPostArmorDamage prefix and handed
        // to its postfix via ref __state. A reference type so the inner ApplyArmor
        // hook can latch MetalPresent on the same instance through s_current.
        private sealed class SparkEvent
        {
            public float Amount;
            public bool MetalPresent;
        }

        // Cross-method bridges (see class remarks). Main-thread only.
        private static SparkEvent? s_current;
        private static Thing? s_instigator;
        private static DamageDef? s_damageDef;
        private static float s_angle;

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
            s_instigator = dinfo.Instigator;
            // Capture the ORIGINAL def and angle here: GetPostArmorDamage may convert
            // Sharp -> Blunt afterwards, but isRanged/isExplosive live on the source
            // def (Bullet, Bomb, ...), and the angle is the source's travel direction.
            s_damageDef = dinfo.Def;
            s_angle = dinfo.Angle;
        }

        private static void GetPostArmorDamagePrefix(float amount, ref SparkEvent __state)
        {
            __state = new SparkEvent { Amount = amount };
            s_current = __state;
        }

        private static void ApplyArmorPostfix(bool metalArmor)
        {
            if (metalArmor && s_current != null)
            {
                s_current.MetalPresent = true;
            }
        }

        private static void GetPostArmorDamagePostfix(
            Pawn pawn,
            bool deflectedByMetalArmor,
            bool diminishedByMetalArmor,
            SparkEvent __state)
        {
            // The event is over; __state still holds its data independently.
            s_current = null;

            if (!MechSparkBridge.Active || !__state.MetalPresent)
            {
                return;
            }

            if (pawn?.Map == null)
            {
                return;
            }

            float bias = deflectedByMetalArmor ? BiasDeflected
                : diminishedByMetalArmor ? BiasDiminished
                : BiasPlainMetal;

            Vector3 travelDir = ResolveTravelDir(pawn);

            // The burst is spawned on the side of the body that faced the attack,
            // not at the corpse's exact centre (see MechSparkLayer.Spawn).
            Vector3 impactDir = ResolveImpactDir(travelDir);

            OverlayMapComponent component = pawn.Map.GetComponent<OverlayMapComponent>();
            component?.NotifyMechSpark(
                pawn.DrawPos,
                travelDir,
                impactDir,
                bias,
                __state.Amount,
                pawn.BodySize);
        }

        /// <summary>
        /// Direction from the pawn toward the attack source, used only to place the
        /// burst on the hit side of the body. <see cref="ResolveTravelDir"/> points
        /// AWAY from the source for ranged and melee (the fan axis is the incoming
        /// shot) but TOWARD the blast centre for explosions (sparks fan outward), so
        /// only the explosive case needs the same vector.
        /// </summary>
        private static Vector3 ResolveImpactDir(Vector3 travelDir)
        {
            if (travelDir.sqrMagnitude < 1e-6f)
            {
                return Vector3.zero;
            }

            return s_damageDef != null && s_damageDef.isExplosive
                ? travelDir
                : -travelDir;
        }

        /// <summary>
        /// Resolves the spark travel direction by damage source. The shader always
        /// fans sparks toward -travelDir, so each branch hands it the vector whose
        /// negation is the desired spray:
        ///   Explosive — dinfo.Angle points from the blast centre to the target
        ///       (outward, set explicitly in DamageWorker.ExplosionDamageThing). Sparks
        ///       should fly outward, away from the blast, so travelDir is the INWARD
        ///       vector (-outward). The instigator is the launcher, NOT the blast
        ///       centre, so position derivation would be wrong here.
        ///   Ranged — dinfo.Angle is the projectile's travel direction (shooter ->
        ///       target, set in Bullet.Impact). Sparks deflect back along it, so
        ///       travelDir is the travel direction itself. Verified: every actively
        ///       used vanilla isRanged def (Bullet/Arrow/ArrowHighVelocity/RangedStab)
        ///       is a Bullet projectile that sets a real angle; the Beam def is unused.
        ///   Other (melee etc.) — best-effort attacker -> target from the instigator
        ///       position; zero (isotropic) when no usable instigator exists.
        /// Angle is deliberately not trusted for non-ranged/non-explosive damage: it
        /// defaults to a random 0..359 whenever a source never calls SetAngle.
        /// </summary>
        private static Vector3 ResolveTravelDir(Pawn pawn)
        {
            DamageDef? def = s_damageDef;
            if (def != null)
            {
                // AngleFlat / ExactRotation.eulerAngles.y are Unity yaw (0 = +z, 90 =
                // +x); Euler(0, angle, 0) * forward is their exact inverse.
                if (def.isExplosive)
                {
                    Vector3 outward = Quaternion.Euler(0f, s_angle, 0f) * Vector3.forward;
                    return -outward;
                }

                if (def.isRanged)
                {
                    return Quaternion.Euler(0f, s_angle, 0f) * Vector3.forward;
                }
            }

            Thing? instigator = s_instigator;
            if (instigator != null && instigator != pawn
                && instigator.Spawned && instigator.Map == pawn.Map)
            {
                Vector3 flat = (pawn.TrueCenter() - instigator.TrueCenter()).Yto0();
                if (flat.sqrMagnitude > 0.01f)
                {
                    return flat.normalized;
                }
            }

            return Vector3.zero;
        }
    }
}