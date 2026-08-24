using System;
using System.Collections.Generic;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ModernAtlas;

/// <summary>
/// Builds transient per-atlas masks for textures used exclusively by
/// registered plant and leaf blocks. This keeps mod vegetation support based
/// on public block metadata and avoids hiding terrain that shares a texture
/// with a vegetation block.
/// </summary>
internal sealed class AtlasVegetationTextureMask : IDisposable
{
    private const double WorkBudgetMilliseconds = 4;
    private const int MappingDownsample = 8;

    private readonly ICoreClientAPI capi;
    private readonly Queue<TextureUsage> pendingTextures = new();
    private readonly HashSet<BakedCompositeTexture> discoveredVegetationTextures = new(
        ReferenceEqualityComparer.Instance
    );
    private readonly HashSet<BakedCompositeTexture> discoveredOtherTextures = new(
        ReferenceEqualityComparer.Instance
    );
    private readonly HashSet<int> vegetationTextureSubIds = new();
    private readonly HashSet<int> otherTextureSubIds = new();
    private readonly List<TextureAtlasPosition> exclusiveVegetationTextures = new();
    private readonly Dictionary<int, int[]> maskPixelsByAtlas = new();
    private readonly Dictionary<int, LoadedTexture> masksByTerrainTextureId = new();
    private LoadedTexture emptyMaskTexture;

    private BuildStage stage = BuildStage.ScanBlocks;
    private int blockIndex;
    private int registeredVegetationBlockCount;
    private int exclusiveTextureIndex;
    private int maskUploadIndex;
    private int atlasPixelSize;
    private int maskPixelSize;
    private int successfulTerrainBindCount;
    private bool loggedReady;
    private bool disposed;

    public bool Ready => stage == BuildStage.Ready;
    public bool Failed => stage == BuildStage.Failed;
    public int ExclusiveTextureCount => exclusiveVegetationTextures.Count;

    public AtlasVegetationTextureMask(ICoreClientAPI capi)
    {
        this.capi = capi;
        emptyMaskTexture = new LoadedTexture(capi);
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
                    case BuildStage.BuildMasks:
                        AdvanceMasks();
                        break;
                    case BuildStage.UploadMasks:
                        UploadNextMask();
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            stage = BuildStage.Failed;
            capi.Logger.Error(
                "[ModernAtlas] Vegetation texture mask could not be prepared: {0}",
                exception.Message
            );
        }

        if (Ready && !loggedReady)
        {
            loggedReady = true;
            capi.Logger.Notification(
                "[ModernAtlas] Vegetation texture mask is ready for {0} exclusive textures from {1} registered plant or leaf blocks across {2} block texture atlases.",
                ExclusiveTextureCount,
                registeredVegetationBlockCount,
                masksByTerrainTextureId.Count
            );
        }
        return Ready;
    }

    public bool BindForTerrainTexture(
        IShaderProgram shader,
        int terrainTextureId,
        int textureUnit
    )
    {
        if (!Ready || !shader.HasUniform("atlasVegetationMaskTex")) return false;

        LoadedTexture mask = masksByTerrainTextureId.TryGetValue(
            terrainTextureId,
            out LoadedTexture? mappedMask
        ) ? mappedMask : emptyMaskTexture;
        if (mask.TextureId <= 0) return false;

        shader.BindTexture2D("atlasVegetationMaskTex", mask.TextureId, textureUnit);
        if (successfulTerrainBindCount < int.MaxValue)
        {
            successfulTerrainBindCount++;
        }
        return true;
    }

    public bool Validate(out string diagnostic)
    {
        diagnostic = $"vegetationBlocks={registeredVegetationBlockCount}, exclusiveTextures={ExclusiveTextureCount}, atlasMasks={masksByTerrainTextureId.Count}, terrainBinds={successfulTerrainBindCount}, ready={Ready}, failed={Failed}";
        return Ready
            && registeredVegetationBlockCount > 0
            && ExclusiveTextureCount > 0
            && masksByTerrainTextureId.Count > 0
            && successfulTerrainBindCount > 0
            && emptyMaskTexture.TextureId > 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        foreach (LoadedTexture texture in masksByTerrainTextureId.Values)
        {
            texture.Dispose();
        }
        masksByTerrainTextureId.Clear();
        maskPixelsByAtlas.Clear();
        emptyMaskTexture.Dispose();
        stage = BuildStage.Failed;
    }

    private void AdvanceBlockScan()
    {
        if (pendingTextures.Count > 0)
        {
            TextureUsage usage = pendingTextures.Dequeue();
            AddTextureUsage(usage.Texture, usage.Vegetation);
            EnqueueChildren(usage.Texture.BakedVariants, usage.Vegetation);
            EnqueueChildren(usage.Texture.BakedTiles, usage.Vegetation);
            return;
        }

        IList<Block> blocks = capi.World.Blocks;
        if (blockIndex >= blocks.Count)
        {
            FinishBlockScan();
            return;
        }

        Block? block = blocks[blockIndex++];
        if (block == null || block.Id <= 0 || block.Textures == null) return;

        bool vegetation = block.BlockMaterial == EnumBlockMaterial.Plant
            || block.BlockMaterial == EnumBlockMaterial.Leaves;
        if (vegetation) registeredVegetationBlockCount++;

        foreach (CompositeTexture composite in block.Textures.Values)
        {
            EnqueueTexture(composite?.Baked, vegetation);
        }
    }

    private void FinishBlockScan()
    {
        TextureAtlasPosition[] positions = capi.BlockTextureAtlas.Positions;
        foreach (int textureSubId in vegetationTextureSubIds)
        {
            if (otherTextureSubIds.Contains(textureSubId)
                || textureSubId < 0
                || textureSubId >= positions.Length)
            {
                continue;
            }

            TextureAtlasPosition? position = positions[textureSubId];
            if (position != null)
            {
                exclusiveVegetationTextures.Add(position);
            }
        }

        atlasPixelSize = Math.Max(1, capi.BlockTextureAtlas.Size.Width);
        maskPixelSize = Math.Max(
            1,
            (atlasPixelSize + MappingDownsample - 1) / MappingDownsample
        );
        stage = BuildStage.BuildMasks;
    }

    private void AdvanceMasks()
    {
        if (exclusiveTextureIndex >= exclusiveVegetationTextures.Count)
        {
            UploadEmptyMask();
            stage = BuildStage.UploadMasks;
            return;
        }

        TextureAtlasPosition position = exclusiveVegetationTextures[
            exclusiveTextureIndex++
        ];
        if (!maskPixelsByAtlas.TryGetValue(position.atlasNumber, out int[]? pixels))
        {
            pixels = new int[checked(maskPixelSize * maskPixelSize)];
            maskPixelsByAtlas[position.atlasNumber] = pixels;
        }
        FillMask(position, pixels);
    }

    private void UploadEmptyMask()
    {
        if (emptyMaskTexture.TextureId > 0) return;

        int[] emptyPixel = { 0 };
        emptyMaskTexture.Width = 1;
        emptyMaskTexture.Height = 1;
        capi.Render.LoadOrUpdateTextureFromBgra(
            emptyPixel,
            false,
            1,
            ref emptyMaskTexture
        );
        if (emptyMaskTexture.TextureId <= 0)
        {
            throw new InvalidOperationException(
                "The empty vegetation mask texture could not be uploaded."
            );
        }
    }

    private void UploadNextMask()
    {
        if (maskUploadIndex >= maskPixelsByAtlas.Count)
        {
            maskPixelsByAtlas.Clear();
            stage = BuildStage.Ready;
            return;
        }

        int atlasNumber = 0;
        int[]? pixels = null;
        int current = 0;
        foreach ((int candidateAtlas, int[] candidatePixels) in maskPixelsByAtlas)
        {
            if (current++ != maskUploadIndex) continue;
            atlasNumber = candidateAtlas;
            pixels = candidatePixels;
            break;
        }
        maskUploadIndex++;
        if (pixels == null) return;

        IList<LoadedTexture> atlasTextures = capi.BlockTextureAtlas.AtlasTextures;
        if (atlasNumber < 0 || atlasNumber >= atlasTextures.Count)
        {
            throw new InvalidOperationException(
                $"Block texture atlas {atlasNumber} is unavailable."
            );
        }

        var mask = new LoadedTexture(capi)
        {
            Width = maskPixelSize,
            Height = maskPixelSize
        };
        capi.Render.LoadOrUpdateTextureFromBgra(pixels, false, 1, ref mask);
        if (mask.TextureId <= 0)
        {
            mask.Dispose();
            throw new InvalidOperationException(
                $"Vegetation mask for block atlas {atlasNumber} could not be uploaded."
            );
        }

        masksByTerrainTextureId[atlasTextures[atlasNumber].TextureId] = mask;
    }

    private void AddTextureUsage(BakedCompositeTexture baked, bool vegetation)
    {
        int textureSubId = baked.TextureSubId;
        if (textureSubId < 0) return;

        if (vegetation) vegetationTextureSubIds.Add(textureSubId);
        else otherTextureSubIds.Add(textureSubId);
    }

    private void FillMask(TextureAtlasPosition position, int[] pixels)
    {
        int minimumX = Math.Max(
            0,
            (int)Math.Floor(position.x1 * atlasPixelSize / MappingDownsample)
        );
        int maximumX = Math.Min(
            maskPixelSize - 1,
            (int)Math.Ceiling(position.x2 * atlasPixelSize / MappingDownsample) - 1
        );
        int minimumY = Math.Max(
            0,
            (int)Math.Floor(position.y1 * atlasPixelSize / MappingDownsample)
        );
        int maximumY = Math.Min(
            maskPixelSize - 1,
            (int)Math.Ceiling(position.y2 * atlasPixelSize / MappingDownsample) - 1
        );

        for (int y = minimumY; y <= maximumY; y++)
        {
            float atlasY = (y + 0.5f) * MappingDownsample / atlasPixelSize;
            if (atlasY < position.y1 || atlasY >= position.y2) continue;
            int row = y * maskPixelSize;
            for (int x = minimumX; x <= maximumX; x++)
            {
                float atlasX = (x + 0.5f) * MappingDownsample / atlasPixelSize;
                if (atlasX < position.x1 || atlasX >= position.x2) continue;
                pixels[row + x] = unchecked((int)0xffffffff);
            }
        }
    }

    private void EnqueueChildren(BakedCompositeTexture[]? children, bool vegetation)
    {
        if (children == null) return;
        foreach (BakedCompositeTexture? child in children)
        {
            EnqueueTexture(child, vegetation);
        }
    }

    private void EnqueueTexture(BakedCompositeTexture? baked, bool vegetation)
    {
        if (baked == null) return;
        HashSet<BakedCompositeTexture> discovered = vegetation
            ? discoveredVegetationTextures
            : discoveredOtherTextures;
        if (discovered.Add(baked))
        {
            pendingTextures.Enqueue(new TextureUsage(baked, vegetation));
        }
    }

    private static bool WithinBudget(
        long started,
        double workBudgetMilliseconds
    )
    {
        double elapsedMilliseconds = (Stopwatch.GetTimestamp() - started)
            * 1000.0
            / Stopwatch.Frequency;
        return elapsedMilliseconds < Math.Max(0.1, workBudgetMilliseconds);
    }

    private readonly record struct TextureUsage(
        BakedCompositeTexture Texture,
        bool Vegetation
    );

    private enum BuildStage
    {
        ScanBlocks,
        BuildMasks,
        UploadMasks,
        Ready,
        Failed
    }
}
