using System.Collections.Generic;
using ProtoBuf;

namespace ModernAtlas;

/// <summary>
/// Server-owned snapshot used only to compose the operator policy panel.
/// Player entries contain -1 for inherit, 0 for deny and 1 for allow.
/// The first, empty-UID entry contains the effective server defaults.
/// </summary>
[ProtoContract]
public sealed class AtlasAdminPolicyState
{
    [ProtoMember(1)]
    public List<AtlasAdminPolicyTarget> Targets { get; set; } = new();

    [ProtoMember(2)]
    public string Status { get; set; } = "";
}

[ProtoContract]
public sealed class AtlasAdminPolicyTarget
{
    [ProtoMember(1)] public string PlayerUid { get; set; } = "";
    [ProtoMember(2)] public string PlayerName { get; set; } = "";
    [ProtoMember(3)] public int ClientSettings { get; set; }
    [ProtoMember(4)] public int HideVegetation { get; set; }
    [ProtoMember(5)] public int CreativeCheatTools { get; set; }
    [ProtoMember(6)] public int ShowPlayers { get; set; }
    [ProtoMember(7)] public int ShowAnimals { get; set; }
    [ProtoMember(8)] public int ShowMobs { get; set; }
    [ProtoMember(9)] public int ShowNpcs { get; set; }
}

/// <summary>
/// A requested update from an operator client. The server rechecks the
/// controlserver privilege, validates every value, persists the config and
/// redistributes effective per-player policies.
/// </summary>
[ProtoContract]
public sealed class AtlasAdminPolicyUpdate
{
    [ProtoMember(1)] public string PlayerUid { get; set; } = "";
    [ProtoMember(2)] public int ClientSettings { get; set; }
    [ProtoMember(3)] public int HideVegetation { get; set; }
    [ProtoMember(4)] public int CreativeCheatTools { get; set; }
    [ProtoMember(5)] public int ShowPlayers { get; set; }
    [ProtoMember(6)] public int ShowAnimals { get; set; }
    [ProtoMember(7)] public int ShowMobs { get; set; }
    [ProtoMember(8)] public int ShowNpcs { get; set; }
}
