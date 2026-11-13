using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>Surface drawn for a replacement shield.</summary>
    public enum ShieldDomeStyle
    {
        Crisp = 0,
        Hex = 1,
        Vanilla = 2,
        Bubble = 3,

        /// <summary>
        /// The ShieldBubble surface with a transparent spherical hex lattice over
        /// it. The lattice is revealed by a feathered horizontal scan band instead
        /// of pulsing, and goes fully opaque for the whole hit / break animation.
        /// Deliberately 4 (past Bubble) rather than squeezed between existing
        /// values: saved settings store the enum by value, so inserting in the
        /// middle would silently change every existing player's chosen style.
        /// </summary>
        HexBubble = 4
    }

    /// <summary>How a shield answers an intercepted hit.</summary>
    public enum ShieldImpactStyle
    {
        Flare = 0,
        Brighten = 1,
        HexWave = 2
    }

    /// <summary>How a shield animates when it goes down.</summary>
    public enum ShieldBreakStyle
    {
        Vanilla = 0,
        Dissolve = 1,
        HexDissipate = 2
    }

    /// <summary>How a shield animates when it comes back up.</summary>
    public enum ShieldRestoreStyle
    {
        None = 0,
        Fade = 1,
        Grow = 2
    }

    /// <summary>How a personal belt answers an absorbed hit.</summary>
    public enum PersonalShieldHitStyle
    {
        Jitter = 0,
        Brighten = 1,
        HexWave = 2
    }

    public sealed class VisualTweaksSettings : ModSettings
    {
        public const int StyleComet = 0;
        public const int StyleFine = 1;
        public const int StyleParticle = 2;

        public const float MinDensity = 0.01f;
        public const float MaxDensity = 1f;

        // Single source of truth for the opacity slider: the settings window and the
        // renderer used to clamp to different ranges (0.05..1.25 vs 0.25..1.5), which made
        // every slider position below 0.25 look identical.
        public const float MinOpacity = 0.05f;
        public const float MaxOpacity = 1.25f;

        // 设置项全部是静态字段：mod 设置在进程内天然只有一份，套一层实例（或单例）没有收益，
        // 还会让每次读取都要先经过一次「拿实例」的间接。见 Gunplay 的 GunplaySettings。
        //
        // ExposeData 是 ModSettings 的实例成员，所以仍需一个实例作为存档读写句柄，
        // 由 VisualTweaksMod 构造函数里的 GetSettings 创建并挂在 Mod.settings 上。
        // 它只用于存取，不承载任何状态。
        public static bool replaceAORenderer = true;
        public static int style = StyleParticle;
        public static bool showGuideLine = false;
        public static bool smoothField = true;
        public static float density = 0.25f;
        public static float streakLength = 1f;
        public static float speedLengthInfluence = 1f;
        public static float lineWidth = 1f;
        public static float opacity = 0.5f;
        public static bool coloredParticles = true;
        public static float animationSpeed = 1f;

        // Particle trails only: particles in air slower than this fade out. 0 disables
        // the filter. Invisible particles stay in the list and keep their block quota,
        // so the particles that remain keep their spatial density.
        public const float MaxLowWindThreshold = 10f;
        public static float particleLowWindThreshold = 0f;

        /// <summary>
        /// Rebuild the overlay every frame instead of on AO's 30 Hz cadence. Costs about
        /// twice the CPU (4.6 → 9.2 ms/s at 60 fps) but removes the two-frame hold that
        /// makes 30 Hz motion read as choppy.
        /// </summary>
        public static bool sixtyFpsMode = true;

        // Experimental: expand the particle ribbons in the vertex shader from a storage
        // buffer instead of on the CPU. The CPU keeps only the particle simulation, so
        // the per-joint field sampling and the vertex/index uploads leave the main
        // thread. Falls back to the CPU emitter automatically when the shader is missing
        // or fails to compile.
        public static bool gpuParticleGeometry = true;

        public static bool pauseWhenPaused = true;
        public static bool scaleSpeedWithGameSpeed = false;

        // Gas / mist field rendering.
        public static bool useGasField = true;
        public static bool gasFieldSmooth = true;

        // How the procedural detail noise moves. The density itself is always advected
        // by AO; this only controls the sub-cell texture that makes it look like gas.
        public const int GasNoiseAmbient = 0;
        public const int GasNoiseLocalFlow = 1;
        public static int gasFieldNoiseMode = GasNoiseLocalFlow;

        // Time constant of the exponential smoothing. It only has to bridge the gaps
        // between 10 Hz publishes, because the cross-fade already hides the
        // frame-to-frame stepping.
        public const float MinSmoothTau = 0.05f;
        public const float MaxSmoothTau = 0.5f;
        public static float gasFieldSmoothTau = 0.15f;

        // Prints one line per window re-centre (origin, size, view). Off by default: the
        // message itself costs ~10 µs plus a 2.5 KB allocation, which is visible in a
        // profiler trace of a frame that re-centres and is exactly what you would be
        // profiling when looking at that frame.
        public static bool gasFieldTraceWindows = false;

        // Temporary flash investigation. On by default ONLY while reproducing the
        // "whole gas layer flashes for one frame" defect: the setting writes a full
        // per-frame snapshot to a temp log. Flip the default to false before shipping.
        public static bool gasFieldDiagnostics = false;
        public static float gasFieldOpacity = 1f;
        public static float gasFieldScale = 1f;
        public static float gasFieldAnimation = 1f;
        public static float gasFieldBlur = 1f;
        public static float gasFieldSharpen = 0f;
        public static float gasFieldFaint = 1f;
        public static bool gasFieldCloudy = true;

        // Deadlife dust is the one gas whose look *is* its sprite: a sparse dust texture
        // rather than a smooth volume. With this on, the field shades it like the other
        // three (a navy cloud). With it off, the field drops the dust channel and a
        // separate layer redraws it with vanilla's own material and texture.
        public static bool gasFieldReplaceDeadlife = true;

        // Explosion shockwave. The per-blast radius and lifetime are derived from the
        // Explosion instance; these sliders only set the global look and the strength
        // scaling, so small and large blasts still differ on screen.
        public static bool explosionRippleEnabled = true;

        public const float MinRippleIntensity = 0.1f;
        public const float MaxRippleIntensity = 2f;
        public static float explosionRippleIntensity = 1f;

        public const float MinRippleRadiusScale = 0.5f;
        public const float MaxRippleRadiusScale = 2f;
        public static float explosionRippleRadiusScale = 1f;

        public static float explosionRippleRadiusInfluence = 0.35f;

        // 0.5 s is the baseline; the slider spans 0.1x..4x of it.
        public const float RippleDurationBaseline = 0.5f;
        public const float MinRippleDuration = RippleDurationBaseline * 0.1f;
        public const float MaxRippleDuration = RippleDurationBaseline * 4f;
        public static float explosionRippleDuration = RippleDurationBaseline;

        // Fixed per-map cap. 256 simultaneous waves is effectively unreachable in
        // normal play, so this is only a safety bound, not a tuning knob.
        public const int MaxRippleCount = 256;

        // Shield hit ripples. Separate from the shockwave switches above because the
        // two answer different questions: one is "do explosions distort the air", the
        // other is "do I get feedback that a shield stopped the shot". A player who
        // finds explosion shockwaves noisy may still want the shield cue.
        //
        // The default life is deliberately short — a ripple on a dome should snap, not
        // sweep. 0.24 s at 60 ticks/s is about 14 ticks, which is also roughly how long
        // vanilla's own Interceptor_BlockedProjectile flash lasts.
        public static bool shieldRippleEnabled = true;

        public const float MinShieldRippleIntensity = 0.1f;
        public const float MaxShieldRippleIntensity = 3f;
        public static float shieldRippleIntensity = 1f;

        public const float MinShieldRippleDuration = 0.08f;
        public const float MaxShieldRippleDuration = 1f;
        public static float shieldRippleDuration = 0.2f;

        // How strongly the incoming projectile's damage drives the ripple. Damage decides
        // size, strength and duration; the shield's own radius only caps the size. This
        // multiplier moves all three at once, so a player who wants every hit to read the
        // same can pull it towards zero and one who wants a rifle shot to visibly outshine
        // a pistol shot can push it up.
        public const float MinShieldRippleDamageScale = 0.25f;
        public const float MaxShieldRippleDamageScale = 3f;
        public static float shieldRippleDamageScale = 1f;

        // Keeps active shields visible without a selection. Vanilla hides them outright
        // (see ShieldVisibilityPatches), which makes a shield's coverage hard to read
        // until something is actually stopped. Off by default: the stock look is the
        // baseline, and this is an opt-in.
        public static bool alwaysShowShields = true;

        // Whether the custom shield surfaces freeze when the game is paused. The
        // shader animation (HexBubble scan, angular drift, break noise) and the idle
        // breathing alpha otherwise keep running over a paused colony, because their
        // clock is wall time rather than game time. Hit and break progress already
        // follows TicksGame and therefore freezes either way.
        public static bool shieldPauseWhenPaused = true;

        // Large shields and personal belts replace vanilla's drawing independently
        // (see ShieldDomePatches and PersonalShieldPatches). Both default to on: the
        // point of the mod is that the drawn radius matches the one the game blocks
        // projectiles with, and a belt has no radius of its own, so it borrows
        // vanilla's energy-driven bubble size instead. Both are re-read every frame,
        // so turning either off is an instant A/B against the stock look.
        public static bool replaceLargeShields = true;
        public static bool replacePersonalShields = true;

        // VEF's shield-field comp (the VFES repulsors) has its own switch. It is a
        // separate family from vanilla projectors because it is a different comp in a
        // different assembly, and pressing the repulsor switch must not silently
        // enable or disable anything vanilla. Its surface, impact and break choices
        // are the large-shield ones: a repulsor is a projector dome, and a second set
        // of style sliders for the same look would just be two places to tune it.
        public static bool replaceRepulsorShields = true;

        // VEF's shield-bubble comp is the personal counterpart: apparel and creature
        // bubbles that are neither vanilla CompShield nor a shield field. It uses the
        // personal family's surface, opacity, hit and break choices, but the switch is
        // separate so a player can keep a warcasket's stock bubble while replacing
        // belts (or the reverse).
        public static bool replaceVefShieldBubbles = true;

        // VEF's hediff shields are the third personal implementation: a HediffComp
        // drawn from Pawn.DrawAt. Separate switch for the same reason as the other two
        // VEF families, and the same personal appearance settings apply.
        public static bool replaceVefHediffShields = true;

        // VEF stores the repulsor's opacity in the def's health colour alpha
        // (roughly 0.35 at low energy to 1.0 at full). That number was authored for
        // an additive MoteGlow billboard; the same alpha through an alpha-blended
        // dome reads much more solid, so this multiplier brings it into the range
        // the other shields use. 0.6 is about the selected dome's peak.
        public const float MinRepulsorShieldOpacity = 0.05f;
        public const float MaxRepulsorShieldOpacity = 1.5f;
        public static float repulsorShieldOpacity = 0.6f;

        // When on, only vanilla comp classes are replaced; mods that derive from them
        // keep their own drawing. Large shields use CompProjectileInterceptor, and the
        // vanilla CompGravshipShieldGenerator is explicitly included as well. Personal
        // belts use CompShield, which mods may also subclass.
        public static bool largeShieldsVanillaOnly = false;
        public static bool personalShieldsVanillaOnly = false;

        // Vanilla's Interceptor_BlockedProjectile effecter contains 3-5 DustPuff
        // flecks on top of its sound and flash. They are aimed at the impact point
        // rather than the dome and can bury the custom hit reaction, so they get a
        // switch. Only the dust children of a large shield's intercept effecter are
        // removed; the sound and flash, and every other dust puff in the game, stay.
        public static bool largeShieldDisableHitDust = true;

        // Which surface the replacement dome draws. Crisp is the original analytic
        // ring; Hex is the spherical panel shell; Vanilla reproduces the matching
        // stock texture — the narrow ForceField ring on a dome, the broader
        // gradient-filled ShieldBubble on a belt — rebuilt in the shader and never
        // extracted from the game. Bubble is the belt's texture offered to a dome as
        // a fourth choice, so a projector can wear the same filled sphere. Belts
        // have their own choice just below.
        //
        // An enum rather than four bools: the styles are mutually exclusive, and with
        // checkboxes two of them could be on at once.
        public static ShieldDomeStyle shieldDomeStyle = ShieldDomeStyle.Vanilla;

        // Belts choose their surface independently. They are a quarter of a dome's
        // diameter but are always shown at one steady level, so a style that reads
        // well on a 25-cell projector does not necessarily read well on a 1.2-cell
        // belt.
        public static ShieldDomeStyle personalShieldDomeStyle = ShieldDomeStyle.Vanilla;

        /// <summary>The surface style chosen for one shield family.</summary>
        internal static ShieldDomeStyle ShieldDomeStyleFor(ShieldHexTarget target) =>
            target == ShieldHexTarget.Personal
                ? personalShieldDomeStyle
                : shieldDomeStyle;

        /// <summary>
        /// The break animation for one family. Each family stores its choice per
        /// surface (crisp / stock-like / hex), so the dome and the belt never move
        /// each other, and switching a surface back and forth keeps that surface's
        /// choice. Both stock-like surfaces share one choice per family: they differ
        /// in profile, not in how they fail.
        /// </summary>
        internal static ShieldBreakStyle CurrentShieldBreakStyleFor(ShieldHexTarget target)
        {
            ShieldDomeStyle style = ShieldDomeStyleFor(target);
            bool personal = target == ShieldHexTarget.Personal;
            // Both lattice surfaces share one choice and one default. The hex
            // bubble's net is the same baked lattice, so losing whole panels is
            // as meaningful there as on the panel shell — the bubble underneath
            // is just the surface the panels sit on.
            if (style == ShieldDomeStyle.Hex
                || style == ShieldDomeStyle.HexBubble)
            {
                return personal
                    ? personalHexShieldBreakStyle
                    : largeHexShieldBreakStyle;
            }

            bool stockLike = style == ShieldDomeStyle.Vanilla
                || (target == ShieldHexTarget.Large && style == ShieldDomeStyle.Bubble);
            if (stockLike)
            {
                return personal
                    ? personalVanillaShieldBreakStyle
                    : largeVanillaShieldBreakStyle;
            }

            return personal
                ? personalCrispShieldBreakStyle
                : largeCrispShieldBreakStyle;
        }

        // How a hit is answered. This is independent of the dome surface, but the
        // valid choices differ: the panel shell has its own lattice wave, while the
        // crisp and stock-like surfaces can either paint a flare or brighten in a
        // sector. Each surface family therefore keeps its own choice, so switching
        // the surface and switching it back does not silently rewrite the animation.
        // The two stock-like profiles share one choice: they differ in profile, not
        // in where a hit lands.
        public static ShieldImpactStyle crispShieldImpactStyle = ShieldImpactStyle.Flare;
        public static ShieldImpactStyle vanillaShieldImpactStyle = ShieldImpactStyle.Brighten;
        public static ShieldImpactStyle hexShieldImpactStyle = ShieldImpactStyle.HexWave;

        /// <summary>The impact animation that belongs to the selected dome style.</summary>
        // HexBubble shares the panel shell's hit choice and its default (the
        // lattice wave): both sample the same per-cell ids from the baked map.
        public static ShieldImpactStyle CurrentShieldImpactStyle =>
            shieldDomeStyle == ShieldDomeStyle.Hex
                || shieldDomeStyle == ShieldDomeStyle.HexBubble
                ? hexShieldImpactStyle
            : (shieldDomeStyle == ShieldDomeStyle.Vanilla
                || shieldDomeStyle == ShieldDomeStyle.Bubble)
                ? vanillaShieldImpactStyle
                : crispShieldImpactStyle;

        // Vanilla's brighten cone is 30 degrees total. It used to be a constant in
        // the renderer; it is a slider now because 30 is only the stock value, and
        // the reaction is also available on the two custom surfaces. The angle is
        // the full width, not the half-angle.
        public const float MinShieldBrightenAngle = 5f;
        public const float MaxShieldBrightenAngle = 90f;
        public static float shieldBrightenAngle = 40f;

        // How long one intercepted shot's reaction stays on screen on a large shield.
        // The flare, the sector brightening and the hex lattice wave share this clock,
        // so one hit reads as one event; 60 ticks = one second. The default 40 is
        // vanilla's own cone fade. Belts have their own duration
        // (personalShieldJitterTicks) because their hit effects are tuned separately.
        public const float MinShieldImpactTicks = 10f;
        public const float MaxShieldImpactTicks = 120f;
        public static float shieldImpactTicks = 30f;

        // How a shield breaks. Independent of the surface, with per-style choices:
        // Vanilla leaves the one-frame disappearance alone, Dissolve is the
        // continuous noise tear, HexDissipate loses whole panels on the shell. Each
        // family stores its choice per surface (crisp / stock-like / hex), so the
        // dome and the belt never move each other. The two stock-like profiles share
        // one choice per family. Vanilla's Shield_Break effecter (flash + sound)
        // always plays underneath either way.
        public static ShieldBreakStyle largeCrispShieldBreakStyle =
            ShieldBreakStyle.Dissolve;
        public static ShieldBreakStyle largeVanillaShieldBreakStyle =
            ShieldBreakStyle.Dissolve;
        public static ShieldBreakStyle largeHexShieldBreakStyle =
            ShieldBreakStyle.HexDissipate;

        public static ShieldBreakStyle personalCrispShieldBreakStyle =
            ShieldBreakStyle.Dissolve;
        public static ShieldBreakStyle personalVanillaShieldBreakStyle =
            ShieldBreakStyle.Dissolve;
        public static ShieldBreakStyle personalHexShieldBreakStyle =
            ShieldBreakStyle.HexDissipate;

        // How a shield comes back after being down. Unlike the break choice this is
        // one value per family, not per surface: coming online is the same event on
        // every surface, and a surface switch should not silently rewrite it. Each
        // family also has its own duration; None ignores it.
        public const float MinShieldRestoreTicks = 10f;
        public const float MaxShieldRestoreTicks = 180f;
        public static ShieldRestoreStyle largeShieldRestoreStyle =
            ShieldRestoreStyle.Grow;
        public static ShieldRestoreStyle personalShieldRestoreStyle =
            ShieldRestoreStyle.Grow;
        public static float largeShieldRestoreTicks = 60f;
        public static float personalShieldRestoreTicks = 60f;

        /// <summary>The break animation that belongs to the selected dome style.</summary>
        public static ShieldBreakStyle CurrentShieldBreakStyle =>
            CurrentShieldBreakStyleFor(ShieldHexTarget.Large);

        public const float MinShieldHexFace = 0f;
        public const float MaxShieldHexFace = 0.6f;
        public static float largeShieldHexFaceStrength = 0.22f;
        public static float personalShieldHexFaceStrength = 0.22f;

        public const float MinShieldHexLine = 0f;
        public const float MaxShieldHexLine = 1.2f;
        public static float largeShieldHexLineStrength = 0.25f;
        public static float personalShieldHexLineStrength = 0.25f;

        // ---- The two lattice styles: centre aperture ------------------------
        //
        // The panel shell and the hex bubble both sit over whatever they protect,
        // and a shield is most in the way exactly where a real glass dome is
        // clearest: dead centre, where the surface faces the viewer. This scales
        // the STATIC material to zero at the centre and back to full over the
        // clear radius, so pawns and the emitter stay readable. The rim, the hex
        // bubble's scan band, and every hit / break effect are deliberately
        // outside it.
        //
        // The radius is a share of the gameplay radius, never a pixel count, so a
        // 1.2-cell belt and a 25-cell gravship dome carry the same shape at every
        // zoom. A wider radius is the whole control: the alpha at the exact centre
        // is always zero, and the radius only decides how quickly it climbs back.
        // 0 disables the effect. The ridge and core are never scaled, so the
        // silhouette survives even at 1.
        public const float MinShieldCenterClear = 0f;
        public const float MaxShieldCenterClear = 1f;
        public static float largeShieldCenterClear = 0.5f;
        public static float personalShieldCenterClear = 0.5f;

        // Icosahedron subdivision k: 10k^2+2 cells over the whole sphere (12 of
        // them pentagons, always). k=2 reads like a football, k=12 like a fine
        // energy mesh (1442 cells).
        //
        // The slider is a DRAFT value, and shieldHexDensityApplied is the one that
        // is actually baked. They are separate because a rebake is not free: at
        // 2048^2 the bake is a few hundred ms of main-thread work, and a slider that
        // rebuilt on every integer step would stall the game repeatedly while dragged.
        // The settings page therefore exposes a button that copies draft -> applied
        // and rebakes once, and the renderer reads only `applied`.
        public const float MinShieldHexDensity = 2f;
        public const float MaxShieldHexDensity = 12f;
        public static float shieldHexDensity = 5f;
        public static int shieldHexDensityApplied = 5;

        // Personal belts get their own lattice bake. The cell count is a property of
        // the dome, not of the world: a k that reads as a fine energy mesh on a
        // 25-cell gravship shield puts the same number of cells on a 1.2-cell belt,
        // where each one is only a few screen pixels and the line term fades into
        // noise. Lower default, same range, and the player can tune it independently.
        //
        // The texel resolution is fixed per target in ShieldHexMap (512² for belts,
        // 2048² for domes). A belt covers a few thousand screen pixels, so 512² is
        // already oversampled and costs 2 MiB instead of 32; exposing it as a slider
        // bought nothing but another thing to keep in sync.
        public static float personalShieldHexDensity = 4f;
        public static int personalShieldHexDensityApplied = 4;

        // ---- Hex bubble: the scan band --------------------------------------
        //
        // The lattice is never lit per cell. One continuous mask — a function of
        // p.y and time only — modulates the whole net layer, so there is no
        // per-panel state to keep and no extra draw call. Band width (0.06) and
        // the leading-edge highlight (0) are fixed after the preview was tuned;
        // everything worth tuning is exposed here. Shared by both shield
        // families: a scan is a look, not a per-shield state.
        public const float MinHexBubbleScanPeriod = 0.6f;
        public const float MaxHexBubbleScanPeriod = 6f;
        public static float hexBubbleScanPeriod = 2.4f;

        /// <summary>True = top to bottom, false = bottom to top.</summary>
        public static bool hexBubbleScanDown = true;

        public const float MinHexBubbleFeather = 0.02f;
        public const float MaxHexBubbleFeather = 0.5f;
        public static float hexBubbleFeather = 0.20f;

        public const int MinHexBubbleStripes = 1;
        public const int MaxHexBubbleStripes = 3;
        public static int hexBubbleStripes = 1;

        public const float MinHexBubbleBaseline = 0f;
        public const float MaxHexBubbleBaseline = 0.6f;
        public static float hexBubbleBaseline = 0.12f;

        /// <summary>
        /// Off by default. On keeps vanilla's GetCurrentAlpha() sin pulse; off
        /// uses one fixed, lower alpha (ShieldHexBubble.SteadyAlpha), so a
        /// non-pulsing bubble does not read brighter than the pulsing one's
        /// average. See CompProjectileInterceptor.GetCurrentAlpha_Selected.
        /// </summary>
        public static bool hexBubblePulse = false;

        // Overall drawing opacity for belts. An interceptor dome's alpha comes from
        // GetCurrentAlpha(), which for a selected shield pulses between 0.2 and 0.62
        // and is much lower when idle; a belt has no such rule, and drawing it at
        // full alpha made it read far more solid than every dome around it. 0.4 is
        // the middle of that selected range and the default; independent of the dome
        // so the two can be tuned separately.
        public const float MinPersonalShieldOpacity = 0.05f;
        public const float MaxPersonalShieldOpacity = 1f;
        public static float personalShieldOpacity = 0.2f;

        // How a personal belt answers an absorbed hit. This is a separate choice from
        // the dome's impact animations: a belt is one character wide, so the effects
        // that read on a 25-cell projector do not automatically read here, and tuning
        // one family must not move the other.
        //
        //   Jitter    — vanilla's push-and-shrink, restored with tunable amplitude.
        //   Brighten  — the dome's mode 4: the struck sector of the surface lights up.
        //   HexSpread — the dome's mode 3: panels around the hit light and walk
        //               outward. It samples the belt's own baked lattice, so that
        //               atlas is baked even when the belt surface itself is not hex.
        //
        // An enum rather than three bools for the same reason the surface style is one:
        // the effects are mutually exclusive. There is deliberately no "off" value:
        // the switch is about *which* answer a hit gets, and the dust toggle below
        // handles the only part of vanilla's feedback that is not wanted.
        public static PersonalShieldHitStyle personalShieldHitStyle =
            PersonalShieldHitStyle.Jitter;

        // Vanilla's absorbed-hit feedback includes a burst of DustPuff flecks on top
        // of the flash and the sound. They are unrelated to the surface and can bury
        // the small, deliberate hit animations this mod draws, so they are switched
        // off by default. Only AbsorbedDamage's dust is affected: the break burst and
        // every other dust puff in the game are untouched.
        public static bool personalShieldDisableHitDust = true;

        // Amplitude of the vanilla-style push. Only the Jitter effect uses it.
        public const float MinPersonalShieldJitterDistance = 0f;
        public const float MaxPersonalShieldJitterDistance = 0.15f;
        public static float personalShieldJitterDistance = 0.05f;

        // How long any of the three effects lasts. One value for all of them on
        // purpose: the player is choosing what a hit looks like, not managing three
        // independent clocks. The field keeps its old name so existing saves keep
        // the duration they were tuned with; 60 ticks is one in-game second.
        public const float MinPersonalShieldJitterTicks = 2f;
        public const float MaxPersonalShieldJitterTicks = 30f;
        public static float personalShieldJitterTicks = 8f;

        // Brighten: peak extra opacity of the struck sector, and the sector's full
        // width. Independent of the dome's own sliders because a belt is small enough
        // that the same numbers do not read the same way.
        public const float MinPersonalShieldBrightenStrength = 0f;
        public const float MaxPersonalShieldBrightenStrength = 1f;
        public static float personalShieldBrightenStrength = 0.55f;

        public const float MinPersonalShieldBrightenAngle = 5f;
        public const float MaxPersonalShieldBrightenAngle = 90f;
        public static float personalShieldBrightenAngle = 30f;

        // HexSpread: peak brightness of the lit panels and how far the wave travels,
        // as a share of the run to the far rim. The belt's lattice is sparser than
        // the dome's, so both defaults sit above the dome's values.
        public const float MinPersonalShieldHexImpactStrength = 0f;
        public const float MaxPersonalShieldHexImpactStrength = 0.8f;
        public static float personalShieldHexImpactStrength = 0.12f;

        public const float MinPersonalShieldHexImpactShare = 0.12f;
        public const float MaxPersonalShieldHexImpactShare = 0.6f;
        public static float personalShieldHexImpactShare = 0.4f;

        // The panel style answers a hit differently from the smooth one - a flash that
        // spreads across the lattice instead of a wedge painted on the surface - so it
        // gets its own strength and reach. Sharing the flare's slider was wrong twice
        // over: the two effects have no common scale (a lit tile is tiny next to a
        // wedge, so it needed an internal x1.6 fudge), and the flare's "size" is an
        // angle, which is meaningless for a lattice wave.
        public const float MinShieldHexImpactStrength = 0f;
        public const float MaxShieldHexImpactStrength = 0.4f;
        public static float shieldHexImpactStrength = 0.1f;

        // Share of the run from the impact to the far rim that the wave crosses. A
        // share and not a panel count, because a panel is 1/k of the dome: a fixed
        // count made a dense shield barely react and a sparse one flood.
        public const float MinShieldHexImpactShare = 0.12f;
        public const float MaxShieldHexImpactShare = 0.6f;
        public static float shieldHexImpactShare = 0.32f;

        // The four dome/flare dials that are worth a slider. Everything else about the
        // shield is derived rather than chosen: line weights scale with zoom through a
        // screen-space derivative, and the colour comes from the def so modded shields
        // keep their identity.
        //
        // Thickness and brightness are separate on purpose. Turning up brightness on a
        // narrow line only produces a brighter narrow line, which is why the first
        // version of this shader still looked thin at maximum weight.
        public const float MinShieldRidgeWidth = 1f;
        public const float MaxShieldRidgeWidth = 8f;
        public static float largeShieldRidgeWidth = 3.6f;
        public static float largeShieldRidgeBrightness = 0.52f;
        public static float personalShieldRidgeWidth = 3.6f;
        public static float personalShieldRidgeBrightness = 0.52f;

        // Peak opacity of one impact flare. Vanilla's cone reaches 0.82, which reads as
        // an opaque slice rather than as light; the default here is a third of that.
        public const float MinShieldFlareAlpha = 0f;
        public const float MaxShieldFlareAlpha = 0.6f;
        public static float shieldFlareAlpha = 0.30f;

        // How much the incoming projectile's damage drives flare brightness. 0 makes
        // every hit look alike, 1 responds linearly. The response is clamped inside the
        // renderer either way, so a 200-damage charge blast cannot blow out the dome and
        // a 3-damage spit round cannot vanish.
        public static float shieldFlareDamageInfluence = 0.5f;

        // Overall flare size. Width otherwise follows the projectile's damage through the
        // same curve the rim ripple uses, so a pistol and a charge lance differ; this is
        // the single knob for "the wedge is too small / too large" that does not require
        // retuning that curve, and it is independent of the ripple's own sliders because
        // the two are separate features.
        public const float MinShieldFlareSize = 0.5f;
        public const float MaxShieldFlareSize = 2f;
        public static float shieldFlareSizeScale = 1f;

        // In ticks. 60 ticks is one in-game second at normal speed; 90 gives the tear
        // time to cross a large dome without lingering into the recharge cycle.
        public const float MinShieldDissolveTicks = 30f;
        public const float MaxShieldDissolveTicks = 180f;
        public static float largeShieldDissolveTicks = 60f;
        public static float personalShieldDissolveTicks = 60f;

        // The dissolve has to supply its own brightness because GetCurrentAlpha() is
        // already zero by then. 0.6 sits at the selected dome's alpha peak and is
        // 1.5x a belt's default 0.4: enough to read as a break without turning the
        // surviving surface into a solid shell. The hot tear carries the initial
        // flash on its own; the shader no longer surges the whole body.
        public const float MinShieldDissolveBrightness = 0.2f;
        public const float MaxShieldDissolveBrightness = 1.5f;
        public static float largeShieldDissolveBrightness = 0.6f;
        public static float personalShieldDissolveBrightness = 0.6f;

        // Vacuum leak airflow (Odyssey vacuum maps). Streams converge on ventilation
        // ports while air escapes; hull breaches add a short two-sided burst. The
        // intensity formula itself is state-driven, this slider only scales the look.
        public static bool vacuumLeakEnabled = true;

        // Global opacity multiplier for the leak stream. The renderer applies a
        // fixed gain on top, so this stays a compact 0.1..2 range with 1 as the
        // default look.
        public const float MinLeakIntensity = 0.1f;
        public const float MaxLeakIntensity = 2f;
        public static float vacuumLeakIntensity = 1f;

        // Multiplier on the scroll speed of the streak noise (both ports and
        // breaches). Integrated with game time, so it still freezes on pause.
        public const float MinLeakSpeed = 0.2f;
        public const float MaxLeakSpeed = 2.5f;
        public static float vacuumLeakSpeed = 1f;

        // Multiplier on the one-shot wall breach and ceiling hole envelopes. It
        // scales their total lifetime only; the streak scroll speed has its own
        // slider, and the continuous door/vent streams are state-driven.
        public const float MinLeakDurationScale = 0.2f;
        public const float MaxLeakDurationScale = 1.5f;
        public static float vacuumLeakDurationScale = 1f;

        // Optional screen-space refraction: the denser air in the stream bends the
        // frame behind it. Strength is the maximum displacement in screen pixels at
        // full coverage; the shader scales it down with the local stream opacity.
        public static bool vacuumLeakDistortion = true;
        public const float MinLeakDistortion = 0f;
        public const float MaxLeakDistortion = 12f;
        public static float vacuumLeakDistortionStrength = 6f;

        // DevMode only: one log line per port per 250-tick scan, for verifying the
        // intensity chain against Tools/vacuum_flow.py without reading game memory.
        public static bool vacuumLeakTrace = false;

        // High-temperature air shimmer. The mask stores temperature normalised to
        // 0..240 C, matching the lower bound of vanilla's auto-ignition range
        // (SteadyEnvironmentEffects.AutoIgnitionTemperatureRange, 240..1000 C).
        // 240/255 is about 0.94 C per code, still finer than the 1 C slider step.
        public const float HeatTempNormMax = 240f;

        public static bool heatHazeEnabled = true;

        // How often the temperature mask is re-baked. Low frequency (the default) re-measures
        // every two seconds: room temperature moves over tens of seconds, and air that reacts
        // the instant a room warms up does not read as hot air, it reads as a light switch.
        // High frequency keeps 5 Hz for players who want the shimmer to track a fire being
        // lit, at ten times the bake and upload cost.
        public static bool heatHazeHighFrequency = false;

        // Which temperature source the heat mask is baked from. Vanilla only knows room
        // temperature: GenTemperature resolves a room per lookup and hands back that
        // room's single value, so a cell-by-cell read would just repeat the room figure
        // across its footprint at a much higher cost. AO publishes a real per-cell field,
        // which is why this switch is only meaningful with AO — UsePerCellTemperature
        // refuses the flag when AO is absent, so a config written with AO installed
        // cannot silently take the expensive path after AO is removed.
        public static bool heatHazePerCellTemp = false;

        public const float MinHeatStartTemp = 0f;
        public const float MaxHeatStartTemp = 100f;
        public static float heatHazeStartTemp = 35f;

        public const float MinHeatFullTemp = 20f;
        public const float MaxHeatFullTemp = 240f;
        public static float heatHazeFullTemp = 80f;

        public const float MinHeatIntensity = 0f;
        public const float MaxHeatIntensity = 3f;
        public static float heatHazeIntensity = 1f;

        public const float MinHeatScale = 0.2f;
        public const float MaxHeatScale = 3f;
        public static float heatHazeScale = 1f;

        // The shader's drift is tuned so that speed 1 equals the previous
        // fixed 2x drift. The slider is a multiplier of that baseline.
        public const float MinHeatSpeed = 0.1f;
        public const float MaxHeatSpeed = 2f;
        public static float heatHazeSpeed = 1f;

        // Maximum screen-space displacement in pixels at full heat.
        public const float MinHeatAmplitude = 0.5f;
        public const float MaxHeatAmplitude = 8f;
        public static float heatHazeAmplitude = 2.5f;

        // Extra contribution from the direct heat-source channel (fires, campfires,
        // heaters, torches, vents). Room temperature already includes indoor heat, so
        // this mainly covers outdoor sources and makes them visible immediately.
        public const float MinHeatFireBoost = 0f;
        public const float MaxHeatFireBoost = 2f;
        public static float heatHazeFireBoost = 1f;

        // Low-temperature room mist. The mask stores room temperature normalised
        // over -160..40 C. The lower bound is set by the coldest threshold the
        // slider allows (-150 C), not by anything reachable in play, so the mask
        // never has to clamp a value that the user explicitly asked for. Roughly
        // 0.78 C per code — still finer than the 1 C slider step. Unlike the heat
        // mask the range has to span negatives, which is why it is not a simple
        // 0..max clamp.
        public const float ColdTempNormMin = -160f;
        public const float ColdTempNormMax = 40f;

        public static bool coldMistEnabled = true;

        // The warmer of the two thresholds: at or above this indoor temperature the
        // room is clear. The floor is negative so the effect can be reserved for
        // genuinely hard freezers.
        public const float MinColdStartTemp = -20f;
        public const float MaxColdStartTemp = 30f;
        public static float coldMistStartTemp = -5f;

        // The colder one: at or below this the mist is at full strength.
        public const float MinColdFullTemp = -150f;
        public const float MaxColdFullTemp = 4f;
        public static float coldMistFullTemp = -75f;

        // The effect reads as unnatural long before full opacity, so the useful
        // band is the bottom of the slider; the range stays wide for odd setups.
        public const float MinColdOpacity = 0.01f;
        public const float MaxColdOpacity = 1f;
        public static float coldMistOpacity = 0.3f;

        // How much of a cold room is covered rather than how thick the haze is:
        // 0 leaves only the densest wisps, 1 fogs the whole room. The shader maps
        // this over a deliberately wide threshold band, so even 0.1 is sparse.
        public const float MinColdCoverage = 0f;
        public const float MaxColdCoverage = 1f;
        public static float coldMistCoverage = 0.5f;

        public const float MinColdScale = 0.3f;
        public const float MaxColdScale = 3f;
        public static float coldMistScale = 1f;

        // Rate of the in-place churn (octave boil plus the broad life orbit), not
        // of a translation: the field never drifts across the map. The shader's
        // constants are tuned around 2.0, and the range runs to double that, so the
        // top of the slider is a visible boil rather than a barely moving field.
        public const float MinColdSpeed = 0.2f;
        public const float MaxColdSpeed = 4f;
        public static float coldMistSpeed = 2f;

        public static bool ReplacementEnabled => replaceAORenderer;

        /// <summary>
        /// True when the heat mask should be baked from AO's per-cell field. False means
        /// vanilla's room temperature, which is also the only option without AO: the
        /// setting is stored as written but ignored, so re-installing AO restores the
        /// user's choice instead of resetting it.
        /// </summary>
        public static bool UsePerCellTemperature =>
            heatHazePerCellTemp && AOIntegration.PerCellTemperatureAvailable;

        public static bool FineStyle => style == StyleFine;

        public static bool ParticleStyle => style == StyleParticle;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref replaceAORenderer, "replaceAORenderer", true);
            Scribe_Values.Look(ref style, "style", StyleParticle);
            Scribe_Values.Look(ref showGuideLine, "showGuideLine", false);
            Scribe_Values.Look(ref smoothField, "smoothField", true);
            Scribe_Values.Look(ref density, "density", 0.25f);
            Scribe_Values.Look(ref streakLength, "streakLength", 1f);
            Scribe_Values.Look(ref speedLengthInfluence, "speedLengthInfluence", 1f);
            Scribe_Values.Look(ref lineWidth, "lineWidth", 1f);
            Scribe_Values.Look(ref opacity, "opacity", 0.5f);
            Scribe_Values.Look(ref coloredParticles, "coloredParticles", true);
            Scribe_Values.Look(ref animationSpeed, "animationSpeed", 1f);
            Scribe_Values.Look(ref particleLowWindThreshold, "particleLowWindThreshold", 0f);
            Scribe_Values.Look(ref sixtyFpsMode, "sixtyFpsMode", true);
            Scribe_Values.Look(ref gpuParticleGeometry, "gpuParticleGeometry", true);
            Scribe_Values.Look(ref pauseWhenPaused, "pauseWhenPaused", true);
            Scribe_Values.Look(
                ref scaleSpeedWithGameSpeed,
                "scaleSpeedWithGameSpeed",
                false);
            Scribe_Values.Look(ref useGasField, "useGasField", true);
            Scribe_Values.Look(ref gasFieldSmooth, "gasFieldSmooth", true);
            Scribe_Values.Look(
                ref gasFieldNoiseMode,
                "gasFieldNoiseMode",
                GasNoiseLocalFlow);
            Scribe_Values.Look(ref gasFieldSmoothTau, "gasFieldSmoothTau", 0.15f);
            Scribe_Values.Look(ref gasFieldTraceWindows, "gasFieldTraceWindows", false);
            Scribe_Values.Look(ref gasFieldDiagnostics, "gasFieldDiagnostics", false);
            Scribe_Values.Look(ref gasFieldOpacity, "gasFieldOpacity", 1f);
            Scribe_Values.Look(ref gasFieldScale, "gasFieldScale", 1f);
            Scribe_Values.Look(ref gasFieldAnimation, "gasFieldAnimation", 1f);
            Scribe_Values.Look(ref gasFieldBlur, "gasFieldBlur", 1f);
            Scribe_Values.Look(ref gasFieldSharpen, "gasFieldSharpen", 0f);
            Scribe_Values.Look(ref gasFieldFaint, "gasFieldFaint", 1f);
            Scribe_Values.Look(ref gasFieldCloudy, "gasFieldCloudy", true);
            Scribe_Values.Look(
                ref gasFieldReplaceDeadlife,
                "gasFieldReplaceDeadlife",
                true);
            Scribe_Values.Look(
                ref explosionRippleEnabled,
                "explosionRippleEnabled",
                true);
            Scribe_Values.Look(
                ref explosionRippleIntensity,
                "explosionRippleIntensity",
                1f);
            Scribe_Values.Look(
                ref explosionRippleRadiusScale,
                "explosionRippleRadiusScale",
                1f);
            Scribe_Values.Look(
                ref explosionRippleRadiusInfluence,
                "explosionRippleRadiusInfluence",
                0.35f);
            Scribe_Values.Look(
                ref explosionRippleDuration,
                "explosionRippleDuration",
                RippleDurationBaseline);
            Scribe_Values.Look(ref shieldRippleEnabled, "shieldRippleEnabled", true);
            Scribe_Values.Look(ref shieldRippleIntensity, "shieldRippleIntensity", 1f);
            Scribe_Values.Look(ref shieldRippleDuration, "shieldRippleDuration", 0.2f);
            Scribe_Values.Look(
                ref shieldRippleDamageScale,
                "shieldRippleDamageScale",
                1f);
            Scribe_Values.Look(ref alwaysShowShields, "alwaysShowShields", true);
            Scribe_Values.Look(
                ref shieldPauseWhenPaused,
                "shieldPauseWhenPaused",
                true);
            Scribe_Values.Look(
                ref replaceLargeShields,
                "replaceLargeShields",
                true);
            Scribe_Values.Look(
                ref replacePersonalShields,
                "replacePersonalShields",
                true);
            Scribe_Values.Look(
                ref replaceRepulsorShields,
                "replaceRepulsorShields",
                true);
            Scribe_Values.Look(
                ref replaceVefShieldBubbles,
                "replaceVefShieldBubbles",
                true);
            Scribe_Values.Look(
                ref replaceVefHediffShields,
                "replaceVefHediffShields",
                true);
            Scribe_Values.Look(
                ref repulsorShieldOpacity,
                "repulsorShieldOpacity",
                0.6f);
            Scribe_Values.Look(
                ref largeShieldsVanillaOnly,
                "largeShieldsVanillaOnly",
                false);
            Scribe_Values.Look(
                ref personalShieldsVanillaOnly,
                "personalShieldsVanillaOnly",
                false);
            Scribe_Values.Look(
                ref largeShieldDisableHitDust,
                "largeShieldDisableHitDust",
                true);

            Scribe_Values.Look(
                ref shieldDomeStyle,
                "shieldDomeStyle",
                ShieldDomeStyle.Vanilla);
            Scribe_Values.Look(
                ref personalShieldDomeStyle,
                "personalShieldDomeStyle",
                ShieldDomeStyle.Vanilla);

            // One choice per surface, defaulting to the reaction that style had.
            Scribe_Values.Look(
                ref crispShieldImpactStyle,
                "crispShieldImpactStyle",
                ShieldImpactStyle.Flare);
            Scribe_Values.Look(
                ref vanillaShieldImpactStyle,
                "vanillaShieldImpactStyle",
                ShieldImpactStyle.Brighten);
            Scribe_Values.Look(
                ref hexShieldImpactStyle,
                "hexShieldImpactStyle",
                ShieldImpactStyle.HexWave);
            Scribe_Values.Look(ref shieldBrightenAngle, "shieldBrightenAngle", 40f);
            Scribe_Values.Look(ref shieldImpactTicks, "shieldImpactTicks", 30f);

            Scribe_Values.Look(
                ref largeCrispShieldBreakStyle,
                "largeCrispShieldBreakStyle",
                ShieldBreakStyle.Dissolve);
            Scribe_Values.Look(
                ref largeVanillaShieldBreakStyle,
                "largeVanillaShieldBreakStyle",
                ShieldBreakStyle.Dissolve);
            Scribe_Values.Look(
                ref largeHexShieldBreakStyle,
                "largeHexShieldBreakStyle",
                ShieldBreakStyle.HexDissipate);
            Scribe_Values.Look(
                ref personalCrispShieldBreakStyle,
                "personalCrispShieldBreakStyle",
                ShieldBreakStyle.Dissolve);
            Scribe_Values.Look(
                ref personalVanillaShieldBreakStyle,
                "personalVanillaShieldBreakStyle",
                ShieldBreakStyle.Dissolve);
            Scribe_Values.Look(
                ref personalHexShieldBreakStyle,
                "personalHexShieldBreakStyle",
                ShieldBreakStyle.HexDissipate);
            Scribe_Values.Look(
                ref largeShieldRestoreStyle,
                "largeShieldRestoreStyle",
                ShieldRestoreStyle.Grow);
            Scribe_Values.Look(
                ref personalShieldRestoreStyle,
                "personalShieldRestoreStyle",
                ShieldRestoreStyle.Grow);
            Scribe_Values.Look(
                ref largeShieldRestoreTicks,
                "largeShieldRestoreTicks",
                60f);
            Scribe_Values.Look(
                ref personalShieldRestoreTicks,
                "personalShieldRestoreTicks",
                60f);

            Scribe_Values.Look(
                ref largeShieldHexFaceStrength,
                "largeShieldHexFaceStrength",
                0.22f);
            Scribe_Values.Look(
                ref personalShieldHexFaceStrength,
                "personalShieldHexFaceStrength",
                0.22f);
            Scribe_Values.Look(
                ref largeShieldHexLineStrength,
                "largeShieldHexLineStrength",
                0.25f);
            Scribe_Values.Look(
                ref personalShieldHexLineStrength,
                "personalShieldHexLineStrength",
                0.25f);
            Scribe_Values.Look(
                ref largeShieldCenterClear,
                "largeShieldCenterClear",
                0.5f);
            Scribe_Values.Look(
                ref personalShieldCenterClear,
                "personalShieldCenterClear",
                0.5f);
            Scribe_Values.Look(ref shieldHexDensity, "shieldHexDensity", 5f);
            Scribe_Values.Look(
                ref shieldHexImpactStrength,
                "shieldHexImpactStrength",
                0.1f);
            Scribe_Values.Look(
                ref shieldHexImpactShare,
                "shieldHexImpactShare",
                0.32f);
            Scribe_Values.Look(
                ref shieldHexDensityApplied,
                "shieldHexDensityApplied",
                5);
            Scribe_Values.Look(
                ref personalShieldHexDensity,
                "personalShieldHexDensity",
                4f);
            Scribe_Values.Look(
                ref personalShieldHexDensityApplied,
                "personalShieldHexDensityApplied",
                4);
            Scribe_Values.Look(
                ref hexBubbleScanPeriod,
                "hexBubbleScanPeriod",
                2.4f);
            Scribe_Values.Look(ref hexBubbleScanDown, "hexBubbleScanDown", true);
            Scribe_Values.Look(ref hexBubbleFeather, "hexBubbleFeather", 0.20f);
            Scribe_Values.Look(ref hexBubbleStripes, "hexBubbleStripes", 1);
            Scribe_Values.Look(ref hexBubbleBaseline, "hexBubbleBaseline", 0.12f);
            Scribe_Values.Look(ref hexBubblePulse, "hexBubblePulse", false);
            Scribe_Values.Look(
                ref personalShieldOpacity,
                "personalShieldOpacity",
                0.2f);
            // The switch has no "off" value; anything out of range falls back to the
            // jitter rather than to no reaction at all.
            Scribe_Values.Look(
                ref personalShieldHitStyle,
                "personalShieldHitStyle",
                PersonalShieldHitStyle.Jitter);
            if (!System.Enum.IsDefined(
                    typeof(PersonalShieldHitStyle),
                    personalShieldHitStyle))
            {
                personalShieldHitStyle = PersonalShieldHitStyle.Jitter;
            }

            // On by default: the dust is vanilla noise around a belt-sized bubble,
            // and the custom surface has its own hit reaction.
            Scribe_Values.Look(
                ref personalShieldDisableHitDust,
                "personalShieldDisableHitDust",
                true);
            Scribe_Values.Look(
                ref personalShieldJitterDistance,
                "personalShieldJitterDistance",
                0.05f);
            Scribe_Values.Look(
                ref personalShieldJitterTicks,
                "personalShieldJitterTicks",
                8f);
            Scribe_Values.Look(
                ref personalShieldBrightenStrength,
                "personalShieldBrightenStrength",
                0.55f);
            Scribe_Values.Look(
                ref personalShieldBrightenAngle,
                "personalShieldBrightenAngle",
                30f);
            Scribe_Values.Look(
                ref personalShieldHexImpactStrength,
                "personalShieldHexImpactStrength",
                0.12f);
            Scribe_Values.Look(
                ref personalShieldHexImpactShare,
                "personalShieldHexImpactShare",
                0.4f);
            Scribe_Values.Look(
                ref largeShieldRidgeWidth,
                "largeShieldRidgeWidth",
                3.6f);
            Scribe_Values.Look(
                ref largeShieldRidgeBrightness,
                "largeShieldRidgeBrightness",
                0.52f);
            Scribe_Values.Look(
                ref personalShieldRidgeWidth,
                "personalShieldRidgeWidth",
                3.6f);
            Scribe_Values.Look(
                ref personalShieldRidgeBrightness,
                "personalShieldRidgeBrightness",
                0.52f);
            Scribe_Values.Look(ref shieldFlareAlpha, "shieldFlareAlpha", 0.30f);
            Scribe_Values.Look(
                ref shieldFlareDamageInfluence,
                "shieldFlareDamageInfluence",
                0.5f);
            Scribe_Values.Look(ref shieldFlareSizeScale, "shieldFlareSizeScale", 1f);
            Scribe_Values.Look(
                ref largeShieldDissolveTicks,
                "largeShieldDissolveTicks",
                60f);
            Scribe_Values.Look(
                ref personalShieldDissolveTicks,
                "personalShieldDissolveTicks",
                60f);
            Scribe_Values.Look(
                ref largeShieldDissolveBrightness,
                "largeShieldDissolveBrightness",
                0.6f);
            Scribe_Values.Look(
                ref personalShieldDissolveBrightness,
                "personalShieldDissolveBrightness",
                0.6f);
            Scribe_Values.Look(ref vacuumLeakEnabled, "vacuumLeakEnabled", true);
            Scribe_Values.Look(ref vacuumLeakIntensity, "vacuumLeakIntensity", 1f);
            Scribe_Values.Look(ref vacuumLeakSpeed, "vacuumLeakSpeed", 1f);
            Scribe_Values.Look(
                ref vacuumLeakDurationScale,
                "vacuumLeakDurationScale",
                1f);
            Scribe_Values.Look(ref vacuumLeakDistortion, "vacuumLeakDistortion", true);
            Scribe_Values.Look(
                ref vacuumLeakDistortionStrength,
                "vacuumLeakDistortionStrength",
                6f);
            Scribe_Values.Look(ref vacuumLeakTrace, "vacuumLeakTrace", false);
            Scribe_Values.Look(ref heatHazeEnabled, "heatHazeEnabled", true);
            Scribe_Values.Look(ref heatHazeHighFrequency, "heatHazeHighFrequency", false);
            Scribe_Values.Look(ref heatHazePerCellTemp, "heatHazePerCellTemp", false);
            Scribe_Values.Look(ref heatHazeStartTemp, "heatHazeStartTemp", 35f);
            Scribe_Values.Look(ref heatHazeFullTemp, "heatHazeFullTemp", 80f);
            Scribe_Values.Look(ref heatHazeIntensity, "heatHazeIntensity", 1f);
            Scribe_Values.Look(ref heatHazeScale, "heatHazeScale", 1f);
            Scribe_Values.Look(ref heatHazeSpeed, "heatHazeSpeed", 1f);
            Scribe_Values.Look(ref heatHazeAmplitude, "heatHazeAmplitude", 2.5f);
            Scribe_Values.Look(ref heatHazeFireBoost, "heatHazeFireBoost", 1f);
            Scribe_Values.Look(ref coldMistEnabled, "coldMistEnabled", true);
            Scribe_Values.Look(ref coldMistStartTemp, "coldMistStartTemp", -5f);
            Scribe_Values.Look(ref coldMistFullTemp, "coldMistFullTemp", -75f);
            Scribe_Values.Look(ref coldMistOpacity, "coldMistOpacity", 0.3f);
            Scribe_Values.Look(ref coldMistCoverage, "coldMistCoverage", 0.5f);
            Scribe_Values.Look(ref coldMistScale, "coldMistScale", 1f);
            Scribe_Values.Look(ref coldMistSpeed, "coldMistSpeed", 2f);

            // A save written while the experimental shader style existed can hold
            // style == 3, which no longer names anything.
            if (style < StyleComet || style > StyleParticle)
            {
                style = StyleParticle;
            }

            // Density used to span 0.5..2 with 1.0 as the default. It is now 0.01..1:
            // the upper end keeps the on-screen count readable, and the low end lets the
            // particle trail thin out to almost nothing.
            density = Mathf.Clamp(density, MinDensity, MaxDensity);
            speedLengthInfluence = Mathf.Clamp01(speedLengthInfluence);
            opacity = Mathf.Clamp(opacity, MinOpacity, MaxOpacity);
            particleLowWindThreshold = Mathf.Clamp(
                particleLowWindThreshold,
                0f,
                MaxLowWindThreshold);
            // The boil mode (2) was removed; a save that selected it falls back to the
            // recommended local-flow mode (the clamp does that mapping).
            gasFieldNoiseMode = Mathf.Clamp(
                gasFieldNoiseMode,
                GasNoiseAmbient,
                GasNoiseLocalFlow);
            gasFieldSmoothTau = Mathf.Clamp(gasFieldSmoothTau, MinSmoothTau, MaxSmoothTau);
            gasFieldOpacity = Mathf.Clamp(gasFieldOpacity, 0.2f, 3f);
            gasFieldScale = Mathf.Clamp(gasFieldScale, 0.5f, 2f);
            gasFieldAnimation = Mathf.Clamp(gasFieldAnimation, 0f, 2f);
            gasFieldBlur = Mathf.Clamp01(gasFieldBlur);
            gasFieldSharpen = Mathf.Clamp01(gasFieldSharpen);
            gasFieldFaint = Mathf.Clamp(gasFieldFaint, 0f, 2f);
            explosionRippleIntensity = Mathf.Clamp(
                explosionRippleIntensity,
                MinRippleIntensity,
                MaxRippleIntensity);
            explosionRippleRadiusScale = Mathf.Clamp(
                explosionRippleRadiusScale,
                MinRippleRadiusScale,
                MaxRippleRadiusScale);
            explosionRippleRadiusInfluence = Mathf.Clamp01(
                explosionRippleRadiusInfluence);
            explosionRippleDuration = Mathf.Clamp(
                explosionRippleDuration,
                MinRippleDuration,
                MaxRippleDuration);
            shieldRippleIntensity = Mathf.Clamp(
                shieldRippleIntensity,
                MinShieldRippleIntensity,
                MaxShieldRippleIntensity);
            shieldRippleDuration = Mathf.Clamp(
                shieldRippleDuration,
                MinShieldRippleDuration,
                MaxShieldRippleDuration);
            shieldRippleDamageScale = Mathf.Clamp(
                shieldRippleDamageScale,
                MinShieldRippleDamageScale,
                MaxShieldRippleDamageScale);
            largeShieldRidgeWidth = Mathf.Clamp(
                largeShieldRidgeWidth,
                MinShieldRidgeWidth,
                MaxShieldRidgeWidth);
            largeShieldRidgeBrightness = Mathf.Clamp01(largeShieldRidgeBrightness);
            personalShieldRidgeWidth = Mathf.Clamp(
                personalShieldRidgeWidth,
                MinShieldRidgeWidth,
                MaxShieldRidgeWidth);
            personalShieldRidgeBrightness = Mathf.Clamp01(personalShieldRidgeBrightness);
            shieldFlareAlpha = Mathf.Clamp(
                shieldFlareAlpha,
                MinShieldFlareAlpha,
                MaxShieldFlareAlpha);
            shieldFlareDamageInfluence = Mathf.Clamp01(shieldFlareDamageInfluence);
            shieldFlareSizeScale = Mathf.Clamp(
                shieldFlareSizeScale,
                MinShieldFlareSize,
                MaxShieldFlareSize);
            largeShieldHexFaceStrength = Mathf.Clamp(
                largeShieldHexFaceStrength,
                MinShieldHexFace,
                MaxShieldHexFace);
            largeShieldHexLineStrength = Mathf.Clamp(
                largeShieldHexLineStrength,
                MinShieldHexLine,
                MaxShieldHexLine);
            personalShieldHexFaceStrength = Mathf.Clamp(
                personalShieldHexFaceStrength,
                MinShieldHexFace,
                MaxShieldHexFace);
            personalShieldHexLineStrength = Mathf.Clamp(
                personalShieldHexLineStrength,
                MinShieldHexLine,
                MaxShieldHexLine);
            largeShieldCenterClear = Mathf.Clamp(
                largeShieldCenterClear,
                MinShieldCenterClear,
                MaxShieldCenterClear);
            personalShieldCenterClear = Mathf.Clamp(
                personalShieldCenterClear,
                MinShieldCenterClear,
                MaxShieldCenterClear);
            shieldHexDensity = Mathf.Clamp(
                shieldHexDensity,
                MinShieldHexDensity,
                MaxShieldHexDensity);
            shieldHexDensityApplied = Mathf.Clamp(
                shieldHexDensityApplied,
                (int)MinShieldHexDensity,
                (int)MaxShieldHexDensity);
            personalShieldHexDensity = Mathf.Clamp(
                personalShieldHexDensity,
                MinShieldHexDensity,
                MaxShieldHexDensity);
            personalShieldHexDensityApplied = Mathf.Clamp(
                personalShieldHexDensityApplied,
                (int)MinShieldHexDensity,
                (int)MaxShieldHexDensity);
            hexBubbleScanPeriod = Mathf.Clamp(
                hexBubbleScanPeriod,
                MinHexBubbleScanPeriod,
                MaxHexBubbleScanPeriod);
            hexBubbleFeather = Mathf.Clamp(
                hexBubbleFeather,
                MinHexBubbleFeather,
                MaxHexBubbleFeather);
            hexBubbleStripes = Mathf.Clamp(
                hexBubbleStripes,
                MinHexBubbleStripes,
                MaxHexBubbleStripes);
            hexBubbleBaseline = Mathf.Clamp(
                hexBubbleBaseline,
                MinHexBubbleBaseline,
                MaxHexBubbleBaseline);
            personalShieldOpacity = Mathf.Clamp(
                personalShieldOpacity,
                MinPersonalShieldOpacity,
                MaxPersonalShieldOpacity);
            repulsorShieldOpacity = Mathf.Clamp(
                repulsorShieldOpacity,
                MinRepulsorShieldOpacity,
                MaxRepulsorShieldOpacity);
            personalShieldJitterDistance = Mathf.Clamp(
                personalShieldJitterDistance,
                MinPersonalShieldJitterDistance,
                MaxPersonalShieldJitterDistance);
            personalShieldJitterTicks = Mathf.Clamp(
                personalShieldJitterTicks,
                MinPersonalShieldJitterTicks,
                MaxPersonalShieldJitterTicks);
            if (personalShieldHitStyle != PersonalShieldHitStyle.Jitter
                && personalShieldHitStyle != PersonalShieldHitStyle.Brighten
                && personalShieldHitStyle != PersonalShieldHitStyle.HexWave)
            {
                personalShieldHitStyle = PersonalShieldHitStyle.Jitter;
            }

            personalShieldBrightenStrength = Mathf.Clamp(
                personalShieldBrightenStrength,
                MinPersonalShieldBrightenStrength,
                MaxPersonalShieldBrightenStrength);
            personalShieldBrightenAngle = Mathf.Round(Mathf.Clamp(
                personalShieldBrightenAngle,
                MinPersonalShieldBrightenAngle,
                MaxPersonalShieldBrightenAngle));
            personalShieldHexImpactStrength = Mathf.Clamp(
                personalShieldHexImpactStrength,
                MinPersonalShieldHexImpactStrength,
                MaxPersonalShieldHexImpactStrength);
            personalShieldHexImpactShare = Mathf.Clamp(
                personalShieldHexImpactShare,
                MinPersonalShieldHexImpactShare,
                MaxPersonalShieldHexImpactShare);
            shieldHexImpactStrength = Mathf.Clamp(
                shieldHexImpactStrength,
                MinShieldHexImpactStrength,
                MaxShieldHexImpactStrength);
            shieldHexImpactShare = Mathf.Clamp(
                shieldHexImpactShare,
                MinShieldHexImpactShare,
                MaxShieldHexImpactShare);

            // The valid animation set is per surface, so a hand-edited or future
            // value falls back to that surface's default rather than to a reaction
            // it cannot draw.
            if (crispShieldImpactStyle != ShieldImpactStyle.Flare
                && crispShieldImpactStyle != ShieldImpactStyle.Brighten)
            {
                crispShieldImpactStyle = ShieldImpactStyle.Flare;
            }

            if (vanillaShieldImpactStyle != ShieldImpactStyle.Flare
                && vanillaShieldImpactStyle != ShieldImpactStyle.Brighten)
            {
                vanillaShieldImpactStyle = ShieldImpactStyle.Brighten;
            }

            if (hexShieldImpactStyle != ShieldImpactStyle.Brighten
                && hexShieldImpactStyle != ShieldImpactStyle.HexWave)
            {
                hexShieldImpactStyle = ShieldImpactStyle.HexWave;
            }

            shieldBrightenAngle = Mathf.Round(Mathf.Clamp(
                shieldBrightenAngle,
                MinShieldBrightenAngle,
                MaxShieldBrightenAngle));
            shieldImpactTicks = Mathf.Clamp(
                shieldImpactTicks,
                MinShieldImpactTicks,
                MaxShieldImpactTicks);

            // Same rule for the break choices. The shell additionally allows its own
            // panel-loss animation; the other surfaces only know vanilla or the
            // continuous tear.
            largeCrispShieldBreakStyle = SanitizeShieldBreakStyle(
                largeCrispShieldBreakStyle,
                ShieldBreakStyle.Dissolve,
                false);
            largeVanillaShieldBreakStyle = SanitizeShieldBreakStyle(
                largeVanillaShieldBreakStyle,
                ShieldBreakStyle.Dissolve,
                false);
            largeHexShieldBreakStyle = SanitizeShieldBreakStyle(
                largeHexShieldBreakStyle,
                ShieldBreakStyle.HexDissipate,
                true);
            personalCrispShieldBreakStyle = SanitizeShieldBreakStyle(
                personalCrispShieldBreakStyle,
                ShieldBreakStyle.Dissolve,
                false);
            personalVanillaShieldBreakStyle = SanitizeShieldBreakStyle(
                personalVanillaShieldBreakStyle,
                ShieldBreakStyle.Dissolve,
                false);
            personalHexShieldBreakStyle = SanitizeShieldBreakStyle(
                personalHexShieldBreakStyle,
                ShieldBreakStyle.HexDissipate,
                true);

            largeShieldRestoreStyle = SanitizeShieldRestoreStyle(
                largeShieldRestoreStyle);
            personalShieldRestoreStyle = SanitizeShieldRestoreStyle(
                personalShieldRestoreStyle);
            largeShieldRestoreTicks = Mathf.Round(Mathf.Clamp(
                largeShieldRestoreTicks,
                MinShieldRestoreTicks,
                MaxShieldRestoreTicks));
            personalShieldRestoreTicks = Mathf.Round(Mathf.Clamp(
                personalShieldRestoreTicks,
                MinShieldRestoreTicks,
                MaxShieldRestoreTicks));

            largeShieldDissolveTicks = Mathf.Clamp(
                largeShieldDissolveTicks,
                MinShieldDissolveTicks,
                MaxShieldDissolveTicks);
            personalShieldDissolveTicks = Mathf.Clamp(
                personalShieldDissolveTicks,
                MinShieldDissolveTicks,
                MaxShieldDissolveTicks);
            largeShieldDissolveBrightness = Mathf.Clamp(
                largeShieldDissolveBrightness,
                MinShieldDissolveBrightness,
                MaxShieldDissolveBrightness);
            personalShieldDissolveBrightness = Mathf.Clamp(
                personalShieldDissolveBrightness,
                MinShieldDissolveBrightness,
                MaxShieldDissolveBrightness);
            vacuumLeakIntensity = Mathf.Clamp(
                vacuumLeakIntensity,
                MinLeakIntensity,
                MaxLeakIntensity);
            vacuumLeakSpeed = Mathf.Clamp(vacuumLeakSpeed, MinLeakSpeed, MaxLeakSpeed);
            vacuumLeakDurationScale = Mathf.Clamp(
                vacuumLeakDurationScale,
                MinLeakDurationScale,
                MaxLeakDurationScale);
            vacuumLeakDistortionStrength = Mathf.Clamp(
                vacuumLeakDistortionStrength,
                MinLeakDistortion,
                MaxLeakDistortion);
            heatHazeStartTemp = Mathf.Clamp(
                heatHazeStartTemp,
                MinHeatStartTemp,
                MaxHeatStartTemp);
            heatHazeStartTemp = Mathf.Round(heatHazeStartTemp);
            heatHazeFullTemp = Mathf.Clamp(
                heatHazeFullTemp,
                MinHeatFullTemp,
                MaxHeatFullTemp);
            heatHazeFullTemp = Mathf.Round(heatHazeFullTemp);
            // A full-temperature value at or below the start value would make the
            // smoothstep degenerate; keep at least one degree of headroom.
            if (heatHazeFullTemp < heatHazeStartTemp + 1f)
            {
                heatHazeFullTemp = Mathf.Min(
                    MaxHeatFullTemp,
                    heatHazeStartTemp + 1f);
            }

            heatHazeIntensity = Mathf.Clamp(
                heatHazeIntensity,
                MinHeatIntensity,
                MaxHeatIntensity);
            heatHazeScale = Mathf.Clamp(
                heatHazeScale,
                MinHeatScale,
                MaxHeatScale);
            heatHazeSpeed = Mathf.Clamp(
                heatHazeSpeed,
                MinHeatSpeed,
                MaxHeatSpeed);
            heatHazeSpeed = Mathf.Round(heatHazeSpeed * 10f) / 10f;
            heatHazeAmplitude = Mathf.Clamp(
                heatHazeAmplitude,
                MinHeatAmplitude,
                MaxHeatAmplitude);
            heatHazeFireBoost = Mathf.Clamp(
                heatHazeFireBoost,
                MinHeatFireBoost,
                MaxHeatFireBoost);
            coldMistStartTemp = Mathf.Clamp(
                coldMistStartTemp,
                MinColdStartTemp,
                MaxColdStartTemp);
            coldMistStartTemp = Mathf.Round(coldMistStartTemp);
            coldMistFullTemp = Mathf.Clamp(
                coldMistFullTemp,
                MinColdFullTemp,
                MaxColdFullTemp);
            coldMistFullTemp = Mathf.Round(coldMistFullTemp);
            // The cold ramp interpolates from full up to start, so a full value at or
            // above the start value would make the smoothstep degenerate. Keep one
            // degree of headroom, mirroring the heat-haze clamp.
            if (coldMistFullTemp > coldMistStartTemp - 1f)
            {
                coldMistFullTemp = Mathf.Max(
                    MinColdFullTemp,
                    coldMistStartTemp - 1f);
            }

            coldMistOpacity = Mathf.Clamp(
                coldMistOpacity,
                MinColdOpacity,
                MaxColdOpacity);
            coldMistCoverage = Mathf.Clamp01(coldMistCoverage);
            coldMistScale = Mathf.Clamp(
                coldMistScale,
                MinColdScale,
                MaxColdScale);
            coldMistSpeed = Mathf.Clamp(
                coldMistSpeed,
                MinColdSpeed,
                MaxColdSpeed);
            coldMistSpeed = Mathf.Round(coldMistSpeed * 10f) / 10f;
        }

        /// <summary>
        /// Keeps a break choice inside what its surface can draw. Vanilla and Dissolve
        /// are valid everywhere; panel loss only exists on the shell.
        /// </summary>
        private static ShieldBreakStyle SanitizeShieldBreakStyle(
            ShieldBreakStyle value,
            ShieldBreakStyle fallback,
            bool allowHexDissipate)
        {
            if (value == ShieldBreakStyle.Vanilla
                || value == ShieldBreakStyle.Dissolve
                || (allowHexDissipate && value == ShieldBreakStyle.HexDissipate))
            {
                return value;
            }

            return fallback;
        }

        /// <summary>Restore choices are the same on both families.</summary>
        private static ShieldRestoreStyle SanitizeShieldRestoreStyle(
            ShieldRestoreStyle value)
        {
            if (value == ShieldRestoreStyle.None
                || value == ShieldRestoreStyle.Fade
                || value == ShieldRestoreStyle.Grow)
            {
                return value;
            }

            return ShieldRestoreStyle.Grow;
        }
    }
}
