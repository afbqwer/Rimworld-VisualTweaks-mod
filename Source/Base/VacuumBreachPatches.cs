using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// 这是整个漏气视觉里仅有的两处必须改原版行为的地方，而且都只读不写。
    ///
    /// 1. 墙体破口：挂 Building.Destroy 的 prefix —— 建筑还在、region 还没重建的
    ///    唯一时刻，能读到两侧房间、开口宽度和拆之前的真空度。
    ///
    /// 2. 天花板破口：挂 RoofGrid.SetRoof 的 prefix —— 屋顶还挂在 grid 上、房间
    ///    还没被判成 ExposedToSpace 的时刻。晚一行房间就整个并入太空，"是哪一块
    ///    天花板放走了空气"就再也读不到了。
    ///
    /// 两个 prefix 都不返回 false、不改字段，异常只记日志，原版流程照常走完。
    /// </summary>
    internal static class VacuumBreachPatches
    {
        /// <summary>
        /// RoofGrid's map field is private. Cached once; SetRoof runs only when a
        /// roof actually changes, so a reflection read here costs nothing.
        /// </summary>
        private static readonly FieldInfo? RoofGridMapField =
            AccessTools.Field(typeof(RoofGrid), "map");

        internal static void Apply(Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(
                typeof(Thing),
                nameof(Thing.Destroy),
                new[] { typeof(DestroyMode) });
            MethodInfo prefix = AccessTools.Method(
                typeof(VacuumBreachPatches),
                nameof(DestroyPrefix));

            if (target == null || prefix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch Thing.Destroy for vacuum breach capture.");
            }
            else
            {
                try
                {
                    harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                    Log.Message("[Visual Tweaks] Patched Thing.Destroy (vacuum breach capture).");
                }
                catch (Exception ex)
                {
                    Log.Error("[Visual Tweaks] Failed to patch Thing.Destroy: " + ex);
                }
            }

            ApplyRoofPatch(harmony);
        }

        private static void ApplyRoofPatch(Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(
                typeof(RoofGrid),
                nameof(RoofGrid.SetRoof),
                new[] { typeof(IntVec3), typeof(RoofDef) });
            MethodInfo prefix = AccessTools.Method(
                typeof(VacuumBreachPatches),
                nameof(RoofSetPrefix));

            if (target == null || prefix == null || RoofGridMapField == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch RoofGrid.SetRoof for ceiling breach capture.");
                return;
            }

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                Log.Message("[Visual Tweaks] Patched RoofGrid.SetRoof (ceiling breach capture).");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch RoofGrid.SetRoof: " + ex);
            }
        }

        private static void DestroyPrefix(Thing __instance)
        {
            if (!VisualTweaksSettings.vacuumLeakEnabled || __instance is not Building building)
            {
                return;
            }

            try
            {
                VacuumBreaches.NotifyDestroyed(building);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[Visual Tweaks] Vacuum breach capture failed: " + ex, 0x5AC0FF1);
            }
        }

        /// <summary>
        /// Fires before the roof is cleared. The grid still holds the old roof,
        /// so the room is still sealed and its pre-event vacuum can be read;
        /// after this line it is already ExposedToSpace and the past is gone.
        /// Only null is interesting — installing or swapping a roof is not a breach.
        /// </summary>
        private static void RoofSetPrefix(RoofGrid __instance, IntVec3 c, RoofDef def)
        {
            if (!VisualTweaksSettings.vacuumLeakEnabled || def != null || RoofGridMapField == null)
            {
                return;
            }

            try
            {
                if (__instance.RoofAt(c) == null)
                {
                    return;
                }

                if (RoofGridMapField.GetValue(__instance) is Map map)
                {
                    VacuumBreaches.NotifyCeilingRemoved(map, c);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[Visual Tweaks] Ceiling breach capture failed: " + ex, 0x5AC0FF2);
            }
        }
    }
}
