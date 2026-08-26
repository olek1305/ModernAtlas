using System;
using System.Collections.Generic;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ModernAtlas;

/// <summary>
/// Builds transient GPU lookup textures that replace every registered ore
/// material with its baked host-rock texture. The lookup is atlas-only, uses
/// public texture-atlas metadata, and is discarded when the world renderer is
/// released.
/// </summary>
internal sealed class AtlasOreTextureReplacement : IDisposable
{
    private const double WorkBudgetMilliseconds = 4;
    private const int MappingDownsample = 4;
    private const int MinimumStoneTileSize = 16;
    private const int MaximumStoneTileSize = 128;

    private readonly ICoreClientAPI capi;
    private readonly List<OreTextureDescriptor> oreTextures = new();
    private readonly HashSet<int> collectedTextureSubIds = new();
    private readonly Queue<BakedCompositeTexture> pendingBakedTextures = new();
    private readonly HashSet<BakedCompositeTexture> discoveredBakedTextures = new(
        ReferenceEqualityComparer.Instance
    );
    private readonly Dictionary<string, int> stoneTileByAsset = new(
        StringComparer.Ordinal
    );
    private readonly List<StoneTile> stoneTiles = new();
    private readonly Dictionary<int, int[]> mappingPixelsByAtlas = new();
    private readonly Dictionary<int, LoadedTexture> mappingTexturesByTerrainId = new();
    private LoadedTexture stoneTexture;
    private LoadedTexture emptyMappingTexture;

    private BuildStage stage = BuildStage.ScanBlocks;
    private int blockIndex;
    private int registeredOreBlockCount;
    private int processedBakedTextureCount;
    private int stoneTileIndex;
    private int oreTextureIndex;
    private int mappingUploadIndex;
    private int[]? stonePixels;
    private int atlasPixelSize;
    private int mappingPixelSize;
    private int stoneTileSize;
    private int stoneTextureSize;
    private bool loggedReady;
    private int successfulTerrainBindCount;
    private bool disposed;

    public bool Ready => stage == BuildStage.Ready;
    public bool Failed => stage == BuildStage.Failed;
    public int OreTextureCount => oreTextures.Count;
    public int StoneMaterialCount => stoneTiles.Count;
    public int AtlasMappingCount => mappingTexturesByTerrainId.Count;

    public AtlasOreTextureReplacement(ICoreClientAPI capi)
    {
        this.capi = capi;
        stoneTexture = new LoadedTexture(capi);
        emptyMappingTexture = new LoadedTexture(capi);
    }

    public bool Advance(double workBudgetMilliseconds = WorkBudgetMilliseconds)
    {
        if (disposed || Ready || Failed) return Ready;

        long started = Stopwatch.GetTimestamp();
        try
        {
            while (WithinBudget(started, workBudgetMilliseconds) && !Ready && !Failed)
            {
                switch (stage)
                {
                    case BuildStage.ScanBlocks:
                        AdvanceBlockScan();
                        break;
                    case BuildStage.BuildStoneTiles:
                        AdvanceStoneTiles();
                        break;
                    case BuildStage.BuildMappings:
                        AdvanceMappings();
                        break;
                    case BuildStage.UploadStoneTexture:
                        UploadStoneTexture();
                        break;
                    case BuildStage.UploadMappings:
                        UploadNextMapping();
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            stage = BuildStage.Failed;
            capi.Logger.Error(
                "[ModernAtlas] Survival ore concealment could not be prepared: {0}",
                exception.Message
            );
        }

        if (Ready && !loggedReady)
        {
            loggedReady = true;
            capi.Logger.Notification(
                "[ModernAtlas] Survival ore concealment is ready for {0} registered ore textures from {1} host-rock materials across {2} block texture atlases.",
                OreTextureCount,
                StoneMaterialCount,
                AtlasMappingCount
            );
        }
        return Ready;
    }

    public bool BindForTerrainTexture(
        IShaderProgram shader,
        int terrainTextureId,
        int mappingTextureUnit,
        int stoneTextureUnit
    )
    {
        if (!Ready
            || stoneTexture.TextureId <= 0
            || !shader.HasUniform("atlasOreMapTex")
            || !shader.HasUniform("atlasStoneTex"))
        {
            return false;
        }

        LoadedTexture mappingTexture = mappingTexturesByTerrainId.TryGetValue(
            terrainTextureId,
            out LoadedTexture? mappedTexture
        ) ? mappedTexture : emptyMappingTexture;
        if (mappingTexture.TextureId <= 0) return false;

        shader.BindTexture2D(
            "atlasOreMapTex",
            mappingTexture.TextureId,
            mappingTextureUnit
        );
        shader.BindTexture2D(
            "atlasStoneTex",
            stoneTexture.TextureId,
            stoneTextureUnit
        );
        if (successfulTerrainBindCount < int.MaxValue)
        {
            successfulTerrainBindCount++;
        }
        return true;
    }

    public bool Validate(out string diagnostic)
    {
        diagnostic = $"oreBlocks={registeredOreBlockCount}, oreTextures={OreTextureCount}, hostRocks={StoneMaterialCount}, atlasMaps={AtlasMappingCount}, terrainBinds={successfulTerrainBindCount}, ready={Ready}, failed={Failed}";
        return Ready
            && registeredOreBlockCount > 0
            && OreTextureCount > 0
            && StoneMaterialCount > 0
            && AtlasMappingCount > 0
            && successfulTerrainBindCount > 0
            && stoneTexture.TextureId > 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        foreach (LoadedTexture texture in mappingTexturesByTerrainId.Values)
        {
            texture.Dispose();
        }
        mappingTexturesByTerrainId.Clear();
        mappingPixelsByAtlas.Clear();
        stoneTexture.Dispose();
        emptyMappingTexture.Dispose();
        stonePixels = null;
        stage = BuildStage.Failed;
    }

    private void AdvanceBlockScan()
    {
        if (pendingBakedTextures.Count > 0)
        {
            BakedCompositeTexture baked = pendingBakedTextures.Dequeue();
            processedBakedTextureCount++;
            AddOreTexture(baked);
            EnqueueChildren(baked.BakedVariants);
            EnqueueChildren(baked.BakedTiles);
            return;
        }

        IList<Block> blocks = capi.World.Blocks;
        if (blockIndex >= blocks.Count)
        {
            FinishBlockScan();
            return;
        }

        Block? block = blocks[blockIndex++];
        if (block == null
            || block.Id <= 0
            || block.BlockMaterial != EnumBlockMaterial.Ore)
        {
            return;
        }
        registeredOreBlockCount++;
        if (block.Textures == null) return;

        var textures = block.Textures!;
        foreach (CompositeTexture composite in textures.Values)
        {
            EnqueueBaked(composite?.Baked);
        }
    }

    private void FinishBlockScan()
    {
        capi.Logger.Debug(
            "[ModernAtlas] Survival ore scan completed: {0} registered ore blocks, {1} baked texture nodes and {2} unique atlas textures.",
            registeredOreBlockCount,
            processedBakedTextureCount,
            oreTextures.Count
        );
        if (oreTextures.Count == 0)
        {
            stage = BuildStage.Ready;
            return;
        }

        atlasPixelSize = Math.Max(1, capi.BlockTextureAtlas.Size.Width);
        mappingPixelSize = Math.Max(
            1,
            (atlasPixelSize + MappingDownsample - 1) / MappingDownsample
        );
        int configuredTileSize = capi.Settings.Int["textureSize"];
        stoneTileSize = Math.Clamp(
            configuredTileSize > 0 ? configuredTileSize : 32,
            MinimumStoneTileSize,
            MaximumStoneTileSize
        );

        foreach (OreTextureDescriptor descriptor in oreTextures)
        {
            string assetKey = descriptor.HostRockTexture.ToString();
            if (stoneTileByAsset.ContainsKey(assetKey)) continue;

            int tileIndex = stoneTiles.Count;
            stoneTileByAsset[assetKey] = tileIndex;
            stoneTiles.Add(new StoneTile(descriptor.HostRockTexture, tileIndex));
        }

        int tilesAcross = (int)Math.Ceiling(Math.Sqrt(stoneTiles.Count));
        stoneTextureSize = NextPowerOfTwo(Math.Max(1, tilesAcross * stoneTileSize));
        int maximumTextureSize = Math.Max(1, capi.Render.GlGetMaxTextureSize());
        if (stoneTextureSize > maximumTextureSize)
        {
            throw new InvalidOperationException(
                $"The host-rock texture atlas requires {stoneTextureSize}px, but the GPU limit is {maximumTextureSize}px."
            );
        }

        stonePixels = new int[checked(stoneTextureSize * stoneTextureSize)];
        stoneTexture.Width = stoneTextureSize;
        stoneTexture.Height = stoneTextureSize;
        stage = BuildStage.BuildStoneTiles;
    }

    private void AdvanceStoneTiles()
    {
        if (stonePixels == null)
        {
            stage = BuildStage.Failed;
            return;
        }
        if (stoneTileIndex >= stoneTiles.Count)
        {
            stage = BuildStage.BuildMappings;
            return;
        }

        StoneTile tile = stoneTiles[stoneTileIndex++];
        int tilesPerRow = Math.Max(1, stoneTextureSize / stoneTileSize);
        int targetX = tile.Index % tilesPerRow * stoneTileSize;
        int targetY = tile.Index / tilesPerRow * stoneTileSize;
        if (!TryCopyHostRock(tile.Texture, targetX, targetY))
        {
            FillNeutralStone(targetX, targetY, tile.Texture.GetHashCode());
        }
    }

    private void AdvanceMappings()
    {
        if (oreTextureIndex >= oreTextures.Count)
        {
            stage = BuildStage.UploadStoneTexture;
            return;
        }

        OreTextureDescriptor descriptor = oreTextures[oreTextureIndex++];
        if (!stoneTileByAsset.TryGetValue(
            descriptor.HostRockTexture.ToString(),
            out int tileIndex
        ))
        {
            return;
        }

        TextureAtlasPosition position = descriptor.Position;
        int atlasNumber = position.atlasNumber;
        if (!mappingPixelsByAtlas.TryGetValue(atlasNumber, out int[]? mappingPixels))
        {
            mappingPixels = new int[checked(mappingPixelSize * mappingPixelSize)];
            mappingPixelsByAtlas[atlasNumber] = mappingPixels;
        }

        FillMapping(position, tileIndex, mappingPixels);
    }

    private void UploadStoneTexture()
    {
        if (stonePixels == null)
        {
            stage = BuildStage.Failed;
            return;
        }

        capi.Render.LoadOrUpdateTextureFromBgra(
            stonePixels,
            false,
            1,
            ref stoneTexture
        );
        stonePixels = null;
        if (stoneTexture.TextureId <= 0)
        {
            throw new InvalidOperationException(
                "The temporary host-rock texture could not be uploaded."
            );
        }
        int[] emptyPixel = { 0 };
        emptyMappingTexture.Width = 1;
        emptyMappingTexture.Height = 1;
        capi.Render.LoadOrUpdateTextureFromBgra(
            emptyPixel,
            false,
            1,
            ref emptyMappingTexture
        );
        if (emptyMappingTexture.TextureId <= 0)
        {
            throw new InvalidOperationException(
                "The empty ore lookup texture could not be uploaded."
            );
        }
        stage = BuildStage.UploadMappings;
    }

    private void UploadNextMapping()
    {
        if (mappingUploadIndex >= mappingPixelsByAtlas.Count)
        {
            mappingPixelsByAtlas.Clear();
            stage = BuildStage.Ready;
            return;
        }

        int atlasNumber = 0;
        int[]? pixels = null;
        int current = 0;
        foreach ((int candidateAtlas, int[] candidatePixels) in mappingPixelsByAtlas)
        {
            if (current++ != mappingUploadIndex) continue;
            atlasNumber = candidateAtlas;
            pixels = candidatePixels;
            break;
        }
        mappingUploadIndex++;
        if (pixels == null) return;

        IList<LoadedTexture> atlasTextures = capi.BlockTextureAtlas.AtlasTextures;
        if (atlasNumber < 0 || atlasNumber >= atlasTextures.Count)
        {
            throw new InvalidOperationException(
                $"Block texture atlas {atlasNumber} is unavailable."
            );
        }

        var mappingTexture = new LoadedTexture(capi)
        {
            Width = mappingPixelSize,
            Height = mappingPixelSize
        };
        capi.Render.LoadOrUpdateTextureFromBgra(
            pixels,
            false,
            1,
            ref mappingTexture
        );
        if (mappingTexture.TextureId <= 0)
        {
            mappingTexture.Dispose();
            throw new InvalidOperationException(
                $"Ore lookup texture for block atlas {atlasNumber} could not be uploaded."
            );
        }

        int terrainTextureId = atlasTextures[atlasNumber].TextureId;
        mappingTexturesByTerrainId[terrainTextureId] = mappingTexture;
    }

    private void AddOreTexture(BakedCompositeTexture baked)
    {
        int textureSubId = baked.TextureSubId;
        TextureAtlasPosition[] positions = capi.BlockTextureAtlas.Positions;
        if (textureSubId < 0
            || textureSubId >= positions.Length
            || !collectedTextureSubIds.Add(textureSubId))
        {
            return;
        }

        TextureAtlasPosition? position = positions[textureSubId];
        if (position == null) return;

        AssetLocation hostRock = baked.TextureFilenames is { Length: > 0 }
            && baked.TextureFilenames[0] != null
                ? ToTextureAssetLocation(baked.TextureFilenames[0])
                : new AssetLocation(
                    "game",
                    "textures/block/stone/rock/granite1.png"
                );
        oreTextures.Add(new OreTextureDescriptor(position, hostRock));
    }

    private bool TryCopyHostRock(AssetLocation texture, int targetX, int targetY)
    {
        if (stonePixels == null) return false;

        try
        {
            IAsset? asset = capi.Assets.TryGet(texture);
            if (asset == null) return false;

            using BitmapRef bitmap = asset.ToBitmap(capi);
            if (bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Pixels.Length == 0)
            {
                return false;
            }

            // Animated block textures stack frames vertically. Host rock is
            // sampled from its first square frame, matching the base material
            // without carrying an ore overlay into the replacement.
            int sourceFrameHeight = Math.Min(bitmap.Height, bitmap.Width);
            for (int y = 0; y < stoneTileSize; y++)
            {
                int sourceY = Math.Clamp(
                    y * sourceFrameHeight / stoneTileSize,
                    0,
                    sourceFrameHeight - 1
                );
                int targetRow = (targetY + y) * stoneTextureSize + targetX;
                int sourceRow = sourceY * bitmap.Width;
                for (int x = 0; x < stoneTileSize; x++)
                {
                    int sourceX = Math.Clamp(
                        x * bitmap.Width / stoneTileSize,
                        0,
                        bitmap.Width - 1
                    );
                    stonePixels[targetRow + x] = bitmap.Pixels[sourceRow + sourceX]
                        | unchecked((int)0xff000000);
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void FillNeutralStone(int targetX, int targetY, int seed)
    {
        if (stonePixels == null) return;

        for (int y = 0; y < stoneTileSize; y++)
        {
            int targetRow = (targetY + y) * stoneTextureSize + targetX;
            for (int x = 0; x < stoneTileSize; x++)
            {
                int hash = unchecked(seed * 397 ^ x * 73471 ^ y * 91229);
                hash ^= hash >> 13;
                int value = 102 + Math.Abs(hash % 27);
                int warm = Math.Clamp(value + 2, 0, 255);
                stonePixels[targetRow + x] = unchecked(
                    (int)0xff000000 | warm << 16 | value << 8 | value
                );
            }
        }
    }

    private void FillMapping(
        TextureAtlasPosition position,
        int tileIndex,
        int[] mappingPixels
    )
    {
        float sourceX1 = position.x1 * atlasPixelSize;
        float sourceY1 = position.y1 * atlasPixelSize;
        float sourceX2 = position.x2 * atlasPixelSize;
        float sourceY2 = position.y2 * atlasPixelSize;
        float sourceWidth = Math.Max(1, sourceX2 - sourceX1);
        float sourceHeight = Math.Max(1, sourceY2 - sourceY1);
        int minimumMapX = Math.Max(0, (int)Math.Floor(sourceX1 / MappingDownsample));
        int maximumMapX = Math.Min(
            mappingPixelSize - 1,
            (int)Math.Ceiling(sourceX2 / MappingDownsample) - 1
        );
        int minimumMapY = Math.Max(0, (int)Math.Floor(sourceY1 / MappingDownsample));
        int maximumMapY = Math.Min(
            mappingPixelSize - 1,
            (int)Math.Ceiling(sourceY2 / MappingDownsample) - 1
        );
        int tilesPerRow = Math.Max(1, stoneTextureSize / stoneTileSize);
        int tileX = tileIndex % tilesPerRow * stoneTileSize;
        int tileY = tileIndex / tilesPerRow * stoneTileSize;

        for (int mapY = minimumMapY; mapY <= maximumMapY; mapY++)
        {
            float sourceY = (mapY + 0.5f) * MappingDownsample;
            if (sourceY < sourceY1 || sourceY >= sourceY2) continue;
            float relativeY = Math.Clamp((sourceY - sourceY1) / sourceHeight, 0, 0.99999f);
            int targetY = tileY + Math.Min(
                stoneTileSize - 1,
                (int)(relativeY * stoneTileSize)
            );
            int mapRow = mapY * mappingPixelSize;

            for (int mapX = minimumMapX; mapX <= maximumMapX; mapX++)
            {
                float sourceX = (mapX + 0.5f) * MappingDownsample;
                if (sourceX < sourceX1 || sourceX >= sourceX2) continue;
                float relativeX = Math.Clamp((sourceX - sourceX1) / sourceWidth, 0, 0.99999f);
                int targetX = tileX + Math.Min(
                    stoneTileSize - 1,
                    (int)(relativeX * stoneTileSize)
                );
                mappingPixels[mapRow + mapX] = EncodeTarget(targetX, targetY);
            }
        }
    }

    private void EnqueueChildren(BakedCompositeTexture[]? children)
    {
        if (children == null) return;
        foreach (BakedCompositeTexture? child in children)
        {
            EnqueueBaked(child);
        }
    }

    private void EnqueueBaked(BakedCompositeTexture? baked)
    {
        if (baked != null && discoveredBakedTextures.Add(baked))
        {
            pendingBakedTextures.Enqueue(baked);
        }
    }

    private static AssetLocation ToTextureAssetLocation(AssetLocation source)
    {
        string path = source.Path;
        if (!path.StartsWith("textures/", StringComparison.Ordinal))
        {
            path = $"textures/{path}";
        }
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            path += ".png";
        }
        return new AssetLocation(source.Domain, path);
    }

    private static int EncodeTarget(int targetX, int targetY)
    {
        int encodedX = targetX + 1;
        int encodedY = targetY + 1;
        if (encodedX > ushort.MaxValue || encodedY > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                "The temporary stone atlas exceeded the 16-bit shader lookup range."
            );
        }

        int red = encodedX >> 8;
        int green = encodedX & 0xff;
        int blue = encodedY >> 8;
        int alpha = encodedY & 0xff;
        return unchecked(alpha << 24 | red << 16 | green << 8 | blue);
    }

    private static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value && result < 1 << 30) result <<= 1;
        return result;
    }

    private static bool WithinBudget(
        long started,
        double workBudgetMilliseconds
    ) =>
        Stopwatch.GetElapsedTime(started).TotalMilliseconds
            < Math.Max(0.1, workBudgetMilliseconds);

    private readonly record struct OreTextureDescriptor(
        TextureAtlasPosition Position,
        AssetLocation HostRockTexture
    );

    private readonly record struct StoneTile(AssetLocation Texture, int Index);

    private enum BuildStage
    {
        ScanBlocks,
        BuildStoneTiles,
        BuildMappings,
        UploadStoneTexture,
        UploadMappings,
        Ready,
        Failed
    }
}
