using System;

namespace ModernAtlas;

/// <summary>
/// Composes the Ore analysis status line. It is separate and pure so the
/// mixed-source wording stays verifiable: a world whose loaded regions carry
/// no OreMaps can never produce that state in game.
/// </summary>
internal static class AtlasOreStatusText
{
    public static string Compose(
        bool failed,
        bool ready,
        int progressPercent,
        AtlasOreLayerSource source,
        int mapCount,
        int regionCount,
        int columnCount,
        int blockCount,
        string filterName,
        int gridSize
    )
    {
        if (failed) return "Unavailable · textured terrain remains active";
        if (!ready)
        {
            return FormattableString.Invariant(
                $"Preparing · {progressPercent}% · loaded data only"
            );
        }

        string regional = FormattableString.Invariant(
            $"Regional potential: {mapCount:n0} maps in {regionCount:n0} regions"
        );
        string columns = FormattableString.Invariant(
            $"Loaded columns: {columnCount:n0} columns, {blockCount:n0} observed blocks"
        );
        string grid = FormattableString.Invariant($"{gridSize} × {gridSize} grid");
        return source switch
        {
            AtlasOreLayerSource.RegionalPotential =>
                $"Ready · {regional} · Filter: {filterName} · {grid}",
            AtlasOreLayerSource.LoadedColumns =>
                $"Ready · {columns} · Filter: {filterName} · {grid}",
            // Both groups are reported: one found regional map must not hide
            // the loaded columns that filled the rest of the radius.
            AtlasOreLayerSource.Mixed =>
                $"Ready · Mixed sources · {regional} · {columns} · Filter: {filterName} · {grid}",
            _ => "Waiting for streamed data · no loaded ore data in range yet"
        };
    }

    /// <summary>
    /// Deterministic self-check of every state, including the mixed radius.
    /// Returns a failure description, or null when all cases hold.
    /// </summary>
    public static string? Validate()
    {
        string mixed = Compose(
            false, true, 100, AtlasOreLayerSource.Mixed,
            3, 2, 1842, 12006, "Cassiterite", 8
        );
        if (!mixed.Contains("Regional potential: 3 maps in 2 regions", StringComparison.Ordinal)
            || !mixed.Contains("Loaded columns: 1,842 columns, 12,006 observed blocks", StringComparison.Ordinal))
        {
            return $"a mixed radius must report both groups, got \"{mixed}\"";
        }
        if (!mixed.Contains("Filter: Cassiterite", StringComparison.Ordinal))
        {
            return $"the readable filter name is missing from \"{mixed}\"";
        }

        string regional = Compose(
            false, true, 100, AtlasOreLayerSource.RegionalPotential,
            3, 2, 0, 0, "All ores", 8
        );
        if (regional.Contains("Loaded columns", StringComparison.Ordinal)
            || !regional.Contains("Regional potential: 3 maps in 2 regions", StringComparison.Ordinal))
        {
            return $"a regional radius must report only its own group, got \"{regional}\"";
        }

        string columns = Compose(
            false, true, 100, AtlasOreLayerSource.LoadedColumns,
            0, 0, 512, 74, "All ores", 8
        );
        if (columns.Contains("Regional potential", StringComparison.Ordinal)
            || !columns.Contains("Loaded columns: 512 columns, 74 observed blocks", StringComparison.Ordinal))
        {
            return $"a column radius must report only its own group, got \"{columns}\"";
        }

        string preparing = Compose(
            false, false, 63, AtlasOreLayerSource.None, 0, 0, 0, 0, "All ores", 8
        );
        if (preparing != "Preparing · 63% · loaded data only")
        {
            return $"unexpected preparing state \"{preparing}\"";
        }

        string waiting = Compose(
            false, true, 100, AtlasOreLayerSource.None, 0, 0, 0, 0, "All ores", 8
        );
        if (!waiting.StartsWith("Waiting for streamed data", StringComparison.Ordinal))
        {
            return $"unexpected empty-data state \"{waiting}\"";
        }

        string unavailable = Compose(
            true, true, 100, AtlasOreLayerSource.Mixed, 3, 2, 5, 5, "All ores", 8
        );
        if (!unavailable.StartsWith("Unavailable", StringComparison.Ordinal))
        {
            return $"unexpected failed state \"{unavailable}\"";
        }
        return null;
    }
}
