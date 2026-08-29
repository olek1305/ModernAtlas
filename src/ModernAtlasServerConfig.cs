using System;
using System.Collections.Generic;

namespace ModernAtlas;

/// <summary>
/// Server-owned atlas disclosure policy. Living models are opt-in as a group;
/// the category flags allow an administrator to disable only one kind after
/// opting in. Multiplayer Cheat Mode remains denied unless the server
/// explicitly changes that default. Settings are non-sensitive client
/// preferences and remain available by default, including when this mod is
/// installed only on the client; set <see cref="AllowClientSettings"/> to
/// false to lock the Settings hierarchy for multiplayer players.
/// </summary>
public sealed class ModernAtlasServerConfig
{
    public bool CheatModeAllowed { get; set; }
    public bool LivingEntitiesEnabled { get; set; }
    /// <summary>
    /// Allows multiplayer clients to open and change ModernAtlas Settings.
    /// The default preserves the client-only installation behavior.
    /// </summary>
    public bool AllowClientSettings { get; set; } = true;
    /// <summary>
    /// Allows multiplayer clients to hide vegetation in the atlas. Disabling
    /// this keeps plants, bushes and leaves visible without changing a
    /// player's saved local preference.
    /// </summary>
    public bool AllowHideVegetation { get; set; } = true;
    public bool ShowPlayers { get; set; } = true;
    public bool ShowAnimals { get; set; } = true;
    public bool ShowMobs { get; set; } = true;
    public bool ShowNpcs { get; set; } = true;
    /// <summary>
    /// Optional persistent exceptions keyed by the player's stable UID. A
    /// null member inherits the corresponding server default.
    /// </summary>
    public Dictionary<string, ModernAtlasPlayerPolicyOverride> PlayerOverrides
        { get; set; } = new(StringComparer.Ordinal);

    public ModernAtlasServerPolicy ToPolicy(string? playerUid = null)
    {
        ModernAtlasServerPolicy policy = new()
        {
            CheatModeAllowed = CheatModeAllowed,
            ClientSettingsLocked = !AllowClientSettings,
            HideVegetationLocked = !AllowHideVegetation,
            ShowPlayers = LivingEntitiesEnabled && ShowPlayers,
            ShowAnimals = LivingEntitiesEnabled && ShowAnimals,
            ShowMobs = LivingEntitiesEnabled && ShowMobs,
            ShowNpcs = LivingEntitiesEnabled && ShowNpcs
        };

        if (!string.IsNullOrWhiteSpace(playerUid)
            && PlayerOverrides.TryGetValue(
                playerUid,
                out ModernAtlasPlayerPolicyOverride? playerOverride
            ))
        {
            policy.CheatModeAllowed = playerOverride.CheatModeAllowed
                ?? policy.CheatModeAllowed;
            policy.ClientSettingsLocked = !(playerOverride.AllowClientSettings
                ?? !policy.ClientSettingsLocked);
            policy.HideVegetationLocked = !(playerOverride.AllowHideVegetation
                ?? !policy.HideVegetationLocked);
            policy.ShowPlayers = playerOverride.ShowPlayers ?? policy.ShowPlayers;
            policy.ShowAnimals = playerOverride.ShowAnimals ?? policy.ShowAnimals;
            policy.ShowMobs = playerOverride.ShowMobs ?? policy.ShowMobs;
            policy.ShowNpcs = playerOverride.ShowNpcs ?? policy.ShowNpcs;
        }

        return policy;
    }
}

public sealed class ModernAtlasPlayerPolicyOverride
{
    public string LastKnownPlayerName { get; set; } = "";
    public bool? CheatModeAllowed { get; set; }
    public bool? AllowClientSettings { get; set; }
    public bool? AllowHideVegetation { get; set; }
    public bool? ShowPlayers { get; set; }
    public bool? ShowAnimals { get; set; }
    public bool? ShowMobs { get; set; }
    public bool? ShowNpcs { get; set; }

    public bool HasAnyOverride =>
        CheatModeAllowed.HasValue
        || AllowClientSettings.HasValue
        || AllowHideVegetation.HasValue
        || ShowPlayers.HasValue
        || ShowAnimals.HasValue
        || ShowMobs.HasValue
        || ShowNpcs.HasValue;
}
