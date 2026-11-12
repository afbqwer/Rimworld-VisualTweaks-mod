using System;
using System.Collections;
using System.Reflection;
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
        private static PropertyInfo? props;
        private static FieldInfo? activeField;
        private static FieldInfo? shieldColour;
        private static FieldInfo? healthColorPoints;
        private static FieldInfo? healthPercent;
        private static FieldInfo? healthColor;

        private static bool fatal;
        private static bool patched;

        /// <summary>
        /// True when VEF is loaded, the shield-field members were bound and the draw
        /// patch is installed. The settings window and the hex prewarm both read this,
        /// so a player without the framework — or with a VEF version whose method
        /// names moved — never sees a switch that cannot do anything.
        /// </summary>
        internal static bool Available => patched;

        internal static void Apply(Harmony harmony)
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
        }

        /// <summary>
        /// Binds the state the draw needs. If any of it is missing, the whole file
        /// backs off: a dome with a guessed radius or colour is worse than the stock
        /// billboard, and the version this was written against is the one whose member
        /// names are listed here.
        ///
        /// Read per frame through <see cref="PropertyInfo"/> rather than open
        /// delegates. Properties are stat-cached on the VEF side, repulsors are counted
        /// in ones per map, and a few boxed floats per frame is a smaller cost than a
        /// delegate shape that has to match a class this assembly cannot reference.
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
            props = AccessTools.Property(type, "Props");
            activeField = AccessTools.Field(type, "active");
            shieldColour = AccessTools.Field(propsType, "shieldColour");
            healthColorPoints = AccessTools.Field(propsType, "healthColorPoints");
            healthPercent = AccessTools.Field(colourPointType, "healthPercent");
            healthColor = AccessTools.Field(colourPointType, "color");

            bool complete = shieldRadius != null
                && energy != null
                && maxEnergy != null
                && indestructible != null
                && props != null
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
        /// records every intercepted projectile exactly once. The angle is VEF's own:
        /// a bearing from the projectile towards the shield, the same convention
        /// vanilla's <c>lastInterceptAngle</c> uses, so the renderer's existing
        /// "point it back at the shooter" rule applies unchanged.
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
            radius = ReadFloat(shieldRadius!, comp);
            if (radius <= 0.01f)
            {
                return false;
            }

            bool isIndestructible = ReadBool(indestructible!, comp);
            float energyNow = ReadFloat(energy!, comp);
            bool isActive = activeField!.GetValue(comp) is bool active && active;

            // VEF's Draw condition, verbatim: a switched-on shield with charge left,
            // or one that does not use charge at all.
            present = isActive && (energyNow > 0f || isIndestructible);

            object? propsObject = props!.GetValue(comp);
            if (propsObject == null)
            {
                return false;
            }

            Color shieldColourValue = shieldColour!.GetValue(propsObject) is Color c
                ? c
                : Color.white;

            Color healthColour = HealthColour(
                shieldColourValue,
                healthColorPoints!.GetValue(propsObject),
                isIndestructible,
                energyNow,
                ReadFloat(maxEnergy!, comp));

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
        /// The point type is <c>VEF.Apparels.HealthColorPoint</c>, so the list arrives
        /// as a plain <see cref="IEnumerable"/> and its two fields are read by
        /// reflection. Copying the type into this assembly is not an option: the whole
        /// file exists to keep VEF out of the compile references.
        /// </summary>
        private static Color HealthColour(
            Color shieldColourValue,
            object? pointsObject,
            bool isIndestructible,
            float energyNow,
            float maxEnergyNow)
        {
            if (isIndestructible
                || pointsObject is not IEnumerable points
                || maxEnergyNow <= 0f)
            {
                return shieldColourValue;
            }

            float healthPercentValue = Mathf.Clamp01(energyNow / maxEnergyNow);

            object? upper = null;
            object? lower = null;
            float upperPercent = 0f;
            float lowerPercent = 0f;

            foreach (object? point in points)
            {
                if (point == null)
                {
                    continue;
                }

                float pointPercent = healthPercent!.GetValue(point) is float value
                    ? value
                    : 0f;

                if (pointPercent >= healthPercentValue && upper == null)
                {
                    upper = point;
                    upperPercent = pointPercent;
                }

                if (pointPercent < healthPercentValue)
                {
                    lower = point;
                    lowerPercent = pointPercent;
                }
            }

            if (upper == null)
            {
                if (lower == null)
                {
                    return shieldColourValue;
                }

                return Color.Lerp(
                    PointColour(lower, shieldColourValue),
                    shieldColourValue,
                    Mathf.InverseLerp(lowerPercent, 1f, healthPercentValue));
            }

            if (lower == null)
            {
                return PointColour(upper, shieldColourValue);
            }

            return Color.Lerp(
                PointColour(lower, shieldColourValue),
                PointColour(upper, shieldColourValue),
                Mathf.InverseLerp(lowerPercent, upperPercent, healthPercentValue));
        }

        private static Color PointColour(object point, Color fallback)
        {
            return healthColor!.GetValue(point) is Color value ? value : fallback;
        }

        private static float ReadFloat(PropertyInfo property, object instance)
        {
            object? value = property.GetValue(instance, null);
            return value is float number ? number : 0f;
        }

        private static bool ReadBool(PropertyInfo property, object instance)
        {
            object? value = property.GetValue(instance, null);
            return value is bool flag && flag;
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
            // The two visuals are independent switches, like on vanilla shields: a
            // player can keep the hit ripple while the repulsor keeps its stock
            // surface, or replace the surface without the ripple.
            if (fatal
                || __instance == null
                || __instance.parent == null
                || __instance.parent is Apparel)
            {
                return;
            }

            if (ShieldDomeBridge.RepulsorActive)
            {
                ShieldImpactLog.Notify(__instance, angle, amount);

                // A fatal shot depletes the energy before this postfix runs, so the
                // dissolve already exists and only its origin is missing. This hands
                // it the fatal bearing while the start tick still matches; a no-op
                // otherwise.
                ShieldDissolveLog.RefreshOrigin(
                    __instance,
                    angle,
                    Find.TickManager?.TicksGame ?? -1);
            }

            // The repulsor's own hit effect says nothing about where the shot came
            // from on the surface, so the same bearing is turned into a rim point for
            // the refraction ripple. VEF's angle points from the projectile towards
            // the shield, so the impact sits on the opposite side of the centre.
            Map? map = __instance.parent.Map;
            if (map == null || !ShieldRippleLayer.Active)
            {
                return;
            }

            float radius = ReadFloat(shieldRadius!, __instance);
            if (radius <= 0.01f)
            {
                return;
            }

            Vector3 center = __instance.parent.DrawPos;
            Vector3 toward = Vector3Utility.FromAngleFlat(angle);

            map.GetComponent<OverlayMapComponent>()?.NotifyShieldHit(
                __instance.parent,
                radius,
                new Vector3(
                    center.x - toward.x * radius,
                    0f,
                    center.z - toward.z * radius),
                amount);
        }

        private static void EnergyDepletedPostfix(ThingComp __instance)
        {
            if (!ShouldRecord(__instance)
                || VisualTweaksSettings.CurrentShieldBreakStyle
                    == ShieldBreakStyle.Vanilla)
            {
                return;
            }

            ShieldDissolveLog.Begin(__instance);
        }
    }
}
