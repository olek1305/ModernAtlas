using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ModernAtlas;

/// <summary>
/// Draws loaded doors whose current open or moving pose is owned by Vintage
/// Story's block-entity animation renderer. Closed, idle doors remain in the
/// completed chunk mesh and are deliberately not duplicated here.
/// </summary>
internal sealed class AtlasAnimatedDoorRendererAdapter
{
    private const int ExteriorSafetyAllowance = 3;
    private const int MaximumScannedSectionsPerFrame = 128;
    private const double ScanBudgetMilliseconds = 0.75;
    private const long ColumnRefreshMilliseconds = 500;

    private readonly ICoreClientAPI capi;
    private readonly FieldInfo animationUtilField;
    private readonly FieldInfo nativeRendererField;
    private readonly FieldInfo shouldRenderField;
    private readonly FieldInfo modelMatrixField;
    private readonly HashSet<BlockEntity> loadedDoorEntities = new(
        ReferenceEqualityComparer.Instance
    );
    private readonly List<(int X, int Z)> scanColumns = new();
    private readonly HashSet<(int X, int Z)> scanColumnSet = new();
    private readonly List<BlockEntity> staleDoorEntities = new();
    private int scanColumnIndex;
    private int scanVerticalChunkIndex;
    private long nextColumnRefreshMilliseconds;
    private bool scanComplete;
    private bool disabled;
    private bool loggedActive;
    private bool loggedFailure;

    public int LastLoadedDoorCount { get; private set; }
    public int LastRenderedDoorCount { get; private set; }
    public bool RenderStateRestored { get; private set; } = true;

    public AtlasAnimatedDoorRendererAdapter(ICoreClientAPI capi)
    {
        this.capi = capi;
        animationUtilField = RequireField(
            typeof(BEBehaviorAnimatable),
            "animUtil"
        );
        nativeRendererField = RequireField(
            typeof(AnimationUtil),
            "renderer"
        );
        shouldRenderField = RequireField(
            typeof(AnimatableRenderer),
            "ShouldRender"
        );
        modelMatrixField = RequireField(
            typeof(AnimatableRenderer),
            "ModelMat"
        );
    }

    public int Render(
        double[] atlasView,
        IReadOnlySet<(int X, int Z)> visibleTerrainColumns,
        double disclosureCenterX,
        double disclosureCenterZ,
        int disclosureRadius,
        AtlasSurfaceHeightTexture? surfaceHeightTexture
    )
    {
        LastLoadedDoorCount = 0;
        LastRenderedDoorCount = 0;
        RenderStateRestored = true;
        if (disabled || atlasView.Length < 16) return 0;

        try
        {
            RefreshScanColumns(visibleTerrainColumns);
            AdvanceLoadedDoorScan();
            RemoveStaleDoorEntities();
            LastLoadedDoorCount = loadedDoorEntities.Count;
            if (loadedDoorEntities.Count == 0) return 0;

            IRenderAPI render = capi.Render;
            float[] cameraMatrixOrigin = render.CameraMatrixOriginf;
            if (cameraMatrixOrigin == null || cameraMatrixOrigin.Length < 16)
            {
                return 0;
            }

            float[] savedCameraMatrix = (float[])cameraMatrixOrigin.Clone();
            AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
            FrameBufferRef? atlasFramebuffer = render.CurrentFrameBuffer;
            RenderStateRestored = false;
            try
            {
                for (int index = 0; index < 16; index++)
                {
                    cameraMatrixOrigin[index] = (float)atlasView[index];
                }

                render.CurrentActiveShader?.Stop();
                render.GLEnableDepthTest();
                render.GLDepthMask(true);
                render.GlColorMask(true, true, true, true);
                render.GlScissorFlag(false);
                render.GlToggleBlend(false, EnumBlendMode.Standard);

                double radius = Math.Max(GlobalConstants.ChunkSize, disclosureRadius);
                double radiusSquared = radius * radius;
                foreach (BlockEntity blockEntity in loadedDoorEntities)
                {
                    BlockPos position = blockEntity.Pos;
                    int chunkX = FloorDiv(position.X, GlobalConstants.ChunkSize);
                    int chunkZ = FloorDiv(position.Z, GlobalConstants.ChunkSize);
                    if (!visibleTerrainColumns.Contains((chunkX, chunkZ))) continue;

                    double dx = position.X + 0.5 - disclosureCenterX;
                    double dz = position.Z + 0.5 - disclosureCenterZ;
                    if (dx * dx + dz * dz >= radiusSquared) continue;
                    if (surfaceHeightTexture != null
                        && (!surfaceHeightTexture.TryGetSurfaceHeight(
                                position.X + 0.5,
                                position.Z + 0.5,
                                out int surfaceHeight
                            )
                            || position.InternalY
                                < surfaceHeight - ExteriorSafetyAllowance))
                    {
                        continue;
                    }

                    AnimatableRenderer? nativeRenderer = ResolveNativeRenderer(
                        blockEntity
                    );
                    if (nativeRenderer == null
                        || shouldRenderField.GetValue(nativeRenderer) is not true)
                    {
                        continue;
                    }

                    RenderNativeDoor(nativeRenderer);
                    LastRenderedDoorCount++;
                }

                if (LastRenderedDoorCount > 0 && !loggedActive)
                {
                    loggedActive = true;
                    capi.Logger.Notification(
                        "[ModernAtlas] Atlas animated-door pass is active: rendered {0} open or moving doors from {1} loaded door block entities.",
                        LastRenderedDoorCount,
                        LastLoadedDoorCount
                    );
                }
            }
            finally
            {
                Array.Copy(savedCameraMatrix, cameraMatrixOrigin, 16);
                renderState.RestoreCapturedState();
                render.CurrentFrameBuffer = atlasFramebuffer;
                render.GLEnableDepthTest();
                render.GLDepthMask(true);
                render.GlColorMask(true, true, true, true);
                render.GlScissorFlag(false);
                render.GlEnableCullFace();
                render.GlToggleBlend(false, EnumBlendMode.Standard);
                RenderStateRestored = true;
            }

            return LastRenderedDoorCount;
        }
        catch (Exception exception)
        {
            disabled = true;
            if (!loggedFailure)
            {
                loggedFailure = true;
                capi.Logger.Error(
                    "[ModernAtlas] Animated doors were disabled for this session without disabling exact terrain: {0}",
                    exception.Message
                );
            }
            return 0;
        }
    }

    public void Clear()
    {
        loadedDoorEntities.Clear();
        staleDoorEntities.Clear();
        scanColumns.Clear();
        scanColumnSet.Clear();
        scanColumnIndex = 0;
        scanVerticalChunkIndex = 0;
        scanComplete = false;
        nextColumnRefreshMilliseconds = 0;
    }

    private void RefreshScanColumns(
        IReadOnlySet<(int X, int Z)> visibleTerrainColumns
    )
    {
        long now = capi.ElapsedMilliseconds;
        if (now < nextColumnRefreshMilliseconds && scanColumns.Count > 0)
        {
            return;
        }
        nextColumnRefreshMilliseconds = now + ColumnRefreshMilliseconds;

        bool changed = scanColumnSet.Count != visibleTerrainColumns.Count;
        if (!changed)
        {
            foreach ((int X, int Z) column in visibleTerrainColumns)
            {
                if (scanColumnSet.Contains(column)) continue;
                changed = true;
                break;
            }
        }
        if (!changed)
        {
            if (!scanComplete) return;
            scanColumnIndex = 0;
            scanVerticalChunkIndex = 0;
            scanComplete = false;
            return;
        }

        scanColumns.Clear();
        scanColumnSet.Clear();
        foreach ((int X, int Z) column in visibleTerrainColumns)
        {
            scanColumns.Add(column);
            scanColumnSet.Add(column);
        }
        scanColumnIndex = 0;
        scanVerticalChunkIndex = 0;
        scanComplete = false;
    }

    private void AdvanceLoadedDoorScan()
    {
        if (scanColumns.Count == 0 || scanComplete) return;

        int chunkSize = GlobalConstants.ChunkSize;
        int verticalChunkCount = Math.Max(
            1,
            (capi.World.BlockAccessor.MapSizeY + chunkSize - 1) / chunkSize
        );
        int dimensionOffset = capi.World.Player.Entity.Pos.Dimension
            * GlobalConstants.DimensionSizeInChunks;
        long started = Stopwatch.GetTimestamp();
        int scannedSections = 0;
        while (scannedSections < MaximumScannedSectionsPerFrame
            && (scannedSections == 0
                || Stopwatch.GetElapsedTime(started).TotalMilliseconds
                    < ScanBudgetMilliseconds))
        {
            if (scanColumnIndex >= scanColumns.Count)
            {
                scanComplete = true;
                return;
            }

            (int X, int Z) column = scanColumns[scanColumnIndex];
            int chunkY = scanVerticalChunkIndex++;
            if (scanVerticalChunkIndex >= verticalChunkCount)
            {
                scanVerticalChunkIndex = 0;
                scanColumnIndex++;
            }
            scannedSections++;

            IWorldChunk? chunk;
            try
            {
                chunk = capi.World.BlockAccessor.GetChunk(
                    column.X,
                    chunkY + dimensionOffset,
                    column.Z
                );
            }
            catch
            {
                continue;
            }
            if (chunk == null
                || chunk.Disposed
                || chunk is IClientChunk clientChunk && !clientChunk.LoadedFromServer)
            {
                continue;
            }

            Dictionary<BlockPos, BlockEntity>? blockEntities;
            try
            {
                blockEntities = chunk.BlockEntities;
            }
            catch
            {
                continue;
            }
            if (blockEntities == null) continue;
            try
            {
                foreach (BlockEntity? blockEntity in blockEntities.Values)
                {
                    if (blockEntity == null || !IsSupportedDoor(blockEntity)) continue;
                    loadedDoorEntities.Add(blockEntity);
                }
            }
            catch
            {
                // Streaming can replace a loaded chunk's block-entity table
                // between the public GetChunk call and this enumeration. The
                // next atlas-only scan retries it without disabling doors.
            }
        }
    }

    private void RemoveStaleDoorEntities()
    {
        staleDoorEntities.Clear();
        foreach (BlockEntity blockEntity in loadedDoorEntities)
        {
            try
            {
                if (!ReferenceEquals(
                        capi.World.BlockAccessor.GetBlockEntity(blockEntity.Pos),
                        blockEntity
                    ))
                {
                    staleDoorEntities.Add(blockEntity);
                }
            }
            catch
            {
                staleDoorEntities.Add(blockEntity);
            }
        }
        foreach (BlockEntity blockEntity in staleDoorEntities)
        {
            loadedDoorEntities.Remove(blockEntity);
        }
    }

    private static bool IsSupportedDoor(BlockEntity blockEntity) =>
        blockEntity.GetBehavior<BEBehaviorDoor>() != null
        || blockEntity.GetBehavior<BEBehaviorTrapDoor>() != null
        || blockEntity.GetBehavior<BEBehaviorCabinetDoors>() != null;

    private AnimatableRenderer? ResolveNativeRenderer(BlockEntity blockEntity)
    {
        BEBehaviorAnimatable? behavior = blockEntity.GetBehavior<BEBehaviorDoor>();
        behavior ??= blockEntity.GetBehavior<BEBehaviorTrapDoor>();
        behavior ??= blockEntity.GetBehavior<BEBehaviorCabinetDoors>();
        if (behavior == null) return null;

        object? animationUtil = animationUtilField.GetValue(behavior);
        return animationUtil == null
            ? null
            : nativeRendererField.GetValue(animationUtil) as AnimatableRenderer;
    }

    private void RenderNativeDoor(AnimatableRenderer renderer)
    {
        if (modelMatrixField.GetValue(renderer) is not float[] modelMatrix)
        {
            throw new InvalidOperationException(
                "The native animated-door model matrix is unavailable."
            );
        }

        float[] savedModelMatrix = ArrayPool<float>.Shared.Rent(modelMatrix.Length);
        Array.Copy(modelMatrix, savedModelMatrix, modelMatrix.Length);
        try
        {
            // Invoke the pose renderer directly. AnimationUtil.OnRenderFrame
            // is intentionally not called, so the atlas cannot advance a door
            // animation or change the block entity's open/closed state.
            renderer.OnRenderFrame(0, EnumRenderStage.Opaque);
        }
        finally
        {
            Array.Copy(savedModelMatrix, modelMatrix, modelMatrix.Length);
            ArrayPool<float>.Shared.Return(savedModelMatrix);
        }
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static FieldInfo RequireField(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(
                name,
                BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly
            );
            if (field != null) return field;
        }
        throw new MissingFieldException(type.FullName, name);
    }
}
