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
    /// times gives V = 10k^2+2 sites; k=2 gives 42 cells, k=8 gives 642. Low k reads
    /// like a football, high k reads like an energy mesh — hence the wider range.
    ///
    /// Doing that per fragment means 42..642 dot products per pixel, over a quad that
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
    /// lives, which makes fwidth() step and the outline shimmer. This mod already
    /// learned that lesson once with the dissolve's lattice hash: a float64 preview
    /// hides what float32 on the GPU does. Half floats give ~0.0005 of edge
    /// precision, and unlike full RGBAFloat, linear filtering is universally
    /// supported on desktop Vulkan — which matters because the lines are
    /// reconstructed by bilinear sampling.
    ///
    /// ---- cost -------------------------------------------------------------
    ///
    /// Built lazily, only when the hexagonal style is actually enabled. 1024^2
    /// texels x ~16 candidate dots (see the bin acceleration below) is a couple of
    /// hundred milliseconds, once per density choice, and 8 MiB of VRAM for one
    /// texture shared by every shield. No per-frame CPU work, no extra draw calls,
    /// no extra geometry. The resolution is what lets k=8 still draw a clean line
    /// after the texture is magnified onto a large dome.
    /// </summary>
    internal static class ShieldHexMap
    {
        internal const int MinSubdivisions = 2;
        internal const int MaxSubdivisions = 8;

        /// <summary>
        /// eRef = ERefPerSubdivision / k is the angular distance that maps to
        /// edgeDistance 1.0. The shader needs its RECIPROCAL to rebuild the field
        /// gradient analytically (1/(eRef*z)), so the constant is exposed rather
        /// than duplicated — the two must not drift apart.
        /// </summary>
        internal const float ERefPerSubdivision = 0.62f;

        private const int Size = 1024;
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
        /// actually changes (the density slider is quantised to integer k).
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
                boundTo = null;
            }

            // SetTexture is not free, so only touch the material when the texture
            // itself changed or when this material has not been bound yet.
            if (texture != null && (boundTo != material || boundFor != k))
            {
                material.SetTexture(MapId, texture);
                boundTo = material;
                boundFor = k;
            }
        }

        private static void Build(int k)
        {
            try
            {
                List<Vector3> sites = BuildSites(k);
                int n = sites.Count;

                // Chord distance between every pair of sites. The Voronoi boundary
                // between sites i and j is the plane n.(si-sj) = 0; dividing the dot
                // difference by |si-sj| turns it into a distance in the same units as
                // the disc, so every cell line keeps the same thickness.
                float[][] separation = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    separation[i] = new float[n];
                    for (int j = 0; j < n; j++)
                    {
                        separation[i][j] = Mathf.Max(
                            (sites[i] - sites[j]).magnitude,
                            1e-4f);
                    }
                }

                int[][] bins = BuildBins(sites);
                int[][] neighbours = BuildNeighbours(sites);

                // At a cell centre the SDF is roughly sin(theta/2) for the site
                // spacing theta, and theta scales as 1/k. The exact scale is not
                // important to the shader (line width is measured with fwidth), but
                // keeping most cells near 1 avoids clipping them flat.
                float eRef = ERefPerSubdivision / k;

                Color[] pixels = new Color[Size * Size];

                for (int y = 0; y < Size; y++)
                {
                    float py = -1f + 2f * (y + 0.5f) / Size;
                    int binRow = Mathf.Clamp(
                        (int)((py * 0.5f + 0.5f) * BinCount),
                        0,
                        BinCount - 1);

                    for (int x = 0; x < Size; x++)
                    {
                        float px = -1f + 2f * (x + 0.5f) / Size;
                        int index = y * Size + x;

                        float r2 = px * px + py * py;
                        if (r2 >= 1f)
                        {
                            // Outside the disc the shader's silhouette clip is already
                            // zero, so any value is fine. Face normal straight up.
                            pixels[index] = new Color(0.5f, 0.5f, 1f, 0f);
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
                        // across one bin that its pre-ranked sites can miss the true
                        // winner (measured: ~1% of pixels wrong at k=8). Walk up the
                        // precomputed neighbour graph until no neighbour is closer;
                        // on a triangular lattice this converges in a step or two.
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
                        // Taking the minimum of continuous functions keeps the field
                        // smooth where the nearest competitor changes.
                        float best = 1e9f;
                        int[] walls = neighbours[i1];
                        for (int q = 0; q < walls.Length; q++)
                        {
                            int j = walls[q];
                            float wall = (d1 - Vector3.Dot(dir, sites[j]))
                                / separation[i1][j];
                            if (wall < best)
                            {
                                best = wall;
                            }
                        }

                        float e = Mathf.Clamp01(best / eRef);
                        Vector3 face = sites[i1];

                        pixels[index] = new Color(
                            face.x * 0.5f + 0.5f,
                            face.y * 0.5f + 0.5f,
                            face.z * 0.5f + 0.5f,
                            e);
                    }
                }

                Texture2D created = new Texture2D(Size, Size, TextureFormat.RGBAHalf, false)
                {
                    name = "VisualTweaksShieldHexMap",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                created.SetPixels(pixels);
                created.Apply(false, true);

                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }

                texture = created;
                builtFor = k;
                failedFor = -1;
                available = true;
            }
            catch (Exception ex)
            {
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
