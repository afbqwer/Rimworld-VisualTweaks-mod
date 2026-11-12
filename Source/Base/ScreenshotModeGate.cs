using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Development aid: "should this feature hand the frame back to the stock visuals?"
    ///
    /// Enabled only when Dev Mode is on *and* screenshot mode (F11) is active. In normal
    /// play F11 hides the GUI but keeps this mod's effects; during development the same
    /// key gives a clean stock reference to compare against. A player who never opens
    /// Dev Mode therefore never loses the effects to a screenshot.
    ///
    /// Every visual feature reads this same gate, so the two states cannot drift apart.
    /// Extracted after duplicated copies appeared in the explosion shockwave, the shield
    /// ripple, the shield visibility patch and the bridge classes.
    /// </summary>
    public static class ScreenshotModeGate
    {
        public static bool Active
        {
            get
            {
                if (!Prefs.DevMode)
                {
                    return false;
                }

                ScreenshotModeHandler? handler = Find.ScreenshotModeHandler;
                return handler != null && handler.Active;
            }
        }
    }
}
