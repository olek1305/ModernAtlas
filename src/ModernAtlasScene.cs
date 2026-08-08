using System;
using System.Reflection;
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
    private const int MaxWallDepth = 32;
    private const int MaxBlocks = 30000;

    private readonly ICoreClientAPI capi;
    private readonly int[] heights = new int[Diameter * Diameter];
    private readonly int[] terrainHeights = new int[Diameter * Diameter];
    private readonly bool[] loaded = new bool[Diameter * Diameter];

    private MeshData? buildingMesh;
    private MultiTextureMeshRef? meshRef;
    private Block? fallbackBlock;
    private TextureAtlasPosition? fallbackTexture;
    private int nextColumn;
    private int blocksAdded;
    private int originX;
    private int originZ;
    private int baseY;
    private int topY;
    private int missingTexturesReplaced;

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
        Array.Clear(terrainHeights);
        Array.Clear(loaded);
        originX = centerX - Radius;
        originZ = centerZ - Radius;
        nextColumn = 0;
        blocksAdded = 0;
        missingTexturesReplaced = 0;
        LoadedColumns = 0;
        baseY = int.MaxValue;
        topY = int.MinValue;
        fallbackBlock = capi.World.GetBlock(new AssetLocation("game:rock-granite"));
        fallbackTexture = fallbackBlock == null
            ? null
            : capi.BlockTextureAtlas.GetPosition(fallbackBlock, "up", true)
                ?? capi.BlockTextureAtlas.GetPosition(fallbackBlock, "all", true);

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
            int terrainY = terrainHeights[index];
            int lowestVisibleY = Math.Max(terrainY, surfaceY - MaxWallDepth);

            for (int y = lowestVisibleY; y <= surfaceY && blocksAdded < MaxBlocks; y++)
            {
                Block? block = capi.World.BlockAccessor.GetBlockOrNull(
                    worldX,
                    y,
                    worldZ,
                    BlockLayersAccess.FluidOrSolid
                );
                if (block == null || block.Id == 0) continue;
                if (IsMultiblockPlaceholder(block)) continue;
                bool mustKeepGround = y == terrainY;
                bool mustKeepTop = y == surfaceY;
                bool mustKeepDoorRoot = IsDoorBlock(block);
                if (!mustKeepGround
                    && !mustKeepTop
                    && !mustKeepDoorRoot
                    && !IsExposedToAir(worldX, y, worldZ)) continue;

                AddBlockMesh(
                    block,
                    worldX,
                    y,
                    worldZ,
                    worldX - CenterX,
                    y - baseY,
                    worldZ - CenterZ
                );
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
                int terrainY = mapChunk.WorldGenTerrainHeightMap[mapIndex];
                if (terrainY <= 0 || terrainY > surfaceY) terrainY = surfaceY;

                int index = localZ * Diameter + localX;
                heights[index] = surfaceY;
                terrainHeights[index] = terrainY;
                loaded[index] = true;
                LoadedColumns++;
                baseY = Math.Min(baseY, terrainY);
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

    private bool IsExposedToAir(int x, int y, int z)
    {
        return IsAir(x - 1, y, z)
            || IsAir(x + 1, y, z)
            || IsAir(x, y, z - 1)
            || IsAir(x, y, z + 1)
            || IsAir(x, y + 1, z);
    }

    private bool IsAir(int x, int y, int z)
    {
        Block? neighbor = capi.World.BlockAccessor.GetBlockOrNull(
            x,
            y,
            z,
            BlockLayersAccess.FluidOrSolid
        );
        return neighbor != null && neighbor.Id == 0;
    }

    private static bool IsMultiblockPlaceholder(Block block)
    {
        string path = block.Code?.Path ?? string.Empty;
        return path.StartsWith("multiblock-monolithic-", StringComparison.OrdinalIgnoreCase);
    }

    private void AddBlockMesh(
        Block block,
        int worldX,
        int worldY,
        int worldZ,
        float x,
        float y,
        float z
    )
    {
        if (buildingMesh == null) return;

        if (TryAddBlockEntityMesh(block, worldX, worldY, worldZ, x, y, z))
        {
            blocksAdded++;
            return;
        }

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
            source = ReplaceUnknownTextures(source);

            int firstVertex = buildingMesh.VerticesCount;
            buildingMesh.AddMeshData(source, x, y, z);
            ApplyWorldColor(block, source, firstVertex, worldX, worldY, worldZ);
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
                fallback = ReplaceUnknownTextures(fallback);
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

    private bool TryAddBlockEntityMesh(
        Block block,
        int worldX,
        int worldY,
        int worldZ,
        float x,
        float y,
        float z
    )
    {
        if (buildingMesh == null) return false;

        BlockPos position = new(worldX, worldY, worldZ);
        BlockEntity? blockEntity = capi.World.BlockAccessor.GetBlockEntity(position);
        if (blockEntity == null || IsContentDisplayBlockEntity(blockEntity)) return false;

        try
        {
            AtlasMeshCollector collector = new();
            bool replacesDefaultMesh = blockEntity.OnTesselation(collector, capi.Tesselator);
            if (!replacesDefaultMesh) return false;

            MeshData captured = collector.Mesh.VerticesCount > 0
                ? collector.Mesh
                : FindPreparedDoorMesh(blockEntity) ?? collector.Mesh;
            if (captured.VerticesCount == 0) return false;
            captured = ReplaceUnknownTextures(captured);

            int firstVertex = buildingMesh.VerticesCount;
            buildingMesh.AddMeshData(captured, x, y, z);
            ApplyWorldColor(block, captured, firstVertex, worldX, worldY, worldZ);
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Debug(
                "[ModernAtlas] Could not capture block-entity mesh at {0}, {1}, {2}: {3}",
                worldX,
                worldY,
                worldZ,
                exception.Message
            );
            return false;
        }
    }

    private static bool IsContentDisplayBlockEntity(BlockEntity blockEntity)
    {
        string typeName = blockEntity.GetType().Name;
        return typeName.Contains("GroundStorage", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Display", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Toolrack", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("ToolRack", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Shelf", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("ArmorStand", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("ItemFrame", StringComparison.OrdinalIgnoreCase);
    }

    private static MeshData? FindPreparedDoorMesh(BlockEntity blockEntity)
    {
        foreach (BlockEntityBehavior behavior in blockEntity.Behaviors)
        {
            Type? type = behavior.GetType();
            if (!type.Name.Contains("Door", StringComparison.OrdinalIgnoreCase)) continue;

            while (type != null)
            {
                FieldInfo? field = type.GetField(
                    "mesh",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
                if (field?.GetValue(behavior) is MeshData mesh && mesh.VerticesCount > 0)
                {
                    return mesh.Clone();
                }
                type = type.BaseType;
            }
        }
        return null;
    }

    private static bool IsDoorBlock(Block block)
    {
        if (block.GetType().Name.Contains("Door", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (BlockBehavior behavior in block.BlockBehaviors)
        {
            if (behavior.GetType().Name.Contains("Door", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private MeshData ReplaceUnknownTextures(MeshData source)
    {
        TextureAtlasPosition unknown = capi.BlockTextureAtlas.UnknownTexturePosition;
        TextureAtlasPosition? replacement = fallbackTexture;
        if (replacement == null
            || source.TextureIndicesCount == 0
            || source.VerticesPerFace <= 0) return source;

        int faceCount = Math.Min(
            source.TextureIndicesCount,
            source.VerticesCount / source.VerticesPerFace
        );
        MeshData? repaired = null;

        for (int face = 0; face < faceCount; face++)
        {
            int firstVertex = face * source.VerticesPerFace;
            int lastVertex = firstVertex + source.VerticesPerFace;
            bool allUnknown = true;
            for (int vertex = firstVertex; vertex < lastVertex; vertex++)
            {
                int uvIndex = vertex * 2;
                if (!unknown.ContainsUV(source.Uv[uvIndex], source.Uv[uvIndex + 1]))
                {
                    allUnknown = false;
                    break;
                }
            }
            if (!allUnknown) continue;

            repaired ??= source.Clone();
            for (int vertex = firstVertex; vertex < lastVertex; vertex++)
            {
                int uvIndex = vertex * 2;
                repaired.Uv[uvIndex] = RemapTextureCoordinate(
                    source.Uv[uvIndex],
                    unknown.x1,
                    unknown.x2,
                    replacement.x1,
                    replacement.x2
                );
                repaired.Uv[uvIndex + 1] = RemapTextureCoordinate(
                    source.Uv[uvIndex + 1],
                    unknown.y1,
                    unknown.y2,
                    replacement.y1,
                    replacement.y2
                );
            }

            int textureIndex = repaired.TextureIndices[face];
            if (textureIndex < repaired.TextureIds.Length)
            {
                repaired.TextureIds[textureIndex] = replacement.atlasTextureId;
            }
            missingTexturesReplaced++;
        }

        return repaired ?? source;
    }

    private static float RemapTextureCoordinate(
        float value,
        float sourceStart,
        float sourceEnd,
        float targetStart,
        float targetEnd
    )
    {
        float sourceSize = sourceEnd - sourceStart;
        if (Math.Abs(sourceSize) < 0.000001f) return targetStart;
        float amount = (value - sourceStart) / sourceSize;
        return targetStart + amount * (targetEnd - targetStart);
    }

    private void ApplyWorldColor(
        Block block,
        MeshData source,
        int firstVertex,
        int worldX,
        int worldY,
        int worldZ
    )
    {
        if (buildingMesh == null || source.ColorMapIdsCount == 0) return;

        int tint = capi.World.ApplyColorMapOnRgba(
            block.ClimateColorMapResolved,
            block.SeasonColorMapResolved,
            ColorUtil.WhiteArgb,
            worldX,
            worldY,
            worldZ,
            false
        );
        byte tintR = ColorUtil.ColorR(tint);
        byte tintG = ColorUtil.ColorG(tint);
        byte tintB = ColorUtil.ColorB(tint);
        int verticesPerFace = Math.Max(1, source.VerticesPerFace);
        int faceCount = Math.Min(
            source.ColorMapIdsCount,
            source.VerticesCount / verticesPerFace
        );

        for (int face = 0; face < faceCount; face++)
        {
            bool usesColorMap = source.ClimateColorMapIds[face] != 0
                || source.SeasonColorMapIds[face] != 0;
            if (!usesColorMap) continue;

            int faceStart = firstVertex + face * verticesPerFace;
            int faceEnd = Math.Min(firstVertex + source.VerticesCount, faceStart + verticesPerFace);
            for (int vertex = faceStart; vertex < faceEnd; vertex++)
            {
                int colorIndex = vertex * 4;
                buildingMesh.Rgba[colorIndex] = MultiplyColor(buildingMesh.Rgba[colorIndex], tintR);
                buildingMesh.Rgba[colorIndex + 1] = MultiplyColor(buildingMesh.Rgba[colorIndex + 1], tintG);
                buildingMesh.Rgba[colorIndex + 2] = MultiplyColor(buildingMesh.Rgba[colorIndex + 2], tintB);
            }
        }
    }

    private static byte MultiplyColor(byte value, byte tint)
    {
        return (byte)((value * tint + 127) / 255);
    }

    private void FinishMesh()
    {
        MeshData completed = buildingMesh!;
        buildingMesh = null;
        if (completed.VerticesCount == 0) return;

        int nonTransparentVertices = 0;
        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float minZ = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        float maxZ = float.MinValue;
        for (int vertex = 0; vertex < completed.VerticesCount; vertex++)
        {
            int xyzIndex = vertex * 3;
            minX = Math.Min(minX, completed.xyz[xyzIndex]);
            minY = Math.Min(minY, completed.xyz[xyzIndex + 1]);
            minZ = Math.Min(minZ, completed.xyz[xyzIndex + 2]);
            maxX = Math.Max(maxX, completed.xyz[xyzIndex]);
            maxY = Math.Max(maxY, completed.xyz[xyzIndex + 1]);
            maxZ = Math.Max(maxZ, completed.xyz[xyzIndex + 2]);

            int colorIndex = vertex * 4;
            if (completed.Rgba[colorIndex + 3] != 0) nonTransparentVertices++;
        }

        // Atlas meshes do not use block-entity or particle-specific custom
        // vertex streams. Texture IDs, UVs, colors and packed normals remain.
        completed.CustomBytes = null;
        completed.CustomFloats = null;
        completed.CustomInts = null;
        completed.CustomShorts = null;
        meshRef = capi.Render.UploadMultiTextureMesh(completed);
        capi.Logger.Notification(
            "[ModernAtlas] 3D scene ready: {0} exterior blocks, {1} vertices ({2} raw opaque), bounds ({3:0.0}, {4:0.0}, {5:0.0}) to ({6:0.0}, {7:0.0}, {8:0.0}); {9} missing material faces replaced with stone.",
            blocksAdded,
            completed.VerticesCount,
            nonTransparentVertices,
            minX,
            minY,
            minZ,
            maxX,
            maxY,
            maxZ,
            missingTexturesReplaced
        );
    }

    private void DisposeMesh()
    {
        meshRef?.Dispose();
        meshRef = null;
    }

    private sealed class AtlasMeshCollector : ITerrainMeshPool
    {
        public MeshData Mesh { get; } = new(256, 384, false, true, true, true);

        public void AddMeshData(MeshData data, int lodLevel = 1)
        {
            if (data != null) Mesh.AddMeshData(data);
        }

        public void AddMeshData(MeshData data, float[] transformation, int lodLevel = 1)
        {
            if (data == null) return;
            MeshData transformed = data.Clone().MatrixTransform(transformation);
            Mesh.AddMeshData(transformed);
        }

        public void AddMeshData(MeshData data, ColorMapData colorMapData, int lodLevel = 1)
        {
            if (data != null) Mesh.AddMeshData(data);
        }
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
