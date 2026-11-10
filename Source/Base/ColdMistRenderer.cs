using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using RimWorld;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Draws the low-temperature room mist.
    ///
    /// Every map owns one instance (through ColdMistLayer). The renderer keeps a
    /// single RGBA32 mask with one texel per map cell that stores the room
    /// temperature (R) and an indoor flag (A), plus a quad covering the part of the
    /// map currently on screen. One draw call then blends animated vapour over the
    /// cold cells; the vapour field churns in place (no advection), so the effect
    /// reads as cold air rather than as a cloud layer sliding past.
    ///
    /// Differences from HeatHazeRenderer, all deliberate:
    ///  * the mask spans the whole map instead of a resized window, because a
    ///    full-map RGBA32 texture is at most a few hundred KB and that removes the
    ///    capacity growth/shrink machinery entirely;
    ///  * cells are filled by walking the visible part of each cold room's regions
    ///    instead of sampling every cell, so the cost follows the cold interior on
    ///    screen rather than the zoom level, and no 2x2 stride is needed at far zoom;
    ///  * no scroll optimisation: because the mask is anchored in map space, a pan needs
    ///    no bookkeeping at all — the cells that stayed on screen are still valid, so only
    ///    the newly exposed strips are baked;
    ///  * the shader needs no scene grab, so there is no refraction path at all.
    /// </summary>
    internal sealed class ColdMistRenderer : IDisposable
    {
        // Vanilla re-equalises every room's temperature on ticks where TicksGame % 120 == 7
        // (MapTemperature.MapTemperatureTick). Baking faster repaints a mask that cannot
        // have changed; baking slower misses changes. One bake per 120 ticks therefore
        // produces the same picture for an eighth of the work at 1x, and still tracks the
        // data at 15x, where 120 ticks is only 0.13 s. The shader animates continuously
        // between bakes either way.
        private const int BakeIntervalTicks = 120;
        private const int TempEqualiseOffset = 7;

        // Hard ceiling on how often the mask is refreshed, whatever the speed tier. Above
        // 1 Hz nothing is perceptible: the vapour keeps animating in the shader between
        // bakes, and one bake per second is already eight times the rate at which room
        // temperature moves at 1x. It doubles as the cadence when there is no tick manager.
        private const float MaxRefreshInterval = 1f;

        // Extra cells baked on each side so the camera can pan a little without a
        // rebake, and so the bilinear filter stays inside baked data at the edges
        // of the visible area.
        private const int BakeMargin = 8;

        // Re-centre only after the view leaves a small dead band. CurrentViewRect is
        // Floor/Ceil of a moving float, so its width jitters by one cell while the
        // camera pans; a tight test would rebake on every frame at some zoom levels.
        private const int WindowSlack = 2;

        // Window dimensions are rounded up to this quantum, otherwise the one-cell
        // jitter in view.Width would also resize (and therefore recentre) the window.
        private const int WindowQuantum = 16;

        // Same queue as the heat haze: after the map visuals, before fog of war.
        private const int RenderQueue = 3174;

        private static readonly float Altitude =
            Altitudes.AltitudeFor(AltitudeLayer.Gas);

        private static readonly int ParamsId = Shader.PropertyToID("_Params");
        private static readonly int Params2Id = Shader.PropertyToID("_Params2");
        private static readonly int WindowId = Shader.PropertyToID("_Window");
        private static readonly int MaskId = Shader.PropertyToID("_ColdMask");

        private static readonly Vector3[] QuadVertices = new Vector3[4];

        private static readonly int[] QuadTriangles =
        {
            0, 1, 2,
            0, 2, 3,
        };

        // maskPixels starts as the shared empty array: an unallocated staging buffer is
        // genuinely "no pixels yet". The engine handles are nullable because EnsureResources
        // is the only place that can create them (the shader def is not loaded at
        // construction time); [MemberNotNull] on that method restores the guarantee for
        // every caller without a single extra null check in the draw path.
        private Color32[] maskPixels = Array.Empty<Color32>();
        private Texture2D? maskTexture;
        private Mesh? mesh;
        private Material? material;

        private int mapWidth;
        private int mapHeight;

        // Window whose cells are currently baked into maskPixels. It only covers the
        // view plus a margin; the rest of the texture is stale and is never sampled
        // because the quad does not reach it.
        private int winMinX;
        private int winMinZ;
        private int winW;
        private int winH;

        private int quadMinX = int.MinValue;
        private int quadMinZ = int.MinValue;
        private int quadWidth = -1;
        private int quadHeight = -1;

        // Window whose cells are actually valid in maskPixels. Normally equal to the window
        // above; it only differs when the window moved, at which point everything outside
        // the old rect is the part that has to be baked again.
        private int bakedMinX = int.MinValue;
        private int bakedMinZ = int.MinValue;
        private int bakedW = -1;
        private int bakedH = -1;

        // Window whose contents are on the GPU. The origin is part of the hash: the
        // mask is anchored in map space, so an identical-looking window one room
        // over still needs an upload.
        private uint uploadedHash = uint.MaxValue;

        private float shaderTime;
        private float lastBake = float.NegativeInfinity;
        private int lastBakeTick = -1;
        private float lastMinTemp = float.PositiveInfinity;
        private bool loggedDraw;
        private bool wasPaused;

        internal void Draw(Map map, CellRect view, bool paused, TimeSpeed speed, int tick)
        {
            // Zero opacity means there is nothing to draw, so do not even allocate for it.
            // Checked before EnsureResources for the same reason the heat haze checks its
            // intensity first: an effect the player has switched off should cost nothing.
            if (VisualTweaksSettings.coldMistOpacity <= 0.001f)
            {
                return;
            }

            EnsureResources(map);

            // Frozen while paused, faster with every game speed tier.
            OverlayClock.Advance(ref shaderTime, paused, speed);

            if (!EnsureWindow(map, view, out bool windowChanged))
            {
                return;
            }

            float realtime = Time.realtimeSinceStartup;

            // Two things can make the mask stale: the world moved, which happens on the
            // tick vanilla re-equalises room temperatures (so the bake follows the tick
            // clock, not the wall clock), or the window moved, because newly exposed cells
            // were never baked. Nothing else, which is why a paused game only bakes when
            // the camera pans and unpausing bakes once instead of waiting out the cadence.
            bool resumed = wasPaused && !paused;
            wasPaused = paused;
            bool cadenceDue = resumed || BakeDue(tick, realtime);

            // A pan leaves every cell that is still on screen valid, because the mask is
            // anchored in map space: only the exposed strips need work. A cadence bake, a
            // resize, a jump or the first bake ever rewrites the whole window instead.
            bool stripsOnly = windowChanged && !cadenceDue && CanBakeStrips();

            bool bakeDue = windowChanged || cadenceDue;
            if (paused && !windowChanged)
            {
                bakeDue = false;
            }

            if (bakeDue)
            {
                if (stripsOnly)
                {
                    BakeExposed(map);
                }
                else
                {
                    Bake(map);
                }

                lastBake = realtime;
                lastBakeTick = tick > 0 ? PhaseAlignedTick(tick) : -1;
            }

            if (!HasVisibleMist())
            {
                return;
            }

            EnsureQuad(mesh);

            float startNorm = Normalise(VisualTweaksSettings.coldMistStartTemp);
            float fullNorm = Normalise(VisualTweaksSettings.coldMistFullTemp);

            material.SetVector(
                ParamsId,
                new Vector4(
                    shaderTime,
                    Mathf.Clamp(VisualTweaksSettings.coldMistOpacity, 0f, 1f),
                    Mathf.Clamp(
                        VisualTweaksSettings.coldMistScale,
                        VisualTweaksSettings.MinColdScale,
                        VisualTweaksSettings.MaxColdScale),
                    Mathf.Clamp(
                        VisualTweaksSettings.coldMistSpeed,
                        VisualTweaksSettings.MinColdSpeed,
                        VisualTweaksSettings.MaxColdSpeed)));
            material.SetVector(
                Params2Id,
                new Vector4(
                    startNorm,
                    fullNorm,
                    Mathf.Clamp01(VisualTweaksSettings.coldMistCoverage),
                    0f));
            material.SetVector(
                WindowId,
                new Vector4(1f / mapWidth, 1f / mapHeight, 0f, 0f));
            material.SetTexture(MaskId, maskTexture);

            Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0);

            VisualTweaksLog.TraceOnce(
                ref loggedDraw,
                "Cold mist drawing " + winW + "x" + winH + " room-temperature window.");
        }

        /// <summary>Maps a Celsius value onto the mask's 0..1 range.</summary>
        private static float Normalise(float celsius)
        {
            return (celsius - VisualTweaksSettings.ColdTempNormMin)
                / (VisualTweaksSettings.ColdTempNormMax - VisualTweaksSettings.ColdTempNormMin);
        }

        /// <summary>
        /// True when a bake is due on the game clock. The wall-clock ceiling comes first so
        /// it applies to every speed tier; <c>tick &lt; lastBakeTick</c> covers a loaded save,
        /// whose clock is not the one this renderer was counting on.
        /// </summary>
        private bool BakeDue(int tick, float realtime)
        {
            if (realtime - lastBake < MaxRefreshInterval)
            {
                return false;
            }

            if (tick <= 0)
            {
                // No tick manager to follow: the ceiling above is the whole cadence.
                return true;
            }

            return lastBakeTick < 0
                || tick < lastBakeTick
                || tick - lastBakeTick >= BakeIntervalTicks;
        }

        /// <summary>
        /// Snaps a tick back to the last vanilla temperature pass, so the cadence keeps
        /// phase with the data instead of drifting: the next bake then lands on the tick
        /// right after vanilla's next equalise pass, never on an arbitrary offset.
        /// </summary>
        private static int PhaseAlignedTick(int tick)
        {
            int sinceEqualise = (tick - TempEqualiseOffset) % BakeIntervalTicks;
            if (sinceEqualise < 0)
            {
                sinceEqualise += BakeIntervalTicks;
            }

            return tick - sinceEqualise;
        }

        private bool HasVisibleMist()
        {
            return lastMinTemp < VisualTweaksSettings.coldMistStartTemp;
        }

        // maskPixels is intentionally left out: it is a real buffer with a non-null
        // default, while the three engine handles only exist after this call.
        [MemberNotNull(nameof(maskTexture), nameof(mesh), nameof(material))]
        private void EnsureResources(Map map)
        {
            if (mesh == null)
            {
                mesh = new Mesh
                {
                    name = "VisualTweaksColdMistQuad"
                };
                mesh.MarkDynamic();
            }

            if (material == null)
            {
                ShaderDef? def = ColdMistBridge.ColdMistShaderDef;
                if (def == null)
                {
                    throw new InvalidOperationException(
                        "Cold mist shader def is not available.");
                }

                material = new Material((Shader)def)
                {
                    name = "VisualTweaksColdMistMat",
                    renderQueue = RenderQueue,
                };
            }

            if (maskTexture == null)
            {
                mapWidth = map.Size.x;
                mapHeight = map.Size.z;
                maskTexture = new Texture2D(
                    mapWidth,
                    mapHeight,
                    TextureFormat.RGBA32,
                    false,
                    true)
                {
                    name = "VisualTweaksColdMistMask",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
            }

            if (maskPixels.Length == 0)
            {
                maskPixels = new Color32[mapWidth * mapHeight];

                // Start from an empty mask so nothing cold can be drawn before the first
                // bake has run.
                maskTexture.SetPixels32(maskPixels);
                maskTexture.Apply(false, false);
            }
        }

        /// <summary>
        /// Picks the window around the current view. Returns false only when the view
        /// is empty (for example while the map is being torn down).
        /// <paramref name="changed"/> is true when the window origin or size moved,
        /// which is the only case that requires an immediate rebake.
        /// </summary>
        private bool EnsureWindow(Map map, CellRect view, out bool changed)
        {
            changed = false;
            CellRect clipped = view.ClipInsideMap(map);
            if (clipped.Width <= 0 || clipped.Height <= 0)
            {
                return false;
            }

            int wantW = Mathf.Max(
                1,
                Mathf.Min(
                    mapWidth,
                    RoundUp(clipped.Width + 2 * BakeMargin, WindowQuantum)));
            int wantH = Mathf.Max(
                1,
                Mathf.Min(
                    mapHeight,
                    RoundUp(clipped.Height + 2 * BakeMargin, WindowQuantum)));

            bool sizeChanged =
                winW <= 0
                || winH <= 0
                || wantW > winW
                || wantW + WindowQuantum < winW
                || wantH > winH
                || wantH + WindowQuantum < winH;

            bool outsideX =
                clipped.minX < winMinX + WindowSlack
                || clipped.maxX > winMinX + winW - 1 - WindowSlack;
            bool outsideZ =
                clipped.minZ < winMinZ + WindowSlack
                || clipped.maxZ > winMinZ + winH - 1 - WindowSlack;

            if (!sizeChanged && !outsideX && !outsideZ)
            {
                return true;
            }

            int newW = sizeChanged ? wantW : winW;
            int newH = sizeChanged ? wantH : winH;

            // Centre the window on the view, then clamp it inside the map. Recentre
            // only the axes that need it: at a map edge one axis is permanently
            // "outside" and recomputing the other every frame would force a rebake on
            // every cell of travel along that edge.
            int newMinX = winMinX;
            int newMinZ = winMinZ;
            if (sizeChanged || outsideX)
            {
                int centredX = clipped.minX - (newW - clipped.Width) / 2;
                newMinX = Mathf.Clamp(centredX, 0, Mathf.Max(0, mapWidth - newW));
            }

            if (sizeChanged || outsideZ)
            {
                int centredZ = clipped.minZ - (newH - clipped.Height) / 2;
                newMinZ = Mathf.Clamp(centredZ, 0, Mathf.Max(0, mapHeight - newH));
            }

            // A clamped window can land on the same origin (map edge, or a stationary
            // camera). Only a real origin or size change invalidates the bake.
            if (!sizeChanged && newMinX == winMinX && newMinZ == winMinZ)
            {
                return true;
            }

            winW = newW;
            winH = newH;
            winMinX = newMinX;
            winMinZ = newMinZ;

            quadWidth = -1;
            changed = true;
            return true;
        }

        private static int RoundUp(int value, int quantum)
        {
            if (value <= 0)
            {
                return quantum;
            }

            return (value + quantum - 1) / quantum * quantum;
        }

        /// <summary>
        /// Refills the whole window, then uploads if anything changed. This is the cadence
        /// path: a room temperature may have moved anywhere in the window, so no cell can
        /// be reused.
        /// </summary>
        private void Bake(Map map)
        {
            EnsureResources(map);

            lastMinTemp = BakeRect(map, new CellRect(winMinX, winMinZ, winW, winH));
            MarkBaked();

            // Nothing is cold enough to draw: leave the previous texture alone and skip the
            // upload. The next bake that crosses the threshold refreshes it before the
            // shader is allowed to draw.
            if (!HasVisibleMist())
            {
                return;
            }

            uint hash = HashWindow(maskPixels);
            if (hash == uploadedHash)
            {
                return;
            }

            uploadedHash = hash;
            UploadMask();
        }

        /// <summary>
        /// Refills only the strips a pan exposed.
        ///
        /// The mask is anchored in map space, so every cell that stayed on screen already
        /// holds the right value and rebaking it would rewrite identical pixels. At most two
        /// strips — one per moved axis — are what actually changed. The heat haze cannot do
        /// this: its mask lives in window-local coordinates, so a pan has to scroll the whole
        /// buffer first and then patch the strips.
        /// </summary>
        private void BakeExposed(Map map)
        {
            int dx = winMinX - bakedMinX;
            int dz = winMinZ - bakedMinZ;

            float minTemp = float.PositiveInfinity;

            if (dx != 0)
            {
                int x = dx > 0 ? bakedMinX + bakedW : winMinX;
                minTemp = Mathf.Min(
                    minTemp,
                    BakeRect(map, new CellRect(x, winMinZ, Mathf.Abs(dx), winH)));
            }

            if (dz != 0)
            {
                int z = dz > 0 ? bakedMinZ + bakedH : winMinZ;
                minTemp = Mathf.Min(
                    minTemp,
                    BakeRect(map, new CellRect(winMinX, z, winW, Mathf.Abs(dz))));
            }

            MarkBaked();

            // A strip that found nothing cold must not close the gate the rest of the window
            // is holding open. Keeping the stale lower value is harmless — those fragments
            // discard — and the next cadence bake recomputes the real minimum.
            if (minTemp < float.PositiveInfinity)
            {
                lastMinTemp = Mathf.Min(lastMinTemp, minTemp);
            }

            // These cells changed for certain, so there is no hash worth computing to prove
            // it and the GPU copy is stale either way. Marking it unknown makes the next
            // cadence bake re-decide instead of trusting a hash that no longer describes
            // the texture.
            uploadedHash = uint.MaxValue;
            if (HasVisibleMist())
            {
                UploadMask();
            }
        }

        private void UploadMask()
        {
            // Reachable only from the bake paths, which EnsureResources has already run, so
            // the texture exists; the null-forgiving read is what keeps the draw path free
            // of a check that can never fire.
            Texture2D texture = maskTexture!;
            texture.SetPixels32(maskPixels);
            texture.Apply(false, false);
        }

        private void MarkBaked()
        {
            bakedMinX = winMinX;
            bakedMinZ = winMinZ;
            bakedW = winW;
            bakedH = winH;
        }

        /// <summary>
        /// True when the mask already holds valid data for a same-sized window that still
        /// overlaps the current one, so a pan can be patched instead of rebaked.
        /// </summary>
        private bool CanBakeStrips()
        {
            return bakedW > 0
                && bakedH > 0
                && winW == bakedW
                && winH == bakedH
                && (winMinX != bakedMinX || winMinZ != bakedMinZ)
                && Mathf.Abs(winMinX - bakedMinX) < winW
                && Mathf.Abs(winMinZ - bakedMinZ) < winH;
        }

        /// <summary>
        /// Clears <paramref name="dirty"/> and refills it from vanilla room data, returning
        /// the lowest temperature it actually wrote (+infinity when it wrote nothing).
        ///
        /// Only enclosed interiors contribute: a room that uses the outdoor temperature (a
        /// courtyard, a ruin, anything with more than a quarter of its ceiling missing) is
        /// treated as outdoors and left alone, and so is a room exposed to vacuum. This is
        /// exactly the "vanilla room cold, no outdoors" scope the effect is meant to cover,
        /// and it keeps AO's own mist system untouched because nothing here reads AO's
        /// fields.
        /// </summary>
        private float BakeRect(Map map, CellRect dirty)
        {
            ClearRect(dirty);

            float startTemp = VisualTweaksSettings.coldMistStartTemp;
            float minTemp = float.PositiveInfinity;

            IReadOnlyList<Room> rooms = map.regionGrid.AllRooms;
            RegionGrid regionGrid = map.regionGrid;

            // The mask as 32-bit words, so a run of identical cells is one Fill instead of
            // one store per cell. Same view the heat haze uses; the texture upload still
            // takes the Color32 array.
            Span<uint> words = MemoryMarshal.Cast<Color32, uint>(maskPixels.AsSpan());

            // Mist in a vacuum would be nonsense, and space maps are AO's business.
            bool checkVacuum = map.Biome != null && map.Biome.inVacuum;

            for (int i = 0; i < rooms.Count; i++)
            {
                Room room = rooms[i];
                if (room == null || room.Dereferenced)
                {
                    continue;
                }

                if (room.UsesOutdoorTemperature)
                {
                    continue;
                }

                if (checkVacuum && room.ExposedToSpace)
                {
                    continue;
                }

                float temp = room.Temperature;
                if (temp >= startTemp)
                {
                    continue;
                }

                byte value = EncodeTemperature(temp);
                Color32 packed = new Color32(value, 0, 0, 255);

                // Region-driven, not room-driven. Region.Cells walks the region's
                // *bounding rectangle* and keeps the cells that resolve back to it, and
                // Room.Cells/District.Cells wrap that in two more yield iterators. Doing
                // the walk here instead costs the same membership test vanilla does, but
                // only over the part of the rectangle that is on screen, and without the
                // iterators. Measured in Tools/ColdMistBakeBench: ~2 ns/cell against
                // ~16 ns/cell through Room.Cells on the same data, plus every cell of a
                // half-visible room that used to be tested and thrown away.
                List<Region> regions = room.Regions;
                bool wrote = false;
                for (int r = 0; r < regions.Count; r++)
                {
                    Region region = regions[r];
                    CellRect extents = region.extentsClose;

                    int minX = Mathf.Max(extents.minX, dirty.minX);
                    int minZ = Mathf.Max(extents.minZ, dirty.minZ);
                    int maxX = Mathf.Min(extents.maxX, dirty.maxX);
                    int maxZ = Mathf.Min(extents.maxZ, dirty.maxZ);
                    if (minX > maxX || minZ > maxZ)
                    {
                        continue;
                    }

                    // A rectangular region *is* its extents, so no membership test is
                    // needed and whole rows can be filled at memset speed. This is the
                    // same trade the heat haze makes on rectangular rooms; doing it per
                    // region rather than per room catches the common case where an
                    // irregular room is still cut into rectangular regions.
                    if (region.CellCount == extents.Width * extents.Height)
                    {
                        uint packedWord = PackWord(packed);
                        for (int z = minZ; z <= maxZ; z++)
                        {
                            int row = (mapHeight - 1 - z) * mapWidth + minX;
                            words.Slice(row, maxX - minX + 1).Fill(packedWord);
                        }

                        wrote = true;
                        continue;
                    }

                    for (int z = minZ; z <= maxZ; z++)
                    {
                        int row = (mapHeight - 1 - z) * mapWidth;
                        for (int x = minX; x <= maxX; x++)
                        {
                            if (regionGrid.GetRegionAt_NoRebuild_InvalidAllowed(new IntVec3(x, 0, z)) != region)
                            {
                                continue;
                            }

                            maskPixels[row + x] = packed;
                            wrote = true;
                        }
                    }
                }

                // Only a room that actually painted something counts towards "is
                // anything visible". A cold room on the far side of the map must not
                // keep the quad alive: every fragment of it would discard.
                if (wrote && temp < minTemp)
                {
                    minTemp = temp;
                }
            }

            return minTemp;
        }

        private static byte EncodeTemperature(float celsius)
        {
            float norm = Normalise(celsius);
            return (byte)Mathf.Clamp(Mathf.RoundToInt(norm * 255f), 0, 255);
        }

        /// <summary>
        /// The same cell as one 32-bit word. Color32 lays its bytes out as r, g, b, a, so on
        /// little-endian the word reads r | g &lt;&lt; 8 | b &lt;&lt; 16 | a &lt;&lt; 24. Only r
        /// and a carry data here, and a row fill overwrites whole cells, so both come from
        /// the packed value and nothing stale survives.
        /// </summary>
        private static uint PackWord(Color32 cell)
        {
            return (uint)cell.r
                | ((uint)cell.g << 8)
                | ((uint)cell.b << 16)
                | ((uint)cell.a << 24);
        }

        /// <summary>
        /// Zeroes a rectangle. A cell that lost its room (a wall built inside it, a roof
        /// removed) must read as "no room" rather than keep an old temperature.
        /// </summary>
        private void ClearRect(CellRect rect)
        {
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                int row = (mapHeight - 1 - z) * mapWidth + rect.minX;
                Array.Clear(maskPixels, row, rect.Width);
            }
        }

        /// <summary>
        /// FNV-1a over the window plus its origin. The origin is part of the hash
        /// because the mask is anchored in map space: two identical-looking windows
        /// one room apart must still be uploaded, while a static camera over static
        /// temperatures hashes the same and skips the upload entirely.
        ///
        /// One 32-bit read per cell instead of a Color32 fetch plus two shifts. The whole
        /// word is hashed rather than just r and a: every byte of a cell is written either
        /// by the row fill (all four from the packed value) or by the clear (all four zero),
        /// so g and b can never be stale.
        /// </summary>
        private uint HashWindow(Color32[] maskPixels)
        {
            uint hash = 2166136261u;
            hash = (hash ^ (uint)winMinX) * 16777619u;
            hash = (hash ^ (uint)winMinZ) * 16777619u;
            hash = (hash ^ (uint)winW) * 16777619u;
            hash = (hash ^ (uint)winH) * 16777619u;

            ReadOnlySpan<uint> words = MemoryMarshal.Cast<Color32, uint>(maskPixels.AsSpan());
            for (int z = 0; z < winH; z++)
            {
                int row = (mapHeight - 1 - (winMinZ + z)) * mapWidth + winMinX;
                ReadOnlySpan<uint> line = words.Slice(row, winW);
                for (int x = 0; x < winW; x++)
                {
                    hash = (hash ^ line[x]) * 16777619u;
                }
            }

            return hash;
        }

        private void EnsureQuad(Mesh mesh)
        {
            if (quadMinX == winMinX
                && quadMinZ == winMinZ
                && quadWidth == winW
                && quadHeight == winH)
            {
                return;
            }

            quadMinX = winMinX;
            quadMinZ = winMinZ;
            quadWidth = winW;
            quadHeight = winH;

            QuadVertices[0] = new Vector3(winMinX, Altitude, winMinZ);
            QuadVertices[1] = new Vector3(winMinX, Altitude, winMinZ + winH);
            QuadVertices[2] = new Vector3(winMinX + winW, Altitude, winMinZ + winH);
            QuadVertices[3] = new Vector3(winMinX + winW, Altitude, winMinZ);

            mesh.Clear(false);
            mesh.SetVertices(QuadVertices);
            mesh.SetTriangles(QuadTriangles, 0);
            mesh.bounds = new Bounds(
                new Vector3(
                    winMinX + winW * 0.5f,
                    Altitude,
                    winMinZ + winH * 0.5f),
                new Vector3(winW, 16f, winH));
        }

        public void Dispose()
        {
            if (maskTexture != null)
            {
                UnityEngine.Object.Destroy(maskTexture);
                maskTexture = null;
            }

            if (material != null)
            {
                UnityEngine.Object.Destroy(material);
                material = null;
            }

            if (mesh != null)
            {
                UnityEngine.Object.Destroy(mesh);
                mesh = null;
            }

            maskPixels = Array.Empty<Color32>();
            mapWidth = 0;
            mapHeight = 0;
            uploadedHash = uint.MaxValue;
            quadWidth = -1;
            quadHeight = -1;
            quadMinX = int.MinValue;
            quadMinZ = int.MinValue;
            bakedMinX = int.MinValue;
            bakedMinZ = int.MinValue;
            bakedW = -1;
            bakedH = -1;
            winW = 0;
            winH = 0;
            lastBake = float.NegativeInfinity;
            lastBakeTick = -1;
            lastMinTemp = float.PositiveInfinity;
            wasPaused = false;
        }
    }
}
