using System;
using System.Collections.Generic;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Builds a short-lived color texture from map chunks and map regions that
/// are already present on the client. Work is spread over render frames and
/// the result is discarded when the atlas or world closes.
/// </summary>
internal sealed class AtlasMapLayerTexture : IDisposable
{
    public const int HorizontalSampleSize = 8;

    private const double WorkBudgetMilliseconds = 4;
    private const int MaximumSamplesPerFrame = 2048;

    private readonly ICoreClientAPI capi;
    private readonly HashSet<(int X, int Z)> sampledOreRegions = new();
    private readonly HashSet<int> oreBlockIds = new();
    private LoadedTexture? texture;
    private int[]? pixels;
    private int nextSampleIndex;
    private double disclosureCenterX;
    private double disclosureCenterZ;
    private int disclosureRadius;
    private int validSampleCount;
    private int oreRegionCount;
    private int oreMapCount;
    private int oreColumnSampleCount;
    private int oreBlockHitCount;
    private bool failed;
    private bool loggedSamplingFailure;

    public AtlasMapLayer Layer { get; private set; } = AtlasMapLayer.TexturedTerrain;
    public bool Ready { get; private set; }
    public int TextureId => Ready ? texture?.TextureId ?? 0 : 0;
    public int OriginX { get; private set; }
    public int OriginZ { get; private set; }
    public int Width => texture?.Width ?? 0;
    public int Height => texture?.Height ?? 0;
    public int ProgressPercent => pixels == null || pixels.Length == 0
        ? Layer == AtlasMapLayer.TexturedTerrain ? 100 : 0
        : Ready
            ? 100
            : Math.Clamp(nextSampleIndex * 100 / pixels.Length, 0, 99);

    public string StatusText
    {
        get
        {
            if (Layer == AtlasMapLayer.TexturedTerrain)
            {
                return "Layer: Textured terrain • live block materials";
            }
            if (failed)
            {
                return $"Layer: {Layer.DisplayName()} • unavailable; textured terrain remains active";
            }
            if (!Ready)
            {
                return $"Layer: {Layer.DisplayName()} • preparing {ProgressPercent}% • loaded data only";
            }
            if (validSampleCount == 0)
            {
                return $"Layer: {Layer.DisplayName()} • no loaded data available";
            }
            string source = Layer == AtlasMapLayer.OreDensity
                ? oreMapCount > 0
                    ? $"{oreMapCount} ore maps in {oreRegionCount} loaded regions"
                    : $"{oreColumnSampleCount} loaded block-column samples; {oreBlockHitCount} ore blocks"
                : $"{validSampleCount} loaded samples";
            return $"Layer: {Layer.DisplayName()} • {Layer.Legend()} • {source}";
        }
    }

    public AtlasMapLayerTexture(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void Begin(
        AtlasMapLayer layer,
        double centerX,
        double centerZ,
        int radius,
        bool spoilerAccess
    )
    {
        if (layer.RequiresSpoilerAccess() && !spoilerAccess)
        {
            layer = AtlasMapLayer.TexturedTerrain;
        }

        Layer = layer;
        Ready = layer == AtlasMapLayer.TexturedTerrain;
        pixels = null;
        nextSampleIndex = 0;
        validSampleCount = 0;
        oreRegionCount = 0;
        oreMapCount = 0;
        oreColumnSampleCount = 0;
        oreBlockHitCount = 0;
        sampledOreRegions.Clear();
        oreBlockIds.Clear();
        failed = false;
        loggedSamplingFailure = false;
        if (Ready) return;

        if (Layer == AtlasMapLayer.OreDensity)
        {
            foreach (Block block in capi.World.Blocks)
            {
                if (block?.Id > 0 && block.BlockMaterial == EnumBlockMaterial.Ore)
                {
                    oreBlockIds.Add(block.Id);
                }
            }
        }

        disclosureCenterX = centerX;
        disclosureCenterZ = centerZ;
        disclosureRadius = Math.Max(GlobalConstants.ChunkSize, radius);
        OriginX = FloorToSample(centerX - disclosureRadius);
        OriginZ = FloorToSample(centerZ - disclosureRadius);
        int maximumX = CeilToSample(centerX + disclosureRadius);
        int maximumZ = CeilToSample(centerZ + disclosureRadius);
        int width = Math.Max(1, (maximumX - OriginX) / HorizontalSampleSize + 1);
        int height = Math.Max(1, (maximumZ - OriginZ) / HorizontalSampleSize + 1);

        pixels = new int[checked(width * height)];
        texture ??= new LoadedTexture(capi);
        texture.Width = width;
        texture.Height = height;
    }

    public bool Advance()
    {
        if (Ready) return true;
        if (pixels == null || texture == null) return false;

        long started = Stopwatch.GetTimestamp();
        int processed = 0;
        while (nextSampleIndex < pixels.Length
            && processed < MaximumSamplesPerFrame
            && (processed == 0
                || Stopwatch.GetElapsedTime(started).TotalMilliseconds
                    < WorkBudgetMilliseconds))
        {
            try
            {
                FillSample(nextSampleIndex);
            }
            catch (Exception exception)
            {
                if (!loggedSamplingFailure)
                {
                    loggedSamplingFailure = true;
                    capi.Logger.Warning(
                        "[ModernAtlas] A loaded-data layer sample could not be read and was skipped: {0}",
                        exception.Message
                    );
                }
            }
            nextSampleIndex++;
            processed++;
        }
        if (nextSampleIndex < pixels.Length) return false;

        try
        {
            capi.Render.LoadOrUpdateTextureFromBgra(pixels, true, 0, ref texture);
            Ready = texture.TextureId > 0;
        }
        catch (Exception exception)
        {
            failed = true;
            Ready = true;
            capi.Logger.Warning(
                "[ModernAtlas] Could not upload the atlas-only {0} layer; textured terrain remains active: {1}",
                Layer.DisplayName(),
                exception.Message
            );
            return true;
        }
        capi.Logger.Notification(
            "[ModernAtlas] Prepared atlas-only {0} layer from {1} samples already loaded by the client; ore maps={2}, ore regions={3}, ore block hits={4}.",
            Layer.DisplayName(),
            validSampleCount,
            oreMapCount,
            oreRegionCount,
            oreBlockHitCount
        );
        return Ready;
    }

    public void Reset()
    {
        Layer = AtlasMapLayer.TexturedTerrain;
        Ready = true;
        pixels = null;
        nextSampleIndex = 0;
        validSampleCount = 0;
        oreRegionCount = 0;
        oreMapCount = 0;
        oreColumnSampleCount = 0;
        oreBlockHitCount = 0;
        sampledOreRegions.Clear();
        oreBlockIds.Clear();
        failed = false;
        loggedSamplingFailure = false;
    }

    public void Dispose()
    {
        texture?.Dispose();
        texture = null;
        pixels = null;
        Ready = false;
    }

    private void FillSample(int index)
    {
        if (pixels == null || texture == null) return;

        int sampleX = index % texture.Width;
        int sampleZ = index / texture.Width;
        int worldX = OriginX + sampleX * HorizontalSampleSize
            + HorizontalSampleSize / 2;
        int worldZ = OriginZ + sampleZ * HorizontalSampleSize
            + HorizontalSampleSize / 2;
        double dx = worldX - disclosureCenterX;
        double dz = worldZ - disclosureCenterZ;
        if (dx * dx + dz * dz > (double)disclosureRadius * disclosureRadius)
        {
            return;
        }
        if (worldX < 0 || worldZ < 0
            || worldX >= capi.World.BlockAccessor.MapSizeX
            || worldZ >= capi.World.BlockAccessor.MapSizeZ)
        {
            return;
        }

        int chunkSize = GlobalConstants.ChunkSize;
        int chunkX = worldX / chunkSize;
        int chunkZ = worldZ / chunkSize;
        IMapChunk? mapChunk = capi.World.BlockAccessor.GetMapChunk(chunkX, chunkZ);
        ushort[]? heightMap = mapChunk?.WorldGenTerrainHeightMap;
        if (heightMap == null || heightMap.Length < chunkSize * chunkSize) return;

        int localX = worldX - chunkX * chunkSize;
        int localZ = worldZ - chunkZ * chunkSize;
        int surfaceY = heightMap[localZ * chunkSize + localX];
        float value;
        if (Layer == AtlasMapLayer.OreDensity)
        {
            if (!TryReadOreDensity(worldX, worldZ, out value)) return;
        }
        else
        {
            ClimateCondition? climate = capi.World.BlockAccessor.GetClimateAt(
                new BlockPos(worldX, surfaceY, worldZ),
                EnumGetClimateMode.WorldGenValues
            );
            if (climate == null) return;
            value = Layer switch
            {
                AtlasMapLayer.SoilFertility => climate.Fertility,
                AtlasMapLayer.Moisture => climate.WorldgenRainfall,
                AtlasMapLayer.Temperature =>
                    (climate.WorldGenTemperature + 20f) / 60f,
                AtlasMapLayer.ForestDensity => climate.ForestDensity,
                AtlasMapLayer.GeologicActivity => climate.GeologicActivity,
                _ => 0f
            };
        }

        pixels[index] = ColorForLayer(Layer, Math.Clamp(value, 0f, 1f));
        validSampleCount++;
    }

    private bool TryReadOreDensity(int worldX, int worldZ, out float density)
    {
        density = 0;
        int regionSize = Math.Max(1, capi.World.BlockAccessor.RegionSize);
        int regionX = worldX / regionSize;
        int regionZ = worldZ / regionSize;
        IMapRegion? region = capi.World.BlockAccessor.GetMapRegion(regionX, regionZ);
        if (region?.OreMaps == null)
        {
            return TryReadLoadedOreColumn(worldX, worldZ, out density);
        }

        float normalizedX = (worldX - regionX * regionSize) / (float)regionSize;
        float normalizedZ = (worldZ - regionZ * regionSize) / (float)regionSize;
        int maps = 0;
        foreach (KeyValuePair<string, IntDataMap2D> entry in region.OreMaps)
        {
            IntDataMap2D? map = entry.Value;
            if (map == null || map.InnerSize <= 0) continue;
            int raw = map.GetUnpaddedColorLerpedForNormalizedPos(
                Math.Clamp(normalizedX, 0f, 1f),
                Math.Clamp(normalizedZ, 0f, 1f)
            );
            density = Math.Max(density, DecodeDensity(raw));
            maps++;
        }
        if (maps == 0)
        {
            return TryReadLoadedOreColumn(worldX, worldZ, out density);
        }

        if (sampledOreRegions.Add((regionX, regionZ)))
        {
            oreRegionCount++;
            oreMapCount += maps;
        }
        return true;
    }

    private bool TryReadLoadedOreColumn(int worldX, int worldZ, out float density)
    {
        density = 0;
        int chunkSize = GlobalConstants.ChunkSize;
        int chunkX = worldX / chunkSize;
        int chunkZ = worldZ / chunkSize;
        int localX = worldX - chunkX * chunkSize;
        int localZ = worldZ - chunkZ * chunkSize;
        int verticalChunkCount = Math.Max(
            1,
            (capi.World.BlockAccessor.MapSizeY + chunkSize - 1) / chunkSize
        );
        int dimensionOffset = capi.World.Player.Entity.Pos.Dimension
            * GlobalConstants.DimensionSizeInChunks;
        int loadedPositions = 0;
        int oreBlocks = 0;

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

            IChunkBlocks? data = chunk.Data;
            if (data == null) continue;
            int remainingHeight = capi.World.BlockAccessor.MapSizeY
                - chunkY * chunkSize;
            int localHeight = Math.Min(chunkSize, Math.Max(0, remainingHeight));
            for (int localY = 0; localY < localHeight; localY++)
            {
                int index = localX
                    + localZ * chunkSize
                    + localY * chunkSize * chunkSize;
                if (index < 0 || index >= data.Length) break;

                int blockId = data.GetBlockId(index, BlockLayersAccess.Solid);
                loadedPositions++;
                if (oreBlockIds.Contains(blockId)) oreBlocks++;
            }
        }

        if (loadedPositions == 0) return false;

        oreColumnSampleCount++;
        oreBlockHitCount += oreBlocks;
        // A single eight-block cell samples one vertical column. A saturating
        // curve keeps isolated veins visible without claiming knowledge about
        // neighboring columns that the atlas did not inspect.
        density = 1f - MathF.Exp(-oreBlocks / 2.5f);
        return true;
    }

    private static float DecodeDensity(int raw)
    {
        if (raw >= 0 && raw <= 255) return raw / 255f;

        uint packed = unchecked((uint)raw);
        int red = (int)((packed >> 16) & 0xff);
        int green = (int)((packed >> 8) & 0xff);
        int blue = (int)(packed & 0xff);
        return Math.Max(red, Math.Max(green, blue)) / 255f;
    }

    private static int ColorForLayer(AtlasMapLayer layer, float value)
    {
        (float R, float G, float B) color = layer switch
        {
            AtlasMapLayer.SoilFertility => ThreeStop(
                value,
                (0.34f, 0.16f, 0.07f),
                (0.77f, 0.63f, 0.18f),
                (0.12f, 0.64f, 0.22f)
            ),
            AtlasMapLayer.Moisture => ThreeStop(
                value,
                (0.65f, 0.33f, 0.08f),
                (0.18f, 0.72f, 0.68f),
                (0.04f, 0.20f, 0.82f)
            ),
            AtlasMapLayer.Temperature => ThreeStop(
                value,
                (0.08f, 0.25f, 0.88f),
                (0.96f, 0.82f, 0.20f),
                (0.88f, 0.10f, 0.04f)
            ),
            AtlasMapLayer.ForestDensity => ThreeStop(
                value,
                (0.74f, 0.65f, 0.42f),
                (0.28f, 0.62f, 0.22f),
                (0.03f, 0.24f, 0.08f)
            ),
            AtlasMapLayer.GeologicActivity => ThreeStop(
                value,
                (0.30f, 0.34f, 0.40f),
                (0.56f, 0.22f, 0.68f),
                (1.00f, 0.34f, 0.04f)
            ),
            AtlasMapLayer.OreDensity => ThreeStop(
                value,
                (0.20f, 0.08f, 0.28f),
                (0.93f, 0.30f, 0.04f),
                (1.00f, 0.94f, 0.34f)
            ),
            _ => (0f, 0f, 0f)
        };
        return EncodeBgra(color.R, color.G, color.B, 0.92f);
    }

    private static (float R, float G, float B) ThreeStop(
        float value,
        (float R, float G, float B) low,
        (float R, float G, float B) middle,
        (float R, float G, float B) high
    ) => value <= 0.5f
        ? Lerp(low, middle, value * 2f)
        : Lerp(middle, high, (value - 0.5f) * 2f);

    private static (float R, float G, float B) Lerp(
        (float R, float G, float B) from,
        (float R, float G, float B) to,
        float amount
    ) =>
    (
        from.R + (to.R - from.R) * amount,
        from.G + (to.G - from.G) * amount,
        from.B + (to.B - from.B) * amount
    );

    private static int EncodeBgra(float red, float green, float blue, float alpha)
    {
        int r = (int)Math.Round(Math.Clamp(red, 0f, 1f) * 255);
        int g = (int)Math.Round(Math.Clamp(green, 0f, 1f) * 255);
        int b = (int)Math.Round(Math.Clamp(blue, 0f, 1f) * 255);
        int a = (int)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255);
        return unchecked((int)((uint)a << 24 | (uint)r << 16 | (uint)g << 8 | (uint)b));
    }

    private static int FloorToSample(double value) =>
        (int)Math.Floor(value / HorizontalSampleSize) * HorizontalSampleSize;

    private static int CeilToSample(double value) =>
        (int)Math.Ceiling(value / HorizontalSampleSize) * HorizontalSampleSize;
}
