using System;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Per-object shield style editor, opened from the gizmo on a pawn or building.
    ///
    /// Every row starts with "follow global": only the choices the player actually
    /// touches become an override. That is what keeps a global style change
    /// meaningful after a few individual shields have been customised.
    /// </summary>
    internal sealed class Window_ShieldStyleOverride : Window
    {
        private const float ScrollbarWidth = 16f;
        private const float ViewHeight = 820f;

        private readonly ThingWithComps thing;
        private readonly ShieldHexTarget target;
        private readonly Listing_Standard listing = new Listing_Standard();
        private Vector2 scroll;

        internal Window_ShieldStyleOverride(ThingWithComps thing, ShieldHexTarget target)
        {
            this.thing = thing;
            this.target = target;

            doCloseX = true;
            closeOnCancel = true;
            absorbInputAroundWindow = true;
            draggable = true;
            resizeable = false;
            optionalTitle = "VisualTweaks_ShieldOverride_GizmoLabel".Translate();
        }

        public override Vector2 InitialSize => new Vector2(600f, 640f);

        public override void DoWindowContents(Rect inRect)
        {
            bool scrollOpen = false;
            try
            {
                ShieldStyleOverride entry =
                    ShieldOverrideGameComponent.GetOrCreate(thing, target);

                Rect viewRect = new Rect(
                    0f,
                    0f,
                    inRect.width - ScrollbarWidth,
                    ViewHeight);
                Widgets.BeginScrollView(inRect, ref scroll, viewRect);
                scrollOpen = true;

                listing.maxOneColumn = true;
                listing.Begin(viewRect);

                listing.Label(thing.LabelShortCap);
                SettingsLayout.Note(
                    listing,
                    entry.IsEmpty
                        ? "VisualTweaks_ShieldOverride_UsingGlobal".Translate()
                        : "VisualTweaks_ShieldOverride_UsingLocal".Translate());

                if (!ReplacementActive())
                {
                    SettingsLayout.Warning(
                        listing,
                        "VisualTweaks_ShieldOverride_ReplacementOff".Translate());
                }

                DrawSurfaceRow(listing, entry);

                // The surface choice decides which impact and break sets exist, so
                // the rows below read the effective style rather than the raw
                // override.
                ShieldStyle effective = ShieldStyle.For(target, thing);
                NormalizeImpactAndBreak(entry, effective);

                if (target == ShieldHexTarget.Large)
                {
                    DrawLargeImpactRow(listing, entry, effective);
                }
                else
                {
                    DrawPersonalHitRow(listing, entry);
                }

                DrawBreakRow(listing, entry, effective);
                DrawRestoreRow(listing, entry);
                DrawResetRow(listing, entry);

                listing.End();
            }
            catch (Exception ex)
            {
                // A throwing window would retry every GUI frame and bury the log.
                // Close it and report once; the shield keeps whatever style it
                // already had.
                Close();
                Log.ErrorOnce(
                    "[Visual Tweaks] Shield style window failed; closed it: " + ex,
                    typeof(Window_ShieldStyleOverride).GetHashCode());
            }
            finally
            {
                if (scrollOpen)
                {
                    Widgets.EndScrollView();
                }
            }
        }

        private void DrawSurfaceRow(
            Listing_Standard listing,
            ShieldStyleOverride entry)
        {
            ShieldDomeStyle[] order = target == ShieldHexTarget.Personal
                ? VisualTweaksMod.PersonalShieldStyleOrder
                : VisualTweaksMod.LargeShieldStyleOrder;
            bool withBubble = target != ShieldHexTarget.Personal;
            string[] labels = WithGlobal(
                VisualTweaksMod.ShieldStyleLabels(withBubble));
            string[] tips = WithGlobalTip(
                VisualTweaksMod.ShieldStyleTips(withBubble));

            int selected = entry.OverrideSurface
                ? VisualTweaksMod.IndexOf(order, entry.Surface) + 1
                : 0;

            listing.Label("VisualTweaks_ShieldOverride_Surface".Translate());
            int picked = SettingsLayout.RadioRow(listing, labels, selected, tips);
            if (picked == selected)
            {
                return;
            }

            if (picked == 0)
            {
                entry.OverrideSurface = false;
            }
            else
            {
                entry.OverrideSurface = true;
                entry.Surface = order[picked - 1];
                if (ShieldStyle.IsHexSurface(entry.Surface))
                {
                    ShieldHexMap.EnsureBaked(target);
                }
            }
        }

        private void DrawLargeImpactRow(
            Listing_Standard listing,
            ShieldStyleOverride entry,
            in ShieldStyle effective)
        {
            bool hex = ShieldStyle.IsHexSurface(effective.Surface);
            ShieldImpactStyle[] order = hex
                ? VisualTweaksMod.HexImpactOrder
                : VisualTweaksMod.StandardImpactOrder;
            string[] labels = WithGlobal(VisualTweaksMod.ImpactLabels(hex));
            string[] tips = WithGlobalTip(VisualTweaksMod.ImpactTips(hex));

            int selected = entry.OverrideImpact
                ? VisualTweaksMod.IndexOf(order, entry.Impact) + 1
                : 0;

            listing.Label("VisualTweaks_ShieldOverride_Impact".Translate());
            int picked = SettingsLayout.RadioRow(listing, labels, selected, tips);
            if (picked == selected)
            {
                return;
            }

            if (picked == 0)
            {
                entry.OverrideImpact = false;
            }
            else
            {
                entry.OverrideImpact = true;
                entry.Impact = order[picked - 1];
            }
        }

        private void DrawPersonalHitRow(
            Listing_Standard listing,
            ShieldStyleOverride entry)
        {
            PersonalShieldHitStyle[] order = VisualTweaksMod.PersonalHitOrder;
            string[] labels = WithGlobal(VisualTweaksMod.PersonalHitLabels());
            string[] tips = WithGlobalTip(VisualTweaksMod.PersonalHitTips());

            int selected = entry.OverridePersonalHit
                ? VisualTweaksMod.IndexOf(order, entry.PersonalHit) + 1
                : 0;

            listing.Label("VisualTweaks_ShieldOverride_PersonalHit".Translate());
            int picked = SettingsLayout.RadioRow(listing, labels, selected, tips);
            if (picked == selected)
            {
                return;
            }

            if (picked == 0)
            {
                entry.OverridePersonalHit = false;
            }
            else
            {
                entry.OverridePersonalHit = true;
                entry.PersonalHit = order[picked - 1];
                if (entry.PersonalHit == PersonalShieldHitStyle.HexWave)
                {
                    ShieldHexMap.EnsureBaked(ShieldHexTarget.Personal);
                }
            }
        }

        private void DrawBreakRow(
            Listing_Standard listing,
            ShieldStyleOverride entry,
            in ShieldStyle effective)
        {
            bool hex = ShieldStyle.IsHexSurface(effective.Surface);
            ShieldBreakStyle[] order = hex
                ? VisualTweaksMod.HexBreakOrder
                : VisualTweaksMod.StandardBreakOrder;
            string[] labels = WithGlobal(VisualTweaksMod.BreakLabels(hex));
            string[] tips = WithGlobalTip(VisualTweaksMod.BreakTips(hex));

            int selected = entry.OverrideBreak
                ? VisualTweaksMod.IndexOf(order, entry.Break) + 1
                : 0;

            listing.Label("VisualTweaks_ShieldOverride_Break".Translate());
            int picked = SettingsLayout.RadioRow(listing, labels, selected, tips);
            if (picked == selected)
            {
                return;
            }

            if (picked == 0)
            {
                entry.OverrideBreak = false;
            }
            else
            {
                entry.OverrideBreak = true;
                entry.Break = order[picked - 1];
            }
        }

        private void DrawRestoreRow(
            Listing_Standard listing,
            ShieldStyleOverride entry)
        {
            ShieldRestoreStyle[] order = VisualTweaksMod.RestoreOrder;
            string[] labels = WithGlobal(VisualTweaksMod.RestoreLabels());
            string[] tips = WithGlobalTip(VisualTweaksMod.RestoreTips());

            int selected = entry.OverrideRestore
                ? VisualTweaksMod.IndexOf(order, entry.Restore) + 1
                : 0;

            listing.Label("VisualTweaks_ShieldOverride_Restore".Translate());
            int picked = SettingsLayout.RadioRow(listing, labels, selected, tips);
            if (picked == selected)
            {
                return;
            }

            if (picked == 0)
            {
                entry.OverrideRestore = false;
            }
            else
            {
                entry.OverrideRestore = true;
                entry.Restore = order[picked - 1];
            }
        }

        private static void DrawResetRow(
            Listing_Standard listing,
            ShieldStyleOverride entry)
        {
            SettingsLayout.Header(
                listing,
                "VisualTweaks_ShieldOverride_GroupReset".Translate());
            int clicked = SettingsLayout.ButtonRow(
                listing,
                new string[] { "VisualTweaks_ShieldOverride_ResetAll".Translate() });
            if (clicked == 0)
            {
                entry.Clear();
            }
        }

        /// <summary>
        /// A surface change can leave an impact or break override that the new
        /// surface cannot draw. Fold it back to that surface's valid default instead
        /// of showing a row whose selected value is never used.
        /// </summary>
        private static void NormalizeImpactAndBreak(
            ShieldStyleOverride entry,
            in ShieldStyle effective)
        {
            if (entry.OverrideImpact && entry.Impact != effective.Impact)
            {
                bool known = effective.Surface == ShieldDomeStyle.Hex
                        || effective.Surface == ShieldDomeStyle.HexBubble
                    ? entry.Impact == ShieldImpactStyle.Brighten
                        || entry.Impact == ShieldImpactStyle.HexWave
                    : entry.Impact == ShieldImpactStyle.Flare
                        || entry.Impact == ShieldImpactStyle.Brighten;
                if (!known)
                {
                    entry.Impact = effective.Impact;
                }
            }

            if (entry.OverrideBreak)
            {
                bool hex = ShieldStyle.IsHexSurface(effective.Surface);
                if (entry.Break == ShieldBreakStyle.HexDissipate && !hex)
                {
                    entry.Break = ShieldBreakStyle.Dissolve;
                }
            }
        }

        private bool ReplacementActive() =>
            target == ShieldHexTarget.Large
                ? ShieldDomeBridge.LargeActive || ShieldDomeBridge.RepulsorActive
                : ShieldDomeBridge.PersonalActive
                    || ShieldDomeBridge.VefBubbleActive
                    || ShieldDomeBridge.VefHediffActive;

        private static string[] WithGlobal(string[] labels)
        {
            string[] result = new string[labels.Length + 1];
            result[0] = "VisualTweaks_ShieldOverride_FollowGlobal".Translate();
            for (int i = 0; i < labels.Length; i++)
            {
                result[i + 1] = labels[i];
            }

            return result;
        }

        private static string[] WithGlobalTip(string[] tips)
        {
            string[] result = new string[tips.Length + 1];
            result[0] = "VisualTweaks_ShieldOverride_FollowGlobalTip".Translate();
            for (int i = 0; i < tips.Length; i++)
            {
                result[i + 1] = tips[i];
            }

            return result;
        }
    }
}
