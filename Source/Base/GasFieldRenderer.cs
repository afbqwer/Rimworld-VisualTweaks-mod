using System;
using System.Diagnostics.CodeAnalysis;
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
        // themselves run faster at 2x/3x. Both the refresh timer and the cross-fade use
        // it: two textures plus exactly one period of fade is the only combination that
        // neither freezes between publishes nor lets a third snapshot overlap the pair.
        private const float DefaultRefreshSeconds = 0.1f;

        // Below ~50 ms the bake cost stops paying for itself (the screen cannot show more
        // than one update per frame anyway); above a second the delay becomes obvious.
        private const float MinPublishPeriod = 0.05f;
        private const float MaxPublishPeriod = 1f;
        private const float DetailFadeStartPixels = 8f;
        private const float DetailFadeRangePixels = 18f;

        private const float Inv255 = 1f / 255f;

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

        // Cells baked beyond the view on each side. The slack is what stops the bake
        // from running on every frame of a slow pan: the view can drift
        // BakeMargin - PanSlack cells before the rect has to move.
        private const int BakeMargin = 17;
        private const int PanSlack = 9;

        // Capacity is rounded up to this many cells so sub-cell camera jitter never
        // changes it, and doubled instead of matched exactly so a zoom-out gesture
        // needs a couple of allocations at most.
        private const int CapacityQuantum = 16;

        // The window only steps in 8-cell increments. Rounding *up* is free (the extra
        // ring is outside the view), and it turns a zoom tween from "one upload per
        // frame" into "one upload per 8 cells", which matters because every upload
        // rebuilds the GPU image.
        private const int WindowQuantum = 8;

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
        private float lastRefresh = float.NegativeInfinity;
        private float fadeStart = float.NegativeInfinity;

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

            // The smoothing toggle only takes effect on the next bake, so force one
            // when it changes (the options window can be open while the game is paused).
            bool smoothChanged = VisualTweaksSettings.gasFieldSmooth != builtSmooth;

            // Switching the noise mode changes what the fragment shader samples, and the
            // local mode needs a fresh flow bake. Force one publish when it changes.
            bool noiseModeChanged =
                GasFieldBridge.EffectiveNoiseMode != builtNoiseMode;

            // Cross-fade progress. Both channels empty and fully faded means every fragment
            // would be discarded, so there is nothing to bake, upload or draw.
            float fade = paused || fadeStart < 0f
                ? 1f
                : Mathf.Clamp01((realtime - fadeStart) / refreshPeriod);

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
                return;
            }

            int previousMinX = rectMinX;
            int previousMinZ = rectMinZ;
            int previousWinW = winW;
            int previousWinH = winH;

            bool rectMoved = EnsureResources(map, view, out bool originMoved);
            EnsureFlowGrid();

            bool scrolled = false;

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
            bool timerDue = !paused && realtime - lastRefresh >= refreshPeriod;

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
                bool incremental = scrolled && !timerDue && !smoothChanged;

                Refresh(map, realtime, incremental);
            }

            if (rectMoved)
            {
                // No hard cut any more: the previous texture is sampled through its own
                // mapping (_Params7), so a moved window cross-fades correctly instead of
                // smearing the old region into the new one.
                if (VisualTweaksSettings.gasFieldTraceWindows
                    && realtime - lastWindowTrace > 0.15f)
                {
                    lastWindowTrace = realtime;
                    Log.Message(
                        "[Visual Tweaks] window "
                        + $"({rectMinX},{rectMinZ}) {winW}x{winH} of cap {width}x{height}, "
                        + $"view ({view.minX},{view.minZ})-({view.maxX},{view.maxZ})");
                }
            }

            // Refresh may just have cleared a channel and restarted the fade-out, so the
            // value computed above is stale. Recompute before deciding whether to draw.
            fade = paused || fadeStart < 0f
                ? 1f
                : Mathf.Clamp01((realtime - fadeStart) / refreshPeriod);

            if (gasClean && mistClean && fade >= 1f)
            {
                // Both channels are empty and the fade-out is over: skip the mesh update,
                // the uniform block and Graphics.DrawMesh. The first bake with content
                // brings the full path back.
                return;
            }

            EnsureQuad(mesh);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            // Cross-fade per frame: without it the 10 Hz publish would hold the same
            // image for several frames even when the values are smoothed.
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

            // The shader's time is the renderer-local clock, not RealTime.UnpausedRealTime:
            // the latter is one float for the whole application session, and after hours
            // its ulp is large enough to quantise this animation into visible micro-steps.
            float shaderTime = (float)shaderClock;
            material.SetVector(ParamsId, new Vector4(fade, 2.6f, opacity, shaderTime));
            material.SetVector(Params2Id, new Vector4(width, height, ambientVelX, ambientVelZ));
            // vWorld -> uv for the baked window: uv = vWorld * xy + zw. The shader used
            // to divide by the map size; now the texture only covers the window.
            //
            // vWorld -> uv for the baked window: uv = vWorld * xy + zw.
            material.SetVector(Params6Id, WindowMap(rectMinX, rectMinZ));
            // Each pair's previous half comes from an older window, so it needs the mapping
            // of the window *it* holds. Only the strip that left or entered at the window
            // edge is affected, and that strip sits in the margin outside the view — the
            // cross-fade's job is to smooth the 10 Hz step, which it can only do if the
            // publish that moved the window blends like any other.
            material.SetVector(
                Params7Id,
                WindowMap(
                    gasTrack.Valid ? gasTrack.PrevMinX : rectMinX,
                    gasTrack.Valid ? gasTrack.PrevMinZ : rectMinZ));
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

            Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0);

            if (!loggedDraw)
            {
                loggedDraw = true;
                Log.Message("[Visual Tweaks] Gas field renderer drawing "
                    + width + "x" + height + " density field.");
            }
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
        /// Most time one publish may integrate and the shader may extrapolate. It tracks
        /// the publish period because AO allows a 30-tick interval (0.5 s at 1x); a fixed
        /// 0.25 s bound would park the pattern halfway between those publishes. The 1.5
        /// factor absorbs frame jitter.
        /// </summary>
        private float FlowStepLimit =>
            Mathf.Min(1f, Mathf.Max(FlowMinStep, refreshPeriod * 1.5f));

        /// <summary>
        /// Advances the renderer-local game-time clock by one frame. Double precision and
        /// a zero origin keep the per-frame delta exact for the whole session, and the
        /// clock freezes with the game (and during a hitch) exactly like the solver does.
        /// </summary>
        private void AdvanceShaderClock(bool paused)
        {
            double now = Time.realtimeSinceStartupAsDouble;
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

                    shaderClock += delta * CurrentSimulationRate();
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

            // --- capacity: grow-only, geometric, clamped to the map -----------------
            int needW = Mathf.Min(mapWidth, viewW + 2 * BakeMargin);
            int needH = Mathf.Min(mapHeight, viewH + 2 * BakeMargin);
            needW = Mathf.Min(RoundUp(needW, CapacityQuantum), mapWidth);
            needH = Mathf.Min(RoundUp(needH, CapacityQuantum), mapHeight);

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
                gasSmoothState = new float[cellCount * 4];
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
                fadeStart = float.NegativeInfinity;
                winW = 0;
                winH = 0;

                rectMoved = true;
                originMoved = true;
            }

            EnsureCanvas();

            // --- window: what gets baked, follows the view exactly -------------------
            int wantWinW = Mathf.Min(width, RoundUp(viewW + 2 * BakeMargin, WindowQuantum));
            int wantWinH = Mathf.Min(height, RoundUp(viewH + 2 * BakeMargin, WindowQuantum));

            // Hysteresis, and it is load-bearing. CurrentViewRect is Floor/Ceil of a moving
            // float, so view.Width jitters by one cell while the camera pans. RoundUp with
            // no dead band turns that into an adjacent-bucket hop almost every frame when
            // the jitter straddles a bucket edge (measured 72 <-> 80 on 70% of frames at
            // some zoom levels). Every hop was a full rebake plus a GPU image rebuild, and
            // it also moved the window origin. Grow on contact, shrink only once the
            // surplus is a whole bucket.
            if (wantWinW > winW || wantWinW + WindowQuantum < winW)
            {
                winW = wantWinW;
                rectMoved = true;
            }

            if (wantWinH > winH || wantWinH + WindowQuantum < winH)
            {
                winH = wantWinH;
                rectMoved = true;
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

            bool viewInsideSlack =
                rectMinX + PanSlack <= view.minX
                && view.maxX <= rectMinX + winW - 1 - PanSlack
                && rectMinZ + PanSlack <= view.minZ
                && view.maxZ <= rectMinZ + winH - 1 - PanSlack;

            if (resize || !windowFitsMap || !viewInsideSlack)
            {
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

                // Snapping moves the origin down, eating into the far margin. That is still
                // >= 10 cells because winW >= viewW + 34, but against the east/south border
                // the clamp above already parked the origin at mapSize - win, which is not
                // block-aligned; snapping there would leave the last few visible cells
                // unbaked. Coverage wins, and it costs nothing: that origin is pinned by the
                // clamp, so it does not change while the camera pans along the border and
                // the block grid stays put anyway.
                minX = snappedX + winW > view.maxX ? snappedX : minX;
                minZ = snappedZ + winH > view.maxZ ? snappedZ : minZ;

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
        /// </summary>
        private void Refresh(Map map, float realtime, bool incremental)
        {
            bool wrote = BakeSmooth(map, realtime, incremental);

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
                if (wrote)
                {
                    fadeStart = realtime;
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

            // Fade every publish that actually produced a picture — including the ones that
            // moved the window: each texture carries its own mapping, and the only cells the
            // two disagree on sit in the margin outside the view.
            //
            // A publish that wrote nothing has nothing to fade *from*. Restarting the fade
            // there is not harmless: it re-exposes the previous texture, and while the gate
            // holds a channel empty that texture is the last one that had content — the
            // ghost. It also pinned the display near fade 0 on every frame the camera moved.
            if (wrote)
            {
                fadeStart = realtime;
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

            // Whatever was baked above covered this window at this origin, so every cell in
            // it now has history for the next blend.
            MarkKnownWindow();

            if (gasWritten)
            {
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

            int rowBase = wideH - blockH;
            float inv = 1f / (WideDownsample * WideDownsample * 255f);

            for (int by = 0; by < blockH; by++)
            {
                int outRow = (rowBase + by) * wideW;
                int srcRow = (height - winH + by * WideDownsample) * width;

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
            if (!newPublish
                && !double.IsNaN(lastFlowTime)
                && clockTime - lastFlowTime >= refreshPeriod * 1.5)
            {
                // CompletedSteps is resolved reflectively; if a future AO stops exposing
                // it, fall back to the configured publish period instead of freezing.
                newPublish = true;
            }
            lastFlowSteps = steps;
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
                return;
            }

            float k = flowInitialized && !reseed
                ? 1f - Mathf.Exp(-(float)dt / FlowVelocityTau)
                : 1f;
            flowInitialized = true;
            lastFlowTime = clockTime;

            float animation = Mathf.Clamp(VisualTweaksSettings.gasFieldAnimation, 0f, 2f);
            // Integrate with the velocity that was displayed during this interval, *then*
            // low-pass toward the new sample. Doing it in the other order would move the
            // pattern by (vNew - vOld) * dt at every publish — small, but visible as a
            // twitch during an explosion. dt is already in game-time seconds, so no extra
            // game-speed factor belongs here.
            float step = (float)dt * animation;
            int count = flowCellsX * flowCellsZ;

            if (step > 0f)
            {
                Array.Copy(flowDx, flowPrevDx, count);
                Array.Copy(flowDz, flowPrevDz, count);
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
                    if (step > 0f)
                    {
                        flowDx[i] += flowVx[i] * step;
                        flowDz[i] += flowVz[i] * step;
                    }

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

                // The shader extends the *realized* displacement of this interval, which
                // already includes the relaxation, so the publish boundary is continuous.
                float invStep = 1f / step;
                for (int i = 0; i < count; i++)
                {
                    flowUx[i] = (flowDx[i] - flowPrevDx[i]) * invStep;
                    flowUz[i] = (flowDz[i] - flowPrevDz[i]) * invStep;
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

            noisePublishTime = clockTime;
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

                if (incremental)
                {
                    // A movement-only publish: only the strip the window move exposed is
                    // unknown. Skipping the known cells inside the loop still walked every
                    // one of them; leaving them out of the runs is the actual saving.
                    if (rowKnown)
                    {
                        if (knownX0 > 0)
                        {
                            BakeGasRow(gasGrid, fogGrid, row, outputRow, 0, knownX0, 1f);
                        }

                        if (knownX1 < winW)
                        {
                            BakeGasRow(gasGrid, fogGrid, row, outputRow, knownX1, winW, 1f);
                        }
                    }
                    else
                    {
                        BakeGasRow(gasGrid, fogGrid, row, outputRow, 0, winW, 1f);
                    }

                    continue;
                }

                if (rowKnown)
                {
                    if (knownX0 > 0)
                    {
                        BakeGasRow(gasGrid, fogGrid, row, outputRow, 0, knownX0, 1f);
                    }

                    BakeGasRow(gasGrid, fogGrid, row, outputRow, knownX0, knownX1, k);

                    if (knownX1 < winW)
                    {
                        BakeGasRow(gasGrid, fogGrid, row, outputRow, knownX1, winW, 1f);
                    }
                }
                else
                {
                    BakeGasRow(gasGrid, fogGrid, row, outputRow, 0, winW, 1f);
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
                float r = 0f;
                float g = 0f;
                float b = 0f;
                float a = 0f;

                if (fogGrid == null || !fogGrid.IsFogged(index))
                {
                    uint packed = gasGrid.GetDirect(index);
                    // Multiply by the reciprocal rather than divide by 255: the shift
                    // is not a power of two, so every /255f was a real divss.
                    r = (packed & 0xFFu) * Inv255;
                    g = ((packed >> 8) & 0xFFu) * Inv255;
                    b = ((packed >> 16) & 0xFFu) * Inv255;
                    a = ((packed >> 24) & 0xFFu) * Inv255;
                }

                int cell = (outputRow + x) * 4;
                gasSmoothState[cell] += (r - gasSmoothState[cell]) * cellK;
                gasSmoothState[cell + 1] += (g - gasSmoothState[cell + 1]) * cellK;
                gasSmoothState[cell + 2] += (b - gasSmoothState[cell + 2]) * cellK;
                gasSmoothState[cell + 3] += (a - gasSmoothState[cell + 3]) * cellK;

                gasSmoothPixels[outputRow + x] = new Color32(
                    ToByte(gasSmoothState[cell]),
                    ToByte(gasSmoothState[cell + 1]),
                    ToByte(gasSmoothState[cell + 2]),
                    ToByte(gasSmoothState[cell + 3]));
            }
        }

        /// <summary>
        /// Diagnostic: compares the baked red channel with the solver at the same cell and at
        /// +/-8 cells on each axis, and logs the match rate plus the quad/uv the draw will
        /// use. A displacement of the texture shows up as a high match rate at a nonzero
        /// offset; a correct one as ~1.0 at offset zero.
        /// </summary>
        private void LogGasSignature(GasGrid gasGrid)
        {
            if (!VisualTweaksSettings.gasFieldTraceWindows
                || traceRealtime - lastSignatureLog < 0.5f)
            {
                return;
            }

            lastSignatureLog = traceRealtime;

            int nz = 0;
            int eq0 = 0, eqxp = 0, eqxm = 0, eqzp = 0, eqzm = 0;
            int stride = Mathf.Max(1, winW / 40);

            for (int z = 2; z < winH - 2; z += stride)
            {
                for (int x = 2; x < winW - 2; x += stride)
                {
                    int baked = gasSmoothPixels[(height - 1 - z) * width + x].r;
                    if (baked == 0)
                    {
                        continue;
                    }

                    nz++;
                    int index = (rectMinZ + z) * mapWidth + rectMinX + x;
                    if (index < 0 || index + 8 >= mapWidth * mapHeight)
                    {
                        continue;
                    }

                    if (baked == (int)(gasGrid.GetDirect(index) & 0xFFu)) eq0++;
                    if (baked == (int)(gasGrid.GetDirect(index + 8) & 0xFFu)) eqxp++;
                    if (baked == (int)(gasGrid.GetDirect(index - 8) & 0xFFu)) eqxm++;
                    if (baked == (int)(gasGrid.GetDirect(index + 8 * mapWidth) & 0xFFu)) eqzp++;
                    if (baked == (int)(gasGrid.GetDirect(index - 8 * mapWidth) & 0xFFu)) eqzm++;
                }
            }

            if (nz == 0)
            {
                return;
            }

            Log.Message(
                "[Visual Tweaks] sig win (" + rectMinX + "," + rectMinZ + ") " + winW + "x" + winH
                + " nz=" + nz
                + $" eq0={100f * eq0 / nz:F1}% eq+x={100f * eqxp / nz:F1}% eq-x={100f * eqxm / nz:F1}%"
                + $" eq+z={100f * eqzp / nz:F1}% eq-z={100f * eqzm / nz:F1}%"
                + " quad (" + quadMinX + "," + quadMinZ + ") " + quadWidth + "x" + quadHeight);
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

            Array.Clear(gasSmoothState, 0, gasSmoothState.Length);
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
                ShiftWindow(gasSmoothState, gasSmoothState, 4, dx, dz);
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
        /// A window resize keeps the origin, so the index of a given world cell is
        /// unchanged and the history is still valid — only the far edges went away.
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
        /// pair and whenever the old window no longer contains the view.
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
            if (!forceUpload && live != null && CoversWindow(track) && SameWindow(pixels, sent))
            {
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
                if (PrevCoversView(track))
                {
                    // Always the old texture: the shader samples it through _Params7, which
                    // carries the window it was baked from, so the two endpoints agree.
                    previous = current;
                }
                else
                {
                    // The previous window does not contain the view, so part of what this
                    // quad draws was never baked into it. Sampling it anyway — even through
                    // its own uv — reads the cleared ring, or with Clamp its edge texels,
                    // and the newly exposed strip blends in from the wrong picture for one
                    // refresh interval. The bake just filled the whole window, so cut:
                    // previous and current point at the same texture for this publish.
                    previous = write;
                    previousIsFresh = true;

                    if (VisualTweaksSettings.gasFieldTraceWindows)
                    {
                        Log.Message(
                            "[Visual Tweaks] previous window ("
                            + track.MinX + "," + track.MinZ + ") " + track.WinW + "x" + track.WinH
                            + " does not cover view (" + viewMinX + "," + viewMinZ + ")-("
                            + viewMaxX + "," + viewMaxZ + "); cross-fade skipped.");
                    }
                }
            }

            ClearOutsideWindow(pixels);
            write.SetPixels32(pixels);
            // No mip chain on the density texture: the wide field is its own texture.
            write.Apply(false, false);
            current = write;

            TrackWindows(track, previousIsFresh);
            Swap(ref pixels, ref sent);
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
        /// The previous half is sampled through _Params7/_Params8, so a moved window is
        /// normally fine: each texture carries its own origin. What it cannot survive is a
        /// view that reaches outside that window — there is no data there, and Clamp turns
        /// uv &lt; 0 into the window's own left edge column. A one-frame camera move larger
        /// than PanSlack (a long first frame, a teleport, a fast zoom) therefore used to
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
