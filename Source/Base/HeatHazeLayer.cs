using System;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// The high-temperature air shimmer.
    /// </summary>
    internal sealed class HeatHazeLayer : IOverlayLayer
    {
        private HeatHazeRenderer? renderer;
        private bool errorLogged;

        public void Draw(Map map, in OverlayFrame frame)
        {
            if (!HeatHazeBridge.Active)
            {
                return;
            }

            if (renderer == null)
            {
                renderer = new HeatHazeRenderer();
            }

            try
            {
                renderer.Draw(map, frame.View, frame.Paused, frame.Speed);
            }
            catch (Exception ex)
            {
                // A persistent draw failure must not spam the log or leave a broken
                // renderer alive. Disable the feature for this session instead.
                if (!errorLogged)
                {
                    errorLogged = true;
                    Log.Error("[Visual Tweaks] Heat haze draw failed: " + ex);
                }

                HeatHazeBridge.DisableRuntime(ex.Message);
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
