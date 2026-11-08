using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using UnityEngine;
using Verse;
using NVector4 = System.Numerics.Vector4;

namespace VisualTweaks
{
    /// <summary>
    /// One instanced draw for every leak visual on the map: port streams, wall
    /// breach jets and ceiling suction discs share the same shader and buffer
    /// layout. Unlike the shockwave renderer
    /// there is no small-count fallback — leak drawing happens every frame while any
    /// leak is active, and a single code path is worth more than the few microseconds
    /// a lone DrawMesh would save.
    ///
    /// Per-instance data: two vec4, (strength, gapV, phase, narrow) and
    /// (seed, lenCells, mode, 0), riding the set=2 binding=3 storage buffer;
    /// transforms ride the instance matrices (binding 2). Same convention as
    /// ExplosionRipple and vanilla's VacuumOverlay (_VacuumPercent).
    /// </summary>
    internal sealed class VacuumLeakRenderer : IDisposable
    {
        private const int RenderQueue = 3177;

        // Safety bound on a single submission. Ports are bounded by the map's
        // ventilation openings and breaches by VacuumBreaches' own cap (64), so
        // normal play stays far below this.
        private const int MaxInstances = 512;

        // Slightly above the entities, below fog of war and darkness — same layer
        // as the vanilla vacuum fog, so the stream covers pawns like "the air
        // itself" but never glows through an unlit room.
        internal static readonly float Altitude =
            Altitudes.AltitudeFor(AltitudeLayer.Gas);

        private const float PeakAlpha = 0.22f;

        // Fixed gain on top of the slider. Keeping it here lets the setting stay a
        // compact 0.1..2 range while the effect still scales far enough to be seen.
        private const float IntensityScale = 2f;

        private static readonly int ParamsId = Shader.PropertyToID("_Params");
        private static readonly int LeakParamsId = Shader.PropertyToID("_LeakParams");

        private readonly List<Matrix4x4> instanceMatrices = new List<Matrix4x4>(64);

        // Two vec4 per instance, matching _LeakParams in the shaders.
        private readonly NVector4[] instanceParams = new NVector4[MaxInstances * 2];

        private readonly MaterialPropertyBlock instancedProperties =
            new MaterialPropertyBlock();

        private Material? material;
        private ComputeBuffer? paramsBuffer;
        private bool loggedDraw;

        internal void Draw(List<VacuumLeakInstance> instances)
        {
            EnsureMaterial();

            material.SetVector(ParamsId, new Vector4(
                VisualTweaksSettings.vacuumLeakIntensity * IntensityScale,
                PeakAlpha,
                VisualTweaksSettings.vacuumLeakDistortion ? 1f : 0f,
                VisualTweaksSettings.vacuumLeakDistortionStrength));

            instanceMatrices.Clear();
            int count = 0;

            for (int i = 0; i < instances.Count && count < MaxInstances; i++)
            {
                VacuumLeakInstance instance = instances[i];
                instanceMatrices.Add(instance.Matrix);

                int p = count * 2;
                instanceParams[p] = new NVector4(
                    instance.Strength,
                    instance.GapV,
                    instance.Phase,
                    instance.Narrow);
                instanceParams[p + 1] = new NVector4(
                    instance.Seed,
                    instance.LenCells,
                    instance.Mode,
                    0f);
                count++;
            }

            if (count == 0)
            {
                return;
            }

            if (paramsBuffer == null)
            {
                // Sized once for the absolute maximum; only the used prefix is
                // written each frame. 16-byte stride = one vec4.
                paramsBuffer = new ComputeBuffer(MaxInstances * 2, 16);
                instancedProperties.SetBuffer(LeakParamsId, paramsBuffer);
            }

            paramsBuffer.SetData(instanceParams, 0, 0, count * 2);

            Graphics.DrawMeshInstanced(
                MeshPool.plane10,
                0,
                material,
                instanceMatrices,
                instancedProperties);

            if (!loggedDraw)
            {
                loggedDraw = true;
                Log.Message("[Visual Tweaks] Vacuum leak drawing (" + count + " instances).");
            }
        }

        [MemberNotNull(nameof(material))]
        private void EnsureMaterial()
        {
            if (material != null)
            {
                return;
            }

            ShaderDef? def = VacuumLeakBridge.LeakShaderDef;
            if (def == null)
            {
                throw new InvalidOperationException(
                    "Vacuum leak shader def is not available.");
            }

            material = new Material((Shader)def)
            {
                name = "VisualTweaksVacuumLeakMat",
                renderQueue = RenderQueue,
            };
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

    /// <summary>
    /// One submitted quad. Wall breaches are a single continuous strip — wet room,
    /// opening, dry-side plume — with the opening at v = GapV and LenCells the strip
    /// length in cells. Ceiling breaches switch the shader to its radial mode: the
    /// quad is scaled to the suction disc's diameter, LenCells is that diameter and
    /// Narrow is the hole radius as a fraction of the disc radius. The matrix already
    /// encodes position, orientation (+Z along the flow) and size.
    /// </summary>
    internal struct VacuumLeakInstance
    {
        public const float ModeLinear = 0f;
        public const float ModeCeiling = 1f;

        public Matrix4x4 Matrix;
        public float Strength;
        public float GapV;
        public float Phase;
        public float Narrow;
        public float LenCells;
        public float Seed;
        public float Mode;
    }
}