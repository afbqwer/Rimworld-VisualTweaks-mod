using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Tells the restore log that a save was just loaded, so it can hold first-sight
    /// animations back for <see cref="ShieldRestoreLog.LoadGraceTicks"/>.
    ///
    /// One game-level hook handles both families. The alternative — arming it from
    /// each shield's own tick patch — would need one patch per comp type, including
    /// VEF's reflected ones, to answer a question that has nothing to do with any of
    /// them.
    ///
    /// <c>GameComponentUtility.LoadedGame</c> is the right seam rather than
    /// <c>Game.ExposeData</c>: it runs exactly once per load, after every component's
    /// data is read, and never on save. It is also not called for a new game, which
    /// is correct — a fresh map has no shields to flash, and the first generator the
    /// player builds should animate.
    ///
    /// A failed patch costs the grace window only: with no load signal the log never
    /// arms, so all shields on screen animate together once after loading. Nothing
    /// else depends on this.
    /// </summary>
    internal static class ShieldRestorePatches
    {
        internal static void Apply(Harmony harmony)
        {
            MethodInfo? target = AccessTools.Method(
                typeof(GameComponentUtility),
                nameof(GameComponentUtility.LoadedGame));
            MethodInfo? postfix = AccessTools.Method(
                typeof(ShieldRestorePatches),
                nameof(LoadedGamePostfix));

            if (target == null || postfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch GameComponentUtility.LoadedGame;"
                    + " every shield on screen will play its restore animation at once"
                    + " after loading a save.");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                VisualTweaksLog.Info("Patched GameComponentUtility.LoadedGame"
                    + " (shield first-sight grace after load).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch shield first-sight grace: "
                    + ex);
            }
        }

        private static void LoadedGamePostfix()
        {
            ShieldRestoreLog.OnGameLoaded();
        }
    }
}
