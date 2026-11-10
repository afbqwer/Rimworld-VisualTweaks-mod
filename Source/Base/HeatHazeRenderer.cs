using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using RimWorld;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Draws the high-temperature air shimmer.
    ///
    /// Every map owns one instance (through HeatHazeLayer). The renderer keeps a
    /// dynamically sized rectangular RGBA32 mask that stores each visible cell's
    /// temperature (R) and a direct fire hotspot (G), plus a quad that covers the mask
    /// window. A single draw call then resamples the scene grab through animated noise
    /// in the shader.
    ///
    /// The mask is independent of the screen resolution: one texel per cell. Capacity
    /// follows the view rect, grows geometrically when a zoom-out needs more cells, and
    /// is reclaimed after a sustained zoom-in, so normal play uploads tens of KB instead
    /// of a fixed 1 MB texture.
    ///
    /// Temperatures come from one of two sources, and which one is a user setting rather
    /// than a property of the install. With per-cell sampling on (only offered when AO is
    /// present) AO's field is read cell by cell. Otherwise the room walk runs: vanilla has
    /// no per-cell field at all — it resolves a room per lookup and returns that room's
    /// single temperature — so walking the rooms and writing each one across its footprint
    /// produces the same mask without paying for a region lookup on every cell.
    /// </summary>
    internal sealed class HeatHazeRenderer : IDisposable
    {
        // Room temperature changes slowly; 5 Hz is far more than enough and keeps the
        // bake (and the texture upload) cheap. The shader animates continuously between
        // bakes.
        private const float BakeInterval = 0.2f;

        // Above this many cells the temperature mask is sampled at half resolution and
        // baked half as often: at the furthest zoom one texel already spans several
        // screen pixels and the air temperature there moves too slowly to notice.
        private const int WideWindowCells = 24000;

        private const float WideBakeInterval = 0.5f;

        // While the whole window is well below the start temperature and nothing in it can
        // heat up, the next bake cannot cross the threshold, so it runs this rarely instead.
        // A temperate map otherwise pays for a window scan and a room walk five times a
        // second to draw nothing at all.
        //
        // The price is that a torch lit in a cold room shows its shimmer one interval late.
        // That reads as the air warming up rather than as latency, and a source that is
        // already burning keeps the fast cadence below, so only the ignition itself waits.
        // This is deliberate: do not "fix" it by shortening the interval.
        private const float ColdBakeInterval = 2f;

        // Rooms within this margin of the start temperature keep the fast cadence, so
        // anything already on its way to hot ramps in without a delay.
        private const float ColdBackoffMargin = 3f;

        // Low-frequency mode, and the default. The shader animates continuously between
        // bakes, so a slower cadence shows up as the shimmer catching up to a temperature
        // change a couple of seconds late, not as a freeze — and late is the more believable
        // of the two: air has to absorb the heat before it starts to rise. It replaces every
        // interval above, the cold backoff included, so ResolveBakeInterval returns it
        // before paying for any of the hot-window tests.
        private const float LowFrequencyBakeInterval = 2f;

        // Extra cells baked on each side so the camera can pan a little without a
        // rebake, and so the bilinear filter has valid samples at the view edge.
        private const int BakeMargin = 8;

        // Re-centre only after the view leaves a small dead band. CurrentViewRect is
        // Floor/Ceil of a moving float, so its width jitters by one cell while the
        // camera pans; a tight test would rebake every frame at some zoom levels.
        private const int WindowSlack = 2;

        // Window dimensions are rounded up to this quantum. Without it the one-cell
        // jitter in view.Width would also resize (and therefore recentre) the mask.
        private const int WindowQuantum = 16;

        // Only shrink a geometrically grown capacity after this many rendered frames of
        // at least a 2x surplus. Reallocating is a GPU image rebuild, so it must be much
        // rarer than the zoom gesture that caused the surplus.
        private const int ShrinkDelayFrames = 180;

        // A heater is not in the heat-pusher family at all: Building_Heater never puts heat
        // into the air, it moves its room's single temperature value. The G channel
        // therefore never saw one, and the room walk stays under the start threshold at the
        // 21 C a heater is set to. While it is actually running, stamp a small plume at the
        // unit so the hot air is visible where it comes out.
        //
        // Deliberately weaker and tighter than a campfire (21 heat/s -> strength 1.0 over
        // 4.25 cells): 21 energy/s spread across a whole room is not the same local flux as
        // 21 heat/s dumped into the air at one cell.
        private const float HeaterPlumeRadius = 2.1f;
        private const float HeaterPlumeStrength = 0.4f;

        // The change hash samples every HashStride-th cell in both axes. It exists to decide
        // whether a texture upload can be skipped, not to compare the mask exactly, so it only
        // has to see changes that the eye would notice. Every heat source covers at least a
        // 3x3 footprint and room temperatures move in whole regions, so nothing meaningful
        // falls between the sample points, while the pass drops to a quarter of the reads and
        // sub-cell flicker stops forcing a 96 KB upload.
        private const int HashStride = 2;

        private const int RenderQueue = 3174;

        private static readonly float Altitude =
            Altitudes.AltitudeFor(AltitudeLayer.Gas);

        private static readonly int ParamsId = Shader.PropertyToID("_Params");
        private static readonly int Params2Id = Shader.PropertyToID("_Params2");
        private static readonly int WindowId = Shader.PropertyToID("_Window");
        private static readonly int MaskId = Shader.PropertyToID("_HeatMask");

        private static readonly Vector3[] QuadVertices = new Vector3[4];

        private static readonly int[] QuadTriangles =
        {
            0, 1, 2,
            0, 2, 3,
        };

        private Color32[] maskPixels = Array.Empty<Color32>();
        // The mask texture is (re)created by ResizeCapacity, mesh/material by
        // EnsureResources. Nullable is honest here; [MemberNotNull] on those two methods
        // restores the guarantee for callers without a null check in the draw path.
        private Texture2D? maskTexture;
        private Mesh? mesh;
        private Material? material;

        // Texture/array size in cells. Grows geometrically, and shrinks only after a
        // sustained surplus on both axes.
        private int capacityW;
        private int capacityH;
        private int capacitySurplusFrames;

        // Hash of the window last uploaded (origin and size included), so an unchanged
        // mask can skip the upload (Waylith's Apply() disposes and recreates the whole GPU
        // image). uint.MaxValue is "nothing uploaded yet" and cannot be produced by FNV-1a
        // over a real window.
        private uint sentHash = uint.MaxValue;

        // Window whose cells are currently baked into maskPixels. Used to scroll the
        // mask when the camera pans and to bake only the newly exposed strips. Only the
        // per-cell path uses this; the room walk always rewrites the whole window.
        private int bakedMinX = int.MinValue;
        private int bakedMinZ = int.MinValue;
        private int bakedW = -1;
        private int bakedH = -1;

        // Which source produced the cells currently in the mask. A source switch makes the
        // cached rect useless: the room walk leaves per-room plateaus across the window, and
        // scrolling those into the per-cell path would keep them in view until the camera
        // moved again.
        private bool bakedPerCellMode;

        private int winMinX;
        private int winMinZ;
        private int winW;
        private int winH;

        private int quadMinX = int.MinValue;
        private int quadMinZ = int.MinValue;
        private int quadWidth = -1;
        private int quadHeight = -1;

        private float shaderTime;
        private float lastBake = float.NegativeInfinity;
        private float bakeInterval = BakeInterval;
        private float lastMaxTempC;
        private float lastMaxFire;
        private bool loggedDraw;
        private bool wasPaused;

        internal void Draw(Map map, CellRect view, bool paused, TimeSpeed speed)
        {
            // No displacement means no reason to pay for the scene grab, the bake, or even
            // the mesh and material. Checked before EnsureResources so a disabled or
            // zero-strength effect allocates nothing at all.
            if (VisualTweaksSettings.heatHazeIntensity <= 0.001f
                || VisualTweaksSettings.heatHazeAmplitude <= 0.001f)
            {
                return;
            }

            EnsureResources();

            // Frozen while paused, faster with every game speed tier.
            OverlayClock.Advance(ref shaderTime, paused, speed);

            bool windowChanged;
            if (!EnsureWindow(map, view, out windowChanged))
            {
                return;
            }

            EnsureMaskTexture();

            float realtime = Time.realtimeSinceStartup;
            bakeInterval = ResolveBakeInterval(map);

            // Nothing in the world changes while paused, so only a window move needs a
            // fresh bake (the mask is anchored to the window origin). Unpausing always
            // bakes once instead of waiting for the next cadence.
            bool resumed = wasPaused && !paused;
            wasPaused = paused;

            // A source switch must show up immediately: the user just clicked it in the
            // settings window, and the old mask describes an entirely different quantity.
            bool modeChanged = VisualTweaksSettings.UsePerCellTemperature != bakedPerCellMode;

            bool bakeDue = windowChanged
                || resumed
                || modeChanged
                || realtime - lastBake >= bakeInterval;
            if (paused && !windowChanged && !modeChanged)
            {
                bakeDue = false;
            }

            if (bakeDue)
            {
                Bake(map);
                lastBake = realtime;
            }

            if (!HasVisibleHeat())
            {
                return;
            }

            EnsureQuad(mesh);

            float startNorm = Mathf.Clamp01(
                VisualTweaksSettings.heatHazeStartTemp / VisualTweaksSettings.HeatTempNormMax);
            float fullNorm = Mathf.Clamp01(
                VisualTweaksSettings.heatHazeFullTemp / VisualTweaksSettings.HeatTempNormMax);
            if (fullNorm <= startNorm)
            {
                fullNorm = Mathf.Min(1f, startNorm + 0.005f);
            }

            material.SetVector(
                ParamsId,
                new Vector4(
                    shaderTime,
                    Mathf.Clamp(VisualTweaksSettings.heatHazeIntensity, 0f, 3f),
                    Mathf.Clamp(VisualTweaksSettings.heatHazeScale, 0.05f, 5f),
                    Mathf.Clamp(
                        VisualTweaksSettings.heatHazeSpeed,
                        VisualTweaksSettings.MinHeatSpeed,
                        VisualTweaksSettings.MaxHeatSpeed)));
            material.SetVector(
                Params2Id,
                new Vector4(
                    startNorm,
                    fullNorm,
                    Mathf.Clamp(VisualTweaksSettings.heatHazeFireBoost, 0f, 2f),
                    Mathf.Clamp(VisualTweaksSettings.heatHazeAmplitude, 0f, 16f)));
            material.SetVector(
                WindowId,
                new Vector4(
                    1f / capacityW,
                    1f / capacityH,
                    -winMinX / (float)capacityW,
                    -winMinZ / (float)capacityH));
            material.SetTexture(MaskId, maskTexture);

            Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0);

            VisualTweaksLog.TraceOnce(
                ref loggedDraw,
                "Heat haze drawing " + winW + "x" + winH + " temperature mask.");
        }

        private bool HasVisibleHeat()
        {
            float start = VisualTweaksSettings.heatHazeStartTemp;
            return lastMaxTempC >= start
                || (lastMaxFire > 0.02f && VisualTweaksSettings.heatHazeFireBoost > 0.01f);
        }

        /// <summary>
        /// Bake cadence for this frame. The last bake measured the whole window, so when it
        /// found nothing within a few degrees of the start temperature and nothing in the
        /// window can heat up in the meantime (no fire, no already-burning source, cold
        /// outdoor air), the next bake cannot change the outcome.
        ///
        /// Low-frequency mode answers before any of that: one cadence for every situation
        /// costs a single branch per frame and skips both dictionary lookups.
        /// </summary>
        private float ResolveBakeInterval(Map map)
        {
            if (!VisualTweaksSettings.heatHazeHighFrequency)
            {
                return LowFrequencyBakeInterval;
            }

            float start = VisualTweaksSettings.heatHazeStartTemp;
            bool maybeHot = lastMaxTempC >= start - ColdBackoffMargin
                || lastMaxFire > 0.02f
                || map.mapTemperature.OutdoorTemp >= start - ColdBackoffMargin
                || map.listerThings.AnyThingWithDef(ThingDefOf.Fire);

            if (!maybeHot)
            {
                return ColdBakeInterval;
            }

            return winW * winH > WideWindowCells ? WideBakeInterval : BakeInterval;
        }

        [MemberNotNull(nameof(mesh), nameof(material))]
        private void EnsureResources()
        {
            if (mesh == null)
            {
                mesh = new Mesh
                {
                    name = "VisualTweaksHeatHazeQuad"
                };
                mesh.MarkDynamic();
            }

            if (material == null)
            {
                ShaderDef? def = HeatHazeBridge.HeatHazeShaderDef;
                if (def == null)
                {
                    throw new InvalidOperationException(
                        "Heat haze shader def is not available.");
                }

                material = new Material((Shader)def)
                {
                    name = "VisualTweaksHeatHazeMat",
                    renderQueue = RenderQueue,
                };
            }
        }

        [MemberNotNull(nameof(maskTexture))]
        private void CreateMaskTexture()
        {
            maskTexture = new Texture2D(
                capacityW,
                capacityH,
                TextureFormat.RGBA32,
                false,
                true)
            {
                name = "VisualTweaksHeatHazeMask",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            // Explicitly clear the new texture: the shader must see a cold mask (not
            // whatever the driver left in the allocation) before the first hot bake.
            maskTexture.SetPixels32(maskPixels);
            maskTexture.Apply(false, false);
        }

        [MemberNotNull(nameof(maskTexture))]
        private void EnsureMaskTexture()
        {
            if (maskTexture == null)
            {
                CreateMaskTexture();
            }
        }

        /// <summary>
        /// Makes the mask texture/array cover the requested window. Growth is geometric,
        /// so a zoom-out gesture allocates a couple of textures at most. Returns true when
        /// the capacity changed, which invalidates every cached rect and uploaded window.
        /// </summary>
        private bool EnsureCapacity(int needW, int needH, Map map)
        {
            if (capacityW >= needW && capacityH >= needH)
            {
                return false;
            }

            int newW = Mathf.Max(needW, capacityW * 2);
            int newH = Mathf.Max(needH, capacityH * 2);
            ResizeCapacity(newW, newH, map);
            return true;
        }

        /// <summary>
        /// Reallocates the mask arrays and texture. The existing contents describe the old
        /// layout, so every cached rect is dropped and the caller must full-bake.
        /// </summary>
        private void ResizeCapacity(int wantW, int wantH, Map map)
        {
            capacityW = Mathf.Max(1, Mathf.Min(wantW, map.Size.x));
            capacityH = Mathf.Max(1, Mathf.Min(wantH, map.Size.z));
            capacitySurplusFrames = 0;

            maskPixels = new Color32[capacityW * capacityH];
            sentHash = uint.MaxValue;

            if (maskTexture != null)
            {
                UnityEngine.Object.Destroy(maskTexture);
                maskTexture = null;
            }

            CreateMaskTexture();

            bakedMinX = int.MinValue;
            bakedMinZ = int.MinValue;
            bakedW = -1;
            bakedH = -1;
            quadWidth = -1;
        }

        /// <summary>
        /// Picks the mask window around the current view. Returns false only when the
        /// view is empty (for example while the map is being torn down). <paramref
        /// name="changed"/> is true when the window origin/size moved or the mask
        /// capacity grew, which is the only case that requires an immediate rebake.
        /// </summary>
        private bool EnsureWindow(Map map, CellRect view, out bool changed)
        {
            changed = false;
            CellRect clipped = view.ClipInsideMap(map);
            if (clipped.Width <= 0 || clipped.Height <= 0)
            {
                return false;
            }

            int wantW = Mathf.Min(
                map.Size.x,
                RoundUp(clipped.Width + 2 * BakeMargin, WindowQuantum));
            int wantH = Mathf.Min(
                map.Size.z,
                RoundUp(clipped.Height + 2 * BakeMargin, WindowQuantum));
            wantW = Mathf.Max(1, wantW);
            wantH = Mathf.Max(1, wantH);

            if (EnsureCapacity(wantW, wantH, map))
            {
                // The texture layout changed; the next bake must be a full one.
                changed = true;
            }

            bool sizeChanged =
                winW <= 0
                || winH <= 0
                || wantW > winW
                || wantW + WindowQuantum < winW
                || wantH > winH
                || wantH + WindowQuantum < winH;

            // A zoom-out leaves a geometrically grown capacity behind. Once the camera
            // has settled at a much smaller window for a few seconds, reclaim the surplus;
            // reallocating during the zoom gesture itself would fight the growth path.
            if (winW > 0
                && winH > 0
                && wantW <= winW
                && wantH <= winH
                && (capacityW > winW * 2 || capacityH > winH * 2))
            {
                if (++capacitySurplusFrames >= ShrinkDelayFrames)
                {
                    // Shrink to the *current* window, not `want`. When sizeChanged is
                    // false the window keeps its existing winW/winH, and capacity must
                    // never fall below that.
                    ResizeCapacity(
                        capacityW > winW * 2 ? winW : capacityW,
                        capacityH > winH * 2 ? winH : capacityH,
                        map);
                    changed = true;
                }
            }
            else
            {
                capacitySurplusFrames = 0;
            }

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

            // Centre the window on the view, then clamp it inside the map.
            //
            // Recentre only the axes that need it. At a map edge one axis is
            // permanently "outside" (the view is clipped and the window is pinned), but
            // recomputing the other axis every frame would move it with the camera and
            // force a rebake on every cell of travel along that edge.
            int newMinX = winMinX;
            int newMinZ = winMinZ;
            if (sizeChanged || outsideX)
            {
                int centredX = clipped.minX - (newW - clipped.Width) / 2;
                newMinX = Mathf.Clamp(centredX, 0, Mathf.Max(0, map.Size.x - newW));
            }

            if (sizeChanged || outsideZ)
            {
                int centredZ = clipped.minZ - (newH - clipped.Height) / 2;
                newMinZ = Mathf.Clamp(centredZ, 0, Mathf.Max(0, map.Size.z - newH));
            }

            // A clamped window can land on the same origin (map edge, or a stationary
            // camera). Only a real origin/size change invalidates the baked mask;
            // otherwise this would rebake every frame for nothing.
            if (!sizeChanged && newMinX == winMinX && newMinZ == winMinZ)
            {
                return true;
            }

            winW = newW;
            winH = newH;
            winMinX = newMinX;
            winMinZ = newMinZ;

            // The old contents describe a different world rectangle (or a different
            // layout after a capacity growth). Rebuilding the quad is cheap; the caller
            // bakes immediately.
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

        private void Bake(Map map)
        {
            EnsureMaskTexture();

            float normScale = 255f / VisualTweaksSettings.HeatTempNormMax;
            float outdoorTemp = map.mapTemperature.OutdoorTemp;

            // Two sources, chosen by the user. With per-cell sampling the AO field is read
            // one cell at a time. Without it there is no per-cell data to read at all:
            // vanilla resolves a room per call and returns that room's single temperature,
            // so the room walk below produces the same mask for a fraction of the cost.
            bool perCell = VisualTweaksSettings.UsePerCellTemperature;
            if (perCell != bakedPerCellMode)
            {
                bakedPerCellMode = perCell;

                // Drop the cached rect: the cells in the buffer came from the other
                // source. The room walk ignores it (it always rewrites the whole window),
                // but the per-cell scroll path would otherwise treat room plateaus as a
                // valid starting point.
                bakedW = -1;
                bakedH = -1;
            }

            int maxTempByte = perCell
                ? BakePerCellField(map, normScale, outdoorTemp)
                : BakeRooms(map, normScale, outdoorTemp);

            // Direct heat sources. Room temperature already picks up indoor fires and
            // heaters, but vanilla outdoor fires sit on cells without a room and
            // campfires/torches would otherwise not show until a room warms up. The
            // G channel keeps them visible immediately.
            int maxFireByte = StampHeatSources(map);

            lastMaxTempC = maxTempByte * VisualTweaksSettings.HeatTempNormMax / 255f;
            lastMaxFire = maxFireByte / 255f;

            bakedMinX = winMinX;
            bakedMinZ = winMinZ;
            bakedW = winW;
            bakedH = winH;

            // Nothing is hot enough to draw: leave the previous texture alone and skip
            // the upload. The next bake that crosses the threshold will refresh it before
            // the shader is allowed to draw.
            if (!HasVisibleHeat())
            {
                return;
            }

            // Waylith's Apply() disposes and recreates the whole GPU image, so an
            // unchanged mask (paused, static weather, same room temperatures) must not be
            // uploaded again. The origin is part of the hash, so a moved window always
            // uploads.
            uint hash = HashWindow();
            if (hash == sentHash)
            {
                return;
            }

            // A scrolled or resized window leaves stale cells in the ring outside the
            // window. The shader can sample that ring (bilinear at the window edge), so it
            // must read as cold. Nothing samples the ring before an upload, so this runs
            // only on the upload path.
            ClearOutsideWindow();

            maskTexture.SetPixels32(maskPixels);
            maskTexture.Apply(false, false);
            sentHash = hash;
        }

        /// <summary>
        /// Bakes from AO's per-cell temperature field, when the user turned per-cell
        /// sampling on. Keeps the cells that are still in
        /// view and re-reads only the strips a pan exposed; a size change, a teleport or the
        /// first bake after a capacity growth falls back to the full window.
        /// </summary>
        private int BakePerCellField(Map map, float normScale, float outdoorTemp)
        {
            int maxTempByte = 0;

            bool scrolled = false;
            if (bakedW == winW && bakedH == winH && bakedW > 0)
            {
                int dx = winMinX - bakedMinX;
                int dz = winMinZ - bakedMinZ;
                if ((dx != 0 || dz != 0)
                    && Math.Abs(dx) < winW
                    && Math.Abs(dz) < winH)
                {
                    ScrollMask(dx, dz);
                    scrolled = true;
                }
            }

            if (scrolled)
            {
                // Bake the newly exposed strips at full resolution: they are narrow, so
                // the saving over re-reading the whole window is still large.
                if (winMinX != bakedMinX)
                {
                    if (winMinX > bakedMinX)
                    {
                        int start = Mathf.Max(0, winW - (winMinX - bakedMinX));
                        BakeTemperatureRect(map, start, winW, 0, winH, 1,
                            normScale, outdoorTemp, ref maxTempByte);
                    }
                    else
                    {
                        int end = Mathf.Min(winW, bakedMinX - winMinX);
                        BakeTemperatureRect(map, 0, end, 0, winH, 1,
                            normScale, outdoorTemp, ref maxTempByte);
                    }
                }

                if (winMinZ != bakedMinZ)
                {
                    if (winMinZ > bakedMinZ)
                    {
                        int start = Mathf.Max(0, winH - (winMinZ - bakedMinZ));
                        BakeTemperatureRect(map, 0, winW, start, winH, 1,
                            normScale, outdoorTemp, ref maxTempByte);
                    }
                    else
                    {
                        int end = Mathf.Min(winH, bakedMinZ - winMinZ);
                        BakeTemperatureRect(map, 0, winW, 0, end, 1,
                            normScale, outdoorTemp, ref maxTempByte);
                    }
                }

                // The temperature channel survives the scroll; the fire channel is
                // cheap to rebuild, so clear it for the whole window and restamp every
                // source. This pass also re-establishes the true window maximum.
                for (int z = 0; z < winH; z++)
                {
                    int row = (capacityH - 1 - z) * capacityW;
                    for (int x = 0; x < winW; x++)
                    {
                        int index = row + x;
                        Color32 cell = maskPixels[index];
                        if (cell.r > maxTempByte)
                        {
                            maxTempByte = cell.r;
                        }

                        if (cell.g != 0)
                        {
                            cell.g = 0;
                            maskPixels[index] = cell;
                        }
                    }
                }
            }
            else
            {
                // Far zoom: one temperature sample can feed a 2x2 cell block. The mask
                // is sampled bilinearly and the shimmer is low-frequency, so the blocks
                // are invisible while the field reads drop by 4x.
                int stride = winW * winH > WideWindowCells ? 2 : 1;
                BakeTemperatureRect(map, 0, winW, 0, winH, stride,
                    normScale, outdoorTemp, ref maxTempByte);
            }

            return maxTempByte;
        }

        /// <summary>
        /// Fills the window from room temperatures: one value per room, written across its
        /// footprint, instead of one GenTemperature call per cell. This is the default and
        /// the only source without AO. Open cells
        /// keep the ambient temperature, which is exactly what vanilla returns for a cell
        /// without a room; wall and door cells have no room either and now read ambient
        /// too, where the per-cell path averaged their neighbours. That is a one-cell
        /// difference on a mask that is bilinearly filtered and drowned in noise, and it
        /// removes the shimmer from walls, which is closer to the truth.
        /// </summary>
        private int BakeRooms(Map map, float normScale, float outdoorTemp)
        {
            Color32 ambient = PackTemperature(outdoorTemp, normScale);

            // Every cell the room walk does not touch must read as outdoor air. Rooms are
            // filled afterwards, so this doubles as the "cell left its room" reset.
            //
            // Filled as 32-bit words one row at a time: the row is contiguous and every
            // cell gets the same value, so this lowers to a memset per row instead of
            // winW * winH individual 4-byte stores.
            uint ambientWord = PackWord(ambient);
            Span<uint> words = MemoryMarshal.Cast<Color32, uint>(maskPixels.AsSpan());
            for (int z = 0; z < winH; z++)
            {
                int row = (capacityH - 1 - z) * capacityW;
                words.Slice(row, winW).Fill(ambientWord);
            }

            int maxTempByte = ambient.r;

            CellRect window = new CellRect(winMinX, winMinZ, winW, winH);
            IReadOnlyList<Room> rooms = map.regionGrid.AllRooms;
            RegionGrid regionGrid = map.regionGrid;

            for (int i = 0; i < rooms.Count; i++)
            {
                Room room = rooms[i];

                // A dereferenced room is mid-rebuild and comes back next bake. A room that
                // uses the outdoor temperature already reads as ambient above.
                if (room == null || room.Dereferenced || room.UsesOutdoorTemperature)
                {
                    continue;
                }

                Color32 packed = PackTemperature(room.Temperature, normScale);

                // Same reading as the ambient fill: nothing to write and nothing to add to
                // the maximum. This is also the cheap reject that keeps ExtentsClose (which
                // walks the room's regions) off the common temperate map entirely.
                if (packed.r == ambient.r)
                {
                    continue;
                }

                // Read once: ExtentsClose recomputes by walking every region of the room,
                // and the old code called it for the overlap test and would have needed it
                // again for the rectangle test.
                CellRect extents = room.ExtentsClose;

                // Only rooms that overlap the window can contribute; a hot room on the far
                // side of the map must not keep this layer alive.
                if (!extents.Overlaps(window))
                {
                    continue;
                }

                int x0 = Mathf.Max(extents.minX, winMinX);
                int x1 = Mathf.Min(extents.maxX, winMinX + winW - 1);
                int z0 = Mathf.Max(extents.minZ, winMinZ);
                int z1 = Mathf.Min(extents.maxZ, winMinZ + winH - 1);
                if (x0 > x1 || z0 > z1)
                {
                    continue;
                }

                // Region-driven from here on. Region.Cells walks the region's *bounding
                // rectangle* and keeps the cells that resolve back to it, and Room.Cells
                // wraps that in a second yield iterator, so the old per-room fallback below
                // paid two state-machine MoveNext calls per cell. Splitting the decision per
                // region instead of per room also keeps nearly every cell on the fast path:
                // an L-shaped room is still cut into rectangular regions, so only the few
                // regions that straddle a corner ever need the membership test.
                List<Region> regions = room.Regions;
                bool wrote = false;
                for (int r = 0; r < regions.Count; r++)
                {
                    Region region = regions[r];
                    CellRect inner = region.extentsClose;

                    int minX = Mathf.Max(inner.minX, x0);
                    int maxX = Mathf.Min(inner.maxX, x1);
                    int minZ = Mathf.Max(inner.minZ, z0);
                    int maxZ = Mathf.Min(inner.maxZ, z1);
                    if (minX > maxX || minZ > maxZ)
                    {
                        continue;
                    }

                    // A rectangular region is exactly its extents, so whole rows can be
                    // filled at memset speed.
                    if (region.CellCount == inner.Width * inner.Height)
                    {
                        uint packedWord = PackWord(packed);
                        int span = maxX - minX + 1;
                        for (int z = minZ; z <= maxZ; z++)
                        {
                            int row = (capacityH - 1 - (z - winMinZ)) * capacityW
                                + (minX - winMinX);
                            words.Slice(row, span).Fill(packedWord);
                        }

                        wrote = true;
                        continue;
                    }

                    for (int z = minZ; z <= maxZ; z++)
                    {
                        int row = (capacityH - 1 - (z - winMinZ)) * capacityW;
                        for (int x = minX; x <= maxX; x++)
                        {
                            if (regionGrid.GetRegionAt_NoRebuild_InvalidAllowed(new IntVec3(x, 0, z)) != region)
                            {
                                continue;
                            }

                            maskPixels[row + (x - winMinX)] = packed;
                            wrote = true;
                        }
                    }
                }

                // Only a room that actually painted something counts towards "is anything
                // visible". An L-shaped room whose bounding box sits in the window but whose
                // cells do not would otherwise raise the maximum and keep a quad alive that
                // discards every fragment.
                if (wrote && packed.r > maxTempByte)
                {
                    maxTempByte = packed.r;
                }
            }

            return maxTempByte;
        }

        private static Color32 PackTemperature(float celsius, float normScale)
        {
            int value = Mathf.Clamp(Mathf.RoundToInt(celsius * normScale), 0, 255);
            return new Color32((byte)value, 0, 0, 255);
        }

        /// <summary>
        /// The same cell as one 32-bit word. Color32 lays its bytes out as r, g, b, a, so on
        /// little-endian the word reads r | g &lt;&lt; 8 | b &lt;&lt; 16 | a &lt;&lt; 24. Row fills and the
        /// hash work on this view; the texture upload still takes the Color32 array.
        ///
        /// Only r and g carry data. The other two are preserved rather than assumed because
        /// a row fill overwrites whole cells, and a cell that keeps a stale b or a would
        /// show up in the hash as a change.
        /// </summary>
        private static uint PackWord(Color32 cell)
        {
            return (uint)cell.r
                | ((uint)cell.g << 8)
                | ((uint)cell.b << 16)
                | ((uint)cell.a << 24);
        }

        /// <summary>
        /// Reads AO's per-cell temperatures into a rectangle of the window, optionally
        /// filling a stride x stride block from one sample. Coordinates are window-local.
        /// This is the only path that touches GenTemperature, and only because AO is the
        /// one source that really is per cell.
        /// </summary>
        private void BakeTemperatureRect(
            Map map,
            int x0,
            int x1,
            int z0,
            int z1,
            int stride,
            float normScale,
            float outdoorTemp,
            ref int maxTempByte)
        {
            for (int z = z0; z < z1; z += stride)
            {
                int worldZ = winMinZ + z;

                for (int x = x0; x < x1; x += stride)
                {
                    int worldX = winMinX + x;
                    IntVec3 cell = new IntVec3(worldX, 0, worldZ);

                    // AO's patch makes GetTemperatureForCell return the per-cell snapshot,
                    // including outdoor cells. If its patch is ever absent on this method,
                    // vanilla returns exactly 21 C for open cells; treat the ambient
                    // temperature as a floor in that case so heat-wave weather stays
                    // visible.
                    //
                    // GetRoom is the expensive part and AO's field almost never returns
                    // exactly 21 C, so keep that check last: the common case then pays only
                    // for the (cheap) temperature comparison.
                    float temp = GenTemperature.GetTemperatureForCell(cell, map);
                    if (Mathf.Approximately(temp, 21f)
                        && outdoorTemp > temp
                        && cell.GetRoom(map) == null)
                    {
                        temp = outdoorTemp;
                    }

                    int value = Mathf.Clamp(
                        Mathf.RoundToInt(temp * normScale),
                        0,
                        255);
                    if (value > maxTempByte)
                    {
                        maxTempByte = value;
                    }

                    Color32 packed = new Color32((byte)value, 0, 0, 255);
                    if (stride == 1)
                    {
                        // Common case (and every pan strip): one cell, one write.
                        maskPixels[(capacityH - 1 - z) * capacityW + x] = packed;
                    }
                    else
                    {
                        int blockZEnd = Mathf.Min(z + stride, z1);
                        int blockXEnd = Mathf.Min(x + stride, x1);
                        for (int bz = z; bz < blockZEnd; bz++)
                        {
                            int row = (capacityH - 1 - bz) * capacityW;
                            for (int bx = x; bx < blockXEnd; bx++)
                            {
                                maskPixels[row + bx] = packed;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Shifts the baked window inside the capacity-sized buffer. (dx, dz) is the new
        /// window origin minus the old one, so a cell moves by (-dx, -dz) in window-local
        /// coordinates. Only the overlapping rectangle is touched; the exposed strips are
        /// rewritten by the caller.
        /// </summary>
        private void ScrollMask(int dx, int dz)
        {
            int sourceX0 = Mathf.Max(0, dx);
            int sourceX1 = Mathf.Min(bakedW, winW + dx);
            int sourceZ0 = Mathf.Max(0, dz);
            int sourceZ1 = Mathf.Min(bakedH, winH + dz);
            if (sourceX1 <= sourceX0 || sourceZ1 <= sourceZ0)
            {
                return;
            }

            int width = sourceX1 - sourceX0;
            int rowCount = sourceZ1 - sourceZ0;

            // Rows are stored bottom-up, so the destination row moves by +dz. Copy in
            // the direction that never overwrites a source row that has not been read.
            bool highToLow = dz < 0;
            for (int i = 0; i < rowCount; i++)
            {
                int z = highToLow ? (sourceZ1 - 1 - i) : (sourceZ0 + i);
                int destinationZ = z - dz;
                int source = (capacityH - 1 - z) * capacityW + sourceX0;
                int destination =
                    (capacityH - 1 - destinationZ) * capacityW + (sourceX0 - dx);

                // Array.Copy handles the horizontal overlap like memmove.
                Array.Copy(maskPixels, source, maskPixels, destination, width);
            }
        }

        /// <summary>
        /// Zeroes the capacity ring outside the current window. The shader maps the
        /// whole capacity, so cells outside the baked window must read as cold.
        /// </summary>
        private void ClearOutsideWindow()
        {
            int windowRowsStart = (capacityH - winH) * capacityW;
            if (windowRowsStart > 0)
            {
                Array.Clear(maskPixels, 0, windowRowsStart);
            }

            if (winW < capacityW)
            {
                for (int z = 0; z < winH; z++)
                {
                    int row = (capacityH - 1 - z) * capacityW;
                    Array.Clear(maskPixels, row + winW, capacityW - winW);
                }
            }
        }

        /// <summary>
        /// FNV-1a over the window plus its origin, size and texture capacity. The origin is
        /// part of the hash because the mask is anchored in map space: a window one room
        /// over must still upload, while a static camera over static temperatures hashes the
        /// same and skips the upload. The capacity is included because a reallocation
        /// recreates the GPU image, so identical content is not the same texture. Only the
        /// two channels the shader reads are hashed.
        ///
        /// This replaces a cell-by-cell comparison against a copy of the last upload, which
        /// cost two extra window-sized passes and a second mask-sized array.
        ///
        /// The sample grid comes from HashStride above: a change confined entirely to cells
        /// the grid skips does not upload. Nothing in this mask is that small — the thinnest
        /// plume is three cells across and room temperature moves in whole rooms — so the
        /// only thing the loss costs is a sub-cell flicker that would not have been visible.
        /// </summary>
        private uint HashWindow()
        {
            uint hash = 2166136261u;
            hash = (hash ^ (uint)winMinX) * 16777619u;
            hash = (hash ^ (uint)winMinZ) * 16777619u;
            hash = (hash ^ (uint)winW) * 16777619u;
            hash = (hash ^ (uint)winH) * 16777619u;
            hash = (hash ^ (uint)capacityW) * 16777619u;
            hash = (hash ^ (uint)capacityH) * 16777619u;

            // One 32-bit read per sampled cell instead of a Color32 field access plus a shift
            // and an or. The low half of the word is exactly the r | g << 8 pair the shader
            // reads, so the mask keeps b and a out of the hash the same way it did before.
            ReadOnlySpan<uint> words = MemoryMarshal.Cast<Color32, uint>(maskPixels.AsSpan());
            for (int z = 0; z < winH; z += HashStride)
            {
                int row = (capacityH - 1 - z) * capacityW;
                ReadOnlySpan<uint> line = words.Slice(row, winW);
                for (int x = 0; x < winW; x += HashStride)
                {
                    hash = (hash ^ (line[x] & 0xFFFFu)) * 16777619u;
                }
            }

            return hash;
        }

        private int StampHeatSources(Map map)
        {
            int maxFireByte = 0;

            var fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            if (fires != null)
            {
                for (int i = 0; i < fires.Count; i++)
                {
                    if (!(fires[i] is Fire fire))
                    {
                        continue;
                    }

                    float radius = 1.5f + fire.fireSize * 2.2f;
                    float strength = Mathf.Clamp01(
                        0.35f + 0.65f * fire.fireSize / Fire.MaxFireSize);
                    StampRadial(
                        fire.Position.ToVector3Shifted(),
                        radius,
                        strength,
                        ref maxFireByte);
                }
            }

            StampHeatPushers(map.listerBuildings.allBuildingsColonist, ref maxFireByte);
            StampHeatPushers(map.listerBuildings.allBuildingsNonColonist, ref maxFireByte);

            return maxFireByte;
        }

        /// <summary>
        /// Stamps every building that is putting heat into the air right now.
        ///
        /// Two kinds land here: heaters, which the component family never exposes, and
        /// buildings whose CompHeatPusher means a live flame or exhaust rather than standing
        /// ambient warmth. Work tables are excluded below and left to the temperature
        /// channel, because their CompHeatPusher is on whenever the bench has power or fuel.
        /// </summary>
        private void StampHeatPushers(List<Building> buildings, ref int maxFireByte)
        {
            if (buildings == null)
            {
                return;
            }

            for (int i = 0; i < buildings.Count; i++)
            {
                Building building = buildings[i];

                // Handled first: a heater has no CompHeatPusher to look up, and this way it
                // does not pay for the lookup either.
                if (building is Building_Heater heater)
                {
                    StampHeater(heater, ref maxFireByte);
                    continue;
                }

                CompHeatPusher pusher = building.TryGetComp<CompHeatPusher>();
                if (pusher == null || !IsHeatSourceActive(pusher))
                {
                    continue;
                }

                // Work tables carry this component as standing background warmth: a stove
                // radiates 3-4 heat/s whenever it has power or fuel, working or not. Sealing
                // it into the G channel is what makes an idle stove shimmer. A kitchen that
                // is really cooking already warms its room through heatPerTickWhileWorking,
                // which the temperature channel picks up on its own.
                if (building.def.building != null
                    && building.def.building.heatPerTickWhileWorking > 0f)
                {
                    continue;
                }

                float heat = pusher.Props.heatPerSecond;
                if (heat <= 0.1f)
                {
                    continue;
                }

                // Campfires are around 21 heat/s, torches and vents 3..9. The square
                // root keeps the big sources from swallowing the whole view while
                // still giving a visible plume to a campfire.
                float radius = 1.5f + Mathf.Sqrt(heat) * 0.6f;
                float strength = Mathf.Clamp01(Mathf.Sqrt(heat / 21f));
                StampRadial(building.TrueCenter(), radius, strength, ref maxFireByte);
            }
        }

        /// <summary>
        /// Stamps the plume of a heater that is currently pushing heat into its room.
        ///
        /// Building_Heater only writes operatingAtHighPower from TickRare, and only while it
        /// has power, so a heater that lost power or was flicked off keeps whatever value it
        /// had. The power and flick checks below cover both cases instead of trusting the
        /// flag alone.
        /// </summary>
        private void StampHeater(Building_Heater heater, ref int maxFireByte)
        {
            if (!heater.SpawnedOrAnyParentSpawned || !FlickUtility.WantsToBeOn(heater))
            {
                return;
            }

            CompTempControl? control = heater.compTempControl;
            if (control == null || !control.operatingAtHighPower)
            {
                return;
            }

            CompPowerTrader? power = heater.compPowerTrader;
            if (power != null && !power.PowerOn)
            {
                return;
            }

            StampRadial(
                heater.TrueCenter(),
                HeaterPlumeRadius,
                HeaterPlumeStrength,
                ref maxFireByte);
        }

        /// <summary>
        /// Mirrors the active part of CompHeatPusherPowered.ShouldPushHeatNow but
        /// deliberately ignores its ambient-temperature window. A lit campfire stops
        /// pushing heat into the room at 28 C, yet its flame is still visibly hot and
        /// should keep shimmering in a heat wave.
        /// </summary>
        private static bool IsHeatSourceActive(CompHeatPusher pusher)
        {
            if (!pusher.enabled)
            {
                return false;
            }

            Thing parent = pusher.parent;
            if (!parent.SpawnedOrAnyParentSpawned || !FlickUtility.WantsToBeOn(parent))
            {
                return false;
            }

            CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
            {
                return false;
            }

            CompRefuelable fuel = parent.TryGetComp<CompRefuelable>();
            if (fuel != null && !fuel.HasFuel)
            {
                return false;
            }

            CompBreakdownable breakdown = parent.TryGetComp<CompBreakdownable>();
            return breakdown == null || !breakdown.BrokenDown;
        }

        private void StampRadial(
            Vector3 world,
            float radius,
            float strength,
            ref int maxFireByte)
        {
            if (radius <= 0.01f || strength <= 0.01f)
            {
                return;
            }

            float radiusSq = radius * radius;
            int minX = Mathf.Max(winMinX, Mathf.FloorToInt(world.x - radius));
            int maxX = Mathf.Min(winMinX + winW - 1, Mathf.CeilToInt(world.x + radius));
            int minZ = Mathf.Max(winMinZ, Mathf.FloorToInt(world.z - radius));
            int maxZ = Mathf.Min(winMinZ + winH - 1, Mathf.CeilToInt(world.z + radius));

            for (int z = minZ; z <= maxZ; z++)
            {
                float dz = z + 0.5f - world.z;
                int row = (capacityH - 1 - (z - winMinZ)) * capacityW;

                for (int x = minX; x <= maxX; x++)
                {
                    float dx = x + 0.5f - world.x;
                    float d2 = dx * dx + dz * dz;
                    if (d2 >= radiusSq)
                    {
                        continue;
                    }

                    float falloff = 1f - d2 / radiusSq;
                    int value = Mathf.Clamp(
                        Mathf.RoundToInt(strength * falloff * falloff * 255f),
                        0,
                        255);
                    if (value <= 0)
                    {
                        continue;
                    }

                    if (value > maxFireByte)
                    {
                        maxFireByte = value;
                    }

                    int index = row + (x - winMinX);
                    Color32 cell = maskPixels[index];
                    if (value > cell.g)
                    {
                        cell.g = (byte)value;
                        maskPixels[index] = cell;
                    }
                }
            }
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
            sentHash = uint.MaxValue;
            capacityW = 0;
            capacityH = 0;
            bakedMinX = int.MinValue;
            bakedMinZ = int.MinValue;
            bakedW = -1;
            bakedH = -1;
            bakedPerCellMode = false;
            quadWidth = -1;
            quadHeight = -1;
            quadMinX = int.MinValue;
            quadMinZ = int.MinValue;
            winW = 0;
            winH = 0;
            lastBake = float.NegativeInfinity;
            wasPaused = false;
        }
    }
}
