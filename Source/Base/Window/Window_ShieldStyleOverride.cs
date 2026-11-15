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
    /// Two scopes share one set of rows. <see cref="EditScope.Object"/> edits the
    /// record of the selected shields themselves; <see cref="EditScope.Type"/> edits
    /// the record of the def that draws them, which every shield of that kind picks
    /// up. The object record wins over the type record, which wins over the global
    /// settings, so a player can restyle a whole shield type and still special-case
    /// one pawn.
    ///
    /// Inside a scope a multi-selection is edited as one set: each row writes its
    /// choice to every record in the scope, so picking a style does not have to be
    /// repeated per object. Rows only become an override when the player touches
    /// them; when the selection disagrees a row is drawn with nothing selected, and
    /// picking a value makes it uniform again.
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

        private enum EditScope
        {
            Object,
            Type,
        }

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
            internal readonly List<ShieldStyleFields> Entries =
                new List<ShieldStyleFields>();

            internal bool AnyHex;
            internal bool AllHex;

            internal FamilyGroup(ShieldHexTarget target)
            {
                Target = target;
            }
        }

        private readonly int hostCount;
        private readonly List<ShieldHost> hosts = new List<ShieldHost>();
        private readonly List<FamilyGroup> groups = new List<FamilyGroup>();
        private readonly List<ShieldStyleFields> allEntries =
            new List<ShieldStyleFields>();
        private readonly Listing_Standard listing = new Listing_Standard();
        private float contentHeight;
        private Vector2 scroll;

        private EditScope scope = EditScope.Object;

        // Every shield the selected hosts actually carry, deduplicated by family and
        // def. A pawn wearing a belt and a psycast shield has two; nothing on a pawn
        // can be selected on its own, so the type scope offers these as a choice.
        private readonly List<ShieldSource> typeSources = new List<ShieldSource>();
        private int typeSourceIndex;

        // Set once the type scope is entered; guarantees the settings file is written
        // on close even if the player only cleared a record.
        private bool typeScopeTouched;

        /// <summary>
        /// The type record of <see cref="typeSourceIndex"/>, created the first time the
        /// type scope is opened. Null until then, so a player who only edits objects
        /// never leaves one behind.
        /// </summary>
        private ShieldTypeStyleOverride? typeEntry;

        internal Window_ShieldStyleOverride(IReadOnlyList<ShieldHost> hosts)
        {
            for (int i = 0; i < hosts.Count; i++)
            {
                ShieldHost host = hosts[i];
                if (host.Thing == null || ContainsHost(host.Thing))
                {
                    continue;
                }

                this.hosts.Add(host);
            }

            hostCount = this.hosts.Count;
            ResolveTypeSources();
            BuildGroups();

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

        /// <summary>
        /// A type record is created the moment the type scope is opened. If the player
        /// ends up clearing it again, drop it: an empty record would be written to the
        /// settings file and would keep the per-draw lookup switched on for nothing.
        ///
        /// Type styles are mod settings, so a session that touched them has to write
        /// that file: RimWorld only writes it on its own when the mod settings dialog
        /// closes, and this window is not that dialog. Object records need no write --
        /// they go to the save with the game.
        /// </summary>
        public override void PostClose()
        {
            base.PostClose();
            try
            {
                for (int i = 0; i < hosts.Count; i++)
                {
                    ShieldOverrideGameComponent.Prune(hosts[i].Thing);
                }

                PruneCurrentTypeEntry();

                if (typeScopeTouched)
                {
                    VisualTweaksSettings.PersistShieldTypeOverrides();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(
                    "[Visual Tweaks] Could not finish shield style bookkeeping: " + ex,
                    typeof(Window_ShieldStyleOverride).GetHashCode() ^ 0x63);
            }
        }

        private bool ContainsHost(ThingWithComps thing)
        {
            for (int i = 0; i < hosts.Count; i++)
            {
                if (ReferenceEquals(hosts[i].Thing, thing))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Collects the shields the hosts carry. Their records are only looked up, never
        /// created: opening the window must not add a record the player never asked for.
        /// </summary>
        private void ResolveTypeSources()
        {
            typeSources.Clear();
            typeSourceIndex = 0;
            typeEntry = null;

            for (int i = 0; i < hosts.Count; i++)
            {
                ShieldType.CollectSources(hosts[i].Thing, typeSources);
            }

            if (typeSources.Count > 0)
            {
                ShieldSource first = typeSources[0];
                typeEntry = VisualTweaksSettings.GetShieldTypeOverride(
                    first.Target,
                    first.DefName);
            }
        }

        private ShieldSource CurrentSource => typeSources[typeSourceIndex];

        /// <summary>
        /// Switches which carried shield the type scope edits. The record of the type
        /// just left is dropped if the player never set anything in it, so browsing the
        /// list does not fill the save with empty records.
        /// </summary>
        private void SelectTypeSource(int index)
        {
            if (index < 0 || index >= typeSources.Count || index == typeSourceIndex)
            {
                return;
            }

            PruneCurrentTypeEntry();
            typeScopeTouched = true;
            typeSourceIndex = index;
            ShieldSource source = CurrentSource;
            typeEntry = VisualTweaksSettings.GetShieldTypeOverride(
                source.Target,
                source.DefName);
            BuildGroups();
        }

        private void PruneCurrentTypeEntry()
        {
            if (typeEntry == null || !typeEntry.IsEmpty)
            {
                return;
            }

            VisualTweaksSettings.PruneShieldTypeOverride(
                CurrentSource.Target,
                CurrentSource.DefName);
            typeEntry = null;
        }

        /// <summary>
        /// Fills the active scope's groups. Called once at open and again whenever the
        /// scope changes; the rows themselves read <see cref="allEntries"/>.
        /// </summary>
        private void BuildGroups()
        {
            groups.Clear();
            allEntries.Clear();

            if (scope == EditScope.Type && typeSources.Count > 0)
            {
                FamilyGroup group = new FamilyGroup(CurrentSource.Target);
                for (int i = 0; i < hosts.Count; i++)
                {
                    group.Hosts.Add(hosts[i].Thing);
                }

                group.Entries.Add(TypeEntry());
                groups.Add(group);
            }
            else
            {
                scope = EditScope.Object;
                for (int i = 0; i < hosts.Count; i++)
                {
                    ShieldHost host = hosts[i];
                    FamilyGroup group = GroupFor(host.Target);
                    if (group.Hosts.Contains(host.Thing))
                    {
                        continue;
                    }

                    group.Hosts.Add(host.Thing);
                    group.Entries.Add(
                        ShieldOverrideGameComponent.GetOrCreate(host.Thing, host.Target));
                }
            }

            // Personal first keeps the shared rows stable regardless of the order
            // the objects were selected in.
            for (int i = 0; i < groups.Count; i++)
            {
                allEntries.AddRange(groups[i].Entries);
            }
        }

        private ShieldTypeStyleOverride TypeEntry()
        {
            if (typeEntry == null)
            {
                ShieldSource source = CurrentSource;
                typeEntry = VisualTweaksSettings.GetOrCreateShieldTypeOverride(
                    source.Target,
                    source.DefName);
            }

            return typeEntry;
        }

        /// <summary>
        /// The type layer an object record sits on, for previewing its rows. This asks
        /// the component rather than the in-progress record, so the object scope shows
        /// what the shield really draws with no matter which source the type scope was
        /// last left on. Only a host with two shields of one family can make those
        /// differ, and the first source is the one the player sees most.
        /// </summary>
        private ShieldStyleFields? TypeLayerOf(ShieldHexTarget target)
        {
            for (int i = 0; i < typeSources.Count; i++)
            {
                ShieldSource source = typeSources[i];
                if (source.Target == target)
                {
                    return VisualTweaksSettings.GetShieldTypeOverride(
                        source.Target,
                        source.DefName);
                }
            }

            return null;
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

                // The scope is chosen before the records are resolved: switching it
                // rebuilds the groups, and every row below reads them in this pass.
                DrawScopeRows(listing);
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

        /// <summary>
        /// Object or type, and -- when the host carries more than one shield -- which of
        /// them the type scope edits. Both rows sit at the top because they decide what
        /// every row below is reading, which is also why they are drawn before the
        /// records are resolved.
        /// </summary>
        private void DrawScopeRows(Listing_Standard listing)
        {
            if (typeSources.Count == 0)
            {
                SettingsLayout.Note(
                    listing,
                    "VisualTweaks_ShieldOverride_TypeUnknown".Translate());
                return;
            }

            string[] labels =
            {
                "VisualTweaks_ShieldOverride_ScopeObject".Translate(),
                "VisualTweaks_ShieldOverride_ScopeType".Translate(CurrentSource.Label),
            };
            string[] tips =
            {
                "VisualTweaks_ShieldOverride_ScopeObjectTip".Translate(),
                "VisualTweaks_ShieldOverride_ScopeTypeTip".Translate(),
            };

            listing.Label("VisualTweaks_ShieldOverride_Scope".Translate());
            int picked = SettingsLayout.RadioRow(
                listing,
                labels,
                scope == EditScope.Type ? 1 : 0,
                tips);
            SetScope(picked == 1 ? EditScope.Type : EditScope.Object);

            if (scope != EditScope.Type || typeSources.Count < 2)
            {
                return;
            }

            // Two shields of one pawn cannot be selected separately -- the click always
            // lands on the pawn -- so this is the only place the choice can be made.
            string[] sourceLabels = new string[typeSources.Count];
            for (int i = 0; i < typeSources.Count; i++)
            {
                sourceLabels[i] = SourceLabel(typeSources[i]);
            }

            listing.Label("VisualTweaks_ShieldOverride_TypePick".Translate());
            int source = SettingsLayout.RadioRows(
                listing,
                sourceLabels,
                typeSourceIndex,
                null,
                2);
            SelectTypeSource(source);
        }

        /// <summary>
        /// The type's label, with the family in front when the host carries both kinds,
        /// because a belt and a dome can share a label.
        /// </summary>
        private string SourceLabel(ShieldSource source)
        {
            bool bothFamilies = false;
            for (int i = 0; i < typeSources.Count; i++)
            {
                if (typeSources[i].IsPersonal != source.IsPersonal)
                {
                    bothFamilies = true;
                    break;
                }
            }

            if (!bothFamilies)
            {
                return source.Label;
            }

            string family = source.IsPersonal
                ? "VisualTweaks_ShieldOverride_FamilyPersonal".Translate()
                : "VisualTweaks_ShieldOverride_FamilyLarge".Translate();
            return family + " · " + source.Label;
        }

        private void SetScope(EditScope next)
        {
            if (scope == next)
            {
                return;
            }

            scope = next;
            if (next == EditScope.Type)
            {
                typeScopeTouched = true;
            }

            BuildGroups();
        }

        /// <summary>
        /// What the object's own record means, in the layered order. Saying "uses the
        /// global style" while a type record is in force would be wrong, and the player
        /// needs to know which scope to open to move a value.
        ///
        /// A host carrying two shields falls through to both their types, so the note
        /// names every type the object's unset rows would follow.
        /// </summary>
        private string ObjectStateNote(ShieldStyleFields entry)
        {
            if (!entry.IsEmpty)
            {
                return "VisualTweaks_ShieldOverride_UsingLocal".Translate();
            }

            string labels = TypedLayerLabels();
            if (!labels.NullOrEmpty())
            {
                return "VisualTweaks_ShieldOverride_UsingType".Translate(labels);
            }

            return "VisualTweaks_ShieldOverride_UsingGlobal".Translate();
        }

        /// <summary>
        /// The labels of every carried shield whose type record is set, joined for a
        /// note, or an empty string when the type layer contributes nothing.
        /// </summary>
        private string TypedLayerLabels()
        {
            string separator = "VisualTweaks_ShieldOverride_TypeJoin".Translate();
            string labels = string.Empty;
            for (int i = 0; i < typeSources.Count; i++)
            {
                ShieldSource source = typeSources[i];
                ShieldTypeStyleOverride? record =
                    VisualTweaksSettings.GetShieldTypeOverride(
                        source.Target,
                        source.DefName);
                if (record == null)
                {
                    continue;
                }

                if (labels.Length > 0)
                {
                    labels += separator;
                }

                labels += source.Label;
            }

            return labels;
        }

        /// <summary>
        /// The comp actually drawing this shield. The def says what the shield is; the
        /// comp type says which system is behind it, which is the only way to tell a
        /// mod's interceptor subclass -- or VEF's hediff shield -- from the vanilla one.
        /// </summary>
        private static void DrawCompTypeNote(Listing_Standard listing, ShieldSource source)
        {
            string name = source.CompTypeName;
            if (name.NullOrEmpty())
            {
                return;
            }

            SettingsLayout.Note(
                listing,
                "VisualTweaks_ShieldOverride_CompType".Translate(name));
        }

        private void DrawHeader(
            Listing_Standard listing,
            bool anyHex,
            bool allHex)
        {
            if (scope == EditScope.Type)
            {
                ShieldSource source = CurrentSource;
                listing.Label(source.Label);
                SettingsLayout.Note(
                    listing,
                    "VisualTweaks_ShieldOverride_TypeNote".Translate(
                        source.Label,
                        source.DefName));
                DrawCompTypeNote(listing, source);
            }
            else if (hostCount == 1 && allEntries.Count == 1)
            {
                listing.Label(groups[0].Hosts[0].LabelShortCap);
                SettingsLayout.Note(listing, ObjectStateNote(allEntries[0]));

                // With one shield there is no ambiguity about which comp draws it, so
                // the object scope can say it too and save a click.
                if (typeSources.Count == 1)
                {
                    DrawCompTypeNote(listing, typeSources[0]);
                }
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
        /// Resolves the effective style for every record in the active scope. Stale
        /// impact/break overrides are folded back to what each record's own surface
        /// can draw, and the hex flags tell the shared rows whether to appear.
        ///
        /// The rows are resolved from the records, not by asking the game again: the
        /// editor already holds them, and in the object scope each record is previewed
        /// on top of the type layer it really draws on.
        /// </summary>
        private void RefreshGroups()
        {
            for (int g = 0; g < groups.Count; g++)
            {
                FamilyGroup group = groups[g];
                ShieldStyleFields? lower = scope == EditScope.Type
                    ? null
                    : TypeLayerOf(group.Target);
                bool anyHex = false;
                bool allHex = true;

                for (int i = 0; i < group.Entries.Count; i++)
                {
                    ShieldStyleFields entry = group.Entries[i];
                    ShieldStyle style = scope == EditScope.Type
                        ? ShieldStyle.ForLayers(group.Target, null, entry)
                        : ShieldStyle.ForLayers(group.Target, entry, lower);
                    NormalizeImpactAndBreak(entry, style);

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
                ShieldStyleFields entry = allEntries[i];
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
                ShieldStyleFields entry = allEntries[i];
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
                        ShieldStyleFields entry = allEntries[i];
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
                                ShieldStyleFields entry = allEntries[i];
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
                ShieldStyleFields entry = allEntries[i];
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
                ShieldStyleFields entry = allEntries[i];
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
                ShieldStyleFields entry = group.Entries[i];
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
                ShieldStyleFields entry = group.Entries[i];
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
                ShieldStyleFields entry = allEntries[i];
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
                ShieldStyleFields entry = allEntries[i];
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
                ShieldStyleFields entry = allEntries[i];
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

            // The wording follows the scope: "clear and follow global" is wrong in the
            // type scope, where clearing means the objects fall back to the type's
            // other record and then to the global settings.
            string reset = scope == EditScope.Type
                ? "VisualTweaks_ShieldOverride_TypeReset".Translate()
                : "VisualTweaks_ShieldOverride_ResetAll".Translate();
            int clicked = SettingsLayout.ButtonRow(
                listing,
                new string[] { reset });
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
            List<ShieldStyleFields> entries,
            Func<ShieldStyleFields, bool> overridden,
            Func<ShieldStyleFields, T> value,
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
            ShieldStyleFields first = allEntries[0];
            for (int i = 1; i < allEntries.Count; i++)
            {
                ShieldStyleFields entry = allEntries[i];
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

                ShieldStyleFields head = group.Entries[0];
                for (int i = 1; i < group.Entries.Count; i++)
                {
                    ShieldStyleFields entry = group.Entries[i];
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
            ShieldStyleFields entry,
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
