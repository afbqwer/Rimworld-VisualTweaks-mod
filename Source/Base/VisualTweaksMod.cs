using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    public sealed class VisualTweaksMod : Mod
    {
        private enum SettingsTab
        {
            Wind = 0,
            Gas = 1,
            Ripple = 2,
            Heat = 3,
            Cold = 4,
            Leak = 5
        }

        private const int TabCount = 6;

        /// <summary>Inner margin of the tab body.</summary>
        private const float PageMargin = 16f;

        /// <summary>Reserved strip at the bottom for the reset button.</summary>
        private const float FooterHeight = 40f;

        private const float ResetButtonWidth = 160f;
        private const float ButtonHeight = 30f;

        /// <summary>Width kept free on the right of the content for the scrollbar.</summary>
        private const float ScrollbarWidth = 16f;

        private const float MinBodyHeight = 60f;

        // Display order of the wind styles. The style constants are not in visual
        // order (Comet = 0, Fine = 1, Particle = 2), so the radio row cannot just
        // index the setting.
        private static readonly int[] StyleOrder =
        {
            VisualTweaksSettings.StyleParticle,
            VisualTweaksSettings.StyleComet,
            VisualTweaksSettings.StyleFine
        };

        private readonly List<TabRecord> tabs = new List<TabRecord>(TabCount);
        private readonly Vector2[] tabScroll = new Vector2[TabCount];
        private readonly float[] tabContentHeight = new float[TabCount];

        // Kept as a field: DoSettingsWindowContents runs every frame while the dialog
        // is open, and a new listing per frame would be pure garbage.
        private readonly Listing_Standard listing = new Listing_Standard();

        private SettingsTab currentTab = SettingsTab.Wind;
        private bool tabsBuilt;
        private bool tabsIncludeWind;

        public VisualTweaksMod(ModContentPack content) : base(content)
        {
            // 创建设置实例并挂到 Mod.settings 上，RimWorld 由此读写设置文件。
            // 设置项本身是 VisualTweaksSettings 的静态字段，这里不需要保存引用。
            GetSettings<VisualTweaksSettings>();
        }

        public override string SettingsCategory()
        {
            return "VisualTweaks_SettingsCategory".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            EnsureTabs();

            // AO can be loaded after this window was last opened, and the wind tab
            // does not exist without it.
            if (currentTab == SettingsTab.Wind && !AOIntegration.StreamlineAvailable)
            {
                currentTab = SettingsTab.Gas;
            }

            Rect body = new Rect(inRect);
            //body.yMax -= FooterHeight;
            //DrawFooter(inRect);

            if (tabs.Count == 0)
            {
                return;
            }

            // TabDrawer paints the strip 32 px above the rect it is given, so the
            // section box itself has to start one strip lower.
            float bodyHeight = Mathf.Max(MinBodyHeight, body.height - TabDrawer.TabHeight);
            Rect sectionRect = new Rect(
                body.x,
                body.yMax - bodyHeight,
                body.width,
                bodyHeight);

            Widgets.DrawMenuSection(sectionRect);
            TabDrawer.DrawTabs(sectionRect, tabs);

            DrawTabBody(sectionRect.ContractedBy(PageMargin));
        }

        /// <summary>
        /// Tabs are rebuilt only when the set of visible tabs changes; building them
        /// every frame would allocate a TabRecord and three closures per frame.
        /// </summary>
        private void EnsureTabs()
        {
            bool wind = AOIntegration.StreamlineAvailable;
            if (tabsBuilt && wind == tabsIncludeWind)
            {
                return;
            }

            tabsBuilt = true;
            tabsIncludeWind = wind;
            tabs.Clear();

            // Without AO the streamline layer does not exist at all, so a tab full of
            // controls that configure nothing would be worse than no tab.
            if (wind)
            {
                tabs.Add(new TabRecord(
                    "VisualTweaks_Tab_Wind".Translate(),
                    delegate { currentTab = SettingsTab.Wind; },
                    () => currentTab == SettingsTab.Wind));
            }

            tabs.Add(new TabRecord(
                "VisualTweaks_Tab_Gas".Translate(),
                delegate { currentTab = SettingsTab.Gas; },
                () => currentTab == SettingsTab.Gas));

            tabs.Add(new TabRecord(
                "VisualTweaks_Tab_Ripple".Translate(),
                delegate { currentTab = SettingsTab.Ripple; },
                () => currentTab == SettingsTab.Ripple));

            tabs.Add(new TabRecord(
                "VisualTweaks_Tab_Heat".Translate(),
                delegate { currentTab = SettingsTab.Heat; },
                () => currentTab == SettingsTab.Heat));

            tabs.Add(new TabRecord(
                "VisualTweaks_Tab_Cold".Translate(),
                delegate { currentTab = SettingsTab.Cold; },
                () => currentTab == SettingsTab.Cold));

            tabs.Add(new TabRecord(
                "VisualTweaks_Tab_Leak".Translate(),
                delegate { currentTab = SettingsTab.Leak; },
                () => currentTab == SettingsTab.Leak));
        }

        private void DrawFooter(Rect inRect)
        {
            Rect footer = new Rect(
                inRect.x,
                inRect.yMax - FooterHeight + 6f,
                inRect.width,
                FooterHeight - 6f);

            // The dialog carries no version anywhere else, and "which build am I on"
            // is the first question when a setting looks wrong.
            string version = Content?.ModMetaData?.ModVersion ?? string.Empty;
            if (!version.NullOrEmpty())
            {
                TextAnchor anchor = Text.Anchor;
                Color color = GUI.color;
                Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = new Color(0.6f, 0.6f, 0.6f);
                Widgets.Label(footer, "VisualTweaks_Version".Translate(version));
                GUI.color = color;
                Text.Anchor = anchor;
            }

            Rect button = new Rect(
                footer.xMax - ResetButtonWidth,
                footer.y + (footer.height - ButtonHeight) / 2f,
                ResetButtonWidth,
                ButtonHeight);

            if (Widgets.ButtonText(button, "VisualTweaks_Reset".Translate()))
            {
                VisualTweaksSettings.Reset();
            }

        }

        private void DrawTabBody(Rect outRect)
        {
            int index = (int)currentTab;

            Rect viewRect = new Rect(
                0f,
                0f,
                outRect.width - ScrollbarWidth,
                Mathf.Max(tabContentHeight[index], outRect.height));

            Widgets.BeginScrollView(outRect, ref tabScroll[index], viewRect);

            // Listing.GetRect calls NewColumnIfNeeded, which on overflow would move the
            // rest of the controls into a second column outside viewRect and reset
            // CurHeight, breaking both the layout and the measured scroll height.
            listing.maxOneColumn = true;
            listing.Begin(viewRect);

            switch (currentTab)
            {
                case SettingsTab.Wind:
                    DoWindTab(listing);
                    break;
                case SettingsTab.Gas:
                    DoGasTab(listing);
                    break;
                case SettingsTab.Ripple:
                    DoRippleTab(listing);
                    break;
                case SettingsTab.Heat:
                    DoHeatTab(listing);
                    break;
                case SettingsTab.Cold:
                    DoColdTab(listing);
                    break;
                case SettingsTab.Leak:
                    DoLeakTab(listing);
                    break;
            }

            listing.End();

            // Measured during Layout so the next frame's viewRect has the right height.
            if (Event.current.type == EventType.Layout)
            {
                tabContentHeight[index] = listing.CurHeight;
            }

            Widgets.EndScrollView();
        }

        private static void DoWindTab(Listing_Standard listing)
        {
            SettingsLayout.Header(listing, "VisualTweaks_Wind_GroupRender".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Wind_ReplaceRenderer".Translate(),
                ref VisualTweaksSettings.replaceAORenderer,
                "VisualTweaks_Wind_ReplaceRendererTip".Translate());
            SettingsLayout.Note(listing, "VisualTweaks_Wind_AOButtonHint".Translate());

            SettingsLayout.Header(listing, "VisualTweaks_Wind_GroupStyle".Translate());
            int current = StyleIndexOf(VisualTweaksSettings.style);
            int picked = SettingsLayout.RadioRow(listing, StyleLabels(), current);
            if (picked != current)
            {
                VisualTweaksSettings.style = StyleOrder[picked];
            }

            listing.CheckboxLabeled(
                "VisualTweaks_Wind_ShowGuideLine".Translate(),
                ref VisualTweaksSettings.showGuideLine,
                "VisualTweaks_Wind_ShowGuideLineTip".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Wind_SmoothField".Translate(),
                ref VisualTweaksSettings.smoothField,
                "VisualTweaks_Wind_SmoothFieldTip".Translate());

            SettingsLayout.Header(listing, "VisualTweaks_Wind_GroupLook".Translate());
            VisualTweaksSettings.density = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Wind_Density".Translate(),
                VisualTweaksSettings.density,
                VisualTweaksSettings.MinDensity,
                VisualTweaksSettings.MaxDensity,
                "VisualTweaks_Wind_DensityTip".Translate());
            VisualTweaksSettings.streakLength = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Wind_StreakLength".Translate(),
                VisualTweaksSettings.streakLength,
                0.5f,
                2f);
            VisualTweaksSettings.speedLengthInfluence = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Wind_SpeedLengthInfluence".Translate(),
                VisualTweaksSettings.speedLengthInfluence,
                0f,
                1f,
                "VisualTweaks_Wind_SpeedLengthInfluenceTip".Translate());
            VisualTweaksSettings.particleLowWindThreshold = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Wind_LowWindThreshold".Translate(),
                VisualTweaksSettings.particleLowWindThreshold,
                0f,
                VisualTweaksSettings.MaxLowWindThreshold,
                "VisualTweaks_Wind_LowWindThresholdTip".Translate());
            VisualTweaksSettings.lineWidth = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Wind_LineWidth".Translate(),
                VisualTweaksSettings.lineWidth,
                0.5f,
                2f);
            VisualTweaksSettings.opacity = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Wind_Opacity".Translate(),
                VisualTweaksSettings.opacity,
                VisualTweaksSettings.MinOpacity,
                VisualTweaksSettings.MaxOpacity);
            listing.CheckboxLabeled(
                "VisualTweaks_Wind_ColoredParticles".Translate(),
                ref VisualTweaksSettings.coloredParticles,
                "VisualTweaks_Wind_ColoredParticlesTip".Translate());

            SettingsLayout.Header(listing, "VisualTweaks_Wind_GroupMotion".Translate());
            VisualTweaksSettings.animationSpeed = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Wind_AnimationSpeed".Translate(),
                VisualTweaksSettings.animationSpeed,
                0f,
                2f);
            listing.CheckboxLabeled(
                "VisualTweaks_Wind_SixtyFps".Translate(),
                ref VisualTweaksSettings.sixtyFpsMode,
                "VisualTweaks_Wind_SixtyFpsTip".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Wind_GpuParticles".Translate(),
                ref VisualTweaksSettings.gpuParticleGeometry,
                "VisualTweaks_Wind_GpuParticlesTip".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Wind_PauseWhenPaused".Translate(),
                ref VisualTweaksSettings.pauseWhenPaused,
                "VisualTweaks_Wind_PauseWhenPausedTip".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Wind_ScaleSpeedWithGameSpeed".Translate(),
                ref VisualTweaksSettings.scaleSpeedWithGameSpeed,
                "VisualTweaks_Wind_ScaleSpeedWithGameSpeedTip".Translate());

            SettingsLayout.Finish(listing);
        }

        private static int StyleIndexOf(int style)
        {
            for (int i = 0; i < StyleOrder.Length; i++)
            {
                if (StyleOrder[i] == style)
                {
                    return i;
                }
            }

            return 0;
        }

        private static string[] StyleLabels()
        {
            return new string[]
            {
                "VisualTweaks_Wind_StyleParticle".Translate(),
                "VisualTweaks_Wind_StyleComet".Translate(),
                "VisualTweaks_Wind_StyleFine".Translate()
            };
        }

        private static void DoGasTab(Listing_Standard listing)
        {
            SettingsLayout.Header(listing, "VisualTweaks_Gas_GroupSwitch".Translate());
            listing.CheckboxLabeled(
                (AOIntegration.StreamlineAvailable
                    ? "VisualTweaks_Gas_ReplaceAOSprites"
                    : "VisualTweaks_Gas_ReplaceVanillaSprites").Translate(),
                ref VisualTweaksSettings.useGasField,
                (AOIntegration.StreamlineAvailable
                    ? "VisualTweaks_Gas_ReplaceTipWithAO"
                    : "VisualTweaks_Gas_ReplaceTipVanilla").Translate());

            if (!GasFieldBridge.ShaderReady)
            {
                SettingsLayout.Warning(listing, "VisualTweaks_Gas_ShaderMissing".Translate());
            }

            SettingsLayout.Header(listing, "VisualTweaks_Gas_GroupMotion".Translate());

            // Local flow needs AO's per-cell wind snapshot. Without it the renderer
            // always falls back to ambient drift, so the whole choice is hidden rather
            // than offering a switch that can never take effect.
            if (AOIntegration.StreamlineAvailable)
            {
                bool localFlow = VisualTweaksSettings.gasFieldNoiseMode
                    == VisualTweaksSettings.GasNoiseLocalFlow;
                listing.CheckboxLabeled(
                    "VisualTweaks_Gas_LocalFlow".Translate(),
                    ref localFlow,
                    "VisualTweaks_Gas_LocalFlowTip".Translate());
                VisualTweaksSettings.gasFieldNoiseMode = localFlow
                    ? VisualTweaksSettings.GasNoiseLocalFlow
                    : VisualTweaksSettings.GasNoiseAmbient;
            }

            listing.CheckboxLabeled(
                "VisualTweaks_Gas_Smooth".Translate(),
                ref VisualTweaksSettings.gasFieldSmooth,
                "VisualTweaks_Gas_SmoothTip".Translate());
            VisualTweaksSettings.gasFieldSmoothTau = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Gas_SmoothTau".Translate(),
                VisualTweaksSettings.gasFieldSmoothTau,
                VisualTweaksSettings.MinSmoothTau,
                VisualTweaksSettings.MaxSmoothTau,
                "VisualTweaks_Gas_SmoothTauTip".Translate());
            VisualTweaksSettings.gasFieldAnimation = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Gas_Animation".Translate(),
                VisualTweaksSettings.gasFieldAnimation,
                0f,
                2f,
                (AOIntegration.StreamlineAvailable
                    ? "VisualTweaks_Gas_AnimationTipWithAO"
                    : "VisualTweaks_Gas_AnimationTipVanilla").Translate());

            SettingsLayout.Header(listing, "VisualTweaks_Gas_GroupLook".Translate());

            // The two presets only move the four appearance parameters below; every
            // other gas setting keeps whatever the user picked. Labelled because a
            // pair of buttons that silently rewrites sliders needs a caption.
            listing.Label("VisualTweaks_Gas_Presets".Translate());
            int preset = SettingsLayout.ButtonRow(
                listing,
                new string[]
                {
                    "VisualTweaks_Gas_PresetSoft".Translate(),
                    "VisualTweaks_Gas_PresetSharp".Translate()
                });

            if (preset == 0)
            {
                VisualTweaksSettings.gasFieldScale = 1f;
                VisualTweaksSettings.gasFieldBlur = 1f;
                VisualTweaksSettings.gasFieldSharpen = 0f;
                VisualTweaksSettings.gasFieldFaint = 1f;
            }
            else if (preset == 1)
            {
                VisualTweaksSettings.gasFieldScale = 0.5f;
                VisualTweaksSettings.gasFieldBlur = 0f;
                VisualTweaksSettings.gasFieldSharpen = 1f;
                VisualTweaksSettings.gasFieldFaint = 1.5f;
            }

            VisualTweaksSettings.gasFieldScale = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Gas_Scale".Translate(),
                VisualTweaksSettings.gasFieldScale,
                0.5f,
                2f,
                "VisualTweaks_Gas_ScaleTip".Translate());
            VisualTweaksSettings.gasFieldBlur = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Gas_Blur".Translate(),
                VisualTweaksSettings.gasFieldBlur,
                0f,
                1f,
                "VisualTweaks_Gas_BlurTip".Translate());
            VisualTweaksSettings.gasFieldSharpen = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Gas_Sharpen".Translate(),
                VisualTweaksSettings.gasFieldSharpen,
                0f,
                1f,
                "VisualTweaks_Gas_SharpenTip".Translate());
            VisualTweaksSettings.gasFieldFaint = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Gas_Faint".Translate(),
                VisualTweaksSettings.gasFieldFaint,
                0f,
                2f,
                "VisualTweaks_Gas_FaintTip".Translate());
            VisualTweaksSettings.gasFieldOpacity = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Gas_Opacity".Translate(),
                VisualTweaksSettings.gasFieldOpacity,
                0.2f,
                3f,
                "VisualTweaks_Gas_OpacityTip".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Gas_Cloudy".Translate(),
                ref VisualTweaksSettings.gasFieldCloudy,
                "VisualTweaks_Gas_CloudyTip".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Gas_ReplaceDeadlife".Translate(),
                ref VisualTweaksSettings.gasFieldReplaceDeadlife,
                "VisualTweaks_Gas_ReplaceDeadlifeTip".Translate());

            SettingsLayout.Header(listing, "VisualTweaks_Gas_GroupDebug".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Gas_TraceWindows".Translate(),
                ref VisualTweaksSettings.gasFieldTraceWindows,
                "VisualTweaks_Gas_TraceWindowsTip".Translate());

            SettingsLayout.Finish(listing);
        }

        private static void DoRippleTab(Listing_Standard listing)
        {
            SettingsLayout.Header(listing, "VisualTweaks_Ripple_GroupSwitch".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Ripple_Enable".Translate(),
                ref VisualTweaksSettings.explosionRippleEnabled,
                "VisualTweaks_Ripple_EnableTip".Translate());

            if (!ExplosionRippleBridge.ShaderReady)
            {
                SettingsLayout.Warning(listing, "VisualTweaks_Ripple_ShaderMissing".Translate());
            }

            SettingsLayout.Header(listing, "VisualTweaks_Ripple_GroupLook".Translate());
            VisualTweaksSettings.explosionRippleIntensity = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Ripple_Intensity".Translate(),
                VisualTweaksSettings.explosionRippleIntensity,
                VisualTweaksSettings.MinRippleIntensity,
                VisualTweaksSettings.MaxRippleIntensity,
                "VisualTweaks_Ripple_IntensityTip".Translate());
            VisualTweaksSettings.explosionRippleRadiusScale = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Ripple_RadiusScale".Translate(),
                VisualTweaksSettings.explosionRippleRadiusScale,
                VisualTweaksSettings.MinRippleRadiusScale,
                VisualTweaksSettings.MaxRippleRadiusScale,
                "VisualTweaks_Ripple_RadiusScaleTip".Translate());
            VisualTweaksSettings.explosionRippleRadiusInfluence = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Ripple_RadiusInfluence".Translate(),
                VisualTweaksSettings.explosionRippleRadiusInfluence,
                0f,
                1f,
                "VisualTweaks_Ripple_RadiusInfluenceTip".Translate());
            VisualTweaksSettings.explosionRippleDuration = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Ripple_Duration".Translate(),
                VisualTweaksSettings.explosionRippleDuration,
                VisualTweaksSettings.MinRippleDuration,
                VisualTweaksSettings.MaxRippleDuration,
                "VisualTweaks_Ripple_DurationTip".Translate());

            SettingsLayout.Finish(listing);
        }

        private static void DoHeatTab(Listing_Standard listing)
        {
            SettingsLayout.Header(listing, "VisualTweaks_Heat_GroupSwitch".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Heat_Enable".Translate(),
                ref VisualTweaksSettings.heatHazeEnabled,
                "VisualTweaks_Heat_EnableTip".Translate());

            if (!HeatHazeBridge.ShaderReady)
            {
                SettingsLayout.Warning(listing, "VisualTweaks_Heat_ShaderMissing".Translate());
            }

            SettingsLayout.Header(listing, "VisualTweaks_Heat_GroupLook".Translate());
            VisualTweaksSettings.heatHazeStartTemp = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Heat_StartTemp".Translate(),
                VisualTweaksSettings.heatHazeStartTemp,
                VisualTweaksSettings.MinHeatStartTemp,
                VisualTweaksSettings.MaxHeatStartTemp,
                "VisualTweaks_Heat_StartTempTip".Translate(),
                step: 1f);
            VisualTweaksSettings.heatHazeFullTemp = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Heat_FullTemp".Translate(),
                VisualTweaksSettings.heatHazeFullTemp,
                VisualTweaksSettings.MinHeatFullTemp,
                VisualTweaksSettings.MaxHeatFullTemp,
                "VisualTweaks_Heat_FullTempTip".Translate(),
                step: 1f);
            if (VisualTweaksSettings.heatHazeFullTemp < VisualTweaksSettings.heatHazeStartTemp + 1f)
            {
                VisualTweaksSettings.heatHazeFullTemp = Mathf.Min(
                    VisualTweaksSettings.MaxHeatFullTemp,
                    VisualTweaksSettings.heatHazeStartTemp + 1f);
            }
            VisualTweaksSettings.heatHazeIntensity = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Heat_Intensity".Translate(),
                VisualTweaksSettings.heatHazeIntensity,
                VisualTweaksSettings.MinHeatIntensity,
                VisualTweaksSettings.MaxHeatIntensity,
                "VisualTweaks_Heat_IntensityTip".Translate());
            VisualTweaksSettings.heatHazeAmplitude = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Heat_Amplitude".Translate(),
                VisualTweaksSettings.heatHazeAmplitude,
                VisualTweaksSettings.MinHeatAmplitude,
                VisualTweaksSettings.MaxHeatAmplitude,
                "VisualTweaks_Heat_AmplitudeTip".Translate());
            VisualTweaksSettings.heatHazeScale = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Heat_Scale".Translate(),
                VisualTweaksSettings.heatHazeScale,
                VisualTweaksSettings.MinHeatScale,
                VisualTweaksSettings.MaxHeatScale,
                "VisualTweaks_Heat_ScaleTip".Translate());
            VisualTweaksSettings.heatHazeSpeed = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Heat_Speed".Translate(),
                VisualTweaksSettings.heatHazeSpeed,
                VisualTweaksSettings.MinHeatSpeed,
                VisualTweaksSettings.MaxHeatSpeed,
                "VisualTweaks_Heat_SpeedTip".Translate(),
                step: 0.1f);
            VisualTweaksSettings.heatHazeFireBoost = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Heat_FireBoost".Translate(),
                VisualTweaksSettings.heatHazeFireBoost,
                VisualTweaksSettings.MinHeatFireBoost,
                VisualTweaksSettings.MaxHeatFireBoost,
                "VisualTweaks_Heat_FireBoostTip".Translate());

            SettingsLayout.Finish(listing);
        }

        private static void DoColdTab(Listing_Standard listing)
        {
            SettingsLayout.Header(listing, "VisualTweaks_Cold_GroupSwitch".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Cold_Enable".Translate(),
                ref VisualTweaksSettings.coldMistEnabled,
                "VisualTweaks_Cold_EnableTip".Translate());

            if (!ColdMistBridge.ShaderReady)
            {
                SettingsLayout.Warning(listing, "VisualTweaks_Cold_ShaderMissing".Translate());
            }

            SettingsLayout.Header(listing, "VisualTweaks_Cold_GroupLook".Translate());
            VisualTweaksSettings.coldMistStartTemp = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Cold_StartTemp".Translate(),
                VisualTweaksSettings.coldMistStartTemp,
                VisualTweaksSettings.MinColdStartTemp,
                VisualTweaksSettings.MaxColdStartTemp,
                "VisualTweaks_Cold_StartTempTip".Translate(),
                step: 1f);
            VisualTweaksSettings.coldMistFullTemp = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Cold_FullTemp".Translate(),
                VisualTweaksSettings.coldMistFullTemp,
                VisualTweaksSettings.MinColdFullTemp,
                VisualTweaksSettings.MaxColdFullTemp,
                "VisualTweaks_Cold_FullTempTip".Translate(),
                step: 1f);

            // The cold ramp interpolates from full up to start; an inverted or equal
            // pair would make the shader's smoothstep degenerate.
            if (VisualTweaksSettings.coldMistFullTemp
                > VisualTweaksSettings.coldMistStartTemp - 1f)
            {
                VisualTweaksSettings.coldMistFullTemp = Mathf.Max(
                    VisualTweaksSettings.MinColdFullTemp,
                    VisualTweaksSettings.coldMistStartTemp - 1f);
            }

            VisualTweaksSettings.coldMistOpacity = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Cold_Opacity".Translate(),
                VisualTweaksSettings.coldMistOpacity,
                VisualTweaksSettings.MinColdOpacity,
                VisualTweaksSettings.MaxColdOpacity,
                "VisualTweaks_Cold_OpacityTip".Translate());
            VisualTweaksSettings.coldMistCoverage = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Cold_Coverage".Translate(),
                VisualTweaksSettings.coldMistCoverage,
                VisualTweaksSettings.MinColdCoverage,
                VisualTweaksSettings.MaxColdCoverage,
                "VisualTweaks_Cold_CoverageTip".Translate());
            VisualTweaksSettings.coldMistScale = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Cold_Scale".Translate(),
                VisualTweaksSettings.coldMistScale,
                VisualTweaksSettings.MinColdScale,
                VisualTweaksSettings.MaxColdScale,
                "VisualTweaks_Cold_ScaleTip".Translate());
            VisualTweaksSettings.coldMistSpeed = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Cold_Speed".Translate(),
                VisualTweaksSettings.coldMistSpeed,
                VisualTweaksSettings.MinColdSpeed,
                VisualTweaksSettings.MaxColdSpeed,
                "VisualTweaks_Cold_SpeedTip".Translate(),
                step: 0.1f);

            SettingsLayout.Finish(listing);
        }

        private static void DoLeakTab(Listing_Standard listing)
        {
            SettingsLayout.Header(listing, "VisualTweaks_Leak_GroupSwitch".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Leak_Enable".Translate(),
                ref VisualTweaksSettings.vacuumLeakEnabled,
                "VisualTweaks_Leak_EnableTip".Translate());

            if (!VacuumLeakBridge.ShaderReady)
            {
                SettingsLayout.Warning(listing, "VisualTweaks_Leak_ShaderMissing".Translate());
            }

            SettingsLayout.Header(listing, "VisualTweaks_Leak_GroupLook".Translate());
            VisualTweaksSettings.vacuumLeakIntensity = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Leak_Intensity".Translate(),
                VisualTweaksSettings.vacuumLeakIntensity,
                VisualTweaksSettings.MinLeakIntensity,
                VisualTweaksSettings.MaxLeakIntensity,
                "VisualTweaks_Leak_IntensityTip".Translate());
            VisualTweaksSettings.vacuumLeakSpeed = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Leak_Speed".Translate(),
                VisualTweaksSettings.vacuumLeakSpeed,
                VisualTweaksSettings.MinLeakSpeed,
                VisualTweaksSettings.MaxLeakSpeed,
                "VisualTweaks_Leak_SpeedTip".Translate());
            VisualTweaksSettings.vacuumLeakDurationScale = SettingsLayout.Slider(
                listing,
                "VisualTweaks_Leak_DurationScale".Translate(),
                VisualTweaksSettings.vacuumLeakDurationScale,
                VisualTweaksSettings.MinLeakDurationScale,
                VisualTweaksSettings.MaxLeakDurationScale,
                "VisualTweaks_Leak_DurationScaleTip".Translate(),
                step: 0.05f);

            listing.CheckboxLabeled(
                "VisualTweaks_Leak_Distortion".Translate(),
                ref VisualTweaksSettings.vacuumLeakDistortion,
                "VisualTweaks_Leak_DistortionTip".Translate());

            if (VisualTweaksSettings.vacuumLeakDistortion)
            {
                VisualTweaksSettings.vacuumLeakDistortionStrength = SettingsLayout.Slider(
                    listing,
                    "VisualTweaks_Leak_DistortionStrength".Translate(),
                    VisualTweaksSettings.vacuumLeakDistortionStrength,
                    VisualTweaksSettings.MinLeakDistortion,
                    VisualTweaksSettings.MaxLeakDistortion,
                    "VisualTweaks_Leak_DistortionStrengthTip".Translate(),
                    step: 1f);
            }

            SettingsLayout.Header(listing, "VisualTweaks_Leak_GroupDebug".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Leak_Trace".Translate(),
                ref VisualTweaksSettings.vacuumLeakTrace,
                "VisualTweaks_Leak_TraceTip".Translate());

            SettingsLayout.Finish(listing);
        }
    }
}
