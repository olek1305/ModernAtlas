using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Draws a coarse, neutral surface underneath the game's exact chunk meshes.
/// It seals caves and unfinished chunk borders without reading or requesting
/// any world data that is not already present on the client.
/// </summary>
internal sealed class SurfaceShellRenderer : IDisposable
{
    private const int SampleStep = 8;
    private const float ShellDepth = 4f;
    private const float BoundarySkirtDepth = 24f;
    private const int RefreshMilliseconds = 1000;
    private static readonly AssetLocation StoneTexture =
        new("game", "block/stone/rock/granite1");

    private readonly ICoreClientAPI capi;
    private readonly Func<IShaderProgram?> shaderProvider;
    private MeshRef? mesh;
    private int textureId;
    private long lastRefreshMilliseconds = long.MinValue;
    private int lastLoadedMapChunkCount = -1;
    private int lastCenterChunkX = int.MinValue;
    private int lastCenterChunkZ = int.MinValue;
    private int lastViewDistance = -1;
    private bool loggedReady;

    public SurfaceShellRenderer(
        ICoreClientAPI capi,
        Func<IShaderProgram?> shaderProvider
    )
    {
        this.capi = capi;
        this.shaderProvider = shaderProvider;
    }

    public void Render(
        float[] projection,
        double[] view,
        Vec3d referencePosition,
        double centerX,
        double centerZ,
        int viewDistance
    )
    {
        RefreshMeshIfNeeded(centerX, centerZ, viewDistance);
        if (mesh == null || !mesh.Initialized || textureId <= 0) return;

        IShaderProgram? shader = shaderProvider();
        if (shader == null || shader.Disposed) return;

        IRenderAPI render = capi.Render;
        render.CurrentActiveShader?.Stop();
        render.GLEnableDepthTest();
        render.GLDepthMask(true);
        render.GlToggleBlend(false, EnumBlendMode.Standard);
        render.GlDisableCullFace();

        shader.Use();
        shader.UniformMatrix("projectionMatrix", projection);
        shader.UniformMatrix(
            "modelViewMatrix",
            Array.ConvertAll(view, value => (float)value)
        );
        shader.Uniform(
            "referencePosition",
            (float)referencePosition.X,
            (float)referencePosition.Y,
            (float)referencePosition.Z
        );
        shader.BindTexture2D("terrainTex", textureId, 0);
        render.RenderMesh(mesh);
        shader.Stop();
        render.GlEnableCullFace();
    }

    public void Dispose()
    {
        mesh?.Dispose();
        mesh = null;
    }

    private void RefreshMeshIfNeeded(double centerX, double centerZ, int viewDistance)
    {
        long now = capi.ElapsedMilliseconds;
        if (lastRefreshMilliseconds != long.MinValue
            && now - lastRefreshMilliseconds < RefreshMilliseconds) return;
        lastRefreshMilliseconds = now;

        int loadedMapChunkCount = capi.World.LoadedMapChunkIndices.Length;
        int chunkSize = GlobalConstants.ChunkSize;
        int centerChunkX = (int)Math.Floor(centerX / chunkSize);
        int centerChunkZ = (int)Math.Floor(centerZ / chunkSize);
        if (loadedMapChunkCount == lastLoadedMapChunkCount
            && centerChunkX == lastCenterChunkX
            && centerChunkZ == lastCenterChunkZ
            && viewDistance == lastViewDistance)
        {
            return;
        }

        lastLoadedMapChunkCount = loadedMapChunkCount;
        lastCenterChunkX = centerChunkX;
        lastCenterChunkZ = centerChunkZ;
        lastViewDistance = viewDistance;
        RebuildMesh(centerX, centerZ, viewDistance);
    }

    private void RebuildMesh(double centerX, double centerZ, int viewDistance)
    {
        int minX = AlignDown((int)Math.Floor(centerX - viewDistance), SampleStep);
        int minZ = AlignDown((int)Math.Floor(centerZ - viewDistance), SampleStep);
        int maxX = AlignDown((int)Math.Ceiling(centerX + viewDistance), SampleStep)
            + SampleStep;
        int maxZ = AlignDown((int)Math.Ceiling(centerZ + viewDistance), SampleStep)
            + SampleStep;
        int pointsX = (maxX - minX) / SampleStep + 1;
        int pointsZ = (maxZ - minZ) / SampleStep + 1;
        float[] heights = new float[pointsX * pointsZ];
        bool[] heightAvailable = new bool[heights.Length];

        for (int zIndex = 0; zIndex < pointsZ; zIndex++)
        {
            int worldZ = minZ + zIndex * SampleStep;
            for (int xIndex = 0; xIndex < pointsX; xIndex++)
            {
                int worldX = minX + xIndex * SampleStep;
                int index = zIndex * pointsX + xIndex;
                heightAvailable[index] = TryGetNaturalSurfaceHeight(
                    worldX,
                    worldZ,
                    out heights[index]
                );
            }
        }

        int cellsX = pointsX - 1;
        int cellsZ = pointsZ - 1;
        bool[] cells = new bool[cellsX * cellsZ];
        int cellCount = 0;
        double radiusSquared = (double)viewDistance * viewDistance;
        for (int zIndex = 0; zIndex < cellsZ; zIndex++)
        {
            double worldZ = minZ + (zIndex + 0.5) * SampleStep;
            for (int xIndex = 0; xIndex < cellsX; xIndex++)
            {
                double worldX = minX + (xIndex + 0.5) * SampleStep;
                double deltaX = worldX - centerX;
                double deltaZ = worldZ - centerZ;
                if (deltaX * deltaX + deltaZ * deltaZ > radiusSquared) continue;

                int topLeft = zIndex * pointsX + xIndex;
                bool available = heightAvailable[topLeft]
                    && heightAvailable[topLeft + 1]
                    && heightAvailable[topLeft + pointsX]
                    && heightAvailable[topLeft + pointsX + 1];
                if (!available) continue;

                cells[zIndex * cellsX + xIndex] = true;
                cellCount++;
            }
        }

        int boundaryCount = 0;
        for (int zIndex = 0; zIndex < cellsZ; zIndex++)
        {
            for (int xIndex = 0; xIndex < cellsX; xIndex++)
            {
                if (!CellAvailable(cells, cellsX, cellsZ, xIndex, zIndex)) continue;
                if (!CellAvailable(cells, cellsX, cellsZ, xIndex - 1, zIndex)) boundaryCount++;
                if (!CellAvailable(cells, cellsX, cellsZ, xIndex + 1, zIndex)) boundaryCount++;
                if (!CellAvailable(cells, cellsX, cellsZ, xIndex, zIndex - 1)) boundaryCount++;
                if (!CellAvailable(cells, cellsX, cellsZ, xIndex, zIndex + 1)) boundaryCount++;
            }
        }

        if (cellCount == 0)
        {
            mesh?.Dispose();
            mesh = null;
            return;
        }

        TextureAtlasPosition texturePosition = capi.BlockTextureAtlas[StoneTexture];
        textureId = texturePosition.atlasTextureId;
        int vertexCapacity = Math.Max(4, (cellCount + boundaryCount) * 4);
        MeshData meshData = new(vertexCapacity);
        int color = unchecked((int)0xffffffff);

        for (int zIndex = 0; zIndex < cellsZ; zIndex++)
        {
            float z0 = minZ + zIndex * SampleStep;
            float z1 = z0 + SampleStep;
            for (int xIndex = 0; xIndex < cellsX; xIndex++)
            {
                if (!CellAvailable(cells, cellsX, cellsZ, xIndex, zIndex)) continue;

                float x0 = minX + xIndex * SampleStep;
                float x1 = x0 + SampleStep;
                int topLeft = zIndex * pointsX + xIndex;
                float y00 = heights[topLeft] - ShellDepth;
                float y10 = heights[topLeft + 1] - ShellDepth;
                float y01 = heights[topLeft + pointsX] - ShellDepth;
                float y11 = heights[topLeft + pointsX + 1] - ShellDepth;

                AddQuad(
                    meshData,
                    texturePosition,
                    color,
                    x0, y00, z0,
                    x0, y01, z1,
                    x1, y11, z1,
                    x1, y10, z0
                );

                if (!CellAvailable(cells, cellsX, cellsZ, xIndex - 1, zIndex))
                {
                    AddSkirt(meshData, texturePosition, color, x0, z0, y00, x0, z1, y01);
                }
                if (!CellAvailable(cells, cellsX, cellsZ, xIndex + 1, zIndex))
                {
                    AddSkirt(meshData, texturePosition, color, x1, z1, y11, x1, z0, y10);
                }
                if (!CellAvailable(cells, cellsX, cellsZ, xIndex, zIndex - 1))
                {
                    AddSkirt(meshData, texturePosition, color, x1, z0, y10, x0, z0, y00);
                }
                if (!CellAvailable(cells, cellsX, cellsZ, xIndex, zIndex + 1))
                {
                    AddSkirt(meshData, texturePosition, color, x0, z1, y01, x1, z1, y11);
                }
            }
        }

        MeshRef replacement = capi.Render.UploadMesh(meshData);
        mesh?.Dispose();
        mesh = replacement;

        if (!loggedReady)
        {
            loggedReady = true;
            capi.Logger.Notification(
                "[ModernAtlas] Built a four-block-deep surface shell from {0} client-available cells with {1} sealed boundary faces.",
                cellCount,
                boundaryCount
            );
        }
    }

    private bool TryGetNaturalSurfaceHeight(int worldX, int worldZ, out float height)
    {
        int chunkSize = GlobalConstants.ChunkSize;
        BlockPos position = new(worldX, 0, worldZ);
        IMapChunk? mapChunk = capi.World.BlockAccessor.GetMapChunkAtBlockPos(position);
        if (mapChunk == null)
        {
            height = 0;
            return false;
        }

        int localX = GameMath.Mod(worldX, chunkSize);
        int localZ = GameMath.Mod(worldZ, chunkSize);
        ushort[] heightMap = mapChunk.WorldGenTerrainHeightMap;
        int index = localZ * chunkSize + localX;
        if (index < 0 || index >= heightMap.Length || heightMap[index] == 0)
        {
            height = 0;
            return false;
        }

        height = heightMap[index] + 1f;
        return true;
    }

    private static void AddSkirt(
        MeshData mesh,
        TextureAtlasPosition texture,
        int color,
        float x0,
        float z0,
        float y0,
        float x1,
        float z1,
        float y1
    )
    {
        AddQuad(
            mesh,
            texture,
            color,
            x0, y0, z0,
            x0, y0 - BoundarySkirtDepth, z0,
            x1, y1 - BoundarySkirtDepth, z1,
            x1, y1, z1
        );
    }

    private static void AddQuad(
        MeshData mesh,
        TextureAtlasPosition texture,
        int color,
        float x0, float y0, float z0,
        float x1, float y1, float z1,
        float x2, float y2, float z2,
        float x3, float y3, float z3
    )
    {
        int firstVertex = mesh.VerticesCount;
        mesh.AddVertex(x0, y0, z0, texture.x1, texture.y1, color);
        mesh.AddVertex(x1, y1, z1, texture.x1, texture.y2, color);
        mesh.AddVertex(x2, y2, z2, texture.x2, texture.y2, color);
        mesh.AddVertex(x3, y3, z3, texture.x2, texture.y1, color);
        mesh.AddQuadIndices(firstVertex);
    }

    private static bool CellAvailable(
        bool[] cells,
        int cellsX,
        int cellsZ,
        int x,
        int z
    )
    {
        return x >= 0 && z >= 0 && x < cellsX && z < cellsZ
            && cells[z * cellsX + x];
    }

    private static int AlignDown(int value, int step)
    {
        return value - GameMath.Mod(value, step);
    }
}
