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
    /// </summary>
    internal readonly struct ShieldStyle
    {
        internal readonly ShieldDomeStyle Surface;
        internal readonly ShieldImpactStyle Impact;
        internal readonly ShieldBreakStyle Break;
        internal readonly ShieldRestoreStyle Restore;
        internal readonly PersonalShieldHitStyle PersonalHit;

        private ShieldStyle(
            ShieldDomeStyle surface,
            ShieldImpactStyle impact,
            ShieldBreakStyle breakStyle,
            ShieldRestoreStyle restore,
            PersonalShieldHitStyle personalHit)
        {
            Surface = surface;
            Impact = impact;
            Break = breakStyle;
            Restore = restore;
            PersonalHit = personalHit;
        }

        /// <summary>True for either spherical lattice surface.</summary>
        internal bool IsHex =>
            Surface == ShieldDomeStyle.Hex
            || Surface == ShieldDomeStyle.HexBubble;

        /// <summary>True when the transparent net is drawn over a bubble surface.</summary>
        internal bool IsHexBubble => Surface == ShieldDomeStyle.HexBubble;

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
            Build(target, null);

        /// <summary>
        /// The choices that apply to one shield. <paramref name="host"/> is the pawn
        /// for personal shields and the building for projector domes, so an override
        /// set through the gizmo follows the object that owns the shield rather than
        /// the comp or apparel that happens to draw it.
        /// </summary>
        internal static ShieldStyle For(ShieldHexTarget target, Thing? host)
        {
            try
            {
                return Build(target, ShieldOverrideGameComponent.Get(host));
            }
            catch (System.Exception ex)
            {
                // A corrupt override must never take shield drawing down with it.
                // Fall back to the global style for this frame and report once.
                Log.ErrorOnce(
                    "[Visual Tweaks] Per-shield style resolution failed; using"
                        + " the global style: " + ex,
                    typeof(ShieldStyle).GetHashCode());
                return Build(target, null);
            }
        }

        private static ShieldStyle Build(
            ShieldHexTarget target,
            ShieldStyleOverride? selection)
        {
            ShieldDomeStyle globalSurface =
                VisualTweaksSettings.ShieldDomeStyleFor(target);

            ShieldDomeStyle surface = selection != null && selection.OverrideSurface
                ? selection.Surface
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
                    selection != null && selection.OverrideImpact
                        ? selection.Impact
                        : (ShieldImpactStyle?)null,
                    surface);

            ShieldBreakStyle breakStyle = ResolveBreak(
                selection != null && selection.OverrideBreak
                    ? selection.Break
                    : (ShieldBreakStyle?)null,
                target,
                surface);

            ShieldRestoreStyle restore = ResolveRestore(
                selection != null && selection.OverrideRestore
                    ? selection.Restore
                    : (ShieldRestoreStyle?)null,
                target);

            PersonalShieldHitStyle personalHit = ResolvePersonalHit(
                selection != null && selection.OverridePersonalHit
                    ? selection.PersonalHit
                    : (PersonalShieldHitStyle?)null);

            return new ShieldStyle(surface, impact, breakStyle, restore, personalHit);
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
}
