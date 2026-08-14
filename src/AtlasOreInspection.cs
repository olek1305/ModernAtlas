using System;

namespace ModernAtlas;

internal enum AtlasOreInspectionSource
{
    RegionalOreMaps,
    LoadedBlockColumn
}

internal readonly record struct AtlasOreReading(
    string Code,
    float Potential,
    int BlockCount,
    bool IsPotential
);

internal sealed record AtlasOreInspection(
    int WorldX,
    int WorldZ,
    int SurfaceY,
    string? HostRockCode,
    AtlasOreInspectionSource Source,
    int SourceMapCount,
    AtlasOreReading[] Readings
);

internal static class AtlasOrePotential
{
    public const float TraceThreshold = 0.002f;
    public const float CategorizedThreshold = 0.025f;

    public static string Grade(float potential)
    {
        potential = Math.Clamp(potential, 0f, 1f);
        if (potential < TraceThreshold) return "Below trace";
        if (potential <= CategorizedThreshold) return "Trace";

        int index = Math.Clamp((int)(potential * 7.5f), 0, 5);
        return index switch
        {
            0 => "Very poor",
            1 => "Poor",
            2 => "Decent",
            3 => "High",
            4 => "Very high",
            _ => "Ultra high"
        };
    }

    internal static bool ValidateThresholds() =>
        Grade(0.001f) == "Below trace"
        && Grade(0.002f) == "Trace"
        && Grade(0.025f) == "Trace"
        && Grade(0.03f) == "Very poor"
        && Grade(0.14f) == "Poor"
        && Grade(0.30f) == "Decent"
        && Grade(0.45f) == "High"
        && Grade(0.60f) == "Very high"
        && Grade(0.70f) == "Ultra high";
}
