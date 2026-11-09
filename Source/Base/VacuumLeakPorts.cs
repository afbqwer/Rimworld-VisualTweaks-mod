using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// One ventilation opening: an open door, a switched-on vent, an unpowered vacuum
    /// barrier, an animal flap — any edifice on a room border whose ExchangeVacuum is
    /// true. Keyed by the edifice's own cell; a gap is found once, from the building,
    /// instead of once per adjacent room.
    ///
    /// Target is the raw intensity from the last scan; Smoothed is the low-passed
    /// value the renderer consumes. Phase integrates the scroll position so a change
    /// in flow speed never jumps the noise pattern.
    /// </summary>
    internal sealed class VacuumLeakPort
    {
        internal IntVec3 Cell;

        /// <summary>Centre of the opening — the quad's gap plane, not the building's anchor cell.</summary>
        internal Vector3 Center;

        /// <summary>Width of the opening in cells: how many building cells face the wet room.</summary>
        internal int GapCells;

        /// <summary>Distance from <see cref="Center"/> to the farthest corner of the quad.</summary>
        internal float Reach;

        /// <summary>Unit XZ vector pointing from the wet room through the gap (wall normal).</summary>
        internal Vector3 Direction = Vector3.right;

        internal float QuadWidth;

        /// <summary>Gap width / quad width: how far the convergence mask narrows at the gap.</summary>
        internal float Narrow;

        /// <summary>
        /// One quad spanning the wet room, the opening and the short dry-side plume.
        /// The old split pair met at the opening with two noise phases and an
        /// overlapping alpha band, which read as two mirrored sheets with a seam.
        /// </summary>
        internal Matrix4x4 Matrix;

        /// <summary>UV v of the opening inside the unified quad.</summary>
        internal float GapV;

        /// <summary>Quad length in cells (wet length + dry length).</summary>
        internal float LenCells;

        internal float Target;
        internal float Smoothed;
        internal float Phase;
        internal float Seed;

        /// <summary>Mark-sweep flag: reset each scan, set when a building claims the port.</summary>
        internal bool Seen;
    }

    /// <summary>
    /// Per-map port table and intensity chain for the continuous leak visual.
    ///
    /// The scan is driven by the map's building lister, not by the floor:
    /// <see cref="ListerBuildings.allBuildingsColonist"/> plus
    /// <see cref="ListerBuildings.allBuildingsNonColonist"/> is every spawned Building
    /// except natural rock, and natural rock is the one kind that can never be a port
    /// (its def is airtight with alwaysExchangeVacuum off, so ExchangeVacuum is false —
    /// the same reason ListerBuildings.Add skips it). So the two lists are exactly the
    /// candidate set, with no probe list of our own to keep in sync and no extra patch.
    ///
    /// Why not walk rooms and their BorderCellsCardinal, the way vanilla's
    /// MergeRoomsIntoGroups does: that costs O(total floor area) per scan — five region
    /// lookups per cell through a three-level yield chain — while a port can only exist
    /// where a building stands. Cost is now O(buildings), which is roughly an order of
    /// magnitude smaller on a developed map and grows slower as a colony spreads out.
    ///
    /// Intensity = (1 - vWet) * (vDry - vWet): the pressure difference drives the flow,
    /// the remaining air on the wet side gates it. The wet side is inferred per gap as
    /// the lower-vacuum room, which also settles which direction the strip faces.
    ///
    /// Cost is further bounded by three gates: the periodic 250-tick sweep, a 15-tick
    /// floor on structural rescans, and a per-cell update for events that name their own
    /// cell (doors, and terrain/roof changes).
    /// </summary>
    internal sealed class VacuumLeakPorts
    {
        // Geometry shared with the breach visuals (see VacuumLeakLayer).
        internal const float StreakLength = 2.5f;
        internal const float JetLength = 4f;

        // Port jets are shorter than breach jets: a continuing leak sheds air that
        // dissipates within a couple of cells, while a breach blows a whole parcel out.
        internal const float PortJetLength = 2f;

        // Cross-axis size of the strip. The shader feathers the mask to zero
        // before the mesh edge, so the visible strip is about 84% of this. The
        // burst needs a wider quad than a standing door leak: its plume blooms
        // out into vacuum while the port just sheds a short puff.
        internal const float PortWidthMargin = 2.1f;
        internal const float BreachWidthMargin = 3f;

        // The port visual is a single strip: StreakLength into the room,
        // PortJetLength into space, with the opening in between.
        internal const float PortLength = StreakLength + PortJetLength;
        internal const float PortGapV = StreakLength / PortLength;

        // Bridges the 0.24 Hz state steps; same exponential form as GasFieldRenderer.
        private const float SmoothTau = 0.2f;

        // Scroll speed in UV/s: base + gain * strength. Faster flow reads as harder leak.
        private const float FlowSpeedBase = 0.55f;
        private const float FlowSpeedGain = 1.75f;

        // Matches vanilla ExchangeRoomVacuum's cadence.
        private const int ScanInterval = 250;

        // Floor on structural rescans. A door needs no rescan at all (it names its own
        // cell), so what is left here is room topology: a region rebuild, a roof
        // collapse, a terrain change. Those arrive in bursts, not as a stream, and
        // 0.25 s of latency on them is invisible while it caps the worst case at one
        // scan per 15 ticks instead of one per frame.
        private const int MinRescanTicks = 15;

        // Above this many individually-dirty cells a full scan is cheaper than the
        // per-cell path, and the queue only grows while nothing consumes it.
        private const int MaxCellDirty = 64;

        private readonly Dictionary<IntVec3, VacuumLeakPort> ports =
            new Dictionary<IntVec3, VacuumLeakPort>();

        private readonly List<IntVec3> removeBuffer = new List<IntVec3>();

        // Cells an event told us about. Recomputing one cell is O(1) and keeps the
        // open-a-door-and-see-flow behaviour on the very next frame.
        private readonly List<IntVec3> cellDirty = new List<IntVec3>(8);
        private readonly HashSet<IntVec3> cellDirtySet = new HashSet<IntVec3>();

        // Rooms touching the building being examined. Reused across buildings: at most
        // four distinct rooms can surround one edifice.
        private readonly List<Room> roomBuffer = new List<Room>(4);

        private int lastScanTick = -1;
        private bool structureDirty = true;

        /// <summary>
        /// Returned by value (ValueCollection is a struct): the caller's foreach then
        /// binds to the struct enumerator instead of boxing the collection.
        /// </summary>
        internal Dictionary<IntVec3, VacuumLeakPort>.ValueCollection All => ports.Values;

        internal static float PhaseSpeed(float strength)
        {
            // The settings multiplier applies to the scroll rate only; the pattern
            // position itself is integrated, so changing the slider never jumps it.
            return (FlowSpeedBase + FlowSpeedGain * strength)
                * VisualTweaksSettings.vacuumLeakSpeed;
        }

        /// <summary>
        /// Called by the map component on region/roof/terrain events: the next Draw
        /// rescans, subject to <see cref="MinRescanTicks"/>.
        /// </summary>
        internal void MarkStructureDirty()
        {
            structureDirty = true;
        }

        /// <summary>
        /// Called by the map component on events that name the cell they changed (a
        /// door toggling). The next Draw recomputes just that cell, so the port can
        /// appear or vanish on the next frame without dragging the whole map through
        /// a rescan.
        /// </summary>
        internal void MarkCellDirty(IntVec3 cell)
        {
            // Nothing consumes this queue while the layer is gated off (effect
            // disabled, another map, screenshot mode), and a raid can toggle a few
            // hundred doors in the meantime. Past the cap the individual cells stop
            // being worth tracking, so collapse to one full scan: bounded memory and
            // still correct, because a scan covers every building anyway.
            if (cellDirty.Count >= MaxCellDirty)
            {
                cellDirty.Clear();
                cellDirtySet.Clear();
                structureDirty = true;
                return;
            }

            if (cellDirtySet.Add(cell))
            {
                cellDirty.Add(cell);
            }
        }

        /// <summary>
        /// Per-frame update: apply the cells events pointed at, rescan when due,
        /// low-pass every port toward its target, integrate scroll phase with game
        /// time (freezes while paused), and drop ports that disappeared and faded out.
        /// </summary>
        internal void Update(Map map, int tick, float dtReal, float dtGame)
        {
            bool periodic = lastScanTick < 0 || tick - lastScanTick >= ScanInterval;
            bool structural = structureDirty && tick - lastScanTick >= MinRescanTicks;

            if (periodic || structural)
            {
                // A full scan recomputes every building, so pending per-cell updates
                // are subsumed by it. Drop them rather than doing the work twice.
                cellDirty.Clear();
                cellDirtySet.Clear();
                Scan(map, tick);
            }
            else if (cellDirty.Count > 0)
            {
                for (int i = 0; i < cellDirty.Count; i++)
                {
                    UpdateCell(map, cellDirty[i]);
                }

                cellDirty.Clear();
                cellDirtySet.Clear();
            }

            float k = 1f - Mathf.Exp(-dtReal / SmoothTau);

            removeBuffer.Clear();
            foreach (KeyValuePair<IntVec3, VacuumLeakPort> pair in ports)
            {
                VacuumLeakPort port = pair.Value;
                port.Smoothed += (port.Target - port.Smoothed) * k;
                port.Phase += dtGame * PhaseSpeed(port.Smoothed);

                if (!port.Seen && port.Smoothed < 0.004f)
                {
                    removeBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < removeBuffer.Count; i++)
            {
                ports.Remove(removeBuffer[i]);
            }
        }

        /// <summary>
        /// Full rebuild of the port table, driven by the building lister.
        /// </summary>
        private void Scan(Map map, int tick)
        {
            lastScanTick = tick;
            structureDirty = false;

            foreach (VacuumLeakPort port in ports.Values)
            {
                port.Seen = false;
                port.Target = 0f;
            }

            // Indexed rather than foreach so the list's struct enumerator is used and
            // nothing here depends on the collection not growing mid-loop.
            ListerBuildings lister = map.listerBuildings;

            List<Building> colonists = lister.allBuildingsColonist;
            for (int i = 0; i < colonists.Count; i++)
            {
                Consider(map, colonists[i]);
            }

            List<Building> others = lister.allBuildingsNonColonist;
            for (int i = 0; i < others.Count; i++)
            {
                Consider(map, others[i]);
            }
        }

        /// <summary>
        /// One lister entry. The passability test is the cheapest useful filter in the
        /// whole path and it is also exact: a building pawns can walk over does not
        /// split the region grid, so it can never sit between two different rooms — it
        /// is always part of whichever room surrounds it. That removes most furniture
        /// before a single region lookup happens.
        /// </summary>
        private void Consider(Map map, Building building)
        {
            if (building.def.passability == Traversability.Standable)
            {
                return;
            }

            TryClaimBuilding(map, building);
        }

        /// <summary>
        /// Recompute the port for one building after an event named its cell. The cell
        /// an event reports is not necessarily the building's own cell (a multi-tile
        /// door), so the port is always keyed by the edifice.
        /// </summary>
        private void UpdateCell(Map map, IntVec3 cell)
        {
            Building? edifice = cell.GetEdifice(map);
            if (edifice == null)
            {
                Retire(cell);
                return;
            }

            if (!TryClaimBuilding(map, edifice))
            {
                Retire(edifice.Position);
            }
        }

        /// <summary>
        /// Decide whether this edifice is a leak, and register it if so. True when a
        /// port now exists for it.
        ///
        /// The wet side is inferred rather than handed in: of the rooms the building
        /// touches, the lowest-vacuum one is losing air and the highest one is where it
        /// goes. That is the same pair the room-driven scan used to resolve, and it
        /// keeps the two entry points (full scan, single cell) in agreement.
        /// </summary>
        private bool TryClaimBuilding(Map map, Building edifice)
        {
            if (!edifice.ExchangeVacuum)
            {
                return false;
            }

            CellRect rect = edifice.OccupiedRect();
            roomBuffer.Clear();

            foreach (IntVec3 occupied in rect)
            {
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 neighbour = occupied + GenAdj.CardinalDirections[d];
                    if (rect.Contains(neighbour) || !neighbour.InBounds(map))
                    {
                        continue;
                    }

                    Room? room = neighbour.GetRoom(map);
                    if (room == null || room.IsDoorway || room.Dereferenced)
                    {
                        continue;
                    }

                    if (!roomBuffer.Contains(room))
                    {
                        roomBuffer.Add(room);
                    }
                }
            }

            if (roomBuffer.Count < 2)
            {
                return false;
            }

            Room? wet = null;
            Room? dry = null;
            float vWet = float.MaxValue;
            float vDry = float.MinValue;

            for (int i = 0; i < roomBuffer.Count; i++)
            {
                Room room = roomBuffer[i];
                float v = room.Vacuum;
                if (v < vWet)
                {
                    vWet = v;
                    wet = room;
                }

                if (v > vDry)
                {
                    vDry = v;
                    dry = room;
                }
            }

            if (wet == null || dry == null || wet == dry)
            {
                // Equal vacuum on both sides means no flow to draw, and it is also how
                // a target of zero resolves below; the two agree by construction.
                return false;
            }

            return Claim(map, edifice.Position, wet, dry, edifice, vWet, vDry);
        }

        /// <summary>
        /// Write a target for one port, creating the entry on first sight. False when
        /// the pressure difference is too small to draw; <see cref="Retire"/> covers
        /// the opposite direction.
        /// </summary>
        private bool Claim(
            Map map,
            IntVec3 cell,
            Room wet,
            Room dry,
            Building edifice,
            float vWet,
            float vDry)
        {
            float target = Mathf.Min((1f - vWet) * (vDry - vWet), 1f);
            if (target <= 0.0005f)
            {
                return false;
            }

            if (!ports.TryGetValue(cell, out VacuumLeakPort? port))
            {
                port = new VacuumLeakPort
                {
                    Cell = cell,
                    // Deterministic coordinate hash, NOT Rand.Value: the scan runs
                    // during Draw, and consuming the shared game RNG at frame rate
                    // would break RimWorld's determinism.
                    Seed = SeedFor(cell),
                };
                ports.Add(cell, port);

                VisualTweaksLog.LeakTrace(string.Concat(
                    "leak port new at ", cell.ToString(),
                    ": room ", wet.CellCount.ToString(), " cells, vWet ",
                    vWet.ToString("F2"), " -> vDry ", vDry.ToString("F2"),
                    ", target ", target.ToString("F2")));
            }

            port.Target = target;
            port.Seen = true;
            UpdateGeometry(port, map, wet, edifice);

            VisualTweaksLog.LeakTrace(string.Concat(
                "leak port ", cell.ToString(),
                ": gap ", port.GapCells.ToString(), " cells",
                ", target ", target.ToString("F3"),
                ", smoothed ", port.Smoothed.ToString("F3")));

            return true;
        }

        private void Retire(IntVec3 cell)
        {
            if (ports.TryGetValue(cell, out VacuumLeakPort? port))
            {
                port.Target = 0f;
            }
        }

        private static float SeedFor(IntVec3 cell)
        {
            return Mathf.Repeat((cell.x * 0.7131f + cell.z * 0.3717f) * 7.13f, 1f);
        }

        /// <summary>
        /// Geometry for one port, measured from the building's face against the wet
        /// room rather than from its anchor cell.
        ///
        /// The opening is the run of building cells that border the wet room: a
        /// one-cell door gives a run of one, a two-cell security gate gives two, and
        /// the quad is widened to match so the mask converges on a hole as wide as
        /// the hole actually is. Anchoring on Position instead would pin the whole
        /// stream to the building's south-west cell and draw a one-cell gap wherever
        /// the building happens to start — which is exactly what a two-cell gate
        /// looked like.
        ///
        /// Direction is the wet room's facing normal, not the room-centre-to-gap
        /// vector the analysis doc suggested: for a gap near the end of a long wall
        /// that vector points diagonally along the wall, while outflow is
        /// perpendicular to it. Faces are compared as whole runs rather than by
        /// summing normals, so an L-shaped contact picks the wider opening instead of
        /// averaging two perpendicular normals into a diagonal nobody wants to see.
        /// </summary>
        private static void UpdateGeometry(VacuumLeakPort port, Map map, Room wet, Building edifice)
        {
            CellRect rect = edifice.OccupiedRect();

            int face = -1;
            int span = 0;
            int lo = 0;
            int hi = 0;

            for (int d = 0; d < 4; d++)
            {
                IntVec3 normal = GenAdj.CardinalDirections[d];
                bool runAlongZ = normal.x != 0;

                int min = int.MaxValue;
                int max = int.MinValue;

                foreach (IntVec3 cell in rect)
                {
                    IntVec3 neighbour = cell + normal;
                    if (rect.Contains(neighbour) || !neighbour.InBounds(map))
                    {
                        continue;
                    }

                    if (neighbour.GetRoom(map) != wet)
                    {
                        continue;
                    }

                    int along = runAlongZ ? cell.z : cell.x;
                    if (along < min)
                    {
                        min = along;
                    }

                    if (along > max)
                    {
                        max = along;
                    }
                }

                // Longest run wins; on a tie the first face found keeps it, which is
                // arbitrary but stable, and a tie means two equal openings on one
                // building — either is a fair picture of where the air goes.
                if (max >= min && max - min + 1 > span)
                {
                    face = d;
                    span = max - min + 1;
                    lo = min;
                    hi = max;
                }
            }

            Vector3 centre;
            Vector3 direction;

            if (face < 0)
            {
                // Defensive only: the wet room was chosen from this building's
                // neighbours, so some face should always have matched.
                span = 1;
                centre = port.Cell.ToVector3Shifted();
                Vector3 away = centre - wet.ExtentsClose.CenterCell.ToVector3Shifted();
                away.y = 0f;
                direction = away.sqrMagnitude < 0.0001f ? Vector3.right : away.normalized;
            }
            else
            {
                IntVec3 normal = GenAdj.CardinalDirections[face];
                direction = new Vector3(-normal.x, 0f, -normal.z);

                // Across the run the quad is centred on the opening; along the flow
                // it sits on the face plane, the middle of the building's outermost
                // cell row.
                float across = (lo + hi + 1) * 0.5f;
                float plane = normal.z > 0 ? rect.maxZ + 0.5f
                    : normal.z < 0 ? rect.minZ + 0.5f
                    : normal.x > 0 ? rect.maxX + 0.5f
                    : rect.minX + 0.5f;

                centre = normal.x != 0
                    ? new Vector3(plane, 0f, across)
                    : new Vector3(across, 0f, plane);
            }

            port.GapCells = span;
            port.Center = centre;
            port.Direction = direction;

            // The margin is a fixed flare budget, not a per-cell one: a two-cell gate
            // gets the same 2.1 cells of flare room as a one-cell door, so the quad
            // grows with the opening instead of ballooning past it. Narrow is the
            // hole radius at the waist, so the stream is exactly as wide as the
            // opening where it passes through.
            port.QuadWidth = span + PortWidthMargin;
            port.Narrow = span / port.QuadWidth;

            // Farthest corner of the quad from the gap centre, for the view cull:
            // 2.5 cells along the flow, half the quad across.
            port.Reach = Mathf.Sqrt(2.5f * 2.5f + port.QuadWidth * 0.5f * port.QuadWidth * 0.5f);

            // Centre the strip so the gap falls inside it at PortGapV: local +Z
            // points along the flow, so v = 0 is inside the room and v = 1 is out
            // in space.
            Vector3 centreOfQuad = centre
                + direction * ((PortJetLength - StreakLength) * 0.5f);
            centreOfQuad.y = VacuumLeakRenderer.Altitude;

            port.Matrix = Matrix4x4.TRS(
                centreOfQuad,
                Quaternion.LookRotation(direction, Vector3.up),
                new Vector3(port.QuadWidth, 1f, PortLength));
            port.GapV = PortGapV;
            port.LenCells = PortLength;
        }
    }
}
