using ProtoBuf;

namespace ModernAtlas;

/// <summary>
/// Server-authorized request for one client to apply a local ModernAtlas
/// preset. The server never edits the client's config itself.
/// </summary>
[ProtoContract]
public sealed class AtlasPresetMessage
{
    [ProtoMember(1)]
    public string Preset { get; set; } = "";
}
