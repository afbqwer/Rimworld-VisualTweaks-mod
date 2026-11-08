using System;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// AO's wind streamline overlay, replaced. AO's own renderer is suppressed by Harmony;
    /// this layer reads AO's public toggle and wind snapshots and draws the replacement.
    ///
    /// Lives in the AO compatibility assembly because <see cref="WindFlowRenderer"/> only
    /// exists there. The base assembly's single map component asks
    /// <see cref="AOIntegration.CreateWindLayer"/> for one instance per map, so it stays a
    /// plain object instead of a MapComponent of its own.
    /// </summary>
    internal sealed class AOWindOverlayLayer : IOverlayLayer
    {
        /// <summary>
        /// Minimum gap between animation-driven rebuilds in 60 fps mode. Only a ceiling
        /// for very high refresh rates; see the comment at the gate in Draw.
        /// </summary>
        private const float SixtyFpsRebuildInterval = 0.008f;

        /// <summary>
        /// AO's own cadence: 1/30 s. Used when the 60 fps mode is switched off.
        /// </summary>
        private const float AoRebuildInterval = 1f / 30f;

        private WindFlowRenderer? renderer;
        private float lastRebuildRealtime = -1f;
        private bool drawErrorLogged;

        // The snapshots AO handed over last rebuild. Null until the first one arrives, and
        // AO may also swap them for null between publishes.
        private bool windReady;
        private float[]? windVelocityX;
        private float[]? windVelocityZ;
        private float[]? windObstacleMask;
        private CellRect windView;
        private float windCellPixels;

        // Everything the uploaded geometry depends on. When none of it moved and nothing
        // is animating, the meshes already in GPU memory are still correct, so the whole
        // rebuild (sampling + geometry + upload, ~1 ms) is skipped.
        private CellRect lastBuiltView;
        private float lastBuiltCellPixels = -1f;
        private int lastBuiltSettingsKey;
        private int lastSnapshotSteps = -1;

        public void Draw(Map map, in OverlayFrame frame)
        {
            if (Find.ScreenshotModeHandler != null && Find.ScreenshotModeHandler.Active)
            {
                return;
            }

            if (!VisualTweaksSettings.ReplacementEnabled)
            {
                windReady = false;
                return;
            }

            // AO's own toggle is the layer's visibility switch, so poll it every frame:
            // frames that do not rebuild still have to honour a toggle-off immediately.
            if (!AOWindBridge.ToggleOn())
            {
                windReady = false;
                return;
            }

            // 三个 out 由 [NotNullWhen(true)] 保证非空，这里不再复查。
            if (!AOWindBridge.TryGetWind(
                    map,
                    out float[]? velocityX,
                    out float[]? velocityZ,
                    out float[]? obstacleMask))
            {
                windReady = false;
                return;
            }

            float now = frame.Realtime;
            float timeScale = ResolveTimeScale(frame.Paused);

            CellRect view = frame.View;
            float cellPixels = frame.CellPixels;
            int settingsKey = SettingsKey();

            // AO swaps the snapshot arrays when it publishes, so a reference change is the
            // reliable "the wind changed" signal; the step counter covers an in-place
            // publish. Either one invalidates the built geometry and the blurred field.
            bool snapshotChanged = !windReady
                || !ReferenceEquals(velocityX, windVelocityX)
                || !ReferenceEquals(velocityZ, windVelocityZ)
                || !ReferenceEquals(obstacleMask, windObstacleMask);

            bool viewChanged = !windReady
                || view.minX != lastBuiltView.minX
                || view.minZ != lastBuiltView.minZ
                || view.Width != lastBuiltView.Width
                || view.Height != lastBuiltView.Height
                || !Mathf.Approximately(cellPixels, lastBuiltCellPixels);

            bool settingsChanged = !windReady || settingsKey != lastBuiltSettingsKey;

            // Dash phase and particle positions advance in real time, so an unpaused
            // overlay needs a rebuild even when the four inputs above are unchanged.
            bool animating = timeScale > 0.0001f;

            bool inputsChanged = snapshotChanged || viewChanged || settingsChanged;

            // AO steps its own particles at a fixed 30 Hz (WindStreamlineRenderer
            // .AnimationStepSeconds) and only re-issues the draw calls every frame, so a
            // 30 Hz rebuild reproduces exactly the motion cadence AO has. On a 60 Hz
            // display that means every streak is frozen for two frames; at 144 Hz the
            // 5/5/4 frame pattern reads as judder. The 60 fps mode rebuilds every frame
            // instead.
            //
            // The 8 ms floor in that mode is not a throttle for normal play, it only stops
            // displays above 120 Hz from paying twice for motion nobody can see. At 60 Hz
            // every frame passes, at 144 Hz it lands on every other frame (72 Hz, even
            // cadence), at 240 Hz every other frame (120 Hz).
            float rebuildInterval = VisualTweaksSettings.sixtyFpsMode
                ? SixtyFpsRebuildInterval
                : AoRebuildInterval;
            bool throttleOk = now - lastRebuildRealtime >= rebuildInterval;
            bool immediate = !windReady || settingsChanged;

            if (immediate || ((inputsChanged || animating) && throttleOk))
            {
                lastRebuildRealtime = now;
                lastBuiltView = view;
                lastBuiltCellPixels = cellPixels;
                lastBuiltSettingsKey = settingsKey;
                windVelocityX = velocityX;
                windVelocityZ = velocityZ;
                windObstacleMask = obstacleMask;
                windView = view;
                windCellPixels = cellPixels;
                windReady = true;

                lastSnapshotSteps = AOWindBridge.CompletedSteps(map);

                if (renderer == null)
                {
                    renderer = new WindFlowRenderer();
                }

                try
                {
                    renderer.Draw(
                        map,
                        velocityX,
                        velocityZ,
                        obstacleMask,
                        windView,
                        windCellPixels,
                        now,
                        true,
                        timeScale,
                        lastSnapshotSteps);
                }
                catch (Exception ex)
                {
                    // Keep windReady: the meshes uploaded last time are still valid, and
                    // clearing it would make every following frame retry immediately
                    // (bypassing the 30 Hz cap) if the failure is persistent.
                    if (!drawErrorLogged)
                    {
                        drawErrorLogged = true;
                        Log.Error("[Visual Tweaks] Draw failed: " + ex);
                    }
                }

                return;
            }

            // Nothing to rebuild: still redraw the meshes we already uploaded. DrawMesh has
            // to be called every frame, but with rebuild=false this only re-issues the
            // draw calls.
            if (!windReady
                || renderer == null
                || windVelocityX == null
                || windVelocityZ == null
                || windObstacleMask == null)
            {
                return;
            }

            try
            {
                renderer.Draw(
                    map,
                    windVelocityX,
                    windVelocityZ,
                    windObstacleMask,
                    windView,
                    windCellPixels,
                    now,
                    false,
                    timeScale,
                    lastSnapshotSteps);
            }
            catch (Exception ex)
            {
                if (!drawErrorLogged)
                {
                    drawErrorLogged = true;
                    Log.Error("[Visual Tweaks] Draw failed: " + ex);
                }
            }
        }

        public void Dispose()
        {
            renderer?.Dispose();
            renderer = null;
            windReady = false;
            windVelocityX = null;
            windVelocityZ = null;
            windObstacleMask = null;
            lastRebuildRealtime = -1f;
            lastBuiltCellPixels = -1f;
            lastBuiltSettingsKey = 0;
            lastSnapshotSteps = -1;
        }

        private static float ResolveTimeScale(bool paused)
        {
            if (paused)
            {
                return VisualTweaksSettings.pauseWhenPaused ? 0f : 1f;
            }

            if (VisualTweaksSettings.scaleSpeedWithGameSpeed)
            {
                // TickRateMultiplier can jump to very large values at ultrafast speed; cap
                // the visual animation so it stays readable.
                TickManager tickManager = Find.TickManager;
                if (tickManager != null)
                {
                    return Mathf.Clamp(tickManager.TickRateMultiplier, 1f, 4f);
                }
            }

            return 1f;
        }

        /// <summary>
        /// Cheap fingerprint of the settings that change geometry, so a slider drag
        /// rebuilds and an untouched settings object does not.
        /// </summary>
        private static int SettingsKey()
        {
            HashCode hash = default;
            hash.Add(VisualTweaksSettings.style);
            hash.Add(VisualTweaksSettings.density);
            hash.Add(VisualTweaksSettings.streakLength);
            hash.Add(VisualTweaksSettings.speedLengthInfluence);
            hash.Add(VisualTweaksSettings.lineWidth);
            // Opacity is deliberately absent: it only tints the materials, which
            // UpdateMaterials reapplies every frame, so dragging the slider must not
            // trigger a geometry rebuild.
            hash.Add(VisualTweaksSettings.animationSpeed);
            hash.Add(VisualTweaksSettings.showGuideLine);
            hash.Add(VisualTweaksSettings.smoothField);
            hash.Add(VisualTweaksSettings.coloredParticles);
            hash.Add(VisualTweaksSettings.scaleSpeedWithGameSpeed);
            return hash.ToHashCode();
        }
    }
}
