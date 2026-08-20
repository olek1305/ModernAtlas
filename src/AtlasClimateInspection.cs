using System;

namespace ModernAtlas;

/// <summary>
/// One hovered climate sample, taken from the same loaded world-generation
/// data the layer already draws. <see cref="Natural"/> is the value in the
/// layer's own unit: a 0-1 fraction for fertility and rainfall, degrees
/// Celsius for temperature.
/// </summary>
internal readonly record struct AtlasClimateInspection(
    int WorldX,
    int WorldZ,
    int SurfaceY,
    AtlasMapLayer Layer,
    float Normalized,
    float Natural
)
{
    /// <summary>
    /// Named band for the sample, so the read-out never depends on color
    /// alone.
    /// </summary>
    public string Grade => Layer switch
    {
        AtlasMapLayer.SoilFertility => Band(
            Normalized,
            "Barren",
            "Poor",
            "Average",
            "Fertile",
            "Lush"
        ),
        AtlasMapLayer.Moisture => Band(
            Normalized,
            "Arid",
            "Dry",
            "Moderate",
            "Humid",
            "Wet"
        ),
        AtlasMapLayer.Temperature => Band(
            Normalized,
            "Freezing",
            "Cold",
            "Mild",
            "Warm",
            "Hot"
        ),
        _ => "Unknown"
    };

    /// <summary>
    /// The measured value in the layer's own unit.
    /// </summary>
    public string ValueText => Layer == AtlasMapLayer.Temperature
        ? FormattableString.Invariant($"{Natural:0.#} °C")
        : FormattableString.Invariant($"{Natural * 100f:0}%");

    private static string Band(
        float value,
        string lowest,
        string low,
        string middle,
        string high,
        string highest
    ) => value switch
    {
        < 0.2f => lowest,
        < 0.4f => low,
        < 0.6f => middle,
        < 0.8f => high,
        _ => highest
    };
}
