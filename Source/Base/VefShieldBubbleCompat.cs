using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Brings VEF's pawn shield bubbles into the personal-shield replacement.
    ///
    /// <c>VEF.Apparels.CompShieldBubble</c> is a third shield implementation,
    /// separate from both vanilla's <see cref="CompShield"/> and VEF's
    /// <c>CompShieldField</c>. It is used by apparel (VFE Pirates warcaskets, for
    /// example) and by creatures whose shield is built in, and it draws a single
    /// pawn-centred billboard (<c>Other/ShieldBubble</c>) from its own
    /// <c>PostDraw</c>/<c>CompDrawWornExtras</c>. None of the existing patches can
    /// see that comp, so without this file those shields keep their stock bubble.
    ///
    /// Everything is bound by reflection: VEF is not a compile reference of this
    /// assembly and a missing VEF must cost nothing. When the type is absent the
    /// class does nothing.
    ///
    /// What the framework's draw does, and what therefore has to be carried over:
    ///
    ///   * it only shows an Active bubble with charge left, and then only when one
    ///     of the def's <c>show*</c> situations applies (drafted, hostile, neutral
    ///     in combat, or always);
    ///   * the drawn diameter is <c>Lerp(minShieldSize, maxShieldSize, Energy)</c>,
    ///     using the raw absolute energy exactly as VEF does;
    ///   * the tint comes from the def's <c>shieldColor</c>, and the texture may be
    ///     overridden by <c>shieldTexPath</c>.
    ///
    /// The texture override is the one case deliberately left alone: a mod that
    /// replaces the stock bubble art made an art choice, and painting the shader
    /// dome over it would erase that. Only the stock texture (or none) is replaced.
    ///
    /// The comp's own damage effects (sound, flash, dust, RESET bolt) are untouched.
    /// </summary>
    internal static class VefShieldBubbleCompat
    {
        private const string CompTypeName = "VEF.Apparels.CompShieldBubble";
        private const string PropsTypeName = "VEF.Apparels.CompProperties_ShieldBubble";

        /// <summary>VEF's own fallback when <c>shieldTexPath</c> is empty.</summary>
        private const string StockBubbleTexPath = "Other/ShieldBubble";

        private static Type? compType;
        private static PropertyInfo? shieldState;
        private static PropertyInfo? energy;
        private static PropertyInfo? pawnProperty;
        private static PropertyInfo? props;
        private static MethodInfo? inCombat;
        private static FieldInfo? shieldColor;
        private static FieldInfo? minShieldSize;
        private static FieldInfo? maxShieldSize;
        private static FieldInfo? shieldTexPath;
        private static FieldInfo? showAlways;
        private static FieldInfo? showWhenDrafted;
        private static FieldInfo? showOnHostiles;
        private static FieldInfo? showOnNeutralInCombat;

        private static bool fatal;
        private static bool patched;

        /// <summary>
        /// True when VEF is loaded, the shield-bubble members were bound and at least
        /// one draw entry point is patched. The settings window and the hex prewarm
        /// both read this.
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

            PatchDrawEntry(harmony, "PostDraw", nameof(PostDrawPrefix), "built-in shield bubble");
            PatchDrawEntry(
                harmony,
                "CompDrawWornExtras",
                nameof(CompDrawWornExtrasPrefix),
                "worn shield bubble");
            PatchAbsorbDamage(harmony);
        }

        /// <summary>
        /// Binds the state the draw needs. If any member is missing the whole file
        /// backs off, because a dome with a guessed size, visibility or colour is
        /// worse than the stock billboard.
        ///
        /// Read per frame through reflection: VEF's own draw also runs every frame,
        /// the bubbles are counted in handfuls, and the alternative would be a
        /// delegate shape matching a class this assembly cannot reference.
        /// </summary>
        private static bool BindMembers()
        {
            Type? type = compType;
            Type? propsType = AccessTools.TypeByName(PropsTypeName);
            if (type == null || propsType == null)
            {
                Log.Warning("[Visual Tweaks] VEF shield bubble types not found;"
                    + " bubble shields keep the stock drawing.");
                return false;
            }

            shieldState = AccessTools.Property(type, "ShieldState");
            energy = AccessTools.Property(type, "Energy");
            pawnProperty = AccessTools.Property(type, "Pawn");
            props = AccessTools.Property(type, "Props");
            inCombat = AccessTools.Method(type, "InCombat", new[] { typeof(Pawn) });
            shieldColor = AccessTools.Field(propsType, "shieldColor");
            minShieldSize = AccessTools.Field(propsType, "minShieldSize");
            maxShieldSize = AccessTools.Field(propsType, "maxShieldSize");
            shieldTexPath = AccessTools.Field(propsType, "shieldTexPath");
            showAlways = AccessTools.Field(propsType, "showAlways");
            showWhenDrafted = AccessTools.Field(propsType, "showWhenDrafted");
            showOnHostiles = AccessTools.Field(propsType, "showOnHostiles");
            showOnNeutralInCombat =
                AccessTools.Field(propsType, "showOnNeutralInCombat");

            bool complete = shieldState != null
                && energy != null
                && pawnProperty != null
                && props != null
                && inCombat != null
                && shieldColor != null
                && minShieldSize != null
                && maxShieldSize != null
                && shieldTexPath != null
                && showAlways != null
                && showWhenDrafted != null
                && showOnHostiles != null
                && showOnNeutralInCombat != null;

            if (!complete)
            {
                Log.Warning("[Visual Tweaks] VEF CompShieldBubble is missing members"
                    + " this mod draws with; bubble shields keep the stock drawing.");
                return false;
            }

            if (shieldState!.PropertyType != typeof(ShieldState)
                || energy!.PropertyType != typeof(float)
                || pawnProperty!.PropertyType != typeof(Pawn)
                || shieldColor!.FieldType != typeof(Color)
                || minShieldSize!.FieldType != typeof(float)
                || maxShieldSize!.FieldType != typeof(float)
                || shieldTexPath!.FieldType != typeof(string)
                || showAlways!.FieldType != typeof(bool)
                || showWhenDrafted!.FieldType != typeof(bool)
                || showOnHostiles!.FieldType != typeof(bool)
                || showOnNeutralInCombat!.FieldType != typeof(bool))
            {
                Log.Warning("[Visual Tweaks] VEF CompShieldBubble members have"
                    + " unexpected types; bubble shields keep the stock drawing.");
                return false;
            }

            return true;
        }

        private static void PatchDrawEntry(
            Harmony harmony,
            string targetName,
            string prefixName,
            string label)
        {
            Type? type = compType;
            if (type == null)
            {
                return;
            }

            MethodInfo? target = AccessTools.Method(type, targetName);
            MethodInfo? prefix = AccessTools.Method(
                typeof(VefShieldBubbleCompat),
                prefixName);

            if (target == null || prefix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch VEF CompShieldBubble."
                    + targetName + "; " + label + " keeps the stock drawing.");
                return;
            }

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                patched = true;
                VisualTweaksLog.Info("Patched VEF CompShieldBubble." + targetName
                    + " (" + label + ").");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch CompShieldBubble."
                    + targetName + ": " + ex);
            }
        }

        /// <summary>
        /// <c>AbsorbingDamage</c> is the public method every absorb path ends in. Its
        /// postfix uses the return value to tell an absorbed hit from a deflected one,
        /// and the state to tell a break from a surviving hit. EMP is the case that
        /// needs both: it returns false (the damage is not absorbed) yet empties the
        /// bubble and moves it to Resetting.
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
                "AbsorbingDamage",
                new[] { typeof(DamageInfo), typeof(bool).MakeByRefType() });
            MethodInfo? prefix = AccessTools.Method(
                typeof(VefShieldBubbleCompat),
                nameof(AbsorbingDamagePrefix));
            MethodInfo? postfix = AccessTools.Method(
                typeof(VefShieldBubbleCompat),
                nameof(AbsorbingDamagePostfix));

            if (target == null || prefix == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch VEF CompShieldBubble."
                    + "AbsorbingDamage; bubble hits and breaks will not animate.");
                return;
            }

            try
            {
                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched VEF CompShieldBubble.AbsorbingDamage"
                    + " (bubble hit and break signals).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch bubble AbsorbingDamage: "
                    + ex);
            }
        }

        private struct DamageSnapshot
        {
            public bool WasActive;
            public float EnergyBefore;
            public float Angle;
        }

        /// <summary>
        /// Captured before the original runs: the state, the charge and the bearing.
        /// After it, a state that fell out of Active is a break, and a smaller charge
        /// with the state unchanged is an absorb.
        /// </summary>
        private static void AbsorbingDamagePrefix(
            ThingComp __instance,
            DamageInfo dinfo,
            out DamageSnapshot __state)
        {
            __state = default;
            if (__instance == null || fatal || !ShieldDomeBridge.VefBubbleActive)
            {
                return;
            }

            if (ReadState(__instance) != ShieldState.Active)
            {
                return;
            }

            __state.WasActive = true;
            __state.EnergyBefore = ReadFloat(energy!, __instance);
            __state.Angle = dinfo.Angle;
        }

        private static void AbsorbingDamagePostfix(
            ThingComp __instance,
            DamageSnapshot __state,
            bool __result)
        {
            if (!__state.WasActive
                || __instance == null
                || fatal
                || !ShieldDomeBridge.VefBubbleActive)
            {
                return;
            }

            try
            {
                if (ReadState(__instance) != ShieldState.Active)
                {
                    // The bubble burst. Start the tear from the side that was hit,
                    // unless the player chose the stock one-frame disappearance.
                    if (VisualTweaksSettings.CurrentShieldBreakStyleFor(
                            ShieldHexTarget.Personal)
                        != ShieldBreakStyle.Vanilla)
                    {
                        PersonalShieldDissolveLog.Begin(
                            __instance,
                            __instance.parent,
                            __state.Angle);
                    }

                    return;
                }

                // Still standing: record the absorbed hit. The return value also
                // covers deflection (a melee hit on a ranged-only bubble returns
                // false), while the charge comparison keeps a deflected hit whose
                // energy happened to change from being drawn as a reaction.
                bool absorbed = __result
                    || ReadFloat(energy!, __instance)
                        < __state.EnergyBefore - 0.0001f;
                if (absorbed)
                {
                    PersonalShieldHitLog.Notify(__instance, __state.Angle);
                }
            }
            catch (Exception ex)
            {
                fatal = true;
                Log.Error("[Visual Tweaks] Bubble hit bookkeeping failed, stock"
                    + " drawing restored: " + ex);
            }
        }

        /// <summary>
        /// Priority.First for the same reason as the other replacements: Harmony stops
        /// at the first prefix returning false, so this must run before another
        /// replacement could skip the original and leave neither drawing.
        /// </summary>
        [HarmonyPriority(Priority.First)]
        private static bool PostDrawPrefix(ThingComp __instance)
        {
            return DrawFor(__instance, wornExtras: false);
        }

        [HarmonyPriority(Priority.First)]
        private static bool CompDrawWornExtrasPrefix(ThingComp __instance)
        {
            return DrawFor(__instance, wornExtras: true);
        }

        /// <summary>
        /// Mirrors VEF's own split: a built-in shield draws from <c>PostDraw</c>, an
        /// apparel shield from <c>CompDrawWornExtras</c>. Checking it here too means
        /// a comp that somehow reaches both paths cannot paint two domes in one frame.
        /// </summary>
        private static bool DrawFor(ThingComp comp, bool wornExtras)
        {
            // Returning true means "run VEF", which is the correct answer whenever
            // this replacement is unavailable or has nothing to draw.
            if (fatal
                || comp == null
                || !ShieldDomeBridge.VefBubbleActive
                || comp.parent == null)
            {
                return true;
            }

            bool apparel = comp.parent is Apparel;
            if (apparel != wornExtras)
            {
                return true;
            }

            try
            {
                Pawn? pawn = pawnProperty!.GetValue(comp) as Pawn;
                if (pawn == null
                    || !pawn.Spawned
                    || pawn.Dead
                    || pawn.Drawer == null)
                {
                    return true;
                }

                object? propsObject = props!.GetValue(comp);
                if (propsObject == null || !IsStockBubble(propsObject))
                {
                    return true;
                }

                // VEF's own gate: Active and charge left make the bubble "up"; one of
                // the def's show situations decides whether it is painted. Keeping
                // the two separate lets a dissolve end the moment the shield
                // recharges, even while the stock rule still hides it.
                float energyNow = ReadFloat(energy!, comp);
                bool active = ReadState(comp) == ShieldState.Active
                    && energyNow > 0f;
                bool visible = active
                    && ShouldDisplay(comp, pawn, propsObject);

                float size = Mathf.Lerp(
                    ReadFloat(minShieldSize!, propsObject),
                    ReadFloat(maxShieldSize!, propsObject),
                    energyNow);

                Color color = shieldColor!.GetValue(propsObject) is Color c
                    ? c
                    : Color.white;

                if (PersonalShieldRenderer.Draw(
                        comp,
                        pawn,
                        size,
                        active,
                        visible,
                        color))
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                // A throwing prefix would be retried every frame by every bubble.
                fatal = true;
                Log.Error("[Visual Tweaks] Bubble shield draw failed, stock drawing"
                    + " restored: " + ex);
                return true;
            }
        }

        /// <summary>
        /// VEF's visibility rule, reproduced because it lives in the draw method
        /// itself. <paramref name="propsObject"/> is the comp's properties instance.
        /// </summary>
        private static bool ShouldDisplay(
            ThingComp comp,
            Pawn pawn,
            object propsObject)
        {
            if (ReadBool(showAlways!, propsObject))
            {
                return true;
            }

            if (ReadBool(showWhenDrafted!, propsObject) && pawn.Drafted)
            {
                return true;
            }

            if (pawn.Faction == Faction.OfPlayer)
            {
                return false;
            }

            if (ReadBool(showOnHostiles!, propsObject)
                && GenHostility.HostileTo(pawn, Faction.OfPlayer))
            {
                return true;
            }

            // The neutral clause in VEF explicitly excludes hostiles, so a hostile
            // pawn whose def only enables the neutral rule is not shown there. Falling
            // through to it here without that check would paint a bubble VEF hides.
            if (GenHostility.HostileTo(pawn, Faction.OfPlayer)
                || !ReadBool(showOnNeutralInCombat!, propsObject)
                || inCombat == null)
            {
                return false;
            }

            return inCombat.Invoke(comp, new object[] { pawn }) is bool fighting
                && fighting;
        }

        /// <summary>
        /// A def that points the bubble at its own texture keeps that texture. Only
        /// the stock path (or VEF's empty fallback to it) is replaced; painting the
        /// dome over deliberate custom art is not this mod's call.
        /// </summary>
        private static bool IsStockBubble(object propsObject)
        {
            string? path = shieldTexPath!.GetValue(propsObject) as string;
            return path.NullOrEmpty()
                || string.Equals(path, StockBubbleTexPath, StringComparison.Ordinal);
        }

        private static ShieldState ReadState(ThingComp comp)
        {
            return shieldState!.GetValue(comp) is ShieldState state
                ? state
                : ShieldState.Disabled;
        }

        private static float ReadFloat(PropertyInfo property, object instance)
        {
            object? value = property.GetValue(instance, null);
            return value is float number ? number : 0f;
        }

        private static float ReadFloat(FieldInfo field, object instance)
        {
            object? value = field.GetValue(instance);
            return value is float number ? number : 0f;
        }

        private static bool ReadBool(FieldInfo field, object instance)
        {
            object? value = field.GetValue(instance);
            return value is bool flag && flag;
        }
    }
}
