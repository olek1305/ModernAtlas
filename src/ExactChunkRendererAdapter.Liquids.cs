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
/// Transparent chunk materials through the engine OIT buffers plus the
/// ModernAtlas stable-liquid pass that draws completed liquid meshes without
/// the camera-dependent stock liquid shader.
/// </summary>
internal sealed partial class ExactChunkRendererAdapter
{
    private bool RenderTransparentChunks(
        float deltaTime,
        float[] projection,
        double[] view,
        Vec3d cameraPosition,
        float waterStillCounter,
        float waterFlowCounter,
        bool cloudsEnabled,
        float pausedCloudAnimationDeltaTime,
        Vec3f? frozenCloudOffset,
        bool hideUndergroundCaves,
        bool concealSurvivalOres,
        bool hideVegetation,
        AtlasSurfaceHeightTexture? surfaceHeightTexture,
        AtlasMapLayerTexture? mapLayerTexture,
        float mapLayerOpacity,
        int disclosureRadius,
        bool blitToDefault
    )
    {
        if (DeveloperDisableTransparentPass)
        {
            if (!loggedTransparentSuccess)
            {
                loggedTransparentSuccess = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Developer diagnostic disabled the atlas transparent/OIT pass."
                );
            }
            return false;
        }
        if (transparentPassDisabled) return false;

        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        bool framebufferLoaded = false;
        List<(object Manager, object Pools)> hiddenLiquidPools = new();
        try
        {
            loadFramebuffer.Invoke(platform, new object[] { EnumFrameBuffer.Transparent });
            framebufferLoaded = true;
            clearFramebuffer.Invoke(platform, new object[] { EnumFrameBuffer.Transparent });
            // VS 1.22.6 adds layered OIT attachments beyond the legacy
            // accumulation/reveal buffers. Invoke only the engine's two OIT
            // setup renderers around chunk liquids; do not trigger the global
            // render stage, which would also draw entities and particles.
            runBeforeOit.Invoke(
                beforeOitRenderer,
                new object[] { deltaTime, EnumRenderStage.OIT }
            );
            HideLiquidPools(hiddenLiquidPools);
            if (!ConfigureAtlasFilters(
                surfaceHeightTexture,
                mapLayerTexture,
                mapLayerOpacity,
                cameraPosition,
                        true,
                        hideUndergroundCaves,
                        concealSurvivalOres,
                        hideVegetation,
                        0,
                        disclosureRadius,
                        true
            ))
            {
                throw new InvalidOperationException(
                    "The transparent-block safety filters could not be configured."
                );
            }
            BeginAtlasTextureBindings(
                concealSurvivalOres,
                vegetationTextureMask.Ready
            );
            atlasTransparentVisibilityOverride = true;
            try
            {
                renderOit.Invoke(chunkRenderer, new object[] { deltaTime });
            }
            finally
            {
                atlasTransparentVisibilityOverride = false;
                EndAtlasTextureBindings();
            }
            RestoreLiquidPools(hiddenLiquidPools);
            runAfterOit.Invoke(
                afterOitRenderer,
                new object[] { deltaTime, EnumRenderStage.OIT }
            );
            unloadFramebuffer.Invoke(platform, new object[] { EnumFrameBuffer.Transparent });
            framebufferLoaded = false;

            // Compose at the Primary framebuffer's own resolution before the
            // final blit. OIT uses texelFetch(gl_FragCoord), so composing it
            // directly onto a differently sized window target under SSAA
            // makes transparent blocks slide relative to terrain while the
            // camera pans.
            mergeTransparentRenderPass.Invoke(platform, Array.Empty<object>());
            BeginAtlasTextureBindings(
                concealSurvivalOres,
                vegetationTextureMask.Ready
            );
            atlasTransparentVisibilityOverride = true;
            try
            {
                renderAfterOit.Invoke(chunkRenderer, new object[] { deltaTime });
            }
            finally
            {
                atlasTransparentVisibilityOverride = false;
                EndAtlasTextureBindings();
            }

            // Resolve every real transparent block before blending stable
            // liquids into Primary. This preserves the world's authored
            // texture colors below water, including the yellow and orange
            // bacterial mats in hot springs, instead of compositing those
            // surfaces at full strength on top of the water afterward.
            RenderStableLiquidBlocks(
                deltaTime,
                projection,
                view,
                cameraPosition,
                waterStillCounter,
                waterFlowCounter,
                hideUndergroundCaves,
                surfaceHeightTexture,
                mapLayerTexture,
                mapLayerOpacity,
                disclosureRadius
            );
            if (cloudsEnabled)
            {
                cloudRenderer?.Render(
                    projection,
                    view,
                    pausedCloudAnimationDeltaTime,
                    true,
                    frozenCloudOffset,
                    atlasSunColor,
                    atlasExposure
                );
            }
            if (!loggedTransparentSuccess)
            {
                loggedTransparentSuccess = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Rendering stable liquid block surfaces and engine OIT transparent materials."
                );
            }
            return true;
        }
        catch (Exception exception)
        {
            transparentPassDisabled = true;
            Exception cause = exception is TargetInvocationException { InnerException: not null }
                ? exception.InnerException
                : exception;
            capi.Logger.Error(
                "[ModernAtlas] Liquid and transparent rendering failed and was disabled for this session: {0}",
                cause.Message
            );
            return false;
        }
        finally
        {
            atlasTransparentVisibilityOverride = false;
            RestoreLiquidPools(hiddenLiquidPools);
            if (framebufferLoaded)
            {
                try
                {
                    unloadFramebuffer.Invoke(platform, new object[] { EnumFrameBuffer.Transparent });
                }
                catch
                {
                    // Preserve the opaque atlas even if framebuffer cleanup is
                    // unavailable after an OIT failure.
                }
            }
            // Restore the target and active program owned by the enclosing
            // atlas pass, including after a transparent-pass exception.
            renderState.RestoreCapturedState();
        }
    }

    private void HideLiquidPools(List<(object Manager, object Pools)> hidden)
    {
        if (poolsByRenderPassField.GetValue(chunkRenderer)
            is not MeshDataPoolManager[][] passes) return;

        foreach (MeshDataPoolManager? manager in passes[(int)EnumChunkRenderPass.Liquid])
        {
            if (manager == null) continue;
            object? pools = managerPoolsField.GetValue(manager);
            if (pools == null) continue;
            object emptyPools = Activator.CreateInstance(pools.GetType())
                ?? throw new InvalidOperationException("Could not create an empty liquid pool list.");
            hidden.Add((manager, pools));
            managerPoolsField.SetValue(manager, emptyPools);
        }
    }

    private void RestoreLiquidPools(List<(object Manager, object Pools)> hidden)
    {
        for (int index = hidden.Count - 1; index >= 0; index--)
        {
            (object manager, object pools) = hidden[index];
            managerPoolsField.SetValue(manager, pools);
        }
        hidden.Clear();
    }

    private void RenderStableLiquidBlocks(
        float deltaTime,
        float[] projection,
        double[] view,
        Vec3d cameraPosition,
        float waterStillCounter,
        float waterFlowCounter,
        bool hideUndergroundCaves,
        AtlasSurfaceHeightTexture? surfaceHeightTexture,
        AtlasMapLayerTexture? mapLayerTexture,
        float mapLayerOpacity,
        int disclosureRadius
    )
    {
        IShaderProgram? activeLiquidShader = stableLiquidShaderProvider();
        if (activeLiquidShader == null || activeLiquidShader.Disposed)
        {
            if (!loggedStableLiquidDiagnostics)
            {
                loggedStableLiquidDiagnostics = true;
                capi.Logger.Error("[ModernAtlas] Stable liquid shader is not loaded.");
            }
            return;
        }
        if (poolsByRenderPassField.GetValue(chunkRenderer)
            is not MeshDataPoolManager[][] passes) return;
        if (textureIdsField.GetValue(chunkRenderer) is not int[] textureIds) return;

        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        try
        {
        render.CurrentActiveShader?.Stop();
        render.GLEnableDepthTest();
        render.GLDepthMask(false);
        render.GlToggleBlend(true, EnumBlendMode.Standard);
        render.GlDisableCullFace();

        activeLiquidShader.Use();
        activeLiquidShader.UniformMatrix("projectionMatrix", projection);
        activeLiquidShader.UniformMatrix(
            "modelViewMatrix",
            Array.ConvertAll(view, value => (float)value)
        );
        activeLiquidShader.Uniform(
            "playerpos",
            (float)cameraPosition.X,
            (float)cameraPosition.Y,
            (float)cameraPosition.Z
        );
        activeLiquidShader.Uniform(
            "disclosureCenterXZ",
            (float)capi.World.Player.Entity.Pos.X,
            (float)capi.World.Player.Entity.Pos.Z
        );
        activeLiquidShader.Uniform(
            "disclosureRadius",
            (float)Math.Max(GlobalConstants.ChunkSize, disclosureRadius)
        );
        activeLiquidShader.Uniform(
            "completeBoundaryEnabled",
            atlasCompleteBoundaryEnabled ? 1 : 0
        );
        activeLiquidShader.Uniform(
            "completeBoundaryMinXZ",
            atlasCompleteMinimumChunkX * (float)GlobalConstants.ChunkSize,
            atlasCompleteMinimumChunkZ * (float)GlobalConstants.ChunkSize
        );
        activeLiquidShader.Uniform(
            "completeBoundaryMaxXZ",
            (atlasCompleteMaximumChunkX + 1) * (float)GlobalConstants.ChunkSize,
            (atlasCompleteMaximumChunkZ + 1) * (float)GlobalConstants.ChunkSize
        );
        bool applySurfaceFilter = hideUndergroundCaves
            && !DeveloperDisableCaveFilter
            && surfaceHeightTexture?.Ready == true
            && surfaceHeightTexture.TextureId > 0;
        activeLiquidShader.Uniform("atlasHideCaves", applySurfaceFilter ? 1 : 0);
        if (applySurfaceFilter && surfaceHeightTexture != null)
        {
            activeLiquidShader.BindTexture2D(
                "atlasSurfaceHeightTex",
                surfaceHeightTexture.TextureId,
                CaveFilterTextureUnit
            );
            activeLiquidShader.Uniform(
                "atlasSurfaceOriginXZ",
                (float)surfaceHeightTexture.OriginX,
                (float)surfaceHeightTexture.OriginZ
            );
            activeLiquidShader.Uniform(
                "atlasSurfaceSampleSize",
                (float)AtlasSurfaceHeightTexture.HorizontalSampleSize
            );
            activeLiquidShader.Uniform("atlasVisibleSubsurfaceDepth", VisibleSubsurfaceDepth);
        }
        bool applyMapLayer = mapLayerTexture?.Ready == true
            && mapLayerTexture.TextureId > 0
            && mapLayerTexture.Layer != AtlasMapLayer.TexturedTerrain;
        activeLiquidShader.Uniform("atlasLayerEnabled", applyMapLayer ? 1 : 0);
        if (applyMapLayer && mapLayerTexture != null)
        {
            activeLiquidShader.BindTexture2D(
                "atlasLayerTex",
                mapLayerTexture.TextureId,
                MapLayerTextureUnit
            );
            activeLiquidShader.Uniform(
                "atlasLayerOriginXZ",
                (float)mapLayerTexture.OriginX,
                (float)mapLayerTexture.OriginZ
            );
            activeLiquidShader.Uniform(
                "atlasLayerSampleSize",
                (float)AtlasMapLayerTexture.HorizontalSampleSize
            );
            activeLiquidShader.Uniform(
                "atlasLayerOpacity",
                Math.Clamp(mapLayerOpacity, 0f, 1f)
            );
            activeLiquidShader.Uniform(
                "atlasLayerContours",
                mapLayerTexture.ContoursEnabled ? 1 : 0
            );
        }
        activeLiquidShader.Uniform("atlasSunDirection", atlasSunDirection);
        activeLiquidShader.Uniform("atlasSunColor", atlasSunColor);
        activeLiquidShader.Uniform("atlasExposure", atlasExposure);
        activeLiquidShader.Uniform("atlasTextureMipBias", atlasTextureMipBias);
        int blockTexturePixels = capi.Settings.Int["textureSize"];
        if (blockTexturePixels <= 0) blockTexturePixels = 32;
        float atlasPixels = capi.BlockTextureAtlas.Size.Width;
        float blockTextureUv = blockTexturePixels / atlasPixels;
        activeLiquidShader.Uniform("blockTextureSize", blockTextureUv, blockTextureUv);
        activeLiquidShader.Uniform("textureAtlasSize", atlasPixels, atlasPixels);
        activeLiquidShader.Uniform(
            "waterStillCounter",
            waterStillCounter
        );
        activeLiquidShader.Uniform(
            "waterFlowCounter",
            waterFlowCounter
        );
        FrameBufferRef primaryFramebuffer = render.FrameBuffers[
            (int)EnumFrameBuffer.Primary
        ];
        if (primaryFramebuffer.DepthTextureId <= 0)
        {
            throw new InvalidOperationException(
                "The Primary opaque depth texture is unavailable for atlas liquid clipping."
            );
        }
        activeLiquidShader.BindTexture2D(
            "opaqueDepthTex",
            primaryFramebuffer.DepthTextureId,
            OpaqueDepthTextureUnit
        );

        MeshDataPoolManager[] managers = passes[(int)EnumChunkRenderPass.Liquid];
        long renderedTriangles = 0;
        long allocatedTriangles = 0;
        int activeManagers = 0;
        liquidChunkCompletion.Clear();
        allowedLiquidLocationCount = 0;
        rejectedLiquidLocationCount = 0;
        atlasLiquidAdapter = this;
        atlasLiquidVisibilityOverride = true;
        try
        {
            for (int index = 0; index < managers.Length && index < textureIds.Length; index++)
            {
                if (managers[index] == null) continue;
                activeManagers++;
                activeLiquidShader.BindTexture2D("terrainTex", textureIds[index], 0);
                managers[index].Render(cameraPosition, "origin", EnumFrustumCullMode.CullInstant);
                long usedVideoMemory = 0;
                long managerRenderedTriangles = 0;
                long managerAllocatedTriangles = 0;
                managers[index].GetStats(
                    ref usedVideoMemory,
                    ref managerRenderedTriangles,
                    ref managerAllocatedTriangles
                );
                renderedTriangles += managerRenderedTriangles;
                allocatedTriangles += managerAllocatedTriangles;
            }
        }
        finally
        {
            atlasLiquidVisibilityOverride = false;
            atlasLiquidAdapter = null;
        }
        activeLiquidShader.Stop();
        render.GLDepthMask(true);
        render.GlToggleBlend(false, EnumBlendMode.Standard);
        render.GlEnableCullFace();

        if (!loggedStableLiquidDiagnostics)
        {
            loggedStableLiquidDiagnostics = true;
            capi.Logger.Notification(
                "[ModernAtlas] Stable liquid draw: shader pass {0}, {1} atlas managers, {2} rendered triangles, {3} allocated triangles; {4} completed liquid mesh locations allowed and {5} rejected; texture detail reduction={6}x.",
                activeLiquidShader.PassId,
                activeManagers,
                renderedTriangles,
                allocatedTriangles,
                allowedLiquidLocationCount,
                rejectedLiquidLocationCount,
                1 << LastRenderedTextureDetailReduction
            );
        }
        }
        finally
        {
            renderState.RestoreCapturedState();
        }
    }

    private bool IsCompletedLiquidChunk(ModelDataPoolLocation location)
    {
        int chunkSize = GlobalConstants.ChunkSize;
        (int chunkX, int chunkY, int chunkZ) chunk = GetMeshChunk(location, chunkSize);
        if (liquidChunkCompletion.TryGetValue(chunk, out bool cached)) return cached;

        // Match liquids to a visible, completed terrain column rather than
        // demanding same-height terrain and a fully loaded 3x3 guard band.
        // Ocean floors may be several vertical chunks below their surface,
        // and the old guard band removed valid edge water before terrain.
        bool completed = visibleTerrainColumns.Contains((chunk.chunkX, chunk.chunkZ))
            && capi.World.BlockAccessor.GetChunk(chunk.chunkX, chunk.chunkY, chunk.chunkZ)
                is IClientChunk { LoadedFromServer: true };
        liquidChunkCompletion[chunk] = completed;
        if (completed) allowedLiquidLocationCount++;
        else rejectedLiquidLocationCount++;
        return completed;
    }

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : (value - divisor + 1) / divisor;

    private static (int X, int Y, int Z) GetMeshChunk(
        ModelDataPoolLocation location,
        int chunkSize
    ) =>
    (
        FloorDiv((int)Math.Floor(location.FrustumCullSphere.x), chunkSize),
        FloorDiv((int)Math.Floor(location.FrustumCullSphere.y), chunkSize),
        FloorDiv((int)Math.Floor(location.FrustumCullSphere.z), chunkSize)
    );

}
