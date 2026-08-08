using ProtoBuf;

namespace ModernAtlas;

/// <summary>
/// Small server-to-client policy packet. Its constructor is deliberately the
/// safe fallback used before a server packet arrives or on an unmodded server.
/// </summary>
[ProtoContract]
public sealed class ModernAtlasServerPolicy
{
    [ProtoMember(1)]
    public bool FogEnabled { get; set; } = true;

    [ProtoMember(2)]
    public bool ShowPlayers { get; set; }

    [ProtoMember(3)]
    public bool ShowAnimals { get; set; }

    [ProtoMember(4)]
    public bool ShowMobs { get; set; }

    [ProtoMember(5)]
    public bool ShowNpcs { get; set; }

    public bool AnyEntityModels => ShowPlayers || ShowAnimals || ShowMobs || ShowNpcs;

    public void CopyFrom(ModernAtlasServerPolicy policy)
    {
        FogEnabled = policy.FogEnabled;
        ShowPlayers = policy.ShowPlayers;
        ShowAnimals = policy.ShowAnimals;
        ShowMobs = policy.ShowMobs;
        ShowNpcs = policy.ShowNpcs;
    }

    public void ResetToSafeDefaults()
    {
        FogEnabled = true;
        ShowPlayers = false;
        ShowAnimals = false;
        ShowMobs = false;
        ShowNpcs = false;
    }
}
