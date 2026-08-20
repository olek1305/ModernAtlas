using System;

namespace ModernAtlas;

/// <summary>
/// The world extent a capture covers. The pixel dimensions come from
/// <see cref="AtlasScreenshotPreview"/>, which both the panel and the capture
/// already share; this adds the one thing the pixel numbers cannot express —
/// how much of the world ends up inside the frame.
/// </summary>
internal static class AtlasScreenshotEstimate
{
    /// <summary>
    /// Blocks covered by the capture. The atlas camera shows 2 × zoom blocks
    /// vertically; the capture area percentage scales both axes linearly.
    /// </summary>
    public static void WorldSpan(
        float zoom,
        float viewportAspect,
        int captureAreaPercent,
        out double widthBlocks,
        out double heightBlocks
    )
    {
        double area = Math.Clamp(captureAreaPercent, 1, 100) / 100d;
        double safeZoom = Math.Max(0.001, zoom);
        double safeAspect = Math.Clamp(viewportAspect, 0.05f, 20f);
        heightBlocks = 2d * safeZoom * area;
        widthBlocks = heightBlocks * safeAspect;
    }

    public static string FormatWorldSpan(
        float zoom,
        float viewportAspect,
        int captureAreaPercent
    )
    {
        WorldSpan(
            zoom,
            viewportAspect,
            captureAreaPercent,
            out double widthBlocks,
            out double heightBlocks
        );
        return FormattableString.Invariant(
            $"World: ~{widthBlocks:n0} × {heightBlocks:n0} blocks"
        );
    }

    /// <summary>
    /// Deterministic self-check: the span has to follow zoom, aspect and the
    /// capture percentage, and degenerate inputs must not produce nonsense.
    /// </summary>
    public static string? Validate()
    {
        WorldSpan(100f, 2f, 100, out double width, out double height);
        if (Math.Abs(height - 200d) > 0.001 || Math.Abs(width - 400d) > 0.001)
        {
            return $"expected 400x200 blocks at zoom 100, aspect 2, got {width}x{height}";
        }

        WorldSpan(100f, 2f, 50, out double halfWidth, out double halfHeight);
        if (Math.Abs(halfHeight - 100d) > 0.001 || Math.Abs(halfWidth - 200d) > 0.001)
        {
            return $"a 50% capture must halve both axes, got {halfWidth}x{halfHeight}";
        }

        WorldSpan(0f, 0f, 0, out double degenerateWidth, out double degenerateHeight);
        if (!double.IsFinite(degenerateWidth)
            || !double.IsFinite(degenerateHeight)
            || degenerateWidth <= 0
            || degenerateHeight <= 0)
        {
            return "degenerate inputs must still produce a positive finite span";
        }
        return null;
    }
}
