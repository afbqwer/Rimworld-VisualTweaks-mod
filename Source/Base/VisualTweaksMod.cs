using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    public sealed class VisualTweaksMod : Mod
    {
        // Declaration order is the order of the tab strip. Wind is last because it is
        // the only optional tab, so AO being missing never shifts the other tabs.
        private enum SettingsTab
        {
            Gas = 0,
            Ripple = 1,
            Temperature = 2,
            Shield = 3,
            Wind = 4
        }

        private const int TabCount = 5;

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

        private SettingsTab currentTab = SettingsTab.Gas;
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

            tabs.Add(new TabRecord(
                "VisualTweaks_Tab_Gas".Translate(),
                delegate { currentTab = SettingsTab.Gas; },
                () => currentTab == SettingsTab.Gas));

            tabs.Add(new TabRecord(
                "VisualTweaks_Tab_Ripple".Translate(),
                delegate { currentTab = SettingsTab.Ripple; },
                () => currentTab == SettingsTab.Ripple));

            // Heat and cold share one tab: they are two ends of the same dial and each
            // held only a handful of rows, so a separate tab for each cost a slot in the
            // tab strip for very little content. The section headers keep them apart.
            tabs.Add(new TabRecord(
                "VisualTweaks_Tab_Temperature".Translate(),
                delegate { currentTab = SettingsTab.Temperature; },
                () => currentTab == SettingsTab.Temperature));

            // Every shield visual lives on this tab: impact ripple, stock display and
            // the custom dome. They used to sit under the ripple tab only because the
            // ripple shader is involved, which buried most of the shield controls.
            tabs.Add(new TabRecord(
                "VisualTweaks_Tab_Shield".Translate(),
                delegate { currentTab = SettingsTab.Shield; },
                () => currentTab == SettingsTab.Shield));

            // Without AO the streamline layer does not exist at all, so a tab full of
            // controls that configure nothing would be worse than no tab.
            if (wind)
            {
                tabs.Add(new TabRecord(
                    "VisualTweaks_Tab_Wind".Translate(),
                    delegate { currentTab = SettingsTab.Wind; },
                    () => currentTab == SettingsTab.Wind));
            }
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
                case SettingsTab.Temperature:
                    DoTemperatureTab(listing);
                    break;
                case SettingsTab.Shield:
                    DoShieldTab(listing);
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

        // Display order of the shield dome styles. Same reason as StyleOrder above:
        // the radio row is indexed by position, not by the setting value.
        private static readonly int[] ShieldStyleOrder =
        {
            VisualTweaksSettings.ShieldDomeStyleCrisp,
            VisualTweaksSettings.ShieldDomeStyleHex,
            VisualTweaksSettings.ShieldDomeStyleVanilla
        };

        private static int ShieldStyleIndexOf(int style)
        {
            for (int i = 0; i < ShieldStyleOrder.Length; i++)
            {
                if (ShieldStyleOrder[i] == style)
                {
                    return i;
                }
            }

            return 0;
        }

        private static string[] ShieldStyleLabels()
        {
            return new string[]
            {
                "VisualTweaks_ShieldDome_StyleCrisp".Translate(),
                "VisualTweaks_ShieldDome_Hex".Translate(),
                "VisualTweaks_ShieldDome_StyleVanilla".Translate()
            };
        }

        private static string[] ShieldStyleTips()
        {
            return new string[]
            {
                "VisualTweaks_ShieldDome_StyleCrispTip".Translate(),
                "VisualTweaks_ShieldDome_HexTip".Translate(),
                "VisualTweaks_ShieldDome_StyleVanillaTip".Translate()
            };
        }

        // Display order of the hit animations. The panel surface has a third option;
        // the other two surfaces share the flare/brighten pair.
        private static readonly int[] StandardImpactOrder =
        {
            VisualTweaksSettings.ShieldImpactFlare,
            VisualTweaksSettings.ShieldImpactBrighten
        };

        private static readonly int[] HexImpactOrder =
        {
            VisualTweaksSettings.ShieldImpactBrighten,
            VisualTweaksSettings.ShieldImpactHexWave
        };

        private static int ImpactIndexOf(int[] order, int value)
        {
            for (int i = 0; i < order.Length; i++)
            {
                if (order[i] == value)
                {
                    return i;
                }
            }

            return 0;
        }

        private static string[] ImpactLabels(bool hexSurface)
        {
            return hexSurface
                ? new string[]
                {
                    "VisualTweaks_ShieldDome_ImpactBrighten".Translate(),
                    "VisualTweaks_ShieldDome_ImpactHexWave".Translate()
                }
                : new string[]
                {
                    "VisualTweaks_ShieldDome_ImpactFlare".Translate(),
                    "VisualTweaks_ShieldDome_ImpactBrighten".Translate()
                };
        }

        private static string[] ImpactTips(bool hexSurface)
        {
            return hexSurface
                ? new string[]
                {
                    "VisualTweaks_ShieldDome_ImpactBrightenTip".Translate(),
                    "VisualTweaks_ShieldDome_ImpactHexWaveTip".Translate()
                }
                : new string[]
                {
                    "VisualTweaks_ShieldDome_ImpactFlareTip".Translate(),
                    "VisualTweaks_ShieldDome_ImpactBrightenTip".Translate()
                };
        }

        // Break animation order. The shell gains its panel-loss option; Vanilla is
        // still listed first so the custom animation can be turned off.
        private static readonly int[] StandardBreakOrder =
        {
            VisualTweaksSettings.ShieldBreakVanilla,
            VisualTweaksSettings.ShieldBreakDissolve
        };

        private static readonly int[] HexBreakOrder =
        {
            VisualTweaksSettings.ShieldBreakVanilla,
            VisualTweaksSettings.ShieldBreakDissolve,
            VisualTweaksSettings.ShieldBreakHexDissipate
        };

        private static string[] BreakLabels(bool hexSurface)
        {
            return hexSurface
                ? new string[]
                {
                    "VisualTweaks_ShieldDome_BreakVanilla".Translate(),
                    "VisualTweaks_ShieldDome_BreakDissolve".Translate(),
                    "VisualTweaks_ShieldDome_BreakHexDissipate".Translate()
                }
                : new string[]
                {
                    "VisualTweaks_ShieldDome_BreakVanilla".Translate(),
                    "VisualTweaks_ShieldDome_BreakDissolve".Translate()
                };
        }

        private static string[] BreakTips(bool hexSurface)
        {
            return hexSurface
                ? new string[]
                {
                    "VisualTweaks_ShieldDome_BreakVanillaTip".Translate(),
                    "VisualTweaks_ShieldDome_BreakDissolveTip".Translate(),
                    "VisualTweaks_ShieldDome_BreakHexDissipateTip".Translate()
                }
                : new string[]
                {
                    "VisualTweaks_ShieldDome_BreakVanillaTip".Translate(),
                    "VisualTweaks_ShieldDome_BreakDissolveTip".Translate()
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
            SettingsLayout.DevModeCheckbox(
                listing,
                "VisualTweaks_Gas_TraceWindows".Translate(),
                ref VisualTweaksSettings.gasFieldTraceWindows,
                "VisualTweaks_Gas_TraceWindowsTip".Translate());
            // Not a DevModeCheckbox: the A/B test must be possible with dev mode off,
            // because dev mode itself changes frame timing.
            listing.CheckboxLabeled(
                "VisualTweaks_Gas_Diagnostics".Translate(),
                ref VisualTweaksSettings.gasFieldDiagnostics,
                "VisualTweaks_Gas_DiagnosticsTip".Translate());

            // Vacuum leak used to have a tab of its own, but it is another way air is
            // shown moving, and the tab strip was getting crowded. It sits at the bottom
            // of the gas tab so the gas controls keep their place.
            DoLeakSection(listing);

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

        /// <summary>
        /// Every shield visual lives here. The groups used to sit under the ripple tab
        /// only because the hit ripple borrows the shockwave shader, but the display and
        /// dome settings have nothing to do with explosions and were easy to miss there.
        /// Each group stays a section of its own: they replace different parts of the
        /// stock shield drawing and are switched on independently.
        /// </summary>
        private static void DoShieldTab(Listing_Standard listing)
        {
            // Borrows the shockwave shader but keeps its own switch: this is feedback
            // on the shield, not air distortion, so it does not follow the ripple toggle.
            SettingsLayout.Header(listing, "VisualTweaks_ShieldRipple_Group".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_ShieldRipple_Enable".Translate(),
                ref VisualTweaksSettings.shieldRippleEnabled,
                "VisualTweaks_ShieldRipple_EnableTip".Translate());
            VisualTweaksSettings.shieldRippleIntensity = SettingsLayout.Slider(
                listing,
                "VisualTweaks_ShieldRipple_Intensity".Translate(),
                VisualTweaksSettings.shieldRippleIntensity,
                VisualTweaksSettings.MinShieldRippleIntensity,
                VisualTweaksSettings.MaxShieldRippleIntensity,
                "VisualTweaks_ShieldRipple_IntensityTip".Translate());
            VisualTweaksSettings.shieldRippleDuration = SettingsLayout.Slider(
                listing,
                "VisualTweaks_ShieldRipple_Duration".Translate(),
                VisualTweaksSettings.shieldRippleDuration,
                VisualTweaksSettings.MinShieldRippleDuration,
                VisualTweaksSettings.MaxShieldRippleDuration,
                "VisualTweaks_ShieldRipple_DurationTip".Translate());
            VisualTweaksSettings.shieldRippleDamageScale = SettingsLayout.Slider(
                listing,
                "VisualTweaks_ShieldRipple_DamageScale".Translate(),
                VisualTweaksSettings.shieldRippleDamageScale,
                VisualTweaksSettings.MinShieldRippleDamageScale,
                VisualTweaksSettings.MaxShieldRippleDamageScale,
                "VisualTweaks_ShieldRipple_DamageScaleTip".Translate());

            // Not a ripple: this one changes what the stock shield drawing does on every
            // frame, so it gets its own section instead of riding along with the effects.
            SettingsLayout.Header(listing, "VisualTweaks_ShieldAlways_Group".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_ShieldAlways_Enable".Translate(),
                ref VisualTweaksSettings.alwaysShowShields,
                "VisualTweaks_ShieldAlways_EnableTip".Translate());

            // Also not a ripple, and not an addition either: this one replaces the
            // stock drawing, so it reads as a separate decision from "show shields
            // that vanilla hides".
            SettingsLayout.Header(listing, "VisualTweaks_ShieldDome_Group".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_ShieldDome_Enable".Translate(),
                ref VisualTweaksSettings.crispShieldDome,
                "VisualTweaks_ShieldDome_EnableTip".Translate());

            // The style is a sub-choice of the custom dome, not a separate drawing
            // path: the same quad and material. It used to be a checkbox for the hex
            // shell alone; three mutually exclusive styles need a radio row, or two
            // of them end up ticked at once.
            listing.Gap(6f);
            int styleIndex = ShieldStyleIndexOf(VisualTweaksSettings.shieldDomeStyle);
            int pickedStyle = SettingsLayout.RadioRow(
                listing,
                ShieldStyleLabels(),
                styleIndex,
                ShieldStyleTips());
            if (pickedStyle != styleIndex)
            {
                VisualTweaksSettings.shieldDomeStyle = ShieldStyleOrder[pickedStyle];
            }

            // Cheap when the texture is already baked. This is also the moment a
            // mid-game switch to the panel shell pays its one-off cost, instead of
            // the first shield that happens to be drawn.
            BakeHexIfNewlyDrawn();

            if (VisualTweaksSettings.HexShieldDome)
            {
                VisualTweaksSettings.shieldHexFaceStrength = SettingsLayout.Slider(
                    listing,
                    "VisualTweaks_ShieldDome_HexFace".Translate(),
                    VisualTweaksSettings.shieldHexFaceStrength,
                    VisualTweaksSettings.MinShieldHexFace,
                    VisualTweaksSettings.MaxShieldHexFace,
                    "VisualTweaks_ShieldDome_HexFaceTip".Translate());
                VisualTweaksSettings.shieldHexLineStrength = SettingsLayout.Slider(
                    listing,
                    "VisualTweaks_ShieldDome_HexLine".Translate(),
                    VisualTweaksSettings.shieldHexLineStrength,
                    VisualTweaksSettings.MinShieldHexLine,
                    VisualTweaksSettings.MaxShieldHexLine,
                    "VisualTweaks_ShieldDome_HexLineTip".Translate());
                VisualTweaksSettings.shieldHexDensity = SettingsLayout.Slider(
                    listing,
                    "VisualTweaks_ShieldDome_HexDensity".Translate(),
                    VisualTweaksSettings.shieldHexDensity,
                    VisualTweaksSettings.MinShieldHexDensity,
                    VisualTweaksSettings.MaxShieldHexDensity,
                    "VisualTweaks_ShieldDome_HexDensityTip".Translate(),
                    step: 1f);

                // The slider above edits a DRAFT. A rebake at 2048^2 is a few hundred ms
                // of main-thread work, and rebuilding on every integer step would stall
                // the game over and over while the handle is dragged. So the only thing
                // that commits the draft is this button, and the note under it says so
                // whenever the two disagree.
                int draftDensity = Mathf.RoundToInt(VisualTweaksSettings.shieldHexDensity);
                int bakedDensity = VisualTweaksSettings.shieldHexDensityApplied;
                if (SettingsLayout.ButtonRow(
                        listing,
                        new[] { "VisualTweaks_ShieldDome_HexBake".Translate().ToString() }) == 0)
                {
                    ShieldHexMap.Apply(ShieldHexTarget.Large, draftDensity);
                    bakedDensity = VisualTweaksSettings.shieldHexDensityApplied;
                }

                if (draftDensity != bakedDensity)
                {
                    SettingsLayout.Note(
                        listing,
                        "VisualTweaks_ShieldDome_HexPending".Translate(
                            bakedDensity,
                            draftDensity));
                }

                // The belt is a different optical problem: a quarter of the
                // diameter and a twentieth of the screen area. Its lattice is
                // therefore baked separately, with its own cell count and a fixed
                // 512² texture; the dome's 5 and 2048 are not defaults it inherits.
                listing.Gap(6f);
                SettingsLayout.SubHeader(
                    listing,
                    "VisualTweaks_ShieldDome_PersonalHexGroup".Translate());

                VisualTweaksSettings.personalShieldHexDensity = SettingsLayout.Slider(
                    listing,
                    "VisualTweaks_ShieldDome_PersonalHexDensity".Translate(),
                    VisualTweaksSettings.personalShieldHexDensity,
                    VisualTweaksSettings.MinShieldHexDensity,
                    VisualTweaksSettings.MaxShieldHexDensity,
                    "VisualTweaks_ShieldDome_PersonalHexDensityTip".Translate(),
                    step: 1f);

                int personalDraftDensity = Mathf.RoundToInt(
                    VisualTweaksSettings.personalShieldHexDensity);
                int personalBakedDensity =
                    VisualTweaksSettings.personalShieldHexDensityApplied;

                if (SettingsLayout.ButtonRow(
                        listing,
                        new[]
                        {
                            "VisualTweaks_ShieldDome_PersonalHexBake".Translate()
                                .ToString()
                        }) == 0)
                {
                    ShieldHexMap.Apply(
                        ShieldHexTarget.Personal,
                        personalDraftDensity);
                    personalBakedDensity =
                        VisualTweaksSettings.personalShieldHexDensityApplied;
                }

                if (personalDraftDensity != personalBakedDensity)
                {
                    SettingsLayout.Note(
                        listing,
                        "VisualTweaks_ShieldDome_PersonalHexPending".Translate(
                            personalBakedDensity,
                            personalDraftDensity));
                }
            }

            // The stock-like style has no ridge line, a core or a fresnel exponent to
            // tune: every width is a fraction of the radius, like the texture it
            // imitates. Showing the ridge sliders would suggest otherwise.
            if (VisualTweaksSettings.VanillaShieldDome)
            {
                SettingsLayout.Note(
                    listing,
                    "VisualTweaks_ShieldDome_VanillaNote".Translate());
            }
            else
            {
                // These read every frame, so a slider drag is visible while the game
                // is paused on a selected shield — the same reason the style row above
                // is live.
                VisualTweaksSettings.shieldRidgeWidth = SettingsLayout.Slider(
                    listing,
                    "VisualTweaks_ShieldDome_RidgeWidth".Translate(),
                    VisualTweaksSettings.shieldRidgeWidth,
                    VisualTweaksSettings.MinShieldRidgeWidth,
                    VisualTweaksSettings.MaxShieldRidgeWidth,
                    "VisualTweaks_ShieldDome_RidgeWidthTip".Translate());
                VisualTweaksSettings.shieldRidgeBrightness = SettingsLayout.Slider(
                    listing,
                    "VisualTweaks_ShieldDome_RidgeBrightness".Translate(),
                    VisualTweaksSettings.shieldRidgeBrightness,
                    0f,
                    1f,
                    "VisualTweaks_ShieldDome_RidgeBrightnessTip".Translate());
            }

            // The hit animation is a separate choice from the surface. Each surface
            // keeps its own selection, because the valid sets differ: the panel shell
            // can use its lattice wave, while the crisp and stock-like surfaces choose
            // between a painted flare and a brightened sector.
            SettingsLayout.SubHeader(
                listing,
                "VisualTweaks_ShieldDome_ImpactGroup".Translate());

            bool hexSurface = VisualTweaksSettings.HexShieldDome;
            int[] impactOrder;
            int currentImpact;
            if (hexSurface)
            {
                impactOrder = HexImpactOrder;
                currentImpact = VisualTweaksSettings.hexShieldImpactStyle;
            }
            else if (VisualTweaksSettings.VanillaShieldDome)
            {
                impactOrder = StandardImpactOrder;
                currentImpact = VisualTweaksSettings.vanillaShieldImpactStyle;
            }
            else
            {
                impactOrder = StandardImpactOrder;
                currentImpact = VisualTweaksSettings.crispShieldImpactStyle;
            }

            int impactIndex = ImpactIndexOf(impactOrder, currentImpact);
            int pickedImpact = SettingsLayout.RadioRow(
                listing,
                ImpactLabels(hexSurface),
                impactIndex,
                ImpactTips(hexSurface));
            if (pickedImpact != impactIndex)
            {
                int pickedValue = impactOrder[pickedImpact];
                if (hexSurface)
                {
                    VisualTweaksSettings.hexShieldImpactStyle = pickedValue;
                }
                else if (VisualTweaksSettings.VanillaShieldDome)
                {
                    VisualTweaksSettings.vanillaShieldImpactStyle = pickedValue;
                }
                else
                {
                    VisualTweaksSettings.crispShieldImpactStyle = pickedValue;
                }
            }

            if (VisualTweaksSettings.CurrentShieldImpactStyle
                == VisualTweaksSettings.ShieldImpactHexWave)
            {
                // The wave's numbers have nothing in common with the flare's, so it
                // keeps its own two controls rather than borrowing the flare's
                // opacity and a hidden fudge factor.
                VisualTweaksSettings.shieldHexImpactStrength = SettingsLayout.Slider(
                    listing,
                    "VisualTweaks_ShieldDome_HexImpact".Translate(),
                    VisualTweaksSettings.shieldHexImpactStrength,
                    VisualTweaksSettings.MinShieldHexImpactStrength,
                    VisualTweaksSettings.MaxShieldHexImpactStrength,
                    "VisualTweaks_ShieldDome_HexImpactTip".Translate());
                VisualTweaksSettings.shieldHexImpactShare = SettingsLayout.Slider(
                    listing,
                    "VisualTweaks_ShieldDome_HexImpactReach".Translate(),
                    VisualTweaksSettings.shieldHexImpactShare,
                    VisualTweaksSettings.MinShieldHexImpactShare,
                    VisualTweaksSettings.MaxShieldHexImpactShare,
                    "VisualTweaks_ShieldDome_HexImpactReachTip".Translate());
            }
            else
            {
                // The flare and the brightened sector share strength and the damage
                // response. Size belongs to the flare (its width follows damage);
                // the brighten sector has a fixed, separately adjustable angle.
                VisualTweaksSettings.shieldFlareAlpha = SettingsLayout.Slider(
                    listing,
                    "VisualTweaks_ShieldDome_FlareAlpha".Translate(),
                    VisualTweaksSettings.shieldFlareAlpha,
                    VisualTweaksSettings.MinShieldFlareAlpha,
                    VisualTweaksSettings.MaxShieldFlareAlpha,
                    "VisualTweaks_ShieldDome_FlareAlphaTip".Translate());
                VisualTweaksSettings.shieldFlareDamageInfluence = SettingsLayout.Slider(
                    listing,
                    "VisualTweaks_ShieldDome_FlareDamage".Translate(),
                    VisualTweaksSettings.shieldFlareDamageInfluence,
                    0f,
                    1f,
                    "VisualTweaks_ShieldDome_FlareDamageTip".Translate());

                if (VisualTweaksSettings.CurrentShieldImpactStyle
                    == VisualTweaksSettings.ShieldImpactFlare)
                {
                    VisualTweaksSettings.shieldFlareSizeScale = SettingsLayout.Slider(
                        listing,
                        "VisualTweaks_ShieldDome_FlareSize".Translate(),
                        VisualTweaksSettings.shieldFlareSizeScale,
                        VisualTweaksSettings.MinShieldFlareSize,
                        VisualTweaksSettings.MaxShieldFlareSize,
                        "VisualTweaks_ShieldDome_FlareSizeTip".Translate());
                }
                else
                {
                    // The angle is the brighten reaction's own control. Vanilla's
                    // stock value is 30 degrees total; it is adjustable because the
                    // reaction is also available on the two custom surfaces.
                    VisualTweaksSettings.shieldBrightenAngle = SettingsLayout.Slider(
                        listing,
                        "VisualTweaks_ShieldDome_BrightenAngle".Translate(),
                        VisualTweaksSettings.shieldBrightenAngle,
                        VisualTweaksSettings.MinShieldBrightenAngle,
                        VisualTweaksSettings.MaxShieldBrightenAngle,
                        "VisualTweaks_ShieldDome_BrightenAngleTip".Translate(),
                        step: 1f);
                }
            }

            // Personal belts get their own absorption reaction. This is not one of
            // the dome's impact styles: a belt is too small for a flare or a panel
            // wave, and its jitter should be tunable without moving the big shield's
            // hit animation. Hidden while the custom drawing is off because vanilla
            // then supplies its own unconfigurable jitter.
            if (VisualTweaksSettings.crispShieldDome)
            {
                SettingsLayout.SubHeader(
                    listing,
                    "VisualTweaks_ShieldDome_PersonalLookGroup".Translate());
                VisualTweaksSettings.personalShieldOpacity = SettingsLayout.Slider(
                    listing,
                    "VisualTweaks_ShieldDome_PersonalOpacity".Translate(),
                    VisualTweaksSettings.personalShieldOpacity,
                    VisualTweaksSettings.MinPersonalShieldOpacity,
                    VisualTweaksSettings.MaxPersonalShieldOpacity,
                    "VisualTweaks_ShieldDome_PersonalOpacityTip".Translate());

                SettingsLayout.SubHeader(
                    listing,
                    "VisualTweaks_ShieldDome_PersonalHitGroup".Translate());
                SettingsLayout.Note(
                    listing,
                    "VisualTweaks_ShieldDome_PersonalHitNote".Translate());
                listing.CheckboxLabeled(
                    "VisualTweaks_ShieldDome_PersonalJitter".Translate(),
                    ref VisualTweaksSettings.personalShieldHitJitter,
                    "VisualTweaks_ShieldDome_PersonalJitterTip".Translate());

                if (VisualTweaksSettings.personalShieldHitJitter)
                {
                    VisualTweaksSettings.personalShieldJitterDistance =
                        SettingsLayout.Slider(
                            listing,
                            "VisualTweaks_ShieldDome_PersonalJitterDistance".Translate(),
                            VisualTweaksSettings.personalShieldJitterDistance,
                            VisualTweaksSettings.MinPersonalShieldJitterDistance,
                            VisualTweaksSettings.MaxPersonalShieldJitterDistance,
                            "VisualTweaks_ShieldDome_PersonalJitterDistanceTip".Translate(),
                            step: 0.01f);
                    VisualTweaksSettings.personalShieldJitterTicks =
                        SettingsLayout.Slider(
                            listing,
                            "VisualTweaks_ShieldDome_PersonalJitterTicks".Translate(),
                            VisualTweaksSettings.personalShieldJitterTicks,
                            VisualTweaksSettings.MinPersonalShieldJitterTicks,
                            VisualTweaksSettings.MaxPersonalShieldJitterTicks,
                            "VisualTweaks_ShieldDome_PersonalJitterTicksTip".Translate(),
                            step: 1f);
                }
            }

            // The break animation is per surface for the same reason: a shell loses
            // panels, a continuous surface tears, and Vanilla means do not animate.
            SettingsLayout.SubHeader(
                listing,
                "VisualTweaks_ShieldDome_BreakGroup".Translate());

            int[] breakOrder;
            int currentBreak;
            if (hexSurface)
            {
                breakOrder = HexBreakOrder;
                currentBreak = VisualTweaksSettings.hexShieldBreakStyle;
            }
            else if (VisualTweaksSettings.VanillaShieldDome)
            {
                breakOrder = StandardBreakOrder;
                currentBreak = VisualTweaksSettings.vanillaShieldBreakStyle;
            }
            else
            {
                breakOrder = StandardBreakOrder;
                currentBreak = VisualTweaksSettings.crispShieldBreakStyle;
            }

            int breakIndex = ImpactIndexOf(breakOrder, currentBreak);
            int pickedBreak = SettingsLayout.RadioRow(
                listing,
                BreakLabels(hexSurface),
                breakIndex,
                BreakTips(hexSurface));
            if (pickedBreak != breakIndex)
            {
                int pickedValue = breakOrder[pickedBreak];
                if (hexSurface)
                {
                    VisualTweaksSettings.hexShieldBreakStyle = pickedValue;
                }
                else if (VisualTweaksSettings.VanillaShieldDome)
                {
                    VisualTweaksSettings.vanillaShieldBreakStyle = pickedValue;
                }
                else
                {
                    VisualTweaksSettings.crispShieldBreakStyle = pickedValue;
                }
            }

            if (VisualTweaksSettings.CurrentShieldBreakStyle
                != VisualTweaksSettings.ShieldBreakVanilla)
            {
                VisualTweaksSettings.shieldDissolveTicks = SettingsLayout.Slider(
                    listing,
                    "VisualTweaks_ShieldDome_DissolveTime".Translate(),
                    VisualTweaksSettings.shieldDissolveTicks,
                    VisualTweaksSettings.MinShieldDissolveTicks,
                    VisualTweaksSettings.MaxShieldDissolveTicks,
                    "VisualTweaks_ShieldDome_DissolveTimeTip".Translate(),
                    step: 1f);
                VisualTweaksSettings.shieldDissolveBrightness = SettingsLayout.Slider(
                    listing,
                    "VisualTweaks_ShieldDome_DissolveBrightness".Translate(),
                    VisualTweaksSettings.shieldDissolveBrightness,
                    VisualTweaksSettings.MinShieldDissolveBrightness,
                    VisualTweaksSettings.MaxShieldDissolveBrightness,
                    "VisualTweaks_ShieldDome_DissolveBrightnessTip".Translate());
            }

            SettingsLayout.Finish(listing);
        }

        /// <summary>
        /// Heat and cold share one tab. They are two ends of the same dial, and each
        /// section is short enough that two tabs cost a slot in the tab strip for very
        /// little content. The section headers keep the two effects apart.
        /// </summary>
        private static void DoTemperatureTab(Listing_Standard listing)
        {
            DoHeatSection(listing);
            DoColdSection(listing);

            SettingsLayout.Finish(listing);
        }

        private static void DoHeatSection(Listing_Standard listing)
        {
            SettingsLayout.Header(listing, "VisualTweaks_Temp_GroupHeat".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Heat_Enable".Translate(),
                ref VisualTweaksSettings.heatHazeEnabled,
                "VisualTweaks_Heat_EnableTip".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Heat_HighFrequency".Translate(),
                ref VisualTweaksSettings.heatHazeHighFrequency,
                "VisualTweaks_Heat_HighFrequencyTip".Translate());

            // Per-cell sampling needs AO: vanilla resolves temperature per room, so a
            // cell-by-cell read would hand back the same value across the room at a much
            // higher cost. The row is hidden rather than disabled, matching the wind tab —
            // as a companion mod there is nothing the user could do about a missing AO, so
            // a greyed control plus an explanation is just noise.
            if (AOIntegration.PerCellTemperatureAvailable)
            {
                listing.CheckboxLabeled(
                    "VisualTweaks_Heat_PerCellTemp".Translate(),
                    ref VisualTweaksSettings.heatHazePerCellTemp,
                    "VisualTweaks_Heat_PerCellTempTip".Translate());
            }

            if (!HeatHazeBridge.ShaderReady)
            {
                SettingsLayout.Warning(listing, "VisualTweaks_Heat_ShaderMissing".Translate());
            }

            SettingsLayout.SubHeader(listing, "VisualTweaks_Temp_GroupLook".Translate());
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
        }

        private static void DoColdSection(Listing_Standard listing)
        {
            SettingsLayout.Header(listing, "VisualTweaks_Temp_GroupCold".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Cold_Enable".Translate(),
                ref VisualTweaksSettings.coldMistEnabled,
                "VisualTweaks_Cold_EnableTip".Translate());

            if (!ColdMistBridge.ShaderReady)
            {
                SettingsLayout.Warning(listing, "VisualTweaks_Cold_ShaderMissing".Translate());
            }

            SettingsLayout.SubHeader(listing, "VisualTweaks_Temp_GroupLook".Translate());
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
        }

        /// <summary>
        /// Also part of the gas tab now: vacuum leak is another way escaping air is
        /// drawn, driven by Odyssey's pressure difference instead of gas cell density.
        /// Keeping it last leaves the gas controls where they have always been.
        /// </summary>
        private static void DoLeakSection(Listing_Standard listing)
        {
            SettingsLayout.Header(listing, "VisualTweaks_Gas_GroupLeak".Translate());
            listing.CheckboxLabeled(
                "VisualTweaks_Leak_Enable".Translate(),
                ref VisualTweaksSettings.vacuumLeakEnabled,
                "VisualTweaks_Leak_EnableTip".Translate());

            if (!VacuumLeakBridge.ShaderReady)
            {
                SettingsLayout.Warning(listing, "VisualTweaks_Leak_ShaderMissing".Translate());
            }

            SettingsLayout.SubHeader(listing, "VisualTweaks_Leak_GroupLook".Translate());
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

            SettingsLayout.SubHeader(listing, "VisualTweaks_Leak_GroupDebug".Translate());
            SettingsLayout.DevModeCheckbox(
                listing,
                "VisualTweaks_Leak_Trace".Translate(),
                ref VisualTweaksSettings.vacuumLeakTrace,
                "VisualTweaks_Leak_TraceTip".Translate());
        }

        /// <summary>
        /// Bakes while the settings page is open, not on the next shield that happens to
        /// be drawn. A settings window is the one moment the player expects the pause,
        /// and it keeps a mid-game style switch from hitching during a fight. No-op
        /// unless the panel style is selected and drawable, and cheap when the texture
        /// is already baked.
        /// </summary>
        private static void BakeHexIfNewlyDrawn()
        {
            if (VisualTweaksSettings.HexShieldDome)
            {
                ShieldHexMap.Apply(
                    ShieldHexTarget.Large,
                    VisualTweaksSettings.shieldHexDensityApplied);
                ShieldHexMap.Apply(
                    ShieldHexTarget.Personal,
                    VisualTweaksSettings.personalShieldHexDensityApplied);
            }
        }
    }
}
