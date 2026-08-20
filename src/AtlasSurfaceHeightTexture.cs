using System;
using System.Collections.Generic;
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
    private int[]? sourcePixels;
    private int[]? pixels;
    private int[]? exteriorPixels;
    private readonly HashSet<int> missingChunkIndices = new();
    private int minimumChunkX;
    private int minimumChunkZ;
    private int chunkCountX;
    private int chunkCountZ;
    private int nextChunkIndex;
    private int nextExteriorRow;
    private int missingRetryCursor;
    private bool refreshingExterior;

    public bool Ready { get; private set; }
    public int TextureId => Ready ? texture?.TextureId ?? 0 : 0;
    public int OriginX => minimumChunkX * GlobalConstants.ChunkSize;
    public int OriginZ => minimumChunkZ * GlobalConstants.ChunkSize;
    public int Width => texture?.Width ?? 0;
    public int Height => texture?.Height ?? 0;

    public bool CoversArea(double centerX, double centerZ, int radius)
    {
        if (!Ready || texture == null || texture.TextureId <= 0) return false;

        double extent = Math.Max(0, radius);
        double minimumX = centerX - extent;
        double maximumX = centerX + extent;
        double minimumZ = centerZ - extent;
        double maximumZ = centerZ + extent;
        return minimumX >= OriginX
            && maximumX < OriginX + Width
            && minimumZ >= OriginZ
            && maximumZ < OriginZ + Height;
    }

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
        int[]? previousSourcePixels = sourcePixels;
        int previousMinimumChunkX = minimumChunkX;
        int previousMinimumChunkZ = minimumChunkZ;
        int previousChunkCountX = chunkCountX;
        int previousChunkCountZ = chunkCountZ;

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
        sourcePixels = new int[checked(width * height)];
        PreserveOverlappingSamples(
            previousSourcePixels,
            previousMinimumChunkX,
            previousMinimumChunkZ,
            previousChunkCountX,
            previousChunkCountZ,
            samplesPerChunk
        );
        pixels = new int[sourcePixels.Length];
        exteriorPixels = null;
        missingChunkIndices.Clear();
        EnsureTextureSize(width, height);
        if (texture == null) return;
        texture.Width = width;
        texture.Height = height;
        nextChunkIndex = 0;
        nextExteriorRow = 0;
        missingRetryCursor = 0;
        refreshingExterior = false;
        Ready = false;
    }

    private void PreserveOverlappingSamples(
        int[]? previousPixels,
        int previousMinimumChunkX,
        int previousMinimumChunkZ,
        int previousChunkCountX,
        int previousChunkCountZ,
        int samplesPerChunk
    )
    {
        if (previousPixels == null || sourcePixels == null
            || previousChunkCountX <= 0 || previousChunkCountZ <= 0)
        {
            return;
        }

        int previousWidth = previousChunkCountX * samplesPerChunk;
        int expectedPreviousLength = checked(
            previousWidth * previousChunkCountZ * samplesPerChunk
        );
        if (previousPixels.Length != expectedPreviousLength) return;

        int overlapMinimumChunkX = Math.Max(minimumChunkX, previousMinimumChunkX);
        int overlapMinimumChunkZ = Math.Max(minimumChunkZ, previousMinimumChunkZ);
        int overlapMaximumChunkX = Math.Min(
            minimumChunkX + chunkCountX - 1,
            previousMinimumChunkX + previousChunkCountX - 1
        );
        int overlapMaximumChunkZ = Math.Min(
            minimumChunkZ + chunkCountZ - 1,
            previousMinimumChunkZ + previousChunkCountZ - 1
        );
        if (overlapMinimumChunkX > overlapMaximumChunkX
            || overlapMinimumChunkZ > overlapMaximumChunkZ)
        {
            return;
        }

        int currentWidth = chunkCountX * samplesPerChunk;
        int copiedChunkCount = 0;
        for (int chunkZ = overlapMinimumChunkZ;
            chunkZ <= overlapMaximumChunkZ;
            chunkZ++)
        {
            int previousChunkOffsetZ = chunkZ - previousMinimumChunkZ;
            int currentChunkOffsetZ = chunkZ - minimumChunkZ;
            for (int chunkX = overlapMinimumChunkX;
                chunkX <= overlapMaximumChunkX;
                chunkX++)
            {
                int previousChunkOffsetX = chunkX - previousMinimumChunkX;
                int currentChunkOffsetX = chunkX - minimumChunkX;
                for (int sampleZ = 0; sampleZ < samplesPerChunk; sampleZ++)
                {
                    int previousIndex =
                        (previousChunkOffsetZ * samplesPerChunk + sampleZ)
                        * previousWidth
                        + previousChunkOffsetX * samplesPerChunk;
                    int currentIndex =
                        (currentChunkOffsetZ * samplesPerChunk + sampleZ)
                        * currentWidth
                        + currentChunkOffsetX * samplesPerChunk;
                    Array.Copy(
                        previousPixels,
                        previousIndex,
                        sourcePixels,
                        currentIndex,
                        samplesPerChunk
                    );
                }
                copiedChunkCount++;
            }
        }

        if (copiedChunkCount > 0)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Preserved transient surface data for {0} overlapping chunks while adapting to the current game view distance.",
                copiedChunkCount
            );
        }
    }

    private void EnsureTextureSize(int width, int height)
    {
        if (texture != null
            && texture.TextureId > 0
            && (texture.Width != width || texture.Height != height))
        {
            capi.Logger.Notification(
                "[ModernAtlas] Reallocating the transient surface-safety texture from {0}x{1} to {2}x{3} after the game view distance changed.",
                texture.Width,
                texture.Height,
                width,
                height
            );
            texture.Dispose();
            texture = null;
        }

        texture ??= new LoadedTexture(capi);
    }

    public bool Advance()
    {
        if (sourcePixels == null || pixels == null || texture == null) return false;
        if (Ready)
        {
            AdvanceMissingChunkRefresh();
            return true;
        }

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
            if (!FillChunk(
                minimumChunkX + relativeChunkX,
                minimumChunkZ + relativeChunkZ,
                relativeChunkX,
                relativeChunkZ
            ))
            {
                missingChunkIndices.Add(nextChunkIndex);
            }
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
        sourcePixels = null;
        pixels = null;
        exteriorPixels = null;
        missingChunkIndices.Clear();
        nextChunkIndex = 0;
        nextExteriorRow = 0;
        missingRetryCursor = 0;
        refreshingExterior = false;
        chunkCountX = 0;
        chunkCountZ = 0;
        Ready = false;
    }

    public void Dispose()
    {
        texture?.Dispose();
        texture = null;
        sourcePixels = null;
        pixels = null;
        exteriorPixels = null;
        missingChunkIndices.Clear();
        Ready = false;
    }

    private bool FillChunk(
        int chunkX,
        int chunkZ,
        int relativeChunkX,
        int relativeChunkZ
    )
    {
        if (sourcePixels == null || texture == null) return false;

        IMapChunk? mapChunk = capi.World.BlockAccessor.GetMapChunk(chunkX, chunkZ);
        ushort[]? heightMap = mapChunk?.WorldGenTerrainHeightMap
            ?? mapChunk?.RainHeightMap;
        int chunkSize = GlobalConstants.ChunkSize;
        if (heightMap == null || heightMap.Length < chunkSize * chunkSize)
        {
            return TryFillChunkFromLoadedBlocks(
                chunkX,
                chunkZ,
                relativeChunkX,
                relativeChunkZ
            );
        }

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
                sourcePixels[outputIndex] = EncodeHeight(maximumHeight);
            }
        }
        return true;
    }

    private bool TryFillChunkFromLoadedBlocks(
        int chunkX,
        int chunkZ,
        int relativeChunkX,
        int relativeChunkZ
    )
    {
        if (sourcePixels == null || texture == null) return false;

        int chunkSize = GlobalConstants.ChunkSize;
        int verticalChunkCount = Math.Max(
            1,
            (capi.World.BlockAccessor.MapSizeY + chunkSize - 1) / chunkSize
        );
        int dimensionOffset = capi.World.Player.Entity.Pos.Dimension
            * GlobalConstants.DimensionSizeInChunks;
        IChunkBlocks?[] loadedBlocks = new IChunkBlocks?[verticalChunkCount];
        bool anyLoaded = false;
        for (int chunkY = 0; chunkY < verticalChunkCount; chunkY++)
        {
            IWorldChunk? chunk = capi.World.BlockAccessor.GetChunk(
                chunkX,
                chunkY + dimensionOffset,
                chunkZ
            );
            if (chunk == null
                || chunk.Disposed
                || chunk is IClientChunk clientChunk && !clientChunk.LoadedFromServer)
            {
                continue;
            }

            loadedBlocks[chunkY] = chunk.Data;
            anyLoaded |= chunk.Data != null;
        }
        if (!anyLoaded) return false;

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
                for (int chunkY = verticalChunkCount - 1;
                    chunkY >= 0 && maximumHeight == 0;
                    chunkY--)
                {
                    IChunkBlocks? data = loadedBlocks[chunkY];
                    if (data == null) continue;
                    int remainingHeight = capi.World.BlockAccessor.MapSizeY
                        - chunkY * chunkSize;
                    int localHeight = Math.Min(chunkSize, Math.Max(0, remainingHeight));
                    for (int localY = localHeight - 1; localY >= 0; localY--)
                    {
                        int index = sourceX
                            + sourceZ * chunkSize
                            + localY * chunkSize * chunkSize;
                        if (index < 0 || index >= data.Length) continue;
                        if (data.GetBlockId(index, BlockLayersAccess.Solid) == 0) continue;

                        maximumHeight = chunkY * chunkSize + localY;
                        break;
                    }
                }

                if (maximumHeight <= 0) continue;
                int outputIndex = (outputBaseZ + sampleZ) * texture.Width
                    + outputBaseX + sampleX;
                sourcePixels[outputIndex] = EncodeHeight(maximumHeight);
            }
        }

        return true;
    }

    private void FillExteriorEnvelopeRow(int row)
    {
        if (sourcePixels == null || exteriorPixels == null || texture == null) return;

        int width = texture.Width;
        int height = texture.Height;
        int rowOffset = row * width;
        for (int x = 0; x < width; x++)
        {
            int encoded = sourcePixels[rowOffset + x];
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
                    int neighbor = sourcePixels[sampleRow + sampleX];
                    if (!IsValidHeight(neighbor)) continue;
                    exteriorHeight = Math.Min(exteriorHeight, DecodeHeight(neighbor));
                }
            }
            exteriorPixels[rowOffset + x] = EncodeHeight(exteriorHeight);
        }
    }

    private void AdvanceMissingChunkRefresh()
    {
        if (sourcePixels == null || texture == null) return;

        long started = capi.ElapsedMilliseconds;
        if (refreshingExterior)
        {
            int processedRows = 0;
            while (nextExteriorRow < texture.Height
                && (processedRows == 0
                    || capi.ElapsedMilliseconds - started < WorkBudgetMilliseconds))
            {
                FillExteriorEnvelopeRow(nextExteriorRow);
                nextExteriorRow++;
                processedRows++;
            }
            if (nextExteriorRow < texture.Height) return;

            pixels = exteriorPixels;
            exteriorPixels = null;
            capi.Render.LoadOrUpdateTextureFromBgra(pixels!, false, 0, ref texture);
            refreshingExterior = false;
            return;
        }

        if (missingChunkIndices.Count == 0) return;

        int totalChunks = chunkCountX * chunkCountZ;
        int checkedIndices = 0;
        int retriedChunks = 0;
        bool updated = false;
        while (checkedIndices < totalChunks
            && retriedChunks < 32
            && (retriedChunks == 0
                || capi.ElapsedMilliseconds - started < WorkBudgetMilliseconds))
        {
            int index = missingRetryCursor;
            missingRetryCursor = (missingRetryCursor + 1) % totalChunks;
            checkedIndices++;
            if (!missingChunkIndices.Contains(index)) continue;

            retriedChunks++;
            int relativeChunkX = index % chunkCountX;
            int relativeChunkZ = index / chunkCountX;
            if (!FillChunk(
                minimumChunkX + relativeChunkX,
                minimumChunkZ + relativeChunkZ,
                relativeChunkX,
                relativeChunkZ
            ))
            {
                continue;
            }

            missingChunkIndices.Remove(index);
            updated = true;
        }

        if (!updated) return;

        exteriorPixels = new int[sourcePixels.Length];
        nextExteriorRow = 0;
        refreshingExterior = true;
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
