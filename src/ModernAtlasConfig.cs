using System.Collections.Generic;

namespace ModernAtlas;

/// <summary>Client-only atlas preferences.</summary>
public sealed class ModernAtlasConfig
{
    private Dictionary<string, bool> cheatModeByWorld = new();

    public bool FogEnabled { get; set; }
    public bool AnimationsEnabled { get; set; } = true;
    public bool ShowPlayerCompass { get; set; }
    public string HandheldInstrumentMode { get; set; } = "compass";
    public bool RenderOnScroll { get; set; } = true;
    public bool SkipOpeningAnimation { get; set; }
    public bool CloudsEnabled { get; set; } = true;
    public int TextureDetailReduction { get; set; }
    public bool PerformanceLightingEnabled { get; set; } = true;
    public bool HideVegetation { get; set; }
    public bool LiveLightingEnabled { get; set; } = true;
    public int FixedSunHour { get; set; } = 12;
    public bool LivingEntitiesEnabled { get; set; } = true;
    public bool ShowPlayers { get; set; } = true;
    public bool ShowAnimals { get; set; } = true;
    public bool ShowMobs { get; set; } = true;
    public bool ShowNpcs { get; set; } = true;
    public bool MapLayersEnabled { get; set; }
    public bool CaveModeEnabled { get; set; } = true;
    public bool SearchModeEnabled { get; set; }
    public bool CameraAngleLocked { get; set; }
    public int AtlasExposurePercent { get; set; } = 100;
    public int MapLayerOpacityPercent { get; set; } = 75;
    public int BoundarySoftnessPercent { get; set; } = 100;
    public int CaveMaskBrightnessPercent { get; set; } = 100;
    public string FogPalette { get; set; } = "neutral";

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
