using System.Collections.Generic;
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

        internal bool IsEmpty =>
            !OverrideSurface
            && !OverrideImpact
            && !OverrideBreak
            && !OverrideRestore
            && !OverridePersonalHit;

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
        }

        internal void Clear()
        {
            OverrideSurface = false;
            OverrideImpact = false;
            OverrideBreak = false;
            OverrideRestore = false;
            OverridePersonalHit = false;
        }
    }

    /// <summary>
    /// Owns every per-shield style override in one save. A GameComponent is the
    /// smallest vanilla container that is created automatically, survives a save,
    /// and needs no def surgery on the shield comps themselves.
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

        private static ShieldOverrideGameComponent? GetCurrent() =>
            Current.Game?.GetComponent<ShieldOverrideGameComponent>();

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

            // Only reachable outside a running game; the window cannot be opened
            // there, so a detached record is a safe fallback rather than a crash.
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
            if (lookup.Count == 0)
            {
                RebuildLookup();
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
            for (int i = 0; i < entries.Count; i++)
            {
                ShieldStyleOverride entry = entries[i];
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
