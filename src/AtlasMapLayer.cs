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
        "ore"
    };

    public static readonly string[] Names =
    {
        "Textured terrain",
        "Soil fertility",
        "Moisture",
        "Temperature",
        "Ore density (Cheat/Creative)"
    };

    public static string DisplayName(this AtlasMapLayer layer) => layer switch
    {
        AtlasMapLayer.TexturedTerrain => "Textured terrain",
        AtlasMapLayer.SoilFertility => "Soil fertility",
        AtlasMapLayer.Moisture => "Moisture",
        AtlasMapLayer.Temperature => "Temperature",
        AtlasMapLayer.OreDensity => "Ore density",
        _ => "Textured terrain"
    };

    public static string Legend(this AtlasMapLayer layer) => layer switch
    {
        AtlasMapLayer.SoilFertility => "barren → fertile",
        AtlasMapLayer.Moisture => "dry → wet",
        AtlasMapLayer.Temperature => "cold → hot",
        AtlasMapLayer.OreDensity => "dark blue: unavailable • blue: none → high potential",
        _ => "live block materials"
    };

    public static string DetailedLegend(this AtlasMapLayer layer) => layer switch
    {
        AtlasMapLayer.SoilFertility => "Barren  •  Average  •  Fertile",
        AtlasMapLayer.Moisture => "Dry  •  Temperate  •  Wet",
        AtlasMapLayer.Temperature => "Cold  •  Mild  •  Hot",
        AtlasMapLayer.OreDensity =>
            "Dark blue: unavailable · Blue: none/below trace · Trace · Very poor · Poor · Decent · High · Very high · Ultra high",
        _ => "Live registered block shapes and materials"
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
