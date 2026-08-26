using System;

namespace ModernAtlas;

/// <summary>
/// Maps the user-facing atlas exposure slider to the atlas-only lighting
/// multiplier. The slider is intentionally not a raw percentage: the old
/// default of 150% was visually neutral, so the new neutral point is 100%.
/// The supported range remains 50..150%; its lower endpoint is deliberately
/// mapped to a useful 1.0 multiplier instead of the old dark 0.5 output.
/// </summary>
internal static class AtlasExposureCalibration
{
    public const int MinimumPercent = 50;
    public const int MaximumPercent = 150;
    public const int DefaultPercent = 100;
    public const int SliderStepPercent = 5;

    // The old implementation passed the displayed value / 100 directly to
    // lighting. Consequently its 150% default produced a 1.5 multiplier.
    // Keep that exact neutral multiplier at the new 100% point, while making
    // the supported 50% endpoint useful and leaving headroom for a genuinely
    // brighter 150% setting.
    public const float MinimumMultiplier = 1.0f;
    public const float NeutralMultiplier = 1.5f;
    public const float MaximumMultiplier = 2.0f;

    public static int ClampPercent(int percent) =>
        Math.Clamp(percent, MinimumPercent, MaximumPercent);

    public static float ToMultiplier(int percent)
    {
        int clampedPercent = ClampPercent(percent);
        return NeutralMultiplier
            + (clampedPercent - DefaultPercent) / 100f;
    }

    public static float ClampMultiplier(float multiplier) =>
        Math.Clamp(multiplier, MinimumMultiplier, MaximumMultiplier);

    /// <summary>
    /// Pure coverage for the recalibration anchors and config normalization.
    /// This does not rescale persisted values: an existing 100 remains 100,
    /// so users who already selected the intended neutral point do not get a
    /// second unexpected adjustment.
    /// </summary>
    public static string? Validate()
    {
        ModernAtlasConfig defaultConfig = new();
        if (DefaultPercent != 100
            || defaultConfig.AtlasExposurePercent != DefaultPercent
            || ClampPercent(0) != MinimumPercent
            || ClampPercent(999) != MaximumPercent
            || Math.Abs(ToMultiplier(50) - 1.0f) > 0.0001f
            || Math.Abs(ToMultiplier(DefaultPercent) - NeutralMultiplier) > 0.0001f
            || Math.Abs(ToMultiplier(MaximumPercent) - MaximumMultiplier) > 0.0001f)
        {
            return "atlas exposure calibration anchors are invalid";
        }

        float previous = ToMultiplier(MinimumPercent);
        for (int percent = MinimumPercent + 1; percent <= MaximumPercent; percent++)
        {
            float current = ToMultiplier(percent);
            if (current <= previous)
            {
                return "atlas exposure calibration is not strictly monotonic";
            }

            previous = current;
        }

        ModernAtlasConfig normalizedConfig = new()
        {
            AtlasExposurePercent = -1
        };
        if (!normalizedConfig.NormalizeAtlasExposure()
            || normalizedConfig.AtlasExposurePercent != MinimumPercent
            || normalizedConfig.NormalizeAtlasExposure())
        {
            return "atlas exposure config normalization is not idempotent";
        }

        return null;
    }
}
