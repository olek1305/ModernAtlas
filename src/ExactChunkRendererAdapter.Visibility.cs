using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
namespace ModernAtlas;

/// <summary>
/// Atlas visibility and the hard disclosure boundary: the Harmony visibility
/// hook, the completed-view boundary anchored to the player's world position
/// and the atlas texture bindings.
/// </summary>
internal sealed partial class ExactChunkRendererAdapter
{
    private static bool UseAtlasVisibility(
        ModelDataPoolLocation __instance,
        EnumFrustumCullMode mode,
        FrustumCulling culler,
        ref bool __result
    )
    {
        if (!atlasVisibilityOverride) return true;

        if (atlasTerrainCollectionOverride
            && __instance.IndicesEnd > __instance.IndicesStart)
        {
            (int X, int Y, int Z) consideredChunk = GetMeshChunk(
                __instance,
                GlobalConstants.ChunkSize
            );
            atlasConsideredTerrainColumns?.Add((consideredChunk.X, consideredChunk.Z));
        }

        if (__instance.Hide)
        {
            __result = false;
            return false;
        }

        if (atlasCompleteBoundaryEnabled
            && __instance.IndicesEnd > __instance.IndicesStart)
        {
            (int X, int Y, int Z) chunk = GetMeshChunk(
                __instance,
                GlobalConstants.ChunkSize
            );
            if (chunk.X < atlasCompleteMinimumChunkX
                || chunk.X > atlasCompleteMaximumChunkX
                || chunk.Z < atlasCompleteMinimumChunkZ
                || chunk.Z > atlasCompleteMaximumChunkZ)
            {
                __result = false;
                return false;
            }
        }

        if (DeveloperDisableFrustum)
        {
            __result = __instance.IndicesEnd > __instance.IndicesStart;
        }
        else
        {
            switch (mode)
            {
                case EnumFrustumCullMode.CullInstant:
                    __result = culler.InFrustum(__instance.FrustumCullSphere);
                    break;
                case EnumFrustumCullMode.CullInstantShadowPassNear:
                    __result = culler.InFrustumShadowPass(__instance.FrustumCullSphere);
                    break;
                case EnumFrustumCullMode.CullInstantShadowPassFar:
                    __result = __instance.LodLevel >= 1
                        && culler.InFrustumShadowPass(__instance.FrustumCullSphere);
                    break;
                case EnumFrustumCullMode.CullNormal:
                    // Use the same native range/LOD decision as the game for every
                    // atlas pass. The transparent pass below is additionally
                    // constrained to a column that the opaque pass actually
                    // accepted, so OIT cannot expose a floating leaf mesh.
                    __result = culler.InFrustumAndRange(
                        __instance.FrustumCullSphere,
                        __instance.FrustumVisible,
                        __instance.LodLevel
                    );
                    __instance.FrustumVisible = __result;
                    break;
                default:
                    __result = true;
                    break;
            }
        }

        if (__result
            && atlasDisclosureCullingOverride
            && !IsInsideDisclosureBoundary(
                __instance.FrustumCullSphere
            ))
        {
            // The guarded culling projection intentionally admits a small
            // screen-space apron to prevent subpixel pool popping. It must
            // never enlarge the player-anchored disclosure area, however.
            // Reject a whole mesh location unless its complete XZ footprint
            // fits inside the boundary. The fragment shader remains the
            // final per-pixel guard for locations that straddle the edge.
            __result = false;
        }

        if (__result
            && atlasSupportedTerrainColumns != null
            && __instance.IndicesEnd > __instance.IndicesStart)
        {
            (int X, int Y, int Z) supportedChunk = GetMeshChunk(
                __instance,
                GlobalConstants.ChunkSize
            );
            if (!atlasSupportedTerrainColumns.Contains(
                    (supportedChunk.X, supportedChunk.Z)
                ))
            {
                __result = false;
            }
        }

        if (__result
            && atlasSupportedSurfaceSections != null
            && __instance.IndicesEnd > __instance.IndicesStart)
        {
            (int X, int Y, int Z) edgeChunk = GetMeshChunk(
                __instance,
                GlobalConstants.ChunkSize
            );
            double edgeCenterX = (edgeChunk.X + 0.5) * GlobalConstants.ChunkSize;
            double edgeCenterZ = (edgeChunk.Z + 0.5) * GlobalConstants.ChunkSize;
            double edgeDeltaX = edgeCenterX - atlasDisclosureCenterX;
            double edgeDeltaZ = edgeCenterZ - atlasDisclosureCenterZ;
            double surfaceOnlyRadius = Math.Max(
                GlobalConstants.ChunkSize,
                atlasDisclosureRadius - GlobalConstants.ChunkSize * 2
            );
            if (edgeDeltaX * edgeDeltaX + edgeDeltaZ * edgeDeltaZ
                    > surfaceOnlyRadius * surfaceOnlyRadius
                && !atlasSupportedSurfaceSections.Contains(edgeChunk))
            {
                // Near the streaming/disclosure frontier, upper vertical
                // sections can complete before the ground silhouette below
                // them. Do not submit those tree/fluid-only sections. The
                // actual surface section remains exact block geometry.
                __result = false;
            }
        }

        if (__result
            && atlasDisclosureCullingOverride
            && (atlasVegetationOnlyVisibilityOverride
                || atlasTransparentVisibilityOverride
                || atlasLiquidVisibilityOverride)
            && !IsInsideDependentMaterialBoundary(__instance.FrustumCullSphere))
        {
            // Dependent surfaces are allowed only slightly inside the hard
            // edge. Their pools can contain leaves, grass or water after the
            // corresponding solid ground mesh has become unavailable. A
            // half-chunk guard prevents that last material-only fringe while
            // the world-space fragment cutoff still supplies the exact circle.
            __result = false;
        }

        if (__result
            && atlasVegetationOnlyVisibilityOverride
            && __instance.IndicesEnd > __instance.IndicesStart)
        {
            (int X, int Y, int Z) vegetationChunk = GetMeshChunk(
                __instance,
                GlobalConstants.ChunkSize
            );
            if (atlasVisibleTerrainColumns?.Contains(
                    (vegetationChunk.X, vegetationChunk.Z)
                ) != true)
            {
                // The vegetation-only pass may use only columns that the
                // preceding ground pass accepted. This prevents an opaque
                // alpha-tested plant mesh from surviving where its terrain
                // column has no completed atlas mesh.
                __result = false;
            }
        }

        if (__result && __instance.IndicesEnd > __instance.IndicesStart)
        {
            if (atlasLiquidVisibilityOverride
                && atlasLiquidAdapter?.IsCompletedLiquidChunk(__instance) != true)
            {
                __result = false;
            }
            else if (atlasTransparentVisibilityOverride)
            {
                (int X, int Y, int Z) chunk = GetMeshChunk(
                    __instance,
                    GlobalConstants.ChunkSize
                );
                if (atlasVisibleTerrainColumns?.Contains((chunk.X, chunk.Z)) != true)
                {
                    // OIT pools can become available independently from the
                    // opaque pool. Composing those fragments without their
                    // completed terrain column blackens the atlas background.
                    __result = false;
                }
            }
        }

        return false;
    }

    private static bool IsInsideDisclosureBoundary(Sphere sphere)
    {
        // This is only a coarse GPU-pool rejection. The final framebuffer
        // mask enforces the exact material-independent circular boundary.
        double deltaX = sphere.x - atlasDisclosureCenterX;
        double deltaZ = sphere.z - atlasDisclosureCenterZ;
        double horizontalDistance = Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
        return horizontalDistance - sphere.radius <= atlasDisclosureRadius;
    }

    private static bool IsInsideDependentMaterialBoundary(Sphere sphere)
    {
        double allowedCenterRadius = atlasDisclosureRadius
            - GlobalConstants.ChunkSize * 0.5;
        if (allowedCenterRadius <= 0) return false;
        double deltaX = sphere.x - atlasDisclosureCenterX;
        double deltaZ = sphere.z - atlasDisclosureCenterZ;
        return deltaX * deltaX + deltaZ * deltaZ
            <= allowedCenterRadius * allowedCenterRadius;
    }

    private static void CollectAtlasVisibleTerrain(
        ModelDataPoolLocation __instance,
        EnumFrustumCullMode mode,
        bool __result
    )
    {
        if (!atlasVisibilityOverride
            || !atlasTerrainCollectionOverride
            || mode != EnumFrustumCullMode.CullNormal
            || !__result
            || __instance.IndicesEnd <= __instance.IndicesStart)
        {
            return;
        }

        (int X, int Y, int Z) chunk = GetMeshChunk(
            __instance,
            GlobalConstants.ChunkSize
        );
        atlasVisibleTerrainColumns?.Add((chunk.X, chunk.Z));
    }

    private void UpdateCompleteViewBoundary(int viewDistanceBlocks)
    {
        atlasCompleteBoundaryEnabled = false;
        if (viewDistanceBlocks <= 0)
        {
            return;
        }

        int chunkSize = GlobalConstants.ChunkSize;
        int playerChunkX = FloorDiv(
            (int)Math.Floor(capi.World.Player.Entity.Pos.X), chunkSize
        );
        int playerChunkZ = FloorDiv(
            (int)Math.Floor(capi.World.Player.Entity.Pos.Z), chunkSize
        );
        int safeRadius = CalculateCompleteViewChunkRadius(viewDistanceBlocks);
        if (safeRadius < 1) return;

        atlasCompleteMinimumChunkX = playerChunkX - safeRadius;
        atlasCompleteMaximumChunkX = playerChunkX + safeRadius;
        atlasCompleteMinimumChunkZ = playerChunkZ - safeRadius;
        atlasCompleteMaximumChunkZ = playerChunkZ + safeRadius;
        atlasCompleteBoundaryEnabled = true;

        if (loggedCompleteBoundaryRadius != safeRadius)
        {
            loggedCompleteBoundaryRadius = safeRadius;
            capi.Logger.Notification(
                "[ModernAtlas] Atlas-only view boundary: {0}x{0} chunks with a one-chunk safety inset (view distance {1} blocks).",
                safeRadius * 2 + 1,
                viewDistanceBlocks
            );
        }
    }

    internal static int CalculateCompleteViewChunkRadius(int viewDistanceBlocks)
    {
        return Math.Max(
            0,
            (int)Math.Floor(
                viewDistanceBlocks
                    / (Math.Sqrt(2d) * GlobalConstants.ChunkSize)
            ) - 1
        );
    }

    private void LogTerrainCoverage(int viewDistanceBlocks)
    {
        if (loggedTerrainCoverage) return;
        loggedTerrainCoverage = true;

        int chunkSize = GlobalConstants.ChunkSize;
        int playerChunkX = (int)Math.Floor(
            capi.World.Player.Entity.Pos.X / chunkSize
        );
        int playerChunkZ = (int)Math.Floor(
            capi.World.Player.Entity.Pos.Z / chunkSize
        );
        int chunkRadius = Math.Max(1, (viewDistanceBlocks + chunkSize - 1) / chunkSize);
        int verticalChunkCount = Math.Max(
            1,
            (capi.World.BlockAccessor.MapSizeY + chunkSize - 1) / chunkSize
        );
        int dimensionOffset = capi.World.Player.Entity.Pos.Dimension
            * GlobalConstants.DimensionSizeInChunks;
        HashSet<(int X, int Z)> loadedColumns = new();
        for (int chunkZ = playerChunkZ - chunkRadius;
            chunkZ <= playerChunkZ + chunkRadius;
            chunkZ++)
        {
            for (int chunkX = playerChunkX - chunkRadius;
                chunkX <= playerChunkX + chunkRadius;
                chunkX++)
            {
                for (int chunkY = 0; chunkY < verticalChunkCount; chunkY++)
                {
                    if (capi.World.BlockAccessor.GetChunk(
                        chunkX,
                        chunkY + dimensionOffset,
                        chunkZ
                    ) is not IClientChunk { LoadedFromServer: true })
                    {
                        continue;
                    }

                    loadedColumns.Add((chunkX, chunkZ));
                    break;
                }
            }
        }

        List<string> missingMeshSamples = new();
        int loadedWithoutMesh = 0;
        foreach ((int X, int Z) column in loadedColumns)
        {
            if (consideredTerrainColumns.Contains(column)) continue;
            loadedWithoutMesh++;
            if (missingMeshSamples.Count < 12)
            {
                missingMeshSamples.Add(
                    $"{column.X - playerChunkX:+0;-0;0},{column.Z - playerChunkZ:+0;-0;0}"
                );
            }
        }

        int meshOutsideAtlasFrustum = 0;
        foreach ((int X, int Z) column in consideredTerrainColumns)
        {
            if (!visibleTerrainColumns.Contains(column)) meshOutsideAtlasFrustum++;
        }

        capi.Logger.Notification(
            "[ModernAtlas] Exact terrain coverage: client-loaded columns={0}, completed mesh columns={1}, atlas-visible mesh columns={2}, loaded without completed mesh={3}, mesh columns outside atlas frustum={4}; missing mesh offsets={5}.",
            loadedColumns.Count,
            consideredTerrainColumns.Count,
            visibleTerrainColumns.Count,
            loadedWithoutMesh,
            meshOutsideAtlasFrustum,
            missingMeshSamples.Count == 0 ? "none" : string.Join(" ", missingMeshSamples)
        );
    }

    private void BeginAtlasTextureBindings(
        bool concealSurvivalOres,
        bool bindVegetationMask
    )
    {
        atlasOreTextureBindingAdapter = concealSurvivalOres || bindVegetationMask
            ? this
            : null;
        atlasOreTextureBindingOverride = concealSurvivalOres;
        atlasVegetationTextureBindingOverride = bindVegetationMask;
        atlasOreTextureBindingRecursion = false;
    }

    private static void EndAtlasTextureBindings()
    {
        atlasOreTextureBindingRecursion = false;
        atlasOreTextureBindingOverride = false;
        atlasVegetationTextureBindingOverride = false;
        atlasOreTextureBindingAdapter = null;
    }

    private static void BindAtlasOreTexturesAfterTerrainTexture(
        object __instance,
        object[] __args
    )
    {
        if ((!atlasOreTextureBindingOverride && !atlasVegetationTextureBindingOverride)
            || atlasOreTextureBindingRecursion
            || atlasOreTextureBindingAdapter is not ExactChunkRendererAdapter adapter
            || __instance is not IShaderProgram shader
            || __args.Length < 3
            || __args[0] is not string samplerName
            || !string.Equals(samplerName, "terrainTex", StringComparison.Ordinal)
            || __args[1] is not int terrainTextureId
            || !shader.HasUniform("atlasConcealOres"))
        {
            return;
        }

        atlasOreTextureBindingRecursion = true;
        try
        {
            if (atlasOreTextureBindingOverride
                && !adapter.oreTextureReplacement.BindForTerrainTexture(
                    shader,
                    terrainTextureId,
                    OreMappingTextureUnit,
                    OreStoneTextureUnit
                )
                && !adapter.loggedOreTextureBindingFailure)
            {
                adapter.loggedOreTextureBindingFailure = true;
                adapter.capi.Logger.Error(
                    "[ModernAtlas] Survival ore concealment could not bind its atlas textures."
                );
            }
            if (atlasVegetationTextureBindingOverride
                && !adapter.vegetationTextureMask.BindForTerrainTexture(
                    shader,
                    terrainTextureId,
                    VegetationMaskTextureUnit
                ))
            {
                throw new InvalidOperationException(
                    "The atlas vegetation mask could not bind its texture."
                );
            }
        }
        finally
        {
            atlasOreTextureBindingRecursion = false;
        }
    }

}
