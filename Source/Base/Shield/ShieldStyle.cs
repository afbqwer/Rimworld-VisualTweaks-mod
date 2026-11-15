using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// The complete set of discrete appearance choices a shield draws with.
    ///
    /// Before this existed every renderer read the global settings directly, which
    /// made a per-object override impossible without threading a dozen booleans
    /// through the draw code. Resolving the choices once, per shield, in one place
    /// also fixed a subtle inconsistency: the material resolved the surface style
    /// from the settings while the impact and break choices resolved theirs from
    /// yet another helper, so the three could disagree about which surface was on
    /// screen.
    ///
    /// Three layers feed it, highest priority first: the object's own record, its
    /// shield type's record, and the global settings. Each field resolves on its own,
    /// so a type-wide color and a per-pawn surface compose instead of cancelling.
    /// </summary>
    internal readonly struct ShieldStyle
    {
        internal readonly ShieldDomeStyle Surface;
        internal readonly ShieldImpactStyle Impact;
        internal readonly ShieldBreakStyle Break;
        internal readonly ShieldRestoreStyle Restore;
        internal readonly ShieldVanishStyle Vanish;
        internal readonly PersonalShieldHitStyle PersonalHit;

        /// <summary>Per-object RGB tint, or null to use the def's own color.</summary>
        internal readonly Color? Tint;

        /// <summary>
        /// Multiplier on the family's resolved opacity. Defaults to 1 so a shield
        /// without an override keeps the exact global behavior.
        /// </summary>
        internal readonly float OpacityMultiplier;

        internal readonly bool HexLineFollowsColor;
        internal readonly bool HexFacesFollowColor;

        private ShieldStyle(
            ShieldDomeStyle surface,
            ShieldImpactStyle impact,
            ShieldBreakStyle breakStyle,
            ShieldRestoreStyle restore,
            ShieldVanishStyle vanish,
            PersonalShieldHitStyle personalHit,
            Color? tint,
            float opacityMultiplier,
            bool hexLineFollowsColor,
            bool hexFacesFollowColor)
        {
            Surface = surface;
            Impact = impact;
            Break = breakStyle;
            Restore = restore;
            Vanish = vanish;
            PersonalHit = personalHit;
            Tint = tint;
            OpacityMultiplier = opacityMultiplier;
            HexLineFollowsColor = hexLineFollowsColor;
            HexFacesFollowColor = hexFacesFollowColor;
        }

        /// <summary>True for either spherical lattice surface.</summary>
        internal bool IsHex =>
            Surface == ShieldDomeStyle.Hex
            || Surface == ShieldDomeStyle.HexBubble;

        /// <summary>True when the transparent net is drawn over a bubble surface.</summary>
        internal bool IsHexBubble => Surface == ShieldDomeStyle.HexBubble;

        /// <summary>
        /// Per-draw bit flags read by the shader from _Params4.w: bit 0 makes the
        /// lattice lines take the shield color, bit 1 does the same for the lit
        /// panels in the hit / panel-loss animations.
        /// </summary>
        internal float HexColorFlags =>
            (HexLineFollowsColor ? 1f : 0f)
            + (HexFacesFollowColor ? 2f : 0f);

        /// <summary>True for the two profiles that need vanilla's texture margin.</summary>
        internal bool IsStockLike =>
            Surface == ShieldDomeStyle.Vanilla
            || Surface == ShieldDomeStyle.Bubble
            || Surface == ShieldDomeStyle.HexBubble;

        /// <summary>True when this shield can actually sample the baked lattice.</summary>
        internal bool HasLattice(ShieldHexTarget target) =>
            IsHex && ShieldHexMap.Available(target);

        /// <summary>The global choices, with the family surface as the entry point.</summary>
        internal static ShieldStyle Global(ShieldHexTarget target) =>
            Build(target, default);

        /// <summary>
        /// The choices that apply to one shield, resolving the two override layers in
        /// order: the record for this object, then the record for this shield's type,
        /// then the global settings. <paramref name="host"/> is the pawn for personal
        /// shields and the building for projector domes, so an override set through
        /// the gizmo follows the object that owns the shield rather than the comp or
        /// apparel that happens to draw it.
        ///
        /// <paramref name="source"/> is the def that owns the shield being drawn, which
        /// every caller can name for free from the comp it is about to draw. That keeps
        /// the type lookup to a field read instead of walking comps and apparel every
        /// frame, and it makes the answer exact when one host carries two shields of
        /// the same family -- the drawn comp says which one this is.
        /// </summary>
        internal static ShieldStyle For(
            ShieldHexTarget target,
            Thing? host,
            Def source)
        {
            try
            {
                ShieldStyleOverride? local = ShieldOverrideGameComponent.Get(host);

                // Nothing below runs until a type record exists: no type overrides means
                // the whole type layer is a single count check.
                ShieldTypeStyleOverride? typed = null;
                if (VisualTweaksSettings.HasShieldTypeOverrides)
                {
                    typed = VisualTweaksSettings.GetShieldTypeOverride(
                        target,
                        source.defName);
                }

                return Build(target, new ShieldStyleLayers(local, typed));
            }
            catch (System.Exception ex)
            {
                // A corrupt override must never take shield drawing down with it.
                // Fall back to the global style for this frame and report once.
                Log.ErrorOnce(
                    "[Visual Tweaks] Per-shield style resolution failed; using"
                        + " the global style: " + ex,
                    typeof(ShieldStyle).GetHashCode());
                return Build(target, default);
            }
        }

        /// <summary>
        /// The style a pair of records resolves to, without asking the game for them.
        /// The editor uses this to show what a type record actually produces, and to
        /// show an object record on top of the type layer its shield really has.
        /// </summary>
        internal static ShieldStyle ForLayers(
            ShieldHexTarget target,
            ShieldStyleFields? local,
            ShieldStyleFields? typed) =>
            Build(target, new ShieldStyleLayers(local, typed));

        private static ShieldStyle Build(
            ShieldHexTarget target,
            in ShieldStyleLayers layers)
        {
            ShieldDomeStyle globalSurface =
                VisualTweaksSettings.ShieldDomeStyleFor(target);

            ShieldDomeStyle surface = layers.OverrideSurface
                ? layers.Surface
                : globalSurface;
            if (!IsKnownSurface(surface))
            {
                surface = globalSurface;
            }

            // Personal shields do not offer the standalone Bubble profile: their
            // Vanilla choice already is the bubble. Treating a hand-edited value as
            // the family default keeps the shader's mapping honest.
            if (target == ShieldHexTarget.Personal
                && surface == ShieldDomeStyle.Bubble)
            {
                surface = ShieldDomeStyle.Vanilla;
            }

            ShieldImpactStyle impact = target == ShieldHexTarget.Personal
                ? ShieldImpactStyle.Flare
                : ResolveImpact(
                    layers.OverrideImpact
                        ? (ShieldImpactStyle?)layers.Impact
                        : null,
                    surface);

            ShieldBreakStyle breakStyle = ResolveBreak(
                layers.OverrideBreak
                    ? (ShieldBreakStyle?)layers.Break
                    : null,
                target,
                surface);

            ShieldRestoreStyle restore = ResolveRestore(
                layers.OverrideRestore
                    ? (ShieldRestoreStyle?)layers.Restore
                    : null,
                target);

            ShieldVanishStyle vanish = ResolveVanish(
                layers.OverrideVanish
                    ? (ShieldVanishStyle?)layers.Vanish
                    : null,
                target);

            PersonalShieldHitStyle personalHit = ResolvePersonalHit(
                layers.OverridePersonalHit
                    ? (PersonalShieldHitStyle?)layers.PersonalHit
                    : null);

            Color? tint = layers.OverrideColor
                ? layers.ShieldColor
                : (Color?)null;

            float opacityMultiplier = layers.OpacityMultiplier;
            if (float.IsNaN(opacityMultiplier))
            {
                opacityMultiplier = 1f;
            }

            opacityMultiplier = Mathf.Clamp(
                opacityMultiplier,
                VisualTweaksSettings.MinShieldOpacityMultiplier,
                VisualTweaksSettings.MaxShieldOpacityMultiplier);

            bool lineFollows = layers.OverrideHexLineColor
                ? layers.HexLineFollowsShield
                : VisualTweaksSettings.hexLineFollowsShieldColor;
            bool facesFollow = layers.OverrideHexFaceColor
                ? layers.HexFacesFollowShield
                : VisualTweaksSettings.hexImpactFacesFollowShieldColor;

            return new ShieldStyle(
                surface,
                impact,
                breakStyle,
                restore,
                vanish,
                personalHit,
                tint,
                opacityMultiplier,
                lineFollows,
                facesFollow);
        }

        /// <summary>
        /// Impact animation for a large shield. Each surface family keeps its own
        /// choice in the settings, and the valid set is smaller than the enum: the
        /// lattice wave needs the hexagonal surface.
        /// </summary>
        internal static ShieldImpactStyle ResolveImpact(
            ShieldImpactStyle? wanted,
            ShieldDomeStyle surface)
        {
            bool hex = IsHexSurface(surface);
            ShieldImpactStyle fallback;
            if (hex)
            {
                fallback = SanitizeLargeImpact(
                    VisualTweaksSettings.hexShieldImpactStyle,
                    ShieldImpactStyle.HexWave,
                    hex: true);
            }
            else if (IsStockLikeSurface(surface, ShieldHexTarget.Large))
            {
                fallback = SanitizeLargeImpact(
                    VisualTweaksSettings.vanillaShieldImpactStyle,
                    ShieldImpactStyle.Brighten,
                    hex: false);
            }
            else
            {
                fallback = SanitizeLargeImpact(
                    VisualTweaksSettings.crispShieldImpactStyle,
                    ShieldImpactStyle.Flare,
                    hex: false);
            }

            return wanted.HasValue
                ? SanitizeLargeImpact(wanted.Value, fallback, hex)
                : fallback;
        }

        /// <summary>
        /// Break animation for one family and surface. The panel-loss animation is
        /// only valid on a lattice surface; everything else can either vanish like
        /// vanilla or tear apart.
        /// </summary>
        internal static ShieldBreakStyle ResolveBreak(
            ShieldBreakStyle? wanted,
            ShieldHexTarget target,
            ShieldDomeStyle surface)
        {
            bool personal = target == ShieldHexTarget.Personal;
            bool hex = IsHexSurface(surface);
            ShieldBreakStyle fallback;
            if (hex)
            {
                fallback = personal
                    ? VisualTweaksSettings.personalHexShieldBreakStyle
                    : VisualTweaksSettings.largeHexShieldBreakStyle;
            }
            else if (IsStockLikeSurface(surface, target))
            {
                fallback = personal
                    ? VisualTweaksSettings.personalVanillaShieldBreakStyle
                    : VisualTweaksSettings.largeVanillaShieldBreakStyle;
            }
            else
            {
                fallback = personal
                    ? VisualTweaksSettings.personalCrispShieldBreakStyle
                    : VisualTweaksSettings.largeCrispShieldBreakStyle;
            }

            fallback = SanitizeBreak(fallback, hex);
            return wanted.HasValue
                ? SanitizeBreak(wanted.Value, hex)
                : fallback;
        }

        internal static ShieldRestoreStyle ResolveRestore(
            ShieldRestoreStyle? wanted,
            ShieldHexTarget target)
        {
            ShieldRestoreStyle fallback = target == ShieldHexTarget.Personal
                ? VisualTweaksSettings.personalShieldRestoreStyle
                : VisualTweaksSettings.largeShieldRestoreStyle;
            fallback = SanitizeRestore(fallback);
            return wanted.HasValue
                ? SanitizeRestore(wanted.Value)
                : fallback;
        }

        /// <summary>
        /// The animation for a shield that goes away without breaking. One value per
        /// family, like the restore choice it mirrors: leaving is the same event on
        /// every surface, so there is nothing surface-specific to resolve.
        /// </summary>
        internal static ShieldVanishStyle ResolveVanish(
            ShieldVanishStyle? wanted,
            ShieldHexTarget target)
        {
            ShieldVanishStyle fallback = target == ShieldHexTarget.Personal
                ? VisualTweaksSettings.personalShieldVanishStyle
                : VisualTweaksSettings.largeShieldVanishStyle;
            fallback = SanitizeVanish(fallback);
            return wanted.HasValue
                ? SanitizeVanish(wanted.Value)
                : fallback;
        }

        internal static PersonalShieldHitStyle ResolvePersonalHit(
            PersonalShieldHitStyle? wanted)
        {
            PersonalShieldHitStyle fallback = SanitizePersonalHit(
                VisualTweaksSettings.personalShieldHitStyle);
            return wanted.HasValue
                ? SanitizePersonalHit(wanted.Value)
                : fallback;
        }

        /// <summary>
        /// Explicit range check instead of Enum.IsDefined. This runs once per
        /// shield per frame, and Enum.IsDefined boxes the value and goes through
        /// reflection; the five legal values are fixed by the enum above.
        /// </summary>
        private static bool IsKnownSurface(ShieldDomeStyle surface) =>
            surface == ShieldDomeStyle.Crisp
            || surface == ShieldDomeStyle.Hex
            || surface == ShieldDomeStyle.Vanilla
            || surface == ShieldDomeStyle.Bubble
            || surface == ShieldDomeStyle.HexBubble;

        internal static bool IsHexSurface(ShieldDomeStyle surface) =>
            surface == ShieldDomeStyle.Hex
            || surface == ShieldDomeStyle.HexBubble;

        internal static bool IsStockLikeSurface(
            ShieldDomeStyle surface,
            ShieldHexTarget target) =>
            surface == ShieldDomeStyle.Vanilla
            || surface == ShieldDomeStyle.HexBubble
            || (target == ShieldHexTarget.Large && surface == ShieldDomeStyle.Bubble);

        private static ShieldImpactStyle SanitizeLargeImpact(
            ShieldImpactStyle value,
            ShieldImpactStyle fallback,
            bool hex)
        {
            // HexWave needs the baked lattice; Flare and Brighten are analytic and
            // exist only on the non-lattice surfaces. A stale override therefore
            // falls back to that surface's own default instead of asking the shader
            // for an effect it cannot draw.
            if (hex)
            {
                return value == ShieldImpactStyle.Brighten
                    || value == ShieldImpactStyle.HexWave
                    ? value
                    : fallback;
            }

            return value == ShieldImpactStyle.Flare
                || value == ShieldImpactStyle.Brighten
                ? value
                : fallback;
        }

        private static ShieldBreakStyle SanitizeBreak(
            ShieldBreakStyle value,
            bool allowHexDissipate)
        {
            if (value == ShieldBreakStyle.Vanilla || value == ShieldBreakStyle.Dissolve)
            {
                return value;
            }

            if (value == ShieldBreakStyle.HexDissipate && allowHexDissipate)
            {
                return value;
            }

            return ShieldBreakStyle.Dissolve;
        }

        private static ShieldRestoreStyle SanitizeRestore(ShieldRestoreStyle value)
        {
            if (value == ShieldRestoreStyle.None
                || value == ShieldRestoreStyle.Fade
                || value == ShieldRestoreStyle.Grow)
            {
                return value;
            }

            return ShieldRestoreStyle.Grow;
        }

        private static ShieldVanishStyle SanitizeVanish(ShieldVanishStyle value)
        {
            if (value == ShieldVanishStyle.None
                || value == ShieldVanishStyle.Fade
                || value == ShieldVanishStyle.Shrink)
            {
                return value;
            }

            return ShieldVanishStyle.Fade;
        }

        private static PersonalShieldHitStyle SanitizePersonalHit(
            PersonalShieldHitStyle value)
        {
            if (value == PersonalShieldHitStyle.Jitter
                || value == PersonalShieldHitStyle.Brighten
                || value == PersonalShieldHitStyle.HexWave)
            {
                return value;
            }

            return PersonalShieldHitStyle.Jitter;
        }
    }

    /// <summary>
    /// The two override records that apply to one shield, highest priority first:
    /// the object's own record and its type's record. Absent layers are null, and a
    /// field unset in both falls through to the global settings.
    ///
    /// Resolution is per field, not per record: a type record that only sets the
    /// color still tints every shield of that type even though individual pawns may
    /// override their surface. That is what makes the layers composable -- adding a
    /// per-pawn override cannot silently drop the type's color.
    ///
    /// A struct over two references so the whole resolution allocates nothing; this
    /// runs once per shield per frame.
    /// </summary>
    internal readonly struct ShieldStyleLayers
    {
        private readonly ShieldStyleFields? primary;
        private readonly ShieldStyleFields? secondary;

        internal ShieldStyleLayers(
            ShieldStyleFields? primary,
            ShieldStyleFields? secondary)
        {
            this.primary = primary;
            this.secondary = secondary;
        }

        internal bool OverrideSurface =>
            (primary != null && primary.OverrideSurface)
            || (secondary != null && secondary.OverrideSurface);

        internal ShieldDomeStyle Surface =>
            primary != null && primary.OverrideSurface
                ? primary.Surface
                : secondary != null && secondary.OverrideSurface
                    ? secondary.Surface
                    : ShieldDomeStyle.Vanilla;

        internal bool OverrideImpact =>
            (primary != null && primary.OverrideImpact)
            || (secondary != null && secondary.OverrideImpact);

        internal ShieldImpactStyle Impact =>
            primary != null && primary.OverrideImpact
                ? primary.Impact
                : secondary != null && secondary.OverrideImpact
                    ? secondary.Impact
                    : ShieldImpactStyle.Flare;

        internal bool OverrideBreak =>
            (primary != null && primary.OverrideBreak)
            || (secondary != null && secondary.OverrideBreak);

        internal ShieldBreakStyle Break =>
            primary != null && primary.OverrideBreak
                ? primary.Break
                : secondary != null && secondary.OverrideBreak
                    ? secondary.Break
                    : ShieldBreakStyle.Dissolve;

        internal bool OverrideRestore =>
            (primary != null && primary.OverrideRestore)
            || (secondary != null && secondary.OverrideRestore);

        internal ShieldRestoreStyle Restore =>
            primary != null && primary.OverrideRestore
                ? primary.Restore
                : secondary != null && secondary.OverrideRestore
                    ? secondary.Restore
                    : ShieldRestoreStyle.Grow;

        internal bool OverrideVanish =>
            (primary != null && primary.OverrideVanish)
            || (secondary != null && secondary.OverrideVanish);

        internal ShieldVanishStyle Vanish =>
            primary != null && primary.OverrideVanish
                ? primary.Vanish
                : secondary != null && secondary.OverrideVanish
                    ? secondary.Vanish
                    : ShieldVanishStyle.Fade;

        internal bool OverridePersonalHit =>
            (primary != null && primary.OverridePersonalHit)
            || (secondary != null && secondary.OverridePersonalHit);

        internal PersonalShieldHitStyle PersonalHit =>
            primary != null && primary.OverridePersonalHit
                ? primary.PersonalHit
                : secondary != null && secondary.OverridePersonalHit
                    ? secondary.PersonalHit
                    : PersonalShieldHitStyle.Jitter;

        internal bool OverrideColor =>
            (primary != null && primary.OverrideColor)
            || (secondary != null && secondary.OverrideColor);

        internal Color ShieldColor =>
            primary != null && primary.OverrideColor
                ? primary.ShieldColor
                : secondary != null && secondary.OverrideColor
                    ? secondary.ShieldColor
                    : Color.white;

        /// <summary>
        /// A multiplier of exactly 1 counts as "not set" rather than as a value, so a
        /// type-wide multiplier still applies to a pawn whose own record never touched
        /// the slider. A real multiplier on the object wins outright instead of
        /// multiplying with the type's: stacking them would turn two mild choices into
        /// an almost invisible shield.
        /// </summary>
        internal float OpacityMultiplier
        {
            get
            {
                if (primary != null
                    && Mathf.Abs(primary.OpacityMultiplier - 1f) > 0.0001f)
                {
                    return primary.OpacityMultiplier;
                }

                return secondary != null ? secondary.OpacityMultiplier : 1f;
            }
        }

        internal bool OverrideHexLineColor =>
            (primary != null && primary.OverrideHexLineColor)
            || (secondary != null && secondary.OverrideHexLineColor);

        internal bool HexLineFollowsShield =>
            primary != null && primary.OverrideHexLineColor
                ? primary.HexLineFollowsShield
                : secondary != null && secondary.OverrideHexLineColor
                    ? secondary.HexLineFollowsShield
                    : true;

        internal bool OverrideHexFaceColor =>
            (primary != null && primary.OverrideHexFaceColor)
            || (secondary != null && secondary.OverrideHexFaceColor);

        internal bool HexFacesFollowShield =>
            primary != null && primary.OverrideHexFaceColor
                ? primary.HexFacesFollowShield
                : secondary != null && secondary.OverrideHexFaceColor
                    ? secondary.HexFacesFollowShield
                    : true;
    }
}
