using System;

namespace ModernAtlas;

/// <summary>
/// The two user-facing bundles that can be applied without opening the atlas.
/// Presets only change ModernAtlas client preferences; they never touch
/// Vintage Story graphics settings or the world.
/// </summary>
internal enum AtlasPresetKind
{
    Low,
    High
}

internal static class AtlasPresetProfile
{
    public const string LowValue = "low";
    public const string HighValue = "high";

    public static bool TryParse(string? value, out AtlasPresetKind preset)
    {
        if (string.Equals(value, LowValue, StringComparison.OrdinalIgnoreCase))
        {
            preset = AtlasPresetKind.Low;
            return true;
        }

        if (string.Equals(value, HighValue, StringComparison.OrdinalIgnoreCase))
        {
            preset = AtlasPresetKind.High;
            return true;
        }

        preset = default;
        return false;
    }

    public static string Value(AtlasPresetKind preset) =>
        preset == AtlasPresetKind.High ? HighValue : LowValue;

    public static string Label(AtlasPresetKind preset) =>
        preset == AtlasPresetKind.High ? "High" : "Low";

    public static bool ApplyLow(ModernAtlasConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Apply(
            config,
            AtlasPerformanceModeInfo.OnDemandValue,
            AtlasDetailModeInfo.ReducedValue,
            animationsEnabled: false,
            performanceLightingEnabled: false,
            hideVegetation: true
        );
    }

    public static bool ApplyHigh(ModernAtlasConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Apply(
            config,
            AtlasPerformanceModeInfo.HighThroughputValue,
            AtlasDetailModeInfo.FullValue,
            animationsEnabled: true,
            performanceLightingEnabled: true,
            hideVegetation: false
        );
    }

    public static bool Apply(
        ModernAtlasConfig config,
        AtlasPresetKind preset
    ) => preset == AtlasPresetKind.High
        ? ApplyHigh(config)
        : ApplyLow(config);

    public static string AppliedMessage(AtlasPresetKind preset) =>
        preset == AtlasPresetKind.Low
            ? "Applied ModernAtlas Low profile (on-demand, reduced detail, animations off, atlas lighting off, vegetation hidden). If the atlas still does not open, lower Vintage Story view distance in the normal Graphics settings."
            : "Applied ModernAtlas High profile (high-throughput, full detail, animations on, atlas lighting on, vegetation visible).";

    public static string ServerRequestMessage(AtlasPresetKind preset) =>
        $"ModernAtlas {Label(preset)} profile request sent to your client.";

    private static bool Apply(
        ModernAtlasConfig config,
        string performanceMode,
        string atlasDetail,
        bool animationsEnabled,
        bool performanceLightingEnabled,
        bool hideVegetation
    )
    {
        bool changed = false;
        if (!string.Equals(config.PerformanceMode, performanceMode, StringComparison.Ordinal))
        {
            config.PerformanceMode = performanceMode;
            changed = true;
        }
        if (!string.Equals(config.AtlasDetail, atlasDetail, StringComparison.Ordinal))
        {
            config.AtlasDetail = atlasDetail;
            changed = true;
        }
        if (config.AnimationsEnabled != animationsEnabled)
        {
            config.AnimationsEnabled = animationsEnabled;
            changed = true;
        }
        if (config.PerformanceLightingEnabled != performanceLightingEnabled)
        {
            config.PerformanceLightingEnabled = performanceLightingEnabled;
            changed = true;
        }
        if (config.HideVegetation != hideVegetation)
        {
            config.HideVegetation = hideVegetation;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Pure policy coverage for command and network callers. This deliberately
    /// uses temporary config objects, so a smoke test cannot leave a saved
    /// preset behind.
    /// </summary>
    public static string? Validate()
    {
        if (!TryParse(LowValue, out AtlasPresetKind low)
            || low != AtlasPresetKind.Low
            || !TryParse(HighValue, out AtlasPresetKind high)
            || high != AtlasPresetKind.High
            || TryParse("invalid", out _))
        {
            return "preset command values must parse as low or high only";
        }

        ModernAtlasConfig lowConfig = new()
        {
            CloudsEnabled = true,
            HandheldInstrumentMode = "time",
            FixedSunHour = 7,
            MapLayersEnabled = true,
            AtlasExposurePercent = 123
        };
        bool lowChanged = ApplyLow(lowConfig);
        if (!lowChanged
            || lowConfig.PerformanceMode != AtlasPerformanceModeInfo.OnDemandValue
            || lowConfig.AtlasDetail != AtlasDetailModeInfo.ReducedValue
            || lowConfig.AnimationsEnabled
            || lowConfig.PerformanceLightingEnabled
            || !lowConfig.HideVegetation
            || !lowConfig.CloudsEnabled
            || lowConfig.HandheldInstrumentMode != "time"
            || lowConfig.FixedSunHour != 7
            || !lowConfig.MapLayersEnabled
            || lowConfig.AtlasExposurePercent != 123
            || ApplyLow(lowConfig))
        {
            return "Low preset values, idempotency, or unrelated preferences are invalid";
        }

        ModernAtlasConfig highConfig = new()
        {
            CloudsEnabled = false,
            HandheldInstrumentMode = "compass",
            FixedSunHour = 19,
            MapLayersEnabled = true,
            AtlasExposurePercent = 77,
            PerformanceMode = AtlasPerformanceModeInfo.OnDemandValue,
            AtlasDetail = AtlasDetailModeInfo.ReducedValue,
            AnimationsEnabled = false,
            PerformanceLightingEnabled = false,
            HideVegetation = true
        };
        bool highChanged = ApplyHigh(highConfig);
        if (!highChanged
            || highConfig.PerformanceMode != AtlasPerformanceModeInfo.HighThroughputValue
            || highConfig.AtlasDetail != AtlasDetailModeInfo.FullValue
            || !highConfig.AnimationsEnabled
            || !highConfig.PerformanceLightingEnabled
            || highConfig.HideVegetation
            || highConfig.CloudsEnabled
            || highConfig.HandheldInstrumentMode != "compass"
            || highConfig.FixedSunHour != 19
            || !highConfig.MapLayersEnabled
            || highConfig.AtlasExposurePercent != 77
            || ApplyHigh(highConfig))
        {
            return "High preset values, idempotency, or unrelated preferences are invalid";
        }

        return null;
    }
}
