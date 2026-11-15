using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// One layer of the shield style stack: the fields a single record can override.
    ///
    /// Two kinds of record exist and both are this shape, because the style window
    /// draws the same rows for both and <see cref="ShieldStyle"/> resolves a field
    /// the same way whoever set it: a per-object record (a pawn or a building) and
    /// a per-type record (every shield drawn by one def).
    ///
    /// Every field is optional. An unset field falls through to the layer below --
    /// object to type, type to the global settings -- so moving a global knob still
    /// moves every shield nobody explicitly overrode.
    /// </summary>
    internal abstract class ShieldStyleFields : IExposable
    {
        public bool OverrideSurface;
        public ShieldDomeStyle Surface = ShieldDomeStyle.Vanilla;

        public bool OverrideImpact;
        public ShieldImpactStyle Impact = ShieldImpactStyle.Flare;

        public bool OverrideBreak;
        public ShieldBreakStyle Break = ShieldBreakStyle.Dissolve;

        public bool OverrideRestore;
        public ShieldRestoreStyle Restore = ShieldRestoreStyle.Grow;

        public bool OverrideVanish;
        public ShieldVanishStyle Vanish = ShieldVanishStyle.Fade;

        public bool OverridePersonalHit;
        public PersonalShieldHitStyle PersonalHit = PersonalShieldHitStyle.Jitter;

        public bool OverrideColor;
        public Color ShieldColor = Color.white;

        /// <summary>
        /// Multiplier on the family's resolved opacity. 1 means no override.
        /// Unlike a replacement value this keeps the lower layer meaningful:
        /// moving that slider still moves this shield.
        /// </summary>
        public float OpacityMultiplier = 1f;

        public bool OverrideHexLineColor;
        public bool HexLineFollowsShield = true;

        public bool OverrideHexFaceColor;
        public bool HexFacesFollowShield = true;

        internal bool IsEmpty =>
            !OverrideSurface
            && !OverrideImpact
            && !OverrideBreak
            && !OverrideRestore
            && !OverrideVanish
            && !OverridePersonalHit
            && !OverrideColor
            && !OverrideHexLineColor
            && !OverrideHexFaceColor
            && Mathf.Abs(OpacityMultiplier - 1f) <= 0.0001f;

        public virtual void ExposeData()
        {
            Scribe_Values.Look(ref OverrideSurface, "overrideSurface", false);
            Scribe_Values.Look(ref Surface, "surface", ShieldDomeStyle.Vanilla);
            Scribe_Values.Look(ref OverrideImpact, "overrideImpact", false);
            Scribe_Values.Look(ref Impact, "impact", ShieldImpactStyle.Flare);
            Scribe_Values.Look(ref OverrideBreak, "overrideBreak", false);
            Scribe_Values.Look(ref Break, "break", ShieldBreakStyle.Dissolve);
            Scribe_Values.Look(ref OverrideRestore, "overrideRestore", false);
            Scribe_Values.Look(ref Restore, "restore", ShieldRestoreStyle.Grow);
            Scribe_Values.Look(ref OverrideVanish, "overrideVanish", false);
            Scribe_Values.Look(ref Vanish, "vanish", ShieldVanishStyle.Fade);
            Scribe_Values.Look(ref OverridePersonalHit, "overridePersonalHit", false);
            Scribe_Values.Look(
                ref PersonalHit,
                "personalHit",
                PersonalShieldHitStyle.Jitter);
            Scribe_Values.Look(ref OverrideColor, "overrideColor", false);
            ASQPicker.ColorPickerWindow.LookColor(
                ref ShieldColor,
                "shieldColor",
                Color.white);
            Scribe_Values.Look(
                ref OpacityMultiplier,
                "opacityMultiplier",
                1f);
            Scribe_Values.Look(
                ref OverrideHexLineColor,
                "overrideHexLineColor",
                false);
            Scribe_Values.Look(
                ref HexLineFollowsShield,
                "hexLineFollowsShield",
                true);
            Scribe_Values.Look(
                ref OverrideHexFaceColor,
                "overrideHexFaceColor",
                false);
            Scribe_Values.Look(
                ref HexFacesFollowShield,
                "hexFacesFollowShield",
                true);
            if (float.IsNaN(OpacityMultiplier))
            {
                OpacityMultiplier = 1f;
            }

            OpacityMultiplier = Mathf.Clamp(
                OpacityMultiplier,
                VisualTweaksSettings.MinShieldOpacityMultiplier,
                VisualTweaksSettings.MaxShieldOpacityMultiplier);
        }

        internal void Clear()
        {
            OverrideSurface = false;
            OverrideImpact = false;
            OverrideBreak = false;
            OverrideRestore = false;
            OverrideVanish = false;
            OverridePersonalHit = false;
            OverrideColor = false;
            OpacityMultiplier = 1f;
            OverrideHexLineColor = false;
            HexLineFollowsShield = true;
            OverrideHexFaceColor = false;
            HexFacesFollowShield = true;
        }
    }

    /// <summary>
    /// A per-shield appearance choice, set from the gizmo on the pawn or building
    /// that owns the shield. This is the highest layer: it wins over the shield's
    /// type record, which in turn wins over the global settings.
    ///
    /// Stored by <see cref="Thing.thingIDNumber"/> rather than by comp reference so
    /// it survives a save/load. For a personal shield the number belongs to the
    /// pawn, which deliberately covers every belt, bubble and hediff shield worn by
    /// that pawn with one entry.
    /// </summary>
    internal sealed class ShieldStyleOverride : ShieldStyleFields
    {
        public int ThingId;

        /// <summary>
        /// The family this record was opened for. Only used to choose which baked
        /// lattice to prepare at load; the actual style resolution follows the
        /// drawing family.
        /// </summary>
        public ShieldHexTarget Target = ShieldHexTarget.Large;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref ThingId, "thingId", 0);
            Scribe_Values.Look(
                ref Target,
                "target",
                ShieldHexTarget.Large);
        }
    }

    /// <summary>
    /// A per-type appearance choice: every shield drawn by one def shares it.
    ///
    /// The key is the def that owns the shield, not the thing wearing it: a shield
    /// belt is <c>Apparel_ShieldBelt</c> whichever colonist wears it, a mech's built
    /// in shield is the mech's race def, and a projector dome is the building def.
    /// That is what the player means by "this kind of shield", and it keeps one
    /// record useful across a whole colony instead of one per pawn.
    ///
    /// Owned by <see cref="VisualTweaksSettings"/>, not by the save: a type style is a
    /// preference that should hold in every colony. The family is part of the key, so
    /// the same def appearing in both stacks cannot make two records collide.
    /// </summary>
    internal sealed class ShieldTypeStyleOverride : ShieldStyleFields
    {
        public string DefName = string.Empty;

        public ShieldHexTarget Target = ShieldHexTarget.Large;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref DefName, "defName", string.Empty);
            Scribe_Values.Look(
                ref Target,
                "target",
                ShieldHexTarget.Large);
        }
    }

    /// <summary>
    /// Owns the per-object style overrides of one save, and moves the per-type records
    /// of older saves into the mod settings while that save is being deserialized.
    ///
    /// A GameComponent is the smallest vanilla container that is created automatically,
    /// survives a save, and needs no def surgery on the shield comps themselves. It is
    /// the right home for a record keyed by <see cref="Thing.thingIDNumber"/>, which
    /// only means something inside the save that issued it. Type styles are not that:
    /// they are keyed by a def, they are a preference, and they live in
    /// <see cref="VisualTweaksSettings"/> so every colony shares them.
    ///
    /// No type data is kept here. A published build scribed it in this component; the
    /// node is read once during deserialization, handed to the settings, and never
    /// written back, so saves made by this build contain only the object layer.
    ///
    /// Threading contract: every reader is a main-thread draw callback
    /// (Thing.PostDraw, MapComponentDraw or the style window) and every writer is
    /// the same thread (window edits, Thing.Destroy cleanup, save/load). The
    /// lookup and its cache are therefore intentionally lock-free. Do not call
    /// these from a background worker without adding synchronization.
    /// </summary>
    internal sealed class ShieldOverrideGameComponent : GameComponent
    {
        private List<ShieldStyleOverride> entries = new List<ShieldStyleOverride>();
        private readonly Dictionary<int, ShieldStyleOverride> lookup =
            new Dictionary<int, ShieldStyleOverride>();

        /// <summary>
        /// Set while deserializing a save that still carried type styles. The records
        /// themselves are handed to the mod settings right there; this only remembers
        /// that the settings file still has to be written, which cannot be done inside
        /// ExposeData without clobbering the save's scribe state.
        /// </summary>
        private static bool importedTypeStyles;

        // GameComponent subclasses are instantiated reflectively by Verse.Game with
        // the Game as the single constructor argument.
        public ShieldOverrideGameComponent(Game game)
        {
        }

        // Resolved on every shield draw. The game object rarely changes, so the
        // component lookup is cached until a different Game appears. While the
        // component is still absent (early game construction), the lookup is
        // retried instead of caching a null forever.
        private static Game? cachedGame;
        private static ShieldOverrideGameComponent? cachedComponent;

        private static ShieldOverrideGameComponent? GetCurrent()
        {
            Game? game = Current.Game;
            if (game == null)
            {
                cachedGame = null;
                cachedComponent = null;
                return null;
            }

            if (!ReferenceEquals(game, cachedGame) || cachedComponent == null)
            {
                cachedGame = game;
                cachedComponent = game.GetComponent<ShieldOverrideGameComponent>();
            }

            return cachedComponent;
        }

        internal static ShieldStyleOverride? Get(Thing? thing)
        {
            if (thing == null || thing.thingIDNumber == 0)
            {
                return null;
            }

            ShieldOverrideGameComponent? component = GetCurrent();
            ShieldStyleOverride? entry = component?.Find(thing.thingIDNumber);
            return entry != null && !entry.IsEmpty ? entry : null;
        }

        internal static ShieldStyleOverride GetOrCreate(
            Thing thing,
            ShieldHexTarget target)
        {
            ShieldOverrideGameComponent? component = GetCurrent();
            if (component != null)
            {
                return component.GetOrCreate(thing.thingIDNumber, target);
            }

            // Only reachable outside a running game, or if the component failed to
            // instantiate. A detached record keeps the window usable; the missing
            // persistence is reported once.
            if (Current.Game != null)
            {
                Log.ErrorOnce(
                    "[Visual Tweaks] Per-shield override component is missing;"
                        + " style changes will not be saved.",
                    typeof(ShieldOverrideGameComponent).GetHashCode());
            }

            return new ShieldStyleOverride
            {
                ThingId = thing.thingIDNumber,
                Target = target,
            };
        }

        internal static void Remove(Thing? thing)
        {
            if (thing == null || thing.thingIDNumber == 0)
            {
                return;
            }

            GetCurrent()?.Remove(thing.thingIDNumber);
        }

        /// <summary>
        /// Drops this object's record if nothing is set in it. Opening the style window
        /// creates a record per host, and a record the player closed without touching
        /// is pure save bloat (it is already ignored by <see cref="Get"/>).
        /// </summary>
        internal static void Prune(Thing? thing)
        {
            if (thing == null || thing.thingIDNumber == 0)
            {
                return;
            }

            ShieldOverrideGameComponent? component = GetCurrent();
            ShieldStyleOverride? entry = component?.Find(thing.thingIDNumber);
            if (entry == null || !entry.IsEmpty)
            {
                return;
            }

            component!.lookup.Remove(thing.thingIDNumber);
            component.entries?.Remove(entry);
        }

        /// <summary>
        /// Number of objects with at least one active override. Empty records that
        /// were only created by opening the window are not counted.
        /// </summary>
        internal static int CountActive()
        {
            ShieldOverrideGameComponent? component = GetCurrent();
            if (component?.entries == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < component.entries.Count; i++)
            {
                ShieldStyleOverride? entry = component.entries[i];
                if (entry != null && !entry.IsEmpty)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Drops every per-object override, including empty records. Type styles are
        /// mod settings and are cleared from the settings page instead.
        /// </summary>
        internal static void ClearAll()
        {
            ShieldOverrideGameComponent? component = GetCurrent();
            if (component == null)
            {
                return;
            }

            component.entries?.Clear();
            component.lookup.Clear();
        }

        /// <summary>
        /// Bakes any lattice a saved override will draw, then flushes the settings file
        /// if deserializing moved type styles into it. Loading is the last point before
        /// play where that cost can land on a loading screen instead of the first frame
        /// that happens to show the shield.
        /// </summary>
        public override void LoadedGame()
        {
            base.LoadedGame();
            FlushImportedTypeStyles();
            EnsureOverrideLattices();
        }

        /// <summary>
        /// Writes the settings file after a migration. Kept out of ExposeData on purpose:
        /// writing it there would switch Scribe's mode and saver out from under the save
        /// that is being read.
        ///
        /// The records are already live in memory by this point, so a failed write only
        /// means they reach disk later; the flag stays set so the next load tries again.
        /// </summary>
        private static void FlushImportedTypeStyles()
        {
            if (!importedTypeStyles)
            {
                return;
            }

            if (VisualTweaksSettings.PersistShieldTypeOverrides())
            {
                importedTypeStyles = false;
                return;
            }

            Log.Warning(
                "[Visual Tweaks] Could not write the settings file after moving shield"
                    + " type styles out of a save; retrying on the next load.");
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref entries, "shieldStyleOverrides", LookMode.Deep);

            // A save written by a build that kept type styles here still carries them.
            // Read that node into a temporary and hand the records to the mod settings
            // before it goes out of scope, so this component holds no type data at all
            // and the next save writes no such node.
            //
            // Types the settings already style are left alone: the settings are the
            // single source of truth, and opening an old colony must not silently
            // rewrite a preference set since upgrading.
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                ImportLegacyTypeStyles();
            }

            RebuildLookup();
        }

        private static void ImportLegacyTypeStyles()
        {
            try
            {
                List<ShieldTypeStyleOverride>? legacy = null;
                Scribe_Collections.Look(
                    ref legacy,
                    "shieldTypeStyleOverrides",
                    LookMode.Deep);

                if (legacy == null || legacy.Count == 0)
                {
                    return;
                }

                int imported = VisualTweaksSettings.ImportShieldTypeOverrides(legacy);
                if (imported > 0)
                {
                    importedTypeStyles = true;
                    VisualTweaksLog.Info(
                        "Moved " + imported + " shield type style(s) out of the save and"
                            + " into the mod settings.");
                }
                else
                {
                    VisualTweaksLog.Info(
                        "Dropped " + legacy.Count + " shield type style record(s) found"
                            + " in a save; the mod settings already style them.");
                }
            }
            catch (Exception ex)
            {
                // A corrupt node must not stop a colony from loading. The settings keep
                // whatever they already had, and the save is clean on the next write.
                Log.ErrorOnce(
                    "[Visual Tweaks] Could not move shield type styles out of a save: "
                        + ex,
                    typeof(ShieldOverrideGameComponent).GetHashCode() ^ 0x5B);
            }
        }

        private ShieldStyleOverride? Find(int thingId)
        {
            // The common case is "no override for anything": skip both the rebuild
            // and the hash lookup entirely. This runs once per visible shield per
            // frame, so the empty path is worth keeping allocation-free and cheap.
            if (lookup.Count == 0)
            {
                if (entries != null && entries.Count > 0)
                {
                    RebuildLookup();
                }

                if (lookup.Count == 0)
                {
                    return null;
                }
            }

            return lookup.TryGetValue(thingId, out ShieldStyleOverride? entry)
                ? entry
                : null;
        }

        private ShieldStyleOverride GetOrCreate(
            int thingId,
            ShieldHexTarget target)
        {
            ShieldStyleOverride? existing = Find(thingId);
            if (existing != null)
            {
                // A pawn that somehow owns both families keeps the family its
                // record was first opened for; the style itself still resolves per
                // drawing family, so this only affects which atlas is prewarmed.
                return existing;
            }

            entries ??= new List<ShieldStyleOverride>();
            ShieldStyleOverride entry = new ShieldStyleOverride
            {
                ThingId = thingId,
                Target = target,
            };
            entries.Add(entry);
            lookup[thingId] = entry;
            return entry;
        }

        private void Remove(int thingId)
        {
            if (lookup.Count == 0)
            {
                RebuildLookup();
            }

            if (lookup.TryGetValue(thingId, out ShieldStyleOverride? entry))
            {
                lookup.Remove(thingId);
                entries.Remove(entry);
            }
        }

        private void RebuildLookup()
        {
            lookup.Clear();
            entries ??= new List<ShieldStyleOverride>();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                ShieldStyleOverride? entry = entries[i];
                if (entry == null || entry.ThingId == 0)
                {
                    entries.RemoveAt(i);
                    continue;
                }

                lookup[entry.ThingId] = entry;
            }
        }

        private void EnsureOverrideLattices()
        {
            bool large = false;
            bool personal = false;

            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    ShieldStyleOverride? entry = entries[i];
                    if (entry != null)
                    {
                        NeedsLattice(entry, entry.Target, ref large, ref personal);
                    }
                }
            }

            // Type records are settings now, and they apply to every colony, so their
            // lattice is prepared here too rather than waiting for the first draw.
            IReadOnlyList<ShieldTypeStyleOverride> types =
                VisualTweaksSettings.ShieldTypeOverrideRecords;
            for (int i = 0; i < types.Count; i++)
            {
                ShieldTypeStyleOverride? entry = types[i];
                if (entry != null)
                {
                    NeedsLattice(entry, entry.Target, ref large, ref personal);
                }
            }

            if (large)
            {
                ShieldHexMap.EnsureBaked(ShieldHexTarget.Large);
            }

            if (personal)
            {
                ShieldHexMap.EnsureBaked(ShieldHexTarget.Personal);
            }
        }

        private static void NeedsLattice(
            ShieldStyleFields? entry,
            ShieldHexTarget target,
            ref bool large,
            ref bool personal)
        {
            if (entry == null)
            {
                return;
            }

            if (entry.OverrideSurface
                && (entry.Surface == ShieldDomeStyle.Hex
                    || entry.Surface == ShieldDomeStyle.HexBubble))
            {
                if (target == ShieldHexTarget.Personal)
                {
                    personal = true;
                }
                else
                {
                    large = true;
                }
            }

            if (target == ShieldHexTarget.Personal
                && entry.OverridePersonalHit
                && entry.PersonalHit == PersonalShieldHitStyle.HexWave)
            {
                personal = true;
            }
        }
    }
}
