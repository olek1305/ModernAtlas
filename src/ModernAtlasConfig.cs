namespace ModernAtlas;

/// <summary>Client-only atlas preferences.</summary>
public sealed class ModernAtlasConfig
{
    public bool FogEnabled { get; set; }
    public bool AnimationsEnabled { get; set; } = true;
    public bool CloudsEnabled { get; set; } = true;
    public bool LiveLightingEnabled { get; set; } = true;
    public int FixedSunHour { get; set; } = 12;
    public bool LivingEntitiesEnabled { get; set; } = true;
    public bool ShowPlayers { get; set; } = true;
    public bool ShowAnimals { get; set; } = true;
    public bool ShowMobs { get; set; } = true;
    public bool ShowNpcs { get; set; } = true;
}
