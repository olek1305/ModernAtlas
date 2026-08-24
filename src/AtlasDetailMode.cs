using System;

namespace ModernAtlas;

/// <summary>
/// Controls atlas-only texture sampling and cloud composition. It is an
/// independent visual axis from the atlas preparation performance profile.
/// </summary>
internal enum AtlasDetailMode
{
    Reduced,
    Full
}

internal static class AtlasDetailModeInfo
{
    public const string ReducedValue = "reduced";
    public const string FullValue = "full";
    public const string ReducedLabel = "Reduced";
    public const string FullLabel = "Full";

    public static AtlasDetailMode Parse(string? value)
    {
        if (string.Equals(value, ReducedValue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "low", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "coarse", StringComparison.OrdinalIgnoreCase))
        {
            return AtlasDetailMode.Reduced;
        }

        return AtlasDetailMode.Full;
    }

    public static string CanonicalValue(AtlasDetailMode mode) =>
        mode == AtlasDetailMode.Reduced ? ReducedValue : FullValue;

    public static string Label(AtlasDetailMode mode) =>
        mode == AtlasDetailMode.Reduced ? ReducedLabel : FullLabel;

    public static string Description(AtlasDetailMode mode) =>
        mode == AtlasDetailMode.Reduced
            ? "Coarser atlas-only texture sampling and no atlas clouds; world graphics unchanged."
            : "Current full atlas texture detail and selected atlas clouds.";

    public static int EffectiveTextureDetailReduction(AtlasDetailMode mode) =>
        mode == AtlasDetailMode.Reduced ? 2 : 0;

    public static bool EffectiveCloudsEnabled(
        AtlasDetailMode mode,
        bool selectedClouds
    ) => mode == AtlasDetailMode.Full && selectedClouds;

    /// <summary>
    /// Pure self-check for config migration and the atlas-only effective draw
    /// policy. This must not inspect or mutate Vintage Story state.
    /// </summary>
    public static string? Validate()
    {
        if (Parse(null) != AtlasDetailMode.Full
            || Parse("") != AtlasDetailMode.Full
            || Parse("invalid") != AtlasDetailMode.Full)
        {
            return "missing or invalid Atlas detail values must resolve to Full";
        }
        if (Parse(ReducedValue) != AtlasDetailMode.Reduced
            || Parse("low") != AtlasDetailMode.Reduced
            || Parse(FullValue) != AtlasDetailMode.Full)
        {
            return "Atlas detail values do not resolve to their expected profiles";
        }
        if (EffectiveTextureDetailReduction(AtlasDetailMode.Reduced) != 2
            || EffectiveTextureDetailReduction(AtlasDetailMode.Full) != 0)
        {
            return "Atlas detail texture reduction policy is incorrect";
        }
        if (EffectiveCloudsEnabled(AtlasDetailMode.Reduced, true)
            || EffectiveCloudsEnabled(AtlasDetailMode.Full, false)
            || !EffectiveCloudsEnabled(AtlasDetailMode.Full, true))
        {
            return "Atlas detail cloud policy is not independent and deterministic";
        }

        return null;
    }
}
