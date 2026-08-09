using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Stores a low-detail, ModernAtlas-owned surface for previously visited map
/// chunks. Exact block geometry continues to come only from chunks loaded by
/// the client; this cache is a compact colored relief behind those meshes.
/// </summary>
internal sealed class AtlasSurfaceCache : IDisposable
{
    private const int FormatVersion = 2;
    private const int SampleStep = 8;
    private const int SamplesPerSide = GlobalConstants.ChunkSize / SampleStep + 1;
    private const int SamplesPerTile = SamplesPerSide * SamplesPerSide;
    private const int MaximumRenderedTiles = 2048;
    private const int NeutralStoneColor = unchecked((int)0xff777873);
    private const long MeshRebuildIntervalMilliseconds = 5000;
    private static readonly byte[] Magic = { (byte)'M', (byte)'A', (byte)'T', (byte)'C' };

    private readonly ICoreClientAPI capi;
    private readonly Func<IShaderProgram?> shaderProvider;
    private readonly Dictionary<long, CacheTile> tiles = new();
    private readonly Queue<(int X, int Z)> captureQueue = new();
    private readonly HashSet<long> queuedCaptures = new();
    private readonly HashSet<long> refreshedThisSession = new();
    private readonly ConcurrentQueue<(int Generation, CacheTile Tile, bool Persist)> backgroundTiles = new();
    private readonly ConcurrentQueue<(int Generation, string Message)> backgroundMessages = new();
    private string? cacheDirectory;
    private MeshRef? visibleMesh;
    private int revision;
    private int meshRevision = -1;
    private int meshCenterX = int.MinValue;
    private int meshCenterZ = int.MinValue;
    private long lastMeshBuildMilliseconds;
    private int lastQueuedPlayerChunkX = int.MinValue;
    private int lastQueuedPlayerChunkZ = int.MinValue;
    private long lastNearbyQueueMilliseconds;
    private int operationGeneration;
    private bool initialized;
    private bool renderDisabled;
    private string status = "Cache waiting for world";
    private string relightStatus = "";

    public AtlasSurfaceCache(ICoreClientAPI capi, Func<IShaderProgram?> shaderProvider)
    {
        this.capi = capi;
        this.shaderProvider = shaderProvider;
    }

    public string Status => string.IsNullOrEmpty(relightStatus)
        ? status
        : $"{status}; {relightStatus}";

    public void SetRelightProgress(int completed, int total, bool finished)
    {
        relightStatus = finished
            ? $"Lighting repaired: {completed}/{total} loaded chunks"
            : $"Repairing lighting: {completed}/{total} loaded chunks";
    }

    public void InitializeWorld()
    {
        ResetWorldState();
        string worldId = SanitizePathPart(capi.World.SavegameIdentifier);
        cacheDirectory = Path.Combine(capi.DataBasePath, "ModernAtlas", "Cache", worldId);
        Directory.CreateDirectory(cacheDirectory);
        initialized = true;
        status = "Cache: 0 tiles";
        QueueNearbyLoadedChunks(force: true);
        BeginLoadOwnedCache(cacheDirectory, operationGeneration);
    }

    public void LeaveWorld()
    {
        ResetWorldState();
    }

    public void Tick(float deltaTime)
    {
        if (!initialized) return;

        IntegrateBackgroundTiles(512);
        while (backgroundMessages.TryDequeue(out var result))
        {
            if (result.Generation != operationGeneration) continue;
            status = result.Message;
            capi.Logger.Notification("[ModernAtlas] {0}", result.Message);
        }

        QueueNearbyLoadedChunks(force: false);
        if (capi.ElapsedMilliseconds - lastNearbyQueueMilliseconds >= 5000)
        {
            QueueNearbyLoadedChunks(force: true);
        }
        CaptureOneLoadedChunk();
    }

    public void MarkDirty(BlockPos position)
    {
        if (!initialized) return;
        int chunkSize = GlobalConstants.ChunkSize;
        EnqueueCapture(FloorDiv(position.X, chunkSize), FloorDiv(position.Z, chunkSize), true);
    }

    public bool Clear()
    {
        if (!initialized || cacheDirectory == null) return true;

        foreach (string file in Directory.EnumerateFiles(cacheDirectory, "*.matc"))
        {
            File.Delete(file);
        }
        tiles.Clear();
        Interlocked.Increment(ref operationGeneration);
        while (backgroundTiles.TryDequeue(out _)) { }
        captureQueue.Clear();
        queuedCaptures.Clear();
        revision++;
        DisposeMesh();
        status = "Cache cleared; nearby chunks will refresh";
        relightStatus = "";
        QueueNearbyLoadedChunks(force: true);
        capi.Logger.Notification("[ModernAtlas] Cleared only the ModernAtlas cache for this world.");
        return true;
    }

    public void Render(
        float[] projection,
        double[] absoluteView,
        double centerX,
        double centerZ,
        int viewDistanceBlocks
    )
    {
        if (!initialized || renderDisabled) return;
        try
        {
            RenderInternal(projection, absoluteView, centerX, centerZ, viewDistanceBlocks);
        }
        catch (Exception exception)
        {
            renderDisabled = true;
            capi.Render.CurrentActiveShader?.Stop();
            capi.Logger.Error(
                "[ModernAtlas] Cached surface rendering failed and was disabled for this session: {0}",
                exception
            );
        }
    }

    private void RenderInternal(
        float[] projection,
        double[] absoluteView,
        double centerX,
        double centerZ,
        int viewDistanceBlocks
    )
    {
        IntegrateBackgroundTiles(512);
        RebuildVisibleMeshIfNeeded(centerX, centerZ, viewDistanceBlocks);
        if (visibleMesh == null) return;

        IShaderProgram? shader = shaderProvider();
        if (shader == null || shader.Disposed) return;

        IRenderAPI render = capi.Render;
        render.CurrentActiveShader?.Stop();
        render.GLEnableDepthTest();
        render.GLDepthMask(true);
        render.GlToggleBlend(false, EnumBlendMode.Standard);
        shader.Use();
        shader.UniformMatrix("projectionMatrix", projection);
        shader.UniformMatrix(
            "modelViewMatrix",
            Array.ConvertAll(absoluteView, value => (float)value)
        );
        render.RenderMesh(visibleMesh);
        shader.Stop();
    }

    public void Dispose()
    {
        ResetWorldState();
    }

    private void QueueNearbyLoadedChunks(bool force)
    {
        EntityPlayer? player = capi.World.Player?.Entity;
        if (player == null) return;
        int chunkSize = GlobalConstants.ChunkSize;
        int playerChunkX = FloorDiv((int)Math.Floor(player.Pos.X), chunkSize);
        int playerChunkZ = FloorDiv((int)Math.Floor(player.Pos.Z), chunkSize);
        if (!force
            && playerChunkX == lastQueuedPlayerChunkX
            && playerChunkZ == lastQueuedPlayerChunkZ) return;

        lastQueuedPlayerChunkX = playerChunkX;
        lastQueuedPlayerChunkZ = playerChunkZ;
        lastNearbyQueueMilliseconds = capi.ElapsedMilliseconds;
        int radius = Math.Clamp(capi.Settings.Int["viewDistance"] / chunkSize + 1, 2, 64);
        for (int ring = 0; ring <= radius; ring++)
        {
            for (int z = -ring; z <= ring; z++)
            {
                for (int x = -ring; x <= ring; x++)
                {
                    if (Math.Max(Math.Abs(x), Math.Abs(z)) != ring) continue;
                    EnqueueCapture(playerChunkX + x, playerChunkZ + z, false);
                }
            }
        }
    }

    private void EnqueueCapture(int chunkX, int chunkZ, bool prioritize)
    {
        long key = ChunkKey(chunkX, chunkZ);
        if (!prioritize && refreshedThisSession.Contains(key)) return;
        if (!queuedCaptures.Add(key)) return;
        if (!prioritize)
        {
            captureQueue.Enqueue((chunkX, chunkZ));
            return;
        }

        Queue<(int X, int Z)> replacement = new();
        replacement.Enqueue((chunkX, chunkZ));
        while (captureQueue.Count > 0) replacement.Enqueue(captureQueue.Dequeue());
        while (replacement.Count > 0) captureQueue.Enqueue(replacement.Dequeue());
    }

    private void CaptureOneLoadedChunk()
    {
        int attempts = Math.Min(24, captureQueue.Count);
        while (attempts-- > 0 && captureQueue.Count > 0)
        {
            (int chunkX, int chunkZ) = captureQueue.Dequeue();
            queuedCaptures.Remove(ChunkKey(chunkX, chunkZ));
            IMapChunk? mapChunk = capi.World.BlockAccessor.GetMapChunk(chunkX, chunkZ);
            if (mapChunk == null) continue;

            CacheTile tile = CaptureTile(chunkX, chunkZ, mapChunk);
            StoreTile(tile, persist: true);
            refreshedThisSession.Add(ChunkKey(chunkX, chunkZ));
            status = $"Cache: {tiles.Count} tiles (nearby terrain refreshed)";
            return;
        }
    }

    private CacheTile CaptureTile(int chunkX, int chunkZ, IMapChunk mapChunk)
    {
        int size = GlobalConstants.ChunkSize;
        ushort[] heights = new ushort[SamplesPerTile];
        int[] colors = new int[SamplesPerTile];
        ushort[] sourceHeights = mapChunk.RainHeightMap;
        BlockPos position = new(0);

        for (int pointZ = 0; pointZ < SamplesPerSide; pointZ++)
        {
            for (int pointX = 0; pointX < SamplesPerSide; pointX++)
            {
                int localX = Math.Min(size - 1, pointX * SampleStep);
                int localZ = Math.Min(size - 1, pointZ * SampleStep);
                int sourceIndex = localZ * size + localX;
                int sampleIndex = pointZ * SamplesPerSide + pointX;
                int worldX = chunkX * size + localX;
                int worldZ = chunkZ * size + localZ;
                int height = sourceIndex < sourceHeights.Length
                    ? sourceHeights[sourceIndex]
                    : capi.World.SeaLevel;
                height = Math.Clamp(height, 0, ushort.MaxValue);
                position.Set(worldX, height, worldZ);
                Block block = capi.World.BlockAccessor.GetBlock(position);
                while (height > 0 && block.Id == 0)
                {
                    height--;
                    position.Y = height;
                    block = capi.World.BlockAccessor.GetBlock(position);
                }

                heights[sampleIndex] = (ushort)height;
                string blockPath = block.Code?.Path ?? "";
                bool unresolved = block.Code == null
                    || blockPath.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                    || blockPath.Contains("missing", StringComparison.OrdinalIgnoreCase);
                int color = unresolved ? NeutralStoneColor : block.GetColor(capi, position);
                colors[sampleIndex] = HasVisibleRgb(color) ? color : NeutralStoneColor;
            }
        }
        return new CacheTile(chunkX, chunkZ, true, heights, colors);
    }

    private void StoreTile(CacheTile tile, bool persist)
    {
        tiles[ChunkKey(tile.X, tile.Z)] = tile;
        revision++;
        if (!persist || cacheDirectory == null) return;

        WriteTileFile(TilePath(cacheDirectory, tile.X, tile.Z), tile);
    }

    private static void WriteTileFile(string path, CacheTile tile)
    {
        string temporaryPath = path + ".tmp";
        using (FileStream stream = File.Create(temporaryPath))
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(tile.X);
            writer.Write(tile.Z);
            writer.Write(tile.HasRelief);
            writer.Write(tile.Heights.Length);
            foreach (ushort height in tile.Heights) writer.Write(height);
            writer.Write(tile.Colors.Length);
            foreach (int color in tile.Colors) writer.Write(color);
        }
        File.Move(temporaryPath, path, true);
    }

    private void BeginLoadOwnedCache(string directory, int generation)
    {
        _ = Task.Run(() =>
        {
            int loaded = 0;
            try
            {
                foreach (string path in Directory.EnumerateFiles(directory, "*.matc"))
                {
                    if (generation != Volatile.Read(ref operationGeneration)) return;
                    CacheTile? tile = ReadTile(path, out bool needsRewrite);
                    if (tile == null) continue;
                    if (!tile.HasRelief)
                    {
                        // Development builds briefly persisted a flat fallback
                        // as thousands of individual files. It has no relief,
                        // so remove this obsolete ModernAtlas-owned format.
                        File.Delete(path);
                        continue;
                    }
                    if (needsRewrite) WriteTileFile(path, tile);
                    backgroundTiles.Enqueue((generation, tile, false));
                    loaded++;
                }
                if (generation == Volatile.Read(ref operationGeneration))
                {
                    backgroundMessages.Enqueue((generation, $"Cache loaded: {loaded} tiles"));
                }
            }
            catch (Exception exception)
            {
                backgroundMessages.Enqueue((generation, $"Cache load failed safely: {exception.Message}"));
            }
        });
    }

    private void IntegrateBackgroundTiles(int budget)
    {
        while (budget-- > 0 && backgroundTiles.TryDequeue(out var result))
        {
            if (result.Generation != operationGeneration) continue;
            CacheTile tile = result.Tile;
            long key = ChunkKey(tile.X, tile.Z);
            if (refreshedThisSession.Contains(key)) continue;
            if (tiles.TryGetValue(key, out CacheTile? current) && current.HasRelief && !tile.HasRelief)
            {
                continue;
            }
            StoreTile(tile, persist: result.Persist);
        }
    }

    private void RebuildVisibleMeshIfNeeded(double centerX, double centerZ, int radiusBlocks)
    {
        int chunkSize = GlobalConstants.ChunkSize;
        int centerChunkX = FloorDiv((int)Math.Floor(centerX), chunkSize);
        int centerChunkZ = FloorDiv((int)Math.Floor(centerZ), chunkSize);
        bool centerChanged = centerChunkX != meshCenterX || centerChunkZ != meshCenterZ;
        if (!centerChanged && meshRevision == revision) return;
        long now = capi.ElapsedMilliseconds;
        // Preserve the last complete mesh while disk loading feeds batches.
        // Rebuilding for every batch caused unnecessary load.
        if (visibleMesh != null && !backgroundTiles.IsEmpty) return;
        if (visibleMesh != null && now - lastMeshBuildMilliseconds < MeshRebuildIntervalMilliseconds) return;

        int radiusChunks = radiusBlocks / chunkSize + 2;
        int radiusSquared = radiusChunks * radiusChunks;
        List<CacheTile> visible = tiles.Values
            .Where(tile =>
            {
                // Exact loaded geometry owns this area. Drawing the coarse
                // relief beneath it can cross the live surface on slopes and
                // produce large triangular dark wedges.
                if (capi.World.BlockAccessor.GetMapChunk(tile.X, tile.Z) != null)
                {
                    return false;
                }
                int dx = tile.X - centerChunkX;
                int dz = tile.Z - centerChunkZ;
                return dx * dx + dz * dz <= radiusSquared;
            })
            .OrderBy(tile =>
            {
                int dx = tile.X - centerChunkX;
                int dz = tile.Z - centerChunkZ;
                return dx * dx + dz * dz;
            })
            .Take(MaximumRenderedTiles)
            .ToList();

        DisposeMesh();
        if (visible.Count > 0)
        {
            int pointsPerSide = chunkSize / SampleStep + 1;
            int vertices = visible.Count * pointsPerSide * pointsPerSide;
            int indices = visible.Count * (pointsPerSide - 1) * (pointsPerSide - 1) * 6;
            MeshData mesh = new(vertices, indices, false, false, true, false);
            foreach (CacheTile tile in visible) AddTileMesh(mesh, tile, pointsPerSide);
            visibleMesh = capi.Render.UploadMesh(mesh);
        }

        meshRevision = revision;
        meshCenterX = centerChunkX;
        meshCenterZ = centerChunkZ;
        lastMeshBuildMilliseconds = now;
    }

    private static void AddTileMesh(MeshData mesh, CacheTile tile, int pointsPerSide)
    {
        int chunkSize = GlobalConstants.ChunkSize;
        int firstVertex = mesh.VerticesCount;
        for (int pointZ = 0; pointZ < pointsPerSide; pointZ++)
        {
            for (int pointX = 0; pointX < pointsPerSide; pointX++)
            {
                int sample = pointZ * SamplesPerSide + pointX;
                float y = tile.Heights[sample] + 0.12f;
                mesh.AddVertexSkipTex(
                    tile.X * chunkSize + Math.Min(chunkSize, pointX * SampleStep),
                    y,
                    tile.Z * chunkSize + Math.Min(chunkSize, pointZ * SampleStep),
                    NormalizeColor(tile.Colors[sample])
                );
            }
        }

        for (int z = 0; z < pointsPerSide - 1; z++)
        {
            for (int x = 0; x < pointsPerSide - 1; x++)
            {
                int northWest = firstVertex + z * pointsPerSide + x;
                int northEast = northWest + 1;
                int southWest = northWest + pointsPerSide;
                int southEast = southWest + 1;
                mesh.AddIndex(northWest);
                mesh.AddIndex(southWest);
                mesh.AddIndex(northEast);
                mesh.AddIndex(northEast);
                mesh.AddIndex(southWest);
                mesh.AddIndex(southEast);
            }
        }
    }

    private static CacheTile? ReadTile(string path, out bool needsRewrite)
    {
        needsRewrite = false;
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic)) return null;
        int version = reader.ReadInt32();
        if (version != 1 && version != FormatVersion) return null;
        int x = reader.ReadInt32();
        int z = reader.ReadInt32();
        bool hasRelief = reader.ReadBoolean();
        int heightCount = reader.ReadInt32();
        int expectedCount = version == 1
            ? GlobalConstants.ChunkSize * GlobalConstants.ChunkSize
            : SamplesPerTile;
        if (heightCount != expectedCount) return null;
        ushort[] heights = new ushort[heightCount];
        for (int index = 0; index < heights.Length; index++) heights[index] = reader.ReadUInt16();
        int colorCount = reader.ReadInt32();
        if (colorCount != heightCount) return null;
        int[] colors = new int[colorCount];
        for (int index = 0; index < colors.Length; index++) colors[index] = reader.ReadInt32();

        if (version == 1 && hasRelief)
        {
            ushort[] compactHeights = new ushort[SamplesPerTile];
            int[] compactColors = new int[SamplesPerTile];
            int chunkSize = GlobalConstants.ChunkSize;
            for (int pointZ = 0; pointZ < SamplesPerSide; pointZ++)
            {
                for (int pointX = 0; pointX < SamplesPerSide; pointX++)
                {
                    int localX = Math.Min(chunkSize - 1, pointX * SampleStep);
                    int localZ = Math.Min(chunkSize - 1, pointZ * SampleStep);
                    int oldIndex = localZ * chunkSize + localX;
                    int newIndex = pointZ * SamplesPerSide + pointX;
                    compactHeights[newIndex] = heights[oldIndex];
                    compactColors[newIndex] = colors[oldIndex];
                }
            }
            heights = compactHeights;
            colors = compactColors;
            needsRewrite = true;
        }
        return new CacheTile(x, z, hasRelief, heights, colors);
    }

    private void ResetWorldState()
    {
        initialized = false;
        Interlocked.Increment(ref operationGeneration);
        renderDisabled = false;
        cacheDirectory = null;
        tiles.Clear();
        captureQueue.Clear();
        queuedCaptures.Clear();
        refreshedThisSession.Clear();
        while (backgroundTiles.TryDequeue(out _)) { }
        while (backgroundMessages.TryDequeue(out _)) { }
        revision++;
        meshRevision = -1;
        lastQueuedPlayerChunkX = int.MinValue;
        lastQueuedPlayerChunkZ = int.MinValue;
        lastNearbyQueueMilliseconds = 0;
        DisposeMesh();
        status = "Cache waiting for world";
    }

    private void DisposeMesh()
    {
        visibleMesh?.Dispose();
        visibleMesh = null;
    }

    private static string TilePath(string directory, int x, int z) =>
        Path.Combine(directory, $"{x}_{z}.matc");

    private static long ChunkKey(int x, int z) => ((long)x << 32) ^ (uint)z;

    private static bool HasVisibleRgb(int color) => (color & 0x00ffffff) != 0;

    private static int NormalizeColor(int color) =>
        (HasVisibleRgb(color) ? color : NeutralStoneColor) | unchecked((int)0xff000000);

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : (value - divisor + 1) / divisor;

    private static string SanitizePathPart(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "unknown-world" : value;
    }

    private sealed record CacheTile(
        int X,
        int Z,
        bool HasRelief,
        ushort[] Heights,
        int[] Colors
    );
}
