using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VoxelAtlas;

/// <summary>
/// A lightweight hillshade overlay. It reads height maps already available to
/// the client, so it neither generates unexplored terrain nor writes save data.
/// </summary>
public sealed class ReliefMapLayer : MapLayer
{
    private const int ChunkSize = GlobalConstants.ChunkSize;
    private const int TileGroupSize = MultiChunkMapComponent.ChunkLen;

    private readonly ICoreClientAPI clientApi;
    private readonly ConcurrentQueue<FastVec2i> pending = new();
    private readonly ConcurrentQueue<ReadyTile> ready = new();
    private readonly Dictionary<FastVec2i, MultiChunkMapComponent> components = new();
    private readonly HashSet<FastVec2i> requested = new();
    private readonly object requestedLock = new();

    public override string Title => Lang.Get("voxelatlas:map-layer-title");
    public override string LayerGroupCode => "terrain";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public override bool RequireChunkLoaded => false;

    public ReliefMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
    {
        clientApi = (ICoreClientAPI)api;
        ZIndex = 2;
    }

    public override void OnViewChangedClient(List<FastVec2i> nowVisible, List<FastVec2i> nowHidden)
    {
        foreach (FastVec2i chunk in nowVisible)
        {
            if (chunk.X < 0 || chunk.Y < 0) continue;

            FastVec2i key = chunk.Copy();
            lock (requestedLock)
            {
                if (!requested.Add(key)) continue;
            }
            pending.Enqueue(key);
        }
    }

    public override void OnOffThreadTick(float dt)
    {
        int budget = 12;
        while (budget-- > 0 && pending.TryDequeue(out FastVec2i chunk))
        {
            IMapChunk? mapChunk = api.World.BlockAccessor.GetMapChunk(chunk.X, chunk.Y);
            if (mapChunk == null)
            {
                // No client-side data means this location remains exactly as
                // represented by the vanilla cache. Do not reveal/generate it.
                lock (requestedLock) requested.Remove(chunk);
                continue;
            }

            int[] pixels = BuildHillshade(chunk, mapChunk);
            ready.Enqueue(new ReadyTile(chunk, pixels));
        }
    }

    public override void OnTick(float dt)
    {
        int budget = 96;
        HashSet<MultiChunkMapComponent> changed = new();
        while (budget-- > 0 && ready.TryDequeue(out ReadyTile tile))
        {
            int groupX = tile.Chunk.X / TileGroupSize;
            int groupZ = tile.Chunk.Y / TileGroupSize;
            FastVec2i groupKey = new(groupX, groupZ);
            FastVec2i baseChunk = new(groupX * TileGroupSize, groupZ * TileGroupSize);

            if (!components.TryGetValue(groupKey, out MultiChunkMapComponent? component))
            {
                component = new MultiChunkMapComponent(clientApi, baseChunk) { renderZ = 51 };
                components[groupKey] = component;
            }

            component.setChunk(
                tile.Chunk.X - baseChunk.X,
                tile.Chunk.Y - baseChunk.Y,
                tile.Pixels
            );
            changed.Add(component);
        }

        foreach (MultiChunkMapComponent component in changed)
        {
            component.FinishSetChunks();
        }
    }

    public override void Render(GuiElementMap mapElement, float dt)
    {
        if (!Active) return;
        foreach (MultiChunkMapComponent component in components.Values)
        {
            if (component.Texture != null && !component.Texture.Disposed)
            {
                component.Render(mapElement, dt);
            }
        }
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElement, StringBuilder hoverText)
    {
        if (!Active) return;

        Vec2f viewPosition = new(
            (float)(args.X - mapElement.Bounds.renderX),
            (float)(args.Y - mapElement.Bounds.renderY)
        );
        Vec3d worldPosition = new();
        mapElement.TranslateViewPosToWorldPos(viewPosition, ref worldPosition);

        int blockX = (int)Math.Floor(worldPosition.X);
        int blockZ = (int)Math.Floor(worldPosition.Z);
        int chunkX = FloorDiv(blockX, ChunkSize);
        int chunkZ = FloorDiv(blockZ, ChunkSize);
        IMapChunk? mapChunk = api.World.BlockAccessor.GetMapChunk(chunkX, chunkZ);
        if (mapChunk == null) return;

        int localX = GameMath.Mod(blockX, ChunkSize);
        int localZ = GameMath.Mod(blockZ, ChunkSize);
        int height = mapChunk.RainHeightMap[localZ * ChunkSize + localX];
        hoverText.AppendLine($"VoxelAtlas — {height} m");
    }

    public override void Dispose()
    {
        foreach (MultiChunkMapComponent component in components.Values)
        {
            if (component.Texture != null && !component.Texture.Disposed)
            {
                component.ActuallyDispose();
            }
        }
        components.Clear();
        base.Dispose();
    }

    private int[] BuildHillshade(FastVec2i chunk, IMapChunk center)
    {
        IMapChunk? west = api.World.BlockAccessor.GetMapChunk(chunk.X - 1, chunk.Y);
        IMapChunk? east = api.World.BlockAccessor.GetMapChunk(chunk.X + 1, chunk.Y);
        IMapChunk? north = api.World.BlockAccessor.GetMapChunk(chunk.X, chunk.Y - 1);
        IMapChunk? south = api.World.BlockAccessor.GetMapChunk(chunk.X, chunk.Y + 1);
        int[] result = new int[ChunkSize * ChunkSize];

        for (int z = 0; z < ChunkSize; z++)
        {
            for (int x = 0; x < ChunkSize; x++)
            {
                int index = z * ChunkSize + x;
                int height = center.RainHeightMap[index];
                int hWest = HeightAt(center, west, x - 1, z, ChunkSize - 1, z);
                int hEast = HeightAt(center, east, x + 1, z, 0, z);
                int hNorth = HeightAt(center, north, x, z - 1, x, ChunkSize - 1);
                int hSouth = HeightAt(center, south, x, z + 1, x, 0);

                // Light comes from north-west. Positive values brighten slopes
                // facing the light and negative values darken the opposite side.
                double slope = (hWest - hEast + hNorth - hSouth) / 10.0;
                slope = Math.Clamp(slope, -1.0, 1.0);

                bool contour = height % 12 == 0 &&
                    (hWest != height || hEast != height || hNorth != height || hSouth != height);

                int alpha = contour ? 62 : 12 + (int)(Math.Abs(slope) * 94);
                int channel = slope >= 0 ? 244 : 18;
                result[index] = PackRgba(channel, channel, channel, alpha);
            }
        }

        return result;
    }

    private static int HeightAt(
        IMapChunk center,
        IMapChunk? neighbor,
        int localX,
        int localZ,
        int neighborX,
        int neighborZ
    )
    {
        if (localX >= 0 && localX < ChunkSize && localZ >= 0 && localZ < ChunkSize)
        {
            return center.RainHeightMap[localZ * ChunkSize + localX];
        }

        return neighbor?.RainHeightMap[neighborZ * ChunkSize + neighborX]
            ?? center.RainHeightMap[GameMath.Clamp(localZ, 0, ChunkSize - 1) * ChunkSize
                + GameMath.Clamp(localX, 0, ChunkSize - 1)];
    }

    private static int PackRgba(int red, int green, int blue, int alpha)
        => red | (green << 8) | (blue << 16) | (alpha << 24);

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private readonly record struct ReadyTile(FastVec2i Chunk, int[] Pixels);
}
