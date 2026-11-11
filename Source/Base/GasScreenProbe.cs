using System;
using System.Globalization;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Reads the actual rendered frame back from the GPU and diffs it against the previous
    /// one, once per frame, while the gas diagnostics are enabled.
    ///
    /// Every other probe in this mod reconstructs what the shader *should* have drawn from
    /// the numbers the renderer passed it. They agree with each other by construction, so
    /// they can only ever prove self-consistency — which is why several rounds of them came
    /// back clean while the picture still stepped. This one asks the framebuffer.
    ///
    /// The capture runs from an IMGUI Repaint, not from Camera.onPostRender. The first
    /// attempt used that callback and had to be thrown away: the only camera that reached
    /// it was an offscreen 2048x2048 one (Find.Camera never appeared at all), so a whole
    /// capture measured a picture that never changes — d=0.00000 on all 83 frames. OnGUI
    /// runs once per frame regardless of which camera drew the map, and by Repaint the
    /// world is composited.
    /// </summary>
    internal static class GasScreenProbe
    {
        /// <summary>
        /// Off by default, and it has to stay that way: the capture is a full-screen GPU
        /// readback every other frame, which costs more than the effect it was watching.
        /// It stays in the tree because it is the only measurement here that reads the
        /// picture instead of the renderer's own numbers, and if anything like this comes
        /// back it is the instrument to reach for first. Turn it on only while diagnosing,
        /// and expect the frame rate to drop.
        /// </summary>
        private const bool Enabled = false;

        private const int ProbeWidth = 320;
        private const int ProbeHeight = 180;

        /// <summary>Diff above which a frame is written as its own PIX_JUMP line.</summary>
        private const float JumpDiff = 0.020f;

        private static Texture2D? texture;
        private static byte[]? current;
        private static byte[]? previous;
        private static bool installed;
        private static bool failed;
        private static int lastFrame = -1;
        private static int originX;
        private static int originY;
        private static int width;
        private static int height;

        /// <summary>Slow average of the diff, so a jump is "much more than usual".</summary>
        private static float baseline = -1f;
        private static int samples;

        internal static void Tick()
        {
            if (!Enabled || installed || failed)
            {
                return;
            }

            try
            {
                GameObject host = new GameObject("VisualTweaksGasScreenProbe")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<GasScreenProbeBehaviour>();
                installed = true;
                GasFieldDiagnostics.Pixel("PIXHOOK gui installed screen=" + Screen.width + "x" + Screen.height);
            }
            catch (Exception e)
            {
                failed = true;
                GasFieldDiagnostics.Pixel("PIXHOOK failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>One capture per frame, called from the IMGUI Repaint.</summary>
        internal static void Capture()
        {
            if (!Enabled || failed || !GasFieldDiagnostics.Enabled || Time.frameCount == lastFrame)
            {
                return;
            }

            lastFrame = Time.frameCount;

            try
            {
                if (!EnsureRegion())
                {
                    return;
                }

                // The backbuffer is not readable in this build: ReadPixels from the screen
                // returned zeros on all 1515 frames of a run, which read as "the picture
                // never changes". ScreenCapture goes through the display path instead and
                // does return the composited frame. It costs a full-screen readback, so it
                // runs on every other frame — the artefact is a frame-scale event, and
                // every second frame still catches it.
                int total = width * height * 3;
                byte[] was = previous!;
                byte[] now = current!;
                long sum = 0;
                int big = 0;
                bool captured = false;

                if ((Time.frameCount & 1) == 0)
                {
                    Texture2D? shot = null;
                    try
                    {
                        shot = ScreenCapture.CaptureScreenshotAsTexture();
                        captured = shot != null && shot.width >= width && shot.height >= height;
                        if (captured)
                        {
                            var full = shot!.GetRawTextureData<byte>();
                            int stride = shot.width * 4;
                            int ox = (shot.width - width) / 2;
                            int oy = (shot.height - height) / 2;
                            for (int y = 0; y < height; y++)
                            {
                                int src = (oy + y) * stride + ox * 4;
                                int dst = y * width * 3;
                                for (int x = 0; x < width; x++)
                                {
                                    now[dst + x * 3] = full[src + x * 4];
                                    now[dst + x * 3 + 1] = full[src + x * 4 + 1];
                                    now[dst + x * 3 + 2] = full[src + x * 4 + 2];
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        captured = false;
                    }
                    finally
                    {
                        if (shot != null)
                        {
                            UnityEngine.Object.Destroy(shot);
                        }
                    }
                }
                else
                {
                    texture!.ReadPixels(new Rect(originX, originY, width, height), 0, 0, false);
                    texture.Apply(false, false);
                    var raw = texture.GetRawTextureData<byte>();
                    int i = 0;
                    for (int o = 0; o < total; o += 3)
                    {
                        now[o] = raw[o];
                        now[o + 1] = raw[o + 1];
                        now[o + 2] = raw[o + 2];
                        i++;
                    }
                }

                for (int o = 0; o < total; o += 3)
                {
                    int d = Math.Abs(now[o] - was[o])
                        + Math.Abs(now[o + 1] - was[o + 1])
                        + Math.Abs(now[o + 2] - was[o + 2]);
                    sum += d;
                    if (d >= 36)
                    {
                        big += 3;
                    }
                }

                int count = width * height;
                float mean = (float)(sum / (double)total / 255.0);
                big /= 3;

                // This frame becomes the reference; the other buffer is scratch.
                previous = now;
                current = was;

                if (samples < 3)
                {
                    GasFieldDiagnostics.Pixel("PIXSRC captured=" + captured + " mean=" + mean.ToString("F5", CultureInfo.InvariantCulture));
                }

                samples++;
                if (baseline < 0f)
                {
                    baseline = mean;
                }
                else
                {
                    // A jump must not raise its own baseline, or a burst of them would be
                    // "normal" by the third frame. Rise slowly, fall quickly.
                    baseline = mean > baseline
                        ? Mathf.Lerp(baseline, mean, 0.02f)
                        : Mathf.Lerp(baseline, mean, 0.25f);
                }

                bool jump = mean > JumpDiff && mean > baseline * 3f;
                string line = "n=" + samples.ToString(CultureInfo.InvariantCulture)
                    + " frame=" + Time.frameCount.ToString(CultureInfo.InvariantCulture)
                    + " t=" + Time.realtimeSinceStartup.ToString("F2", CultureInfo.InvariantCulture)
                    + " d=" + mean.ToString("F5", CultureInfo.InvariantCulture)
                    + " base=" + baseline.ToString("F5", CultureInfo.InvariantCulture)
                    + " big=" + (big / (float)count).ToString("F3", CultureInfo.InvariantCulture)
                    + " cut=" + GasFieldDiagnostics.FlagValue("crossFadeCut")
                    + " tmr=" + GasFieldDiagnostics.FlagValue("timerDue")
                    + " rect=" + GasFieldDiagnostics.FlagValue("rectMoved")
                    + " fade=" + (GasFieldDiagnostics.Number("fade.fade") ?? 0f).ToString("F2", CultureInfo.InvariantCulture)
                    + " vw=" + (GasFieldDiagnostics.Number("window.viewMinX") ?? 0f).ToString("F0", CultureInfo.InvariantCulture)
                    + "," + (GasFieldDiagnostics.Number("window.viewMinZ") ?? 0f).ToString("F0", CultureInfo.InvariantCulture);

                // Every frame's number goes to the log so a run that shows nothing can still
                // be read as a distribution; only the spikes get the louder prefix.
                GasFieldDiagnostics.Pixel((jump ? "PIX_JUMP " : "PIX ") + line);
            }
            catch (Exception e)
            {
                failed = true;
                GasFieldDiagnostics.Pixel("PIX failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        private static bool EnsureRegion()
        {
            int w = Mathf.Min(ProbeWidth, Screen.width);
            int h = Mathf.Min(ProbeHeight, Screen.height);
            if (w < 8 || h < 8)
            {
                return false;
            }

            if (texture != null && width == w && height == h)
            {
                return true;
            }

            if (texture != null)
            {
                UnityEngine.Object.Destroy(texture);
                texture = null;
            }

            width = w;
            height = h;
            originX = Mathf.Max(0, (Screen.width - w) / 2);
            originY = Mathf.Max(0, (Screen.height - h) / 2);
            texture = new Texture2D(w, h, TextureFormat.RGB24, false, true)
            {
                name = "VisualTweaksGasScreenProbe",
                filterMode = FilterMode.Point,
            };
            current = new byte[w * h * 3];
            previous = new byte[w * h * 3];
            baseline = -1f;
            return true;
        }
    }

    /// <summary>
    /// The host for the screen capture. It exists only to get an OnGUI Repaint callback,
    /// which is the one per-frame hook that does not depend on which camera renders the
    /// map. Depth is set as low as possible so the read happens before the game's own
    /// windows are painted on top of it.
    /// </summary>
    internal sealed class GasScreenProbeBehaviour : MonoBehaviour
    {
        private void OnGUI()
        {
            GUI.depth = -30000;
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            GasScreenProbe.Capture();
        }
    }
}
