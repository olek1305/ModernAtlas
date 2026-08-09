using ProtoBuf;

namespace ModernAtlas;

[ProtoContract]
internal sealed class ModernAtlasRelightRequest
{
    [ProtoMember(1)]
    public int ViewDistanceBlocks { get; set; }
}

[ProtoContract]
internal sealed class ModernAtlasRelightProgress
{
    [ProtoMember(1)]
    public int Completed { get; set; }

    [ProtoMember(2)]
    public int Total { get; set; }

    [ProtoMember(3)]
    public bool Finished { get; set; }
}
