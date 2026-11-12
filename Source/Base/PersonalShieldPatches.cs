using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Takes over <c>CompShield</c>'s drawing for personal shield belts, and starts
    /// the custom break animation when the belt actually fails.
    ///
    /// <c>CompShield.Draw()</c> is private, so the two places that call it are what
    /// get patched: <c>CompDrawWornExtras</c> for worn apparel and <c>PostDraw</c>
    /// for a shield built into a pawn. Both prefixes return false when the custom
    /// dome was submitted, which removes vanilla's bubble rather than layering over
    /// it; when nothing was drawn they return true, and the stock method decides for
    /// itself (including drawing nothing).
    ///
    /// <c>ShouldDisplay</c> is protected and not virtual. It is bound once as an open
    /// delegate instead of being copied, so a future vanilla change to the
    /// "is this belt worth showing" rule cannot make the custom drawing and the
    /// gameplay state disagree. If the binding fails, no patch is installed at all:
    /// a dome shown in situations vanilla keeps hidden is worse than the stock look.
    ///
    /// The break signal comes from <c>PostPreApplyDamage</c>, not from the private
    /// <c>Break()</c>. A shield that was Active before the call and is not Active
    /// after it is a shield that this damage just broke — the same observation, made
    /// on a method vanilla is unlikely to inline away, and without depending on a
    /// private name.
    /// </summary>
    internal static class PersonalShieldPatches
    {
        private static Func<CompShield, bool>? shouldDisplayOf;
        private static bool fatal;

        internal static void Apply(Harmony harmony)
        {
            shouldDisplayOf = BindShouldDisplay();
            if (shouldDisplayOf == null)
            {
                Log.Warning("[Visual Tweaks] Personal shield belts need CompShield."
                    + "ShouldDisplay; replacement not applied.");
                return;
            }

            PatchDrawEntry(
                harmony,
                nameof(CompShield.CompDrawWornExtras),
                nameof(CompDrawWornExtrasPrefix),
                "worn personal shield");

            PatchDrawEntry(
                harmony,
                nameof(CompShield.PostDraw),
                nameof(PostDrawPrefix),
                "built-in personal shield");

            PatchBreakSignal(harmony);
        }

        /// <summary>
        /// Binds a protected parameterless instance property getter returning bool
        /// as an open delegate, so the per-frame cost is a delegate call rather than
        /// reflection.
        /// </summary>
        private static Func<CompShield, bool>? BindShouldDisplay()
        {
            MethodInfo? getter = AccessTools.PropertyGetter(
                typeof(CompShield), "ShouldDisplay");

            if (getter == null || getter.ReturnType != typeof(bool))
            {
                Log.Warning("[Visual Tweaks] CompShield.ShouldDisplay not found with"
                    + " the expected signature; personal shield drawing kept stock.");
                return null;
            }

            try
            {
                return (Func<CompShield, bool>)Delegate.CreateDelegate(
                    typeof(Func<CompShield, bool>),
                    getter);
            }
            catch (Exception ex)
            {
                Log.Warning("[Visual Tweaks] Cannot bind CompShield.ShouldDisplay: "
                    + ex.Message);
                return null;
            }
        }

        private static void PatchDrawEntry(
            Harmony harmony,
            string targetName,
            string prefixName,
            string label)
        {
            MethodInfo? target = AccessTools.Method(typeof(CompShield), targetName);
            MethodInfo? prefix = AccessTools.Method(
                typeof(PersonalShieldPatches), prefixName);

            if (target == null || prefix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch CompShield." + targetName
                    + "; " + label + " keeps the stock drawing.");
                return;
            }

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                VisualTweaksLog.Info("Patched CompShield." + targetName
                    + " (" + label + ").");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch CompShield." + targetName
                    + ": " + ex);
            }
        }

        private static void PatchBreakSignal(Harmony harmony)
        {
            MethodInfo? target = AccessTools.Method(
                typeof(CompShield),
                nameof(CompShield.PostPreApplyDamage));
            MethodInfo? prefix = AccessTools.Method(
                typeof(PersonalShieldPatches),
                nameof(PostPreApplyDamagePrefix));
            MethodInfo? postfix = AccessTools.Method(
                typeof(PersonalShieldPatches),
                nameof(PostPreApplyDamagePostfix));

            if (target == null || prefix == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch CompShield.PostPreApplyDamage;"
                    + " personal shield breaks will not dissolve.");
                return;
            }

            try
            {
                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched CompShield.PostPreApplyDamage"
                    + " (personal shield break).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch CompShield."
                    + "PostPreApplyDamage: " + ex);
            }
        }

        /// <summary>
        /// Priority.First because Harmony stops at the first prefix that returns false.
        /// Another mod taking over the same draw method the same way would otherwise
        /// skip the original before this runs, and then neither drawing would appear.
        /// </summary>
        [HarmonyPriority(Priority.First)]
        private static bool CompDrawWornExtrasPrefix(CompShield __instance)
        {
            return DrawFor(__instance, wornExtras: true);
        }

        [HarmonyPriority(Priority.First)]
        private static bool PostDrawPrefix(CompShield __instance)
        {
            return DrawFor(__instance, wornExtras: false);
        }

        /// <summary>
        /// Mirrors the split the two vanilla entry points already make: worn apparel
        /// draws from CompDrawWornExtras, a built-in shield from PostDraw. Checking it
        /// here as well means a comp that somehow reaches both paths cannot paint two
        /// domes on one frame.
        /// </summary>
        private static bool DrawFor(CompShield comp, bool wornExtras)
        {
            // Returning true means "run vanilla", which is the correct answer whenever
            // the replacement is unavailable.
            if (fatal || !ShieldDomeBridge.Active || comp == null)
            {
                return true;
            }

            if (wornExtras)
            {
                if (!comp.IsApparel
                    || comp.parent is not Apparel apparel
                    || apparel.Wearer == null)
                {
                    return true;
                }
            }
            else if (comp.parent is not Pawn)
            {
                return true;
            }

            Func<CompShield, bool>? shouldDisplay = shouldDisplayOf;
            if (shouldDisplay == null)
            {
                return true;
            }

            try
            {
                return !PersonalShieldRenderer.Draw(comp, shouldDisplay);
            }
            catch (Exception ex)
            {
                // A throwing prefix would be retried every frame by every belt on the
                // map. Latch instead, and let vanilla draw from here on.
                fatal = true;
                Log.Error("[Visual Tweaks] Personal shield draw failed, stock drawing"
                    + " restored: " + ex);
                return true;
            }
        }

        /// <summary>
        /// Captured before the original runs, because after an absorbed hit the energy
        /// (and possibly the shield's whole state) has already changed.
        /// </summary>
        private struct DamageSnapshot
        {
            public bool WasActive;
            public float Angle;
        }

        private static void PostPreApplyDamagePrefix(
            CompShield __instance,
            ref DamageInfo dinfo,
            out DamageSnapshot __state)
        {
            __state = default;
            if (__instance != null && __instance.ShieldState == ShieldState.Active)
            {
                __state.WasActive = true;
                __state.Angle = dinfo.Angle;
            }
        }

        private static void PostPreApplyDamagePostfix(
            CompShield __instance,
            DamageSnapshot __state)
        {
            // Only a call that started with a working shield can have broken it.
            // A shield already Resetting or Disabled is hit again and again, and must
            // not restart a dissolve that is already playing.
            if (!__state.WasActive
                || __instance == null
                || __instance.ShieldState == ShieldState.Active)
            {
                return;
            }

            BeginIfEnabled(__instance, __state.Angle);
        }

        private static void BeginIfEnabled(CompShield comp, float damageAngle)
        {
            // No custom break when the dome is not installed (shader failed, setting
            // off, screenshot mode): the stock one-frame disappearance is the only
            // sensible answer when there is no custom surface. The per-style choice
            // can also be Vanilla, which means do not animate at all.
            if (fatal
                || !ShieldDomeBridge.Active
                || VisualTweaksSettings.CurrentShieldBreakStyle
                    == VisualTweaksSettings.ShieldBreakVanilla)
            {
                return;
            }

            Pawn? pawn = comp.parent is Apparel apparel
                ? apparel.Wearer
                : comp.parent as Pawn;
            if (pawn == null || !pawn.Spawned || pawn.Dead)
            {
                return;
            }

            PersonalShieldDissolveLog.Begin(comp, damageAngle);
        }
    }
}
