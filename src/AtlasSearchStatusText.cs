using System;

namespace ModernAtlas;

/// <summary>
/// The search panel's status line. Pure, so every state stays verifiable
/// without driving a live scan into each one.
/// </summary>
internal static class AtlasSearchStatusText
{
    public static string Compose(
        bool hasActiveQuery,
        bool scanning,
        int scanPercent,
        int markerCount,
        string controllerText
    )
    {
        if (!hasActiveQuery) return controllerText;
        if (scanning)
        {
            return FormattableString.Invariant(
                $"Searching loaded data · {scanPercent}% · no distant chunk requests"
            );
        }
        if (markerCount == 0)
        {
            return "No matches in loaded data · nothing outside it was requested";
        }
        return controllerText;
    }

    /// <summary>
    /// Deterministic self-check of the three states plus the inactive query.
    /// Returns a failure description, or null when all hold.
    /// </summary>
    public static string? Validate()
    {
        string idle = Compose(false, false, 0, 0, "Type at least 2 characters");
        if (idle != "Type at least 2 characters")
        {
            return $"an inactive query must keep the controller text, got \"{idle}\"";
        }

        string scanning = Compose(true, true, 42, 3, "3 markers • scanning");
        if (!scanning.StartsWith("Searching loaded data", StringComparison.Ordinal)
            || !scanning.Contains("42%", StringComparison.Ordinal))
        {
            return $"unexpected scanning state \"{scanning}\"";
        }

        string empty = Compose(true, false, 100, 0, "0 markers • search complete");
        if (!empty.StartsWith("No matches in loaded data", StringComparison.Ordinal))
        {
            return $"unexpected empty state \"{empty}\"";
        }

        string results = Compose(true, false, 100, 12, "12 markers • search complete");
        if (results != "12 markers • search complete")
        {
            return $"a finished search with results must keep the controller text, got \"{results}\"";
        }
        return null;
    }
}
