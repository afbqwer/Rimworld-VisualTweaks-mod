using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
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
    ///
    /// The same postfix records hits that were absorbed without breaking, so the
    /// renderer can answer them. Which answer that is — vanilla's jitter, a sector
    /// brightening or a lattice wave — is a player setting with its own parameters;
    /// the dome's own impact choice is independent.
    ///
    /// Vanilla's <c>AbsorbedDamage</c> also throws a handful of dust flecks on every
    /// absorbed hit. They are not part of the surface and they are large relative to
    /// a belt, so a switch, on by default, suppresses just those. The suppression is
    /// scoped to the inside of that one method: the flash, the sound, the break burst
    /// and every other dust puff in the game are unaffected.
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
            PatchHitDust(harmony);
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
        /// <c>AbsorbedDamage</c> is private, and it is where vanilla spawns the hit
        /// dust. Patching it rather than the public entry point keeps the scope exact:
        /// this method only runs for an absorbed, non-breaking hit, so the break burst
        /// and every other dust puff in the game stay untouched.
        ///
        /// The dust itself comes from a public static helper used all over the game,
        /// so that helper gets a prefix reading a flag which is set for the duration of
        /// this one call. A finalizer clears the flag even if the original throws, so a
        /// failed hit can never leave dust disabled for the rest of the session.
        /// </summary>
        private static void PatchHitDust(Harmony harmony)
        {
            MethodInfo? absorbed = AccessTools.Method(
                typeof(CompShield),
                "AbsorbedDamage");
            MethodInfo? prefix = AccessTools.Method(
                typeof(PersonalShieldPatches),
                nameof(AbsorbedDamagePrefix));
            MethodInfo? finalizer = AccessTools.Method(
                typeof(PersonalShieldPatches),
                nameof(AbsorbedDamageFinalizer));
            MethodInfo? dustPrefix = AccessTools.Method(
                typeof(PersonalShieldPatches),
                nameof(ThrowDustPuffPrefix));

            if (absorbed == null || prefix == null || finalizer == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch CompShield.AbsorbedDamage;"
                    + " hit dust cannot be switched off.");
                return;
            }

            try
            {
                harmony.Patch(
                    absorbed,
                    prefix: new HarmonyMethod(prefix),
                    finalizer: new HarmonyMethod(finalizer));
                VisualTweaksLog.Info("Patched CompShield.AbsorbedDamage"
                    + " (hit dust switch).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch CompShield."
                    + "AbsorbedDamage: " + ex);
            }

            // AbsorbedDamage uses the Vector3 overload. The IntVec3 one forwards to
            // it, but patching both keeps the gate independent of which overload a
            // future version calls.
            PatchDustOverload(harmony, typeof(Vector3), dustPrefix);
            PatchDustOverload(harmony, typeof(IntVec3), dustPrefix);
        }

        private static void PatchDustOverload(
            Harmony harmony,
            Type locType,
            MethodInfo? prefix)
        {
            if (prefix == null)
            {
                Log.Warning("[Visual Tweaks] ThrowDustPuff prefix missing;"
                    + " hit dust cannot be switched off.");
                return;
            }

            MethodInfo? target = AccessTools.Method(
                typeof(FleckMaker),
                nameof(FleckMaker.ThrowDustPuff),
                new[] { locType, typeof(Map), typeof(float) });

            if (target == null)
            {
                Log.Warning("[Visual Tweaks] FleckMaker.ThrowDustPuff(" + locType.Name
                    + ", Map, float) not found; that overload keeps its dust.");
                return;
            }

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch FleckMaker.ThrowDustPuff("
                    + locType.Name + ", Map, float): " + ex);
            }
        }

        /// <summary>
        /// True only for the duration of vanilla's <c>AbsorbedDamage</c> when the
        /// player turned hit dust off. Read by the <c>ThrowDustPuff</c> prefix; the
        /// game runs this logic on the main thread, and the finalizer above clears it
        /// even when the original throws.
        /// </summary>
        private static bool suppressAbsorbedDust;

        private static void AbsorbedDamagePrefix(CompShield __instance)
        {
            // Only while this mod is the one drawing the belt: the replacement being
            // off, or a draw failure that latched fatal, hands the whole presentation
            // — dust included — back to vanilla instead of leaving one hidden visual
            // change behind.
            suppressAbsorbedDust = !fatal
                && VisualTweaksSettings.personalShieldDisableHitDust
                && ShieldDomeBridge.ShouldReplace(__instance);
        }

        private static Exception? AbsorbedDamageFinalizer(Exception? __exception)
        {
            suppressAbsorbedDust = false;
            return __exception;
        }

        private static bool ThrowDustPuffPrefix()
        {
            return !suppressAbsorbedDust;
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
            if (fatal || !ShieldDomeBridge.ShouldReplace(comp))
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

            /// <summary>
            /// True when the vanilla method would absorb this damage rather than let
            /// it through: ranged or explosive, and not flagged to ignore shields.
            /// Melee and EMP therefore never reach the hit-effect path (EMP breaks and
            /// is handled as a state change instead).
            /// </summary>
            public bool Absorbable;

            public float Angle;
        }

        private static void PostPreApplyDamagePrefix(
            CompShield __instance,
            ref DamageInfo dinfo,
            out DamageSnapshot __state)
        {
            __state = default;
            if (__instance == null || __instance.ShieldState != ShieldState.Active)
            {
                return;
            }

            __state.WasActive = true;
            __state.Absorbable = !dinfo.Def.ignoreShields
                && (dinfo.Def.isRanged || dinfo.Def.isExplosive);
            __state.Angle = dinfo.Angle;
        }

        private static void PostPreApplyDamagePostfix(
            CompShield __instance,
            DamageSnapshot __state)
        {
            // Only a call that started with a working shield can have broken it or
            // absorbed anything. A shield already Resetting or Disabled is hit again
            // and again, and must not restart an effect that is already playing.
            // A belt this mod is not replacing keeps vanilla's own reaction.
            if (!__state.WasActive
                || __instance == null
                || !ShieldDomeBridge.ShouldReplace(__instance))
            {
                return;
            }

            if (__instance.ShieldState != ShieldState.Active)
            {
                BeginIfEnabled(__instance, __state.Angle);
                return;
            }

            // Absorbed without breaking: the belt survives, so this is the hit-effect
            // case. A shield that was Active and is still Active did not break, but
            // only a genuinely absorbable hit should react — melee and ignoreShields
            // damage leave the state untouched too. Every effect needs the record,
            // including the jitter, which only reads it when drawing.
            if (__state.Absorbable)
            {
                PersonalShieldHitLog.Notify(__instance, __state.Angle);
            }
        }

        private static void BeginIfEnabled(CompShield comp, float damageAngle)
        {
            // No custom break when the dome is not installed (shader failed, setting
            // off, screenshot mode): the stock one-frame disappearance is the only
            // sensible answer when there is no custom surface. The per-style choice
            // can also be Vanilla, which means do not animate at all.
            if (fatal
                || !ShieldDomeBridge.ShouldReplace(comp)
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
