using System;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>Curated, neutral fog colors used only inside the atlas.</summary>
internal static class AtlasVisualPalettes
{
    public static readonly string[] Values =
    {
        "neutral",
        "cool",
        "warm",
        "slate"
    };

    public static readonly string[] Names =
    {
        "Neutral gray",
        "Cool mist",
        "Warm haze",
        "Dark slate"
    };

    public static int IndexOf(string? value)
    {
        for (int index = 0; index < Values.Length; index++)
        {
            if (string.Equals(Values[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return 0;
    }

    public static Vec3f FogColor(string? value) => value switch
    {
        "cool" => new Vec3f(0.29f, 0.37f, 0.44f),
        "warm" => new Vec3f(0.40f, 0.36f, 0.31f),
        "slate" => new Vec3f(0.14f, 0.18f, 0.21f),
        _ => new Vec3f(0.32f, 0.38f, 0.40f)
    };
}
