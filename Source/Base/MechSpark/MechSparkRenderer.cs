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
    /// Draws the active spark bursts as world-space quads. Above a small threshold
    /// all visible bursts are submitted as one instanced draw: the per-burst
    /// transform rides in the instance matrix and the animated values travel
    /// through a per-instance storage buffer. Below the threshold the renderer
    /// keeps one cheap DrawMesh per burst. Same two-path structure as the
    /// shockwave renderer, with two vec4 per instance instead of three.
    /// </summary>
    internal sealed class MechSparkRenderer : IDisposable
    {
        // Vanilla Map/MoteGlow's queue: sparks are motes in everything but name,
        // so they keep the stock mote slot — under fog of war and darkness, over
        // the map. Additive blend comes from the ShaderDef.
        private const int RenderQueue = 3151;

        // Same break-even as the shockwave: the instanced setup has a fixed cost
        // in the low microsecond range, a single burst is cheaper with DrawMesh.
        private const int InstancedThreshold = 8;

        // Quad diameter in cells. The shader's maximum spark travel (0.8 local
        // units) plus gravity drift stays inside r == 1 with room for the edge
        // fade, so 1.3 cells of spray radius fits a 2.6-cell quad.
        internal const float QuadSize = 2.6f;

        // One AltInc above the vanilla overhead motes keeps the spray on top of
        // any fleck the impact itself spawned. Waylith orders transparent draws
        // inside a queue by camera depth, so altitude is the only ordering knob.
        internal static readonly float Altitude =
            Altitudes.AltitudeFor(AltitudeLayer.MoteOverhead) + Altitudes.AltInc;

        private static readonly int ParamsId = Shader.PropertyToID("_Params");
        private static readonly int Params2Id = Shader.PropertyToID("_Params2");
        private static readonly int Params3Id = Shader.PropertyToID("_Params3");
        private static readonly int SparkParamsId = Shader.PropertyToID("_SparkParams");

        // Reused every frame; capacity is the hard upper bound from the settings.
        private readonly List<Matrix4x4> instanceMatrices =
            new List<Matrix4x4>(VisualTweaksSettings.MaxSparkInstances);

        // Two vec4 per burst, matching _SparkParams in the shaders:
        //   (t, intensity, seed, count), (dirX, dirZ, bias, unused)
        private readonly NVector4[] instanceParams =
            new NVector4[VisualTweaksSettings.MaxSparkInstances * 2];

        private readonly MaterialPropertyBlock instancedProperties =
            new MaterialPropertyBlock();

        // Small-count path. The engine copies this span straight into its
        // property arena, with no MaterialPropertyBlock dictionary in between.
        private readonly DrawProperty[] drawProperties = new DrawProperty[2];

        private Material? material;
        private ComputeBuffer? paramsBuffer;
        private bool loggedDraw;
        private bool loggedInstanced;

        internal void Draw(
            CellRect view,
            List<MechSparkInstance> bursts,
            int nowTick)
        {
            EnsureMaterial();

            instanceMatrices.Clear();
            int count = 0;

            for (int i = bursts.Count - 1; i >= 0; i--)
            {
                MechSparkInstance burst = bursts[i];
                float age = burst.AgeSeconds(nowTick);
                if (age >= burst.Life)
                {
                    // Keep chronological order: the cap relies on index 0 being
                    // the oldest burst.
                    bursts.RemoveAt(i);
                    continue;
                }

                if (OutsideView(view, burst))
                {
                    continue;
                }

                float t = Mathf.Clamp01(age * burst.InvLife);

                instanceMatrices.Add(burst.Matrix);
                int p = count * 2;
                instanceParams[p] = new NVector4(
                    t,
                    burst.Intensity,
                    burst.Seed,
                    burst.Count);
                instanceParams[p + 1] = new NVector4(
                    burst.DirX,
                    burst.DirZ,
                    burst.Bias,
                    0f);
                count++;
            }

            if (count == 0)
            {
                return;
            }

            if (count <= InstancedThreshold)
            {
                DrawIndividually(count, material);
            }
            else
            {
                DrawInstanced(count, material);
            }

            VisualTweaksLog.TraceOnce(ref loggedDraw, "Mechanoid sparks drawing.");
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
                    VisualTweaksSettings.MaxSparkInstances * 2,
                    16);
                instancedProperties.SetBuffer(SparkParamsId, paramsBuffer);
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
                "Mechanoid sparks instanced batch active (" + count + " bursts).");
        }

        private static bool OutsideView(CellRect view, MechSparkInstance burst)
        {
            float pad = QuadSize * 0.5f;
            return burst.X + pad < view.minX
                || burst.X - pad > view.maxX
                || burst.Z + pad < view.minZ
                || burst.Z - pad > view.maxZ;
        }

        [MemberNotNull(nameof(material))]
        private void EnsureMaterial()
        {
            if (material != null)
            {
                return;
            }

            ShaderDef? def = MechSparkBridge.SparkShaderDef;
            if (def == null)
            {
                throw new InvalidOperationException(
                    "Mechanoid spark shader def is not available.");
            }

            material = new Material((Shader)def)
            {
                name = "VisualTweaksMechSparkMat",
                renderQueue = RenderQueue,
            };

            // Look-only constants, identical for every burst:
            // (gravity drift, trail length, flash sigma, maximum travel).
            material.SetVector(Params3Id, new Vector4(0.22f, 0.16f, 0.14f, 0.8f));
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