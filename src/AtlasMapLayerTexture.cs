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
    private const int MaximumRetrySamplesPerFrame = 512;
    private const int RetryIntervalMilliseconds = 250;

    private readonly ICoreClientAPI capi;
    private readonly HashSet<(int X, int Z)> sampledOreRegions = new();
    private readonly HashSet<int> oreBlockIds = new();
    private readonly Dictionary<int, string> oreCodeByBlockId = new();
    private readonly HashSet<string> discoveredOreCodeSet = new(StringComparer.Ordinal);
    private readonly List<string> discoveredOreCodes = new();
    private readonly List<int> pendingSampleIndices = new();
    private LoadedTexture? texture;
    private int[]? pixels;
    private int nextSampleIndex;
    private int nextPendingSampleIndex;
    private long nextPendingRetryMilliseconds;
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
    private bool loggedStreamingRefresh;

    public AtlasMapLayer Layer { get; private set; } = AtlasMapLayer.TexturedTerrain;
    public bool Ready { get; private set; }
    public int TextureId => Ready ? texture?.TextureId ?? 0 : 0;
    public int OriginX { get; private set; }
    public int OriginZ { get; private set; }
    public int Width => texture?.Width ?? 0;
    public int Height => texture?.Height ?? 0;
    public string? SelectedOreCode { get; private set; }
    public IReadOnlyList<string> DiscoveredOreCodes => discoveredOreCodes;
    public int OreCodeRevision { get; private set; }
    public bool ContoursEnabled => Layer is AtlasMapLayer.SoilFertility
        or AtlasMapLayer.Moisture
        or AtlasMapLayer.Temperature;
    public int ProgressPercent => pixels == null || pixels.Length == 0
        ? Layer == AtlasMapLayer.TexturedTerrain ? 100 : 0
        : Ready
            ? 100
            : Math.Clamp(nextSampleIndex * 100 / pixels.Length, 0, 99);

    /// <summary>
    /// Which sources the ore layer actually painted from. Regional maps and
    /// loaded block columns can both contribute inside one radius, so a
    /// single found OreMap must not hide the columns that filled the rest.
    /// </summary>
    public AtlasOreLayerSource OreLayerSource
    {
        get
        {
            if (Layer != AtlasMapLayer.OreDensity) return AtlasOreLayerSource.None;
            bool regional = oreMapCount > 0;
            bool columns = oreColumnSampleCount > 0;
            if (regional && columns) return AtlasOreLayerSource.Mixed;
            if (regional) return AtlasOreLayerSource.RegionalPotential;
            if (columns) return AtlasOreLayerSource.LoadedColumns;
            return AtlasOreLayerSource.None;
        }
    }
    public int OreMapCount => oreMapCount;
    public int OreRegionCount => oreRegionCount;
    public int OreColumnSampleCount => oreColumnSampleCount;
    public int OreBlockHitCount => oreBlockHitCount;
    public int ValidSampleCount => validSampleCount;
    public int DiscoveredOreCount => discoveredOreCodes.Count;
    public bool Failed => failed;

    /// <summary>
    /// Status line for the layers that carry one scalar per sample. The ore
    /// layer needs readable ore names, so the dialog builds its line instead.
    /// </summary>
    public string StatusText
    {
        get
        {
            if (Layer == AtlasMapLayer.TexturedTerrain)
            {
                return "Live block materials";
            }
            if (failed)
            {
                return "Unavailable · textured terrain remains active";
            }
            if (!Ready)
            {
                return FormattableString.Invariant(
                    $"Preparing · {ProgressPercent}% · loaded data only"
                );
            }
            if (validSampleCount == 0)
            {
                return "Waiting for streamed data · nothing loaded in range yet";
            }
            return FormattableString.Invariant(
                $"Ready · {validSampleCount:n0} loaded samples · {HorizontalSampleSize} × {HorizontalSampleSize} grid"
            );
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
        bool spoilerAccess,
        string? selectedOreCode = null
    )
    {
        if (layer.RequiresSpoilerAccess() && !spoilerAccess)
        {
            layer = AtlasMapLayer.TexturedTerrain;
        }

        Layer = layer;
        SelectedOreCode = layer == AtlasMapLayer.OreDensity
            && !string.IsNullOrWhiteSpace(selectedOreCode)
                ? selectedOreCode.Trim()
                : null;
        Ready = layer == AtlasMapLayer.TexturedTerrain;
        pixels = null;
        nextSampleIndex = 0;
        nextPendingSampleIndex = 0;
        nextPendingRetryMilliseconds = 0;
        pendingSampleIndices.Clear();
        validSampleCount = 0;
        oreRegionCount = 0;
        oreMapCount = 0;
        oreColumnSampleCount = 0;
        oreBlockHitCount = 0;
        sampledOreRegions.Clear();
        oreBlockIds.Clear();
        oreCodeByBlockId.Clear();
        discoveredOreCodeSet.Clear();
        discoveredOreCodes.Clear();
        OreCodeRevision++;
        failed = false;
        loggedSamplingFailure = false;
        loggedStreamingRefresh = false;
        if (Ready) return;

        if (Layer == AtlasMapLayer.OreDensity)
        {
            foreach (Block block in capi.World.Blocks)
            {
                if (block?.Id > 0 && block.BlockMaterial == EnumBlockMaterial.Ore)
                {
                    oreBlockIds.Add(block.Id);
                    oreCodeByBlockId[block.Id] = ResolveOreBlockCode(block);
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
        EnsureTextureSize(width, height);
        if (texture == null) return;
        texture.Width = width;
        texture.Height = height;
    }

    private void EnsureTextureSize(int width, int height)
    {
        if (texture != null
            && texture.TextureId > 0
            && (texture.Width != width || texture.Height != height))
        {
            capi.Logger.Notification(
                "[ModernAtlas] Reallocating the transient map-layer texture from {0}x{1} to {2}x{3} after the game view distance changed.",
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
        if (Ready)
        {
            AdvancePendingSamples();
            return true;
        }
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
                if (!FillSample(nextSampleIndex))
                {
                    pendingSampleIndices.Add(nextSampleIndex);
                }
            }
            catch (Exception exception)
            {
                pendingSampleIndices.Add(nextSampleIndex);
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

    private void AdvancePendingSamples()
    {
        if (failed
            || pixels == null
            || texture == null
            || pendingSampleIndices.Count == 0
            || capi.ElapsedMilliseconds < nextPendingRetryMilliseconds)
        {
            return;
        }

        nextPendingRetryMilliseconds = capi.ElapsedMilliseconds
            + RetryIntervalMilliseconds;
        long started = Stopwatch.GetTimestamp();
        int processed = 0;
        bool pixelsChanged = false;
        while (pendingSampleIndices.Count > 0
            && processed < MaximumRetrySamplesPerFrame
            && (processed == 0
                || Stopwatch.GetElapsedTime(started).TotalMilliseconds
                    < WorkBudgetMilliseconds))
        {
            if (nextPendingSampleIndex >= pendingSampleIndices.Count)
            {
                nextPendingSampleIndex = 0;
            }

            int listIndex = nextPendingSampleIndex;
            int sampleIndex = pendingSampleIndices[listIndex];
            int previousPixel = pixels[sampleIndex];
            bool completed = false;
            try
            {
                completed = FillSample(sampleIndex);
            }
            catch (Exception exception)
            {
                if (!loggedSamplingFailure)
                {
                    loggedSamplingFailure = true;
                    capi.Logger.Warning(
                        "[ModernAtlas] A streaming map-layer sample could not be read and will be retried: {0}",
                        exception.Message
                    );
                }
            }

            pixelsChanged |= pixels[sampleIndex] != previousPixel;
            if (completed)
            {
                int lastIndex = pendingSampleIndices.Count - 1;
                pendingSampleIndices[listIndex] = pendingSampleIndices[lastIndex];
                pendingSampleIndices.RemoveAt(lastIndex);
            }
            else
            {
                nextPendingSampleIndex++;
            }
            processed++;
        }

        if (!pixelsChanged) return;
        try
        {
            capi.Render.LoadOrUpdateTextureFromBgra(pixels, true, 0, ref texture);
            if (!loggedStreamingRefresh)
            {
                loggedStreamingRefresh = true;
                capi.Logger.Notification(
                    "[ModernAtlas] The active atlas layer now fills newly loaded client chunks automatically."
                );
            }
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not refresh the atlas-only {0} layer after client chunks streamed in: {1}",
                Layer.DisplayName(),
                exception.Message
            );
        }
    }

    public void Reset()
    {
        Layer = AtlasMapLayer.TexturedTerrain;
        Ready = true;
        pixels = null;
        nextSampleIndex = 0;
        nextPendingSampleIndex = 0;
        nextPendingRetryMilliseconds = 0;
        pendingSampleIndices.Clear();
        validSampleCount = 0;
        oreRegionCount = 0;
        oreMapCount = 0;
        oreColumnSampleCount = 0;
        oreBlockHitCount = 0;
        sampledOreRegions.Clear();
        oreBlockIds.Clear();
        oreCodeByBlockId.Clear();
        discoveredOreCodeSet.Clear();
        discoveredOreCodes.Clear();
        SelectedOreCode = null;
        OreCodeRevision++;
        failed = false;
        loggedSamplingFailure = false;
        loggedStreamingRefresh = false;
    }

    public void Dispose()
    {
        texture?.Dispose();
        texture = null;
        pixels = null;
        pendingSampleIndices.Clear();
        discoveredOreCodeSet.Clear();
        discoveredOreCodes.Clear();
        SelectedOreCode = null;
        OreCodeRevision++;
        Ready = false;
    }

    private bool FillSample(int index)
    {
        if (pixels == null || texture == null) return false;

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
            return true;
        }
        if (worldX < 0 || worldZ < 0
            || worldX >= capi.World.BlockAccessor.MapSizeX
            || worldZ >= capi.World.BlockAccessor.MapSizeZ)
        {
            return true;
        }

        float value;
        if (Layer == AtlasMapLayer.OreDensity)
        {
            // Regional ore-potential maps and exact loaded block columns do
            // not depend on the terrain-height map. Requiring that separate
            // map-chunk field here left otherwise loaded terrain uncolored.
            // A loaded column with no ore is a valid zero sample (blue); a
            // genuinely unavailable column still returns false and remains
            // transparent so the atlas does not invent knowledge.
            if (!TryReadOreDensity(worldX, worldZ, out value))
            {
                // Exact GPU terrain can remain drawable after the CPU block
                // column needed for the fallback scan is no longer present.
                // Give that already visible terrain a distinct dark-blue
                // unknown state instead of leaking its normal material color;
                // do not count it as a valid zero-ore result.
                pixels[index] = UnavailableOreColor();
                return false;
            }
        }
        else
        {
            if (!TryReadClimateSample(worldX, worldZ, out _, out value, out _))
            {
                return false;
            }
        }

        pixels[index] = ColorForLayer(Layer, Math.Clamp(value, 0f, 1f));
        validSampleCount++;
        return true;
    }

    /// <summary>
    /// Reads one world-generation climate sample from already loaded data.
    /// Returns the value normalized for the ramp and the layer's own natural
    /// unit (fertility and rainfall as a fraction, temperature in degrees
    /// Celsius). Nothing is requested from the server here.
    /// </summary>
    private bool TryReadClimateSample(
        int worldX,
        int worldZ,
        out int surfaceY,
        out float normalized,
        out float natural
    )
    {
        surfaceY = 0;
        normalized = 0f;
        natural = 0f;
        int chunkSize = GlobalConstants.ChunkSize;
        int chunkX = worldX / chunkSize;
        int chunkZ = worldZ / chunkSize;
        IMapChunk? mapChunk = capi.World.BlockAccessor.GetMapChunk(chunkX, chunkZ);
        ushort[]? heightMap = mapChunk?.WorldGenTerrainHeightMap;
        if (heightMap == null || heightMap.Length < chunkSize * chunkSize) return false;

        int localX = worldX - chunkX * chunkSize;
        int localZ = worldZ - chunkZ * chunkSize;
        surfaceY = heightMap[localZ * chunkSize + localX];
        ClimateCondition? climate = capi.World.BlockAccessor.GetClimateAt(
            new BlockPos(worldX, surfaceY, worldZ),
            EnumGetClimateMode.WorldGenValues
        );
        if (climate == null) return false;

        switch (Layer)
        {
            case AtlasMapLayer.SoilFertility:
                normalized = climate.Fertility;
                natural = climate.Fertility;
                return true;
            case AtlasMapLayer.Moisture:
                normalized = climate.WorldgenRainfall;
                natural = climate.WorldgenRainfall;
                return true;
            case AtlasMapLayer.Temperature:
                normalized = (climate.WorldGenTemperature + 20f) / 60f;
                natural = climate.WorldGenTemperature;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Hover read-out for the climate layers. It reuses the sample the layer
    /// already draws, so the card can never claim knowledge the overlay does
    /// not have.
    /// </summary>
    public bool TryInspectClimate(
        int worldX,
        int worldZ,
        out AtlasClimateInspection? inspection
    )
    {
        inspection = null;
        if (Layer is not (AtlasMapLayer.SoilFertility
                or AtlasMapLayer.Moisture
                or AtlasMapLayer.Temperature)
            || worldX < 0
            || worldZ < 0
            || worldX >= capi.World.BlockAccessor.MapSizeX
            || worldZ >= capi.World.BlockAccessor.MapSizeZ)
        {
            return false;
        }

        double dx = worldX - disclosureCenterX;
        double dz = worldZ - disclosureCenterZ;
        if (dx * dx + dz * dz > (double)disclosureRadius * disclosureRadius)
        {
            return false;
        }

        if (!TryReadClimateSample(
            worldX,
            worldZ,
            out int surfaceY,
            out float normalized,
            out float natural
        ))
        {
            return false;
        }

        inspection = new AtlasClimateInspection(
            worldX,
            worldZ,
            surfaceY,
            Layer,
            Math.Clamp(normalized, 0f, 1f),
            natural
        );
        return true;
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
            RegisterOreCode(entry.Key);
            int raw = map.GetUnpaddedColorLerpedForNormalizedPos(
                Math.Clamp(normalizedX, 0f, 1f),
                Math.Clamp(normalizedZ, 0f, 1f)
            );
            if (SelectedOreCode == null
                || string.Equals(entry.Key, SelectedOreCode, StringComparison.Ordinal))
            {
                density = Math.Max(density, AtlasRegionalOreReadings.DecodeDensity(raw));
            }
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
        bool loaded = ReadLoadedOreColumn(
            worldX,
            worldZ,
            null,
            SelectedOreCode,
            out _,
            out int oreBlocks,
            out int visibleOreBlocks
        );
        if (!loaded)
        {
            density = 0;
            return false;
        }

        oreColumnSampleCount++;
        oreBlockHitCount += visibleOreBlocks;
        // A single eight-block cell samples one vertical column. A saturating
        // curve keeps isolated veins visible without claiming knowledge about
        // neighboring columns that the atlas did not inspect.
        density = 1f - MathF.Exp(-visibleOreBlocks / 2.5f);
        return true;
    }

    private bool ReadLoadedOreColumn(
        int worldX,
        int worldZ,
        Dictionary<string, int>? counts,
        string? filterCode,
        out int loadedPositions,
        out int oreBlocks,
        out int matchingOreBlocks
    )
    {
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
        loadedPositions = 0;
        oreBlocks = 0;
        matchingOreBlocks = 0;

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
                if (!oreBlockIds.Contains(blockId)) continue;
                oreBlocks++;
                if (oreCodeByBlockId.TryGetValue(blockId, out string? oreCode))
                {
                    RegisterOreCode(oreCode);
                    if (filterCode == null
                        || string.Equals(oreCode, filterCode, StringComparison.Ordinal))
                    {
                        matchingOreBlocks++;
                    }
                    if (counts != null)
                    {
                        counts.TryGetValue(oreCode, out int count);
                        counts[oreCode] = count + 1;
                    }
                }
            }
        }

        return loadedPositions > 0;
    }

    public bool TryInspectOre(int worldX, int worldZ, out AtlasOreInspection? inspection)
    {
        inspection = null;
        if (Layer != AtlasMapLayer.OreDensity
            || worldX < 0
            || worldZ < 0
            || worldX >= capi.World.BlockAccessor.MapSizeX
            || worldZ >= capi.World.BlockAccessor.MapSizeZ)
        {
            return false;
        }

        double dx = worldX - disclosureCenterX;
        double dz = worldZ - disclosureCenterZ;
        if (dx * dx + dz * dz > (double)disclosureRadius * disclosureRadius)
        {
            return false;
        }

        int chunkSize = GlobalConstants.ChunkSize;
        int chunkX = worldX / chunkSize;
        int chunkZ = worldZ / chunkSize;
        IMapChunk? mapChunk = capi.World.BlockAccessor.GetMapChunk(chunkX, chunkZ);
        ushort[]? heightMap = mapChunk?.WorldGenTerrainHeightMap;
        if (heightMap == null || heightMap.Length < chunkSize * chunkSize) return false;

        int localX = worldX - chunkX * chunkSize;
        int localZ = worldZ - chunkZ * chunkSize;
        int surfaceY = heightMap[localZ * chunkSize + localX];
        string? hostRockCode = FindLoadedHostRock(worldX, surfaceY, worldZ);

        int regionSize = Math.Max(1, capi.World.BlockAccessor.RegionSize);
        int regionX = worldX / regionSize;
        int regionZ = worldZ / regionSize;
        IMapRegion? region = capi.World.BlockAccessor.GetMapRegion(regionX, regionZ);
        if (region?.OreMaps != null && region.OreMaps.Count > 0)
        {
            float normalizedX = (worldX - regionX * regionSize) / (float)regionSize;
            float normalizedZ = (worldZ - regionZ * regionSize) / (float)regionSize;
            var encounteredCodes = new List<string>();
            AtlasOreReading[] readings = AtlasRegionalOreReadings.Build(
                region.OreMaps,
                normalizedX,
                normalizedZ,
                SelectedOreCode,
                encounteredCodes,
                out int sourceMapCount
            );
            foreach (string code in encounteredCodes) RegisterOreCode(code);
            inspection = new AtlasOreInspection(
                worldX,
                worldZ,
                surfaceY,
                hostRockCode,
                AtlasOreInspectionSource.RegionalOreMaps,
                sourceMapCount,
                readings
            );
            return true;
        }

        var blockCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!ReadLoadedOreColumn(
            worldX,
            worldZ,
            blockCounts,
            null,
            out _,
            out _,
            out _
        ))
        {
            return false;
        }

        var blockReadings = new List<AtlasOreReading>(blockCounts.Count);
        foreach (KeyValuePair<string, int> entry in blockCounts)
        {
            blockReadings.Add(new AtlasOreReading(entry.Key, 0, entry.Value, false));
        }
        blockReadings.Sort((left, right) => right.BlockCount.CompareTo(left.BlockCount));
        inspection = new AtlasOreInspection(
            worldX,
            worldZ,
            surfaceY,
            hostRockCode,
            AtlasOreInspectionSource.LoadedBlockColumn,
            0,
            blockReadings.ToArray()
        );
        return true;
    }

    private string? FindLoadedHostRock(int worldX, int surfaceY, int worldZ)
    {
        for (int y = surfaceY; y >= Math.Max(0, surfaceY - 96); y--)
        {
            if (!TryGetLoadedBlock(worldX, y, worldZ, out Block? block)) return null;
            if (block?.BlockMaterial == EnumBlockMaterial.Stone)
            {
                return block.Code?.ToString();
            }
        }
        return null;
    }

    private bool TryGetLoadedBlock(int worldX, int worldY, int worldZ, out Block? block)
    {
        block = null;
        int chunkSize = GlobalConstants.ChunkSize;
        int chunkX = worldX / chunkSize;
        int chunkY = worldY / chunkSize;
        int chunkZ = worldZ / chunkSize;
        int dimensionOffset = capi.World.Player.Entity.Pos.Dimension
            * GlobalConstants.DimensionSizeInChunks;
        IWorldChunk? chunk = capi.World.BlockAccessor.GetChunk(
            chunkX,
            chunkY + dimensionOffset,
            chunkZ
        );
        if (chunk == null
            || chunk.Disposed
            || chunk is IClientChunk clientChunk && !clientChunk.LoadedFromServer
            || chunk.Data == null)
        {
            return false;
        }

        int localX = worldX - chunkX * chunkSize;
        int localY = worldY - chunkY * chunkSize;
        int localZ = worldZ - chunkZ * chunkSize;
        int index = localX + localZ * chunkSize + localY * chunkSize * chunkSize;
        if (index < 0 || index >= chunk.Data.Length) return false;
        int blockId = chunk.Data.GetBlockId(index, BlockLayersAccess.Solid);
        if (blockId < 0 || blockId >= capi.World.Blocks.Count) return false;
        block = capi.World.Blocks[blockId];
        return true;
    }

    private void RegisterOreCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        string normalized = code.Trim();
        if (!discoveredOreCodeSet.Add(normalized)) return;
        discoveredOreCodes.Add(normalized);
        discoveredOreCodes.Sort(StringComparer.Ordinal);
        OreCodeRevision++;
    }

    private static string ResolveOreBlockCode(Block block)
    {
        if (block.Variant != null
            && block.Variant.TryGetValue("type", out string? type)
            && !string.IsNullOrWhiteSpace(type))
        {
            return type;
        }

        string path = block.Code?.Path ?? "unknown-ore";
        string[] segments = path.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3
            && segments[0] == "ore"
            && segments[1] is "poor" or "medium" or "rich" or "bountiful")
        {
            return segments[2];
        }
        return segments.Length >= 2 && segments[0] == "ore"
            ? segments[1]
            : path;
    }

    private static int ColorForLayer(AtlasMapLayer layer, float value)
    {
        (float R, float G, float B) color = AtlasMapLayerPalette.Color(layer, value);
        // Zero alpha remains the invalid/unloaded sentinel. Valid samples use
        // the upper three quarters of alpha to carry the normalized scalar so
        // the shader can draw terrain-following contours independently from
        // the user-selected overlay opacity.
        float encodedScalar = 0.25f + Math.Clamp(
            AtlasMapLayerPalette.ExpandContrast(Math.Clamp(value, 0f, 1f)),
            0f,
            1f
        ) * 0.75f;
        return EncodeBgra(color.R, color.G, color.B, encodedScalar);
    }

    private static int UnavailableOreColor()
    {
        (float R, float G, float B) color = AtlasMapLayerPalette.Unavailable;
        return EncodeBgra(color.R, color.G, color.B, 0.25f);
    }

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
