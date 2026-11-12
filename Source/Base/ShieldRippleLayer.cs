using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Ripples on projectile-interceptor shields at the moment a shot is stopped.
    ///
    /// This does not own a shader. A shield hit and an explosion shockwave are the
    /// same shape on screen — a ring expanding on a ground-plane quad — so the
    /// ripples are queued through the existing shockwave renderer with their own
    /// material constants. Two reasons that is the right call and not just the cheap
    /// one:
    ///
    /// 1. The shockwave scene grab is taken at queue 3178, which is after the shield
    ///    itself (MoteGlow, default 3000). The refracted sample therefore already
    ///    contains the shield's own glow ring, and the ripple visibly bends it —
    ///    exactly the "the surface is being disturbed" read we want.
    /// 2. A second shader with its own scene grab would mean a second full-frame
    ///    copy every frame. Shield hits are not rare: a mech cluster shield can soak
    ///    dozens of bullets per second in a raid. Refraction-by-reuse costs nothing
    ///    extra when no ripple is alive.
    ///
    /// Where the ripple goes: <see cref="CompProjectileInterceptor.CheckIntercept"/>
    /// only fires when the projectile segment crosses the circle outline
    /// (GenGeo.IntersectLineCircleOutline), so the impact is always within one
    /// tile-step of the shield rim. The spawn point is pinned onto that rim, which
    /// is also the only honest answer in a top-down view: the dome is drawn as a flat
    /// disc, so "on the surface" means "on the silhouette".
    ///
    /// The ripple is then clipped to the inside of the dome by the shader, so only the
    /// near half — the half that is actually on the visible surface — is drawn. See the
    /// clip block in VisualTweaksExplosionRipple.frag.glsl.
    /// </summary>
    internal sealed class ShieldRippleLayer : IOverlayLayer
    {
        /// <summary>
        /// Independent of <see cref="VisualTweaksSettings.MaxRippleCount"/>: a shield
        /// ripple is a fraction of the size of a shockwave and several hits often land
        /// inside one animation, so a much smaller ceiling still never looks starved.
        /// </summary>
        private const int MaxRipples = 64;

        /// <summary>
        /// Material constants for this renderer instance. The renderer creates its own
        /// material, so these do not leak into the explosion shockwaves.
        ///
        /// _Params3 = (radial noise amplitude, angular frequency, angular softness, -).
        /// The angular frequency is lower than the shockwave's because the quad is small:
        /// the same frequency would read as jitter instead of a bend.
        ///
        /// _Params4 = (refraction as a fraction of the quad radius, crest gain, -, -).
        /// The refraction sits between the shockwave's 0.18 and a hard lens: strong
        /// enough to visibly displace the shield's ring, weak enough that a ripple on a
        /// 5-cell shield does not smear the pawns standing under it.
        /// </summary>
        private static readonly Vector4 RippleParams3 = new Vector4(0.012f, 2.4f, 6f, 0f);

        private static readonly Vector4 RippleParams4 = new Vector4(0.22f, 0.12f, 0f, 0f);

        private readonly List<ExplosionRippleInstance> ripples =
            new List<ExplosionRippleInstance>(MaxRipples);

        private ExplosionRippleRenderer? renderer;
        private bool errorLogged;

        // Same shield, same tick: collapse to one ripple. A burst that all resolves in
        // one tick would otherwise stack N identical rings on the same pixel, which
        // reads as a single brighter ring at N times the fill cost.
        private int lastSpawnShieldId = -1;
        private int lastSpawnTick = -1;

        /// <summary>
        /// True when shield ripples should appear. The shader belongs to the shockwave
        /// feature, so it has to be resolved and compiled — but the shockwave's own
        /// on/off switch is deliberately not part of this gate: turning off explosion
        /// shockwaves must not silently turn off shield feedback.
        /// </summary>
        internal static bool Active =>
            ExplosionRippleBridge.ShaderReady
            && VisualTweaksSettings.shieldRippleEnabled
            && !ScreenshotModeActive;

        private static bool ScreenshotModeActive
        {
            get
            {
                ScreenshotModeHandler? handler = Find.ScreenshotModeHandler;
                return handler != null && handler.Active;
            }
        }

        /// <summary>
        /// Called from the interceptor patch. <paramref name="hitPos"/> is the
        /// projectile's position after the step that crossed the shield.
        /// </summary>
        internal void Spawn(CompProjectileInterceptor interceptor, Vector3 hitPos)
        {
            if (interceptor?.parent == null || !interceptor.parent.Spawned)
            {
                return;
            }

            TickManager? tickManager = Find.TickManager;
            if (tickManager == null)
            {
                return;
            }

            float shieldRadius = interceptor.Props.radius;
            if (!(shieldRadius > 0.01f))
            {
                return;
            }

            int now = tickManager.TicksGame;
            int shieldId = interceptor.parent.thingIDNumber;
            if (shieldId == lastSpawnShieldId && now == lastSpawnTick)
            {
                return;
            }

            Vector3 center = interceptor.parent.DrawPos;
            float dx = hitPos.x - center.x;
            float dz = hitPos.z - center.z;
            float distSq = dx * dx + dz * dz;
            if (distSq < 1e-6f)
            {
                // Dead centre: no direction to place the ripple along. Only reachable
                // when a projectile spawns inside the shield and the geometry gives us
                // nothing; one skipped frame of feedback is the right trade.
                return;
            }

            // Pin the ripple exactly on the silhouette. Two reasons, and the second one
            // is what makes the shader clip exact:
            //
            // 1. The segment that triggered this call crossed the circle outline, so the
            //    impact is within one tile-step of the rim. Pulling it onto the rim is a
            //    sub-tile correction, not a relocation.
            // 2. The clip term below assumes the impact point lies on the shield sphere.
            //    If it were allowed to sit inside, the local circle would need a second
            //    constant term and the ripple would be clipped into a sliver.
            //
            // A projectile stopped at the exact centre has no usable direction and was
            // rejected above, so there is no case left where pinning to the rim is wrong
            // by more than that one tile-step.
            float dist = Mathf.Sqrt(distSq);
            float dirX = dx / dist;
            float dirZ = dz / dist;

            float x = center.x + dirX * shieldRadius;
            float z = center.z + dirZ * shieldRadius;

            // The ripple is a fraction of the shield: a fixed size would swallow a
            // 2-cell low-shield and vanish on a 25-cell gravship bubble.
            float radius = Mathf.Clamp(shieldRadius * 0.2f, 0.55f, 2.2f);
            float life = Mathf.Clamp(
                VisualTweaksSettings.shieldRippleDuration,
                VisualTweaksSettings.MinShieldRippleDuration,
                VisualTweaksSettings.MaxShieldRippleDuration);

            // Clip curvature in quad-local units: 1 / (2 * R_local) where
            // R_local = shieldRadius / quadRadius. Large shields converge on 0, which
            // degenerates to a straight cut — correct, since their edge is nearly flat
            // across a ripple this small.
            float quadRadius = radius * ExplosionRippleRenderer.QuadOvershoot;
            float clipCurvature = quadRadius / (2f * shieldRadius);

            float diameter = radius * 2f * ExplosionRippleRenderer.QuadOvershoot;

            Matrix4x4 matrix = default;
            matrix.m00 = diameter;
            matrix.m11 = 1f;
            matrix.m22 = diameter;
            matrix.m33 = 1f;
            matrix.m03 = x;
            matrix.m13 = ExplosionRippleRenderer.Altitude;
            matrix.m23 = z;

            TrimToCap();
            ripples.Add(new ExplosionRippleInstance
            {
                X = x,
                Z = z,
                Radius = radius,
                Life = life,
                InvLife = 1f / life,
                Intensity = VisualTweaksSettings.shieldRippleIntensity,
                Seed = Rand.Value,
                AngleMin = 0f,
                AngleMax = -1f,
                // A thin band: the whole point is that it reads as a snap rather than a
                // wave. The shockwave's 0.13-0.17 is normalised against a quad several
                // tiles across; on a 1-cell quad the same number would be a thick blob,
                // so this goes the other way and stays tight. The tail is near zero for
                // the same reason — a rarefaction wake needs room to read as a wake.
                BandWidth = 0.16f,
                TailLength = 0.1f,
                ClipDirX = dirX,
                ClipDirZ = dirZ,
                ClipCurvature = clipCurvature,
                ClipEnabled = 1f,
                Matrix = matrix,
                StartTick = now,
                StartRealtime = Time.realtimeSinceStartup,
                UseTicks = true,
            });

            lastSpawnShieldId = shieldId;
            lastSpawnTick = now;
        }

        public void Draw(Map map, in OverlayFrame frame)
        {
            if (!Active || ripples.Count == 0)
            {
                return;
            }

            if (renderer == null)
            {
                renderer = new ExplosionRippleRenderer(RippleParams3, RippleParams4);
            }

            TrimToCap();

            try
            {
                renderer.Draw(frame.View, ripples, frame.Tick);
            }
            catch (Exception ex)
            {
                // Same policy as the shockwave layer: one broken effect disables itself
                // for the session instead of spamming the log or drawing garbage.
                if (!errorLogged)
                {
                    errorLogged = true;
                    Log.Error("[Visual Tweaks] Shield ripple draw failed: " + ex);
                }

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
            lastSpawnShieldId = -1;
            lastSpawnTick = -1;
        }

        private void TrimToCap()
        {
            if (ripples.Count > MaxRipples)
            {
                ripples.RemoveRange(0, ripples.Count - MaxRipples);
            }
        }
    }
}
