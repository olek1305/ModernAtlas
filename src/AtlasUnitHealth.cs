using System;

namespace ModernAtlas;

/// <summary>
/// One entity's health prepared for display. The bar and the caption come
/// from the same reading, so the number always matches the fill.
/// </summary>
internal readonly record struct AtlasUnitHealthReading(
    bool Available,
    float Fraction,
    string Text
);

/// <summary>
/// Turns a raw health pair into something safe to draw. Entities can report a
/// zero, negative or non-finite maximum — a bar computed from that would
/// divide by zero or run off its track.
/// </summary>
internal static class AtlasUnitHealth
{
    public static AtlasUnitHealthReading Resolve(
        bool hasHealth,
        float current,
        float maximum
    )
    {
        if (!hasHealth
            || !float.IsFinite(current)
            || !float.IsFinite(maximum)
            || maximum <= 0)
        {
            return new AtlasUnitHealthReading(false, 0f, "unavailable");
        }

        float clampedCurrent = Math.Clamp(current, 0f, maximum);
        float fraction = Math.Clamp(clampedCurrent / maximum, 0f, 1f);
        string text = FormattableString.Invariant(
            $"{clampedCurrent:0.#} / {maximum:0.#} ({fraction * 100f:0}%)"
        );
        return new AtlasUnitHealthReading(true, fraction, text);
    }

    /// <summary>
    /// Deterministic self-check for the states a live entity cannot be made
    /// to produce on demand. Returns a failure description, or null.
    /// </summary>
    public static string? Validate()
    {
        AtlasUnitHealthReading normal = Resolve(true, 12f, 15f);
        if (!normal.Available
            || Math.Abs(normal.Fraction - 0.8f) > 0.001f
            || normal.Text != "12 / 15 (80%)")
        {
            return $"unexpected normal reading \"{normal.Text}\" at {normal.Fraction}";
        }

        AtlasUnitHealthReading zeroMaximum = Resolve(true, 5f, 0f);
        if (zeroMaximum.Available || zeroMaximum.Fraction != 0f)
        {
            return "a zero maximum must report unavailable, never a filled bar";
        }

        AtlasUnitHealthReading negative = Resolve(true, -3f, 15f);
        if (!negative.Available || negative.Fraction != 0f || negative.Text != "0 / 15 (0%)")
        {
            return $"negative health must clamp to empty, got \"{negative.Text}\"";
        }

        AtlasUnitHealthReading overflow = Resolve(true, 40f, 15f);
        if (!overflow.Available || Math.Abs(overflow.Fraction - 1f) > 0.001f
            || overflow.Text != "15 / 15 (100%)")
        {
            return $"health above the maximum must clamp to full, got \"{overflow.Text}\"";
        }

        AtlasUnitHealthReading nan = Resolve(true, float.NaN, 15f);
        if (nan.Available) return "a non-finite reading must report unavailable";

        AtlasUnitHealthReading missing = Resolve(false, 12f, 15f);
        if (missing.Available) return "an entity without health data must report unavailable";
        return null;
    }
}
