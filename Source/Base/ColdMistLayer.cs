using System;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// The low-temperature room mist.
    /// </summary>
    internal sealed class ColdMistLayer : IOverlayLayer
    {
        private ColdMistRenderer? renderer;
        private bool errorLogged;

        public void Draw(Map map, in OverlayFrame frame)
        {
            if (!ColdMistBridge.Active)
            {
                return;
            }

            if (renderer == null)
            {
                renderer = new ColdMistRenderer();
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
                    Log.Error("[Visual Tweaks] Cold mist draw failed: " + ex);
                }

                ColdMistBridge.DisableRuntime(ex.Message);
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
