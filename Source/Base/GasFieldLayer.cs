using System;
using RimWorld;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// The replacement gas/mist field. A layer of its own so the field keeps rendering even
    /// when the wind overlay is switched off in the settings.
    /// </summary>
    internal sealed class GasFieldLayer : IOverlayLayer
    {
        private GasFieldRenderer? renderer;
        private bool errorLogged;
        private bool? lastActive;

        public void Draw(Map map, in OverlayFrame frame)
        {
            // Includes screenshot mode: while a screenshot is being taken the field
            // steps aside and AO's renderers draw the original sprites.
            bool active = GasFieldBridge.Active;

            // Toggling the renderer (or losing the shader) has to rebuild the gas section
            // meshes: while active they were cleared, so AO needs a regeneration to come
            // back, and while inactive stale sprite quads must not linger under the field.
            if (lastActive != active)
            {
                lastActive = active;
                map.mapDrawer?.WholeMapChanged(MapMeshFlagDefOf.Gas);
            }

            if (!active)
            {
                return;
            }

            if (renderer == null)
            {
                renderer = new GasFieldRenderer();
            }

            try
            {
                renderer.Draw(
                    map,
                    frame.View,
                    frame.CellPixels,
                    frame.Realtime,
                    frame.Paused);
            }
            catch (Exception ex)
            {
                // Stop replacing AO's renderers: otherwise a persistent failure would
                // leave the map with no gas at all.
                if (!errorLogged)
                {
                    errorLogged = true;
                    Log.Error("[Visual Tweaks] Gas field draw failed: " + ex);
                }

                GasFieldBridge.DisableRuntime(ex.Message);

                renderer?.Dispose();
                renderer = null;
            }
        }

        public void Dispose()
        {
            renderer?.Dispose();
            renderer = null;
            errorLogged = false;
        }
    }
}
