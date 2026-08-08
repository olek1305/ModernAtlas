using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ModernAtlas;

/// <summary>
/// A block-aware surface and hillshade overlay. Exact materials are sampled
/// only from chunks already loaded by the client; older map-only areas keep a
/// translucent relief fallback and never trigger chunk generation.
/// </summary>
public sealed class ModernAtlasReliefMapLayer : MapLayer
{
    private const int ChunkSize = GlobalConstants.ChunkSize;
    private const int TileGroupSize = MultiChunkMapComponent.ChunkLen;

    private readonly ICoreClientAPI clientApi;
    private readonly Queue<FastVec2i> pending = new();
    private readonly Dictionary<FastVec2i, MultiChunkMapComponent> components = new();
    private readonly HashSet<FastVec2i> requested = new();
    private readonly HashSet<FastVec2i> visible = new();

    public override string Title => Lang.Get("modernatlas:map-layer-title");
    public override string LayerGroupCode => "terrain";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public override bool RequireChunkLoaded => false;

    public ModernAtlasReliefMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
    {
        clientApi = (ICoreClientAPI)api;
        ZIndex = 2;
        clientApi.Event.BlockChanged += OnBlockChanged;
    }

    public override void OnViewChangedClient(List<FastVec2i> nowVisible, List<FastVec2i> nowHidden)
    {
        foreach (FastVec2i chunk in nowVisible)
        {
            if (chunk.X < 0 || chunk.Y < 0) continue;
            visible.Add(chunk.Copy());
            RequestChunk(chunk, false);
        }
        foreach (FastVec2i chunk in nowHidden)
        {
            visible.Remove(chunk);
        }
    }

    public override void OnTick(float dt)
    {
        // Block.GetColor() may consult the client texture atlas, so surface
        // snapshots are deliberately captured on the main thread. The small
        // budget keeps opening the map responsive.
        int budget = 2;
        HashSet<MultiChunkMapComponent> changed = new();
        while (budget-- > 0 && pending.Count > 0)
        {
            FastVec2i chunk = pending.Dequeue();
            IMapChunk? mapChunk = api.World.BlockAccessor.GetMapChunk(chunk.X, chunk.Y);
            if (mapChunk == null)
            {
                requested.Remove(chunk);
                continue;
            }

            int[] pixels = BuildSurfaceTile(chunk, mapChunk);
            int groupX = chunk.X / TileGroupSize;
            int groupZ = chunk.Y / TileGroupSize;
            FastVec2i groupKey = new(groupX, groupZ);
            FastVec2i baseChunk = new(groupX * TileGroupSize, groupZ * TileGroupSize);

            if (!components.TryGetValue(groupKey, out MultiChunkMapComponent? component))
            {
                component = new MultiChunkMapComponent(clientApi, baseChunk) { renderZ = 51 };
                components[groupKey] = component;
            }

            component.setChunk(
                chunk.X - baseChunk.X,
                chunk.Y - baseChunk.Y,
                pixels
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
        hoverText.AppendLine($"ModernAtlas — {height} m");
    }

    public override void Dispose()
    {
        clientApi.Event.BlockChanged -= OnBlockChanged;
        foreach (MultiChunkMapComponent component in components.Values)
        {
            if (component.Texture != null && !component.Texture.Disposed)
            {
                component.ActuallyDispose();
            }
        }
        components.Clear();
        pending.Clear();
        requested.Clear();
        visible.Clear();
        base.Dispose();
    }

    private int[] BuildSurfaceTile(FastVec2i chunk, IMapChunk center)
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

                double slope = Math.Clamp(
                    (hWest - hEast + hNorth - hSouth) / 12.0,
                    -1.0,
                    1.0
                );
                bool hardEdge = Math.Abs(height - hEast) >= 2 || Math.Abs(height - hSouth) >= 2;

                int worldX = chunk.X * ChunkSize + x;
                int worldZ = chunk.Y * ChunkSize + z;
                Block? surface = FindSurfaceBlock(worldX, height, worldZ, out int surfaceY);
                if (surface == null)
                {
                    // The precise block column is not client-loaded. Preserve
                    // vanilla imagery and add only a modest height cue.
                    int shade = slope >= 0 ? 232 : 28;
                    int alpha = 18 + (int)(Math.Abs(slope) * 70);
                    result[index] = PackRgba(shade, shade, shade, alpha);
                    continue;
                }

                int color = GetSafeBlockColor(surface, new BlockPos(worldX, surfaceY, worldZ));
                double light = 0.90 + slope * 0.22;
                if (hardEdge) light *= 0.76;

                int red = ClampByte((int)(ColorUtil.ColorR(color) * light));
                int green = ClampByte((int)(ColorUtil.ColorG(color) * light));
                int blue = ClampByte((int)(ColorUtil.ColorB(color) * light));
                result[index] = PackRgba(red, green, blue, 238);
            }
        }

        return result;
    }

    private Block? FindSurfaceBlock(int worldX, int rainHeight, int worldZ, out int surfaceY)
    {
        // A little headroom handles foliage and unusual map-height providers;
        // the downward scan finds the roof/ground when the reported cell is air.
        for (int y = rainHeight + 2; y >= Math.Max(0, rainHeight - 12); y--)
        {
            Block? block = api.World.BlockAccessor.GetBlockOrNull(
                worldX,
                y,
                worldZ,
                BlockLayersAccess.FluidOrSolid
            );
            if (block == null)
            {
                surfaceY = rainHeight;
                return null;
            }
            if (block.Id != 0)
            {
                surfaceY = y;
                return block;
            }
        }

        surfaceY = rainHeight;
        return null;
    }

    private int GetSafeBlockColor(Block block, BlockPos pos)
    {
        string path = block.Code?.Path ?? string.Empty;
        if (path.Length == 0 || path.Contains("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return ColorUtil.ToRgba(255, 126, 126, 120);
        }

        try
        {
            return block.GetColor(clientApi, pos);
        }
        catch (Exception exception)
        {
            clientApi.Logger.Debug(
                "[ModernAtlas] Using neutral stone color for {0}: {1}",
                block.Code,
                exception.Message
            );
            return ColorUtil.ToRgba(255, 126, 126, 120);
        }
    }

    private void OnBlockChanged(BlockPos pos, Block? oldBlock)
    {
        FastVec2i chunk = new(FloorDiv(pos.X, ChunkSize), FloorDiv(pos.Z, ChunkSize));
        if (visible.Contains(chunk)) RequestChunk(chunk, true);
    }

    private void RequestChunk(FastVec2i chunk, bool force)
    {
        FastVec2i key = chunk.Copy();
        if (force) requested.Remove(key);
        if (!requested.Add(key)) return;
        pending.Enqueue(key);
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

    private static int ClampByte(int value) => Math.Clamp(value, 0, 255);

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
