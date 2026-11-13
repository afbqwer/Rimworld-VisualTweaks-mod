using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// A per-shield appearance choice, set from the gizmo on the pawn or building
    /// that owns the shield. Every field is optional: an unset field follows the
    /// global setting, so turning a global style knob still moves every shield that
    /// was never explicitly overridden.
    ///
    /// Stored by <see cref="Thing.thingIDNumber"/> rather than by comp reference so
    /// it survives a save/load. For a personal shield the number belongs to the
    /// pawn, which deliberately covers every belt, bubble and hediff shield worn by
    /// that pawn with one entry.
    /// </summary>
    internal sealed class ShieldStyleOverride : IExposable
    {
        public int ThingId;

        /// <summary>
        /// The family this record was opened for. Only used to choose which baked
        /// lattice to prepare at load; the actual style resolution follows the
        /// drawing family.
        /// </summary>
        public ShieldHexTarget Target = ShieldHexTarget.Large;

        public bool OverrideSurface;
        public ShieldDomeStyle Surface = ShieldDomeStyle.Vanilla;

        public bool OverrideImpact;
        public ShieldImpactStyle Impact = ShieldImpactStyle.Flare;

        public bool OverrideBreak;
        public ShieldBreakStyle Break = ShieldBreakStyle.Dissolve;

        public bool OverrideRestore;
        public ShieldRestoreStyle Restore = ShieldRestoreStyle.Grow;

        public bool OverridePersonalHit;
        public PersonalShieldHitStyle PersonalHit = PersonalShieldHitStyle.Jitter;

        public bool OverrideColor;
        public Color ShieldColor = Color.white;

        /// <summary>
        /// Multiplier on the family's resolved opacity. 1 means no override.
        /// Unlike a replacement value this keeps the global opacity setting
        /// meaningful: moving that slider still moves this shield.
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
            && !OverridePersonalHit
            && !OverrideColor
            && !OverrideHexLineColor
            && !OverrideHexFaceColor
            && Mathf.Abs(OpacityMultiplier - 1f) <= 0.0001f;

        public void ExposeData()
        {
            Scribe_Values.Look(ref ThingId, "thingId", 0);
            Scribe_Values.Look(
                ref Target,
                "target",
                ShieldHexTarget.Large);
            Scribe_Values.Look(ref OverrideSurface, "overrideSurface", false);
            Scribe_Values.Look(ref Surface, "surface", ShieldDomeStyle.Vanilla);
            Scribe_Values.Look(ref OverrideImpact, "overrideImpact", false);
            Scribe_Values.Look(ref Impact, "impact", ShieldImpactStyle.Flare);
            Scribe_Values.Look(ref OverrideBreak, "overrideBreak", false);
            Scribe_Values.Look(ref Break, "break", ShieldBreakStyle.Dissolve);
            Scribe_Values.Look(ref OverrideRestore, "overrideRestore", false);
            Scribe_Values.Look(ref Restore, "restore", ShieldRestoreStyle.Grow);
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
    /// Owns every per-shield style override in one save. A GameComponent is the
    /// smallest vanilla container that is created automatically, survives a save,
    /// and needs no def surgery on the shield comps themselves.
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

        /// <summary>Drops every per-object override, including empty records.</summary>
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
        /// Bakes any lattice a saved override will draw. Loading is the last point
        /// before play where the cost can land on a loading screen instead of the
        /// first frame that happens to show the shield.
        /// </summary>
        public override void LoadedGame()
        {
            base.LoadedGame();
            EnsureOverrideLattices();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref entries, "shieldStyleOverrides", LookMode.Deep);
            RebuildLookup();
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
            if (entries == null)
            {
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                ShieldStyleOverride? entry = entries[i];
                if (entry == null)
                {
                    continue;
                }

                if (entry.OverrideSurface
                    && (entry.Surface == ShieldDomeStyle.Hex
                        || entry.Surface == ShieldDomeStyle.HexBubble))
                {
                    if (entry.Target == ShieldHexTarget.Personal)
                    {
                        personal = true;
                    }
                    else
                    {
                        large = true;
                    }
                }

                if (entry.Target == ShieldHexTarget.Personal
                    && entry.OverridePersonalHit
                    && entry.PersonalHit == PersonalShieldHitStyle.HexWave)
                {
                    personal = true;
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
    }
}
