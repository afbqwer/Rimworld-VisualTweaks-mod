using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Drawing helpers shared by every settings tab.
    ///
    /// The point of this file is to keep one visual rhythm (header, row, gap) in a
    /// single place. The settings page used to be a flat column of Listing_Standard
    /// calls with GapLine dividers, so every control carried the same weight and
    /// nothing told the eye where one group ended and the next began.
    ///
    /// Only vanilla primitives are used (DrawBoxSolid, Label, HorizontalSlider,
    /// RadioButtonLabeled, ButtonText) so the page keeps matching the rest of the
    /// game's UI instead of looking like a custom-drawn overlay.
    /// </summary>
    internal static class SettingsLayout
    {
        /// <summary>Vertical air after a control row.</summary>
        public const float RowGap = 6f;

        public const float HeaderHeight = 28f;

        /// <summary>Label line plus slider rail.</summary>
        public const float SliderRowHeight = LabelLineHeight + RailHeight;

        public const float RadioRowHeight = 26f;

        public const float ButtonRowHeight = 30f;

        public const float ButtonRowGap = 10f;

        private const float LabelLineHeight = 22f;
        private const float RailHeight = 16f;
        private const float ValueWidth = 60f;
        private const float HeaderIndent = 10f;

        private static readonly Color HeaderBack = new Color(1f, 1f, 1f, 0.07f);
        private static readonly Color HeaderAccent = new Color(0.52f, 0.63f, 0.8f, 0.9f);
        private static readonly Color HeaderText = new Color(0.93f, 0.93f, 0.93f);
        private static readonly Color ValueText = new Color(0.78f, 0.81f, 0.86f);
        private static readonly Color NoteText = new Color(0.66f, 0.66f, 0.66f);
        private static readonly Color WarnText = new Color(0.93f, 0.6f, 0.52f);

        /// <summary>
        /// Group header: a light band with a coloured left edge. Section titles used
        /// to be plain labels, which read the same as a row caption.
        /// </summary>
        public static void Header(Listing_Standard listing, string label)
        {
            Rect rect = listing.GetRect(HeaderHeight);

            Widgets.DrawBoxSolid(rect, HeaderBack);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 3f, rect.height), HeaderAccent);

            TextAnchor anchor = Text.Anchor;
            Color color = GUI.color;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = HeaderText;
            Widgets.Label(new Rect(rect.x + HeaderIndent, rect.y, rect.width - HeaderIndent, rect.height), label);
            GUI.color = color;
            Text.Anchor = anchor;

            listing.Gap(RowGap + 2f);
        }

        /// <summary>Small grey helper text. Used where a tooltip is too easy to miss.</summary>
        public static void Note(Listing_Standard listing, string text)
        {
            DrawNote(listing, text, NoteText);
        }

        /// <summary>Note in warning colour, for "this setting cannot take effect" cases.</summary>
        public static void Warning(Listing_Standard listing, string text)
        {
            DrawNote(listing, text, WarnText);
        }

        private static void DrawNote(Listing_Standard listing, string text, Color color)
        {
            if (text.NullOrEmpty())
            {
                return;
            }

            Text.Font = GameFont.Tiny;
            Rect rect = listing.GetRect(Text.CalcHeight(text, listing.ColumnWidth - 8f));
            rect.xMin += 4f;
            rect.xMax -= 4f;

            Color previous = GUI.color;
            GUI.color = color;
            Widgets.Label(rect, text);
            GUI.color = previous;
            Text.Font = GameFont.Small;

            listing.Gap(RowGap);
        }

        /// <summary>
        /// Slider row: the caption and its current value share the top line, the rail
        /// spans the full width underneath. Giving the rail the whole row width is the
        /// main usability win here — the old layout squeezed it into the right 60% and
        /// printed the value above the handle in the middle of the rail.
        /// </summary>
        public static float Slider(
            Listing_Standard listing,
            string label,
            float value,
            float min,
            float max,
            string? tooltip = null,
            float step = 0.01f)
        {
            Rect rect = listing.GetRect(SliderRowHeight);

            Rect labelRect = new Rect(rect.x, rect.y, rect.width - ValueWidth, LabelLineHeight);
            Rect valueRect = new Rect(rect.xMax - ValueWidth, rect.y, ValueWidth, LabelLineHeight);

            TextAnchor anchor = Text.Anchor;
            Color color = GUI.color;
            Text.WordWrap = false;

            Text.Anchor = TextAnchor.LowerLeft;
            Widgets.Label(labelRect, label);

            Text.Anchor = TextAnchor.LowerRight;
            GUI.color = ValueText;
            Widgets.Label(valueRect, step >= 1f ? value.ToString("0") : value.ToString("0.00"));

            GUI.color = color;
            Text.WordWrap = true;
            Text.Anchor = anchor;

            if (!tooltip.NullOrEmpty())
            {
                TooltipHandler.TipRegion(
                    new Rect(rect.x, rect.y, rect.width, LabelLineHeight),
                    tooltip);
            }

            Rect railRect = new Rect(rect.x, rect.y + LabelLineHeight, rect.width, RailHeight);
            float result = Widgets.HorizontalSlider(railRect, value, min, max, roundTo: step);

            listing.Gap(RowGap);
            return result;
        }

        /// <summary>
        /// Mutually exclusive choices laid out side by side. Returns the index of the
        /// clicked cell, or <paramref name="selected"/> when nothing was clicked.
        /// </summary>
        public static int RadioRow(Listing_Standard listing, string[] labels, int selected, string[]? tips = null)
        {
            Rect rect = listing.GetRect(RadioRowHeight);
            int count = labels.Length;
            float width = (rect.width - ButtonRowGap * (count - 1)) / count;
            int result = selected;

            for (int i = 0; i < count; i++)
            {
                Rect cell = new Rect(rect.x + i * (width + ButtonRowGap), rect.y, width, RadioRowHeight);

                // RadioButtonLabeled draws the dot at the right edge without reserving
                // space for it, so the caption has to be cut short first.
                string caption = labels[i].Truncate(cell.width - 28f);
                if (Widgets.RadioButtonLabeled(cell, caption, i == selected))
                {
                    result = i;
                }

                if (tips != null && i < tips.Length && !tips[i].NullOrEmpty())
                {
                    TooltipHandler.TipRegion(cell, tips[i]);
                }
            }

            listing.Gap(RowGap);
            return result;
        }

        /// <summary>Row of plain buttons of equal width. Returns the clicked index, or -1.</summary>
        public static int ButtonRow(Listing_Standard listing, string[] labels)
        {
            Rect rect = listing.GetRect(ButtonRowHeight);
            int count = labels.Length;
            float width = (rect.width - ButtonRowGap * (count - 1)) / count;
            int clicked = -1;

            for (int i = 0; i < count; i++)
            {
                Rect cell = new Rect(rect.x + i * (width + ButtonRowGap), rect.y, width, ButtonRowHeight);
                if (Widgets.ButtonText(cell, labels[i]))
                {
                    clicked = i;
                }
            }

            listing.Gap(RowGap);
            return clicked;
        }

        /// <summary>Trailing air so the last control clears the scrollbar end.</summary>
        public static void Finish(Listing_Standard listing)
        {
            listing.Gap(16f);
        }
    }
}
