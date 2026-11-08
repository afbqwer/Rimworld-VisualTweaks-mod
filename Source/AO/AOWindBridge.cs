using System.Diagnostics.CodeAnalysis;
using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Loose coupling to Atmosphere Overhaul. All AO types are resolved by name so this
    /// assembly still loads (and simply does nothing) if AO is absent or changes.
    /// </summary>
    internal static class AOWindBridge
    {
        // Resolved by name at startup; null whenever AO is absent or renamed a member.
        internal static Type? MapComponentType { get; private set; }
        internal static Type? StreamlineRendererType { get; private set; }

        private static FieldInfo? showStreamlineField;
        private static MethodInfo? tryGetWindMethod;
        private static PropertyInfo? completedStepsProperty;
        private static FieldInfo? completedStepsField;
        private static readonly object?[] WindArgs = new object?[3];

        // Note: no bound delegates here. Delegate.CreateDelegate refuses to bind when the
        // delegate's first parameter is a base of the method's declaring type, and the AO
        // type is only known by name, so the call has to stay reflective. The cost is
        // kept down by calling it at the rebuild rate instead of every frame.

        internal static bool Available =>
            MapComponentType != null &&
            StreamlineRendererType != null &&
            showStreamlineField != null &&
            tryGetWindMethod != null;

        internal static void Initialize()
        {
            MapComponentType = AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.AtmosphereMapComponent");
            StreamlineRendererType = AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.WindStreamlineRenderer");

            Type settingsType = AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.AtmosphereOverlaySettings");
            if (settingsType != null)
            {
                showStreamlineField = AccessTools.Field(settingsType, "ShowStreamlineOverlay");
            }

            if (MapComponentType != null)
            {
                tryGetWindMethod = AccessTools.Method(MapComponentType, "TryGetWindSnapshotData");
                completedStepsProperty = AccessTools.Property(MapComponentType, "CompletedSteps");
                completedStepsField = AccessTools.Field(MapComponentType, "completedSteps");
            }
        }

        internal static bool ToggleOn()
        {
            if (showStreamlineField == null)
            {
                return false;
            }

            try
            {
                return showStreamlineField.GetValue(null) is bool on && on;
            }
            catch
            {
                return false;
            }
        }

        internal static MapComponent? GetComponent(Map map)
        {
            Type? componentType = MapComponentType;
            if (componentType == null)
            {
                return null;
            }

            try
            {
                return map.GetComponent(componentType);
            }
            catch
            {
                return null;
            }
        }

        internal static int CompletedSteps(Map map)
        {
            MapComponent? component = GetComponent(map);
            if (component == null)
            {
                return 0;
            }

            try
            {
                if (completedStepsProperty != null)
                {
                    return completedStepsProperty.GetValue(component, null) is int fromProperty
                        ? fromProperty
                        : 0;
                }

                if (completedStepsField != null)
                {
                    return completedStepsField.GetValue(component) is int fromField ? fromField : 0;
                }
            }
            catch
            {
                // Fall through to the stable default.
            }

            return 0;
        }

        internal static bool TryGetWind(
            Map map,
            [NotNullWhen(true)] out float[]? velocityX,
            [NotNullWhen(true)] out float[]? velocityZ,
            [NotNullWhen(true)] out float[]? obstacleMask)
        {
            velocityX = null;
            velocityZ = null;
            obstacleMask = null;

            if (tryGetWindMethod == null)
            {
                return false;
            }

            MapComponent? component = GetComponent(map);
            if (component == null)
            {
                return false;
            }

            WindArgs[0] = null;
            WindArgs[1] = null;
            WindArgs[2] = null;

            try
            {
                if (!(tryGetWindMethod.Invoke(component, WindArgs) is bool found && found))
                {
                    return false;
                }

                velocityX = WindArgs[0] as float[];
                velocityZ = WindArgs[1] as float[];
                obstacleMask = WindArgs[2] as float[];

                return velocityX != null && velocityZ != null && obstacleMask != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
