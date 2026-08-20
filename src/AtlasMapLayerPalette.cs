using System;

namespace ModernAtlas;

/// <summary>
/// The single source of truth for map-layer colors. The overlay texture and
/// every legend read the same ramp, so a swatch shown in the panel is exactly
/// the color the atlas paints for that value.
/// </summary>
internal static class AtlasMapLayerPalette
{
    /// <summary>
    /// Color for a normalized layer value. The contrast expansion applied to
    /// the rendered overlay is part of the ramp so legends cannot drift away
    /// from the map.
    /// </summary>
    public static (float R, float G, float B) Color(AtlasMapLayer layer, float value)
    {
        value = ExpandContrast(Math.Clamp(value, 0f, 1f));
        return layer switch
        {
            AtlasMapLayer.SoilFertility => ThreeStop(
                value,
                (0.30f, 0.08f, 0.025f),
                (1.00f, 0.66f, 0.025f),
                (0.025f, 0.90f, 0.15f)
            ),
            AtlasMapLayer.Moisture => ThreeStop(
                value,
                (1.00f, 0.29f, 0.015f),
                (0.02f, 0.88f, 0.75f),
                (0.015f, 0.14f, 1.00f)
            ),
            AtlasMapLayer.Temperature => ThreeStop(
                value,
                (0.015f, 0.27f, 1.00f),
                (1.00f, 0.91f, 0.04f),
                (1.00f, 0.035f, 0.015f)
            ),
            AtlasMapLayer.OreDensity => ThreeStop(
                value,
                (0.015f, 0.16f, 1.00f),
                (0.91f, 0.10f, 0.58f),
                (1.00f, 0.94f, 0.17f)
            ),
            _ => (0f, 0f, 0f)
        };
    }

    /// <summary>
    /// The distinct state used for terrain that is drawn but whose data could
    /// not be read. It must never look like a value on the ramp.
    /// </summary>
    public static (float R, float G, float B) Unavailable => (0.055f, 0.10f, 0.30f);

    public static float ExpandContrast(float value)
    {
        float expanded = Math.Clamp((value - 0.5f) * 1.75f + 0.5f, 0f, 1f);
        return expanded * expanded * (3f - 2f * expanded);
    }

    private static (float R, float G, float B) ThreeStop(
        float value,
        (float R, float G, float B) low,
        (float R, float G, float B) middle,
        (float R, float G, float B) high
    ) => value <= 0.5f
        ? Lerp(low, middle, value * 2f)
        : Lerp(middle, high, (value - 0.5f) * 2f);

    private static (float R, float G, float B) Lerp(
        (float R, float G, float B) from,
        (float R, float G, float B) to,
        float amount
    ) =>
    (
        from.R + (to.R - from.R) * amount,
        from.G + (to.G - from.G) * amount,
        from.B + (to.B - from.B) * amount
    );
}
