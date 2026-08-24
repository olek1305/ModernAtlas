using System;

namespace ModernAtlas;

/// <summary>
/// Controls atlas-owned preparation after the player begins opening the atlas.
/// It never changes Vintage Story's view distance, requests world data or
/// schedules work while the atlas is closed.
/// </summary>
internal enum AtlasPerformanceMode
{
    OnDemand,
    HighThroughput
}

/// <summary>
/// Canonical values, parsing and the small first-frame budgets shared by the
/// atlas preparation stages. The focused atlas cadence intentionally remains
/// unchanged in both profiles; only preparation work is reduced on demand.
/// </summary>
internal static class AtlasPerformanceModeInfo
{
    public const string OnDemandValue = "on-demand";
    public const string HighThroughputValue = "high-throughput";
    public const string OnDemandLabel = "On-demand";
    public const string HighThroughputLabel = "High throughput";

    public static AtlasPerformanceMode Parse(string? value)
    {
        if (string.Equals(value, HighThroughputValue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "high", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "high-performance", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "prewarm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "prewarmed", StringComparison.OrdinalIgnoreCase))
        {
            return AtlasPerformanceMode.HighThroughput;
        }

        return AtlasPerformanceMode.OnDemand;
    }

    public static string CanonicalValue(AtlasPerformanceMode mode) =>
        mode == AtlasPerformanceMode.HighThroughput
            ? HighThroughputValue
            : OnDemandValue;

    public static string Label(AtlasPerformanceMode mode) =>
        mode == AtlasPerformanceMode.HighThroughput
            ? HighThroughputLabel
            : OnDemandLabel;

    public static string Description(AtlasPerformanceMode mode) =>
        mode == AtlasPerformanceMode.HighThroughput
            ? "Larger atlas-only preparation budgets after opening starts; no work while closed."
            : "Smaller atlas-only preparation budgets; no work while closed.";

    /// <summary>
    /// This explicit invariant prevents a performance profile from turning
    /// into an ordinary-world scheduler again. Both modes remain dormant
    /// until the player starts opening the atlas.
    /// </summary>
    public static bool AllowsClosedAtlasWork(AtlasPerformanceMode mode)
    {
        _ = mode;
        return false;
    }

    public static bool PreparesAtlasDuringOpening(AtlasPerformanceMode mode) =>
        mode == AtlasPerformanceMode.HighThroughput;

    public static bool UsesCapturedTransitionBackground(
        AtlasPerformanceMode mode
    ) => mode == AtlasPerformanceMode.HighThroughput;

    /// <summary>
    /// Resolves both the configured profile and texture readiness. On-demand
    /// must return false even when a previous High snapshot is still resident,
    /// so a profile change can never expose stale ordinary-world pixels.
    /// </summary>
    public static bool CanUseOrdinaryWorldSnapshot(
        AtlasPerformanceMode mode,
        bool snapshotReady
    ) => UsesCapturedTransitionBackground(mode) && snapshotReady;

    public static AtlasPerformanceBudget Budget(AtlasPerformanceMode mode) =>
        mode == AtlasPerformanceMode.HighThroughput
            ? AtlasPerformanceBudget.HighThroughput
            : AtlasPerformanceBudget.OnDemand;

    /// <summary>
    /// Pure self-check used by the automated smoke test. Keep this free of
    /// client and rendering state so it also catches config migrations early.
    /// </summary>
    public static string? Validate()
    {
        if (Parse(null) != AtlasPerformanceMode.OnDemand
            || Parse("") != AtlasPerformanceMode.OnDemand
            || Parse("invalid") != AtlasPerformanceMode.OnDemand)
        {
            return "missing or invalid values must resolve to On-demand";
        }
        if (Parse("low") != AtlasPerformanceMode.OnDemand
            || Parse(OnDemandValue) != AtlasPerformanceMode.OnDemand)
        {
            return "low and on-demand aliases must resolve to On-demand";
        }
        if (Parse("high") != AtlasPerformanceMode.HighThroughput
            || Parse(HighThroughputValue) != AtlasPerformanceMode.HighThroughput)
        {
            return "high and high-throughput aliases must resolve to High throughput";
        }
        if (AllowsClosedAtlasWork(AtlasPerformanceMode.OnDemand)
            || AllowsClosedAtlasWork(AtlasPerformanceMode.HighThroughput))
        {
            return "both profiles must prohibit work while the atlas is closed";
        }
        if (PreparesAtlasDuringOpening(AtlasPerformanceMode.OnDemand)
            || !PreparesAtlasDuringOpening(AtlasPerformanceMode.HighThroughput))
        {
            return "only High throughput may prepare atlas resources during opening";
        }
        if (UsesCapturedTransitionBackground(AtlasPerformanceMode.OnDemand)
            || !UsesCapturedTransitionBackground(AtlasPerformanceMode.HighThroughput))
        {
            return "only High throughput may capture its transition background after opening starts";
        }
        if (CanUseOrdinaryWorldSnapshot(AtlasPerformanceMode.OnDemand, true))
        {
            return "On-demand must ignore a resident ordinary-world snapshot";
        }
        if (!CanUseOrdinaryWorldSnapshot(AtlasPerformanceMode.HighThroughput, true)
            || CanUseOrdinaryWorldSnapshot(AtlasPerformanceMode.HighThroughput, false))
        {
            return "High throughput snapshot use must follow texture readiness";
        }

        AtlasPerformanceBudget low = Budget(AtlasPerformanceMode.OnDemand);
        AtlasPerformanceBudget high = Budget(AtlasPerformanceMode.HighThroughput);
        if (low.FirstFrameWorkBudgetMilliseconds >= high.FirstFrameWorkBudgetMilliseconds
            || low.SurfaceMaximumChunks >= high.SurfaceMaximumChunks
            || low.OreWorkBudgetMilliseconds >= high.OreWorkBudgetMilliseconds
            || low.VegetationWorkBudgetMilliseconds >= high.VegetationWorkBudgetMilliseconds)
        {
            return "On-demand preparation budgets must be smaller than High throughput";
        }
        return null;
    }
}

internal readonly record struct AtlasPerformanceBudget(
    double FirstFrameWorkBudgetMilliseconds,
    int SurfaceMaximumChunks,
    double OreWorkBudgetMilliseconds,
    double VegetationWorkBudgetMilliseconds
)
{
    public static AtlasPerformanceBudget OnDemand => new(
        1,
        64,
        1,
        1
    );

    public static AtlasPerformanceBudget HighThroughput => new(
        4,
        512,
        4,
        4
    );
}
