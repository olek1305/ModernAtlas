using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Incrementally captures and meshes the exterior of client-loaded block
/// columns. The scene deliberately never queries entities or transient render
/// systems.
/// </summary>
public sealed class ModernAtlasScene : IDisposable
{
    private const int Radius = 24;
    private const int Diameter = Radius * 2;
    private const int MaxWallDepth = 20;
    private const int MaxBlocks = 14000;

    private readonly ICoreClientAPI capi;
    private readonly int[] heights = new int[Diameter * Diameter];
    private readonly bool[] loaded = new bool[Diameter * Diameter];

    private MeshData? buildingMesh;
    private MultiTextureMeshRef? meshRef;
    private Block? fallbackBlock;
    private int nextColumn;
    private int blocksAdded;
    private int originX;
    private int originZ;
    private int baseY;
    private int topY;

    public bool IsBuilding => buildingMesh != null;
    public bool IsReady => meshRef != null;
    public float Progress => buildingMesh == null ? (meshRef == null ? 0 : 1) : nextColumn / (float)heights.Length;
    public int BlocksAdded => blocksAdded;
    public int LoadedColumns { get; private set; }
    public int CenterX => originX + Radius;
    public int CenterZ => originZ + Radius;
    public float VerticalCenter => (topY - baseY) * 0.38f;
    public MultiTextureMeshRef? MeshRef => meshRef;

    public ModernAtlasScene(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void Begin(int centerX, int centerZ)
    {
        DisposeMesh();
        Array.Clear(heights);
        Array.Clear(loaded);
        originX = centerX - Radius;
        originZ = centerZ - Radius;
        nextColumn = 0;
        blocksAdded = 0;
        LoadedColumns = 0;
        baseY = int.MaxValue;
        topY = int.MinValue;
        fallbackBlock = capi.World.GetBlock(new AssetLocation("game:rock-granite"));

        CaptureColumnHeights();
        if (baseY == int.MaxValue)
        {
            baseY = Math.Max(0, (int)capi.World.Player.Entity.Pos.Y - 8);
            topY = baseY + 16;
        }
        else
        {
            baseY = Math.Max(0, baseY - 2);
        }

        buildingMesh = new MeshData(8192, 12288, false, true, true, true);
    }

    public void BuildStep(int columnBudget)
    {
        if (buildingMesh == null) return;

        while (columnBudget-- > 0 && nextColumn < heights.Length && blocksAdded < MaxBlocks)
        {
            int index = nextColumn++;
            if (!loaded[index]) continue;

            int localX = index % Diameter;
            int localZ = index / Diameter;
            int worldX = originX + localX;
            int worldZ = originZ + localZ;
            int surfaceY = heights[index];
            int neighborFloor = LowestNeighborHeight(localX, localZ, surfaceY);
            int lowestVisibleY = Math.Min(
                surfaceY,
                Math.Max(baseY, Math.Max(surfaceY - MaxWallDepth, neighborFloor + 1))
            );

            for (int y = lowestVisibleY; y <= surfaceY && blocksAdded < MaxBlocks; y++)
            {
                Block? block = capi.World.BlockAccessor.GetBlockOrNull(
                    worldX,
                    y,
                    worldZ,
                    BlockLayersAccess.FluidOrSolid
                );
                if (block == null || block.Id == 0) continue;

                AddBlockMesh(block, worldX - CenterX, y - baseY, worldZ - CenterZ);
            }
        }

        if (nextColumn >= heights.Length || blocksAdded >= MaxBlocks)
        {
            FinishMesh();
        }
    }

    public void Dispose()
    {
        DisposeMesh();
        buildingMesh = null;
    }

    private void CaptureColumnHeights()
    {
        int chunkSize = GlobalConstants.ChunkSize;
        for (int localZ = 0; localZ < Diameter; localZ++)
        {
            for (int localX = 0; localX < Diameter; localX++)
            {
                int worldX = originX + localX;
                int worldZ = originZ + localZ;
                int chunkX = FloorDiv(worldX, chunkSize);
                int chunkZ = FloorDiv(worldZ, chunkSize);
                IMapChunk? mapChunk = capi.World.BlockAccessor.GetMapChunk(chunkX, chunkZ);
                if (mapChunk == null) continue;

                int mapIndex = GameMath.Mod(worldZ, chunkSize) * chunkSize
                    + GameMath.Mod(worldX, chunkSize);
                int rainHeight = mapChunk.RainHeightMap[mapIndex];
                if (!TryFindSurface(worldX, rainHeight, worldZ, out int surfaceY)) continue;

                int index = localZ * Diameter + localX;
                heights[index] = surfaceY;
                loaded[index] = true;
                LoadedColumns++;
                baseY = Math.Min(baseY, surfaceY);
                topY = Math.Max(topY, surfaceY);
            }
        }
    }

    private bool TryFindSurface(int worldX, int rainHeight, int worldZ, out int surfaceY)
    {
        for (int y = rainHeight + 2; y >= Math.Max(0, rainHeight - 14); y--)
        {
            Block? block = capi.World.BlockAccessor.GetBlockOrNull(
                worldX,
                y,
                worldZ,
                BlockLayersAccess.FluidOrSolid
            );
            if (block == null)
            {
                surfaceY = rainHeight;
                return false;
            }
            if (block.Id != 0)
            {
                surfaceY = y;
                return true;
            }
        }

        surfaceY = rainHeight;
        return false;
    }

    private int LowestNeighborHeight(int x, int z, int ownHeight)
    {
        int result = ownHeight;
        result = Math.Min(result, HeightOrOwn(x - 1, z, ownHeight));
        result = Math.Min(result, HeightOrOwn(x + 1, z, ownHeight));
        result = Math.Min(result, HeightOrOwn(x, z - 1, ownHeight));
        result = Math.Min(result, HeightOrOwn(x, z + 1, ownHeight));
        return result;
    }

    private int HeightOrOwn(int x, int z, int ownHeight)
    {
        if (x < 0 || z < 0 || x >= Diameter || z >= Diameter) return ownHeight;
        int index = z * Diameter + x;
        return loaded[index] ? heights[index] : ownHeight;
    }

    private void AddBlockMesh(Block block, float x, float y, float z)
    {
        if (buildingMesh == null) return;

        string path = block.Code?.Path ?? string.Empty;
        Block meshBlock = path.Length == 0
            || path.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                ? fallbackBlock ?? block
                : block;

        try
        {
            MeshData source = capi.TesselatorManager.GetDefaultBlockMesh(meshBlock);
            if (source.VerticesCount == 0 && fallbackBlock != null && meshBlock != fallbackBlock)
            {
                source = capi.TesselatorManager.GetDefaultBlockMesh(fallbackBlock);
            }
            if (source.VerticesCount == 0) return;

            buildingMesh.AddMeshData(source, x, y, z);
            blocksAdded++;
        }
        catch (Exception exception)
        {
            capi.Logger.Debug(
                "[ModernAtlas] Could not mesh block {0}; using stone fallback: {1}",
                block.Code,
                exception.Message
            );
            if (fallbackBlock == null || block == fallbackBlock) return;

            try
            {
                MeshData fallback = capi.TesselatorManager.GetDefaultBlockMesh(fallbackBlock);
                buildingMesh.AddMeshData(fallback, x, y, z);
                blocksAdded++;
            }
            catch (Exception fallbackException)
            {
                capi.Logger.Debug(
                    "[ModernAtlas] Stone fallback mesh also failed: {0}",
                    fallbackException.Message
                );
            }
        }
    }

    private void FinishMesh()
    {
        MeshData completed = buildingMesh!;
        buildingMesh = null;
        if (completed.VerticesCount == 0) return;

        // Atlas meshes do not use block-entity or particle-specific custom
        // vertex streams. Texture IDs, UVs, colors and packed normals remain.
        completed.CustomBytes = null;
        completed.CustomFloats = null;
        completed.CustomInts = null;
        completed.CustomShorts = null;
        meshRef = capi.Render.UploadMultiTextureMesh(completed);
        capi.Logger.Notification(
            "[ModernAtlas] 3D scene ready: {0} exterior blocks, {1} vertices.",
            blocksAdded,
            completed.VerticesCount
        );
    }

    private void DisposeMesh()
    {
        meshRef?.Dispose();
        meshRef = null;
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
