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

        // Burst diameter follows the pawn's DRAWN size, which is not linear in
        // BodySize: BodySize behaves like a mass/volume scalar while a top-down
        // sprite is 2D, so its linear size tracks bodySize^(2/3). A BodySize-4
        // pawn therefore reads about 2.5x a BodySize-1 sprite, not 4x; vanilla
        // art follows the same curve (centipede BodySize 3 -> 3-cell sprite,
        // scyther 1 -> 1.5). BodySize 1 leaves the user's diameter untouched.
        // The clamp keeps a tiny baby from collapsing to a dot and a BodySize-20
        // modded monster from throwing sparks across half the map.
        private const float BodySizeToSpriteExponent = 2f / 3f;
        private const float MinBodySizeScale = 0.6f;
        private const float MaxBodySizeScale = 4f;

        private MechSparkRenderer? renderer;
        private bool errorLogged;

        internal void Spawn(
            Vector3 position,
            Vector3 travelDir,
            Vector3 impactDir,
            float bias,
            float damage,
            float bodySize)
        {
            TickManager? tickManager = Find.TickManager;
            bool useTicks = tickManager != null;
            int startTick = tickManager?.TicksGame ?? 0;

            // Brightness is the global slider only. Damage drives the spark COUNT,
            // not the brightness: heavier hits throw more sparks, interpolated
            // between the user's lower and upper bounds.
            float intensity = VisualTweaksSettings.mechSparkIntensity;
            float countT = Mathf.Clamp01(
                damage / VisualTweaksSettings.MechSparkDamageForMaxCount);
            int count = Mathf.RoundToInt(Mathf.Lerp(
                VisualTweaksSettings.mechSparkCountMin,
                VisualTweaksSettings.mechSparkCountMax,
                countT));

            float life = Mathf.Max(
                0.05f,
                VisualTweaksSettings.mechSparkDuration * Rand.Range(0.85f, 1.15f));

            // Best-effort attack direction from the armor hook (attacker -> target).
            // A zero direction leaves the burst isotropic via a zero bias; the bias
            // itself is graded by the armor outcome (deflect / diminish / plain).
            Vector3 flatDir = travelDir.Yto0();
            bool hasDir = flatDir.sqrMagnitude > 1e-4f;
            Vector3 dir = hasDir ? flatDir.normalized : Vector3.zero;

            // The transform never changes during the burst's lifetime; identity
            // rotation means the matrix is just scale + translation. The diameter is
            // the user's size setting (cells) times the pawn's drawn-size scale; the
            // shader is scale-invariant, so this sizes the streaks' world length too.
            float sizeScale = Mathf.Clamp(
                Mathf.Pow(bodySize, BodySizeToSpriteExponent),
                MinBodySizeScale,
                MaxBodySizeScale);
            float diameter = VisualTweaksSettings.mechSparkSize * sizeScale;

            // Fire from the hit side of the body instead of its exact centre: shift
            // the quad toward the attack source by half the body radius, with a
            // little lateral scatter so repeated hits do not stack on one dot. With
            // no resolved source, seed the origin inside the silhouette instead, so
            // the burst never starts from a dead-centre pinpoint.
            Vector3 center = position;
            Vector3 flatImpact = impactDir.Yto0();
            float radius = diameter * 0.5f;
            if (flatImpact.sqrMagnitude > 1e-4f)
            {
                flatImpact = flatImpact.normalized;
                Vector3 lateral = new Vector3(-flatImpact.z, 0f, flatImpact.x);
                center += (flatImpact + lateral * Rand.Range(-0.3f, 0.3f))
                    * (radius * 0.5f);
            }
            else
            {
                Vector2 scatter = Rand.InsideUnitCircle * (radius * 0.35f);
                center.x += scatter.x;
                center.z += scatter.y;
            }

            Matrix4x4 matrix = default;
            matrix.m00 = diameter;
            matrix.m11 = 1f;
            matrix.m22 = diameter;
            matrix.m33 = 1f;
            matrix.m03 = center.x;
            matrix.m13 = MechSparkRenderer.Altitude;
            matrix.m23 = center.z;

            TrimToCap();
            bursts.Add(new MechSparkInstance
            {
                X = center.x,
                Z = center.z,
                Life = life,
                InvLife = 1f / life,
                Intensity = intensity,
                Seed = Rand.Value,
                Count = count,
                DirX = dir.x,
                DirZ = dir.z,
                Bias = hasDir ? bias * VisualTweaksSettings.mechSparkDirection : 0f,
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