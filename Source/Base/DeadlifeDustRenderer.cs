using System;
using System.Diagnostics.CodeAnalysis;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Redraws deadlife dust with vanilla's own sprite look when the gas field stops
    /// shading it.
    ///
    /// The field is a single smooth volume; dust is the one gas whose appearance *is* its
    /// texture, so converting it to a smooth density cloud loses what makes it read as
    /// dust. This renderer therefore borrows vanilla's material outright instead of
    /// reimplementing the look: same <c>GasRotating</c> shader, same
    /// <c>Things/Gas/DeadlifeDust</c> texture, same per-cell quad, same vertex colour
    /// contract. Anything else would be an approximation of a shader we already have.
    ///
    /// The vertex colour is not a normal tint. The vertex stage computes
    /// <c>vDensityAlphas = inColor / sum(inColor)</c> and
    /// <c>vAlpha = mix(0.2, 1, sum(inColor) / 1020)</c>, i.e. it expects the raw 0..255
    /// densities, not Unity's usual normalised 0..1 colours. Writing (0,0,0,dust) is what
    /// makes a dust-only cell come out exactly as vanilla draws it: the shader normalises
    /// to (0,0,0,1), takes the dust branch, and scales alpha the same way. That is also
    /// why the mesh is written through a hand-built vertex stream — <c>Mesh.colors</c>
    /// would quantise to bytes and turn every quad into a fixed 20% alpha.
    ///
    /// No section bookkeeping is needed. Vanilla seeds each quad's jitter with
    /// <c>Rand.PushState(cellIndex)</c>, so the same cell always produces the same
    /// offsets, and the rotation is animated inside the shader from the time uniform.
    /// Rebuilding the whole visible set on a timer is therefore stable rather than jittery.
    /// </summary>
    internal sealed class DeadlifeDustRenderer : IDisposable
    {
        // Dust is emitted by corpses and dissipates slowly; nothing visible changes between
        // publishes at this cadence, and the mesh upload is the expensive part.
        private const float RebuildInterval = 0.1f;

        // Vanilla's own gas material queue. Above the field (2990) so dust is not dimmed by
        // the gas cloud it sits in, and below fog of war (3175), which covers it as usual.
        private const int RenderQueue = 3000;

        private static readonly float Altitude = Altitudes.AltitudeFor(AltitudeLayer.Gas);

        // Copies of the protected ranges SectionLayer_Gas uses for its quads. Kept as
        // literals because those members are protected and the values are part of the look.
        private static readonly FloatRange VertexScaleOffset = new FloatRange(0.4f, 0.6f);
        private static readonly FloatRange VertexPositionOffset = new FloatRange(-0.2f, 0.2f);
        private static readonly FloatRange VertexAltitudeOffset = new FloatRange(-0.01f, 0.01f);

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int RotationSpeedId = Shader.PropertyToID("_RotationSpeed");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        // Position, colour, uv. Colour and uv are Float32 on purpose: see the class comment.
        // The offsets used by UploadMesh (0, 12, 28) follow this order.
        private static readonly VertexAttributeDescriptor[] VertexLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 3),
        };

        private Mesh? mesh;
        private Material? material;
        private readonly MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();

        private Vector3[] positions = Array.Empty<Vector3>();
        private Color[] colors = Array.Empty<Color>();
        private Vector3[] uvs = Array.Empty<Vector3>();
        private int[] triangles = Array.Empty<int>();

        private int vertexCount;
        private int indexCount;
        private uint signature = uint.MaxValue;

        private int lastMinX = int.MinValue;
        private int lastMinZ = int.MinValue;
        private int lastWidth = -1;
        private int lastHeight = -1;
        private float lastRebuild = float.NegativeInfinity;
        private bool loggedDraw;
        private bool loggedStrideError;

        internal void Draw(Map map, CellRect view, float realtime)
        {
            if (!DebugViewSettings.drawGas)
            {
                return;
            }

            GasGrid? gasGrid = map.gasGrid;
            if (gasGrid == null || !gasGrid.CalculateGasEffects)
            {
                return;
            }

            EnsureResources();

            bool viewChanged = view.minX != lastMinX
                || view.minZ != lastMinZ
                || view.Width != lastWidth
                || view.Height != lastHeight;

            if (viewChanged || realtime - lastRebuild >= RebuildInterval)
            {
                Rebuild(map, gasGrid, view);
                lastMinX = view.minX;
                lastMinZ = view.minZ;
                lastWidth = view.Width;
                lastHeight = view.Height;
                lastRebuild = realtime;
            }

            if (vertexCount == 0)
            {
                return;
            }

            // Same channel vanilla's SectionLayer_Gas uses for its own quads. Passed per draw
            // through a property block rather than written on the material, which is the
            // path this exact shader is known to read the uniform from.
            propertyBlock.SetFloat(ShaderPropertyIDs.AgeSecsPausable, RealTime.UnpausedRealTime);
            Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0, null, 0, propertyBlock);

            VisualTweaksLog.TraceOnce(
                ref loggedDraw,
                "Deadlife dust drawn with the vanilla sprite layer ("
                    + vertexCount / 4 + " cells).");
        }

        [MemberNotNull(nameof(mesh), nameof(material))]
        private void EnsureResources()
        {
            if (mesh == null)
            {
                mesh = new Mesh { name = "VisualTweaksDeadlifeDust" };
                mesh.MarkDynamic();
            }

            if (material != null)
            {
                return;
            }

            // A private material, not MaterialPool's: the pooled one is the section layer's
            // and is shared with every other gas quad on the map.
            material = new Material(ShaderDatabase.GasRotating)
            {
                name = "VisualTweaksDeadlifeDustMat",
                renderQueue = RenderQueue,
            };

            // Vanilla binds the same cloud texture to all three gas slots. Their
            // contribution is multiplied by a zero vertex channel here, but leaving a
            // sampler unbound is not something to rely on.
            Texture2D cloud = ContentFinder<Texture2D>.Get("Things/Gas/GasCloudThickA");
            material.SetTexture(MainTexId, cloud);
            material.SetTexture(ShaderPropertyIDs.ToxGasTex, cloud);
            material.SetTexture(ShaderPropertyIDs.RotGasTex, cloud);
            material.SetTexture(
                ShaderPropertyIDs.DeadlifeDustTex,
                ContentFinder<Texture2D>.Get("Things/Gas/DeadlifeDust"));
            material.SetVector(ColorId, Vector4.one);
            material.SetFloat(RotationSpeedId, 1f);
        }

        /// <summary>
        /// Collects every visible cell that holds dust and rebuilds the quad set. A
        /// <paramref name="signature"/> that has not changed skips the upload: the arrays
        /// are cheap to refill, the vertex stream is not.
        /// </summary>
        private void Rebuild(Map map, GasGrid gasGrid, CellRect view)
        {
            CellRect clipped = view.ClipInsideMap(map);
            if (clipped.Width <= 0 || clipped.Height <= 0)
            {
                vertexCount = 0;
                indexCount = 0;
                return;
            }

            int mapWidth = map.Size.x;
            FogGrid? fogGrid = map.fogGrid;

            uint hash = 2166136261u;
            int quads = 0;

            for (int z = clipped.minZ; z <= clipped.maxZ; z++)
            {
                int row = z * mapWidth;
                for (int x = clipped.minX; x <= clipped.maxX; x++)
                {
                    int index = row + x;

                    // Dust is the top byte of the packed densities.
                    int dust = (int)(gasGrid.GetDirect(index) >> 24);
                    if (dust <= 0)
                    {
                        continue;
                    }

                    // Fog covers the layer anyway, but a quad behind it is pure fill cost.
                    if (fogGrid != null && fogGrid.IsFogged(index))
                    {
                        continue;
                    }

                    hash = (hash ^ (uint)index) * 16777619u;
                    hash = (hash ^ (uint)dust) * 16777619u;

                    AddQuad(quads, x, z, index, dust);
                    quads++;
                }
            }

            vertexCount = quads * 4;
            indexCount = quads * 6;

            if (vertexCount == 0)
            {
                signature = 0u;
                return;
            }

            hash = (hash ^ (uint)clipped.minX) * 16777619u;
            hash = (hash ^ (uint)clipped.minZ) * 16777619u;

            if (hash == signature)
            {
                return;
            }

            signature = hash;
            UploadMesh(clipped);
        }

        /// <summary>
        /// One vanilla gas quad: four corners expanded around the cell by a random amount,
        /// a small position and altitude jitter, and the uv z component carrying the cell
        /// index as the shader's per-quad animation phase.
        /// </summary>
        private void AddQuad(int quad, int x, int z, int index, int dust)
        {
            EnsureCapacity(quad + 1);

            // Same seeding as vanilla's AddCell, so the jitter is stable across rebuilds
            // and adjacent cells do not share a phase.
            Rand.PushState(index);
            float scale = VertexScaleOffset.RandomInRange;
            float offsetX = VertexPositionOffset.RandomInRange;
            float offsetZ = VertexPositionOffset.RandomInRange;
            float y = Altitude + VertexAltitudeOffset.RandomInRange;
            Rand.PopState();

            float x0 = x - scale + offsetX;
            float x1 = x + 1f + scale + offsetX;
            float z0 = z - scale + offsetZ;
            float z1 = z + 1f + scale + offsetZ;

            int v = quad * 4;
            positions[v] = new Vector3(x0, y, z0);
            positions[v + 1] = new Vector3(x0, y, z1);
            positions[v + 2] = new Vector3(x1, y, z1);
            positions[v + 3] = new Vector3(x1, y, z0);

            uvs[v] = new Vector3(0f, 0f, index);
            uvs[v + 1] = new Vector3(0f, 1f, index);
            uvs[v + 2] = new Vector3(1f, 1f, index);
            uvs[v + 3] = new Vector3(1f, 0f, index);

            // 0..255 floats; the vertex stage normalises by their sum.
            Color color = new Color(0f, 0f, 0f, dust);
            colors[v] = color;
            colors[v + 1] = color;
            colors[v + 2] = color;
            colors[v + 3] = color;
        }

        /// <summary>
        /// Grows to fit one more quad. Sized by what the window actually holds rather than
        /// by its area: a zoomed-out map covers hundreds of thousands of cells, and only a
        /// handful of them ever carry dust. Doubling keeps a spreading cloud from
        /// reallocating on every rebuild; Array.Resize keeps the quads already written.
        /// </summary>
        private void EnsureCapacity(int quads)
        {
            int needed = quads * 4;
            if (positions.Length >= needed)
            {
                return;
            }

            int vertices = Mathf.NextPowerOfTwo(Mathf.Max(needed, 256));
            Array.Resize(ref positions, vertices);
            Array.Resize(ref uvs, vertices);
            Array.Resize(ref colors, vertices);

            // The index pattern is identical for every quad, so only the newly added range
            // has to be written.
            int oldQuads = triangles.Length / 6;
            Array.Resize(ref triangles, vertices / 4 * 6);
            FillTriangles(oldQuads, vertices / 4);
        }

        private void FillTriangles(int fromQuad, int toQuad)
        {
            for (int i = fromQuad; i < toQuad; i++)
            {
                int v = i * 4;
                int t = i * 6;
                triangles[t] = v;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }
        }

        /// <summary>
        /// Writes the three streams by hand. Mesh.SetVertices and friends re-interleave the
        /// whole thing on the next draw and cannot express a Float32 colour at all, which is
        /// the one format the vanilla shader can read.
        /// </summary>
        private unsafe void UploadMesh(CellRect window)
        {
            if (mesh == null || vertexCount == 0)
            {
                return;
            }

            Mesh.MeshDataArray data = Mesh.AllocateWritableMeshData(1);
            Mesh.MeshData writable = data[0];
            writable.SetVertexBufferParams(vertexCount, VertexLayout);
            writable.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
            writable.subMeshCount = 1;

            Bounds bounds = new Bounds(
                new Vector3(window.minX + window.Width * 0.5f, Altitude, window.minZ + window.Height * 0.5f),
                new Vector3(window.Width + 2f, 16f, window.Height + 2f));
            writable.SetSubMesh(
                0,
                new SubMeshDescriptor(0, indexCount, MeshTopology.Triangles)
                {
                    bounds = bounds,
                    vertexCount = vertexCount,
                });

            int stride = writable.GetVertexBufferStride(0);

            // 3 floats of position + 4 of colour + 3 of uv, tightly packed (offsets 0, 12,
            // 28). The engine has always packed descriptors in declaration order with
            // natural alignment; if that ever changes, the hand-written offsets below would
            // scribble one attribute into the next and the dust would render as garbage.
            // Failing here says so once instead.
            if (stride != 40)
            {
                if (!loggedStrideError)
                {
                    loggedStrideError = true;
                    Log.Error(
                        "[Visual Tweaks] Deadlife dust: unexpected vertex stride "
                        + stride + " (expected 40); sprite pass disabled.");
                }

                data.Dispose();
                return;
            }

            byte* stream = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(
                writable.GetVertexData<byte>(0));

            for (int i = 0; i < vertexCount; i++)
            {
                byte* vertex = stream + i * stride;

                Vector3 position = positions[i];
                *(float*)vertex = position.x;
                *(float*)(vertex + 4) = position.y;
                *(float*)(vertex + 8) = position.z;

                Color color = colors[i];
                *(float*)(vertex + 12) = color.r;
                *(float*)(vertex + 16) = color.g;
                *(float*)(vertex + 20) = color.b;
                *(float*)(vertex + 24) = color.a;

                Vector3 uv = uvs[i];
                *(float*)(vertex + 28) = uv.x;
                *(float*)(vertex + 32) = uv.y;
                *(float*)(vertex + 36) = uv.z;
            }

            int* indices = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(
                writable.GetIndexData<int>());
            for (int i = 0; i < indexCount; i++)
            {
                indices[i] = triangles[i];
            }

            Mesh.ApplyAndDisposeWritableMeshData(data, mesh);
            mesh.bounds = bounds;
        }

        public void Dispose()
        {
            if (mesh != null)
            {
                UnityEngine.Object.Destroy(mesh);
                mesh = null;
            }

            if (material != null)
            {
                UnityEngine.Object.Destroy(material);
                material = null;
            }

            positions = Array.Empty<Vector3>();
            colors = Array.Empty<Color>();
            uvs = Array.Empty<Vector3>();
            triangles = Array.Empty<int>();
            vertexCount = 0;
            indexCount = 0;
            signature = uint.MaxValue;
            lastMinX = int.MinValue;
            lastMinZ = int.MinValue;
            lastWidth = -1;
            lastHeight = -1;
            lastRebuild = float.NegativeInfinity;
            loggedDraw = false;
            loggedStrideError = false;
        }
    }
}
