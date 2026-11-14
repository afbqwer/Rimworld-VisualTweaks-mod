using System;
using System.Collections.Generic;
using ASQPicker;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>One object whose shield the style window edits.</summary>
    internal readonly struct ShieldHost
    {
        internal readonly ThingWithComps Thing;
        internal readonly ShieldHexTarget Target;

        internal ShieldHost(ThingWithComps thing, ShieldHexTarget target)
        {
            Thing = thing;
            Target = target;
        }
    }

    /// <summary>
    /// Shield style editor, opened from the gizmo on a pawn or building.
    ///
    /// A multi-selection is edited as one set: each row writes its choice to every
    /// selected shield, so picking a style does not have to be repeated per object.
    /// Rows only become an override when the player touches them; when the
    /// selection disagrees a row is drawn with nothing selected, and picking a
    /// value makes it uniform again.
    ///
    /// The impact animation is the one exception: personal and large shields use
    /// different enums, so each family present gets its own impact row (the label
    /// names the family when both are selected).
    /// </summary>
    internal sealed class Window_ShieldStyleOverride : Window
    {
        private const float ScrollbarWidth = 16f;

        /// <summary>
        /// First-frame estimate so the scroll view already has room. The real
        /// height is taken from the listing after the first layout pass.
        /// </summary>
        private const float InitialSingleHeight = 820f;
        private const float InitialBothFamiliesHeight = 1000f;

        // Every option either surface family can draw. Only used when the
        // selection holds both hex and non-hex shields, so a value is never
        // displayed as the wrong choice just because the other surface lacks it.
        private static readonly ShieldImpactStyle[] MixedImpactOrder =
        {
            ShieldImpactStyle.Flare,
            ShieldImpactStyle.Brighten,
            ShieldImpactStyle.HexWave,
        };

        private static readonly ShieldBreakStyle[] MixedBreakOrder =
        {
            ShieldBreakStyle.Vanilla,
            ShieldBreakStyle.Dissolve,
            ShieldBreakStyle.HexDissipate,
        };

        private sealed class FamilyGroup
        {
            internal readonly ShieldHexTarget Target;
            internal readonly List<ThingWithComps> Hosts = new List<ThingWithComps>();
            internal readonly List<ShieldStyleOverride> Entries =
                new List<ShieldStyleOverride>();

            internal bool AnyHex;
            internal bool AllHex;

            internal FamilyGroup(ShieldHexTarget target)
            {
                Target = target;
            }
        }

        private readonly int hostCount;
        private readonly List<FamilyGroup> groups = new List<FamilyGroup>();
        private readonly List<ShieldStyleOverride> allEntries =
            new List<ShieldStyleOverride>();
        private readonly Listing_Standard listing = new Listing_Standard();
        private float contentHeight;
        private Vector2 scroll;

        internal Window_ShieldStyleOverride(IReadOnlyList<ShieldHost> hosts)
        {
            hostCount = hosts.Count;
            BuildGroups(hosts);

            doCloseX = true;
            closeOnCancel = true;
            absorbInputAroundWindow = true;
            draggable = true;
            resizeable = false;
            optionalTitle = "VisualTweaks_ShieldOverride_GizmoLabel".Translate();
            contentHeight = groups.Count > 1
                ? InitialBothFamiliesHeight
                : InitialSingleHeight;
        }

        public override Vector2 InitialSize => new Vector2(620f, 660f);

        private void BuildGroups(IReadOnlyList<ShieldHost> hosts)
        {
            for (int i = 0; i < hosts.Count; i++)
            {
                ShieldHost host = hosts[i];
                if (host.Thing == null)
                {
                    continue;
                }

                FamilyGroup group = GroupFor(host.Target);
                if (group.Hosts.Contains(host.Thing))
                {
                    continue;
                }

                group.Hosts.Add(host.Thing);
                group.Entries.Add(
                    ShieldOverrideGameComponent.GetOrCreate(host.Thing, host.Target));
            }

            // Personal first keeps the shared rows stable regardless of the order
            // the objects were selected in.
            for (int i = 0; i < groups.Count; i++)
            {
                allEntries.AddRange(groups[i].Entries);
            }
        }

        private FamilyGroup GroupFor(ShieldHexTarget target)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i].Target == target)
                {
                    return groups[i];
                }
            }

            FamilyGroup group = new FamilyGroup(target);
            if (target == ShieldHexTarget.Personal)
            {
                groups.Insert(0, group);
            }
            else
            {
                groups.Add(group);
            }

            return group;
        }

        private FamilyGroup? GroupOf(ShieldHexTarget target)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i].Target == target)
                {
                    return groups[i];
                }
            }

            return null;
        }

        public override void DoWindowContents(Rect inRect)
        {
            bool scrollOpen = false;
            try
            {
                Rect viewRect = new Rect(
                    0f,
                    0f,
                    inRect.width - ScrollbarWidth,
                    contentHeight);
                Widgets.BeginScrollView(inRect, ref scroll, viewRect);
                scrollOpen = true;

                listing.maxOneColumn = true;
                listing.Begin(viewRect);

                RefreshGroups();
                bool anyHex = AnyHex();
                bool allHex = AllHex();

                DrawHeader(listing, anyHex, allHex);

                // Surface, color, opacity and the hex switches are family-neutral;
                // one row therefore edits every selected shield.
                DrawSurfaceRow(listing);
                DrawColorRow(listing);
                DrawOpacityRow(listing);

                if (anyHex)
                {
                    DrawHexColorRows(listing);
                }

                // Impact is split because the two families use different enums.
                // The label names the family only when both are present.
                FamilyGroup? large = GroupOf(ShieldHexTarget.Large);
                if (large != null)
                {
                    DrawLargeImpactRow(listing, large);
                }

                FamilyGroup? personal = GroupOf(ShieldHexTarget.Personal);
                if (personal != null)
                {
                    DrawPersonalHitRow(listing, personal);
                }

                DrawBreakRow(listing, allHex, anyHex);
                DrawVanishRow(listing);
                DrawRestoreRow(listing);
                DrawResetRow(listing);

                listing.End();

                // The layout height is only known after the first pass. Writing it
                // back keeps the scrollbar honest without measuring every row by hand.
                contentHeight = Mathf.Max(320f, listing.CurHeight + 12f);
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

        private void DrawHeader(
            Listing_Standard listing,
            bool anyHex,
            bool allHex)
        {
            if (hostCount == 1 && allEntries.Count == 1)
            {
                listing.Label(groups[0].Hosts[0].LabelShortCap);
                SettingsLayout.Note(
                    listing,
                    allEntries[0].IsEmpty
                        ? "VisualTweaks_ShieldOverride_UsingGlobal".Translate()
                        : "VisualTweaks_ShieldOverride_UsingLocal".Translate());
            }
            else
            {
                listing.Label(
                    "VisualTweaks_ShieldOverride_MultiTitle".Translate(hostCount));

                bool anyLocal = false;
                for (int i = 0; i < allEntries.Count; i++)
                {
                    if (!allEntries[i].IsEmpty)
                    {
                        anyLocal = true;
                        break;
                    }
                }

                SettingsLayout.Note(
                    listing,
                    anyLocal
                        ? "VisualTweaks_ShieldOverride_MultiSomeLocal".Translate()
                        : "VisualTweaks_ShieldOverride_MultiAllGlobal".Translate());

                if (EntriesDiffer())
                {
                    SettingsLayout.Note(
                        listing,
                        "VisualTweaks_ShieldOverride_MixedNote".Translate());
                }

                if (anyHex && !allHex)
                {
                    SettingsLayout.Note(
                        listing,
                        "VisualTweaks_ShieldOverride_MixedSurface".Translate());
                }
            }

            for (int i = 0; i < groups.Count; i++)
            {
                if (!ReplacementActive(groups[i].Target))
                {
                    SettingsLayout.Warning(
                        listing,
                        "VisualTweaks_ShieldOverride_ReplacementOff".Translate());
                    break;
                }
            }

            // The same master switch as the Shields tab. It lives here too so a
            // player who finds the gizmo noisy can turn it off without hunting
            // through the settings page; the window stays open while they do it.
            bool showGizmos = VisualTweaksSettings.shieldOverrideGizmo;
            listing.CheckboxLabeled(
                "VisualTweaks_ShieldOverride_Enable".Translate(),
                ref showGizmos,
                "VisualTweaks_ShieldOverride_EnableTip".Translate());
            if (showGizmos != VisualTweaksSettings.shieldOverrideGizmo)
            {
                VisualTweaksSettings.shieldOverrideGizmo = showGizmos;

                // This window is not the settings dialog, so the game will not
                // write mod settings on its close. Persist the master switch
                // immediately; it is one tiny XML write on a click.
                try
                {
                    LoadedModManager.GetMod<VisualTweaksMod>()?.WriteSettings();
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce(
                        "[Visual Tweaks] Could not persist the gizmo switch: " + ex,
                        typeof(Window_ShieldStyleOverride).GetHashCode() ^ 0x51);
                }
            }
        }

        /// <summary>
        /// Resolves the effective style for every host. Stale impact/break
        /// overrides are folded back to what each entry's own surface can draw,
        /// and the hex flags tell the shared rows whether to appear.
        /// </summary>
        private void RefreshGroups()
        {
            for (int g = 0; g < groups.Count; g++)
            {
                FamilyGroup group = groups[g];
                bool anyHex = false;
                bool allHex = true;

                for (int i = 0; i < group.Hosts.Count; i++)
                {
                    ShieldStyle style = ShieldStyle.For(group.Target, group.Hosts[i]);
                    NormalizeImpactAndBreak(group.Entries[i], style);

                    bool hex = ShieldStyle.IsHexSurface(style.Surface);
                    anyHex |= hex;
                    allHex &= hex;
                }

                group.AnyHex = anyHex;
                group.AllHex = allHex;
            }
        }

        private bool AnyHex()
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i].AnyHex)
                {
                    return true;
                }
            }

            return false;
        }

        private bool AllHex()
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (!groups[i].AllHex)
                {
                    return false;
                }
            }

            return groups.Count > 0;
        }

        private void DrawSurfaceRow(Listing_Standard listing)
        {
            // Both families support every shared profile; only the dome's extra
            // standalone Bubble is unavailable to belts, so it is hidden when the
            // selection mixes families rather than silently mapping to Vanilla.
            bool largeOnly = GroupOf(ShieldHexTarget.Large) != null
                && GroupOf(ShieldHexTarget.Personal) == null;
            ShieldDomeStyle[] order = largeOnly
                ? VisualTweaksMod.LargeShieldStyleOrder
                : VisualTweaksMod.PersonalShieldStyleOrder;
            string[] labels = WithGlobal(
                VisualTweaksMod.ShieldStyleLabels(largeOnly));
            string[] tips = WithGlobalTip(
                VisualTweaksMod.ShieldStyleTips(largeOnly));

            int selected = CommonChoice(
                allEntries,
                static entry => entry.OverrideSurface,
                static entry => entry.Surface,
                order);

            listing.Label("VisualTweaks_ShieldOverride_Surface".Translate());
            int picked = SettingsLayout.RadioRows(listing, labels, selected, tips, 3);
            if (picked < 0 || picked == selected)
            {
                return;
            }

            for (int i = 0; i < allEntries.Count; i++)
            {
                ShieldStyleOverride entry = allEntries[i];
                if (picked == 0)
                {
                    entry.OverrideSurface = false;
                }
                else
                {
                    entry.OverrideSurface = true;
                    entry.Surface = order[picked - 1];
                }
            }

            if (picked > 0 && ShieldStyle.IsHexSurface(order[picked - 1]))
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    ShieldHexMap.EnsureBaked(groups[i].Target);
                }
            }
        }

        private void DrawColorRow(Listing_Standard listing)
        {
            SettingsLayout.SubHeader(
                listing,
                "VisualTweaks_ShieldOverride_ColorGroup".Translate());

            bool allOverridden = true;
            bool anyOverridden = false;
            Color shown = Color.white;
            bool haveShown = false;

            for (int i = 0; i < allEntries.Count; i++)
            {
                ShieldStyleOverride entry = allEntries[i];
                if (!entry.OverrideColor)
                {
                    allOverridden = false;
                    continue;
                }

                anyOverridden = true;
                if (!haveShown)
                {
                    shown = entry.ShieldColor;
                    haveShown = true;
                }
            }

            bool enabled = allOverridden;
            listing.CheckboxLabeled(
                "VisualTweaks_ShieldOverride_ColorOverride".Translate(),
                ref enabled,
                "VisualTweaks_ShieldOverride_ColorOverrideTip".Translate());
            if (enabled != allOverridden)
            {
                if (enabled)
                {
                    // Starting from a half-overridden selection keeps the color the
                    // player can already see instead of jumping to the def color.
                    Color start = anyOverridden ? shown : BaseColor();
                    for (int i = 0; i < allEntries.Count; i++)
                    {
                        ShieldStyleOverride entry = allEntries[i];
                        entry.OverrideColor = true;
                        entry.ShieldColor = start;
                    }

                    anyOverridden = true;
                    shown = start;
                    haveShown = true;
                }
                else
                {
                    for (int i = 0; i < allEntries.Count; i++)
                    {
                        allEntries[i].OverrideColor = false;
                    }

                    anyOverridden = false;
                }
            }

            if (!anyOverridden)
            {
                return;
            }

            if (!haveShown)
            {
                shown = BaseColor();
            }

            Rect row = listing.GetRect(30f);
            Rect swatch = new Rect(row.x, row.y + 2f, 26f, 26f);
            ColorPickerWindow.DrawCheckerSwatch(swatch, shown);

            Rect pick = new Rect(swatch.xMax + 8f, row.y, 160f, 30f);
            if (Widgets.ButtonText(
                    pick,
                    "VisualTweaks_ShieldOverride_ColorPick".Translate()))
            {
                Find.WindowStack?.Add(
                    new ColorPickerWindow(
                        shown,
                        delegate(Color picked)
                        {
                            // The shield's own alpha carries transparency; the
                            // picker's alpha is deliberately discarded.
                            Color rgb = new Color(picked.r, picked.g, picked.b, 1f);
                            for (int i = 0; i < allEntries.Count; i++)
                            {
                                ShieldStyleOverride entry = allEntries[i];
                                entry.OverrideColor = true;
                                entry.ShieldColor = rgb;
                            }
                        }));
            }

            Rect original = new Rect(pick.xMax + 8f, row.y, 170f, 30f);
            if (Widgets.ButtonText(
                    original,
                    "VisualTweaks_ShieldOverride_ColorOriginal".Translate()))
            {
                for (int i = 0; i < allEntries.Count; i++)
                {
                    allEntries[i].OverrideColor = false;
                }
            }

            TooltipHandler.TipRegion(
                swatch,
                "VisualTweaks_ShieldOverride_ColorSwatchTip".Translate());
            listing.Gap(SettingsLayout.RowGap);
        }

        private void DrawOpacityRow(Listing_Standard listing)
        {
            SettingsLayout.SubHeader(
                listing,
                "VisualTweaks_ShieldOverride_OpacityGroup".Translate());

            float value = allEntries.Count > 0
                ? allEntries[0].OpacityMultiplier
                : 1f;
            bool mixed = false;
            for (int i = 1; i < allEntries.Count; i++)
            {
                if (Mathf.Abs(allEntries[i].OpacityMultiplier - value) > 0.0001f)
                {
                    mixed = true;
                    break;
                }
            }

            float updated = SettingsLayout.Slider(
                listing,
                "VisualTweaks_ShieldOverride_Opacity".Translate(),
                value,
                VisualTweaksSettings.MinShieldOpacityMultiplier,
                VisualTweaksSettings.MaxShieldOpacityMultiplier,
                "VisualTweaks_ShieldOverride_OpacityTip".Translate(),
                0.01f,
                mixed
                    ? "VisualTweaks_ShieldOverride_MixedValue".Translate().Resolve()
                    : null);

            if (!Mathf.Approximately(updated, value))
            {
                for (int i = 0; i < allEntries.Count; i++)
                {
                    allEntries[i].OpacityMultiplier = updated;
                }
            }
        }

        private void DrawHexColorRows(Listing_Standard listing)
        {
            SettingsLayout.SubHeader(
                listing,
                "VisualTweaks_ShieldOverride_HexColorGroup".Translate());
            DrawTriStateRow(
                listing,
                "VisualTweaks_ShieldOverride_LineFollows".Translate(),
                line: true);
            DrawTriStateRow(
                listing,
                "VisualTweaks_ShieldOverride_FacesFollow".Translate(),
                line: false);
        }

        /// <summary>
        /// A boolean with an explicit "follow global" state. A plain checkbox
        /// would silently turn every unset shield into an override the first time
        /// the global setting moved. A mixed value is drawn as no selection;
        /// picking one applies it to every selected shield.
        /// </summary>
        private void DrawTriStateRow(
            Listing_Standard listing,
            string label,
            bool line)
        {
            string[] labels =
            {
                "VisualTweaks_ShieldOverride_FollowGlobal".Translate(),
                "VisualTweaks_ShieldOverride_TriOn".Translate(),
                "VisualTweaks_ShieldOverride_TriOff".Translate(),
            };
            string[] tips =
            {
                "VisualTweaks_ShieldOverride_FollowGlobalTip".Translate(),
                string.Empty,
                string.Empty,
            };

            int selected = -1;
            bool have = false;
            for (int i = 0; i < allEntries.Count; i++)
            {
                ShieldStyleOverride entry = allEntries[i];
                bool overridden = line
                    ? entry.OverrideHexLineColor
                    : entry.OverrideHexFaceColor;
                bool value = line
                    ? entry.HexLineFollowsShield
                    : entry.HexFacesFollowShield;
                int index = !overridden ? 0 : (value ? 1 : 2);

                if (!have)
                {
                    selected = index;
                    have = true;
                }
                else if (selected != index)
                {
                    selected = -1;
                    break;
                }
            }

            listing.Label(label);
            int picked = SettingsLayout.RadioRow(listing, labels, selected, tips);
            if (picked < 0 || picked == selected)
            {
                return;
            }

            for (int i = 0; i < allEntries.Count; i++)
            {
                ShieldStyleOverride entry = allEntries[i];
                bool overridden = picked > 0;
                bool value = picked == 1;
                if (line)
                {
                    entry.OverrideHexLineColor = overridden;
                    if (overridden)
                    {
                        entry.HexLineFollowsShield = value;
                    }
                }
                else
                {
                    entry.OverrideHexFaceColor = overridden;
                    if (overridden)
                    {
                        entry.HexFacesFollowShield = value;
                    }
                }
            }
        }

        private void DrawLargeImpactRow(
            Listing_Standard listing,
            FamilyGroup group)
        {
            // HexWave needs the lattice and Flare does not exist on it, so a group
            // holding both surface kinds gets the union. Each entry then keeps the
            // value it can draw; NormalizeImpactAndBreak folds the rest back.
            ShieldImpactStyle[] order;
            string[] optionLabels;
            string[] optionTips;
            if (group.AllHex)
            {
                order = VisualTweaksMod.HexImpactOrder;
                optionLabels = VisualTweaksMod.ImpactLabels(true);
                optionTips = VisualTweaksMod.ImpactTips(true);
            }
            else if (group.AnyHex)
            {
                order = MixedImpactOrder;
                optionLabels = UnionImpactLabels();
                optionTips = UnionImpactTips();
            }
            else
            {
                order = VisualTweaksMod.StandardImpactOrder;
                optionLabels = VisualTweaksMod.ImpactLabels(false);
                optionTips = VisualTweaksMod.ImpactTips(false);
            }

            string[] labels = WithGlobal(optionLabels);
            string[] tips = WithGlobalTip(optionTips);

            int selected = CommonChoice(
                group.Entries,
                static entry => entry.OverrideImpact,
                static entry => entry.Impact,
                order);

            listing.Label(FamilyPrefix(ShieldHexTarget.Large)
                + "VisualTweaks_ShieldOverride_Impact".Translate());
            int picked = SettingsLayout.RadioRow(listing, labels, selected, tips);
            if (picked < 0 || picked == selected)
            {
                return;
            }

            for (int i = 0; i < group.Entries.Count; i++)
            {
                ShieldStyleOverride entry = group.Entries[i];
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
        }

        private void DrawPersonalHitRow(
            Listing_Standard listing,
            FamilyGroup group)
        {
            PersonalShieldHitStyle[] order = VisualTweaksMod.PersonalHitOrder;
            string[] labels = WithGlobal(VisualTweaksMod.PersonalHitLabels());
            string[] tips = WithGlobalTip(VisualTweaksMod.PersonalHitTips());

            int selected = CommonChoice(
                group.Entries,
                static entry => entry.OverridePersonalHit,
                static entry => entry.PersonalHit,
                order);

            listing.Label(FamilyPrefix(ShieldHexTarget.Personal)
                + "VisualTweaks_ShieldOverride_PersonalHit".Translate());
            int picked = SettingsLayout.RadioRow(listing, labels, selected, tips);
            if (picked < 0 || picked == selected)
            {
                return;
            }

            for (int i = 0; i < group.Entries.Count; i++)
            {
                ShieldStyleOverride entry = group.Entries[i];
                if (picked == 0)
                {
                    entry.OverridePersonalHit = false;
                }
                else
                {
                    entry.OverridePersonalHit = true;
                    entry.PersonalHit = order[picked - 1];
                }
            }

            if (picked > 0 && order[picked - 1] == PersonalShieldHitStyle.HexWave)
            {
                ShieldHexMap.EnsureBaked(ShieldHexTarget.Personal);
            }
        }

        private void DrawBreakRow(
            Listing_Standard listing,
            bool allHex,
            bool anyHex)
        {
            ShieldBreakStyle[] order;
            string[] optionLabels;
            string[] optionTips;
            if (allHex)
            {
                order = VisualTweaksMod.HexBreakOrder;
                optionLabels = VisualTweaksMod.BreakLabels(true);
                optionTips = VisualTweaksMod.BreakTips(true);
            }
            else if (anyHex)
            {
                order = MixedBreakOrder;
                optionLabels = UnionBreakLabels();
                optionTips = UnionBreakTips();
            }
            else
            {
                order = VisualTweaksMod.StandardBreakOrder;
                optionLabels = VisualTweaksMod.BreakLabels(false);
                optionTips = VisualTweaksMod.BreakTips(false);
            }

            string[] labels = WithGlobal(optionLabels);
            string[] tips = WithGlobalTip(optionTips);

            int selected = CommonChoice(
                allEntries,
                static entry => entry.OverrideBreak,
                static entry => entry.Break,
                order);

            listing.Label("VisualTweaks_ShieldOverride_Break".Translate());
            int picked = SettingsLayout.RadioRow(listing, labels, selected, tips);
            if (picked < 0 || picked == selected)
            {
                return;
            }

            for (int i = 0; i < allEntries.Count; i++)
            {
                ShieldStyleOverride entry = allEntries[i];
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
        }

        private void DrawRestoreRow(Listing_Standard listing)
        {
            ShieldRestoreStyle[] order = VisualTweaksMod.RestoreOrder;
            string[] labels = WithGlobal(VisualTweaksMod.RestoreLabels());
            string[] tips = WithGlobalTip(VisualTweaksMod.RestoreTips());

            int selected = CommonChoice(
                allEntries,
                static entry => entry.OverrideRestore,
                static entry => entry.Restore,
                order);

            listing.Label("VisualTweaks_ShieldOverride_Restore".Translate());
            int picked = SettingsLayout.RadioRow(listing, labels, selected, tips);
            if (picked < 0 || picked == selected)
            {
                return;
            }

            for (int i = 0; i < allEntries.Count; i++)
            {
                ShieldStyleOverride entry = allEntries[i];
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
        }

        private void DrawVanishRow(Listing_Standard listing)
        {
            ShieldVanishStyle[] order = VisualTweaksMod.VanishOrder;
            string[] labels = WithGlobal(VisualTweaksMod.VanishLabels());
            string[] tips = WithGlobalTip(VisualTweaksMod.VanishTips());

            int selected = CommonChoice(
                allEntries,
                static entry => entry.OverrideVanish,
                static entry => entry.Vanish,
                order);

            listing.Label("VisualTweaks_ShieldOverride_Vanish".Translate());
            int picked = SettingsLayout.RadioRow(listing, labels, selected, tips);
            if (picked < 0 || picked == selected)
            {
                return;
            }

            for (int i = 0; i < allEntries.Count; i++)
            {
                ShieldStyleOverride entry = allEntries[i];
                if (picked == 0)
                {
                    entry.OverrideVanish = false;
                }
                else
                {
                    entry.OverrideVanish = true;
                    entry.Vanish = order[picked - 1];
                }
            }
        }

        private void DrawResetRow(Listing_Standard listing)
        {
            SettingsLayout.Header(
                listing,
                "VisualTweaks_ShieldOverride_GroupReset".Translate());
            int clicked = SettingsLayout.ButtonRow(
                listing,
                new string[] { "VisualTweaks_ShieldOverride_ResetAll".Translate() });
            if (clicked != 0)
            {
                return;
            }

            for (int i = 0; i < allEntries.Count; i++)
            {
                allEntries[i].Clear();
            }
        }

        /// <summary>
        /// The index every entry agrees on (0 = follow global), or -1 when they
        /// disagree. RadioRows already renders -1 as "nothing selected".
        /// </summary>
        private static int CommonChoice<T>(
            List<ShieldStyleOverride> entries,
            Func<ShieldStyleOverride, bool> overridden,
            Func<ShieldStyleOverride, T> value,
            T[] order)
        {
            if (entries.Count == 0)
            {
                return 0;
            }

            int selected = overridden(entries[0])
                ? VisualTweaksMod.IndexOf(order, value(entries[0])) + 1
                : 0;

            for (int i = 1; i < entries.Count; i++)
            {
                int index = overridden(entries[i])
                    ? VisualTweaksMod.IndexOf(order, value(entries[i])) + 1
                    : 0;
                if (index != selected)
                {
                    return -1;
                }
            }

            return selected;
        }

        /// <summary>
        /// True when the entries hold different explicit choices. Only rows that
        /// will actually be drawn are compared, so a stale personal impact value
        /// cannot make an otherwise uniform selection look mixed.
        /// </summary>
        private bool EntriesDiffer()
        {
            if (allEntries.Count < 2)
            {
                return false;
            }

            // Rows shared by both families are compared across the whole
            // selection. Hex flags only matter, and are only shown, when at least
            // one selected shield actually draws the lattice.
            bool anyHex = AnyHex();
            ShieldStyleOverride first = allEntries[0];
            for (int i = 1; i < allEntries.Count; i++)
            {
                ShieldStyleOverride entry = allEntries[i];
                if (Differs(first.OverrideSurface, first.Surface, entry.OverrideSurface, entry.Surface)
                    || Differs(first.OverrideBreak, first.Break, entry.OverrideBreak, entry.Break)
                    || Differs(first.OverrideRestore, first.Restore, entry.OverrideRestore, entry.Restore)
                    || Differs(first.OverrideVanish, first.Vanish, entry.OverrideVanish, entry.Vanish)
                    || first.OverrideColor != entry.OverrideColor
                    || (first.OverrideColor && !SameRgb(first.ShieldColor, entry.ShieldColor))
                    || Mathf.Abs(first.OpacityMultiplier - entry.OpacityMultiplier) > 0.0001f)
                {
                    return true;
                }

                if (anyHex
                    && (Differs(first.OverrideHexLineColor, first.HexLineFollowsShield, entry.OverrideHexLineColor, entry.HexLineFollowsShield)
                        || Differs(first.OverrideHexFaceColor, first.HexFacesFollowShield, entry.OverrideHexFaceColor, entry.HexFacesFollowShield)))
                {
                    return true;
                }
            }

            // Impact and personal-hit rows are per family; compare them only among
            // the entries that actually use them.
            for (int g = 0; g < groups.Count; g++)
            {
                FamilyGroup group = groups[g];
                if (group.Entries.Count < 2)
                {
                    continue;
                }

                ShieldStyleOverride head = group.Entries[0];
                for (int i = 1; i < group.Entries.Count; i++)
                {
                    ShieldStyleOverride entry = group.Entries[i];
                    bool differs = group.Target == ShieldHexTarget.Large
                        ? Differs(head.OverrideImpact, head.Impact, entry.OverrideImpact, entry.Impact)
                        : Differs(head.OverridePersonalHit, head.PersonalHit, entry.OverridePersonalHit, entry.PersonalHit);
                    if (differs)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool Differs<T>(
            bool firstOverridden,
            T firstValue,
            bool entryOverridden,
            T entryValue)
        {
            return firstOverridden != entryOverridden
                || (firstOverridden && !EqualityComparer<T>.Default.Equals(firstValue, entryValue));
        }

        private static bool SameRgb(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) <= 0.0001f
            && Mathf.Abs(a.g - b.g) <= 0.0001f
            && Mathf.Abs(a.b - b.b) <= 0.0001f;

        /// <summary>The first host's def color, used as the picker's starting point.</summary>
        private Color BaseColor()
        {
            for (int g = 0; g < groups.Count; g++)
            {
                for (int i = 0; i < groups[g].Hosts.Count; i++)
                {
                    CompProjectileInterceptor? interceptor =
                        groups[g].Hosts[i].GetComp<CompProjectileInterceptor>();
                    if (interceptor != null)
                    {
                        return interceptor.Props.color;
                    }
                }
            }

            // Vanilla belts and most VEF bubbles are white; a VEF bubble with a
            // def color still lets the player pick from white and adjust.
            return Color.white;
        }

        private string FamilyPrefix(ShieldHexTarget target)
        {
            if (groups.Count < 2)
            {
                return string.Empty;
            }

            string label = target == ShieldHexTarget.Personal
                ? "VisualTweaks_ShieldOverride_FamilyPersonal".Translate()
                : "VisualTweaks_ShieldOverride_FamilyLarge".Translate();
            return label + " · ";
        }

        private static bool ReplacementActive(ShieldHexTarget target) =>
            target == ShieldHexTarget.Large
                ? ShieldDomeBridge.LargeActive || ShieldDomeBridge.RepulsorActive
                : ShieldDomeBridge.PersonalActive
                    || ShieldDomeBridge.VefBubbleActive
                    || ShieldDomeBridge.VefHediffActive;

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

        private static string[] UnionImpactLabels()
        {
            return new string[]
            {
                "VisualTweaks_ShieldDome_ImpactFlare".Translate(),
                "VisualTweaks_ShieldDome_ImpactBrighten".Translate(),
                "VisualTweaks_ShieldDome_ImpactHexWave".Translate(),
            };
        }

        private static string[] UnionImpactTips()
        {
            return new string[]
            {
                "VisualTweaks_ShieldDome_ImpactFlareTip".Translate(),
                "VisualTweaks_ShieldDome_ImpactBrightenTip".Translate(),
                "VisualTweaks_ShieldDome_ImpactHexWaveTip".Translate(),
            };
        }

        private static string[] UnionBreakLabels()
        {
            return new string[]
            {
                "VisualTweaks_ShieldDome_BreakVanilla".Translate(),
                "VisualTweaks_ShieldDome_BreakDissolve".Translate(),
                "VisualTweaks_ShieldDome_BreakHexDissipate".Translate(),
            };
        }

        private static string[] UnionBreakTips()
        {
            return new string[]
            {
                "VisualTweaks_ShieldDome_BreakVanillaTip".Translate(),
                "VisualTweaks_ShieldDome_BreakDissolveTip".Translate(),
                "VisualTweaks_ShieldDome_BreakHexDissipateTip".Translate(),
            };
        }

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
