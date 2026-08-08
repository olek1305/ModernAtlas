namespace ModernAtlas;

/// <summary>
/// Client-only atlas preferences. The radius limits rendering of meshes that
/// are already available to the client; it never requests world generation.
/// </summary>
public sealed class ModernAtlasConfig
{
    public const int DefaultRadius = 500;
    public const int MinimumRadius = 250;
    public const int MaximumRadius = 5000;

    public int RadiusBlocks { get; set; } = DefaultRadius;
    public bool FogEnabled { get; set; } = true;

    // Written while the atlas is open. If the process ends before the dialog
    // closes cleanly, the next launch restores the safe default radius.
    public bool AtlasSessionInProgress { get; set; }

    public void Validate()
    {
        RadiusBlocks = System.Math.Clamp(RadiusBlocks, MinimumRadius, MaximumRadius);
    }
}
