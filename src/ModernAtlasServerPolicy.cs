using ProtoBuf;

namespace ModernAtlas;

/// <summary>
/// Small server-to-client policy packet. Its constructor is deliberately the
/// safe fallback used before a server packet arrives or on an unmodded server.
/// </summary>
[ProtoContract]
public sealed class ModernAtlasServerPolicy
{
    /// <summary>
    /// The protobuf constructor is also the client-side fallback before a
    /// server packet arrives. Keep every authority-sensitive disclosure
    /// switch disabled here. Client Settings are intentionally represented by
    /// the inverse lock bit below so an older packet remains compatible with
    /// the client-only installation described in the README.
    /// </summary>
    public ModernAtlasServerPolicy()
    {
        ResetToSafeDefaults();
    }

    [ProtoMember(2)]
    public bool ShowPlayers { get; set; }

    [ProtoMember(3)]
    public bool ShowAnimals { get; set; }

    [ProtoMember(4)]
    public bool ShowMobs { get; set; }

    [ProtoMember(5)]
    public bool ShowNpcs { get; set; }

    [ProtoMember(6)]
    public bool CheatModeAllowed { get; set; }

    /// <summary>
    /// When true, the server owner has explicitly locked the non-sensitive
    /// ModernAtlas Settings hierarchy for multiplayer clients. This is an
    /// inverse flag on purpose: protobuf packets produced before this field
    /// existed, and an absent policy channel, leave it false and preserve the
    /// client-only Settings experience.
    /// </summary>
    [ProtoMember(7)]
    public bool ClientSettingsLocked { get; set; }

    /// <summary>
    /// Inverse for protobuf compatibility: old packets and client-only
    /// servers keep the existing Hide vegetation control available.
    /// </summary>
    [ProtoMember(8)]
    public bool HideVegetationLocked { get; set; }

    public bool AnyEntityModels => ShowPlayers || ShowAnimals || ShowMobs || ShowNpcs;

    public void CopyFrom(ModernAtlasServerPolicy policy)
    {
        ShowPlayers = policy.ShowPlayers;
        ShowAnimals = policy.ShowAnimals;
        ShowMobs = policy.ShowMobs;
        ShowNpcs = policy.ShowNpcs;
        CheatModeAllowed = policy.CheatModeAllowed;
        ClientSettingsLocked = policy.ClientSettingsLocked;
        HideVegetationLocked = policy.HideVegetationLocked;
    }

    public void ResetToSafeDefaults()
    {
        ShowPlayers = false;
        ShowAnimals = false;
        ShowMobs = false;
        ShowNpcs = false;
        CheatModeAllowed = false;
        ClientSettingsLocked = false;
        HideVegetationLocked = false;
    }
}
