using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>Which shell of the room opened: a wall (side jet) or a ceiling (inward suction).</summary>
    internal enum VacuumBreachKind
    {
        Wall,
        Ceiling,
    }

    /// <summary>
    /// 原版数据里不存在"破口"这个东西（见分析文档 2.3）：墙被拆掉时房间直接并入太空，
    /// 邻接表里不留任何痕迹。这个类型就是补出来的那个对象 —— 它只在拆除发生的那一瞬
    /// 采集一次，之后由渲染层消费。
    ///
    /// 采集点是 Building.Destroy 的 prefix：那一刻建筑还在、region 还没重建，
    /// 所以两侧房间都还能读到。晚一帧，房间就已经没了。
    ///
    /// 天花板的采集点同理是 RoofGrid.SetRoof 的 prefix：屋顶还挂在 grid 上、房间
    /// 还没被判成 ExposedToSpace 的时刻。两类破口共用同一套寿命和强度曲线，只是
    /// 渲染方向不同 —— 墙把气喷出去，天花板把气吸进来。
    /// </summary>
    internal sealed class VacuumBreach
    {
        internal VacuumBreachKind Kind;
        internal int RoomId;
        internal int StartTick;

        /// <summary>
        /// Tick of the most recently added cell. Explosion cells are affected over
        /// several ticks (1.5 ticks per cell of distance), so merging has to chain
        /// from the last addition rather than the first one; the initial event and
        /// the end of a multi-tick chain are not within the merge window of each
        /// other even though they belong to the same hole.
        /// </summary>
        internal int LastTick;

        internal readonly List<IntVec3> Cells = new List<IntVec3>(8);

        // 破口几何：Center 是被拆格子（们）的重心，Normal 垂直于缺口面、从房间指向太空，
        // Tangent 沿破口长度方向。
        internal Vector3 Center;
        internal Vector3 Normal = Vector3.right;
        internal Vector3 Tangent = Vector3.forward;
        internal int Width = 1;

        /// <summary>
        /// Ceiling holes are round on the GPU: the opening's equivalent radius in
        /// cells, derived from the removed cells' area. Unused for wall breaches.
        /// </summary>
        internal float HoleRadius = 0.5f;

        /// <summary>
        /// Ceiling-only: radius of the disc the suction is drawn on. Covers the
        /// hole plus a couple of cells of room floor, because the flow is visible
        /// where the air still is, not only where the opening is.
        /// </summary>
        internal float SuctionRadius = 2.8f;

        /// <summary>
        /// Set once Normal comes from the wall faces. Late cells of the same
        /// explosion can arrive after the region grid merged the room into space;
        /// those must not overwrite a resolved normal with the centre-based guess.
        /// </summary>
        internal bool NormalResolved;

        // 事件规模：房间多大、拆之前有多空。
        internal int RoomCells;
        internal float VacuumBefore;
        internal Vector3 RoomCenter;

        internal float DurationSeconds;

        /// <summary>
        /// Scroll position of the leak shader's noise, integrated by VacuumLeakLayer
        /// with game time. Lives here so it survives across frames without a parallel
        /// dictionary; resets naturally because breaches are transient.
        /// </summary>
        internal float FlowPhase;

        internal float AgeSeconds(int tick)
        {
            return (tick - StartTick).TicksToSeconds();
        }

        /// <summary>
        /// Duration after the user's time multiplier. Applied at evaluation time
        /// rather than when the breach is captured, so moving the slider also
        /// affects leaks that are already running.
        /// </summary>
        internal float ScaledDuration =>
            DurationSeconds * VisualTweaksSettings.vacuumLeakDurationScale;

        internal bool Expired(int tick) => AgeSeconds(tick) > ScaledDuration;

        /// <summary>
        /// 0..1 的包络。原版在这条路径上没有时间维度（真空值 0 tick 就到 1），
        /// 所以时长只能是作者曲线 —— 但强度起点由拆除前的真空度决定，不是常数。
        /// </summary>
        internal float Strength(int tick)
        {
            float peak = 1f - Mathf.Clamp01(VacuumBefore);
            float duration = ScaledDuration;
            if (duration <= 0.001f)
            {
                return 0f;
            }

            float x = Mathf.Clamp01(AgeSeconds(tick) / duration);
            float decay = (1f - x) * (1f - x);
            return peak * decay;
        }
    }

    internal static class VacuumBreaches
    {
        /// <summary>
        /// 一次爆发的基线时长：门的持续泄漏在小房间里也通常能看到 3 秒以上，
        /// 一次性破口不应因为房间小就明显更短。房间越大，按比例再延长。
        /// </summary>
        private const float BaseDurationSeconds = 3f;

        /// <summary>在基线之上按 房间格数 ÷ 破口格数 增加的秒数。只用于视觉。</summary>
        private const float SecondsPerCellRatio = 0.015f;

        /// <summary>
        /// Lower bound for a large opening. A breach covering several cells vents
        /// dramatically faster than a single puncture, but the effect still needs
        /// one readable beat before the vanilla fog takes over.
        /// </summary>
        private const float MinDurationSeconds = 1.2f;

        private const float MaxDurationSeconds = 6f;

        /// <summary>
        /// 同一次爆炸会连调用好几次 Destroy，用 tick + 房间 + 相邻关系把它们合成一个破口。
        /// tick 取“上一次加入格子的时刻”而不是首次时刻：爆炸按 1.5 tick/格向四周推进，
        /// 一条墙上的格子可能在好几 tick 里陆续被拆掉，只和首次时刻比较会让后半段另起炉灶。
        /// 缓速冲击波（propagationSpeed 0.5，对角线相邻的两格）可以隔 4~5 tick 才轮到，
        /// 所以窗口取 5。相邻关系是必需的：暂停时用开发工具连拆几处墙，所有 Destroy 的
        /// tick 都相同，只按房间合并会把多处独立的破口并成一个，画在它们中间。
        /// </summary>
        private const int MergeWindowTicks = 5;

        /// <summary>
        /// Upper bound on how long one chain of cells may keep extending the same
        /// breach. A single explosion spreads at 1.5 ticks per cell of distance, and
        /// the largest vanilla shockwave (30.9 cells at propagationSpeed 0.5) needs
        /// about 92 ticks, so two seconds covers it while still stopping unrelated
        /// later explosions on the same wall from merging forever.
        /// </summary>
        private const int MaxMergeSpanTicks = 120;

        /// <summary>Quota per map, not for the whole process.</summary>
        private const int MaxTrackedPerMap = 64;

        /// <summary>No air left to move; same cutoff the port scan uses.</summary>
        private const float FullVacuumCutoff = 0.999f;

        /// <summary>
        /// Live breaches, one bucket per map. The effect is per-map state, and the
        /// single shared list this used to be had two cross-map consequences: one
        /// busy map could spend the whole quota and silently swallow another map's
        /// captures, and tearing down one map had no way to drop only its own rows.
        /// Keys are Map.uniqueID; a bucket is dropped by <see cref="ClearFor"/> when
        /// its map is removed or disposed, and every bucket is dropped by
        /// <see cref="SyncSession"/> when the game itself is replaced.
        /// </summary>
        private static readonly Dictionary<int, List<VacuumBreach>> byMap =
            new Dictionary<int, List<VacuumBreach>>(4);

        /// <summary>
        /// The Game instance the buckets belong to. Current.Game is replaced wholesale
        /// on save load, on a new game and on quit-to-menu, so a reference mismatch is
        /// the one signal that every row held so far was captured by a session that no
        /// longer exists. This is what makes the reset independent of teardown paths:
        /// most of them never reach MapComponent.MapRemoved or Map.Dispose at all.
        /// </summary>
        private static Game? sessionOwner;

        /// <summary>
        /// Drops every bucket when the session changed. Costs one reference comparison,
        /// so it is safe to call from the capture paths and from the draw path.
        /// </summary>
        private static void SyncSession()
        {
            Game? game = Current.Game;
            if (!ReferenceEquals(game, sessionOwner))
            {
                sessionOwner = game;
                byMap.Clear();
            }
        }

        private static List<VacuumBreach>? BucketOrNull(int mapId)
        {
            return byMap.TryGetValue(mapId, out List<VacuumBreach>? bucket) ? bucket : null;
        }

        /// <summary>
        /// The bucket to append to, or null when this map already holds its quota of
        /// live breaches. Expired rows are pruned only when the quota is reached, which
        /// is the only moment the length actually matters.
        /// </summary>
        private static List<VacuumBreach>? ReserveBucket(int mapId, int tick)
        {
            if (!byMap.TryGetValue(mapId, out List<VacuumBreach>? bucket))
            {
                bucket = new List<VacuumBreach>(8);
                byMap[mapId] = bucket;
                return bucket;
            }

            if (bucket.Count < MaxTrackedPerMap)
            {
                return bucket;
            }

            PruneStale(bucket, tick);
            return bucket.Count < MaxTrackedPerMap ? bucket : null;
        }

        /// <summary>
        /// 由 Building.Destroy 的 prefix 调用。只负责采集，不负责画。
        /// </summary>
        internal static void NotifyDestroyed(Building building)
        {
            if (building == null || building.Destroyed || !building.Spawned)
            {
                return;
            }

            Map map = building.Map;
            if (map == null || map.Disposed || map.Biome == null || !map.Biome.inVacuum)
            {
                return;
            }

            if (Current.ProgramState != ProgramState.Playing)
            {
                return;
            }

            // 非气密建筑拆了不改变空气通路；气密建筑才是壳。
            if (!building.IsAirtight)
            {
                return;
            }

            SyncSession();

            CellRect rect = GenAdj.OccupiedRect(building);

            Room? wetRoom = null;
            bool opensToSpace = false;
            foreach (IntVec3 cell in rect)
            {
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    IntVec3 neighbour = cell + dir;
                    if (rect.Contains(neighbour) || !neighbour.InBounds(map))
                    {
                        continue;
                    }

                    Room room = neighbour.GetRoom(map);
                    if (room == null)
                    {
                        continue;
                    }

                    if (room.ExposedToSpace)
                    {
                        opensToSpace = true;
                        continue;
                    }

                    // 取最大的一侧：这面墙实际隔开的是那间房。
                    if (room.CellCount > 0 && (wetRoom == null || room.CellCount > wetRoom.CellCount))
                    {
                        wetRoom = room;
                    }
                }
            }

            if (opensToSpace && wetRoom != null)
            {
                Register(map, wetRoom, rect);
                return;
            }

            // 拆开的不是"房间 ↔ 太空"，那只是新增一个普通通道，交给常规通道扫描即可。
            // 例外：同一轮爆炸的后续墙可能已经看不到湿房间（先到的破口把房间并进了
            // 太空），但仍然是同一条正在扩大的缺口；这时只补几何，沿用已经定下的法线。
            if (opensToSpace && wetRoom == null)
            {
                TryExtendWall(map, rect);
            }
        }

        /// <summary>
        /// 由 RoofGrid.SetRoof 的 prefix 调用：只在屋顶确实存在、且即将被清成 null 时。
        /// 此刻房间还没被判成 ExposedToSpace，还能读到拆之前的真空度；晚一步房间就
        /// 整个并入太空，也再没有"是这一块天花板放走了空气"的记录了。
        ///
        /// 只有真正起密封作用的那一块才算事件：地形本来就通向真空、房间已经暴露、
        /// 或者本来就没有空气（vacuum 已满），拆屋顶不会让气流发生变化。
        /// 同一次塌方/连拆会在同一 tick 或相邻几 tick 里连续调用，按相邻关系并成一个洞；
        /// 空投仓在 HitRoof 里一次性清掉整个落脚区，全部落在同一 tick。
        /// </summary>
        internal static void NotifyCeilingRemoved(Map map, IntVec3 cell)
        {
            if (map == null || map.Disposed || map.Biome == null || !map.Biome.inVacuum)
            {
                return;
            }

            if (Current.ProgramState != ProgramState.Playing || !cell.InBounds(map))
            {
                return;
            }

            SyncSession();

            int tick = Find.TickManager.TicksGame;
            int mapId = map.uniqueID;

            VacuumBreach? breach = FindCeiling(mapId, tick, cell);
            if (breach == null)
            {
                // 地形本身漏真空时，屋顶并不参与密封，拆掉它不会产生新的气流。
                if (map.terrainGrid.TerrainAt(cell).exposesToVacuum)
                {
                    return;
                }

                Room? room = cell.GetRoom(map);
                if (room == null
                    || room.Dereferenced
                    || room.IsDoorway
                    || room.ExposedToSpace
                    || room.Vacuum >= FullVacuumCutoff)
                {
                    return;
                }

                List<VacuumBreach>? bucket = ReserveBucket(mapId, tick);
                if (bucket == null)
                {
                    return;
                }

                breach = new VacuumBreach
                {
                    Kind = VacuumBreachKind.Ceiling,
                    RoomId = room.ID,
                    StartTick = tick,
                    LastTick = tick,
                    RoomCells = Mathf.Max(1, room.CellCount),
                    VacuumBefore = Mathf.Clamp01(room.Vacuum),
                    RoomCenter = room.ExtentsClose.CenterCell.ToVector3Shifted(),
                };
                bucket.Add(breach);
            }

            if (!breach.Cells.Contains(cell))
            {
                breach.Cells.Add(cell);
                breach.LastTick = tick;
            }

            RecomputeCeiling(breach);

            VisualTweaksLog.LeakTrace(string.Concat(
                "ceiling hole: ", breach.Cells.Count.ToString(),
                " cell(s), room ", breach.RoomCells.ToString(), " cells @ vacuum ",
                breach.VacuumBefore.ToString("F2"), ", radius ",
                breach.HoleRadius.ToString("F2"), ", ",
                breach.DurationSeconds.ToString("F2"), "s"));
        }

        private static void Register(Map map, Room room, CellRect rect)
        {
            int tick = Find.TickManager.TicksGame;
            int mapId = map.uniqueID;

            List<VacuumBreach>? bucket = BucketOrNull(mapId);

            VacuumBreach? breach = null;
            if (bucket != null)
            {
                for (int i = bucket.Count - 1; i >= 0; i--)
                {
                    VacuumBreach candidate = bucket[i];
                    if (candidate.Kind == VacuumBreachKind.Wall
                        && candidate.RoomId == room.ID
                        && tick - candidate.LastTick <= MergeWindowTicks
                        && tick - candidate.StartTick <= MaxMergeSpanTicks
                        && Touches(candidate, rect))
                    {
                        breach = candidate;
                        break;
                    }
                }
            }

            if (breach == null)
            {
                bucket = ReserveBucket(mapId, tick);
                if (bucket == null)
                {
                    return;
                }

                breach = new VacuumBreach
                {
                    Kind = VacuumBreachKind.Wall,
                    RoomId = room.ID,
                    StartTick = tick,
                    LastTick = tick,
                    RoomCells = Mathf.Max(1, room.CellCount),
                    VacuumBefore = Mathf.Clamp01(room.Vacuum),
                    RoomCenter = room.ExtentsClose.CenterCell.ToVector3Shifted(),
                };
                bucket.Add(breach);
            }

            bool added = false;
            foreach (IntVec3 cell in rect)
            {
                if (!breach.Cells.Contains(cell))
                {
                    breach.Cells.Add(cell);
                    added = true;
                }
            }

            if (!added && breach.Cells.Count > 0)
            {
                return;
            }

            breach.LastTick = tick;
            RecomputeWall(breach, map, room);

            VisualTweaksLog.LeakTrace(string.Concat(
                "breach: ", breach.Cells.Count.ToString(), " cell(s), ",
                "room ", breach.RoomCells.ToString(), " cells @ vacuum ",
                breach.VacuumBefore.ToString("F2"), ", width ", breach.Width.ToString(),
                ", ", breach.DurationSeconds.ToString("F2"), "s"));
        }

        /// <summary>
        /// Extend an existing wall breach with a later cell of the same event. No
        /// room is available any more (the first hole already merged the room into
        /// space), so the normal stays as resolved and only the extent is recomputed.
        /// </summary>
        private static void TryExtendWall(Map map, CellRect rect)
        {
            int tick = Find.TickManager.TicksGame;
            List<VacuumBreach>? bucket = BucketOrNull(map.uniqueID);
            if (bucket == null)
            {
                return;
            }

            for (int i = bucket.Count - 1; i >= 0; i--)
            {
                VacuumBreach candidate = bucket[i];
                if (candidate.Kind != VacuumBreachKind.Wall
                    || tick - candidate.LastTick > MergeWindowTicks
                    || tick - candidate.StartTick > MaxMergeSpanTicks
                    || !Touches(candidate, rect))
                {
                    continue;
                }

                bool added = false;
                foreach (IntVec3 cell in rect)
                {
                    if (!candidate.Cells.Contains(cell))
                    {
                        candidate.Cells.Add(cell);
                        added = true;
                    }
                }

                if (!added)
                {
                    return;
                }

                candidate.LastTick = tick;
                UpdateCenter(candidate);
                RecomputeWallExtent(candidate);

                VisualTweaksLog.LeakTrace(string.Concat(
                    "breach extended: ",
                    candidate.Cells.Count.ToString(), " cell(s), width ",
                    candidate.Width.ToString(), ", ",
                    candidate.DurationSeconds.ToString("F2"), "s"));

                return;
            }
        }

        private static VacuumBreach? FindCeiling(int mapId, int tick, IntVec3 cell)
        {
            List<VacuumBreach>? bucket = BucketOrNull(mapId);
            if (bucket == null)
            {
                return null;
            }

            for (int i = bucket.Count - 1; i >= 0; i--)
            {
                VacuumBreach candidate = bucket[i];
                if (candidate.Kind != VacuumBreachKind.Ceiling
                    || tick - candidate.LastTick > MergeWindowTicks
                    || tick - candidate.StartTick > MaxMergeSpanTicks)
                {
                    continue;
                }

                if (Touches(candidate, cell))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// True when the new cells continue the same opening: overlapping or
        /// Chebyshev-adjacent to a cell already in the breach. One explosion
        /// reports its wall cells through several Destroy calls and they must
        /// still merge; separate holes — including several dev-tool deletions
        /// while paused, which all share a tick — must stay separate visuals.
        /// </summary>
        private static bool Touches(VacuumBreach breach, CellRect rect)
        {
            for (int i = 0; i < breach.Cells.Count; i++)
            {
                IntVec3 cell = breach.Cells[i];
                foreach (IntVec3 other in rect)
                {
                    if (Mathf.Abs(cell.x - other.x) <= 1
                        && Mathf.Abs(cell.z - other.z) <= 1)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool Touches(VacuumBreach breach, IntVec3 cell)
        {
            for (int i = 0; i < breach.Cells.Count; i++)
            {
                IntVec3 other = breach.Cells[i];
                if (Mathf.Abs(cell.x - other.x) <= 1
                    && Mathf.Abs(cell.z - other.z) <= 1)
                {
                    return true;
                }
            }

            return false;
        }

        private static void RecomputeWall(VacuumBreach breach, Map map, Room room)
        {
            UpdateCenter(breach);

            Vector3 normal = OpeningNormal(map, breach.Cells, room);
            if (normal.sqrMagnitude >= 0.0001f)
            {
                breach.Normal = normal.normalized;
                breach.NormalResolved = true;
            }
            else if (!breach.NormalResolved)
            {
                // No cardinal wet neighbour yet (corner oddity, or a first call
                // after a merge): approximate from the room centre, the old way.
                Vector3 approx = breach.Center - breach.RoomCenter;
                approx.y = 0f;
                breach.Normal = approx.sqrMagnitude < 0.0001f
                    ? Vector3.right
                    : approx.normalized;
            }

            breach.Tangent = new Vector3(-breach.Normal.z, 0f, breach.Normal.x);

            RecomputeWallExtent(breach);
        }

        /// <summary>Opening width along the already-resolved wall tangent.</summary>
        private static void RecomputeWallExtent(VacuumBreach breach)
        {
            float min = float.MaxValue;
            float max = float.MinValue;
            for (int i = 0; i < breach.Cells.Count; i++)
            {
                float t = Vector3.Dot(breach.Cells[i].ToVector3Shifted() - breach.Center, breach.Tangent);
                min = Mathf.Min(min, t);
                max = Mathf.Max(max, t);
            }

            breach.Width = Mathf.Max(1, Mathf.RoundToInt(max - min) + 1);

            UpdateDuration(breach);
        }

        /// <summary>
        /// One-shot burst length, shared by walls and ceilings. A single-cell
        /// opening starts from the 3 s base (about as long as a door leak in a
        /// small ship room); every merged cell then divides the result, because a
        /// multi-cell hole vents the same room much faster and a lingering effect
        /// reads as if the air kept escaping after the room is already empty.
        /// The quadratic envelope still decides how quickly it fades.
        /// </summary>
        private static void UpdateDuration(VacuumBreach breach)
        {
            float cells = Mathf.Max(1f, breach.Cells.Count);
            float ratio = breach.RoomCells / cells;
            float duration = (BaseDurationSeconds + ratio * SecondsPerCellRatio)
                / Mathf.Sqrt(cells);
            breach.DurationSeconds = Mathf.Clamp(
                duration,
                MinDurationSeconds,
                MaxDurationSeconds);
        }

        private static void UpdateCenter(VacuumBreach breach)
        {
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < breach.Cells.Count; i++)
            {
                sum += breach.Cells[i].ToVector3Shifted();
            }

            breach.Center = breach.Cells.Count > 0
                ? sum / breach.Cells.Count
                : Vector3.zero;
        }

        /// <summary>
        /// Ceiling holes have no wall face to aim at: the air converges on the
        /// opening from every direction, so the only geometry is a centre and an
        /// equivalent radius. The radius comes from the removed cells' area, and
        /// the drawn disc pads it so the flow is visible over the room floor.
        /// Duration uses the same merged-cell count as the walls.
        /// </summary>
        private static void RecomputeCeiling(VacuumBreach breach)
        {
            UpdateCenter(breach);

            // Equivalent-area circle of the removed cells, plus a small margin so
            // a single-cell hole still reads as an opening rather than a pinhole.
            breach.HoleRadius = Mathf.Max(
                0.6f,
                Mathf.Sqrt(breach.Cells.Count / Mathf.PI) + 0.1f);
            breach.SuctionRadius = Mathf.Clamp(
                breach.HoleRadius + 2.2f,
                2.8f,
                5.5f);

            UpdateDuration(breach);
        }

        /// <summary>
        /// Mean normal of the faces the opening was cut through: for every
        /// destroyed wall cell, the vectors from its wet-side neighbours to the
        /// cell. That is perpendicular to the wall face, unlike
        /// breach-centre minus room-centre, which tilts toward the room centre
        /// whenever the hole sits off-axis and made the stream look aimed inward.
        ///
        /// Relies on the same precondition as Register: called from the Destroy
        /// prefix, while the region grid still contains the wet room.
        /// </summary>
        private static Vector3 OpeningNormal(Map map, List<IntVec3> cells, Room wet)
        {
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < cells.Count; i++)
            {
                IntVec3 cell = cells[i];
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 neighbour = cell + GenAdj.CardinalDirections[d];
                    if (!neighbour.InBounds(map))
                    {
                        continue;
                    }

                    if (neighbour.GetRoom(map) == wet)
                    {
                        sum += new Vector3(cell.x - neighbour.x, 0f, cell.z - neighbour.z);
                    }
                }
            }

            return sum;
        }

        /// <summary>
        /// True when a row cannot be drawn at <paramref name="tick"/>: either its
        /// envelope has run out, or it is dated in the future.
        ///
        /// The future case is a backstop rather than the main defence — SyncSession
        /// already drops everything when the game is replaced — but it also covers a
        /// dev tool that sets the tick backwards within one session, and any future
        /// path that revives a row across a load. It matters because AgeSeconds() then
        /// goes negative, which Expired() reads as "still running" and Strength() reads
        /// as full strength: such a row would replay at full power instead of expiring.
        /// </summary>
        private static bool IsStale(VacuumBreach breach, int tick)
        {
            return breach.StartTick > tick || breach.Expired(tick);
        }

        private static void PruneStale(List<VacuumBreach> bucket, int tick)
        {
            for (int i = bucket.Count - 1; i >= 0; i--)
            {
                if (IsStale(bucket[i], tick))
                {
                    bucket.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 渲染层每帧调用。会顺手清掉过期（以及在读档/改 tick 之后变成"未来"）的破口。
        ///
        /// Only this map's bucket is touched: the other maps' rows belong to visuals
        /// that are still running there, and they get pruned when their own map draws
        /// (or dropped wholesale by ClearFor when that map goes away).
        /// </summary>
        internal static void CollectActive(Map map, List<VacuumBreach> result)
        {
            result.Clear();
            if (map == null)
            {
                return;
            }

            SyncSession();

            int tick = Find.TickManager.TicksGame;
            List<VacuumBreach>? bucket = BucketOrNull(map.uniqueID);
            if (bucket == null)
            {
                return;
            }

            for (int i = bucket.Count - 1; i >= 0; i--)
            {
                VacuumBreach breach = bucket[i];
                if (IsStale(breach, tick))
                {
                    bucket.RemoveAt(i);
                    continue;
                }

                result.Add(breach);
            }
        }

        /// <summary>
        /// Drops one map's breaches. Called when that map is removed or disposed, so a
        /// teardown on one map cannot delete another map's still-running visuals.
        /// </summary>
        internal static void ClearFor(int mapId)
        {
            byMap.Remove(mapId);
        }
    }
}
