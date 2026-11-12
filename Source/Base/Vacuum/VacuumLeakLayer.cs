using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// The vacuum leak layer: continuous streams at ventilation ports, the
    /// one-shot wall bursts captured by VacuumBreachPatches, and the inward
    /// suction disc left behind when a ceiling is opened. Two instance streams,
    /// one shader, one draw.
    ///
    /// Timing model: intensities are low-passed with real time (they only have to
    /// bridge the 0.24 Hz state steps), scroll phases integrate with GAME time so
    /// the flow freezes while paused and accelerates with game speed — this is a
    /// state visualisation, not decoration. Breach envelopes already run on ticks
    /// (VacuumBreach.Strength), so both tracks pause consistently.
    /// </summary>
    internal sealed class VacuumLeakLayer : IOverlayLayer
    {
        private readonly VacuumLeakPorts ports = new VacuumLeakPorts();
        private readonly List<VacuumBreach> breaches = new List<VacuumBreach>(8);
        private readonly List<VacuumLeakInstance> instances = new List<VacuumLeakInstance>(64);

        private VacuumLeakRenderer? renderer;
        private int lastTick = -1;
        private float lastRealtime;
        private bool errorLogged;

        /// <summary>Forwarded from the map component's structural event subscriptions.</summary>
        internal void MarkStructureDirty()
        {
            ports.MarkStructureDirty();
        }

        /// <summary>
        /// Forwarded for events that name the cell they changed: the port for that
        /// cell is recomputed on the next frame without a map-wide rescan.
        /// </summary>
        internal void MarkCellDirty(IntVec3 cell)
        {
            ports.MarkCellDirty(cell);
        }

        public void Draw(Map map, in OverlayFrame frame)
        {
            // Cheapest test first: Biome.inVacuum is a field read, while
            // VacuumLeakBridge.Active walks the screenshot-mode handler chain. On a
            // non-vacuum map this is the only work the layer does all frame.
            if (map.Biome == null || !map.Biome.inVacuum || !VacuumLeakBridge.Active)
            {
                return;
            }

            float dtReal = 0f;
            float dtGame = 0f;
            if (lastTick >= 0)
            {
                dtReal = Mathf.Clamp(frame.Realtime - lastRealtime, 0f, 0.1f);

                // Upper bound is not cosmetic: the gate above can hold for a long
                // time (screenshot mode, feature toggled off) while the game keeps
                // ticking, and an unbounded catch-up would advance the integrated
                // scroll phase by minutes of game time in one frame — far enough for
                // the shader's float noise coordinates to lose precision. 0.25 s
                // covers 4x speed down to 15 fps and nothing else can reach it.
                dtGame = Mathf.Clamp((frame.Tick - lastTick) / 60f, 0f, 0.25f);
            }

            lastTick = frame.Tick;
            lastRealtime = frame.Realtime;

            ports.Update(map, frame.Tick, dtReal, dtGame);

            instances.Clear();
            AppendPorts(frame.View);
            AppendBreaches(map, frame.Tick, dtGame, frame.View);

            if (instances.Count == 0)
            {
                return;
            }

            if (renderer == null)
            {
                renderer = new VacuumLeakRenderer();
            }

            try
            {
                renderer.Draw(instances);
            }
            catch (Exception ex)
            {
                // Same containment as the shockwave layer: one broken effect must
                // not spam the log or take the other layers down with it.
                if (!errorLogged)
                {
                    errorLogged = true;
                    Log.Error("[Visual Tweaks] Vacuum leak draw failed: " + ex);
                }

                VacuumLeakBridge.DisableRuntime(ex.Message);
                renderer?.Dispose();
                renderer = null;
                instances.Clear();
            }
        }

        private void AppendPorts(CellRect view)
        {
            foreach (VacuumLeakPort port in ports.All)
            {
                if (port.Smoothed <= 0.004f)
                {
                    continue;
                }

                // One quad spans the room, the opening and the dry-side plume, so
                // there is no overlapping pair to double-blend at the gap. The port
                // carries its own bounding radius: a wide gate's quad is wider, and
                // the old fixed 3.0 would have clipped it while part of it was still
                // on screen.
                if (OutsideView(view, port.Center, port.Reach))
                {
                    continue;
                }

                instances.Add(new VacuumLeakInstance
                {
                    Matrix = port.Matrix,
                    Strength = port.Smoothed,
                    GapV = port.GapV,
                    Phase = port.Phase,
                    Narrow = port.Narrow,
                    LenCells = port.LenCells,
                    Seed = port.Seed,
                });
            }
        }

        private void AppendBreaches(Map map, int tick, float dtGame, CellRect view)
        {
            VacuumBreaches.CollectActive(map, breaches);

            for (int i = 0; i < breaches.Count; i++)
            {
                VacuumBreach breach = breaches[i];
                float strength = breach.Strength(tick);
                if (strength <= 0.004f)
                {
                    continue;
                }

                bool ceiling = breach.Kind == VacuumBreachKind.Ceiling;

                // Pad covers the farthest corner of whatever shape is drawn. The
                // wall strip runs JetLength out into space; the ceiling disc is
                // round, so its pad is just the disc radius.
                float pad = ceiling
                    ? breach.SuctionRadius + 0.6f
                    : VacuumLeakPorts.JetLength + breach.Width * 0.5f + 1f;
                if (OutsideView(view, breach.Center, pad))
                {
                    // Keep the phase integrating even off-screen so a camera pan
                    // never reveals a frozen-then-jumping stream.
                    breach.FlowPhase += dtGame * VacuumLeakPorts.PhaseSpeed(strength);
                    continue;
                }

                breach.FlowPhase += dtGame * VacuumLeakPorts.PhaseSpeed(strength);

                float seed = Mathf.Repeat(breach.StartTick * 0.618034f, 1f);

                if (ceiling)
                {
                    // A ceiling is opened from above, so from the map camera the
                    // air reads as converging on one point instead of crossing a
                    // line. A square quad is scaled to the disc diameter; the
                    // shader clips it to a circle and scrolls noise inward.
                    float diameter = breach.SuctionRadius * 2f;
                    instances.Add(new VacuumLeakInstance
                    {
                        Matrix = Matrix4x4.TRS(
                            new Vector3(breach.Center.x, VacuumLeakRenderer.Altitude, breach.Center.z),
                            Quaternion.identity,
                            new Vector3(diameter, 1f, diameter)),
                        Strength = strength,
                        GapV = 0f,
                        Phase = breach.FlowPhase,
                        Narrow = breach.HoleRadius / breach.SuctionRadius,
                        LenCells = diameter,
                        Seed = seed,
                        Mode = VacuumLeakInstance.ModeCeiling,
                    });
                    continue;
                }

                float quadWidth = breach.Width + VacuumLeakPorts.BreachWidthMargin;
                float narrow = breach.Width / quadWidth;
                Quaternion rotation = Quaternion.LookRotation(breach.Normal, Vector3.up);

                // One strip from deep in the room, through the opening, out into
                // space. Splitting it at the opening made the dry half a mirrored
                // copy with its own seed and a doubling alpha band; a single uv
                // field has no juncture to show. Strength still comes from real
                // quantities (opening width, room volume, pre-demolition vacuum)
                // and the 3-6 s envelope, never a permanent atmosphere.
                float wet = VacuumLeakPorts.StreakLength;
                float dry = VacuumLeakPorts.JetLength;
                float total = wet + dry;
                Vector3 center = breach.Center
                    + breach.Normal * ((dry - wet) * 0.5f);
                center.y = VacuumLeakRenderer.Altitude;

                instances.Add(new VacuumLeakInstance
                {
                    Matrix = Matrix4x4.TRS(
                        center,
                        rotation,
                        new Vector3(quadWidth, 1f, total)),
                    Strength = strength,
                    GapV = wet / total,
                    Phase = breach.FlowPhase,
                    Narrow = narrow,
                    LenCells = total,
                    Seed = seed,
                });
            }
        }

        private static bool OutsideView(CellRect view, Vector3 center, float pad)
        {
            return center.x + pad < view.minX
                || center.x - pad > view.maxX
                || center.z + pad < view.minZ
                || center.z - pad > view.maxZ;
        }

        public void Dispose()
        {
            renderer?.Dispose();
            renderer = null;
            instances.Clear();
            breaches.Clear();
            errorLogged = false;
            lastTick = -1;
        }
    }
}