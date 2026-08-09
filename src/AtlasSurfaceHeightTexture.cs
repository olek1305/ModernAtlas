using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ModernAtlas;

/// <summary>
/// A transient GPU view of the world-generation exterior height from map
/// chunks that the client already has. It keeps one sample per block and
/// applies a one-block minimum envelope so complete cliff steps and building
/// floors remain visible without exposing cave interiors. It is rebuilt only
/// while the atlas is open and is never stored as an atlas terrain cache.
/// </summary>
internal sealed class AtlasSurfaceHeightTexture : IDisposable
{
    public const int HorizontalSampleSize = 1;

    private const int WorkBudgetMilliseconds = 4;
    private const int MaximumChunksPerFrame = 512;
    private const int ExteriorEnvelopeRadius = 1;

    private readonly ICoreClientAPI capi;
    private LoadedTexture? texture;
    private int[]? pixels;
    private int[]? exteriorPixels;
    private int minimumChunkX;
    private int minimumChunkZ;
    private int chunkCountX;
    private int chunkCountZ;
    private int nextChunkIndex;
    private int nextExteriorRow;

    public bool Ready { get; private set; }
    public int TextureId => Ready ? texture?.TextureId ?? 0 : 0;
    public int OriginX => minimumChunkX * GlobalConstants.ChunkSize;
    public int OriginZ => minimumChunkZ * GlobalConstants.ChunkSize;
    public int Width => texture?.Width ?? 0;
    public int Height => texture?.Height ?? 0;
    public int ProgressPercent
    {
        get
        {
            int totalChunks = chunkCountX * chunkCountZ;
            if (totalChunks <= 0) return 0;
            if (Ready) return 100;
            if (nextChunkIndex < totalChunks)
            {
                return Math.Clamp(nextChunkIndex * 80 / totalChunks, 0, 80);
            }

            int height = texture?.Height ?? 0;
            return height <= 0
                ? 80
                : Math.Clamp(80 + nextExteriorRow * 20 / height, 80, 99);
        }
    }

    public AtlasSurfaceHeightTexture(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void Begin(double centerX, double centerZ, int radius)
    {
        int chunkSize = GlobalConstants.ChunkSize;
        minimumChunkX = (int)Math.Floor((centerX - radius) / chunkSize);
        minimumChunkZ = (int)Math.Floor((centerZ - radius) / chunkSize);
        int maximumChunkX = (int)Math.Floor((centerX + radius) / chunkSize);
        int maximumChunkZ = (int)Math.Floor((centerZ + radius) / chunkSize);
        chunkCountX = maximumChunkX - minimumChunkX + 1;
        chunkCountZ = maximumChunkZ - minimumChunkZ + 1;

        int samplesPerChunk = chunkSize / HorizontalSampleSize;
        int width = chunkCountX * samplesPerChunk;
        int height = chunkCountZ * samplesPerChunk;
        pixels = new int[checked(width * height)];
        exteriorPixels = null;
        texture ??= new LoadedTexture(capi);
        texture.Width = width;
        texture.Height = height;
        nextChunkIndex = 0;
        nextExteriorRow = 0;
        Ready = false;
    }

    public bool Advance()
    {
        if (Ready) return true;
        if (pixels == null || texture == null) return false;

        long started = capi.ElapsedMilliseconds;
        int totalChunks = chunkCountX * chunkCountZ;
        int processed = 0;
        while (nextChunkIndex < totalChunks
            && processed < MaximumChunksPerFrame
            && (processed == 0
                || capi.ElapsedMilliseconds - started < WorkBudgetMilliseconds))
        {
            int relativeChunkX = nextChunkIndex % chunkCountX;
            int relativeChunkZ = nextChunkIndex / chunkCountX;
            FillChunk(
                minimumChunkX + relativeChunkX,
                minimumChunkZ + relativeChunkZ,
                relativeChunkX,
                relativeChunkZ
            );
            nextChunkIndex++;
            processed++;
        }

        if (nextChunkIndex < totalChunks) return false;

        exteriorPixels ??= new int[pixels.Length];
        int processedRows = 0;
        while (nextExteriorRow < texture.Height
            && (processedRows == 0
                || capi.ElapsedMilliseconds - started < WorkBudgetMilliseconds))
        {
            FillExteriorEnvelopeRow(nextExteriorRow);
            nextExteriorRow++;
            processedRows++;
        }
        if (nextExteriorRow < texture.Height) return false;

        pixels = exteriorPixels;
        exteriorPixels = null;

        capi.Render.LoadOrUpdateTextureFromBgra(pixels, false, 0, ref texture);
        Ready = texture.TextureId > 0;
        return Ready;
    }

    public bool TryGetSurfaceHeight(double worldX, double worldZ, out int surfaceHeight)
    {
        surfaceHeight = 0;
        if (!Ready || pixels == null || texture == null) return false;

        int sampleX = (int)Math.Floor((worldX - OriginX) / HorizontalSampleSize);
        int sampleZ = (int)Math.Floor((worldZ - OriginZ) / HorizontalSampleSize);
        if (sampleX < 0 || sampleZ < 0
            || sampleX >= texture.Width || sampleZ >= texture.Height)
        {
            return false;
        }

        int encoded = pixels[sampleZ * texture.Width + sampleX];
        if ((encoded & 0xff) == 0) return false;

        surfaceHeight = DecodeHeight(encoded);
        return true;
    }

    public void Reset()
    {
        pixels = null;
        exteriorPixels = null;
        nextChunkIndex = 0;
        nextExteriorRow = 0;
        chunkCountX = 0;
        chunkCountZ = 0;
        Ready = false;
    }

    public void Dispose()
    {
        texture?.Dispose();
        texture = null;
        pixels = null;
        exteriorPixels = null;
        Ready = false;
    }

    private void FillChunk(
        int chunkX,
        int chunkZ,
        int relativeChunkX,
        int relativeChunkZ
    )
    {
        if (pixels == null || texture == null) return;

        IMapChunk? mapChunk = capi.World.BlockAccessor.GetMapChunk(chunkX, chunkZ);
        ushort[]? heightMap = mapChunk?.WorldGenTerrainHeightMap;
        int chunkSize = GlobalConstants.ChunkSize;
        if (heightMap == null || heightMap.Length < chunkSize * chunkSize) return;

        int samplesPerChunk = chunkSize / HorizontalSampleSize;
        int outputBaseX = relativeChunkX * samplesPerChunk;
        int outputBaseZ = relativeChunkZ * samplesPerChunk;
        for (int sampleZ = 0; sampleZ < samplesPerChunk; sampleZ++)
        {
            int sourceZ = sampleZ * HorizontalSampleSize;
            for (int sampleX = 0; sampleX < samplesPerChunk; sampleX++)
            {
                int sourceX = sampleX * HorizontalSampleSize;
                int maximumHeight = 0;
                for (int dz = 0; dz < HorizontalSampleSize; dz++)
                {
                    int sourceRow = (sourceZ + dz) * chunkSize;
                    for (int dx = 0; dx < HorizontalSampleSize; dx++)
                    {
                        maximumHeight = Math.Max(
                            maximumHeight,
                            heightMap[sourceRow + sourceX + dx]
                        );
                    }
                }

                int outputIndex = (outputBaseZ + sampleZ) * texture.Width
                    + outputBaseX + sampleX;
                pixels[outputIndex] = EncodeHeight(maximumHeight);
            }
        }
    }

    private void FillExteriorEnvelopeRow(int row)
    {
        if (pixels == null || exteriorPixels == null || texture == null) return;

        int width = texture.Width;
        int height = texture.Height;
        int rowOffset = row * width;
        for (int x = 0; x < width; x++)
        {
            int encoded = pixels[rowOffset + x];
            if (!IsValidHeight(encoded)) continue;

            int exteriorHeight = DecodeHeight(encoded);
            for (int offsetZ = -ExteriorEnvelopeRadius;
                offsetZ <= ExteriorEnvelopeRadius;
                offsetZ++)
            {
                int sampleZ = row + offsetZ;
                if (sampleZ < 0 || sampleZ >= height) continue;
                int sampleRow = sampleZ * width;
                for (int offsetX = -ExteriorEnvelopeRadius;
                    offsetX <= ExteriorEnvelopeRadius;
                    offsetX++)
                {
                    int sampleX = x + offsetX;
                    if (sampleX < 0 || sampleX >= width) continue;
                    int neighbor = pixels[sampleRow + sampleX];
                    if (!IsValidHeight(neighbor)) continue;
                    exteriorHeight = Math.Min(exteriorHeight, DecodeHeight(neighbor));
                }
            }
            exteriorPixels[rowOffset + x] = EncodeHeight(exteriorHeight);
        }
    }

    private static bool IsValidHeight(int encoded) => (encoded & 0xff) != 0;

    private static int DecodeHeight(int encoded) =>
        ((encoded >> 16) & 0xff) * 256 + ((encoded >> 8) & 0xff);

    private static int EncodeHeight(int height)
    {
        int highByte = height >> 8;
        int lowByte = height & 0xff;
        // LoadOrUpdateTextureFromBgra consumes 0xAARRGGBB values. Red and
        // green hold the 16-bit height; blue marks valid samples.
        return unchecked(
            (int)0xff000000 | (highByte << 16) | (lowByte << 8) | 0xff
        );
    }
}
