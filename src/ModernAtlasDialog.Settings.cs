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
            Math.Clamp(config.AtlasExposurePercent, 50, 150),
            50,
            150,
            5,
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
        // Current values live on the sliders; the neutral defaults are stated
        // next to them so "neutral" is a number, not a guess.
        visualLabModal?.GetDynamicText("visual-lab-exposure-default")?.SetNewText(
            FormattableString.Invariant(
                $"now {Math.Clamp(config.AtlasExposurePercent, 50, 150)}% · default {DefaultAtlasExposurePercent}%"
            )
        );
        visualLabModal?.GetDynamicText("visual-lab-cave-default")?.SetNewText(
            FormattableString.Invariant(
                $"now {Math.Clamp(config.CaveMaskBrightnessPercent, 50, 150)}% · default {DefaultCaveMaskBrightnessPercent}%"
            )
        );
    }

    internal const int DefaultAtlasExposurePercent = 150;
    internal const int DefaultCaveMaskBrightnessPercent = 100;
    internal const int DefaultMapLayerOpacityPercent = 75;

    private bool OnAtlasExposureChanged(int value)
    {
        config.AtlasExposurePercent = Math.Clamp(value, 50, 150);
        saveConfig();
        return true;
    }

    private bool OnCaveMaskBrightnessChanged(int value)
    {
        config.CaveMaskBrightnessPercent = Math.Clamp(value, 50, 150);
        saveConfig();
        return true;
    }

    private bool ResetVisualTuning()
    {
        config.AtlasExposurePercent = DefaultAtlasExposurePercent;
        config.MapLayerOpacityPercent = DefaultMapLayerOpacityPercent;
        config.CaveMaskBrightnessPercent = DefaultCaveMaskBrightnessPercent;
        saveConfig();
        SyncVisualLabControls();
        return true;
    }

    private void OnRenderOnScrollToggled(bool enabled)
    {
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

    private void OnMapLayersToggled(bool enabled)
    {
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
        if (!CreativeCheatSettingsAvailable) return;

        config.CaveModeEnabled = enabled;
        selectedEntityId = null;
        PrepareSurfaceSafetyFilter();
        saveConfig();
        SyncCreativeSettingsControls();
    }

    private void OnSearchModeToggled(bool enabled)
    {
        if (!CreativeCheatSettingsAvailable)
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
        if (!CreativeCheatSettingsAvailable) return;

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
        if (synchronizingPerformanceControls) return;
        config.PerformanceLightingEnabled = enabled;
        saveConfig();
        SyncPerformanceControls();
    }

    private void OnHideVegetationToggled(bool enabled)
    {
        if (synchronizingPerformanceControls) return;
        config.HideVegetation = enabled;
        saveConfig();
        SyncPerformanceControls();
    }

    private void OnSkipOpeningAnimationToggled(bool enabled)
    {
        config.SkipOpeningAnimation = enabled;
        saveConfig();
        SyncSettingsControls();
    }

    private void OnCloudsToggled(bool enabled)
    {
        config.CloudsEnabled = enabled;
        saveConfig();
        SyncSettingsControls();
    }

    private void OnLivingEntitiesToggled(bool enabled)
    {
        config.LivingEntitiesEnabled = enabled;
        SaveEntitySettings();
    }

    private void OnPlayersToggled(bool enabled)
    {
        config.ShowPlayers = enabled;
        SaveEntitySettings();
    }

    private void OnAnimalsToggled(bool enabled)
    {
        config.ShowAnimals = enabled;
        SaveEntitySettings();
    }

    private void OnMobsToggled(bool enabled)
    {
        config.ShowMobs = enabled;
        SaveEntitySettings();
    }

    private void OnNpcsToggled(bool enabled)
    {
        config.ShowNpcs = enabled;
        SaveEntitySettings();
    }

    private void OnLiveLightingToggled(bool enabled)
    {
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
        config.FixedSunHour = Math.Clamp(hour, 0, 24);
        config.LiveLightingEnabled = false;
        config.PerformanceLightingEnabled = true;
        saveConfig();
        SyncSettingsControls();
        SyncPerformanceControls();
        return true;
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
        visibleEntityPolicy.ShowPlayers = masterEnabled
            && config.ShowPlayers
            && (capi.IsSinglePlayer || serverPolicy.ShowPlayers);
        visibleEntityPolicy.ShowAnimals = masterEnabled
            && config.ShowAnimals
            && (capi.IsSinglePlayer || serverPolicy.ShowAnimals);
        visibleEntityPolicy.ShowMobs = masterEnabled
            && config.ShowMobs
            && (capi.IsSinglePlayer || serverPolicy.ShowMobs);
        visibleEntityPolicy.ShowNpcs = masterEnabled
            && config.ShowNpcs
            && (capi.IsSinglePlayer || serverPolicy.ShowNpcs);
    }

    private void SyncSettingsControls()
    {
        if (settingsModal == null) return;
        settingsModal.GetAtlasSwitch("render-on-scroll")?.SetValue(
            presentationChangeCoordinator.DisplayedValue(config.RenderOnScroll)
        );
        settingsModal.GetAtlasSwitch("scroll-realtime-weather")?.SetValue(
            config.ScrollRealtimeWeatherEnabled
        );
        settingsModal.GetAtlasChoice("handheld-instrument")?.SetSelectedIndex(
            HandheldInstrumentChoiceIndex
        );
        settingsModal.GetAtlasSwitch("map-layers")?.SetValue(config.MapLayersEnabled);
        settingsModal.GetAtlasSwitch("search-mode")?.SetValue(
            CreativeCheatSettingsAvailable && config.SearchModeEnabled
        );
        settingsModal.GetAtlasSwitch("search-mode")!.Enabled =
            CreativeCheatSettingsAvailable;
        settingsModal.GetAtlasSwitch("animations")?.SetValue(config.AnimationsEnabled);
        // The switch shows the stored preference in every mode; actual Creative
        // only suppresses its effect, so the value survives a mode change.
        settingsModal.GetAtlasSwitch("close-on-damage")?.SetValue(
            config.CloseAtlasOnDamage
        );
        settingsModal.GetAtlasSwitch("skip-opening-animation")?.SetValue(
            config.SkipOpeningAnimation
        );
        bool liveCloudsAvailable =
            VolumetricCloudRendererAdapter.IsEnabledByGraphicsSettings(capi);
        settingsModal.GetAtlasSwitch("clouds")?.SetValue(
            liveCloudsAvailable && config.CloudsEnabled
        );
        settingsModal.GetAtlasSwitch("clouds")!.Enabled = liveCloudsAvailable;
        bool solarLightingActive = config.PerformanceLightingEnabled;
        settingsModal.GetAtlasSwitch("live-lighting")?.SetValue(
            solarLightingActive && config.LiveLightingEnabled
        );
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
            !solarLightingActive || !config.LiveLightingEnabled;
        bool serverAllowsAny = capi.IsSinglePlayer || serverPolicy.AnyEntityModels;
        settingsModal.GetAtlasSwitch("entities")?.SetValue(config.LivingEntitiesEnabled && serverAllowsAny);
        settingsModal.GetAtlasSwitch("entities")!.Enabled = serverAllowsAny;
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
            performanceModal.GetAtlasSwitch("performance-lighting")?.SetValue(
                config.PerformanceLightingEnabled
            );
            performanceModal.GetAtlasSwitch("hide-vegetation")?.SetValue(
                config.HideVegetation
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
        creativeSettingsModal.GetAtlasSwitch("cave-mode")!.Enabled = available;
        creativeSettingsModal.GetAtlasSwitch("camera-angle-lock")!.Enabled = available;
    }

    private void SyncEntityCategorySwitch(string key, bool clientEnabled, bool serverEnabled)
    {
        bool categoryAllowed = capi.IsSinglePlayer || serverEnabled;
        settingsModal.GetAtlasSwitch(key)?.SetValue(clientEnabled && categoryAllowed);
        settingsModal.GetAtlasSwitch(key)!.Enabled = config.LivingEntitiesEnabled && categoryAllowed;
    }

}
