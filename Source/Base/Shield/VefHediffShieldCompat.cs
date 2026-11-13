using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Brings VEF's hediff-granted shields into the personal-shield replacement.
    ///
    /// <c>VEF.Hediffs.HediffComp_Shield</c> is the third VEF shield family and the
    /// only one that is not a ThingComp. It is granted by hediffs (Vanilla Psycasts
    /// Expanded's fire shield, creature/ability shields, ...) and VEF draws it from
    /// <c>Pawn.DrawAt</c> through its <c>ShieldsSystem</c> patch: one quad at the
    /// pawn's draw position, using the def's <c>graphic</c> (size, offset and colour).
    ///
    /// The draw method itself has no state check — VEF paints the bubble for as long
    /// as the hediff exists, even while the shield is recharging — so the replacement
    /// deliberately differs there: a depleted shield hides until it recharges, and
    /// the break animation carries the moment it went down. That matches what the
    /// other two shield families do in this mod; leaving a solid dome over an empty
    /// shield would be the one surface that lies about its own state.
    ///
    /// Everything is bound by reflection: VEF is not a compile reference and a missing
    /// VEF must cost nothing. The comp itself is a HediffComp, not a ThingComp, which
    /// is why the personal renderer and its logs key on object rather than a comp base.
    ///
    /// The graphic's texture is not preserved. These switches exist to replace the
    /// surface, and unlike the vanilla families there is no separate "only vanilla
    /// types" rule to opt out per def; the def's size, offset and colour still drive
    /// the dome, so its footprint and tint stay its own.
    ///
    /// Only shields that do not override <c>DrawAt</c> themselves are covered. An
    /// override is a mod drawing its own effect through the same comp and is left
    /// alone, the same way the vanilla shield patches leave custom draw methods alone.
    /// </summary>
    internal static class VefHediffShieldCompat
    {
        private const string CompTypeName = "VEF.Hediffs.HediffComp_Shield";
        private const string PropsTypeName = "VEF.Hediffs.HediffCompProperties_Shield";

        private static Type? compType;
        private static FieldInfo? energy;
        private static FieldInfo? useEnergy;
        private static FieldInfo? ticksTillReset;
        private static FieldInfo? graphic;

        private static bool fatal;
        private static bool signalsBroken;
        private static bool patched;

        /// <summary>
        /// True when VEF is loaded, the shield members were bound and the draw patch
        /// is installed. The settings window and the hex prewarm both read this.
        /// </summary>
        internal static bool Available => patched;

        /// <summary>
        /// True when any hediff on this pawn carries VEF's shield comp. Used only by
        /// the per-shield style gizmo, so a missing VEF costs one type check.
        /// </summary>
        internal static bool PawnHasShield(Pawn? pawn)
        {
            Type? type = compType;
            if (pawn == null || type == null || pawn.health?.hediffSet == null)
            {
                return false;
            }

            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                if (hediffs[i] is not HediffWithComps withComps
                    || withComps.comps == null)
                {
                    continue;
                }

                List<HediffComp> comps = withComps.comps;
                for (int j = 0; j < comps.Count; j++)
                {
                    if (type.IsInstanceOfType(comps[j]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static void Apply(Harmony harmony)
        {
            try
            {
                compType = AccessTools.TypeByName(CompTypeName);
                if (compType == null)
                {
                    // VEF absent is the normal case, not a warning.
                    return;
                }

                if (!BindMembers())
                {
                    compType = null;
                    return;
                }

                PatchDrawAt(harmony);
                PatchPreApplyDamage(harmony);
            }
            catch (Exception ex)
            {
                // A framework with an unexpected shape must not abort the rest of
                // the mod's startup; this family simply keeps the stock drawing.
                compType = null;
                patched = false;
                Log.Error("[Visual Tweaks] Hediff shield compat setup failed; stock"
                    + " drawing kept: " + ex);
            }
        }

        /// <summary>
        /// Binds the state the draw and the damage signal need. The shield's own
        /// <c>ShieldActive</c> property is deliberately not bound: it is exactly
        /// <c>energy &gt; 0 || !useEnergy</c>, and reading the two fields avoids one
        /// more name that a framework update could move.
        ///
        /// The graphic lives on HediffCompProperties_Draw, one or more base classes up,
        /// so the field is searched up the chain.
        /// </summary>
        private static bool BindMembers()
        {
            Type? type = compType;
            Type? propsType = AccessTools.TypeByName(PropsTypeName);
            if (type == null || propsType == null)
            {
                Log.Warning("[Visual Tweaks] VEF hediff shield types not found;"
                    + " hediff shields keep the stock drawing.");
                return false;
            }

            energy = FindField(type, "energy");
            useEnergy = FindField(type, "useEnergy");
            ticksTillReset = FindField(type, "ticksTillReset");
            graphic = FindField(propsType, "graphic");

            bool complete = energy != null
                && useEnergy != null
                && ticksTillReset != null
                && graphic != null;

            if (!complete)
            {
                Log.Warning("[Visual Tweaks] VEF HediffComp_Shield is missing members"
                    + " this mod draws with; hediff shields keep the stock drawing.");
                return false;
            }

            if (energy!.FieldType != typeof(float)
                || useEnergy!.FieldType != typeof(bool)
                || ticksTillReset!.FieldType != typeof(int)
                || graphic!.FieldType != typeof(GraphicData))
            {
                Log.Warning("[Visual Tweaks] VEF HediffComp_Shield members have"
                    + " unexpected types; hediff shields keep the stock drawing.");
                return false;
            }

            return true;
        }

        private static FieldInfo? FindField(Type? type, string name)
        {
            for (Type? current = type; current != null; current = current.BaseType)
            {
                FieldInfo? field = current.GetField(
                    name,
                    BindingFlags.Instance
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly);

                if (field != null)
                {
                    return field;
                }
            }

            return null;
        }

        private static void PatchDrawAt(Harmony harmony)
        {
            Type? type = compType;
            if (type == null)
            {
                return;
            }

            MethodInfo? target = AccessTools.Method(
                type,
                "DrawAt",
                new[] { typeof(Vector3) });
            MethodInfo? prefix = AccessTools.Method(
                typeof(VefHediffShieldCompat),
                nameof(DrawAtPrefix));

            if (target == null || prefix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch VEF HediffComp_Shield."
                    + "DrawAt; hediff shields keep the stock drawing.");
                return;
            }

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                patched = true;
                VisualTweaksLog.Info("Patched VEF HediffComp_Shield.DrawAt"
                    + " (hediff shield dome).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch hediff shield DrawAt: "
                    + ex);
            }
        }

        /// <summary>
        /// <c>PreApplyDamage</c> is the public entry every absorption and both break
        /// paths go through. The prefix snapshots the bearing and the charge, because
        /// the original may zero the energy and reset it in the same call when the
        /// def has no recharge delay.
        /// </summary>
        private static void PatchPreApplyDamage(Harmony harmony)
        {
            Type? type = compType;
            if (type == null)
            {
                return;
            }

            MethodInfo? target = AccessTools.Method(
                type,
                "PreApplyDamage",
                new[] { typeof(DamageInfo).MakeByRefType(), typeof(bool).MakeByRefType() });
            MethodInfo? prefix = AccessTools.Method(
                typeof(VefHediffShieldCompat),
                nameof(PreApplyDamagePrefix));
            MethodInfo? postfix = AccessTools.Method(
                typeof(VefHediffShieldCompat),
                nameof(PreApplyDamagePostfix));

            if (target == null || prefix == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch VEF HediffComp_Shield."
                    + "PreApplyDamage; hediff shield hits and breaks will not animate.");
                return;
            }

            try
            {
                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched VEF HediffComp_Shield.PreApplyDamage"
                    + " (hediff shield hit and break signals).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch hediff shield PreApplyDamage: "
                    + ex);
            }
        }

        private struct DamageSnapshot
        {
            public bool WasActive;
            public float EnergyBefore;
            public float Angle;
        }

        private static void PreApplyDamagePrefix(
            HediffComp __instance,
            ref DamageInfo dinfo,
            out DamageSnapshot __state)
        {
            __state = default;
            if (__instance == null
                || fatal
                || signalsBroken
                || !ShieldDomeBridge.VefHediffActive)
            {
                return;
            }

            try
            {
                if (!ShieldActive(__instance))
                {
                    return;
                }

                __state.WasActive = true;
                __state.EnergyBefore = ReadFloat(energy!, __instance);
                __state.Angle = dinfo.Angle;
            }
            catch (Exception ex)
            {
                signalsBroken = true;
                Log.ErrorOnce(
                    "[Visual Tweaks] Hediff shield hit snapshot failed; hit and break"
                        + " effects disabled for this session: " + ex,
                    typeof(VefHediffShieldCompat).GetHashCode());
            }
        }

        private static void PreApplyDamagePostfix(
            HediffComp __instance,
            ref bool absorbed,
            DamageSnapshot __state)
        {
            if (!__state.WasActive
                || __instance == null
                || fatal
                || signalsBroken
                || !ShieldDomeBridge.VefHediffActive)
            {
                return;
            }

            try
            {
                float energyNow = ReadFloat(energy!, __instance);
                int resetTicks = ReadInt(ticksTillReset!, __instance);
                bool draining = ReadBool(useEnergy!, __instance);

                // A real down period only exists when the shield uses charge and the
                // def asks for a recharge delay. VEF's defaults reset instantly inside
                // Break(), leaving no state a dissolve could play over; in that case the
                // hit below is the only honest feedback.
                if (draining && resetTicks > 0 && energyNow <= 0f)
                {
                    if (ShieldStyle.For(
                            ShieldHexTarget.Personal,
                            __instance.Pawn).Break
                        != ShieldBreakStyle.Vanilla)
                    {
                        PersonalShieldDissolveLog.Begin(
                            __instance,
                            __instance.Pawn,
                            __state.Angle);
                    }

                    return;
                }

                if (absorbed || energyNow < __state.EnergyBefore - 0.0001f)
                {
                    PersonalShieldHitLog.Notify(__instance, __state.Angle);
                }
            }
            catch (Exception ex)
            {
                // One ErrorOnce rather than one per hit; the dome keeps drawing.
                signalsBroken = true;
                Log.ErrorOnce(
                    "[Visual Tweaks] Hediff shield hit bookkeeping failed; hit and"
                        + " break effects disabled for this session: " + ex,
                    typeof(VefHediffShieldCompat).GetHashCode() ^ 0x3D1);
            }
        }

        /// <summary>
        /// Priority.First for the same reason as the other replacements: Harmony stops
        /// at the first prefix returning false, so this must run before another
        /// replacement could skip the original and leave neither drawing.
        /// </summary>
        [HarmonyPriority(Priority.First)]
        private static bool DrawAtPrefix(HediffComp __instance, Vector3 drawPos)
        {
            // Returning true means "run VEF". Every early exit is a case where the
            // stock drawing is the better answer.
            if (fatal
                || __instance == null
                || !ShieldDomeBridge.VefHediffActive)
            {
                return true;
            }

            try
            {
                Pawn? pawn = __instance.Pawn;
                if (pawn == null || !pawn.Spawned || pawn.Dead)
                {
                    return true;
                }

                if (__instance.props == null)
                {
                    return true;
                }

                GraphicData? data = graphic!.GetValue(__instance.props) as GraphicData;
                if (data == null)
                {
                    return true;
                }

                // The graphic's own size is the stock footprint. The dome is circular,
                // so a non-square def gets the larger axis rather than being smaller
                // than the art it replaces.
                float size = Mathf.Max(data.drawSize.x, data.drawSize.y);
                if (size <= 0.01f)
                {
                    return true;
                }

                bool active = ShieldActive(__instance);
                Vector3 center = drawPos + data.drawOffset;

                if (PersonalShieldRenderer.Draw(
                        __instance,
                        pawn,
                        center,
                        size,
                        active,
                        active,
                        data.color))
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                // A throwing prefix would be retried every frame by every hediff.
                fatal = true;
                Log.Error("[Visual Tweaks] Hediff shield draw failed, stock drawing"
                    + " restored: " + ex);
                return true;
            }
        }

        /// <summary>VEF's own <c>ShieldActive</c> expression.</summary>
        private static bool ShieldActive(HediffComp comp)
        {
            return ReadBool(useEnergy!, comp)
                ? ReadFloat(energy!, comp) > 0f
                : true;
        }

        private static float ReadFloat(FieldInfo field, object instance)
        {
            object? value = field.GetValue(instance);
            return value is float number ? number : 0f;
        }

        private static int ReadInt(FieldInfo field, object instance)
        {
            object? value = field.GetValue(instance);
            return value is int number ? number : 0;
        }

        private static bool ReadBool(FieldInfo field, object instance)
        {
            object? value = field.GetValue(instance);
            return value is bool flag && flag;
        }
    }
}
