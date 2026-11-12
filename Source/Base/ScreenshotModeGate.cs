using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// One place to ask "is a screenshot being taken". Every visual feature has to sit
    /// this out for the same reason: a screenshot should show the stock game, not this
    /// mod's additions.
    ///
    /// Extracted after the third copy: the explosion shockwave and the shield ripple
    /// each carried their own identical five-line property, and the shield visibility
    /// patch would have made it three.
    /// </summary>
    internal static class ScreenshotModeGate
    {
        internal static bool Active
        {
            get
            {
                ScreenshotModeHandler? handler = Find.ScreenshotModeHandler;
                return handler != null && handler.Active;
            }
        }
    }
}
