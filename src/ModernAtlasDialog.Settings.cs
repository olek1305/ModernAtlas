using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
namespace ModernAtlas;

/// <summary>
/// Atlas settings callbacks: gameplay switches, entity visibility policy,
/// lighting selection and the atlas visual lab controls.
/// </summary>
public sealed partial class ModernAtlasDialog
{
    private void ConfigureVisualLabSliders()
    {
        visualLabModal.GetAtlasSlider("atlas-exposure")?.SetValues(
            AtlasExposureCalibration.ClampPercent(config.AtlasExposurePercent),
            AtlasExposureCalibration.MinimumPercent,
            AtlasExposureCalibration.MaximumPercent,
            AtlasExposureCalibration.SliderStepPercent,
            "%"
        );
        visualLabModal.GetAtlasSlider("cave-mask-brightness")?.SetValues(
            Math.Clamp(config.CaveMaskBrightnessPercent, 50, 150),
            50,
            150,
            5,
            "%"
        );
    }

    private void SyncVisualLabControls()
    {
        ConfigureVisualLabSliders();
        bool settingsAllowed = SettingsAccessAllowed;
        if (visualLabModal != null)
        {
            visualLabModal.GetAtlasSlider("atlas-exposure")!.Enabled = settingsAllowed;
            visualLabModal.GetAtlasSlider("cave-mask-brightness")!.Enabled = settingsAllowed;
            visualLabModal.GetAtlasButton("visual-lab-reset")!.Enabled = settingsAllowed;
        }
        // Current values live on the sliders; the neutral defaults are stated
        // next to them so "neutral" is a number, not a guess.
        visualLabModal?.GetDynamicText("visual-lab-exposure-default")?.SetNewText(
            FormattableString.Invariant(
                $"now {AtlasExposureCalibration.ClampPercent(config.AtlasExposurePercent)}% · neutral {DefaultAtlasExposurePercent}%"
            )
        );
        visualLabModal?.GetDynamicText("visual-lab-cave-default")?.SetNewText(
            FormattableString.Invariant(
                $"now {Math.Clamp(config.CaveMaskBrightnessPercent, 50, 150)}% · default {DefaultCaveMaskBrightnessPercent}%"
            )
        );
    }

    internal const int DefaultAtlasExposurePercent = AtlasExposureCalibration.DefaultPercent;
    internal const int DefaultCaveMaskBrightnessPercent = 100;
    internal const int DefaultMapLayerOpacityPercent = 75;

    private bool OnAtlasExposureChanged(int value)
    {
        if (!SettingsAccessAllowed) return false;
        config.AtlasExposurePercent = AtlasExposureCalibration.ClampPercent(value);
        saveConfig();
        return true;
    }

    private bool OnCaveMaskBrightnessChanged(int value)
    {
        if (!SettingsAccessAllowed) return false;
        config.CaveMaskBrightnessPercent = Math.Clamp(value, 50, 150);
        saveConfig();
        return true;
    }

    private bool ResetVisualTuning()
    {
        if (!SettingsAccessAllowed) return false;
        config.AtlasExposurePercent = DefaultAtlasExposurePercent;
        config.MapLayerOpacityPercent = DefaultMapLayerOpacityPercent;
        config.CaveMaskBrightnessPercent = DefaultCaveMaskBrightnessPercent;
        saveConfig();
        SyncVisualLabControls();
        return true;
    }

    private void OnRenderOnScrollToggled(bool enabled)
    {
        if (!SettingsAccessAllowed) return;
        presentationChangeCoordinator.Request(
            enabled,
            capi.ElapsedMilliseconds,
            PresentationChangeDebounceMilliseconds
        );
        // Keep the requested state visible in the surviving Settings composer
        // while the committed map viewport continues to render unchanged.
        SyncSettingsControls();
        SyncToolbarControls();
    }

    private void OnScrollRealtimeWeatherToggled(bool enabled)
    {
        if (!SettingsAccessAllowed) return;
        config.ScrollRealtimeWeatherEnabled = enabled;
        saveConfig();
        SyncSettingsControls();
        SyncToolbarControls();
    }

    private void OnPlayerCompassToggled(bool enabled)
    {
        config.ShowPlayerCompass = enabled;
        saveConfig();
        SyncSettingsControls();
        SyncToolbarControls();
    }

    private bool ToggleCompass()
    {
        OnPlayerCompassToggled(!config.ShowPlayerCompass);
        return true;
    }

    private void OnHandheldInstrumentChoiceChanged(string value, bool selected)
    {
        if (!selected) return;
        if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            config.ShowPlayerCompass = false;
        }
        else
        {
            config.HandheldInstrumentMode = string.Equals(
                value,
                "time",
                StringComparison.OrdinalIgnoreCase
            )
                ? "time"
                : "compass";
            config.ShowPlayerCompass = true;
        }
        saveConfig();
        SyncSettingsControls();
        SyncToolbarControls();
    }

    private void OnSettingsHandheldInstrumentChoiceChanged(
        string value,
        bool selected
    )
    {
        if (!SettingsAccessAllowed) return;
        OnHandheldInstrumentChoiceChanged(value, selected);
    }

    private void OnMapLayersToggled(bool enabled)
    {
        if (!SettingsAccessAllowed) return;
        config.MapLayersEnabled = enabled;
        if (enabled)
        {
            if (IsOpened()) PrepareMapLayer();
        }
        else
        {
            SetMapLayer(AtlasMapLayer.TexturedTerrain);
        }
        saveConfig();
        SyncSettingsControls();
        SyncToolbarControls();
    }

    private void OnCaveModeToggled(bool enabled)
    {
        if (!SettingsAccessAllowed || !CreativeCheatSettingsAvailable) return;

        config.CaveModeEnabled = enabled;
        selectedEntityId = null;
        PrepareSurfaceSafetyFilter();
        saveConfig();
        SyncCreativeSettingsControls();
    }

    private void OnSearchModeToggled(bool enabled)
    {
        if (!SettingsAccessAllowed || !CreativeCheatSettingsAvailable)
        {
            SyncSettingsControls();
            return;
        }

        config.SearchModeEnabled = enabled;
        if (!enabled)
        {
            searchPanel?.UnfocusOwnElements();
            ClearSearch();
        }
        saveConfig();
        SyncSettingsControls();
        SyncCreativeSettingsControls();
        SyncToolbarControls();
    }

    private void OnCameraAngleLockToggled(bool enabled)
    {
        if (!SettingsAccessAllowed || !CreativeCheatSettingsAvailable) return;

        config.CameraAngleLocked = enabled;
        if (enabled)
        {
            targetPitchDegrees = pitchDegrees;
        }
        saveConfig();
        SyncCreativeSettingsControls();
    }

    private void OnCloseAtlasOnDamageToggled(bool enabled)
    {
        if (!SettingsAccessAllowed) return;
        config.CloseAtlasOnDamage = enabled;
        saveConfig();
        capi.Logger.Notification(
            "[ModernAtlas] Close atlas when taking damage: {0} (effective now: {1}).",
            enabled,
            AutoCloseOnDamageActive
        );
    }

    private void OnAnimationsToggled(bool enabled)
    {
        if (!SettingsAccessAllowed) return;
        if (!enabled && config.AnimationsEnabled)
        {
            CaptureAnimationFrame();
        }
        config.AnimationsEnabled = enabled;
        saveConfig();
        SyncSettingsControls();
    }

    private void OnPerformanceLightingToggled(bool enabled)
    {
        if (synchronizingPerformanceControls || !SettingsAccessAllowed) return;
        config.PerformanceLightingEnabled = enabled;
        saveConfig();
        SyncPerformanceControls();
    }

    private void OnPerformanceModeChanged(string value, bool selected)
    {
        if (!selected || synchronizingPerformanceControls || !SettingsAccessAllowed) return;

        AtlasPerformanceMode mode = AtlasPerformanceModeInfo.Parse(value);
        string canonical = AtlasPerformanceModeInfo.CanonicalValue(mode);
        if (string.Equals(config.PerformanceMode, canonical, StringComparison.Ordinal))
        {
            SyncPerformanceControls();
            return;
        }

        config.PerformanceMode = canonical;
        saveConfig();
        SyncPerformanceControls();
        capi.Logger.Notification(
            "[ModernAtlas] Atlas performance mode changed to {0}; closed-atlas work remains disabled; opening preparation: {1}.",
            AtlasPerformanceModeInfo.Label(mode),
            AtlasPerformanceModeInfo.PreparesAtlasDuringOpening(mode)
                ? "enhanced"
                : "on-demand"
        );
        performanceTelemetryLogger?.Invoke("performance mode changed");
    }

    private void OnAtlasDetailChanged(string value, bool selected)
    {
        if (!selected || synchronizingPerformanceControls || !SettingsAccessAllowed) return;

        AtlasDetailMode mode = AtlasDetailModeInfo.Parse(value);
        string canonical = AtlasDetailModeInfo.CanonicalValue(mode);
        if (string.Equals(config.AtlasDetail, canonical, StringComparison.Ordinal))
        {
            SyncPerformanceControls();
            return;
        }

        config.AtlasDetail = canonical;
        saveConfig();
        SyncPerformanceControls();
        capi.Logger.Notification(
            "[ModernAtlas] Atlas detail changed to {0}; texture reduction={1}; atlas clouds are {2}.",
            AtlasDetailModeInfo.Label(mode),
            AtlasDetailModeInfo.EffectiveTextureDetailReduction(mode),
            AtlasDetailModeInfo.EffectiveCloudsEnabled(mode, config.CloudsEnabled)
                ? "enabled"
                : "suppressed"
        );
    }

    private void OnHideVegetationToggled(bool enabled)
    {
        if (synchronizingPerformanceControls
            || !SettingsAccessAllowed
            || !HideVegetationAllowed)
        {
            SyncPerformanceControls();
            return;
        }
        config.HideVegetation = enabled;
        saveConfig();
        SyncPerformanceControls();
    }

    private void OnSkipOpeningAnimationToggled(bool enabled)
    {
        if (!SettingsAccessAllowed) return;
        config.SkipOpeningAnimation = enabled;
        saveConfig();
        SyncSettingsControls();
    }

    private void OnCloudsToggled(bool enabled)
    {
        if (!SettingsAccessAllowed) return;
        config.CloudsEnabled = enabled;
        saveConfig();
        SyncSettingsControls();
    }

    private void OnLivingEntitiesToggled(bool enabled)
    {
        if (!SettingsAccessAllowed) return;
        config.LivingEntitiesEnabled = enabled;
        SaveEntitySettings();
    }

    private void OnPlayersToggled(bool enabled)
    {
        if (!SettingsAccessAllowed) return;
        config.ShowPlayers = enabled;
        SaveEntitySettings();
    }

    private void OnAnimalsToggled(bool enabled)
    {
        if (!SettingsAccessAllowed) return;
        config.ShowAnimals = enabled;
        SaveEntitySettings();
    }

    private void OnMobsToggled(bool enabled)
    {
        if (!SettingsAccessAllowed) return;
        config.ShowMobs = enabled;
        SaveEntitySettings();
    }

    private void OnNpcsToggled(bool enabled)
    {
        if (!SettingsAccessAllowed) return;
        config.ShowNpcs = enabled;
        SaveEntitySettings();
    }

    private void OnLiveLightingToggled(bool enabled)
    {
        if (!SettingsAccessAllowed) return;
        config.LiveLightingEnabled = enabled;
        // Live and fixed solar controls are meaningful only with directional
        // atlas lighting enabled. Selecting either mode must not silently
        // leave the old performance override on neutral flat daylight.
        config.PerformanceLightingEnabled = true;
        saveConfig();
        SyncSettingsControls();
        SyncPerformanceControls();
    }

    private bool OnFixedSunHourChanged(int hour)
    {
        if (!SettingsAccessAllowed) return false;
        config.FixedSunHour = Math.Clamp(hour, 0, 24);
        config.LiveLightingEnabled = false;
        config.PerformanceLightingEnabled = true;
        saveConfig();
        SyncSettingsControls();
        SyncPerformanceControls();
        return true;
    }

    private static bool IsSettingsHierarchySection(AtlasPanelSection section) =>
        section == AtlasPanelSection.Settings
        || section == AtlasPanelSection.Performance
        || section == AtlasPanelSection.Creative
        || section == AtlasPanelSection.VisualLab;

    /// <summary>
    /// A policy update can arrive between two GUI frames. Dispose the active
    /// Settings composer immediately instead of queueing an animated close:
    /// this prevents a pending child section from being rebuilt by a viewport
    /// recompose and leaves Exit/Hide and the atlas renderer untouched.
    /// </summary>
    private void CloseSettingsHierarchyForServerPolicy()
    {
        // The presentation switch commits after a short debounce. Revocation
        // must cancel that pending write even when the Settings panel was
        // already closing, otherwise a client preference can change after the
        // authoritative deny packet has been applied.
        presentationChangeCoordinator.Reset(config.RenderOnScroll);
        bool currentIsSettings = IsSettingsHierarchySection(bottomPanelSection);
        bool queuedIsSettings = IsSettingsHierarchySection(queuedBottomPanelSection);
        if (!SettingsHierarchyOpen && !currentIsSettings && !queuedIsSettings)
        {
            return;
        }

        ResetPointerDrag();
        if (currentIsSettings)
        {
            DisposeBottomPanelComposer();
        }
        else
        {
            queuedBottomPanelSection = AtlasPanelSection.None;
        }
        SetLogicalBottomPanel(AtlasPanelSection.None);
        settingsScrollOffset = 0;
        settingsSectionScrollOffset = 0;
        capi.Logger.Notification(
            "[ModernAtlas] Closed the Settings hierarchy because the server policy disabled client Settings; local configuration was preserved."
        );
    }

    private void SaveEntitySettings()
    {
        RefreshVisibleEntityPolicy();
        saveConfig();
        SyncSettingsControls();
    }

    private void RefreshVisibleEntityPolicy()
    {
        bool masterEnabled = config.LivingEntitiesEnabled;
        visibleEntityPolicy.ShowPlayers =
            ModernAtlasServerSettingsPolicy.AllowsEntityCategory(
                capi.IsSinglePlayer,
                config.ShowPlayers,
                serverPolicy.ShowPlayers,
                masterEnabled
            );
        visibleEntityPolicy.ShowAnimals =
            ModernAtlasServerSettingsPolicy.AllowsEntityCategory(
                capi.IsSinglePlayer,
                config.ShowAnimals,
                serverPolicy.ShowAnimals,
                masterEnabled
            );
        visibleEntityPolicy.ShowMobs =
            ModernAtlasServerSettingsPolicy.AllowsEntityCategory(
                capi.IsSinglePlayer,
                config.ShowMobs,
                serverPolicy.ShowMobs,
                masterEnabled
            );
        visibleEntityPolicy.ShowNpcs =
            ModernAtlasServerSettingsPolicy.AllowsEntityCategory(
                capi.IsSinglePlayer,
                config.ShowNpcs,
                serverPolicy.ShowNpcs,
                masterEnabled
            );
    }

    private void SyncSettingsControls()
    {
        if (settingsModal == null) return;
        bool settingsAllowed = SettingsAccessAllowed;
        settingsModal.GetAtlasSwitch("render-on-scroll")?.SetValue(
            presentationChangeCoordinator.DisplayedValue(config.RenderOnScroll)
        );
        settingsModal.GetAtlasSwitch("render-on-scroll")!.Enabled = settingsAllowed;
        settingsModal.GetAtlasSwitch("scroll-realtime-weather")?.SetValue(
            config.ScrollRealtimeWeatherEnabled
        );
        settingsModal.GetAtlasSwitch("scroll-realtime-weather")!.Enabled = settingsAllowed;
        GuiElementAtlasChoice? settingsInstrument =
            settingsModal.GetAtlasChoice("handheld-instrument");
        if (settingsInstrument != null)
        {
            settingsInstrument.SetSelectedIndex(HandheldInstrumentChoiceIndex);
            settingsInstrument.Enabled = settingsAllowed;
        }
        settingsModal.GetAtlasSwitch("map-layers")?.SetValue(config.MapLayersEnabled);
        settingsModal.GetAtlasSwitch("map-layers")!.Enabled = settingsAllowed;
        settingsModal.GetAtlasSwitch("search-mode")?.SetValue(
            CreativeCheatSettingsAvailable && config.SearchModeEnabled
        );
        settingsModal.GetAtlasSwitch("search-mode")!.Enabled =
            settingsAllowed && CreativeCheatSettingsAvailable;
        settingsModal.GetAtlasSwitch("animations")?.SetValue(config.AnimationsEnabled);
        settingsModal.GetAtlasSwitch("animations")!.Enabled = settingsAllowed;
        // The switch shows the stored preference in every mode; actual Creative
        // only suppresses its effect, so the value survives a mode change.
        settingsModal.GetAtlasSwitch("close-on-damage")?.SetValue(
            config.CloseAtlasOnDamage
        );
        settingsModal.GetAtlasSwitch("close-on-damage")!.Enabled = settingsAllowed;
        settingsModal.GetAtlasSwitch("skip-opening-animation")?.SetValue(
            config.SkipOpeningAnimation
        );
        settingsModal.GetAtlasSwitch("skip-opening-animation")!.Enabled = settingsAllowed;
        bool liveCloudsAvailable =
            VolumetricCloudRendererAdapter.IsEnabledByGraphicsSettings(capi);
        settingsModal.GetAtlasSwitch("clouds")?.SetValue(
            liveCloudsAvailable && config.CloudsEnabled
        );
        settingsModal.GetAtlasSwitch("clouds")!.Enabled =
            settingsAllowed && liveCloudsAvailable;
        bool solarLightingActive = config.PerformanceLightingEnabled;
        settingsModal.GetAtlasSwitch("live-lighting")?.SetValue(
            solarLightingActive && config.LiveLightingEnabled
        );
        settingsModal.GetAtlasSwitch("live-lighting")!.Enabled = settingsAllowed;
        settingsModal.GetAtlasSlider("fixed-sun-hour")?.SetValues(
            Math.Clamp(config.FixedSunHour, 0, 24),
            0,
            24,
            1,
            "h"
        );
        // When neutral flat lighting is active, allow either solar control to
        // opt back into directional lighting. Do not show a live switch as on
        // while the performance override is silently suppressing it.
        settingsModal.GetAtlasSlider("fixed-sun-hour")!.Enabled =
            settingsAllowed
            && (!solarLightingActive || !config.LiveLightingEnabled);
        bool serverAllowsAny = capi.IsSinglePlayer || serverPolicy.AnyEntityModels;
        settingsModal.GetAtlasSwitch("entities")?.SetValue(
            config.LivingEntitiesEnabled && serverAllowsAny
        );
        settingsModal.GetAtlasSwitch("entities")!.Enabled =
            settingsAllowed && serverAllowsAny;
        SyncEntityCategorySwitch("players", config.ShowPlayers, serverPolicy.ShowPlayers);
        SyncEntityCategorySwitch("animals", config.ShowAnimals, serverPolicy.ShowAnimals);
        SyncEntityCategorySwitch("mobs", config.ShowMobs, serverPolicy.ShowMobs);
        SyncEntityCategorySwitch("npcs", config.ShowNpcs, serverPolicy.ShowNpcs);
    }

    private void SyncPerformanceControls()
    {
        if (performanceModal == null) return;

        synchronizingPerformanceControls = true;
        try
        {
            performanceModal.GetAtlasChoice("performance-mode")?.SetSelectedIndex(
                PerformanceModeChoiceIndex
            );
            performanceModal.GetAtlasChoice("performance-mode")!.Enabled =
                SettingsAccessAllowed;
            performanceModal.GetDynamicText("performance-description")?.SetNewText(
                AtlasPerformanceModeInfo.Description(PerformanceMode)
            );
            performanceModal.GetAtlasChoice("atlas-detail")?.SetSelectedIndex(
                AtlasDetailChoiceIndex
            );
            performanceModal.GetAtlasChoice("atlas-detail")!.Enabled =
                SettingsAccessAllowed;
            performanceModal.GetDynamicText("atlas-detail-description")?.SetNewText(
                AtlasDetailModeInfo.Description(CurrentAtlasDetailMode)
            );
            performanceModal.GetAtlasSwitch("performance-lighting")?.SetValue(
                config.PerformanceLightingEnabled
            );
            performanceModal.GetAtlasSwitch("performance-lighting")!.Enabled =
                SettingsAccessAllowed;
            performanceModal.GetAtlasSwitch("hide-vegetation")?.SetValue(
                HideVegetationActive
            );
            performanceModal.GetAtlasSwitch("hide-vegetation")!.Enabled =
                SettingsAccessAllowed && HideVegetationAllowed;
            performanceModal.GetDynamicText("hide-vegetation-label")?.SetNewText(
                HideVegetationAllowed
                    ? "Hide vegetation"
                    : $"Hide vegetation · {ModernAtlasServerSettingsPolicy.DisabledReason}"
            );
            performanceModal.GetDynamicText("hide-vegetation-description")?.SetNewText(
                HideVegetationAllowed
                    ? "Hides registered plants, bushes and leaves, including mods."
                    : ModernAtlasServerSettingsPolicy.DisabledReason
            );
        }
        finally
        {
            synchronizingPerformanceControls = false;
        }
    }

    private void SyncCreativeSettingsControls()
    {
        if (creativeSettingsModal == null) return;

        bool available = CreativeCheatSettingsAvailable;
        creativeSettingsModal.GetAtlasSwitch("cave-mode")?.SetValue(
            available && config.CaveModeEnabled
        );
        creativeSettingsModal.GetAtlasSwitch("camera-angle-lock")?.SetValue(
            available && config.CameraAngleLocked
        );
        creativeSettingsModal.GetAtlasSwitch("cave-mode")!.Enabled =
            SettingsAccessAllowed && available;
        creativeSettingsModal.GetAtlasSwitch("camera-angle-lock")!.Enabled =
            SettingsAccessAllowed && available;
    }

    private void SyncEntityCategorySwitch(string key, bool clientEnabled, bool serverEnabled)
    {
        bool categoryAllowed = capi.IsSinglePlayer || serverEnabled;
        settingsModal.GetAtlasSwitch(key)?.SetValue(clientEnabled && categoryAllowed);
        settingsModal.GetAtlasSwitch(key)!.Enabled =
            SettingsAccessAllowed && config.LivingEntitiesEnabled && categoryAllowed;
    }

}
