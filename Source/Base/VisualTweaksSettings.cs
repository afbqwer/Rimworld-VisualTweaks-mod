using UnityEngine;
using Verse;

namespace VisualTweaks
{
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
        public static float shieldRippleIntensity = 1.2f;

        public const float MinShieldRippleDuration = 0.08f;
        public const float MaxShieldRippleDuration = 1f;
        public static float shieldRippleDuration = 0.24f;

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
        public static bool alwaysShowShields = false;

        // Replaces vanilla's shield drawing entirely (see ShieldDomePatches and
        // PersonalShieldPatches): interceptor domes and personal shield belts both
        // get the analytic surface. On by default: unlike the switch above this is
        // not an addition to the stock look but a re-draw of it, and the point of the
        // mod is that the drawn radius matches the one the game blocks projectiles
        // with. A belt has no radius of its own, so it borrows vanilla's energy-driven
        // bubble size instead. Turn it off live to A/B against vanilla — both patches
        // re-read this every frame, so no restart and no save reload.
        public static bool crispShieldDome = true;

        // Which surface the replacement dome draws. Crisp is the original analytic
        // ring; Hex is the spherical panel shell; Vanilla is a soft, wide-gradient
        // reproduction of the stock ForceField read (rebuilt in the shader, never
        // extracted from the game). Belts have their own choice just below.
        //
        // An int rather than three bools: the three styles are mutually exclusive,
        // and with checkboxes two of them could be on at once. The old
        // hexShieldDome bool is still read below so an existing save keeps its
        // choice, then migrated into shieldDomeStyle.
        public const int ShieldDomeStyleCrisp = 0;
        public const int ShieldDomeStyleHex = 1;
        public const int ShieldDomeStyleVanilla = 2;

        public static int shieldDomeStyle = ShieldDomeStyleCrisp;

        // Belts choose their surface independently. They are a quarter of a dome's
        // diameter but are always shown at one steady level, so a style that reads
        // well on a 25-cell projector does not necessarily read well on a 1.2-cell
        // belt. Migrated from shieldDomeStyle for saves written before this existed.
        public static int personalShieldDomeStyle = ShieldDomeStyleCrisp;

        // Legacy save value; only read during ExposeData migration.
        private static bool hexShieldDome;

        /// <summary>True when the spherical panel shell is the chosen style.</summary>
        public static bool HexShieldDome => shieldDomeStyle == ShieldDomeStyleHex;

        /// <summary>True when the stock-like soft dome is the chosen style.</summary>
        public static bool VanillaShieldDome => shieldDomeStyle == ShieldDomeStyleVanilla;

        /// <summary>True when the belt's independently chosen surface is the shell.</summary>
        public static bool PersonalHexShieldDome =>
            personalShieldDomeStyle == ShieldDomeStyleHex;

        /// <summary>True when the belt's independently chosen surface is stock-like.</summary>
        public static bool PersonalVanillaShieldDome =>
            personalShieldDomeStyle == ShieldDomeStyleVanilla;

        /// <summary>True when either kind of shield needs a baked lattice.</summary>
        public static bool AnyHexShieldDome => HexShieldDome || PersonalHexShieldDome;

        /// <summary>The surface style chosen for one shield family.</summary>
        internal static int ShieldDomeStyleFor(ShieldHexTarget target) =>
            target == ShieldHexTarget.Personal
                ? personalShieldDomeStyle
                : shieldDomeStyle;

        /// <summary>True when this family's chosen surface samples the hex atlas.</summary>
        internal static bool IsHexShieldDome(ShieldHexTarget target) =>
            ShieldDomeStyleFor(target) == ShieldDomeStyleHex;

        /// <summary>True when this family's chosen surface is stock-like.</summary>
        internal static bool IsVanillaShieldDome(ShieldHexTarget target) =>
            ShieldDomeStyleFor(target) == ShieldDomeStyleVanilla;

        /// <summary>
        /// The break animation for one family. Break choices are stored per surface
        /// (crisp / stock-like / hex), so a belt that picks hex gets the same break
        /// choice the dome would get for that surface, while the two families can
        /// still use different surfaces.
        /// </summary>
        internal static int CurrentShieldBreakStyleFor(ShieldHexTarget target)
        {
            int style = ShieldDomeStyleFor(target);
            return style == ShieldDomeStyleHex ? hexShieldBreakStyle
                : style == ShieldDomeStyleVanilla ? vanillaShieldBreakStyle
                : crispShieldBreakStyle;
        }

        // How a hit is answered. This is independent of the dome surface, but the
        // valid choices differ: the panel shell has its own lattice wave, while the
        // crisp and stock-like surfaces can either paint a flare or brighten in a
        // sector. Each dome style therefore keeps its own choice, so switching the
        // surface and switching it back does not silently rewrite the animation.
        public const int ShieldImpactFlare = 0;
        public const int ShieldImpactBrighten = 1;
        public const int ShieldImpactHexWave = 2;

        public static int crispShieldImpactStyle = ShieldImpactFlare;
        public static int vanillaShieldImpactStyle = ShieldImpactBrighten;
        public static int hexShieldImpactStyle = ShieldImpactHexWave;

        /// <summary>The impact animation that belongs to the selected dome style.</summary>
        public static int CurrentShieldImpactStyle =>
            shieldDomeStyle == ShieldDomeStyleHex ? hexShieldImpactStyle
            : shieldDomeStyle == ShieldDomeStyleVanilla ? vanillaShieldImpactStyle
            : crispShieldImpactStyle;

        // Vanilla's brighten cone is 30 degrees total. It used to be a constant in
        // the renderer; it is a slider now because 30 is only the stock value, and
        // the reaction is also available on the two custom surfaces. The angle is
        // the full width, not the half-angle.
        public const float MinShieldBrightenAngle = 5f;
        public const float MaxShieldBrightenAngle = 90f;
        public static float shieldBrightenAngle = 30f;

        // How a shield breaks. Independent of the surface, with per-style choices:
        // Vanilla leaves the one-frame disappearance alone, Dissolve is the
        // continuous noise tear, HexDissipate loses whole panels on the shell. Each
        // surface keeps its own choice for the same reason the hit animations do.
        public const int ShieldBreakVanilla = 0;
        public const int ShieldBreakDissolve = 1;
        public const int ShieldBreakHexDissipate = 2;

        public static int crispShieldBreakStyle = ShieldBreakDissolve;
        public static int vanillaShieldBreakStyle = ShieldBreakDissolve;
        public static int hexShieldBreakStyle = ShieldBreakHexDissipate;

        /// <summary>The break animation that belongs to the selected dome style.</summary>
        public static int CurrentShieldBreakStyle =>
            shieldDomeStyle == ShieldDomeStyleHex ? hexShieldBreakStyle
            : shieldDomeStyle == ShieldDomeStyleVanilla ? vanillaShieldBreakStyle
            : crispShieldBreakStyle;

        public const float MinShieldHexFace = 0f;
        public const float MaxShieldHexFace = 0.6f;
        public static float shieldHexFaceStrength = 0.22f;

        public const float MinShieldHexLine = 0f;
        public const float MaxShieldHexLine = 1.2f;
        public static float shieldHexLineStrength = 0.75f;

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

        // Overall drawing opacity for belts. An interceptor dome's alpha comes from
        // GetCurrentAlpha(), which for a selected shield pulses between 0.2 and 0.62
        // and is much lower when idle; a belt has no such rule, and drawing it at
        // full alpha made it read far more solid than every dome around it. 0.4 is
        // the middle of that selected range and the default; independent of the dome
        // so the two can be tuned separately.
        public const float MinPersonalShieldOpacity = 0.05f;
        public const float MaxPersonalShieldOpacity = 1f;
        public static float personalShieldOpacity = 0.4f;

        // Personal belt hit jitter. This is vanilla's own absorption reaction: the
        // bubble is pushed along the shot and shrinks by the same amount for a few
        // ticks. It is deliberately NOT one of the dome's impact animations — a belt
        // is too small for a flare or a panel wave, and the two features should be
        // tunable without moving each other. Defaults are vanilla's numbers.
        public static bool personalShieldHitJitter = true;

        public const float MinPersonalShieldJitterDistance = 0f;
        public const float MaxPersonalShieldJitterDistance = 0.15f;
        public static float personalShieldJitterDistance = 0.05f;

        public const float MinPersonalShieldJitterTicks = 2f;
        public const float MaxPersonalShieldJitterTicks = 30f;
        public static float personalShieldJitterTicks = 8f;

        // The panel style answers a hit differently from the smooth one - a flash that
        // spreads across the lattice instead of a wedge painted on the surface - so it
        // gets its own strength and reach. Sharing the flare's slider was wrong twice
        // over: the two effects have no common scale (a lit tile is tiny next to a
        // wedge, so it needed an internal x1.6 fudge), and the flare's "size" is an
        // angle, which is meaningless for a lattice wave.
        public const float MinShieldHexImpactStrength = 0f;
        public const float MaxShieldHexImpactStrength = 0.8f;
        public static float shieldHexImpactStrength = 0.28f;

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
        public static float shieldRidgeWidth = 3.6f;

        public static float shieldRidgeBrightness = 0.52f;

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

        // Legacy save value: the old global dissolve switch. It is only read during
        // ExposeData migration now; the per-style break choices above decide what
        // actually plays. Vanilla's Shield_Break effecter (flash + sound) always
        // plays underneath either way.
        public static bool shieldDissolveEnabled = true;

        // In ticks. 60 ticks is one in-game second at normal speed; 90 gives the tear
        // time to cross a large dome without lingering into the recharge cycle.
        public const float MinShieldDissolveTicks = 30f;
        public const float MaxShieldDissolveTicks = 180f;
        public static float shieldDissolveTicks = 90f;

        // The dissolve has to supply its own brightness because GetCurrentAlpha() is
        // already zero by then. 0.9 is just below the intact dome's selected alpha
        // peak multiplied by the surge, so the break reads as a flare, not a dimming.
        public const float MinShieldDissolveBrightness = 0.2f;
        public const float MaxShieldDissolveBrightness = 1.5f;
        public static float shieldDissolveBrightness = 0.9f;

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
        public static float coldMistStartTemp = 0f;

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
            Scribe_Values.Look(ref shieldRippleIntensity, "shieldRippleIntensity", 1.2f);
            Scribe_Values.Look(ref shieldRippleDuration, "shieldRippleDuration", 0.24f);
            Scribe_Values.Look(
                ref shieldRippleDamageScale,
                "shieldRippleDamageScale",
                1f);
            Scribe_Values.Look(ref alwaysShowShields, "alwaysShowShields", false);
            Scribe_Values.Look(ref crispShieldDome, "crispShieldDome", true);

            // The old save only knows the boolean. -1 means the new key was absent,
            // so its value decides the migrated style; otherwise the int wins even
            // when the stale boolean disagrees.
            Scribe_Values.Look(ref hexShieldDome, "hexShieldDome", false);
            Scribe_Values.Look(ref shieldDomeStyle, "shieldDomeStyle", -1);
            if (shieldDomeStyle < ShieldDomeStyleCrisp
                || shieldDomeStyle > ShieldDomeStyleVanilla)
            {
                shieldDomeStyle = hexShieldDome
                    ? ShieldDomeStyleHex
                    : ShieldDomeStyleCrisp;
            }

            // Saves written before belts had their own surface simply keep whatever
            // the shared style was, so upgrading does not change how they look.
            // From then on the two are independent.
            Scribe_Values.Look(
                ref personalShieldDomeStyle,
                "personalShieldDomeStyle",
                -1);
            if (personalShieldDomeStyle < ShieldDomeStyleCrisp
                || personalShieldDomeStyle > ShieldDomeStyleVanilla)
            {
                personalShieldDomeStyle = shieldDomeStyle;
            }

            // One choice per surface. Saves written before this setting existed take
            // the defaults, which are exactly the reactions those styles had:
            // crisp = flare, stock-like = sector brightening, hex = lattice wave.
            Scribe_Values.Look(
                ref crispShieldImpactStyle,
                "crispShieldImpactStyle",
                ShieldImpactFlare);
            Scribe_Values.Look(
                ref vanillaShieldImpactStyle,
                "vanillaShieldImpactStyle",
                ShieldImpactBrighten);
            Scribe_Values.Look(
                ref hexShieldImpactStyle,
                "hexShieldImpactStyle",
                ShieldImpactHexWave);
            Scribe_Values.Look(ref shieldBrightenAngle, "shieldBrightenAngle", 30f);

            // The old global switch decides the migrated break choice: saves that had
            // it off start with Vanilla, saves that had it on keep the animation each
            // surface used before (continuous for crisp/stock, panels for hex). After
            // this read, only the per-style fields matter at runtime.
            Scribe_Values.Look(
                ref shieldDissolveEnabled,
                "shieldDissolveEnabled",
                true);
            Scribe_Values.Look(ref crispShieldBreakStyle, "crispShieldBreakStyle", -1);
            Scribe_Values.Look(ref vanillaShieldBreakStyle, "vanillaShieldBreakStyle", -1);
            Scribe_Values.Look(ref hexShieldBreakStyle, "hexShieldBreakStyle", -1);

            if (crispShieldBreakStyle < 0)
            {
                crispShieldBreakStyle = shieldDissolveEnabled
                    ? ShieldBreakDissolve
                    : ShieldBreakVanilla;
            }

            if (vanillaShieldBreakStyle < 0)
            {
                vanillaShieldBreakStyle = shieldDissolveEnabled
                    ? ShieldBreakDissolve
                    : ShieldBreakVanilla;
            }

            if (hexShieldBreakStyle < 0)
            {
                hexShieldBreakStyle = shieldDissolveEnabled
                    ? ShieldBreakHexDissipate
                    : ShieldBreakVanilla;
            }

            Scribe_Values.Look(
                ref shieldHexFaceStrength,
                "shieldHexFaceStrength",
                0.22f);
            Scribe_Values.Look(
                ref shieldHexLineStrength,
                "shieldHexLineStrength",
                0.75f);
            Scribe_Values.Look(ref shieldHexDensity, "shieldHexDensity", 5f);
            Scribe_Values.Look(
                ref shieldHexImpactStrength,
                "shieldHexImpactStrength",
                0.28f);
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
                ref personalShieldOpacity,
                "personalShieldOpacity",
                0.4f);
            Scribe_Values.Look(
                ref personalShieldHitJitter,
                "personalShieldHitJitter",
                true);
            Scribe_Values.Look(
                ref personalShieldJitterDistance,
                "personalShieldJitterDistance",
                0.05f);
            Scribe_Values.Look(
                ref personalShieldJitterTicks,
                "personalShieldJitterTicks",
                8f);
            Scribe_Values.Look(ref shieldRidgeWidth, "shieldRidgeWidth", 3.6f);
            Scribe_Values.Look(ref shieldRidgeBrightness, "shieldRidgeBrightness", 0.52f);
            Scribe_Values.Look(ref shieldFlareAlpha, "shieldFlareAlpha", 0.30f);
            Scribe_Values.Look(
                ref shieldFlareDamageInfluence,
                "shieldFlareDamageInfluence",
                0.5f);
            Scribe_Values.Look(ref shieldFlareSizeScale, "shieldFlareSizeScale", 1f);
            Scribe_Values.Look(ref shieldDissolveTicks, "shieldDissolveTicks", 90f);
            Scribe_Values.Look(
                ref shieldDissolveBrightness,
                "shieldDissolveBrightness",
                0.9f);
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
            Scribe_Values.Look(ref coldMistStartTemp, "coldMistStartTemp", 0f);
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
            shieldRidgeWidth = Mathf.Clamp(
                shieldRidgeWidth,
                MinShieldRidgeWidth,
                MaxShieldRidgeWidth);
            shieldRidgeBrightness = Mathf.Clamp01(shieldRidgeBrightness);
            shieldFlareAlpha = Mathf.Clamp(
                shieldFlareAlpha,
                MinShieldFlareAlpha,
                MaxShieldFlareAlpha);
            shieldFlareDamageInfluence = Mathf.Clamp01(shieldFlareDamageInfluence);
            shieldFlareSizeScale = Mathf.Clamp(
                shieldFlareSizeScale,
                MinShieldFlareSize,
                MaxShieldFlareSize);
            shieldHexFaceStrength = Mathf.Clamp(
                shieldHexFaceStrength,
                MinShieldHexFace,
                MaxShieldHexFace);
            shieldHexLineStrength = Mathf.Clamp(
                shieldHexLineStrength,
                MinShieldHexLine,
                MaxShieldHexLine);
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
            personalShieldOpacity = Mathf.Clamp(
                personalShieldOpacity,
                MinPersonalShieldOpacity,
                MaxPersonalShieldOpacity);
            personalShieldJitterDistance = Mathf.Clamp(
                personalShieldJitterDistance,
                MinPersonalShieldJitterDistance,
                MaxPersonalShieldJitterDistance);
            personalShieldJitterTicks = Mathf.Clamp(
                personalShieldJitterTicks,
                MinPersonalShieldJitterTicks,
                MaxPersonalShieldJitterTicks);
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
            if (crispShieldImpactStyle != ShieldImpactFlare
                && crispShieldImpactStyle != ShieldImpactBrighten)
            {
                crispShieldImpactStyle = ShieldImpactFlare;
            }

            if (vanillaShieldImpactStyle != ShieldImpactFlare
                && vanillaShieldImpactStyle != ShieldImpactBrighten)
            {
                vanillaShieldImpactStyle = ShieldImpactBrighten;
            }

            if (hexShieldImpactStyle != ShieldImpactBrighten
                && hexShieldImpactStyle != ShieldImpactHexWave)
            {
                hexShieldImpactStyle = ShieldImpactHexWave;
            }

            shieldBrightenAngle = Mathf.Round(Mathf.Clamp(
                shieldBrightenAngle,
                MinShieldBrightenAngle,
                MaxShieldBrightenAngle));

            // Same rule for the break choices. The shell additionally allows its own
            // panel-loss animation; the other surfaces only know vanilla or the
            // continuous tear.
            if (crispShieldBreakStyle != ShieldBreakVanilla
                && crispShieldBreakStyle != ShieldBreakDissolve)
            {
                crispShieldBreakStyle = ShieldBreakDissolve;
            }

            if (vanillaShieldBreakStyle != ShieldBreakVanilla
                && vanillaShieldBreakStyle != ShieldBreakDissolve)
            {
                vanillaShieldBreakStyle = ShieldBreakDissolve;
            }

            if (hexShieldBreakStyle != ShieldBreakVanilla
                && hexShieldBreakStyle != ShieldBreakDissolve
                && hexShieldBreakStyle != ShieldBreakHexDissipate)
            {
                hexShieldBreakStyle = ShieldBreakHexDissipate;
            }

            shieldDissolveTicks = Mathf.Clamp(
                shieldDissolveTicks,
                MinShieldDissolveTicks,
                MaxShieldDissolveTicks);
            shieldDissolveBrightness = Mathf.Clamp(
                shieldDissolveBrightness,
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

        public static void Reset()
        {
            replaceAORenderer = true;
            style = StyleParticle;
            showGuideLine = false;
            smoothField = true;
            density = 0.25f;
            streakLength = 1f;
            speedLengthInfluence = 1f;
            lineWidth = 1f;
            opacity = 0.5f;
            coloredParticles = true;
            animationSpeed = 1f;
            particleLowWindThreshold = 0f;
            sixtyFpsMode = true;
            gpuParticleGeometry = true;
            pauseWhenPaused = true;
            scaleSpeedWithGameSpeed = false;
            useGasField = true;
            gasFieldSmooth = true;
            gasFieldNoiseMode = GasNoiseLocalFlow;
            gasFieldSmoothTau = 0.15f;
            gasFieldTraceWindows = false;
            gasFieldDiagnostics = false;
            gasFieldOpacity = 1f;
            gasFieldScale = 1f;
            gasFieldAnimation = 1f;
            gasFieldBlur = 1f;
            gasFieldSharpen = 0f;
            gasFieldFaint = 1f;
            gasFieldCloudy = true;
            gasFieldReplaceDeadlife = true;
            explosionRippleEnabled = true;
            explosionRippleIntensity = 1f;
            explosionRippleRadiusScale = 1f;
            explosionRippleRadiusInfluence = 0.35f;
            explosionRippleDuration = RippleDurationBaseline;
            shieldRippleEnabled = true;
            shieldRippleIntensity = 1.2f;
            shieldRippleDuration = 0.24f;
            shieldRippleDamageScale = 1f;
            alwaysShowShields = false;
            crispShieldDome = true;
            shieldDomeStyle = ShieldDomeStyleCrisp;
            personalShieldDomeStyle = ShieldDomeStyleCrisp;
            hexShieldDome = false;
            crispShieldImpactStyle = ShieldImpactFlare;
            vanillaShieldImpactStyle = ShieldImpactBrighten;
            hexShieldImpactStyle = ShieldImpactHexWave;
            shieldBrightenAngle = 30f;
            crispShieldBreakStyle = ShieldBreakDissolve;
            vanillaShieldBreakStyle = ShieldBreakDissolve;
            hexShieldBreakStyle = ShieldBreakHexDissipate;
            shieldHexFaceStrength = 0.22f;
            shieldHexLineStrength = 0.75f;
            shieldHexDensity = 5f;
            shieldHexDensityApplied = 5;
            personalShieldHexDensity = 4f;
            personalShieldHexDensityApplied = 4;
            personalShieldOpacity = 0.4f;
            personalShieldHitJitter = true;
            personalShieldJitterDistance = 0.05f;
            personalShieldJitterTicks = 8f;
            shieldHexImpactStrength = 0.28f;
            shieldHexImpactShare = 0.32f;
            shieldRidgeWidth = 3.6f;
            shieldRidgeBrightness = 0.52f;
            shieldFlareAlpha = 0.30f;
            shieldFlareDamageInfluence = 0.5f;
            shieldFlareSizeScale = 1f;
            shieldDissolveEnabled = true;
            shieldDissolveTicks = 90f;
            shieldDissolveBrightness = 0.9f;
            vacuumLeakEnabled = true;
            vacuumLeakIntensity = 1f;
            vacuumLeakSpeed = 1f;
            vacuumLeakDurationScale = 1f;
            vacuumLeakDistortion = true;
            vacuumLeakDistortionStrength = 6f;
            vacuumLeakTrace = false;
            heatHazeEnabled = true;
            heatHazeHighFrequency = false;
            heatHazePerCellTemp = false;
            heatHazeStartTemp = 35f;
            heatHazeFullTemp = 80f;
            heatHazeIntensity = 1f;
            heatHazeScale = 1f;
            heatHazeSpeed = 1f;
            heatHazeAmplitude = 2.5f;
            heatHazeFireBoost = 1f;
            coldMistEnabled = true;
            coldMistStartTemp = 0f;
            coldMistFullTemp = -75f;
            coldMistOpacity = 0.1f;
            coldMistCoverage = 0.1f;
            coldMistScale = 1f;
            coldMistSpeed = 2f;
        }
    }
}
