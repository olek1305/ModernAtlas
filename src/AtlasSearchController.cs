using System;
using System.Collections.Generic;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace ModernAtlas;

/// <summary>
/// Incrementally searches only chunks and entities already present on the
/// client. Work is deliberately spread over atlas frames and never asks the
/// server or chunk provider to load additional world data.
/// </summary>
internal sealed class AtlasSearchController
{
    private const int MinimumQueryLength = 2;
    private const int QueryDebounceMilliseconds = 280;
    private const int WorkBudgetMilliseconds = 3;
    private const int MaximumBlockMarkers = 256;
    private const int MaximumDynamicMarkers = 128;
    private const int BlockMarkerCellSize = 4;
    private const int DynamicRefreshMilliseconds = 200;

    private readonly ICoreClientAPI capi;
    private readonly AtlasSearchLanguageIndex languageIndex;
    private readonly HashSet<int> matchingBlockIds = new();
    private readonly HashSet<(int X, int Y, int Z)> occupiedBlockCells = new();
    private readonly List<int> fuzzyBlockIds = new();
    private readonly List<ChunkColumnCandidate> chunkColumns = new();
    private readonly List<int> verticalChunkOrder = new();
    private readonly List<AtlasSearchResult> blockResults = new();
    private readonly List<AtlasSearchResult> dynamicResults = new();

    private string query = "";
    private string compactQuery = "";
    private SearchStage stage;
    private long queryChangedMilliseconds;
    private long lastDynamicRefreshMilliseconds;
    private int blockRegistryIndex;
    private int minimumChunkX;
    private int maximumChunkX;
    private int minimumChunkZ;
    private int maximumChunkZ;
    private int preparingChunkX;
    private int preparingChunkZ;
    private int chunkColumnIndex;
    private int verticalChunkIndex;
    private int probedChunkCoordinates;
    private IWorldChunk? currentChunk;
    private ChunkCandidate currentChunkCandidate;
    private int currentBlockIndex;
    private int requestedViewDistance;
    private bool requestedSurfaceSafety;
    private bool blockResultsTruncated;
    private int inspectedChunks;
    private int readyChunks;
    private int paletteMatchingChunks;
    private int scannedBlockPositions;
    private int rawBlockMatches;
    private int radiusRejectedMatches;
    private int surfaceRejectedMatches;

    public IReadOnlyList<AtlasSearchResult> BlockResults => blockResults;
    public IReadOnlyList<AtlasSearchResult> DynamicResults => dynamicResults;
    public bool HasActiveQuery => query.Length >= MinimumQueryLength;
    public bool BlockScanComplete => stage == SearchStage.Complete;
    public string Query => query;
    public string SearchLanguageSummary => languageIndex.LanguageSummary;
    public string DiagnosticSummary =>
        $"blockTypes={matchingBlockIds.Count}, columns={chunkColumns.Count}, probedCoordinates={probedChunkCoordinates}, inspectedChunks={inspectedChunks}, readyChunks={readyChunks}, paletteChunks={paletteMatchingChunks}, scannedPositions={scannedBlockPositions}, rawMatches={rawBlockMatches}, radiusRejected={radiusRejectedMatches}, surfaceRejected={surfaceRejectedMatches}, markers={blockResults.Count}";

    public string StatusText
    {
        get
        {
            if (query.Length == 0) return "";
            if (query.Length < MinimumQueryLength)
            {
                return "Type at least 2 characters • loaded data only";
            }

            int markerCount = blockResults.Count + dynamicResults.Count;
            string markerText = markerCount == 1
                ? "1 marker"
                : $"{markerCount} markers";
            string scanText = stage switch
            {
                SearchStage.Debounce => "waiting for input",
                SearchStage.ResolveLanguageAliases => "matching translated names",
                SearchStage.ResolveBlockTypes => "matching block types",
                SearchStage.PrepareChunks => "collecting loaded chunks",
                SearchStage.ScanChunks =>
                    $"scanning loaded chunks {ScanProgressPercent}%",
                SearchStage.Complete when blockResultsTruncated =>
                    $"showing the first {MaximumBlockMarkers} block markers",
                SearchStage.Complete => "search complete",
                _ => "loaded data only"
            };
            return $"{markerText} • {scanText} • no distant chunk requests";
        }
    }

    private int ScanProgressPercent => chunkColumns.Count == 0
        ? 100
        : Math.Clamp(
            probedChunkCoordinates * 100
                / Math.Max(1, chunkColumns.Count * verticalChunkOrder.Count),
            0,
            99
        );

    public AtlasSearchController(ICoreClientAPI capi)
    {
        this.capi = capi;
        languageIndex = new AtlasSearchLanguageIndex(capi);
    }

    public void SetQuery(string? value)
    {
        string normalized = AtlasSearchLanguageIndex.Normalize(value);
        if (string.Equals(query, normalized, StringComparison.Ordinal)) return;

        query = normalized;
        compactQuery = AtlasSearchLanguageIndex.Compact(query);
        queryChangedMilliseconds = capi.ElapsedMilliseconds;
        RestartSearch(SearchStage.Debounce);
    }

    internal void SetQueryImmediatelyForAutomatedTest(string value)
    {
        query = AtlasSearchLanguageIndex.Normalize(value);
        compactQuery = AtlasSearchLanguageIndex.Compact(query);
        queryChangedMilliseconds = 0;
        RestartSearch(query.Length >= MinimumQueryLength
            ? SearchStage.ResolveLanguageAliases
            : SearchStage.Idle);
    }

    public void Clear()
    {
        query = "";
        compactQuery = "";
        queryChangedMilliseconds = 0;
        RestartSearch(SearchStage.Idle);
    }

    public void Advance(
        int viewDistanceBlocks,
        bool surfaceSafety,
        AtlasSurfaceHeightTexture surfaceHeightTexture,
        IReadOnlyList<AtlasRenderedEntity> renderedEntities,
        bool allowDroppedItems
    )
    {
        if (!HasActiveQuery)
        {
            dynamicResults.Clear();
            return;
        }

        if (requestedViewDistance != 0
            && (requestedViewDistance != viewDistanceBlocks
                || requestedSurfaceSafety != surfaceSafety))
        {
            RestartSearch(SearchStage.ResolveLanguageAliases);
        }
        requestedViewDistance = viewDistanceBlocks;
        requestedSurfaceSafety = surfaceSafety;

        RefreshDynamicResults(
            renderedEntities,
            allowDroppedItems,
            surfaceSafety,
            surfaceHeightTexture,
            viewDistanceBlocks
        );

        if (surfaceSafety && !surfaceHeightTexture.Ready) return;
        if (stage == SearchStage.Debounce)
        {
            if (capi.ElapsedMilliseconds - queryChangedMilliseconds
                < QueryDebounceMilliseconds)
            {
                return;
            }
            stage = SearchStage.ResolveLanguageAliases;
        }

        long started = Stopwatch.GetTimestamp();
        while (WithinBudget(started))
        {
            if (stage == SearchStage.ResolveLanguageAliases)
            {
                if (!languageIndex.ResolveBlockQueryStep(query, compactQuery))
                {
                    stage = SearchStage.ResolveBlockTypes;
                }
                continue;
            }
            if (stage == SearchStage.ResolveBlockTypes)
            {
                if (!ResolveBlockTypesStep()) break;
                continue;
            }
            if (stage == SearchStage.PrepareChunks)
            {
                if (!PrepareChunkCandidatesStep()) break;
                continue;
            }
            if (stage == SearchStage.ScanChunks)
            {
                if (!ScanChunksStep(surfaceSafety, surfaceHeightTexture)) break;
                continue;
            }
            break;
        }
    }

    private bool ResolveBlockTypesStep()
    {
        IList<Block> blocks = capi.World.Blocks;
        if (blockRegistryIndex >= blocks.Count)
        {
            InitializeChunkCandidatePreparation();
            stage = SearchStage.PrepareChunks;
            return true;
        }

        Block block = blocks[blockRegistryIndex++];
        if (block?.Code == null || block.Id == 0) return true;

        // Asset codes are stable, language-independent and safe to inspect
        // while Vintage Story builds the handbook on a worker thread. Calling
        // GetHeldItemName here would concurrently mutate the engine's global
        // translation service and can corrupt its non-concurrent hash sets.
        if (Matches(block.Code.ToString())
            || languageIndex.MatchesPreparedBlock(block.Code))
        {
            matchingBlockIds.Add(block.Id);
        }
        return true;
    }

    private bool PrepareChunkCandidatesStep()
    {
        if (preparingChunkZ > maximumChunkZ)
        {
            chunkColumns.Sort(static (left, right) =>
                left.HorizontalDistanceSquared.CompareTo(
                    right.HorizontalDistanceSquared
                )
            );
            chunkColumnIndex = 0;
            verticalChunkIndex = 0;
            stage = matchingBlockIds.Count == 0 || chunkColumns.Count == 0
                ? SearchStage.Complete
                : SearchStage.ScanChunks;
            return true;
        }

        int chunkSize = GlobalConstants.ChunkSize;
        int chunkX = preparingChunkX;
        int chunkZ = preparingChunkZ;
        preparingChunkX++;
        if (preparingChunkX > maximumChunkX)
        {
            preparingChunkX = minimumChunkX;
            preparingChunkZ++;
        }

        double chunkCenterX = chunkX * chunkSize + chunkSize * 0.5;
        double chunkCenterZ = chunkZ * chunkSize + chunkSize * 0.5;
        double dx = chunkCenterX - capi.World.Player.Entity.Pos.X;
        double dz = chunkCenterZ - capi.World.Player.Entity.Pos.Z;
        double allowance = requestedViewDistance + chunkSize * 0.75;
        double horizontalDistanceSquared = dx * dx + dz * dz;
        if (horizontalDistanceSquared > allowance * allowance) return true;

        chunkColumns.Add(new ChunkColumnCandidate(
            chunkX,
            chunkZ,
            horizontalDistanceSquared
        ));
        return true;
    }

    private void InitializeChunkCandidatePreparation()
    {
        int chunkSize = GlobalConstants.ChunkSize;
        int radiusInChunks = (requestedViewDistance + chunkSize - 1) / chunkSize + 1;
        int playerChunkX = (int)Math.Floor(
            capi.World.Player.Entity.Pos.X / chunkSize
        );
        int playerChunkY = (int)Math.Floor(
            capi.World.Player.Entity.Pos.Y / chunkSize
        );
        int playerChunkZ = (int)Math.Floor(
            capi.World.Player.Entity.Pos.Z / chunkSize
        );
        int mapChunksX = Math.Max(1, capi.World.BlockAccessor.MapSizeX / chunkSize);
        int mapChunksZ = Math.Max(1, capi.World.BlockAccessor.MapSizeZ / chunkSize);
        int mapChunksY = Math.Max(
            1,
            (capi.World.BlockAccessor.MapSizeY + chunkSize - 1) / chunkSize
        );
        minimumChunkX = Math.Clamp(playerChunkX - radiusInChunks, 0, mapChunksX - 1);
        maximumChunkX = Math.Clamp(playerChunkX + radiusInChunks, 0, mapChunksX - 1);
        minimumChunkZ = Math.Clamp(playerChunkZ - radiusInChunks, 0, mapChunksZ - 1);
        maximumChunkZ = Math.Clamp(playerChunkZ + radiusInChunks, 0, mapChunksZ - 1);
        preparingChunkX = minimumChunkX;
        preparingChunkZ = minimumChunkZ;

        verticalChunkOrder.Clear();
        for (int distance = 0; verticalChunkOrder.Count < mapChunksY; distance++)
        {
            int below = playerChunkY - distance;
            int above = playerChunkY + distance;
            if (below >= 0 && below < mapChunksY)
            {
                verticalChunkOrder.Add(below);
            }
            if (distance > 0 && above >= 0 && above < mapChunksY)
            {
                verticalChunkOrder.Add(above);
            }
        }
    }

    private bool ScanChunksStep(
        bool surfaceSafety,
        AtlasSurfaceHeightTexture surfaceHeightTexture
    )
    {
        if (blockResults.Count >= MaximumBlockMarkers)
        {
            blockResultsTruncated = true;
            currentChunk = null;
            stage = SearchStage.Complete;
            return false;
        }

        if (currentChunk == null)
        {
            if (!TryBeginNextChunk())
            {
                stage = SearchStage.Complete;
                return false;
            }
        }

        if (currentChunk == null || currentChunk.Disposed)
        {
            AbandonCurrentChunk();
            return true;
        }

        IChunkBlocks? data;
        int dataLength;
        int index;
        int solidId;
        int fluidId;
        try
        {
            data = currentChunk.Data;
            dataLength = data?.Length ?? 0;
            if (data == null || currentBlockIndex >= dataLength)
            {
                AbandonCurrentChunk();
                return true;
            }

            index = currentBlockIndex++;
            scannedBlockPositions++;
            solidId = data.GetBlockId(index, BlockLayersAccess.Solid);
            fluidId = data.GetFluid(index);
        }
        catch (Exception exception) when (IsTransientChunkAccessFailure(exception))
        {
            // Streaming can dispose a loaded chunk between the checks above
            // and the actual palette read. Skip it and continue next frame.
            AbandonCurrentChunk();
            return true;
        }

        int chunkSize = GlobalConstants.ChunkSize;
        int matchingId = matchingBlockIds.Contains(solidId)
            ? solidId
            : matchingBlockIds.Contains(fluidId)
                ? fluidId
                : 0;
        if (matchingId != 0)
        {
            rawBlockMatches++;
            int localX = index % chunkSize;
            int localZ = (index / chunkSize) % chunkSize;
            int localY = index / (chunkSize * chunkSize);
            int worldX = currentChunkCandidate.X * chunkSize + localX;
            int worldY = currentChunkCandidate.Y * chunkSize + localY;
            int worldZ = currentChunkCandidate.Z * chunkSize + localZ;
            bool insideRadius = IsInsidePlayerRadius(worldX + 0.5, worldZ + 0.5);
            bool surfaceSafe = IsSurfaceSafe(
                    worldX + 0.5,
                    worldY,
                    worldZ + 0.5,
                    surfaceSafety,
                    surfaceHeightTexture
                );
            if (!insideRadius) radiusRejectedMatches++;
            else if (!surfaceSafe) surfaceRejectedMatches++;
            else
            {
                AddBlockResult(worldX, worldY, worldZ, matchingId);
            }
        }

        if (currentBlockIndex >= dataLength)
        {
            AbandonCurrentChunk();
        }
        return true;
    }

    private bool TryBeginNextChunk()
    {
        int dimensionOffset = capi.World.Player.Entity.Pos.Dimension
            * GlobalConstants.DimensionSizeInChunks;
        while (chunkColumnIndex < chunkColumns.Count)
        {
            ChunkColumnCandidate column = chunkColumns[chunkColumnIndex];
            int chunkY = verticalChunkOrder[verticalChunkIndex++];
            if (verticalChunkIndex >= verticalChunkOrder.Count)
            {
                verticalChunkIndex = 0;
                chunkColumnIndex++;
            }
            probedChunkCoordinates++;
            inspectedChunks++;
            IWorldChunk? chunk = capi.World.BlockAccessor.GetChunk(
                column.X,
                chunkY + dimensionOffset,
                column.Z
            );
            if (chunk == null
                || chunk.Disposed
                || chunk is IClientChunk clientChunk && !clientChunk.LoadedFromServer)
            {
                continue;
            }
            readyChunks++;

            IChunkBlocks? chunkData;
            fuzzyBlockIds.Clear();
            try
            {
                chunkData = chunk.Data;
                if (chunkData == null) continue;
                chunkData.FuzzyListBlockIds(fuzzyBlockIds);
            }
            catch (Exception exception) when (IsTransientChunkAccessFailure(exception))
            {
                // A chunk can be unloaded by the streaming thread after
                // GetChunk returns it. It is no longer eligible atlas data.
                continue;
            }
            bool mayContainMatch = false;
            foreach (int blockId in fuzzyBlockIds)
            {
                if (!matchingBlockIds.Contains(blockId)) continue;
                mayContainMatch = true;
                break;
            }
            if (!mayContainMatch) continue;
            paletteMatchingChunks++;

            try
            {
                chunk.Unpack_ReadOnly();
                if (chunk.Disposed || chunk.Data == null) continue;
            }
            catch (Exception exception) when (IsTransientChunkAccessFailure(exception))
            {
                continue;
            }
            currentChunk = chunk;
            currentChunkCandidate = new ChunkCandidate(column.X, chunkY, column.Z);
            currentBlockIndex = 0;
            return true;
        }
        return false;
    }

    private void AbandonCurrentChunk()
    {
        currentChunk = null;
        currentBlockIndex = 0;
    }

    private static bool IsTransientChunkAccessFailure(Exception exception)
    {
        return exception is NullReferenceException
            or ObjectDisposedException
            or InvalidOperationException
            or IndexOutOfRangeException
            or ArgumentOutOfRangeException;
    }

    private void AddBlockResult(int worldX, int worldY, int worldZ, int blockId)
    {
        var cell = (
            X: FloorDivide(worldX, BlockMarkerCellSize),
            Y: FloorDivide(worldY, BlockMarkerCellSize),
            Z: FloorDivide(worldZ, BlockMarkerCellSize)
        );
        if (!occupiedBlockCells.Add(cell)) return;

        string label = capi.World.GetBlock(blockId)?.Code?.ToString() ?? "Block";
        blockResults.Add(new AtlasSearchResult(
            worldX + 0.5,
            worldY + 0.5,
            worldZ + 0.5,
            AtlasSearchResultKind.Block,
            label
        ));
    }

    private void RefreshDynamicResults(
        IReadOnlyList<AtlasRenderedEntity> renderedEntities,
        bool allowDroppedItems,
        bool surfaceSafety,
        AtlasSurfaceHeightTexture surfaceHeightTexture,
        int viewDistanceBlocks
    )
    {
        long now = capi.ElapsedMilliseconds;
        if (now - lastDynamicRefreshMilliseconds < DynamicRefreshMilliseconds) return;
        lastDynamicRefreshMilliseconds = now;
        dynamicResults.Clear();

        foreach (AtlasRenderedEntity rendered in renderedEntities)
        {
            if (dynamicResults.Count >= MaximumDynamicMarkers) break;
            Entity entity = rendered.Entity;
            if (!entity.Alive || !MatchesEntity(entity)) continue;
            dynamicResults.Add(new AtlasSearchResult(
                entity.Pos.X,
                entity.Pos.Y + Math.Max(0.5, entity.SelectionBox.Y2),
                entity.Pos.Z,
                ToSearchKind(rendered.Kind),
                GetEntityLabel(entity)
            ));
        }

        if (!allowDroppedItems || dynamicResults.Count >= MaximumDynamicMarkers) return;
        try
        {
            foreach (Entity entity in capi.World.LoadedEntities.Values)
            {
                if (dynamicResults.Count >= MaximumDynamicMarkers) break;
                if (entity is not EntityItem item
                    || !entity.Alive
                    || entity.Pos.Dimension != capi.World.Player.Entity.Pos.Dimension
                    || !IsInsidePlayerRadius(entity.Pos.X, entity.Pos.Z, viewDistanceBlocks)
                    || !IsSurfaceSafe(
                        entity.Pos.X,
                        entity.Pos.Y,
                        entity.Pos.Z,
                        surfaceSafety,
                        surfaceHeightTexture
                    )
                )
                {
                    continue;
                }

                ItemStack? stack = item.Itemstack;
                string code = stack?.Collectible?.Code?.ToString() ?? "";
                string name = AtlasSafeDisplayName.ForItemStack(
                    stack,
                    "Dropped item"
                );
                if (!Matches(code)
                    && !Matches(name)
                    && !languageIndex.Matches(
                        stack?.Collectible?.Code,
                        AtlasSearchAliasKind.Item,
                        query,
                        compactQuery
                    ))
                {
                    continue;
                }
                dynamicResults.Add(new AtlasSearchResult(
                    entity.Pos.X,
                    entity.Pos.Y + 0.35,
                    entity.Pos.Z,
                    AtlasSearchResultKind.DroppedItem,
                    name
                ));
            }
        }
        catch (InvalidOperationException)
        {
            // The loaded-entity dictionary can change between client events.
            // A later throttled refresh will obtain a stable snapshot.
        }
    }

    private bool MatchesEntity(Entity entity)
    {
        return Matches(AtlasSafeDisplayName.ForEntity(entity))
            || Matches(entity.Code?.ToString())
            || Matches(entity.Properties?.Class)
            || languageIndex.Matches(
                entity.Code,
                AtlasSearchAliasKind.Entity,
                query,
                compactQuery
            );
    }

    private bool Matches(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        string normalized = AtlasSearchLanguageIndex.Normalize(candidate);
        return normalized.Contains(query, StringComparison.Ordinal)
            || compactQuery.Length > 0
                && AtlasSearchLanguageIndex.Compact(normalized)
                    .Contains(compactQuery, StringComparison.Ordinal);
    }

    internal bool ValidateBilingualSearchForAutomatedTest(out string diagnostic) =>
        languageIndex.ValidateForAutomatedTest(out diagnostic);

    private bool IsInsidePlayerRadius(double worldX, double worldZ)
    {
        return IsInsidePlayerRadius(worldX, worldZ, requestedViewDistance);
    }

    private bool IsInsidePlayerRadius(
        double worldX,
        double worldZ,
        int viewDistanceBlocks
    )
    {
        double dx = worldX - capi.World.Player.Entity.Pos.X;
        double dz = worldZ - capi.World.Player.Entity.Pos.Z;
        return dx * dx + dz * dz
            <= (double)viewDistanceBlocks * viewDistanceBlocks;
    }

    private static bool IsSurfaceSafe(
        double worldX,
        double worldY,
        double worldZ,
        bool surfaceSafety,
        AtlasSurfaceHeightTexture surfaceHeightTexture
    )
    {
        return !surfaceSafety
            || surfaceHeightTexture.TryGetSurfaceHeight(
                worldX,
                worldZ,
                out int surfaceHeight
            ) && worldY >= surfaceHeight - 3;
    }

    private string GetEntityLabel(Entity entity)
    {
        return AtlasSafeDisplayName.ForEntity(entity);
    }

    private static AtlasSearchResultKind ToSearchKind(AtlasEntityKind kind)
    {
        return kind switch
        {
            AtlasEntityKind.Player => AtlasSearchResultKind.Player,
            AtlasEntityKind.Animal => AtlasSearchResultKind.Animal,
            AtlasEntityKind.Mob => AtlasSearchResultKind.Mob,
            AtlasEntityKind.Npc => AtlasSearchResultKind.Npc,
            _ => AtlasSearchResultKind.Animal
        };
    }

    private void RestartSearch(SearchStage nextStage)
    {
        stage = nextStage;
        matchingBlockIds.Clear();
        occupiedBlockCells.Clear();
        fuzzyBlockIds.Clear();
        chunkColumns.Clear();
        verticalChunkOrder.Clear();
        blockResults.Clear();
        dynamicResults.Clear();
        blockRegistryIndex = 0;
        minimumChunkX = 0;
        maximumChunkX = 0;
        minimumChunkZ = 0;
        maximumChunkZ = 0;
        preparingChunkX = 0;
        preparingChunkZ = 0;
        chunkColumnIndex = 0;
        verticalChunkIndex = 0;
        probedChunkCoordinates = 0;
        currentChunk = null;
        currentBlockIndex = 0;
        requestedViewDistance = 0;
        blockResultsTruncated = false;
        inspectedChunks = 0;
        readyChunks = 0;
        paletteMatchingChunks = 0;
        scannedBlockPositions = 0;
        rawBlockMatches = 0;
        radiusRejectedMatches = 0;
        surfaceRejectedMatches = 0;
        lastDynamicRefreshMilliseconds = 0;
        languageIndex.BeginBlockQuery();
    }

    private static bool WithinBudget(long started)
    {
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds
            < WorkBudgetMilliseconds;
    }

    private static int FloorDivide(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private enum SearchStage
    {
        Idle,
        Debounce,
        ResolveLanguageAliases,
        ResolveBlockTypes,
        PrepareChunks,
        ScanChunks,
        Complete
    }

    private readonly record struct ChunkCandidate(int X, int Y, int Z);

    private readonly record struct ChunkColumnCandidate(
        int X,
        int Z,
        double HorizontalDistanceSquared
    );
}
