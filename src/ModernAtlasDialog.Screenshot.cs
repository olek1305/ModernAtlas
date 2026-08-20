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
/// Screenshot settings UI and the interactive BEFORE/AFTER filter preview
/// that runs on one frozen atlas source frame.
/// </summary>
public sealed partial class ModernAtlasDialog
{
    /// <summary>
    /// Compact collapsible screenshot panel for the main atlas UI, placed in
    /// the left column under the search and map-layer panels. Collapsed it is
    /// a slim bar with a quick TAKE action; expanded it exposes the full
    /// styling controls.
    /// </summary>
    private void ComposeScreenshotPanel(double contentX, double contentY)
    {
        const double panelWidth = 560;
        if (screenshotPanelCollapsed)
        {
            ElementBounds root = ElementBounds.Fixed(contentX + 18, contentY, panelWidth, 52);
            screenshotPanelBounds = root;
            screenshotPanel = capi.Gui.CreateCompo("modernatlas-screenshot", root)
                .AddStaticCustomDraw(
                    ElementBounds.Fixed(0, 0, panelWidth, 52),
                    AtlasUiStyle.DrawCard
                )
                .AddAtlasButton(
                    "▸",
                    ToggleScreenshotPanel,
                    ElementBounds.Fixed(8, 6, 40, 40),
                    "shot-toggle",
                    AtlasButtonStyle.Icon
                )
                .AddStaticText(
                    "SCREENSHOT",
                    AtlasUiStyle.LabelFont(12),
                    ElementBounds.Fixed(58, 14, 190, 24)
                )
                .AddDynamicText(
                    "",
                    AtlasUiStyle.DetailFont(9),
                    ElementBounds.Fixed(250, 8, 196, 36),
                    "shot-collapsed-status"
                )
                .AddAtlasButton(
                    "TAKE",
                    TakeScreenshot,
                    ElementBounds.Fixed(450, 6, 104, 40),
                    "shot-take",
                    AtlasButtonStyle.Dark
                )
                .Compose();
            screenshotPanel.GetDynamicText("shot-collapsed-status")
                ?.SetNewText(BuildScreenshotCollapsedStatus());
            return;
        }

        ElementBounds rootExpanded = ElementBounds.Fixed(contentX + 18, contentY, panelWidth, 238);
        screenshotPanelBounds = rootExpanded;
        screenshotPanel = capi.Gui.CreateCompo("modernatlas-screenshot", rootExpanded)
            .AddStaticCustomDraw(
                ElementBounds.Fixed(0, 0, panelWidth, 238),
                AtlasUiStyle.DrawCard
            )
            .AddAtlasButton(
                "▾",
                ToggleScreenshotPanel,
                ElementBounds.Fixed(8, 6, 40, 40),
                "shot-toggle",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "SCREENSHOT",
                AtlasUiStyle.LabelFont(12),
                ElementBounds.Fixed(58, 14, 84, 24)
            )
            .AddStaticText(
                "Resolution",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(148, 18, 70, 24)
            )
            .AddAtlasChoice(
                new[] { "1", "2", "3", "4", "5", "6", "7", "8" },
                new[]
                {
                    "1x", "2x", "3x", "4x", "5x", "6x", "7x", "8x"
                },
                ScreenshotScaleIndex,
                OnScreenshotScaleChanged,
                ElementBounds.Fixed(218, 8, 120, 44),
                "shot-scale"
            )
            .AddStaticText(
                "Capture area",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(18, 66, 100, 24)
            )
            .AddAtlasChoice(
                new[] { "100", "75", "50", "25" },
                new[] { "100%", "75%", "50%", "25%" },
                ScreenshotCaptureAreaIndex,
                OnScreenshotCaptureAreaChanged,
                ElementBounds.Fixed(120, 56, 154, 44),
                "shot-area"
            )
            .AddAtlasButton(
                "TAKE",
                TakeScreenshot,
                ElementBounds.Fixed(450, 56, 104, 44),
                "shot-take",
                AtlasButtonStyle.Dark
            )
            .AddStaticText(
                "Resolution controls the tile grid. Capture area selects a centered part of the current view; the camera returns when done.",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(18, 108, 524, 38)
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(10),
                ElementBounds.Fixed(18, 148, 524, 66),
                "shot-preview"
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(9),
                ElementBounds.Fixed(18, 214, 524, 18),
                "shot-status"
            )
            .Compose();
        ConfigureScreenshotSettingsControls();
    }

    private void ConfigureScreenshotSettingsControls()
    {
        GuiComposer? options = screenshotPanel;
        options?.GetAtlasChoice("shot-scale")?.SetSelectedIndex(
            ScreenshotScaleIndex
        );
        options?.GetAtlasChoice("shot-area")?.SetSelectedIndex(
            ScreenshotCaptureAreaIndex
        );
        options?.GetDynamicText("shot-preview")?.SetNewText(
            BuildScreenshotPreviewText()
        );
        options?.GetAtlasChoice("shot-filter-preset")?.SetSelectedIndex(
            ScreenshotFilterPresetIndex
        );
        options?.GetAtlasSwitch("shot-filter-enabled")?.SetValue(
            config.ScreenshotFiltersEnabled
                && AtlasScreenshotFilterSettings.NormalizePreset(
                    config.ScreenshotFilterPreset
                ) != "off"
        );
        ConfigureScreenshotFilterSlider(
            screenshotFilterTuningPanel,
            "shot-filter-intensity",
            config.ScreenshotFilterIntensityPercent,
            0,
            200,
            5
        );
        ConfigureScreenshotFilterSlider(
            screenshotFilterTuningPanel,
            "shot-filter-saturation",
            config.ScreenshotSaturationPercent,
            0,
            200,
            5
        );
        ConfigureScreenshotFilterSlider(
            screenshotFilterTuningPanel,
            "shot-filter-contrast",
            config.ScreenshotContrastPercent,
            0,
            200,
            5
        );
        ConfigureScreenshotFilterSlider(
            screenshotFilterTuningPanel,
            "shot-filter-temperature",
            config.ScreenshotTemperaturePercent,
            -100,
            100,
            5
        );
        ConfigureScreenshotFilterSlider(
            screenshotFilterTuningPanel,
            "shot-filter-shadow-tint",
            config.ScreenshotShadowTintStrengthPercent,
            0,
            100,
            5
        );
        ConfigureScreenshotFilterSlider(
            screenshotFilterTuningPanel,
            "shot-filter-ao",
            config.ScreenshotAmbientOcclusionPercent,
            0,
            100,
            5
        );
        ConfigureScreenshotFilterSlider(
            screenshotFilterTuningPanel,
            "shot-filter-indirect",
            config.ScreenshotIndirectLightPercent,
            0,
            100,
            5
        );
        ConfigureScreenshotFilterSlider(
            screenshotFilterTuningPanel,
            "shot-filter-bloom",
            config.ScreenshotBloomPercent,
            0,
            100,
            5
        );
    }

    private void ConfigureScreenshotFilterSlider(
        GuiComposer? composer,
        string key,
        int current,
        int minimum,
        int maximum,
        int step
    )
    {
        composer?.GetAtlasSlider(key)?.SetValues(
            current,
            minimum,
            maximum,
            step,
            "%"
        );
    }

    private void SyncScreenshotSettingsControls()
    {
        ConfigureScreenshotSettingsControls();
        if (screenshotPreviewOpen)
        {
            screenshotPreviewFilterDirty = true;
            SyncScreenshotPreviewControls();
        }
    }

    private bool ToggleScreenshotPanel()
    {
        ToggleScreenshotOptions();
        return true;
    }

    private bool ToggleScreenshotOptions()
    {
        if (!ScreenshotOptionsOpen)
        {
            settingsScrollOffset = 0;
        }
        RequestBottomPanel(AtlasPanelSection.ScreenshotOptions);
        return true;
    }

    private bool CloseScreenshotOptions()
    {
        if (ScreenshotOptionsOpen) RequestBottomPanel(AtlasPanelSection.None);
        return true;
    }

    private bool OpenScreenshotFilterTuning()
    {
        if (!ScreenshotFilterUsesCustomPreset)
        {
            screenshotStatusOverride =
                "Filter tuning is available only for the Custom screenshot preset.";
            screenshotStatusShownUntilMilliseconds =
                capi.ElapsedMilliseconds + 4000;
            pendingScreenshotPanelRecompose = ScreenshotOptionsOpen;
            return true;
        }
        settingsScrollOffset = 0;
        RequestBottomPanel(AtlasPanelSection.ScreenshotFilterTuning);
        return true;
    }

    private bool CloseScreenshotFilterTuning()
    {
        settingsScrollOffset = 0;
        RequestBottomPanel(AtlasPanelSection.ScreenshotOptions);
        return true;
    }

    internal bool ScreenshotPanelExpanded =>
        ScreenshotOptionsOpen || ScreenshotFilterTuningOpen;

    private string BuildScreenshotFilterStatusText()
    {
        string preset = AtlasScreenshotFilterSettings.NormalizePreset(
            config.ScreenshotFilterPreset
        );
        string label = !config.ScreenshotFiltersEnabled || preset == "off"
            ? "Off"
            : AtlasScreenshotFilterSettings.PresetNames[
                Math.Clamp(
                    Array.IndexOf(AtlasScreenshotFilterSettings.PresetValues, preset),
                    0,
                    AtlasScreenshotFilterSettings.PresetNames.Length - 1
                )
            ];
        return $"Filter: {label} | change appearance in Preview";
    }

    private string BuildScreenshotCollapsedStatus()
    {
        if (tileScreenshot.Busy && tileScreenshot.PendingPath is string pendingPath)
        {
            return $"Saving to:\n{ShortenScreenshotPath(pendingPath, 27)}";
        }
        if (tileScreenshot.LastSavedPath is string savedPath)
        {
            return $"Saved to:\n{ShortenScreenshotPath(savedPath, 27)}";
        }
        if (tileScreenshot.LastError is string error)
        {
            return $"Screenshot error:\n{ShortenScreenshotPath(error, 27)}";
        }

        int scale = Math.Clamp(
            config.ScreenshotScale,
            AtlasTiledScreenshot.MinimumResolutionScale,
            AtlasTiledScreenshot.MaximumResolutionScale
        );
        int area = AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
            config.ScreenshotCaptureAreaPercent
        );
        string filterPreset = !config.ScreenshotFiltersEnabled
            ? "Off"
            : AtlasScreenshotFilterSettings.PresetNames[
                ScreenshotFilterPresetIndex
            ];
        return $"{scale}x / {area}% area / {filterPreset}";
    }

    private void RecomposeScreenshotPanel()
    {
        if (ScreenshotOptionsOpen || ScreenshotFilterTuningOpen)
        {
            OpenBottomPanelImmediately(bottomPanelSection);
        }
    }

    private int ScreenshotScaleIndex => Math.Clamp(
        config.ScreenshotScale,
        AtlasTiledScreenshot.MinimumResolutionScale,
        AtlasTiledScreenshot.MaximumResolutionScale
    ) - AtlasTiledScreenshot.MinimumResolutionScale;

    private int ScreenshotCaptureAreaIndex =>
        AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
            config.ScreenshotCaptureAreaPercent
        ) switch
        {
            100 => 0,
            75 => 1,
            50 => 2,
            _ => 3
        };

    private int ScreenshotFilterPresetIndex =>
        AtlasScreenshotFilterSettings.PresetIndex(config);

    private bool OpenScreenshotPreview()
    {
        if (screenshotPreviewOpen || screenshotPreviewOpening) return true;
        if (tileScreenshot.Busy)
        {
            screenshotStatusOverride =
                "Screenshot preview is unavailable while a capture is still processing.";
            screenshotStatusShownUntilMilliseconds =
                capi.ElapsedMilliseconds + 5000;
            return true;
        }
        ResetPointerDrag();
        // A pending presentation toggle can otherwise commit in the same
        // frame as the fresh source render and change the viewport under the
        // comparison. Preview owns the frozen presentation until it closes.
        presentationChangeCoordinator.Reset(config.RenderOnScroll);
        bottomPanel?.UnfocusOwnElements();
        DisposeBottomPanelComposer();
        screenshotPreviewScrollOffset = 0;
        screenshotPreviewFilterDiagnostic = null;
        screenshotPreviewFilterBlocked = false;
        screenshotPreviewFilterDirty = true;
        screenshotPreviewExpectedEntityCount =
            exactChunkRenderer?.LastRenderedEntityCount ?? 0;
        screenshotPreviewRenderedEntityCount = 0;
        screenshotPreviewOpening = true;
        screenshotPreviewOpen = false;
        lastScreenshotPreviewCloseMilliseconds = -10000;
        // The next ordinary atlas render becomes the frozen source. Keep all
        // living models visible in this interactive preview; only an actual
        // tiled PNG capture hides the local photographer.
        lastAtlasWorldRenderMilliseconds = 0;
        capi.Logger.Notification(
            "[ModernAtlas] Screenshot Preview requested; forcing one fresh atlas frame with living models preserved before opening the modal."
        );
        return true;
    }

    private void BeginScreenshotPreviewFromFreshFrame()
    {
        screenshotPreviewOpening = false;
        FrameBufferRef? source = exactChunkRenderer?.ResolvedFramebuffer;
        int sourceTextureId = source?.ColorTextureIds is { Length: > 0 }
            ? source.ColorTextureIds[0]
            : 0;
        if (source == null
            || source.Disposed
            || sourceTextureId <= 0)
        {
            screenshotPreviewFilterDiagnostic =
                "Preview source framebuffer is unavailable after the fresh atlas frame.";
            screenshotStatusOverride = screenshotPreviewFilterDiagnostic;
            screenshotStatusShownUntilMilliseconds =
                capi.ElapsedMilliseconds + 6000;
            DisposeScreenshotPreviewState();
            lastAtlasWorldRenderMilliseconds = 0;
            return;
        }

        AtlasViewportBounds viewport = AtlasViewport;
        float viewportAspect = viewport.Width
            / (float)Math.Max(1, viewport.Height);
        screenshotPreviewLayout = tileScreenshot.GetCaptureLayout(
            config.ScreenshotScale,
            config.ScreenshotCaptureAreaPercent,
            viewportAspect
        );
        if (!screenshotPreviewLayout.IsValid)
        {
            screenshotStatusOverride =
                "Screenshot preview is unavailable: the game window is too small.";
            screenshotStatusShownUntilMilliseconds =
                capi.ElapsedMilliseconds + 6000;
            DisposeScreenshotPreviewState();
            lastAtlasWorldRenderMilliseconds = 0;
            return;
        }

        screenshotPreviewSourceFramebuffer = source;
        screenshotPreviewSourceTextureId = sourceTextureId;
        screenshotPreviewAfterTextureId = sourceTextureId;
        screenshotPreviewSourceDepthTextureId =
            exactChunkRenderer?.PrimaryDepthTextureId ?? 0;
        screenshotPreviewSourceValidityTextureId =
            exactChunkRenderer?.BoundaryValidityTextureId ?? 0;
        screenshotPreviewRenderedEntityCount =
            exactChunkRenderer?.LastRenderedEntityCount ?? 0;
        if (!exactChunkRenderer!.TryGetLastAtlasCamera(
                out float[] previewProjection,
                out double[] previewView
            ))
        {
            screenshotPreviewProjection = Array.Empty<float>();
            screenshotPreviewView = Array.Empty<double>();
            screenshotPreviewFilterDiagnostic =
                "The frozen atlas camera matrices are unavailable; filtered preview is disabled until a fresh frame is available.";
        }
        else
        {
            screenshotPreviewProjection = previewProjection;
            screenshotPreviewView = previewView;
            screenshotPreviewFilterDiagnostic = null;
        }

        screenshotPreviewOpen = true;
        screenshotPreviewFilterDirty = true;
        screenshotPreviewLastFilterFrame = -1;
        ComposeScreenshotPreviewModal();
        capi.Logger.Notification(
            "[ModernAtlas] Screenshot Preview opened on one frozen source frame: {0}x{1}, capture area={2}%, readback={3}x{4}, margin={5}, living models={6} (previous frame={7}).",
            screenshotPreviewLayout.FrameWidth,
            screenshotPreviewLayout.FrameHeight,
            screenshotPreviewLayout.CaptureAreaPercent,
            screenshotPreviewLayout.StoredWidth,
            screenshotPreviewLayout.StoredHeight,
            screenshotPreviewLayout.Margin,
            screenshotPreviewRenderedEntityCount,
            screenshotPreviewExpectedEntityCount
        );
    }

    private void UpdateScreenshotPreviewFilter()
    {
        if (!screenshotPreviewOpen
            || (!screenshotPreviewFilterDirty
                && screenshotPreviewLastFilterFrame >= 0)
            || screenshotPreviewLastFilterFrame == atlasRenderFrameId)
        {
            return;
        }
        screenshotPreviewLastFilterFrame = atlasRenderFrameId;
        screenshotPreviewFilterDirty = false;
        pendingScreenshotPreviewRecompose = false;

        AtlasScreenshotFilterSettings requested =
            AtlasScreenshotFilterSettings.FromConfig(config);
        screenshotPreviewRequestedSettings = requested;
        if (!requested.Enabled)
        {
            // Off is intentionally a texture identity operation. Do not run
            // a shader pass or even copy the source so BEFORE/AFTER remain
            // bit-identical RGB views of the same frozen framebuffer.
            screenshotPreviewEffectiveSettings =
                AtlasScreenshotFilterSettings.CreatePreset("off");
            screenshotPreviewAfterTextureId = screenshotPreviewSourceTextureId;
            screenshotPreviewFilterBlocked = false;
            screenshotPreviewFilterDiagnostic =
                "FILTER OFF  ·  BEFORE and AFTER are identical.";
            SyncScreenshotPreviewControls();
            return;
        }

        if (screenshotPreviewSourceFramebuffer == null
            || screenshotPreviewSourceFramebuffer.Disposed
            || screenshotPreviewProjection.Length != 16
            || screenshotPreviewView.Length != 16)
        {
            screenshotPreviewFilterBlocked = true;
            screenshotPreviewAfterTextureId = screenshotPreviewSourceTextureId;
            screenshotPreviewFilterDiagnostic =
                "Filtered preview is unavailable because the frozen source or camera matrices are invalid.";
            SyncScreenshotPreviewControls();
            return;
        }

        if (!TryPrepareEffectiveScreenshotFilterSettings(
                requested,
                out AtlasScreenshotFilterSettings effective,
                out _,
                out string preparationDiagnostic
            ))
        {
            screenshotPreviewEffectiveSettings = requested;
            screenshotPreviewFilterBlocked = true;
            screenshotPreviewAfterTextureId = screenshotPreviewSourceTextureId;
            screenshotPreviewFilterDiagnostic =
                $"Filtered preview unavailable: {preparationDiagnostic}";
            SyncScreenshotPreviewControls();
            return;
        }

        screenshotPreviewEffectiveSettings = effective;
        FrameBufferRef? filtered = screenshotFilterPass.Apply(
            screenshotPreviewSourceFramebuffer,
            screenshotPreviewSourceDepthTextureId,
            screenshotPreviewSourceValidityTextureId,
            effective,
            screenshotPreviewProjection,
            screenshotPreviewView,
            screenshotPreviewLayout.Margin
        );
        if (filtered == null
            || filtered.Disposed
            || filtered.ColorTextureIds is not { Length: > 0 }
            || filtered.ColorTextureIds[0] <= 0)
        {
            screenshotPreviewFilterBlocked = true;
            screenshotPreviewAfterTextureId = screenshotPreviewSourceTextureId;
            screenshotPreviewFilterDiagnostic =
                $"Filtered preview unavailable: {screenshotFilterPass.LastError ?? "the GPU filter framebuffer is unavailable"}";
        }
        else
        {
            screenshotPreviewFilterBlocked = false;
            screenshotPreviewAfterTextureId = filtered.ColorTextureIds[0];
            screenshotPreviewFilterDiagnostic =
                BuildScreenshotPreviewEffectiveDiagnostic(
                    requested,
                    effective
                );
        }
        SyncScreenshotPreviewControls();
    }

    private bool TryPrepareEffectiveScreenshotFilterSettings(
        AtlasScreenshotFilterSettings requested,
        out AtlasScreenshotFilterSettings effective,
        out AtlasScreenshotFilterMode mode,
        out string diagnostic
    )
    {
        effective = requested;
        mode = requested.Enabled
            ? AtlasScreenshotFilterMode.Filtered
            : AtlasScreenshotFilterMode.Unfiltered;
        diagnostic = requested.Enabled
            ? "filtered screenshot preparation failed"
            : "the screenshot filter is Off";
        if (!requested.Enabled)
        {
            effective = AtlasScreenshotFilterSettings.CreatePreset("off");
            return true;
        }

        FrameBufferRef? resolved = exactChunkRenderer?.ResolvedFramebuffer;
        if (resolved == null
            || resolved.Disposed
            || resolved.ColorTextureIds is not { Length: > 0 }
            || resolved.ColorTextureIds[0] <= 0)
        {
            diagnostic = "the resolved atlas color framebuffer is unavailable";
            return false;
        }

        int depthTextureId = exactChunkRenderer?.PrimaryDepthTextureId ?? 0;
        int validityTextureId =
            exactChunkRenderer?.BoundaryValidityTextureId ?? 0;
        if (validityTextureId <= 0
            && screenshotFilterPass.UsesSpatialEffects(effective))
        {
            effective = effective.WithoutSpatialEffects();
            capi.Logger.Warning(
                "[ModernAtlas] Screenshot validity mask is unavailable; spatial effects are disabled while color grading remains enabled."
            );
        }
        if (depthTextureId <= 0
            && screenshotFilterPass.RequiresDepthTexture(effective))
        {
            effective = effective.WithoutDepthEffects();
            capi.Logger.Warning(
                "[ModernAtlas] Screenshot depth is unavailable; depth relief and indirect light are disabled while color grading and masked bloom remain enabled."
            );
        }
        if (!screenshotFilterPass.Preflight(
                resolved,
                depthTextureId,
                validityTextureId,
                effective,
                out string preflightDiagnostic
            ))
        {
            diagnostic = preflightDiagnostic;
            return false;
        }

        diagnostic = screenshotFilterPass.UsesSpatialEffects(effective)
            ? "filtered screenshot is ready with disclosure-masked spatial effects"
            : "filtered screenshot is ready with color grading only";
        return true;
    }

    private static string BuildScreenshotPreviewEffectiveDiagnostic(
        AtlasScreenshotFilterSettings requested,
        AtlasScreenshotFilterSettings effective
    )
    {
        if (requested.AmbientOcclusionPercent > 0
            && effective.AmbientOcclusionPercent == 0
            || requested.IndirectLightPercent > 0
            && effective.IndirectLightPercent == 0)
        {
            return "Depth is unavailable: depth relief and indirect light are disabled; color grading remains active.";
        }
        if (requested.BloomPercent > 0 && effective.BloomPercent == 0)
        {
            return "Spatial effects are unavailable: color grading remains active.";
        }
        return "READY  ·  AFTER shows exactly how the filter will look in the saved PNG.";
    }

    private void DisposeScreenshotPreviewState()
    {
        screenshotPreviewModal?.UnfocusOwnElements();
        screenshotPreviewModal?.Dispose();
        screenshotPreviewModal = null;
        screenshotPreviewOpen = false;
        screenshotPreviewOpening = false;
        screenshotPreviewFilterDirty = false;
        pendingScreenshotPreviewRecompose = false;
        screenshotPreviewFilterBlocked = false;
        screenshotPreviewLastFilterFrame = -1;
        screenshotPreviewScrollOffset = 0;
        screenshotPreviewPressedButton = null;
        screenshotPreviewFilterDiagnostic = null;
        screenshotPreviewSourceTextureId = 0;
        screenshotPreviewAfterTextureId = 0;
        screenshotPreviewSourceFramebuffer = null;
        screenshotPreviewSourceDepthTextureId = 0;
        screenshotPreviewSourceValidityTextureId = 0;
        screenshotPreviewProjection = Array.Empty<float>();
        screenshotPreviewView = Array.Empty<double>();
        screenshotPreviewBeforeBounds = null;
        screenshotPreviewAfterBounds = null;
        screenshotPreviewBounds = null;
        screenshotPreviewLayout = default;
        screenshotPreviewExpectedEntityCount = 0;
        screenshotPreviewRenderedEntityCount = 0;
    }

    private void CloseScreenshotPreview(bool returnToScreenshotPanel)
    {
        bool wasOpen = screenshotPreviewOpen || screenshotPreviewOpening
            || screenshotPreviewModal != null;
        DisposeScreenshotPreviewState();
        if (!wasOpen) return;

        lastScreenshotPreviewCloseMilliseconds = capi.ElapsedMilliseconds;
        ResetPointerDrag();
        // The next ordinary atlas frame must not be a stale frozen source.
        lastAtlasWorldRenderMilliseconds = 0;
        if (returnToScreenshotPanel
            && IsOpened()
            && !worldTeardownStarted)
        {
            OpenBottomPanelImmediately(AtlasPanelSection.ScreenshotOptions);
            SyncScreenshotSettingsControls();
        }
        capi.Logger.Notification(
            "[ModernAtlas] Screenshot Preview closed; released frozen source state and forced a fresh ordinary atlas frame."
        );
    }

    private bool TakeScreenshotFromPreview()
    {
        if (!screenshotPreviewOpen) return TakeScreenshot();
        bool filteredRequested = config.ScreenshotFiltersEnabled
            && AtlasScreenshotFilterSettings.NormalizePreset(
                config.ScreenshotFilterPreset
            ) != "off";
        if (filteredRequested && screenshotPreviewFilterBlocked)
        {
            screenshotPreviewFilterDiagnostic =
                "TAKE SCREENSHOT is disabled until the filtered preview succeeds or the filter is set to Off.";
            SyncScreenshotPreviewControls();
            return true;
        }
        CloseScreenshotPreview(false);
        return TakeScreenshot();
    }

    private bool BackToScreenshotPanel()
    {
        CloseScreenshotPreview(true);
        return true;
    }

    private bool TryGetScreenshotPreviewButtonAt(
        int mouseX,
        int mouseY,
        out string key
    )
    {
        key = "";
        if (screenshotPreviewModal == null) return false;

        string[] buttonKeys =
        {
            "preview-close",
            "preview-filter-reset",
            "preview-back",
            "preview-take"
        };
        foreach (string buttonKey in buttonKeys)
        {
            GuiElementAtlasButton? button =
                screenshotPreviewModal.GetAtlasButton(buttonKey);
            if (button?.Bounds.PointInside(mouseX, mouseY) != true) continue;
            key = buttonKey;
            return true;
        }
        return false;
    }

    private void RenderScreenshotPreviewTextures()
    {
        if (!screenshotPreviewOpen
            || screenshotPreviewBeforeBounds == null
            || screenshotPreviewAfterBounds == null)
        {
            return;
        }
        ElementBounds before = screenshotPreviewBeforeBounds;
        ElementBounds after = screenshotPreviewAfterBounds;
        scrollViewportRenderer.RenderScreenshotPreview(
            screenshotPreviewSourceTextureId,
            screenshotPreviewAfterTextureId > 0
                ? screenshotPreviewAfterTextureId
                : screenshotPreviewSourceTextureId,
            (int)Math.Round(before.absX),
            (int)Math.Round(before.absY),
            before.OuterWidthInt,
            before.OuterHeightInt,
            (int)Math.Round(after.absX),
            (int)Math.Round(after.absY),
            after.OuterWidthInt,
            after.OuterHeightInt,
            screenshotPreviewLayout
        );
    }

    private double ScreenshotPreviewScrollMaximum(
        double width,
        double height
    ) => screenshotPreviewScrollMaximumValue;

    private double screenshotPreviewScrollMaximumValue;

    private void ComposeScreenshotPreviewModal()
    {
        screenshotPreviewModal?.Dispose();
        screenshotPreviewModal = null;

        double guiScale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double guiWidth = Math.Max(1, capi.Render.FrameWidth / guiScale);
        double guiHeight = Math.Max(1, capi.Render.FrameHeight / guiScale);
        bool narrow = guiWidth < 820;
        double modalWidth = Math.Min(
            guiWidth - 16,
            narrow ? guiWidth - 16 : 1120
        );
        modalWidth = Math.Max(300, modalWidth);
        double imageGap = narrow ? 10 : 18;
        double frameAspect = screenshotPreviewLayout.StoredWidth
            / (float)Math.Max(1, screenshotPreviewLayout.StoredHeight);
        // Reserve real screen space for the filter controls. Scale both images
        // down together without changing their aspect ratio.
        double availableImageWidth = Math.Max(
            100,
            (modalWidth - 32 - imageGap) * 0.5
        );
        double imageHeight = Math.Min(
            availableImageWidth / Math.Max(0.05, frameAspect),
            narrow ? 130 : 210
        );
        imageHeight = Math.Max(72, imageHeight);
        double imageWidth = Math.Min(
            availableImageWidth,
            imageHeight * Math.Max(0.05, frameAspect)
        );
        double imageTop = 94;
        double imageX = Math.Max(
            16,
            (modalWidth - imageWidth * 2 - imageGap) * 0.5
        );
        double afterX = imageX + imageWidth + imageGap;
        double afterY = imageTop;
        double imagesBottom = afterY + imageHeight;
        double controlsTop = imagesBottom + 34;
        double footerHeight = 48;
        const int filterControlCount = 11;
        double rowStep = narrow ? 28 : 34;
        int filterRows = narrow
            ? filterControlCount
            : (filterControlCount + 1) / 2;
        double infoTop = controlsTop + (narrow ? 68 : 36);
        double sliderTop = infoTop + 64;
        double desiredContentBottom = controlsTop
            + (infoTop - controlsTop)
            + 64
            + filterRows * rowStep
            + 48;
        double modalHeight = Math.Min(
            guiHeight - 12,
            desiredContentBottom + footerHeight + 16
        );
        modalHeight = Math.Max(260, modalHeight);
        double footerTop = modalHeight - footerHeight - 8;
        double bodyBottom = Math.Max(controlsTop + 1, footerTop - 4);
        screenshotPreviewScrollMaximumValue = Math.Max(
            0,
            desiredContentBottom - bodyBottom
        );
        screenshotPreviewScrollOffset = Math.Clamp(
            screenshotPreviewScrollOffset,
            0,
            screenshotPreviewScrollMaximumValue
        );

        ElementBounds root = ElementBounds.Fixed(0, 0, modalWidth, modalHeight)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        screenshotPreviewBounds = root;
        ElementBounds beforeBounds = ElementBounds.Fixed(
            imageX,
            imageTop,
            imageWidth,
            imageHeight
        );
        ElementBounds afterBounds = ElementBounds.Fixed(
            afterX,
            afterY,
            imageWidth,
            imageHeight
        );
        screenshotPreviewBeforeBounds = beforeBounds;
        screenshotPreviewAfterBounds = afterBounds;

        double contentOffset = screenshotPreviewScrollOffset;
        double contentWidth = Math.Max(1, modalWidth - 32);
        double sliderColumnWidth = narrow
            ? contentWidth
            : Math.Max(220, (contentWidth - 14) / 2);
        double leftColumn = 16;
        double rightColumn = leftColumn + sliderColumnWidth + 14;
        double labelWidth = Math.Min(132, sliderColumnWidth * 0.47);
        double sliderWidth = Math.Max(64, sliderColumnWidth - labelWidth - 4);

        GuiComposer composer = capi.Gui.CreateCompo(
                "modernatlas-screenshot-preview",
                root
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(0, 0, modalWidth, modalHeight),
                AtlasUiStyle.DrawOpaquePreviewCard
            )
            .AddStaticText(
                "SCREENSHOT PREVIEW",
                AtlasUiStyle.TitleFont(17),
                ElementBounds.Fixed(16, 12, Math.Max(110, modalWidth - 230), 26)
            )
            .AddAtlasButton(
                "RESET TO DEFAULT",
                ResetScreenshotFilterToDefault,
                ElementBounds.Fixed(modalWidth - 190, 8, 136, 28),
                "preview-filter-reset",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "×",
                BackToScreenshotPanel,
                ElementBounds.Fixed(modalWidth - 44, 8, 32, 28),
                "preview-close",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "FILTER PRESET",
                AtlasUiStyle.LabelFont(10),
                ElementBounds.Fixed(16, 53, 86, 20)
            )
            .AddAtlasChoice(
                AtlasScreenshotFilterSettings.PresetValues,
                AtlasScreenshotFilterSettings.PresetNames,
                ScreenshotFilterPresetIndex,
                OnScreenshotFilterPresetChanged,
                ElementBounds.Fixed(104, 44, Math.Max(96, modalWidth - 204), 32),
                "preview-filter-preset"
            )
            .AddStaticText(
                "ON",
                AtlasUiStyle.DetailFont(9),
                ElementBounds.Fixed(modalWidth - 96, 53, 32, 18)
            )
            .AddAtlasSwitch(
                OnScreenshotFiltersToggled,
                ElementBounds.Fixed(modalWidth - 58, 46, 44, 28),
                "preview-filter-enabled"
            )
            .AddStaticText(
                "BEFORE",
                AtlasUiStyle.LabelFont(10),
                ElementBounds.Fixed(imageX, imageTop - 21, imageWidth, 18)
            )
            .AddStaticText(
                "AFTER",
                AtlasUiStyle.LabelFont(10),
                ElementBounds.Fixed(afterX, afterY - 21, imageWidth, 18)
            )
            .AddStaticCustomDraw(beforeBounds, AtlasUiStyle.DrawInsetCard)
            .AddStaticCustomDraw(afterBounds, AtlasUiStyle.DrawInsetCard);

        GuiElementClipHelpler.BeginClip(
            composer,
            ElementBounds.Fixed(
                0,
                controlsTop,
                modalWidth,
                Math.Max(1, bodyBottom - controlsTop)
            )
        );
        if (narrow)
        {
            composer
                .AddStaticText(
                    "Resolution",
                    AtlasUiStyle.DetailFont(10),
                    ElementBounds.Fixed(16, 7 - contentOffset, 72, 20)
                )
                .AddAtlasChoice(
                    new[] { "1", "2", "3", "4", "5", "6", "7", "8" },
                    new[] { "1x", "2x", "3x", "4x", "5x", "6x", "7x", "8x" },
                    ScreenshotScaleIndex,
                    OnScreenshotScaleChanged,
                    ElementBounds.Fixed(90, -contentOffset, contentWidth - 74, 30),
                    "preview-scale"
                )
                .AddStaticText(
                    "Capture area",
                    AtlasUiStyle.DetailFont(10),
                    ElementBounds.Fixed(16, 43 - contentOffset, 82, 20)
                )
                .AddAtlasChoice(
                    new[] { "100", "75", "50", "25" },
                    new[] { "100%", "75%", "50%", "25%" },
                    ScreenshotCaptureAreaIndex,
                    OnScreenshotCaptureAreaChanged,
                    ElementBounds.Fixed(102, 36 - contentOffset, contentWidth - 86, 30),
                    "preview-area"
                );
        }
        else
        {
            composer
                .AddStaticText(
                    "Resolution",
                    AtlasUiStyle.DetailFont(10),
                    ElementBounds.Fixed(16, 7 - contentOffset, 70, 20)
                )
                .AddAtlasChoice(
                    new[] { "1", "2", "3", "4", "5", "6", "7", "8" },
                    new[] { "1x", "2x", "3x", "4x", "5x", "6x", "7x", "8x" },
                    ScreenshotScaleIndex,
                    OnScreenshotScaleChanged,
                    ElementBounds.Fixed(86, -contentOffset, 118, 30),
                    "preview-scale"
                )
                .AddStaticText(
                    "Capture area",
                    AtlasUiStyle.DetailFont(10),
                    ElementBounds.Fixed(222, 7 - contentOffset, 84, 20)
                )
                .AddAtlasChoice(
                    new[] { "100", "75", "50", "25" },
                    new[] { "100%", "75%", "50%", "25%" },
                    ScreenshotCaptureAreaIndex,
                    OnScreenshotCaptureAreaChanged,
                    ElementBounds.Fixed(308, -contentOffset, 118, 30),
                    "preview-area"
                )
                .AddStaticText(
                    "RGB BALANCE AFFECTS THE WHOLE IMAGE",
                    AtlasUiStyle.DetailFont(9),
                    ElementBounds.Fixed(444, 7 - contentOffset, 260, 20)
                );
        }

        composer
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(16, infoTop - controlsTop - contentOffset, contentWidth, 58),
                "preview-info"
            );

        (string Label, string Key, ActionConsumable<int> Callback, int Value, int Min, int Max)[] controls =
        {
            ("Intensity", "preview-filter-intensity", OnScreenshotFilterIntensityChanged, config.ScreenshotFilterIntensityPercent, 0, 200),
            ("Saturation", "preview-filter-saturation", OnScreenshotSaturationChanged, config.ScreenshotSaturationPercent, 0, 200),
            ("Contrast", "preview-filter-contrast", OnScreenshotContrastChanged, config.ScreenshotContrastPercent, 0, 200),
            ("Temperature", "preview-filter-temperature", OnScreenshotTemperatureChanged, config.ScreenshotTemperaturePercent, -100, 100),
            ("Shadow tint", "preview-filter-shadow-tint", OnScreenshotShadowTintChanged, config.ScreenshotShadowTintStrengthPercent, 0, 100),
            ("Depth relief", "preview-filter-ao", OnScreenshotAmbientOcclusionChanged, config.ScreenshotAmbientOcclusionPercent, 0, 100),
            ("Indirect light", "preview-filter-indirect", OnScreenshotIndirectLightChanged, config.ScreenshotIndirectLightPercent, 0, 100),
            ("Bloom", "preview-filter-bloom", OnScreenshotBloomChanged, config.ScreenshotBloomPercent, 0, 100),
            ("Red balance", "preview-filter-red", OnScreenshotRedChanged, config.ScreenshotRedBalancePercent, 50, 150),
            ("Green balance", "preview-filter-green", OnScreenshotGreenChanged, config.ScreenshotGreenBalancePercent, 50, 150),
            ("Blue balance", "preview-filter-blue", OnScreenshotBlueChanged, config.ScreenshotBlueBalancePercent, 50, 150)
        };
        for (int index = 0; index < controls.Length; index++)
        {
            int row = narrow ? index : index / 2;
            bool right = !narrow && index % 2 == 1;
            double x = right ? rightColumn : leftColumn;
            double y = sliderTop - controlsTop
                + row * rowStep
                - contentOffset;
            composer
                .AddStaticText(
                    controls[index].Label,
                    AtlasUiStyle.DetailFont(10),
                    ElementBounds.Fixed(x, y + 2, labelWidth, 22)
                )
                .AddAtlasSlider(
                    controls[index].Callback,
                    ElementBounds.Fixed(x + labelWidth, y, sliderWidth, 24),
                    controls[index].Key
                );
        }
        double diagnosticY = sliderTop - controlsTop
            + (narrow ? controls.Length : (controls.Length + 1) / 2) * rowStep
            + 8
            - contentOffset;
        composer.AddDynamicText(
            "",
            AtlasUiStyle.DetailFont(9),
            ElementBounds.Fixed(16, diagnosticY, contentWidth, 40),
            "preview-diagnostic"
        );
        GuiElementClipHelpler.EndClip(composer);

        composer
            .AddAtlasButton(
                "BACK TO SP",
                BackToScreenshotPanel,
                ElementBounds.Fixed(16, modalHeight - 42, Math.Max(120, modalWidth * 0.35), 30),
                "preview-back",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "TAKE SCREENSHOT",
                TakeScreenshotFromPreview,
                ElementBounds.Fixed(Math.Max(16, modalWidth - Math.Max(170, modalWidth * 0.40) - 16), modalHeight - 42, Math.Max(170, modalWidth * 0.40), 30),
                "preview-take",
                AtlasButtonStyle.Dark
            )
            .AddDynamicText(
                screenshotPreviewScrollMaximumValue > 0
                    ? "Scroll for all Custom controls"
                    : "",
                AtlasUiStyle.DetailFont(8),
                ElementBounds.Fixed(16, modalHeight - 12, Math.Max(120, modalWidth - 32), 12),
                "preview-scroll-status"
            );

        screenshotPreviewModal = composer.Compose(false);
        SyncScreenshotPreviewControls();
    }

    private void SyncScreenshotPreviewControls()
    {
        GuiComposer? preview = screenshotPreviewModal;
        if (preview == null) return;
        preview.GetAtlasChoice("preview-scale")?.SetSelectedIndex(
            ScreenshotScaleIndex
        );
        preview.GetAtlasChoice("preview-area")?.SetSelectedIndex(
            ScreenshotCaptureAreaIndex
        );
        preview.GetAtlasChoice("preview-filter-preset")?.SetSelectedIndex(
            ScreenshotFilterPresetIndex
        );
        preview.GetAtlasSwitch("preview-filter-enabled")?.SetValue(
            config.ScreenshotFiltersEnabled
                && AtlasScreenshotFilterSettings.NormalizePreset(
                    config.ScreenshotFilterPreset
                ) != "off"
        );
        GuiElementAtlasButton? takeButton = preview.GetAtlasButton("preview-take");
        if (takeButton != null)
        {
            bool filteredRequested = config.ScreenshotFiltersEnabled
                && AtlasScreenshotFilterSettings.NormalizePreset(
                    config.ScreenshotFilterPreset
                ) != "off";
            takeButton.Enabled = !filteredRequested || !screenshotPreviewFilterBlocked;
        }
        ConfigureScreenshotFilterSlider(
            preview,
            "preview-filter-intensity",
            config.ScreenshotFilterIntensityPercent,
            0,
            200,
            5
        );
        ConfigureScreenshotFilterSlider(
            preview,
            "preview-filter-saturation",
            config.ScreenshotSaturationPercent,
            0,
            200,
            5
        );
        ConfigureScreenshotFilterSlider(
            preview,
            "preview-filter-contrast",
            config.ScreenshotContrastPercent,
            0,
            200,
            5
        );
        ConfigureScreenshotFilterSlider(
            preview,
            "preview-filter-temperature",
            config.ScreenshotTemperaturePercent,
            -100,
            100,
            5
        );
        ConfigureScreenshotFilterSlider(
            preview,
            "preview-filter-shadow-tint",
            config.ScreenshotShadowTintStrengthPercent,
            0,
            100,
            5
        );
        ConfigureScreenshotFilterSlider(
            preview,
            "preview-filter-ao",
            config.ScreenshotAmbientOcclusionPercent,
            0,
            100,
            5
        );
        ConfigureScreenshotFilterSlider(
            preview,
            "preview-filter-indirect",
            config.ScreenshotIndirectLightPercent,
            0,
            100,
            5
        );
        ConfigureScreenshotFilterSlider(
            preview,
            "preview-filter-bloom",
            config.ScreenshotBloomPercent,
            0,
            100,
            5
        );
        ConfigureScreenshotFilterSlider(
            preview,
            "preview-filter-red",
            config.ScreenshotRedBalancePercent,
            50,
            150,
            5
        );
        ConfigureScreenshotFilterSlider(
            preview,
            "preview-filter-green",
            config.ScreenshotGreenBalancePercent,
            50,
            150,
            5
        );
        ConfigureScreenshotFilterSlider(
            preview,
            "preview-filter-blue",
            config.ScreenshotBlueBalancePercent,
            50,
            150,
            5
        );
        preview.GetDynamicText("preview-info")?.SetNewText(
            BuildScreenshotPreviewInfoText()
        );
        preview.GetDynamicText("preview-diagnostic")?.SetNewText(
            screenshotPreviewFilterDiagnostic ?? ""
        );
        preview.GetDynamicText("preview-scroll-status")?.SetNewText(
            screenshotPreviewScrollMaximumValue > 0
                ? "Scroll for all Custom controls"
                : ""
        );
    }

    private string BuildScreenshotPreviewInfoText()
    {
        int scale = Math.Clamp(
            config.ScreenshotScale,
            AtlasTiledScreenshot.MinimumResolutionScale,
            AtlasTiledScreenshot.MaximumResolutionScale
        );
        int area = AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
            config.ScreenshotCaptureAreaPercent
        );
        AtlasScreenshotPreview preview = tileScreenshot.GetPreview(
            scale,
            area,
            AtlasViewport.Width / (float)Math.Max(1, AtlasViewport.Height)
        );
        if (!preview.IsValid)
        {
            return "OUTPUT PNG unavailable — the game window is too small.";
        }
        string summary =
            $"OUTPUT PNG  {preview.OutputWidth} × {preview.OutputHeight} px  ·  {preview.OutputMegapixels:0.0} MP\n"
            + $"{preview.EffectiveDetailFactor:0.#}× effective detail  ·  {scale}×{scale} tile grid  ·  {area}% centered capture";
        if (!preview.WasDownsampled) return summary;

        return summary
            + $"\nSAFETY LIMIT  Reduced from {preview.RequestedWidth} × {preview.RequestedHeight} px";
    }

}
