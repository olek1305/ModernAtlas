using System;

namespace ModernAtlas;

/// <summary>
/// The small dot the toolbar draws on its Layers button. It answers one
/// question at a glance: is the map showing live materials, or is a data
/// layer tinting it, and which one.
/// </summary>
internal static class AtlasToolbarLayerIndicator
{
    /// <summary>
    /// Neutral grey while textured terrain is active, otherwise the layer's
    /// own mid-ramp color so the dot matches what the map is painted with.
    /// </summary>
    public static (float R, float G, float B) Color(AtlasMapLayer layer) =>
        layer == AtlasMapLayer.TexturedTerrain
            ? (0.62f, 0.67f, 0.72f)
            : AtlasMapLayerPalette.Color(layer, 0.5f);

    public static bool IsNeutral(AtlasMapLayer layer) =>
        layer == AtlasMapLayer.TexturedTerrain;

    /// <summary>
    /// Deterministic self-check: the neutral state must be grey and every
    /// data layer must differ from it and from the other layers, so the dot
    /// cannot report two layers with the same color.
    /// </summary>
    public static string? Validate()
    {
        (float R, float G, float B) neutral = Color(AtlasMapLayer.TexturedTerrain);
        if (Math.Abs(neutral.R - neutral.G) > 0.12f || Math.Abs(neutral.G - neutral.B) > 0.12f)
        {
            return $"the neutral indicator must stay grey, got {neutral}";
        }
        if (!IsNeutral(AtlasMapLayer.TexturedTerrain))
        {
            return "textured terrain must report the neutral state";
        }

        AtlasMapLayer[] dataLayers =
        {
            AtlasMapLayer.SoilFertility,
            AtlasMapLayer.Moisture,
            AtlasMapLayer.Temperature,
            AtlasMapLayer.OreDensity
        };
        for (int index = 0; index < dataLayers.Length; index++)
        {
            if (IsNeutral(dataLayers[index]))
            {
                return $"{dataLayers[index]} must not report the neutral state";
            }
            (float R, float G, float B) color = Color(dataLayers[index]);
            if (Distance(color, neutral) < 0.20f)
            {
                return $"{dataLayers[index]} is too close to the neutral dot";
            }
            for (int other = index + 1; other < dataLayers.Length; other++)
            {
                if (Distance(color, Color(dataLayers[other])) < 0.20f)
                {
                    return $"{dataLayers[index]} and {dataLayers[other]} share an indicator color";
                }
            }
        }
        return null;
    }

    private static float Distance(
        (float R, float G, float B) left,
        (float R, float G, float B) right
    )
    {
        float dr = left.R - right.R;
        float dg = left.G - right.G;
        float db = left.B - right.B;
        return MathF.Sqrt(dr * dr + dg * dg + db * db);
    }
}
