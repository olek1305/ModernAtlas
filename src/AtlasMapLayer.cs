namespace ModernAtlas;

/// <summary>
/// Reversible color overlays applied only to the exact block geometry drawn
/// by the atlas. None of these layers creates replacement terrain.
/// </summary>
internal enum AtlasMapLayer
{
    TexturedTerrain,
    SoilFertility,
    Moisture,
    Temperature,
    ForestDensity,
    GeologicActivity,
    OreDensity
}

internal static class AtlasMapLayerInfo
{
    public static readonly string[] Values =
    {
        "textured",
        "fertility",
        "moisture",
        "temperature",
        "forest",
        "geologic",
        "ore"
    };

    public static readonly string[] Names =
    {
        "Textured terrain",
        "Soil fertility",
        "Moisture",
        "Temperature",
        "Forest density",
        "Geologic activity",
        "Ore density (Cheat/Creative)"
    };

    public static string DisplayName(this AtlasMapLayer layer) => layer switch
    {
        AtlasMapLayer.TexturedTerrain => "Textured terrain",
        AtlasMapLayer.SoilFertility => "Soil fertility",
        AtlasMapLayer.Moisture => "Moisture",
        AtlasMapLayer.Temperature => "Temperature",
        AtlasMapLayer.ForestDensity => "Forest density",
        AtlasMapLayer.GeologicActivity => "Geologic activity",
        AtlasMapLayer.OreDensity => "Ore density",
        _ => "Textured terrain"
    };

    public static string Legend(this AtlasMapLayer layer) => layer switch
    {
        AtlasMapLayer.SoilFertility => "barren → fertile",
        AtlasMapLayer.Moisture => "dry → wet",
        AtlasMapLayer.Temperature => "cold → hot",
        AtlasMapLayer.ForestDensity => "open → dense",
        AtlasMapLayer.GeologicActivity => "quiet → active",
        AtlasMapLayer.OreDensity => "low → high potential",
        _ => "live block materials"
    };

    public static bool RequiresSpoilerAccess(this AtlasMapLayer layer) =>
        layer == AtlasMapLayer.OreDensity;

    public static AtlasMapLayer FromValue(string? value)
    {
        for (int index = 0; index < Values.Length; index++)
        {
            if (string.Equals(Values[index], value, System.StringComparison.Ordinal))
            {
                return (AtlasMapLayer)index;
            }
        }
        return AtlasMapLayer.TexturedTerrain;
    }
}
