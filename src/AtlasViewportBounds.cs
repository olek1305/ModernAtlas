using System;

namespace ModernAtlas;

internal readonly record struct AtlasViewportBounds(
    int X,
    int Y,
    int Width,
    int Height
)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public AtlasViewportBounds Inset(int pixels)
    {
        int inset = Math.Max(0, pixels);
        return new AtlasViewportBounds(
            X + inset,
            Y + inset,
            Math.Max(1, Width - inset * 2),
            Math.Max(1, Height - inset * 2)
        );
    }

    public AtlasViewportBounds Expand(int horizontalPixels, int verticalPixels)
    {
        int horizontal = Math.Max(0, horizontalPixels);
        int vertical = Math.Max(0, verticalPixels);
        return new AtlasViewportBounds(
            X - horizontal,
            Y - vertical,
            Width + horizontal * 2,
            Height + vertical * 2
        );
    }

    public bool Contains(int x, int y) =>
        x >= X && x <= Right && y >= Y && y <= Bottom;
}
