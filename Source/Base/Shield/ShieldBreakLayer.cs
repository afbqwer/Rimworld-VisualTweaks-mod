using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Break animation for a shield whose host building was destroyed.
    ///
    /// Every other break ends with the host still on the map: PostDraw keeps running
    /// and <see cref="ShieldDomeRenderer"/> reads the dissolve state from the comp. A
    /// destroyed building has neither. Thing.Destroy takes it out of the dynamic draw
    /// list before the next frame, so without this layer the dome would simply vanish
    /// and the building's death would be the only thing on screen.
    ///
    /// The clock is the same <see cref="ShieldDissolveLog.State"/> the normal path
    /// uses. A destruction that interrupts an in-flight break — a lord shutdown, or the
    /// hit that killed the shield a moment earlier — continues that animation instead of
    /// starting a second one. TicksGame still drives it (through OverlayFrame.Tick), so
    /// it freezes on pause and follows game speed exactly like the live dome.
    ///
    /// Lives in the map's overlay stack rather than a Mote or a dummy thing because the
    /// dome is a shader surface, not a sprite: it needs the same material, altitude and
    /// quad matrix as the shield it replaces, and none of those survive the host.
    /// </summary>
    internal sealed class ShieldBreakLayer : IOverlayLayer
    {
        /// <summary>Only a handful of shield buildings can die in the same tick.</summary>
        private const int MaxBreaks = 8;

        private sealed class Break
        {
            public Vector3 Center;
            public float Radius;
            public Color Color;
            public float Mode;
            public ShieldDomeStyle Surface;
            public ShieldBreakStyle BreakStyle;
            public ShieldDissolveLog.State State = null!;
        }

        private readonly List<Break> breaks = new List<Break>(MaxBreaks);

        /// <summary>
        /// Queues a break handed over by the destruction patch. The state must already
        /// have a valid StartTick: the caller either started it now or found one that
        /// was still running.
        /// </summary>
        internal void Spawn(
            ShieldDissolveLog.State state,
            Vector3 center,
            float radius,
            Color color,
            float mode,
            ShieldDomeStyle surface,
            ShieldBreakStyle breakStyle)
        {
            if (state == null)
            {
                return;
            }

            if (breaks.Count >= MaxBreaks)
            {
                breaks.RemoveRange(0, breaks.Count - MaxBreaks + 1);
            }

            breaks.Add(new Break
            {
                Center = center,
                Radius = radius,
                Color = color,
                Mode = mode,
                Surface = surface,
                BreakStyle = breakStyle,
                State = state,
            });
        }

        public void Draw(Map map, in OverlayFrame frame)
        {
            if (breaks.Count == 0)
            {
                return;
            }

            // Only the custom surface can draw a break. If the replacement is switched
            // off or the shader died, drop the queue rather than leaving stale state
            // that would reappear if it is switched back on. Each break remembers its
            // own style choice, because the global style may have moved since the
            // host was destroyed.
            if (!ShieldDomeBridge.LargeActive)
            {
                breaks.Clear();
                return;
            }

            int now = frame.Tick;

            for (int i = breaks.Count - 1; i >= 0; i--)
            {
                Break item = breaks[i];
                if (item.BreakStyle == ShieldBreakStyle.Vanilla)
                {
                    breaks.RemoveAt(i);
                    continue;
                }

                int age = now - item.State.StartTick;
                if (age < 0 || age >= item.State.DurationTicks)
                {
                    breaks.RemoveAt(i);
                    continue;
                }

                float progress = item.State.DurationTicks <= 0
                    ? 1f
                    : (float)age / item.State.DurationTicks;

                if (!ShieldDomeRenderer.DrawDetachedBreak(
                        item.Center,
                        item.Radius,
                        item.Color,
                        item.Mode,
                        progress,
                        item.State.OriginX,
                        item.State.OriginZ,
                        item.State.Seed,
                        item.Surface))
                {
                    // The renderer latches itself off on failure; there is no point
                    // keeping a queue for a surface that will never draw again.
                    breaks.Clear();
                    return;
                }
            }
        }

        public void Dispose()
        {
            breaks.Clear();
        }
    }
}
