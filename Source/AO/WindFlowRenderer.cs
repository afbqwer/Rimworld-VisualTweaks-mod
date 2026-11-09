using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Map wind visualization with three styles: comet streaks, fine streamlines, and
    /// particle trails. Streamline styles integrate true paths through AO's velocity
    /// snapshot; the particle style moves world-anchored particles along the field and
    /// draws a short, life-faded trail centered on each one, like AO's original layer
    /// but with fade-in / fade-out.
    /// </summary>
    internal sealed class WindFlowRenderer : IDisposable
    {
        private const int BucketCount = 4;
        private const int MaxStepsPerDirection = 56;
        private const int MaxPathPoints = MaxStepsPerDirection * 2 + 8;
        private const float StepCells = 0.6f;
        private const float DashCyclesPerSecond = 0.5f;

        // Comet streaks: short, dense, directional. Fine streamlines: longer and softer.
        private const float CometSeedSpacingCells = 1.7f;
        private const float FineSeedSpacingCells = 2.2f;
        private const int CometMaxSeeds = 800;
        private const int FineMaxSeeds = 520;
        private const float CometLineLengthPixels = 100f;
        private const float FineLineLengthPixels = 170f;
        private const float CometGuideWidthPixels = 2.6f;
        private const float FineGuideWidthPixels = 3.4f;
        private const float CometDashWidthPixels = 3.0f;
        private const float FineDashWidthPixels = 4.0f;
        private const float CometDashPeriodPixels = 20f;
        private const float FineDashPeriodPixels = 26f;
        private const float CometDashDuty = 0.58f;
        private const float FineDashDuty = 0.72f;

        // Particle trails: AO-like lifetime particles with soft head and fading tail.
        private const int ParticleBaseCount = 850;
        private const int ParticleMaxCount = 3200;
        private const int ParticleInstantSpawnCap = 4000;

        // Trail length follows the wind speed, the same rule Gunplay uses for
        // projectiles (length = speed × factor). The factor is in cells per (cell/s),
        // i.e. how long a wake the particle drags behind it; 0.35 is about a sixth of
        // a second of travel, so a jet leaves a long streak and still air a stub.
        private const float TrailCellsPerSpeed = 0.35f;
        private const float TrailMinCells = 0.5f;
        private const float TrailMaxCells = 6.5f;

        // Screen-space floor: zooming far out would otherwise shrink every streak below
        // a couple of pixels and make the layer disappear.
        private const float TrailMinPixels = 10f;

        // Reference wind for the "how much does speed change the trail length" slider.
        // When its influence is turned down, every trail approaches the length it would
        // have at this speed, so calm and windy areas look alike. 5 m/s is a typical
        // open-field wind in AO's units.
        private const float TrailSpeedReference = 5f;

        private const float ParticleWidthPixels = 4.2f;
        private const float ParticleMinLife = 2.4f;
        private const float ParticleMaxLife = 4.6f;
        private const float ParticleFadeInSeconds = 0.35f;
        private const float ParticleFadeInCells = 1.2f;
        private const float ParticleFadeOutSeconds = 1.1f;

        // A trail that runs into a wall, a fogged cell or the cull edge has no time left to
        // fade out on its own, so it is dissolved over this window instead of popping out.
        private const float ParticleDeathFadeSeconds = 0.3f;

        // Time constant for the low-wind hide slider. Speed changes are smoothed per
        // particle so a gust crossing the threshold fades the trail instead of blinking.
        private const float ParticleVisibilityFadeSeconds = 0.35f;
        private const float ParticleMotionSpeedScale = 2.6f;
        private const float ParticleSpawnSpeedReference = 2f;
        private const float ParticleDensityArea = 1200f;

        // RimWorld starts at CameraDriver RootSize 24. The target count is calibrated to
        // the density the old per-area formula produced at that zoom, then held constant
        // as the camera zooms so the on-screen density does not change.
        private const float ParticleDefaultRootSize = 24f;

        // Density is managed by a coarse occupancy grid: each block in the view has a
        // particle quota. Spawns only go into blocks below quota, so the layer stays
        // uniform without inlet/outlet bookkeeping. The block size adapts to the zoom so
        // the grid keeps roughly ParticleTargetBlocks cells and each block a useful
        // number of particles instead of degenerating to 0/1 at far zoom.
        private const float ParticleTargetBlocks = 64f;
        private const float ParticleCullMarginCells = 1.5f;
        private const float ParticleSpawnFractionPerTick = 0.08f;

        // Trail geometry: the ribbon is rebuilt by walking upstream from the particle
        // head, re-reading the field at every joint. The joint count is picked per trail
        // from its on-screen length - one joint per ArcSegmentPixels - because a short
        // trail at default zoom does not need as many as a long one at max zoom. Both the
        // field taps and the vertex count scale with it, so this is the main cost knob.
        private const int ArcTrailSegments = 8;
        private const int ArcMinSegments = 3;
        private const float ArcSegmentPixels = 12f;

        // How many joints share one field sample. 1 samples the field at every joint and
        // is the look this layer was tuned with; 2 halves the taps (measured 320 -> 230 us
        // for 1451 particles) and makes the ribbon hold a direction for one extra joint.
        // It is left at 1 on purpose: that is a visual call, not a performance one.
        private const int ArcSamplingStride = 1;

        // Below this many particles the plan + dispatch of the pool costs more than the
        // emit it would parallelise.
        private const int ParallelEmitThreshold = 256;

        // Cap the emit at four workers. Measured on 1451 particles: 4 workers gave 2.98x,
        // 8 gave 3.42x and 16 gave 3.63x, because the serial planning pass and the dispatch
        // cost are the floor long before core count is. Four keeps the gain while leaving
        // the rest of the machine to the render thread and other mods.
        private const int EmitMaxDegreeOfParallelism = 4;

        private static readonly ParallelOptions EmitParallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(
                1,
                Math.Min(EmitMaxDegreeOfParallelism, Environment.ProcessorCount)),
        };

        // ---- experimental GPU ribbon path --------------------------------------------
        // The CPU packs twelve floats per particle into a storage buffer; the vertex
        // shader rebuilds the whole ribbon from that plus the per-cell wind field, so
        // no vertex or index data is ever uploaded after the static mesh is built.
        private const string GpuShaderDefName = "VisualTweaksAOWindGpu";
        private const string GpuVertexShaderFile = "VisualTweaksAOWindGpu.vert.glsl";
        private const string GpuFragmentShaderFile = "VisualTweaksAOWindGpu.frag.glsl";
        private const int GpuVerticesPerParticle = (ArcTrailSegments + 1) * 2;
        private const int GpuFloatsPerParticle = 12;
        private const int GpuParticleCapacity = ParticleMaxCount;

        private static readonly int GpuParamsId = Shader.PropertyToID("_Params");
        private static readonly int GpuParams2Id = Shader.PropertyToID("_Params2");

        // The ribbon narrows towards the tail. Width is in units of the particle's
        // half width, so the slider still scales the whole streak.
        private const float ArcTailWidthFactor = 0.50f;
        private const float ArcHeadWidthFactor = 0.90f;

        // How strongly each upstream joint blends towards the freshly sampled wind
        // direction. Pure snapping zig-zags on the per-cell noise in AO's snapshot.
        private const float ArcDirectionBlend = 0.65f;

        private const int DashTextureWidth = 64;
        private const int DashTextureHeight = 8;

        // The Gunplay-style silhouette needs more texels across the width than the
        // older bell profile: its flat core is a real plateau, and at 8 texels a
        // plateau plus a soft edge band collapses back into a smooth hill.
        private const int ParticleTextureWidth = 64;
        private const int ParticleTextureHeight = 16;

        // Drawn *under* the map overlays instead of over them. RimWorld's overlay
        // materials (fog of war, darkness, snow, shadow masks) sit at the default
        // transparent queue of 3000, so anything below that is painted first and is then
        // covered by them: fogged cells hide the streaks by themselves instead of needing
        // to be skipped, and night darkness dims them the way it dims the terrain.
        // Anything above 3000 draws on top of the fog again.
        private const int BaseRenderQueue = 2990;
        private const int DashRenderQueue = 2991;

        // One AltInc below AltitudeLayer.FogOfWar, i.e. between the visual effects and the
        // fog quad. Only matters for depth-tested passes; the queue above does the
        // ordering for alpha-blended geometry.
        private const float FogLayerOffset = -Altitudes.AltInc;

        private const float DashAltitudeOffset = 0.003f;

        private static readonly Color[] BucketColors =
        {
            new Color(0.45f, 0.78f, 1.00f, 0.55f),
            new Color(0.35f, 0.95f, 0.82f, 0.62f),
            new Color(0.95f, 0.88f, 0.45f, 0.68f),
            new Color(1.00f, 0.55f, 0.45f, 0.72f)
        };

        private static readonly Color BaseColor = new Color(0.60f, 0.82f, 1.00f, 0.14f);

        // Neutral particle tint, matching the average alpha of the speed buckets so
        // switching the colour off does not also change the trails' weight.
        private static readonly Color WhiteParticleColor = new Color(1.00f, 1.00f, 1.00f, 0.62f);

        private readonly Mesh[] meshes = new Mesh[BucketCount];
        private readonly Material[] materials = new Material[BucketCount];
        private readonly List<Vector3>[] vertices = new List<Vector3>[BucketCount];
        private readonly List<Color32>[] colors = new List<Color32>[BucketCount];
        private readonly List<Vector2>[] uvs = new List<Vector2>[BucketCount];
        private readonly List<int>[] triangles = new List<int>[BucketCount];

        private readonly List<Vector3> baseVertices = new List<Vector3>(8192);
        private readonly List<Color32> baseColors = new List<Color32>(8192);
        private readonly List<Vector2> baseUvs = new List<Vector2>(8192);
        private readonly List<int> baseTriangles = new List<int>(16384);

        private readonly List<FlowPoint> forward = new List<FlowPoint>(128);
        private readonly List<FlowPoint> backward = new List<FlowPoint>(128);
        private readonly List<FlowPoint> line = new List<FlowPoint>(256);
        private readonly float[] pointArcs = new float[MaxPathPoints];
        private readonly List<Particle> particles = new List<Particle>(ParticleMaxCount);

        // Scratch space for one arcing trail. The particle emit phase runs on the thread
        // pool, so this cannot be instance state.
        // Per-thread scratch, allocated on first use by EnsureThreadScratch. On a thread
        // that has never emitted they are genuinely null, so the type says so.
        [ThreadStatic] private static Vector3[]? threadArcCenters;
        [ThreadStatic] private static Vector3[]? threadArcNormals;
        [ThreadStatic] private static Vector3[]? threadArcDirections;

        // Particle geometry is written into these flat arrays instead of the per-bucket
        // lists. Each particle owns a disjoint slice (planned up front), so the emit phase
        // can run on any number of threads without locking, and the upload reads spans with
        // no per-vertex List.Add.
        private Vector3[] flatVertices = Array.Empty<Vector3>();
        private Color32[] flatColors = Array.Empty<Color32>();
        private Vector2[] flatUvs = Array.Empty<Vector2>();
        private int[] flatIndices = Array.Empty<int>();
        private int[] particleVertexOffset = Array.Empty<int>();
        private int[] particleIndexOffset = Array.Empty<int>();
        private int[] particleSegments = Array.Empty<int>();
        private int[] particleBucket = Array.Empty<int>();
        private float[] particleLength = Array.Empty<float>();
        private Color32[] particleColor = Array.Empty<Color32>();
        private readonly int[] bucketVertexStart = new int[BucketCount];
        private readonly int[] bucketVertexCount = new int[BucketCount];
        private readonly int[] bucketVertexCursor = new int[BucketCount];
        private readonly int[] bucketIndexStart = new int[BucketCount];
        private readonly int[] bucketIndexCount = new int[BucketCount];
        private readonly int[] bucketIndexCursor = new int[BucketCount];
        // Set lazily on the first parallel emit (`??=`), so nullable is the honest type.
        private Action<int>? emitSliceAction;
        private float emitHalfWidth;
        private float emitAltitude;

        private static readonly Color32 WhiteColor = new Color32(255, 255, 255, 255);

        private Mesh? baseMesh;
        private Material? baseMaterial;
        private Texture2D? lineTexture;
        private Texture2D? cometTexture;
        private Texture2D? fineTexture;
        private Texture2D? particleTexture;

        private bool fineStyle;
        private bool particleStyle;
        private bool smoothField;
        private bool showGuide;
        private float densityScale;
        private float lengthScale;
        private float speedLengthInfluence = 1f;
        private float widthScale;
        private float opacityScale;
        private bool coloredParticles;
        private float lowWindHideThreshold;
        private float animationScale;
        private float seedSpacingCells;
        private int maxSeeds;
        private float lineLengthPixels;
        private float guideWidthPixels;
        private float dashWidthPixels;
        private float dashPeriodPixels;
        private float dashDuty;

        private float dashPhase;
        private float lastRealtime = -1f;
        private int appliedMaterialStyle = -1;
        private float appliedMaterialOpacity = -1f;
        private bool appliedColoredParticles;
        private float particleLastRealtime = -1f;
        private uint particleRng = 0x9E3779B9u;
        private bool particleLogged;
        private bool wasParticleStyle;
        private bool particleViewValid;
        private int particleViewMinX;
        private int particleViewMinZ;
        private int particleViewWidth;
        private int particleViewHeight;
        private int[] particleBlockCounts = Array.Empty<int>();
        private int particleBlockCountX;
        private int particleBlockCountZ;
        private float particleBlockMinX;
        private float particleBlockMinZ;
        private float particleBlockCells = 4f;
        private int[] particleBlockCapacities = Array.Empty<int>();
        private bool[] particleBlockBlocked = Array.Empty<bool>();
        private bool particleBlockCapacitiesValid;
        private int particleBlockCacheViewMinX;
        private int particleBlockCacheViewMinZ;
        private int particleBlockCacheViewWidth;
        private int particleBlockCacheViewHeight;
        private int particleBlockCacheMapWidth;
        private int particleBlockCacheMapHeight;
        private float particleBlockCacheDensity;
        private int particleBlockCacheTarget;
        private int particleLastTargetCount = -1;
        private float particleTrimCellPixels = -1f;
        private readonly List<int> particleSpawnBlocks = new List<int>(2048);
        private int[] particleBlockRemovals = Array.Empty<int>();

        // GPU path state. The particle buffer is the only thing rewritten every rebuild;
        // the three field buffers change only when AO publishes a new snapshot.
        private bool gpuRequested;
        private bool gpuReady;
        private bool gpuFailed;
        private Material? gpuMaterial;
        private Mesh? gpuMesh;
        private ComputeBuffer? gpuParticleBuffer;
        private ComputeBuffer? gpuFieldVxBuffer;
        private ComputeBuffer? gpuFieldVzBuffer;
        private ComputeBuffer? gpuFieldMaskBuffer;
        private readonly MaterialPropertyBlock gpuProperties = new MaterialPropertyBlock();
        private float[] gpuRecords = Array.Empty<float>();
        private int gpuActiveCount;
        private int gpuFieldWidth = -1;
        private int gpuFieldHeight = -1;
        private int gpuFieldVersion = int.MinValue;
        private float[] gpuFieldVxSource = Array.Empty<float>();
        private float[] gpuFieldVzSource = Array.Empty<float>();
        private float[] gpuFieldMaskSource = Array.Empty<float>();
        private float gpuAppliedOpacity = -1f;
        private bool gpuLoggedDraw;

        private int width;
        private int height;
        // The published snapshot. An empty array is the honest "nothing published yet"
        // value: PublishField's size check already rejects it, so no branch changes.
        private float[] velocityX = Array.Empty<float>();
        private float[] velocityZ = Array.Empty<float>();
        private float[] obstacleMask = Array.Empty<float>();

        // The five-tap smoothing used to run per sample, i.e. five bilinear lookups for
        // every tap of every integration step. It is a fixed convolution kernel, so it is
        // cheaper to blur the whole field once per published snapshot (AO publishes every
        // six ticks) and then do a single tap per sample. Measured 3.4x on a full rebuild;
        // see VisualTweaks-Perf-Analysis.md section 10.
        private float[] smoothVelocityX = Array.Empty<float>();
        private float[] smoothVelocityZ = Array.Empty<float>();
        private float[] smoothScratch = Array.Empty<float>();
        private int smoothWidth = -1;
        private int smoothHeight = -1;
        private int smoothVersion = int.MinValue;
        private bool smoothReady;
        private float[] smoothSourceX = Array.Empty<float>();
        private float[] smoothSourceZ = Array.Empty<float>();

        private bool layerLogged;
        private float baseAltitude;
        private float dashAltitude;
        private float baseHalfWidth;
        private float dashHalfWidth;
        private float dashPeriodCells;
        private float pathMinX;
        private float pathMaxX;
        private float pathMinZ;
        private float pathMaxZ;

        private int lastLineCount;
        private int lastSegmentCount;
        private int lastDashCount;
        private bool buildLogged;

        internal WindFlowRenderer()
        {
            particleRng = unchecked((uint)Environment.TickCount * 2654435761u);
            if (particleRng == 0u)
            {
                particleRng = 0x9E3779B9u;
            }

            for (int i = 0; i < BucketCount; i++)
            {
                vertices[i] = new List<Vector3>(4096);
                colors[i] = new List<Color32>(4096);
                uvs[i] = new List<Vector2>(4096);
                triangles[i] = new List<int>(8192);
            }
        }

        internal void Draw(
            Map map,
            float[] velocityX,
            float[] velocityZ,
            float[] obstacleMask,
            CellRect view,
            float cellPixels,
            float realtime,
            bool rebuild,
            float timeScale,
            int fieldVersion)
        {
            ApplySettings();
            EnsureResources();
            UpdateMaterials();
            UpdateAnimation(realtime, timeScale);

            // While frozen the component skips the rebuild entirely, so nothing else
            // refreshes the particle clock. Pin it on every frozen frame: otherwise the
            // first frame after un-pausing treats the whole pause as one delta and
            // fast-forwards every particle by up to 0.15 s of travel in a single step.
            if (timeScale <= 0.0001f)
            {
                particleLastRealtime = realtime;
            }

            if (rebuild)
            {
                if (particleStyle)
                {
                    UpdateParticles(
                        map,
                        velocityX,
                        velocityZ,
                        obstacleMask,
                        view,
                        cellPixels,
                        realtime,
                        timeScale,
                        fieldVersion);
                }
                else
                {
                    Build(map, velocityX, velocityZ, obstacleMask, view, cellPixels, fieldVersion);
                }
            }

            if (!particleStyle && showGuide && baseMesh != null && baseMaterial != null && baseVertices.Count > 0)
            {
                Graphics.DrawMesh(baseMesh, Matrix4x4.identity, baseMaterial, 0);
            }

            if (particleStyle && gpuReady && gpuRequested)
            {
                // One draw for the whole layer: the vertex shader expands every ribbon
                // from the storage buffers, so the per-bucket meshes stay untouched.
                if (gpuMesh != null && gpuMaterial != null && gpuActiveCount > 0)
                {
                    Graphics.DrawMesh(
                        gpuMesh,
                        Matrix4x4.identity,
                        gpuMaterial,
                        0,
                        null,
                        0,
                        gpuProperties);
                }

                return;
            }

            for (int i = 0; i < BucketCount; i++)
            {
                if (meshes[i] != null && materials[i] != null && HasBucketGeometry(i))
                {
                    Graphics.DrawMesh(meshes[i], Matrix4x4.identity, materials[i], 0);
                }
            }
        }

        private void ApplySettings()
        {
            fineStyle = VisualTweaksSettings.FineStyle;
            particleStyle = VisualTweaksSettings.ParticleStyle;
            gpuRequested = VisualTweaksSettings.gpuParticleGeometry;
            smoothField = VisualTweaksSettings.smoothField;
            showGuide = VisualTweaksSettings.showGuideLine;
            densityScale = Mathf.Clamp(
                VisualTweaksSettings.density,
                VisualTweaksSettings.MinDensity,
                VisualTweaksSettings.MaxDensity);
            lengthScale = Mathf.Clamp(VisualTweaksSettings.streakLength, 0.5f, 2f);
            speedLengthInfluence = Mathf.Clamp01(VisualTweaksSettings.speedLengthInfluence);
            widthScale = Mathf.Clamp(VisualTweaksSettings.lineWidth, 0.5f, 2f);
            opacityScale = Mathf.Clamp(
                VisualTweaksSettings.opacity,
                VisualTweaksSettings.MinOpacity,
                VisualTweaksSettings.MaxOpacity);

            coloredParticles = VisualTweaksSettings.coloredParticles;
            animationScale = Mathf.Clamp(VisualTweaksSettings.animationSpeed, 0f, 2f);
            lowWindHideThreshold = Mathf.Clamp(
                VisualTweaksSettings.particleLowWindThreshold,
                0f,
                VisualTweaksSettings.MaxLowWindThreshold);

            if (particleStyle != wasParticleStyle)
            {
                wasParticleStyle = particleStyle;
                particles.Clear();
                particleLastRealtime = -1f;
                particleLogged = false;
                particleViewValid = false;
                ClearGeometry();
            }

            // Both ceilings have to travel with the density range. At the old floor of 150
            // seeds the comet style saturated at density 0.19 and the fine style at 0.29,
            // so the bottom of the slider did nothing. The 0.01 floor needs a 220-cell
            // spacing and a 5-seed ceiling, so both clamps sit a little past that; the
            // whole slider stays live for every style.
            seedSpacingCells = Mathf.Clamp(
                (fineStyle ? FineSeedSpacingCells : CometSeedSpacingCells) / densityScale,
                0.7f,
                240f);
            maxSeeds = Mathf.Clamp(
                Mathf.RoundToInt((fineStyle ? FineMaxSeeds : CometMaxSeeds) * densityScale),
                4,
                1400);
            lineLengthPixels = (fineStyle ? FineLineLengthPixels : CometLineLengthPixels) * lengthScale;
            guideWidthPixels = (fineStyle ? FineGuideWidthPixels : CometGuideWidthPixels) * widthScale;
            dashWidthPixels = (fineStyle ? FineDashWidthPixels : CometDashWidthPixels) * widthScale;
            dashPeriodPixels = fineStyle ? FineDashPeriodPixels : CometDashPeriodPixels;
            dashDuty = fineStyle ? FineDashDuty : CometDashDuty;
        }

        private void UpdateMaterials()
        {
            EnsureResources();

            int styleKey = particleStyle ? 2 : (fineStyle ? 1 : 0);
            if (styleKey == appliedMaterialStyle
                && Mathf.Approximately(opacityScale, appliedMaterialOpacity)
                && coloredParticles == appliedColoredParticles)
            {
                return;
            }

            appliedMaterialStyle = styleKey;
            appliedMaterialOpacity = opacityScale;
            appliedColoredParticles = coloredParticles;

            Texture2D desired = particleStyle
                ? particleTexture
                : (fineStyle ? fineTexture : cometTexture);

            for (int i = 0; i < BucketCount; i++)
            {
                Material material = materials[i];
                if (material == null)
                {
                    continue;
                }

                if (material.mainTexture != desired)
                {
                    material.mainTexture = desired;
                }

                Color color = particleStyle && !coloredParticles
                    ? WhiteParticleColor
                    : BucketColors[i];

                color.a = Mathf.Clamp01(color.a * opacityScale);
                material.color = color;
            }

            if (baseMaterial != null)
            {
                Color color = BaseColor;
                color.a = Mathf.Clamp01(color.a * opacityScale);
                baseMaterial.color = color;
            }
        }

        private void UpdateAnimation(float realtime, float timeScale)
        {
            // Same rule as the particle clock: a gap longer than half a second means the
            // map was not drawn (map switch, load, pause) rather than a slow frame, so do
            // not carry it into the dash phase.
            float gap = realtime - lastRealtime;
            float deltaTime = lastRealtime < 0f || gap > 0.5f
                ? 0f
                : Mathf.Clamp(gap, 0f, 0.2f);
            lastRealtime = realtime;
            dashPhase += deltaTime * timeScale * DashCyclesPerSecond * animationScale;
            dashPhase -= Mathf.Floor(dashPhase);
        }

        private bool PrepareField(
            Map map,
            float[] velocityXArray,
            float[] velocityZArray,
            float[] obstacleMaskArray,
            int fieldVersion)
        {
            velocityX = velocityXArray;
            velocityZ = velocityZArray;
            obstacleMask = obstacleMaskArray;
            width = map.Size.x;
            height = map.Size.z;

            int cellCount = width * height;
            bool valid = width >= 3
                && height >= 3
                && velocityX.Length >= cellCount
                && velocityZ.Length >= cellCount
                && obstacleMask.Length >= cellCount;

            if (!valid)
            {
                smoothReady = false;
                return false;
            }

            EnsureSmoothedField(fieldVersion);
            return true;
        }

        /// <summary>
        /// Rebuilds the pre-smoothed velocity field, but only when AO publishes a new
        /// snapshot or the map size changes. Every sample taken with smoothField on then
        /// costs one bilinear tap instead of five.
        /// </summary>
        private void EnsureSmoothedField(int fieldVersion)
        {
            // Particles sample the raw field only (their ribbons are back-traced with a
            // single tap each), so blurring it for them would be pure waste.
            if (!smoothField || particleStyle)
            {
                smoothReady = false;
                return;
            }

            int cellCount = width * height;

            if (smoothVelocityX.Length < cellCount)
            {
                smoothVelocityX = new float[cellCount];
                smoothVelocityZ = new float[cellCount];
                smoothScratch = new float[cellCount];
                smoothVersion = int.MinValue;
            }

            // Two independent invalidation signals: AO's step counter, and the snapshot
            // arrays themselves (AO swaps buffers on publish, so a reference change means
            // new data even if the counter is unavailable).
            bool sourceChanged = !ReferenceEquals(smoothSourceX, velocityX)
                || !ReferenceEquals(smoothSourceZ, velocityZ);

            if (smoothReady
                && !sourceChanged
                && smoothVersion == fieldVersion
                && smoothWidth == width
                && smoothHeight == height)
            {
                return;
            }

            BlurField(velocityX, smoothVelocityX);
            BlurField(velocityZ, smoothVelocityZ);

            smoothVersion = fieldVersion;
            smoothWidth = width;
            smoothHeight = height;
            smoothSourceX = velocityX;
            smoothSourceZ = velocityZ;
            smoothReady = true;
        }

        /// <summary>
        /// Separable 3-tap blur (0.25 / 0.5 / 0.25) with clamped edges. This replaces the
        /// previous per-sample five-tap kernel: same smoothing intent, one pass per
        /// published snapshot instead of five lookups per sample.
        /// </summary>
        private void BlurField(float[] source, float[] destination)
        {
            int w = width;
            int h = height;
            float[] scratch = smoothScratch;

            for (int z = 0; z < h; z++)
            {
                int row = z * w;
                scratch[row] = 0.25f * source[row] + 0.75f * source[row + 1];
                scratch[row + w - 1] = 0.25f * source[row + w - 2] + 0.75f * source[row + w - 1];

                for (int x = 1; x < w - 1; x++)
                {
                    int i = row + x;
                    scratch[i] = 0.25f * source[i - 1] + 0.5f * source[i] + 0.25f * source[i + 1];
                }
            }

            int lastRow = (h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                destination[x] = 0.25f * scratch[x] + 0.75f * scratch[w + x];
                destination[lastRow + x] = 0.25f * scratch[lastRow - w + x]
                    + 0.75f * scratch[lastRow + x];
            }

            for (int z = 1; z < h - 1; z++)
            {
                int row = z * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    destination[i] = 0.25f * scratch[i - w] + 0.5f * scratch[i]
                        + 0.25f * scratch[i + w];
                }
            }
        }

        /// <summary>
        /// AO-like lifetime particles. Each particle advances through the wind field and
        /// drags a wake behind it, drawn as a ribbon that is back-traced through the
        /// same field so it bends with the flow. Alpha follows a smooth fade-in on
        /// spawn and a longer fade-out before respawning, which AO's original layer
        /// lacks.
        ///
        /// Density is managed per coarse cell: every cell in the view has a fixed
        /// particle quota, and particles only spawn into cells below that quota. The
        /// field is a statistical sample of the wind, not a conserved fluid, so there is
        /// no inlet/outlet bookkeeping. Deaths and view changes are refilled locally,
        /// which keeps density uniform and avoids both upwind drainage and off-screen
        /// clumping.
        /// </summary>
        private void UpdateParticles(
            Map map,
            float[] velocityXArray,
            float[] velocityZArray,
            float[] obstacleMaskArray,
            CellRect view,
            float cellPixels,
            float realtime,
            float timeScale,
            int fieldVersion)
        {
            ClearGeometry();

            if (!PrepareField(map, velocityXArray, velocityZArray, obstacleMaskArray, fieldVersion))
            {
                particles.Clear();
                return;
            }

            // A gap this large is not a frame: it is a pause, a map switch or a load that
            // skipped drawing. Simulating it would teleport every particle, so treat it as
            // "no time passed" instead of catching up.
            float gap = realtime - particleLastRealtime;
            float realDelta = particleLastRealtime < 0f || gap > 0.5f
                ? 0f
                : Mathf.Clamp(gap, 0f, 0.15f);
            particleLastRealtime = realtime;
            float dt = Mathf.Min(realDelta * Mathf.Max(0f, timeScale), 0.2f);
            bool frozen = timeScale <= 0.0001f;

            float cellPx = Mathf.Max(cellPixels, 0.5f);

            // CellRect width/height can change by one cell while panning because the
            // camera position is rounded to integer cells. Do not treat that as a zoom:
            // trimming the quotas on such a one-cell fluctuation makes a scatter of
            // particles vanish across the screen after a tiny pan. Use the actual zoom
            // scale instead, with a small tolerance so continuous zoom still trims in
            // small steps.
            bool zoomChanged = !particleViewValid
                || particleTrimCellPixels <= 0f
                || Mathf.Abs(cellPx - particleTrimCellPixels)
                    > particleTrimCellPixels * 0.025f;
            bool viewChanged = zoomChanged
                || view.minX != particleViewMinX
                || view.minZ != particleViewMinZ
                || view.Width != particleViewWidth
                || view.Height != particleViewHeight;
            particleViewValid = true;
            particleViewMinX = view.minX;
            particleViewMinZ = view.minZ;
            particleViewWidth = view.Width;
            particleViewHeight = view.Height;
            float altitude = Altitudes.AltitudeFor(AltitudeLayer.FogOfWar) + FogLayerOffset + DashAltitudeOffset;
            float halfWidth = Mathf.Clamp(ParticleWidthPixels * widthScale / cellPx, 0.02f, 3f) * 0.5f;
            float fadeInSeconds = Mathf.Max(0.05f, ParticleFadeInSeconds);
            float fadeOutSeconds = Mathf.Max(0.05f, ParticleFadeOutSeconds);

            // Width of the wind-speed band over which the hide threshold fades in.
            // Proportional to the threshold but clamped so a low setting still fades
            // softly and a high setting does not smear across most of the field.
            float visibilityBand = Mathf.Clamp(lowWindHideThreshold * 0.3f, 0.3f, 1.2f);

            // Both the argument and the result are frame constants, so the Exp belongs
            // outside the loop: it used to be one libm call per particle per rebuild.
            float visibilityLerp = 1f - Mathf.Exp(-dt / ParticleVisibilityFadeSeconds);

            // A thin shell keeps a streak visible while its head leaves the screen, but
            // particles are only ever spawned inside the view itself.
            float cullMinX = Mathf.Max(1f, view.minX - ParticleCullMarginCells);
            float cullMaxX = Mathf.Min(width - 2f, view.maxX + ParticleCullMarginCells);
            float cullMinZ = Mathf.Max(1f, view.minZ - ParticleCullMarginCells);
            float cullMaxZ = Mathf.Min(height - 2f, view.maxZ + ParticleCullMarginCells);

            for (int i = particles.Count - 1; i >= 0; i--)
            {
                Particle particle = particles[i];
                particle.Age += dt;
                particle.FadeInAge += dt;

                bool dissolved = particle.Dying
                    && particle.Age - particle.DeathAge >= ParticleDeathFadeSeconds;

                if (particle.Age >= particle.Life || dissolved)
                {
                    RemoveParticle(i);
                    continue;
                }

                // Dying particles hold their last position and just fade: the trail has
                // already left the field (wall, fog, dead air), so moving it again would
                // push the ribbon through the obstacle it just hit.
                if (particle.Dying)
                {
                    particles[i] = particle;
                    continue;
                }

                // Raw bilinear sampling, like AO's original renderer: the 5-tap smoothing
                // used for streamlines dulls narrow jets (fans, vents) and makes the
                // particle layer feel sluggish indoors.
                if (!SampleVelocityRaw(particle.X, particle.Z, out float sampledX, out float sampledZ))
                {
                    BeginDeath(ref particle);
                    particles[i] = particle;
                    continue;
                }

                float speed = Mathf.Sqrt(sampledX * sampledX + sampledZ * sampledZ);
                if (!IsFinite(speed) || speed < 0.01f)
                {
                    BeginDeath(ref particle);
                    particles[i] = particle;
                    continue;
                }

                float dirX = sampledX / speed;
                float dirZ = sampledZ / speed;
                float blendX = dirX * 0.85f + particle.DirX * 0.15f;
                float blendZ = dirZ * 0.85f + particle.DirZ * 0.15f;
                float blendLength = Mathf.Sqrt(blendX * blendX + blendZ * blendZ);
                if (!IsFinite(blendLength) || blendLength < 1e-5f)
                {
                    BeginDeath(ref particle);
                    particles[i] = particle;
                    continue;
                }

                blendX /= blendLength;
                blendZ /= blendLength;

                float motion = speed * ParticleMotionSpeedScale * animationScale;
                float nextX = particle.X + blendX * motion * dt;
                float nextZ = particle.Z + blendZ * motion * dt;

                if (!IsFinite(nextX) || !IsFinite(nextZ)
                    || nextX < cullMinX || nextX > cullMaxX
                    || nextZ < cullMinZ || nextZ > cullMaxZ)
                {
                    BeginDeath(ref particle);
                    particles[i] = particle;
                    continue;
                }

                if (!IsOpen(nextX, nextZ))
                {
                    BeginDeath(ref particle);
                    particles[i] = particle;
                    continue;
                }

                particle.X = nextX;
                particle.Z = nextZ;
                particle.DirX = blendX;
                particle.DirZ = blendZ;
                particle.Speed = speed;

                // Low-wind hiding: approach the target alpha instead of snapping when the
                // particle crosses the threshold. The particle stays in the list (and in
                // its block quota) while invisible, so hiding calm air does not repack
                // the remaining particles into a denser pattern.
                float targetVisibility = LowWindVisibility(
                    speed,
                    lowWindHideThreshold,
                    visibilityBand);
                particle.Visibility += (targetVisibility - particle.Visibility)
                    * visibilityLerp;

                // The step was built as a unit direction times (motion * dt), so its
                // length is motion * dt; measuring the vector we just constructed costs a
                // sqrt per particle for nothing. motion is never negative here (speed is
                // clamped above 0.01 and both scales are non-negative).
                particle.Travel += motion * dt;
                particles[i] = particle;
            }

            // Density is screen-space: the target is the count the old per-area formula
            // produced at the default zoom, held constant as the camera zooms. Scaling
            // with the visible map area would keep world-space density constant, which
            // makes the screen look denser when zooming out.
            float aspect = UI.screenHeight > 0
                ? UI.screenWidth / (float)UI.screenHeight
                : 16f / 9f;
            float defaultViewArea = ParticleDefaultRootSize * ParticleDefaultRootSize
                * 4f * aspect;
            // 120 was the floor when the density slider bottomed out at 0.1. At 0.01 the
            // formula yields ~29 particles at the default zoom, so the floor now sits
            // below that and the bottom of the slider still changes the count.
            int targetCount = Mathf.Clamp(
                Mathf.RoundToInt(
                    ParticleBaseCount * densityScale * defaultViewArea / ParticleDensityArea),
                12,
                ParticleMaxCount);

            // Zooming out shrinks each cell's screen-space quota, so particles in the
            // old (now central) area can exceed it. Trim them as the quota changes; the
            // freed slots are then refilled across the newly revealed area, keeping the
            // on-screen density even.
            bool trimExcess = zoomChanged || targetCount < particleLastTargetCount;
            int deficit = targetCount - particles.Count;
            if (trimExcess || deficit > 0)
            {
                BuildBlockOccupancy(cullMinX, cullMaxX, cullMinZ, cullMaxZ, view, targetCount);
                if (trimExcess)
                {
                    TrimExcessParticles();
                    deficit = targetCount - particles.Count;
                    particleTrimCellPixels = cellPx;
                }
            }

            if (deficit > 0)
            {
                BuildSpawnBlockList(view);
            }

            if (deficit > 0 && particleSpawnBlocks.Count > 0)
            {
                // On a view change the deficit is filled across all under-quota cells,
                // not in a ring around the old view. Randomizing age mixes lifetimes;
                // the separate FadeInAge still fades every new particle in smoothly.
                // Routine replacements are rate-limited to avoid bursts.
                bool randomizeAge = viewChanged || deficit > Mathf.Max(8, targetCount / 8);
                int spawnBudget = viewChanged
                    ? Mathf.Min(deficit, ParticleInstantSpawnCap)
                    : Mathf.Min(
                        deficit,
                        Mathf.Max(1, Mathf.CeilToInt(targetCount * ParticleSpawnFractionPerTick)));
                int attempts = 0;
                int attemptLimit = Mathf.Max(32, spawnBudget * 10);

                while (particles.Count < targetCount && spawnBudget > 0 && attempts < attemptLimit)
                {
                    attempts++;
                    int blockIndex = particleSpawnBlocks[(int)(NextRandom01() * particleSpawnBlocks.Count)];
                    if (particleBlockBlocked[blockIndex]
                        || particleBlockCounts[blockIndex] >= particleBlockCapacities[blockIndex])
                    {
                        continue;
                    }

                    if (!TryGetBlockSpawnRect(
                            blockIndex,
                            view,
                            out float spawnMinX,
                            out float spawnMaxX,
                            out float spawnMinZ,
                            out float spawnMaxZ))
                    {
                        continue;
                    }

                    if (TrySpawnParticle(
                            spawnMinX, spawnMaxX, spawnMinZ, spawnMaxZ,
                            randomizeAge,
                            frozen,
                            out Particle particle))
                    {
                        particles.Add(particle);
                        CountParticleBlock(particle.X, particle.Z);
                        spawnBudget--;
                    }
                    else
                    {
                        // Calm or unreachable cells would otherwise consume every
                        // attempt in the same frame. Retry them next frame instead.
                        particleBlockBlocked[blockIndex] = true;
                    }
                }
            }

            particleLastTargetCount = targetCount;

            if (gpuRequested && EnsureGpuResources())
            {
                BuildParticleGeometryGpu(altitude, halfWidth, cellPx, fieldVersion);
            }
            else
            {
                BuildParticleGeometry(altitude, halfWidth, cellPx);
            }

            if (particles.Count > 0)
            {
                VisualTweaksLog.TraceOnce(
                    ref particleLogged,
                    "Particle trails: " + particles.Count + " particles.");
            }
        }

        /// <summary>
        /// Picks a random point inside one cell quota and validates it against the field.
        /// When <paramref name="randomizeAge"/> is true the particle is born mid-life
        /// (used when filling a newly revealed view), so a whole area does not visibly
        /// pop in at once. Routine replacements fade in from zero instead.
        /// </summary>
        private bool TrySpawnParticle(
            float minX,
            float maxX,
            float minZ,
            float maxZ,
            bool randomizeAge,
            bool frozen,
            out Particle particle)
        {
            particle = default;

            for (int attempt = 0; attempt < 16; attempt++)
            {
                float x = minX + NextRandom01() * Mathf.Max(0.01f, maxX - minX);
                float z = minZ + NextRandom01() * Mathf.Max(0.01f, maxZ - minZ);

                if (!IsOpen(x, z))
                {
                    continue;
                }

                if (!SampleVelocityRaw(x, z, out float velocityX, out float velocityZ))
                {
                    continue;
                }

                float speed = Mathf.Sqrt(velocityX * velocityX + velocityZ * velocityZ);
                if (!IsFinite(speed) || speed < 0.01f)
                {
                    continue;
                }

                // AO-like spawn weighting: livelier cells receive more particles, so the
                // layer reads as moving air instead of uniformly scattered dots.
                if (NextRandom01() > Mathf.Clamp01(speed / ParticleSpawnSpeedReference))
                {
                    continue;
                }

                float life = ParticleMinLife + NextRandom01() * (ParticleMaxLife - ParticleMinLife);
                float age = randomizeAge ? life * (0.15f + NextRandom01() * 0.35f) : 0f;

                particle = new Particle
                {
                    X = x,
                    Z = z,
                    DirX = velocityX / speed,
                    DirZ = velocityZ / speed,
                    Speed = speed,
                    Age = age,
                    Life = life,
                    // Batch fills start at slightly different fade phases so a panned or
                    // zoomed view does not brighten as one synchronized pulse. While the
                    // game is paused there is no time to fade, so spawn fully visible.
                    FadeInAge = frozen
                        ? ParticleFadeInSeconds
                        : (randomizeAge
                            ? NextRandom01() * ParticleFadeInSeconds * 0.4f
                            : 0f),
                    Travel = 0f,
                    Size = 0.75f + NextRandom01() * 0.5f,
                    Brightness = 0.7f + NextRandom01() * 0.3f,
                    // Born at its target alpha, so a particle spawned into calm air does
                    // not flash fully opaque and then fade out.
                    Visibility = LowWindVisibility(
                        speed,
                        lowWindHideThreshold,
                        Mathf.Clamp(lowWindHideThreshold * 0.3f, 0.3f, 1.2f))
                };
                return true;
            }

            return false;
        }

        /// <summary>
        /// Builds the coarse occupancy grid for the cull region. The block size adapts
        /// to the zoom to keep roughly a fixed number of blocks on screen, and each
        /// block's quota is proportional to the part of it that lies inside the view.
        /// Quotas therefore represent screen-space density rather than world-space
        /// density, and blocks clipped by the screen edge cannot accumulate a full
        /// block's particles in a thin sliver.
        /// </summary>
        private void BuildBlockOccupancy(
            float minX,
            float maxX,
            float minZ,
            float maxZ,
            CellRect view,
            int targetCount)
        {
            bool sameGrid = particleBlockCapacitiesValid
                && view.minX == particleBlockCacheViewMinX
                && view.minZ == particleBlockCacheViewMinZ
                && view.Width == particleBlockCacheViewWidth
                && view.Height == particleBlockCacheViewHeight
                && width == particleBlockCacheMapWidth
                && height == particleBlockCacheMapHeight
                && densityScale == particleBlockCacheDensity
                && targetCount == particleBlockCacheTarget;

            if (!sameGrid)
            {
                float viewMinX = Mathf.Max(1f, view.minX);
                float viewMaxX = Mathf.Min(width - 2f, view.maxX);
                float viewMinZ = Mathf.Max(1f, view.minZ);
                float viewMaxZ = Mathf.Min(height - 2f, view.maxZ);
                float viewArea = Mathf.Max(
                    1f,
                    (viewMaxX - viewMinX) * (viewMaxZ - viewMinZ));

                // Keep the block count roughly constant across zoom levels. A fixed
                // map-cell block size would shrink quotas to 0/1 at far zoom and make
                // the old view's particles look clumped.
                particleBlockCells = Mathf.Clamp(
                    Mathf.Sqrt(viewArea / ParticleTargetBlocks),
                    2f,
                    32f);

                particleBlockMinX = minX;
                particleBlockMinZ = minZ;
                particleBlockCountX = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / particleBlockCells) + 1);
                particleBlockCountZ = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / particleBlockCells) + 1);

                int gridSize = particleBlockCountX * particleBlockCountZ;
                if (particleBlockCounts.Length < gridSize)
                {
                    particleBlockCounts = new int[gridSize];
                }

                if (particleBlockCapacities.Length < gridSize)
                {
                    particleBlockCapacities = new int[gridSize];
                }

                if (particleBlockBlocked.Length < gridSize)
                {
                    particleBlockBlocked = new bool[gridSize];
                }

                Array.Clear(particleBlockCapacities, 0, gridSize);

                for (int bz = 0; bz < particleBlockCountZ; bz++)
                {
                    float blockMinZ = particleBlockMinZ + bz * particleBlockCells;
                    float visibleMinZ = Mathf.Max(blockMinZ, viewMinZ);
                    float visibleMaxZ = Mathf.Min(blockMinZ + particleBlockCells, viewMaxZ);
                    if (visibleMaxZ <= visibleMinZ)
                    {
                        continue;
                    }

                    for (int bx = 0; bx < particleBlockCountX; bx++)
                    {
                        float blockMinX = particleBlockMinX + bx * particleBlockCells;
                        float visibleMinX = Mathf.Max(blockMinX, viewMinX);
                        float visibleMaxX = Mathf.Min(blockMinX + particleBlockCells, viewMaxX);
                        if (visibleMaxX <= visibleMinX)
                        {
                            continue;
                        }

                        int index = bz * particleBlockCountX + bx;
                        float visibleArea = (visibleMaxX - visibleMinX) * (visibleMaxZ - visibleMinZ);
                        particleBlockCapacities[index] = Mathf.Max(
                            1,
                            Mathf.CeilToInt(targetCount * visibleArea / viewArea));
                    }
                }

                particleBlockCapacitiesValid = true;
                particleBlockCacheViewMinX = view.minX;
                particleBlockCacheViewMinZ = view.minZ;
                particleBlockCacheViewWidth = view.Width;
                particleBlockCacheViewHeight = view.Height;
                particleBlockCacheMapWidth = width;
                particleBlockCacheMapHeight = height;
                particleBlockCacheDensity = densityScale;
                particleBlockCacheTarget = targetCount;
            }

            int needed = particleBlockCountX * particleBlockCountZ;
            Array.Clear(particleBlockCounts, 0, needed);
            Array.Clear(particleBlockBlocked, 0, needed);

            for (int i = 0; i < particles.Count; i++)
            {
                CountParticleBlock(particles[i].X, particles[i].Z);
            }
        }

        /// <summary>
        /// Lists every cell that intersects the view and is below its quota. Spawns are
        /// drawn from this list, so a pan or zoom only fills under-dense cells and the
        /// overlap keeps its density instead of gaining a ring of fresh particles.
        /// </summary>
        private void BuildSpawnBlockList(CellRect view)
        {
            particleSpawnBlocks.Clear();

            float viewMinX = Mathf.Max(1f, view.minX);
            float viewMaxX = Mathf.Min(width - 2f, view.maxX);
            float viewMinZ = Mathf.Max(1f, view.minZ);
            float viewMaxZ = Mathf.Min(height - 2f, view.maxZ);
            if (viewMaxX <= viewMinX || viewMaxZ <= viewMinZ)
            {
                return;
            }

            for (int bz = 0; bz < particleBlockCountZ; bz++)
            {
                float blockMinZ = particleBlockMinZ + bz * particleBlockCells;
                float blockMaxZ = blockMinZ + particleBlockCells;
                if (blockMaxZ <= viewMinZ || blockMinZ >= viewMaxZ)
                {
                    continue;
                }

                for (int bx = 0; bx < particleBlockCountX; bx++)
                {
                    float blockMinX = particleBlockMinX + bx * particleBlockCells;
                    float blockMaxX = blockMinX + particleBlockCells;
                    if (blockMaxX <= viewMinX || blockMinX >= viewMaxX)
                    {
                        continue;
                    }

                    int index = bz * particleBlockCountX + bx;
                    int free = particleBlockCapacities[index] - particleBlockCounts[index];
                    for (int slot = 0; slot < free; slot++)
                    {
                        // Weight each cell by its remaining quota. Picking a slot uniformly
                        // fills the whole view evenly instead of oversampling cells that
                        // happen to be listed first.
                        particleSpawnBlocks.Add(index);
                    }
                }
            }
        }

        private bool TryGetBlockSpawnRect(
            int blockIndex,
            CellRect view,
            out float minX,
            out float maxX,
            out float minZ,
            out float maxZ)
        {
            int bx = blockIndex % particleBlockCountX;
            int bz = blockIndex / particleBlockCountX;

            minX = Mathf.Max(1f, Mathf.Max(view.minX, particleBlockMinX + bx * particleBlockCells));
            maxX = Mathf.Min(width - 2f, Mathf.Min(view.maxX, particleBlockMinX + (bx + 1) * particleBlockCells));
            minZ = Mathf.Max(1f, Mathf.Max(view.minZ, particleBlockMinZ + bz * particleBlockCells));
            maxZ = Mathf.Min(height - 2f, Mathf.Min(view.maxZ, particleBlockMinZ + (bz + 1) * particleBlockCells));

            return maxX > minX && maxZ > minZ;
        }

        private void CountParticleBlock(float x, float z)
        {
            int index = ParticleBlockIndex(x, z);
            if (index >= 0)
            {
                particleBlockCounts[index]++;
            }
        }

        /// <summary>
        /// Removes particles from cells that now exceed their screen-space quota. This
        /// happens when zooming out: the old view becomes the centre of a larger view,
        /// and keeping the old world-space density would make that area look crowded.
        /// Removal is spread over the continuous zoom's frames because each frame only
        /// trims the current overage.
        /// </summary>
        private void TrimExcessParticles()
        {
            if (particles.Count == 0 || particleBlockCounts.Length == 0)
            {
                return;
            }

            int needed = particleBlockCountX * particleBlockCountZ;
            if (particleBlockRemovals.Length < needed)
            {
                particleBlockRemovals = new int[needed];
            }

            Array.Clear(particleBlockRemovals, 0, needed);

            bool anyExcess = false;
            for (int i = 0; i < needed; i++)
            {
                int excess = particleBlockCounts[i] - particleBlockCapacities[i];
                if (excess > 0)
                {
                    particleBlockRemovals[i] = excess;
                    anyExcess = true;
                }
            }

            if (!anyExcess)
            {
                return;
            }

            int write = 0;
            for (int i = 0; i < particles.Count; i++)
            {
                Particle particle = particles[i];
                int block = ParticleBlockIndex(particle.X, particle.Z);
                if (block >= 0 && particleBlockRemovals[block] > 0)
                {
                    particleBlockRemovals[block]--;
                    particleBlockCounts[block]--;
                    continue;
                }

                particles[write++] = particle;
            }

            if (write < particles.Count)
            {
                particles.RemoveRange(write, particles.Count - write);
            }
        }

        private int ParticleBlockIndex(float x, float z)
        {
            if (particleBlockCounts.Length == 0)
            {
                return -1;
            }

            int bx = (int)((x - particleBlockMinX) / particleBlockCells);
            int bz = (int)((z - particleBlockMinZ) / particleBlockCells);
            if (bx < 0 || bx >= particleBlockCountX || bz < 0 || bz >= particleBlockCountZ)
            {
                return -1;
            }

            return bz * particleBlockCountX + bx;
        }

        private void RemoveParticle(int index)
        {
            int last = particles.Count - 1;
            if (index != last)
            {
                particles[index] = particles[last];
            }

            particles.RemoveAt(last);
        }

        /// <summary>
        /// Ends a particle's life early but keeps it in the list for
        /// <see cref="ParticleDeathFadeSeconds"/> so its trail dissolves instead of
        /// vanishing. Called where a particle hits a wall, fog, dead air or the cull box.
        /// </summary>
        private static void BeginDeath(ref Particle particle)
        {
            if (particle.Dying)
            {
                return;
            }

            particle.Dying = true;
            particle.DeathAge = particle.Age;
        }

        /// <summary>
        /// Alpha multiplier for the low-wind hide slider. 0 disables it. Fully hidden at
        /// or below the threshold, fully visible at threshold + band, smoothstep in
        /// between so a particle crossing the line fades instead of popping.
        /// </summary>
        private static float LowWindVisibility(float speed, float threshold, float band)
        {
            if (threshold <= 0f || speed >= threshold + band)
            {
                return 1f;
            }

            if (speed <= threshold)
            {
                return 0f;
            }

            float t = (speed - threshold) / band;
            return t * t * (3f - 2f * t);
        }

        private float NextRandom01()
        {
            particleRng ^= particleRng << 13;
            particleRng ^= particleRng >> 17;
            particleRng ^= particleRng << 5;
            return (particleRng & 0xFFFFFFu) / 16777216f;
        }

        private void Build(
            Map map,
            float[] velocityXArray,
            float[] velocityZArray,
            float[] obstacleMaskArray,
            CellRect view,
            float cellPixels,
            int fieldVersion)
        {
            ClearGeometry();

            if (!PrepareField(map, velocityXArray, velocityZArray, obstacleMaskArray, fieldVersion))
            {
                return;
            }

            int minX = Mathf.Max(1, view.minX);
            int maxX = Mathf.Min(width - 2, view.maxX);
            int minZ = Mathf.Max(1, view.minZ);
            int maxZ = Mathf.Min(height - 2, view.maxZ);

            if (minX > maxX || minZ > maxZ)
            {
                return;
            }

            baseAltitude = Altitudes.AltitudeFor(AltitudeLayer.FogOfWar) + FogLayerOffset;
            dashAltitude = baseAltitude + DashAltitudeOffset;

            float cellPx = Mathf.Max(cellPixels, 0.5f);
            float maxLengthCells = Mathf.Clamp(lineLengthPixels / cellPx, 2f, 120f);
            int stepsPerDirection = Mathf.Clamp(
                Mathf.CeilToInt(maxLengthCells / (StepCells * 2f)),
                2,
                MaxStepsPerDirection);

            baseHalfWidth = Mathf.Clamp(guideWidthPixels / cellPx, 0.015f, 4f) * 0.5f;
            dashHalfWidth = Mathf.Clamp(dashWidthPixels / cellPx, 0.02f, 4f) * 0.5f;
            dashPeriodCells = Mathf.Max(0.1f, dashPeriodPixels / cellPx);

            // Fixed world-space seed grid. The view only decides which integer cells are
            // visited; seed positions themselves never change when the camera moves.
            int minIX = Mathf.FloorToInt((minX - 2f) / seedSpacingCells);
            int maxIX = Mathf.CeilToInt((maxX + 2f) / seedSpacingCells);
            int minIZ = Mathf.FloorToInt((minZ - 2f) / seedSpacingCells);
            int maxIZ = Mathf.CeilToInt((maxZ + 2f) / seedSpacingCells);

            // Use the view's cell size (not the rounded grid extents) so panning does not
            // change the keep probability and make seeds pop in the middle of the screen.
            int approxColumns = Mathf.Max(1, Mathf.CeilToInt(view.Width / seedSpacingCells));
            int approxRows = Mathf.Max(1, Mathf.CeilToInt(view.Height / seedSpacingCells));
            int approxSeeds = approxColumns * approxRows;
            float keepProbability = approxSeeds > maxSeeds
                ? Mathf.Clamp01((float)maxSeeds / approxSeeds)
                : 1f;

            float margin = seedSpacingCells * 2f + 2f;
            pathMinX = minX - margin;
            pathMaxX = maxX + margin;
            pathMinZ = minZ - margin;
            pathMaxZ = maxZ + margin;

            for (int iz = minIZ; iz <= maxIZ; iz++)
            {
                for (int ix = minIX; ix <= maxIX; ix++)
                {
                    int key = unchecked(ix * 73856093 ^ iz * 19349663);

                    // Stable subset selection: a seed either exists or does not, but its
                    // position never changes with camera pan or zoom.
                    if (keepProbability < 1f && Hash01(key ^ unchecked((int)0x9e3779b9)) > keepProbability)
                    {
                        continue;
                    }

                    float jitterX = (Hash01(key) - 0.5f) * seedSpacingCells * 0.7f;
                    float jitterZ = (Hash01(key ^ 0x5f3759df) - 0.5f) * seedSpacingCells * 0.7f;
                    float seedXf = ix * seedSpacingCells + jitterX;
                    float seedZf = iz * seedSpacingCells + jitterZ;

                    if (!IsOpen(seedXf, seedZf))
                    {
                        continue;
                    }

                    if (!SampleVelocity(seedXf, seedZf, out float seedVelocityX, out float seedVelocityZ))
                    {
                        continue;
                    }

                    float seedSpeed = Mathf.Sqrt(seedVelocityX * seedVelocityX + seedVelocityZ * seedVelocityZ);
                    if (!IsFinite(seedSpeed) || seedSpeed < 0.2f)
                    {
                        continue;
                    }

                    forward.Clear();
                    backward.Clear();
                    Integrate(seedXf, seedZf, 1, stepsPerDirection, forward);
                    Integrate(seedXf, seedZf, -1, stepsPerDirection, backward);

                    if (forward.Count + backward.Count < 3)
                    {
                        continue;
                    }

                    line.Clear();
                    for (int i = backward.Count - 1; i >= 1; i--)
                    {
                        line.Add(backward[i]);
                    }
                    for (int i = 0; i < forward.Count; i++)
                    {
                        line.Add(forward[i]);
                    }

                    if (line.Count < 3)
                    {
                        continue;
                    }

                    AppendLine(line, Hash01(key ^ 0x1234567));
                }
            }

            UploadMeshes(baseAltitude);

            if (lastLineCount > 0)
            {
                VisualTweaksLog.TraceOnce(
                    ref buildLogged,
                    "Built " + lastLineCount + " streamlines, "
                        + lastSegmentCount + " segments, " + lastDashCount + " dashes.");
            }
        }

        private void Integrate(float startX, float startZ, int sign, int maxSteps, List<FlowPoint> points)
        {
            points.Clear();

            if (!IsOpen(startX, startZ))
            {
                return;
            }

            if (!SampleVelocity(startX, startZ, out float velocityX0, out float velocityZ0))
            {
                return;
            }

            float speed = Mathf.Sqrt(velocityX0 * velocityX0 + velocityZ0 * velocityZ0);
            if (!IsFinite(speed) || speed < 0.2f)
            {
                return;
            }

            float x = startX;
            float z = startZ;
            float lastDirX = velocityX0 / speed * sign;
            float lastDirZ = velocityZ0 / speed * sign;
            points.Add(new FlowPoint(x, z, speed));

            for (int step = 0; step < maxSteps; step++)
            {
                float midX = x + lastDirX * StepCells * 0.5f;
                float midZ = z + lastDirZ * StepCells * 0.5f;

                if (!IsOpen(midX, midZ))
                {
                    break;
                }

                if (!SampleVelocity(midX, midZ, out float midVelocityX, out float midVelocityZ))
                {
                    break;
                }

                float midSpeed = Mathf.Sqrt(midVelocityX * midVelocityX + midVelocityZ * midVelocityZ);
                if (!IsFinite(midSpeed) || midSpeed < 0.05f)
                {
                    break;
                }

                float desiredX = midVelocityX / midSpeed * sign;
                float desiredZ = midVelocityZ / midSpeed * sign;
                float blendX = desiredX * 0.82f + lastDirX * 0.18f;
                float blendZ = desiredZ * 0.82f + lastDirZ * 0.18f;
                float blendLength = Mathf.Sqrt(blendX * blendX + blendZ * blendZ);
                if (!IsFinite(blendLength) || blendLength < 1e-5f)
                {
                    break;
                }

                blendX /= blendLength;
                blendZ /= blendLength;

                float nextX = x + blendX * StepCells;
                float nextZ = z + blendZ * StepCells;

                if (!IsFinite(nextX) || !IsFinite(nextZ)
                    || nextX < pathMinX || nextX > pathMaxX
                    || nextZ < pathMinZ || nextZ > pathMaxZ)
                {
                    break;
                }

                if (!IsOpen(nextX, nextZ))
                {
                    break;
                }

                // Do not sample the destination as well: the step direction already came
                // from the mid-point sample and IsOpen above proves the destination cell is
                // open, so a second lookup there only re-derived the point's speed. The
                // mid-point speed is 0.3 cells stale, which is under one dash width.
                x = nextX;
                z = nextZ;
                lastDirX = blendX;
                lastDirZ = blendZ;
                points.Add(new FlowPoint(x, z, midSpeed));
            }
        }

        private void AppendLine(List<FlowPoint> points, float phase)
        {
            int count = points.Count;
            if (count < 2 || count > pointArcs.Length)
            {
                return;
            }

            pointArcs[0] = 0f;
            for (int i = 1; i < count; i++)
            {
                pointArcs[i] = pointArcs[i - 1] + Distance(points[i - 1], points[i]);
            }

            float totalLength = pointArcs[count - 1];
            if (!IsFinite(totalLength) || totalLength < 1f)
            {
                return;
            }

            lastLineCount++;

            // Optional faint continuous guide. Disabled by default in comet style because
            // long continuous lines are what made the old look snake-like.
            if (showGuide)
            {
                float fadeCells = Mathf.Clamp(totalLength * 0.22f, 0.35f, 2.5f);
                for (int i = 0; i < count - 1; i++)
                {
                    float arc0 = pointArcs[i];
                    float arc1 = pointArcs[i + 1];
                    float segmentLength = arc1 - arc0;
                    if (segmentLength < 1e-4f)
                    {
                        continue;
                    }

                    float taper0 = TaperAt(arc0, totalLength, fadeCells);
                    float taper1 = TaperAt(arc1, totalLength, fadeCells);
                    Vector3 tangent0 = DirectionAt(points, i);
                    Vector3 tangent1 = DirectionAt(points, i + 1);
                    Vector3 normal0 = new Vector3(-tangent0.z, 0f, tangent0.x);
                    Vector3 normal1 = new Vector3(-tangent1.z, 0f, tangent1.x);

                    EmitQuad(
                        baseVertices, baseColors, baseUvs, baseTriangles,
                        new Vector3(points[i].X, baseAltitude, points[i].Z),
                        new Vector3(points[i + 1].X, baseAltitude, points[i + 1].Z),
                        normal0, normal1,
                        baseHalfWidth * taper0, baseHalfWidth * taper1,
                        arc0 / 8f, arc1 / 8f,
                        WhiteColor);

                    lastSegmentCount++;
                }
            }

            // Moving dashes: one straight quad per dash, positioned by arc length.
            float dashStartArc = Wrap01(phase + dashPhase) * dashPeriodCells;
            float dashLengthCells = dashPeriodCells * dashDuty;
            int cursor = 0;

            while (dashStartArc < totalLength)
            {
                float dashEndArc = Mathf.Min(dashStartArc + dashLengthCells, totalLength);
                if (dashEndArc - dashStartArc >= 0.08f)
                {
                    SampleArc(points, pointArcs, count, dashStartArc, ref cursor, out float x0, out float z0, out float speed0);
                    SampleArc(points, pointArcs, count, dashEndArc, ref cursor, out float x1, out float z1, out float speed1);

                    Vector3 dashP0 = new Vector3(x0, dashAltitude, z0);
                    Vector3 dashP1 = new Vector3(x1, dashAltitude, z1);
                    float dx = dashP1.x - dashP0.x;
                    float dz = dashP1.z - dashP0.z;
                    float length = Mathf.Sqrt(dx * dx + dz * dz);

                    if (length > 1e-4f && IsFinite(length))
                    {
                        Vector3 tangent = new Vector3(dx / length, 0f, dz / length);
                        Vector3 normal = new Vector3(-tangent.z, 0f, tangent.x);
                        int bucket = Bucket((speed0 + speed1) * 0.5f);

                        // u runs 0..1 across a single dash, so the texture never has to wrap.
                        EmitQuad(
                            vertices[bucket], colors[bucket], uvs[bucket], triangles[bucket],
                            dashP0, dashP1, normal, normal,
                            dashHalfWidth, dashHalfWidth,
                            0f, 1f,
                            WhiteColor);

                        lastDashCount++;
                    }
                }

                dashStartArc += dashPeriodCells;
            }
        }

        private static void SampleArc(
            List<FlowPoint> points,
            float[] arcs,
            int count,
            float arc,
            ref int cursor,
            out float x,
            out float z,
            out float speed)
        {
            if (arc <= 0f)
            {
                x = points[0].X;
                z = points[0].Z;
                speed = points[0].Speed;
                return;
            }

            float total = arcs[count - 1];
            if (arc >= total)
            {
                FlowPoint last = points[count - 1];
                x = last.X;
                z = last.Z;
                speed = last.Speed;
                return;
            }

            if (cursor < 0)
            {
                cursor = 0;
            }

            while (cursor < count - 2 && arcs[cursor + 1] < arc)
            {
                cursor++;
            }

            float span = arcs[cursor + 1] - arcs[cursor];
            float t = span > 1e-5f ? Mathf.Clamp01((arc - arcs[cursor]) / span) : 0f;
            FlowPoint a = points[cursor];
            FlowPoint b = points[cursor + 1];
            x = Mathf.Lerp(a.X, b.X, t);
            z = Mathf.Lerp(a.Z, b.Z, t);
            speed = Mathf.Lerp(a.Speed, b.Speed, t);
        }

        /// <summary>
        /// Shared per-particle plan: visibility, life fade, trail length, joint count and
        /// bucket. Both paths run the same math here, so they cannot drift apart.
        ///
        /// With <paramref name="packGpu"/> false the results land in the per-particle
        /// arrays plus the bucket counters the CPU emitter needs; with true they go
        /// straight into the GPU record buffer, one pass instead of a second traversal
        /// that only re-read the arrays it had just written. Returns the visible count
        /// (which is also the GPU record count).
        /// </summary>
        private int PlanParticles(float cellPx, bool packGpu)
        {
            int count = particles.Count;
            for (int b = 0; b < BucketCount; b++)
            {
                bucketVertexCount[b] = 0;
                bucketIndexCount[b] = 0;
            }

            if (count == 0)
            {
                return 0;
            }

            EnsureParticleGeometryCapacity(count);

            float minTrailCells = Mathf.Max(TrailMinCells, TrailMinPixels / cellPx);
            float maxTrailCells = Mathf.Max(TrailMaxCells, minTrailCells);
            float referenceLength = TrailSpeedReference * TrailCellsPerSpeed * lengthScale;
            float fadeInSeconds = Mathf.Max(0.05f, ParticleFadeInSeconds);
            float fadeOutSeconds = Mathf.Max(0.05f, ParticleFadeOutSeconds);

            int visible = 0;
            int record = 0;

            // ---- plan (and, on the GPU path, pack) ----
            for (int i = 0; i < count; i++)
            {
                Particle particle = particles[i];

                // Fade in over travelled distance OR time since spawning, whichever
                // completes first. FadeInAge is separate from Age so particles that are
                // born mid-life (after a pan or zoom) still fade in visibly.
                float fadeIn = Mathf.Max(
                    Mathf.Clamp01(particle.Travel / ParticleFadeInCells),
                    Mathf.Clamp01(particle.FadeInAge / fadeInSeconds));
                float fadeOut = Mathf.Clamp01((particle.Life - particle.Age) / fadeOutSeconds);
                float fade = Mathf.Min(fadeIn, fadeOut);
                if (particle.Dying)
                {
                    // Dissolve from whatever alpha the trail had at the moment it hit
                    // something, so a wall or the fog edge never pops a streak out.
                    fade *= Mathf.Clamp01(
                        1f - (particle.Age - particle.DeathAge) / ParticleDeathFadeSeconds);
                }

                // The low-wind slider feeds a per-particle alpha that eases over time.
                // Multiplying it here hides the whole ribbon in calm air while the
                // particle itself keeps its slot in the quota.
                fade *= particle.Visibility;

                fade = fade * fade * (3f - 2f * fade);

                // (int)MathF.Round rather than Mathf.RoundToInt: the latter is a call
                // chain (RoundToInt -> MathF.Round -> MonoToInt) whose latency lands on the
                // dependency path. Same value for anything in range.
                byte alpha = (byte)(int)MathF.Round(255f * fade * particle.Brightness);
                particleSegments[i] = 0;
                if (alpha == 0)
                {
                    continue;
                }

                // Speed-proportional, like a wake: the faster the air moves past the
                // particle, the longer the streak it drags. The settings slider blends that
                // away, towards the length a reference wind would give, so at 0 every trail
                // is the same length however fast the local air is. Clamped in cells so calm
                // pockets do not vanish, plus a pixel floor for zoomed-out views.
                // speedLengthInfluence is clamped to 0..1 by ApplySettings, so the plain
                // lerp is the same value without the Clamp01 chain inside Mathf.Lerp.
                float speedLength = particle.Speed * TrailCellsPerSpeed * lengthScale;
                float blendedLength = referenceLength
                    + (speedLength - referenceLength) * speedLengthInfluence;
                float trailCells = Mathf.Clamp(
                    blendedLength,
                    minTrailCells,
                    maxTrailCells) * particle.Size;

                // The trail grows with the particle: either as far as it has travelled, or
                // in step with the alpha fade-in, whichever is further along.
                float growth = Mathf.Clamp01(
                    Mathf.Max(particle.Travel / Mathf.Max(trailCells, 0.01f), fadeIn));
                float length = trailCells * growth;
                if (length < 0.05f)
                {
                    continue;
                }

                // One joint per ~12 screen pixels is already sub-pixel accurate for a
                // ribbon this narrow; see ArcSegmentPixels. The arithmetic is the same as
                // Mathf.Clamp(Mathf.Ceiling(...)) without two wrapper calls per particle.
                int segments = (int)MathF.Ceiling(length * cellPx / ArcSegmentPixels);
                if (segments < ArcMinSegments)
                {
                    segments = ArcMinSegments;
                }
                else if (segments > ArcTrailSegments)
                {
                    segments = ArcTrailSegments;
                }
                int bucket = Bucket(particle.Speed);

                if (packGpu)
                {
                    // Three vec4 per particle, packed in the same order the vertex shader
                    // reads them: head position + direction, length + size + joint count,
                    // tint (rgb) with the planned alpha folded into a.
                    Color tint = coloredParticles
                        ? BucketColors[bucket]
                        : WhiteParticleColor;
                    int offset = record * GpuFloatsPerParticle;
                    gpuRecords[offset] = particle.X;
                    gpuRecords[offset + 1] = particle.Z;
                    gpuRecords[offset + 2] = particle.DirX;
                    gpuRecords[offset + 3] = particle.DirZ;
                    gpuRecords[offset + 4] = length;
                    gpuRecords[offset + 5] = particle.Size;
                    gpuRecords[offset + 6] = segments;
                    gpuRecords[offset + 7] = 0f;
                    gpuRecords[offset + 8] = tint.r;
                    gpuRecords[offset + 9] = tint.g;
                    gpuRecords[offset + 10] = tint.b;
                    // The life fade already lives in the planned alpha; the global
                    // opacity multiplier stays on the material (ub._Color.a).
                    gpuRecords[offset + 11] = tint.a * (alpha / 255f);
                    record++;
                }
                else
                {
                    particleSegments[i] = segments;
                    particleBucket[i] = bucket;
                    particleLength[i] = length;
                    particleColor[i] = new Color32(255, 255, 255, alpha);
                    bucketVertexCount[bucket] += (segments + 1) * 2;
                    bucketIndexCount[bucket] += segments * 6;
                }

                visible++;
            }

            return visible;
        }

        /// <summary>
        /// CPU emitter: lays the buckets out, gives every visible particle a disjoint slice
        /// of the flat buffers, then expands the ribbons on the thread pool.
        /// </summary>
        private void BuildParticleGeometry(float altitude, float halfWidth, float cellPx)
        {
            if (PlanParticles(cellPx, false) == 0)
            {
                return;
            }

            int count = particles.Count;

            // ---- lay the buckets out back to back, then give every particle its slice ----
            int vertexCursor = 0;
            int indexCursor = 0;
            for (int b = 0; b < BucketCount; b++)
            {
                bucketVertexStart[b] = vertexCursor;
                bucketIndexStart[b] = indexCursor;
                bucketVertexCursor[b] = vertexCursor;
                bucketIndexCursor[b] = indexCursor;
                vertexCursor += bucketVertexCount[b];
                indexCursor += bucketIndexCount[b];
            }

            for (int i = 0; i < count; i++)
            {
                int segments = particleSegments[i];
                if (segments == 0)
                {
                    continue;
                }

                int bucket = particleBucket[i];
                particleVertexOffset[i] = bucketVertexCursor[bucket];
                particleIndexOffset[i] = bucketIndexCursor[bucket];
                bucketVertexCursor[bucket] += (segments + 1) * 2;
                bucketIndexCursor[bucket] += segments * 6;
            }

            emitAltitude = altitude;
            emitHalfWidth = halfWidth;

            // ---- emit ----
            if (count >= ParallelEmitThreshold)
            {
                emitSliceAction ??= EmitParticleSlice;
                Parallel.For(0, count, EmitParallelOptions, emitSliceAction);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    EmitParticleSlice(i);
                }
            }

            UploadParticleGeometry(altitude);
        }

        private void EnsureParticleGeometryCapacity(int particleCount)
        {
            if (particleVertexOffset.Length < particleCount)
            {
                int size = Mathf.NextPowerOfTwo(Mathf.Max(particleCount, 64));
                particleVertexOffset = new int[size];
                particleIndexOffset = new int[size];
                particleSegments = new int[size];
                particleBucket = new int[size];
                particleLength = new float[size];
                particleColor = new Color32[size];
            }

            int maxVertices = particleVertexOffset.Length * (ArcTrailSegments + 1) * 2;
            if (flatVertices.Length < maxVertices)
            {
                int size = Mathf.NextPowerOfTwo(maxVertices);
                flatVertices = new Vector3[size];
                flatColors = new Color32[size];
                flatUvs = new Vector2[size];
            }

            // Sized from the real worst case (every particle at the joint cap) rather than
            // derived from the vertex capacity: a derived size happened to land exactly on
            // the bound, which is one arithmetic change away from an out-of-bounds write.
            int maxIndices = particleVertexOffset.Length * ArcTrailSegments * 6;
            if (flatIndices.Length < maxIndices)
            {
                flatIndices = new int[Mathf.NextPowerOfTwo(maxIndices)];
            }
        }

        private void UploadParticleGeometry(float altitude)
        {
            for (int b = 0; b < BucketCount; b++)
            {
                int vertexCount = bucketVertexCount[b];
                if (vertexCount == 0)
                {
                    continue;
                }

                UploadMesh(
                    meshes[b],
                    new ReadOnlySpan<Vector3>(flatVertices, bucketVertexStart[b], vertexCount),
                    new ReadOnlySpan<Color32>(flatColors, bucketVertexStart[b], vertexCount),
                    new ReadOnlySpan<Vector2>(flatUvs, bucketVertexStart[b], vertexCount),
                    new ReadOnlySpan<int>(flatIndices, bucketIndexStart[b], bucketIndexCount[b]),
                    altitude);
            }
        }

        // ---- GPU ribbon path ---------------------------------------------------------

        /// <summary>
        /// Resolves the GPU shader and allocates the static mesh, the per-particle storage
        /// buffer and the property block that binds it. Any failure logs once and falls
        /// back to the CPU emitter for the rest of the session.
        /// </summary>
        [MemberNotNullWhen(true, nameof(gpuMaterial), nameof(gpuMesh), nameof(gpuParticleBuffer))]
        private bool EnsureGpuResources()
        {
            if (gpuReady)
            {
                // gpuReady is only set together with the three handles and cleared by
                // DisposeGpuResources, so this is also a correct answer for the caller.
                return gpuMaterial != null && gpuMesh != null && gpuParticleBuffer != null;
            }

            if (gpuFailed)
            {
                return false;
            }

            ShaderDef def = DefDatabase<ShaderDef>.GetNamedSilentFail(GpuShaderDefName);
            if (def == null)
            {
                gpuFailed = true;
                Log.Warning("[Visual Tweaks] GPU ribbons: shader def '" + GpuShaderDefName
                    + "' missing; using the CPU emitter.");
                return false;
            }

            if (!Waylith.Rendering.ShaderSourceLoader.TryResolveShaderPath(GpuVertexShaderFile, out _)
                || !Waylith.Rendering.ShaderSourceLoader.TryResolveShaderPath(GpuFragmentShaderFile, out _))
            {
                gpuFailed = true;
                Log.Warning("[Visual Tweaks] GPU ribbons: shader sources not found; using the CPU emitter.");
                return false;
            }

            if (!Waylith.Rendering.ShaderRegistry.TryResolve(
                    GpuShaderDefName,
                    out Waylith.Rendering.Shader? engineShader)
                || engineShader == null
                || engineShader.HasErrors)
            {
                gpuFailed = true;
                Log.Warning("[Visual Tweaks] GPU ribbons: shader '" + GpuShaderDefName
                    + "' failed to compile or parse; using the CPU emitter.");
                return false;
            }

            gpuMaterial = new Material((Shader)def)
            {
                name = "VisualTweaksAOWindGpuMat",
                renderQueue = DashRenderQueue,
            };
            gpuMaterial.mainTexture = particleTexture;
            gpuMaterial.color = new Color(1f, 1f, 1f, opacityScale);
            gpuAppliedOpacity = opacityScale;

            gpuMesh = NewGpuMesh();

            // One buffer, three vec4 per particle. Capacity is the hard particle cap, so
            // the buffer is never re-created; only the written prefix changes.
            gpuRecords = new float[GpuParticleCapacity * GpuFloatsPerParticle];
            gpuParticleBuffer = new ComputeBuffer(GpuParticleCapacity * GpuFloatsPerParticle, 4);
            gpuProperties.SetBuffer("_AOParticles", gpuParticleBuffer);

            gpuReady = true;
            VisualTweaksLog.Info(
                "GPU ribbons: shader ready, "
                + (GpuParticleCapacity * GpuVerticesPerParticle) + " static vertices.");
            return true;
        }

        /// <summary>
        /// The wind field lives in three raw float buffers rather than one interleaved
        /// buffer so the AO snapshot arrays can be uploaded with a memcpy and no per-cell
        /// CPU pass. They are only rewritten when AO publishes (about 10 Hz).
        ///
        /// AO copies its solver output into one fixed snapshot array
        /// (AtmosphereField.PublishSnapshot does Array.Copy), so the reference never
        /// changes: the step counter is the upload signal. Reference comparison stays as
        /// a fallback for AO builds that swap the arrays instead.
        /// </summary>
        private void EnsureGpuFieldBuffers(int fieldVersion)
        {
            int cells = width * height;
            if (cells <= 0)
            {
                return;
            }

            if (gpuFieldVxBuffer == null || gpuFieldWidth != width || gpuFieldHeight != height)
            {
                gpuFieldVxBuffer?.Dispose();
                gpuFieldVzBuffer?.Dispose();
                gpuFieldMaskBuffer?.Dispose();
                gpuFieldVxBuffer = null;
                gpuFieldVzBuffer = null;
                gpuFieldMaskBuffer = null;

                gpuFieldWidth = width;
                gpuFieldHeight = height;
                gpuFieldVersion = int.MinValue;
                gpuFieldVxSource = Array.Empty<float>();
                gpuFieldVzSource = Array.Empty<float>();
                gpuFieldMaskSource = Array.Empty<float>();
            }

            // Created here rather than in the branch above so both paths prove non-null:
            // the rebuild path has just nulled them, the cached path already had them.
            gpuFieldVxBuffer ??= NewFieldBuffer(cells, "_AOWindVX");
            gpuFieldVzBuffer ??= NewFieldBuffer(cells, "_AOWindVZ");
            gpuFieldMaskBuffer ??= NewFieldBuffer(cells, "_AOWindMask");

            if (fieldVersion == gpuFieldVersion
                && ReferenceEquals(gpuFieldVxSource, velocityX)
                && ReferenceEquals(gpuFieldVzSource, velocityZ)
                && ReferenceEquals(gpuFieldMaskSource, obstacleMask))
            {
                return;
            }

            gpuFieldVxBuffer.SetData(velocityX, 0, 0, cells);
            gpuFieldVzBuffer.SetData(velocityZ, 0, 0, cells);
            gpuFieldMaskBuffer.SetData(obstacleMask, 0, 0, cells);

            gpuFieldVersion = fieldVersion;
            gpuFieldVxSource = velocityX;
            gpuFieldVzSource = velocityZ;
            gpuFieldMaskSource = obstacleMask;
        }

        private ComputeBuffer NewFieldBuffer(int cells, string propertyName)
        {
            ComputeBuffer buffer = new ComputeBuffer(cells, 4);
            gpuProperties.SetBuffer(propertyName, buffer);
            return buffer;
        }

        private void BuildParticleGeometryGpu(
            float altitude,
            float halfWidth,
            float cellPx,
            int fieldVersion)
        {
            if (!EnsureGpuResources())
            {
                return;
            }

            EnsureGpuFieldBuffers(fieldVersion);

            // Planning and packing are the same loop on this path: there is no bucket
            // layout to prepare, so a second traversal would only re-read what the first
            // one just wrote.
            gpuActiveCount = PlanParticles(cellPx, true);
            int record = gpuActiveCount;
            if (record == 0)
            {
                return;
            }

            gpuParticleBuffer.SetData(gpuRecords, 0, 0, record * GpuFloatsPerParticle);
            gpuMaterial.SetVector(
                GpuParamsId,
                new Vector4(altitude, halfWidth, ArcTailWidthFactor, ArcHeadWidthFactor));
            gpuMaterial.SetVector(
                GpuParams2Id,
                new Vector4(width, height, record, 0f));

            if (!Mathf.Approximately(gpuAppliedOpacity, opacityScale))
            {
                gpuAppliedOpacity = opacityScale;
                gpuMaterial.color = new Color(1f, 1f, 1f, opacityScale);
            }

            VisualTweaksLog.TraceOnce(
                ref gpuLoggedDraw,
                "GPU ribbons active: " + record + " particles, "
                    + (record * GpuVerticesPerParticle) + " vertices, "
                    + ((record * GpuFloatsPerParticle * 4) / 1024) + " KB/rebuild.");
        }

        /// <summary>
        /// A fixed-capacity mesh whose vertex stream is never read: only gl_VertexIndex
        /// matters, and every vertex expands a ribbon joint in the shader. Static indices
        /// mean the GPU buffers are uploaded exactly once.
        /// </summary>
        private Mesh NewGpuMesh()
        {
            int vertexCount = GpuParticleCapacity * GpuVerticesPerParticle;
            int indexCount = GpuParticleCapacity * ArcTrailSegments * 6;

            Vector3[] vertices = new Vector3[vertexCount];
            int[] triangles = new int[indexCount];

            // Two vertices per joint, so the strip indices must mirror the CPU emitter
            // exactly: (v, v+1, v+3) and (v, v+3, v+2) per segment. Plain sequential
            // indices would skip half of every quad and leave diagonal slivers.
            int index = 0;
            for (int p = 0; p < GpuParticleCapacity; p++)
            {
                int vertexBase = p * GpuVerticesPerParticle;
                for (int segment = 0; segment < ArcTrailSegments; segment++)
                {
                    int v = vertexBase + segment * 2;
                    triangles[index++] = v;
                    triangles[index++] = v + 1;
                    triangles[index++] = v + 3;
                    triangles[index++] = v;
                    triangles[index++] = v + 3;
                    triangles[index++] = v + 2;
                }
            }

            Mesh mesh = new Mesh
            {
                name = "VisualTweaksAOWindGpuMesh",
            };
            mesh.vertices = vertices;
            mesh.triangles = triangles;

            // The shader places every vertex, so the mesh's own bounds would otherwise
            // cull the whole layer. One static box covering the map is enough.
            mesh.bounds = new Bounds(
                new Vector3(width * 0.5f, 0f, height * 0.5f),
                new Vector3(width + 8f, 64f, height + 8f));
            return mesh;
        }

        private void DisposeGpuResources()
        {
            if (gpuParticleBuffer != null)
            {
                gpuParticleBuffer.Dispose();
                gpuParticleBuffer = null;
            }

            if (gpuFieldVxBuffer != null)
            {
                gpuFieldVxBuffer.Dispose();
                gpuFieldVxBuffer = null;
            }

            if (gpuFieldVzBuffer != null)
            {
                gpuFieldVzBuffer.Dispose();
                gpuFieldVzBuffer = null;
            }

            if (gpuFieldMaskBuffer != null)
            {
                gpuFieldMaskBuffer.Dispose();
                gpuFieldMaskBuffer = null;
            }

            if (gpuMaterial != null)
            {
                UnityEngine.Object.Destroy(gpuMaterial);
                gpuMaterial = null;
            }

            if (gpuMesh != null)
            {
                UnityEngine.Object.Destroy(gpuMesh);
                gpuMesh = null;
            }

            gpuRecords = Array.Empty<float>();
            gpuActiveCount = 0;
            gpuFieldWidth = -1;
            gpuFieldHeight = -1;
            gpuFieldVersion = int.MinValue;
            gpuFieldVxSource = Array.Empty<float>();
            gpuFieldVzSource = Array.Empty<float>();
            gpuFieldMaskSource = Array.Empty<float>();
            gpuReady = false;
        }

        private void EmitArcTrailSlice(
            in Particle particle,
            float altitude,
            float length,
            int segments,
            float halfWidth,
            Color32 color,
            int vertexOffset,
            int indexOffset,
            int bucketVertexBase)
        {
            EnsureThreadScratch();
            Vector3[] centers = threadArcCenters;
            Vector3[] normals = threadArcNormals;
            Vector3[] directions = threadArcDirections;

            float step = length / segments;
            float size = Mathf.Max(0.05f, particle.Size);

            float x = particle.X;
            float z = particle.Z;
            float dirX = particle.DirX;
            float dirZ = particle.DirZ;
            centers[0] = new Vector3(x, altitude, z);

            for (int i = 1; i <= segments; i++)
            {
                // Blend rather than snap: AO's snapshot is per-cell, so following the
                // raw sample makes the ribbon zig-zag at cell boundaries.
                bool tapHere = ArcSamplingStride <= 1 || (i % ArcSamplingStride) == 1;
                if (tapHere && SampleVelocityRaw(x, z, out float sampledX, out float sampledZ))
                {
                    float speed = Mathf.Sqrt(sampledX * sampledX + sampledZ * sampledZ);
                    if (IsFinite(speed) && speed > 0.05f)
                    {
                        float blendX = dirX * (1f - ArcDirectionBlend)
                            + (sampledX / speed) * ArcDirectionBlend;
                        float blendZ = dirZ * (1f - ArcDirectionBlend)
                            + (sampledZ / speed) * ArcDirectionBlend;
                        float blendLength = Mathf.Sqrt(blendX * blendX + blendZ * blendZ);
                        if (IsFinite(blendLength) && blendLength > 1e-5f)
                        {
                            dirX = blendX / blendLength;
                            dirZ = blendZ / blendLength;
                        }
                    }
                }

                x -= dirX * step;
                z -= dirZ * step;
                centers[i] = new Vector3(x, altitude, z);
            }

            Vector3 fallbackNormal = new Vector3(-particle.DirZ, 0f, particle.DirX);

            // One direction per segment, computed once. The joint loop below needs each
            // segment's direction twice (as "next" for joint i and "previous" for joint
            // i+1); recomputing it there means a sqrt and two divides per joint, twice
            // over. Same values, just not repeated.
            for (int i = 0; i < segments; i++)
            {
                directions[i] = SegmentDirection(centers[i], centers[i + 1]);
            }

            for (int i = 0; i <= segments; i++)
            {
                Vector3 previousDirection = i > 0 ? directions[i - 1] : Vector3.zero;
                Vector3 nextDirection = i < segments ? directions[i] : Vector3.zero;

                Vector3 previousNormal = new Vector3(-previousDirection.z, 0f, previousDirection.x);
                Vector3 nextNormal = new Vector3(-nextDirection.z, 0f, nextDirection.x);

                Vector3 normal;
                if (i == 0)
                {
                    normal = nextNormal;
                }
                else if (i == segments)
                {
                    normal = previousNormal;
                }
                else
                {
                    normal = previousNormal + nextNormal;
                    float miterLength = normal.magnitude;
                    if (!IsFinite(miterLength) || miterLength < 1e-4f)
                    {
                        // U-turn: the mitre has no direction, so fall back to the segment.
                        normal = previousNormal;
                    }
                    else
                    {
                        normal /= miterLength;

                        // A plain perpendicular shortens the ribbon on the inside of a
                        // bend. Dividing by the cosine of half the turn keeps the
                        // visible width constant; the clamp stops a hairpin from
                        // shooting out a huge fin.
                        normal /= Mathf.Max(0.5f, Vector3.Dot(normal, previousNormal));
                    }
                }

                // A degenerate segment leaves a zero normal, which would collapse this
                // joint's two vertices onto the centreline.
                normals[i] = normal.sqrMagnitude > 1e-8f
                    ? normal
                    : (i > 0 ? normals[i - 1] : fallbackNormal);
            }

            float inverseSegments = 1f / segments;
            int vertex = vertexOffset;
            for (int i = 0; i <= segments; i++)
            {
                // f = 0 at the head, 1 at the tail; the texture runs 0 (tail) to 1 (head).
                float u = 1f - i * inverseSegments;
                float width = halfWidth
                    * Mathf.Lerp(ArcTailWidthFactor, ArcHeadWidthFactor, u)
                    * size;
                Vector3 offset = normals[i] * width;

                flatVertices[vertex] = centers[i] - offset;
                flatVertices[vertex + 1] = centers[i] + offset;
                flatColors[vertex] = color;
                flatColors[vertex + 1] = color;
                flatUvs[vertex] = new Vector2(u, 0f);
                flatUvs[vertex + 1] = new Vector2(u, 1f);
                vertex += 2;
            }

            // Winding mirrors EmitQuad: (p0-n, p0+n, p1+n, p1-n) with (0,1,2)(0,2,3).
            int index = indexOffset;
            for (int i = 0; i < segments; i++)
            {
                int v0 = vertexOffset + i * 2 - bucketVertexBase;
                flatIndices[index] = v0;
                flatIndices[index + 1] = v0 + 1;
                flatIndices[index + 2] = v0 + 3;
                flatIndices[index + 3] = v0;
                flatIndices[index + 4] = v0 + 3;
                flatIndices[index + 5] = v0 + 2;
                index += 6;
            }
        }

        [MemberNotNull(
            nameof(threadArcCenters),
            nameof(threadArcNormals),
            nameof(threadArcDirections))]
        private static void EnsureThreadScratch()
        {
            // All three are always allocated together, so testing the tuple is the same
            // condition as testing centers alone — and it is the one the compiler accepts.
            if (threadArcCenters != null
                && threadArcNormals != null
                && threadArcDirections != null)
            {
                return;
            }

            threadArcCenters = new Vector3[ArcTrailSegments + 1];
            threadArcNormals = new Vector3[ArcTrailSegments + 1];
            threadArcDirections = new Vector3[ArcTrailSegments + 1];
        }

        private void EmitParticleSlice(int index)
        {
            int segments = particleSegments[index];
            if (segments == 0)
            {
                return;
            }

            EmitArcTrailSlice(
                particles[index],
                emitAltitude,
                particleLength[index],
                segments,
                emitHalfWidth,
                particleColor[index],
                particleVertexOffset[index],
                particleIndexOffset[index],
                bucketVertexStart[particleBucket[index]]);
        }

        private static Vector3 SegmentDirection(Vector3 from, Vector3 to)
        {
            float dx = to.x - from.x;
            float dz = to.z - from.z;
            float length = Mathf.Sqrt(dx * dx + dz * dz);
            if (!IsFinite(length) || length < 1e-5f)
            {
                return Vector3.zero;
            }

            return new Vector3(dx / length, 0f, dz / length);
        }

        private static void EmitQuad(
            List<Vector3> targetVertices,
            List<Color32> targetColors,
            List<Vector2> targetUvs,
            List<int> targetTriangles,
            Vector3 p0,
            Vector3 p1,
            Vector3 normal0,
            Vector3 normal1,
            float width0,
            float width1,
            float u0,
            float u1,
            Color32 color)
        {
            int baseIndex = targetVertices.Count;

            targetVertices.Add(p0 - normal0 * width0);
            targetVertices.Add(p0 + normal0 * width0);
            targetVertices.Add(p1 + normal1 * width1);
            targetVertices.Add(p1 - normal1 * width1);

            targetColors.Add(color);
            targetColors.Add(color);
            targetColors.Add(color);
            targetColors.Add(color);

            targetUvs.Add(new Vector2(u0, 0f));
            targetUvs.Add(new Vector2(u0, 1f));
            targetUvs.Add(new Vector2(u1, 1f));
            targetUvs.Add(new Vector2(u1, 0f));

            targetTriangles.Add(baseIndex);
            targetTriangles.Add(baseIndex + 1);
            targetTriangles.Add(baseIndex + 2);
            targetTriangles.Add(baseIndex);
            targetTriangles.Add(baseIndex + 2);
            targetTriangles.Add(baseIndex + 3);
        }

        private static Vector3 DirectionAt(List<FlowPoint> points, int index)
        {
            int previous = Mathf.Max(0, index - 1);
            int next = Mathf.Min(points.Count - 1, index + 1);
            float dx = points[next].X - points[previous].X;
            float dz = points[next].Z - points[previous].Z;
            float length = Mathf.Sqrt(dx * dx + dz * dz);
            if (length < 1e-5f || !IsFinite(length))
            {
                return new Vector3(1f, 0f, 0f);
            }

            return new Vector3(dx / length, 0f, dz / length);
        }

        private static float Distance(FlowPoint a, FlowPoint b)
        {
            float dx = b.X - a.X;
            float dz = b.Z - a.Z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float TaperAt(float arc, float totalLength, float fadeCells)
        {
            float fromStart = Mathf.Clamp01(arc / fadeCells);
            float fromEnd = Mathf.Clamp01((totalLength - arc) / fadeCells);
            float k = Mathf.Min(fromStart, fromEnd);
            return k * k * (3f - 2f * k);
        }

        private static int Bucket(float speed)
        {
            if (speed < 1.5f)
            {
                return 0;
            }
            if (speed < 5f)
            {
                return 1;
            }
            if (speed < 12f)
            {
                return 2;
            }
            return 3;
        }

        private bool IsOpen(float x, float z)
        {
            // One comparison chain replaces three separate guards: every comparison with
            // NaN is false, so the IsFinite pair added nothing, and inside this range the
            // rounding below cannot leave [1, width-2], so both clamps were dead code.
            // Being small enough to inline matters more than the guards: the long form
            // measured 17 us of a 97 us loop at 1451 particles, and the two grid reads it
            // saves are only 0.2 us of that. It runs once per particle per rebuild.
            if (!(x >= 1f && z >= 1f && x <= width - 2f && z <= height - 2f))
            {
                return false;
            }

            int xi = (int)MathF.Round(x);
            int zi = (int)MathF.Round(z);
            int index = zi * width + xi;

            if ((uint)index >= (uint)obstacleMask.Length || obstacleMask[index] <= 0.5f)
            {
                return false;
            }

            // No fog test here any more: the layer is drawn under the fog of war (see
            // BaseRenderQueue), so fogged cells are hidden by the fog itself. Dropping it
            // also keeps this method small enough to inline, which matters more than the
            // two guards it used to carry.
            //
            // Roofs are intentionally allowed: AO simulates indoor air (fans, vents,
            // pressure equalisation), and its original renderer draws those flows too.
            return true;
        }

        private bool SampleVelocity(float x, float z, out float sampledX, out float sampledZ)
        {
            if (!smoothField)
            {
                return SampleField(velocityX, velocityZ, x, z, out sampledX, out sampledZ);
            }

            // The pre-blurred field carries the same smoothing the five taps below
            // approximate, so one bilinear tap is enough.
            if (smoothReady)
            {
                return SampleField(smoothVelocityX, smoothVelocityZ, x, z, out sampledX, out sampledZ);
            }

            float sumX = 0f;
            float sumZ = 0f;
            float sumWeight = 0f;
            AddVelocityTap(x, z, 0.40f, ref sumX, ref sumZ, ref sumWeight);
            AddVelocityTap(x + 0.85f, z, 0.15f, ref sumX, ref sumZ, ref sumWeight);
            AddVelocityTap(x - 0.85f, z, 0.15f, ref sumX, ref sumZ, ref sumWeight);
            AddVelocityTap(x, z + 0.85f, 0.15f, ref sumX, ref sumZ, ref sumWeight);
            AddVelocityTap(x, z - 0.85f, 0.15f, ref sumX, ref sumZ, ref sumWeight);

            if (sumWeight < 1e-4f)
            {
                sampledX = 0f;
                sampledZ = 0f;
                return false;
            }

            sampledX = sumX / sumWeight;
            sampledZ = sumZ / sumWeight;
            return IsFinite(sampledX) && IsFinite(sampledZ);
        }

        private void AddVelocityTap(
            float x,
            float z,
            float weight,
            ref float sumX,
            ref float sumZ,
            ref float sumWeight)
        {
            if (!SampleVelocityRaw(x, z, out float tapX, out float tapZ))
            {
                return;
            }

            sumX += tapX * weight;
            sumZ += tapZ * weight;
            sumWeight += weight;
        }

        private bool SampleVelocityRaw(float x, float z, out float sampledX, out float sampledZ)
        {
            return SampleField(velocityX, velocityZ, x, z, out sampledX, out sampledZ);
        }

        /// <summary>
        /// Mask-weighted bilinear tap on an arbitrary velocity field pair. Mask weighting
        /// keeps a sample near a wall from averaging in the zeroed blocked cell.
        /// </summary>
        private bool SampleField(
            float[] fieldX,
            float[] fieldZ,
            float x,
            float z,
            out float sampledX,
            out float sampledZ)
        {
            sampledX = 0f;
            sampledZ = 0f;

            if (!IsFinite(x) || !IsFinite(z)
                || x < 0f || z < 0f || x > width - 1f || z > height - 1f)
            {
                return false;
            }

            // x and z are finite and non-negative here, so truncation is floor and the
            // lower clamp can never fire; only the upper one can. That same bound keeps
            // fx and fz inside [0, 1], which made both Clamp01 calls dead work.
            int x0 = (int)x;
            if (x0 > width - 2)
            {
                x0 = width - 2;
            }

            int z0 = (int)z;
            if (z0 > height - 2)
            {
                z0 = height - 2;
            }

            int x1 = x0 + 1;
            int z1 = z0 + 1;

            float fx = x - x0;
            float fz = z - z0;
            float fx0 = 1f - fx;
            float fz0 = 1f - fz;

            int i00 = z0 * width + x0;
            int i10 = z0 * width + x1;
            int i01 = z1 * width + x0;
            int i11 = z1 * width + x1;

            float w00 = fx0 * fz0 * obstacleMask[i00];
            float w10 = fx * fz0 * obstacleMask[i10];
            float w01 = fx0 * fz * obstacleMask[i01];
            float w11 = fx * fz * obstacleMask[i11];
            float denominator = w00 + w10 + w01 + w11;

            if (denominator < 1e-4f)
            {
                return false;
            }

            float inv = 1f / denominator;
            sampledX = (fieldX[i00] * w00 + fieldX[i10] * w10 + fieldX[i01] * w01 + fieldX[i11] * w11) * inv;
            sampledZ = (fieldZ[i00] * w00 + fieldZ[i10] * w10 + fieldZ[i01] * w01 + fieldZ[i11] * w11) * inv;
            return IsFinite(sampledX) && IsFinite(sampledZ);
        }

        /// <summary>
        /// The two styles keep their geometry in different places: streamlines in the
        /// per-bucket lists, particles in the flat arrays. Decide emptiness per style so a
        /// style switch cannot leave a stale mesh being drawn.
        /// </summary>
        private bool HasBucketGeometry(int bucket)
        {
            return particleStyle ? bucketVertexCount[bucket] > 0 : vertices[bucket].Count > 0;
        }

        private void ClearGeometry()
        {
            lastLineCount = 0;
            lastSegmentCount = 0;
            lastDashCount = 0;

            for (int i = 0; i < BucketCount; i++)
            {
                bucketVertexCount[i] = 0;
                bucketIndexCount[i] = 0;
            }

            // Only the CPU-side lists need clearing. The meshes are fully replaced by
            // SetVertices/SetTriangles on the next upload and the draw guards check the
            // list counts, so calling Mesh.Clear here would just churn GPU buffers.
            baseVertices.Clear();
            baseColors.Clear();
            baseUvs.Clear();
            baseTriangles.Clear();

            for (int i = 0; i < BucketCount; i++)
            {
                vertices[i].Clear();
                colors[i].Clear();
                uvs[i].Clear();
                triangles[i].Clear();
            }
        }

        /// <summary>
        /// Vertex layout written by <see cref="UploadMesh"/>. Same semantics the compat path
        /// produced (position float3, colour UNorm8x4, uv0 float4) so the stock Transparent
        /// shader keeps reading the same channels.
        /// </summary>
        private static readonly VertexAttributeDescriptor[] VertexLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
        };

        private void UploadMeshes(float altitude)
        {
            EnsureResources();

            if (baseVertices.Count > 0)
            {
                UploadMesh(
                    baseMesh,
                    CollectionsMarshal.AsSpan(baseVertices),
                    CollectionsMarshal.AsSpan(baseColors),
                    CollectionsMarshal.AsSpan(baseUvs),
                    CollectionsMarshal.AsSpan(baseTriangles),
                    altitude);
            }

            for (int i = 0; i < BucketCount; i++)
            {
                if (vertices[i].Count > 0)
                {
                    UploadMesh(
                        meshes[i],
                        CollectionsMarshal.AsSpan(vertices[i]),
                        CollectionsMarshal.AsSpan(colors[i]),
                        CollectionsMarshal.AsSpan(uvs[i]),
                        CollectionsMarshal.AsSpan(triangles[i]),
                        altitude);
                }
            }
        }

        /// <summary>
        /// Writes the vertex/index streams straight into a writable mesh. The Mesh.SetVertices
        /// family is far more expensive than it looks in 1.7: every call does
        /// <c>List.ToArray()</c>, and the next draw re-interleaves the whole thing into the
        /// engine's CpuMeshData. For the particle layer that measured ~400 us and 616 KB of
        /// gen0 garbage per rebuild. One pass here replaces all of it with no managed
        /// allocation beyond the small layout array inside SetVertexBufferParams.
        /// </summary>
        private unsafe void UploadMesh(
            Mesh mesh,
            ReadOnlySpan<Vector3> sourceVertices,
            ReadOnlySpan<Color32> sourceColors,
            ReadOnlySpan<Vector2> sourceUvs,
            ReadOnlySpan<int> sourceTriangles,
            float altitude)
        {
            if (mesh == null)
            {
                return;
            }

            int vertexCount = sourceVertices.Length;
            int indexCount = sourceTriangles.Length;
            if (vertexCount == 0 || indexCount == 0)
            {
                return;
            }

            Mesh.MeshDataArray data = Mesh.AllocateWritableMeshData(1);
            Mesh.MeshData writable = data[0];
            writable.SetVertexBufferParams(vertexCount, VertexLayout);
            writable.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
            writable.subMeshCount = 1;

            // ApplyAndDispose leaves bounds at zero, which would let the renderer cull the
            // whole layer. One static box covering the map is cheaper than rescanning 26k
            // vertices per rebuild and just as correct: we only ever draw geometry that is
            // inside the view anyway.
            Bounds bounds = new Bounds(
                new Vector3(width * 0.5f, altitude, height * 0.5f),
                new Vector3(width + 8f, 16f, height + 8f));
            SubMeshDescriptor descriptor = new SubMeshDescriptor(0, indexCount, MeshTopology.Triangles)
            {
                bounds = bounds,
                vertexCount = vertexCount,
            };
            writable.SetSubMesh(0, descriptor);

            int stride = writable.GetVertexBufferStride(0);
            byte* stream = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(writable.GetVertexData<byte>(0));

            for (int i = 0; i < vertexCount; i++)
            {
                byte* vertex = stream + i * stride;
                Vector3 position = sourceVertices[i];
                *(float*)vertex = position.x;
                *(float*)(vertex + 4) = position.y;
                *(float*)(vertex + 8) = position.z;

                Color32 color = sourceColors[i];
                vertex[12] = color.r;
                vertex[13] = color.g;
                vertex[14] = color.b;
                vertex[15] = color.a;

                Vector2 uv = sourceUvs[i];
                *(float*)(vertex + 16) = uv.x;
                *(float*)(vertex + 20) = uv.y;
                *(float*)(vertex + 24) = 0f;
                *(float*)(vertex + 28) = 0f;
            }

            int* indices = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(writable.GetIndexData<int>());
            for (int i = 0; i < indexCount; i++)
            {
                indices[i] = sourceTriangles[i];
            }

            Mesh.ApplyAndDisposeWritableMeshData(data, mesh);
            mesh.bounds = bounds;
        }

        [MemberNotNull(
            nameof(baseMesh),
            nameof(baseMaterial),
            nameof(lineTexture),
            nameof(cometTexture),
            nameof(fineTexture),
            nameof(particleTexture))]
        private void EnsureResources()
        {
            if (lineTexture == null)
            {
                lineTexture = CreateLineTexture();
            }

            if (cometTexture == null)
            {
                cometTexture = CreateCometTexture();
            }

            if (fineTexture == null)
            {
                fineTexture = CreateFineTexture();
            }

            if (particleTexture == null)
            {
                particleTexture = CreateParticleTexture();
            }

            if (baseMesh == null)
            {
                baseMesh = NewMesh("AOWindStreamlineBase");
            }

            if (baseMaterial == null)
            {
                baseMaterial = NewMaterial("AOWindStreamlineBaseMat", BaseColor, lineTexture, BaseRenderQueue);
            }

            // One line at startup. If the fog ever stops covering this layer, this number
            // is the reason: our queue has to sit below the fog's.
            VisualTweaksLog.TraceOnce(
                ref layerLogged,
                "queue " + BaseRenderQueue
                    + ", fog queue " + (MatBases.FogOfWar != null ? MatBases.FogOfWar.renderQueue : -1)
                    + ", fog altitude " + Altitudes.AltitudeFor(AltitudeLayer.FogOfWar));

            for (int i = 0; i < BucketCount; i++)
            {
                if (meshes[i] == null)
                {
                    meshes[i] = NewMesh("AOWindStreamlineBucket" + i);
                }

                if (materials[i] == null)
                {
                    materials[i] = NewMaterial(
                        "AOWindStreamlineMat" + i,
                        BucketColors[i],
                        cometTexture,
                        DashRenderQueue);
                }
            }
        }

        private static Mesh NewMesh(string name)
        {
            Mesh mesh = new Mesh
            {
                name = name
            };
            mesh.MarkDynamic();
            mesh.indexFormat = IndexFormat.UInt32;
            return mesh;
        }

        private static Material NewMaterial(string name, Color color, Texture texture, int renderQueue)
        {
            return new Material(ShaderDatabase.Transparent)
            {
                name = name,
                color = color,
                mainTexture = texture,
                renderQueue = renderQueue
            };
        }

        private static Texture2D CreateLineTexture()
        {
            Texture2D texture = NewTexture("AOWindStreamlineGuide");
            Color32[] pixels = new Color32[DashTextureWidth * DashTextureHeight];

            for (int y = 0; y < DashTextureHeight; y++)
            {
                float v = (y + 0.5f) / DashTextureHeight;
                float edge = Mathf.Sin(Mathf.PI * v);
                byte alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(edge) * 255f);

                for (int x = 0; x < DashTextureWidth; x++)
                {
                    pixels[y * DashTextureWidth + x] = new Color32(255, 255, 255, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static Texture2D CreateCometTexture()
        {
            Texture2D texture = NewTexture("AOWindStreamlineComet");
            Color32[] pixels = new Color32[DashTextureWidth * DashTextureHeight];

            for (int y = 0; y < DashTextureHeight; y++)
            {
                float v = (y + 0.5f) / DashTextureHeight;
                float edge = Mathf.Sin(Mathf.PI * v);

                for (int x = 0; x < DashTextureWidth; x++)
                {
                    float u = (x + 0.5f) / DashTextureWidth;
                    float tail = Mathf.Pow(u, 0.55f);
                    float cut = 1f - Mathf.Pow(u, 10f);
                    float head = 0.7f * Mathf.Exp(-Mathf.Pow((u - 0.88f) / 0.07f, 2f));
                    float along = Mathf.Clamp01(tail * cut + head);
                    float alpha = Mathf.Clamp01(along * edge);
                    pixels[y * DashTextureWidth + x] = new Color32(
                        255,
                        255,
                        255,
                        (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        /// <summary>
        /// Particle trail silhouette, modelled on Gunplay's TrailTexture: a flat,
        /// fully opaque core with a soft shoulder across the width, and along the
        /// length a bright body that only dims to a semi-transparent head instead of
        /// vanishing. Per-vertex alpha handles the life fade independently of this.
        /// </summary>
        private static Texture2D CreateParticleTexture()
        {
            Texture2D texture = NewTexture(
                "AOWindStreamlineParticle",
                ParticleTextureWidth,
                ParticleTextureHeight);
            Color32[] pixels = new Color32[ParticleTextureWidth * ParticleTextureHeight];

            for (int y = 0; y < ParticleTextureHeight; y++)
            {
                float v = (y + 0.5f) / ParticleTextureHeight;
                float cross = TrailCrossAlpha(v);

                for (int x = 0; x < ParticleTextureWidth; x++)
                {
                    float u = (x + 0.5f) / ParticleTextureWidth;
                    float alpha = TrailAlongAlpha(u) * cross;

                    pixels[y * ParticleTextureWidth + x] = new Color32(
                        255,
                        255,
                        255,
                        (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        // u = 0 at the tail, 1 at the leading head.
        private static float TrailAlongAlpha(float u)
        {
            const float TopFade = 0.09f;
            const float TopAlpha = 0.22f;
            const float TopFadeCurve = 1.7f;
            const float TailFalloff = 1.15f;

            if (u >= 1f - TopFade)
            {
                float t = (u - (1f - TopFade)) / TopFade;
                return Mathf.Lerp(1f, TopAlpha, Mathf.Pow(t, TopFadeCurve));
            }

            return Mathf.Pow(u / (1f - TopFade), TailFalloff);
        }

        private static float TrailCrossAlpha(float v) // v = 0..1 across the width
        {
            const float FlatCore = 0.5f;
            const float EdgeFalloff = 1.6f;

            // d = 0 on the centre line, 1 at the quad edge.
            float d = Mathf.Abs(2f * v - 1f);
            if (d <= FlatCore)
            {
                return 1f;
            }

            float t = (d - FlatCore) / (1f - FlatCore);

            // Gunplay keeps a fixed alpha floor at the edge (0.35), which prints a hard
            // line once the streak is only a few pixels wide. Falling to zero at the
            // quad edge instead keeps the same shoulder shape without that seam.
            return Mathf.Pow(1f - t, EdgeFalloff);
        }

        private static Texture2D CreateFineTexture()
        {
            Texture2D texture = NewTexture("AOWindStreamlineFine");
            Color32[] pixels = new Color32[DashTextureWidth * DashTextureHeight];

            for (int y = 0; y < DashTextureHeight; y++)
            {
                float v = (y + 0.5f) / DashTextureHeight;
                float edge = Mathf.Sin(Mathf.PI * v);

                for (int x = 0; x < DashTextureWidth; x++)
                {
                    float u = (x + 0.5f) / DashTextureWidth;
                    float along = Mathf.Sin(Mathf.PI * u);
                    float alpha = Mathf.Clamp01(along * edge);
                    pixels[y * DashTextureWidth + x] = new Color32(
                        255,
                        255,
                        255,
                        (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static Texture2D NewTexture(
            string name,
            int width = DashTextureWidth,
            int height = DashTextureHeight)
        {
            return new Texture2D(
                width,
                height,
                TextureFormat.RGBA32,
                false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
        }

        private static float Wrap01(float value)
        {
            value -= Mathf.Floor(value);
            return value;
        }

        // float.IsFinite rather than the hand-rolled pair: the two-property form compiles
        // to a call the JIT will not inline, and this sits inside SampleVelocityRaw (twice,
        // per tap) plus a dozen other hot spots. The built-in is the same predicate and
        // inlines to a couple of instructions.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFinite(float value)
        {
            return float.IsFinite(value);
        }

        private static float Hash01(int value)
        {
            unchecked
            {
                uint x = (uint)(value * 747796405 + 2891336453);
                x = ((x >> (int)((x >> 28) + 4)) ^ x) * 277803737u;
                x = (x >> 22) ^ x;
                return (x & 0xFFFFFFu) / 16777216f;
            }
        }

        public void Dispose()
        {
            if (baseMesh != null)
            {
                UnityEngine.Object.Destroy(baseMesh);
                baseMesh = null;
            }

            if (baseMaterial != null)
            {
                UnityEngine.Object.Destroy(baseMaterial);
                baseMaterial = null;
            }

            if (lineTexture != null)
            {
                UnityEngine.Object.Destroy(lineTexture);
                lineTexture = null;
            }

            for (int i = 0; i < BucketCount; i++)
            {
                if (meshes[i] != null)
                {
                    UnityEngine.Object.Destroy(meshes[i]);
                    Array.Clear(meshes, i, 1);
                }

                if (materials[i] != null)
                {
                    UnityEngine.Object.Destroy(materials[i]);
                    Array.Clear(materials, i, 1);
                }
            }

            if (cometTexture != null)
            {
                UnityEngine.Object.Destroy(cometTexture);
                cometTexture = null;
            }

            if (fineTexture != null)
            {
                UnityEngine.Object.Destroy(fineTexture);
                fineTexture = null;
            }

            if (particleTexture != null)
            {
                UnityEngine.Object.Destroy(particleTexture);
                particleTexture = null;
            }

            DisposeGpuResources();
        }

        private struct Particle
        {
            public float X;
            public float Z;
            public float DirX;
            public float DirZ;
            public float Speed;
            public float Age;
            public float Life;
            public float FadeInAge;
            public float Travel;
            public float Size;
            public float Brightness;

            // Eased 0..1 alpha from the low-wind hide threshold. Independent of the
            // spawn/death fades so it can respond to the local wind speed alone.
            public float Visibility;

            // Set when the particle stopped moving for a reason other than old age
            // (wall, fog, dead air, cull edge). It holds its last position and dissolves.
            public bool Dying;
            public float DeathAge;
        }

        private struct FlowPoint
        {
            public float X;
            public float Z;
            public float Speed;

            public FlowPoint(float x, float z, float speed)
            {
                X = x;
                Z = z;
                Speed = speed;
            }
        }
    }
}
