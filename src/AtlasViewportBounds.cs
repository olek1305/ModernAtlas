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

    public bool Contains(int x, int y) =>
        x >= X && x <= Right && y >= Y && y <= Bottom;
}
