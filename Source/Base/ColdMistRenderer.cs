using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections.Generic;
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
    ///  * cells are filled by walking rooms instead of sampling every cell, so the
    ///    cost scales with the number of *cold* rooms rather than with the zoom
    ///    level, and no 2x2 stride is needed at far zoom;
    ///  * no scroll optimisation: a window rebake clears and refills the window,
    ///    which is cheaper than the bookkeeping for shifted strips;
    ///  * the shader needs no scene grab, so there is no refraction path at all.
    /// </summary>
    internal sealed class ColdMistRenderer : IDisposable
    {
        // Room temperature only moves when a cooler, heater, door or roof changes;
        // 4 Hz is far below anything the eye can catch and keeps the room walk and
        // the texture upload cheap. The shader animates continuously between bakes.
        private const float BakeInterval = 0.25f;

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

        // Window whose contents are on the GPU. The origin is part of the hash: the
        // mask is anchored in map space, so an identical-looking window one room
        // over still needs an upload.
        private uint uploadedHash = uint.MaxValue;

        private float shaderTime;
        private float lastBake = float.NegativeInfinity;
        private float lastMinTemp = float.PositiveInfinity;
        private bool loggedDraw;
        private bool wasPaused;

        internal void Draw(Map map, CellRect view, bool paused, TimeSpeed speed)
        {
            EnsureResources(map);

            // Frozen while paused, faster with every game speed tier.
            OverlayClock.Advance(ref shaderTime, paused, speed);

            if (VisualTweaksSettings.coldMistOpacity <= 0.001f)
            {
                return;
            }

            if (!EnsureWindow(map, view, out bool windowChanged))
            {
                return;
            }

            float realtime = Time.realtimeSinceStartup;

            // Nothing in the world changes while paused, so only a window move needs a
            // fresh bake (newly exposed cells were never baked). Unpausing always bakes
            // once instead of waiting for the next cadence.
            bool resumed = wasPaused && !paused;
            wasPaused = paused;
            bool bakeDue = windowChanged || resumed || realtime - lastBake >= BakeInterval;
            if (paused && !windowChanged)
            {
                bakeDue = false;
            }

            if (bakeDue)
            {
                Bake(map);
                lastBake = realtime;
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
        /// Refills the visible window from vanilla room data.
        ///
        /// Only enclosed interiors contribute: a room that uses the outdoor
        /// temperature (a courtyard, a ruin, anything with more than a quarter of its
        /// ceiling missing) is treated as outdoors and left alone, and so is a room
        /// exposed to vacuum. This is exactly the "vanilla room cold, no outdoors"
        /// scope the effect is meant to cover, and it keeps AO's own mist system
        /// untouched because nothing here reads AO's fields.
        /// </summary>
        private void Bake(Map map)
        {
            EnsureResources(map);

            ClearWindow(maskPixels);

            float startTemp = VisualTweaksSettings.coldMistStartTemp;
            float minTemp = float.PositiveInfinity;

            CellRect window = new CellRect(winMinX, winMinZ, winW, winH);
            IReadOnlyList<Room> rooms = map.regionGrid.AllRooms;

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

                // Only rooms that actually overlap the baked window count towards
                // "is anything visible". A cold room on the far side of the map must
                // not keep the quad alive: every fragment of it would discard.
                if (!room.ExtentsClose.Overlaps(window))
                {
                    continue;
                }

                if (temp < minTemp)
                {
                    minTemp = temp;
                }

                byte value = EncodeTemperature(temp);
                Color32 packed = new Color32(value, 0, 0, 255);

                foreach (IntVec3 cell in room.Cells)
                {
                    if (cell.x < winMinX
                        || cell.x >= winMinX + winW
                        || cell.z < winMinZ
                        || cell.z >= winMinZ + winH)
                    {
                        continue;
                    }

                    maskPixels[(mapHeight - 1 - cell.z) * mapWidth + cell.x] = packed;
                }
            }

            lastMinTemp = minTemp;

            // Nothing is cold enough to draw: leave the previous texture alone and
            // skip the upload. The next bake that crosses the threshold refreshes it
            // before the shader is allowed to draw.
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
            maskTexture.SetPixels32(maskPixels);
            maskTexture.Apply(false, false);
        }

        private static byte EncodeTemperature(float celsius)
        {
            float norm = Normalise(celsius);
            return (byte)Mathf.Clamp(Mathf.RoundToInt(norm * 255f), 0, 255);
        }

        /// <summary>
        /// Zeroes the window. A cell that lost its room (a wall built inside it, a
        /// roof removed) must read as "no room" rather than keep an old temperature.
        /// </summary>
        private void ClearWindow(Color32[] maskPixels)
        {
            for (int z = 0; z < winH; z++)
            {
                int row = (mapHeight - 1 - (winMinZ + z)) * mapWidth + winMinX;
                Array.Clear(maskPixels, row, winW);
            }
        }

        /// <summary>
        /// FNV-1a over the window plus its origin. The origin is part of the hash
        /// because the mask is anchored in map space: two identical-looking windows
        /// one room apart must still be uploaded, while a static camera over static
        /// temperatures hashes the same and skips the upload entirely.
        /// </summary>
        private uint HashWindow(Color32[] maskPixels)
        {
            uint hash = 2166136261u;
            hash = (hash ^ (uint)winMinX) * 16777619u;
            hash = (hash ^ (uint)winMinZ) * 16777619u;
            hash = (hash ^ (uint)winW) * 16777619u;
            hash = (hash ^ (uint)winH) * 16777619u;

            for (int z = 0; z < winH; z++)
            {
                int row = (mapHeight - 1 - (winMinZ + z)) * mapWidth + winMinX;
                for (int x = 0; x < winW; x++)
                {
                    Color32 cell = maskPixels[row + x];
                    hash = (hash ^ (uint)(cell.r | (cell.a << 8))) * 16777619u;
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
            winW = 0;
            winH = 0;
            lastBake = float.NegativeInfinity;
            lastMinTemp = float.PositiveInfinity;
            wasPaused = false;
        }
    }
}
