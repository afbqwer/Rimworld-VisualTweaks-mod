using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Active building-impact bursts (debris chips + dust/smoke). Spawned by the
    /// Thing.TakeDamage patch when damage lands on a building; this layer only
    /// ages and draws them. All motion lives in the fragment shader — an instance
    /// is a handful of floats, no per-chip CPU state.
    ///
    /// One lifetime covers both phases: the shader spends the first fraction of
    /// it on debris (fast, gravity-driven) and the full span on smoke (slow
    /// expansion and dissipation), so a single quad per hit carries the whole
    /// effect.
    /// </summary>
    internal sealed class BuildingImpactLayer : IOverlayLayer
    {
        // Rolling buffer, same rationale as the spark layer: the cap bounds both
        // draw submissions and the per-frame loop. A firefight dense enough to
        // exceed 128 simultaneous bursts drops the oldest, nearly faded ones.
        private readonly List<BuildingImpactInstance> bursts =
            new List<BuildingImpactInstance>();

        private BuildingImpactRenderer? renderer;
        private bool errorLogged;

        internal void Spawn(
            Vector3 position,
            Vector3 faceNormal,
            Vector3 travelDir,
            float bias,
            float damage,
            Color materialColor,
            bool destroyed)
        {
            TickManager? tickManager = Find.TickManager;
            bool useTicks = tickManager != null;
            int startTick = tickManager?.TicksGame ?? 0;

            // Damage drives the debris COUNT between the user's bounds; a
            // destroyed building throws a noticeably bigger burst. Brightness
            // stays the global slider only, same contract as the sparks.
            float countT = Mathf.Clamp01(
                damage / VisualTweaksSettings.BuildingImpactDamageForMaxCount);
            float count = Mathf.Lerp(
                VisualTweaksSettings.buildingImpactCountMin,
                VisualTweaksSettings.buildingImpactCountMax,
                countT);
            if (destroyed)
            {
                count *= 1.6f;
            }
            int debrisCount = Mathf.Clamp(Mathf.RoundToInt(count), 1, 24);

            float smoke = VisualTweaksSettings.buildingImpactSmoke * (destroyed ? 1.7f : 1f);
            float intensity = VisualTweaksSettings.buildingImpactIntensity;

            float life = Mathf.Max(
                0.2f,
                VisualTweaksSettings.buildingImpactDuration
                    * Rand.Range(0.85f, 1.15f)
                    * (destroyed ? 1.3f : 1f));

            Vector3 flatDir = travelDir.Yto0();
            bool hasDir = flatDir.sqrMagnitude > 1e-4f;
            Vector3 dir = hasDir ? flatDir.normalized : Vector3.zero;

            // Outward normal of the struck face. The shader keeps every chip in
            // that half-space and clips whatever would cross the wall, so a
            // horizontal vector is all this needs. A zero normal (no usable
            // face) disables the constraint rather than guessing.
            Vector3 flatNormal = faceNormal.Yto0();
            bool hasNormal = flatNormal.sqrMagnitude > 1e-4f;
            if (hasNormal)
            {
                flatNormal = flatNormal.normalized;
            }

            // Keep the spray on the outside even if the attack direction points
            // into the wall (grazing fire): the bias target is projected onto
            // the outside half-space. The shader also enforces the half-space
            // per chip; this only makes the average fan lean the right way.
            if (hasNormal && hasDir && Vector3.Dot(dir, flatNormal) < 0f)
            {
                dir -= flatNormal * Vector3.Dot(dir, flatNormal);
                dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : -flatNormal;
            }

            // Identity rotation: the matrix is scale + translation only. The
            // shader is scale-invariant in local units, so the user's size
            // setting needs no shader change. Destruction widens the quad too.
            float diameter = VisualTweaksSettings.buildingImpactSize * (destroyed ? 1.25f : 1f);
            Matrix4x4 matrix = default;
            matrix.m00 = diameter;
            matrix.m11 = 1f;
            matrix.m22 = diameter;
            matrix.m33 = 1f;
            matrix.m03 = position.x;
            matrix.m13 = BuildingImpactRenderer.Altitude;
            matrix.m23 = position.z;

            TrimToCap();
            bursts.Add(new BuildingImpactInstance
            {
                X = position.x,
                Z = position.z,
                Life = life,
                InvLife = 1f / life,
                Intensity = intensity,
                Seed = Rand.Value,
                Count = debrisCount,
                DirX = dir.x,
                DirZ = dir.z,
                Bias = hasDir ? bias : 0f,
                NormalX = hasNormal ? flatNormal.x : 0f,
                NormalZ = hasNormal ? flatNormal.z : 0f,
                FaceClip = hasNormal ? 1f : 0f,
                Smoke = smoke,
                ColR = materialColor.r,
                ColG = materialColor.g,
                ColB = materialColor.b,
                Matrix = matrix,
                StartTick = startTick,
                StartRealtime = Time.realtimeSinceStartup,
                UseTicks = useTicks,
            });
        }

        public void Draw(Map map, in OverlayFrame frame)
        {
            if (!BuildingImpactBridge.Active || bursts.Count == 0)
            {
                return;
            }

            if (renderer == null)
            {
                renderer = new BuildingImpactRenderer();
            }

            TrimToCap();

            try
            {
                renderer.Draw(frame.View, bursts, frame.Tick);
            }
            catch (Exception ex)
            {
                // A persistent draw failure must not spam the log or leave a
                // broken renderer alive. Disable the feature for this session.
                if (!errorLogged)
                {
                    errorLogged = true;
                    Log.Error("[Visual Tweaks] Building impact draw failed: " + ex);
                }

                BuildingImpactBridge.DisableRuntime(ex.Message);
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
            if (bursts.Count > VisualTweaksSettings.MaxBuildingImpactInstances)
            {
                bursts.RemoveRange(
                    0,
                    bursts.Count - VisualTweaksSettings.MaxBuildingImpactInstances);
            }
        }
    }

    internal struct BuildingImpactInstance
    {
        public float X;
        public float Z;
        public float Life;

        // Cached to avoid a division per burst per frame.
        public float InvLife;

        public float Intensity;
        public float Seed;
        public int Count;

        // Travel direction (world xz, unit) and the backward bias share; zero
        // direction leaves the spray isotropic.
        public float DirX;
        public float DirZ;
        public float Bias;

        // Outward normal of the struck face (world xz, unit) and the 1/0 flag
        // that enables the half-space constraint + face clip in the shader.
        public float NormalX;
        public float NormalZ;
        public float FaceClip;

        // Smoke amount (0 disables the smoke phase) and the material colour the
        // debris chips and dust are tinted with.
        public float Smoke;
        public float ColR;
        public float ColG;
        public float ColB;

        public Matrix4x4 Matrix;

        public int StartTick;
        public float StartRealtime;
        public bool UseTicks;

        public float AgeSeconds(int nowTick)
        {
            // Tick time freezes with a paused game and accelerates with game
            // speed, matching RimWorld's own flecks. Real time is only a
            // fallback for the brief periods without a tick manager.
            float age = UseTicks
                ? (nowTick - StartTick) / 60f
                : Time.realtimeSinceStartup - StartRealtime;

            return age > 0f ? age : 0f;
        }
    }
}