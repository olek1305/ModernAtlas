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
/// Opt-in automated atlas smoke test: it drives the real dialog controls,
/// captures diagnostic frames and validates the results. This partial holds
/// only test-time code; normal play never enters it.
/// </summary>
public sealed partial class ModernAtlasDialog
{
    internal void BeginAutomatedSmokeTest(Action<bool> completion)
    {
        presentationChangeCoordinator.Reset(config.RenderOnScroll);
        automatedOriginalMapLayersEnabled = config.MapLayersEnabled;
        automatedOriginalCaveModeEnabled = config.CaveModeEnabled;
        automatedOriginalSearchModeEnabled = config.SearchModeEnabled;
        automatedOriginalCameraAngleLocked = config.CameraAngleLocked;
        automatedOriginalRenderOnScroll = config.RenderOnScroll;
        automatedOriginalLiveLightingEnabled = config.LiveLightingEnabled;
        automatedOriginalCloudsEnabled = config.CloudsEnabled;
        automatedOriginalFixedSunHour = config.FixedSunHour;
        automatedOriginalShowPlayerCompass = config.ShowPlayerCompass;
        automatedOriginalHandheldInstrumentMode = HandheldInstrumentMode;
        automatedOriginalPerformanceLightingEnabled =
            config.PerformanceLightingEnabled;
        automatedOriginalHideVegetation = config.HideVegetation;
        automatedOriginalCheatModeEnabled = cheatModeEnabled;
        automatedOriginalScreenshotScale = config.ScreenshotScale;
        automatedOriginalScreenshotCaptureAreaPercent =
            config.ScreenshotCaptureAreaPercent;
        automatedOriginalScreenshotFilters = new ScreenshotFilterPreferences(
            config
        );
        automatedScreenshotSequenceEnabled = IsEnvironmentFlagEnabled(
            SmokeScreenshotSequenceEnvironmentVariable
        );
        automatedScreenshotIntensityZeroEnabled =
            !automatedScreenshotSequenceEnabled
            && IsEnvironmentFlagEnabled(
                SmokeScreenshotIntensityZeroEnvironmentVariable
            );
        automatedScreenshotCancelMode = !automatedScreenshotSequenceEnabled
            && IsEnvironmentFlagEnabled(SmokeScreenshotCancelEnvironmentVariable);
        automatedScreenshotCancelTriggered = false;
        automatedScreenshotCancelPassed = false;
        automatedSmokeScreenshotPreviewPending = false;
        automatedSmokeScreenshotPreviewPassed = false;
        automatedSmokeScreenshotPreviewTakeCancelPassed = false;
        automatedScreenshotSequenceFailed = false;
        automatedScreenshotOutputValidationPassed = false;
        automatedScreenshotMaskDiagnosticsEnabled =
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    SmokeScreenshotMaskEnvironmentVariable
                )
            )
            || !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    SmokeScreenshotEnvironmentVariable
                )
            );
        automatedScreenshotMaskValidationPassed =
            !automatedScreenshotMaskDiagnosticsEnabled;
        automatedScreenshotMaskCoverageFailure = false;
        automatedScreenshotFilterValidationPassed =
            !automatedScreenshotSequenceEnabled
            && !automatedScreenshotIntensityZeroEnabled;
        automatedScreenshotFilterReadbackFailure = false;
        automatedScreenshotFilterComparedTileCount = 0;
        automatedScreenshotFilterChangedPixelCount = 0;
        automatedScreenshotFilterComparedPixelCount = 0;
        automatedScreenshotFilterMinimumChangedRatio = 1d;
        automatedScreenshotFilterMaximumChangedRatio = 0d;
        automatedScreenshotFilterMaximumChannelDelta = 0;
        automatedScreenshotDiagnosticNextMilliseconds = 0;
        automatedScreenshotSequenceStep = 0;
        automatedFirstScreenshotPath = null;
        automatedSecondScreenshotPath = null;
        automatedScreenshotPublicEntriesBefore =
            CapturePublicScreenshotEntries();
        automatedScreenshotJobEntriesBefore = CaptureScreenshotJobEntries();
        // The default automated capture uses a 2x2 tile grid so the tiled
        // pipeline is exercised without a large multi-frame sequence. The
        // optional environment overrides are used for targeted large-capture
        // regression runs, for example 8x at 25% area.
        int smokeScreenshotScale = automatedScreenshotSequenceEnabled ? 8 : 2;
        string? forcedScreenshotScale = Environment.GetEnvironmentVariable(
            SmokeScreenshotScaleEnvironmentVariable
        );
        if (int.TryParse(forcedScreenshotScale, out int parsedScreenshotScale))
        {
            smokeScreenshotScale = Math.Clamp(parsedScreenshotScale, 1, 8);
        }
        int smokeScreenshotCaptureArea = automatedScreenshotSequenceEnabled
            ? 25
            : 100;
        string? forcedScreenshotCaptureArea =
            Environment.GetEnvironmentVariable(
                SmokeScreenshotCaptureAreaEnvironmentVariable
            );
        if (int.TryParse(
            forcedScreenshotCaptureArea,
            out int parsedScreenshotCaptureArea
        ))
        {
            smokeScreenshotCaptureArea =
                AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
                    parsedScreenshotCaptureArea
                );
        }
        if ((!automatedScreenshotSequenceEnabled
                && (smokeScreenshotScale != 2
                    || smokeScreenshotCaptureArea != 100))
            || (automatedScreenshotSequenceEnabled
                && (smokeScreenshotScale != 8
                    || smokeScreenshotCaptureArea != 25)))
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test will capture the targeted {0}x/{1}% screenshot configuration.",
                smokeScreenshotScale,
                smokeScreenshotCaptureArea
            );
        }
        if (automatedScreenshotSequenceEnabled)
        {
            // The regression sequence keeps both A/B captures at the same
            // maximum tiled resolution. This makes the Off/Atlas Relief
            // comparison meaningful while exercising all 64 tiles.
            smokeScreenshotScale = 8;
            smokeScreenshotCaptureArea = 25;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test will run the consecutive screenshot sequence 8x/25% Off -> 8x/25% Atlas Relief."
            );
        }
        automatedSmokeScreenshotScale = smokeScreenshotScale;
        automatedSmokeScreenshotCaptureAreaPercent = smokeScreenshotCaptureArea;
        if (automatedScreenshotIntensityZeroEnabled)
        {
            AtlasScreenshotFilterSettings.ApplyPresetToConfig(
                config,
                "custom"
            );
            config.ScreenshotFiltersEnabled = true;
            config.ScreenshotFilterIntensityPercent = 0;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test will verify exact RGB preservation for a Custom screenshot filter with Intensity=0."
            );
        }
        if (automatedScreenshotCancelMode)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test will close the screenshot modal during stitching and verify exact-job cancellation cleanup."
            );
        }
        config.ScreenshotScale = smokeScreenshotScale;
        config.ScreenshotCaptureAreaPercent = smokeScreenshotCaptureArea;
        if (automatedScreenshotSequenceEnabled)
        {
            // The first half of the optional consecutive regression is the
            // exact baseline: no screenshot post-process at all.
            AtlasScreenshotFilterSettings.ApplyPresetToConfig(config, "off");
        }
        automatedSmokeTestPreferencesCaptured = true;
        if (capi.IsSinglePlayer)
        {
            cheatModeEnabled = true;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test temporarily accepted Cheat Mode for Creative-only atlas checks."
            );
        }
        capi.Logger.Notification(
            "[ModernAtlas] Automated smoke player game mode: {0}.",
            capi.World.Player?.WorldData.CurrentGameMode.ToString() ?? "unavailable"
        );
        config.MapLayersEnabled = true;
        config.CaveModeEnabled = false;
        config.SearchModeEnabled = true;
        config.CameraAngleLocked = false;
        config.RenderOnScroll = true;
        config.CloudsEnabled = !IsEnvironmentFlagEnabled(
            SmokeDisableCloudsEnvironmentVariable
        );
        if (!config.CloudsEnabled)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test disabled atlas clouds for the rendering A/B diagnostic."
            );
        }
        automatedSmokeTestInitialViewDistance = GameViewDistance;
        automatedSmokeTestViewDistancePassed = true;
        automatedSmokeTestViewDistanceUnchanged = true;
        string? expectedSmokeViewDistance = Environment.GetEnvironmentVariable(
            SmokeExpectedViewDistanceEnvironmentVariable
        );
        if (!string.IsNullOrWhiteSpace(expectedSmokeViewDistance))
        {
            if (int.TryParse(expectedSmokeViewDistance, out int expectedDistance))
            {
                automatedSmokeTestViewDistancePassed =
                    automatedSmokeTestInitialViewDistance == expectedDistance;
                if (automatedSmokeTestViewDistancePassed)
                {
                    capi.Logger.Notification(
                        "[ModernAtlas] Automated smoke view-distance probe passed: configured={0}; atlas read the same value without changing it.",
                        automatedSmokeTestInitialViewDistance
                    );
                }
                else
                {
                    capi.Logger.Error(
                        "[ModernAtlas] Automated smoke view-distance probe failed: expected configured value {0}, read {1}; the test never changes viewDistance.",
                        expectedDistance,
                        automatedSmokeTestInitialViewDistance
                    );
                }
            }
            else
            {
                automatedSmokeTestViewDistancePassed = false;
                capi.Logger.Error(
                    "[ModernAtlas] Automated smoke view-distance probe could not parse MODERNATLAS_SMOKE_EXPECT_VIEW_DISTANCE={0}.",
                    expectedSmokeViewDistance
                );
            }
        }
        else
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke view-distance probe: configured={0}; no setting mutation is performed.",
                automatedSmokeTestInitialViewDistance
            );
        }
        // Capture the resized compass in the early smoke frames. The normal
        // interface exercise later selects Time, so one run now covers both
        // instrument faces before restoring the player's original choice.
        config.ShowPlayerCompass = true;
        config.HandheldInstrumentMode = "compass";
        string? forcedSunHour = Environment.GetEnvironmentVariable(
            SmokeFixedSunHourEnvironmentVariable
        );
        if (int.TryParse(forcedSunHour, out int parsedSunHour))
        {
            config.LiveLightingEnabled = false;
            config.FixedSunHour = Math.Clamp(parsedSunHour, 0, 23);
            config.PerformanceLightingEnabled = true;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test forced the atlas sun to {0}:00 for lighting inspection.",
                config.FixedSunHour
            );
        }
        automatedSmokeTestActive = true;
        automatedSmokeTestElapsedSeconds = 0;
        automatedSmokeTestCompletion = completion;
        automatedSmokeTestRequiresUnlockedPitch = false;
        automatedSmokeTestRenderedAtPitchFloor = false;
        automatedSmokeTestInterfaceControlsAttempted = false;
        automatedSmokeTestInterfaceControlsPassed = false;
        automatedSmokeTestUnitInspectionAttempted = false;
        automatedSmokeTestUnitInspectionPassed = false;
        automatedSmokeTestSearchInputAttempted = false;
        automatedSmokeTestSearchInputPassed = false;
        automatedScreenshotCaptureRequested = false;
        automatedScreenshotCapturePassed = false;
        automatedSmokeScreenshotPreviewPending = false;
        automatedSmokeScreenshotPreviewPassed = false;
        automatedSmokeScreenshotPreviewTakeCancelPassed = false;
        automatedScreenshotBaselineZoom = 0;
        automatedScreenshotBaselineTargetZoom = 0;
        automatedScreenshotBaselineCenterX = 0;
        automatedScreenshotBaselineCenterY = 0;
        automatedScreenshotBaselineCenterZ = 0;
        automatedSmokeTestBilingualSearchPassed =
            searchController.ValidateBilingualSearchForAutomatedTest(
                out string languageDiagnostic
            );
        automatedSmokeTestOreConcealmentPassed = false;
        automatedSmokeTestCreativeOreRevealFrameRendered = false;
        automatedSmokeTestForceSurvivalOreConcealment = true;
        if (automatedSmokeTestBilingualSearchPassed)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated bilingual search check passed: {0}.",
                languageDiagnostic
            );
        }
        else
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated bilingual search check failed: {0}.",
                languageDiagnostic
            );
        }
        automatedSmokeTestSafeSurfaceFrameRendered = false;
        automatedSmokeTestSafeSurfaceScreenshotHandled = false;
        automatedSmokeTestBorderTopDownPending = false;
        automatedSmokeTestBorderTopDownFrameRendered = false;
        automatedSmokeTestMaximumZoomPending = false;
        automatedSmokeTestMaximumZoomFrameRendered = false;
        automatedSmokeTestPartialZoomPending = false;
        automatedSmokeTestPartialZoomFrameRendered = false;
        automatedSmokeTestZoomBeforeMaximum = 0;
        automatedSmokeTestPitchBeforeMaximum = 0;
        automatedSmokeTestMaximumZoomStartedSeconds = 0;
        automatedSmokeTestPartialZoomStartedSeconds = 0;
        automatedSmokeTestBorderTopDownStartedSeconds = 0;
        automatedSmokeTestSearchPhase = 0;
        automatedSmokeTestSearchPassed = false;
        automatedLanternBlockX = 0;
        automatedLanternBlockY = 0;
        automatedLanternBlockZ = 0;
        automatedSmokeTestMapLayerPhase = 0;
        automatedSmokeTestOreLayerZoom = 0;
        automatedSmokeTestMapLayerPassed = false;
        automatedSmokeTestDamagePhase = 0;
        automatedSmokeTestDamageWarningPassed = false;
        pendingAutomatedMapLayerScreenshotSuffix = null;
        automatedSmokeTestPerformanceModeSelected = false;
        automatedSmokeTestPerformanceModeRendered = false;
        automatedSmokeTestPresentationPassed = false;
        automatedSmokeTestResolvedAtlasAlphaChecked = false;
        automatedSmokeTestResolvedAtlasAlphaPassed = false;
        automatedSmokeCloseStatePassed = true;
        automatedSmokeTestPresentationPhase = 0;
        automatedSmokeTestSettingsComposer = null;
        automatedSmokeTestPerformanceComposer = null;
        automatedSmokeTestCreativeComposer = null;
        automatedSmokeTestVisualLabComposer = null;
        automatedSmokeScreenshotPhase = 0;
        AutomatedSmokeTestRenderedExactWorld = false;
    }

    private void AdvanceAutomatedSmokeTest()
    {
        if (!automatedSmokeTestActive) return;

        automatedSmokeTestElapsedSeconds += atlasRealDeltaTime;
        if (automatedScreenshotCancelMode
            && automatedScreenshotCaptureRequested
            && !automatedScreenshotCancelTriggered
            && tileScreenshot.State == AtlasScreenshotJobState.Stitching)
        {
            automatedScreenshotCancelTriggered = true;
            tileScreenshot.Cancel();
            RestoreScreenshotCamera();
            pendingScreenshotRequest = false;
            CloseScreenshotProgressModal();
            capi.Logger.Notification(
                "[ModernAtlas] Automated screenshot modal was closed during Stitching; waiting for exact-job cleanup."
            );
        }
        if (automatedScreenshotCancelMode
            && automatedScreenshotCancelTriggered
            && !automatedScreenshotCancelPassed
            && !tileScreenshot.Busy)
        {
            bool cameraRestored = !pendingScreenshotRequest
                && (Math.Abs(zoom - automatedScreenshotBaselineZoom) < 0.001f
                    || Math.Abs(zoom - automatedScreenshotBaselineTargetZoom) < 0.001f)
                && Math.Abs(targetZoom - automatedScreenshotBaselineTargetZoom) < 0.001f
                && Math.Abs(centerX - automatedScreenshotBaselineCenterX) < 0.001
                && Math.Abs(centerY - automatedScreenshotBaselineCenterY) < 0.001
                && Math.Abs(centerZ - automatedScreenshotBaselineCenterZ) < 0.001;
            if (tileScreenshot.State == AtlasScreenshotJobState.Cancelled
                && tileScreenshot.LastSavedPath == null
                && cameraRestored
                && ValidateCancelledScreenshotOutputs())
            {
                automatedScreenshotCancelPassed = true;
                automatedScreenshotCapturePassed = false;
                capi.Logger.Notification(
                    "[ModernAtlas] Automated screenshot cancellation cleanup passed: no final PNG or public sidecar was exposed and the camera was restored."
                );
            }
            else if (tileScreenshot.LastError != null)
            {
                automatedScreenshotSequenceFailed = true;
                capi.Logger.Error(
                    "[ModernAtlas] Automated screenshot cancellation test failed: {0}.",
                    tileScreenshot.LastError
                );
            }
        }
        if (automatedScreenshotCaptureRequested && !automatedScreenshotCapturePassed)
        {
            string? capturedPath = tileScreenshot.LastSavedPath;
            string? capturedError = tileScreenshot.LastError;
            bool cameraRestored = !pendingScreenshotRequest
                && (Math.Abs(zoom - automatedScreenshotBaselineZoom) < 0.001f
                    || Math.Abs(zoom - automatedScreenshotBaselineTargetZoom) < 0.001f)
                && Math.Abs(targetZoom - automatedScreenshotBaselineTargetZoom) < 0.001f
                && Math.Abs(centerX - automatedScreenshotBaselineCenterX) < 0.001
                && Math.Abs(centerY - automatedScreenshotBaselineCenterY) < 0.001
                && Math.Abs(centerZ - automatedScreenshotBaselineCenterZ) < 0.001;
            if (capi.ElapsedMilliseconds >= automatedScreenshotDiagnosticNextMilliseconds)
            {
                automatedScreenshotDiagnosticNextMilliseconds =
                    capi.ElapsedMilliseconds + 1000;
                capi.Logger.Notification(
                    "[ModernAtlas] Automated screenshot completion diagnostic: path={0}, exists={1}, busy={2}, state={3}, pending={4}, cameraRestored={5}, zoom={6}/{7}, targetZoom={8}/{9}, center=({10:0.###},{11:0.###},{12:0.###})/({13:0.###},{14:0.###},{15:0.###}).",
                    capturedPath ?? "<null>",
                    capturedPath != null && File.Exists(capturedPath),
                    tileScreenshot.Busy,
                    tileScreenshot.State,
                    pendingScreenshotRequest,
                    cameraRestored,
                    zoom,
                    automatedScreenshotBaselineZoom,
                    targetZoom,
                    automatedScreenshotBaselineTargetZoom,
                    centerX,
                    centerY,
                    centerZ,
                    automatedScreenshotBaselineCenterX,
                    automatedScreenshotBaselineCenterY,
                    automatedScreenshotBaselineCenterZ
                );
            }
            if (capturedPath != null
                && File.Exists(capturedPath)
                && !tileScreenshot.Busy
                && cameraRestored)
            {
                try
                {
                    AtlasTiledScreenshot.ValidateCommittedPngForAutomation(
                        capturedPath
                    );
                    if (automatedScreenshotSequenceEnabled
                        && automatedScreenshotSequenceStep == 0
                        && !TryCopySmokeScreenshotOutput(
                            capturedPath,
                            "unfiltered-off"
                        ))
                    {
                        throw new IOException(
                            "Could not save the unfiltered smoke PNG diagnostic."
                        );
                    }
                    if (automatedScreenshotSequenceEnabled
                        && automatedScreenshotSequenceStep == 0)
                    {
                        automatedFirstScreenshotPath = capturedPath;
                        automatedScreenshotSequenceStep = 1;
                        automatedScreenshotCaptureRequested = false;
                        automatedScreenshotCapturePassed = false;
                        config.ScreenshotScale = 8;
                        config.ScreenshotCaptureAreaPercent = 25;
                        AtlasScreenshotFilterSettings.ApplyPresetToConfig(
                            config,
                            "atlas-relief"
                        );
                        SyncScreenshotSettingsControls();
                        CloseScreenshotProgressModal();
                        capi.Logger.Notification(
                            "[ModernAtlas] Automated consecutive screenshot job 8x/25% with preset Off passed: {0}; the camera was restored. Queueing 8x/25% with preset Atlas Relief.",
                            capturedPath
                        );
                    }
                    else
                    {
                        if (!automatedScreenshotSequenceEnabled
                            && !TryCopySmokeScreenshotOutput(
                                capturedPath,
                                "screenshot-tiled"
                            ))
                        {
                            throw new IOException(
                                "Could not save the tiled smoke PNG diagnostic."
                            );
                        }
                        if (automatedScreenshotSequenceEnabled
                            && !TryCopySmokeScreenshotOutput(
                                capturedPath,
                                "filtered-atlas-relief"
                            ))
                        {
                            throw new IOException(
                                "Could not save the filtered Atlas Relief smoke PNG diagnostic."
                            );
                        }
                        automatedSecondScreenshotPath =
                            automatedScreenshotSequenceEnabled
                                ? capturedPath
                                : automatedSecondScreenshotPath;
                        automatedScreenshotSequenceStep =
                            automatedScreenshotSequenceEnabled ? 2 : 1;
                        automatedScreenshotCapturePassed = true;
                        CloseScreenshotProgressModal();
                        capi.Logger.Notification(
                            "[ModernAtlas] Automated tiled-screenshot capture passed: {0}; the atlas camera was restored.",
                            capturedPath
                        );
                    }
                }
                catch (Exception exception)
                {
                    automatedScreenshotCapturePassed = false;
                    automatedScreenshotSequenceFailed = true;
                    automatedScreenshotCaptureRequested = true;
                    CloseScreenshotProgressModal();
                    capi.Logger.Error(
                        "[ModernAtlas] Automated screenshot PNG validation failed for {0}: {1}.",
                        capturedPath,
                        exception.Message
                    );
                }
            }
            else if (capturedError != null)
            {
                automatedScreenshotCapturePassed = false;
                automatedScreenshotSequenceFailed = true;
                // Keep the request latched in the failed state. Releasing it
                // here would immediately launch the same broken capture again
                // on the next frame and hide the original diagnostic.
                automatedScreenshotCaptureRequested = true;
                CloseScreenshotProgressModal();
                capi.Logger.Error(
                    "[ModernAtlas] Automated tiled-screenshot capture failed: {0}.",
                    capturedError
                );
            }
        }
        // Every other automated step must be complete before the tiled
        // capture starts. The map-layer and presentation exercises move the
        // camera; queueing earlier would make the camera-restore comparison
        // race their legitimate zoom changes.
        bool smokeStepsComplete = AutomatedSmokeTestRenderedExactWorld
            && exactChunkRenderer?.BoundaryResolvedLastFrame == true
            && automatedSmokeTestSafeSurfaceFrameRendered
            && (!automatedSmokeTestRequiresUnlockedPitch
                || automatedSmokeTestRenderedAtPitchFloor)
            && automatedSmokeTestInterfaceControlsPassed
            && (!automatedSmokeTestUnitInspectionAttempted
                || automatedSmokeTestUnitInspectionPassed)
            && automatedSmokeTestSearchInputPassed
            && automatedSmokeTestBilingualSearchPassed
            && automatedSmokeTestOreConcealmentPassed
            && automatedSmokeTestCreativeOreRevealFrameRendered
            && automatedSmokeTestPartialZoomFrameRendered
            && automatedSmokeTestMaximumZoomFrameRendered
            && automatedSmokeTestSearchPassed
            && automatedSmokeTestMapLayerPassed
            && automatedSmokeTestPerformanceModeRendered
            && automatedSmokeTestPresentationPassed
            && automatedSmokeTestResolvedAtlasAlphaPassed
            && automatedSmokeTestViewDistancePassed
            && automatedSmokeTestViewDistanceUnchanged
            && automatedSmokeScreenshotPreviewPassed
            && automatedSmokeScreenshotPreviewTakeCancelPassed;
        if (smokeStepsComplete
            && !automatedScreenshotCaptureRequested
            && !automatedScreenshotSequenceFailed
            && (!automatedScreenshotCancelMode
                || !automatedScreenshotCancelTriggered))
        {
            if (!config.RenderOnScroll)
            {
                // The presentation test ends in fullscreen mode. Return to
                // the scroll viewport first so the capture exercises the
                // scroll-aspect tile cropping as well. Request only once:
                // re-requesting every frame would restart the debounce timer.
                if (!presentationChangeCoordinator.HasPending)
                {
                    OnRenderOnScrollToggled(true);
                }
            }
            else if (!presentationChangeCoordinator.HasPending)
            {
                automatedScreenshotBaselineZoom = zoom;
                automatedScreenshotBaselineTargetZoom = targetZoom;
                automatedScreenshotBaselineCenterX = centerX;
                automatedScreenshotBaselineCenterY = centerY;
                automatedScreenshotBaselineCenterZ = centerZ;
                // Save the live viewport frame right before the capture, so
                // the stitched PNG can be compared against this reference to
                // prove the tile order and orientation.
                string? referencePath = Environment.GetEnvironmentVariable(
                    SmokeScreenshotEnvironmentVariable
                );
                if (!string.IsNullOrWhiteSpace(referencePath))
                {
                    string prefix = referencePath.EndsWith(
                        ".png",
                        StringComparison.OrdinalIgnoreCase
                    )
                        ? referencePath[..^4]
                        : referencePath;
                    TrySaveAutomatedSmokeScreenshot(
                        $"{prefix}-screenshot-view.png"
                    );
                }
                // The automated capture explicitly covers the unlocked
                // Creative/Cheat 0-degree view. Restore the player's atlas
                // pitch after the job so this probe never changes a saved
                // preference or the final soft-exit state.
                automatedScreenshotPitchBeforeCapture = pitchDegrees;
                automatedScreenshotTargetPitchBeforeCapture = targetPitchDegrees;
                automatedScreenshotPitchSnapshotted = true;
                pitchDegrees = UnlockedMinimumPitchDegrees;
                targetPitchDegrees = UnlockedMinimumPitchDegrees;
                bool takeAccepted = TakeScreenshot();
                automatedScreenshotCaptureRequested = pendingScreenshotRequest;
                if (!takeAccepted || !automatedScreenshotCaptureRequested)
                {
                    automatedScreenshotSequenceFailed = true;
                    automatedScreenshotCaptureRequested = true;
                    capi.Logger.Error(
                        "[ModernAtlas] Automated tiled-screenshot job was rejected before capture began."
                    );
                }
                capi.Logger.Notification(
                    "[ModernAtlas] Automated smoke test queued a {0}x{0} tiled atlas screenshot of the centered {1}% area.",
                    Math.Clamp(config.ScreenshotScale, 1, 8),
                    AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
                        config.ScreenshotCaptureAreaPercent
                    )
                );
            }
        }
        bool screenshotPassed = automatedScreenshotCancelMode
            ? automatedScreenshotCancelPassed
            : automatedScreenshotCaptureRequested
            && automatedScreenshotCapturePassed;
        bool passed = smokeStepsComplete
            && screenshotPassed
            && !automatedScreenshotSequenceFailed
            && (!automatedScreenshotSequenceEnabled
                || automatedScreenshotSequenceStep >= 2);
        if (passed && automatedSmokeTestElapsedSeconds < 3f) return;
        // Exact loaded-data search and map-layer preparation are budgeted over
        // many frames. A large standard world can legitimately need more than
        // one minute before those phases finish, especially after a second
        // atlas cycle. Keep the timeout finite, but avoid treating ordinary
        // client streaming variance as a rendering failure.
        float screenshotTimeoutSeconds = config.ScreenshotScale >= 8
            ? 180f
            : 120f;
        if (!passed && automatedSmokeTestElapsedSeconds < screenshotTimeoutSeconds)
            return;

        if (passed
            && !automatedScreenshotCancelMode
            && !automatedScreenshotOutputValidationPassed)
        {
            automatedScreenshotOutputValidationPassed =
                ValidateAutomatedScreenshotOutputs(out string outputDiagnostic);
            if (!automatedScreenshotOutputValidationPassed)
            {
                passed = false;
                capi.Logger.Error(
                    "[ModernAtlas] Automated screenshot output validation failed: {0}.",
                    outputDiagnostic
                );
            }
        }

        if (!passed)
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated smoke summary: exact={0}, boundary={1}, resolvedAlpha={2}(checked={3}), viewDistance={4}(unchanged={5}, initial={6}), safeSurface={7}, pitch={8}/{9}, interface={10}, unit={11}/{12}, searchInput={13}, bilingual={14}, ore={15}, creativeOre={16}, partialZoom={17}, maximumZoom={18}, search={19}(phase={20}), layers={21}(phase={22}), performance={23}(selected={24}, renderedVegetationHidden={25}, flatLighting={26}), presentation={27}, screenshotPreview={34}, screenshotCapture={28}(requested={29}, sequenceStep={30}, outputs={31}, cancel={32}/{33}).",
                AutomatedSmokeTestRenderedExactWorld,
                exactChunkRenderer?.BoundaryResolvedLastFrame == true,
                automatedSmokeTestResolvedAtlasAlphaPassed,
                automatedSmokeTestResolvedAtlasAlphaChecked,
                automatedSmokeTestViewDistancePassed,
                automatedSmokeTestViewDistanceUnchanged,
                automatedSmokeTestInitialViewDistance,
                automatedSmokeTestSafeSurfaceFrameRendered,
                automatedSmokeTestRenderedAtPitchFloor,
                automatedSmokeTestRequiresUnlockedPitch,
                automatedSmokeTestInterfaceControlsPassed,
                automatedSmokeTestUnitInspectionPassed,
                automatedSmokeTestUnitInspectionAttempted,
                automatedSmokeTestSearchInputPassed,
                automatedSmokeTestBilingualSearchPassed,
                automatedSmokeTestOreConcealmentPassed,
                automatedSmokeTestCreativeOreRevealFrameRendered,
                automatedSmokeTestPartialZoomFrameRendered,
                automatedSmokeTestMaximumZoomFrameRendered,
                automatedSmokeTestSearchPassed,
                automatedSmokeTestSearchPhase,
                automatedSmokeTestMapLayerPassed,
                automatedSmokeTestMapLayerPhase,
                automatedSmokeTestPerformanceModeRendered,
                automatedSmokeTestPerformanceModeSelected,
                exactChunkRenderer?.LastRenderedVegetationHidden ?? false,
                !(exactChunkRenderer?.LastRenderedPerformanceLightingEnabled ?? true),
                automatedSmokeTestPresentationPassed,
                automatedScreenshotCapturePassed,
                automatedScreenshotCaptureRequested,
                automatedScreenshotSequenceStep,
                automatedScreenshotOutputValidationPassed,
                automatedScreenshotCancelPassed,
                automatedScreenshotCancelMode,
                automatedSmokeScreenshotPreviewPassed
            );
        }

        if (passed && automatedSmokeTestDamagePhase < 3)
        {
            AdvanceAutomatedDamageWarningTest();
            if (automatedSmokeTestDamagePhase < 3) return;
        }
        passed = passed && automatedSmokeTestDamageWarningPassed;

        automatedSmokeTestActive = false;
        Action<bool>? completion = automatedSmokeTestCompletion;
        automatedSmokeTestCompletion = null;
        RestoreAutomatedSmokeTestPreferences();
        completion?.Invoke(passed);
    }

    /// <summary>
    /// Drives the damage-warning test across frames: the checks and the vignette
    /// frame first, then the emergency close. Nothing hurts the player and
    /// nothing is written to the save; the auto-close preference is changed in
    /// memory only and restored.
    /// </summary>
    private void AdvanceAutomatedDamageWarningTest()
    {
        if (automatedSmokeTestDamagePhase == 0)
        {
            if (!ExerciseAutomatedDamageWarningChecks())
            {
                automatedSmokeTestDamagePhase = 3;
                return;
            }
            // Leave the steady LOW HEALTH state on and capture the next frame:
            // the warning is drawn before the screenshot is taken, so the stored
            // image really shows it.
            TriggerDamageWarningForAutomatedTest(true);
            QueueAutomatedMapLayerScreenshot("damage-warning");
            automatedSmokeTestDamagePhase = 1;
            return;
        }

        if (automatedSmokeTestDamagePhase == 1)
        {
            // Wait for the queued frame to be stored before the state is reset.
            if (pendingAutomatedMapLayerScreenshotSuffix != null) return;

            ResetDamageWarning();
            // Raise the signal exactly as production does from the render pass;
            // the close itself is deferred to the next frame start.
            config.CloseAtlasOnDamage = true;
            automatedSmokeTestDamageWasOpenBeforeSignal = IsOpened();
            TriggerDamageWarningForAutomatedTest(false);
            ScheduleAutomatedEmergencyCloseCheck();
            automatedSmokeTestDamagePhase = 2;
        }
    }

    private bool ExerciseAutomatedDamageWarningChecks()
    {
        EnumGameMode mode = LocalGameModeForAutomatedTest;
        // The policy must depend on the actual game mode only. Cheat Mode is a
        // protected feature switch and may never suppress a safety warning.
        bool policyRules =
            DamageWarningPolicyForModeForAutomatedTest(EnumGameMode.Survival)
            && DamageWarningPolicyForModeForAutomatedTest(EnumGameMode.Guest)
            && !DamageWarningPolicyForModeForAutomatedTest(EnumGameMode.Creative);
        bool livePolicy = DamageWarningPolicyActive
            == DamageWarningPolicyForModeForAutomatedTest(mode);

        // Behavioural Creative check: resolved as Creative the signal must leave
        // the overlay and the auto-close inert.
        SetDamageWarningModeOverrideForAutomatedTest(EnumGameMode.Creative);
        ResetDamageWarning();
        TriggerDamageWarningForAutomatedTest(false);
        bool creativeSuppressed = !DamageWarningWouldRenderForAutomatedTest
            && !AutoCloseOnDamageActiveForAutomatedTest
            && IsOpened();

        // The Survival behaviour is exercised through the in-memory override so
        // it does not depend on the test world's mode. Cheat Mode stays enabled
        // here, which is exactly the case that must not suppress the warning.
        SetDamageWarningModeOverrideForAutomatedTest(EnumGameMode.Survival);
        bool cheatStillWarns = DamageWarningPolicyActive;

        automatedSmokeTestCloseOnDamageBefore = config.CloseAtlasOnDamage;
        config.CloseAtlasOnDamage = false;

        ResetDamageWarning();
        TriggerDamageWarningForAutomatedTest(false);
        bool pulseActive = DamageWarningWouldRenderForAutomatedTest
            && !DamageWarningLowHealthForAutomatedTest;

        TriggerDamageWarningForAutomatedTest(true);
        bool lowHealthActive = DamageWarningWouldRenderForAutomatedTest
            && DamageWarningLowHealthForAutomatedTest;

        // A capture must never bake the warning into its image.
        pendingScreenshotRequest = true;
        bool suppressedDuringCapture = !DamageWarningWouldRenderForAutomatedTest;
        pendingScreenshotRequest = false;

        // The overlay must be drawable, not merely flagged active.
        bool texturesReady = DamageWarningTexturesReadyForAutomatedTest;

        ResetDamageWarning();
        bool clearedAfterReset = !DamageWarningActiveForAutomatedTest;
        bool stillInteractive = IsOpened() && CaptureAllInputs();

        bool passed = texturesReady
            && policyRules
            && livePolicy
            && creativeSuppressed
            && cheatStillWarns
            && pulseActive
            && lowHealthActive
            && suppressedDuringCapture
            && clearedAfterReset
            && stillInteractive;
        if (!passed)
        {
            config.CloseAtlasOnDamage = automatedSmokeTestCloseOnDamageBefore;
            SetDamageWarningModeOverrideForAutomatedTest(null);
            automatedSmokeTestDamageWarningPassed = false;
            capi.Logger.Error(
                "[ModernAtlas] Automated damage-warning check failed: texturesReady={10}, mode={0}, rules={1}, live={2}, creativeSuppressed={3}, cheatStillWarns={4}, pulse={5}, lowHealth={6}, suppressedDuringCapture={7}, cleared={8}, interactive={9}.",
                mode,
                policyRules,
                livePolicy,
                creativeSuppressed,
                cheatStillWarns,
                pulseActive,
                lowHealthActive,
                suppressedDuringCapture,
                clearedAfterReset,
                stillInteractive,
                texturesReady
            );
            return false;
        }

        capi.Logger.Notification(
            "[ModernAtlas] Automated damage-warning check passed: actual world mode={0}; resolved Creative suppresses the overlay and auto-close, Survival with accepted Cheat Mode keeps warning, the pulse and LOW HEALTH states render, a capture stays clean and input remains live.",
            mode
        );
        return true;
    }

    /// <summary>
    /// Verifies the deferred safety close between frames, so the atlas dialog is
    /// never closed or reopened from inside its own render pass.
    /// </summary>
    private void ScheduleAutomatedEmergencyCloseCheck()
    {
        capi.Event.RegisterCallback(
            _ =>
            {
                bool wasOpen = automatedSmokeTestDamageWasOpenBeforeSignal;
                bool closed = !IsOpened();
                bool reopened = closed && TryOpen();
                config.CloseAtlasOnDamage = automatedSmokeTestCloseOnDamageBefore;
                SetDamageWarningModeOverrideForAutomatedTest(null);
                ResetDamageWarning();
                automatedSmokeTestDamageWarningPassed = wasOpen && closed && reopened;
                automatedSmokeTestDamagePhase = 3;
                if (automatedSmokeTestDamageWarningPassed)
                {
                    capi.Logger.Notification(
                        "[ModernAtlas] Automated emergency-close check passed: one damage signal closed the atlas at the next frame start without the stowing animation, and it reopened for the remaining checks."
                    );
                }
                else
                {
                    capi.Logger.Error(
                        "[ModernAtlas] Automated emergency-close check failed: wasOpen={0}, closed={1}, reopened={2}.",
                        wasOpen,
                        closed,
                        reopened
                    );
                }
            },
            120
        );
    }

    private void ExerciseAutomatedInterfaceControls()
    {
        if (automatedSmokeTestInterfaceControlsAttempted) return;
        automatedSmokeTestInterfaceControlsAttempted = true;

        bool accessAvailable = CreativeCheatSettingsAvailable;
        bool toolbarPresent = overlay?.GetElement("settings-button")
                is GuiElementAtlasButton
            && overlay?.GetElement("quick-screenshot-button")
                is GuiElementAtlasButton
            && overlay?.GetElement("screenshot-options-button")
                is GuiElementAtlasButton
            && overlay?.GetElement("map-options-button")
                is GuiElementAtlasButton
            && overlay?.GetElement("search-button")
                is GuiElementAtlasButton
            && overlay?.GetElement("instrument-button")
                is GuiElementAtlasButton;
        bool initialFocusReleased = searchPanel?.GetTextInput("search-input")?.HasFocus != true;

        bool settingsOpenedByClick = ClickAtlasControlForAutomatedTest(
            overlay,
            "settings-button"
        ) && settingsModalOpen;
        GuiComposer? settingsComposer = settingsModal;
        bool settingsControlsPresent = settingsComposer?.GetElement("map-layers")
                is GuiElementAtlasSwitch
            && settingsComposer.GetElement("performance-open")
                is GuiElementAtlasButton
            && settingsComposer.GetElement("search-mode")
                is GuiElementAtlasSwitch;

        bool mapLayersBefore = config.MapLayersEnabled;
        bool mapLayersToggled = settingsOpenedByClick
            && ClickAtlasControlForAutomatedTest(settingsComposer, "map-layers")
            && config.MapLayersEnabled != mapLayersBefore;
        bool mapLayersRestored = mapLayersToggled
            && ClickAtlasControlForAutomatedTest(settingsComposer, "map-layers")
            && config.MapLayersEnabled == mapLayersBefore;
        bool skipBefore = config.SkipOpeningAnimation;
        bool skipToggled = ClickAtlasControlForAutomatedTest(
                settingsComposer,
                "skip-opening-animation"
            )
            && config.SkipOpeningAnimation != skipBefore;
        bool skipRestored = skipToggled
            && ClickAtlasControlForAutomatedTest(
                settingsComposer,
                "skip-opening-animation"
            )
            && config.SkipOpeningAnimation == skipBefore;

        bool performanceQueued = ClickAtlasControlForAutomatedTest(
                settingsComposer,
                "performance-open"
            )
            && queuedBottomPanelSection == AtlasPanelSection.Performance;
        OpenBottomPanelImmediately(AtlasPanelSection.Performance);
        bool performanceOpened = performanceQueued && performanceModalOpen;
        bool flatLightingSelected = performanceOpened
            && ClickAtlasControlForAutomatedTest(
                performanceModal,
                "performance-lighting"
            );
        bool vegetationSelected = performanceOpened
            && ClickAtlasControlForAutomatedTest(
                performanceModal,
                "hide-vegetation"
            );
        automatedSmokeTestPerformanceModeSelected = performanceOpened
            && flatLightingSelected
            && vegetationSelected;
        if (automatedSmokeTestPerformanceModeSelected)
        {
            OnPerformanceLightingToggled(false);
            OnHideVegetationToggled(true);
            lastAtlasWorldRenderMilliseconds = 0;
        }
        bool performanceBack = ClickAtlasControlForAutomatedTest(
                performanceModal,
                "performance-back"
            )
            && queuedBottomPanelSection == AtlasPanelSection.Settings;
        OpenBottomPanelImmediately(AtlasPanelSection.Settings);
        settingsComposer = settingsModal;
        bool presentationControlPresent = settingsComposer?.GetElement(
                "render-on-scroll"
            ) is GuiElementAtlasSwitch;
        bool fixedLightingPrepared = true;
        if (config.LiveLightingEnabled)
        {
            fixedLightingPrepared = ClickAtlasControlForAutomatedTest(
                settingsComposer,
                "live-lighting"
            ) && !config.LiveLightingEnabled;
        }
        int fixedHourBefore = config.FixedSunHour;
        bool sliderBoundaryHandled = DragAtlasSliderBeyondBoundsForAutomatedTest(
            settingsComposer,
            "fixed-sun-hour",
            fixedHourBefore != 23
        );
        bool sliderBoundaryClamped = config.FixedSunHour == (fixedHourBefore != 23 ? 23 : 0);
        OnFixedSunHourChanged(fixedHourBefore);
        settingsComposer?.GetAtlasSlider("fixed-sun-hour")?.SetValue(fixedHourBefore);
        bool liveLightingRestored = !config.LiveLightingEnabled
            || (ClickAtlasControlForAutomatedTest(settingsComposer, "live-lighting")
                && config.LiveLightingEnabled);
        if (automatedSmokeTestPerformanceModeSelected)
        {
            // The live/fixed lighting controls deliberately re-enable
            // directional lighting. Re-apply the already-tested developer
            // visual state so the next real atlas frame observes flat
            // lighting and hidden vegetation before the test continues.
            OnPerformanceLightingToggled(false);
            OnHideVegetationToggled(true);
            lastAtlasWorldRenderMilliseconds = 0;
        }
        OpenBottomPanelImmediately(AtlasPanelSection.Instrument);
        bool timeInstrumentSelected = bottomPanel?.GetElement(
                "handheld-instrument"
            ) is GuiElementAtlasChoice
            && ClickAtlasControlForAutomatedTest(
                bottomPanel,
                "handheld-instrument"
            )
            && config.ShowPlayerCompass
            && HandheldInstrumentMode == "time";
        OpenBottomPanelImmediately(AtlasPanelSection.Settings);
        settingsComposer = settingsModal;

        // The toolbar request intentionally goes through the same close-then-
        // open state machine used by real clicks. The direct immediate call
        // below only settles the already-tested transition for the next UI
        // assertions; it never creates a second active composer.
        bool settingsClosed = ClickAtlasControlForAutomatedTest(
            settingsComposer,
            "settings-close"
        ) && !settingsModalOpen;
        bool screenshotQueued = ClickAtlasControlForAutomatedTest(
                overlay,
                "screenshot-options-button"
            )
            && queuedBottomPanelSection == AtlasPanelSection.ScreenshotOptions;
        OpenBottomPanelImmediately(AtlasPanelSection.ScreenshotOptions);
        GuiComposer? screenshotComposer = screenshotPanel;
        bool screenshotControlsPresent = screenshotQueued
            && screenshotComposer?.GetElement("shot-scale") is GuiElementAtlasChoice
            && screenshotComposer.GetElement("shot-area") is GuiElementAtlasChoice
            && screenshotComposer.GetElement("shot-preview") != null
            && screenshotComposer.GetElement("shot-preview-open")
                is GuiElementAtlasButton
            && screenshotComposer.GetElement("shot-take") is GuiElementAtlasButton;
        bool screenshotPreviewButtonOpened = screenshotControlsPresent
            && ClickAtlasControlForAutomatedTest(
                screenshotComposer,
                "shot-preview-open"
            )
            && screenshotPreviewOpening;
        bool screenshotPreviewEscapeClosed = false;
        if (screenshotPreviewButtonOpened)
        {
            // This compact-test probe verifies the real button binding and
            // the first Escape return path without consuming the later
            // fresh-frame preview exercise.
            screenshotPreviewEscapeClosed = HandleEscape()
                && !screenshotPreviewOpen
                && !screenshotPreviewOpening
                && ScreenshotOptionsOpen;
            OpenBottomPanelImmediately(AtlasPanelSection.ScreenshotOptions);
            screenshotComposer = screenshotPanel;
        }
        bool screenshotFilterControlsMovedToPreview = screenshotControlsPresent
            && screenshotComposer?.GetElement("shot-filter-preset") == null
            && screenshotComposer?.GetElement("shot-filter-enabled") == null
            && screenshotComposer?.GetElement("shot-filter-reset") == null;
        bool screenshotCompactScrollPassed =
            ScreenshotOptionsScrollMaximum(320, 150) > 0
            && ScreenshotOptionsControlsReachableAtHeight(320, 150);
        bool screenshotOffPresetPassed = false;
        bool screenshotAtlasReliefPresetPassed = false;
        bool screenshotAllPresetsPassed = false;
        bool screenshotSliderSelectsCustomPassed = false;
        bool screenshotFilterResetPassed = false;
        if (screenshotFilterControlsMovedToPreview)
        {
            OnScreenshotFilterPresetChanged("off", true);
            screenshotOffPresetPassed = !config.ScreenshotFiltersEnabled
                && ScreenshotFilterPresetIndex == 0
                && screenshotComposer?.GetElement("shot-filter-tuning") == null
                && screenshotComposer?.GetElement("shot-filter-reset") == null;
            OnScreenshotFilterPresetChanged("atlas-relief", true);
            screenshotAtlasReliefPresetPassed = config.ScreenshotFiltersEnabled
                && ScreenshotFilterPresetIndex == 2;
            string[] smokePresets =
            {
                "off",
                "natural",
                "atlas-relief",
                "cinematic",
                "old-photo",
                "western",
                "custom"
            };
            screenshotAllPresetsPassed = true;
            foreach (string smokePreset in smokePresets)
            {
                OnScreenshotFilterPresetChanged(smokePreset, true);
                screenshotAllPresetsPassed = screenshotAllPresetsPassed
                    && ScreenshotFilterPresetIndex
                        == Array.IndexOf(
                            AtlasScreenshotFilterSettings.PresetValues,
                            smokePreset
                        );
            }
            OnScreenshotFilterPresetChanged("custom", true);
            // A numeric control must leave a preset editing mode and enter
            // Custom before it changes any persisted value. Exercise the
            // real callback with a preset value first, then restore the
            // complete preference snapshot below.
            OnScreenshotFilterPresetChanged("natural", true);
            int intensityProbe = Math.Clamp(
                config.ScreenshotFilterIntensityPercent + 5,
                0,
                200
            );
            OnScreenshotFilterIntensityChanged(intensityProbe);
            screenshotSliderSelectsCustomPassed =
                config.ScreenshotFiltersEnabled
                && AtlasScreenshotFilterSettings.NormalizePreset(
                    config.ScreenshotFilterPreset
                ) == "custom";
            OnScreenshotFilterPresetChanged("custom", true);
            screenshotFilterResetPassed =
                screenshotComposer?.GetElement("shot-filter-tuning") == null
                && screenshotComposer?.GetElement("shot-filter-reset") == null
                && ResetScreenshotFilterToDefault()
                && !config.ScreenshotFiltersEnabled
                && ScreenshotFilterPresetIndex == 0;
            automatedOriginalScreenshotFilters.Restore(config);
            if (automatedScreenshotSequenceEnabled)
            {
                // The UI exercise restores the user's preferences, while
                // the optional two-job regression must still begin with its
                // exact unfiltered baseline.
                AtlasScreenshotFilterSettings.ApplyPresetToConfig(
                    config,
                    "off"
                );
            }
            else if (automatedScreenshotIntensityZeroEnabled)
            {
                AtlasScreenshotFilterSettings.ApplyPresetToConfig(
                    config,
                    "custom"
                );
                config.ScreenshotFiltersEnabled = true;
                config.ScreenshotFilterIntensityPercent = 0;
            }
            SyncScreenshotSettingsControls();
        }
        int screenshotScaleBefore = config.ScreenshotScale;
        bool screenshotScaleChanged = screenshotControlsPresent
            && ClickAtlasControlForAutomatedTest(screenshotComposer, "shot-scale");
        if (screenshotScaleChanged)
        {
            OnScreenshotScaleChanged(
                screenshotScaleBefore == 4 ? "1" : "4",
                true
            );
            screenshotScaleChanged = config.ScreenshotScale != screenshotScaleBefore;
            OnScreenshotScaleChanged(screenshotScaleBefore.ToString(), true);
        }
        int captureAreaBefore = AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
            config.ScreenshotCaptureAreaPercent
        );
        bool captureAreaChanged = screenshotControlsPresent
            && ClickAtlasControlForAutomatedTest(screenshotComposer, "shot-area");
        if (captureAreaChanged)
        {
            int alternateArea = captureAreaBefore == 25 ? 100 : 25;
            OnScreenshotCaptureAreaChanged(alternateArea.ToString(), true);
            captureAreaChanged = config.ScreenshotCaptureAreaPercent != captureAreaBefore;
            OnScreenshotCaptureAreaChanged(captureAreaBefore.ToString(), true);
        }
        bool screenshotPreviewValid = screenshotControlsPresent
            && ValidateScreenshotPreviewRange();

        // Quick screenshot is a thin binding to the same tiled job. Start it
        // once, then cancel it immediately so this UI test leaves no PNG and
        // the later full capture still exercises the normal pipeline.
        bool quickButtonPresent = overlay?.GetElement("quick-screenshot-button")
            is GuiElementAtlasButton;
        bool quickAccepted = quickButtonPresent && TakeQuickScreenshot();
        bool quickStarted = pendingScreenshotRequest;
        if (quickStarted) CancelScreenshotCapture();

        bool mapQueued = ClickAtlasControlForAutomatedTest(
                overlay,
                "map-options-button"
            )
            && queuedBottomPanelSection == AtlasPanelSection.MapOptions;
        OpenBottomPanelImmediately(AtlasPanelSection.MapOptions);
        GuiComposer? mapComposer = mapLayerPanel;
        bool mapControlsPresent = mapQueued
            && mapComposer?.GetElement("map-layer") is GuiElementAtlasChoice
            && mapComposer.GetElement("map-layers") == null;
        bool mapLayerResetToTextured = mapControlsPresent
            && ExerciseAutomatedMapLayerChoice();
        // Every layer change rebuilds the bottom panel, so the captured
        // composer reference is stale after the arrow sequence.
        mapComposer = mapLayerPanel;
        bool mapClosed = ClickAtlasControlForAutomatedTest(mapComposer, "map-layer-toggle")
            && bottomPanelSection == AtlasPanelSection.MapOptions;
        OpenBottomPanelImmediately(AtlasPanelSection.MapOptions);

        bool instrumentOpened = ClickAtlasControlForAutomatedTest(
                overlay,
                "instrument-button"
            );
        OpenBottomPanelImmediately(AtlasPanelSection.Instrument);
        bool instrumentControlsPresent = instrumentOpened
            && bottomPanelSection == AtlasPanelSection.Instrument
            && bottomPanel?.GetElement("handheld-instrument") is GuiElementAtlasChoice;

        bool searchOpened = false;
        bool searchControlsPresent = false;
        if (SearchModeActive)
        {
            ResetBottomPanelState();
            searchOpened = ClickAtlasControlForAutomatedTest(overlay, "search-button");
            searchControlsPresent = searchOpened
                && searchPanel?.GetTextInput("search-input") != null;
        }

        bool creativeOpened = false;
        bool creativeClosed = false;
        bool visualOpened = false;
        bool visualClosed = false;
        if (accessAvailable)
        {
            OpenBottomPanelImmediately(AtlasPanelSection.Settings);
            bool creativeQueued = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "creative-settings-button"
                )
                && queuedBottomPanelSection == AtlasPanelSection.Creative;
            OpenBottomPanelImmediately(AtlasPanelSection.Creative);
            creativeOpened = creativeQueued
                && creativeSettingsModalOpen
                && creativeSettingsModal?.GetElement("camera-angle-lock")
                    is GuiElementAtlasSwitch;
            creativeClosed = creativeOpened
                && ClickAtlasControlForAutomatedTest(
                    creativeSettingsModal,
                    "creative-settings-close"
                )
                && queuedBottomPanelSection == AtlasPanelSection.Settings;
            OpenBottomPanelImmediately(AtlasPanelSection.Settings);
            visualOpened = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "visual-lab-open"
                )
                && queuedBottomPanelSection == AtlasPanelSection.VisualLab;
            OpenBottomPanelImmediately(AtlasPanelSection.VisualLab);
            visualOpened = visualOpened
                && visualLabModal?.GetAtlasSlider("atlas-exposure") != null
                && visualLabModal?.GetAtlasSlider("cave-mask-brightness") != null;
            visualClosed = visualOpened
                && ClickAtlasControlForAutomatedTest(
                    visualLabModal,
                    "visual-lab-back"
                )
                && queuedBottomPanelSection == AtlasPanelSection.Settings;
        }

        float originalYaw = targetYawDegrees;
        float originalPitch = targetPitchDegrees;
        config.CameraAngleLocked = true;
        ApplyRotationDrag(8, 24);
        bool cameraAngleStayedLocked = Math.Abs(targetPitchDegrees - originalPitch) < 0.001f;
        bool cameraYawStayedFree = Math.Abs(
            NormalizeSignedDegrees(targetYawDegrees - originalYaw)
        ) > 0.001f;
        targetYawDegrees = originalYaw;
        targetPitchDegrees = originalPitch;
        config.CameraAngleLocked = false;
        bool safeSurfaceWasActive = SurfaceSafetyEnabled;
        config.CaveModeEnabled = true;
        bool caveModeActivated = !SurfaceSafetyEnabled;
        PrepareSurfaceSafetyFilter();

        UpdateToolbarTooltip(
            (int)Math.Round(overlay?.GetAtlasButton("settings-button")?.Bounds.absX ?? 0),
            (int)Math.Round(overlay?.GetAtlasButton("settings-button")?.Bounds.absY ?? 0)
        );
        bool tooltipPassed = toolbarTooltipText == "Settings";
        UpdateToolbarTooltip(-1, -1);
        int bottomPanelAliasCount = 0;
        if (ReferenceEquals(settingsModal, bottomPanel)) bottomPanelAliasCount++;
        if (ReferenceEquals(performanceModal, bottomPanel)) bottomPanelAliasCount++;
        if (ReferenceEquals(creativeSettingsModal, bottomPanel)) bottomPanelAliasCount++;
        if (ReferenceEquals(visualLabModal, bottomPanel)) bottomPanelAliasCount++;
        if (ReferenceEquals(searchPanel, bottomPanel)) bottomPanelAliasCount++;
        if (ReferenceEquals(mapLayerPanel, bottomPanel)) bottomPanelAliasCount++;
        if (ReferenceEquals(screenshotPanel, bottomPanel)) bottomPanelAliasCount++;
        if (ReferenceEquals(unitPanel, bottomPanel)) bottomPanelAliasCount++;
        bool oneBottomPanel = bottomPanel != null && bottomPanelAliasCount == 1;
        bool responsiveBounds = overlay?.GetAtlasButton("settings-button") != null
            && (!hasBottomPanelGeometry
                || (bottomPanelGeometry.Height <= capi.Render.FrameHeight * 0.5
                    && bottomPanelGeometry.Width > 0));
        int renderedEntityCount = exactChunkRenderer?.LastRenderedEntityCount ?? 0;
        bool heldItemsSuppressed = renderedEntityCount > 0
            && exactChunkRenderer?.LastSuppressedHeldItemCount == renderedEntityCount;

        ResetBottomPanelState();
        automatedSmokeTestInterfaceControlsPassed = toolbarPresent
            && accessAvailable
            && initialFocusReleased
            && settingsOpenedByClick
            && settingsControlsPresent
            && mapLayersToggled
            && mapLayersRestored
            && skipToggled
            && skipRestored
            && performanceBack
            && performanceOpened
            && automatedSmokeTestPerformanceModeSelected
            && presentationControlPresent
            && fixedLightingPrepared
            && sliderBoundaryHandled
            && sliderBoundaryClamped
            && liveLightingRestored
            && timeInstrumentSelected
            && settingsClosed
            && screenshotControlsPresent
            && screenshotPreviewButtonOpened
            && screenshotPreviewEscapeClosed
            && screenshotFilterControlsMovedToPreview
            && screenshotCompactScrollPassed
            && screenshotOffPresetPassed
            && screenshotAtlasReliefPresetPassed
            && screenshotAllPresetsPassed
            && screenshotSliderSelectsCustomPassed
            && screenshotFilterResetPassed
            && screenshotScaleChanged
            && captureAreaChanged
            && screenshotPreviewValid
            && quickAccepted
            && (quickStarted || tileScreenshot.State == AtlasScreenshotJobState.Cancelled)
            && mapControlsPresent
            && mapLayerResetToTextured
            && mapClosed
            && instrumentControlsPresent
            && (!SearchModeActive || (searchOpened && searchControlsPresent))
            && (!accessAvailable || (creativeOpened && creativeClosed && visualOpened && visualClosed))
            && tooltipPassed
            && oneBottomPanel
            && responsiveBounds
            && caveModeActivated
            && cameraAngleStayedLocked
            && cameraYawStayedFree
            && heldItemsSuppressed;
        if (automatedSmokeTestInterfaceControlsPassed)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated compact UI test passed: toolbar, tooltips, one shared bottom panel, rapid section switching, Settings, scrollable screenshot options at constrained height, quick screenshot binding, map options, search focus, instrument, Creative/Cheat controls, Hide UI/Escape and responsive bounds were exercised for {0} living models.",
                renderedEntityCount
            );
        }
        else
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated compact UI test failed: toolbar={0}, settings={1}/{2}, screenshot={3}, previewButton/escape={4}/{5}, filters={6}, compactScroll={7}, presets={8}/{9}, sliderCustom={10}, reset={11}, scale={12}, area={13}, preview={14}, quick={15}/{16}, map={17}/{18}, instrument={19}, search={20}, creative={21}/{22}, visual={23}/{24}, tooltip={25}, onePanel={26}, bounds={27}, performance={28}, heldItems={29}/{30}.",
                toolbarPresent,
                settingsOpenedByClick,
                settingsControlsPresent,
                screenshotControlsPresent,
                screenshotPreviewButtonOpened,
                screenshotPreviewEscapeClosed,
                screenshotFilterControlsMovedToPreview,
                screenshotCompactScrollPassed,
                screenshotOffPresetPassed,
                screenshotAtlasReliefPresetPassed,
                screenshotSliderSelectsCustomPassed,
                screenshotFilterResetPassed,
                screenshotScaleChanged,
                captureAreaChanged,
                screenshotPreviewValid,
                quickAccepted,
                quickStarted,
                mapControlsPresent,
                mapLayerResetToTextured,
                instrumentControlsPresent,
                searchOpened && searchControlsPresent,
                creativeOpened,
                creativeClosed,
                visualOpened,
                visualClosed,
                tooltipPassed,
                oneBottomPanel,
                responsiveBounds,
                automatedSmokeTestPerformanceModeSelected,
                exactChunkRenderer?.LastSuppressedHeldItemCount ?? 0,
                renderedEntityCount
            );
        }
        return;

#if false
        if (automatedSmokeTestInterfaceControlsAttempted) return;
        automatedSmokeTestInterfaceControlsAttempted = true;

        bool accessAvailable = CreativeCheatSettingsAvailable;
        bool settingsOpenedByClick = ClickAtlasControlForAutomatedTest(
                overlay,
                "settings-button"
            )
            && settingsModalOpen;
        bool settingsClosedByToggle = settingsOpenedByClick
            && ClickAtlasControlForAutomatedTest(overlay, "settings-button")
            && !SettingsHierarchyOpen;
        bool settingsReopenedByToggle = settingsClosedByToggle
            && ClickAtlasControlForAutomatedTest(overlay, "settings-button")
            && settingsModalOpen;
        bool mapLayersBeforeClick = config.MapLayersEnabled;
        bool settingsSwitchClicked = settingsReopenedByToggle
            && ClickAtlasControlForAutomatedTest(settingsModal, "map-layers")
            && config.MapLayersEnabled != mapLayersBeforeClick;
        bool settingsSwitchRestored = settingsSwitchClicked
            && ClickAtlasControlForAutomatedTest(settingsModal, "map-layers")
            && config.MapLayersEnabled == mapLayersBeforeClick;
        bool skipOpeningBeforeClick = config.SkipOpeningAnimation;
        bool skipOpeningClicked = settingsOpenedByClick
            && ClickAtlasControlForAutomatedTest(
                settingsModal,
                "skip-opening-animation"
            )
            && config.SkipOpeningAnimation != skipOpeningBeforeClick;
        bool skipOpeningRestored = skipOpeningClicked
            && ClickAtlasControlForAutomatedTest(
                settingsModal,
                "skip-opening-animation"
            )
            && config.SkipOpeningAnimation == skipOpeningBeforeClick;
        bool performanceOpenedByClick = settingsOpenedByClick
            && ClickAtlasControlForAutomatedTest(settingsModal, "performance-open")
            && performanceModalOpen;
        if (!config.PerformanceLightingEnabled)
        {
            OnPerformanceLightingToggled(true);
        }
        bool flatLightingSelected = performanceOpenedByClick
            && ClickAtlasControlForAutomatedTest(
                performanceModal,
                "performance-lighting"
            )
            && !config.PerformanceLightingEnabled;
        if (config.HideVegetation)
        {
            OnHideVegetationToggled(false);
        }
        bool vegetationSelected = performanceOpenedByClick
            && ClickAtlasControlForAutomatedTest(
                performanceModal,
                "hide-vegetation"
            )
            && config.HideVegetation;
        automatedSmokeTestPerformanceModeSelected =
            flatLightingSelected
            && vegetationSelected
            && performanceModal?.GetElement("performance-lighting")
                is GuiElementAtlasSwitch;
        if (automatedSmokeTestPerformanceModeSelected)
        {
            // The next frame must render the selected visual controls before
            // the screenshot/modal exercise changes presentation again.
            lastAtlasWorldRenderMilliseconds = 0;
        }
        bool performanceClosedByClick = performanceOpenedByClick
            && ClickAtlasControlForAutomatedTest(
                performanceModal,
                "performance-back"
            )
            && settingsModalOpen
            && !performanceModalOpen;
        bool presentationSwitchAvailable = performanceClosedByClick
            && settingsModal?.GetElement("render-on-scroll")
                is GuiElementAtlasSwitch;
        bool liveLightingBeforeSliderTest = config.LiveLightingEnabled;
        bool fixedLightingPrepared = !liveLightingBeforeSliderTest
            || (settingsOpenedByClick
                && ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "live-lighting"
                )
                && !config.LiveLightingEnabled);
        int fixedSunHourBeforeDrag = config.FixedSunHour;
        bool dragSliderToMaximum = fixedSunHourBeforeDrag != 23;
        bool sliderBoundaryDragHandled = fixedLightingPrepared
            && DragAtlasSliderBeyondBoundsForAutomatedTest(
                settingsModal,
                "fixed-sun-hour",
                dragSliderToMaximum
            );
        bool sliderBoundaryDragClamped = config.FixedSunHour
            == (dragSliderToMaximum ? 23 : 0);
        OnFixedSunHourChanged(fixedSunHourBeforeDrag);
        settingsModal?.GetAtlasSlider("fixed-sun-hour")?.SetValue(fixedSunHourBeforeDrag);
        bool liveLightingRestored = !liveLightingBeforeSliderTest
            || (ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "live-lighting"
                )
                && config.LiveLightingEnabled);
        OnHandheldInstrumentChoiceChanged("compass", true);
        bool timeInstrumentSelected = ClickAtlasControlForAutomatedTest(
                settingsModal,
                "handheld-instrument"
            )
            && config.ShowPlayerCompass
            && HandheldInstrumentMode == "time";
        if (automatedSmokeTestPerformanceModeSelected)
        {
            // Live/fixed lighting controls intentionally re-enable directional
            // lighting. Re-apply the already-tested developer visual choice so
            // one real atlas frame observes flat lighting and hidden vegetation
            // before the smoke test restores the player's preferences.
            OnPerformanceLightingToggled(false);
            OnHideVegetationToggled(true);
            lastAtlasWorldRenderMilliseconds = 0;
        }
        bool settingsClosedByClick = settingsOpenedByClick
            && ClickAtlasControlForAutomatedTest(settingsModal, "settings-close")
            && !settingsModalOpen;
        // The screenshot panel lives in the main atlas UI under the modals.
        // Its clicks are only valid while the settings modal is closed, and
        // the collapsed panel must expand first. The toggle defers its own
        // recomposition, so the exercise applies it synchronously.
        bool screenshotToggleClicked = settingsClosedByClick
            && ClickAtlasControlForAutomatedTest(screenshotPanel, "shot-toggle")
            && ScreenshotPanelExpanded;
        if (screenshotToggleClicked)
        {
            pendingScreenshotPanelRecompose = false;
            RecomposeScreenshotPanel();
        }
        bool screenshotPanelExpanded = screenshotToggleClicked
            && screenshotPanel?.GetElement("shot-scale") is GuiElementAtlasChoice;
        bool screenshotAreaControlPresent = screenshotPanelExpanded
            && screenshotPanel?.GetElement("shot-area") is GuiElementAtlasChoice
            && screenshotPanel?.GetElement("shot-preview") != null;
        int screenshotScaleBefore = config.ScreenshotScale;
        bool screenshotScaleChanged = false;
        if (screenshotPanelExpanded
            && ClickAtlasControlForAutomatedTest(screenshotPanel, "shot-scale"))
        {
            // The dropdown list itself cannot be driven without real mouse
            // input; invoke its handler the same way the player's selection
            // would, then restore the original value.
            OnScreenshotScaleChanged(
                screenshotScaleBefore == 4 ? "1" : "4",
                true
            );
            screenshotScaleChanged = config.ScreenshotScale != screenshotScaleBefore;
            OnScreenshotScaleChanged(screenshotScaleBefore.ToString(), true);
        }
        bool screenshotScaleRestored = screenshotScaleChanged
            && config.ScreenshotScale == screenshotScaleBefore;
        int screenshotAreaBefore =
            AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
                config.ScreenshotCaptureAreaPercent
            );
        bool screenshotAreaChanged = false;
        if (screenshotAreaControlPresent
            && ClickAtlasControlForAutomatedTest(screenshotPanel, "shot-area"))
        {
            int alternateArea = screenshotAreaBefore == 25 ? 100 : 25;
            OnScreenshotCaptureAreaChanged(alternateArea.ToString(), true);
            screenshotAreaChanged = config.ScreenshotCaptureAreaPercent
                == alternateArea;
            OnScreenshotCaptureAreaChanged(screenshotAreaBefore.ToString(), true);
        }
        bool screenshotAreaRestored = screenshotAreaChanged
            && config.ScreenshotCaptureAreaPercent == screenshotAreaBefore;
        bool screenshotPreviewRangePassed = screenshotAreaControlPresent
            && ValidateScreenshotPreviewRange();
        // Collapse and re-expand the map-layer panel. The collapsed panel
        // lacks the dropdown and status elements, which previously crashed
        // the per-frame status updates.
        bool mapLayerToggleClicked = settingsClosedByClick
            && ClickAtlasControlForAutomatedTest(mapLayerPanel, "map-layer-toggle")
            && mapLayerPanelCollapsed;
        if (mapLayerToggleClicked)
        {
            pendingMapLayerPanelRecompose = false;
            RecomposeMapLayerPanel();
        }
        bool mapLayerCollapsedSafe = mapLayerToggleClicked
            && mapLayerPanel?.GetElement("map-layer") == null;
        bool mapLayerReExpanded = mapLayerCollapsedSafe
            && ClickAtlasControlForAutomatedTest(mapLayerPanel, "map-layer-toggle")
            && !mapLayerPanelCollapsed;
        if (mapLayerReExpanded)
        {
            pendingMapLayerPanelRecompose = false;
            RecomposeMapLayerPanel();
        }
        mapLayerReExpanded = mapLayerReExpanded
            && mapLayerPanel?.GetElement("map-layer") is GuiElementAtlasChoice;
        bool creativeOpenedByClick = accessAvailable
            && ClickAtlasControlForAutomatedTest(
                creativeSettingsShortcut,
                "creative-settings-button"
            )
            && creativeSettingsModalOpen;
        bool creativeClosedByClick = creativeOpenedByClick
            && ClickAtlasControlForAutomatedTest(
                creativeSettingsModal,
                "creative-settings-close"
            )
            && !creativeSettingsModalOpen;

        float originalYaw = targetYawDegrees;
        float originalPitch = targetPitchDegrees;
        config.CameraAngleLocked = true;
        ApplyRotationDrag(8, 24);
        bool cameraAngleStayedLocked = Math.Abs(targetPitchDegrees - originalPitch) < 0.001f;
        bool cameraYawStayedFree = Math.Abs(
            NormalizeSignedDegrees(targetYawDegrees - originalYaw)
        ) > 0.001f;
        targetYawDegrees = originalYaw;
        targetPitchDegrees = originalPitch;
        config.CameraAngleLocked = false;

        bool safeSurfaceWasActive = SurfaceSafetyEnabled;
        config.CaveModeEnabled = true;
        bool caveModeActivated = !SurfaceSafetyEnabled;
        PrepareSurfaceSafetyFilter();

        bool hidden = ClickAtlasControlForAutomatedTest(overlay, "hide-ui-button")
            && interfaceHidden;
        // Only press Escape when the UI really is hidden. Otherwise this
        // destructive call could close the atlas or an open modal instead of
        // restoring the interface.
        bool restored = hidden && HandleEscape() && !interfaceHidden && IsOpened();
        bool neumorphicControls = overlay?.GetElement("settings-button")
                is GuiElementAtlasButton
            && settingsModal?.GetElement("map-layers") is GuiElementAtlasSwitch
            && settingsModal?.GetElement("skip-opening-animation")
                is GuiElementAtlasSwitch
            && settingsModal?.GetElement("scroll-realtime-weather")
                is GuiElementAtlasSwitch
            && settingsModal?.GetElement("performance-open")
                is GuiElementAtlasButton
            && settingsModal?.GetElement("handheld-instrument")
                is GuiElementAtlasChoice
            && performanceModal?.GetElement("hide-vegetation")
                is GuiElementAtlasSwitch
            && creativeSettingsModal?.GetElement("camera-angle-lock")
                is GuiElementAtlasSwitch
            && screenshotPanel?.GetElement("shot-scale")
                is GuiElementAtlasChoice
            && screenshotPanel?.GetElement("shot-area")
                is GuiElementAtlasChoice
            && screenshotPanel?.GetElement("shot-preview") != null
            && screenshotPanel?.GetElement("shot-take")
                is GuiElementAtlasButton
            && mapLayerPanel?.GetElement("map-layer") is GuiElementAtlasChoice;
        int renderedEntityCount = exactChunkRenderer?.LastRenderedEntityCount ?? 0;
        bool heldItemsSuppressed = renderedEntityCount > 0
            && exactChunkRenderer?.LastSuppressedHeldItemCount == renderedEntityCount;
        automatedSmokeTestInterfaceControlsPassed = accessAvailable
            && settingsOpenedByClick
            && settingsClosedByToggle
            && settingsReopenedByToggle
            && settingsSwitchClicked
            && settingsSwitchRestored
            && skipOpeningClicked
            && skipOpeningRestored
            && automatedSmokeTestPerformanceModeSelected
            && performanceClosedByClick
            && presentationSwitchAvailable
            && fixedLightingPrepared
            && sliderBoundaryDragHandled
            && sliderBoundaryDragClamped
            && liveLightingRestored
            && timeInstrumentSelected
            && screenshotPanelExpanded
            && screenshotScaleChanged
            && screenshotScaleRestored
            && screenshotAreaChanged
            && screenshotAreaRestored
            && screenshotPreviewRangePassed
            && mapLayerToggleClicked
            && mapLayerCollapsedSafe
            && mapLayerReExpanded
            && settingsClosedByClick
            && creativeOpenedByClick
            && creativeClosedByClick
            && MapLayerControlsVisible
            && SearchModeActive
            && safeSurfaceWasActive
            && caveModeActivated
            && cameraAngleStayedLocked
            && cameraYawStayedFree
            && hidden
            && restored
            && neumorphicControls
            && heldItemsSuppressed;
        if (automatedSmokeTestInterfaceControlsPassed)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test exercised the toggleable Settings panel, compact neumorphic controls, Compass/Time instrument selector, map-layer and skip-opening switches, Creative/Cheat cave/search/camera controls, the independent Screenshot resolution and capture-area selectors with 2x-8x preview validation and a queued tiled capture, Escape-restored hidden UI and held-item suppression for {0} living models; debounced presentation coverage is running next.",
                renderedEntityCount
            );
        }
        else
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated interface-controls test failed: access={0}, settingsOpen/toggle/reopen={1}/{2}/{3}, mapLayerSwitch={4}/{5}, skipOpening={6}/{7}, presentationControl={8}, fixedLighting={9}/{10}, sliderBoundary={11}/{12}, timeInstrument={13}, screenshotPanel={14}, screenshotScale={15}/{16}, screenshotArea={17}/{18}, screenshotPreviewRange={19}, mapLayerToggle={20}/{21}/{22}, settingsClose={23}, creativeOpen={24}, creativeClose={25}, layers={26}, search={27}, safeSurface={28}, cave={29}, angleLock={30}, yaw={31}, hidden={32}, restored={33}, neumorphic={34}, heldItems={35}/{36}.",
                accessAvailable,
                settingsOpenedByClick,
                settingsClosedByToggle,
                settingsReopenedByToggle,
                settingsSwitchClicked,
                settingsSwitchRestored,
                skipOpeningClicked,
                skipOpeningRestored,
                presentationSwitchAvailable,
                fixedLightingPrepared,
                liveLightingRestored,
                sliderBoundaryDragHandled,
                sliderBoundaryDragClamped,
                timeInstrumentSelected,
                screenshotPanelExpanded,
                screenshotScaleChanged,
                screenshotScaleRestored,
                screenshotAreaChanged,
                screenshotAreaRestored,
                screenshotPreviewRangePassed,
                mapLayerToggleClicked,
                mapLayerCollapsedSafe,
                mapLayerReExpanded,
                settingsClosedByClick,
                creativeOpenedByClick,
                creativeClosedByClick,
                MapLayerControlsVisible,
                SearchModeActive,
                safeSurfaceWasActive,
                caveModeActivated,
                cameraAngleStayedLocked,
                cameraYawStayedFree,
                hidden,
                restored,
                neumorphicControls,
                exactChunkRenderer?.LastSuppressedHeldItemCount ?? 0,
                renderedEntityCount
            );
        }
    }
#endif
    }

    private bool ValidateScreenshotPreviewRange()
    {
        AtlasViewportBounds viewport = AtlasViewport;
        float viewportAspect = viewport.Width
            / (float)Math.Max(1, viewport.Height);
        int[] captureAreas = { 100, 75, 50, 25 };
        int previewCount = 0;
        int limitedCount = 0;
        bool passed = true;
        for (
            int resolutionScale = AtlasTiledScreenshot.MinimumResolutionScale;
            resolutionScale <= AtlasTiledScreenshot.MaximumResolutionScale;
            resolutionScale++
        )
        {
            foreach (int captureAreaPercent in captureAreas)
            {
                AtlasScreenshotPreview preview = tileScreenshot.GetPreview(
                    resolutionScale,
                    captureAreaPercent,
                    viewportAspect
                );
                AtlasScreenshotCaptureLayout layout =
                    tileScreenshot.GetCaptureLayout(
                        resolutionScale,
                        captureAreaPercent,
                        viewportAspect
                    );
                float expectedDetail = resolutionScale
                    * (100f / captureAreaPercent);
                int expectedFrameWidth = Math.Clamp(
                    (int)Math.Round(
                        layout.StoredWidth * captureAreaPercent / 100f
                    ),
                    1,
                    Math.Max(1, layout.StoredWidth)
                );
                int expectedFrameHeight = Math.Clamp(
                    (int)Math.Round(
                        layout.StoredHeight * captureAreaPercent / 100f
                    ),
                    1,
                    Math.Max(1, layout.StoredHeight)
                );
                passed = passed
                    && preview.IsValid
                    && layout.IsValid
                    && preview.OutputWidth > 0
                    && preview.OutputHeight > 0
                    && preview.OutputPixels
                        <= AtlasTiledScreenshot.MaximumStitchedPixels
                    && Math.Abs(
                        preview.RequestedDetailFactor - expectedDetail
                    ) < 0.001f
                    && layout.CaptureFrameWidth == expectedFrameWidth
                    && layout.CaptureFrameHeight == expectedFrameHeight
                    && layout.CaptureFrameX >= layout.ReadX
                    && layout.CaptureFrameY >= layout.ReadY
                    && layout.CaptureFrameX + layout.CaptureFrameWidth
                        <= layout.ReadX + layout.StoredWidth
                    && layout.CaptureFrameY + layout.CaptureFrameHeight
                        <= layout.ReadY + layout.StoredHeight
                    && (captureAreaPercent != 100
                        || (layout.CaptureFrameX == layout.ReadX
                            && layout.CaptureFrameY == layout.ReadY
                            && layout.CaptureFrameWidth == layout.StoredWidth
                            && layout.CaptureFrameHeight == layout.StoredHeight));
                previewCount++;
                if (preview.WasDownsampled) limitedCount++;
            }
        }

        if (passed)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated screenshot preview range passed for {0} combinations (1x-8x resolution, 100/75/50/25% capture area); {1} combinations are explicitly marked as pixel-budget limited.",
                previewCount,
                limitedCount
            );
        }
        else
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated screenshot preview range failed for one or more of {0} resolution/area combinations.",
                previewCount
            );
        }
        return passed;
    }

    private bool ValidateScreenshotPreviewModalControls(out string diagnostic)
    {
        diagnostic = "";
        if (!screenshotPreviewOpen || screenshotPreviewModal == null)
        {
            diagnostic = "the preview modal is not open";
            return false;
        }
        if (!screenshotPreviewLayout.IsValid
            || screenshotPreviewSourceTextureId <= 0
            || screenshotPreviewBeforeBounds == null
            || screenshotPreviewAfterBounds == null)
        {
            diagnostic = "the frozen source, capture layout or image bounds are invalid";
            return false;
        }
        bool filteredRequested = config.ScreenshotFiltersEnabled
            && AtlasScreenshotFilterSettings.NormalizePreset(
                config.ScreenshotFilterPreset
            ) != "off";
        if (filteredRequested
            && (screenshotPreviewFilterBlocked
                || screenshotPreviewAfterTextureId <= 0
                || screenshotPreviewAfterTextureId
                    == screenshotPreviewSourceTextureId))
        {
            diagnostic = "the filtered AFTER framebuffer was not published";
            return false;
        }

        if (filteredRequested
            && screenshotPreviewSourceFramebuffer != null
            && screenshotFilterPass.LastOutputFramebuffer is FrameBufferRef filteredFramebuffer
            && filteredFramebuffer.Disposed == false)
        {
            if (!screenshotFilterPass.TryCompareReadback(
                    screenshotPreviewSourceFramebuffer,
                    filteredFramebuffer,
                    out AtlasScreenshotFilterReadbackDiagnostics rgbDiagnostics,
                    out string rgbError
                ))
            {
                diagnostic = $"same-source filtered RGB comparison failed: {rgbError}";
                return false;
            }

            if (rgbDiagnostics.IsExactRgb)
            {
                diagnostic = "filtered preview did not change any source RGB pixels";
                return false;
            }

            // Off is deliberately an identity operation in the production
            // path: AFTER receives the exact source texture instead of a
            // second render. Read the source twice as a compact regression
            // proof that the same-source RGB contract is exact and that the
            // preview readback path itself does not introduce a difference.
            if (!screenshotFilterPass.TryCompareReadback(
                    screenshotPreviewSourceFramebuffer,
                    screenshotPreviewSourceFramebuffer,
                    out AtlasScreenshotFilterReadbackDiagnostics offDiagnostics,
                    out string offError
                )
                || !offDiagnostics.IsExactRgb)
            {
                diagnostic = $"Off preview RGB identity failed: {offError ?? offDiagnostics.ToString()}";
                return false;
            }

            diagnostic =
                $"BEFORE/AFTER, 11 sliders, centered {screenshotPreviewLayout.CaptureAreaPercent}% capture frame; Off RGB exact, filtered output changed {rgbDiagnostics.ChangedPixelCount} pixels (max Δ{rgbDiagnostics.MaximumChannelDelta}), whole-image Blue={config.ScreenshotBlueBalancePercent}%";
        }
        else if (filteredRequested)
        {
            diagnostic = "filtered preview framebuffer is unavailable for same-source RGB validation";
            return false;
        }
        else
        {
            bool offIdentity = screenshotPreviewAfterTextureId
                == screenshotPreviewSourceTextureId;
            if (!offIdentity)
            {
                diagnostic = "Off preview does not publish the exact source texture";
                return false;
            }
            diagnostic =
                $"BEFORE/AFTER, 11 sliders, centered {screenshotPreviewLayout.CaptureAreaPercent}% capture frame; Off RGB identity";
        }

        if (screenshotPreviewExpectedEntityCount > 0
            && screenshotPreviewRenderedEntityCount <= 0)
        {
            diagnostic =
                $"preview lost living models: previous frame={screenshotPreviewExpectedEntityCount}, preview={screenshotPreviewRenderedEntityCount}";
            return false;
        }

        string[] requiredButtons =
        {
            "preview-close",
            "preview-filter-reset",
            "preview-back",
            "preview-take"
        };
        foreach (string key in requiredButtons)
        {
            if (screenshotPreviewModal.GetAtlasButton(key) == null)
            {
                diagnostic = $"missing preview button: {key}";
                return false;
            }
        }

        string[] requiredChoices =
        {
            "preview-scale",
            "preview-area",
            "preview-filter-preset"
        };
        foreach (string key in requiredChoices)
        {
            if (screenshotPreviewModal.GetAtlasChoice(key) == null)
            {
                diagnostic = $"missing preview choice: {key}";
                return false;
            }
        }
        if (screenshotPreviewModal.GetAtlasSwitch("preview-filter-enabled") == null)
        {
            diagnostic = "missing preview filter switch";
            return false;
        }

        string[] requiredSliders =
        {
            "preview-filter-intensity",
            "preview-filter-saturation",
            "preview-filter-contrast",
            "preview-filter-temperature",
            "preview-filter-shadow-tint",
            "preview-filter-ao",
            "preview-filter-indirect",
            "preview-filter-bloom",
            "preview-filter-red",
            "preview-filter-green",
            "preview-filter-blue"
        };
        foreach (string key in requiredSliders)
        {
            if (screenshotPreviewModal.GetAtlasSlider(key) == null)
            {
                diagnostic = $"missing preview slider: {key}";
                return false;
            }
        }

        // Element existence did not catch a previous responsive-layout bug:
        // clip-local children were given modal-absolute Y coordinates, leaving
        // a large empty center and placing every slider behind the footer.
        // When no scrolling is required, require every editing control to be
        // physically between the preview images and the action buttons.
        if (screenshotPreviewScrollMaximumValue <= 0
            && screenshotPreviewAfterBounds != null
            && screenshotPreviewModal.GetAtlasButton("preview-back")
                is GuiElementAtlasButton backButton)
        {
            double visibleTop = screenshotPreviewAfterBounds.absY
                + screenshotPreviewAfterBounds.OuterHeight;
            double visibleBottom = backButton.Bounds.absY;
            string[] visibleControlKeys =
            {
                "preview-scale",
                "preview-area"
            };
            foreach (string key in visibleControlKeys)
            {
                GuiElement? element = screenshotPreviewModal.GetElement(key);
                if (element == null
                    || element.Bounds.absY < visibleTop
                    || element.Bounds.absY + element.Bounds.OuterHeight
                        > visibleBottom)
                {
                    diagnostic = $"preview control is outside the visible body: {key}";
                    return false;
                }
            }
            foreach (string key in requiredSliders)
            {
                GuiElementAtlasSlider? slider =
                    screenshotPreviewModal.GetAtlasSlider(key);
                if (slider == null
                    || slider.Bounds.absY < visibleTop
                    || slider.Bounds.absY + slider.Bounds.OuterHeight
                        > visibleBottom)
                {
                    diagnostic = $"preview slider is outside the visible body: {key}";
                    return false;
                }
            }
        }

        if (screenshotPreviewModal.GetDynamicText("preview-info") == null
            || screenshotPreviewModal.GetDynamicText("preview-diagnostic") == null)
        {
            diagnostic = "missing preview dimensions or diagnostic text";
            return false;
        }

        return true;
    }

    private static bool IsEnvironmentFlagEnabled(string variableName)
    {
        string? value = Environment.GetEnvironmentVariable(variableName);
        return value == "1"
            || value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
            || value?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
    }

    private HashSet<string> CapturePublicScreenshotEntries()
    {
        string folder = capi.GetOrCreateDataPath(
            System.IO.Path.Combine("Screenshots", "ModernAtlas")
        );
        HashSet<string> entries = new(StringComparer.Ordinal);
        if (!Directory.Exists(folder)) return entries;
        foreach (string entry in Directory.GetFileSystemEntries(folder))
        {
            entries.Add(System.IO.Path.GetFullPath(entry));
        }
        return entries;
    }

    private bool ValidateAutomatedScreenshotOutputs(out string diagnostic)
    {
        int expectedCount = automatedScreenshotSequenceEnabled ? 2 : 1;
        List<string> outputPaths = new();
        if (automatedFirstScreenshotPath != null)
        {
            outputPaths.Add(automatedFirstScreenshotPath);
        }
        if (automatedScreenshotSequenceEnabled
            && automatedSecondScreenshotPath != null)
        {
            outputPaths.Add(automatedSecondScreenshotPath);
        }
        if (!automatedScreenshotSequenceEnabled
            && tileScreenshot.LastSavedPath != null)
        {
            outputPaths.Clear();
            outputPaths.Add(tileScreenshot.LastSavedPath);
        }

        HashSet<string> uniquePaths = new(StringComparer.Ordinal);
        foreach (string path in outputPaths)
        {
            if (!uniquePaths.Add(System.IO.Path.GetFullPath(path)))
            {
                diagnostic = "Two screenshot jobs resolved to the same output path.";
                return false;
            }
            if (!File.Exists(path))
            {
                diagnostic = $"Committed PNG is missing: {path}";
                return false;
            }
            try
            {
                AtlasTiledScreenshot.ValidateCommittedPngForAutomation(path);
            }
            catch (Exception exception)
            {
                diagnostic = $"PNG cannot be read completely ({path}): {exception.Message}";
                return false;
            }
        }
        if (outputPaths.Count != expectedCount)
        {
            diagnostic =
                $"Expected {expectedCount} committed PNG files, observed {outputPaths.Count}.";
            return false;
        }

        HashSet<string> before = automatedScreenshotPublicEntriesBefore
            ?? new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> after = CapturePublicScreenshotEntries();
        List<string> newEntries = new();
        foreach (string entry in after)
        {
            if (!before.Contains(entry)) newEntries.Add(entry);
        }
        int newPngCount = 0;
        foreach (string entry in newEntries)
        {
            if (Directory.Exists(entry))
            {
                diagnostic = $"A new directory appeared in the public folder: {entry}";
                return false;
            }
            if (!entry.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                diagnostic = $"A new public sidecar appeared: {entry}";
                return false;
            }
            newPngCount++;
        }
        if (newPngCount != expectedCount)
        {
            diagnostic =
                $"Expected exactly {expectedCount} new public PNG files, observed {newPngCount}.";
            return false;
        }

        if (automatedScreenshotSequenceEnabled)
        {
            if (tileScreenshot.LastCompletedFilterMode
                    != AtlasScreenshotFilterMode.Filtered
                || tileScreenshot.LastCompletedFilterPreset
                    != "atlas-relief"
                || tileScreenshot.LastCompletedFilterTileCount
                    != tileScreenshot.LastCompletedFilterTotalTiles
                || tileScreenshot.LastCompletedFilterTotalTiles < 2)
            {
                diagnostic =
                    $"Filtered job proof failed: mode={tileScreenshot.LastCompletedFilterMode}, preset={tileScreenshot.LastCompletedFilterPreset}, filteredTiles={tileScreenshot.LastCompletedFilterTileCount}/{tileScreenshot.LastCompletedFilterTotalTiles}.";
                return false;
            }
            AtlasValidityMaskAggregateDiagnostics? maskDiagnostics =
                tileScreenshot.LastCompletedValidityMaskDiagnostics;
            if (!automatedScreenshotMaskValidationPassed
                || automatedScreenshotMaskCoverageFailure
                || !maskDiagnostics.HasValue
                || maskDiagnostics.Value.TileCount
                    != tileScreenshot.LastCompletedFilterTotalTiles
                || !maskDiagnostics.Value.HasSensibleCoverage)
            {
                diagnostic = maskDiagnostics.HasValue
                    ? $"Filtered validity-mask proof failed: aggregate coverage {maskDiagnostics.Value}; empty tiles are legal, but the complete job must contain sensible aggregate coverage and at least one non-empty tile."
                    : "Filtered validity-mask proof failed: no mask diagnostics were committed.";
                return false;
            }
            if (outputPaths.Count != 2)
            {
                diagnostic = "The Off and Atlas Relief comparison PNGs are incomplete.";
                return false;
            }
            string firstHash = ComputeFileSha256(outputPaths[0]);
            string secondHash = ComputeFileSha256(outputPaths[1]);
            if (automatedScreenshotFilterReadbackFailure
                || automatedScreenshotFilterComparedTileCount
                    != tileScreenshot.LastCompletedFilterTotalTiles
                || automatedScreenshotFilterChangedPixelCount <= 0
                || automatedScreenshotFilterComparedPixelCount <= 0
                || automatedScreenshotFilterMaximumChangedRatio <= 0d
                || automatedScreenshotFilterMaximumChangedRatio > 1d)
            {
                diagnostic =
                    $"Same-source filtered readback proof failed: comparedTiles={automatedScreenshotFilterComparedTileCount}/{tileScreenshot.LastCompletedFilterTotalTiles}, changedPixels={automatedScreenshotFilterChangedPixelCount}/{automatedScreenshotFilterComparedPixelCount}, changedRatio={automatedScreenshotFilterMinimumChangedRatio:0.####}-{automatedScreenshotFilterMaximumChangedRatio:0.####}, maxChannelDelta={automatedScreenshotFilterMaximumChannelDelta}.";
                return false;
            }
            if (string.Equals(firstHash, secondHash, StringComparison.Ordinal))
            {
                diagnostic =
                    "Atlas Relief filtered PNG is byte-identical to the Off PNG even though the same-source readback proof reported changes.";
                return false;
            }
            automatedScreenshotFilterValidationPassed = true;
            capi.Logger.Notification(
                "[ModernAtlas] Automated filtered screenshot proof passed: all {0} tiles used preset Atlas Relief with aggregate validity {1}; same-source readback changed {2}/{3} pixels across tiles (ratio range {4:0.####}-{5:0.####}, maxChannelDelta={6}); Off/Atlas Relief PNG hashes differ ({7} vs {8}).",
                tileScreenshot.LastCompletedFilterTotalTiles,
                maskDiagnostics.Value,
                automatedScreenshotFilterChangedPixelCount,
                automatedScreenshotFilterComparedPixelCount,
                automatedScreenshotFilterMinimumChangedRatio,
                automatedScreenshotFilterMaximumChangedRatio,
                automatedScreenshotFilterMaximumChannelDelta,
                firstHash,
                secondHash
            );
        }
        else if (automatedScreenshotIntensityZeroEnabled)
        {
            if (automatedScreenshotFilterReadbackFailure
                || automatedScreenshotFilterComparedTileCount
                    != tileScreenshot.LastCompletedFilterTotalTiles
                || automatedScreenshotFilterChangedPixelCount != 0
                || automatedScreenshotFilterComparedPixelCount <= 0)
            {
                diagnostic =
                    $"Intensity=0 exact-RGB proof failed: comparedTiles={automatedScreenshotFilterComparedTileCount}/{tileScreenshot.LastCompletedFilterTotalTiles}, changedPixels={automatedScreenshotFilterChangedPixelCount}/{automatedScreenshotFilterComparedPixelCount}, ratio range={automatedScreenshotFilterMinimumChangedRatio:0.####}-{automatedScreenshotFilterMaximumChangedRatio:0.####}.";
                return false;
            }
            automatedScreenshotFilterValidationPassed = true;
            capi.Logger.Notification(
                "[ModernAtlas] Automated Intensity=0 screenshot proof passed: same-source readback was byte-identical in RGB for {0} tiles.",
                automatedScreenshotFilterComparedTileCount
            );
        }

        diagnostic =
            $"{expectedCount} unique PNG files are readable and no new public sidecars were created.";
        capi.Logger.Notification(
            "[ModernAtlas] Automated screenshot output validation passed: {0}",
            diagnostic
        );
        return true;
    }

    private static string ComputeFileSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private bool ValidateCancelledScreenshotOutputs()
    {
        HashSet<string> before = automatedScreenshotPublicEntriesBefore
            ?? new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> after = CapturePublicScreenshotEntries();
        if (before.Count != after.Count) return false;
        foreach (string entry in before)
        {
            if (!after.Contains(entry)) return false;
        }
        HashSet<string> beforeJobs = automatedScreenshotJobEntriesBefore
            ?? new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> afterJobs = CaptureScreenshotJobEntries();
        if (beforeJobs.Count != afterJobs.Count)
        {
            return false;
        }
        foreach (string entry in beforeJobs)
        {
            if (!afterJobs.Contains(entry)) return false;
        }
        return tileScreenshot.State == AtlasScreenshotJobState.Cancelled;
    }

    private HashSet<string> CaptureScreenshotJobEntries()
    {
        string jobRoot = capi.GetOrCreateDataPath(
            System.IO.Path.Combine(
                "ModData",
                "ModernAtlas",
                "ScreenshotJobs"
            )
        );
        HashSet<string> entries = new(StringComparer.Ordinal);
        if (!Directory.Exists(jobRoot)) return entries;
        foreach (string entry in Directory.GetFileSystemEntries(jobRoot))
        {
            entries.Add(System.IO.Path.GetFullPath(entry));
        }
        return entries;
    }

    private void AdvanceAutomatedPresentationSwitchTest()
    {
        if (!automatedSmokeTestActive
            || !automatedSmokeTestInterfaceControlsAttempted
            || !automatedSmokeTestInterfaceControlsPassed
            || automatedSmokeScreenshotPhase < AutomatedUiScreenshotPhaseCount
            || automatedSmokeScreenshotPreviewPending
            || screenshotPreviewOpening
            || screenshotPreviewOpen
            || automatedSmokeTestPresentationPassed
            || automatedSmokeTestPresentationPhase < 0
            || automatedSmokeTestPresentationPhase >= 5)
        {
            return;
        }

        switch (automatedSmokeTestPresentationPhase)
        {
            case 0:
            {
                if (!settingsModalOpen && bottomPanelAnimationActive)
                {
                    // The UI screenshot sequence ends by requesting Settings
                    // from the Visual Lab. The shared-panel state machine
                    // must finish that close-then-open transition before the
                    // debounce exercise starts; a valid queued request is not
                    // an error just because the logical Settings flag is
                    // still false for one or two render frames.
                    if (BottomPanelClosing
                        && queuedBottomPanelSection == AtlasPanelSection.Settings)
                    {
                        return;
                    }
                    return;
                }

                if (!settingsModalOpen
                    && (!ClickAtlasControlForAutomatedTest(
                            overlay,
                            "settings-button"
                        )
                        || !settingsModalOpen))
                {
                    FailAutomatedPresentationSwitchTest(
                        "could not open Settings before the first rapid sequence"
                    );
                    return;
                }

                automatedSmokeTestSettingsComposer = settingsModal;
                automatedSmokeTestPerformanceComposer = performanceModal;
                automatedSmokeTestCreativeComposer = creativeSettingsModal;
                automatedSmokeTestVisualLabComposer = visualLabModal;
                bool startedOnScroll = config.RenderOnScroll;
                bool firstClick = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "render-on-scroll"
                );
                bool secondClick = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "render-on-scroll"
                );
                bool queuedScrollFullscreenScroll = startedOnScroll
                    && firstClick
                    && secondClick
                    && config.RenderOnScroll
                    && presentationChangeCoordinator.HasPending
                    && presentationChangeCoordinator.RequestedRenderOnScroll;
                bool pendingFrameCaptured = queuedScrollFullscreenScroll
                    && CaptureAutomatedPresentationFrame(
                        "presentation-scroll-fullscreen-scroll-pending"
                    );
                if (!pendingFrameCaptured)
                {
                    FailAutomatedPresentationSwitchTest(
                        "Scroll -> Fullscreen -> Scroll did not retain the committed scroll view while pending"
                    );
                    return;
                }

                automatedSmokeTestPresentationPhase = 1;
                return;
            }

            case 1:
            {
                if (presentationChangeCoordinator.HasPending) return;

                bool settingsComposerKept = ReferenceEquals(
                    settingsModal,
                    automatedSmokeTestSettingsComposer
                );
                bool scrollCommittedState = config.RenderOnScroll
                    && settingsModalOpen
                    && settingsComposerKept;
                bool scrollCommitted = scrollCommittedState
                    && CaptureAutomatedPresentationFrame(
                        "presentation-scroll-fullscreen-scroll"
                    );
                if (!scrollCommitted)
                {
                    capi.Logger.Error(
                        "[ModernAtlas] Presentation phase diagnostic: committedScroll={0}, settingsOpen={1}, settingsComposerKept={2}, pending={3}, requestedScroll={4}, screenshot={5}.",
                        config.RenderOnScroll,
                        settingsModalOpen,
                        settingsComposerKept,
                        presentationChangeCoordinator.HasPending,
                        presentationChangeCoordinator.RequestedRenderOnScroll,
                        scrollCommittedState
                    );
                    FailAutomatedPresentationSwitchTest(
                        "the final Scroll choice was not committed after the debounce interval"
                    );
                    return;
                }

                bool fullscreenClick = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "render-on-scroll"
                );
                bool fullscreenQueued = fullscreenClick
                    && config.RenderOnScroll
                    && presentationChangeCoordinator.HasPending
                    && !presentationChangeCoordinator.RequestedRenderOnScroll
                    && CaptureAutomatedPresentationFrame(
                        "presentation-fullscreen-pending"
                    );
                if (!fullscreenQueued)
                {
                    FailAutomatedPresentationSwitchTest(
                        "could not queue the fullscreen presentation without rebuilding Settings"
                    );
                    return;
                }

                automatedSmokeTestPresentationPhase = 2;
                return;
            }

            case 2:
            {
                if (presentationChangeCoordinator.HasPending) return;

                bool fullscreenCommitted = !config.RenderOnScroll
                    && settingsModalOpen
                    && ReferenceEquals(
                        settingsModal,
                        automatedSmokeTestSettingsComposer
                    )
                    && CaptureAutomatedPresentationFrame(
                        "presentation-fullscreen"
                    );
                if (!fullscreenCommitted)
                {
                    FailAutomatedPresentationSwitchTest(
                        "the fullscreen presentation did not replace the scroll viewport after commit"
                    );
                    return;
                }

                bool firstReturnToScroll = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "render-on-scroll"
                );
                bool secondReturnToScroll = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "render-on-scroll"
                );
                bool queuedFullscreenScrollFullscreen = !config.RenderOnScroll
                    && firstReturnToScroll
                    && secondReturnToScroll
                    && presentationChangeCoordinator.HasPending
                    && !presentationChangeCoordinator.RequestedRenderOnScroll
                    && CaptureAutomatedPresentationFrame(
                        "presentation-fullscreen-scroll-fullscreen-pending"
                    );
                if (!queuedFullscreenScrollFullscreen)
                {
                    FailAutomatedPresentationSwitchTest(
                        "Fullscreen -> Scroll -> Fullscreen did not keep the latest fullscreen request pending"
                    );
                    return;
                }

                automatedSmokeTestPresentationPhase = 3;
                return;
            }

            case 3:
            {
                if (presentationChangeCoordinator.HasPending) return;

                bool finalFullscreen = !config.RenderOnScroll
                    && settingsModalOpen
                    && ReferenceEquals(
                        settingsModal,
                        automatedSmokeTestSettingsComposer
                    )
                    && ReferenceEquals(
                        performanceModal,
                        automatedSmokeTestPerformanceComposer
                    )
                    && ReferenceEquals(
                        creativeSettingsModal,
                        automatedSmokeTestCreativeComposer
                    )
                    && ReferenceEquals(
                        visualLabModal,
                        automatedSmokeTestVisualLabComposer
                    )
                    && CaptureAutomatedPresentationFrame(
                        "presentation-fullscreen-scroll-fullscreen"
                    );
                bool weatherBefore = config.ScrollRealtimeWeatherEnabled;
                bool weatherToggled = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "scroll-realtime-weather"
                )
                    && config.ScrollRealtimeWeatherEnabled != weatherBefore;
                bool weatherRestored = weatherToggled
                    && ClickAtlasControlForAutomatedTest(
                        settingsModal,
                        "scroll-realtime-weather"
                    )
                    && config.ScrollRealtimeWeatherEnabled == weatherBefore;
                bool otherBefore = config.SkipOpeningAnimation;
                bool otherToggled = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "skip-opening-animation"
                )
                    && config.SkipOpeningAnimation != otherBefore;
                bool otherRestored = otherToggled
                    && ClickAtlasControlForAutomatedTest(
                        settingsModal,
                        "skip-opening-animation"
                    )
                    && config.SkipOpeningAnimation == otherBefore;
                bool settingsStillActive = settingsModalOpen
                    && ReferenceEquals(
                        settingsModal,
                        automatedSmokeTestSettingsComposer
                    );
                bool closed = settingsStillActive
                    && ClickAtlasControlForAutomatedTest(
                        settingsModal,
                        "settings-close"
                    )
                    && !settingsModalOpen;

                automatedSmokeTestPresentationPassed = finalFullscreen
                    && weatherToggled
                    && weatherRestored
                    && otherToggled
                    && otherRestored
                    && settingsStillActive
                    && closed;
                automatedSmokeTestPresentationPhase = 5;
                if (automatedSmokeTestPresentationPassed)
                {
                    capi.Logger.Notification(
                        "[ModernAtlas] Automated presentation debounce check passed: Scroll -> Fullscreen -> Scroll and Fullscreen -> Scroll -> Fullscreen kept the last choice, retained Settings/modal composers, and produced opaque transition frames."
                    );
                }
                else
                {
                    capi.Logger.Error(
                        "[ModernAtlas] Automated presentation debounce check failed: finalFullscreen={0}, weather={1}/{2}, other={3}/{4}, settingsActive={5}, closed={6}.",
                        finalFullscreen,
                        weatherToggled,
                        weatherRestored,
                        otherToggled,
                        otherRestored,
                        settingsStillActive,
                        closed
                    );
                }
                return;
            }
        }
    }

    private void FailAutomatedPresentationSwitchTest(string diagnostic)
    {
        automatedSmokeTestPresentationPhase = -1;
        automatedSmokeTestPresentationPassed = false;
        capi.Logger.Error(
            "[ModernAtlas] Automated presentation debounce check failed: {0}.",
            diagnostic
        );
    }

    private bool CaptureAutomatedPresentationFrame(string suffix)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(
            SmokeScreenshotEnvironmentVariable
        );
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return ValidateAutomatedSmokeWindowAlpha();
        }

        string prefix = configuredPath.EndsWith(
            ".png",
            StringComparison.OrdinalIgnoreCase
        )
            ? configuredPath[..^4]
            : configuredPath;
        return TrySaveAutomatedSmokeScreenshot($"{prefix}-{suffix}.png");
    }

    private bool ClickAtlasControlForAutomatedTest(
        GuiComposer? composer,
        string key,
        double horizontalFraction = 0.5
    )
    {
        if (composer == null) return false;
        GuiElement? element = composer.GetElement(key);
        if (element == null) return false;

        // GuiElementAtlasChoice decides the direction from the pressed half, so
        // an arrow test must aim left or right of the centre; 0.5 advances.
        int x = (int)Math.Round(
            element.Bounds.absX + element.Bounds.OuterWidth * horizontalFraction
        );
        int y = (int)Math.Round(element.Bounds.absY + element.Bounds.OuterHeight * 0.5);
        MouseEvent down = new(x, y, EnumMouseButton.Left, 0);
        // Exercise the same composer-owned press/release path as a real
        // dialog click. Calling the outer dialog dispatcher here lets the
        // full-screen overlay compete with a panel control at the same
        // coordinate, which can drop a valid Settings button callback.
        composer.OnMouseDown(down);
        MouseEvent up = new(x, y, EnumMouseButton.Left, 0);
        composer.OnMouseUp(up);
        return down.Handled && up.Handled;
    }

    /// <summary>
    /// Drives the MAP OPTIONS layer selector through its arrows. The drop-down
    /// it replaced opened its list below the bottom panel, so Moisture,
    /// Temperature and Ore density were covered and unclickable. This exercise
    /// proves every allowed layer is reachable with a plain click, forwards,
    /// backwards and across the wrap, and that the panel survives the rebuild
    /// each change requests.
    /// </summary>
    private bool ExerciseAutomatedMapLayerChoice()
    {
        List<AtlasMapLayer> expected = new()
        {
            AtlasMapLayer.TexturedTerrain,
            AtlasMapLayer.SoilFertility,
            AtlasMapLayer.Moisture,
            AtlasMapLayer.Temperature
        };
        if (UnitInspectionEnabled) expected.Add(AtlasMapLayer.OreDensity);

        if (activeMapLayer != AtlasMapLayer.TexturedTerrain)
        {
            SetMapLayer(AtlasMapLayer.TexturedTerrain);
        }
        RefreshAutomatedMapLayerPanel();
        if (mapLayerPanel?.GetAtlasChoice("map-layer") == null)
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated map-layer selector test found no arrow selector."
            );
            return false;
        }

        for (int step = 1; step < expected.Count; step++)
        {
            if (!AdvanceAutomatedMapLayerChoice(true, expected[step])) return false;
        }
        // One more forward click has to wrap onto the neutral layer, and one
        // backward click from there has to reach the last allowed layer. With
        // Ore density filtered out of the list, that wrap can never select it.
        if (!AdvanceAutomatedMapLayerChoice(true, AtlasMapLayer.TexturedTerrain))
        {
            return false;
        }
        if (!AdvanceAutomatedMapLayerChoice(false, expected[expected.Count - 1]))
        {
            return false;
        }
        if (!AdvanceAutomatedMapLayerChoice(true, AtlasMapLayer.TexturedTerrain))
        {
            return false;
        }

        capi.Logger.Notification(
            "[ModernAtlas] Automated map-layer selector test reached all {0} allowed layers with arrow clicks, including the wrap in both directions, and returned to Textured terrain (ore access={1}).",
            expected.Count,
            UnitInspectionEnabled
        );
        return activeMapLayer == AtlasMapLayer.TexturedTerrain;
    }

    /// <summary>
    /// Drives the MAP OPTIONS ore filter through its arrows. Its drop-down list
    /// opened below the bottom panel, so ores past the first visible entries
    /// could not be picked. With several ores the exercise steps forward, back
    /// and across both wraps; with only "All ores" available it proves the
    /// single option stays selected without reporting a change.
    /// </summary>
    private bool ExerciseAutomatedOreFilterChoice()
    {
        OpenBottomPanelImmediately(AtlasPanelSection.MapOptions);
        GuiElementAtlasChoice? choice = mapLayerPanel?.GetAtlasChoice("ore-filter");
        if (choice == null)
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated ore-filter test found no arrow selector in MAP OPTIONS."
            );
            ResetBottomPanelState();
            return false;
        }

        GetOreFilterOptions(
            oreFilterLabelLimit,
            out string[] values,
            out _,
            out _
        );
        bool passed;
        if (values.Length <= 1)
        {
            passed = AdvanceAutomatedOreFilterChoice(true, AllOresFilterValue)
                && selectedOreCode == null;
            capi.Logger.Notification(
                "[ModernAtlas] Automated ore-filter test confirmed the single \"All ores\" option stays selected without a false change (stable={0}).",
                passed
            );
        }
        else
        {
            // Visit every ore in list order, then prove the forward wrap onto
            // All ores, the backward wrap onto the last ore and the return.
            passed = true;
            for (int index = 1; index < values.Length && passed; index++)
            {
                passed = AdvanceAutomatedOreFilterChoice(true, values[index]);
            }
            passed = passed
                && AdvanceAutomatedOreFilterChoice(true, AllOresFilterValue)
                && AdvanceAutomatedOreFilterChoice(false, values[values.Length - 1])
                && AdvanceAutomatedOreFilterChoice(true, AllOresFilterValue)
                // Leave a filtered ore selected for the following ore-map phase.
                && AdvanceAutomatedOreFilterChoice(true, values[1]);
            capi.Logger.Notification(
                "[ModernAtlas] Automated ore-filter test clicked through every one of the {0} options ({1} ores), both wraps and the return, then left the filter on {2} (passed={3}).",
                values.Length,
                values.Length - 1,
                selectedOreCode ?? "<all ores>",
                passed
            );
        }

        ResetBottomPanelState();
        return passed;
    }

    private bool AdvanceAutomatedOreFilterChoice(bool forward, string expectedValue)
    {
        if (!ClickAtlasControlForAutomatedTest(
                mapLayerPanel,
                "ore-filter",
                forward ? 0.75 : 0.25
            ))
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated ore-filter arrow click was not handled by the selector."
            );
            return false;
        }

        GuiElementAtlasChoice? choice = mapLayerPanel?.GetAtlasChoice("ore-filter");
        string? expectedCode = string.Equals(
            expectedValue,
            AllOresFilterValue,
            StringComparison.Ordinal
        ) ? null : expectedValue;
        if (choice?.SelectedValue == expectedValue
            && string.Equals(selectedOreCode, expectedCode, StringComparison.Ordinal))
        {
            return true;
        }

        capi.Logger.Error(
            "[ModernAtlas] Automated ore-filter arrow click ({0}) selected {1} with filter {2}; expected {3}.",
            forward ? "next" : "previous",
            choice?.SelectedValue ?? "<none>",
            selectedOreCode ?? "<all ores>",
            expectedValue
        );
        return false;
    }

    private void RefreshAutomatedMapLayerPanel()
    {
        if (!pendingMapLayerPanelRecompose) return;
        // Switching to or from Ore density rebuilds the panel to add or remove
        // the ore filter. The dialog does this between frames; the test has to
        // do it explicitly before it inspects the new element tree.
        pendingMapLayerPanelRecompose = false;
        RecomposeMapLayerPanel();
    }

    private bool AdvanceAutomatedMapLayerChoice(
        bool forward,
        AtlasMapLayer expectedLayer
    )
    {
        if (!ClickAtlasControlForAutomatedTest(
                mapLayerPanel,
                "map-layer",
                forward ? 0.75 : 0.25
            ))
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated map-layer arrow click was not handled by the selector."
            );
            return false;
        }
        RefreshAutomatedMapLayerPanel();

        GuiElementAtlasChoice? choice = mapLayerPanel?.GetAtlasChoice("map-layer");
        string expectedValue = AtlasMapLayerInfo.Values[(int)expectedLayer];
        // The legend follows the layer immediately. The layer status is filled
        // by the budgeted texture build, so it is not required in this frame;
        // the later data-layer exercise waits for that state.
        bool legendPresent = mapLayerPanel?.GetDynamicText("layer-legend") != null
            && mapLayerPanel?.GetDynamicText("layer-status") != null
            && activeMapLayer.DetailedLegend().Length > 0;
        // Switching to or from Ore density has to rebuild the panel so the ore
        // filter row appears with the layer and disappears with it.
        bool oreFilterRowCorrect =
            (mapLayerPanel?.GetElement("ore-filter") is GuiElementAtlasChoice)
                == (expectedLayer == AtlasMapLayer.OreDensity);
        if (activeMapLayer == expectedLayer
            && choice?.SelectedValue == expectedValue
            && legendPresent
            && oreFilterRowCorrect)
        {
            return true;
        }

        capi.Logger.Error(
            "[ModernAtlas] Automated map-layer arrow click ({0}) selected {1}/{2} instead of {3}; legend elements present={4}, ore filter row correct={5}.",
            forward ? "next" : "previous",
            activeMapLayer,
            choice?.SelectedValue ?? "<none>",
            expectedValue,
            legendPresent,
            oreFilterRowCorrect
        );
        return false;
    }

    private bool DragAtlasSliderBeyondBoundsForAutomatedTest(
        GuiComposer? composer,
        string key,
        bool towardMaximum
    )
    {
        GuiElementAtlasSlider? slider = composer?.GetElement(key) as GuiElementAtlasSlider;
        if (slider == null) return false;

        int startX = (int)Math.Round(slider.Bounds.absX + slider.Bounds.OuterWidth * 0.5);
        int startY = (int)Math.Round(slider.Bounds.absY + slider.Bounds.OuterHeight * 0.5);
        int endX = (int)Math.Round(
            towardMaximum
                ? slider.Bounds.absX + slider.Bounds.OuterWidth + 160
                : slider.Bounds.absX - 160
        );
        int endY = (int)Math.Round(slider.Bounds.absY - slider.Bounds.OuterHeight - 80);

        MouseEvent down = new(startX, startY, EnumMouseButton.Left, 0);
        OnMouseDown(down);
        MouseEvent move = new(endX, endY, endX - startX, endY - startY);
        OnMouseMove(move);
        MouseEvent up = new(endX, endY, EnumMouseButton.Left, 0);
        OnMouseUp(up);
        return down.Handled && move.Handled && up.Handled;
    }

    private void CaptureAutomatedSmokeScreenshot()
    {
        if (!automatedSmokeTestActive
            || !AutomatedSmokeTestRenderedExactWorld)
        {
            return;
        }

        // Leave the live atlas undisturbed long enough to validate a stable
        // post-opening frame instead of capturing the first transient draw.
        if (!automatedSmokeTestSafeSurfaceScreenshotHandled
            && automatedSmokeTestElapsedSeconds < 2f)
        {
            return;
        }

        string? configuredPath = Environment.GetEnvironmentVariable(
            SmokeScreenshotEnvironmentVariable
        );
        if (automatedSmokeScreenshotPreviewPending)
        {
            // Opening the preview deliberately waits for one fresh atlas draw
            // before publishing the modal.  Keep this state latched until
            // that frame exists, then capture the actual BEFORE/AFTER UI and
            // close through the same lifecycle path a player uses.
            if (!screenshotPreviewOpen)
            {
                return;
            }

            bool controlsPassed = ValidateScreenshotPreviewModalControls(
                out string previewDiagnostic
            );
            bool screenshotSaved = string.IsNullOrWhiteSpace(configuredPath);
            if (screenshotSaved)
            {
                capi.Logger.Notification(
                    "[ModernAtlas] Automated screenshot filter preview controls passed without a requested UI output path: {0}.",
                    previewDiagnostic
                );
            }
            else
            {
                string previewPrefix = configuredPath!.EndsWith(
                    ".png",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? configuredPath[..^4]
                    : configuredPath;
                screenshotSaved = TrySaveAutomatedSmokeScreenshot(
                    $"{previewPrefix}-screenshot-filter-preview.png"
                );
            }

            // Exercise the same action exposed by the modal, then cancel it
            // immediately. This proves TAKE SCREENSHOT uses the existing
            // tiled job and that the cancellation path restores the frozen
            // camera before the UI screenshot sequence continues.
            float previewZoomBeforeTake = zoom;
            float previewTargetZoomBeforeTake = targetZoom;
            double previewCenterXBeforeTake = centerX;
            double previewCenterYBeforeTake = centerY;
            double previewCenterZBeforeTake = centerZ;
            bool previewTakeAccepted = TakeScreenshotFromPreview();
            bool previewCaptureQueued = pendingScreenshotRequest;
            if (previewCaptureQueued)
            {
                CancelScreenshotCapture();
            }
            automatedSmokeScreenshotPreviewTakeCancelPassed =
                previewTakeAccepted
                && previewCaptureQueued
                && !pendingScreenshotRequest
                && Math.Abs(zoom - previewZoomBeforeTake) < 0.001f
                && Math.Abs(targetZoom - previewTargetZoomBeforeTake) < 0.001f
                && Math.Abs(centerX - previewCenterXBeforeTake) < 0.001
                && Math.Abs(centerY - previewCenterYBeforeTake) < 0.001
                && Math.Abs(centerZ - previewCenterZBeforeTake) < 0.001;
            if (!automatedSmokeScreenshotPreviewTakeCancelPassed)
            {
                controlsPassed = false;
                previewDiagnostic += "; TAKE SCREENSHOT/cancel did not restore the camera";
            }

            automatedSmokeScreenshotPreviewPassed = controlsPassed
                && screenshotSaved;
            if (automatedSmokeScreenshotPreviewPassed)
            {
                capi.Logger.Notification(
                    "[ModernAtlas] Automated screenshot filter preview passed: {0}; saved BEFORE/AFTER Custom preview with a whole-image blue balance at centered 25% capture area.",
                    previewDiagnostic
                );
            }
            else
            {
                capi.Logger.Error(
                    "[ModernAtlas] Automated screenshot filter preview failed: controls={0}, saved={1}, diagnostic={2}.",
                    controlsPassed,
                    screenshotSaved,
                    previewDiagnostic
                );
            }

            CloseScreenshotPreview(true);
            automatedSmokeScreenshotPreviewPending = false;
            // The preview uses a deterministic Custom/25% configuration
            // only for its diagnostic frame. Restore the player's choices
            // before continuing the normal UI screenshot sequence. Keep the
            // smoke capture overrides (including the Off baseline for the
            // optional two-job filter sequence) intact until world teardown.
            if (automatedScreenshotSequenceEnabled)
            {
                AtlasScreenshotFilterSettings.ApplyPresetToConfig(
                    config,
                    "off"
                );
            }
            else if (automatedScreenshotIntensityZeroEnabled)
            {
                AtlasScreenshotFilterSettings.ApplyPresetToConfig(
                    config,
                    "custom"
                );
                config.ScreenshotFiltersEnabled = true;
                config.ScreenshotFilterIntensityPercent = 0;
            }
            else
            {
                automatedOriginalScreenshotFilters.Restore(config);
            }
            config.ScreenshotScale = automatedSmokeScreenshotScale;
            config.ScreenshotCaptureAreaPercent =
                automatedSmokeScreenshotCaptureAreaPercent;
            SyncScreenshotSettingsControls();
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                // No comparison images were requested, so there is no Map
                // Options frame to preserve. Settle directly on Settings;
                // leaving a queued Map Options animation here can close the
                // composer underneath the following presentation debounce
                // regression one frame after it starts.
                OpenBottomPanelImmediately(AtlasPanelSection.Settings);
            }
            else
            {
                // Keep phase 3 pending: the next UI frame must be the real
                // map-options frame. The preview replaces only the transition
                // between Screenshot Options and Map Options; advancing here
                // would label/map the following frame as Search and shift the
                // remaining compact-UI screenshot sequence by one.
                ToggleMapOptions();
            }
            return;
        }
        if (pendingAutomatedMapLayerScreenshotSuffix != null)
        {
            string layerSuffix = pendingAutomatedMapLayerScreenshotSuffix;
            pendingAutomatedMapLayerScreenshotSuffix = null;
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string layerPrefix = configuredPath.EndsWith(
                    ".png",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? configuredPath[..^4]
                    : configuredPath;
                TrySaveAutomatedSmokeScreenshot($"{layerPrefix}-{layerSuffix}.png");
            }
            return;
        }
        if (!automatedSmokeTestSafeSurfaceScreenshotHandled)
        {
            automatedSmokeTestSafeSurfaceScreenshotHandled = true;
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string safePrefix = configuredPath.EndsWith(
                    ".png",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? configuredPath[..^4]
                    : configuredPath;
                TrySaveAutomatedSmokeScreenshot(
                    $"{safePrefix}-survival-safe.png"
                );
            }
            automatedSmokeTestZoomBeforeMaximum = targetZoom;
            automatedSmokeTestPitchBeforeMaximum = targetPitchDegrees;
            targetPitchDegrees = 72;
            pitchDegrees = 72;
            zoom = targetZoom;
            automatedSmokeTestBorderTopDownStartedSeconds =
                automatedSmokeTestElapsedSeconds;
            automatedSmokeTestBorderTopDownPending = true;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test is holding a fitted top-down standard-world view for border inspection before layers and Cave Mode."
            );
            return;
        }

        if (automatedSmokeTestBorderTopDownPending)
        {
            if (automatedSmokeTestElapsedSeconds
                - automatedSmokeTestBorderTopDownStartedSeconds
                < MaximumZoomScreenshotSettleSeconds)
            {
                return;
            }

            automatedSmokeTestBorderTopDownPending = false;
            automatedSmokeTestBorderTopDownFrameRendered = true;
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string borderPrefix = configuredPath.EndsWith(
                    ".png",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? configuredPath[..^4]
                    : configuredPath;
                automatedSmokeTestBorderTopDownFrameRendered =
                    TrySaveAutomatedSmokeScreenshot(
                        $"{borderPrefix}-border-topdown.png"
                    );
            }
            ApplyZoomWheel(1, false);
            ApplyZoomWheel(1, false);
            // The automated check validates a settled zoom level. Snap only
            // its synthetic wheel input to the target so live world frames
            // and newly completed chunk meshes cannot make the two-second
            // capture race camera interpolation.
            zoom = targetZoom;
            automatedSmokeTestPartialZoomStartedSeconds =
                automatedSmokeTestElapsedSeconds;
            automatedSmokeTestPartialZoomPending = true;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test is holding partial scroll zoom for 2 seconds before capture."
            );
            return;
        }

        if (automatedSmokeTestPartialZoomPending)
        {
            if (automatedSmokeTestElapsedSeconds
                - automatedSmokeTestPartialZoomStartedSeconds
                < MaximumZoomScreenshotSettleSeconds)
            {
                return;
            }

            automatedSmokeTestPartialZoomPending = false;
            automatedSmokeTestPartialZoomFrameRendered = config.RenderOnScroll
                && zoom > MaximumZoomIn + 0.001f
                && zoom < automatedSmokeTestZoomBeforeMaximum - 0.001f;
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string partialZoomPrefix = configuredPath.EndsWith(
                    ".png",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? configuredPath[..^4]
                    : configuredPath;
                automatedSmokeTestPartialZoomFrameRendered &=
                    TrySaveAutomatedSmokeScreenshot(
                        $"{partialZoomPrefix}-scroll-partial-zoom.png"
                    );
            }
            targetZoom = MaximumZoomIn;
            zoom = MaximumZoomIn;
            automatedSmokeTestMaximumZoomStartedSeconds =
                automatedSmokeTestElapsedSeconds;
            automatedSmokeTestMaximumZoomPending = true;
            capi.Logger.Notification(
                automatedSmokeTestPartialZoomFrameRendered
                    ? "[ModernAtlas] Automated partial scroll zoom capture passed; holding maximum zoom for 2 seconds."
                    : "[ModernAtlas] Automated partial scroll zoom capture failed; holding maximum zoom for 2 seconds."
            );
            return;
        }

        if (automatedSmokeTestMaximumZoomPending)
        {
            if (automatedSmokeTestElapsedSeconds
                - automatedSmokeTestMaximumZoomStartedSeconds
                < MaximumZoomScreenshotSettleSeconds)
            {
                return;
            }

            automatedSmokeTestMaximumZoomPending = false;
            // Streaming continues while the atlas is open, so a newly
            // completed edge mesh can make a frame more expensive and leave
            // the smoothed value a fraction behind its already clamped
            // target. Capture the exact target used by real wheel input.
            zoom = targetZoom;
            automatedSmokeTestMaximumZoomFrameRendered = config.RenderOnScroll
                && Math.Abs(targetZoom - MaximumZoomIn) < 0.001f;
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string maximumZoomPrefix = configuredPath.EndsWith(
                    ".png",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? configuredPath[..^4]
                    : configuredPath;
                automatedSmokeTestMaximumZoomFrameRendered &=
                    TrySaveAutomatedSmokeScreenshot(
                        $"{maximumZoomPrefix}-scroll-maximum-zoom.png"
                    );
            }
            targetZoom = automatedSmokeTestZoomBeforeMaximum;
            zoom = automatedSmokeTestZoomBeforeMaximum;
            targetPitchDegrees = automatedSmokeTestPitchBeforeMaximum;
            pitchDegrees = automatedSmokeTestPitchBeforeMaximum;
            if (automatedSmokeTestMaximumZoomFrameRendered)
            {
                capi.Logger.Notification(
                    "[ModernAtlas] Automated scroll clipping check passed at maximum zoom."
                );
            }
            else
            {
                capi.Logger.Error(
                    "[ModernAtlas] Automated scroll clipping check failed at maximum zoom."
                );
            }
            if (automatedSmokeTestRequiresUnlockedPitch)
            {
                targetPitchDegrees = UnlockedMinimumPitchDegrees;
                pitchDegrees = UnlockedMinimumPitchDegrees;
                FitLoadedTerrain();
                zoom = targetZoom;
                capi.Logger.Notification(
                    "[ModernAtlas] Automated smoke test is now exercising the unlocked 0-degree camera pitch."
                );
            }
            return;
        }

        if (!automatedSmokeTestInterfaceControlsPassed
            || automatedSmokeScreenshotPhase >= AutomatedUiScreenshotPhaseCount)
        {
            return;
        }
        if (automatedSmokeScreenshotPhase == 0 && bottomPanel != null)
        {
            // The unit-inspection exercise intentionally opens its own panel.
            // The first comparison frame is the neutral atlas view, so close
            // that panel before saving the navbar-only screenshot. Return and
            // wait one complete render frame so the old panel cannot remain in
            // the framebuffer captured below.
            ResetBottomPanelState();
            return;
        }
        // Wait for the shared bottom panel to settle before storing each UI
        // comparison frame. This keeps the screenshot sequence deterministic
        // while the real UI still uses the short render-time slide animation.
        if (bottomPanelAnimationActive)
        {
            return;
        }
        // A layer change updates the selector label immediately but rebuilds
        // the panel (adding or removing the ore filter row) on the next frame.
        // Waiting for that rebuild keeps every stored UI frame representative.
        if (pendingMapLayerPanelRecompose)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            // UI image files are optional, but the interactive preview is a
            // required smoke-test contract. Exercise it once even without an
            // output prefix before allowing the later capture checks to run.
            automatedSmokeScreenshotPhase = AutomatedUiScreenshotPhaseCount;
            QueueAutomatedScreenshotPreview();
            return;
        }

        string prefix = configuredPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? configuredPath[..^4]
            : configuredPath;
        string suffix = automatedSmokeScreenshotPhase switch
        {
            0 => "atlas",
            1 => "settings",
            2 => "screenshot-options",
            3 => "map-options",
            4 => "search",
            5 => "instrument",
            6 => "performance",
            7 => "creative",
            _ => "visual-lab"
        };
        string path = $"{prefix}-{suffix}.png";
        if (!TrySaveAutomatedSmokeScreenshot(path))
        {
            automatedSmokeScreenshotPhase = 5;
            return;
        }

        automatedSmokeScreenshotPhase++;
        switch (automatedSmokeScreenshotPhase)
        {
            case 1:
                OpenSettingsModal();
                break;
            case 2:
                ToggleScreenshotOptions();
                break;
            case 3:
                if (!QueueAutomatedScreenshotPreview())
                {
                    automatedSmokeScreenshotPhase = 5;
                }
                break;
            case 4:
                ToggleSearchPanel();
                break;
            case 5:
                ToggleInstrumentPanel();
                break;
            case 6:
                OpenPerformanceModal();
                break;
            case 7:
                OpenCreativeSettingsModal();
                break;
            case 8:
                OpenVisualLab();
                break;
            default:
                CloseVisualLab();
                break;
        }
    }

    private bool QueueAutomatedScreenshotPreview()
    {
        // Start from a stable preset, then exercise the user-facing whole-image
        // Blue slider. The smallest centered area also covers the exact crop
        // guide used by the tiled PNG capture.
        config.ScreenshotScale = 2;
        config.ScreenshotCaptureAreaPercent = 25;
        AtlasScreenshotFilterSettings.ApplyPresetToConfig(
            config,
            "atlas-relief"
        );
        OnScreenshotBlueChanged(140);
        SyncScreenshotSettingsControls();
        automatedSmokeScreenshotPreviewPending = true;
        if (OpenScreenshotPreview()
            && (screenshotPreviewOpen || screenshotPreviewOpening))
        {
            return true;
        }

        automatedSmokeScreenshotPreviewPending = false;
        automatedSmokeScreenshotPreviewPassed = false;
        automatedSmokeScreenshotPreviewTakeCancelPassed = false;
        capi.Logger.Error(
            "[ModernAtlas] Automated screenshot filter preview could not be opened."
        );
        return false;
    }

    private bool ValidateAutomatedSmokeWindowAlpha()
    {
        try
        {
            using BitmapRef screenshot = capi.Render.GrabScreenshot(
                capi.Render.FrameWidth,
                capi.Render.FrameHeight,
                false,
                true,
                true
            );
            int minimumAlpha = 255;
            foreach (int pixel in screenshot.Pixels)
            {
                minimumAlpha = Math.Min(minimumAlpha, (pixel >> 24) & 0xff);
            }
            if (minimumAlpha < 255)
            {
                capi.Logger.Error(
                    "[ModernAtlas] Automated presentation frame retained transparent window pixels: minimum alpha {0}.",
                    minimumAlpha
                );
                return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated presentation alpha validation failed: {0}",
                exception.Message
            );
            return false;
        }
    }

    private bool TrySaveAutomatedSmokeScreenshot(string path)
    {
        try
        {
            using BitmapRef screenshot = capi.Render.GrabScreenshot(
                capi.Render.FrameWidth,
                capi.Render.FrameHeight,
                false,
                true,
                true
            );
            int minimumAlpha = 255;
            foreach (int pixel in screenshot.Pixels)
            {
                minimumAlpha = Math.Min(minimumAlpha, (pixel >> 24) & 0xff);
            }
            if (minimumAlpha < 255)
            {
                capi.Logger.Error(
                    "[ModernAtlas] Automated atlas UI screenshot retained transparent window pixels: minimum alpha {0}.",
                    minimumAlpha
                );
                return false;
            }
            screenshot.Save(path);
            capi.Logger.Notification(
                "[ModernAtlas] Saved opaque automated atlas UI screenshot: {0}",
                path
            );
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated atlas UI screenshot failed for {0}: {1}",
                path,
                exception.Message
            );
            return false;
        }
    }

    private void SaveScreenshotValidityMaskDebug(
        int tile,
        byte[] mask,
        bool filtered,
        bool nonEmptyCoverage
    )
    {
        string? configuredPrefix = Environment.GetEnvironmentVariable(
            SmokeScreenshotMaskEnvironmentVariable
        );
        if (string.IsNullOrWhiteSpace(configuredPrefix))
        {
            configuredPrefix = Environment.GetEnvironmentVariable(
                SmokeScreenshotEnvironmentVariable
            );
        }
        if (string.IsNullOrWhiteSpace(configuredPrefix) || mask.Length == 0)
        {
            return;
        }
        if (configuredPrefix.EndsWith(
                ".png",
                StringComparison.OrdinalIgnoreCase
            ))
        {
            configuredPrefix = configuredPrefix[..^4];
        }

        try
        {
            int width = exactChunkRenderer?.ResolvedFramebuffer?.Width ?? 0;
            int height = exactChunkRenderer?.ResolvedFramebuffer?.Height ?? 0;
            if (width <= 0 || height <= 0 || mask.Length != width * height)
            {
                capi.Logger.Warning(
                    "[ModernAtlas] Validity mask debug image has inconsistent dimensions for tile {0}.",
                    tile
                );
                return;
            }
            byte[] rgb = new byte[checked(width * height * 3)];
            for (int y = 0; y < height; y++)
            {
                int sourceRow = (height - 1 - y) * width;
                int targetRow = y * width * 3;
                for (int x = 0; x < width; x++)
                {
                    byte value = mask[sourceRow + x];
                    int target = targetRow + x * 3;
                    rgb[target] = value;
                    rgb[target + 1] = value;
                    rgb[target + 2] = value;
                }
            }
            string path =
                $"{configuredPrefix}-validity-mask-{(filtered ? "filtered" : "off")}-tile-{tile:D4}.png";
            Directory.CreateDirectory(
                System.IO.Path.GetDirectoryName(path) ?? "."
            );
            AtlasPngEncoder.SavePng(rgb, width, height, path);
            capi.Logger.Notification(
                "[ModernAtlas] Saved screenshot validity mask diagnostic: {0}",
                path
            );
            if (filtered && nonEmptyCoverage)
            {
                string coveragePath =
                    $"{configuredPrefix}-validity-mask-filtered-last-nonempty-tile.png";
                AtlasPngEncoder.SavePng(rgb, width, height, coveragePath);
                capi.Logger.Notification(
                    "[ModernAtlas] Saved last non-empty filtered screenshot validity tile diagnostic: {0}",
                    coveragePath
                );
            }
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not save screenshot validity mask tile {0}: {1}",
                tile,
                exception.Message
            );
        }
    }

    private bool TryCopySmokeScreenshotOutput(string sourcePath, string suffix)
    {
        string? configuredPrefix = Environment.GetEnvironmentVariable(
            SmokeScreenshotEnvironmentVariable
        );
        if (string.IsNullOrWhiteSpace(configuredPrefix)) return true;
        if (configuredPrefix.EndsWith(
                ".png",
                StringComparison.OrdinalIgnoreCase
            ))
        {
            configuredPrefix = configuredPrefix[..^4];
        }
        try
        {
            string targetPath = $"{configuredPrefix}-{suffix}.png";
            Directory.CreateDirectory(
                System.IO.Path.GetDirectoryName(targetPath) ?? "."
            );
            File.Copy(sourcePath, targetPath, true);
            capi.Logger.Notification(
                "[ModernAtlas] Saved automated screenshot output diagnostic: {0}",
                targetPath
            );
            return File.Exists(targetPath) && new FileInfo(targetPath).Length > 0;
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "[ModernAtlas] Could not copy automated screenshot output {0}: {1}",
                suffix,
                exception.Message
            );
            return false;
        }
    }

    /// <summary>
    /// Captures the ordinary Vintage Story frame while the atlas is closed.
    /// This deliberately does not call <see cref="ForceOpaqueWindowAlpha"/>
    /// or any atlas compositor: the screenshot must observe the real world
    /// handoff after the atlas scope has ended.
    /// </summary>
    internal bool CaptureAutomatedOrdinaryWorldScreenshot(string suffix)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(
            SmokeScreenshotEnvironmentVariable
        );
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return true;
        }

        string prefix = configuredPath.EndsWith(
            ".png",
            StringComparison.OrdinalIgnoreCase
        )
            ? configuredPath[..^4]
            : configuredPath;
        string path = $"{prefix}-{suffix}.png";
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using BitmapRef screenshot = capi.Render.GrabScreenshot(
                capi.Render.FrameWidth,
                capi.Render.FrameHeight,
                false,
                true,
                true
            );
            screenshot.Save(path);
            capi.Logger.Notification(
                "[ModernAtlas] Saved ordinary-world smoke screenshot after atlas state handoff: {0}",
                path
            );
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "[ModernAtlas] Ordinary-world smoke screenshot failed for {0}: {1}",
                path,
                exception.Message
            );
            return false;
        }
    }

    private void RestoreAutomatedSmokeTestPreferences()
    {
        if (!automatedSmokeTestPreferencesCaptured) return;

        presentationChangeCoordinator.Reset(config.RenderOnScroll);

        if (automatedSmokeTestPartialZoomPending
            || automatedSmokeTestMaximumZoomPending
            || automatedSmokeTestBorderTopDownPending)
        {
            targetZoom = automatedSmokeTestZoomBeforeMaximum;
            zoom = automatedSmokeTestZoomBeforeMaximum;
            targetPitchDegrees = automatedSmokeTestPitchBeforeMaximum;
            pitchDegrees = automatedSmokeTestPitchBeforeMaximum;
            automatedSmokeTestPartialZoomPending = false;
            automatedSmokeTestMaximumZoomPending = false;
            automatedSmokeTestBorderTopDownPending = false;
        }

        automatedSmokeTestPreferencesCaptured = false;
        bool accessChanged = cheatModeEnabled != automatedOriginalCheatModeEnabled;
        cheatModeEnabled = automatedOriginalCheatModeEnabled;
        automatedOriginalScreenshotFilters.Restore(config);
        config.ScreenshotScale = automatedOriginalScreenshotScale;
        config.ScreenshotCaptureAreaPercent =
            automatedOriginalScreenshotCaptureAreaPercent;
        SyncScreenshotSettingsControls();
        config.MapLayersEnabled = automatedOriginalMapLayersEnabled;
        config.CaveModeEnabled = automatedOriginalCaveModeEnabled;
        config.SearchModeEnabled = automatedOriginalSearchModeEnabled;
        config.CameraAngleLocked = automatedOriginalCameraAngleLocked;
        config.LiveLightingEnabled = automatedOriginalLiveLightingEnabled;
        config.CloudsEnabled = automatedOriginalCloudsEnabled;
        config.FixedSunHour = automatedOriginalFixedSunHour;
        config.ShowPlayerCompass = automatedOriginalShowPlayerCompass;
        config.HandheldInstrumentMode = automatedOriginalHandheldInstrumentMode;
        config.PerformanceLightingEnabled = automatedOriginalPerformanceLightingEnabled;
        if (accessChanged)
        {
            ClampPitchToAccessLevel(immediate: true);
        }
        config.HideVegetation = automatedOriginalHideVegetation;
        if (config.RenderOnScroll != automatedOriginalRenderOnScroll)
        {
            config.RenderOnScroll = automatedOriginalRenderOnScroll;
            pendingInterfaceRecompose = true;
        }
        screenshotPanelCollapsed = true;
        mapLayerPanelCollapsed = false;
        if (!SearchModeActive)
        {
            ClearSearch();
        }
        if (!config.MapLayersEnabled)
        {
            activeMapLayer = AtlasMapLayer.TexturedTerrain;
            mapLayerTexture.Reset();
        }
        SyncSettingsControls();
        SyncPerformanceControls();
        SyncCreativeSettingsControls();
        saveConfig();
    }

    private void ExerciseAutomatedSearchInput()
    {
        if (automatedSmokeTestSearchInputAttempted) return;

        automatedSmokeTestSearchInputAttempted = true;
        if (SearchModeActive && searchPanel == null)
        {
            OpenBottomPanelImmediately(AtlasPanelSection.Search);
        }
        GuiElementTextInput? input = searchPanel?.GetTextInput("search-input");
        bool initiallyUnfocused = input?.HasFocus == false;
        bool focused = input != null
            && searchPanel?.FocusElement(input.TabIndex) == true;
        if (input == null || !focused)
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated search-input test could not focus the atlas text box."
            );
            return;
        }

        input.SetValue("", true);
        searchController.Clear();
        KeyEvent keyDown = new()
        {
            KeyCode = (int)GlKeys.G,
            KeyChar = 'g'
        };
        OnKeyDown(keyDown);
        bool remainedOpen = IsOpened();
        KeyEvent keyPress = new()
        {
            KeyCode = (int)GlKeys.G,
            KeyChar = 'g'
        };
        OnKeyPress(keyPress);

        string enteredText = input.GetText();
        string enteredQuery = searchController.Query;
        input.SetValue("", true);
        searchController.Clear();
        searchPanel?.UnfocusOwnElements();
        bool focusReleased = !input.HasFocus;
        automatedSmokeTestSearchInputPassed = initiallyUnfocused
            && remainedOpen
            && keyDown.Handled
            && keyPress.Handled
            && string.Equals(enteredText, "g", StringComparison.Ordinal)
            && string.Equals(enteredQuery, "g", StringComparison.Ordinal)
            && focusReleased;
        if (automatedSmokeTestSearchInputPassed)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test opened search unfocused, typed G after focusing it, then released focus without closing the atlas."
            );
        }
        else
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated search-input test failed: initiallyUnfocused={0}, open={1}, keyDown={2}, keyPress={3}, focusReleased={4}, text='{5}', query='{6}'.",
                initiallyUnfocused,
                remainedOpen,
                keyDown.Handled,
                keyPress.Handled,
                focusReleased,
                enteredText,
                enteredQuery
            );
        }
    }

    private void UnfocusSearchOutsideInput(MouseEvent args)
    {
        GuiElementTextInput? input = searchPanel?.GetTextInput("search-input");
        if (input?.HasFocus == true && !input.IsPositionInside(args.X, args.Y))
        {
            searchPanel?.UnfocusOwnElements();
        }
    }

    private void ExerciseAutomatedUnitInspection()
    {
        if (automatedSmokeTestUnitInspectionAttempted
            || !UnitInspectionEnabled
            || exactChunkRenderer == null
            || exactChunkRenderer.LastRenderedEntities.Count == 0)
        {
            return;
        }

        automatedSmokeTestUnitInspectionAttempted = true;
        AtlasRenderedEntity candidate = exactChunkRenderer.LastRenderedEntities[0];
        foreach (AtlasRenderedEntity renderedEntity in exactChunkRenderer.LastRenderedEntities)
        {
            if (renderedEntity.Entity.EntityId == capi.World.Player.Entity.EntityId)
            {
                candidate = renderedEntity;
                break;
            }
        }

        Entity entity = candidate.Entity;
        float selectionMiddle = (entity.SelectionBox.Y1 + entity.SelectionBox.Y2) * 0.5f;
        bool projected = TryProjectAtlasPosition(
            entity.Pos.X,
            entity.Pos.Y + selectionMiddle,
            entity.Pos.Z,
            out double screenX,
            out double screenY,
            out _
        );
        bool selected = projected
            && TrySelectRenderedEntity((int)Math.Round(screenX), (int)Math.Round(screenY));
        bool healthAvailable = AtlasEntityInspectionAdapter.TryGetHealth(
            entity,
            out _,
            out _
        );
        automatedSmokeTestUnitInspectionPassed = selected && healthAvailable;
        if (automatedSmokeTestUnitInspectionPassed)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test selected a rendered living model and read its unit-frame health."
            );
        }
        else
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated unit-frame test failed: projected={0}, selected={1}, health={2}.",
                projected,
                selected,
                healthAvailable
            );
        }
    }

    private void ExerciseAutomatedSearch()
    {
        if (automatedSmokeTestSearchPassed
            || !automatedSmokeTestUnitInspectionPassed)
        {
            return;
        }

        if (automatedSmokeTestSearchPhase == 0)
        {
            searchController.SetQueryImmediatelyForAutomatedTest("player");
            automatedSmokeTestSearchPhase = 1;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test started a loaded-entity atlas search."
            );
            return;
        }

        if (automatedSmokeTestSearchPhase == 1)
        {
            bool foundPlayer = false;
            foreach (AtlasSearchResult result in searchController.DynamicResults)
            {
                if (result.Kind != AtlasSearchResultKind.Player) continue;
                foundPlayer = true;
                break;
            }
            if (!foundPlayer) return;

            if (!TryGetAutomatedBlockSearchQuery(out string blockQuery))
            {
                automatedSmokeTestSearchPhase = -1;
                capi.Logger.Error(
                    "[ModernAtlas] Automated atlas search test could not find a loaded surface block near the player."
                );
                return;
            }

            searchController.SetQueryImmediatelyForAutomatedTest(blockQuery);
            automatedSmokeTestSearchPhase = 2;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test found a loaded entity marker and started an incremental block search for '{0}'.",
                blockQuery
            );
            return;
        }

        if (automatedSmokeTestSearchPhase == 2
            && searchController.BlockResults.Count > 0)
        {
            searchController.SetQueryImmediatelyForAutomatedTest("lantern");
            automatedSmokeTestSearchPhase = 3;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test found loaded entity and block markers and started a real placed-lantern search."
            );
            return;
        }
        if (automatedSmokeTestSearchPhase == 2
            && searchController.BlockScanComplete)
        {
            automatedSmokeTestSearchPhase = -1;
            capi.Logger.Error(
                "[ModernAtlas] Automated atlas block search completed without finding its known loaded block: {0}.",
                searchController.DiagnosticSummary
            );
            return;
        }

        if (automatedSmokeTestSearchPhase == 3)
        {
            if (!TrySelectAutomatedLanternBlock(out string activeQuery))
            {
                if (searchController.BlockScanComplete)
                {
                    automatedSmokeTestSearchPassed = true;
                    capi.Logger.Notification(
                        "[ModernAtlas] Automated placed-lantern marker comparison was not exercised because the loaded test-world chunks contain no lantern marker; the real registered attribute-backed lantern block alias check passed."
                    );
                }
                return;
            }

            searchController.SetQueryImmediatelyForAutomatedTest(activeQuery);
            automatedSmokeTestSearchPhase = 4;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test found a real placed-lantern marker in English and started the active-language query for the same loaded block."
            );
            return;
        }

        if (automatedSmokeTestSearchPhase != 4) return;
        if (HasAutomatedLanternBlockMarker())
        {
            automatedSmokeTestSearchPassed = true;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test found the same real placed-lantern marker in English and the active game language."
            );
        }
        else if (searchController.BlockScanComplete)
        {
            automatedSmokeTestSearchPhase = -1;
            capi.Logger.Error(
                "[ModernAtlas] Automated active-language placed-lantern search completed without the marker found by the English query."
            );
        }
    }

    private bool TrySelectAutomatedLanternBlock(out string activeQuery)
    {
        activeQuery = "";
        foreach (AtlasSearchResult result in searchController.BlockResults)
        {
            if (result.Kind != AtlasSearchResultKind.Block) continue;
            var position = new BlockPos(
                (int)Math.Floor(result.X),
                (int)Math.Floor(result.Y),
                (int)Math.Floor(result.Z)
            );
            Block block = capi.World.BlockAccessor.GetBlock(position);
            if (!searchController.TryGetActiveLanguageQueryForLanternBlock(
                    block,
                    out activeQuery
                ))
            {
                continue;
            }

            automatedLanternBlockX = result.X;
            automatedLanternBlockY = result.Y;
            automatedLanternBlockZ = result.Z;
            return true;
        }
        return false;
    }

    private bool HasAutomatedLanternBlockMarker()
    {
        foreach (AtlasSearchResult result in searchController.BlockResults)
        {
            if (result.Kind == AtlasSearchResultKind.Block
                && Math.Abs(result.X - automatedLanternBlockX) <= 0.01
                && Math.Abs(result.Y - automatedLanternBlockY) <= 0.01
                && Math.Abs(result.Z - automatedLanternBlockZ) <= 0.01)
            {
                return true;
            }
        }
        return false;
    }

    private bool TryGetAutomatedBlockSearchQuery(out string query)
    {
        query = "";
        int worldX = (int)Math.Floor(capi.World.Player.Entity.Pos.X);
        int worldZ = (int)Math.Floor(capi.World.Player.Entity.Pos.Z);
        int surfaceY = capi.World.BlockAccessor.GetRainMapHeightAt(worldX, worldZ);
        int startY = Math.Clamp(
            surfaceY > 0
                ? surfaceY
                : (int)Math.Floor(capi.World.Player.Entity.Pos.Y) - 1,
            0,
            capi.World.BlockAccessor.MapSizeY - 1
        );
        BlockPos position = new(worldX, startY, worldZ);
        for (int offset = 0; offset <= 32 && position.Y >= 0; offset++, position.Y--)
        {
            Block block = capi.World.BlockAccessor.GetBlock(position);
            string path = block.Code?.Path ?? "";
            if (block.Id == 0 || path.Length < 2) continue;
            query = path;
            return true;
        }
        return false;
    }

    private void ExerciseAutomatedMapLayers()
    {
        if (automatedSmokeTestMapLayerPassed || !automatedSmokeTestSearchPassed)
        {
            return;
        }

        if (automatedSmokeTestMapLayerPhase == 0)
        {
            targetPitchDegrees = 72;
            pitchDegrees = 72;
            FitLoadedTerrain();
            zoom = targetZoom;
            SetMapLayer(AtlasMapLayer.Moisture);
            automatedSmokeTestMapLayerPhase = 1;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test started the loaded-data moisture layer."
            );
            return;
        }

        if (automatedSmokeTestMapLayerPhase == 1)
        {
            if (!mapLayerTexture.Ready) return;
            if (mapLayerTexture.TextureId <= 0)
            {
                automatedSmokeTestMapLayerPhase = -1;
                capi.Logger.Error(
                    "[ModernAtlas] Automated map-layer test prepared moisture data without a GPU texture."
                );
                return;
            }
            if (QueueAutomatedMapLayerScreenshot("layer-moisture"))
            {
                automatedSmokeTestMapLayerPhase = 10;
                return;
            }
            automatedSmokeTestMapLayerPhase = 10;
        }

        if (automatedSmokeTestMapLayerPhase == 10)
        {
            if (pendingAutomatedMapLayerScreenshotSuffix != null) return;
            SetMapLayer(AtlasMapLayer.OreDensity);
            automatedSmokeTestMapLayerPhase = 2;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test rendered the moisture layer and started the Creative/Cheat ore-density layer."
            );
            return;
        }

        if (automatedSmokeTestMapLayerPhase == 2 && mapLayerTexture.Ready)
        {
            if (!AtlasOrePotential.ValidateThresholds())
            {
                automatedSmokeTestMapLayerPhase = -1;
                capi.Logger.Error(
                    "[ModernAtlas] Automated ore-potential test rejected the vanilla grade thresholds."
                );
                return;
            }

            int sampleX = (int)Math.Floor(
                capi.World.Player.Entity.Pos.X / AtlasMapLayerTexture.HorizontalSampleSize
            ) * AtlasMapLayerTexture.HorizontalSampleSize
                + AtlasMapLayerTexture.HorizontalSampleSize / 2;
            int sampleZ = (int)Math.Floor(
                capi.World.Player.Entity.Pos.Z / AtlasMapLayerTexture.HorizontalSampleSize
            ) * AtlasMapLayerTexture.HorizontalSampleSize
                + AtlasMapLayerTexture.HorizontalSampleSize / 2;
            if (!mapLayerTexture.TryInspectOre(
                sampleX,
                sampleZ,
                out AtlasOreInspection? automatedInspection
            ) || automatedInspection == null)
            {
                automatedSmokeTestMapLayerPhase = -1;
                capi.Logger.Error(
                    "[ModernAtlas] Automated ore-potential test could not inspect the loaded player column."
                );
                return;
            }

            oreHoverInspection = automatedInspection;
            oreHoverCellX = sampleX;
            oreHoverCellZ = sampleZ;
            oreHoverMouseX = AtlasViewport.X + AtlasViewport.Width / 2;
            oreHoverMouseY = AtlasViewport.Y + AtlasViewport.Height / 2;
            BuildOreHoverTexture(automatedInspection);
            if (QueueAutomatedMapLayerScreenshot("layer-ore-overview"))
            {
                automatedSmokeTestMapLayerPhase = 20;
                return;
            }
            automatedSmokeTestMapLayerPhase = 20;
        }

        if (automatedSmokeTestMapLayerPhase == 20)
        {
            if (pendingAutomatedMapLayerScreenshotSuffix != null) return;

            if (mapLayerTexture.DiscoveredOreCodes.Count > 0 || mapLayerTexture.Ready)
            {
                if (!ExerciseAutomatedOreFilterChoice())
                {
                    automatedSmokeTestMapLayerPhase = -1;
                    return;
                }
                PrepareMapLayer();
                automatedSmokeTestMapLayerPhase = 3;
                capi.Logger.Notification(
                    "[ModernAtlas] Automated smoke test selected the loaded ore filter {0} through the arrow selector.",
                    selectedOreCode ?? "<all ores>"
                );
                return;
            }
        }

        if (automatedSmokeTestMapLayerPhase == 3)
        {
            if (!mapLayerTexture.Ready) return;
            if (mapLayerTexture.TryInspectOre(
                oreHoverCellX,
                oreHoverCellZ,
                out AtlasOreInspection? filteredInspection
            ) && filteredInspection != null)
            {
                oreHoverInspection = filteredInspection;
                BuildOreHoverTexture(filteredInspection);
            }
            if (QueueAutomatedMapLayerScreenshot("layer-ore-filtered"))
            {
                automatedSmokeTestMapLayerPhase = 30;
                return;
            }
            automatedSmokeTestMapLayerPhase = 30;
        }
        if (automatedSmokeTestMapLayerPhase == 30
            && pendingAutomatedMapLayerScreenshotSuffix != null)
        {
            return;
        }
        if (automatedSmokeTestMapLayerPhase == 30)
        {
            automatedSmokeTestOreLayerZoom = zoom;
            targetZoom = Math.Clamp(
                zoom * 0.62f,
                MaximumZoomIn,
                30000
            );
            automatedSmokeTestMapLayerPhase = 31;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test is zooming the filtered ore layer while keeping its transient data texture active."
            );
            return;
        }
        if (automatedSmokeTestMapLayerPhase == 31)
        {
            if (Math.Abs(targetZoom - zoom) > 0.01f) return;
            if (QueueAutomatedMapLayerScreenshot("layer-ore-filtered-zoom"))
            {
                automatedSmokeTestMapLayerPhase = 32;
                return;
            }
            automatedSmokeTestMapLayerPhase = 32;
        }
        if (automatedSmokeTestMapLayerPhase == 32
            && pendingAutomatedMapLayerScreenshotSuffix != null)
        {
            return;
        }
        if (automatedSmokeTestMapLayerPhase is not (20 or 32)
            || !mapLayerTexture.Ready)
        {
            return;
        }
        if (mapLayerTexture.TextureId <= 0)
        {
            automatedSmokeTestMapLayerPhase = -1;
            capi.Logger.Error(
                "[ModernAtlas] Automated map-layer test prepared ore data without a GPU texture."
            );
            return;
        }

        automatedSmokeTestMapLayerPassed = true;
        targetZoom = automatedSmokeTestOreLayerZoom > 0
            ? automatedSmokeTestOreLayerZoom
            : targetZoom;
        zoom = targetZoom;
        selectedOreCode = null;
        SetMapLayer(AtlasMapLayer.TexturedTerrain);
        capi.Logger.Notification(
            "[ModernAtlas] Automated smoke test rendered climate and Creative/Cheat ore map layers from loaded data and retained the selected ore colors through zoom."
        );
    }

    private bool QueueAutomatedMapLayerScreenshot(string suffix)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            SmokeScreenshotEnvironmentVariable
        )))
        {
            return false;
        }
        pendingAutomatedMapLayerScreenshotSuffix = suffix;
        return true;
    }

}
