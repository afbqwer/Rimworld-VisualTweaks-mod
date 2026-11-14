using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Brings the VFES repulsors into the shield-dome replacement.
    ///
    /// The repulsor does not use <see cref="CompProjectileInterceptor"/>. Its shield is
    /// <c>VEF.Apparels.CompShieldField</c>, which lives in the Vanilla Expanded
    /// Framework and draws a single billboard quad (<c>Other/ShieldBubble</c>) from
    /// its own <c>PostDraw</c>. None of the vanilla shield patches can see that comp,
    /// so without this file a repulsor keeps its stock billboard while every vanilla
    /// projector nearby gets the new dome.
    ///
    /// Everything here is bound by reflection: VEF is not a compile reference of this
    /// assembly, and a missing VEF must cost nothing. When the type is absent the
    /// class does nothing, exactly like <see cref="CombatExtendedShieldCompat"/>.
    ///
    /// What the framework's draw actually does, and therefore what has to be carried
    /// over rather than re-invented:
    ///
    ///   * it draws only while <c>active</c> and (<c>Energy &gt; 0</c> or the shield
    ///     is indestructible), so "present" is a two-term state, not just <c>active</c>;
    ///   * its opacity is the alpha of the def's health colour, which slides from the
    ///     lowest health point to the shield colour (usually white) at full energy;
    ///   * the quad is nudged and shrunk for a few frames after a hit, which the dome's
    ///     own impact reaction replaces, so it is not copied here.
    ///
    /// The comp's own hit and break effects (sound, flash, dust, EMP flecks) are left
    /// alone. This file only takes over the surface, which is the same split the
    /// vanilla shield patches make.
    ///
    /// Built-in shields only: a shield field worn as apparel is a belt, and VEF gives
    /// it the same bubble every other belt uses. Those are left to
    /// <see cref="PersonalShieldPatches"/>' family rather than being drawn as a dome.
    /// </summary>
    internal static class VefRepulsorShieldCompat
    {
        private const string CompTypeName = "VEF.Apparels.CompShieldField";
        private const string PropsTypeName = "VEF.Apparels.CompProperties_ShieldField";
        private const string ColourPointTypeName = "VEF.Apparels.HealthColorPoint";

        private static Type? compType;
        private static PropertyInfo? shieldRadius;
        private static PropertyInfo? energy;
        private static PropertyInfo? maxEnergy;
        private static PropertyInfo? indestructible;
        private static FieldInfo? activeField;
        private static FieldInfo? shieldColour;
        private static FieldInfo? healthColorPoints;
        private static FieldInfo? healthPercent;
        private static FieldInfo? healthColor;

        // Compiled readers for the values the draw path reads every frame; the
        // FieldInfo/PropertyInfo above are kept for binding and for the one-time colour
        // ramp parse. See <see cref="VefMemberAccess"/>.
        private static Func<object, float>? shieldRadiusOf;
        private static Func<object, float>? energyOf;
        private static Func<object, float>? maxEnergyOf;
        private static Func<object, bool>? indestructibleOf;
        private static Func<object, bool>? activeOf;
        private static Func<object, Color>? shieldColourOf;

        private static MethodInfo? absorbDamageTarget;
        private static MethodInfo? recordImpactMethod;
        private static MethodInfo? energyShieldTickTarget;

        /// <summary>
        /// The projectile seen by the injected <c>EnergyShieldTick</c> call, waiting
        /// for the absorb postfix. One slot is enough: the transpiler records it
        /// immediately before <c>AbsorbDamage</c>, which runs synchronously on the
        /// same thread, and the postfix clears it on every call.
        /// </summary>
        [ThreadStatic]
        private static ThingComp? pendingImpactComp;

        [ThreadStatic]
        private static Thing? pendingImpactProjectile;

        private static readonly ConditionalWeakTable<object, ColourCurve> ColourCurves =
            new ConditionalWeakTable<object, ColourCurve>();

        private static readonly ConditionalWeakTable<object, ColourCurve>.CreateValueCallback
            CreateCurve = CreateColourCurve;

        private static bool fatal;
        private static bool signalsBroken;
        private static bool patched;

        /// <summary>
        /// True when VEF is loaded, the shield-field members were bound and the draw
        /// patch is installed. The settings window and the hex prewarm both read this,
        /// so a player without the framework — or with a VEF version whose method
        /// names moved — never sees a switch that cannot do anything.
        /// </summary>
        internal static bool Available => patched;

        /// <summary>True when this comp is VEF's shield field (a repulsor dome).</summary>
        internal static bool IsRepulsor(ThingComp? comp) =>
            comp != null && compType != null && compType.IsInstanceOfType(comp);

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

                PatchPostDraw(harmony);
                PatchAbsorbDamage(harmony);
                PatchEnergyDepleted(harmony);
                PatchEnergyShieldTick(harmony);
            }
            catch (Exception ex)
            {
                // A framework with an unexpected shape must not abort the rest of
                // the mod's startup; this family simply keeps the stock drawing.
                compType = null;
                patched = false;
                Log.Error("[Visual Tweaks] Repulsor compat setup failed; stock drawing"
                    + " kept: " + ex);
            }
        }

        /// <summary>
        /// Binds the state the draw needs. If any of it is missing, the whole file
        /// backs off: a dome with a guessed radius or colour is worse than the stock
        /// billboard, and the version this was written against is the one whose member
        /// names are listed here.
        ///
        /// Each per-frame member is also compiled into a box-free reader here. The
        /// plain <c>GetValue</c> reads used to box a float or a bool on every visible
        /// repulsor every frame; the compiled readers return the same values without
        /// that allocation.
        /// </summary>
        private static bool BindMembers()
        {
            Type? type = compType;
            Type? propsType = AccessTools.TypeByName(PropsTypeName);
            Type? colourPointType = AccessTools.TypeByName(ColourPointTypeName);
            if (type == null || propsType == null || colourPointType == null)
            {
                Log.Warning("[Visual Tweaks] VEF shield field types not found;"
                    + " repulsor shields keep the stock drawing.");
                return false;
            }

            shieldRadius = AccessTools.Property(type, "ShieldRadius");
            energy = AccessTools.Property(type, "Energy");
            maxEnergy = AccessTools.Property(type, "MaxEnergy");
            indestructible = AccessTools.Property(type, "Indestructible");
            activeField = AccessTools.Field(type, "active");
            shieldColour = AccessTools.Field(propsType, "shieldColour");
            healthColorPoints = AccessTools.Field(propsType, "healthColorPoints");
            healthPercent = AccessTools.Field(colourPointType, "healthPercent");
            healthColor = AccessTools.Field(colourPointType, "color");

            bool complete = shieldRadius != null
                && energy != null
                && maxEnergy != null
                && indestructible != null
                && activeField != null
                && shieldColour != null
                && healthColorPoints != null
                && healthPercent != null
                && healthColor != null;

            if (!complete)
            {
                Log.Warning("[Visual Tweaks] VEF CompShieldField is missing members this"
                    + " mod draws with; repulsor shields keep the stock drawing.");
                return false;
            }

            if (shieldRadius!.PropertyType != typeof(float)
                || energy!.PropertyType != typeof(float)
                || maxEnergy!.PropertyType != typeof(float)
                || indestructible!.PropertyType != typeof(bool)
                || activeField!.FieldType != typeof(bool)
                || shieldColour!.FieldType != typeof(Color)
                || healthPercent!.FieldType != typeof(float)
                || healthColor!.FieldType != typeof(Color))
            {
                Log.Warning("[Visual Tweaks] VEF CompShieldField members have unexpected"
                    + " types; repulsor shields keep the stock drawing.");
                return false;
            }

            shieldRadiusOf = VefMemberAccess.CompileProperty<float>(shieldRadius);
            energyOf = VefMemberAccess.CompileProperty<float>(energy);
            maxEnergyOf = VefMemberAccess.CompileProperty<float>(maxEnergy);
            indestructibleOf =
                VefMemberAccess.CompileProperty<bool>(indestructible);
            activeOf = VefMemberAccess.CompileField<bool>(activeField);
            shieldColourOf = VefMemberAccess.CompileField<Color>(shieldColour);

            if (shieldRadiusOf == null
                || energyOf == null
                || maxEnergyOf == null
                || indestructibleOf == null
                || activeOf == null
                || shieldColourOf == null)
            {
                Log.Warning("[Visual Tweaks] VEF CompShieldField members could not be"
                    + " compiled; repulsor shields keep the stock drawing.");
                return false;
            }

            return true;
        }

        private static void PatchPostDraw(Harmony harmony)
        {
            Type? type = compType;
            if (type == null)
            {
                return;
            }

            MethodInfo? target = AccessTools.Method(type, "PostDraw");
            MethodInfo? prefix = AccessTools.Method(
                typeof(VefRepulsorShieldCompat),
                nameof(PostDrawPrefix));

            if (target == null || prefix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch VEF CompShieldField.PostDraw;"
                    + " repulsor shields keep the stock drawing.");
                return;
            }

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                patched = true;
                VisualTweaksLog.Info("Patched VEF CompShieldField.PostDraw"
                    + " (repulsor shield dome).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch repulsor PostDraw: " + ex);
            }
        }

        /// <summary>
        /// Both absorb overloads end in the float one, so patching that single method
        /// records every intercepted projectile exactly once.
        ///
        /// The angle is NOT the quantity vanilla's <c>lastInterceptAngle</c> is. VEF
        /// passes <c>ExactRotation.eulerAngles.y</c>, the launch-to-target flight
        /// bearing, which is constant for the whole trajectory; vanilla uses the
        /// bearing from the projectile's position at the rim crossing towards the
        /// centre. The two coincide only for shots aimed at the centre. On an
        /// off-centre shot they differ by the grazing angle, and on a 16.9-cell
        /// repulsor that put the visuals on the wrong side of the dome. The 90-degree
        /// conversion between Unity's Y-euler convention and the <c>AngleToFlat</c>
        /// convention is still required, but the reference point is what has to be
        /// replaced: <see cref="PatchEnergyShieldTick"/> records the real projectile,
        /// and the postfix prefers its position over the flight bearing.
        /// </summary>
        private static void PatchAbsorbDamage(Harmony harmony)
        {
            Type? type = compType;
            if (type == null)
            {
                return;
            }

            MethodInfo? target = AccessTools.Method(
                type,
                "AbsorbDamage",
                new[] { typeof(float), typeof(DamageDef), typeof(float) });
            MethodInfo? postfix = AccessTools.Method(
                typeof(VefRepulsorShieldCompat),
                nameof(AbsorbDamagePostfix));

            if (target == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch VEF CompShieldField.AbsorbDamage;"
                    + " repulsor hits will not ripple.");
                return;
            }

            // Kept for the transpiler, which identifies the two call sites by this
            // method. The postfix itself does not need it.
            absorbDamageTarget = target;

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched VEF CompShieldField.AbsorbDamage"
                    + " (repulsor hit bearings).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch repulsor AbsorbDamage: " + ex);
            }
        }

        /// <summary>
        /// The one shutdown signal that exists on this comp. VEF has no BreakShield
        /// method for the ordinary case: both an empty energy bar and an EMP end in
        /// <c>Energy = 0</c>, which runs this private notify. Deactivation for lack of
        /// a threat is deliberately NOT observed — VEF flips <c>active</c> whenever a
        /// raid starts or ends, and an edge detector here would play the break
        /// animation on every lull in combat.
        /// </summary>
        private static void PatchEnergyDepleted(Harmony harmony)
        {
            Type? type = compType;
            if (type == null)
            {
                return;
            }

            MethodInfo? target = AccessTools.Method(type, "Notify_EnergyDepleted");
            MethodInfo? postfix = AccessTools.Method(
                typeof(VefRepulsorShieldCompat),
                nameof(EnergyDepletedPostfix));

            if (target == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch VEF CompShieldField."
                    + "Notify_EnergyDepleted; repulsor breaks will not dissolve.");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched VEF CompShieldField.Notify_EnergyDepleted"
                    + " (repulsor break).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch repulsor energy depletion: "
                    + ex);
            }
        }

        /// <summary>
        /// Installs the transpiler that records the actual projectile at VEF's two
        /// absorb call sites. This patch is an accuracy improvement, not a
        /// prerequisite: when it cannot be installed the postfix keeps using the
        /// flight bearing, which is what the file did before.
        /// </summary>
        private static void PatchEnergyShieldTick(Harmony harmony)
        {
            Type? type = compType;
            if (type == null || absorbDamageTarget == null)
            {
                return;
            }

            MethodInfo? target = AccessTools.Method(
                type,
                "EnergyShieldTick",
                Type.EmptyTypes);
            MethodInfo? transpiler = AccessTools.Method(
                typeof(VefRepulsorShieldCompat),
                nameof(EnergyShieldTickTranspiler));
            recordImpactMethod = AccessTools.Method(
                typeof(VefRepulsorShieldCompat),
                nameof(RecordImpact));

            if (target == null || transpiler == null || recordImpactMethod == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch VEF CompShieldField."
                    + "EnergyShieldTick; repulsor hits keep the flight bearing.");
                return;
            }

            // The transpiler reads the method body to type-check the projectile local.
            energyShieldTickTarget = target;

            try
            {
                harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
                VisualTweaksLog.Info("Patched VEF CompShieldField.EnergyShieldTick"
                    + " (repulsor impact origin).");
            }
            catch (Exception ex)
            {
                energyShieldTickTarget = null;
                Log.Error("[Visual Tweaks] Failed to patch repulsor EnergyShieldTick;"
                    + " repulsor hit directions stay approximate: " + ex);
            }
        }

        /// <summary>
        /// Records the projectile that is about to be passed to the comp's absorb
        /// call. Injected by <see cref="EnergyShieldTickTranspiler"/>; the postfix
        /// consumes it on the same thread.
        /// </summary>
        internal static void RecordImpact(ThingComp? comp, Thing? projectile)
        {
            pendingImpactComp = comp;
            pendingImpactProjectile = projectile;
        }

        /// <summary>
        /// Hands the recorded projectile to the postfix and clears the slot. Clearing
        /// happens for every call, including the source-based absorb overload that has
        /// no projectile, so a stale entry can never leak into a later hit.
        /// </summary>
        private static Thing? TakePendingProjectile(ThingComp? comp)
        {
            Thing? projectile = ReferenceEquals(pendingImpactComp, comp)
                ? pendingImpactProjectile
                : null;
            pendingImpactComp = null;
            pendingImpactProjectile = null;
            return projectile;
        }

        /// <summary>
        /// The point where this projectile crossed the shield rim on its way in.
        ///
        /// VEF absorbs a projectile while it is already inside the scan ring (on a
        /// large shield, up to five cells inside), so its current position is not the
        /// point that should ripple. Projectiles fly in a straight line and
        /// <c>ExactRotation</c> is that line's constant direction, so stepping
        /// backwards from the current position along the line to the circle recovers
        /// the entry point — the same kind of point vanilla's interception reports.
        /// The projectile is always inside or just outside the circle at this moment;
        /// the positive root is the crossing behind it either way.
        /// </summary>
        private static Vector3 ProjectileRimPoint(
            Projectile projectile,
            Vector3 center,
            float radius)
        {
            Vector3 position = projectile.DrawPos;
            Vector3 travel = Vector3Utility.HorizontalVectorFromAngle(
                projectile.ExactRotation.eulerAngles.y);

            float dx = position.x - center.x;
            float dz = position.z - center.z;

            // Solve |position - t * travel - center| = radius for the crossing behind
            // the projectile: t = (u.d) + sqrt(R^2 - |u|^2 + (u.d)^2).
            float along = dx * travel.x + dz * travel.z;
            float discriminant = radius * radius - (dx * dx + dz * dz) + along * along;
            float back = along + Mathf.Sqrt(Mathf.Max(0f, discriminant));

            return new Vector3(
                position.x - travel.x * back,
                0f,
                position.z - travel.z * back);
        }

        /// <summary>
        /// Inserts <c>RecordImpact(this, projectile)</c> at the head of both absorb
        /// calls inside <c>EnergyShieldTick</c>. This is the only place the projectile
        /// object is still reachable: the public
        /// <c>AbsorbDamage(float, DamageDef, float)</c> receives only
        /// <c>ExactRotation.eulerAngles.y</c>, the flight bearing, while the real rim
        /// point is the projectile's own position.
        ///
        /// The stack shape is unchanged. In both call sites the argument list starts
        /// with <c>ldarg.0; ldloc &lt;projectile&gt;</c>, so the inserted block is
        /// placed between those two instructions: it duplicates the comp, loads the
        /// projectile and calls the recorder, leaving the original comp on the stack
        /// for the absorb call. Inserting after the <c>ldarg.0</c> rather than before
        /// it also keeps a branch that targets that instruction landing ahead of the
        /// duplicate.
        ///
        /// Nothing is emitted unless the local after <c>ldarg.0</c> really is a Thing
        /// (VEF's loop variable is a Projectile). A body of a different shape yields
        /// no insertions: the transpiler logs once, hands the method back untouched,
        /// and the postfix falls back to the flight bearing instead of risking bad IL
        /// in the framework's tick.
        /// </summary>
        private static IEnumerable<CodeInstruction> EnergyShieldTickTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> source = new List<CodeInstruction>(instructions);
            MethodInfo? record = recordImpactMethod;
            Type? type = compType;
            if (record == null || type == null)
            {
                return source;
            }

            // Each entry is (insert-after index, projectile load to clone).
            List<KeyValuePair<int, CodeInstruction>> insertions =
                new List<KeyValuePair<int, CodeInstruction>>();

            for (int i = 0; i < source.Count; i++)
            {
                if (source[i].opcode != OpCodes.Call
                    && source[i].opcode != OpCodes.Callvirt)
                {
                    continue;
                }

                if (source[i].operand is not MethodInfo called
                    || called.DeclaringType != type
                    || called.Name != "AbsorbDamage")
                {
                    continue;
                }

                int thisLoad = FindAbsorbArgumentStart(source, i);
                if (thisLoad < 0
                    || HasControlFlow(source, thisLoad + 1, i)
                    || ContainsInsertion(insertions, thisLoad + 1))
                {
                    continue;
                }

                CodeInstruction projectileLoad = source[thisLoad + 1];
                insertions.Add(new KeyValuePair<int, CodeInstruction>(
                    thisLoad + 1,
                    projectileLoad));
            }

            if (insertions.Count == 0)
            {
                Log.Warning("[Visual Tweaks] VEF CompShieldField.EnergyShieldTick has"
                    + " an unexpected shape; repulsor hits keep the flight bearing.");
                return source;
            }

            List<CodeInstruction> result =
                new List<CodeInstruction>(source.Count + insertions.Count * 3);
            int next = 0;
            for (int i = 0; i <= source.Count; i++)
            {
                while (next < insertions.Count && insertions[next].Key == i)
                {
                    CodeInstruction projectileLoad = insertions[next].Value;
                    result.Add(new CodeInstruction(OpCodes.Ldarg_0));
                    result.Add(new CodeInstruction(
                        projectileLoad.opcode,
                        projectileLoad.operand));
                    result.Add(new CodeInstruction(OpCodes.Call, record));
                    next++;
                }

                if (i < source.Count)
                {
                    result.Add(source[i]);
                }
            }

            return result;
        }

        /// <summary>
        /// The nearest preceding <c>ldarg.0</c> that starts the absorb call's argument
        /// list: it must be followed immediately by a load of a Thing local. Scanning
        /// for the pair rather than the load alone skips a <c>ldarg.0</c> that loads
        /// the comp merely to read a field in a later argument.
        /// </summary>
        private static int FindAbsorbArgumentStart(
            List<CodeInstruction> instructions,
            int callIndex)
        {
            for (int i = callIndex - 1; i >= 0; i--)
            {
                if (!IsThisLoad(instructions[i]) || i + 1 >= callIndex)
                {
                    continue;
                }

                CodeInstruction projectileLoad = instructions[i + 1];
                if (IsLocalLoad(projectileLoad.opcode)
                    && IsThingLocal(projectileLoad, energyShieldTickTarget))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// True when a control transfer sits between the candidate argument start and
        /// the call. That means the call is reached from a different path (the
        /// candidate belongs to an earlier statement), so it is not this call's
        /// argument list and must not be used. The real VEF body has no such transfer
        /// before either absorb call.
        /// </summary>
        private static bool HasControlFlow(
            List<CodeInstruction> instructions,
            int from,
            int to)
        {
            for (int i = from; i < to; i++)
            {
                FlowControl flow = instructions[i].opcode.FlowControl;
                if (flow == FlowControl.Branch
                    || flow == FlowControl.Cond_Branch
                    || flow == FlowControl.Return
                    || flow == FlowControl.Throw)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsInsertion(
            List<KeyValuePair<int, CodeInstruction>> insertions,
            int index)
        {
            for (int i = 0; i < insertions.Count; i++)
            {
                if (insertions[i].Key == index)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True for <c>ldarg.0</c> in either the short form the C# compiler emits or a
        /// long form another rewriter may have substituted.
        /// </summary>
        private static bool IsThisLoad(CodeInstruction instruction)
        {
            if (instruction.opcode == OpCodes.Ldarg_0)
            {
                return true;
            }

            if (instruction.opcode != OpCodes.Ldarg_S
                && instruction.opcode != OpCodes.Ldarg)
            {
                return false;
            }

            if (instruction.operand is ParameterInfo parameter)
            {
                return parameter.Position == 0;
            }

            if (instruction.operand is int index)
            {
                return index == 0;
            }

            return instruction.operand is byte small && small == 0;
        }

        private static bool IsLocalLoad(OpCode code)
        {
            return code == OpCodes.Ldloc_0
                || code == OpCodes.Ldloc_1
                || code == OpCodes.Ldloc_2
                || code == OpCodes.Ldloc_3
                || code == OpCodes.Ldloc_S
                || code == OpCodes.Ldloc;
        }

        /// <summary>
        /// True when the local this instruction loads is a Thing (in practice VEF's
        /// Projectile loop variable). The short ldloc forms carry the index in the
        /// opcode itself, so the declared type has to come from the method body.
        /// </summary>
        private static bool IsThingLocal(CodeInstruction load, MethodInfo? method)
        {
            if (load.operand is LocalBuilder builder)
            {
                return typeof(Thing).IsAssignableFrom(builder.LocalType);
            }

            if (method == null)
            {
                return false;
            }

            OpCode code = load.opcode;
            int index;
            if (code == OpCodes.Ldloc_0)
            {
                index = 0;
            }
            else if (code == OpCodes.Ldloc_1)
            {
                index = 1;
            }
            else if (code == OpCodes.Ldloc_2)
            {
                index = 2;
            }
            else if (code == OpCodes.Ldloc_3)
            {
                index = 3;
            }
            else if (load.operand is int number)
            {
                index = number;
            }
            else if (load.operand is byte small)
            {
                index = small;
            }
            else
            {
                return false;
            }

            try
            {
                IList<LocalVariableInfo>? locals =
                    method.GetMethodBody()?.LocalVariables;
                return locals != null
                    && index >= 0
                    && index < locals.Count
                    && typeof(Thing).IsAssignableFrom(locals[index].LocalType);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Priority.First for the same reason as the vanilla dome: Harmony stops at the
        /// first prefix returning false, so this has to run before another replacement
        /// could skip the original and leave neither drawing.
        /// </summary>
        [HarmonyPriority(Priority.First)]
        private static bool PostDrawPrefix(ThingComp __instance)
        {
            // Returning true means "let VEF draw". Every early exit is a case where
            // the stock billboard is the right answer.
            if (fatal
                || __instance == null
                || !ShieldDomeBridge.RepulsorActive
                || __instance.parent == null
                || __instance.parent is Apparel)
            {
                return true;
            }

            try
            {
                if (!TryRead(__instance, out float radius, out float alpha,
                        out Color color, out bool present))
                {
                    return true;
                }

                return !ShieldDomeRenderer.Draw(
                    __instance,
                    __instance.parent,
                    radius,
                    alpha,
                    color,
                    present);
            }
            catch (Exception ex)
            {
                // A throwing prefix would be retried every frame by every repulsor.
                fatal = true;
                Log.Error("[Visual Tweaks] Repulsor shield draw failed, stock drawing"
                    + " restored: " + ex);
                return true;
            }
        }

        /// <summary>
        /// Turns the comp's reflected state into the same numbers a vanilla interceptor
        /// would supply: a world radius, an opacity and an RGB tint. The colour curve
        /// is VEF's own (lowest health point up to the shield colour at full energy),
        /// copied rather than approximated because the defs in the wild rely on it —
        /// the VFES defs leave <c>shieldColour</c> white and express the whole ramp
        /// through the health points.
        /// </summary>
        private static bool TryRead(
            ThingComp comp,
            out float radius,
            out float alpha,
            out Color color,
            out bool present)
        {
            radius = 0f;
            alpha = 0f;
            color = Color.white;
            present = false;

            // Null-forgiving: BindMembers either found every member or disabled the
            // file, so none of these can be null once a patch is installed.
            radius = shieldRadiusOf!(comp);
            if (radius <= 0.01f)
            {
                return false;
            }

            bool isIndestructible = indestructibleOf!(comp);
            float energyNow = energyOf!(comp);
            bool isActive = activeOf!(comp);

            // VEF's Draw condition, verbatim: a switched-on shield with charge left,
            // or one that does not use charge at all.
            present = isActive && (energyNow > 0f || isIndestructible);

            object? propsObject = comp.props;
            if (propsObject == null)
            {
                return false;
            }

            Color shieldColourValue = shieldColourOf!(propsObject);

            Color healthColour = HealthColour(
                shieldColourValue,
                ColourCurves.GetValue(propsObject, CreateCurve),
                isIndestructible,
                energyNow,
                maxEnergyOf!(comp));

            color = healthColour;

            // VEF carries opacity in the colour alpha, and the defs set that alpha
            // below 1 even at full energy. The slider is a multiplier on top, not a
            // replacement: 1.0 is exactly the stock alpha, and the default sits below
            // it because that alpha was authored for an additive glow.
            alpha = present
                ? healthColour.a * VisualTweaksSettings.repulsorShieldOpacity
                : 0f;

            return true;
        }

        /// <summary>
        /// Copy of the framework's health-colour interpolation. Kept as a loop rather
        /// than LINQ because it runs per repulsor per frame and because the exact
        /// first/last matching point semantics matter: a point at or above the current
        /// health is the upper end, the last point below it is the lower end, and a
        /// missing upper end means "interpolate to the shield colour".
        ///
        /// The points themselves are parsed once by <see cref="ColourCurve"/>; the
        /// point type is <c>VEF.Apparels.HealthColorPoint</c>, so its two fields are
        /// read by reflection, which is exactly the per-frame cost the cache removes.
        /// </summary>
        private static Color HealthColour(
            Color shieldColourValue,
            ColourCurve curve,
            bool isIndestructible,
            float energyNow,
            float maxEnergyNow)
        {
            ColourCurve.Point[] points = curve.Points;
            if (isIndestructible || points.Length == 0 || maxEnergyNow <= 0f)
            {
                return shieldColourValue;
            }

            float healthPercentValue = Mathf.Clamp01(energyNow / maxEnergyNow);

            int upper = -1;
            int lower = -1;
            for (int i = 0; i < points.Length; i++)
            {
                float percent = points[i].Percent;
                if (percent >= healthPercentValue && upper < 0)
                {
                    upper = i;
                }

                if (percent < healthPercentValue)
                {
                    lower = i;
                }
            }

            if (upper < 0)
            {
                if (lower < 0)
                {
                    return shieldColourValue;
                }

                return Color.Lerp(
                    points[lower].Colour,
                    shieldColourValue,
                    Mathf.InverseLerp(
                        points[lower].Percent,
                        1f,
                        healthPercentValue));
            }

            if (lower < 0)
            {
                return points[upper].Colour;
            }

            return Color.Lerp(
                points[lower].Colour,
                points[upper].Colour,
                Mathf.InverseLerp(
                    points[lower].Percent,
                    points[upper].Percent,
                    healthPercentValue));
        }

        private static ColourCurve CreateColourCurve(object propsObject)
        {
            try
            {
                return new ColourCurve(propsObject);
            }
            catch (Exception ex)
            {
                // A malformed def must not cost the dome: fall back to its solid
                // shield colour. Returning here also caches the empty curve for this
                // props instance, so the failure is not retried every frame.
                Log.ErrorOnce(
                    "[Visual Tweaks] Repulsor health colour curve failed; the dome"
                        + " uses its solid shield colour: " + ex,
                    typeof(VefRepulsorShieldCompat).GetHashCode() ^ 0x51);
                return ColourCurve.Empty;
            }
        }

        /// <summary>
        /// The def's health colour points, parsed once per properties instance.
        ///
        /// The curve is static per def, and reading every point through reflection on
        /// every frame was the one per-frame cost with no reason to exist: the values
        /// cannot change while the game is running. The cache is keyed by the props
        /// object, so two defs with different ramps never share an entry, and a
        /// ConditionalWeakTable keeps it from outliving the defs.
        /// </summary>
        private sealed class ColourCurve
        {
            internal readonly struct Point
            {
                internal readonly float Percent;
                internal readonly Color Colour;

                internal Point(float percent, Color colour)
                {
                    Percent = percent;
                    Colour = colour;
                }
            }

            internal static readonly ColourCurve Empty = new ColourCurve();

            internal readonly Point[] Points;

            private ColourCurve()
            {
                Points = Array.Empty<Point>();
            }

            internal ColourCurve(object propsObject)
            {
                if (healthColorPoints!.GetValue(propsObject) is not IEnumerable source)
                {
                    Points = Array.Empty<Point>();
                    return;
                }

                List<Point> parsed = new List<Point>();
                foreach (object? item in source)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    float percent = healthPercent!.GetValue(item) is float value
                        ? value
                        : 0f;
                    Color colour = healthColor!.GetValue(item) is Color c
                        ? c
                        : Color.white;
                    parsed.Add(new Point(percent, colour));
                }

                Points = parsed.Count == 0
                    ? Array.Empty<Point>()
                    : parsed.ToArray();
            }
        }

        /// <summary>
        /// Gate for the dome-signal patch. A repulsor this mod is not drawing keeps
        /// VEF's own reactions untouched.
        /// </summary>
        private static bool ShouldRecord(ThingComp comp)
        {
            return !fatal
                && comp != null
                && comp.parent != null
                && !(comp.parent is Apparel)
                && ShieldDomeBridge.RepulsorActive;
        }

        private static void AbsorbDamagePostfix(
            ThingComp __instance,
            float amount,
            float angle)
        {
            // Consume the pending projectile before any early exit, so the single slot
            // cannot survive into an unrelated hit.
            Thing? projectile = TakePendingProjectile(__instance);

            // The two visuals are independent switches, like on vanilla shields: a
            // player can keep the hit ripple while the repulsor keeps its stock
            // surface, or replace the surface without the ripple.
            if (fatal
                || signalsBroken
                || __instance == null
                || __instance.parent == null
                || __instance.parent is Apparel)
            {
                return;
            }

            // This runs inside VEF's projectile tick. One bad read must not take the
            // interception down with it; unlike the draw path, the failure only
            // disables this file's hit bookkeeping, never the drawing.
            try
            {
                Thing host = __instance.parent;
                Vector3 center = host.DrawPos;
                Map? map = host.Map;
                float radius = shieldRadiusOf!(__instance);

                // The transpiler normally hands us the real projectile. VEF absorbs it
                // while it is already inside the scan ring (up to five cells inside on
                // a large shield), so its DrawPos is not the rim point; the entry point
                // is reconstructed from its straight trajectory below. When that patch
                // is unavailable (or the hit came through the source-based overload),
                // fall back to VEF's angle, converted once from Unity's Y-euler
                // convention to the interceptor's AngleToFlat convention.
                Projectile? bullet = projectile as Projectile;
                bool fromProjectile = bullet != null
                    && map != null
                    && bullet.Spawned
                    && bullet.Map == map
                    && radius > 0.01f;

                // Both conventions point from the projectile towards the centre; the
                // shared consumers negate them to get the outward direction. The rim
                // point is the same quantity vanilla's lastInterceptAngle is built
                // from, so the two families now agree.
                Vector3 hitPos;
                float domeAngle;
                if (fromProjectile)
                {
                    hitPos = ProjectileRimPoint(bullet!, center, radius);
                    domeAngle = hitPos.AngleToFlat(center);
                }
                else
                {
                    domeAngle = angle - 90f;

                    // Without a projectile, the best available point is the rim in the
                    // direction the round came from. Exact only for shots aimed at the
                    // centre, which is why it is the fallback and not the rule.
                    Vector3 toward = Vector3Utility.HorizontalVectorFromAngle(angle);
                    hitPos = new Vector3(
                        center.x - toward.x * radius,
                        0f,
                        center.z - toward.z * radius);
                }

                if (ShieldDomeBridge.RepulsorActive)
                {
                    ShieldImpactLog.Notify(__instance, domeAngle, amount);

                    // A fatal shot depletes the energy before this postfix runs, so
                    // the dissolve already exists and only its origin is missing. This
                    // hands it the fatal bearing while the start tick still matches; a
                    // no-op otherwise.
                    ShieldDissolveLog.RefreshOrigin(
                        __instance,
                        domeAngle,
                        Find.TickManager?.TicksGame ?? -1);
                }

                if (map == null || !ShieldRippleLayer.Active || radius <= 0.01f)
                {
                    return;
                }

                // The ripple gets the same kind of point vanilla passes (the projectile
                // crossing the rim); the layer projects it onto the silhouette.
                map.GetComponent<OverlayMapComponent>()?.NotifyShieldHit(
                    host,
                    radius,
                    hitPos,
                    amount);
            }
            catch (Exception ex)
            {
                // One ErrorOnce rather than one per hit; the dome keeps drawing.
                signalsBroken = true;
                Log.ErrorOnce(
                    "[Visual Tweaks] Repulsor hit bookkeeping failed; hit and break"
                        + " effects disabled for this session: " + ex,
                    typeof(VefRepulsorShieldCompat).GetHashCode());
            }
        }

        private static void EnergyDepletedPostfix(ThingComp __instance)
        {
            if (signalsBroken
                || !ShouldRecord(__instance)
                || ShieldStyle.For(ShieldHexTarget.Large, __instance.parent).Break
                    == ShieldBreakStyle.Vanilla)
            {
                return;
            }

            try
            {
                ShieldDissolveLog.Begin(__instance);
            }
            catch (Exception ex)
            {
                signalsBroken = true;
                Log.ErrorOnce(
                    "[Visual Tweaks] Repulsor break bookkeeping failed; break"
                        + " animations disabled for this session: " + ex,
                    typeof(VefRepulsorShieldCompat).GetHashCode() ^ 0x5A17);
            }
        }
    }
}
