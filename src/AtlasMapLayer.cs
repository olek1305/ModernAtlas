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
        "Ore analysis (Cheat/Creative)"
    };

    public static string DisplayName(this AtlasMapLayer layer) => layer switch
    {
        AtlasMapLayer.TexturedTerrain => "Textured terrain",
        AtlasMapLayer.SoilFertility => "Soil fertility",
        AtlasMapLayer.Moisture => "Moisture",
        AtlasMapLayer.Temperature => "Temperature",
        AtlasMapLayer.OreDensity => "Ore analysis",
        _ => "Textured terrain"
    };

    public static string Legend(this AtlasMapLayer layer) => layer switch
    {
        AtlasMapLayer.SoilFertility => "barren → fertile",
        AtlasMapLayer.Moisture => "dry → wet",
        AtlasMapLayer.Temperature => "cold → hot",
        AtlasMapLayer.OreDensity => "dark blue: unavailable • blue: none → high",
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

    /// <summary>
    /// One line naming what the layer measures and, where it matters, what it
    /// deliberately is not.
    /// </summary>
    public static string Description(this AtlasMapLayer layer) => layer switch
    {
        AtlasMapLayer.SoilFertility => "World-generation soil fertility",
        AtlasMapLayer.Moisture => "World-generation rainfall, not current weather",
        AtlasMapLayer.Temperature => "World-generation base temperature, not current weather",
        AtlasMapLayer.OreDensity => "Regional ore potential where mapped, otherwise loaded block columns",
        _ => "Live registered block shapes and materials"
    };

    /// <summary>
    /// Labels for the low, middle and high end of the legend ramp. The
    /// temperature stops are the real endpoints of its normalization.
    /// </summary>
    public static (string Low, string Middle, string High) LegendStops(
        this AtlasMapLayer layer
    ) => layer switch
    {
        AtlasMapLayer.SoilFertility => ("Barren", "Average", "Fertile"),
        AtlasMapLayer.Moisture => ("Dry", "Moderate", "Wet"),
        AtlasMapLayer.Temperature => ("-20 °C", "10 °C", "40 °C"),
        AtlasMapLayer.OreDensity => OreLegendStops(AtlasOreLayerSource.RegionalPotential),
        _ => ("", "", "")
    };

    /// <summary>
    /// The ore ramp means two different things. Regional maps carry a graded
    /// potential; a loaded block column only carries how many ore blocks the
    /// atlas actually saw in that column.
    /// </summary>
    public static (string Low, string Middle, string High) OreLegendStops(
        AtlasOreLayerSource source
    ) => source switch
    {
        AtlasOreLayerSource.RegionalPotential => ("Below trace", "High", "Ultra high"),
        AtlasOreLayerSource.LoadedColumns => ("No ore seen", "Few blocks", "Dense"),
        // Mixed radii paint both meanings, so the stops stay neutral and the
        // caption names the two sources.
        _ => ("None", "Moderate", "Strongest")
    };

    public static bool HasLegendRamp(this AtlasMapLayer layer) =>
        layer != AtlasMapLayer.TexturedTerrain;

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
