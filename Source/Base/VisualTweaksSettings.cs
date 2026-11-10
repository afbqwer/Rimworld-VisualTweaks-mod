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
            vacuumLeakEnabled = true;
            vacuumLeakIntensity = 1f;
            vacuumLeakSpeed = 1f;
            vacuumLeakDurationScale = 1f;
            vacuumLeakDistortion = true;
            vacuumLeakDistortionStrength = 6f;
            vacuumLeakTrace = false;
            heatHazeEnabled = true;
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
