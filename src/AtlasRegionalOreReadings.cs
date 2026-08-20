using System;
using System.Collections.Generic;
using Vintagestory.API.Datastructures;

namespace ModernAtlas;

/// <summary>
/// Turns a loaded region's ore-potential maps into the readings the atlas
/// shows. This is pure: it touches no world, no chunk and no client state, so
/// the branch stays verifiable in worlds whose loaded regions carry no
/// OreMaps at all.
/// </summary>
internal static class AtlasRegionalOreReadings
{
    /// <summary>
    /// Reads every map at one normalized position inside the region.
    /// Readings below the trace threshold are dropped unless they belong to
    /// the selected ore, which stays visible so a filtered card can state
    /// that the ore is mapped but negligible here. Results are ordered by
    /// descending potential.
    /// </summary>
    /// <param name="encounteredCodes">
    /// Optional collector for every ore code seen, including the dropped ones.
    /// The caller registers those for the filter list.
    /// </param>
    public static AtlasOreReading[] Build(
        IEnumerable<KeyValuePair<string, IntDataMap2D>> oreMaps,
        float normalizedX,
        float normalizedZ,
        string? selectedOreCode,
        ICollection<string>? encounteredCodes,
        out int sourceMapCount
    )
    {
        sourceMapCount = 0;
        var readings = new List<AtlasOreReading>();
        float clampedX = Math.Clamp(normalizedX, 0f, 1f);
        float clampedZ = Math.Clamp(normalizedZ, 0f, 1f);
        foreach (KeyValuePair<string, IntDataMap2D> entry in oreMaps)
        {
            IntDataMap2D? map = entry.Value;
            if (map == null || map.InnerSize <= 0) continue;

            sourceMapCount++;
            encounteredCodes?.Add(entry.Key);
            float potential = DecodeDensity(
                map.GetUnpaddedColorLerpedForNormalizedPos(clampedX, clampedZ)
            );
            if (potential < AtlasOrePotential.TraceThreshold
                && !string.Equals(entry.Key, selectedOreCode, StringComparison.Ordinal))
            {
                continue;
            }
            readings.Add(new AtlasOreReading(entry.Key, potential, 0, true));
        }
        readings.Sort((left, right) => right.Potential.CompareTo(left.Potential));
        return readings.ToArray();
    }

    /// <summary>
    /// Vintage Story stores ore potential either as a plain byte or as a
    /// packed color. Take the strongest channel in the packed case.
    /// </summary>
    public static float DecodeDensity(int raw)
    {
        if (raw >= 0 && raw <= 255) return raw / 255f;

        uint packed = unchecked((uint)raw);
        int red = (int)((packed >> 16) & 0xff);
        int green = (int)((packed >> 8) & 0xff);
        int blue = (int)(packed & 0xff);
        return Math.Max(red, Math.Max(green, blue)) / 255f;
    }

    /// <summary>
    /// Deterministic self-check over prepared <see cref="IntDataMap2D"/>
    /// instances. It covers what a world without regional OreMaps cannot:
    /// map selection, decoding, the trace threshold, the selected-ore
    /// exemption, ordering and the source-map count. Returns a failure
    /// description, or null when every case holds.
    /// </summary>
    public static string? Validate()
    {
        var maps = new Dictionary<string, IntDataMap2D>(StringComparer.Ordinal)
        {
            // Uniform values keep the expectation independent of the API's
            // bilinear interpolation between neighboring samples.
            ["cassiterite"] = UniformMap(191),
            ["bismuthinite"] = UniformMap(64),
            // Below the trace threshold: 0.4/255 is far under 0.002.
            ["quartz"] = UniformMap(0),
            // A packed color value must decode through its strongest channel.
            ["sphalerite"] = UniformMap(unchecked((int)0x00206010)),
            // Degenerate maps must be skipped without counting as a source.
            ["ignored"] = new IntDataMap2D { Data = Array.Empty<int>(), Size = 0 }
        };

        var encountered = new List<string>();
        AtlasOreReading[] readings = Build(
            maps,
            0.5f,
            0.5f,
            null,
            encountered,
            out int sourceMapCount
        );

        if (sourceMapCount != 4)
        {
            return $"expected 4 usable ore maps, counted {sourceMapCount}";
        }
        if (encountered.Count != 4 || !encountered.Contains("quartz"))
        {
            return $"expected every usable code to be collected, got {encountered.Count} without quartz";
        }
        if (readings.Length != 3)
        {
            return $"expected the below-trace ore to be dropped, got {readings.Length} readings";
        }
        if (readings[0].Code != "cassiterite"
            || readings[1].Code != "sphalerite"
            || readings[2].Code != "bismuthinite")
        {
            return "expected readings ordered by descending potential";
        }
        if (!readings[0].IsPotential || readings[0].BlockCount != 0)
        {
            return "expected regional readings to carry a potential, not a block count";
        }
        if (Math.Abs(readings[0].Potential - 191f / 255f) > 0.001f)
        {
            return $"expected 191/255 for a plain byte, got {readings[0].Potential}";
        }
        if (Math.Abs(readings[1].Potential - 0x60 / 255f) > 0.001f)
        {
            return $"expected the strongest channel of a packed color, got {readings[1].Potential}";
        }
        if (AtlasOrePotential.Grade(readings[0].Potential) != "Ultra high")
        {
            return $"expected the top reading to grade as Ultra high, got {AtlasOrePotential.Grade(readings[0].Potential)}";
        }

        // The selected ore stays visible even below the trace threshold.
        AtlasOreReading[] filtered = Build(
            maps,
            0.5f,
            0.5f,
            "quartz",
            null,
            out _
        );
        if (filtered.Length != 4)
        {
            return $"expected the selected below-trace ore to survive, got {filtered.Length} readings";
        }
        if (filtered[3].Code != "quartz" || filtered[3].Potential >= AtlasOrePotential.TraceThreshold)
        {
            return "expected the selected below-trace ore last, with its real potential";
        }

        if (Build(Array.Empty<KeyValuePair<string, IntDataMap2D>>(), 0.5f, 0.5f, null, null, out int emptyCount).Length != 0
            || emptyCount != 0)
        {
            return "expected no readings and no sources for an empty map set";
        }
        return null;
    }

    private static IntDataMap2D UniformMap(int value)
    {
        const int size = 4;
        var data = new int[size * size];
        for (int index = 0; index < data.Length; index++) data[index] = value;
        return new IntDataMap2D { Data = data, Size = size };
    }
}
