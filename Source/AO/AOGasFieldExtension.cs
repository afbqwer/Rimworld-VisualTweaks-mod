using System.Diagnostics.CodeAnalysis;
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Atmosphere Overhaul data source for the base mod's gas field renderer: mist
    /// snapshots, the temporal-patch blend, the solver cadence and the per-cell wind
    /// used by the local-flow noise mode. All AO members are optional: if any of them
    /// is missing the base renderer simply falls back to vanilla data.
    /// </summary>
    internal sealed class AOGasFieldExtension : IGasFieldExtension
    {
        // AO map component members.
        // Every member is optional: AO may rename or drop any of them, and the
        // reflection below simply yields null when it does.
        private readonly MethodInfo? tryGetMistMethod;
        private readonly FieldInfo? settingsField;
        private readonly FieldInfo? temporalPatchManagerField;
        private readonly PropertyInfo? ambientWindProperty;

        // AO's solver cadence, resolved once from the settings assembly. Static property,
        // so there is no instance to go through.
        private PropertyInfo? simulationIntervalGetter;
        private bool simulationIntervalResolved;

        // LocalAtmosphereSettings getters, resolved from the settings instance type.
        private PropertyInfo? maxMistGetter;
        private PropertyInfo? mistRenderZeroGetter;
        private PropertyInfo? mistRenderFullGetter;

        // Temporal patch members, resolved from runtime types.
        private PropertyInfo? publishedSnapshotGetter;
        private PropertyInfo? topologyGetter;
        private PropertyInfo? patchMistGetter;
        private PropertyInfo? activeCellsGetter;
        private PropertyInfo? coreWeightGetter;
        private PropertyInfo? mapIndexToSlotGetter;

        /// <summary>
        /// Out-argument carrier for <see cref="MethodInfo.Invoke(object, object[])"/>. The
        /// method leaves the two slots as they are, so the nullable element type is what
        /// the reflection contract actually is.
        /// </summary>
        private readonly object?[] mistArgs = new object?[2];

        public AOGasFieldExtension()
        {
            Type? componentType = AOWindBridge.MapComponentType;
            if (componentType != null)
            {
                tryGetMistMethod = AccessTools.Method(componentType, "TryGetMistSnapshotData");
                settingsField = AccessTools.Field(componentType, "settings");
                temporalPatchManagerField = AccessTools.Field(componentType, "temporalPatchManager");
                ambientWindProperty = AccessTools.Property(componentType, "EffectiveAmbientWind");
            }
        }

        public bool SupportsLocalWind => AOWindBridge.Available;

        public int SimulationIntervalTicks
        {
            get
            {
                if (simulationIntervalGetter == null)
                {
                    return 6;
                }

                try
                {
                    object? ticks = simulationIntervalGetter.GetValue(null);
                    return ticks is int value ? value : 6;
                }
                catch
                {
                    return 6;
                }
            }
        }

        public bool TryGetWind(
            Map map,
            [NotNullWhen(true)] out float[]? velocityX,
            [NotNullWhen(true)] out float[]? velocityZ,
            [NotNullWhen(true)] out float[]? obstacleMask)
        {
            return AOWindBridge.TryGetWind(map, out velocityX, out velocityZ, out obstacleMask);
        }

        public int CompletedSteps(Map map)
        {
            return AOWindBridge.CompletedSteps(map);
        }

        public bool TryGetMistData(
            Map map,
            [NotNullWhen(true)] out float[]? mist,
            [NotNullWhen(true)] out float[]? obstacleMask)
        {
            mist = null;
            obstacleMask = null;

            if (tryGetMistMethod == null)
            {
                return false;
            }

            MapComponent? component = AOWindBridge.GetComponent(map);
            if (component == null)
            {
                return false;
            }

            mistArgs[0] = null;
            mistArgs[1] = null;

            try
            {
                if (!(tryGetMistMethod.Invoke(component, mistArgs) is bool found && found))
                {
                    return false;
                }

                mist = mistArgs[0] as float[];
                obstacleMask = mistArgs[1] as float[];

                return mist != null && obstacleMask != null;
            }
            catch
            {
                return false;
            }
        }

        public bool TryGetMistRenderSettings(
            Map map,
            out float maxDensity,
            out float renderZero,
            out float renderFull)
        {
            // AO's tuning defaults; used when the settings object is unavailable.
            maxDensity = 100f;
            renderZero = 1f;
            renderFull = 3f;

            if (settingsField == null)
            {
                return false;
            }

            MapComponent? component = AOWindBridge.GetComponent(map);
            if (component == null)
            {
                return false;
            }

            try
            {
                object? settings = settingsField.GetValue(component);
                if (settings == null)
                {
                    return false;
                }

                if (!simulationIntervalResolved)
                {
                    simulationIntervalResolved = true;
                    try
                    {
                        Type? aoSettings = settings.GetType().Assembly.GetType(
                            "AtmosphereOverhaul.AtmosphereOverhaulSettings");
                        simulationIntervalGetter = aoSettings?.GetProperty(
                            "SimulationIntervalTicks",
                            BindingFlags.Public | BindingFlags.Static);
                    }
                    catch
                    {
                        simulationIntervalGetter = null;
                    }
                }

                if (maxMistGetter == null)
                {
                    Type type = settings.GetType();
                    maxMistGetter = type.GetProperty("MaxMistDensity");
                    mistRenderZeroGetter = type.GetProperty("MistRenderZeroDensity");
                    mistRenderFullGetter = type.GetProperty("MistRenderFullDensity");
                }

                if (maxMistGetter == null || mistRenderZeroGetter == null || mistRenderFullGetter == null)
                {
                    return false;
                }

                if (maxMistGetter.GetValue(settings) is not float max
                    || mistRenderZeroGetter.GetValue(settings) is not float zero
                    || mistRenderFullGetter.GetValue(settings) is not float full)
                {
                    return false;
                }

                maxDensity = max;
                renderZero = zero;
                renderFull = full;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Blends AO's temporal-patch mist into the coarse snapshot. This mirrors
        /// TemporalPatchManager.TryGetMist: mist = lerp(coarse, patch, CoreWeight).
        /// </summary>
        public void ApplyTemporalPatchMist(Map map, float[] mist)
        {
            if (temporalPatchManagerField == null || mist == null)
            {
                return;
            }

            try
            {
                MapComponent? component = AOWindBridge.GetComponent(map);
                if (component == null)
                {
                    return;
                }

                object? manager = temporalPatchManagerField.GetValue(component);
                if (manager == null)
                {
                    return;
                }

                if (publishedSnapshotGetter == null)
                {
                    publishedSnapshotGetter = manager.GetType().GetProperty("PublishedSnapshot");
                }

                if (publishedSnapshotGetter == null)
                {
                    return;
                }

                object? snapshot = publishedSnapshotGetter.GetValue(manager);
                if (snapshot == null)
                {
                    return;
                }

                // The snapshot and topology types are only reachable through instances,
                // so their members are cached on the first non-null snapshot.
                if (topologyGetter == null)
                {
                    CachePatchMembers(snapshot);
                }

                if (topologyGetter == null)
                {
                    return;
                }

                object? topology = topologyGetter.GetValue(snapshot);
                if (topology == null)
                {
                    return;
                }

                if (activeCellsGetter == null)
                {
                    CacheTopologyMembers(topology);
                }

                if (activeCellsGetter == null || coreWeightGetter == null
                    || mapIndexToSlotGetter == null || patchMistGetter == null)
                {
                    return;
                }

                int[]? activeCells = activeCellsGetter.GetValue(topology) as int[];
                float[]? coreWeight = coreWeightGetter.GetValue(topology) as float[];
                int[]? slotByIndex = mapIndexToSlotGetter.GetValue(topology) as int[];
                float[]? patchMist = patchMistGetter.GetValue(snapshot) as float[];

                if (activeCells == null || coreWeight == null || slotByIndex == null || patchMist == null)
                {
                    return;
                }

                for (int i = 0; i < activeCells.Length; i++)
                {
                    int index = activeCells[i];
                    if ((uint)index >= (uint)mist.Length)
                    {
                        continue;
                    }

                    float weight = coreWeight[index];
                    if (weight <= 0f)
                    {
                        continue;
                    }

                    int slot = slotByIndex[index];
                    if ((uint)slot >= (uint)patchMist.Length)
                    {
                        continue;
                    }

                    mist[index] = Mathf.Lerp(mist[index], patchMist[slot], weight);
                }
            }
            catch
            {
                // A changed AO layout just means no patch blending; the coarse field
                // still renders.
            }
        }

        public bool TryGetAmbientWind(Map map, out float windX, out float windZ)
        {
            windX = 0f;
            windZ = 0f;

            if (ambientWindProperty == null)
            {
                return false;
            }

            MapComponent? component = AOWindBridge.GetComponent(map);
            if (component == null)
            {
                return false;
            }

            try
            {
                if (ambientWindProperty.GetValue(component, null) is not Vector2 wind)
                {
                    return false;
                }

                float length = wind.magnitude;
                if (length < 0.01f || !float.IsFinite(length))
                {
                    return false;
                }

                windX = wind.x / length;
                windZ = wind.y / length;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void CachePatchMembers(object snapshot)
        {
            Type snapshotType = snapshot.GetType();
            topologyGetter = snapshotType.GetProperty("Topology");
            patchMistGetter = snapshotType.GetProperty("Mist");
        }

        private void CacheTopologyMembers(object topology)
        {
            Type topologyType = topology.GetType();
            activeCellsGetter = topologyType.GetProperty("ActiveCells");
            coreWeightGetter = topologyType.GetProperty("CoreWeight");
            mapIndexToSlotGetter = topologyType.GetProperty("MapIndexToSlot");
        }
    }
}
