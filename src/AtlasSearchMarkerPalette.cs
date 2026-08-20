using System;

namespace ModernAtlas;

/// <summary>
/// The single source of marker colors and category names for search results.
/// The map markers and the panel legend both read it, so a legend swatch can
/// never drift from the marker it explains.
/// </summary>
internal static class AtlasSearchMarkerPalette
{
    public static (float R, float G, float B) Color(AtlasSearchResultKind kind) => kind switch
    {
        AtlasSearchResultKind.Block => (1f, 0.72f, 0.18f),
        AtlasSearchResultKind.Player => (0.20f, 0.86f, 1f),
        AtlasSearchResultKind.Animal => (0.34f, 1f, 0.48f),
        AtlasSearchResultKind.Mob => (1f, 0.22f, 0.18f),
        AtlasSearchResultKind.Npc => (0.78f, 0.48f, 1f),
        AtlasSearchResultKind.DroppedItem => (1f, 0.46f, 0.12f),
        _ => (1f, 1f, 1f)
    };

    /// <summary>Plural category name used by the legend.</summary>
    public static string DisplayName(AtlasSearchResultKind kind) => kind switch
    {
        AtlasSearchResultKind.Block => "Blocks",
        AtlasSearchResultKind.Player => "Players",
        AtlasSearchResultKind.Animal => "Animals",
        AtlasSearchResultKind.Mob => "Hostile mobs",
        AtlasSearchResultKind.Npc => "NPCs",
        AtlasSearchResultKind.DroppedItem => "Dropped items",
        _ => "Other"
    };

    public static readonly AtlasSearchResultKind[] Order =
    {
        AtlasSearchResultKind.Block,
        AtlasSearchResultKind.Player,
        AtlasSearchResultKind.Animal,
        AtlasSearchResultKind.Mob,
        AtlasSearchResultKind.Npc,
        AtlasSearchResultKind.DroppedItem
    };

    /// <summary>
    /// Deterministic self-check: every kind must have a name and a color, and
    /// no two categories may share a color, otherwise the legend would explain
    /// two different markers with one swatch.
    /// </summary>
    public static string? Validate()
    {
        foreach (AtlasSearchResultKind kind in Order)
        {
            if (string.IsNullOrWhiteSpace(DisplayName(kind)))
            {
                return $"{kind} has no category name";
            }
        }
        for (int index = 0; index < Order.Length; index++)
        {
            (float R, float G, float B) left = Color(Order[index]);
            for (int other = index + 1; other < Order.Length; other++)
            {
                (float R, float G, float B) right = Color(Order[other]);
                float dr = left.R - right.R;
                float dg = left.G - right.G;
                float db = left.B - right.B;
                // Hostile mobs (red) and dropped items (orange) are the
                // closest pair in the shipped palette; the legend never relies
                // on color alone, every swatch carries its category name.
                if (MathF.Sqrt(dr * dr + dg * dg + db * db) < 0.20f)
                {
                    return $"{Order[index]} and {Order[other]} share a marker color";
                }
            }
        }
        return null;
    }
}
