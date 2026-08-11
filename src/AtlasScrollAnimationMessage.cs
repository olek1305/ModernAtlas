using ProtoBuf;

namespace ModernAtlas;

internal enum AtlasScrollPhase
{
    Stop,
    Retrieve,
    Handoff,
    HoldOpen,
    RollClosed,
    ReleaseRight,
    Stow
}

[ProtoContract]
internal sealed class AtlasScrollAnimationMessage
{
    [ProtoMember(1)]
    public string PlayerUid { get; set; } = "";

    [ProtoMember(2)]
    public AtlasScrollPhase Phase { get; set; }
}
