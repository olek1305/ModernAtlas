using ProtoBuf;

namespace ModernAtlas;

/// <summary>
/// Test-only request/response carried on the existing ModernAtlas policy
/// channel. The server is the only side allowed to apply or restore health.
/// </summary>
[ProtoContract]
internal sealed class ModernAtlasSmokeDamageMessage
{
    [ProtoMember(1)]
    public int RequestId { get; set; }

    [ProtoMember(2)]
    public ModernAtlasSmokeDamageOperation Operation { get; set; }

    [ProtoMember(3)]
    public float Amount { get; set; }

    [ProtoMember(4)]
    public float Health { get; set; }

    [ProtoMember(5)]
    public bool Accepted { get; set; }

    [ProtoMember(6)]
    public string Diagnostic { get; set; } = "";
}

internal enum ModernAtlasSmokeDamageOperation
{
    Apply = 1,
    Restore = 2
}
