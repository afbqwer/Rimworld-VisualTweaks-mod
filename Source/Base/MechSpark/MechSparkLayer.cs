using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Active metal-hit spark bursts. Bursts are spawned by the ArmorUtility patch
    /// when damage is absorbed by metal; this layer only ages and draws them. All
    /// particle motion lives in the fragment shader, so an instance is just a handful
    /// of floats — there is no per-spark CPU state at all.
    /// </summary>
    internal sealed class MechSparkLayer : IOverlayLayer
    {
        // Rolling buffer, same rationale as the shockwave layer: the cap bounds
        // both the draw submissions and the per-frame loop. A firefight heavy
        // enough to exceed 128 simultaneous bursts is dropping the oldest ones,
        // which are nearly faded anyway.
        private readonly List<MechSparkInstance> bursts =
            new List<MechSparkInstance>();

        private MechSparkRenderer? renderer;
        private bool errorLogged;

        internal void Spawn(Vector3 position, Vector3 travelDir, float damage)
        {
            TickManager? tickManager = Find.TickManager;
            bool useTicks = tickManager != null;
            int startTick = tickManager?.TicksGame ?? 0;

            // Heavier rounds throw a little more light, but the slider stays the
            // main control: the damage factor only spans 0.7x..1.6x.
            float intensity = VisualTweaksSettings.mechSparkIntensity
                * Mathf.Clamp(0.7f + damage / 60f, 0.7f, 1.6f);

            float life = Mathf.Max(
                0.05f,
                VisualTweaksSettings.mechSparkDuration * Rand.Range(0.85f, 1.15f));

            // The spray fans away from the shooter. A zero direction (no launch
            // origin captured) leaves the burst isotropic via a zero bias.
            Vector3 flatDir = travelDir.Yto0();
            bool hasDir = flatDir.sqrMagnitude > 1e-4f;
            Vector3 dir = hasDir ? flatDir.normalized : Vector3.zero;

            // The transform never changes during the burst's lifetime; identity
            // rotation means the matrix is just scale + translation.
            float diameter = MechSparkRenderer.QuadSize;
            Matrix4x4 matrix = default;
            matrix.m00 = diameter;
            matrix.m11 = 1f;
            matrix.m22 = diameter;
            matrix.m33 = 1f;
            matrix.m03 = position.x;
            matrix.m13 = MechSparkRenderer.Altitude;
            matrix.m23 = position.z;

            TrimToCap();
            bursts.Add(new MechSparkInstance
            {
                X = position.x,
                Z = position.z,
                Life = life,
                InvLife = 1f / life,
                Intensity = intensity,
                Seed = Rand.Value,
                Count = VisualTweaksSettings.mechSparkCount,
                DirX = dir.x,
                DirZ = dir.z,
                Bias = hasDir ? 0.5f : 0f,
                Matrix = matrix,
                StartTick = startTick,
                StartRealtime = Time.realtimeSinceStartup,
                UseTicks = useTicks,
            });
        }

        public void Draw(Map map, in OverlayFrame frame)
        {
            if (!MechSparkBridge.Active || bursts.Count == 0)
            {
                return;
            }

            if (renderer == null)
            {
                renderer = new MechSparkRenderer();
            }

            TrimToCap();

            try
            {
                renderer.Draw(frame.View, bursts, frame.Tick);
            }
            catch (Exception ex)
            {
                // A persistent draw failure must not spam the log or leave a broken
                // renderer alive. Disable the feature for this session instead.
                if (!errorLogged)
                {
                    errorLogged = true;
                    Log.Error("[Visual Tweaks] Metal-hit spark draw failed: " + ex);
                }

                MechSparkBridge.DisableRuntime(ex.Message);
                renderer?.Dispose();
                renderer = null;
                bursts.Clear();
            }
        }

        public void Dispose()
        {
            renderer?.Dispose();
            renderer = null;
            bursts.Clear();
            errorLogged = false;
        }

        private void TrimToCap()
        {
            if (bursts.Count > VisualTweaksSettings.MaxSparkInstances)
            {
                bursts.RemoveRange(
                    0,
                    bursts.Count - VisualTweaksSettings.MaxSparkInstances);
            }
        }
    }

    internal struct MechSparkInstance
    {
        public float X;
        public float Z;
        public float Life;

        // Cached to avoid a division per burst per frame.
        public float InvLife;

        public float Intensity;
        public float Seed;
        public int Count;

        // Optional travel direction (world xz, unit) and the backward bias share.
        // The armor hook passes zero, so bursts are isotropic; the directional path
        // is kept as a capability (the preview exercises it).
        public float DirX;
        public float DirZ;
        public float Bias;

        public Matrix4x4 Matrix;

        public int StartTick;
        public float StartRealtime;
        public bool UseTicks;

        public float AgeSeconds(int nowTick)
        {
            // Tick time freezes with a paused game and accelerates with game speed,
            // matching RimWorld's own flecks. Real time is only a fallback for the
            // brief periods where no tick manager exists.
            float age = UseTicks
                ? (nowTick - StartTick) / 60f
                : Time.realtimeSinceStartup - StartRealtime;

            return age > 0f ? age : 0f;
        }
    }
}