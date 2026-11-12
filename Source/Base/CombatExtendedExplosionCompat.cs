using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Bridges Combat Extended's explosion start into the mod's shockwave.
    ///
    /// CE does not use <see cref="Explosion.StartExplosion"/>: GenExplosionCE spawns an
    /// ExplosionCE and calls its own StartExplosionCE, so the postfix this mod hangs on
    /// the vanilla method is dead under CE and explosions get no shockwave at all.
    ///
    /// The patch is installed only when CE is loaded and is bound by reflection, so the
    /// base assembly keeps working without CE and without a compile reference.
    ///
    /// Two CE specifics drive the code below:
    /// StartExplosionCE ignores <see cref="Explosion.doVisualEffects"/> (it always emits
    /// its own FleckMakerCE splash), so that flag must not veto the effect here; and an
    /// explosion that merges into an existing one calls StartExplosionCE twice in the
    /// same tick for the same instance — the outer call early-returns into
    /// RestartAfterMerge and the nested call performs the real start, both reaching the
    /// postfix with the same radius and position. Without the per-tick guard every
    /// merged explosion would draw two identical, fully overlapping rings.
    /// </summary>
    internal static class CombatExtendedExplosionCompat
    {
        private const string ExplosionCETypeName = "CombatExtended.ExplosionCE";

        private static int dedupTick = -1;
        private static readonly HashSet<Explosion> spawnedThisTick =
            new HashSet<Explosion>();

        internal static void Apply(Harmony harmony)
        {
            Type? explosionType = AccessTools.TypeByName(ExplosionCETypeName);
            if (explosionType == null)
            {
                // CE absent is the normal case, not a warning.
                return;
            }

            MethodInfo? target = AccessTools.Method(
                explosionType,
                "StartExplosionCE",
                new[] { typeof(SoundDef), typeof(List<Thing>) });
            MethodInfo? prefix = AccessTools.Method(
                typeof(CombatExtendedExplosionCompat),
                nameof(StartExplosionCEPrefix));
            MethodInfo? postfix = AccessTools.Method(
                typeof(CombatExtendedExplosionCompat),
                nameof(StartExplosionCEPostfix));

            if (target == null || prefix == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Combat Extended found, but ExplosionCE."
                    + "StartExplosionCE has an unexpected signature; explosion shockwaves"
                    + " stay missing under CE.");
                return;
            }

            try
            {
                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched CombatExtended.ExplosionCE.StartExplosionCE"
                    + " (CE explosion shockwave).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch "
                    + "CombatExtended.ExplosionCE.StartExplosionCE: " + ex);
            }
        }

        /// <summary>
        /// <c>__instance</c> is typed as <see cref="Explosion"/> so this assembly needs
        /// no reference to CE's type; the state only forwards "this is a real instance"
        /// to the postfix.
        /// </summary>
        [HarmonyPriority(Priority.High)]
        private static void StartExplosionCEPrefix(Explosion __instance, out bool __state)
        {
            __state = __instance != null;
        }

        private static void StartExplosionCEPostfix(Explosion __instance, bool __state)
        {
            if (!__state || __instance == null || AlreadySpawnedThisTick(__instance))
            {
                return;
            }

            ExplosionRipplePatches.TrySpawn(__instance);
        }

        /// <summary>
        /// One shockwave per explosion per tick, which only filters the duplicate
        /// merge-start described above. The set is cleared the moment the tick
        /// advances, so it never accumulates instances and costs no more than one hash
        /// lookup per explosion.
        /// </summary>
        private static bool AlreadySpawnedThisTick(Explosion explosion)
        {
            int tick = Find.TickManager?.TicksGame ?? -1;
            if (tick != dedupTick)
            {
                dedupTick = tick;
                spawnedThisTick.Clear();
            }

            return !spawnedThisTick.Add(explosion);
        }
    }
}
