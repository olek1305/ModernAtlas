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
/// Screenshot settings callbacks and the tiled camera-grid capture: filter
/// values, capture start, per-tile camera, progress modal and status text.
/// </summary>
public sealed partial class ModernAtlasDialog
{
    private void OnScreenshotScaleChanged(string value, bool selected)
    {
        if (!selected) return;
        if (int.TryParse(value, out int scale))
        {
            config.ScreenshotScale = Math.Clamp(
                scale,
                AtlasTiledScreenshot.MinimumResolutionScale,
                AtlasTiledScreenshot.MaximumResolutionScale
            );
            saveConfig();
            if (screenshotPreviewOpen)
            {
                pendingScreenshotPreviewRecompose = true;
            }
        }
        SyncScreenshotSettingsControls();
    }

    private void OnScreenshotCaptureAreaChanged(string value, bool selected)
    {
        if (!selected) return;
        if (int.TryParse(value, out int captureAreaPercent))
        {
            config.ScreenshotCaptureAreaPercent =
                AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
                    captureAreaPercent
                );
            saveConfig();
            if (screenshotPreviewOpen)
            {
                pendingScreenshotPreviewRecompose = true;
            }
        }
        SyncScreenshotSettingsControls();
    }

    private void OnScreenshotFilterPresetChanged(string value, bool selected)
    {
        if (!selected) return;
        AtlasScreenshotFilterSettings.ApplyPresetToConfig(config, value);
        saveConfig();
        if (screenshotPreviewOpen)
        {
            screenshotPreviewFilterDirty = true;
        }
        if (ScreenshotFilterTuningOpen && !ScreenshotFilterUsesCustomPreset)
        {
            RequestBottomPanel(AtlasPanelSection.ScreenshotOptions);
        }
        else if (ScreenshotOptionsOpen)
        {
            // The Filter tuning control is conditional on Custom. Recompose
            // after the choice event so the stale button cannot remain visible
            // for the rest of the session.
            pendingScreenshotPanelRecompose = true;
        }
        SyncScreenshotSettingsControls();
    }

    private void OnScreenshotFiltersToggled(bool enabled)
    {
        if (enabled)
        {
            string preset = AtlasScreenshotFilterSettings.NormalizePreset(
                config.ScreenshotFilterPreset
            );
            if (preset == "off") preset = "natural";
            if (preset == "custom")
            {
                config.ScreenshotFiltersEnabled = true;
                config.ScreenshotFilterPreset = "custom";
            }
            else
            {
                AtlasScreenshotFilterSettings.ApplyPresetToConfig(config, preset);
            }
        }
        else
        {
            config.ScreenshotFiltersEnabled = false;
        }
        saveConfig();
        if (screenshotPreviewOpen)
        {
            screenshotPreviewFilterDirty = true;
        }
        SyncScreenshotSettingsControls();
    }

    private bool ResetScreenshotFilterToDefault()
    {
        // The product default is an unfiltered PNG. Keep the Natural numeric
        // baseline in the saved preferences so enabling the filter again
        // starts from a predictable value instead of stale Custom sliders.
        AtlasScreenshotFilterSettings.ApplyPresetToConfig(config, "natural");
        config.ScreenshotFilterPreset = "off";
        config.ScreenshotFiltersEnabled = false;
        saveConfig();

        screenshotStatusOverride =
            "Screenshot filter reset to Off (default).";
        screenshotStatusShownUntilMilliseconds = capi.ElapsedMilliseconds + 4000;
        if (screenshotPreviewOpen)
        {
            screenshotPreviewFilterDirty = true;
            screenshotPreviewFilterBlocked = false;
            screenshotPreviewFilterDiagnostic =
                "DEFAULT RESTORED  ·  Filter Off; BEFORE and AFTER are identical.";
        }
        SyncScreenshotSettingsControls();
        return true;
    }

    private bool SetCustomScreenshotFilterValue(Action setter)
    {
        config.ScreenshotFiltersEnabled = true;
        config.ScreenshotFilterPreset = "custom";
        setter();
        saveConfig();
        if (screenshotPreviewOpen)
        {
            screenshotPreviewFilterDirty = true;
        }
        SyncScreenshotSettingsControls();
        return true;
    }

    private bool OnScreenshotFilterIntensityChanged(int value) =>
        SetCustomScreenshotFilterValue(
            () => config.ScreenshotFilterIntensityPercent = Math.Clamp(value, 0, 200)
        );

    private bool OnScreenshotSaturationChanged(int value) =>
        SetCustomScreenshotFilterValue(
            () => config.ScreenshotSaturationPercent = Math.Clamp(value, 0, 200)
        );

    private bool OnScreenshotContrastChanged(int value) =>
        SetCustomScreenshotFilterValue(
            () => config.ScreenshotContrastPercent = Math.Clamp(value, 0, 200)
        );

    private bool OnScreenshotTemperatureChanged(int value) =>
        SetCustomScreenshotFilterValue(
            () => config.ScreenshotTemperaturePercent = Math.Clamp(value, -100, 100)
        );

    private bool OnScreenshotShadowTintChanged(int value) =>
        SetCustomScreenshotFilterValue(
            () => config.ScreenshotShadowTintStrengthPercent = Math.Clamp(value, 0, 100)
        );

    private bool OnScreenshotAmbientOcclusionChanged(int value) =>
        SetCustomScreenshotFilterValue(
            () => config.ScreenshotAmbientOcclusionPercent = Math.Clamp(value, 0, 100)
        );

    private bool OnScreenshotIndirectLightChanged(int value) =>
        SetCustomScreenshotFilterValue(
            () => config.ScreenshotIndirectLightPercent = Math.Clamp(value, 0, 100)
        );

    private bool OnScreenshotBloomChanged(int value) =>
        SetCustomScreenshotFilterValue(
            () => config.ScreenshotBloomPercent = Math.Clamp(value, 0, 100)
        );

    private bool OnScreenshotRedChanged(int value) =>
        SetCustomScreenshotFilterValue(
            () => config.ScreenshotRedBalancePercent = Math.Clamp(value, 50, 150)
        );

    private bool OnScreenshotGreenChanged(int value) =>
        SetCustomScreenshotFilterValue(
            () => config.ScreenshotGreenBalancePercent = Math.Clamp(value, 50, 150)
        );

    private bool OnScreenshotBlueChanged(int value) =>
        SetCustomScreenshotFilterValue(
            () => config.ScreenshotBlueBalancePercent = Math.Clamp(value, 50, 150)
        );

    private string BuildScreenshotPreviewText()
    {
        int resolutionScale = Math.Clamp(
            config.ScreenshotScale,
            AtlasTiledScreenshot.MinimumResolutionScale,
            AtlasTiledScreenshot.MaximumResolutionScale
        );
        int captureAreaPercent =
            AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
                config.ScreenshotCaptureAreaPercent
            );
        AtlasViewportBounds viewport = AtlasViewport;
        AtlasScreenshotPreview preview = tileScreenshot.GetPreview(
            resolutionScale,
            captureAreaPercent,
            viewport.Width / (float)Math.Max(1, viewport.Height)
        );
        if (!preview.IsValid)
        {
            return "PNG preview unavailable: the game window is too small.";
        }

        string firstLine =
            $"PNG: {preview.OutputWidth} × {preview.OutputHeight} — {preview.OutputMegapixels:0.0} MP";
        string detailLine = preview.WasDownsampled
            ? $"Detail: {preview.EffectiveDetailFactor:0.#}× effective ({preview.RequestedDetailFactor:0.#}× requested) | Area: {captureAreaPercent}%"
            : $"Detail: {preview.RequestedDetailFactor:0.#}× | Area: {captureAreaPercent}% | Grid: {resolutionScale}×{resolutionScale}";
        if (!preview.WasDownsampled)
        {
            return $"{firstLine}\n{detailLine}";
        }

        return $"{firstLine}\n{detailLine}\nWARNING: requested {preview.RequestedWidth} × {preview.RequestedHeight} ({preview.RequestedMegapixels:0.0} MP) exceeds the {AtlasTiledScreenshot.MaximumStitchedPixels / 1_000_000d:0} MP safety limit; the PNG will be reduced.";
    }

    /// <summary>
    /// Starts the tiled capture of the current camera view. Tile zero's
    /// camera is applied immediately; every following tile is applied after
    /// the previous tile's frame was read back.
    /// </summary>
    private bool TakeScreenshot() => StartScreenshotCapture(
        Math.Clamp(
            config.ScreenshotScale,
            AtlasTiledScreenshot.MinimumResolutionScale,
            AtlasTiledScreenshot.MaximumResolutionScale
        ),
        AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
            config.ScreenshotCaptureAreaPercent
        ),
        "Screenshot"
    );

    private bool TakeQuickScreenshot() => StartScreenshotCapture(1, 100, "Quick screenshot");

    private bool StartScreenshotCapture(
        int gridSize,
        int captureAreaPercent,
        string captureLabel
    )
    {
        if (tileScreenshot.Busy)
        {
            screenshotStatusOverride = "Previous screenshot is still being processed…";
            screenshotStatusShownUntilMilliseconds = capi.ElapsedMilliseconds + 4000;
            return true;
        }
        if (atlasFrameCacheTexture?.TextureId <= 0)
        {
            screenshotStatusOverride = "Atlas frame is not ready yet.";
            screenshotStatusShownUntilMilliseconds = capi.ElapsedMilliseconds + 4000;
            return true;
        }

        gridSize = Math.Clamp(
            gridSize,
            AtlasTiledScreenshot.MinimumResolutionScale,
            AtlasTiledScreenshot.MaximumResolutionScale
        );
        captureAreaPercent = AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
            captureAreaPercent
        );
        AtlasViewportBounds viewport = AtlasViewport;
        float viewportAspect = viewport.Width / (float)Math.Max(1, viewport.Height);
        if (!TryPrepareEffectiveScreenshotFilterSettings(
                AtlasScreenshotFilterSettings.FromConfig(config),
                out AtlasScreenshotFilterSettings frozenFilterSettings,
                out AtlasScreenshotFilterMode frozenFilterMode,
                out string filterPreparationDiagnostic
            ))
        {
            screenshotStatusOverride =
                $"Filtered screenshot unavailable: {filterPreparationDiagnostic}";
            screenshotStatusShownUntilMilliseconds =
                capi.ElapsedMilliseconds + 6000;
            capi.Logger.Error(
                "[ModernAtlas] Refused filtered screenshot job before tile 0: {0}",
                filterPreparationDiagnostic
            );
            return true;
        }
        if (frozenFilterMode == AtlasScreenshotFilterMode.Filtered)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Screenshot filter preflight passed: preset={0}, mode=Filtered, spatial={1}, validityMask={2}.",
                frozenFilterSettings.Preset,
                screenshotFilterPass.UsesSpatialEffects(frozenFilterSettings),
                exactChunkRenderer?.BoundaryValidityTextureId > 0
            );
        }
        if (!tileScreenshot.StartCapture(
            gridSize,
            captureAreaPercent,
            zoom,
            yawDegrees,
            pitchDegrees,
            viewportAspect,
            frozenFilterSettings,
            frozenFilterMode
        ))
        {
            screenshotStatusOverride = "Screenshot failed to start; see the log for details.";
            screenshotStatusShownUntilMilliseconds = capi.ElapsedMilliseconds + 4000;
            return true;
        }

        screenshotBaselineZoom = zoom;
        screenshotBaselineTargetZoom = targetZoom;
        screenshotBaselineCenterX = centerX;
        screenshotBaselineCenterY = centerY;
        screenshotBaselineCenterZ = centerZ;
        screenshotBaselineTargetCenterX = targetCenterX;
        screenshotBaselineTargetCenterZ = targetCenterZ;
        screenshotCameraSnapshotted = true;

        // Pin wind, water and cloud counters to one captured frame so every
        // tile renders identical animated surfaces and the stitched image
        // has no moving-water, drifting-cloud or waving-vegetation seams.
        CaptureAnimationFrame();
        screenshotFrozenWindWaveCounter = frozenWindWaveCounter;
        screenshotFrozenWindWaveCounterHighFrequency =
            frozenWindWaveCounterHighFrequency;
        screenshotFrozenWaterStillCounter = frozenWaterStillCounter;
        screenshotFrozenWaterFlowCounter = frozenWaterFlowCounter;
        screenshotFrozenCloudOffset = exactChunkRenderer?.GetLiveCloudOffset();

        pendingScreenshotRequest = true;
        ApplyTileCamera(0);
        // Force a fresh world frame for every tile instead of the throttled
        // idle interval.
        lastAtlasWorldRenderMilliseconds = 0;
        OpenScreenshotProgressModal();
        soundController.PlayPageTouch();
        capi.Logger.Notification(
            "[ModernAtlas] Queued {3}: {0}x{0} tiled atlas screenshot of the centered {1}% area: {2}.",
            gridSize,
            captureAreaPercent,
            BuildScreenshotPreviewText().Replace('\n', ' '),
            captureLabel
        );
        return true;
    }

    /// <summary>
    /// Runs once per rendered atlas frame while the tile grid is captured.
    /// Reads the Primary sub-region for the just-rendered tile, then snaps
    /// the camera to the next tile or restores the original view after the
    /// last one.
    /// </summary>
    private void AdvanceTileScreenshot()
    {
        FrameBufferRef? resolvedFramebuffer = exactChunkRenderer?.ResolvedFramebuffer;
        bool filteredJob = tileScreenshot.ActiveFilterMode
            == AtlasScreenshotFilterMode.Filtered;
        bool requiresValidityMask = filteredJob
            && screenshotFilterPass.RequiresValidityMask(
                tileScreenshot.ActiveFilterSettings
            );
        bool shouldReadValidityMask = automatedScreenshotMaskDiagnosticsEnabled
            && (!filteredJob || requiresValidityMask);
        int currentTile = tileScreenshot.CurrentTile;
        if (shouldReadValidityMask
            && resolvedFramebuffer != null
            && exactChunkRenderer != null
            && exactChunkRenderer.TryReadBoundaryValidityMask(
                out byte[] validityMask,
                out AtlasValidityMaskDiagnostics validityDiagnostics
            ))
        {
            tileScreenshot.RecordValidityMaskDiagnostics(validityDiagnostics);
            if (requiresValidityMask && validityDiagnostics.ValidPixelCount > 0
                && !automatedScreenshotMaskCoverageFailure)
            {
                automatedScreenshotMaskValidationPassed = true;
            }
            if (currentTile == 0 || filteredJob)
            {
                capi.Logger.Notification(
                    "[ModernAtlas] Screenshot validity mask tile {0}: {1}",
                    currentTile,
                    validityDiagnostics
                );
            }
            SaveScreenshotValidityMaskDebug(
                currentTile,
                validityMask,
                filteredJob,
                filteredJob && validityDiagnostics.ValidPixelCount > 0
            );
        }
        else if (shouldReadValidityMask && requiresValidityMask)
        {
            automatedScreenshotMaskCoverageFailure = true;
            automatedScreenshotMaskValidationPassed = false;
            capi.Logger.Error(
                "[ModernAtlas] Filtered screenshot tile {0} has no readable validity mask diagnostic.",
                currentTile
            );
        }

        FrameBufferRef? captureFramebuffer = resolvedFramebuffer;
        if (filteredJob)
        {
            if (resolvedFramebuffer == null || exactChunkRenderer == null)
            {
                tileScreenshot.FailCapture(
                    $"Filtered screenshot tile {currentTile + 1} has no resolved atlas framebuffer."
                );
            }
            else if (!exactChunkRenderer.TryGetLastAtlasCamera(
                out float[] filterProjection,
                out double[] filterView
            ))
            {
                tileScreenshot.FailCapture(
                    $"Filtered screenshot tile {currentTile + 1} has no stable atlas camera matrices."
                );
            }
            else
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                captureFramebuffer = screenshotFilterPass.Apply(
                    resolvedFramebuffer,
                    exactChunkRenderer.PrimaryDepthTextureId,
                    exactChunkRenderer.BoundaryValidityTextureId,
                    tileScreenshot.ActiveFilterSettings,
                    filterProjection,
                    filterView,
                    tileScreenshot.FilterOverlapMargin
                );
                stopwatch.Stop();
                if (currentTile == 0)
                {
                    capi.Logger.Notification(
                        "[ModernAtlas] Screenshot filter timing: first tile took {0:0.##} ms (preset={1}, mode=Filtered).",
                        stopwatch.Elapsed.TotalMilliseconds,
                        tileScreenshot.ActiveFilterSettings.Preset
                    );
                }
                if (captureFramebuffer == null)
                {
                    tileScreenshot.FailCapture(
                        $"Filtered screenshot tile {currentTile + 1} failed: {screenshotFilterPass.LastError ?? "unknown filter error"}"
                    );
                }
                else if (automatedScreenshotSequenceEnabled
                    || automatedScreenshotIntensityZeroEnabled)
                {
                    if (!screenshotFilterPass.TryCompareReadback(
                            resolvedFramebuffer,
                            captureFramebuffer,
                            out AtlasScreenshotFilterReadbackDiagnostics readbackDiagnostics,
                            out string readbackError
                        ))
                    {
                        automatedScreenshotFilterReadbackFailure = true;
                        automatedScreenshotSequenceFailed = true;
                        tileScreenshot.FailCapture(
                            $"Filtered screenshot tile {currentTile + 1} readback proof failed: {readbackError}"
                        );
                    }
                    else
                    {
                        automatedScreenshotFilterComparedTileCount++;
                        automatedScreenshotFilterChangedPixelCount +=
                            readbackDiagnostics.ChangedPixelCount;
                        automatedScreenshotFilterComparedPixelCount +=
                            readbackDiagnostics.TotalPixelCount;
                        automatedScreenshotFilterMinimumChangedRatio = Math.Min(
                            automatedScreenshotFilterMinimumChangedRatio,
                            readbackDiagnostics.ChangedRatio
                        );
                        automatedScreenshotFilterMaximumChangedRatio = Math.Max(
                            automatedScreenshotFilterMaximumChangedRatio,
                            readbackDiagnostics.ChangedRatio
                        );
                        automatedScreenshotFilterMaximumChannelDelta = Math.Max(
                            automatedScreenshotFilterMaximumChannelDelta,
                            readbackDiagnostics.MaximumChannelDelta
                        );
                        if (automatedScreenshotIntensityZeroEnabled
                            && !readbackDiagnostics.IsExactRgb)
                        {
                            automatedScreenshotFilterReadbackFailure = true;
                            automatedScreenshotSequenceFailed = true;
                            tileScreenshot.FailCapture(
                                $"Intensity=0 changed {readbackDiagnostics.ChangedPixelCount} RGB pixels on screenshot tile {currentTile + 1}."
                            );
                        }
                    }
                }
            }
        }

        if (captureFramebuffer == null
            || !tileScreenshot.CaptureCurrentFrame(captureFramebuffer, filteredJob))
        {
            string failure = tileScreenshot.LastError
                ?? (filteredJob
                    ? $"Filtered screenshot tile {currentTile + 1} could not be captured."
                    : "Screenshot readback failed.");
            tileScreenshot.Cancel();
            RestoreScreenshotCamera();
            pendingScreenshotRequest = false;
            screenshotStatusOverride = failure;
            screenshotStatusShownUntilMilliseconds = capi.ElapsedMilliseconds + 6000;
            CloseScreenshotProgressModal();
            return;
        }

        if (tileScreenshot.CaptureActive)
        {
            ApplyTileCamera(tileScreenshot.CurrentTile);
            lastAtlasWorldRenderMilliseconds = 0;
            return;
        }

        // The last tile is stored; the background stitcher now assembles the
        // image while the camera returns to the player's original view.
        RestoreScreenshotCamera();
        pendingScreenshotRequest = false;
        capi.Logger.Notification(
            "[ModernAtlas] Captured {0} atlas tiles at {1}; stitching in the background.",
            tileScreenshot.TotalTiles,
            tileScreenshot.PendingPath
        );
    }

    private void ApplyTileCamera(int tileIndex)
    {
        tileScreenshot.GetTileCamera(
            tileIndex,
            out float tileZoom,
            out double offsetX,
            out double offsetZ,
            out double offsetY
        );
        zoom = tileZoom;
        targetZoom = tileZoom;
        centerX = screenshotBaselineCenterX + offsetX;
        centerY = screenshotBaselineCenterY + offsetY;
        centerZ = screenshotBaselineCenterZ + offsetZ;
        targetCenterX = centerX;
        targetCenterZ = centerZ;
    }

    private void RestoreScreenshotCamera()
    {
        if (screenshotCameraSnapshotted)
        {
            screenshotCameraSnapshotted = false;
            zoom = screenshotBaselineZoom;
            targetZoom = screenshotBaselineTargetZoom;
            centerX = screenshotBaselineCenterX;
            centerY = screenshotBaselineCenterY;
            centerZ = screenshotBaselineCenterZ;
            targetCenterX = screenshotBaselineTargetCenterX;
            targetCenterZ = screenshotBaselineTargetCenterZ;
        }
        RestoreAutomatedScreenshotPitch();
    }

    private void RestoreAutomatedScreenshotPitch()
    {
        if (!automatedScreenshotPitchSnapshotted) return;
        pitchDegrees = automatedScreenshotPitchBeforeCapture;
        targetPitchDegrees = automatedScreenshotTargetPitchBeforeCapture;
        automatedScreenshotPitchSnapshotted = false;
    }

    private void CancelScreenshotCapture()
    {
        if (tileScreenshot.Busy)
        {
            tileScreenshot.Cancel();
        }
        RestoreScreenshotCamera();
        pendingScreenshotRequest = false;
        CloseScreenshotProgressModal();
    }

    private void OpenScreenshotProgressModal()
    {
        screenshotProgressModal?.Dispose();
        ElementBounds root = ElementBounds.Fixed(0, 0, 420, 190)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        screenshotProgressModal = capi.Gui.CreateCompo(
                "modernatlas-screenshot-progress",
                root
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(0, 0, 420, 190),
                AtlasUiStyle.DrawCard
            )
            .AddStaticText(
                "SCREENSHOT",
                AtlasUiStyle.TitleFont(17),
                ElementBounds.Fixed(58, 18, 300, 26)
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(22, 54, 376, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddDynamicText(
                "Taking screenshots, please wait…",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(22, 70, 376, 40),
                "shot-progress"
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(9),
                ElementBounds.Fixed(22, 112, 376, 34),
                "shot-result"
            )
            .AddAtlasButton(
                "CANCEL / CLOSE",
                CloseScreenshotProgressModal,
                ElementBounds.Fixed(136, 153, 148, 30),
                "shot-modal-close",
                AtlasButtonStyle.Compact
            )
            .Compose(false);
    }

    /// <summary>Cancels a running capture or dismisses the finished status.</summary>
    private bool CloseScreenshotProgressModal()
    {
        if (screenshotProgressModal == null) return true;
        if (tileScreenshot.Busy)
        {
            tileScreenshot.Cancel();
            RestoreScreenshotCamera();
            pendingScreenshotRequest = false;
            capi.Logger.Notification(
                "[ModernAtlas] The tiled atlas screenshot was cancelled; the camera was restored."
            );
        }
        screenshotProgressModal.Dispose();
        screenshotProgressModal = null;
        return true;
    }

    private void UpdateScreenshotStatusText()
    {
        string? text;
        if (screenshotProgressModal != null)
        {
            AtlasScreenshotJobState screenshotState = tileScreenshot.State;
            if (tileScreenshot.CaptureActive)
            {
                text = $"Taking screenshots, please wait… (tile {tileScreenshot.CurrentTile + 1} of {tileScreenshot.TotalTiles})";
            }
            else if (screenshotState == AtlasScreenshotJobState.Failed)
            {
                text = $"Screenshot failed: {tileScreenshot.LastError ?? "see the log"}";
            }
            else if (screenshotState == AtlasScreenshotJobState.Cancelled)
            {
                text = "Screenshot cancelled; private staging is being removed…";
            }
            else if (screenshotState == AtlasScreenshotJobState.Committed)
            {
                text = tileScreenshot.Busy
                    ? "PNG committed; removing private staging…"
                    : "Screenshot committed.";
            }
            else if (screenshotState == AtlasScreenshotJobState.Validating)
            {
                text = "Validating PNG before commit; please wait…";
            }
            else if (tileScreenshot.Busy)
            {
                AtlasScreenshotPreview preview = tileScreenshot.ActivePreview;
                text = preview.IsValid
                    ? $"Stitching {preview.OutputWidth} × {preview.OutputHeight} PNG; please wait for completion…"
                    : "Stitching and saving the screenshot…";
            }
            else
            {
                text = "Screenshot finished.";
            }
        }
        else
        {
            string? lastPath = tileScreenshot.LastSavedPath;
            string? lastError = tileScreenshot.LastError;
            if (lastPath != null)
            {
                // The saved location stays readable until the atlas closes or
                // a new capture replaces it; a short toast was not readable.
                text = FormatScreenshotStatus("Saved:", lastPath);
            }
            else if (lastError != null)
            {
                text = $"Screenshot error: {lastError}";
            }
            else if (capi.ElapsedMilliseconds < screenshotStatusShownUntilMilliseconds)
            {
                text = screenshotStatusOverride;
            }
            else
            {
                text = null;
            }
        }
        overlay?.GetDynamicText("screenshot-status")?.SetNewText(text ?? "");
        string panelStatus = text ?? BuildScreenshotFilterStatusText();
        if (tileScreenshot.Busy && tileScreenshot.PendingPath is string pendingPath)
        {
            panelStatus = $"Saving to: {ShortenScreenshotPath(pendingPath, 72)}";
        }
        screenshotPanel?.GetDynamicText("shot-status")?.SetNewText(panelStatus);
        screenshotPanel?.GetDynamicText("shot-collapsed-status")?.SetNewText(
            BuildScreenshotCollapsedStatus()
        );
        screenshotFilterTuningPanel?.GetDynamicText(
            "shot-filter-tuning-status"
        )?.SetNewText(panelStatus);

        if (screenshotProgressModal == null) return;
        screenshotProgressModal.GetDynamicText("shot-progress")?.SetNewText(
            text ?? ""
        );
        string result = "";
        if (tileScreenshot.Busy && tileScreenshot.PendingPath is string resultPendingPath)
        {
            result = $"Saving to:\n{ShortenScreenshotPath(resultPendingPath, 48)}";
        }
        else if (!tileScreenshot.Busy)
        {
            if (tileScreenshot.LastSavedPath != null)
            {
                result = FormatScreenshotStatus("Saved:", tileScreenshot.LastSavedPath);
            }
            else if (tileScreenshot.LastError != null)
            {
                result = $"Error: {tileScreenshot.LastError}";
            }
        }
        else if (tileScreenshot.State == AtlasScreenshotJobState.Failed)
        {
            result = $"Error: {tileScreenshot.LastError ?? "see the log"}";
        }
        screenshotProgressModal.GetDynamicText("shot-result")?.SetNewText(result);
    }

    /// <summary>
    /// Shows the public screenshot location instead of the often very long
    /// absolute data path. The full path remains in the log and PNG manifest.
    /// </summary>
    private static string FormatScreenshotStatus(string prefix, string path)
    {
        return $"{prefix} {ShortenScreenshotPath(path, 72)}";
    }

    private static string ShortenScreenshotPath(string path, int maximumLength)
    {
        if (string.IsNullOrEmpty(path)) return "";

        string normalized = path.Replace('\\', '/');
        const string publicFolder = "Screenshots/ModernAtlas/";
        int publicFolderIndex = normalized.IndexOf(
            publicFolder,
            StringComparison.OrdinalIgnoreCase
        );
        string displayPath = publicFolderIndex >= 0
            ? normalized[publicFolderIndex..]
            : normalized;
        if (displayPath.Length <= maximumLength) return displayPath;

        int slash = displayPath.LastIndexOf('/');
        string fileName = slash >= 0
            ? displayPath[(slash + 1)..]
            : displayPath;
        if (maximumLength <= 4)
        {
            return fileName[..Math.Min(fileName.Length, maximumLength)];
        }
        int suffixLength = Math.Min(
            fileName.Length,
            Math.Max(1, maximumLength - 2)
        );
        return $"…{fileName[^suffixLength..]}";
    }

}
