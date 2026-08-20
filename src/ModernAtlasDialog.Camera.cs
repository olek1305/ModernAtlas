using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
namespace ModernAtlas;

/// <summary>
/// Atlas world rendering entry point plus camera state: pan, rotation, tilt,
/// zoom, terrain fitting and the scroll viewport clip.
/// </summary>
public sealed partial class ModernAtlasDialog
{
    private bool RenderLiveWorld(float deltaTime)
    {
        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        try
        {
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);

        preparingSurfaceFilter = !surfaceHeightTexture.Advance();
        preparingOreConcealment = SurvivalOreConcealmentEnabled
            && exactChunkRenderer != null
            && !exactChunkRenderer.AdvanceSurvivalOreConcealment();
        preparingVegetationMask = config.HideVegetation
            && exactChunkRenderer != null
            && !exactChunkRenderer.AdvanceVegetationMask();
        if (preparingSurfaceFilter
            || preparingOreConcealment
            || preparingVegetationMask)
        {
            ClearSurfacePreparationFrame();
            return false;
        }

        AtlasViewportBounds viewport = AtlasViewport;
        float aspect = viewport.Width / (float)Math.Max(1, viewport.Height);
        float farPlane = Math.Max(2000, GameViewDistance * 6);
        bool captureProjection = pendingScreenshotRequest;
        float verticalHalfExtent = zoom;
        float horizontalHalfExtent = zoom * aspect;
        if (captureProjection)
        {
            // Tile capture: cover the tile's world area with square world
            // pixels in Primary, then crop the sub-region on readback. In
            // scroll presentation the map viewport is taller than the window
            // aspect, so the vertical half-extent grows accordingly.
            verticalHalfExtent = zoom * tileScreenshot.ProjectionVerticalFactor;
            horizontalHalfExtent = verticalHalfExtent
                * (render.FrameWidth / (float)Math.Max(1, render.FrameHeight));
        }
        Mat4f.Ortho(
            projection,
            -horizontalHalfExtent,
            horizontalHalfExtent,
            -verticalHalfExtent,
            verticalHalfExtent,
            0.1f,
            farPlane
        );
        float yaw = yawDegrees * GameMath.DEG2RAD;
        float pitch = pitchDegrees * GameMath.DEG2RAD;
        DefaultShaderUniforms uniforms = capi.Render.ShaderUniforms;
        float animationOffset = !captureProjection
            && capi.IsSinglePlayer
            && capi.IsGamePaused
                ? atlasAnimationSeconds
                : 0;
        // Every tile must render identical wind, water and cloud counters so
        // the stitched image has no animated seams.
        float windWaveCounter = captureProjection
            ? screenshotFrozenWindWaveCounter
            : config.AnimationsEnabled
                ? uniforms.WindWaveCounter + animationOffset
                : frozenWindWaveCounter;
        float windWaveCounterHighFrequency = captureProjection
            ? screenshotFrozenWindWaveCounterHighFrequency
            : config.AnimationsEnabled
                ? uniforms.WindWaveCounterHighFreq + animationOffset
                : frozenWindWaveCounterHighFrequency;
        float waterStillCounter = captureProjection
            ? screenshotFrozenWaterStillCounter
            : config.AnimationsEnabled
                ? uniforms.WaterStillCounter + animationOffset
                : frozenWaterStillCounter;
        float waterFlowCounter = captureProjection
            ? screenshotFrozenWaterFlowCounter
            : config.AnimationsEnabled
                ? uniforms.WaterFlowCounter + animationOffset
                : frozenWaterFlowCounter;
        float renderAnimationOffset = !captureProjection
            && config.AnimationsEnabled
            && capi.IsSinglePlayer
            && capi.IsGamePaused
                ? atlasRealDeltaTime
                : 0;

        // Only the tiled PNG capture hides the local player's own model so the
        // saved photo contains no photographer. Interactive Screenshot Preview
        // preserves exactly the living models visible on the atlas.
        if (exactChunkRenderer != null)
        {
            exactChunkRenderer.HideLocalPlayerModel = captureProjection;
        }
        bool rendered;
        try
        {
            rendered = exactChunkRenderer?.Render(
                deltaTime,
                projection,
                centerX,
                centerY,
                centerZ,
                yaw,
                pitch,
                GameViewDistance,
                SurfaceSafetyEnabled,
                SurvivalOreConcealmentEnabled,
                surfaceHeightTexture,
                mapLayerTexture,
                EffectiveMapLayerOpacity,
                0,
                config.PerformanceLightingEnabled,
                config.HideVegetation,
                config.AnimationsEnabled,
                Math.Clamp(config.AtlasExposurePercent, 50, 150) / 100f,
                Math.Clamp(config.CaveMaskBrightnessPercent, 50, 150) / 100f,
                windWaveCounter,
                windWaveCounterHighFrequency,
                waterStillCounter,
                waterFlowCounter,
                config.CloudsEnabled,
                config.LiveLightingEnabled,
                config.FixedSunHour,
                renderAnimationOffset,
                captureProjection ? screenshotFrozenCloudOffset : null,
                visibleEntityPolicy,
                false
            ) == true;
        }
        finally
        {
            if (exactChunkRenderer != null)
            {
                exactChunkRenderer.HideLocalPlayerModel = false;
            }
        }
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
        return rendered;
        }
        finally
        {
            renderState.RestoreGuiHandoff();
        }
    }

    private void ResetView()
    {
        CenterOnPlayer();
        targetYawDegrees = 42;
        if (!config.CameraAngleLocked || !CreativeCheatSettingsAvailable)
        {
            targetPitchDegrees = 72;
        }
        FitLoadedTerrain();
    }

    private void CenterOnPlayer()
    {
        targetCenterX = capi.World.Player.Entity.Pos.X;
        targetCenterZ = capi.World.Player.Entity.Pos.Z;
        centerY = capi.World.Player.Entity.Pos.Y;
        FocusOnExteriorSurface();
    }

    private void Pan(double x, double z, float amount, KeyEvent args)
    {
        targetCenterX += x * amount;
        targetCenterZ += z * amount;
        args.Handled = true;
    }

    private void ApplyRotationDrag(double deltaX, double deltaY)
    {
        targetYawDegrees = NormalizeDegrees(targetYawDegrees + (float)deltaX * 0.42f);
        if (!config.CameraAngleLocked || !CreativeCheatSettingsAvailable)
        {
            targetPitchDegrees = Math.Clamp(
                targetPitchDegrees - (float)deltaY * 0.32f,
                MinimumPitchDegrees,
                86
            );
        }
    }

    private void ResetPointerDrag()
    {
        leftDragging = false;
        rightDragging = false;
        leftDragDistance = 0;
    }

    private void PrepareSurfaceSafetyFilter()
    {
        preparedGameViewDistance = GameViewDistance;
        double playerX = capi.World.Player.Entity.Pos.X;
        double playerZ = capi.World.Player.Entity.Pos.Z;
        if (surfaceHeightTexture.CoversArea(
                playerX,
                playerZ,
                GameViewDistance
            ))
        {
            // This is transient world data, not a persistent terrain cache.
            // Keep it across G close/open while it still covers the required
            // loaded-world footprint; rebuilding it here creates a blank
            // preparation interval on every second atlas open.
            preparingSurfaceFilter = false;
            return;
        }

        preparingSurfaceFilter = true;

        surfaceHeightTexture.Begin(
            playerX,
            playerZ,
            GameViewDistance
        );
    }

    private void ClearSurfacePreparationFrame()
    {
        // Draw an opaque Primary frame while the small surface texture is
        // prepared. Clearing the default framebuffer directly can leave the
        // native Linux window transparent after the atlas closes.
        exactChunkRenderer?.RenderSurfacePreparationFrame(!config.RenderOnScroll);
    }

    private void FitLoadedTerrain()
    {
        float radius = GameViewDistance;
        AtlasViewportBounds viewport = AtlasViewport;
        float aspect = viewport.Width / (float)Math.Max(1, viewport.Height);
        float yaw = targetYawDegrees * GameMath.DEG2RAD;
        float pitch = targetPitchDegrees * GameMath.DEG2RAD;

        // The engine's completed chunks rarely form a perfect circle while it
        // is streaming. Measure only client-loaded columns and project every
        // chunk corner through the selected yaw, so an asymmetric or diagonal
        // edge is still completely visible after reset or opening.
        bool measuredLoadedTerrain = TryMeasureLoadedTerrainFootprint(
            yaw,
            out float horizontalExtent,
            out float forwardExtent
        );
        if (!measuredLoadedTerrain)
        {
            horizontalExtent = radius;
            forwardExtent = radius;
        }

        // Add vertical headroom for trees, buildings and hills so neither the
        // top nor bottom edge is clipped at low camera angles.
        float horizontalFit = horizontalExtent / Math.Max(0.5f, aspect);
        float verticalFit = forwardExtent * Math.Abs(MathF.Sin(pitch))
            + Math.Min(192, radius * 0.3f) * Math.Abs(MathF.Cos(pitch));
        targetZoom = Math.Clamp(Math.Max(horizontalFit, verticalFit) * 1.08f, 80, 30000);
    }

    private bool TryMeasureLoadedTerrainFootprint(
        float yaw,
        out float horizontalExtent,
        out float forwardExtent
    )
    {
        horizontalExtent = 0;
        forwardExtent = 0;

        int chunkSize = GlobalConstants.ChunkSize;
        int radius = GameViewDistance;
        int minimumChunkX = (int)Math.Floor(
            (capi.World.Player.Entity.Pos.X - radius) / chunkSize
        );
        int maximumChunkX = (int)Math.Floor(
            (capi.World.Player.Entity.Pos.X + radius) / chunkSize
        );
        int minimumChunkZ = (int)Math.Floor(
            (capi.World.Player.Entity.Pos.Z - radius) / chunkSize
        );
        int maximumChunkZ = (int)Math.Floor(
            (capi.World.Player.Entity.Pos.Z + radius) / chunkSize
        );
        int verticalChunkCount = Math.Max(
            1,
            (capi.World.BlockAccessor.MapSizeY + chunkSize - 1) / chunkSize
        );
        double originX = targetCenterX;
        double originZ = targetCenterZ;
        float sinYaw = MathF.Sin(yaw);
        float cosYaw = MathF.Cos(yaw);
        float measuredHorizontalExtent = 0;
        float measuredForwardExtent = 0;
        bool found = false;

        IReadOnlyCollection<(int X, int Z)> completedColumns = exactChunkRenderer
            ?.CompletedTerrainColumns ?? Array.Empty<(int X, int Z)>();
        foreach ((int X, int Z) column in completedColumns)
        {
            if (column.X < minimumChunkX || column.X > maximumChunkX
                || column.Z < minimumChunkZ || column.Z > maximumChunkZ)
            {
                continue;
            }
            MeasureChunk(column.X, column.Z);
        }
        if (found)
        {
            horizontalExtent = measuredHorizontalExtent;
            forwardExtent = measuredForwardExtent;
            return true;
        }

        // Before the first atlas frame there is no completed-mesh snapshot.
        // Fall back to loaded columns only for that initial fit. Later resets
        // use actual GPU mesh coverage so a view-distance increase cannot zoom
        // out to thousands of queued, not-yet-renderable chunks.
        for (int chunkZ = minimumChunkZ; chunkZ <= maximumChunkZ; chunkZ++)
        {
            for (int chunkX = minimumChunkX; chunkX <= maximumChunkX; chunkX++)
            {
                bool loaded = false;
                for (int chunkY = 0; chunkY < verticalChunkCount; chunkY++)
                {
                    if (capi.World.BlockAccessor.GetChunk(chunkX, chunkY, chunkZ)
                        is IClientChunk { LoadedFromServer: true })
                    {
                        loaded = true;
                        break;
                    }
                }
                if (!loaded) continue;

                MeasureChunk(chunkX, chunkZ);
            }
        }

        horizontalExtent = measuredHorizontalExtent;
        forwardExtent = measuredForwardExtent;
        return found;

        void MeasureChunk(int chunkX, int chunkZ)
        {
            found = true;
            double minimumX = chunkX * (double)chunkSize - originX;
            double maximumX = minimumX + chunkSize;
            double minimumZ = chunkZ * (double)chunkSize - originZ;
            double maximumZ = minimumZ + chunkSize;
            MeasureProjectedCorner(minimumX, minimumZ);
            MeasureProjectedCorner(minimumX, maximumZ);
            MeasureProjectedCorner(maximumX, minimumZ);
            MeasureProjectedCorner(maximumX, maximumZ);
        }

        void MeasureProjectedCorner(double x, double z)
        {
            float right = (float)(x * cosYaw - z * sinYaw);
            float forward = (float)(x * sinYaw + z * cosYaw);
            measuredHorizontalExtent = Math.Max(measuredHorizontalExtent, Math.Abs(right));
            measuredForwardExtent = Math.Max(measuredForwardExtent, Math.Abs(forward));
        }
    }

    private void FocusOnExteriorSurface()
    {
        int x = (int)Math.Floor(targetCenterX);
        int z = (int)Math.Floor(targetCenterZ);
        BlockPos position = new(x, 0, z);
        if (capi.World.BlockAccessor.GetMapChunkAtBlockPos(position) == null) return;

        int surfaceY = capi.World.BlockAccessor.GetRainMapHeightAt(position);
        if (surfaceY > 0)
        {
            centerY = surfaceY + 0.5;
        }
    }

    private void AdvancePausedAnimation()
    {
        long now = capi.ElapsedMilliseconds;
        float realDeltaTime = Math.Clamp(
            (now - lastAtlasFrameMilliseconds) / 1000f,
            0,
            0.25f
        );
        atlasRealDeltaTime = realDeltaTime;
        lastAtlasFrameMilliseconds = now;

        if (config.AnimationsEnabled && capi.IsSinglePlayer && capi.IsGamePaused)
        {
            atlasAnimationSeconds += realDeltaTime;
        }
    }

    private void AdvanceCamera(float realDeltaTime)
    {
        float blend = 1f - MathF.Exp(-14f * Math.Clamp(realDeltaTime, 0, 0.1f));
        if (blend <= 0) return;

        double oldCenterX = centerX;
        double oldCenterZ = centerZ;
        float oldYaw = yawDegrees;
        float oldPitch = pitchDegrees;
        float oldZoom = zoom;

        centerX += (targetCenterX - centerX) * blend;
        centerZ += (targetCenterZ - centerZ) * blend;
        float yawDelta = NormalizeSignedDegrees(targetYawDegrees - yawDegrees);
        yawDegrees = NormalizeDegrees(yawDegrees + yawDelta * blend);
        pitchDegrees += (targetPitchDegrees - pitchDegrees) * blend;
        zoom += (targetZoom - zoom) * blend;

        if (Math.Abs(targetCenterX - centerX) < 0.001) centerX = targetCenterX;
        if (Math.Abs(targetCenterZ - centerZ) < 0.001) centerZ = targetCenterZ;
        if (Math.Abs(NormalizeSignedDegrees(targetYawDegrees - yawDegrees)) < 0.001f)
        {
            yawDegrees = targetYawDegrees;
        }
        if (Math.Abs(targetPitchDegrees - pitchDegrees) < 0.001f) pitchDegrees = targetPitchDegrees;
        if (Math.Abs(targetZoom - zoom) < 0.001f) zoom = targetZoom;

        if (Math.Abs(centerX - oldCenterX) > 0.0001
            || Math.Abs(centerZ - oldCenterZ) > 0.0001
            || Math.Abs(NormalizeSignedDegrees(yawDegrees - oldYaw)) > 0.0001f
            || Math.Abs(pitchDegrees - oldPitch) > 0.0001f
            || Math.Abs(zoom - oldZoom) > 0.0001f)
        {
        }
    }

    private void CaptureAnimationFrame()
    {
        DefaultShaderUniforms uniforms = capi.Render.ShaderUniforms;
        float animationOffset = capi.IsSinglePlayer && capi.IsGamePaused
            ? atlasAnimationSeconds
            : 0;
        frozenWindWaveCounter = uniforms.WindWaveCounter + animationOffset;
        frozenWindWaveCounterHighFrequency = uniforms.WindWaveCounterHighFreq + animationOffset;
        frozenWaterStillCounter = uniforms.WaterStillCounter + animationOffset;
        frozenWaterFlowCounter = uniforms.WaterFlowCounter + animationOffset;
    }

    private bool BeginScrollContentClip()
    {
        if (!config.RenderOnScroll) return false;

        AtlasViewportBounds clip = AtlasViewport.Inset(2);
        capi.Render.GlScissor(
            clip.X,
            Math.Max(0, capi.Render.FrameHeight - clip.Bottom),
            clip.Width,
            clip.Height
        );
        capi.Render.GlScissorFlag(true);
        return true;
    }

    private void EndScrollContentClip(bool clipped)
    {
        if (clipped) capi.Render.GlScissorFlag(false);
    }

    private void ForceOpaqueWindowAlpha()
    {
        IShaderProgram? shader = atlasOpacityShaderProvider();
        if (shader == null || shader.Disposed) return;

        opacityQuad ??= capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        try
        {
            render.CurrentFrameBuffer = null;
            render.CurrentActiveShader?.Stop();
            render.GLDisableDepthTest();
            render.GLDepthMask(false);
            render.GlToggleBlend(false, EnumBlendMode.Standard);
            render.GlColorMask(false, false, false, true);
            shader.Use();
            render.RenderMesh(opacityQuad);
        }
        finally
        {
            renderState.RestoreGuiHandoff(true);
            try
            {
                capi.Render.GetEngineShader(EnumShaderProgram.Gui).Use();
            }
            catch
            {
                // The GUI shader may already be unavailable during teardown.
            }
        }
    }

    private void Rotate(float degrees, KeyEvent args)
    {
        targetYawDegrees = NormalizeDegrees(targetYawDegrees + degrees);
        args.Handled = true;
    }

    private static float NormalizeDegrees(float value)
    {
        value %= 360;
        return value < 0 ? value + 360 : value;
    }

    private static float NormalizeSignedDegrees(float value)
    {
        value = NormalizeDegrees(value);
        return value > 180 ? value - 360 : value;
    }
}
