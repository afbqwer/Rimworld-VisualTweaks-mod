using System;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Draws deadlife dust on its own when the gas field is told not to shade it.
    ///
    /// Kept separate from <see cref="GasFieldLayer"/> rather than bolted onto the field
    /// renderer: the two produce their geometry in completely different ways (a density
    /// volume versus one sprite quad per cell) and share nothing but the map and the view.
    /// </summary>
    internal sealed class DeadlifeDustLayer : IOverlayLayer
    {
        private DeadlifeDustRenderer? renderer;
        private bool errorLogged;

        public void Draw(Map map, in OverlayFrame frame)
        {
            // The field already shades dust in this mode, so this pass would only ever
            // paint a second copy on top of it.
            if (!GasFieldBridge.Active || VisualTweaksSettings.gasFieldReplaceDeadlife)
            {
                return;
            }

            if (renderer == null)
            {
                renderer = new DeadlifeDustRenderer();
            }

            try
            {
                renderer.Draw(map, frame.View, frame.Realtime);
            }
            catch (Exception ex)
            {
                // Losing this pass would leave deadlife dust invisible on a map where it is
                // a gameplay signal, so the fallback is to have the field shade it again
                // rather than to show nothing. That flips the same setting the shader reads,
                // so the next frame is already correct.
                if (!errorLogged)
                {
                    errorLogged = true;
                    Log.Error(
                        "[Visual Tweaks] Deadlife dust pass failed, falling back to the gas field: "
                        + ex);
                }

                renderer?.Dispose();
                renderer = null;

                VisualTweaksSettings.gasFieldReplaceDeadlife = true;
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
