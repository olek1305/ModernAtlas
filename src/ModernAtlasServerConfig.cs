namespace ModernAtlas;

/// <summary>
/// Server-owned atlas disclosure policy. Living models are opt-in as a group;
/// the category flags allow an administrator to disable only one kind after
/// opting in. Fog remains enabled and multiplayer Cheat Mode remains denied
/// unless the server explicitly changes those defaults.
/// </summary>
public sealed class ModernAtlasServerConfig
{
    public bool FogEnabled { get; set; } = true;
    public bool CheatModeAllowed { get; set; }
    public bool LivingEntitiesEnabled { get; set; }
    public bool ShowPlayers { get; set; } = true;
    public bool ShowAnimals { get; set; } = true;
    public bool ShowMobs { get; set; } = true;
    public bool ShowNpcs { get; set; } = true;

    public ModernAtlasServerPolicy ToPolicy()
    {
        return new ModernAtlasServerPolicy
        {
            FogEnabled = FogEnabled,
            CheatModeAllowed = CheatModeAllowed,
            ShowPlayers = LivingEntitiesEnabled && ShowPlayers,
            ShowAnimals = LivingEntitiesEnabled && ShowAnimals,
            ShowMobs = LivingEntitiesEnabled && ShowMobs,
            ShowNpcs = LivingEntitiesEnabled && ShowNpcs
        };
    }
}
