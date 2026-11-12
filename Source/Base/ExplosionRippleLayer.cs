using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Active explosion shockwaves. Waves are spawned by the Explosion.StartExplosion
    /// postfix; this layer only ages and draws them.
    /// </summary>
    internal sealed class ExplosionRippleLayer : IOverlayLayer
    {
        // Rolling buffer: the oldest wave is dropped when the fixed safety cap is hit.
        // Keeping the list bounded also bounds the draw submissions and the
        // per-frame loop; it is the only unbounded part of this feature.
        private readonly List<ExplosionRippleInstance> ripples =
            new List<ExplosionRippleInstance>();

        private ExplosionRippleRenderer? renderer;
        private bool errorLogged;

        internal void Spawn(Explosion explosion)
        {
            if (explosion == null)
            {
                return;
            }

            // The user asked for radius-linked appearance and no damage-type filter.
            // A missing tick manager is only possible outside a running game; the
            // real-time fallback keeps the effect alive in that case.
            TickManager? tickManager = Find.TickManager;
            bool useTicks = tickManager != null;
            int startTick = tickManager?.TicksGame ?? 0;

            float radius = Mathf.Max(
                0.25f,
                explosion.radius * VisualTweaksSettings.explosionRippleRadiusScale);

            float influence = Mathf.Clamp01(
                VisualTweaksSettings.explosionRippleRadiusInfluence);
            float radiusFactor = Mathf.Pow(
                Mathf.Max(radius, 0.5f) / 4f,
                influence);

            float intensity = Mathf.Max(
                0f,
                VisualTweaksSettings.explosionRippleIntensity * radiusFactor);

            // Larger blasts linger a little longer. The base slider stays the main
            // control; this only adds a gentle radius correlation on top.
            float life = Mathf.Max(
                0.05f,
                VisualTweaksSettings.explosionRippleDuration
                    * Mathf.Clamp(0.8f + 0.05f * radius, 0.8f, 1.5f));

            Vector3 center = explosion.Position.ToVector3Shifted();
            FloatRange? affectedAngle = explosion.affectedAngle;

            // Everything that only depends on the radius is resolved once here, not
            // every frame in the draw loop.
            float sizeT = Mathf.Clamp01(radius / 12f);
            float diameter = radius * 2f * ExplosionRippleRenderer.QuadOvershoot;

            // The transform never changes during the wave's lifetime. Identity
            // rotation means the matrix is just scale + translation, so fill it
            // directly instead of calling Matrix4x4.TRS every frame.
            Matrix4x4 matrix = default;
            matrix.m00 = diameter;
            matrix.m11 = 1f;
            matrix.m22 = diameter;
            matrix.m33 = 1f;
            matrix.m03 = center.x;
            matrix.m13 = ExplosionRippleRenderer.Altitude;
            matrix.m23 = center.z;

            TrimToCap();
            ripples.Add(new ExplosionRippleInstance
            {
                X = center.x,
                Z = center.z,
                Radius = radius,
                Life = life,
                InvLife = 1f / life,
                Intensity = intensity,
                Seed = Rand.Value,
                // A negative maximum is the shader's "no directional mask" sentinel;
                // it saves a separate per-wave flag in the uniform payload.
                AngleMin = affectedAngle?.min ?? 0f,
                AngleMax = affectedAngle?.max ?? -1f,
                BandWidth = Mathf.Lerp(0.17f, 0.13f, sizeT),
                TailLength = Mathf.Lerp(0.34f, 0.26f, sizeT),
                Matrix = matrix,
                StartTick = startTick,
                StartRealtime = Time.realtimeSinceStartup,
                UseTicks = useTicks,
            });
        }

        public void Draw(Map map, in OverlayFrame frame)
        {
            if (!ExplosionRippleBridge.Active || ripples.Count == 0)
            {
                return;
            }

            if (renderer == null)
            {
                renderer = new ExplosionRippleRenderer();
            }

            TrimToCap();

            try
            {
                renderer.Draw(frame.View, ripples, frame.Tick);
            }
            catch (Exception ex)
            {
                // A persistent draw failure must not spam the log or leave a broken
                // renderer alive. Disable the feature for this session instead.
                if (!errorLogged)
                {
                    errorLogged = true;
                    Log.Error("[Visual Tweaks] Explosion shockwave draw failed: " + ex);
                }

                ExplosionRippleBridge.DisableRuntime(ex.Message);
                renderer?.Dispose();
                renderer = null;
                ripples.Clear();
            }
        }

        public void Dispose()
        {
            renderer?.Dispose();
            renderer = null;
            ripples.Clear();
            errorLogged = false;
        }

        private void TrimToCap()
        {
            // Fixed safety bound: 256 simultaneous explosions is effectively
            // unreachable in normal play, so this is not exposed as a setting.
            if (ripples.Count > VisualTweaksSettings.MaxRippleCount)
            {
                ripples.RemoveRange(
                    0,
                    ripples.Count - VisualTweaksSettings.MaxRippleCount);
            }
        }
    }

    internal struct ExplosionRippleInstance
    {
        public float X;
        public float Z;
        public float Radius;
        public float Life;

        // Cached to avoid a division per wave per frame.
        public float InvLife;

        public float Intensity;
        public float Seed;

        // Radius-only values, resolved at spawn. Rebuilding these every frame was
        // pure redundancy: they never change during the wave's lifetime.
        public float BandWidth;
        public float TailLength;
        public Matrix4x4 Matrix;

        public float AngleMin;
        public float AngleMax;

        // Optional circular clip. Shield ripples set this to the shield's outward
        // direction at the impact point and the local curvature of the shield edge;
        // shockwaves leave every field at zero, which the shader reads as "no clip".
        // Kept here rather than in a subclass so the renderer keeps one instance type
        // and one buffer layout.
        public float ClipDirX;
        public float ClipDirZ;
        public float ClipCurvature;
        public float ClipEnabled;

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
