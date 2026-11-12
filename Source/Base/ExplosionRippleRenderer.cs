using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using DrawProperty = Waylith.Rendering.DrawProperty;
using DrawPropertyBlock = Waylith.Rendering.DrawPropertyBlock;
using NVector4 = System.Numerics.Vector4;

namespace VisualTweaks
{
    /// <summary>
    /// Draws the active shockwaves as scaled, world-space quads. Above a small
    /// threshold all visible waves are submitted as one instanced draw: the
    /// per-wave transform rides in the instance matrix and the animated values
    /// travel through a per-instance storage buffer. Below the threshold the
    /// renderer keeps one cheap DrawMesh per wave, which avoids the fixed cost
    /// of an instanced submission for a single explosion.
    /// </summary>
    internal sealed class ExplosionRippleRenderer : IDisposable
    {
        private const int RenderQueue = 3178;

        // A single wave is ~0.2 µs with the plain path; the instanced setup has a
        // fixed cost (buffer rent/write, descriptor set, binding list) in the low
        // microsecond range, so only batch once there are enough waves to pay for
        // it. Eight is a conservative break-even; the two paths share all state.
        private const int InstancedThreshold = 8;

        // The quad is slightly larger than the visible maximum radius so the soft
        // leading edge is not clipped by the mesh boundary.
        internal const float QuadOvershoot = 1.15f;
        private const float RingFrontAtEdge = 1f / QuadOvershoot;

        // Constant for the whole session (altitude layers are a fixed ladder), so
        // resolve it once instead of through an extension method per frame.
        internal static readonly float Altitude =
            Altitudes.AltitudeFor(AltitudeLayer.MoteOverhead);

        private static readonly int ParamsId = Shader.PropertyToID("_Params");
        private static readonly int Params2Id = Shader.PropertyToID("_Params2");
        private static readonly int Params3Id = Shader.PropertyToID("_Params3");
        private static readonly int Params4Id = Shader.PropertyToID("_Params4");
        private static readonly int RippleParamsId = Shader.PropertyToID("_RippleParams");

        // Reused every frame; capacity is the hard upper bound from the settings.
        private readonly List<Matrix4x4> instanceMatrices =
            new List<Matrix4x4>(VisualTweaksSettings.MaxRippleCount);

        // Two vec4 per wave, matching _RippleParams in the shaders:
        //   (t, front, width, strength), (tail, seed, angleMin, angleMax)
        private readonly NVector4[] instanceParams =
            new NVector4[VisualTweaksSettings.MaxRippleCount * 2];

        private readonly MaterialPropertyBlock instancedProperties =
            new MaterialPropertyBlock();

        // Small-count path. The engine copies this span straight into its
        // property arena, with no MaterialPropertyBlock dictionary in between.
        private readonly DrawProperty[] drawProperties = new DrawProperty[2];

        // Created on first draw (the shader def is not loaded yet at construction) and
        // released by Dispose. Nullable is the honest type here; EnsureMaterial and the
        // creation block in DrawInstanced are the only places that can make it non-null.
        private Material? material;
        private ComputeBuffer? paramsBuffer;
        private bool loggedDraw;
        private bool loggedInstanced;

        // Look-only constants, applied to this instance's own material. They are
        // constructor arguments rather than hardcoded values because the shield ripple
        // layer borrows this renderer with a tighter noise and a stronger lens; a
        // second copy of the draw code would be 240 lines of duplicated GPU plumbing
        // for four numbers.
        private readonly Vector4 params3;
        private readonly Vector4 params4;

        /// <summary>Explosion shockwave look: broad warp, mild lens.</summary>
        internal ExplosionRippleRenderer()
            : this(
                new Vector4(0.02f, 3.2f, 8f, 0f),
                new Vector4(0.18f, 0.06f, 0f, 0f))
        {
        }

        internal ExplosionRippleRenderer(Vector4 params3, Vector4 params4)
        {
            this.params3 = params3;
            this.params4 = params4;
        }

        internal void Draw(
            CellRect view,
            List<ExplosionRippleInstance> ripples,
            int nowTick)
        {
            EnsureMaterial();

            instanceMatrices.Clear();
            int count = 0;

            for (int i = ripples.Count - 1; i >= 0; i--)
            {
                ExplosionRippleInstance ripple = ripples[i];
                float age = ripple.AgeSeconds(nowTick);
                if (age >= ripple.Life)
                {
                    // Keep chronological order: the cap relies on index 0 being the
                    // oldest wave. The shift is O(n) but only happens once per wave.
                    ripples.RemoveAt(i);
                    continue;
                }

                if (OutsideView(view, ripple))
                {
                    continue;
                }

                float t = Mathf.Clamp01(age * ripple.InvLife);

                // Fast at first, then slowing down: a real shock front decelerates.
                float eased = 1f - (1f - t) * (1f - t);
                float front = Mathf.Lerp(0.02f, RingFrontAtEdge, eased);

                // The band keeps its full strength for most of the animation and
                // softens near the end as it leaves the quad.
                float strength = ripple.Intensity
                    * Mathf.Lerp(1f, 0.45f, t * t);
                if (strength <= 0.001f)
                {
                    continue;
                }

                instanceMatrices.Add(ripple.Matrix);
                int p = count * 2;
                instanceParams[p] = new NVector4(
                    t,
                    front,
                    ripple.BandWidth,
                    strength);
                instanceParams[p + 1] = new NVector4(
                    ripple.TailLength,
                    ripple.Seed,
                    ripple.AngleMin,
                    ripple.AngleMax);
                count++;
            }

            if (count == 0)
            {
                return;
            }

            // EnsureMaterial above narrows the field, but the flow analysis does not
            // carry that into a callee, so hand the handle over explicitly.
            if (count <= InstancedThreshold)
            {
                DrawIndividually(count, material);
            }
            else
            {
                DrawInstanced(count, material);
            }

            VisualTweaksLog.TraceOnce(ref loggedDraw, "Explosion shockwave drawing.");
        }

        private void DrawIndividually(int count, Material material)
        {
            for (int i = 0; i < count; i++)
            {
                int p = i * 2;
                drawProperties[0] = DrawProperty.Vector(ParamsId, instanceParams[p]);
                drawProperties[1] = DrawProperty.Vector(Params2Id, instanceParams[p + 1]);

                Graphics.DrawMesh(
                    MeshPool.plane10,
                    instanceMatrices[i],
                    material,
                    0,
                    null,
                    0,
                    new DrawPropertyBlock(drawProperties));
            }
        }

        private void DrawInstanced(int count, Material material)
        {
            if (paramsBuffer == null)
            {
                // Sized once for the absolute maximum; only the used prefix is
                // written each frame. 16-byte stride = one vec4.
                paramsBuffer = new ComputeBuffer(
                    VisualTweaksSettings.MaxRippleCount * 2,
                    16);
                instancedProperties.SetBuffer(RippleParamsId, paramsBuffer);
            }

            paramsBuffer.SetData(instanceParams, 0, 0, count * 2);

            Graphics.DrawMeshInstanced(
                MeshPool.plane10,
                0,
                material,
                instanceMatrices,
                instancedProperties);

            VisualTweaksLog.TraceOnce(
                ref loggedInstanced,
                "Explosion shockwave instanced batch active (" + count + " waves).");
        }

        private static bool OutsideView(CellRect view, ExplosionRippleInstance ripple)
        {
            float pad = ripple.Radius * QuadOvershoot;
            return ripple.X + pad < view.minX
                || ripple.X - pad > view.maxX
                || ripple.Z + pad < view.minZ
                || ripple.Z - pad > view.maxZ;
        }

        [MemberNotNull(nameof(material))]
        private void EnsureMaterial()
        {
            if (material != null)
            {
                return;
            }

            ShaderDef? def = ExplosionRippleBridge.RippleShaderDef;
            if (def == null)
            {
                throw new InvalidOperationException(
                    "Explosion shockwave shader def is not available.");
            }

            material = new Material((Shader)def)
            {
                name = "VisualTweaksExplosionRippleMat",
                renderQueue = RenderQueue,
            };

            // Look-only constants: identical for every wave, so they live on the
            // shared material. _Params3 = (noise amplitude, angular frequency,
            // angular softness); _Params4 = (refraction of the visual radius,
            // crest gain).
            material.SetVector(Params3Id, params3);
            material.SetVector(Params4Id, params4);
        }

        public void Dispose()
        {
            if (paramsBuffer != null)
            {
                paramsBuffer.Dispose();
                paramsBuffer = null;
            }

            if (material != null)
            {
                UnityEngine.Object.Destroy(material);
                material = null;
            }
        }
    }
}