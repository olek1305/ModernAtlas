namespace ModernAtlas;

internal enum AtlasSearchResultKind
{
    Block,
    Player,
    Animal,
    Mob,
    Npc,
    DroppedItem
}

/// <summary>
/// A lightweight marker for a match found in data already loaded by the
/// client. Search results never own world objects or persistent terrain data.
/// </summary>
internal readonly record struct AtlasSearchResult(
    double X,
    double Y,
    double Z,
    AtlasSearchResultKind Kind,
    string Label
);
