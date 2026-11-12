using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Bakes a spherical hex/pentagon lattice into one small texture, shared by every
    /// hexagonal shield on the map.
    ///
    /// ---- why a texture at all --------------------------------------------
    ///
    /// A hexagon grid drawn in screen space is a flat honeycomb pasted onto a circle:
    /// the cells do not shrink toward the silhouette, so a "dome" reads as a decal.
    /// What is wanted is a *spherical* tiling. That has a hard constraint: Euler's
    /// formula forbids tiling a closed surface with hexagons alone — exactly twelve
    /// pentagons are required (3V = 5F5 + 6F6, 2E = 5F5 + 6F6, V-E+F = 2). Those
    /// pentagons are not decoration, they are where the curvature lives, and they are
    /// deliberately NOT highlighted: a compromise the geometry needs is not a feature
    /// to draw attention to.
    ///
    /// The tiling used here is the Voronoi diagram of a subdivided icosahedron's
    /// vertices (a Goldberg polyhedron's dual). Subdividing each of the 20 faces k
    /// times gives V = 10k^2+2 sites; k=2 gives 42 cells, k=12 gives 1442. Low k reads
    /// like a football, high k reads like an energy mesh — hence the wide range.
    ///
    /// Doing that per fragment means 42..1442 dot products per pixel, over a quad that
    /// can cover most of the screen on a 25-cell gravship dome. Baking it once costs
    /// one texture sample per pixel instead — cheaper than the four-octave Fbm the
    /// dissolve already runs — and cell count becomes a design choice, not a cost.
    ///
    /// ---- what is stored ---------------------------------------------------
    ///
    /// The texture is the *orthographic projection of the upper hemisphere*: texel
    /// (x,y) encodes the direction n = (p.x, p.y, sqrt(1-|p|^2)) for p in the unit
    /// disc. That is the pseudo-3D trick — the disc on screen is treated as a dome,
    /// so a spherical tiling viewed from above is exactly what the eye expects.
    ///
    ///   RGB = the winning site's direction, mapped to 0..1. Constant within a cell,
    ///         so it doubles as a flat face normal and as a per-cell id.
    ///   A   = edgeDistance, 0 on a cell boundary and 1 at the cell centre.
    ///
    /// edgeDistance is a TRUE Voronoi SDF: the minimum over every neighbouring site
    /// j of (d1-dj)/|s1-sj|, not just the runner-up's dot product. The minimum of
    /// continuous functions stays continuous where the nearest competitor changes,
    /// which keeps the gradient — and therefore the drawn line width — even across
    /// a whole cell. No channel carries a pentagon flag.
    ///
    /// RGBAHalf rather than RGBA32 on purpose. An 8-bit edge-distance channel has
    /// only ~2 usable quantisation levels in the one-pixel band where the cell line
    /// lives, which makes the derivative step and the outline shimmer. This mod
    /// already learned that lesson once with the dissolve's lattice hash: a float64
    /// preview hides what float32 on the GPU does. Half floats give ~0.0005 of edge
    /// precision, and unlike full RGBAFloat, linear filtering is universally
    /// supported on desktop Vulkan — which matters because the lines are
    /// reconstructed by bilinear sampling.
    ///
    /// ---- when the bake happens --------------------------------------------
    ///
    /// Never in the middle of gameplay, if it can be helped. Three entry points:
    ///
    ///   1. <see cref="Prewarm"/> from the mod's StaticConstructorOnStartup — the
    ///      normal case. Mod settings are read in the Mod constructor, which runs
    ///      before static constructors, so the enabled flag and the density are both
    ///      already known here and the whole cost lands on the loading screen.
    ///   2. The settings checkbox, the moment it is switched on, so enabling the
    ///      style mid-game does not bill the first shield that happens to be drawn.
    ///   3. The rebake button, when the density draft is committed.
    ///
    /// <see cref="Bind"/> keeps its own "texture missing" branch as a safety net for
    /// anything that turns the style on without going through the UI, but in normal
    /// play it never fires.
    ///
    /// ---- cost -------------------------------------------------------------
    ///
    /// 2048^2 texels at 25 dots each (bin guess + hill climb + wall set) measured
    /// 0.2 s in a Release .NET build of the same loop, so the Mono JIT plus the
    /// SetPixels upload puts the in-game cost at a fraction of a second, once. VRAM
    /// is 32 MiB for one texture shared by every shield. No per-frame CPU work, no
    /// extra draw calls, no extra geometry, and nothing is baked at all while the
    /// style is off.
    ///
    /// 2048 rather than 1024 because the ceiling moved: at k=12 a cell spans ~0.10 of
    /// the radius, so a 25-cell gravship dome magnified to 1500 px would put ~60 px
    /// of screen on 100 texels. At 1024 that left ~50 texels per cell and the line
    /// reconstruction started to stair-step on the largest shields.
    /// </summary>
    internal static class ShieldHexMap
    {
        internal const int MinSubdivisions = 2;
        internal const int MaxSubdivisions = 12;

        /// <summary>
        /// eRef = ERefPerSubdivision / k is the angular distance that maps to
        /// edgeDistance 1.0. The shader needs its RECIPROCAL to rebuild the field
        /// gradient analytically (1/(eRef*z)), so the constant is exposed rather
        /// than duplicated — the two must not drift apart.
        /// </summary>
        internal const float ERefPerSubdivision = 0.62f;

        private const int Size = 2048;

        /// <summary>
        /// Rows handed to one SetPixels call. A full Color[Size * Size] staging array
        /// would be 64 MiB at this resolution; filling the texture in strips keeps the
        /// transient allocation near 2 MiB and stays cache-friendly.
        /// </summary>
        private const int RowsPerBlock = 128;

        private const int BinCount = 24;
        private const int Candidates = 16;

        private static readonly int MapId = Shader.PropertyToID("_HexMap");

        private static Texture2D? texture;
        private static int builtFor = -1;
        private static bool available;
        private static Material? boundTo;
        private static int boundFor = -1;
        private static int failedFor = -1;

        /// <summary>False if the texture could not be built; the renderer falls back to
        /// the smooth dome rather than drawing a black disc.</summary>
        internal static bool Available => available;

        private static readonly float Phi = (1f + Mathf.Sqrt(5f)) * 0.5f;

        // Unit icosahedron vertices (normalised on use) and its 20 triangular faces.
        private static readonly Vector3[] IcosaVertices =
        {
            new Vector3(-1f, Phi, 0f),  new Vector3(1f, Phi, 0f),
            new Vector3(-1f, -Phi, 0f), new Vector3(1f, -Phi, 0f),
            new Vector3(0f, -1f, Phi),  new Vector3(0f, 1f, Phi),
            new Vector3(0f, -1f, -Phi), new Vector3(0f, 1f, -Phi),
            new Vector3(Phi, 0f, -1f),  new Vector3(Phi, 0f, 1f),
            new Vector3(-Phi, 0f, -1f), new Vector3(-Phi, 0f, 1f),
        };

        private static readonly int[] IcosaFaces =
        {
            0, 11, 5,   0, 5, 1,    0, 1, 7,    0, 7, 10,   0, 10, 11,
            1, 5, 9,    5, 11, 4,   11, 10, 2,  10, 7, 6,   7, 1, 8,
            3, 9, 4,    3, 4, 2,    3, 2, 6,    3, 6, 8,    3, 8, 9,
            4, 9, 5,    2, 4, 11,   6, 2, 10,   8, 6, 7,    9, 8, 1,
        };

        /// <summary>
        /// Ensures the material carries a hex map for this density. Cheap after the
        /// first call: the texture is cached and only rebuilt when the cell count
        /// actually changes.
        /// </summary>
        internal static void Bind(Material material, int subdivisions)
        {
            int k = Mathf.Clamp(subdivisions, MinSubdivisions, MaxSubdivisions);

            // ApplyLook runs on every visible shield every frame. A failed bake must
            // not be retried (and re-logged) sixty times a second; remember which
            // density failed and keep the smooth dome until it changes.
            if (failedFor == k)
            {
                return;
            }

            if (texture == null || builtFor != k)
            {
                Build(k);
            }

            // SetTexture is not free, so only touch the material when the texture
            // itself changed or when this material has not been bound yet. Build
            // clears boundTo, so a fresh texture always republishes.
            if (texture != null && (boundTo != material || boundFor != k))
            {
                material.SetTexture(MapId, texture);
                boundTo = material;
                boundFor = k;
            }
        }

        /// <summary>
        /// True when something would actually sample the texture: the hex style is
        /// picked, the custom dome that draws it is on, and its shader compiled.
        /// Baking otherwise costs 32 MiB and a fraction of a second for an image
        /// nothing reads.
        /// </summary>
        private static bool CanBeDrawn =>
            VisualTweaksSettings.hexShieldDome
            && VisualTweaksSettings.crispShieldDome
            && ShieldDomeBridge.ShaderReady;

        /// <summary>
        /// Bakes during startup, before any map exists, so no shield ever pays for
        /// this in the middle of a fight. Called from the mod's
        /// StaticConstructorOnStartup, which runs after mod settings are read (the Mod
        /// constructor calls GetSettings) and before the first map loads.
        /// </summary>
        internal static void Prewarm()
        {
            if (!CanBeDrawn)
            {
                return;
            }

            int k = Mathf.Clamp(
                VisualTweaksSettings.shieldHexDensityApplied,
                MinSubdivisions,
                MaxSubdivisions);

            if (texture != null && builtFor == k)
            {
                return;
            }

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            Build(k);
            watch.Stop();

            if (available)
            {
                VisualTweaksLog.Info("hex shield map baked at startup (k=" + k
                    + ", " + Size + "x" + Size + ", " + watch.ElapsedMilliseconds
                    + " ms).");
            }
        }

        /// <summary>
        /// Rebakes for a new cell density immediately, so the stall lands on the
        /// button press that asked for it instead of on the next shield that happens
        /// to come into view. If the hex style is off nothing is baked: the texture is
        /// only ever built for a style that will draw it, and ApplyDensity has already
        /// recorded the value for when it is switched on.
        /// </summary>
        internal static void ApplyDensity(int subdivisions)
        {
            int k = Mathf.Clamp(subdivisions, MinSubdivisions, MaxSubdivisions);
            VisualTweaksSettings.shieldHexDensityApplied = k;

            if (!CanBeDrawn || failedFor == k)
            {
                return;
            }

            if (texture != null && builtFor == k)
            {
                return;
            }

            Build(k);
        }

        private static void Build(int k)
        {
            Texture2D? created = null;

            try
            {
                List<Vector3> sites = BuildSites(k);
                int n = sites.Count;

                // The eight closest sites to each. Their Voronoi walls are the only
                // ones a cell can have, so this list is both the hill-climb graph and
                // the wall set. Storing the pair distances alongside it keeps the
                // inner loop contiguous instead of chasing an n x n matrix that would
                // be 8 MB at k=12 for eight values actually used.
                int[][] neighbours = BuildNeighbours(sites);
                float[][] separation = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    int[] near = neighbours[i];
                    float[] seps = new float[near.Length];
                    for (int q = 0; q < near.Length; q++)
                    {
                        seps[q] = Mathf.Max((sites[i] - sites[near[q]]).magnitude, 1e-4f);
                    }

                    separation[i] = seps;
                }

                int[][] bins = BuildBins(sites);

                // At a cell centre the SDF is roughly sin(theta/2) for the site
                // spacing theta, and theta scales as 1/k. Keeping most cells near 1
                // avoids clipping them flat; the shader reads this same number back
                // (as 1/eRef) to rebuild the exact field gradient.
                float eRef = ERefPerSubdivision / k;

                created = new Texture2D(Size, Size, TextureFormat.RGBAHalf, false)
                {
                    name = "VisualTweaksShieldHexMap",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };

                Color[] strip = new Color[Size * RowsPerBlock];

                for (int y0 = 0; y0 < Size; y0 += RowsPerBlock)
                {
                    int rows = Mathf.Min(RowsPerBlock, Size - y0);
                    int w = 0;

                    for (int y = y0; y < y0 + rows; y++)
                    {
                        float py = -1f + 2f * (y + 0.5f) / Size;
                        int binRow = Mathf.Clamp(
                            (int)((py * 0.5f + 0.5f) * BinCount),
                            0,
                            BinCount - 1);

                        for (int x = 0; x < Size; x++)
                        {
                            float px = -1f + 2f * (x + 0.5f) / Size;

                            float r2 = px * px + py * py;
                            if (r2 >= 1f)
                            {
                                // Outside the disc the shader's silhouette clip is
                                // already zero, so any value is fine. Face normal up.
                                strip[w++] = new Color(0.5f, 0.5f, 1f, 0f);
                                continue;
                            }

                            Vector3 dir = new Vector3(px, py, Mathf.Sqrt(1f - r2));
                            int binCol = Mathf.Clamp(
                                (int)((px * 0.5f + 0.5f) * BinCount),
                                0,
                                BinCount - 1);
                            int[] candidates = bins[binRow * BinCount + binCol];

                            // The coarse bin supplies a STARTING GUESS, not the answer.
                            // Near the silhouette, n.z = sqrt(1-|p|^2) changes so fast
                            // across one bin that its pre-ranked sites can miss the
                            // true winner (measured: ~1% of pixels wrong at k=8). Walk
                            // up the neighbour graph until no neighbour is closer; on a
                            // triangular lattice this converges in a step or two.
                            float d1 = -2f;
                            int i1 = candidates[0];
                            for (int c = 0; c < candidates.Length; c++)
                            {
                                float d = Vector3.Dot(dir, sites[candidates[c]]);
                                if (d > d1)
                                {
                                    d1 = d;
                                    i1 = candidates[c];
                                }
                            }

                            for (int step = 0; step < 8; step++)
                            {
                                int next = -1;
                                float nd = d1;
                                int[] near = neighbours[i1];
                                for (int q = 0; q < near.Length; q++)
                                {
                                    float d = Vector3.Dot(dir, sites[near[q]]);
                                    if (d > nd)
                                    {
                                        nd = d;
                                        next = near[q];
                                    }
                                }

                                if (next < 0)
                                {
                                    break;
                                }

                                i1 = next;
                                d1 = nd;
                            }

                            // True Voronoi SDF: the walls of a cell are formed only by
                            // its Delaunay neighbours, all of which are in this list.
                            // Taking the minimum of continuous functions keeps the
                            // field smooth where the nearest competitor changes.
                            float best = 1e9f;
                            int[] walls = neighbours[i1];
                            float[] seps = separation[i1];
                            for (int q = 0; q < walls.Length; q++)
                            {
                                float wall = (d1 - Vector3.Dot(dir, sites[walls[q]]))
                                    / seps[q];
                                if (wall < best)
                                {
                                    best = wall;
                                }
                            }

                            float e = Mathf.Clamp01(best / eRef);
                            Vector3 face = sites[i1];

                            strip[w++] = new Color(
                                face.x * 0.5f + 0.5f,
                                face.y * 0.5f + 0.5f,
                                face.z * 0.5f + 0.5f,
                                e);
                        }
                    }

                    created.SetPixels(0, y0, Size, rows, strip, 0);
                }

                created.Apply(false, true);

                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }

                texture = created;
                builtFor = k;
                failedFor = -1;
                available = true;

                // The previous texture object was destroyed above. Any material still
                // holding it has to be re-bound, and Build is exactly the event that
                // makes that true — including when a pre-bake means Bind itself will
                // skip its own "density changed" branch.
                boundTo = null;
                boundFor = -1;
            }
            catch (Exception ex)
            {
                if (created != null)
                {
                    UnityEngine.Object.Destroy(created);
                }

                available = false;
                texture = null;
                builtFor = -1;
                failedFor = k;
                Log.Error("[Visual Tweaks] Hex shield map could not be baked;"
                    + " falling back to the smooth dome: " + ex);
            }
        }

        /// <summary>
        /// For each site, the eight closest other sites. On this lattice that
        /// always contains every Delaunay neighbour (six for a hexagon cell, five
        /// for a pentagon), plus one or two harmless extras whose wall distance is
        /// larger and therefore never wins the minimum. Built once per bake.
        /// </summary>
        private static int[][] BuildNeighbours(List<Vector3> sites)
        {
            int n = sites.Count;
            int take = Mathf.Min(8, n - 1);
            int[][] result = new int[n][];
            float[] scores = new float[n];

            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    scores[j] = j == i
                        ? -2f
                        : Vector3.Dot(sites[i], sites[j]);
                }

                int[] kept = new int[take];
                for (int a = 0; a < take; a++)
                {
                    int best = -1;
                    for (int j = 0; j < n; j++)
                    {
                        if (j == i || scores[j] <= -1.5f)
                        {
                            continue;
                        }

                        if (best < 0 || scores[j] > scores[best])
                        {
                            best = j;
                        }
                    }

                    kept[a] = best;
                    scores[best] = -2f;
                }

                result[i] = kept;
            }

            return result;
        }

        /// <summary>
        /// Sites are the barycentric lattice points of the k-times subdivided
        /// icosahedron, projected back onto the sphere. Twelve of them are the original
        /// icosahedron vertices; their Voronoi cells are the mandatory pentagons. No
        /// flag is stored for them — the lattice produces the right shapes on its own.
        /// </summary>
        private static List<Vector3> BuildSites(int k)
        {
            var lookup = new HashSet<string>();
            var sites = new List<Vector3>();

            for (int f = 0; f < 20; f++)
            {
                Vector3 A = IcosaVertices[IcosaFaces[f * 3]].normalized;
                Vector3 B = IcosaVertices[IcosaFaces[f * 3 + 1]].normalized;
                Vector3 C = IcosaVertices[IcosaFaces[f * 3 + 2]].normalized;

                for (int i = 0; i <= k; i++)
                {
                    for (int j = 0; j <= k - i; j++)
                    {
                        int m = k - i - j;
                        Vector3 p = ((A * m) + (B * i) + (C * j)) / k;
                        p = p.normalized;

                        // "F5" rounding keeps shared edge and corner points from
                        // being added twice by the twenty faces. At k=12 the sites are
                        // far apart relative to 1e-5, so nothing distinct is merged.
                        string key = p.x.ToString("F5") + "," + p.y.ToString("F5")
                            + "," + p.z.ToString("F5");
                        if (lookup.Add(key))
                        {
                            sites.Add(p);
                        }
                    }
                }
            }

            return sites;
        }

        /// <summary>
        /// Acceleration for the bake: for each coarse direction bin, pre-rank the sites
        /// and keep 16 as a STARTING GUESS. The texel then walks the neighbour graph
        /// to the true winner, so a coarse bin can never leave a wrong cell behind —
        /// which matters near r == 1 where the hemisphere direction changes fastest.
        /// </summary>
        private static int[][] BuildBins(List<Vector3> sites)
        {
            int n = sites.Count;
            int[][] bins = new int[BinCount * BinCount][];
            int[] order = new int[n];
            float[] scores = new float[n];

            for (int by = 0; by < BinCount; by++)
            {
                float py = -1f + 2f * (by + 0.5f) / BinCount;
                for (int bx = 0; bx < BinCount; bx++)
                {
                    float px = -1f + 2f * (bx + 0.5f) / BinCount;
                    float r2 = px * px + py * py;
                    Vector3 dir = r2 < 1f
                        ? new Vector3(px, py, Mathf.Sqrt(1f - r2))
                        : new Vector3(px, py, 0f).normalized;

                    for (int i = 0; i < n; i++)
                    {
                        order[i] = i;
                        scores[i] = Vector3.Dot(dir, sites[i]);
                    }

                    int take = Mathf.Min(Candidates, n);
                    for (int a = 0; a < take; a++)
                    {
                        int best = a;
                        for (int b = a + 1; b < n; b++)
                        {
                            if (scores[order[b]] > scores[order[best]])
                            {
                                best = b;
                            }
                        }

                        int tmp = order[a];
                        order[a] = order[best];
                        order[best] = tmp;
                    }

                    int[] kept = new int[take];
                    Array.Copy(order, kept, take);
                    bins[by * BinCount + bx] = kept;
                }
            }

            return bins;
        }
    }
}
