using ProtoBuf;

namespace ModernAtlas;

/// <summary>
/// Server-authorized request for one client to change its atlas Cheat Mode.
/// </summary>
[ProtoContract]
public sealed class AtlasCheatModeMessage
{
    [ProtoMember(1)]
    public bool Enabled { get; set; }
}
