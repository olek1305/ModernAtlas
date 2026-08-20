using System;
using System.Collections.Generic;

namespace ModernAtlas;

/// <summary>Client-only atlas preferences.</summary>
public sealed class ModernAtlasConfig
{
    private Dictionary<string, bool> cheatModeByWorld = new();
    private int screenshotFilterIntensityPercent = 100;
    private int screenshotSaturationPercent = 100;
    private int screenshotContrastPercent = 100;
    private int screenshotTemperaturePercent;
    private int screenshotShadowTintStrengthPercent;
    private int screenshotRedBalancePercent = 100;
    private int screenshotGreenBalancePercent = 100;
    private int screenshotBlueBalancePercent = 100;
    private int screenshotShadowRedPercent = 100;
    private int screenshotShadowGreenPercent = 100;
    private int screenshotShadowBluePercent = 100;
    private int screenshotHighlightRedPercent = 100;
    private int screenshotHighlightGreenPercent = 100;
    private int screenshotHighlightBluePercent = 100;
    private int screenshotAmbientOcclusionPercent;
    private int screenshotIndirectLightPercent;
    private int screenshotBloomPercent;

    public bool AnimationsEnabled { get; set; } = true;
    public bool ShowPlayerCompass { get; set; }
    public string HandheldInstrumentMode { get; set; } = "compass";
    public bool RenderOnScroll { get; set; } = true;
    public bool ScrollRealtimeWeatherEnabled { get; set; } = true;
    public bool SkipOpeningAnimation { get; set; }
    public bool CloudsEnabled { get; set; }
    public bool PerformanceLightingEnabled { get; set; } = true;
    public bool HideVegetation { get; set; }
    public bool LiveLightingEnabled { get; set; } = true;
    public int FixedSunHour { get; set; } = 12;
    public bool LivingEntitiesEnabled { get; set; } = true;
    public bool ShowPlayers { get; set; } = true;
    public bool ShowAnimals { get; set; } = true;
    public bool ShowMobs { get; set; } = true;
    public bool ShowNpcs { get; set; } = true;
    public bool MapLayersEnabled { get; set; } = false;
    public bool CaveModeEnabled { get; set; } = true;
    public bool SearchModeEnabled { get; set; }
    public bool CameraAngleLocked { get; set; }
    public bool CloseAtlasOnDamage { get; set; } = true;
    public int AtlasExposurePercent { get; set; } = 150;
    public int MapLayerOpacityPercent { get; set; } = 75;
    public int CaveMaskBrightnessPercent { get; set; } = 100;

    /// <summary>
    /// Tiled screenshot grid size (1-8): the current view is captured as an
    /// N-by-N camera grid and stitched into one high-resolution image.
    /// </summary>
    public int ScreenshotScale { get; set; } = 2;

    /// <summary>
    /// Linear capture area as a percentage of the current atlas view. The
    /// supported choices are 100, 75, 50 and 25; the selected area is always
    /// centered and does not change the saved image dimensions.
    /// </summary>
    public int ScreenshotCaptureAreaPercent { get; set; } = 100;

    /// <summary>
    /// Screenshot-only GPU grading. This is deliberately separate from the
    /// atlas visual lab: interactive atlas frames and the ordinary world must
    /// remain unfiltered.
    /// </summary>
    public bool ScreenshotFiltersEnabled { get; set; }
    public string ScreenshotFilterPreset { get; set; } = "natural";
    public int ScreenshotFilterIntensityPercent
    {
        get => screenshotFilterIntensityPercent;
        set => screenshotFilterIntensityPercent = Math.Clamp(value, 0, 200);
    }
    public int ScreenshotSaturationPercent
    {
        get => screenshotSaturationPercent;
        set => screenshotSaturationPercent = Math.Clamp(value, 0, 200);
    }
    public int ScreenshotContrastPercent
    {
        get => screenshotContrastPercent;
        set => screenshotContrastPercent = Math.Clamp(value, 0, 200);
    }
    public int ScreenshotTemperaturePercent
    {
        get => screenshotTemperaturePercent;
        set => screenshotTemperaturePercent = Math.Clamp(value, -100, 100);
    }
    public int ScreenshotShadowTintStrengthPercent
    {
        get => screenshotShadowTintStrengthPercent;
        set => screenshotShadowTintStrengthPercent = Math.Clamp(value, 0, 100);
    }
    public int ScreenshotRedBalancePercent
    {
        get => screenshotRedBalancePercent;
        set => screenshotRedBalancePercent = Math.Clamp(value, 50, 150);
    }
    public int ScreenshotGreenBalancePercent
    {
        get => screenshotGreenBalancePercent;
        set => screenshotGreenBalancePercent = Math.Clamp(value, 50, 150);
    }
    public int ScreenshotBlueBalancePercent
    {
        get => screenshotBlueBalancePercent;
        set => screenshotBlueBalancePercent = Math.Clamp(value, 50, 150);
    }
    public int ScreenshotShadowRedPercent
    {
        get => screenshotShadowRedPercent;
        set => screenshotShadowRedPercent = Math.Clamp(value, 50, 150);
    }
    public int ScreenshotShadowGreenPercent
    {
        get => screenshotShadowGreenPercent;
        set => screenshotShadowGreenPercent = Math.Clamp(value, 50, 150);
    }
    public int ScreenshotShadowBluePercent
    {
        get => screenshotShadowBluePercent;
        set => screenshotShadowBluePercent = Math.Clamp(value, 50, 150);
    }
    public int ScreenshotHighlightRedPercent
    {
        get => screenshotHighlightRedPercent;
        set => screenshotHighlightRedPercent = Math.Clamp(value, 50, 150);
    }
    public int ScreenshotHighlightGreenPercent
    {
        get => screenshotHighlightGreenPercent;
        set => screenshotHighlightGreenPercent = Math.Clamp(value, 50, 150);
    }
    public int ScreenshotHighlightBluePercent
    {
        get => screenshotHighlightBluePercent;
        set => screenshotHighlightBluePercent = Math.Clamp(value, 50, 150);
    }
    public int ScreenshotAmbientOcclusionPercent
    {
        get => screenshotAmbientOcclusionPercent;
        set => screenshotAmbientOcclusionPercent = Math.Clamp(value, 0, 100);
    }
    public int ScreenshotIndirectLightPercent
    {
        get => screenshotIndirectLightPercent;
        set => screenshotIndirectLightPercent = Math.Clamp(value, 0, 100);
    }
    public int ScreenshotBloomPercent
    {
        get => screenshotBloomPercent;
        set => screenshotBloomPercent = Math.Clamp(value, 0, 100);
    }

    /// <summary>
    /// Stores the player's explicit spoiler decision per save without writing
    /// anything to the Vintage Story save or map database.
    /// </summary>
    public Dictionary<string, bool> CheatModeByWorld
    {
        get => cheatModeByWorld;
        set => cheatModeByWorld = value ?? new Dictionary<string, bool>();
    }
}
