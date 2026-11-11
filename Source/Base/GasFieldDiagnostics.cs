using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Deliberately expensive, throw-away diagnostics for the "whole gas layer flashes
    /// for one frame while the camera moves" investigation.
    ///
    /// The defect has several independent causes (layer skipped for a frame, previous
    /// texture rebound, uv mapping changed before the upload, noise mode / publish clock
    /// stepped, mesh rebuilt under a deferred draw). Patching one only ever suppressed
    /// one trigger, so this does NOT try to fix or gate anything: it records a full
    /// snapshot of every candidate quantity each frame, compares it with the previous
    /// frame, and when any of them changes it writes a self-contained report (trigger
    /// frame in full + a compact history of the preceding frames) to a file.
    ///
    /// No human has to spot the flash. Reproduce it once, send the log, and the report
    /// around that timestamp says which cause fired and in what order.
    ///
    /// TURN IT OFF after the investigation: set Enabled to false (or delete this file
    /// and its call sites). It allocates per frame and writes to disk on every event.
    /// </summary>
    internal static class GasFieldDiagnostics
    {
        // Live setting, read on every call so toggling the checkbox in the options
        // window takes effect on the very next frame, with no reload or restart. When
        // false, every sampling entry point returns immediately: no formatting, no
        // frame allocation, no disk IO. Flip the default in VisualTweaksSettings to
        // false before shipping.
        internal static bool Enabled => VisualTweaksSettings.gasFieldDiagnostics;

        internal static string LogPath =>
            Path.Combine(Path.GetTempPath(), "VisualTweaks_GasField_Flash.log");

        private const int HistoryFrames = 16;
        // A moving camera can produce ~20 events/s at ~10 KB each. Keep enough history
        // that a flash reproduced after several minutes is not rotated away.
        private const long MaxLogBytes = 64L * 1024 * 1024;
        private const int MaxRotations = 5;
        private const int SummaryEvery = 50;
        private const double MinEventSpacing = 0.05;

        // Any change in one of these keys between two frames can move the whole layer.
        private static readonly string[] WatchKeys =
        {
            "gate.active", "gate.screenshot", "gate.useGasField", "gate.shaderReady",
            "gate.gasClean", "gate.mistClean", "gate.drawGas",
            "window.rectMinX", "window.rectMinZ", "window.winW", "window.winH",
            "window.width", "window.height", "window.quadMinX", "window.quadMinZ",
            "track.gasMinX", "track.gasMinZ", "track.gasPrevMinX", "track.gasPrevMinZ",
            "track.gasWinW", "track.gasWinH", "track.gasValid",
            "track.mistMinX", "track.mistMinZ", "track.mistPrevMinX",
            "track.mistPrevMinZ", "track.mistValid",
            "tex.gasCurrent", "tex.gasPrevious", "tex.mistCurrent", "tex.mistPrevious",
            "tex.gasWideCurrent", "tex.gasWidePrevious", "tex.wind",
            "tex.gasPrevEqualsCur", "tex.mistPrevEqualsCur",
            "uniform.effectiveNoiseMode", "uniform.params6OriginX",
            "uniform.params6OriginZ", "uniform.params7OriginX", "uniform.params7OriginZ",
            "uniform.params8OriginX", "uniform.params8OriginZ", "uniform.opacity",
            "noise.supportsLocalWind", "noise.animation", "noise.flowCellsX",
            "noise.flowCellsZ", "clock.paused", "clock.timeSpeed",
            // clock.timerDue is deliberately absent: it is a per-publish 0/1 edge that
            // already arrives as FLAG:event.timerDue, and watching the value as well just
            // reported it twice (870 events out of 1531).
            "clock.noisePublishTime", "fade.fadeStart",
            "upload.previousIsFresh", "layer.active",
            // What the baked picture actually contains, as two scalars over the window.
            // The tex.* keys only prove that a pointer swap happened; these prove whether
            // the image underneath changed at all — the difference between a harmless
            // re-bind and the flash itself.
            "content.gasMean", "content.gasLit",
            "state.builtSmooth", "state.builtNoiseMode",
        };

        // A 0 -> 1 transition in one of these is itself an event worth reporting.
        private static readonly string[] FlagKeys =
        {
            "event.rectMoved", "event.originMoved", "event.scrolled", "event.resize",
            "event.quadRebuilt", "event.flowUploaded", "event.gasUploaded",
            "event.mistUploaded", "event.gasPreviousFresh", "event.mistPreviousFresh",
            "event.sameOrigin", "event.prevCoversView", "event.coversWindow",
            "event.crossFadeCut", "event.collapsePair", "event.forceUpload",
            "event.incremental", "event.timerDue", "event.smoothChanged",
            "event.noiseModeChanged", "event.refreshed", "event.fadeRestarted",
            "event.fadeGuardApplied",
            "event.newPublish", "event.reseed", "event.tryGetWindFailed",
            "event.flowGridRebuilt", "event.textureResize", "event.uvStale",
            "event.prevWindowMismatch", "event.sigMisplaced",
        };

        // Numeric discontinuities that are too large to be ordinary animation.
        private const double FadeJump = 0.35;
        private const double SinceReset = 0.02;
        private const double ClockJump = 0.10;
        private const double FlowUJump = 2.0;

        /// <summary>
        /// How far the shader's noise offset may move backwards, in noise units, before it
        /// counts as a teleport. It advances about 0.1 per publish, so 0.003 is well below
        /// any ordinary frame-to-frame step and well above float noise.
        /// </summary>
        private const double NoiseBack = 0.003;

        /// <summary>
        /// Mean screen-space step (0..1 of a density channel) that counts as a jump.
        /// Gas densities are small — a dense blast cell is 1.0 but an ordinary cloud sits
        /// well under 0.3 — so 0.035 across a 24x24 grid over the view is already a
        /// picture-wide shift, not a cell or two changing.
        /// </summary>
        private const double ScreenJump = 0.035;

        private sealed class Frame
        {
            public int Index;
            public double Realtime;
            public bool DrawMesh;
            public string SkipReason = "";
            public readonly List<KeyValuePair<string, string>> Fields =
                new List<KeyValuePair<string, string>>(96);
            public readonly List<string> Notes = new List<string>();

            public string? Get(string key)
            {
                for (int i = Fields.Count - 1; i >= 0; i--)
                {
                    if (Fields[i].Key == key)
                    {
                        return Fields[i].Value;
                    }
                }

                return null;
            }

            public string Compact()
            {
                StringBuilder sb = new StringBuilder(256);
                sb.Append('#').Append(Index)
                    .Append(" t=").Append(Realtime.ToString("F3", CultureInfo.InvariantCulture))
                    .Append(" draw=").Append(DrawMesh ? '1' : '0');
                if (SkipReason.Length > 0)
                {
                    sb.Append(" skip=").Append(SkipReason);
                }

                Append(sb, "fade", "fade.fade");
                Append(sb, "fadeBefore", "fade.fadeBeforeGuard");
                Append(sb, "cut", "event.crossFadeCut");
                Append(sb, "rect", "window.rectMinX");
                Append(sb, "/", "window.rectMinZ");
                Append(sb, "win", "window.winW");
                Append(sb, "x", "window.winH");
                // The view the renderer was handed. A camera that jumps for one frame and
                // returns would move the gas and nothing else in this log would say so —
                // every other key is derived from the window that view produced.
                Append(sb, "vwX", "window.viewMinX");
                Append(sb, "vwZ", "window.viewMinZ");
                Append(sb, "tmr", "clock.timerDue");
                Append(sb, "since", "clock.sincePublish");
                Append(sb, "prevEq", "tex.gasPrevEqualsCur");
                Append(sb, "gc", "gate.gasClean");
                Append(sb, "mc", "gate.mistClean");
                Append(sb, "gasCur", "tex.gasCurrent");
                Append(sb, "gasPrev", "tex.gasPrevious");
                Append(sb, "prevWin", "track.gasPrevMinX");
                Append(sb, "/", "track.gasPrevMinZ");
                Append(sb, "flowUp", "event.flowUploaded");
                Append(sb, "quad", "event.quadRebuilt");
                return sb.ToString();
            }

            private void Append(StringBuilder sb, string label, string key)
            {
                string? value = Get(key);
                if (value != null)
                {
                    sb.Append(' ').Append(label).Append('=').Append(value);
                }
            }
        }

        private static readonly Frame[] History = new Frame[HistoryFrames];
        private static int historyCount;
        private static int historyHead;
        private static Frame? current;
        private static Frame? previous;
        private static int frameIndex;
        private static int lastBeginFrameCount = int.MinValue;
        private static bool lastEnabledState = true;
        private static int eventCount;
        private static int suppressed;
        private static string lastReasonKey = "";
        private static double lastEventRealtime = double.NegativeInfinity;
        private static bool sessionStarted;
        private static bool writeFailed;
        private static int rotation;
        private static double lastJumpWrite = double.NegativeInfinity;
        private static int jumpSuppressed;

        /// <summary>Minimum seconds between two JUMP lines.</summary>
        private const double JumpSpacing = 0.08;

        /// <summary>Length of the window the periodic PEAK record reports on.</summary>
        private const double PeakWindow = 2.5;

        private static float peakMean;
        private static string peakDescription = "";
        private static double peakWindowStart;
        private static readonly Dictionary<string, int> ReasonCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        internal static void BeginFrame(double realtime)
        {
            bool nowEnabled = VisualTweaksSettings.gasFieldDiagnostics;
            if (nowEnabled != lastEnabledState)
            {
                lastEnabledState = nowEnabled;
                // Write() deliberately does not check Enabled: the on/off boundary is
                // itself evidence for the A/B comparison, so record it even when the
                // switch was just turned off.
                Write("=== DIAGNOSTICS " + (nowEnabled ? "ENABLED" : "DISABLED")
                    + " @ realtime=" + realtime.ToString("F3", CultureInfo.InvariantCulture)
                    + " frame=" + Time.frameCount.ToString(CultureInfo.InvariantCulture)
                    + " ===\n");
            }

            if (!Enabled)
            {
                return;
            }

            if (lastBeginFrameCount == Time.frameCount)
            {
                return;
            }

            // An exception outside GasFieldLayer's try (or a layer we did not instrument)
            // can leave the previous frame open. Close it so the frame chain stays intact.
            if (current != null)
            {
                Note("previous frame was never closed by its Draw call");
                EndFrame(false, "frame-not-closed");
            }

            lastBeginFrameCount = Time.frameCount;
            current = new Frame
            {
                Index = ++frameIndex,
                Realtime = realtime,
            };

            // Default every flag to 0 so a flag that simply was not reached this frame
            // is indistinguishable from "off", and Get() always has a previous value.
            for (int i = 0; i < FlagKeys.Length; i++)
            {
                current.Fields.Add(new KeyValuePair<string, string>(FlagKeys[i], "0"));
            }

            for (int i = 0; i < WatchKeys.Length; i++)
            {
                current.Fields.Add(new KeyValuePair<string, string>(WatchKeys[i], ""));
            }

            Put("time.realtime", realtime);
            Put("time.frameCount", Time.frameCount);
            Put("time.deltaTime", Time.deltaTime);
            Put("time.unscaledDeltaTime", Time.unscaledDeltaTime);
            Put("time.realtimeSinceStartup", Time.realtimeSinceStartup);
            Put("clock.paused", Find.TickManager != null && Find.TickManager.Paused);
            Put("clock.timeSpeed", Find.TickManager != null
                ? Find.TickManager.CurTimeSpeed.ToString()
                : "none");
            Put("clock.ticksGame", Find.TickManager != null ? Find.TickManager.TicksGame : 0);

            if (Find.CameraDriver != null)
            {
                CellRect view = Find.CameraDriver.CurrentViewRect;
                Put("camera.viewMinX", view.minX);
                Put("camera.viewMinZ", view.minZ);
                Put("camera.viewMaxX", view.maxX);
                Put("camera.viewMaxZ", view.maxZ);
                Put("camera.cellPixels", Find.CameraDriver.CellSizePixels);
                Put("camera.rootSize", Find.CameraDriver.ZoomRootSize);
                IntVec3 mapPos = Find.CameraDriver.MapPosition;
                Put("camera.x", mapPos.x);
                Put("camera.z", mapPos.z);
            }
        }

        /// <summary>Records one named quantity. Keys are stable so the comparator can diff them.</summary>
        internal static void Put(string key, object? value)
        {
            if (!Enabled || current == null)
            {
                return;
            }

            current.Fields.Add(
                new KeyValuePair<string, string>(key, Format(value)));
        }

        // Typed overloads matter for the A/B test. With only the object? signature,
        // every int/float/bool Put boxes on the caller side BEFORE the call, so the
        // "off" half would still allocate ~120 objects per frame. These overloads put
        // the guard before any conversion, making a switched-off diagnostic cost one
        // static field read per call and nothing else.
        internal static void Put(string key, string? value)
        {
            if (!Enabled || current == null)
            {
                return;
            }

            current.Fields.Add(new KeyValuePair<string, string>(key, value ?? "null"));
        }

        internal static void Put(string key, bool value)
        {
            if (!Enabled || current == null)
            {
                return;
            }

            current.Fields.Add(new KeyValuePair<string, string>(key, value ? "1" : "0"));
        }

        internal static void Put(string key, int value)
        {
            if (!Enabled || current == null)
            {
                return;
            }

            current.Fields.Add(new KeyValuePair<string, string>(key,
                value.ToString(CultureInfo.InvariantCulture)));
        }

        internal static void Put(string key, float value)
        {
            if (!Enabled || current == null)
            {
                return;
            }

            current.Fields.Add(new KeyValuePair<string, string>(key,
                float.IsFinite(value)
                    ? value.ToString("F4", CultureInfo.InvariantCulture)
                    : value.ToString(CultureInfo.InvariantCulture)));
        }

        internal static void Put(string key, double value)
        {
            if (!Enabled || current == null)
            {
                return;
            }

            current.Fields.Add(new KeyValuePair<string, string>(key,
                double.IsFinite(value)
                    ? value.ToString("F4", CultureInfo.InvariantCulture)
                    : value.ToString(CultureInfo.InvariantCulture)));
        }

        internal static void Note(string text)
        {
            if (!Enabled || current == null)
            {
                return;
            }

            current.Notes.Add(text);
        }

        internal static void Flag(string key, bool on = true)
        {
            Put("event." + key, on);
        }

        /// <summary>
        /// Reads back a flag this frame already recorded, for the one-line JUMP record.
        /// Returns "-" when the frame never set it, so the line never invents a value.
        /// </summary>
        internal static string FlagValue(string key)
        {
            if (!Enabled || current == null)
            {
                return "-";
            }

            return current.Get("event." + key) ?? "-";
        }

        /// <summary>
        /// Writes a self-contained line the moment a measured discontinuity happens.
        ///
        /// The event reports are diff-driven: they fire when a watched key changes and
        /// carry the surrounding history. That is the wrong shape for "the picture moved",
        /// because the interesting frame is often one where every watched key stayed the
        /// same (same window, same textures, only the fade or the content moved). Those
        /// frames are exactly the ones the user reports and the log never showed. This
        /// bypasses the event machinery and records the measurement itself.
        ///
        /// Throttled so a badly broken frame cannot flood the file, with the number of
        /// dropped lines reported inline.
        /// </summary>
        internal static void Jump(string text)
        {
            if (!Enabled)
            {
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (now - lastJumpWrite < JumpSpacing)
            {
                jumpSuppressed++;
                return;
            }

            lastJumpWrite = now;
            Write("JUMP " + text
                + (jumpSuppressed > 0
                    ? " (+" + jumpSuppressed.ToString(CultureInfo.InvariantCulture) + " suppressed)"
                    : "")
                + "\n");
            jumpSuppressed = 0;
        }

        /// <summary>
        /// Whether this frame's measured step is worth describing for the periodic PEAK
        /// record. Two calls on purpose: the caller only builds the description when this
        /// says yes, so the common frame allocates nothing.
        /// </summary>
        internal static bool IsPeak(float mean)
        {
            return Enabled && mean > peakMean;
        }

        /// <summary>
        /// Records the worst frame of the last few seconds and writes it out when the window
        /// closes, whether or not it tripped any threshold.
        ///
        /// This exists because the two previous rounds of instrumentation produced *zero*
        /// data: the thresholds were set for a picture that changes by tens of percent, the
        /// field on screen changes by a fraction of a percent, and "no JUMP lines" was
        /// indistinguishable from "the measurement never ran". A periodic worst-of record
        /// cannot come back empty, so every run tells us what the numbers actually are and
        /// the thresholds can be calibrated against a real distribution.
        /// </summary>
        internal static void Peak(float mean, string? description)
        {
            if (!Enabled)
            {
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (peakWindowStart <= 0.0)
            {
                peakWindowStart = now;
            }

            // A null description means "this frame is not a new worst", which still has to
            // be reported so the window can close on schedule.
            if (description != null && mean > peakMean)
            {
                peakMean = mean;
                peakDescription = description;
            }

            if (now - peakWindowStart < PeakWindow)
            {
                return;
            }

            if (peakDescription.Length > 0)
            {
                Write("PEAK worst of " + PeakWindow.ToString("F0", CultureInfo.InvariantCulture)
                    + "s: " + peakDescription + "\n");
            }

            peakMean = 0f;
            peakDescription = "";
            peakWindowStart = now;
        }

        /// <summary>Call at every exit of GasFieldRenderer.Draw, including the early returns.</summary>
        internal static void EndFrame(bool drawMesh, string skipReason = "")
        {
            if (!Enabled || current == null)
            {
                return;
            }

            current.DrawMesh = drawMesh;
            current.SkipReason = skipReason;
            Put("frame.drawMesh", drawMesh);
            if (skipReason.Length > 0)
            {
                Put("frame.skipReason", skipReason);
            }

            List<string> reasons = CollectReasons(previous, current);
            if (reasons.Count > 0)
            {
                CountReasons(reasons);
                string key = string.Join("|", reasons);
                bool important = HasImportant(reasons);
                bool spaced = current.Realtime - lastEventRealtime >= MinEventSpacing;
                if (important || spaced || key != lastReasonKey)
                {
                    WriteReport(current, reasons, suppressed);
                    suppressed = 0;
                    lastEventRealtime = current.Realtime;
                    lastReasonKey = key;
                }
                else
                {
                    suppressed++;
                }
            }

            History[historyHead] = current;
            historyHead = (historyHead + 1) % HistoryFrames;
            if (historyCount < HistoryFrames)
            {
                historyCount++;
            }

            previous = current;
            current = null;
        }

        /// <summary>Use when Draw threw: keeps the frame chain from going stale.</summary>
        internal static void AbortFrame(Exception ex)
        {
            if (!Enabled || current == null)
            {
                // The frame was already closed before the exception; the caller logs it.
                return;
            }

            Note("EXCEPTION " + ex.GetType().Name + ": " + ex.Message);
            EndFrame(false, "exception");
        }

        private static List<string> CollectReasons(Frame? before, Frame now)
        {
            List<string> reasons = new List<string>();
            if (before == null)
            {
                reasons.Add("SESSION_FIRST_FRAME");
                return reasons;
            }

            if (before.DrawMesh != now.DrawMesh)
            {
                reasons.Add("DRAW_FLIP:" + (now.DrawMesh ? "on" : "off"));
            }

            if (before.SkipReason != now.SkipReason && now.SkipReason.Length > 0)
            {
                reasons.Add("SKIP:" + now.SkipReason);
            }

            for (int i = 0; i < WatchKeys.Length; i++)
            {
                string key = WatchKeys[i];
                string? a = before.Get(key);
                string? b = now.Get(key);

                // An empty value means "this frame never put the key", i.e. the code path
                // that measures it was not reached — not that the quantity became empty.
                // Comparing it against the other frame invents a change every time a path
                // alternates between short-circuiting and running: upload.previousIsFresh
                // alone produced 1024 of 1531 events that way, all of them noise, because
                // UploadTexture skips it on the frames it does not upload.
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                {
                    continue;
                }

                if (!string.Equals(a, b, StringComparison.Ordinal))
                {
                    reasons.Add("CHG:" + key + "=" + a + "->" + b);
                }
            }

            for (int i = 0; i < FlagKeys.Length; i++)
            {
                string key = FlagKeys[i];
                if (now.Get(key) == "1" && before.Get(key) != "1")
                {
                    reasons.Add("FLAG:" + key);
                }
            }

            // A drop *after* the blend reached 1 is the cross-fade restarting, which is the
            // healthy case: the previous texture is whatever the screen was already
            // showing, so the new 0 is the same picture the old 1 was. Only a drop that
            // happens while the blend is still climbing is a cut — that is the flash.
            // Reporting both is why 508 of 1531 events were flagged as candidates for a
            // jump that the eye cannot see.
            double fadeBefore = Number(before, "fade.fade");
            double fadeNow = Number(now, "fade.fade");
            double fadeDrop = fadeBefore - fadeNow;
            if (fadeDrop >= FadeJump && fadeBefore < 1f)
            {
                reasons.Add("FADE_CUT:" + fadeBefore.ToString("F2", CultureInfo.InvariantCulture)
                    + "->" + fadeNow.ToString("F2", CultureInfo.InvariantCulture)
                    + " (" + fadeDrop.ToString("F2", CultureInfo.InvariantCulture) + ")");
            }

            double sinceNow = Number(now, "clock.sincePublish");
            double sinceBefore = Number(before, "clock.sincePublish");
            if (sinceNow < sinceBefore - SinceReset)
            {
                reasons.Add("SINCE_RESET:" +
                    sinceBefore.ToString("F3", CultureInfo.InvariantCulture) + "->" +
                    sinceNow.ToString("F3", CultureInfo.InvariantCulture));
            }

            // A fixed threshold is only correct at 1x. At 6x a perfectly ordinary frame
            // advances the clock by 0.1 s, so *every* frame tripped this — 1153 of 1531
            // events in the first real log — and the genuine jumps drowned in it. Scale
            // with the previous frame's advance instead (the same 2.5x rule GasTrace uses
            // to flag a speed change) and keep the absolute value as a floor for 1x.
            double clockDelta = Number(now, "clock.shaderClock") -
                Number(before, "clock.shaderClock");
            double clockJumpLimit = Math.Max(ClockJump,
                Number(now, "clock.prevClockAdvance") * 2.5);
            if (clockDelta >= clockJumpLimit)
            {
                reasons.Add("CLOCK_JUMP:" + clockDelta.ToString("F3", CultureInfo.InvariantCulture)
                    + "/" + clockJumpLimit.ToString("F3", CultureInfo.InvariantCulture));
            }

            double flowJump = Math.Abs(Number(now, "noise.flowUMax") -
                Number(before, "noise.flowUMax"));
            if (flowJump >= FlowUJump)
            {
                reasons.Add("FLOW_U_JUMP:" + flowJump.ToString("F2", CultureInfo.InvariantCulture));
            }

            // The measured step of the drawn picture. Unlike every other reason here this
            // one is not a guess about a mechanism: it is the quantity the user perceives.
            double screenDelta = Number(now, "screen.deltaMean");
            if (screenDelta >= ScreenJump)
            {
                reasons.Add("SCREEN_JUMP:" + screenDelta.ToString("F3", CultureInfo.InvariantCulture)
                    + "/max " + Number(now, "screen.deltaMax").ToString("F3", CultureInfo.InvariantCulture));
            }

            // The shader's noise offset is a path integral and can only grow. A frame where
            // it shrinks means the whole pattern teleported backwards for one frame — the
            // "it jumps somewhere else and comes straight back" report, with no density
            // change to show for it, which is why every other probe missed it.
            double noiseX = Number(now, "noise.offsetX");
            double noiseZ = Number(now, "noise.offsetZ");
            bool noiseKnown = !string.IsNullOrEmpty(now.Get("noise.offsetX"))
                && !string.IsNullOrEmpty(before.Get("noise.offsetX"));
            double noiseBack = noiseKnown
                ? Math.Max(Number(before, "noise.offsetX") - noiseX,
                    Number(before, "noise.offsetZ") - noiseZ)
                : 0.0;
            if (noiseBack >= NoiseBack)
            {
                reasons.Add("NOISE_BACK:" + noiseBack.ToString("F4", CultureInfo.InvariantCulture));
            }

            return reasons;
        }

        private static bool HasImportant(List<string> reasons)
        {
            for (int i = 0; i < reasons.Count; i++)
            {
                string r = reasons[i];
                if (r.StartsWith("DRAW_FLIP", StringComparison.Ordinal)
                    || r.StartsWith("SKIP", StringComparison.Ordinal)
                    || r.StartsWith("FADE_CUT", StringComparison.Ordinal)
                    || r.StartsWith("SESSION_FIRST_FRAME", StringComparison.Ordinal)
                    || r.StartsWith("CHG:tex.", StringComparison.Ordinal)
                    || r.StartsWith("CHG:tex", StringComparison.Ordinal)
                    || r.StartsWith("CHG:uniform.effectiveNoiseMode", StringComparison.Ordinal)
                    || r.StartsWith("CHG:gate.active", StringComparison.Ordinal)
                    || r.StartsWith("CHG:gate.gasClean", StringComparison.Ordinal)
                    || r.StartsWith("CHG:gate.mistClean", StringComparison.Ordinal)
                    || r.StartsWith("FLAG:event.quadRebuilt", StringComparison.Ordinal)
                    || r.StartsWith("SINCE_RESET", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static double Number(Frame frame, string key)
        {
            string? text = frame.Get(key);
            return text != null && double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value)
                ? value
                : 0.0;
        }

        private static string Format(object? value)
        {
            if (value == null)
            {
                return "null";
            }

            if (value is bool b)
            {
                return b ? "1" : "0";
            }

            if (value is float f)
            {
                return float.IsFinite(f)
                    ? f.ToString("F4", CultureInfo.InvariantCulture)
                    : f.ToString(CultureInfo.InvariantCulture);
            }

            if (value is double d)
            {
                return double.IsFinite(d)
                    ? d.ToString("F4", CultureInfo.InvariantCulture)
                    : d.ToString(CultureInfo.InvariantCulture);
            }

            if (value is Enum)
            {
                return value.ToString() ?? "";
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        private static void CountReasons(List<string> reasons)
        {
            for (int i = 0; i < reasons.Count; i++)
            {
                string r = reasons[i];
                int cut = r.IndexOf(':');
                string group = cut > 0 ? r.Substring(0, cut) : r;
                ReasonCounts.TryGetValue(group, out int count);
                ReasonCounts[group] = count + 1;
            }
        }

        private static void WriteReport(Frame frame, List<string> reasons, int skipped)
        {
            eventCount++;
            bool high = IsHighConfidence(reasons);
            StringBuilder sb = new StringBuilder(8192);
            sb.AppendLine("=== EVENT #" + eventCount
                + (high ? " [HIGH_CONFIDENCE_FLASH_CANDIDATE]" : "")
                + " t=" + frame.Realtime.ToString("F3", CultureInfo.InvariantCulture)
                + " frame=" + (frame.Get("time.frameCount") ?? "?")
                + " suppressed=" + skipped
                + " reasons=" + reasons.Count + " ===");
            for (int i = 0; i < reasons.Count; i++)
            {
                sb.AppendLine("  R: " + reasons[i]);
            }

            sb.AppendLine("-- context: last " + historyCount + " frames (oldest first) --");
            for (int i = 0; i < historyCount; i++)
            {
                int slot = (historyHead - historyCount + i + HistoryFrames * 2) % HistoryFrames;
                Frame? old = History[slot];
                if (old != null)
                {
                    sb.AppendLine("  " + old.Compact());
                    if (old.Notes.Count > 0)
                    {
                        for (int n = 0; n < old.Notes.Count; n++)
                        {
                            sb.AppendLine("      note: " + old.Notes[n]);
                        }
                    }
                }
            }

            sb.AppendLine("-- trigger frame: full snapshot --");
            // One line per key, with its last non-empty value. The prefill in BeginFrame
            // exists so Get() always has something to compare against, not so every report
            // carries 96 lines of "key = " followed later by the same key with the value.
            // It also removes the second way a report could be misread: the old output
            // printed event.rectMoved = 0 near the top and = 1 near the bottom.
            List<KeyValuePair<string, string>> snapshot =
                new List<KeyValuePair<string, string>>(frame.Fields.Count);
            HashSet<string> seenKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = frame.Fields.Count - 1; i >= 0; i--)
            {
                KeyValuePair<string, string> field = frame.Fields[i];
                if (field.Value.Length == 0 || !seenKeys.Add(field.Key))
                {
                    continue;
                }

                snapshot.Add(field);
            }

            snapshot.Reverse();
            for (int i = 0; i < snapshot.Count; i++)
            {
                sb.Append("  ").Append(snapshot[i].Key)
                    .Append(" = ").AppendLine(snapshot[i].Value);
            }

            if (frame.Notes.Count > 0)
            {
                sb.AppendLine("-- trigger frame: notes --");
                for (int i = 0; i < frame.Notes.Count; i++)
                {
                    sb.AppendLine("  " + frame.Notes[i]);
                }
            }

            if (eventCount % SummaryEvery == 0)
            {
                sb.AppendLine("-- running reason totals --");
                foreach (KeyValuePair<string, int> pair in ReasonCounts)
                {
                    sb.Append("  ").Append(pair.Key).Append(" = ")
                        .AppendLine(pair.Value.ToString(CultureInfo.InvariantCulture));
                }
            }

            sb.AppendLine();
            Write(sb.ToString());

            if (eventCount == 1 || eventCount % 200 == 0)
            {
                Log.Warning("[Visual Tweaks] Gas flash diagnostics: " + eventCount
                    + " events -> " + LogPath
                    + " | search for HIGH_CONFIDENCE_FLASH_CANDIDATE");
            }
        }

        /// <summary>
        /// Marks reports whose reasons can change what the whole layer shows in one frame.
        /// Purely a reading aid: everything is recorded regardless of this flag.
        /// </summary>
        private static bool IsHighConfidence(List<string> reasons)
        {
            for (int i = 0; i < reasons.Count; i++)
            {
                string r = reasons[i];
                if (r.StartsWith("DRAW_FLIP", StringComparison.Ordinal)
                    || r.StartsWith("SKIP", StringComparison.Ordinal)
                    || r.StartsWith("FADE_CUT", StringComparison.Ordinal)
                    || r.StartsWith("SINCE_RESET", StringComparison.Ordinal)
                    || r.StartsWith("CHG:tex.", StringComparison.Ordinal)
                    || r.StartsWith("CHG:uniform.params", StringComparison.Ordinal)
                    || r.StartsWith("CHG:uniform.effectiveNoiseMode", StringComparison.Ordinal)
                    || r.StartsWith("CHG:gate.active", StringComparison.Ordinal)
                    || r.StartsWith("CHG:gate.gasClean", StringComparison.Ordinal)
                    || r.StartsWith("CHG:gate.mistClean", StringComparison.Ordinal)
                    || r.StartsWith("FLAG:event.crossFadeCut", StringComparison.Ordinal)
                    || r.StartsWith("FLAG:event.quadRebuilt", StringComparison.Ordinal)
                    || r.StartsWith("FLAG:event.collapsePair", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Write(string text)
        {
            if (writeFailed)
            {
                return;
            }

            try
            {
                string path = LogPath;
                EnsureSession(path);
                FileInfo info = new FileInfo(path);
                if (info.Exists && info.Length > MaxLogBytes)
                {
                    Rotate(path);
                }

                File.AppendAllText(path, text, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                writeFailed = true;
                Log.Error("[Visual Tweaks] Gas flash diagnostics cannot write "
                    + LogPath + ": " + ex);
            }
        }

        private static void EnsureSession(string path)
        {
            if (sessionStarted)
            {
                return;
            }

            sessionStarted = true;

            // Append, never overwrite. This used to be File.WriteAllText, so starting the
            // game wiped the previous run: between the 19:17 and 19:25 sessions, 19 MB of
            // evidence was replaced by a 562 byte header before anyone read it. Rotation
            // (MaxLogBytes) is what keeps the file bounded now, not clobbering.
            StringBuilder sb = new StringBuilder(1024);
            sb.AppendLine();
            sb.AppendLine("=== SESSION " + DateTime.Now.ToString(
                "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ===");
            sb.AppendLine("started = " + DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
            sb.AppendLine("path = " + path);
            sb.AppendLine("history = " + HistoryFrames + " frames");
            sb.AppendLine("settings: useGasField=" + VisualTweaksSettings.useGasField
                + " smooth=" + VisualTweaksSettings.gasFieldSmooth
                + " smoothTau=" + VisualTweaksSettings.gasFieldSmoothTau
                + " noiseMode=" + VisualTweaksSettings.gasFieldNoiseMode
                + " animation=" + VisualTweaksSettings.gasFieldAnimation
                + " opacity=" + VisualTweaksSettings.gasFieldOpacity
                + " scale=" + VisualTweaksSettings.gasFieldScale
                + " blur=" + VisualTweaksSettings.gasFieldBlur
                + " sharpen=" + VisualTweaksSettings.gasFieldSharpen
                + " replaceDust=" + VisualTweaksSettings.gasFieldReplaceDeadlife);
            sb.AppendLine("A report is written whenever any watched quantity changes "
                + "between two frames. Each report contains the full trigger-frame "
                + "snapshot plus a compact history of preceding frames.");
            sb.AppendLine();
            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static void Rotate(string path)
        {
            rotation++;
            if (rotation > MaxRotations)
            {
                rotation = 1;
            }

            string target = path + "." + rotation.ToString(CultureInfo.InvariantCulture) + ".log";
            try
            {
                // Move, not copy: copying 64 MB on the main thread would itself cause
                // the kind of hitch this diagnostic is trying to observe.
                File.Move(path, target, overwrite: true);
                File.WriteAllText(path,
                    "=== rotated; previous events in " + Path.GetFileName(target) + " ===\n",
                    Encoding.UTF8);
            }
            catch
            {
                // A rotation failure must not stop diagnostics; the next write just
                // keeps appending to the oversized file.
            }
        }
    }
}
