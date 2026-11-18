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
    /// Draws the active building-impact bursts as world-space quads. Same
    /// two-path structure as the spark renderer: above a small threshold all
    /// visible bursts go into one instanced draw (transform in the instance
    /// matrix, animated values in a per-instance storage buffer, four vec4 per
    /// instance — the fourth carries the struck face normal for the wall clip);
    /// below it, one cheap DrawMesh per burst.
    ///
    /// Unlike the additive sparks, debris and dust are matter: the ShaderDef
    /// uses alpha blending, so the chips occlude what is behind them instead of
    /// glowing through it.
    /// </summary>
    internal sealed class BuildingImpactRenderer : IDisposable
    {
        // Vanilla Map/Mote's queue: the burst is a mote in everything but name,
        // so it keeps the stock mote slot — under fog of war and darkness, over
        // the map. Alpha blend comes from the ShaderDef.
        private const int RenderQueue = 3151;

        // Same break-even as the sparks: the instanced setup has a fixed cost in
        // the low microsecond range, a single burst is cheaper with DrawMesh.
        private const int InstancedThreshold = 8;

        // Deliberately BELOW the door/building altitude (14) and above the
        // ground layers: the map mesh writes depth, so at this height the wall's
        // own pixels occlude the burst per-pixel — corners, silhouettes and
        // alpha-cutout edges included — instead of a geometric plane clip. This
        // is why the smoke no longer needs a face clip: it simply draws under
        // the wall. Higher altitudes would pass the depth test against walls and
        // paint over them (that is why vanilla motes sit above buildings).
        internal static readonly float Altitude =
            Altitudes.AltitudeFor(AltitudeLayer.DoorMoveable) - Altitudes.AltInc;

        private static readonly int ParamsId = Shader.PropertyToID("_Params");
        private static readonly int Params2Id = Shader.PropertyToID("_Params2");
        private static readonly int Params3Id = Shader.PropertyToID("_Params3");
        private static readonly int Params4Id = Shader.PropertyToID("_Params4");
        private static readonly int Params5Id = Shader.PropertyToID("_Params5");
        private static readonly int ImpactParamsId = Shader.PropertyToID("_ImpactParams");

        // Reused every frame; capacity is the hard upper bound from the settings.
        private readonly List<Matrix4x4> instanceMatrices =
            new List<Matrix4x4>(VisualTweaksSettings.MaxBuildingImpactInstances);

        // Four vec4 per burst, matching _ImpactParams in the shaders:
        //   (t, intensity, seed, count), (dirX, dirZ, bias, smoke),
        //   (colR, colG, colB, face clip on/off), (normalX, normalZ, -, -)
        private readonly NVector4[] instanceParams =
            new NVector4[VisualTweaksSettings.MaxBuildingImpactInstances * 4];

        private readonly MaterialPropertyBlock instancedProperties =
            new MaterialPropertyBlock();

        // Small-count path. The engine copies this span straight into its
        // property arena, with no MaterialPropertyBlock dictionary in between.
        private readonly DrawProperty[] drawProperties = new DrawProperty[4];

        private Material? material;
        private ComputeBuffer? paramsBuffer;
        private bool loggedDraw;
        private bool loggedInstanced;

        internal void Draw(
            CellRect view,
            List<BuildingImpactInstance> bursts,
            int nowTick)
        {
            EnsureMaterial();

            instanceMatrices.Clear();
            int count = 0;

            for (int i = bursts.Count - 1; i >= 0; i--)
            {
                BuildingImpactInstance burst = bursts[i];
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
                int p = count * 4;
                instanceParams[p] = new NVector4(
                    t,
                    burst.Intensity,
                    burst.Seed,
                    burst.Count);
                instanceParams[p + 1] = new NVector4(
                    burst.DirX,
                    burst.DirZ,
                    burst.Bias,
                    burst.Smoke);
                instanceParams[p + 2] = new NVector4(
                    burst.ColR,
                    burst.ColG,
                    burst.ColB,
                    burst.FaceClip);
                instanceParams[p + 3] = new NVector4(
                    burst.NormalX,
                    burst.NormalZ,
                    0f,
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

            VisualTweaksLog.TraceOnce(ref loggedDraw, "Building impact debris drawing.");
        }

        private void DrawIndividually(int count, Material material)
        {
            for (int i = 0; i < count; i++)
            {
                int p = i * 4;
                drawProperties[0] = DrawProperty.Vector(ParamsId, instanceParams[p]);
                drawProperties[1] = DrawProperty.Vector(Params2Id, instanceParams[p + 1]);
                drawProperties[2] = DrawProperty.Vector(Params4Id, instanceParams[p + 2]);
                drawProperties[3] = DrawProperty.Vector(Params5Id, instanceParams[p + 3]);

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
                    VisualTweaksSettings.MaxBuildingImpactInstances * 4,
                    16);
                instancedProperties.SetBuffer(ImpactParamsId, paramsBuffer);
            }

            paramsBuffer.SetData(instanceParams, 0, 0, count * 4);

            Graphics.DrawMeshInstanced(
                MeshPool.plane10,
                0,
                material,
                instanceMatrices,
                instancedProperties);

            VisualTweaksLog.TraceOnce(
                ref loggedInstanced,
                "Building impact debris instanced batch active (" + count + " bursts).");
        }

        private static bool OutsideView(CellRect view, BuildingImpactInstance burst)
        {
            float pad = burst.Matrix.m00 * 0.5f;
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

            ShaderDef? def = BuildingImpactBridge.ImpactShaderDef;
            if (def == null)
            {
                throw new InvalidOperationException(
                    "Building impact shader def is not available.");
            }

            material = new Material((Shader)def)
            {
                name = "VisualTweaksBuildingImpactMat",
                renderQueue = RenderQueue,
            };

            // Look-only constants, identical for every burst:
            // (chip path curl, max debris travel, debris life fraction, smoke
            // drift). The debris phase occupies the first 0.7 of the burst life
            // so chips settle visibly — their fall is a shrink, not a fade —
            // and smoke spans the whole life, lingering after they land. No
            // screen-space gravity: the view is top-down.
            material.SetVector(Params3Id, new Vector4(0.06f, 0.75f, 0.70f, 0.18f));
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