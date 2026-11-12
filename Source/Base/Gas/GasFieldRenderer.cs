using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Draws the gas/mist field as one view-sized quad. AO's per-cell sprite stacks are
    /// replaced by a density texture (one texel per cell) that the fragment shader turns
    /// into a continuous, noise-driven cloud.
    ///
    /// Two texture pairs are kept per field so each simulation publish can cross-fade
    /// into the next one; AO publishes mist every few ticks, so without the fade the
    /// density would visibly step.
    /// </summary>
    internal sealed class GasFieldRenderer : IDisposable
    {
        private const int RenderQueue = 2990;
        // Fallback cadence: AO's default of 6 ticks at 60 TPS. The live value comes from
        // CurrentPublishPeriod(), because AO lets the interval be 3..30 ticks and ticks
        // themselves run faster at 2x/3x. The refresh timer uses it as-is.
        private const float DefaultRefreshSeconds = 0.1f;

        // The cross-fade, however, must be *shorter* than the period. It used to use the
        // period itself, on the reasoning that two textures plus exactly one period of fade
        // neither freezes nor lets a third snapshot overlap. What that missed is the
        // boundary: fadeStart and lastTimedRefresh are both stamped with the same realtime
        // on a publish frame, so fade reaches 1 on precisely the frame the next publish
        // restarts it. The screen then never holds a fully faded frame — it runs
        // 0 -> 0.98 -> 0 once per period, a whole-layer square wave at the publish rate
        // (first real log: 379 fade restarts, 508 fade jumps, 762 flash candidates in four
        // minutes). The leftover fraction below is the stable tail that turns the fade back
        // into a transition. 0.6 keeps the blend soft (6 frames at 10 Hz) without ever
        // touching 1 only on the restart frame.
        private const float FadeFraction = 0.9f;
        private const float FadeFloorFraction = 0.35f;
        private const float FadeFrames = 2.4f;

        /// <summary>Hard ceiling on one blend, so a single hitch cannot stretch it.</summary>
        // 0.08 rather than 0.12: the gate below guarantees the blend finishes before the
        // next publish, so a shorter blend costs nothing in continuity and keeps the field
        // updating at 12-20 Hz instead of 5-7. It was 0.12 while the frame rate was being
        // measured down by the screen probe, and it stretched the publish interval with it.
        private const float MaxFadeSpan = 0.08f;

        // Below ~50 ms the bake cost stops paying for itself (the screen cannot show more
        // than one update per frame anyway); above a second the delay becomes obvious.
        private const float MinPublishPeriod = 0.05f;
        private const float MaxPublishPeriod = 1f;
        private const float DetailFadeStartPixels = 8f;
        private const float DetailFadeRangePixels = 18f;

        private const float Inv255 = 1f / 255f;

        /// <summary>
        /// The value below which <see cref="ToByte"/> returns 0 — RoundToInt(v * 255) rounds
        /// 0.5 to the even number, i.e. to 0, so anything under half a byte is black.
        ///
        /// The empty-cell fast path in BakeGasRow collapses the history to zero at exactly
        /// this value. That is not an approximation of the picture, it is the picture: every
        /// one of those cells was converted to 0 anyway. If ToByte ever changes its rounding,
        /// this constant has to change with it, or faint gas will either disappear early or
        /// leave a residue that never clears.
        /// </summary>
        private const float HalfByte = 0.5f / 255f;

        // Local flow field for the noise animation. The density textures are one texel per
        // cell; this one is deliberately much coarser: the noise only needs to know which
        // way the gas is being pushed, and eight cells per texel still resolves the main
        // indoor/outdoor split and the big channeling. Coarser also means every whole-map
        // pass below (integration, blur, staging) shrinks with the square of this value.
        //
        // It stores the *accumulated displacement*, not the current velocity. Displacement
        // (integral of v dt) is what makes the pattern follow the gas. The first version
        // used velocity * elapsed time, which is only valid while the velocity is constant:
        // every solver publish rewrote the wind and the pattern teleported by
        // (newV - oldV) * elapsed, and an explosion (AO injects up to ExplosionMaxWindSpeed
        // = 60 for a few publishes) rewrote it hard enough to shear the noise into static.
        // Integrating on the CPU makes the position continuous across wind updates; the
        // shader only extrapolates the last publish's velocity in between uploads.
        //
        // The history grid is anchored to map cell (0,0), and the texture uploaded to the
        // GPU is that same grid — not a resampled window. A camera-relative texture had to
        // be rebuilt on every zoom step, which made the noise shimmer locally while the
        // camera moved.
        private const int FlowCellSize = 8;

        // Exponential low-pass on the velocity before it is integrated. This is what turns
        // the solver's discrete (and occasionally explosive) wind rewrites into a smooth
        // ramp: a one-publish 60 m/s impulse barely moves the integrated displacement.
        private const float FlowVelocityTau = 0.22f;

        // Velocity magnitude cap. Normal weather is 0..15 m/s; the cap only clips the
        // explosion impulse, so a blast still pushes the pattern but cannot fling it.
        private const float FlowWindCap = 20f;

        // A persistent wind difference between two regions (a sealed room and the storm
        // outside) makes their accumulated displacements diverge forever. A diffusion term
        // cannot stop that: it widens the transition but the gradient keeps growing with
        // time (verified numerically), and once the noise coordinate is compressed by an
        // order of magnitude the detail turns into stripes.
        //
        // So the displacement is split into a uniform part that follows the map-wide mean
        // wind, and a local deviation that is relaxed back toward that base with this time
        // constant. The deviation still follows the local wind for a few seconds (so wakes
        // and wall lees bend the pattern), but it can never grow without bound.
        private const float FlowDeviationTau = 2f;

        // Hard bound on the local deviation, for gusts and explosions. Beyond this the
        // pattern stops following the local wind rather than shearing apart.
        private const float FlowMaxDeviation = 8f;

        // Light blur applied while relaxing the deviation. It keeps the transition between
        // two wind regimes wide enough that the noise frequency does not visibly jump.
        private const int FlowRelaxPasses = 3;

        // Least time a single publish may integrate. AO's interval is configurable up to
        // 30 ticks (0.5 s at 1x), so the actual bound is refreshPeriod * 1.5 (see
        // FlowStepLimit); this floor only matters at the fast end.
        private const float FlowMinStep = 0.25f;

        // Ceiling on how fast the visual clock may run, as a multiple of real time.
        //
        // TickRateMultiplier is not bounded. RimWorld 1.7 returns 20000 when
        // UltraSpeedBoost is on and 150 for a map-less Ultrafast; speed mods push it into
        // the thousands as well. The noise offset is integrated from this clock, so an
        // unclamped multiplier made the renderer advance ~100 game-seconds per frame while
        // FlowStepLimit stayed at 0.25 s — the second log shows exactly that:
        // lastClockAdvance = 102.5 with deltaTime = 0.0625, i.e. x1640. Every publish then
        // moved the pattern by the difference, and the whole layer flickered.
        //
        // 4x keeps every vanilla speed up to Fast exactly as it was, lets Superfast and
        // Ultrafast run the noise at a quarter and a fifteenth of the density's rate, and
        // makes an unbounded multiplier harmless. Above a few frames per second the eye
        // cannot follow the advection anyway, so nothing is lost visually.
        private const float MaxVisualRate = 4f;

        // Time constant of the continuous-path smoothing lives in the settings, since
        // the cross-fade already hides the 10 Hz cadence and only the size of the jump
        // between publishes is left for the smoothing to absorb.

        // The halo is a box average this many cells across, uploaded as its own small
        // texture and magnified with bilinear filtering in the shader. 8 gives roughly the
        // support the old mip level 3 had (8-cell texel + 8-cell linear = ~16 cells), which
        // is what the gate below needs to feather a 255 -> 0 blast rim.
        // Also the alignment the window origin is snapped to (see EnsureResources): the wide
        // blocks are anchored at the origin, so the origin must be a whole number of blocks.
        private const int WideDownsample = 8;

        // INERT. These describe the "window follows the camera" design that no longer runs:
        // the density texture is the whole map, so there is no margin, no slack and no
        // recentre to space out. They are kept only because the degenerate recentre block
        // still reads them (it always computes the same 0), and because deleting them in
        // the same pass as the behavioural change would mix two kinds of edit. If that
        // block is ever removed, remove these with it.
        private const int BakeMargin = 17;
        private const int PanSlack = 9;

        // INERT. Capacity is the map size now, so there is nothing to round and no
        // geometric growth left to do.
        private const int CapacityQuantum = 16;

        // The window only steps in 8-cell increments. Rounding *up* is free (the extra
        // ring is outside the view), and it turns a zoom tween from "one upload per
        // frame" into "one upload per 8 cells", which matters because every upload
        // rebuilds the GPU image.
        //
        // INERT for the same reason as BakeMargin: the window never changes size any more.
        private const int WindowQuantum = 8;

        // A window may grow on contact, but it may only shrink after the surplus has held
        // for this many rendered frames. Grow-on-contact is safe (the extra ring is simply
        // baked and never sampled); shrinking is not, because it moves the window back and
        // forth under a view width that jitters by a cell or two. The first log had the
        // window walking 250 -> 240 -> 248 -> 250 -> 224 -> 208 -> 184 -> 168 -> 176 in
        // 0.9 s, and every step was a full rebake: one flash per step.
        private const int WindowShrinkDelayFrames = 30;

        // Capacity is recycled only after this many rendered frames of "half of it is
        // surplus". Reallocating six textures is a GPU image rebuild plus a ~2.3 ms fence
        // wait, so a zoom that comes straight back in must not be able to trigger one.
        private const int ShrinkDelayFrames = 180;

        private static readonly Vector3[] QuadVertices = new Vector3[4];
        private static readonly int[] QuadTriangles = { 0, 1, 2, 0, 2, 3 };

        // Cached shader property ids: the string overloads hash the name on every call,
        // and Draw sets thirteen of them per frame.
        private static readonly int ParamsId = Shader.PropertyToID("_Params");
        private static readonly int Params2Id = Shader.PropertyToID("_Params2");
        private static readonly int Params3Id = Shader.PropertyToID("_Params3");
        private static readonly int Params4Id = Shader.PropertyToID("_Params4");
        private static readonly int Params6Id = Shader.PropertyToID("_Params6");
        private static readonly int Params7Id = Shader.PropertyToID("_Params7");
        private static readonly int Params8Id = Shader.PropertyToID("_Params8");
        private static readonly int Params9Id = Shader.PropertyToID("_Params9");
        private static readonly int Params10Id = Shader.PropertyToID("_Params10");
        private static readonly int Params11Id = Shader.PropertyToID("_Params11");
        private static readonly int WindTexId = Shader.PropertyToID("_WindTex");
        private static readonly int GasTexCurId = Shader.PropertyToID("_GasTexCur");
        private static readonly int GasTexPrevId = Shader.PropertyToID("_GasTexPrev");
        private static readonly int MistTexCurId = Shader.PropertyToID("_MistTexCur");
        private static readonly int MistTexPrevId = Shader.PropertyToID("_MistTexPrev");
        private static readonly int GasTexWideCurId = Shader.PropertyToID("_GasTexWideCur");
        private static readonly int GasTexWidePrevId = Shader.PropertyToID("_GasTexWidePrev");

        // Texture size (capacity) and the baked window inside it, both in cells. The two
        // are deliberately decoupled:
        //
        //   capacity  - allocated once and only ever grown (geometrically), because
        //               recreating a texture costs a GPU image rebuild plus a fence wait
        //               (TextureBatchGpuWait, ~2.3 ms in a real capture).
        //   window    - what is actually baked, follows the camera every frame.
        //
        // CurrentViewRect is built with Floor/Ceil on a moving float, so its width jitters
        // by +-1 while panning, and RootSize is interpolated every frame while zooming.
        // Sizing textures from that value directly (the first version of this file) made
        // every pan and every zoom frame dispose and recreate all eight textures.
        private int width;
        private int height;
        private int winW;
        private int winH;
        private int mapWidth;
        private int mapHeight;
        private int rectMinX;
        private int rectMinZ;

        // The clipped view of the current frame. A cross-fade may only sample the
        // previous pair inside the window it was baked from; this is the rectangle the
        // previous window has to cover for the fade to be meaningful at all. Refreshed
        // by EnsureResources, which is the only place the view is clipped.
        private int viewMinX;
        private int viewMinZ;
        private int viewMaxX;
        private int viewMaxZ;

        // GPU resources and scratch buffers below. Their sizes depend on the map and on
        // the clip rect, so they cannot exist before the first frame: EnsureResources (or
        // EnsureFlowGrid) allocates every one of them before it is first read, and
        // Dispose is the only place that clears them.
        //
        // Three shapes, split by what a missing value actually means:
        //   * Buffers default to Array.Empty<T>(): a shared singleton, no allocation, and
        //     "no pixels/values yet" is a truthful description. Reads before the first
        //     bake then do nothing instead of throwing, and the field stays non-nullable,
        //     so the hot loops keep their plain `field[i]` form with no null checks.
        //   * The texture airs (current/previous) are `?` because "invalidated by a
        //     resize" is one of their real states; the pair logic already tests for it.
        //   * Handles that only exist after an Ensure* call are also `?`, and that call is
        //     marked [MemberNotNull] so the compiler hands the guarantee back. No read in
        //     the draw path pays for a null check.
        private Texture2D? gasCurrent;
        private Texture2D? gasPrevious;
        private Texture2D? mistCurrent;
        private Texture2D? mistPrevious;

        // Float density textures plus an exponential smoothing state. The smoothing state
        // survives between publishes; the colour buffer is only an upload staging area.
        private Texture2D? gasSmoothA;
        private Texture2D? gasSmoothB;
        private Texture2D? mistSmoothA;
        private Texture2D? mistSmoothB;
        // Colour32, not Color: these are upload staging buffers, and the engine stores the
        // texture as RGBA32 anyway (see NewSmoothTexture). SetPixels32 is a single memcpy
        // into the level; SetPixels(Color[]) converts pixel by pixel and was 40% of the
        // cost of a window-move frame.
        private Color32[] gasSmoothPixels = Array.Empty<Color32>();
        private Color32[] mistSmoothPixels = Array.Empty<Color32>();
        private Color32[] gasSmoothSent = Array.Empty<Color32>();
        private Color32[] mistSmoothSent = Array.Empty<Color32>();
        // Map-indexed copy of the published mist, because the temporal-patch blend must
        // not modify AO's array (gameplay code reads it too). Only window rows are filled.
        private float[] mistScratch = Array.Empty<float>();
        private float[] gasSmoothState = Array.Empty<float>();
        private float[] mistSmoothState = Array.Empty<float>();
        private bool smoothInitialized;

        // --- diagnostics only: what the screen actually showed --------------------
        // Every other probe in this file answers "did a pointer/uniform/texture change".
        // None of them answers the only question a flash report is really asking: did the
        // *picture* change between two frames? Two CPU copies answer it.
        //
        //   shadowPixels / shadowMinX..  a copy of the last gas upload, i.e. the content
        //                                the texture that becomes `previous` holds. This
        //                                mirrors exactly what _Params7 claims, because
        //                                TrackWindows sets PrevMin from the same upload.
        //   screenComposited             the composite this renderer drew last frame,
        //                                sampled on a fixed world grid over the view.
        //
        // With those, each frame can reproduce the shader's own mix (mix(prev, cur, fade))
        // in world space and measure the step. A large step on a frame where nothing else
        // moved is a visible jump; a large step that the fade hides is not. That is the
        // distinction the pointer-level snapshots could never make.
        private Color32[] shadowPixels = Array.Empty<Color32>();
        // The half of the ping-pong that was uploaded last, i.e. the CPU side of the
        // texture bound as _GasTexCur. UploadTexture swaps the staging buffers, so after
        // an upload the picture lives in what used to be `sent`.
        private Color32[] displayedGas = Array.Empty<Color32>();
        private int[] sampleX = Array.Empty<int>();
        private int[] sampleZ = Array.Empty<int>();

        /// <summary>
        /// The window each gas texture was actually uploaded with, recorded at upload time.
        ///
        /// Everything else about the cross-fade is bookkeeping: TrackWindows *says* which
        /// window the previous texture holds, and _Params7 is derived from what it says. If
        /// that claim is ever wrong the shader samples a texture through the wrong mapping
        /// and the whole field is drawn displaced by exactly the difference — for as long as
        /// it lasts, and then it snaps back when the next upload rewrites the claim. That is
        /// the "it jumps somewhere else for a frame and comes straight back" report, and no
        /// amount of diffing uniform values against each other can see it, because they are
        /// all derived from the same claim. Recording the truth separately can.
        /// </summary>
        private struct TexWindowRecord
        {
            public Texture2D? Tex;
            public int X;
            public int Z;
            public int W;
            public int H;
        }

        private TexWindowRecord gasWindowA;
        private TexWindowRecord gasWindowB;
        private int shadowMinX;
        private int shadowMinZ;
        private int shadowW;
        private int shadowH;
        private float[] screenComposited = Array.Empty<float>();
        // The current half alone, sampled at the same cells. Splitting this out answers a
        // question the composite cannot: on a frame with no upload and fade at 1, the
        // composite *is* the current texture, so any change there is the bake itself
        // churning, not the blend.
        private float[] screenCurrent = Array.Empty<float>();
        // The wide (8x8 block) field at the same cells. The halo it feeds is the only part
        // of the picture that reacts to the block grid rather than to a cell, so it is the
        // only part that can move while every density sample stays put.
        private float[] screenHalo = Array.Empty<float>();
        // Last frame's composite at the same cells, -1 where the point was not compared.
        // The snapshot has to be rewritten every frame: it used to be written only in the
        // periodic lattice rebuild, so every "frame to frame" delta was really a
        // "this frame vs up to 0.2 s ago" delta and a one-frame jump was diluted ~12x.
        private float[] screenWas = Array.Empty<float>();
        private bool screenPrimed;
        private int screenSize;
        private int latticeCount;
        private int latticeStride = 4;
        private float latticeRebuiltAt = float.NegativeInfinity;

        // Screen-space box of the drawn quad, in pixels (see RecordQuadProjection).
        private bool projectValid;
        // The four quantities that actually vary per frame in what the shader consumes.
        // Everything else it reads is either constant or derived from these and the window.
        private float liveDetail;
        private float liveCellPixels;
        private float liveOffX;
        private float liveOffZ;
        private float liveFade;
        private float prevDetail = -1f;
        private float prevCellPixels = -1f;
        private float prevOffX;
        private float prevOffZ;
        private float prevFade = -1f;
        private float deltaDetail;
        private float lastHaloDelta;
        private bool knownCleared;

        // What this renderer last wrote into the material, so the next frame can ask the
        // material whether anyone else overwrote it in between.
        //
        // This is the one remaining way the field can be drawn somewhere else for exactly
        // one frame while every CPU-side number stays correct: Graphics.DrawMesh is queued,
        // and the material's constants are read when the queue is flushed at the end of the
        // frame. If anything writes those same properties after this method returns, the
        // queued draw uses *that* value — a wrong origin for one frame, reverted the next.
        // Reading the values back at the start of the next frame detects exactly that.
        private Vector4 setParams;
        private Vector4 setParams6;
        private Vector4 setParams7;
        private bool haveSetUniforms;
        private float deltaCellPixels;
        private float deltaOff;
        private float deltaFade;

        private float projectMinX;
        private float projectMinY;
        private float projectMaxX;
        private float projectMaxY;

        /// <summary>Samples per axis of the composite snapshot (24x24 over the view).</summary>
        private const int ScreenGrid = 24;

        /// <summary>Search radius, in cells, of the frame-to-frame displacement fit.</summary>
        private const int AlignRadius = 4;

        /// <summary>How much better the shifted fit has to be before it counts as a move.</summary>
        private const float AlignGain = 0.25f;

        /// <summary>Fewest compared samples the displacement fit will trust.</summary>
        private const int MinAlignPoints = 12;

        /// <summary>Cells below this density (0..1 of one channel) are not worth sampling.</summary>
        private const float LatticeFloor = 0.003f;

        /// <summary>
        /// How often the sample set is rebuilt. Long enough that the set stays fixed across
        /// many frames (the comparison needs that), short enough to follow a pan.
        /// </summary>
        private const float LatticeSeconds = 0.2f;

        /// <summary>Mean |delta| a frame needs before it is written as a JUMP line.</summary>
        private const float ScreenJumpMean = 0.035f;

        /// <summary>Score at which a non-density input step counts as the worst of a window.</summary>
        private const float PeakOtherScore = 1f;

        // The region of the *current* window whose smoothing state is valid, in window
        // coordinates. The state is indexed by texture row/column, and those are anchored
        // at the window origin, so after a pan the rows the previous bake did not cover
        // hold whatever was there before — i.e. a different part of the map. Blending a
        // freshly exposed cell with that memory paints a ghost of the old place and decays;
        // a cell outside this rect is seeded straight from the solver instead (k = 1).
        //
        // Rectangular rather than "the first knownW columns", because a scroll makes the
        // valid part sit off the origin, and a pan on one axis only must not claim the
        // other axis is known too.
        private int knownX0;
        private int knownX1;
        private int knownZ0;
        private int knownZ1;

        // The channel's texture and smoothing state are known to be all zero. Set by the
        // content gate, cleared by any real bake, so a publish with nothing to draw costs
        // a scan instead of a per-cell loop.
        private bool gasClean;
        private bool mistClean;

        // Set when the buffers were scrolled: the staged bytes then describe the new
        // window, but the buffer the last upload came from still holds the old layout, so
        // comparing the two would be meaningless and every scroll needs its upload anyway.
        private bool forceUpload;

        private int capacitySurplusFrames;

        // Frames the window has been wider than it needs to be, per axis. Reset by any
        // grow or by the surplus falling back inside one quantum (see
        // WindowShrinkDelayFrames).
        private int windowSurplusWFrames;
        private int windowSurplusHFrames;

        // Temporary diagnostic (window trace setting): proves whether the bytes in the
        // density texture are the solver values for the window they claim to cover.
        private float traceRealtime;
        private float lastSignatureLog = float.NegativeInfinity;

        // Magnified copy of the gas field, one texel per WideDownsample^2 cells. Its world
        // coverage and origin match the density texture exactly, so the shader samples it
        // with the same uv — only the texel size differs.
        private Texture2D? gasWideA;
        private Texture2D? gasWideB;
        private Texture2D? gasWideCurrent;
        private Texture2D? gasWidePrevious;
        private Color[] gasWidePixels = Array.Empty<Color>();
        private int wideW;
        private int wideH;

        // Per-window copy of the flow history, sampled into this float texture. RGBA32F:
        //   rg = displayed displacement (cells)
        //   ba = smoothed velocity (cells per real second, already scaled by game speed)
        // Float, not 8-bit: a displacement that has to cover tens of cells would quantise
        // to a third of a cell per step at 8 bits, which reads as the pattern twitching on
        // every publish. RGBA32F is supported by the engine's texture creation path (the
        // enum maps straight to VkFormat R32G32B32A32_SFloat).
        private Texture2D? windTexture;
        private float[] windUpload = Array.Empty<float>();
        private float[] windSent = Array.Empty<float>();
        private int builtNoiseMode = -1;

        // Map-anchored flow history. D is integrated every publish; V is the temporally
        // smoothed velocity that the shader extrapolates between uploads.
        private float[] flowDx = Array.Empty<float>();
        private float[] flowDz = Array.Empty<float>();
        private float[] flowVx = Array.Empty<float>();
        private float[] flowVz = Array.Empty<float>();
        private int flowCellsX;
        private int flowCellsZ;
        private bool flowInitialized;
        private int lastFlowSteps = int.MinValue;
        private double lastFlowTime = double.NaN;

        // Last time the noise pattern's offset was baked. Shared by the ambient and the
        // local-flow mode (only one of them is active), because both hand the shader the
        // same "seconds elapsed since the last publish" so it can extrapolate in between.
        private double noisePublishTime;

        // Renderer-local game-time clock. RealTime.UnpausedRealTime already folds game
        // speed in, but it is a single float accumulated for the whole application
        // session: after a few hours its ulp is milliseconds, which quantises the
        // per-frame animation into visible micro-steps. This clock is a double and starts
        // at zero with the renderer, and the shader receives only small differences.
        private double shaderClock;
        private double lastClockReal;

        // Previous frame's clock advance, in game-seconds. Only used to tell a speed change
        // or a hitch apart from the ordinary advance (which is 0.25 s per frame at 15x).
        private double lastClockAdvance;

        // The advance of the frame *before* lastClockAdvance was overwritten. The
        // diagnostic needs it: `clock.lastClockAdvance` is put into the frame snapshot
        // after this frame's advance is already stored, so comparing a frame's delta
        // against it always compares a number with itself. One generation back is what
        // makes "this frame moved the clock 3x more than the last one" observable.
        private double previousClockAdvance;

        // The clamped game speed this frame's clock advance used. FlowStepLimit is derived
        // from it (real seconds). Read here rather than by calling CurrentVisualRate() from
        // the property, to keep the reflective TickRateMultiplier read at one per frame.
        private float visualRate = 1f;

        // Staging buffers for the flow texture. The velocity written to the shader is the
        // *realized* displacement of the last interval divided by the interval, not the
        // filtered wind velocity: in steady state they are the same, but the relaxation
        // step below also nudges cells by a fraction of a cell. Extrapolating only the
        // wind would leave that fraction to be applied as a jump at every publish, which
        // reads as a 10 Hz tremble.
        private float[] flowUx = Array.Empty<float>();
        private float[] flowUz = Array.Empty<float>();

        // Displacement before the current publish, used to derive the realized velocity.
        private float[] flowPrevDx = Array.Empty<float>();
        private float[] flowPrevDz = Array.Empty<float>();

        // Scratch for the in-place separable blur used by the deviation relaxation.
        private float[] flowBlurScratch = Array.Empty<float>();

        // Uniform part of the displacement: the integral of the map-wide mean wind. The
        // per-cell field holds the total, but the relaxation subtracts this base so only
        // the local deviation is bounded.
        private double baseDx;
        private double baseDz;

        // Which window the texture that `current` points at was baked from, and which one
        // `previous` holds. The cross-fade is only valid when the two agree; when they do
        // not it means a stale window is being mixed through the *new* uv mapping, which
        // shows up as the field jumping to a wrong place and drifting back over a refresh
        // interval. Tracked so that defect can announce itself instead of being guessed at.
        // Which baked window each ping-pong pair currently holds, and which one its
        // "previous" half holds. This MUST be per pair: the gas and the mist are baked and
        // uploaded independently, so one shared record is overwritten by whichever pair
        // uploads last. When that happened the shader mapped the previous gas texture with
        // the *new* window's offset, which reads as the whole field — halo included —
        // jumping by the window delta and drifting back over the cross-fade.
        private sealed class WindowTrack
        {
            public int MinX;
            public int MinZ;
            public int WinW;
            public int WinH;
            public int PrevMinX;
            public int PrevMinZ;
            public bool Valid;
        }

        private readonly WindowTrack gasTrack = new WindowTrack();
        private readonly WindowTrack mistTrack = new WindowTrack();
        private float lastStaleFadeLog = float.NegativeInfinity;
        private float lastWindowTrace = float.NegativeInfinity;

        private bool builtSmooth;

        private Mesh? mesh;
        private Material? material;

        private int quadMinX = int.MinValue;
        private int quadMinZ = int.MinValue;
        private int quadWidth = -1;
        private int quadHeight = -1;

        private float refreshPeriod = DefaultRefreshSeconds;

        // Two clocks, deliberately separate. `lastRefresh` timestamps the last bake of
        // *any* kind (including the ones a camera move forces) and only feeds the
        // smoothing delta in BakeSmooth. `lastTimedRefresh` is the 10 Hz solver cadence:
        // a window-move refresh must not touch it. When both used lastRefresh, a
        // continuous pan reset the timer every frame, timerDue never fired, and every
        // bake took the incremental path — which reuses the shifted pixels without
        // resampling the solver, so the gas appeared to freeze while the camera moved.
        private float lastRefresh = float.NegativeInfinity;
        private float lastTimedRefresh = float.NegativeInfinity;
        private float fadeStart = float.NegativeInfinity;

        // How long the blend from the previous snapshot to the current one takes, frozen at
        // the moment the blend restarts.
        //
        // It used to be recomputed from refreshPeriod on every frame, which is a function of
        // the publish clock only — and that is the problem. At 60 fps a 60 ms blend is four
        // drawn frames and reads as a dissolve; at 15 fps it is a single step, i.e. a hard
        // cut with extra bookkeeping. "Worse the lower the frame rate" is exactly this: the
        // blend exists, but no frame ever samples it in between. Widening it to at least a
        // couple of frames is what makes the same publish look like a blend at any frame
        // rate; freezing it at the restart keeps the value monotone, which a per-frame
        // recompute could not promise (a change mid-climb moves fade backwards, and a fade
        // that steps backwards is a visible flash of its own).
        private float fadeSpan = DefaultRefreshSeconds * FadeFraction;

        // Consecutive frames on which a timed publish was due and deliberately skipped
        // because the previous blend had not finished. Diagnostic: it is the direct measure
        // of "the publish rate is outrunning the frame rate".
        private int blendHeldFrames;

        private float NextFadeSpan()
        {
            float period = Mathf.Max(0.01f, refreshPeriod);
            float frame = Time.smoothDeltaTime;
            if (!(frame > 0.0001f) || frame > 0.5f)
            {
                frame = 0.016f;
            }

            // It used to be capped under one publish period, on the argument that a blend
            // outliving its own publish would mix two generations. The cap was the bug: at
            // 40 fps the period is two and a half frames, the blend needs 2.4, and so every
            // publish truncated the blend at ~0.55. A truncated blend is not a smaller
            // blend — it is a step, because the display jumps from 55% of the way to the
            // new picture back to the start of the next one. The publish now waits for the
            // blend instead (see Refresh), so the blend can be as long as the frame rate
            // needs; MaxFadeSpan only keeps a hitch from stretching it absurdly.
            return Mathf.Clamp(frame * FadeFrames, period * FadeFloorFraction, MaxFadeSpan);
        }

        private void RestartFade(float realtime)
        {
            fadeStart = realtime;
            fadeSpan = NextFadeSpan();
        }

        // Left as a property for the two call sites that only need the current width of
        // the blend; it now reports the frozen value.
        private float FadeDuration => fadeSpan;

        // Ambient drift history, in noise units. The offset is a path integral of the
        // wind, not wind * absolute time: the latter teleported the whole pattern
        // whenever the wind vector changed (weather transitions, AO's ambient wind),
        // because the jump was scaled by the time since the renderer started. The
        // velocity is smoothed like the local flow's, and the shader only extends the
        // last publish by the time elapsed since it, so a direction change turns into
        // a curve instead of a step.
        private const float AmbientDriftRate = 0.45f;
        private double ambientDx;
        private double ambientDz;
        private double ambientLastTime = double.NaN;
        private float ambientVelX;
        private float ambientVelZ;

        // The publish period only changes with AO's interval setting or the game speed,
        // but resolving it costs a reflective property read plus AO's own settings clamp.
        // Half a second of staleness is invisible (it only shifts the bake cadence and the
        // cross-fade duration), and the empty-field shortcut below then costs almost nothing.
        private const float PublishPeriodCacheSeconds = 0.5f;
        private float publishPeriodTime = float.NegativeInfinity;

        private bool loggedDraw;

        internal void Draw(
            Map map,
            CellRect view,
            float cellPixels,
            float realtime,
            bool paused)
        {
            GasFieldDiagnostics.BeginFrame(realtime);
            knownCleared = false;
            CheckUniformsUntouched();

            // The framebuffer probe reads the real frame back, once per frame, while the
            // diagnostics are on. Everything else here reconstructs the picture; this is the
            // only reader that cannot be fooled by the renderer's own bookkeeping.
            GasScreenProbe.Tick();
            GasFieldDiagnostics.Put("gate.active", GasFieldBridge.Active);
            GasFieldDiagnostics.Put("gate.useGasField", VisualTweaksSettings.useGasField);
            GasFieldDiagnostics.Put("gate.shaderReady", GasFieldBridge.ShaderReady);
            GasFieldDiagnostics.Put("gate.screenshot",
                Find.ScreenshotModeHandler != null && Find.ScreenshotModeHandler.Active);
            GasFieldDiagnostics.Put("gate.gasClean", gasClean);
            GasFieldDiagnostics.Put("gate.mistClean", mistClean);
            GasFieldDiagnostics.Put("gate.drawGas", DebugViewSettings.drawGas);
            GasFieldDiagnostics.Put("state.builtSmooth", builtSmooth);
            GasFieldDiagnostics.Put("state.builtNoiseMode", builtNoiseMode);
            GasFieldDiagnostics.Put("noise.supportsLocalWind", GasFieldBridge.SupportsLocalWind);
            GasFieldDiagnostics.Put("noise.animation", VisualTweaksSettings.gasFieldAnimation);
            GasFieldDiagnostics.Put("noise.flowCellsX", flowCellsX);
            GasFieldDiagnostics.Put("noise.flowCellsZ", flowCellsZ);
            GasFieldDiagnostics.Put("clock.refreshPeriod", refreshPeriod);
            GasFieldDiagnostics.Put("clock.flowStepLimit", FlowStepLimit);
            GasFieldDiagnostics.Put("clock.lastRefresh", lastRefresh);
            GasFieldDiagnostics.Put("clock.lastTimedRefresh", lastTimedRefresh);
            GasFieldDiagnostics.Put("fade.fadeStart", fadeStart);
            GasFieldDiagnostics.Put("uniform.opacity", VisualTweaksSettings.gasFieldOpacity);

            // Refresh the publish period on the settings / game-speed cadence, not per
            // frame: the reflective read plus AO's own interval clamp is measurable at idle.
            if (publishPeriodTime < 0f
                || realtime - publishPeriodTime >= PublishPeriodCacheSeconds)
            {
                publishPeriodTime = realtime;
                refreshPeriod = CurrentPublishPeriod();
            }

            AdvanceShaderClock(paused);
            traceRealtime = realtime;

            GasFieldDiagnostics.Put("clock.shaderClock", shaderClock);
            GasFieldDiagnostics.Put("clock.noisePublishTime", noisePublishTime);
            GasFieldDiagnostics.Put("clock.sincePublishRaw", shaderClock - noisePublishTime);
            GasFieldDiagnostics.Put("clock.lastClockAdvance", lastClockAdvance);
            GasFieldDiagnostics.Put("clock.prevClockAdvance", previousClockAdvance);
            GasFieldDiagnostics.Put("noise.ambientDx", ambientDx);
            GasFieldDiagnostics.Put("noise.ambientDz", ambientDz);
            GasFieldDiagnostics.Put("noise.ambientVelX", ambientVelX);
            GasFieldDiagnostics.Put("noise.ambientVelZ", ambientVelZ);
            GasFieldDiagnostics.Put("noise.baseDx", baseDx);
            GasFieldDiagnostics.Put("noise.baseDz", baseDz);

            // The smoothing toggle only takes effect on the next bake, so force one
            // when it changes (the options window can be open while the game is paused).
            bool smoothChanged = VisualTweaksSettings.gasFieldSmooth != builtSmooth;

            // Switching the noise mode changes what the fragment shader samples, and the
            // local mode needs a fresh flow bake. Force one publish when it changes.
            bool noiseModeChanged =
                GasFieldBridge.EffectiveNoiseMode != builtNoiseMode;

            // Cross-fade progress. Both channels empty and fully faded means every fragment
            // would be discarded, so there is nothing to bake, upload or draw. FadeDuration
            // is shorter than refreshPeriod on purpose; see the constant.
            float fade = paused || fadeStart < 0f
                ? 1f
                : Mathf.Clamp01((realtime - fadeStart) / FadeDuration);

            if (gasClean
                && mistClean
                && fade >= 1f
                && !smoothChanged
                && !noiseModeChanged
                && !paused
                && realtime - lastRefresh < refreshPeriod)
            {
                // Refresh still runs once per publish (the last condition), which is how a
                // map that gains gas or mist starts drawing again. In between, the field is
                // empty, so skip the window bookkeeping, the bake and the quad entirely.
                //
                // Paused is excluded: while paused the publish timer never fires, so a
                // camera move is the only thing that can notice newly spawned gas.
                if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("top empty-field gate: skip whole frame");
                GasFieldDiagnostics.EndFrame(false, "empty-gate-top");
                return;
            }

            int previousMinX = rectMinX;
            int previousMinZ = rectMinZ;
            int previousWinW = winW;
            int previousWinH = winH;

            bool rectMoved = EnsureResources(map, view, out bool originMoved);
            EnsureFlowGrid();

            GasFieldDiagnostics.Flag("rectMoved", rectMoved);
            GasFieldDiagnostics.Flag("originMoved", originMoved);
            GasFieldDiagnostics.Flag("forceUpload", forceUpload);
            GasFieldDiagnostics.Put("window.rectMinX", rectMinX);
            GasFieldDiagnostics.Put("window.rectMinZ", rectMinZ);
            GasFieldDiagnostics.Put("window.winW", winW);
            GasFieldDiagnostics.Put("window.winH", winH);
            GasFieldDiagnostics.Flag("fullMapWindow", winW == mapWidth && winH == mapHeight);
            GasFieldDiagnostics.Put("window.width", width);
            GasFieldDiagnostics.Put("window.height", height);
            GasFieldDiagnostics.Put("window.viewMinX", viewMinX);
            GasFieldDiagnostics.Put("window.viewMinZ", viewMinZ);
            GasFieldDiagnostics.Put("window.viewMaxX", viewMaxX);
            GasFieldDiagnostics.Put("window.viewMaxZ", viewMaxZ);
            GasFieldDiagnostics.Put("window.knownX0", knownX0);
            GasFieldDiagnostics.Put("window.knownX1", knownX1);
            GasFieldDiagnostics.Put("window.knownZ0", knownZ0);
            GasFieldDiagnostics.Put("window.knownZ1", knownZ1);
            GasFieldDiagnostics.Put("track.gasMinX", gasTrack.MinX);
            GasFieldDiagnostics.Put("track.gasMinZ", gasTrack.MinZ);
            GasFieldDiagnostics.Put("track.gasPrevMinX", gasTrack.PrevMinX);
            GasFieldDiagnostics.Put("track.gasPrevMinZ", gasTrack.PrevMinZ);
            GasFieldDiagnostics.Put("track.gasWinW", gasTrack.WinW);
            GasFieldDiagnostics.Put("track.gasWinH", gasTrack.WinH);
            GasFieldDiagnostics.Put("track.gasValid", gasTrack.Valid);
            GasFieldDiagnostics.Put("track.mistMinX", mistTrack.MinX);
            GasFieldDiagnostics.Put("track.mistMinZ", mistTrack.MinZ);
            GasFieldDiagnostics.Put("track.mistPrevMinX", mistTrack.PrevMinX);
            GasFieldDiagnostics.Put("track.mistPrevMinZ", mistTrack.PrevMinZ);
            GasFieldDiagnostics.Put("track.mistValid", mistTrack.Valid);
            GasFieldDiagnostics.Put("track.gasCoversWindow", CoversWindow(gasTrack));
            GasFieldDiagnostics.Put("track.mistCoversWindow", CoversWindow(mistTrack));
            GasFieldDiagnostics.Flag("coversWindow", CoversWindow(gasTrack) || CoversWindow(mistTrack));

            bool scrolled = false;

            // UNREACHABLE — keep the reasoning, it is the reason the window is pinned.
            //
            // `originMoved` is set only where the window origin changes, and the origin is
            // 0 for the lifetime of the map now. If this ever fires again it means the
            // window started moving again, and everything it does below becomes wrong:
            // the smoothing state is map-anchored (see BakeGasRow), so shifting it here
            // would corrupt it, and the staging pixels no longer need a scroll because the
            // texture no longer moves under them.
            if (originMoved)
            {
                // The smoothing state and the staging pixels are indexed by the cell's
                // offset from the window origin, so moving the origin shifts every entry.
                // Scrolling them costs one memmove per channel and keeps the history; the
                // strip the move exposed is left out of the known rect, so the next bake
                // seeds just that strip from the solver instead of the whole window.
                int shiftX = rectMinX - previousMinX;
                int shiftZ = rectMinZ - previousMinZ;
                if (previousWinW == winW
                    && previousWinH == winH
                    && shiftX > -winW && shiftX < winW
                    && shiftZ > -winH && shiftZ < winH)
                {
                    ScrollBuffers(shiftX, shiftZ);
                    scrolled = true;
                }
                else
                {
                    // Too far to scroll (teleport, zoom, first frame): drop the history.
                    ClearKnown();
                }
            }
            else if (previousWinW != winW || previousWinH != winH)
            {
                // Same origin, so the index of a given world cell did not move — only the
                // far edge of the known rect needs trimming.
                ClampKnown();
            }

            GasFieldDiagnostics.Flag("knownCleared", knownCleared);
            GasFieldDiagnostics.Flag("scrolled", scrolled);
            GasFieldDiagnostics.Put("window.scrolled", scrolled);
            GasFieldDiagnostics.Put("window.previousMinX", previousMinX);
            GasFieldDiagnostics.Put("window.previousMinZ", previousMinZ);
            GasFieldDiagnostics.Put("window.previousWinW", previousWinW);
            GasFieldDiagnostics.Put("window.previousWinH", previousWinH);

            if (noiseModeChanged)
            {
                // Whichever history was active may be minutes old; the next bake starts a
                // new integration window instead of replaying it. The ambient offset also
                // restarts at zero, like a renderer that was just created.
                lastFlowTime = double.NaN;
                ambientLastTime = double.NaN;
            }

            // The solver only moves while the game runs. A paused game still needs one
            // upload so a map that was just loaded (or a slider change) shows the field.
            // Deliberately independent of lastRefresh: a window-move refresh must not
            // postpone the next timed publish, or a continuous pan starves the 10 Hz
            // resample and the gas stops advancing on screen. The clock advances only on
            // a due publish, so pausing still freezes the cadence with the game.
            bool timerDue = !paused && realtime - lastTimedRefresh >= refreshPeriod;

            // A publish does not *replace* a running blend, it truncates it — and the
            // truncation is a step of (1 - fade) x delta inside one frame. At the gas's
            // edge, where the density is ~0.004 and the whole visible change is a few
            // percent of alpha, that step is the artefact: the contour moves out, then
            // moves back when the next blend starts. Measured at 40 fps: the blend peaked
            // at 0.42..0.60 and was cut every publish, so 40..58% of every publish's change
            // arrived as a one-frame step. At 60 fps the same publish was sampled twice in
            // between and read as a blend, which is why the defect tracks the frame rate.
            //
            // Waiting costs nothing: the solver's gas diffuses over seconds, so the field
            // only has to be re-read as often as it can be shown smoothly. With this gate
            // the effective period stretches to the blend length when the frame rate cannot
            // fit both, instead of the blend being cut short.
            if (timerDue && !paused && fadeStart > 0f
                && realtime - fadeStart < FadeDuration)
            {
                timerDue = false;
                blendHeldFrames++;
            }

            if (timerDue)
            {
                lastTimedRefresh = realtime;
                blendHeldFrames = 0;
            }

            GasFieldDiagnostics.Put("clock.blendHeld", blendHeldFrames);
            GasFieldDiagnostics.Put("clock.fadeSpan", FadeDuration);

            GasFieldDiagnostics.Flag("timerDue", timerDue);
            GasFieldDiagnostics.Flag("smoothChanged", smoothChanged);
            GasFieldDiagnostics.Flag("noiseModeChanged", noiseModeChanged);
            GasFieldDiagnostics.Put("clock.timerDue", timerDue);
            GasFieldDiagnostics.Put("clock.realtimeSinceLastRefresh", realtime - lastRefresh);
            GasFieldDiagnostics.Put("clock.realtimeSinceLastTimed", realtime - lastTimedRefresh);
            GasFieldDiagnostics.Put("fade.fadeStart", fadeStart);

            if (gasCurrent == null || rectMoved || smoothChanged || noiseModeChanged || timerDue)
            {
                // Any window change makes the bound texture describe the wrong rectangle, so
                // the upload is not optional — do not rely on the per-channel "unchanged"
                // shortcut to notice.
                if (rectMoved)
                {
                    forceUpload = true;
                }

                // Only a publish that actually scrolled the buffers may skip cells. That is
                // the one case where the staging pixels provably hold this window: the
                // scroll copies them out of the buffer the last upload left valid. Every
                // other publish leaves `pixels` as the *rotated* buffer — UploadTexture
                // swaps the pair, so it holds what was uploaded two publishes ago, at some
                // older window. Skipping cells then uploads that stale picture as the
                // current window: the whole field lands where it used to be, a whole number
                // of window steps away, which is exactly the "jumps to a random nearby place
                // with no gas" report. A size change without an origin move (zoom crossing a
                // bucket, view clipped against the map border) used to take that path.
                // A timed publish always resamples the whole window, even when the same
                // frame also moved it. `incremental` is only for a movement-only bake in
                // between two timed publishes.
                bool incremental = scrolled && !timerDue && !smoothChanged;

                GasFieldDiagnostics.Flag("incremental", incremental);
                GasFieldDiagnostics.Flag("refreshed", true);
                if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("Refresh enter: rect=(" + rectMinX + "," + rectMinZ
                    + ") " + winW + "x" + winH
                    + " timerDue=" + timerDue + " incremental=" + incremental
                    + " rectMoved=" + rectMoved + " forceUpload=" + forceUpload);

                // Restart on a window move as well as on a timed publish.
                //
                // The pair this upload just replaced holds the picture that was on screen
                // one frame ago, and _Params7 can sample it at its own origin, so fade = 0
                // shows exactly what the previous frame showed and the following frames
                // walk forward from there. Not restarting left fade pinned at 1, which
                // showed the freshly baked texture immediately — a hard cut on precisely
                // the frames whose content had moved the most.
                //
                // This used to be `timerDue && !rectMoved` because the Draw layer forced
                // fade back to 1 on every moved frame; the two cancelled into a 0 -> 1 step
                // within one frame, which is the flash. With that guard gone (the shader can
                // now blend across origins), a restart is safe and is what makes panning
                // and zooming smooth.
                // A scroll-only window move does *not* restart the blend.
                //
                // On the incremental path the staging pixels were copied out of the buffer
                // that is still on screen (ScrollBuffers, from `sent`) and only the strip
                // outside the view was re-baked, so the new texture is the same picture at a
                // new origin. There is nothing to fade: at fade 1 the display is already
                // that picture, and at fade 0.5 both halves are it too. Restarting anyway
                // costs a half-step of density for no reason.
                //
                // It is not a rare path. Measured on a 90 s capture at 20 Hz publishing and
                // 37 fps: two publishes every 0.05 s, so the blend gets exactly one frame
                // and tops out around 0.55 (0 -> 0.59 -> 0); and while panning, the window
                // recentres every 2..3 frames, each recentre firing a second restart. The
                // field was never once shown settled, and the per-frame step measured
                // 0.0112 mean against a local density of 0.016 — a ~70% lurch, larger the
                // lower the frame rate. Removing the restart from the scroll path takes
                // out the half that changes nothing.
                bool restartFade = timerDue || (rectMoved && !incremental);
                GasFieldDiagnostics.Put("refresh.restartFade", restartFade);
                if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("fade restart: timerDue=" + timerDue
                    + " rectMoved=" + rectMoved + " incremental=" + incremental
                    + " -> restartFade=" + restartFade);

                Refresh(map, realtime, incremental, restartFade);
            }

            if (rectMoved)
            {
                // No hard cut any more: the previous texture is sampled through its own
                // mapping (_Params7), so a moved window cross-fades correctly instead of
                // smearing the old region into the new one.
                if (VisualTweaksLog.GasTraceEnabled
                    && realtime - lastWindowTrace > 0.15f)
                {
                    lastWindowTrace = realtime;
                    VisualTweaksLog.GasTrace(
                        "window "
                        + $"({rectMinX},{rectMinZ}) {winW}x{winH} of cap {width}x{height}, "
                        + $"view ({view.minX},{view.minZ})-({view.maxX},{view.maxZ})");
                }
            }

            // Refresh may just have cleared a channel and restarted the fade-out, so the
            // value computed above is stale. Recompute before deciding whether to draw.
            fade = paused || fadeStart < 0f
                ? 1f
                : Mathf.Clamp01((realtime - fadeStart) / FadeDuration);

            // No hard cut at this layer any more.
            //
            // UploadTexture already decides, per publish, whether the old texture answers
            // for the current view (PrevCoversView) and cuts the pair when it does not.
            // When it does, _Params7 lets the shader sample that texture at its own origin,
            // so a window that moved blends like any other publish.
            //
            // Forcing fade to 1 on every moved frame — what this used to do — is why
            // panning and zooming flashed at all. A moving camera changes the window on
            // almost every frame, so the cross-fade was overridden on exactly the frames
            // whose content differed most, and the size of the step scales with the frame
            // interval: the lower the frame rate, the larger the jump. That is the
            // "frequent when the frame rate is low, rare when it is high" report, measured.
            float fadeBeforeGuard = fade;
            GasFieldDiagnostics.Put("fade.fadeBeforeGuard", fadeBeforeGuard);
            GasFieldDiagnostics.Put("fade.fadeAfterGuard", fade);

            // The fade is never gated on the frame rate. It was tried and reverted: it made
            // what is on screen depend on a measured frame time, and refreshPeriod itself is
            // cached for half a second, so switching the game speed moved a 60 fps setup
            // across the threshold half a second *after* the switch and cut the blend in one
            // step — a flash of the whole field, on every speed change. Whatever the fade
            // does, it must be a function of the publish clock alone.

            GasFieldDiagnostics.Put("fade.fade", fade);
            GasFieldDiagnostics.Put("gate.gasClean", gasClean);
            GasFieldDiagnostics.Put("gate.mistClean", mistClean);

            if (gasClean && mistClean && fade >= 1f)
            {
                // Both channels are empty and the fade-out is over: skip the mesh update,
                // the uniform block and Graphics.DrawMesh. The first bake with content
                // brings the full path back.
                if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("post-refresh empty gate: skip whole frame");
                GasFieldDiagnostics.EndFrame(false, "empty-gate-after-refresh");
                return;
            }

            int quadBeforeX = quadMinX;
            int quadBeforeZ = quadMinZ;
            int quadBeforeW = quadWidth;
            EnsureQuad(mesh);
            GasFieldDiagnostics.Flag("quadRebuilt",
                quadBeforeX != quadMinX || quadBeforeZ != quadMinZ || quadBeforeW != quadWidth);
            GasFieldDiagnostics.Put("window.quadMinX", quadMinX);
            GasFieldDiagnostics.Put("window.quadMinZ", quadMinZ);

            // Where the quad actually lands on screen, in pixels.
            //
            // Every other probe in this file reads the CPU-side picture and the uniforms, and
            // every one of them came back clean across four logs: the bake matches the
            // solver, the uv matches the window, the previous pair's claim matches what it
            // holds, and the noise offset is monotone. If the field still moves on screen
            // with all of that true, the difference is in the projection — the mesh, the
            // camera, or the transform — and that is the one thing none of them look at.
            // Two WorldToScreenPoint calls a frame settle it: a quad that jumps for one
            // frame shows up here as a pixel offset, and a quad that does not cannot be
            // responsible for what the eye reports.
            RecordQuadProjection();

            if (width <= 0 || height <= 0)
            {
                GasFieldDiagnostics.EndFrame(false, "invalid-size");
                return;
            }

            // Print the state the frame is actually drawn from, once per camera move: which
            // window each pair claims to hold, and the fade it will be drawn with.
            if (VisualTweaksLog.GasTraceEnabled && rectMoved)
            {
                VisualTweaksLog.GasTrace(
                    "frame"
                    + $" period={refreshPeriod:F3} fade={fade:F2} timerDue={timerDue}"
                    + $" | window ({rectMinX},{rectMinZ}) {winW}x{winH} cap {width}x{height}"
                    + $" | gas ({gasTrack.MinX},{gasTrack.MinZ}) {gasTrack.WinW}x{gasTrack.WinH}"
                    + $" prev ({gasTrack.PrevMinX},{gasTrack.PrevMinZ})"
                    + $" | mist prev ({mistTrack.PrevMinX},{mistTrack.PrevMinZ})");
            }

            // Cross-fade per frame: without it the 10 Hz publish would hold the same
            // image for several frames even when the values are smoothed. The branch below
            // is the guard that it is at least *possible*, i.e. that each live texture still
            // answers for the window it is being drawn with.
            //
            // The previous texture keeps its own mapping, and the ring outside its baked
            // band is cleared to zero on upload, so a cell it never covered fades in from
            // nothing. Its uv still has to be masked to [0,1] in the shader: Clamp would
            // otherwise repeat the window's own edge texels for everything outside it. A
            // window move whose previous window does not contain the view skips the fade
            // entirely (see UploadTexture), which is the case a long first frame hits.
            if ((gasTrack.Valid || mistTrack.Valid) && realtime - lastStaleFadeLog > 1f)
            {
                // The live texture must cover the window being drawn; `previous` is allowed
                // to describe an older window because it carries its own mapping. A channel
                // the content gate has emptied is exempt: its texture is uniformly zero, so
                // there is nothing for a stale mapping to misplace, and it deliberately
                // skips the upload that would have refreshed the record.
                bool gasStale = !gasClean && !CoversWindow(gasTrack);
                bool mistStale = !mistClean && !CoversWindow(mistTrack);
                if (gasStale || mistStale)
                {
                    lastStaleFadeLog = realtime;
                    Log.Warning(
                        "[Visual Tweaks] Texture/window mismatch"
                        + $" (gas={gasStale} mist={mistStale}, fade={fade:F2}):"
                        + $" gas ({gasTrack.MinX},{gasTrack.MinZ}) {gasTrack.WinW}x{gasTrack.WinH},"
                        + $" mist ({mistTrack.MinX},{mistTrack.MinZ}) {mistTrack.WinW}x{mistTrack.WinH},"
                        + $" window ({rectMinX},{rectMinZ}) {winW}x{winH}.");
                }
            }

            float opacity = Mathf.Clamp(VisualTweaksSettings.gasFieldOpacity, 0.2f, 3f);
            float scale = Mathf.Clamp(VisualTweaksSettings.gasFieldScale, 0.5f, 2f);
            float detail = Mathf.Clamp01(
                (cellPixels - DetailFadeStartPixels) / DetailFadeRangePixels);
            liveDetail = detail;
            liveCellPixels = cellPixels;

            // The shader's time is the renderer-local clock, not RealTime.UnpausedRealTime:
            // the latter is one float for the whole application session, and after hours
            // its ulp is large enough to quantise this animation into visible micro-steps.
            float shaderTime = (float)shaderClock;
            GasFieldDiagnostics.Put("uniform.opacity", opacity);
            GasFieldDiagnostics.Put("uniform.scale", scale);
            GasFieldDiagnostics.Put("uniform.detail", detail);
            GasFieldDiagnostics.Put("uniform.shaderTime", shaderTime);
            GasFieldDiagnostics.Put("uniform.params2Width", width);
            GasFieldDiagnostics.Put("uniform.params2Height", height);
            GasFieldDiagnostics.Put("uniform.params6OriginX", rectMinX);
            GasFieldDiagnostics.Put("uniform.params6OriginZ", rectMinZ);
            GasFieldDiagnostics.Put("uniform.params7OriginX",
                gasTrack.Valid ? gasTrack.PrevMinX : rectMinX);
            GasFieldDiagnostics.Put("uniform.params7OriginZ",
                gasTrack.Valid ? gasTrack.PrevMinZ : rectMinZ);
            GasFieldDiagnostics.Put("uniform.params8OriginX",
                mistTrack.Valid ? mistTrack.PrevMinX : rectMinX);
            GasFieldDiagnostics.Put("uniform.params8OriginZ",
                mistTrack.Valid ? mistTrack.PrevMinZ : rectMinZ);
            GasFieldDiagnostics.Put("uniform.effectiveNoiseMode", GasFieldBridge.EffectiveNoiseMode);
            GasFieldDiagnostics.Put("uniform.replaceDust",
                VisualTweaksSettings.gasFieldReplaceDeadlife);
            material.SetVector(ParamsId, new Vector4(fade, 2.6f, opacity, shaderTime));
            setParams = new Vector4(fade, 2.6f, opacity, shaderTime);
            material.SetVector(Params2Id, new Vector4(width, height, ambientVelX, ambientVelZ));
            // vWorld -> uv for the baked window: uv = vWorld * xy + zw. The shader used
            // to divide by the map size; now the texture only covers the window.
            //
            // vWorld -> uv for the baked window: uv = vWorld * xy + zw.
            Vector4 mapCur = WindowMap(rectMinX, rectMinZ);
            material.SetVector(Params6Id, mapCur);
            setParams6 = mapCur;
            // Each pair's previous half comes from an older window, so it needs the mapping
            // of the window *it* holds. Only the strip that left or entered at the window
            // edge is affected, and that strip sits in the margin outside the view — the
            // cross-fade's job is to smooth the 10 Hz step, which it can only do if the
            // publish that moved the window blends like any other.
            Vector4 mapPrev = WindowMap(
                gasTrack.Valid ? gasTrack.PrevMinX : rectMinX,
                gasTrack.Valid ? gasTrack.PrevMinZ : rectMinZ);
            material.SetVector(Params7Id, mapPrev);
            setParams7 = mapPrev;
            haveSetUniforms = true;
            material.SetVector(
                Params8Id,
                WindowMap(
                    mistTrack.Valid ? mistTrack.PrevMinX : rectMinX,
                    mistTrack.Valid ? mistTrack.PrevMinZ : rectMinZ));
            // Noise motion mode: x = 0 ambient / 1 local flow, y = animation strength,
            // z = time already elapsed since the last noise bake (the shader extends the
            // published velocity by this much), w = draw the deadlife dust channel.
            double sincePublish = shaderClock - noisePublishTime;
            if (!(sincePublish > 0.0))
            {
                sincePublish = 0.0;
            }
            else if (sincePublish > FlowStepLimit)
            {
                sincePublish = FlowStepLimit;
            }

            GasFieldDiagnostics.Put("clock.sincePublish", sincePublish);
            GasFieldDiagnostics.Put("clock.sincePublishSaturated",
                shaderClock - noisePublishTime >= FlowStepLimit);

            // The exact quantity the shader feeds into `off0`: the accumulated drift plus
            // the extrapolation since the last bake. It must be monotone — the shader has
            // no other state — so a frame where it goes backwards is a one-frame jump of
            // the whole *pattern* with no density change behind it. Reported as two
            // scalars here and checked frame to frame by the diagnostics.
            GasFieldDiagnostics.Put("noise.offsetX", ambientDx + ambientVelX * (float)sincePublish);
            GasFieldDiagnostics.Put("noise.offsetZ", ambientDz + ambientVelZ * (float)sincePublish);
            liveOffX = (float)(ambientDx + ambientVelX * sincePublish);
            liveOffZ = (float)(ambientDz + ambientVelZ * sincePublish);
            liveFade = fade;
            material.SetVector(
                Params9Id,
                new Vector4(
                    GasFieldBridge.EffectiveNoiseMode,
                    VisualTweaksSettings.gasFieldAnimation,
                    (float)sincePublish,
                    VisualTweaksSettings.gasFieldReplaceDeadlife ? 1f : 0f));
            // The flow texture spans the whole map at FlowCellSize cells per texel, so its
            // vWorld -> uv mapping is fixed: neither panning nor zooming changes it.
            material.SetVector(
                Params10Id,
                new Vector4(
                    1f / (flowCellsX * FlowCellSize),
                    1f / (flowCellsZ * FlowCellSize),
                    0f,
                    0f));
            // Ambient drift: xy = accumulated displacement (noise units). Local flow has
            // its own map-anchored displacement texture, so it leaves this at zero.
            material.SetVector(
                Params11Id,
                new Vector4((float)ambientDx, (float)ambientDz, 0f, 0f));
            // Higher slider = larger clumps, so the noise frequency goes down. w is the
            // old ambient scroll rate; the ambient offset is integrated on the CPU now
            // (see BakeAmbient), so it stays zero.
            material.SetVector(
                Params3Id,
                new Vector4(
                    1.1f / scale,
                    0.35f,
                    detail,
                    0f));
            material.SetVector(
                Params4Id,
                new Vector4(
                    VisualTweaksSettings.gasFieldBlur,
                    VisualTweaksSettings.gasFieldFaint,
                    VisualTweaksSettings.gasFieldCloudy ? 1f : 0f,
                    VisualTweaksSettings.gasFieldSharpen));

            Texture2D gasCur = gasCurrent != null ? gasCurrent : gasSmoothA;
            Texture2D gasPrev = gasPrevious != null ? gasPrevious : gasCur;
            Texture2D mistCur = mistCurrent != null ? mistCurrent : mistSmoothA;
            Texture2D mistPrev = mistPrevious != null ? mistPrevious : mistCur;
            Texture2D wideCur = gasWideCurrent != null ? gasWideCurrent : gasWideA;
            Texture2D widePrev = gasWidePrevious != null ? gasWidePrevious : wideCur;

            material.SetTexture(WindTexId, windTexture);

            material.SetTexture(GasTexCurId, gasCur);
            material.SetTexture(GasTexPrevId, gasPrev);
            material.SetTexture(MistTexCurId, mistCur);
            material.SetTexture(MistTexPrevId, mistPrev);
            material.SetTexture(GasTexWideCurId, wideCur);
            material.SetTexture(GasTexWidePrevId, widePrev);

            GasFieldDiagnostics.Put("tex.gasCurrent", gasCur?.name ?? "null");
            GasFieldDiagnostics.Put("tex.gasPrevious", gasPrev?.name ?? "null");
            GasFieldDiagnostics.Put("tex.mistCurrent", mistCur?.name ?? "null");
            GasFieldDiagnostics.Put("tex.mistPrevious", mistPrev?.name ?? "null");
            GasFieldDiagnostics.Put("tex.gasWideCurrent", wideCur?.name ?? "null");
            GasFieldDiagnostics.Put("tex.gasWidePrevious", widePrev?.name ?? "null");
            GasFieldDiagnostics.Put("tex.wind", windTexture?.name ?? "null");
            GasFieldDiagnostics.Put("tex.gasPrevEqualsCur", ReferenceEquals(gasCur, gasPrev));
            GasFieldDiagnostics.Put("tex.mistPrevEqualsCur", ReferenceEquals(mistCur, mistPrev));
            GasFieldDiagnostics.Put("tex.widePrevEqualsCur", ReferenceEquals(wideCur, widePrev));
            GasFieldDiagnostics.Put("tex.material", material?.name ?? "null");
            GasFieldDiagnostics.Put("tex.mesh", mesh?.name ?? "null");

            // Invariant, checked on the state the frame is actually drawn from: the texture
            // bound as current must be the one _Params6 describes. Any disagreement is a
            // whole-field displacement by exactly the difference — which is what "it jumps
            // to another place" looks like, and nothing else in the snapshot would say so.
            bool uvStale = gasTrack.Valid
                && (gasTrack.MinX != rectMinX
                    || gasTrack.MinZ != rectMinZ
                    || gasTrack.WinW < winW
                    || gasTrack.WinH < winH);
            GasFieldDiagnostics.Flag("uvStale", uvStale);

            // The block grid's alignment, in cells. Must be 0 on both axes: any other value
            // means the 8x8 wide field is grouped on cells that will be regrouped the next
            // time the window recentres — the halo then steps by that offset and steps back.
            GasFieldDiagnostics.Put("wide.offsetX", rectMinX % WideDownsample);
            GasFieldDiagnostics.Put("wide.offsetZ", rectMinZ % WideDownsample);
            GasFieldDiagnostics.Flag("wideMisaligned",
                rectMinX % WideDownsample != 0 || rectMinZ % WideDownsample != 0);
            GasFieldDiagnostics.Put("geom.uvOffsetX", gasTrack.Valid ? rectMinX - gasTrack.MinX : 0);
            GasFieldDiagnostics.Put("geom.uvOffsetZ", gasTrack.Valid ? rectMinZ - gasTrack.MinZ : 0);

            // And the same check for the previous half: compare the window _Params7 claims
            // against the window that texture was really uploaded with. A nonzero shift here
            // means the cross-fade is drawing the old picture at the wrong origin — a
            // whole-field displacement for exactly as long as the wrong claim survives.
            // The same check for the current half, against what that texture was really baked
            // from rather than against what TrackWindows claims. Strictly stronger than
            // uvStale, which can only compare a claim with itself.
            if (gasCurrent != null)
            {
                TexWindowRecord heldCur = ReferenceEquals(gasCurrent, gasSmoothA) ? gasWindowA : gasWindowB;
                if (ReferenceEquals(heldCur.Tex, gasCurrent))
                {
                    int curShiftX = rectMinX - heldCur.X;
                    int curShiftZ = rectMinZ - heldCur.Z;
                    GasFieldDiagnostics.Put("geom.curShiftX", curShiftX);
                    GasFieldDiagnostics.Put("geom.curShiftZ", curShiftZ);
                    GasFieldDiagnostics.Flag("curWindowMismatch", curShiftX != 0 || curShiftZ != 0);
                }
            }

            if (gasPrevious != null && gasTrack.Valid)
            {
                TexWindowRecord held = ReferenceEquals(gasPrevious, gasSmoothA) ? gasWindowA : gasWindowB;
                if (ReferenceEquals(held.Tex, gasPrevious))
                {
                    int shiftX = gasTrack.PrevMinX - held.X;
                    int shiftZ = gasTrack.PrevMinZ - held.Z;
                    GasFieldDiagnostics.Put("geom.prevShiftX", shiftX);
                    GasFieldDiagnostics.Put("geom.prevShiftZ", shiftZ);
                    GasFieldDiagnostics.Flag("prevWindowMismatch", shiftX != 0 || shiftZ != 0);
                }
            }

            // Measure the composite the shader is about to draw, *before* drawing it: the
            // jump this reports is the step the eye is about to see, not a reconstruction
            // from uniforms after the fact.
            SampleComposite(realtime, fade, sincePublish);

            Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0);

            GasFieldDiagnostics.EndFrame(true);

            VisualTweaksLog.TraceOnce(
                ref loggedDraw,
                "Gas field renderer drawing " + width + "x" + height + " density field.");
        }

        /// <summary>
        /// Separates the two sizes: the texture capacity (grown rarely, geometrically) and
        /// the baked window (recomputed every frame, free). Returns true when the baked
        /// window changed, which means the texture contents no longer match the camera.
        /// </summary>
        /// <summary>
        /// One publish of AO's solver, in real seconds. SimulationIntervalTicks is a tick
        /// count (default 6, configurable 3..30) and ticks run TickRateMultiplier times
        /// faster at 2x/3x, so both have to be folded in. Clamped at both ends: a paused
        /// or absent tick manager would otherwise divide by zero, and at high game speed
        /// the solver can publish faster than a frame.
        /// </summary>
        private static float CurrentPublishPeriod()
        {
            int ticks = GasFieldBridge.SimulationIntervalTicks;
            float rate = 1f;

            TickManager tickManager = Find.TickManager;
            if (tickManager != null)
            {
                float multiplier = tickManager.TickRateMultiplier;
                if (multiplier > 0.01f)
                {
                    rate = multiplier;
                }
            }

            return Mathf.Clamp(ticks / (60f * rate), MinPublishPeriod, MaxPublishPeriod);
        }

        /// <summary>
        /// How many game-seconds one real second contains. The shader clock is advanced by
        /// this factor, because AO's advection runs faster in real time at 3x and the noise
        /// must stay in step with the density it is drawn on.
        /// </summary>
        private static float CurrentSimulationRate()
        {
            TickManager tickManager = Find.TickManager;
            if (tickManager == null)
            {
                return 1f;
            }

            float multiplier = tickManager.TickRateMultiplier;
            return multiplier > 0.01f ? multiplier : 1f;
        }

        /// <summary>
        /// The game speed the *visuals* are allowed to run at. Same as the simulation rate
        /// below MaxVisualRate, and clamped above it — see the constant for why an
        /// unclamped multiplier destroys the field.
        /// </summary>
        private static float CurrentVisualRate()
        {
            return Mathf.Min(CurrentSimulationRate(), MaxVisualRate);
        }

        /// <summary>
        /// Most time one publish may integrate and the shader may extrapolate. It tracks
        /// the publish period because AO allows a 30-tick interval (0.5 s at 1x); a fixed
        /// 0.25 s bound would park the pattern halfway between those publishes. The 1.5
        /// factor absorbs frame jitter.
        ///
        /// The period is in *real* seconds, so it has to be scaled by the visual rate to
        /// stay in the same units as the shader clock. Without that factor the limit is
        /// correct at 1x and wrong by the whole speed multiplier above it.
        /// </summary>
        private float FlowStepLimit =>
            Mathf.Min(1f, Mathf.Max(FlowMinStep, refreshPeriod * 1.5f * visualRate));

        /// <summary>
        /// Advances the renderer-local game-time clock by one frame. Double precision and
        /// a zero origin keep the per-frame delta exact for the whole session, and the
        /// clock freezes with the game (and during a hitch) exactly like the solver does.
        /// </summary>
        private void AdvanceShaderClock(bool paused)
        {
            double now = Time.realtimeSinceStartupAsDouble;

            // Clamped here, not in CurrentSimulationRate: the constant applies to what the
            // field *shows*, and the solver's own cadence is AO's business. Hold the value
            // even while paused so FlowStepLimit keeps describing the current setting.
            visualRate = CurrentVisualRate();

            if (lastClockReal > 0.0 && !paused)
            {
                double delta = now - lastClockReal;
                if (delta > 0.0)
                {
                    // A hitch must not teleport the pattern; the solver did not run for
                    // the missing time either (its own interval is capped).
                    if (delta > 0.25)
                    {
                        delta = 0.25;
                    }

                    double advance = delta * visualRate;

                    // Only a *jump* is interesting. At 15x a perfectly normal frame advances
                    // 0.25 s of game time, so an absolute threshold would log every frame and
                    // turn the diagnostic into the problem; two and a half times the previous
                    // frame's advance is what a speed change or a hitch looks like.
                    if (VisualTweaksLog.GasTraceEnabled
                        && advance > 0.1
                        && advance > lastClockAdvance * 2.5)
                    {
                        // Switching the game speed charges the *whole* elapsed interval at
                        // the new rate: 1x -> 15x adds 14 x delta of game time that never
                        // passed, which the flow integration turns into a couple of cells of
                        // noise displacement — one visible shimmer of the pattern. Traced
                        // because it is the other candidate for "the field flashes when the
                        // speed changes", and it only fires on a hitch or a speed change.
                        VisualTweaksLog.GasTrace(
                            "clock jump"
                            + $" delta={delta:F4}s rate={visualRate:F1} advance={advance:F3}s"
                            + $" previous={lastClockAdvance:F3}s clock={shaderClock:F2}");
                    }

                    previousClockAdvance = lastClockAdvance;
                    lastClockAdvance = advance;
                    shaderClock += advance;
                }
            }

            lastClockReal = now;
        }

        [MemberNotNull(
            nameof(gasSmoothA),
            nameof(gasSmoothB),
            nameof(mistSmoothA),
            nameof(mistSmoothB),
            nameof(gasWideA),
            nameof(gasWideB),
            nameof(mesh),
            nameof(material))]
        private bool EnsureResources(Map map, CellRect view, out bool originMoved)
        {
            mapWidth = map.Size.x;
            mapHeight = map.Size.z;

            // CurrentViewRect is not clipped to the map: near an edge it happily reports
            // cells that do not exist (log: view maxZ 255 on a 250-tall map). No window can
            // cover that, so the slack test would fail every single frame, the window would
            // hug the border, and part of the *visible* map would get no gas at all. Clip
            // first and everything downstream reasons about real cells only.
            int clipX = Mathf.Clamp(view.minX, 0, Mathf.Max(0, mapWidth - 1));
            int clipZ = Mathf.Clamp(view.minZ, 0, Mathf.Max(0, mapHeight - 1));
            int clipW = Mathf.Clamp(view.maxX - clipX + 1, 1, Mathf.Max(1, mapWidth - clipX));
            int clipH = Mathf.Clamp(view.maxZ - clipZ + 1, 1, Mathf.Max(1, mapHeight - clipZ));
            view = new CellRect(clipX, clipZ, clipW, clipH);

            viewMinX = view.minX;
            viewMinZ = view.minZ;
            viewMaxX = view.maxX;
            viewMaxZ = view.maxZ;

            int viewW = Mathf.Max(1, view.Width);
            int viewH = Mathf.Max(1, view.Height);

            // --- capacity: the whole map ------------------------------------------
            //
            // The texture used to be a window that followed the camera: view + margin,
            // reallocated when the zoom outgrew it, recentred in 8-cell steps, scrolled
            // when it moved, and cross-faded between two windows that could disagree.
            // Every single artifact this investigation measured lived on that path — the
            // worst frames all carried `rectMoved=1` with `cur` and `prev` of different
            // sizes, and none of them appeared while the window was still.
            //
            // A whole-map texture is the same size as the window was (the bake already
            // uploaded the full capacity every publish, and the window was view + 34 cells
            // on a map that is 250 wide), and it makes the mapping constant for the
            // lifetime of the map: uv = vWorld / mapSize, no origin, no resize on zoom, no
            // scrolling, and _Params6 == _Params7 always, so the cross-fade can no longer
            // mix two different mappings at all.
            int needW = mapWidth;
            int needH = mapHeight;

            bool rectMoved = false;
            originMoved = false;
            bool resize = gasSmoothA == null;

            if (!resize && (needW > width || needH > height))
            {
                // Grow geometrically: a zoom-out gesture sweeps tens of cells, and one
                // allocation per gesture beats one per bucket crossed.
                needW = Mathf.Min(mapWidth, Mathf.Max(needW, width * 2));
                needH = Mathf.Min(mapHeight, Mathf.Max(needH, height * 2));
                resize = true;
                capacitySurplusFrames = 0;
            }
            else if (!resize)
            {
                // Shrink only from a *sustained* 2x surplus. Zooming out once and back in
                // leaves the capacity at twice what it needs, which doubles the bytes every
                // upload has to copy (SetPixels32 always takes the whole texture); but the
                // reallocation itself is a GPU image rebuild plus a fence wait, so it must
                // be much rarer than the zoom it cleans up after.
                if (needW * 2 <= width && needH * 2 <= height)
                {
                    if (++capacitySurplusFrames >= ShrinkDelayFrames)
                    {
                        resize = true;
                        capacitySurplusFrames = 0;
                    }
                }
                else
                {
                    capacitySurplusFrames = 0;
                }
            }

            if (resize)
            {
                GasFieldDiagnostics.Flag("resize", true);
                GasFieldDiagnostics.Flag("textureResize", true);
                if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("EnsureResources RESIZE: " + width + "x" + height
                    + " -> " + needW + "x" + needH);
                DisposeTextures();

                width = needW;
                height = needH;
                int cellCount = width * height;

                gasSmoothPixels = new Color32[cellCount];
                mistSmoothPixels = new Color32[cellCount];
                gasSmoothSent = new Color32[cellCount];
                mistSmoothSent = new Color32[cellCount];

                int mapCells = mapWidth * mapHeight;
                if (mistScratch.Length != mapCells)
                {
                    mistScratch = new float[mapCells];
                }
                // Map-sized, not capacity-sized: see BakeGasRow. Reallocated only when the
                // map itself changes, and every entry starts as NaN ("no history").
                // Reallocate when the map size changes, and *also* whenever the smoothing
                // was already invalidated (`DisposeTextures` clears that flag). The length
                // test alone misses a map change onto a map of the same size — the array is
                // then still the right size but full of the previous map's history, which
                // shows up as gas that is not there for the first few publishes. Resetting
                // costs one pass over the array, and with the window pinned to the map this
                // branch only runs on a map change.
                int smoothCells = mapWidth * mapHeight;
                if (gasSmoothState.Length != smoothCells * 4 || !smoothInitialized)
                {
                    if (gasSmoothState.Length != smoothCells * 4)
                    {
                        gasSmoothState = new float[smoothCells * 4];
                    }

                    for (int i = 0; i < gasSmoothState.Length; i++)
                    {
                        gasSmoothState[i] = float.NaN;
                    }
                }
                mistSmoothState = new float[cellCount * 2];

                wideW = Mathf.Max(1, width / WideDownsample);
                wideH = Mathf.Max(1, height / WideDownsample);
                gasWidePixels = new Color[wideW * wideH];

                gasCurrent = null;
                gasPrevious = null;
                mistCurrent = null;
                mistPrevious = null;
                gasWideCurrent = null;
                gasWidePrevious = null;
                gasTrack.Valid = false;
                mistTrack.Valid = false;
                ClearKnown();
                gasClean = false;
                mistClean = false;
                smoothInitialized = false;
                lastRefresh = float.NegativeInfinity;
                lastTimedRefresh = float.NegativeInfinity;
                fadeStart = float.NegativeInfinity;
                winW = 0;
                winH = 0;

                rectMoved = true;
                originMoved = true;
            }

            EnsureCanvas();

            // --- window: the whole map, always --------------------------------------
            //
            // Keeping the window equal to the map is what removes the last of the moving
            // parts: the origin is 0 forever, `rectMoved` is true only on the first frame
            // and after a map change, and nothing downstream ever sees a window that moved.
            int wantWinW = width;
            int wantWinH = height;

            // Hysteresis, and it is load-bearing. CurrentViewRect is Floor/Ceil of a moving
            // float, so view.Width jitters by one cell while the camera pans. RoundUp with
            // no dead band turns that into an adjacent-bucket hop almost every frame when
            // the jitter straddles a bucket edge (measured 72 <-> 80 on 70% of frames at
            // some zoom levels). Every hop was a full rebake plus a GPU image rebuild, and
            // it also moved the window origin.
            //
            // Growing is immediate: the freshly exposed ring is outside the view, so
            // baking it costs nothing visually. Shrinking waits, because it is the half
            // that oscillates — a pan that alternates a view width between 152 and 168
            // hops the window between 144 and 168 every frame, and each hop is a full
            // rebake. The delay makes the shrink follow a real zoom gesture instead of the
            // jitter riding on top of it.
            //
            // Growing needs a dead band too, and leaving it out was the remaining source
            // of the jump: grow-on-contact plus delayed-shrink turns a view that jitters
            // across a bucket edge into a clean 1 Hz sawtooth (grow now, shrink 30 frames
            // later, grow again). Requiring a full quantum to grow costs at most 8 cells of
            // the 34-cell margin — still 13 per side against PanSlack's 9 — and it makes
            // the window hold still for a view that is only wobbling.
            //
            // The dead band must never be allowed to starve the margin, though. Holding the
            // window 8 cells under wantWinW, with snapping eating up to 7 more, left the
            // east margin at 2 cells; the slack test then failed on every frame and
            // recentred forever — 3477 "recentre" notes in 30 s, most of them slack=False
            // with no border flush at all. The floor below breaks that loop: the window may
            // lag a bucket behind for hysteresis, but never behind the view plus both
            // margins.
            int minWinW = Mathf.Min(width, viewW + 2 * BakeMargin);
            int minWinH = Mathf.Min(height, viewH + 2 * BakeMargin);

            if (winW < minWinW || wantWinW > winW + WindowQuantum)
            {
                winW = wantWinW;
                windowSurplusWFrames = 0;
                rectMoved = true;
            }
            else if (wantWinW + WindowQuantum < winW)
            {
                if (++windowSurplusWFrames >= WindowShrinkDelayFrames)
                {
                    winW = wantWinW;
                    windowSurplusWFrames = 0;
                    rectMoved = true;
                }
            }
            else
            {
                windowSurplusWFrames = 0;
            }

            if (winH < minWinH || wantWinH > winH + WindowQuantum)
            {
                winH = wantWinH;
                windowSurplusHFrames = 0;
                rectMoved = true;
            }
            else if (wantWinH + WindowQuantum < winH)
            {
                if (++windowSurplusHFrames >= WindowShrinkDelayFrames)
                {
                    winH = wantWinH;
                    windowSurplusHFrames = 0;
                    rectMoved = true;
                }
            }
            else
            {
                windowSurplusHFrames = 0;
            }

            // Invariants the bakes rely on:
            //   a) the window never leaves the map   (rectMin + win <= map size)
            //   b) the view is covered with PanSlack cells to spare on every side
            //
            // (a) is not implied by (b): when the window grows, the slack test gets *looser*
            // while the old origin can already be too far right/bottom. Checking only (b)
            // let the bake index past the last map row (IndexOutOfRangeException).
            bool windowFitsMap =
                rectMinX >= 0 && rectMinZ >= 0
                && rectMinX + winW <= mapWidth
                && rectMinZ + winH <= mapHeight;

            // A side already flush with the map border can never have PanSlack of margin:
            // the clamp parked it there and no camera movement can push it further. Testing
            // the slack on such a side is not merely useless, it is the bug — it makes
            // viewInsideSlack permanently false, so the *un-pinned* axes get recentred
            // every single frame and follow the view's one-cell jitter.
            //
            // That is the "layer jumps while the camera is still" report, and it is exactly
            // what the log shows: rectMinX pinned at 0 (camera viewMinX -168, clipped to 0)
            // while rectMinZ walks 50 <-> 48 twice per publish, each step a full rebake.
            bool flushWest = rectMinX <= 0;
            bool flushEast = rectMinX + winW >= mapWidth;
            bool flushNorth = rectMinZ <= 0;
            bool flushSouth = rectMinZ + winH >= mapHeight;

            bool viewInsideSlack =
                (flushWest || rectMinX + PanSlack <= view.minX)
                && (flushEast || view.maxX <= rectMinX + winW - 1 - PanSlack)
                && (flushNorth || rectMinZ + PanSlack <= view.minZ)
                && (flushSouth || view.maxZ <= rectMinZ + winH - 1 - PanSlack);

            if (resize || !windowFitsMap || !viewInsideSlack)
            {
                // Which of the three fired, and what the window is about to become. Without
                // this the log only shows the consequences (a moved rect, a rebake) and the
                // reason has to be reconstructed from the surrounding numbers — the last two
                // rounds were spent doing exactly that.
                if (GasFieldDiagnostics.Enabled)
                {
                    GasFieldDiagnostics.Note("recentre: resize=" + resize
                        + " fits=" + windowFitsMap
                        + " slack=" + viewInsideSlack
                        + " flush=" + (flushWest ? "W" : "") + (flushEast ? "E" : "")
                        + (flushNorth ? "N" : "") + (flushSouth ? "S" : "")
                        + " view=(" + view.minX + "," + view.minZ + ")-("
                        + view.maxX + "," + view.maxZ + ")"
                        + " win=(" + rectMinX + "," + rectMinZ + ") " + winW + "x" + winH);
                }

                int minX = Mathf.Clamp(view.minX - BakeMargin, 0, Mathf.Max(0, mapWidth - winW));
                int minZ = Mathf.Clamp(view.minZ - BakeMargin, 0, Mathf.Max(0, mapHeight - winH));

                // The wide field is an 8x8 block average whose blocks start at the window
                // origin (BakeWide writes block j from world rectMin + 8j). An origin that
                // is not a multiple of WideDownsample lands those blocks on a different set
                // of cells after every recentre, so the average itself changes: measured
                // 4.9% mean and 17% locally for a 14-cell recentre, against 0.2% when the
                // origin stays aligned. That is what made the dense-gas halo shimmer while
                // the camera moved — the narrow field is per-cell and unaffected, which is
                // why only the extra glow flickered.
                int snappedX = minX - minX % WideDownsample;
                int snappedZ = minZ - minZ % WideDownsample;

                // Snap unconditionally, and grow the window by the amount the origin moved
                // so the far edge stays exactly where it was. Snapping used to be
                // conditional ("only if the snapped window still reaches view.max"), which
                // left the origin unaligned exactly where the map clamp pins it — at a
                // border — and that is where this camera spends its time.
                //
                // Measured on the 60 s capture: rectMinZ % 8 took the values 0 and 2 (59
                // and 159 snapshots), so every recentre between "free" and "pinned at the
                // south border" rebuilt the 8x8 wide field over a *different set of cells*.
                // The narrow density field is per cell and unaffected, but the wide field is
                // what widens the dense gas's contour — the halo — so the picture's interior
                // stayed put while the visible edge stepped by up to six cells and stepped
                // back. It also explains the frame-rate dependence: at 30 fps the camera
                // travels twice as far per recentre as at 60, so the block offset changes by
                // more and does so on more of the frames.
                // INERT: minX and minZ are clamped to 0 by the map-sized window, so both
                // grows are 0 and this block never does anything. It stays because it is the
                // fix for the wide-block misalignment, and if the window ever becomes a
                // moving rectangle again this has to be here (and to stay unconditional).
                int growX = minX - snappedX;
                int growZ = minZ - snappedZ;
                if ((growX != 0 || growZ != 0)
                    && winW + growX <= width
                    && winH + growZ <= height)
                {
                    minX = snappedX;
                    minZ = snappedZ;
                    winW += growX;
                    winH += growZ;
                }

                // The old conditional application is gone: the snap above is now applied
                // unconditionally and the window grew instead of the far edge moving, so
                // the last visible cells on the border side are still baked (the far edge
                // never moved at all). Nothing here may undo the alignment.

                // Assign unconditionally: the clamp result is the only valid origin, even
                // when it happens to equal the current one.
                if (minX != rectMinX || minZ != rectMinZ)
                {
                    rectMoved = true;
                    originMoved = true;
                }

                rectMinX = minX;
                rectMinZ = minZ;
            }

            // Belt and braces: every bake indexes the map directly, so the window must sit
            // inside it no matter which branch above ran (and no matter how it is edited
            // later). Two clamps per frame, no per-cell cost.
            int safeX = Mathf.Clamp(rectMinX, 0, Mathf.Max(0, mapWidth - winW));
            int safeZ = Mathf.Clamp(rectMinZ, 0, Mathf.Max(0, mapHeight - winH));
            if (safeX != rectMinX || safeZ != rectMinZ)
            {
                rectMinX = safeX;
                rectMinZ = safeZ;
                rectMoved = true;
                originMoved = true;
            }

            return rectMoved;
        }

        /// <summary>
        /// Float 0..1 to a byte, matching the engine's implicit Color32 conversion exactly
        /// (round half to even, then clamp) so switching to SetPixels32 changes nothing
        /// on screen.
        ///
        /// The obvious alternative — (byte)(Clamp01(v) * 255f + 0.5f) — measured *slower*
        /// in the real loop (106 us against 88 us for an 80x64 gas bake): converting float
        /// straight to byte costs more than rounding to int and truncating, and the earlier
        /// "this is 43% of the bake" figure came from a micro-loop that the JIT had
        /// vectorised and the bake loop cannot.
        /// </summary>
        private static byte ToByte(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
        }

        private static int RoundUp(int value, int quantum)
        {
            return ((value + quantum - 1) / quantum) * quantum;
        }

        /// <summary>
        /// The density texture. RGBA32, explicitly.
        ///
        /// This used to ask for TextureFormat.RGBAHalf on the theory that 16-bit texels keep
        /// the smoothed value between 1/255 steps. That never took effect: the engine maps
        /// TextureFormat to its own format with a switch that has no RGBAHalf case, so the
        /// request silently fell through to Rgba32 — which is why SetPixels(Color[]) was
        /// converting to Color32 per texel. Asking for RGBA32 says what actually happens;
        /// a real half-float path would have to build the texture through
        /// Waylith.Rendering.Texture2D and fill the level bytes itself.
        ///
        /// No mip chain: the wide field is its own texture, and the CPU mip generator only
        /// understands RGBA32 anyway.
        /// </summary>
        private Texture2D NewSmoothTexture(string name)
        {
            return new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
        }

        /// <summary>
        /// The wide field: same format as the density texture (so the halo keeps sub-step
        /// precision on thin gas) but WideDownsample times smaller, and stretched back up
        /// by the sampler's bilinear filter.
        /// </summary>
        private Texture2D NewWideTexture(string name)
        {
            return new Texture2D(wideW, wideH, TextureFormat.RGBA32, false, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
        }

        /// <summary>
        /// Creates the six canvas textures, the quad and the material if they are missing.
        /// Idempotent by construction: `??=` makes "just released by DisposeTextures" and
        /// "already allocated" the same statement, so every exit path can prove them
        /// non-null. That is what lets the frame path read them without a null check —
        /// the cost is one reference compare per handle, per call, not per cell.
        /// </summary>
        [MemberNotNull(
            nameof(gasSmoothA),
            nameof(gasSmoothB),
            nameof(mistSmoothA),
            nameof(mistSmoothB),
            nameof(gasWideA),
            nameof(gasWideB),
            nameof(mesh),
            nameof(material))]
        private void EnsureCanvas()
        {
            gasSmoothA ??= NewSmoothTexture("VisualTweaksGasFieldGasSmoothA");
            gasSmoothB ??= NewSmoothTexture("VisualTweaksGasFieldGasSmoothB");
            mistSmoothA ??= NewSmoothTexture("VisualTweaksGasFieldMistSmoothA");
            mistSmoothB ??= NewSmoothTexture("VisualTweaksGasFieldMistSmoothB");
            gasWideA ??= NewWideTexture("VisualTweaksGasFieldGasWideA");
            gasWideB ??= NewWideTexture("VisualTweaksGasFieldGasWideB");

            if (mesh == null)
            {
                mesh = new Mesh
                {
                    name = "VisualTweaksGasFieldQuad"
                };
                mesh.MarkDynamic();
            }

            EnsureMaterial();
        }

        [MemberNotNull(nameof(material))]
        private void EnsureMaterial()
        {
            if (material != null)
            {
                return;
            }

            ShaderDef? def = GasFieldBridge.FieldShaderDef;
            if (def == null)
            {
                throw new InvalidOperationException("Gas field shader def is not available.");
            }

            material = new Material((Shader)def)
            {
                name = "VisualTweaksGasFieldMat",
                renderQueue = RenderQueue,
            };

            // Per-gas tints, matching the constants baked into vanilla's
            // Map/GasRotating.frag.glsl (smoke 200,200,200 / tox 180,214,24 / rot
            // 214,90,24 / deadlife 0.01,0.05,0.2). Vanilla never exposes those as data —
            // GasGrid only carries densities — so there is nothing to read at runtime and
            // the values have to be copied by hand. Keep the @default block in
            // VisualTweaksGasField.frag.glsl in sync, or a later edit will only half-apply.
            material.SetVector("_SmokeColor", new Vector4(0.784f, 0.784f, 0.784f, 1f));
            material.SetVector("_ToxColor", new Vector4(0.706f, 0.839f, 0.094f, 1f));
            material.SetVector("_RotColor", new Vector4(0.839f, 0.353f, 0.094f, 1f));
            // Vanilla multiplies its near-black deadlife tint by a sparse dust texture, so
            // it only ever covers a few texels per cell. This shader has no sprite behind
            // it: the tint lands on a smooth full-cell field, where 0.01,0.05,0.2 would
            // read as a hole punched in the map. Same hue and channel ratio, lifted so the
            // dust stays a dark navy cloud instead of a silhouette.
            material.SetVector("_DustColor", new Vector4(0.02f, 0.08f, 0.32f, 1f));
            material.SetVector("_MistColor", new Vector4(0.87f, 0.91f, 0.96f, 1f));
        }

        /// <summary>
        /// Bakes the two per-cell fields into the back textures and swaps them in. The
        /// mist snapshot is copied first: the temporal-patch blend must not modify AO's
        /// published array, which gameplay code also reads.
        ///
        /// <paramref name="restartFade"/> is true for a timed publish *and* for a window
        /// move. Both upload a new texture, and the half that stops being live holds what
        /// the screen showed one frame ago; _Params7 samples it at its own origin, so
        /// starting the blend again there is continuous rather than a jump.
        ///
        /// It was once limited to stable windows, because the Draw layer forced fade back
        /// to 1 whenever the window moved — the restart and the guard cancelled into a
        /// single-frame 0 -> 1 step, which is exactly the flash. That guard is gone now
        /// that the shader can blend a pair with different origins.
        /// </summary>
        private void Refresh(Map map, float realtime, bool incremental, bool restartFade)
        {
            if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("Refresh begin: restartFade=" + restartFade
                + " incremental=" + incremental);
            bool wrote = BakeSmooth(map, realtime, incremental);
            GasFieldDiagnostics.Put("refresh.wrote", wrote);
            GasFieldDiagnostics.Put("refresh.restartFade", restartFade);
            GasFieldDiagnostics.Put("refresh.gasClean", gasClean);
            GasFieldDiagnostics.Put("refresh.mistClean", mistClean);
            GasFieldDiagnostics.Flag("fadeRestarted", wrote && restartFade);

            builtSmooth = VisualTweaksSettings.gasFieldSmooth;

            // Legacy ambient drift needs no local field; the local-flow mode samples the
            // map-anchored flow history. The gate is safe even though only the window is
            // drawn: with nothing in the window the whole history is frozen, so it stays
            // self-consistent and resumes from a coherent state when gas reappears.
            int noiseMode = GasFieldBridge.EffectiveNoiseMode;
            builtNoiseMode = noiseMode;

            if (gasClean && mistClean)
            {
                // Nothing will be drawn until content returns. The noise offset is only
                // consumed by a draw and the integration would bail out anyway, so skip
                // both; the next non-empty Refresh fills them before any uniform is written.
                lastRefresh = realtime;
                if (wrote && restartFade)
                {
                    RestartFade(realtime);
                }

                return;
            }

            if (noiseMode == VisualTweaksSettings.GasNoiseAmbient)
            {
                BakeAmbient(map, shaderClock);
            }
            else
            {
                BakeFlow(map, shaderClock);
            }

            lastRefresh = realtime;

            // A publish that wrote nothing has nothing to fade *from*: restarting there
            // re-exposes the previous texture, and while the gate holds a channel empty
            // that texture is the last one that had content — the ghost. That is the only
            // condition left; the window-stability rule that used to be here went with the
            // Draw-side guard (see the parameter description).
            if (wrote && restartFade)
            {
                RestartFade(realtime);
            }
        }

        /// <summary>
        /// Bakes the raw solver values into floats, runs one exponential smoothing step
        /// (when enabled), then uploads as RGBA32.
        /// </summary>
        private bool BakeSmooth(Map map, float realtime, bool incremental)
        {
            EnsureCanvas();
            float dt = realtime - lastRefresh;
            if (!float.IsFinite(dt) || dt <= 0f || dt > 2f * refreshPeriod)
            {
                dt = refreshPeriod;
            }

            float tau = Mathf.Max(0.02f, VisualTweaksSettings.gasFieldSmoothTau);
            float k = smoothInitialized && VisualTweaksSettings.gasFieldSmooth
                ? 1f - Mathf.Exp(-dt / tau)
                : 1f;
            smoothInitialized = true;

            // Each bake returns whether it touched its staging buffer at all. A channel
            // that knows it is empty returns false without doing anything, which is the
            // whole point of the content gate: no gas in the window means no per-cell loop,
            // no wide pass and no upload.
            bool gasWritten = BakeGasSmooth(map, k, incremental);
            bool mistWritten = BakeMistSmooth(map, k, incremental);
            GasFieldDiagnostics.Put("refresh.gasWritten", gasWritten);
            GasFieldDiagnostics.Put("refresh.mistWritten", mistWritten);
            GasFieldDiagnostics.Put("refresh.smoothK", k);
            GasFieldDiagnostics.Put("refresh.smoothDt", dt);

            // Whatever was baked above covered this window at this origin, so every cell in
            // it now has history for the next blend.
            MarkKnownWindow();

            if (gasWritten)
            {
                // Before UploadTexture swaps the pair: this samples the pixels that were
                // just baked, which is what the report needs to tell "the picture changed"
                // apart from "the binding changed".
                PutGasContentSignature();

                // The wide texture only has to follow the gas texture; if the density did
                // not change there is nothing new to halate either.
                BakeWide();
                if (UploadTexture(
                        gasSmoothA,
                        gasSmoothB,
                        ref gasSmoothPixels,
                        ref gasSmoothSent,
                        ref gasCurrent,
                        ref gasPrevious,
                        gasTrack,
                        out bool gasPreviousFresh))
                {
                    UploadWide(gasPreviousFresh, gasWideA, gasWideB);
                }

                if (gasClean)
                {
                    CollapsePair(ref gasPrevious, gasCurrent, gasTrack);
                    gasWidePrevious = gasWideCurrent;
                }
            }

            if (mistWritten)
            {
                UploadTexture(
                    mistSmoothA,
                    mistSmoothB,
                    ref mistSmoothPixels,
                    ref mistSmoothSent,
                    ref mistCurrent,
                    ref mistPrevious,
                    mistTrack,
                    out _);

                if (mistClean)
                {
                    CollapsePair(ref mistPrevious, mistCurrent, mistTrack);
                }
            }

            forceUpload = false;
            return gasWritten || mistWritten;
        }

        /// <summary>
        /// Reduces the freshly baked window to two scalars and puts them in the frame
        /// snapshot.
        ///
        /// The tex.* keys in a report prove which texture name is bound; they say nothing
        /// about whether the pixels underneath moved. Two frames that swap A and B while
        /// both numbers here stay put are a re-bind of the same picture, which looks like
        /// nothing. A hard cut that also moves them is the flash. That distinction is
        /// exactly what the first log could not make, so every window hop looked identical
        /// to a real flash.
        ///
        /// Sampling at most 32 x 32 texels keeps it cheap enough to leave on while the
        /// diagnostics switch is on — this never runs otherwise.
        /// </summary>
        private void PutGasContentSignature()
        {
            if (!GasFieldDiagnostics.Enabled
                || gasSmoothPixels.Length == 0
                || winW <= 0
                || winH <= 0)
            {
                return;
            }

            int w = width;
            int step = Mathf.Max(1, Mathf.Max(winW, winH) / 32);
            long luma = 0;
            int lit = 0;
            int count = 0;

            for (int z = 0; z < winH; z += step)
            {
                int row = (height - 1 - z) * w;
                for (int x = 0; x < winW; x += step)
                {
                    Color32 c = gasSmoothPixels[row + x];
                    // Alpha carries the visibility mask and rgb the density phase, so the
                    // sum moves both when the gas shifts and when it thickens.
                    int v = c.r + c.g + c.b + c.a;
                    luma += v;
                    if (v != 0)
                    {
                        lit++;
                    }

                    count++;
                }
            }

            GasFieldDiagnostics.Put("content.gasMean",
                count > 0 ? (double)luma / count : 0.0);
            GasFieldDiagnostics.Put("content.gasLit",
                count > 0 ? (double)lit / count : 0.0);
        }

        /// <summary>
        /// Points the cross-fade's "old" endpoint at the texture that was just uploaded.
        ///
        /// The gate empties a channel by zeroing the staging buffer and uploading it, which
        /// leaves the *other* half of the pair holding the last picture that had content —
        /// and the fade keeps being restarted (every publish, and every frame the camera
        /// moves), so that picture is blended back in at every interval. It is sampled
        /// through its own mapping, so it reappears exactly where the content used to be:
        /// pan out of the gas, pan back, and the field's ghost is still there, permanently,
        /// because the gate never uploads anything to move it off the pair.
        ///
        /// The gate only closes once the whole window — view plus margin — is empty, so the
        /// content it dropped was already outside the view: collapsing costs no visible
        /// fade-out.
        /// </summary>
        // `current` may be null on the frame after a resize, and "previous collapses to
        // nothing" is exactly what the pair pointers mean then.
        private static void CollapsePair(ref Texture2D? previous, Texture2D? current, WindowTrack track)
        {
            GasFieldDiagnostics.Flag("collapsePair", true);
            if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("CollapsePair: previous <- current ("
                + (current?.name ?? "null") + ")");
            previous = current;
            track.PrevMinX = track.MinX;
            track.PrevMinZ = track.MinZ;
        }

        /// <summary>
        /// Box-averages the density staging buffer into the wide texture, one texel per
        /// WideDownsample^2 cells. The result covers the same world rectangle as the density
        /// texture (only coarser), so the shader reuses the same uv for both.
        /// </summary>
        private void BakeWide()
        {
            Array.Clear(gasWidePixels, 0, gasWidePixels.Length);

            int blockW = winW / WideDownsample;
            int blockH = winH / WideDownsample;
            if (blockW <= 0 || blockH <= 0)
            {
                return;
            }

            // (rowBase is gone: the wide blocks are anchored like the gas rows now, at the
            // bottom of the texture, not at the bottom of the *content*.)
            float inv = 1f / (WideDownsample * WideDownsample * 255f);

            for (int by = 0; by < blockH; by++)
            {
                // This loop has to use the *same* row convention as the gas bake, and it
                // did not. BakeGasSmooth writes window offset z to row (height - 1 - z);
                // this used to read from (height - winH + 8*by) and write to
                // (wideH - blockH + by), which is neither the same row nor the same end of
                // the texture: block 0 averaged the window's *northern* strip and landed
                // where the shader samples its *southern* one. The halo was therefore
                // mirrored in Z and displaced by whatever spare capacity the texture had —
                // and that spare capacity is exactly (height - winH), which changes every
                // time the window resizes.
                //
                // Only Z. X was always correct: block bx covers cells 8bx..8bx+7 and lands
                // in column bx, which is what the shader's uv asks for. That asymmetry is
                // the reported one — moving the camera vertically jitters the gas's edge,
                // moving it horizontally does not, because with the window spanning the
                // whole map width (winW == width) the X axis never changes at all.
                //
                // Reacts to the frame rate for the same reason: the vertical window size
                // steps every 8 cells of camera travel, so a slow frame changes it by more.
                int outRow = (wideH - 1 - by) * wideW;
                int srcRow = (height - 1 - (by * WideDownsample + WideDownsample - 1)) * width;

                for (int bx = 0; bx < blockW; bx++)
                {
                    int src = srcRow + bx * WideDownsample;
                    int r = 0;
                    int g = 0;
                    int b = 0;
                    int a = 0;

                    for (int y = 0; y < WideDownsample; y++)
                    {
                        int row = src + y * width;
                        for (int x = 0; x < WideDownsample; x++)
                        {
                            Color32 c = gasSmoothPixels[row + x];
                            r += c.r;
                            g += c.g;
                            b += c.b;
                            a += c.a;
                        }
                    }

                    // Integer accumulation over bytes, one normalisation per wide texel:
                    // 64 float multiplies per texel would cost more than the reads.
                    gasWidePixels[outRow + bx] =
                        new Color(r * inv, g * inv, b * inv, a * inv);
                }
            }
        }

        /// <summary>
        /// Swaps the wide texture in. It is small enough (a few KB) that the same skip test
        /// is not worth the bookkeeping: it only runs when the density itself was uploaded.
        ///
        /// <paramref name="previousIsFresh"/> mirrors the density pair's cut: when the
        /// density previous was pointed at the just-written texture, the wide previous has
        /// to follow, otherwise the shader would map an old wide window through _Params7 =
        /// the new origin and the halo alone would jump.
        /// </summary>
        private void UploadWide(bool previousIsFresh, Texture2D wideA, Texture2D wideB)
        {
            Texture2D write;
            if (gasWideCurrent == null)
            {
                write = wideA;
                gasWidePrevious = wideA;
            }
            else
            {
                write = gasWideCurrent == wideA ? wideB : wideA;
                gasWidePrevious = previousIsFresh ? write : gasWideCurrent;
            }

            write.SetPixels(gasWidePixels, 0);
            write.Apply(false, false);
            gasWideCurrent = write;
        }

        /// <summary>
        /// The map-anchored history grid and the GPU copy of it. Allocated once per map
        /// size; a map change or a first use starts from zero displacement, which is
        /// correct because there is no gas to move yet.
        ///
        /// The texture spans the whole map at FlowCellSize cells per texel and is mapped
        /// by world position, so panning and zooming never resample it. It is uploaded
        /// only when the solver publishes, and only if the bytes changed.
        /// </summary>
        [MemberNotNull(nameof(windTexture))]
        private void EnsureFlowGrid()
        {
            int cellsX = Mathf.Max(1, (mapWidth + FlowCellSize - 1) / FlowCellSize);
            int cellsZ = Mathf.Max(1, (mapHeight + FlowCellSize - 1) / FlowCellSize);

            if (flowCellsX == cellsX && flowCellsZ == cellsZ)
            {
                // Cached grid: the only thing that can be missing is the texture itself,
                // which Dispose releases. `??=` keeps this path provably non-null.
                windTexture ??= NewFlowTexture(cellsX, cellsZ);
                return;
            }

            GasFieldDiagnostics.Flag("flowGridRebuilt", true);
            if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("EnsureFlowGrid REBUILT: " + flowCellsX + "x" + flowCellsZ
                + " -> " + cellsX + "x" + cellsZ);
            flowCellsX = cellsX;
            flowCellsZ = cellsZ;

            int count = cellsX * cellsZ;
            flowDx = new float[count];
            flowDz = new float[count];
            flowVx = new float[count];
            flowVz = new float[count];
            flowUx = new float[count];
            flowUz = new float[count];
            flowPrevDx = new float[count];
            flowPrevDz = new float[count];
            flowBlurScratch = new float[count];
            windUpload = new float[count * 4];
            windSent = new float[count * 4];
            baseDx = 0.0;
            baseDz = 0.0;

            DestroyTexture(ref windTexture);
            windTexture = NewFlowTexture(cellsX, cellsZ);

            flowInitialized = false;
            lastFlowSteps = int.MinValue;
            lastFlowTime = double.NaN;
        }

        private static Texture2D NewFlowTexture(int cellsX, int cellsZ)
        {
            // The CPU level is filled with raw floats (SetPixels32 would memcpy Color32
            // bytes into a 32-bit float level), so UploadFlowIfChanged writes the level
            // bytes through CoreTexture directly.
            return new Texture2D(
                cellsX,
                cellsZ,
                TextureFormat.RGBAFloat,
                false,
                true)
            {
                name = "VisualTweaksGasFieldFlow",
                wrapMode = TextureWrapMode.Clamp,
                // The shader samples with texelFetch (float textures are not always
                // linearly filterable), so the sampler setting is only a safeguard.
                filterMode = FilterMode.Point,
            };
        }

        /// <summary>
        /// One publish of the ambient drift: integrate the wind direction into a small
        /// displacement vector that the shader adds to the noise coordinate.
        ///
        /// This used to be evaluated in the shader as wind * absolute time, which is only
        /// correct while the wind is constant. Vanilla derives its heading from the world
        /// seed, the tick and the rainfall, so a weather transition can rotate it by up to
        /// a radian; multiplying that change by the minutes since the renderer started
        /// moved the whole pattern by tens of cells in one publish. Integrating here makes
        /// the offset a path integral: a direction change is a corner in the path, not a
        /// jump, and the low-pass turns the corner into a short curve. Units are the
        /// shader's noise units — AmbientDriftRate * animation per second, the same
        /// steady-state speed the old time-multiplied formula produced.
        /// </summary>
        private void BakeAmbient(Map map, double clockTime)
        {
            GasFieldBridge.TryGetAmbientWind(map, out float windX, out float windZ);

            float animation = Mathf.Clamp(VisualTweaksSettings.gasFieldAnimation, 0f, 2f);
            float rate = AmbientDriftRate * animation;

            // A NaN last time means "start a new integration window": a fresh renderer, or
            // a switch back from the local-flow mode. Start at zero rather than replay a
            // history that belongs to a different formulation.
            bool reseed = double.IsNaN(ambientLastTime);
            double dt = reseed ? 0.0 : clockTime - ambientLastTime;
            if (!double.IsFinite(dt) || dt <= 0.0)
            {
                dt = 0.0;
            }
            else if (dt > FlowStepLimit)
            {
                dt = FlowStepLimit;
            }

            if (reseed)
            {
                ambientDx = 0.0;
                ambientDz = 0.0;
                ambientVelX = windX * rate;
                ambientVelZ = windZ * rate;
            }
            else
            {
                // Move by the velocity that was on screen during this interval, then let
                // the new sample bend it. Same order as BakeFlow, so the shader's
                // extrapolation and the integrated offset meet without a step.
                ambientDx += ambientVelX * dt;
                ambientDz += ambientVelZ * dt;

                float targetX = windX * rate;
                float targetZ = windZ * rate;
                float k = dt > 0.0
                    ? 1f - Mathf.Exp(-(float)dt / FlowVelocityTau)
                    : 0f;
                ambientVelX += (targetX - ambientVelX) * k;
                ambientVelZ += (targetZ - ambientVelZ) * k;
            }

            ambientLastTime = clockTime;
            noisePublishTime = clockTime;
            GasFieldDiagnostics.Put("clock.noisePublishTime", clockTime);
            GasFieldDiagnostics.Put("noise.ambientDx", ambientDx);
            GasFieldDiagnostics.Put("noise.ambientDz", ambientDz);
            GasFieldDiagnostics.Put("noise.ambientVelX", ambientVelX);
            GasFieldDiagnostics.Put("noise.ambientVelZ", ambientVelZ);
            if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("BakeAmbient publish: dt=" + dt.ToString("F4"));
        }

        /// <summary>
        /// One publish of the local flow: sample the solver's wind, low-pass it, cap it,
        /// and integrate it into the per-cell displacement. Running this on the CPU (and
        /// only one grid point per eight cells) is what lets the pattern keep a continuous
        /// position across wind rewrites without a per-frame texture upload.
        /// </summary>
        private void BakeFlow(Map map, double clockTime)
        {
            EnsureFlowGrid();

            // Zooming and panning do not touch the flow field at all any more: the texture
            // is world-anchored, so only an actual solver publish has anything to do.
            // CompletedSteps increments exactly once per published snapshot.
            int steps = GasFieldBridge.CompletedSteps(map);
            bool newPublish = steps != lastFlowSteps;
            GasFieldDiagnostics.Put("flow.completedSteps", steps);
            GasFieldDiagnostics.Put("flow.lastFlowSteps", lastFlowSteps);
            GasFieldDiagnostics.Flag("newPublish", newPublish);
            GasFieldDiagnostics.Put("flow.newPublish", newPublish);
            GasFieldDiagnostics.Put("flow.simulationIntervalTicks",
                GasFieldBridge.SimulationIntervalTicks);
            if (!newPublish
                && !double.IsNaN(lastFlowTime)
                && clockTime - lastFlowTime >= refreshPeriod * 1.5)
            {
                // CompletedSteps is resolved reflectively; if a future AO stops exposing
                // it, fall back to the configured publish period instead of freezing.
                newPublish = true;
            }
            // Do not latch the step counter until the snapshot is actually consumed.
            // On a failed TryGetWind the next frame must still count as a new publish;
            // latching it here kept the early return alive while shaderClock moved on,
            // so sincePublish saturated at FlowStepLimit and the pattern stalled until
            // the timeout fallback happened to fire.
            if (!newPublish && flowInitialized)
            {
                return;
            }

            // A NaN lastFlowTime means "start a new integration window": either the grid
            // was just allocated, or the noise mode changed under a live history. In the
            // second case the velocities are stale but the displacement is still valid, so
            // this publish must not move anything (dt = 0) — it only re-seeds the velocity
            // from the current solver sample. It must not bail out either: the early
            // return would leave lastFlowTime at NaN and freeze the flow for good.
            bool reseed = double.IsNaN(lastFlowTime);
            GasFieldDiagnostics.Flag("reseed", reseed);
            GasFieldDiagnostics.Put("flow.reseed", reseed);
            GasFieldDiagnostics.Put("flow.lastFlowTime", lastFlowTime);
            double dt = reseed ? 0.0 : clockTime - lastFlowTime;
            if (!double.IsFinite(dt) || dt <= 0.0)
            {
                dt = 0.0;
            }
            else if (dt > FlowStepLimit)
            {
                dt = FlowStepLimit;
            }

            if (dt <= 0.0 && flowInitialized && !reseed)
            {
                return;
            }

            if (!GasFieldBridge.TryGetWind(
                    map,
                    out float[]? velocityX,
                    out float[]? velocityZ,
                    out float[]? obstacleMask))
            {
                // No solver data: hold the last displacement instead of snapping to zero.
                GasFieldDiagnostics.Flag("tryGetWindFailed", true);
                if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("BakeFlow: TryGetWind FAILED, hold last displacement");
                return;
            }

            float k = flowInitialized && !reseed
                ? 1f - Mathf.Exp(-(float)dt / FlowVelocityTau)
                : 1f;
            flowInitialized = true;
            lastFlowSteps = steps;
            lastFlowTime = clockTime;

            float animation = Mathf.Clamp(VisualTweaksSettings.gasFieldAnimation, 0f, 2f);
            // Land the displacement exactly where the shader already is. flowUx/flowUz
            // are the rates uploaded with the last texture, and the shader has been
            // showing D + U * (clock - noisePublishTime) * animation ever since.
            // Integrating with flowVx instead made the publish boundary jump by
            // (U_new - U_old) * dt * animation: a solver rewrite, an explosion or even
            // a window-mean change then read as one frame of stutter / snap-back. The
            // ambient path never had this problem because it integrates and extrapolates
            // with the same ambientVel. dt is in game-time seconds, so no extra
            // game-speed factor belongs here.
            float step = (float)dt * animation;
            int count = flowCellsX * flowCellsZ;

            if (step > 0f)
            {
                Array.Copy(flowDx, flowPrevDx, count);
                Array.Copy(flowDz, flowPrevDz, count);

                for (int i = 0; i < count; i++)
                {
                    flowDx[i] += flowUx[i] * step;
                    flowDz[i] += flowUz[i] * step;
                }
            }

            for (int gz = 0; gz < flowCellsZ; gz++)
            {
                float worldZ = (gz + 0.5f) * FlowCellSize;
                int row = gz * flowCellsX;

                for (int gx = 0; gx < flowCellsX; gx++)
                {
                    float worldX = (gx + 0.5f) * FlowCellSize;
                    SampleWind(
                        velocityX,
                        velocityZ,
                        obstacleMask,
                        worldX,
                        worldZ,
                        out float vx,
                        out float vz);

                    // Clip the explosion impulse before it reaches the integrator. The
                    // density itself is advected with the full wind; the detail pattern
                    // only needs the direction, and an uncapped 60 m/s spike would still
                    // shear the noise even after the low-pass.
                    float speedSq = vx * vx + vz * vz;
                    if (speedSq > FlowWindCap * FlowWindCap)
                    {
                        float scale = FlowWindCap / Mathf.Sqrt(speedSq);
                        vx *= scale;
                        vz *= scale;
                    }

                    int i = row + gx;
                    if (k < 1f)
                    {
                        vx = flowVx[i] + (vx - flowVx[i]) * k;
                        vz = flowVz[i] + (vz - flowVz[i]) * k;
                    }

                    flowVx[i] = vx;
                    flowVz[i] = vz;
                }
            }

            if (step > 0f)
            {
                // Base velocity: mean over the current window, not the whole map. A view
                // that is mostly a sealed room then has a slow base instead of being
                // dragged along by the map's outdoor average. The window mean changes
                // gradually while panning, and the base displacement never jumps — it only
                // changes the reference the deviation is relaxed toward.
                int wx0 = Mathf.Clamp(rectMinX / FlowCellSize, 0, flowCellsX - 1);
                int wz0 = Mathf.Clamp(rectMinZ / FlowCellSize, 0, flowCellsZ - 1);
                int wx1 = Mathf.Clamp(
                    (rectMinX + winW - 1) / FlowCellSize,
                    wx0,
                    flowCellsX - 1);
                int wz1 = Mathf.Clamp(
                    (rectMinZ + winH - 1) / FlowCellSize,
                    wz0,
                    flowCellsZ - 1);

                double sumVx = 0.0;
                double sumVz = 0.0;
                int samples = 0;
                for (int z = wz0; z <= wz1; z++)
                {
                    int row = z * flowCellsX;
                    for (int x = wx0; x <= wx1; x++)
                    {
                        sumVx += flowVx[row + x];
                        sumVz += flowVz[row + x];
                        samples++;
                    }
                }

                float invSamples = 1f / Mathf.Max(1, samples);
                baseDx += sumVx * invSamples * step;
                baseDz += sumVz * invSamples * step;

                // Bound the part of the displacement that differs from the window-wide
                // mean. This is what keeps a persistent wind difference from compressing
                // the noise into stripes after a few minutes.
                RelaxFlowDeviation(Mathf.Exp(-(float)dt / FlowDeviationTau));

                // flowU is the rate the shader will use for the *next* interval, so it no
                // longer has to equal this interval's measured difference. Keep that
                // measured part (it carries the relaxation) and blend in the local wind:
                // without it U becomes purely self-referential and the per-cell differences
                // that "follow local wind" exists to show flatten out over a few publishes.
                // The same low-pass gain as the velocity stops a solver rewrite from
                // becoming a rate step. Note the boundary is continuous regardless of how
                // U is defined: the CPU advances D by the very U the shader extrapolated.
                float invStep = 1f / step;
                float uk = 1f - Mathf.Exp(-(float)dt / FlowVelocityTau);
                for (int i = 0; i < count; i++)
                {
                    float realizedUx = (flowDx[i] - flowPrevDx[i]) * invStep;
                    float realizedUz = (flowDz[i] - flowPrevDz[i]) * invStep;
                    flowUx[i] = realizedUx + (flowVx[i] - realizedUx) * uk;
                    flowUz[i] = realizedUz + (flowVz[i] - realizedUz) * uk;
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    flowUx[i] = flowVx[i];
                    flowUz[i] = flowVz[i];
                }
            }

            float uMax = 0f;
            double uSum = 0.0;
            for (int i = 0; i < count; i++)
            {
                float m = Mathf.Max(Mathf.Abs(flowUx[i]), Mathf.Abs(flowUz[i]));
                if (m > uMax)
                {
                    uMax = m;
                }

                uSum += m;
            }

            GasFieldDiagnostics.Put("noise.flowUMax", uMax);
            GasFieldDiagnostics.Put("noise.flowUMean", count > 0 ? (float)(uSum / count) : 0f);
            GasFieldDiagnostics.Put("noise.flowDxCenter", count > 0 ? flowDx[count / 2] : 0f);
            GasFieldDiagnostics.Put("noise.flowDzCenter", count > 0 ? flowDz[count / 2] : 0f);
            GasFieldDiagnostics.Put("noise.flowVxCenter", count > 0 ? flowVx[count / 2] : 0f);
            GasFieldDiagnostics.Put("noise.flowUxCenter", count > 0 ? flowUx[count / 2] : 0f);
            GasFieldDiagnostics.Put("flow.dt", dt);
            GasFieldDiagnostics.Put("flow.step", step);
            if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("BakeFlow publish: dt=" + dt.ToString("F4")
                + " steps=" + steps + " reseed=" + reseed);

            noisePublishTime = clockTime;
            GasFieldDiagnostics.Put("clock.noisePublishTime", clockTime);
            UploadFlowTexture();
        }

        /// <summary>
        /// Relaxes the local part of the displacement toward the map-wide mean and blurs
        /// it slightly. The uniform part is preserved exactly; only the deviation is
        /// bounded, so the noise keeps following the general wind while a sealed room or a
        /// lee cannot accumulate an ever-growing shear.
        /// </summary>
        private void RelaxFlowDeviation(float decay)
        {
            int count = flowCellsX * flowCellsZ;
            BlurMap(flowDx, flowBlurScratch, FlowRelaxPasses);
            BlurMap(flowDz, flowBlurScratch, FlowRelaxPasses);

            float bx = (float)baseDx;
            float bz = (float)baseDz;

            for (int i = 0; i < count; i++)
            {
                float dx = Mathf.Clamp(
                    (flowDx[i] - bx) * decay,
                    -FlowMaxDeviation,
                    FlowMaxDeviation);
                float dz = Mathf.Clamp(
                    (flowDz[i] - bz) * decay,
                    -FlowMaxDeviation,
                    FlowMaxDeviation);
                flowDx[i] = bx + dx;
                flowDz[i] = bz + dz;
            }
        }

        /// <summary>
        /// In-place separable 1-2-1 blur over the whole flow grid, using one scratch array.
        /// Edges clamp so the map border does not pull the field toward zero.
        /// </summary>
        private void BlurMap(float[] channel, float[] scratch, int passes)
        {
            int w = flowCellsX;
            int h = flowCellsZ;

            for (int pass = 0; pass < passes; pass++)
            {
                for (int z = 0; z < h; z++)
                {
                    int row = z * w;
                    for (int x = 0; x < w; x++)
                    {
                        int xm = x > 0 ? x - 1 : 0;
                        int xp = x < w - 1 ? x + 1 : w - 1;
                        scratch[row + x] = (channel[row + xm]
                            + 2f * channel[row + x]
                            + channel[row + xp]) * 0.25f;
                    }
                }

                for (int x = 0; x < w; x++)
                {
                    for (int z = 0; z < h; z++)
                    {
                        int zm = z > 0 ? z - 1 : 0;
                        int zp = z < h - 1 ? z + 1 : h - 1;
                        channel[z * w + x] = (scratch[zm * w + x]
                            + 2f * scratch[z * w + x]
                            + scratch[zp * w + x]) * 0.25f;
                    }
                }
            }
        }

        /// <summary>
        /// Copies the map-anchored grid into the GPU level: rg = accumulated displacement,
        /// ba = realized velocity. Rows are written in CPU order because the raw level
        /// upload does not flip them (SetPixels32 is what flips the density textures).
        /// </summary>
        private void UploadFlowTexture()
        {
            if (windUpload.Length == 0 || flowDx.Length == 0)
            {
                return;
            }

            int count = flowCellsX * flowCellsZ;
            for (int i = 0; i < count; i++)
            {
                int o = i * 4;
                windUpload[o] = flowDx[i];
                windUpload[o + 1] = flowDz[i];
                windUpload[o + 2] = flowUx[i];
                windUpload[o + 3] = flowUz[i];
            }

            UploadFlowIfChanged();
        }

        /// <summary>
        /// Bilinear sample of the map-indexed velocity snapshot, weighted by the obstacle
        /// mask. Ignoring walls is deliberate: a wall corner has zero velocity, and a plain
        /// bilinear sample would drag the whole block toward calm exactly where the gas is
        /// being channeled along the wall.
        /// </summary>
        private void SampleWind(
            float[] velocityX,
            float[] velocityZ,
            float[] obstacleMask,
            float worldX,
            float worldZ,
            out float vx,
            out float vz)
        {
            vx = 0f;
            vz = 0f;

            int cells = mapWidth * mapHeight;
            if (velocityX.Length < cells
                || velocityZ.Length < cells)
            {
                return;
            }

            int x0 = Mathf.Clamp(Mathf.FloorToInt(worldX), 0, mapWidth - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(worldZ), 0, mapHeight - 1);
            int x1 = Mathf.Min(x0 + 1, mapWidth - 1);
            int z1 = Mathf.Min(z0 + 1, mapHeight - 1);

            int i00 = z0 * mapWidth + x0;
            int i10 = z0 * mapWidth + x1;
            int i01 = z1 * mapWidth + x0;
            int i11 = z1 * mapWidth + x1;

            float fx = Mathf.Clamp01(worldX - x0);
            float fz = Mathf.Clamp01(worldZ - z0);

            float w00 = (1f - fx) * (1f - fz) * MaskAt(obstacleMask, i00);
            float w10 = fx * (1f - fz) * MaskAt(obstacleMask, i10);
            float w01 = (1f - fx) * fz * MaskAt(obstacleMask, i01);
            float w11 = fx * fz * MaskAt(obstacleMask, i11);

            float total = w00 + w10 + w01 + w11;
            if (total <= 1e-5f)
            {
                return;
            }

            float inv = 1f / total;
            vx = (velocityX[i00] * w00
                + velocityX[i10] * w10
                + velocityX[i01] * w01
                + velocityX[i11] * w11) * inv;
            vz = (velocityZ[i00] * w00
                + velocityZ[i10] * w10
                + velocityZ[i01] * w01
                + velocityZ[i11] * w11) * inv;
        }

        private static float MaskAt(float[] mask, int index)
        {
            if (mask == null || (uint)index >= (uint)mask.Length)
            {
                return 1f;
            }

            return mask[index] > 0.5f ? 1f : 0f;
        }

        /// <summary>
        /// Uploads the float level by hand: SetPixels32 memcpys 4-byte Color32s, which
        /// would reinterpret a 32-bit float level as bytes. MarkDirty is what makes Apply
        /// rebuild the GPU image; without it the upload is skipped.
        /// </summary>
        private void UploadFlowIfChanged()
        {
            EnsureFlowGrid();
            // Local copy for the null test; a test on the field itself would make the
            // field "maybe null" for the copy below.
            float[]? sent = windSent;
            if (sent != null && SameBytes(windUpload, sent))
            {
                GasFieldDiagnostics.Flag("flowUploaded", false);
                if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("UploadFlowIfChanged: SKIPPED (bytes identical)");
                return;
            }

            var cpuData = windTexture.CoreTexture.CpuData;
            if (cpuData == null || cpuData.Levels.Length == 0)
            {
                return;
            }

            Span<byte> level = cpuData.Levels[0];
            Span<byte> source = MemoryMarshal.AsBytes(windUpload.AsSpan());
            if (level.Length != source.Length)
            {
                return;
            }

            source.CopyTo(level);
            cpuData.MarkDirty();
            windTexture.Apply(false, false);
            Array.Copy(windUpload, windSent, windUpload.Length);
            GasFieldDiagnostics.Flag("flowUploaded", true);
            if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("UploadFlowIfChanged: UPLOAD "
                + windUpload.Length + " floats -> " + (windTexture?.name ?? "null"));
        }

        private static bool SameBytes<T>(T[] a, T[] b) where T : struct
        {
            return b != null
                && a.Length == b.Length
                && MemoryMarshal.AsBytes(a.AsSpan())
                    .SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
        }

        /// <summary>
        /// Returns true when the staging buffer changed and needs uploading.
        /// </summary>
        private bool BakeGasSmooth(Map map, float k, bool incremental)
        {
            bool drawGas = DebugViewSettings.drawGas;
            GasGrid gasGrid = map.gasGrid;

            // Content gate. Most maps spend most of their life with no gas at all, and a
            // map that does have gas usually has it in one corner; either way the per-cell
            // loop below produces nothing but zeros for the whole window.
            if (!drawGas || !WindowHasGas(map, gasGrid))
            {
                return ClearGasField();
            }

            if (gasClean)
            {
                // Coming back from empty: the staging buffer holds bytes from before the
                // clear, and the gate skipped the uploads that would have kept it in step
                // with the window, so nothing in it can be reused.
                gasClean = false;
                incremental = false;
            }

            FogGrid fogGrid = map.fogGrid;

            int w = width;
            int h = height;

            for (int z = 0; z < winH; z++)
            {
                int row = (rectMinZ + z) * mapWidth + rectMinX;
                int outputRow = (h - 1 - z) * w;

                // A row or column outside the known rect has no history for these world
                // cells, so it takes the solver value directly (see the field comment).
                bool rowKnown = z >= knownZ0 && z < knownZ1;

                // INERT: `incremental` is false for the lifetime of the map (it needs
                // `scrolled`, which needs `originMoved`, which needs a window origin that
                // moves). Kept because it is the cheap version of this loop and would be
                // the right thing to restore if the window ever becomes a moving rectangle
                // again.
                if (incremental)
                {
                    // A movement-only publish: only the strip the window move exposed is
                    // unknown. Skipping the known cells inside the loop still walked every
                    // one of them; leaving them out of the runs is the actual saving.
                    if (rowKnown)
                    {
                        if (knownX0 > 0)
                        {
                            BakeGasRow(gasGrid, fogGrid, row, outputRow, 0, knownX0, k);
                        }

                        if (knownX1 < winW)
                        {
                            BakeGasRow(gasGrid, fogGrid, row, outputRow, knownX1, winW, k);
                        }
                    }
                    else
                    {
                        BakeGasRow(gasGrid, fogGrid, row, outputRow, 0, winW, k);
                    }

                    continue;
                }

                // Every span takes the same coefficient now. A cell that has no history is
                // seeded from the solver by the NaN check inside BakeGasRow; a cell that has
                // one is smoothed, wherever the window happens to be. The old k=1 spans were
                // there to seed cells the window had just exposed, and they did it by
                // throwing away the history of every cell they touched.
                if (rowKnown)
                {
                    if (knownX0 > 0)
                    {
                        BakeGasRow(gasGrid, fogGrid, row, outputRow, 0, knownX0, k);
                    }

                    BakeGasRow(gasGrid, fogGrid, row, outputRow, knownX0, knownX1, k);

                    if (knownX1 < winW)
                    {
                        BakeGasRow(gasGrid, fogGrid, row, outputRow, knownX1, winW, k);
                    }
                }
                else
                {
                    BakeGasRow(gasGrid, fogGrid, row, outputRow, 0, winW, k);
                }
            }

            LogGasSignature(gasGrid);

            return true;
        }

        /// <summary>
        /// Writes one horizontal run of the gas window. The caller splits each row into the
        /// known span (smoothed toward the solver with the publish coefficient) and the
        /// freshly exposed spans (seeded straight from the solver), so a movement-only bake
        /// never touches the cells that already hold the right world cell.
        /// </summary>
        private void BakeGasRow(
            GasGrid gasGrid,
            FogGrid fogGrid,
            int row,
            int outputRow,
            int x0,
            int x1,
            float cellK)
        {
            // The content gate above already established that gas is drawn at all.
            for (int x = x0; x < x1; x++)
            {
                int index = row + x;
                uint packed = 0u;

                // A fogged cell reads as empty, which is exactly what packed = 0 means, so
                // the fast path below covers it without a second branch.
                if (fogGrid == null || !fogGrid.IsFogged(index))
                {
                    packed = gasGrid.GetDirect(index);
                }

                // The history is indexed by *map cell*, not by texel. That is the whole
                // point of this loop: a texel means "this cell, in this window", so any
                // window change used to invalidate it — and invalidating it meant seeding
                // it straight from the solver, i.e. dropping the temporal lag in one frame.
                // The lag is what the eye reads as the gas: at the edge the solver value can
                // be tens of percent above the smoothed one, so a rebake moved the whole
                // contour outward and the next publishes pulled it back. "It jumps to
                // another place and then immediately returns", on every pan and zoom, worst
                // at a low frame rate. Map-anchored, the history simply follows the camera.
                //
                // NaN marks "never seen": seeded once with the solver value, then smoothed.
                int cell = index * 4;
                float s0 = gasSmoothState[cell];
                float s1 = gasSmoothState[cell + 1];
                float s2 = gasSmoothState[cell + 2];
                float s3 = gasSmoothState[cell + 3];

                // Empty cell, empty history: four conversions, four lerps and four NaN
                // tests all of which can only produce zero. Measured at 469 us for a
                // 250x250 map, of which the overwhelming majority was this — burnt 90% of
                // the bake on cells the picture does not contain.
                //
                // NaN compares false, so a cell baked for the first time takes the slow
                // path once and is seeded from the solver (which is zero here) as usual.
                if (packed == 0u && s0 < HalfByte && s1 < HalfByte && s2 < HalfByte && s3 < HalfByte)
                {
                    gasSmoothState[cell] = 0f;
                    gasSmoothState[cell + 1] = 0f;
                    gasSmoothState[cell + 2] = 0f;
                    gasSmoothState[cell + 3] = 0f;
                    gasSmoothPixels[outputRow + x] = default;
                    continue;
                }

                // Multiply by the reciprocal rather than divide by 255: the shift
                // is not a power of two, so every /255f was a real divss.
                float r = (packed & 0xFFu) * Inv255;
                float g = ((packed >> 8) & 0xFFu) * Inv255;
                float b = ((packed >> 16) & 0xFFu) * Inv255;
                float a = ((packed >> 24) & 0xFFu) * Inv255;

                gasSmoothState[cell] = float.IsNaN(s0) ? r : s0 + (r - s0) * cellK;
                gasSmoothState[cell + 1] = float.IsNaN(s1) ? g : s1 + (g - s1) * cellK;
                gasSmoothState[cell + 2] = float.IsNaN(s2) ? b : s2 + (b - s2) * cellK;
                gasSmoothState[cell + 3] = float.IsNaN(s3) ? a : s3 + (a - s3) * cellK;

                gasSmoothPixels[outputRow + x] = new Color32(
                    ToByte(gasSmoothState[cell]),
                    ToByte(gasSmoothState[cell + 1]),
                    ToByte(gasSmoothState[cell + 2]),
                    ToByte(gasSmoothState[cell + 3]));
            }
        }

        /// <summary>
        /// Compares the material's current constants with what this renderer wrote last
        /// frame. A difference means someone else touched the material between the queued
        /// draw and the flush, which would draw the field at a different place for exactly
        /// one frame and then look correct again.
        /// </summary>
        private void CheckUniformsUntouched()
        {
            if (!GasFieldDiagnostics.Enabled || !haveSetUniforms || material == null)
            {
                haveSetUniforms = material != null;
                return;
            }

            Vector4 p = material.GetVector(ParamsId);
            Vector4 p6 = material.GetVector(Params6Id);
            Vector4 p7 = material.GetVector(Params7Id);
            bool touched = (p - setParams).sqrMagnitude > 1e-10f
                || (p6 - setParams6).sqrMagnitude > 1e-10f
                || (p7 - setParams7).sqrMagnitude > 1e-10f;

            GasFieldDiagnostics.Flag("uniformTouched", touched);
            if (touched)
            {
                GasFieldDiagnostics.Put("uniform.driftP6z", p6.z - setParams6.z);
                GasFieldDiagnostics.Put("uniform.driftP6w", p6.w - setParams6.w);
                GasFieldDiagnostics.Put("uniform.driftP7w", p7.w - setParams7.w);
                GasFieldDiagnostics.Put("uniform.driftFade", p.x - setParams.x);
            }
        }

        /// <summary>
        /// Diagnostic: how far the baked texels sit from the solver, at the correct offset
        /// and at +/-8 cells on each axis.
        ///
        /// Uses the sum of all four channels and a mean absolute error, not the red channel
        /// with exact equality. The old version counted `baked.r == solver.r`, which is
        /// meaningless on a faint field: an ordinary cloud is 1..3 per channel, the temporal
        /// filter shifts every one of those by one, and a gas type that is not red leaves
        /// the channel at zero — the last log had nz = 0 and the check silently never ran.
        /// The mean error is scale-free, so the argmin over the offsets still finds a
        /// displacement of a handful of bytes.
        /// </summary>
        private void LogGasSignature(GasGrid gasGrid)
        {
            if ((!VisualTweaksLog.GasTraceEnabled && !GasFieldDiagnostics.Enabled)
                || traceRealtime - lastSignatureLog < 0.5f)
            {
                return;
            }

            lastSignatureLog = traceRealtime;

            int nz = 0;
            long err0 = 0, errx = 0, errz = 0;
            int stride = Mathf.Max(1, winW / 40);

            for (int z = 2; z < winH - 2; z += stride)
            {
                for (int x = 2; x < winW - 2; x += stride)
                {
                    Color32 c = gasSmoothPixels[(height - 1 - z) * width + x];
                    int baked = c.r + c.g + c.b + c.a;
                    if (baked == 0)
                    {
                        continue;
                    }

                    int index = (rectMinZ + z) * mapWidth + rectMinX + x;
                    if (index < 0 || index + 8 >= mapWidth * mapHeight)
                    {
                        continue;
                    }

                    nz++;
                    err0 += Math.Abs(baked - GasSum(gasGrid.GetDirect(index)));
                    errx += Math.Abs(baked - GasSum(gasGrid.GetDirect(index + 8)));
                    errz += Math.Abs(baked - GasSum(gasGrid.GetDirect(index + 8 * mapWidth)));
                }
            }

            if (nz == 0)
            {
                return;
            }

            // Also into the frame snapshot, so every report carries a direct answer to
            // "is the field drawn where the solver says it is".
            float e0 = err0 / (float)nz;
            float ex = errx / (float)nz;
            float ez = errz / (float)nz;
            GasFieldDiagnostics.Put("sig.err0", e0);
            GasFieldDiagnostics.Put("sig.errCellX", ex);
            GasFieldDiagnostics.Put("sig.errCellZ", ez);
            GasFieldDiagnostics.Put("sig.cells", nz);
            GasFieldDiagnostics.Flag("sigMisplaced", e0 > 1f && (e0 > ex * 1.5f || e0 > ez * 1.5f));

            VisualTweaksLog.GasTrace(
                "sig win (" + rectMinX + "," + rectMinZ + ") " + winW + "x" + winH
                + " nz=" + nz
                + $" err0={e0:F2} err+x={ex:F2} err+z={ez:F2}"
                + " quad (" + quadMinX + "," + quadMinZ + ") " + quadWidth + "x" + quadHeight);
        }

        /// <summary>Sum of the four packed gas channels, for the signature comparison.</summary>
        private static int GasSum(uint packed)
        {
            return (int)(packed & 0xFFu)
                + (int)((packed >> 8) & 0xFFu)
                + (int)((packed >> 16) & 0xFFu)
                + (int)((packed >> 24) & 0xFFu);
        }

        /// <summary>
        /// Exact test for gas anywhere in the window. Vectorised over each row of vanilla's
        /// cell array: the alternative, GasGrid.GetDirect per cell, costs a method call and
        /// a bounds check per cell and would eat a tenth of what it is trying to save.
        /// Falls back to "assume there is gas" when the array is not reachable, so a
        /// vanilla layout change degrades to the old behaviour instead of a blank field.
        /// </summary>
        private bool WindowHasGas(Map map, GasGrid gasGrid)
        {
            if (gasGrid == null || !gasGrid.CalculateGasEffects)
            {
                // Vanilla skips its own simulation when it has never had gas, so the grid
                // is all zeros by construction.
                return false;
            }

            if (!GasFieldBridge.TryGetGasDensity(map, out uint[]? density))
            {
                return true;
            }

            // When the dust channel is handed back to vanilla's sprite layer, a window that
            // holds nothing but dust has to read as empty: otherwise the field keeps baking
            // and uploading a quad whose every fragment discards. Dust is the top byte, so
            // masking it out is the whole test. The vectorised scan is kept for the normal
            // case; the masked one only runs while the setting is off.
            bool drawDust = VisualTweaksSettings.gasFieldReplaceDeadlife;

            for (int z = 0; z < winH; z++)
            {
                int offset = (rectMinZ + z) * mapWidth + rectMinX;
                if (offset < 0 || offset + winW > density.Length)
                {
                    return true;
                }

                if (drawDust)
                {
                    if (density.AsSpan(offset, winW).IndexOfAnyExcept(0u) >= 0)
                    {
                        return true;
                    }

                    continue;
                }

                for (int x = 0; x < winW; x++)
                {
                    if ((density[offset + x] & 0x00FFFFFFu) != 0u)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Empties the channel and reports whether that changed anything. Once the texture
        /// is all zeros it stays all zeros regardless of the window, so the whole bake and
        /// the upload can be skipped until content shows up again.
        /// </summary>
        private bool ClearGasField()
        {
            if (gasClean)
            {
                return false;
            }

            // The gas history is map-anchored and deliberately survives a window change;
            // only the "which part of this window is baked" bookkeeping is reset here.
            ClearWindowPixels(gasSmoothPixels);
            gasClean = true;

            // The upload below is the only chance to get the old picture off the screen: once
            // the gate is closed nothing else will ever upload this channel. Do not let the
            // "same bytes as last time" shortcut swallow it.
            forceUpload = true;
            return true;
        }

        /// <summary>
        /// Returns true when the staging buffer changed and needs uploading.
        /// </summary>
        private bool BakeMistSmooth(Map map, float k, bool incremental)
        {
            if (!GasFieldBridge.TryGetMistData(
                    map,
                    out float[]? mist,
                    out float[]? obstacleMask)
                || mist.Length < mapWidth * mapHeight
                || obstacleMask.Length < mapWidth * mapHeight)
            {
                return ClearMistField();
            }

            GasFieldBridge.TryGetMistRenderSettings(
                map,
                out float maxMist,
                out float renderZero,
                out float renderFull);

            // The gate has to look at the blended copy, not at AO's snapshot: the temporal
            // patches add mist where the coarse field is still zero.
            CopyWindow(mist, mistScratch);
            GasFieldBridge.ApplyTemporalPatchMist(map, mistScratch);
            // AO's own mist renderer maps everything below renderZero to zero opacity
            // (SmoothStep over renderZero..renderFull), so a cell that cannot draw a
            // pixel there cannot draw one here either. The old exact-zero test kept the
            // whole window bake alive for invisible residue, which is most of the map
            // most of the time.
            bool anyMist = WindowHasMist(mistScratch, Mathf.Max(0f, renderZero));

            if (!anyMist)
            {
                return ClearMistField();
            }

            if (mistClean)
            {
                // See BakeGasSmooth: the staging pixels survived the window moves the gate
                // skipped, so they cannot be shifted into place any more.
                mistClean = false;
                incremental = false;
            }

            FogGrid fogGrid = map.fogGrid;
            int w = width;
            int h = height;
            float zero = Mathf.Max(0f, renderZero);
            float full = Mathf.Max(zero + 0.001f, renderFull);
            // Both ranges are constant for the whole bake, so the per-cell code multiplies
            // by a reciprocal instead of dividing twice.
            float invFull = 1f / Mathf.Max(0.001f, maxMist);
            float invRange = 1f / (full - zero);

            for (int z = 0; z < winH; z++)
            {
                int row = (rectMinZ + z) * mapWidth + rectMinX;
                int outputRow = (h - 1 - z) * w;

                bool rowKnown = z >= knownZ0 && z < knownZ1;

                if (incremental)
                {
                    // See BakeGasSmooth: a movement-only bake leaves the known cells out of
                    // the runs instead of visiting them just to skip them.
                    if (rowKnown)
                    {
                        if (knownX0 > 0)
                        {
                            BakeMistRow(
                                mistScratch, obstacleMask, fogGrid, row, outputRow,
                                0, knownX0, 1f, zero, invFull, invRange);
                        }

                        if (knownX1 < winW)
                        {
                            BakeMistRow(
                                mistScratch, obstacleMask, fogGrid, row, outputRow,
                                knownX1, winW, 1f, zero, invFull, invRange);
                        }
                    }
                    else
                    {
                        BakeMistRow(
                            mistScratch, obstacleMask, fogGrid, row, outputRow,
                            0, winW, 1f, zero, invFull, invRange);
                    }

                    continue;
                }

                if (rowKnown)
                {
                    if (knownX0 > 0)
                    {
                        BakeMistRow(
                            mistScratch, obstacleMask, fogGrid, row, outputRow,
                            0, knownX0, 1f, zero, invFull, invRange);
                    }

                    BakeMistRow(
                        mistScratch, obstacleMask, fogGrid, row, outputRow,
                        knownX0, knownX1, k, zero, invFull, invRange);

                    if (knownX1 < winW)
                    {
                        BakeMistRow(
                            mistScratch, obstacleMask, fogGrid, row, outputRow,
                            knownX1, winW, 1f, zero, invFull, invRange);
                    }
                }
                else
                {
                    BakeMistRow(
                        mistScratch, obstacleMask, fogGrid, row, outputRow,
                        0, winW, 1f, zero, invFull, invRange);
                }
            }

            return true;
        }

        /// <summary>
        /// Writes one horizontal run of the mist window, with the same known/fresh split as
        /// <see cref="BakeGasRow"/>. The content gate above already proved that at least one
        /// cell renders, so there is no hasMist test per cell.
        /// </summary>
        private void BakeMistRow(
            float[] source,
            float[] obstacleMask,
            FogGrid fogGrid,
            int row,
            int outputRow,
            int x0,
            int x1,
            float cellK,
            float zero,
            float invFull,
            float invRange)
        {
            for (int x = x0; x < x1; x++)
            {
                int index = row + x;
                float value = 0f;

                if (!(fogGrid != null && fogGrid.IsFogged(index))
                    && obstacleMask[index] > 0.5f)
                {
                    float raw = source[index];

                    // InverseLerp + SmoothStep written out: identical result, but a cell
                    // under the render threshold — most of the map, most of the time —
                    // now skips the ramp and both divisions instead of producing 0
                    // through three multiplies and two clamps.
                    if (raw > zero)
                    {
                        float t = (raw - zero) * invRange;
                        if (t > 1f)
                        {
                            t = 1f;
                        }

                        value = Mathf.Min(1f, raw * invFull) * (t * t * (3f - 2f * t));
                    }
                }

                int cell = (outputRow + x) * 2;
                mistSmoothState[cell] += (value - mistSmoothState[cell]) * cellK;
                mistSmoothPixels[outputRow + x] = new Color32(
                    ToByte(mistSmoothState[cell]),
                    255,
                    0,
                    0);
            }
        }

        /// <summary>
        /// Whether any cell in the window renders mist at all, over the blended copy. The
        /// test is the same one the bake applies: raw > renderZero. AO's own mist renderer
        /// maps everything below that to zero opacity, so a residue that cannot draw a pixel
        /// there must not keep this channel's whole-window loop alive.
        ///
        /// Mist values are non-negative, and IEEE-754 bit patterns are monotonic over that
        /// range, so the row can still be tested with the vectorised span search instead of
        /// a scalar comparison loop.
        /// </summary>
        private bool WindowHasMist(float[] values, float threshold)
        {
            float gate = threshold > 0f ? threshold : 0f;
            uint limit = BitConverter.SingleToUInt32Bits(gate);

            for (int z = 0; z < winH; z++)
            {
                int offset = (rectMinZ + z) * mapWidth + rectMinX;
                if (offset < 0 || offset + winW > values.Length)
                {
                    return true;
                }

                // Anything outside [0, limit] is either above the render threshold or a
                // negative/NaN artefact; both keep the gate open rather than risk a hole.
                Span<uint> bits = MemoryMarshal.Cast<float, uint>(
                    values.AsSpan(offset, winW));
                if (bits.IndexOfAnyExceptInRange(0u, limit) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The texture rows and the smoothing state are addressed by the cell's offset
        /// from the window origin, so moving the origin by (dx, dz) has to move the
        /// contents the other way or every cell resolves to a different world cell than
        /// its history belongs to. Shifting is a memmove per channel — cheaper than the
        /// per-cell rebake it replaces, and it keeps the smoothing state instead of
        /// snapping the freshly exposed strip to the raw solver value.
        /// </summary>
        /// <summary>
        /// UNREACHABLE: the only caller is the `originMoved` branch, and the window origin
        /// is pinned to 0. Left in place because it documents why the scrolling design was
        /// fragile in the first place, and because deleting it is a separate, riskier edit.
        /// If it is ever revived, note that the gas smoothing state is map-anchored now and
        /// must NOT be shifted — only the staging pixels ever needed scrolling.
        /// </summary>
        private void ScrollBuffers(int windowDx, int windowDz)
        {
            forceUpload = true;

            if (!KnownValid)
            {
                // Nothing to carry over: the whole window will be seeded.
                return;
            }

            if (windowDx == 0 && windowDz == 0)
            {
                return;
            }

            // The content moves opposite to the window: the cell that sat at offset x+dx
            // is now at offset x, so its state has to come back by dx.
            int dx = -windowDx;
            int dz = -windowDz;

            // The smoothing state is single-buffered, so it moves in place.
            // The staging pixels are not: UploadTexture swaps `pixels` and `sent`, which
            // leaves the freshly uploaded bytes in `sent` and two-bakes-ago bytes in
            // `pixels`. Shifting `pixels` would therefore carry a window that is one step
            // stale — the whole field lands a few cells off after every upload. So the
            // pixels are always re-derived from `sent`, which is the buffer that ended up
            // holding the window that was last written (whether it was uploaded or found
            // identical).
            if (!gasClean)
            {
                // gasSmoothState used to be shifted here, because it lived in texel space.
                // It lives in map space now, so a window move needs no reindexing at all —
                // and shifting it would corrupt it.
                ShiftPixels(gasSmoothSent, gasSmoothPixels, dx, dz);
            }

            if (!mistClean)
            {
                ShiftWindow(mistSmoothState, mistSmoothState, 2, dx, dz);
                ShiftPixels(mistSmoothSent, mistSmoothPixels, dx, dz);
            }

            // The history rectangle rides along with the content; whatever the shift pushed
            // outside the window is simply gone.
            knownX0 = Mathf.Clamp(knownX0 + dx, 0, winW);
            knownX1 = Mathf.Clamp(knownX1 + dx, 0, winW);
            knownZ0 = Mathf.Clamp(knownZ0 + dz, 0, winH);
            knownZ1 = Mathf.Clamp(knownZ1 + dz, 0, winH);
            if (knownX1 < knownX0)
            {
                knownX1 = knownX0;
            }

            if (knownZ1 < knownZ0)
            {
                knownZ1 = knownZ0;
            }
        }

        /// <summary>
        /// Shifts the baked window inside a capacity-sized buffer by (dx, dz) cells. Only
        /// the window rows are touched; the ring outside it is stale by design.
        ///
        /// Rows are copied south-to-north or north-to-south depending on the sign of dz,
        /// because when source and target are the same array the source row of one copy is
        /// the destination row of another; going the wrong way would read what was already
        /// overwritten. Columns are handled by Array.Copy, which memmoves when the ranges
        /// overlap.
        /// </summary>
        /// <summary>
        /// UNREACHABLE. Only ScrollBuffers calls this, and ScrollBuffers is itself
        /// unreachable. Kept because the row convention it encodes (`height - 1 - z`, with
        /// the copy direction chosen by the sign of dz) is the same convention BakeWide had
        /// wrong for months — it is useful documentation of the convention, not dead weight
        /// to be dropped carelessly.
        /// </summary>
        private void ShiftWindow<T>(T[] source, T[] target, int stride, int dx, int dz)
        {
            int count = winW - Math.Abs(dx);
            if (count <= 0)
            {
                return;
            }

            int sourceX = dx > 0 ? 0 : -dx;
            int targetX = dx > 0 ? dx : 0;

            int zStart = dz > 0 ? winH - 1 : 0;
            int zEnd = dz > 0 ? -1 : winH;
            int zStep = dz > 0 ? -1 : 1;

            for (int z = zStart; z != zEnd; z += zStep)
            {
                int sourceZ = z - dz;
                if (sourceZ < 0 || sourceZ >= winH)
                {
                    continue;
                }

                int targetOffset = ((height - 1 - z) * width + targetX) * stride;
                int sourceOffset = ((height - 1 - sourceZ) * width + sourceX) * stride;
                Array.Copy(source, sourceOffset, target, targetOffset, count * stride);
            }
        }

        private void ShiftPixels(Color32[] source, Color32[] target, int dx, int dz)
        {
            if (source == null || target == null || source.Length != target.Length)
            {
                return;
            }

            ShiftWindow(source, target, 1, dx, dz);
        }

        private bool KnownValid => knownX1 > knownX0 && knownZ1 > knownZ0;

        /// <summary>
        /// After a bake every cell in the window has history, including the ones the empty
        /// gate just zeroed.
        /// </summary>
        private void MarkKnownWindow()
        {
            knownX0 = 0;
            knownX1 = winW;
            knownZ0 = 0;
            knownZ1 = winH;
        }

        /// <summary>
        /// DEGENERATE, not dead: it runs once after a resize (and on the first frame), when
        /// the window size changes but the origin does not. With both dimensions pinned to
        /// the map, that only happens when the map itself changes, and clamping a rect that
        /// is already the full map is a no-op.
        /// </summary>
        private void ClampKnown()
        {
            knownX0 = Mathf.Clamp(knownX0, 0, winW);
            knownX1 = Mathf.Clamp(knownX1, 0, winW);
            knownZ0 = Mathf.Clamp(knownZ0, 0, winH);
            knownZ1 = Mathf.Clamp(knownZ1, 0, winH);
        }

        private void ClearKnown()
        {
            knownCleared = true;
            knownX0 = 0;
            knownX1 = 0;
            knownZ0 = 0;
            knownZ1 = 0;
        }

        /// <summary>
        /// Zeroes the baked window (not the ring outside it) of a capacity-sized staging
        /// buffer.
        /// </summary>
        private void ClearWindowPixels<T>(T[] pixels)
        {
            for (int z = 0; z < winH; z++)
            {
                Array.Clear(pixels, (height - 1 - z) * width, winW);
            }
        }

        private bool ClearMistField()
        {
            if (mistClean)
            {
                return false;
            }

            Array.Clear(mistSmoothState, 0, mistSmoothState.Length);
            ClearWindowPixels(mistSmoothPixels);
            mistClean = true;
            forceUpload = true;
            return true;
        }

        /// <summary>
        /// Uploads a baked float field into the back texture and swaps it in. Returns early
        /// when the live texture already covers the window and holds the same bytes, since
        /// Waylith's Apply() disposes and recreates the whole GPU image.
        ///
        /// On a real upload <paramref name="previousIsFresh"/> reports that the previous
        /// half was pointed at the newly written texture instead of the old one, which
        /// turns the next cross-fade into a no-op. That happens on the first upload of a
        /// pair, whenever the window origin moved, and whenever the old window no longer
        /// contains the view — the three cases where the two endpoints would not share one
        /// uv mapping.
        ///
        /// The two buffers are swapped rather than copied, so the next bake writes into the
        /// one that still holds this upload — which is exactly what the compare needs.
        /// </summary>
        private bool UploadTexture(
            Texture2D textureA,
            Texture2D textureB,
            ref Color32[] pixels,
            ref Color32[] sent,
            ref Texture2D? current,
            ref Texture2D? previous,
            WindowTrack track,
            out bool previousIsFresh)
        {
            previousIsFresh = false;

            // Read the possibly-null state through a local: testing `current` itself would
            // leave the field "maybe null" for the rest of the method, and the swap below
            // assigns it to `previous`.
            Texture2D? live = current;
            string channel = track == gasTrack ? "gas" : "mist";
            GasFieldDiagnostics.Put("upload.channel", channel);
            GasFieldDiagnostics.Put("upload.forceUpload", forceUpload);
            GasFieldDiagnostics.Put("upload.liveIsNull", live == null);
            GasFieldDiagnostics.Put("upload.coversWindow", CoversWindow(track));
            GasFieldDiagnostics.Put("upload.sameWindow", SameWindow(pixels, sent));
            if (!forceUpload && live != null && CoversWindow(track) && SameWindow(pixels, sent))
            {
                if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("UploadTexture " + channel
                    + ": SHORT-CIRCUIT, no upload this frame");
                GasFieldDiagnostics.Flag(channel == "gas" ? "gasUploaded" : "mistUploaded", false);
                return false;
            }

            Texture2D write;
            bool fresh = live == null;
            if (fresh)
            {
                write = textureA;
                previous = textureA;
                previousIsFresh = true;
            }
            else
            {
                write = current == textureA ? textureB : textureA;

                // A cross-fade is only sound between two textures baked from the *same*
                // window origin. Equal origins means the whole uv mapping is identical, so
                // mixing the two is mixing two pictures of the same place; a different
                // origin means the shader would have to carry two mappings (_Params6 and
                // _Params7/_Params8) that have to stay in exact lockstep with which texture
                // is bound where — and when they do not, the whole field is drawn at the
                // previous window's offset, i.e. exactly where it sat before the camera
                // moved, and it fades back out as `fade` climbs. Pinning the origin removes
                // that whole class of defect instead of chasing the mapping, at the price of
                // one hard step per publish while the window is moving — the content in the
                // overlap is the same picture anyway, so there is nothing to fade.
                bool sameOrigin = track.Valid
                    && track.MinX == rectMinX
                    && track.MinZ == rectMinZ;
                bool prevCovers = PrevCoversView(track);
                GasFieldDiagnostics.Flag("sameOrigin", sameOrigin);
                GasFieldDiagnostics.Flag("prevCoversView", prevCovers);
                GasFieldDiagnostics.Put("upload.sameOrigin", sameOrigin);
                GasFieldDiagnostics.Put("upload.prevCoversView", prevCovers);
                GasFieldDiagnostics.Put("upload.trackMinX", track.MinX);
                GasFieldDiagnostics.Put("upload.trackMinZ", track.MinZ);
                GasFieldDiagnostics.Put("upload.trackPrevMinX", track.PrevMinX);
                GasFieldDiagnostics.Put("upload.trackPrevMinZ", track.PrevMinZ);
                GasFieldDiagnostics.Put("upload.trackWinW", track.WinW);
                GasFieldDiagnostics.Put("upload.trackWinH", track.WinH);
                GasFieldDiagnostics.Put("upload.trackValid", track.Valid);
                GasFieldDiagnostics.Put("upload.rectMinX", rectMinX);
                GasFieldDiagnostics.Put("upload.rectMinZ", rectMinZ);

                // Coverage is the only thing that decides whether the old texture can be
                // blended from — *not* whether the origin happens to match.
                //
                // This used to require sameOrigin, and that one condition is what made the
                // layer jump on every pan and every zoom. A moving camera changes the
                // origin constantly, so the cross-fade was cut on essentially every upload
                // and each publish landed as a hard cut. At 60 fps the step between two
                // publishes is small enough to hide; at 20 fps it is three times larger,
                // which is exactly the "worse the lower the frame rate" report.
                //
                // _Params7 already carries the mapping of the window the previous texture
                // holds (set right next to _Params6, from gasTrack.PrevMin), so a pair with
                // different origins samples both halves correctly and the overlap — the same
                // world cells in both — blends to the same value. The earlier note here
                // about the two mappings drifting out of lockstep describes the state before
                // _Params7 existed.
                if (prevCovers)
                {
                    // Always the old texture, at whatever origin _Params7 describes.
                    previous = current;
                }
                else
                {
                    // The previous picture does not contain the view, so part of what this
                    // quad draws was never baked into it. Sampling it anyway — even through
                    // its own uv — reads the cleared ring, or with Clamp its edge texels,
                    // and the newly exposed strip blends in from the wrong picture for one
                    // refresh interval. The bake just filled the whole window, so cut:
                    // previous and current point at the same texture for this publish,
                    // which makes the fade a no-op.
                    previous = write;
                    previousIsFresh = true;
                    GasFieldDiagnostics.Flag("crossFadeCut", true);
                    if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("UploadTexture " + channel
                        + ": CROSS-FADE CUT (sameOrigin=" + sameOrigin
                        + " prevCovers=" + prevCovers + ")");

                    if (VisualTweaksLog.GasTraceEnabled)
                    {
                        VisualTweaksLog.GasTrace(
                            "cross-fade cut: previous window ("
                            + track.MinX + "," + track.MinZ + ") " + track.WinW + "x" + track.WinH
                            + (sameOrigin
                                ? " does not cover the view"
                                : " sits at another origin than the live window")
                            + ", view (" + viewMinX + "," + viewMinZ + ")-("
                            + viewMaxX + "," + viewMaxZ + ").");
                    }
                }
            }

            ClearOutsideWindow(pixels);
            write.SetPixels32(pixels);
            // No mip chain on the density texture: the wide field is its own texture.
            write.Apply(false, false);
            current = write;

            // The window the *previous* half holds, read before TrackWindows overwrites it.
            // The diagnostic copy below needs it: the previous texture was baked at this
            // window, not at the one this upload is writing.
            int prevWindowX = track.MinX;
            int prevWindowZ = track.MinZ;
            int prevWindowW = track.WinW;
            int prevWindowH = track.WinH;

            TrackWindows(track, previousIsFresh);
            GasFieldDiagnostics.Flag(channel == "gas" ? "gasUploaded" : "mistUploaded", true);
            GasFieldDiagnostics.Flag(channel == "gas" ? "gasPreviousFresh" : "mistPreviousFresh",
                previousIsFresh);
            GasFieldDiagnostics.Put("upload.previousIsFresh", previousIsFresh);
            GasFieldDiagnostics.Put("upload.write", write?.name ?? "null");
            GasFieldDiagnostics.Put("upload.previous", previous?.name ?? "null");
            GasFieldDiagnostics.Put("upload.current", current?.name ?? "null");
            GasFieldDiagnostics.Put("upload.trackMinXAfter", track.MinX);
            GasFieldDiagnostics.Put("upload.trackMinZAfter", track.MinZ);
            GasFieldDiagnostics.Put("upload.trackPrevMinXAfter", track.PrevMinX);
            GasFieldDiagnostics.Put("upload.trackPrevMinZAfter", track.PrevMinZ);
            Swap(ref pixels, ref sent);

            // Diagnostic bookkeeping for the composite metric. After the swap the two
            // aliases have traded places: `sent` now holds the bytes that were just
            // uploaded (what the shader samples as _GasTexCur), and `pixels` holds the
            // *other* buffer — the one uploaded one publish earlier, which is exactly what
            // `previous` now points at.
            //
            // This had them backwards. The shadow was copied from `sent` *after* the swap,
            // i.e. from the new picture, so the "previous" half of every reconstructed
            // composite was the current one: the metric could only ever report cur against
            // cur. That is why contentDiff was exactly 0.0000 for two hundred samples and
            // why the alignment kept finding no shift — it was blending a picture with
            // itself.
            if (GasFieldDiagnostics.Enabled && ReferenceEquals(track, gasTrack))
            {
                displayedGas = sent;
                if (previousIsFresh)
                {
                    // Both halves were pointed at the fresh texture (a cut), so "previous"
                    // is this upload.
                    CaptureShadow(sent, rectMinX, rectMinZ, winW, winH);
                }
                else
                {
                    CaptureShadow(pixels, prevWindowX, prevWindowZ, prevWindowW, prevWindowH);
                }

                // Record what this texture really holds, next to what TrackWindows is about
                // to claim it holds. The two are compared on the frame the pair is drawn.
                TexWindowRecord record = new TexWindowRecord
                {
                    Tex = write,
                    X = rectMinX,
                    Z = rectMinZ,
                    W = winW,
                    H = winH,
                };
                if (ReferenceEquals(write, gasSmoothA))
                {
                    gasWindowA = record;
                }
                else
                {
                    gasWindowB = record;
                }
            }

            return true;
        }

        /// <summary>
        /// The texture is larger than the baked window (capacity only grows, and it is
        /// rounded up plus grown geometrically), while each bake only writes the window
        /// rows. Mips are generated from the *whole* texture, so anything left in the ring
        /// outside the window is averaged into the coarse levels — and the wide field is
        /// sampled from exactly those levels. Left alone, the ring slowly fills with gas
        /// from wherever the window used to be, and the halo shows it. Clearing it makes
        /// the halo fade out at the window edge instead.
        /// </summary>
        /// <summary>
        /// Keeps a CPU copy of the gas staging buffer as it is uploaded, together with the
        /// window it was baked for. Called from UploadTexture *before* the ping-pong swap,
        /// so from the next frame on it describes `previous` — the half the shader samples
        /// through _Params7.
        /// </summary>
        private void CaptureShadow(Color32[] pixels, int originX, int originZ, int windowW, int windowH)
        {
            if (shadowPixels.Length != pixels.Length)
            {
                shadowPixels = new Color32[pixels.Length];
            }

            Array.Copy(pixels, shadowPixels, pixels.Length);
            shadowMinX = originX;
            shadowMinZ = originZ;
            shadowW = windowW;
            shadowH = windowH;
        }

        /// <summary>
        /// Rebuilds the sample set: the cells that actually hold gas, inside the view, with
        /// a stride that adapts to how much of the window is lit.
        ///
        /// A uniform grid is the wrong instrument for this field. An ordinary cloud lights
        /// 5% of the window, so a 24x24 grid over the view spends 95% of its points on
        /// empty cells: the mean delta is divided by 20 and the correlation is fitted to
        /// noise. Sampling the lit texels instead makes every point carry information, and
        /// the stride feedback keeps the count near the capacity at any zoom level.
        /// </summary>
        private void RebuildLattice(float realtime, float fade, out double contentSum, out int contentCount)
        {
            contentSum = 0.0;
            contentCount = 0;
            latticeRebuiltAt = realtime;
            latticeCount = 0;

            int cap = sampleX.Length;
            if (cap == 0 || winW <= 0 || winH <= 0)
            {
                return;
            }

            int stride = Mathf.Clamp(latticeStride, 1, 16);
            bool overflow = false;

            for (int z = 0; z < winH; z += stride)
            {
                int wz = rectMinZ + z;
                if (wz < viewMinZ || wz > viewMaxZ)
                {
                    continue;
                }

                for (int x = 0; x < winW; x += stride)
                {
                    int wx = rectMinX + x;
                    if (wx < viewMinX || wx > viewMaxX)
                    {
                        continue;
                    }

                    float cur = SampleGasAt(displayedGas, rectMinX, rectMinZ, winW, winH, wx, wz);
                    if (cur < LatticeFloor)
                    {
                        continue;
                    }

                    if (latticeCount >= cap)
                    {
                        overflow = true;
                        break;
                    }

                    float prev = SampleGasAt(shadowPixels, shadowMinX, shadowMinZ, shadowW, shadowH, wx, wz);
                    if (fade < 0.999f && fade > 0.001f)
                    {
                        contentSum += Math.Abs(cur - prev);
                        contentCount++;
                    }

                    sampleX[latticeCount] = wx;
                    sampleZ[latticeCount] = wz;
                    screenComposited[latticeCount] = prev + (cur - prev) * fade;
                    screenCurrent[latticeCount] = cur;
                    screenHalo[latticeCount] = WideAt(wx, wz);
                    latticeCount++;
                }

                if (overflow)
                {
                    break;
                }
            }

            // Feedback, so the count lands near the capacity instead of on one side of it:
            // too many lit cells and the scan stops early (a biased corner of the view),
            // too few and most of the alignment's points are wasted on empty space.
            if (overflow)
            {
                latticeStride = Mathf.Min(16, latticeStride + 1);
            }
            else if (latticeCount < cap / 3)
            {
                latticeStride = Mathf.Max(1, latticeStride - 1);
            }
        }

        /// <summary>
        /// One density sample at a world cell, through the same index convention the bake
        /// writes and LogGasSignature reads (texture row 0 holds the north edge of the
        /// window). Outside the window the shader reads the cleared ring, which is zero.
        /// </summary>
        private float SampleGasAt(Color32[] pixels, int originX, int originZ, int windowW, int windowH,
            int worldX, int worldZ)
        {
            int x = worldX - originX;
            int z = worldZ - originZ;
            if (x < 0 || z < 0 || x >= windowW || z >= windowH
                || width <= 0 || x >= width || z >= height)
            {
                return 0f;
            }

            Color32 c = pixels[(height - 1 - z) * width + x];
            // Max over the channels: four gas types share the texture, and a jump in any
            // one of them is a visible jump.
            int m = c.r;
            if (c.g > m) m = c.g;
            if (c.b > m) m = c.b;
            if (c.a > m) m = c.a;
            return m * Inv255;
        }

        /// <summary>
        /// The composite the shader would draw at one world cell: the same mix of the same
        /// two textures with the same mappings _Params6/_Params7 carry.
        /// </summary>
        /// <summary>
        /// The wide field at a world cell: the mean of the 8x8 block that contains it,
        /// grouped exactly the way BakeWide groups it — blocks start at the window origin,
        /// not at a world multiple of eight. Written this way on purpose: reproducing the
        /// shader's own grouping is the only way to tell "the density changed" from "the
        /// block grid moved", and the grid is the one thing that used to move on its own.
        /// </summary>
        private float WideAt(int worldX, int worldZ)
        {
            int blocksX = winW / WideDownsample;
            int blocksZ = winH / WideDownsample;
            if (blocksX <= 0 || blocksZ <= 0)
            {
                return 0f;
            }

            int bx = Mathf.Clamp((worldX - rectMinX) / WideDownsample, 0, blocksX - 1);
            int bz = Mathf.Clamp((worldZ - rectMinZ) / WideDownsample, 0, blocksZ - 1);
            int cellX = rectMinX + bx * WideDownsample;
            int cellZ = rectMinZ + bz * WideDownsample;

            float sum = 0f;
            for (int z = 0; z < WideDownsample; z++)
            {
                for (int x = 0; x < WideDownsample; x++)
                {
                    sum += SampleGasAt(displayedGas, rectMinX, rectMinZ, winW, winH, cellX + x, cellZ + z);
                }
            }

            return sum / (WideDownsample * WideDownsample);
        }

        private float CompositeAt(int worldX, int worldZ, float fade)
        {
            float cur = SampleGasAt(displayedGas, rectMinX, rectMinZ, winW, winH, worldX, worldZ);
            float prev = SampleGasAt(shadowPixels, shadowMinX, shadowMinZ, shadowW, shadowH, worldX, worldZ);
            return prev + (cur - prev) * fade;
        }

        /// <summary>
        /// Rebuilds the composite the shader is about to draw and diffs it against the one
        /// it drew last frame.
        ///
        /// Two numbers matter, and the second one is the one that answers the report:
        ///   screen.deltaMean / deltaMax  how far the drawn picture moved at a fixed offset
        ///   align.dx / align.dz          the offset that best matches last frame's picture
        ///
        /// The absolute delta is useless here: this field is faint (a lit cell is 7/255 on
        /// average), so moving the whole cloud one cell changes the mean by 0.0004 — below
        /// any threshold that would not also fire on ordinary evolution. The alignment is
        /// scale-free: it asks "which shift makes this frame look like the last one", so a
        /// one-frame displacement shows up as a nonzero (dx,dz) with a large gain even when
        /// the picture barely changes in magnitude.
        /// </summary>
        private void SampleComposite(float realtime, float fade, double sincePublish)
        {
            if (!GasFieldDiagnostics.Enabled || width <= 0 || height <= 0
                || displayedGas.Length != width * height
                || shadowPixels.Length != width * height)
            {
                return;
            }

            // How far each of the four time-varying shader inputs moved since the previous
            // drawn frame. Everything else the fragment shader reads is constant or derived
            // from the window, so if the picture stepped and none of these did, the step did
            // not come from a uniform.
            if (prevFade < 0f)
            {
                deltaFade = 0f;
                deltaCellPixels = 0f;
                deltaDetail = 0f;
                deltaOff = 0f;
            }
            else
            {
                deltaFade = Mathf.Abs(liveFade - prevFade);
                deltaCellPixels = liveCellPixels - prevCellPixels;
                deltaDetail = liveDetail - prevDetail;
                float ox = liveOffX - prevOffX;
                float oz = liveOffZ - prevOffZ;
                deltaOff = Mathf.Sqrt(ox * ox + oz * oz);
            }

            prevFade = liveFade;
            prevCellPixels = liveCellPixels;
            prevDetail = liveDetail;
            prevOffX = liveOffX;
            prevOffZ = liveOffZ;

            int n = ScreenGrid * ScreenGrid;
            if (screenComposited.Length != n || screenSize != width * height)
            {
                // A texture resize reallocates every buffer, so the snapshot describes a
                // different coordinate system; start over instead of comparing across it.
                screenSize = width * height;
                screenComposited = new float[n];
                screenCurrent = new float[n];
                screenHalo = new float[n];
                screenWas = new float[n];
                sampleX = new int[n];
                sampleZ = new int[n];
                latticeCount = 0;
                screenPrimed = false;
            }

            double sum = 0.0;
            float max = 0f;
            double contentSum = 0.0;
            int contentCount = 0;
            int changed = 0;
            int compared = 0;
            double curSum = 0.0;
            double haloSum = 0.0;
            int count = latticeCount;

            // Pass 1: re-sample the *same world cells* the previous frame sampled, so a
            // camera pan contributes no delta at all, and so the comparison is between two
            // pictures of the same place.
            if (screenPrimed && count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    screenWas[i] = -1f;
                }

                for (int i = 0; i < count; i++)
                {
                    int wx = sampleX[i];
                    int wz = sampleZ[i];
                    if (wx < viewMinX || wx > viewMaxX || wz < viewMinZ || wz > viewMaxZ)
                    {
                        continue;
                    }

                    float curNow = SampleGasAt(displayedGas, rectMinX, rectMinZ, winW, winH, wx, wz);
                    float prevNow = SampleGasAt(shadowPixels, shadowMinX, shadowMinZ, shadowW, shadowH, wx, wz);
                    float mixed = prevNow + (curNow - prevNow) * fade;

                    curSum += Mathf.Abs(curNow - screenCurrent[i]);
                    float haloNow = WideAt(wx, wz);
                    haloSum += Mathf.Abs(haloNow - screenHalo[i]);
                    screenHalo[i] = haloNow;
                    float delta = Mathf.Abs(mixed - screenComposited[i]);
                    screenWas[i] = screenComposited[i];
                    screenComposited[i] = mixed;
                    screenCurrent[i] = curNow;
                    compared++;
                    sum += delta;
                    if (delta > max)
                    {
                        max = delta;
                    }

                    if (delta >= 0.02f)
                    {
                        changed++;
                    }
                }
            }

            // Pass 1b: which shift of the current picture best reproduces last frame's?
            //
            // This is the measurement that matches the report ("it jumps to another place
            // for one frame and comes back"). It is a normalized L1 match, so it does not
            // care how faint the field is: what matters is that the pattern is where the
            // pattern was. Runs before the lattice is rebuilt, which overwrites what it
            // compares against.
            int alignX = 0;
            int alignZ = 0;
            float alignGain = 0f;
            float alignErr = 0f;
            bool alignEdge = false;
            if (compared >= MinAlignPoints)
            {
                float bestErr = float.MaxValue;
                float zeroErr = 0f;
                for (int dz = -AlignRadius; dz <= AlignRadius; dz++)
                {
                    for (int dx = -AlignRadius; dx <= AlignRadius; dx++)
                    {
                        double err = 0.0;
                        double norm = 0.0;
                        for (int i = 0; i < count; i++)
                        {
                            float was = screenWas[i];
                            if (was < 0f)
                            {
                                continue;
                            }

                            float c = CompositeAt(sampleX[i] + dx, sampleZ[i] + dz, fade);
                            err += Math.Abs(c - was);
                            norm += c + was;
                        }

                        if (norm <= 1e-9)
                        {
                            continue;
                        }

                        float e = (float)(err / norm);
                        if (dx == 0 && dz == 0)
                        {
                            zeroErr = e;
                        }

                        if (e < bestErr)
                        {
                            bestErr = e;
                            alignX = dx;
                            alignZ = dz;
                        }
                    }
                }

                alignErr = bestErr;
                if (zeroErr > 1e-6f)
                {
                    alignGain = 1f - bestErr / zeroErr;
                }

                alignEdge = Mathf.Abs(alignX) == AlignRadius || Mathf.Abs(alignZ) == AlignRadius;
            }

            // Pass 2: rebuild the sample set from the current picture — but only at cells
            // that actually hold gas.
            //
            // This is the third attempt at the sampling and the first that is not blind. A
            // uniform 24x24 grid over the view hits ~5% lit cells on an ordinary cloud, so
            // the mean delta is diluted by an order of magnitude and the correlation has
            // almost no points to work on; measured on the last two logs, the absolute
            // delta never exceeded 0.007 and the alignment never even ran. Sampling the
            // lit texels themselves makes the same 576 points carry ~20x the signal.
            //
            // Rebuilt on a timer rather than every frame: the set has to be fixed between
            // the frame that records it and the frame that compares against it, and the
            // scan is the expensive part.
            if (count == 0 || realtime - latticeRebuiltAt >= LatticeSeconds)
            {
                RebuildLattice(realtime, fade, out contentSum, out contentCount);
                count = latticeCount;
            }
            else if (fade < 0.999f && fade > 0.001f)
            {
                // Content diff on the fixed set, for the frames in between.
                for (int i = 0; i < count; i++)
                {
                    int wx = sampleX[i];
                    int wz = sampleZ[i];
                    if (wx < viewMinX || wx > viewMaxX || wz < viewMinZ || wz > viewMaxZ)
                    {
                        continue;
                    }

                    float cur = SampleGasAt(displayedGas, rectMinX, rectMinZ, winW, winH, wx, wz);
                    float prev = SampleGasAt(shadowPixels, shadowMinX, shadowMinZ, shadowW, shadowH, wx, wz);
                    contentSum += Math.Abs(cur - prev);
                    contentCount++;
                }
            }

            float mean = compared > 0 ? (float)(sum / compared) : -1f;
            lastHaloDelta = compared > 0 ? (float)(haloSum / compared) : 0f;
            float content = contentCount > 0 ? (float)(contentSum / contentCount) : 0f;
            GasFieldDiagnostics.Put("screen.deltaMean", mean);
            GasFieldDiagnostics.Put("screen.deltaMax", max);
            GasFieldDiagnostics.Put("screen.changed", compared > 0 ? changed / (float)compared : 0f);
            GasFieldDiagnostics.Put("screen.compared", compared);
            GasFieldDiagnostics.Put("screen.samples", count);
            GasFieldDiagnostics.Put("screen.latticeStride", latticeStride);
            GasFieldDiagnostics.Put("screen.contentDiff", content);
            GasFieldDiagnostics.Put("cur.deltaMean", compared > 0 ? (float)(curSum / compared) : -1f);

            // How far the *halo* moved. The halo is an 8x8 block average anchored at the
            // window origin, so it is the one quantity that changes when the window origin
            // lands on a different block — even though not a single density value did. Read
            // together with wide.offsetX/Z this separates "the cell data moved" from "only
            // the block grid moved", which is the difference the eye reports as the gas's
            // edge stepping while the cloud stays still.
            GasFieldDiagnostics.Put("halo.deltaMean", compared > 0 ? (float)(haloSum / compared) : -1f);
            GasFieldDiagnostics.Put("screen.prevEqualsCur", ReferenceEquals(gasCurrent, gasPrevious));
            GasFieldDiagnostics.Put("align.dx", alignX);
            GasFieldDiagnostics.Put("align.dz", alignZ);
            GasFieldDiagnostics.Put("align.gain", alignGain);
            GasFieldDiagnostics.Put("align.err", alignErr);

            if (!screenPrimed || compared < MinAlignPoints)
            {
                screenPrimed = true;
                return;
            }

            // Two independent ways of saying "the picture jumped": it moved to another
            // place (align), or it changed in place beyond anything the fade could hide
            // (deltaMean). Both write a line the moment they happen with everything needed
            // to attribute them, instead of waiting for some watched key to change and
            // reconstructing afterwards.
            bool shifted = (Mathf.Abs(alignX) + Mathf.Abs(alignZ) >= 1) && alignGain > AlignGain;
            bool stepped = mean >= ScreenJumpMean;
            bool peak = GasFieldDiagnostics.IsPeak(mean);

            // Second ranking, independent of the density: how big a step one of the *other*
            // inputs took. A jump caused by the noise pattern or by a zoom lands here even
            // though the density picture barely moves, and would otherwise never be written.
            float other = Mathf.Max(
                deltaOff / 0.05f,
                Mathf.Max(Mathf.Abs(deltaCellPixels) / 2f, Mathf.Abs(deltaDetail) / 0.2f));
            bool peakOther = GasFieldDiagnostics.IsPeak(mean) || other >= PeakOtherScore;

            if (shifted || stepped || peak || peakOther)
            {
                string description = DescribeFrame(
                    shifted ? "SHIFT" : "STEP", realtime, alignX, alignZ, alignGain, alignEdge,
                    compared, mean, max, content,
                    compared > 0 ? (float)(curSum / compared) : 0f, fade, sincePublish);

                if (shifted || stepped)
                {
                    GasFieldDiagnostics.Jump(description);
                }

                // Worst frame of the last few seconds, kept even when it trips no threshold
                // at all: that is what keeps a run from coming back with nothing to read.
                GasFieldDiagnostics.Peak(mean, peak ? description : null);
                GasFieldDiagnostics.PeakOther(other, peakOther ? description : null);
            }
            else
            {
                GasFieldDiagnostics.Peak(mean, null);
                GasFieldDiagnostics.PeakOther(other, null);
            }

            screenPrimed = true;
        }

        /// <summary>
        /// Projects the quad's two opposite corners to screen pixels and records the box.
        /// Cheap (two transforms), and it is the only probe that reports where the draw
        /// actually went rather than what it was asked to contain.
        /// </summary>
        private void RecordQuadProjection()
        {
            projectValid = false;

            Camera? cam = Find.Camera;
            if (cam == null || mesh == null || quadWidth <= 0 || quadHeight <= 0)
            {
                return;
            }

            float altitude = QuadVertices[0].y;
            Vector3 a = cam.WorldToScreenPoint(new Vector3(quadMinX, altitude, quadMinZ));
            Vector3 b = cam.WorldToScreenPoint(
                new Vector3(quadMinX + quadWidth, altitude, quadMinZ + quadHeight));
            if (a.z <= 0f || b.z <= 0f)
            {
                return;
            }

            projectMinX = Mathf.Min(a.x, b.x);
            projectMinY = Mathf.Min(a.y, b.y);
            projectMaxX = Mathf.Max(a.x, b.x);
            projectMaxY = Mathf.Max(a.y, b.y);
            projectValid = true;

            GasFieldDiagnostics.Put("project.x0", projectMinX);
            GasFieldDiagnostics.Put("project.y0", projectMinY);
            GasFieldDiagnostics.Put("project.w", projectMaxX - projectMinX);
            GasFieldDiagnostics.Put("project.h", projectMaxY - projectMinY);

            // --- did the layer slide relative to the world? ------------------------
            //
            // This is a differential test and it is the only one here that cannot be
            // fooled by the renderer's own bookkeeping: it compares how far the quad moved
            // on screen with how far the *camera* moved, both in the same frame. The quad
            // sits at fixed world coordinates, so its screen motion must be exactly the
            // camera's motion, scaled by the zoom. Any leftover is the layer sliding over
            // the terrain — which is precisely what "it jumps to another place for one
            // frame" looks like, and it needs no assumption about textures, uniforms or
            // window bookkeeping to measure.
            //
            // Zoom frames are excluded: a changing cell size also moves the projected box,
            // so the difference would not be a slide.
            // A "did the quad slide over the terrain" differential was tried here and thrown
            // away: the projected box is computed *from* the camera, so the quad's screen
            // motion is the camera's motion by construction and the difference can only
            // ever be zero — or, as it did once, the quad's own world movement (a window
            // recentre) misread as a two-cell slide. It was circular. The only non-circular
            // question is what the shader samples at each pixel, which SampleComposite
            // answers directly against the same arrays and mappings the shader uses.
            Vector3 camPos = cam.transform.position;
            float cellPx = (projectMaxX - projectMinX) / Mathf.Max(1, quadWidth);
            GasFieldDiagnostics.Put("project.cellPx", cellPx);
            GasFieldDiagnostics.Put("project.camX", camPos.x);
            GasFieldDiagnostics.Put("project.camZ", camPos.z);
        }

        /// <summary>
        /// The one-line record of a measured frame. Shared by the immediate JUMP lines and
        /// the periodic PEAK record so both carry exactly the same fields.
        /// </summary>
        private string DescribeFrame(
            string tag, float realtime, int dx, int dz, float gain, bool edge, int points,
            float mean, float max, float content, float curDelta, float fade, double sincePublish)
        {
            return "frame=" + Time.frameCount.ToString(CultureInfo.InvariantCulture)
                + " t=" + realtime.ToString("F2", CultureInfo.InvariantCulture)
                + " " + tag
                + " dx=" + dx + " dz=" + dz
                + " gain=" + gain.ToString("F2", CultureInfo.InvariantCulture)
                + (edge ? "(edge)" : "")
                + " pts=" + points
                + " deltaMean=" + mean.ToString("F4", CultureInfo.InvariantCulture)
                + " deltaMax=" + max.ToString("F3", CultureInfo.InvariantCulture)
                + " content=" + content.ToString("F3", CultureInfo.InvariantCulture)
                + " fade=" + fade.ToString("F3", CultureInfo.InvariantCulture)
                + " since=" + sincePublish.ToString("F3", CultureInfo.InvariantCulture)
                + " cur=(" + rectMinX + "," + rectMinZ + ") " + winW + "x" + winH
                + " prev=(" + shadowMinX + "," + shadowMinZ + ") " + shadowW + "x" + shadowH
                + " view=(" + viewMinX + "," + viewMinZ + ")-(" + viewMaxX + "," + viewMaxZ + ")"
                + " rectMoved=" + GasFieldDiagnostics.FlagValue("rectMoved")
                + " cut=" + GasFieldDiagnostics.FlagValue("crossFadeCut")
                + " timerDue=" + GasFieldDiagnostics.FlagValue("timerDue")
                + " curD=" + curDelta.ToString("F4", CultureInfo.InvariantCulture)
                + " inc=" + GasFieldDiagnostics.FlagValue("incremental")
                + " cleared=" + GasFieldDiagnostics.FlagValue("knownCleared")
                + " ref=" + GasFieldDiagnostics.FlagValue("refreshed")
                + " uvStale=" + GasFieldDiagnostics.FlagValue("uvStale")
                + " dFade=" + deltaFade.ToString("F2", CultureInfo.InvariantCulture)
                + " dPx=" + deltaCellPixels.ToString("F2", CultureInfo.InvariantCulture)
                + " dDet=" + deltaDetail.ToString("F3", CultureInfo.InvariantCulture)
                + " dOff=" + deltaOff.ToString("F3", CultureInfo.InvariantCulture)
                + " halo=" + lastHaloDelta.ToString("F4", CultureInfo.InvariantCulture)
                + " curMismatch=" + GasFieldDiagnostics.FlagValue("curWindowMismatch")
                + " prevMismatch=" + GasFieldDiagnostics.FlagValue("prevWindowMismatch")
                + (projectValid
                    ? " proj=" + projectMinX.ToString("F1", CultureInfo.InvariantCulture)
                        + "," + projectMinY.ToString("F1", CultureInfo.InvariantCulture)
                        + " " + (projectMaxX - projectMinX).ToString("F1", CultureInfo.InvariantCulture)
                        + "x" + (projectMaxY - projectMinY).ToString("F1", CultureInfo.InvariantCulture)
                    : " proj=-");
        }

        /// <summary>
        /// vWorld -> uv for a window whose south-west corner is (originX, originZ).
        /// Both textures of a pair share the capacity, so only the offset differs.
        /// </summary>
        private Vector4 WindowMap(int originX, int originZ)
        {
            return new Vector4(
                1f / width,
                1f / height,
                -originX / (float)width,
                -originZ / (float)height);
        }

        /// <summary>
        /// Whether the live texture can be reused for the current window without an upload.
        ///
        /// The origin has to be *equal*, not merely contained. Texel (0,0) is the window
        /// origin, and the shader derives uv from the current origin (=_Params6), so a
        /// texture baked from a different origin is drawn displaced by exactly that
        /// difference — the whole field jumps sideways. Containment was the old test, and a
        /// window that shrank while its origin moved (view clipped against a map border, or
        /// a zoom step that crosses a bucket) satisfies it while being anchored somewhere
        /// else; if the pixels happened to compare equal the upload was skipped and the
        /// field stayed displaced until the next real publish. Reproduced offline: 16 cells,
        /// 1344 of 1920 sampled cells wrong.
        ///
        /// With equal origins the content is a subset of what the texture holds, at the same
        /// offsets, so skipping is sound.
        /// </summary>
        private bool CoversWindow(WindowTrack track)
        {
            return track.Valid
                && track.MinX == rectMinX
                && track.MinZ == rectMinZ
                && track.WinW >= winW
                && track.WinH >= winH;
        }

        /// <summary>
        /// Whether the window the previous texture was baked from contains the whole view.
        ///
        /// The caller has already established that the previous texture sits at the same
        /// origin as the live one, so this is purely a size test: the fade is only meaningful
        /// when the old window still answers for every cell the quad draws. What it cannot
        /// survive is a view that reaches outside that window — there is no data there, and
        /// Clamp turns uv &lt; 0 into the window's own left edge column. A one-frame camera
        /// move larger than PanSlack (a long first frame, a teleport, a fast zoom) would
        /// smear the old edge across the newly exposed area until the fade reached 1. The
        /// shader masks those samples to zero as well; this test decides whether the fade
        /// is worth starting at all.
        /// </summary>
        private bool PrevCoversView(WindowTrack track)
        {
            return track.Valid
                && track.MinX <= viewMinX
                && track.MinZ <= viewMinZ
                && track.MinX + track.WinW - 1 >= viewMaxX
                && track.MinZ + track.WinH - 1 >= viewMaxZ;
        }

        /// <summary>
        /// Remembers which baked window the live texture holds, so the upload shortcut can
        /// tell whether that texture still answers for the current one.
        /// </summary>
        private void TrackWindows(WindowTrack track, bool previousIsFresh)
        {
            // `previous` is the texture that was live until this upload, so it holds the
            // window this record described before the update. When it was just pointed at
            // the fresh texture instead, both halves hold the current window.
            if (previousIsFresh || !track.Valid)
            {
                track.PrevMinX = rectMinX;
                track.PrevMinZ = rectMinZ;
            }
            else
            {
                track.PrevMinX = track.MinX;
                track.PrevMinZ = track.MinZ;
            }

            track.MinX = rectMinX;
            track.MinZ = rectMinZ;
            track.WinW = winW;
            track.WinH = winH;
            track.Valid = true;
        }

        private void ClearOutsideWindow<T>(T[] pixels)
        {
            int top = height - winH;
            if (top > 0)
            {
                Array.Clear(pixels, 0, top * width);
            }

            int side = width - winW;
            if (side > 0)
            {
                for (int row = top; row < height; row++)
                {
                    Array.Clear(pixels, row * width + winW, side);
                }
            }
        }

        private static void Swap<T>(ref T[] a, ref T[] b)
        {
            T[] swap = a;
            a = b;
            b = swap;
        }

        /// <summary>
        /// Compares only the baked window rows. The ring between the window and the
        /// texture capacity is stale on purpose and never sampled, so comparing it would
        /// cost bandwidth (and fail spuriously) for nothing.
        /// </summary>
        private bool SameWindow<T>(T[] pixels, T[] sent) where T : struct
        {
            if (sent == null || pixels.Length != sent.Length)
            {
                return false;
            }

            for (int z = 0; z < winH; z++)
            {
                int offset = (height - 1 - z) * width;
                if (!MemoryMarshal.AsBytes(pixels.AsSpan(offset, winW))
                        .SequenceEqual(MemoryMarshal.AsBytes(sent.AsSpan(offset, winW))))
                {
                    return false;
                }
            }

            return true;
        }

        private void EnsureQuad(Mesh mesh)
        {
            if (quadMinX == rectMinX
                && quadMinZ == rectMinZ
                && quadWidth == winW
                && quadHeight == winH)
            {
                return;
            }

            quadMinX = rectMinX;
            quadMinZ = rectMinZ;
            quadWidth = winW;
            quadHeight = winH;

            int minX = rectMinX;
            int maxX = rectMinX + winW;
            int minZ = rectMinZ;
            int maxZ = rectMinZ + winH;

            float altitude = Altitudes.AltitudeFor((AltitudeLayer)29);
            QuadVertices[0] = new Vector3(minX, altitude, minZ);
            QuadVertices[1] = new Vector3(minX, altitude, maxZ);
            QuadVertices[2] = new Vector3(maxX, altitude, maxZ);
            QuadVertices[3] = new Vector3(maxX, altitude, minZ);

            mesh.Clear(false);
            mesh.SetVertices(QuadVertices);
            mesh.SetTriangles(QuadTriangles, 0);
            mesh.bounds = new Bounds(
                new Vector3((minX + maxX) * 0.5f, altitude, (minZ + maxZ) * 0.5f),
                new Vector3(maxX - minX, 16f, maxZ - minZ));
        }

        /// <summary>
        /// Copies the active window out of a map-indexed array into a same-indexed
        /// scratch buffer. Rows outside the window are left untouched and never read.
        /// </summary>
        private void CopyWindow(float[] source, float[] destination)
        {
            for (int z = 0; z < winH; z++)
            {
                int offset = (rectMinZ + z) * mapWidth + rectMinX;
                Array.Copy(source, offset, destination, offset, winW);
            }
        }

        private void DisposeTextures()
        {
            DestroyTexture(ref gasWideA);
            DestroyTexture(ref gasWideB);
            DestroyTexture(ref gasSmoothA);
            DestroyTexture(ref gasSmoothB);
            DestroyTexture(ref mistSmoothA);
            DestroyTexture(ref mistSmoothB);
            smoothInitialized = false;
            gasCurrent = null;
            gasPrevious = null;
            mistCurrent = null;
            mistPrevious = null;
            gasWideCurrent = null;
            gasWidePrevious = null;
        }

        private static void DestroyTexture(ref Texture2D? texture)
        {
            if (texture != null)
            {
                UnityEngine.Object.Destroy(texture);
                texture = null;
            }
        }

        public void Dispose()
        {
            DisposeTextures();

            // The flow texture/history is capacity-independent, so it intentionally
            // survives EnsureResources' texture reallocation and is only released here.
            DestroyTexture(ref windTexture);
            windUpload = Array.Empty<float>();
            windSent = Array.Empty<float>();
            // Clearing the arrays alone is not enough: EnsureFlowGrid's cache test also
            // looks at the recorded grid size, and 0 x 0 can never match a real map.
            flowDx = Array.Empty<float>();
            flowCellsX = 0;
            flowCellsZ = 0;
            flowDz = Array.Empty<float>();
            flowVx = Array.Empty<float>();
            flowVz = Array.Empty<float>();
            flowUx = Array.Empty<float>();
            flowUz = Array.Empty<float>();
            flowPrevDx = Array.Empty<float>();
            flowPrevDz = Array.Empty<float>();
            flowBlurScratch = Array.Empty<float>();
            baseDx = 0.0;
            baseDz = 0.0;
            flowInitialized = false;
            lastFlowSteps = int.MinValue;
            lastFlowTime = double.NaN;

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
        }
    }
}
