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
/// Atlas GUI composition: the toolbar overlay, the animated bottom panel and
/// the modal open/close actions that select a panel section.
/// </summary>
public sealed partial class ModernAtlasDialog
{
    private void RecomposeInterface()
    {
        AtlasPanelSection section = bottomPanelSection != AtlasPanelSection.None
            ? bottomPanelSection
            : queuedBottomPanelSection;
        bool panelWasVisible = bottomPanelProgress > 0.01f
            || section != AtlasPanelSection.None;
        DisposeBottomPanelComposer();
        ComposeOverlay();
        if (panelWasVisible && section != AtlasPanelSection.None)
        {
            OpenBottomPanelImmediately(section);
        }
        composedFrameWidth = capi.Render.FrameWidth;
        composedFrameHeight = capi.Render.FrameHeight;
        composedGuiScale = RuntimeEnv.GUIScale;
        SyncToolbarControls();
        SyncSettingsControls();
        SyncPerformanceControls();
        SyncCreativeSettingsControls();
        SyncMapLayerControls();
        SyncScreenshotSettingsControls();
        return;

#if false
        overlay?.Dispose();
        searchPanel?.Dispose();
        mapLayerPanel?.Dispose();
        creativeSettingsShortcut?.Dispose();
        settingsModal?.Dispose();
        performanceModal?.Dispose();
        creativeSettingsModal?.Dispose();
        visualLabModal?.Dispose();
        screenshotPanel?.Dispose();
        screenshotProgressModal?.Dispose();
        unitPanel?.Dispose();
        overlay = null;
        searchPanel = null;
        mapLayerPanel = null;
        creativeSettingsShortcut = null;
        settingsModal = null;
        performanceModal = null;
        creativeSettingsModal = null;
        visualLabModal = null;
        screenshotPanel = null;
        screenshotProgressModal = null;
        unitPanel = null;
        if (tileScreenshot.Busy)
        {
            // A presentation recomposition cannot happen through user input
            // while the capture modal owns the pointer, but automated tests
            // may drive both; never leave the camera pinned to a tile.
            tileScreenshot.Cancel();
            RestoreScreenshotCamera();
            pendingScreenshotRequest = false;
        }
        ComposeOverlay();
        composedFrameWidth = capi.Render.FrameWidth;
        composedFrameHeight = capi.Render.FrameHeight;
        composedGuiScale = RuntimeEnv.GUIScale;
        if (searchController.Query.Length > 0)
        {
            searchPanel?.GetTextInput("search-input")?.SetValue(
                searchController.Query,
                true
            );
        }
        SyncSettingsControls();
        SyncPerformanceControls();
        SyncCreativeSettingsControls();
        SyncMapLayerChoice();
    }
#endif
    }

    private void RecomposeViewportInterface()
    {
        overlay?.Dispose();
        overlay = null;
        ComposeOverlay(true);
        RepositionActiveBottomPanel();
        composedFrameWidth = capi.Render.FrameWidth;
        composedFrameHeight = capi.Render.FrameHeight;
        composedGuiScale = RuntimeEnv.GUIScale;
        return;

#if false
        // Presentation changes alter only the map-relative controls. Keep
        // Settings and every modal child alive so a switch cannot receive its
        // MouseUp on a newly-created composer and so modal input remains valid.
        overlay?.UnfocusOwnElements();
        searchPanel?.UnfocusOwnElements();
        mapLayerPanel?.UnfocusOwnElements();
        creativeSettingsShortcut?.UnfocusOwnElements();
        unitPanel?.UnfocusOwnElements();
        overlay?.Dispose();
        searchPanel?.Dispose();
        mapLayerPanel?.Dispose();
        creativeSettingsShortcut?.Dispose();
        unitPanel?.Dispose();
        overlay = null;
        searchPanel = null;
        mapLayerPanel = null;
        creativeSettingsShortcut = null;
        unitPanel = null;

        ComposeOverlay(true);
        composedFrameWidth = capi.Render.FrameWidth;
        composedFrameHeight = capi.Render.FrameHeight;
        composedGuiScale = RuntimeEnv.GUIScale;
        if (searchController.Query.Length > 0)
        {
            searchPanel?.GetTextInput("search-input")?.SetValue(
                searchController.Query,
                true
            );
        }
        SyncMapLayerChoice();
    }
#endif
    }

    private void ComposeCompactOverlay()
    {
        overlay?.Dispose();
        overlay = null;

        AtlasViewportBounds viewport = AtlasViewport;
        double guiScale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double contentX = config.RenderOnScroll ? viewport.X / guiScale : 0;
        double contentY = config.RenderOnScroll ? viewport.Y / guiScale : 0;
        double guiWidth = viewport.Width / guiScale;
        double guiHeight = viewport.Height / guiScale;
        double toolbarWidth = ToolbarWidthGui(guiWidth);
        double toolbarX = contentX + 10;
        double toolbarY = contentY + 10;
        double toolbarHeight = Math.Max(170, guiHeight - 20);
        double rowHeight = Math.Clamp((toolbarHeight - 62) / 7, 24, 30);
        double rowGap = Math.Max(2, rowHeight * 0.12);
        double buttonX = toolbarX + 10;
        double buttonWidth = toolbarWidth - 20;
        // Leave enough breathing room for the larger version and view
        // distance labels without letting the first button overlap them.
        double sectionY = toolbarY + 66;
        double pairWidth = Math.Max(24, (buttonWidth - 4) * 0.5);
        double pairY = sectionY + rowHeight + rowGap;
        double mapY = pairY + rowHeight + rowGap;
        double searchY = mapY + rowHeight + rowGap;
        double instrumentY = searchY + rowHeight + rowGap;
        double exitY = toolbarY + toolbarHeight - rowHeight - 9;
        double hideY = exitY - rowHeight - rowGap;

        ElementBounds tooltipBounds = ElementBounds.Fixed(
            toolbarX + toolbarWidth + 8,
            contentY,
            Math.Max(120, guiWidth - toolbarWidth - 20),
            guiHeight
        );

        overlay = capi.Gui.CreateCompo("modernatlas-compact-overlay", ElementBounds.Fill)
            .AddStaticCustomDraw(
                ElementBounds.Fixed(toolbarX, toolbarY, toolbarWidth, toolbarHeight),
                AtlasUiStyle.DrawToolbarPanel
            )
            .AddStaticText(
                "ModernAtlas",
                AtlasUiStyle.TitleFont(13),
                ElementBounds.Fixed(toolbarX + 10, toolbarY + 10, toolbarWidth - 20, 18)
            )
            .AddStaticText(
                "v0.6.7",
                AtlasUiStyle.DetailFont(14),
                ElementBounds.Fixed(toolbarX + 10, toolbarY + 27, toolbarWidth - 20, 18)
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(toolbarX + 10, toolbarY + 46, toolbarWidth - 20, 16),
                "status"
            )
            .AddAtlasButton(
                "Settings",
                ToggleSettingsModal,
                ElementBounds.Fixed(buttonX, sectionY, buttonWidth, rowHeight),
                "settings-button",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "Shot",
                TakeQuickScreenshot,
                ElementBounds.Fixed(buttonX, pairY, pairWidth, rowHeight),
                "quick-screenshot-button",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "Setup",
                ToggleScreenshotOptions,
                ElementBounds.Fixed(buttonX + pairWidth + 4, pairY, pairWidth, rowHeight),
                "screenshot-options-button",
                AtlasButtonStyle.Compact
            )
            // The indicator lives inside the toolbar's existing width: the
            // Layers button gives up the space, so nothing more of the map is
            // covered.
            .AddAtlasButton(
                "Layers",
                ToggleMapOptions,
                ElementBounds.Fixed(
                    buttonX,
                    mapY,
                    Math.Max(40, buttonWidth - LayerIndicatorReservedWidth),
                    rowHeight
                ),
                "map-options-button",
                AtlasButtonStyle.Compact
            )

            .AddAtlasButton(
                "Search",
                ToggleSearchPanel,
                ElementBounds.Fixed(buttonX, searchY, buttonWidth, rowHeight),
                "search-button",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "Hand",
                ToggleInstrumentPanel,
                ElementBounds.Fixed(buttonX, instrumentY, buttonWidth, rowHeight),
                "instrument-button",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "Hide",
                HideInterface,
                ElementBounds.Fixed(buttonX, hideY, buttonWidth, rowHeight),
                "hide-ui-button",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "Exit",
                CloseAtlas,
                ElementBounds.Fixed(buttonX, exitY, buttonWidth, rowHeight),
                "exit-button",
                AtlasButtonStyle.Compact
            )
            .AddDynamicCustomDraw(
                tooltipBounds,
                (context, surface, bounds) => AtlasUiStyle.DrawTooltip(
                    context,
                    surface,
                    bounds,
                    toolbarTooltipText,
                    toolbarTooltipLocalY
                ),
                "toolbar-tooltip"
            )
            .Compose(false);

        toolbarTooltipText = "";
        toolbarTooltipLocalY = 0;
        SyncToolbarControls();
    }

    private const double LayerIndicatorReservedWidth = 16;
    private const double LayerIndicatorDotSize = 11;

    /// <summary>
    /// Builds the active-layer dot: neutral while textured terrain is live,
    /// otherwise the layer's own mid-ramp color. It is a small texture drawn
    /// over the toolbar rather than a composer element, because the toolbar's
    /// interactive buttons are painted after the composer's static pass and
    /// would cover it.
    /// </summary>
    private void EnsureToolbarLayerIndicatorTexture()
    {
        if (layerIndicatorTexture is { TextureId: > 0 }
            && layerIndicatorTextureLayer == activeMapLayer)
        {
            return;
        }

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        int size = Math.Max(8, (int)Math.Ceiling(LayerIndicatorDotSize * scale));
        layerIndicatorTexture ??= new LoadedTexture(capi);
        using ImageSurface surface = new(Format.Argb32, size, size);
        using Context context = new(surface);
        AtlasUiStyle.Clear(context);

        (float red, float green, float blue) = AtlasToolbarLayerIndicator.Color(activeMapLayer);
        bool neutral = AtlasToolbarLayerIndicator.IsNeutral(activeMapLayer);
        double center = size * 0.5;
        double radius = size * 0.34;
        context.Arc(center, center + Math.Max(1, scale * 0.6), radius, 0, Math.PI * 2);
        context.SetSourceRGBA(0.02, 0.03, 0.04, 0.38);
        context.Fill();
        context.Arc(center, center, radius, 0, Math.PI * 2);
        context.SetSourceRGBA(red, green, blue, neutral ? 0.58 : 0.98);
        context.FillPreserve();
        context.SetSourceRGBA(1, 1, 1, neutral ? 0.34 : 0.68);
        context.LineWidth = Math.Max(1, scale * 0.8);
        context.Stroke();

        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref layerIndicatorTexture);
        layerIndicatorTextureLayer = activeMapLayer;
    }

    /// <summary>
    /// Draws the dot in the space the Layers button gave up, so the toolbar
    /// keeps its footprint.
    /// </summary>
    private void RenderToolbarLayerIndicator()
    {
        if (interfaceHidden) return;
        if (!TryResolveToolbarLayerIndicatorPlacement(
            out float x,
            out float y,
            out float size
        ))
        {
            return;
        }
        capi.Render.Render2DTexture(
            layerIndicatorTexture!.TextureId,
            x,
            y,
            size,
            size,
            51,
            ColorUtil.WhiteArgbVec
        );
    }

    /// <summary>
    /// Where the dot goes: centered in the strip the Layers button gave up.
    /// Shared with the automated test, which checks that the dot exists and
    /// stays inside that strip instead of trusting that it was drawn.
    /// </summary>
    private bool TryResolveToolbarLayerIndicatorPlacement(
        out float x,
        out float y,
        out float size
    )
    {
        x = 0;
        y = 0;
        size = 0;
        GuiElementAtlasButton? layersButton = overlay?.GetAtlasButton("map-options-button");
        if (layersButton == null) return false;

        EnsureToolbarLayerIndicatorTexture();
        if (layerIndicatorTexture is not { TextureId: > 0 }) return false;

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        size = layerIndicatorTexture.Width;
        x = (float)(layersButton.Bounds.absX
            + layersButton.Bounds.OuterWidth
            + (LayerIndicatorReservedWidth * scale - size) * 0.5);
        y = (float)(layersButton.Bounds.absY
            + (layersButton.Bounds.OuterHeight - size) * 0.5);
        return true;
    }

    /// <summary>
    /// Invalidates the toolbar dot after the active layer changed.
    /// </summary>
    private void RefreshToolbarLayerIndicator()
    {
        if (layerIndicatorTextureLayer != activeMapLayer)
        {
            layerIndicatorTextureLayer = null;
        }
    }

    private void SyncToolbarControls()
    {
        if (overlay == null) return;
        RefreshToolbarLayerIndicator();

        bool settingsActive = SettingsHierarchyOpen;
        overlay.GetAtlasButton("settings-button")?.SetActive(settingsActive);
        overlay.GetAtlasButton("screenshot-options-button")?.SetActive(
            ScreenshotOptionsOpen
        );
        overlay.GetAtlasButton("map-options-button")?.SetActive(MapPanelOpen);
        overlay.GetAtlasButton("search-button")?.SetActive(SearchPanelOpen);
        overlay.GetAtlasButton("instrument-button")?.SetActive(InstrumentPanelOpen);
        overlay.GetAtlasButton("search-button")!.Enabled = SearchModeActive;
        overlay.GetAtlasButton("quick-screenshot-button")!.Enabled =
            !tileScreenshot.Busy;
    }

    private void UpdateToolbarTooltip(int mouseX, int mouseY)
    {
        toolbarTooltipText = "";
        toolbarTooltipLocalY = 0;
        if (interfaceHidden || overlay == null) return;

        (string Key, string Text)[] tooltips =
        {
            ("settings-button", "Settings"),
            ("quick-screenshot-button", "Quick screenshot"),
            ("screenshot-options-button", "Screenshot setup"),
            ("map-options-button", $"Map layers · {activeMapLayer.DisplayName()}"),
            ("search-button", "Search loaded data"),
            ("instrument-button", "Handheld instrument"),
            ("hide-ui-button", "Hide atlas UI"),
            ("exit-button", "Exit atlas")
        };
        foreach ((string key, string text) in tooltips)
        {
            GuiElementAtlasButton? button = overlay.GetAtlasButton(key);
            if (button == null || !button.Bounds.PointInside(mouseX, mouseY))
            {
                continue;
            }

            toolbarTooltipText = text;
            toolbarTooltipLocalY = mouseY - (config.RenderOnScroll ? AtlasViewport.Y : 0);
            return;
        }
    }

    private double ToolbarWidthGui(double guiWidth) => Math.Clamp(guiWidth * 0.12, 128, 148);

    private double GetBottomPanelHeightGui(
        AtlasPanelSection section,
        double guiWidth,
        double guiHeight
    )
    {
        bool compact = guiWidth < 460;
        double desired = section switch
        {
            AtlasPanelSection.Settings => 236,
            AtlasPanelSection.ScreenshotOptions => compact ? 286 : 214,
            AtlasPanelSection.ScreenshotFilterTuning => compact ? 320 : 262,
            AtlasPanelSection.MapOptions => MapOptionsPanelHeightGui(compact),
            AtlasPanelSection.Search => compact ? 168 : 148,
            AtlasPanelSection.Instrument => 118,
            AtlasPanelSection.Performance => 152,
            AtlasPanelSection.Creative => 132,
            AtlasPanelSection.VisualLab => 158,
            AtlasPanelSection.Unit => compact ? 200 : 190,
            _ => 0
        };
        // Keep the panel inside the viewport. Content that does not fit is
        // exposed through the panel's wheel-scroll range; a minimum taller
        // than the viewport would cover the map and place the footer outside
        // the visible window on compact displays.
        double availableHeight = Math.Max(1, guiHeight - 16);
        double visualBudget = Math.Max(1, guiHeight * 0.35);
        return Math.Max(
            1,
            Math.Min(availableHeight, Math.Min(desired, visualBudget))
        );
    }

    private ElementBounds GetBottomPanelRoot(
        AtlasPanelSection section,
        out AtlasPanelGeometry geometry
    )
    {
        double guiScale = Math.Max(0.5, RuntimeEnv.GUIScale);
        AtlasViewportBounds viewport = AtlasViewport;
        double contentX = config.RenderOnScroll ? viewport.X / guiScale : 0;
        double contentY = config.RenderOnScroll ? viewport.Y / guiScale : 0;
        double guiWidth = viewport.Width / guiScale;
        double guiHeight = viewport.Height / guiScale;
        double toolbarWidth = ToolbarWidthGui(guiWidth);
        double panelWidth = Math.Max(180, guiWidth - toolbarWidth - 30);
        panelWidth = Math.Min(panelWidth, Math.Max(180, guiWidth - 8));
        double panelHeight = GetBottomPanelHeightGui(section, guiWidth, guiHeight);
        double panelX = contentX + toolbarWidth + 18;
        double panelY = contentY + guiHeight - panelHeight - 8;
        geometry = new AtlasPanelGeometry(
            (int)Math.Round(panelX * guiScale),
            (int)Math.Round(panelY * guiScale),
            Math.Max(1, (int)Math.Round(panelWidth * guiScale)),
            Math.Max(1, (int)Math.Round(panelHeight * guiScale))
        );
        bottomPanelGeometry = geometry;
        hasBottomPanelGeometry = true;
        ElementBounds root = ElementBounds.Fixed(
            panelX,
            panelY,
            panelWidth,
            panelHeight
        );
        bottomPanelBounds = root;
        searchPanelBounds = section == AtlasPanelSection.Search ? root : null;
        mapLayerPanelBounds = section == AtlasPanelSection.MapOptions ? root : null;
        screenshotPanelBounds = section is AtlasPanelSection.ScreenshotOptions
            or AtlasPanelSection.ScreenshotFilterTuning
            ? root
            : null;
        return root;
    }

    private void SetBottomPanelAliases(AtlasPanelSection section)
    {
        settingsModal = section == AtlasPanelSection.Settings ? bottomPanel : null;
        performanceModal = section == AtlasPanelSection.Performance
            ? bottomPanel
            : null;
        creativeSettingsModal = section == AtlasPanelSection.Creative
            ? bottomPanel
            : null;
        visualLabModal = section == AtlasPanelSection.VisualLab
            ? bottomPanel
            : null;
        searchPanel = section == AtlasPanelSection.Search ? bottomPanel : null;
        mapLayerPanel = section == AtlasPanelSection.MapOptions ? bottomPanel : null;
        screenshotPanel = section == AtlasPanelSection.ScreenshotOptions
            ? bottomPanel
            : null;
        screenshotFilterTuningPanel =
            section == AtlasPanelSection.ScreenshotFilterTuning
                ? bottomPanel
                : null;
        unitPanel = section == AtlasPanelSection.Unit ? bottomPanel : null;
    }

    private GuiComposer? GetBottomPanelComposer(AtlasPanelSection section) =>
        bottomPanelSection == section || bottomPanelVisualSection == section
            ? bottomPanel
            : null;

    private AtlasPanelSection bottomPanelVisualSection => bottomPanel == null
        ? AtlasPanelSection.None
        : settingsModal != null
            ? AtlasPanelSection.Settings
            : performanceModal != null
                ? AtlasPanelSection.Performance
                : creativeSettingsModal != null
                    ? AtlasPanelSection.Creative
                    : visualLabModal != null
                        ? AtlasPanelSection.VisualLab
                        : searchPanel != null
                            ? AtlasPanelSection.Search
                            : mapLayerPanel != null
                                ? AtlasPanelSection.MapOptions
                                : screenshotFilterTuningPanel != null
                                    ? AtlasPanelSection.ScreenshotFilterTuning
                                    : screenshotPanel != null
                                    ? AtlasPanelSection.ScreenshotOptions
                                    : unitPanel != null
                                        ? AtlasPanelSection.Unit
                                        : AtlasPanelSection.None;

    private void DisposeBottomPanelComposer()
    {
        bottomPanel?.UnfocusOwnElements();
        bottomPanel?.Dispose();
        bottomPanel = null;
        SetBottomPanelAliases(AtlasPanelSection.None);
        bottomPanelSection = AtlasPanelSection.None;
        queuedBottomPanelSection = AtlasPanelSection.None;
        bottomPanelProgress = 0;
        bottomPanelAnimationActive = false;
        bottomPanelBounds = null;
        searchPanelBounds = null;
        mapLayerPanelBounds = null;
        screenshotPanelBounds = null;
        screenshotFilterTuningPanel = null;
        hasBottomPanelGeometry = false;
    }

    private void ResetBottomPanelState()
    {
        DisposeBottomPanelComposer();
        settingsModalOpen = false;
        performanceModalOpen = false;
        creativeSettingsModalOpen = false;
        visualLabModalOpen = false;
        toolbarTooltipText = "";
        toolbarTooltipLocalY = 0;
        settingsScrollOffset = 0;
        settingsSectionScrollOffset = 0;
    }

    private void SetLogicalBottomPanel(AtlasPanelSection section)
    {
        settingsModalOpen = section == AtlasPanelSection.Settings;
        performanceModalOpen = section == AtlasPanelSection.Performance;
        creativeSettingsModalOpen = section == AtlasPanelSection.Creative;
        visualLabModalOpen = section == AtlasPanelSection.VisualLab;
    }

    private GuiComposer CreateBottomPanelComposer(
        AtlasPanelSection section,
        ElementBounds root
    )
    {
        double width = root.fixedWidth;
        double height = root.fixedHeight;
        GuiComposer composer = capi.Gui.CreateCompo(
            "modernatlas-bottom-panel",
            root
        ).AddStaticCustomDraw(
            ElementBounds.Fixed(0, 0, width, height),
            AtlasUiStyle.DrawCard
        );

        switch (section)
        {
            case AtlasPanelSection.Settings:
                ComposeSettingsBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.ScreenshotOptions:
                ComposeScreenshotBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.ScreenshotFilterTuning:
                ComposeScreenshotFilterTuningPanel(composer, width, height);
                break;
            case AtlasPanelSection.MapOptions:
                ComposeMapBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.Search:
                ComposeSearchBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.Instrument:
                ComposeInstrumentBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.Performance:
                ComposePerformanceBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.Creative:
                ComposeCreativeBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.VisualLab:
                ComposeVisualLabBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.Unit:
                ComposeUnitBottomPanel(composer, width, height);
                break;
        }
        return composer.Compose(false);
    }

    private void ComposeBottomPanel(AtlasPanelSection section, bool animate)
    {
        DisposeBottomPanelComposer();
        ElementBounds root = GetBottomPanelRoot(section, out _);
        bottomPanelSection = section;
        bottomPanel = CreateBottomPanelComposer(section, root);
        SetBottomPanelAliases(section);
        SetLogicalBottomPanel(section);
        bottomPanelProgress = animate ? 0 : 1;
        bottomPanelAnimationStart = bottomPanelProgress;
        bottomPanelAnimationTarget = 1;
        bottomPanelAnimationStartedMilliseconds = capi.ElapsedMilliseconds;
        bottomPanelAnimationActive = animate;
        SyncSettingsControls();
        SyncPerformanceControls();
        SyncCreativeSettingsControls();
        SyncMapLayerControls();
        // The visual lab was missing here, so its sliders and the new
        // current/default read-outs only refreshed on a reset.
        SyncVisualLabControls();
        SyncScreenshotSettingsControls();
        SyncToolbarControls();
    }

    private void OpenBottomPanelImmediately(AtlasPanelSection section)
    {
        ComposeBottomPanel(section, false);
    }

    private void StartClosingBottomPanel(AtlasPanelSection nextSection)
    {
        queuedBottomPanelSection = nextSection;
        SetLogicalBottomPanel(AtlasPanelSection.None);
        bottomPanelAnimationStart = bottomPanelProgress;
        bottomPanelAnimationTarget = 0;
        bottomPanelAnimationStartedMilliseconds = capi.ElapsedMilliseconds;
        bottomPanelAnimationActive = true;
        SyncToolbarControls();
    }

    private void RequestBottomPanel(AtlasPanelSection section)
    {
        if (section == AtlasPanelSection.None)
        {
            if (bottomPanelSection != AtlasPanelSection.None)
            {
                StartClosingBottomPanel(AtlasPanelSection.None);
            }
            return;
        }
        if (section == AtlasPanelSection.Search && !SearchModeActive) return;
        if (section == AtlasPanelSection.Creative
            && !CreativeCheatSettingsAvailable)
        {
            return;
        }

        ResetPointerDrag();
        if (section != AtlasPanelSection.Unit) selectedEntityId = null;

        if (bottomPanelAnimationActive && BottomPanelClosing)
        {
            if (section == bottomPanelSection)
            {
                // A second click on the active toolbar shortcut reverses the
                // close animation instead of waiting for a stale queued open.
                queuedBottomPanelSection = AtlasPanelSection.None;
                SetLogicalBottomPanel(section);
                bottomPanelAnimationStart = bottomPanelProgress;
                bottomPanelAnimationTarget = 1;
                bottomPanelAnimationStartedMilliseconds = capi.ElapsedMilliseconds;
                bottomPanelAnimationActive = true;
                SyncToolbarControls();
            }
            else
            {
                queuedBottomPanelSection = section;
            }
            return;
        }

        if (bottomPanelSection == section && !BottomPanelClosing)
        {
            StartClosingBottomPanel(AtlasPanelSection.None);
            return;
        }

        if (bottomPanelSection != AtlasPanelSection.None)
        {
            StartClosingBottomPanel(section);
            return;
        }

        OpenBottomPanel(section);
    }

    private void OpenBottomPanel(AtlasPanelSection section)
    {
        OpenBottomPanelImmediately(section);
        bottomPanelProgress = 0;
        bottomPanelAnimationStart = 0;
        bottomPanelAnimationTarget = 1;
        bottomPanelAnimationStartedMilliseconds = capi.ElapsedMilliseconds;
        bottomPanelAnimationActive = true;
    }

    private void AdvanceBottomPanelAnimation()
    {
        if (!bottomPanelAnimationActive) return;

        float elapsed = Math.Max(
            0,
            capi.ElapsedMilliseconds - bottomPanelAnimationStartedMilliseconds
        );
        float progress = Math.Clamp(
            elapsed / BottomPanelAnimationMilliseconds,
            0,
            1
        );
        float eased = 1f - MathF.Pow(1f - progress, 3f);
        bottomPanelProgress = bottomPanelAnimationStart
            + (bottomPanelAnimationTarget - bottomPanelAnimationStart) * eased;
        if (progress < 1f) return;

        bottomPanelProgress = bottomPanelAnimationTarget;
        bottomPanelAnimationActive = false;
        if (bottomPanelProgress > 0.5f) return;

        AtlasPanelSection next = queuedBottomPanelSection;
        queuedBottomPanelSection = AtlasPanelSection.None;
        DisposeBottomPanelComposer();
        if (next != AtlasPanelSection.None)
        {
            OpenBottomPanel(next);
        }
        SyncToolbarControls();
    }

    private void RepositionActiveBottomPanel()
    {
        if (bottomPanel == null || bottomPanelSection == AtlasPanelSection.None)
        {
            return;
        }

        AtlasPanelSection section = bottomPanelSection;
        ElementBounds? existingRoot = bottomPanelBounds;
        ElementBounds newRoot = GetBottomPanelRoot(section, out _);
        if (existingRoot == null) return;
        existingRoot.fixedX = newRoot.fixedX;
        existingRoot.fixedY = newRoot.fixedY;
        existingRoot.fixedWidth = newRoot.fixedWidth;
        existingRoot.fixedHeight = newRoot.fixedHeight;
        bottomPanelBounds = existingRoot;
        searchPanelBounds = section == AtlasPanelSection.Search ? existingRoot : null;
        mapLayerPanelBounds = section == AtlasPanelSection.MapOptions ? existingRoot : null;
        screenshotPanelBounds = section is AtlasPanelSection.ScreenshotOptions
            or AtlasPanelSection.ScreenshotFilterTuning
            ? existingRoot
            : null;
        bottomPanel.ReCompose();
    }

    private bool PanelCoversPoint(int x, int y)
    {
        if (!hasBottomPanelGeometry || bottomPanel == null) return false;
        if (bottomPanelProgress <= 0.01f) return false;
        int visibleHeight = Math.Max(
            1,
            (int)Math.Round(bottomPanelGeometry.Height * bottomPanelProgress)
        );
        int visibleTop = bottomPanelGeometry.Bottom - visibleHeight;
        return x >= bottomPanelGeometry.X
            && x <= bottomPanelGeometry.X + bottomPanelGeometry.Width
            && y >= visibleTop
            && y <= bottomPanelGeometry.Bottom;
    }

    private void RenderActiveBottomPanel(float deltaTime)
    {
        if (bottomPanel == null || bottomPanelProgress <= 0.01f) return;
        if (bottomPanelSection == AtlasPanelSection.Unit)
        {
            UpdateUnitInspectionText();
        }

        int visibleHeight = Math.Max(
            1,
            (int)Math.Round(bottomPanelGeometry.Height * bottomPanelProgress)
        );
        int visibleTop = bottomPanelGeometry.Bottom - visibleHeight;
        IRenderAPI render = capi.Render;
        render.GlScissor(
            bottomPanelGeometry.X,
            Math.Max(0, render.FrameHeight - bottomPanelGeometry.Bottom),
            bottomPanelGeometry.Width,
            visibleHeight
        );
        render.GlScissorFlag(true);
        try
        {
            bottomPanel.Render(deltaTime);
            // Drawn after the panel so it sits above its controls, and inside
            // the same scissor so it cannot leak past the panel edge.
            if (bottomPanelSection == AtlasPanelSection.Search) RenderSearchLegend();
            if (bottomPanelSection == AtlasPanelSection.Unit) RenderUnitHealthBar();
        }
        finally
        {
            render.GlScissorFlag(false);
        }
    }

    private double ScreenshotFilterTuningScrollMaximum(
        double width,
        double height
    )
    {
        if (AtlasScreenshotFilterSettings.NormalizePreset(
                config.ScreenshotFilterPreset
            ) != "custom")
        {
            return 0;
        }
        bool compact = width < 460;
        double rowStep = compact ? 27 : 32;
        double sliderHeight = compact ? 22 : 24;
        int rowCount = compact ? 8 : 4;
        double controlsBottom = 45 + (rowCount - 1) * rowStep + sliderHeight;
        double noteHeight = compact ? 16 : 30;
        double noteStart = 45 + rowCount * rowStep + 4;
        double contentBottom = Math.Max(controlsBottom, noteStart + noteHeight);
        return Math.Max(0, contentBottom - Math.Max(1, height - 18));
    }

    private double ScreenshotOptionsScrollMaximum(
        double width,
        double height
    )
    {
        bool compact = width < 460;
        double contentBottom = compact ? 174 : 108;
        double footerTop = Math.Max(35, height - 39);
        return Math.Max(0, contentBottom - footerTop);
    }

    private bool ScreenshotOptionsControlsReachableAtHeight(
        double width,
        double height
    )
    {
        bool compact = width < 460;
        double bodyTop = 35;
        double footerTop = Math.Max(bodyTop, height - 39);
        double maximum = ScreenshotOptionsScrollMaximum(width, height);
        if (footerTop <= bodyTop || maximum <= 0) return false;

        double[] controlTops = compact
            ? new[] { 35d, 71d, 107d, 143d }
            : new[] { 36d, 36d, 76d, 76d };
        double[] controlHeights = compact
            ? new[] { 30d, 30d, 30d, 30d }
            : new[] { 30d, 30d, 30d, 30d };
        for (int index = 0; index < controlTops.Length; index++)
        {
            double offset = Math.Clamp(
                controlTops[index] + controlHeights[index] - footerTop - 1,
                0,
                maximum
            );
            double visibleTop = controlTops[index] - offset;
            double visibleBottom = visibleTop + controlHeights[index];
            if (visibleBottom <= bodyTop || visibleTop >= footerTop)
            {
                return false;
            }
        }
        return true;
    }

    private static void AddSettingSwitch(
        GuiComposer composer,
        string label,
        Action<bool> callback,
        string key,
        double x,
        double y,
        double columnWidth,
        double rowHeight,
        double labelWidth,
        double switchWidth
    )
    {
        composer
            .AddStaticText(label, AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(x, y + 4, labelWidth, rowHeight - 6))
            .AddAtlasSwitch(callback, ElementBounds.Fixed(x + columnWidth - switchWidth, y, switchWidth, rowHeight), key);
    }

    private void ComposeScreenshotBottomPanel(GuiComposer composer, double width, double height)
    {
        bool compact = width < 460;
        double contentOffset = Math.Clamp(
            settingsScrollOffset,
            0,
            ScreenshotOptionsScrollMaximum(width, height)
        );
        double bodyTop = 35;
        double footerTop = Math.Max(bodyTop, height - 39);
        composer
            .AddStaticText(
                "SCREENSHOT OPTIONS",
                AtlasUiStyle.TitleFont(15),
                ElementBounds.Fixed(14, 9, 260, 24)
            )
            .AddAtlasButton(
                "×",
                CloseScreenshotOptions,
                ElementBounds.Fixed(width - 42, 7, 32, 28),
                "shot-toggle",
                AtlasButtonStyle.Icon
            );

        GuiElementClipHelpler.BeginClip(
            composer,
            ElementBounds.Fixed(
                0,
                bodyTop,
                width,
                Math.Max(1, footerTop - bodyTop)
            )
        );
        if (compact)
        {
            composer
                .AddStaticText("Resolution", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, 42 - contentOffset, 62, 20))
                .AddAtlasChoice(
                    new[] { "1", "2", "3", "4", "5", "6", "7", "8" },
                    new[] { "1x", "2x", "3x", "4x", "5x", "6x", "7x", "8x" },
                    ScreenshotScaleIndex,
                    OnScreenshotScaleChanged,
                    ElementBounds.Fixed(80, 35 - contentOffset, Math.Max(86, width - 96), 30),
                    "shot-scale"
                )
                .AddStaticText("Capture area", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, 78 - contentOffset, 72, 20))
                .AddAtlasChoice(
                    new[] { "100", "75", "50", "25" },
                    new[] { "100%", "75%", "50%", "25%" },
                    ScreenshotCaptureAreaIndex,
                    OnScreenshotCaptureAreaChanged,
                    ElementBounds.Fixed(90, 71 - contentOffset, Math.Max(76, width - 106), 30),
                    "shot-area"
                )
                .AddAtlasButton(
                    "PREVIEW SCREENSHOT",
                    OpenScreenshotPreview,
                    ElementBounds.Fixed(16, 107 - contentOffset, Math.Max(100, width - 32), 30),
                    "shot-preview-open",
                    AtlasButtonStyle.Compact
                )
                .AddAtlasButton(
                    "TAKE SCREENSHOT",
                    TakeScreenshot,
                    ElementBounds.Fixed(16, 143 - contentOffset, Math.Max(100, width - 32), 30),
                    "shot-take",
                    AtlasButtonStyle.Compact
                );
        }
        else
        {
            composer
                .AddStaticText("Resolution", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, 43 - contentOffset, 66, 20))
                .AddAtlasChoice(
                    new[] { "1", "2", "3", "4", "5", "6", "7", "8" },
                    new[] { "1x", "2x", "3x", "4x", "5x", "6x", "7x", "8x" },
                    ScreenshotScaleIndex,
                    OnScreenshotScaleChanged,
                    ElementBounds.Fixed(82, 36 - contentOffset, 112, 30),
                    "shot-scale"
                )
                .AddStaticText("Capture area", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(204, 43 - contentOffset, 76, 20))
                .AddAtlasChoice(
                    new[] { "100", "75", "50", "25" },
                    new[] { "100%", "75%", "50%", "25%" },
                    ScreenshotCaptureAreaIndex,
                    OnScreenshotCaptureAreaChanged,
                    ElementBounds.Fixed(280, 36 - contentOffset, 112, 30),
                    "shot-area"
                )
                .AddAtlasButton(
                    "PREVIEW SCREENSHOT",
                    OpenScreenshotPreview,
                    ElementBounds.Fixed(16, 76 - contentOffset, Math.Max(100, (width - 48) * 0.5), 30),
                    "shot-preview-open",
                    AtlasButtonStyle.Compact
                )
                .AddAtlasButton(
                    "TAKE SCREENSHOT",
                    TakeScreenshot,
                    ElementBounds.Fixed(32 + Math.Max(100, (width - 48) * 0.5), 76 - contentOffset, Math.Max(100, (width - 48) * 0.5), 30),
                    "shot-take",
                    AtlasButtonStyle.Compact
                );
        }

        GuiElementClipHelpler.EndClip(composer);
        composer
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(8),
                ElementBounds.Fixed(16, height - 39, Math.Max(140, width - 32), 20),
                "shot-preview"
            )
            .AddDynamicText(
                BuildScreenshotFilterStatusText(),
                AtlasUiStyle.DetailFont(8),
                ElementBounds.Fixed(16, height - 17, Math.Max(140, width - 32), 14),
                "shot-status"
            );
    }

    private void ComposeScreenshotFilterTuningPanel(
        GuiComposer composer,
        double width,
        double height
    )
    {
        bool compact = width < 460;
        double contentOffset = Math.Clamp(
            settingsScrollOffset,
            0,
            ScreenshotFilterTuningScrollMaximum(width, height)
        );
        bool custom = AtlasScreenshotFilterSettings.NormalizePreset(
            config.ScreenshotFilterPreset
        ) == "custom";
        composer
            .AddStaticText(
                "FILTER TUNING",
                AtlasUiStyle.TitleFont(15),
                ElementBounds.Fixed(44, 9, 220, 24)
            )
            .AddAtlasButton(
                "‹",
                CloseScreenshotFilterTuning,
                ElementBounds.Fixed(10, 7, 32, 28),
                "shot-filter-back",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "Capture-only filter; live atlas unchanged.",
                AtlasUiStyle.DetailFont(8),
                ElementBounds.Fixed(44, 31, Math.Max(120, width - 60), 14)
            );

        if (!custom)
        {
            composer
                .AddStaticText(
                    "Preset values are active. Select Custom to tune the screenshot filter.",
                    AtlasUiStyle.DetailFont(9),
                    ElementBounds.Fixed(16, 48 - contentOffset, Math.Max(120, width - 32), 38)
                )
                .AddDynamicText(
                    "",
                    AtlasUiStyle.DetailFont(9),
                    ElementBounds.Fixed(16, height - 18, Math.Max(120, width - 32), 14),
                    "shot-filter-tuning-status"
                );
            return;
        }

        double columnWidth = compact
            ? Math.Max(90, width - 32)
            : Math.Max(120, (width - 42) * 0.5);
        double left = 16;
        double right = compact ? left : 22 + columnWidth;
        double labelWidth = Math.Min(84, columnWidth * 0.42);
        double sliderWidth = Math.Max(46, columnWidth - labelWidth - 4);
        double rowStep = compact ? 27 : 32;
        double sliderHeight = compact ? 22 : 24;
        (string Label, string Key, ActionConsumable<int> Callback, int Value, int Min, int Max)[] controls =
        {
            ("Intensity", "shot-filter-intensity", OnScreenshotFilterIntensityChanged, config.ScreenshotFilterIntensityPercent, 0, 200),
            ("Saturation", "shot-filter-saturation", OnScreenshotSaturationChanged, config.ScreenshotSaturationPercent, 0, 200),
            ("Contrast", "shot-filter-contrast", OnScreenshotContrastChanged, config.ScreenshotContrastPercent, 0, 200),
            ("Temperature", "shot-filter-temperature", OnScreenshotTemperatureChanged, config.ScreenshotTemperaturePercent, -100, 100),
            ("Shadow tint", "shot-filter-shadow-tint", OnScreenshotShadowTintChanged, config.ScreenshotShadowTintStrengthPercent, 0, 100),
            ("Depth relief", "shot-filter-ao", OnScreenshotAmbientOcclusionChanged, config.ScreenshotAmbientOcclusionPercent, 0, 100),
            ("Indirect light (screen-space)", "shot-filter-indirect", OnScreenshotIndirectLightChanged, config.ScreenshotIndirectLightPercent, 0, 100),
            ("Bloom", "shot-filter-bloom", OnScreenshotBloomChanged, config.ScreenshotBloomPercent, 0, 100)
        };
        for (int index = 0; index < controls.Length; index++)
        {
            int row = compact ? index : index / 2;
            bool rightColumn = !compact && index % 2 == 1;
            double x = rightColumn ? right : left;
            double y = 45 + row * rowStep - contentOffset;
            AddScreenshotFilterSlider(
                composer,
                controls[index].Label,
                controls[index].Key,
                controls[index].Callback,
                x,
                y,
                columnWidth,
                labelWidth,
                sliderWidth,
                sliderHeight
            );
        }
        double noteY = 45 + (compact ? controls.Length : 4) * rowStep
            + 4 - contentOffset;
        double noteHeight = compact ? 16 : 30;
        double footerTop = height - 18;
        if (noteY + noteHeight <= footerTop)
        {
            composer.AddStaticText(
                compact
                    ? "Spatial effects stay inside the disclosure mask; indirect light is screen-space only."
                    : "Spatial effects remain inside the disclosure validity mask; indirect light is a screen-space approximation, not true global illumination.",
                AtlasUiStyle.DetailFont(8),
                ElementBounds.Fixed(16, noteY, Math.Max(120, width - 32), noteHeight),
                "shot-filter-tuning-note"
            );
        }
        composer.AddDynamicText(
            ScreenshotFilterTuningScrollMaximum(width, height) > 0
                ? "Scroll for all tuning controls"
                : "",
            AtlasUiStyle.DetailFont(8),
            ElementBounds.Fixed(16, height - 18, Math.Max(120, width - 32), 14),
            "shot-filter-tuning-status"
        );
    }

    private static void AddScreenshotFilterSlider(
        GuiComposer composer,
        string label,
        string key,
        ActionConsumable<int> callback,
        double x,
        double y,
        double columnWidth,
        double labelWidth,
        double sliderWidth,
        double sliderHeight
    )
    {
        composer
            .AddStaticText(
                label,
                AtlasUiStyle.DetailFont(8),
                ElementBounds.Fixed(x, y + 2, labelWidth, sliderHeight - 2)
            )
            .AddAtlasSlider(
                callback,
                ElementBounds.Fixed(x + labelWidth, y, sliderWidth, sliderHeight),
                key
            );
    }

    private void ComposeMapBottomPanel(GuiComposer composer, double width, double height)
    {
        bool compact = width < 460;
        bool oreControls = activeMapLayer == AtlasMapLayer.OreDensity
            && CreativeCheatSettingsAvailable;
        double contentWidth = Math.Max(120, width - 32);
        double selectorX = compact ? 16 : 82;
        double selectorWidth = Math.Max(120, width - (compact ? 32 : 98));
        composer
            .AddStaticText("MAP OPTIONS", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(14, 9, 220, 24))
            .AddAtlasButton("×", CloseMapOptions, ElementBounds.Fixed(width - 42, 7, 32, 28), "map-layer-toggle", AtlasButtonStyle.Icon);
        if (!compact)
        {
            composer.AddStaticText(
                "Layer",
                AtlasUiStyle.DetailFont(10),
                ElementBounds.Fixed(16, 43, 58, 20)
            );
        }
        // The panel is anchored to the bottom edge, so a drop-down list opened
        // here fell outside the card and left Moisture, Temperature and Ore
        // density unreachable. The arrow selector keeps every option inside the
        // control's own bounds.
        GetMapLayerChoiceOptions(
            selectorWidth < 220,
            out string[] layerValues,
            out string[] layerNames,
            out int layerIndex
        );
        composer.AddAtlasChoice(
            layerValues,
            layerNames,
            layerIndex,
            OnMapLayerChanged,
            ElementBounds.Fixed(selectorX, compact ? 42 : 38, selectorWidth, 34),
            "map-layer"
        );

        double y = compact ? 84 : 80;
        if (oreControls)
        {
            double oreSelectorWidth = Math.Max(120, width - (compact ? 32 : 94));
            oreFilterLabelLimit = OreFilterLabelLimit(oreSelectorWidth);
            GetOreFilterOptions(
                oreFilterLabelLimit,
                out string[] values,
                out string[] names,
                out int selectedIndex
            );
            if (!compact)
            {
                composer.AddStaticText(
                    "Ore",
                    AtlasUiStyle.DetailFont(10),
                    ElementBounds.Fixed(16, y + 5, 64, 20)
                );
            }
            composer.AddAtlasChoice(
                values,
                names,
                selectedIndex,
                OnOreFilterChanged,
                ElementBounds.Fixed(compact ? 16 : 78, y, oreSelectorWidth, 34),
                "ore-filter"
            );
            y += 42;
        }

        if (activeMapLayer.HasLegendRamp())
        {
            // The overlay strength was configurable only through the config
            // file. The layer it applies to is chosen here, so the slider
            // belongs here too.
            composer
                .AddStaticText(
                    "Opacity",
                    AtlasUiStyle.DetailFont(10),
                    ElementBounds.Fixed(16, y + 6, 62, 20)
                )
                .AddAtlasSlider(
                    OnMapLayerOpacityChanged,
                    ElementBounds.Fixed(82, y, Math.Max(90, width - 98), 30),
                    "layer-opacity"
                );
            y += 38;

            AtlasMapLayer legendLayer = activeMapLayer;
            // The ore ramp changes meaning with its source, and the source is
            // only known once data has streamed in, so the stops are dynamic.
            (string low, string middle, string high) = MapLayerLegendStops();
            double stopWidth = Math.Max(40, contentWidth / 3);
            composer
                .AddStaticCustomDraw(
                    ElementBounds.Fixed(16, y, contentWidth, 14),
                    (context, surface, bounds) =>
                        DrawLayerLegendRamp(context, bounds, legendLayer)
                )
                .AddDynamicText(
                    low,
                    AtlasUiStyle.DetailFont(9),
                    ElementBounds.Fixed(16, y + 16, stopWidth, 15),
                    "legend-low"
                )
                .AddDynamicText(
                    middle,
                    AtlasUiStyle.DetailFont(9)
                        .WithOrientation(EnumTextOrientation.Center),
                    ElementBounds.Fixed(16 + stopWidth, y + 16, stopWidth, 15),
                    "legend-middle"
                )
                .AddDynamicText(
                    high,
                    AtlasUiStyle.DetailFont(9)
                        .WithOrientation(EnumTextOrientation.Right),
                    ElementBounds.Fixed(16 + stopWidth * 2, y + 16, stopWidth, 15),
                    "legend-high"
                );
            y += 33;

            if (legendLayer == AtlasMapLayer.OreDensity)
            {
                composer
                    .AddStaticCustomDraw(
                        ElementBounds.Fixed(16, y + 2, 13, 13),
                        DrawUnavailableLegendSwatch
                    )
                    .AddStaticText(
                        "Unavailable — no loaded data for this column",
                        AtlasUiStyle.DetailFont(9),
                        ElementBounds.Fixed(35, y, Math.Max(90, width - 51), 16)
                    );
                y += 20;
            }
        }

        composer
            .AddDynamicText("", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, y, contentWidth, 16), "layer-status")
            .AddDynamicText(MapLayerLegendCaption, AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, y + 16, contentWidth, 16), "layer-legend");
    }

    /// <summary>
    /// Height the Map options panel needs for the active layer. Ore adds its
    /// filter row and the unavailable swatch; textured terrain shows neither
    /// an opacity slider nor a legend ramp.
    /// </summary>
    private double MapOptionsPanelHeightGui(bool compact)
    {
        double height = compact ? 84 : 80;
        if (activeMapLayer == AtlasMapLayer.OreDensity
            && CreativeCheatSettingsAvailable)
        {
            height += 42;
        }
        if (activeMapLayer.HasLegendRamp())
        {
            height += 38 + 33;
            if (activeMapLayer == AtlasMapLayer.OreDensity) height += 20;
        }
        return height + 44;
    }

    /// <summary>
    /// The caption under the legend: what the layer measures and how coarse
    /// and how limited the underlying data is.
    /// </summary>
    private string MapLayerLegendCaption
    {
        get
        {
            if (!activeMapLayer.HasLegendRamp()) return activeMapLayer.Description();

            string grid = $"{AtlasMapLayerTexture.HorizontalSampleSize} × {AtlasMapLayerTexture.HorizontalSampleSize} block grid · loaded data only";
            if (activeMapLayer != AtlasMapLayer.OreDensity)
            {
                return $"{activeMapLayer.Description()} · {grid}";
            }
            if (selectedOreCode != null)
            {
                // Readable name first, asset code as the secondary detail.
                return $"Single ore: {searchController.GetEnglishOreName(selectedOreCode)} · {selectedOreCode} · {grid}";
            }
            // With no filter the cell is painted from the strongest reading
            // found there. The wording follows the source, because a regional
            // map carries a potential grade and a loaded column does not.
            string meaning = mapLayerTexture.OreLayerSource switch
            {
                AtlasOreLayerSource.RegionalPotential =>
                    "All ores: color shows the highest potential here, never a sum",
                AtlasOreLayerSource.LoadedColumns =>
                    "All ores: color shows the strongest ore seen here, never a sum",
                AtlasOreLayerSource.Mixed =>
                    "All ores: highest mapped potential where mapped, strongest observed ore elsewhere, never a sum",
                _ => "All ores: color shows the strongest single reading, never a sum"
            };
            return $"{meaning} · {grid}";
        }
    }

    /// <summary>
    /// Legend stop labels for the active layer, resolved against the ore
    /// layer's current data source.
    /// </summary>
    private (string Low, string Middle, string High) MapLayerLegendStops() =>
        activeMapLayer == AtlasMapLayer.OreDensity
            ? AtlasMapLayerInfo.OreLegendStops(mapLayerTexture.OreLayerSource)
            : activeMapLayer.LegendStops();

    private void ComposeSearchBottomPanel(GuiComposer composer, double width, double height)
    {
        composer
            .AddStaticText("SEARCH", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(14, 9, 130, 24))
            .AddAtlasButton("×", CloseSearchPanel, ElementBounds.Fixed(width - 42, 7, 32, 28), "search-close", AtlasButtonStyle.Icon)
            .AddAtlasTextInput(ElementBounds.Fixed(16, 38, Math.Max(120, width - 82), 34), OnSearchTextChanged, AtlasUiStyle.InputFont(12), "search-input")
            .AddAtlasButton("×", ClearSearch, ElementBounds.Fixed(width - 58, 38, 42, 34), "search-clear", AtlasButtonStyle.Icon)
            .AddDynamicText("", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, 78, Math.Max(120, width - 32), 16), "search-status");
        composer.GetTextInput("search-input")?.SetMaxLength(80);
        composer.GetTextInput("search-input")?.SetPlaceHolderText(
            $"Block, creature, player or item — {searchController.SearchLanguageSummary}"
        );
    }

    private void ComposeInstrumentBottomPanel(GuiComposer composer, double width, double height)
    {
        composer
            .AddStaticText("INSTRUMENT", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(14, 9, 160, 24))
            .AddAtlasButton("×", CloseInstrumentPanel, ElementBounds.Fixed(width - 42, 7, 32, 28), "instrument-close", AtlasButtonStyle.Icon)
            .AddStaticText("Handheld display", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(16, 48, 130, 20))
            .AddAtlasChoice(new[] { "off", "compass", "time" }, new[] { "Off", "Compass", "Time" }, HandheldInstrumentChoiceIndex, OnHandheldInstrumentChoiceChanged, ElementBounds.Fixed(154, 40, Math.Max(120, width - 170), 34), "handheld-instrument")
            .AddStaticText("Compass and Time share one physical wooden shell.", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, height - 26, Math.Max(120, width - 32), 18), "instrument-note");
    }

    private void ComposePerformanceBottomPanel(GuiComposer composer, double width, double height)
    {
        composer
            .AddStaticText("PERFORMANCE", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(44, 9, 180, 24))
            .AddAtlasButton("‹", ClosePerformanceModal, ElementBounds.Fixed(10, 7, 32, 28), "performance-back", AtlasButtonStyle.Icon)
            .AddStaticText("Atlas lighting", AtlasUiStyle.DetailFont(11), ElementBounds.Fixed(16, 45, width - 84, 22))
            .AddAtlasSwitch(OnPerformanceLightingToggled, ElementBounds.Fixed(width - 62, 40, 50, 28), "performance-lighting")
            .AddStaticText("Hide vegetation", AtlasUiStyle.DetailFont(11), ElementBounds.Fixed(16, 78, width - 84, 22))
            .AddAtlasSwitch(OnHideVegetationToggled, ElementBounds.Fixed(width - 62, 73, 50, 28), "hide-vegetation")
            .AddStaticText("Developer visual controls only affect the atlas framebuffer.", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, height - 25, Math.Max(120, width - 32), 18), "performance-note");
    }

    private void ComposeCreativeBottomPanel(GuiComposer composer, double width, double height)
    {
        composer
            .AddStaticText("CREATIVE / CHEAT", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(14, 9, 220, 24))
            .AddAtlasButton("×", CloseCreativeSettingsModal, ElementBounds.Fixed(width - 42, 7, 32, 28), "creative-settings-close", AtlasButtonStyle.Icon)
            .AddStaticText("Cave mode", AtlasUiStyle.DetailFont(11), ElementBounds.Fixed(16, 46, width - 84, 22))
            .AddAtlasSwitch(OnCaveModeToggled, ElementBounds.Fixed(width - 62, 41, 50, 28), "cave-mode")
            .AddStaticText("Lock camera angle", AtlasUiStyle.DetailFont(11), ElementBounds.Fixed(16, 79, width - 84, 22))
            .AddAtlasSwitch(OnCameraAngleLockToggled, ElementBounds.Fixed(width - 62, 74, 50, 28), "camera-angle-lock")
            .AddStaticText("Available only in Creative or accepted Cheat Mode.", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, height - 24, Math.Max(120, width - 32), 17), "creative-note");
    }

    private void ComposeVisualLabBottomPanel(GuiComposer composer, double width, double height)
    {
        double sliderWidth = Math.Max(110, width - 226);
        composer
            .AddStaticText("ATLAS VISUAL LAB", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(44, 9, 220, 24))
            .AddAtlasButton("‹", CloseVisualLab, ElementBounds.Fixed(10, 7, 32, 28), "visual-lab-back", AtlasButtonStyle.Icon)
            .AddStaticText("Exposure", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(16, 39, 190, 18))
            .AddAtlasSlider(OnAtlasExposureChanged, ElementBounds.Fixed(210, 38, sliderWidth, 32), "atlas-exposure")
            // The old "Cave mask" label named an implementation detail; this
            // says what the control actually does.
            .AddStaticText("Cave occlusion brightness", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(16, 76, 190, 18))
            .AddAtlasSlider(OnCaveMaskBrightnessChanged, ElementBounds.Fixed(210, 75, sliderWidth, 32), "cave-mask-brightness")
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(8.5f),
                ElementBounds.Fixed(16, 55, 190, 14),
                "visual-lab-exposure-default"
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(8.5f),
                ElementBounds.Fixed(16, 92, 190, 14),
                "visual-lab-cave-default"
            )
            .AddAtlasButton("Reset tuning", ResetVisualTuning, ElementBounds.Fixed(16, height - 37, Math.Max(120, width * 0.4), 28), "visual-lab-reset", AtlasButtonStyle.Compact)
            .AddStaticText(
                "Developer visual controls: they affect only the atlas framebuffer, never the ordinary world.",
                AtlasUiStyle.DetailFont(9),
                ElementBounds.Fixed(Math.Max(150, width * 0.4) + 26, height - 32, Math.Max(120, width * 0.6 - 42), 18),
                "visual-lab-note"
            );
    }

    private void ComposeUnitBottomPanel(GuiComposer composer, double width, double height)
    {
        double labelWidth = width < 460 ? 74 : 92;
        double rowsHeight = Math.Max(20, height - UnitRowsTop - 8);
        // Six rows need roughly this much; when the panel is capped shorter
        // than that, the rows continue in a second column instead of being
        // silently cut off.
        unitRowsSplit = rowsHeight < UnitRowCount * UnitRowHeight;
        int firstColumnRows = unitRowsSplit ? (UnitRowCount + 1) / 2 : UnitRowCount;
        double columnWidth = unitRowsSplit
            ? Math.Max(150, (width - 32 - 16) * 0.5)
            : Math.Max(120, width - 32);
        double valueX = 16 + labelWidth + 6;
        composer
            .AddStaticText("UNIT", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(14, 9, 160, 24))
            .AddAtlasButton("×", CloseUnitInspection, ElementBounds.Fixed(width - 42, 7, 32, 28), "unit-close", AtlasButtonStyle.Icon)
            .AddDynamicText("", AtlasUiStyle.LabelFont(12), ElementBounds.Fixed(16, 36, Math.Max(120, width - 32), 20), "unit-name")
            // The health bar itself is a texture drawn over the panel; the
            // rows below start under the space it reserves.
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(10),
                ElementBounds.Fixed(16, UnitRowsTop, labelWidth, firstColumnRows * UnitRowHeight),
                "unit-labels"
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(10),
                ElementBounds.Fixed(valueX, UnitRowsTop, Math.Max(60, columnWidth - labelWidth - 6), firstColumnRows * UnitRowHeight),
                "unit-details"
            );
        if (!unitRowsSplit) return;

        double secondX = 16 + columnWidth + 16;
        composer
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(10),
                ElementBounds.Fixed(secondX, UnitRowsTop, labelWidth, firstColumnRows * UnitRowHeight),
                "unit-labels-2"
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(10),
                ElementBounds.Fixed(secondX + labelWidth + 6, UnitRowsTop, Math.Max(60, columnWidth - labelWidth - 6), firstColumnRows * UnitRowHeight),
                "unit-details-2"
            );
    }

    private const double UnitHealthBarTop = 60;
    private const double UnitHealthBarHeight = 15;
    private const double UnitHealthBarMaximumWidth = 320;
    private const double UnitRowsTop = 84;
    private const double UnitRowHeight = 17;
    private const int UnitRowCount = 6;

    private void ComposeOverlay(bool viewportOnly = false)
    {
        ComposeCompactOverlay();
        return;

#if false
        ElementBounds root = ElementBounds.Fill;
        double guiScale = Math.Max(0.5, RuntimeEnv.GUIScale);
        AtlasViewportBounds viewport = AtlasViewport;
        double contentX = config.RenderOnScroll ? viewport.X / guiScale : 0;
        double contentY = config.RenderOnScroll ? viewport.Y / guiScale : 0;
        double guiWidth = viewport.Width / guiScale;
        double relativeActionX = Math.Max(18, guiWidth - 232);
        double actionX = contentX + relativeActionX;
        double compassY = contentY + 56;
        double hideUiY = contentY + (CreativeCheatSettingsAvailable ? 148 : 102);
        double screenshotY = hideUiY + 46;
        double headerWidth = Math.Min(
            430,
            Math.Max(1, relativeActionX - 20)
        );
        bool compactHeader = headerWidth < 390;
        bool narrowHeader = headerWidth < 230;
        double headerHeight = narrowHeader ? 92 : compactHeader ? 78 : 50;
        double titleY = narrowHeader ? 20 : 25;
        double versionX = narrowHeader ? 30 : 174;
        double versionY = narrowHeader ? 48 : 18;
        double statusX = compactHeader ? 30 : 234;
        double statusY = narrowHeader ? 68 : compactHeader ? 52 : 28;
        double statusWidth = Math.Max(
            54,
            headerWidth - statusX - 18
        );

        overlay = capi.Gui.CreateCompo("modernatlas-3d", root)
            .AddStaticCustomDraw(
                ElementBounds.Fixed(contentX + 12, contentY + 10, headerWidth, headerHeight),
                AtlasUiStyle.DrawCard
            )
            .AddStaticText(
                "MODERNATLAS",
                AtlasUiStyle.TitleFont(narrowHeader ? 16 : 20),
                ElementBounds.Fixed(
                    contentX + 30,
                    contentY + titleY,
                    Math.Max(94, headerWidth - 48),
                    30
                )
            )
            .AddStaticText(
                "v0.6.7",
                AtlasUiStyle.DetailFont(9),
                ElementBounds.Fixed(contentX + versionX, contentY + versionY, 52, 18)
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(
                    contentX + statusX,
                    contentY + statusY,
                    statusWidth,
                    24
                ),
                "status"
            )
            .AddAtlasButton(
                "SETTINGS",
                ToggleSettingsModal,
                ElementBounds.Fixed(actionX, contentY + 10, 128, 44),
                "settings-button"
            )
            .AddAtlasButton(
                "EXIT",
                CloseAtlas,
                ElementBounds.Fixed(actionX + 136, contentY + 10, 78, 44),
                "exit-button"
            )
            .AddAtlasButton(
                "INSTRUMENT",
                ToggleCompass,
                ElementBounds.Fixed(actionX, compassY, 214, 44),
                "compass-button",
                AtlasButtonStyle.Dark
            )
            .AddAtlasButton(
                "HIDE UI",
                HideInterface,
                ElementBounds.Fixed(actionX, hideUiY, 214, 44),
                "hide-ui-button",
                AtlasButtonStyle.Dark
            )
            .AddAtlasButton(
                "SCREENSHOT",
                TakeScreenshot,
                ElementBounds.Fixed(actionX, screenshotY, 214, 44),
                "screenshot-button",
                AtlasButtonStyle.Dark
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(9),
                ElementBounds.Fixed(actionX, screenshotY + 50, 214, 60),
                "screenshot-status"
            )
            .Compose(false);

        creativeSettingsShortcut = capi.Gui.CreateCompo(
                "modernatlas-creative-settings-shortcut",
                ElementBounds.Fill
            )
            .AddAtlasButton(
                "CREATIVE / CHEAT SETTINGS",
                OpenCreativeSettingsModal,
                ElementBounds.Fixed(actionX, contentY + 102, 214, 44),
                "creative-settings-button"
            )
            .Compose(false);

        ElementBounds searchRoot = ElementBounds.Fixed(
            contentX + 18,
            contentY + 112,
            560,
            82
        );
        searchPanelBounds = searchRoot;
        searchPanel = capi.Gui.CreateCompo("modernatlas-search", searchRoot)
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 560, 82), AtlasUiStyle.DrawCard)
            .AddStaticText(
                "SEARCH",
                AtlasUiStyle.LabelFont(12),
                ElementBounds.Fixed(18, 17, 124, 24)
            )
            .AddAtlasTextInput(
                ElementBounds.Fixed(142, 10, 328, 40),
                OnSearchTextChanged,
                AtlasUiStyle.InputFont(13),
                "search-input"
            )
            .AddAtlasButton(
                "×",
                ClearSearch,
                ElementBounds.Fixed(478, 8, 64, 44),
                "search-clear",
                AtlasButtonStyle.Icon
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(18, 55, 524, 20),
                "search-status"
            )
            .Compose(false);
        searchPanel.GetTextInput("search-input")?.SetMaxLength(80);
        searchPanel.GetTextInput("search-input")?.SetPlaceHolderText(
            $"Block, creature, player or item — {searchController.SearchLanguageSummary}"
        );

        ComposeMapLayerPanel(
            contentX,
            contentY + (SearchModeActive ? 202 : headerHeight + 22),
            Math.Min(560, Math.Max(200, relativeActionX - 36))
        );

        // The screenshot panel stacks in the left column below the map-layer
        // panel (or the search panel / header when those are hidden).
        double screenshotPanelY = contentY + headerHeight + 30;
        if (MapLayerControlsVisible && mapLayerPanelBounds != null)
        {
            screenshotPanelY = mapLayerPanelBounds.fixedY
                + mapLayerPanelBounds.fixedHeight + 8;
        }
        else if (SearchModeActive && searchPanelBounds != null)
        {
            screenshotPanelY = searchPanelBounds.fixedY
                + searchPanelBounds.fixedHeight + 8;
        }
        ComposeScreenshotPanel(contentX, screenshotPanelY);

        ElementBounds unitRoot = config.RenderOnScroll
            ? ElementBounds.Fixed(
                contentX + guiWidth - 394,
                contentY + Math.Max(150, viewport.Height / guiScale * 0.5 - 129),
                370,
                258
            )
            : ElementBounds.Fixed(0, 0, 370, 258)
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedOffset(-24, 0);
        unitPanel = capi.Gui.CreateCompo("modernatlas-unit-inspection", unitRoot)
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 370, 258), AtlasUiStyle.DrawCard)
            .AddDynamicText(
                "",
                AtlasUiStyle.TitleFont(18),
                ElementBounds.Fixed(24, 22, 260, 32),
                "unit-name"
            )
            .AddAtlasButton(
                "×",
                CloseUnitInspection,
                ElementBounds.Fixed(310, 12, 48, 44),
                "unit-close",
                AtlasButtonStyle.Icon
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(22, 62, 326, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(24, 76, 322, 160),
                "unit-details"
            )
            .Compose();

        if (viewportOnly) return;

        ElementBounds modalRoot = ElementBounds.Fixed(0, 0, 430, 800)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        settingsModal = capi.Gui.CreateCompo("modernatlas-settings", modalRoot)
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 430, 800), AtlasUiStyle.DrawCard)
            .AddStaticText(
                "SETTINGS",
                AtlasUiStyle.TitleFont(20),
                ElementBounds.Fixed(26, 24, 260, 30)
            )
            .AddAtlasButton(
                "×",
                CloseSettingsModal,
                ElementBounds.Fixed(366, 12, 50, 46),
                "settings-close",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "DISPLAY",
                AtlasUiStyle.LabelFont(11),
                ElementBounds.Fixed(26, 66, 180, 22)
            )
            .AddStaticText(
                "Map layer",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 94, 240, 26)
            )
            .AddAtlasSwitch(
                OnMapLayersToggled,
                ElementBounds.Fixed(350, 88, 62, 38),
                "map-layers"
            )
            .AddStaticText(
                "Search loaded map (Creative/Cheat)",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 130, 310, 26)
            )
            .AddAtlasSwitch(
                OnSearchModeToggled,
                ElementBounds.Fixed(350, 124, 62, 38),
                "search-mode"
            )
            .AddStaticText(
                "Atlas animations",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 166, 240, 26)
            )
            .AddAtlasSwitch(
                OnAnimationsToggled,
                ElementBounds.Fixed(350, 160, 62, 38),
                "animations"
            )
            .AddStaticText(
                "Performance",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 202, 170, 26)
            )
            .AddAtlasButton(
                "OPEN",
                OpenPerformanceModal,
                ElementBounds.Fixed(198, 194, 214, 40),
                "performance-open",
                AtlasButtonStyle.Dark
            )
            .AddStaticText(
                "Skip scroll transitions",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 238, 260, 26)
            )
            .AddAtlasSwitch(
                OnSkipOpeningAnimationToggled,
                ElementBounds.Fixed(350, 232, 62, 38),
                "skip-opening-animation"
            )
            .AddStaticText(
                "Live 3D clouds",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 274, 240, 26)
            )
            .AddAtlasSwitch(
                OnCloudsToggled,
                ElementBounds.Fixed(350, 268, 62, 38),
                "clouds"
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 311, 378, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "LIVING MODELS",
                AtlasUiStyle.LabelFont(11),
                ElementBounds.Fixed(26, 324, 200, 22)
            )
            .AddStaticText(
                "Living entities",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 352, 240, 26)
            )
            .AddAtlasSwitch(
                OnLivingEntitiesToggled,
                ElementBounds.Fixed(350, 346, 62, 38),
                "entities"
            )
            .AddStaticText(
                "Players",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 388, 200, 24)
            )
            .AddAtlasSwitch(
                OnPlayersToggled,
                ElementBounds.Fixed(350, 382, 62, 38),
                "players"
            )
            .AddStaticText(
                "Animals",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 422, 200, 24)
            )
            .AddAtlasSwitch(
                OnAnimalsToggled,
                ElementBounds.Fixed(350, 416, 62, 38),
                "animals"
            )
            .AddStaticText(
                "Hostile mobs",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 456, 200, 24)
            )
            .AddAtlasSwitch(
                OnMobsToggled,
                ElementBounds.Fixed(350, 450, 62, 38),
                "mobs"
            )
            .AddStaticText(
                "NPCs",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 490, 200, 24)
            )
            .AddAtlasSwitch(
                OnNpcsToggled,
                ElementBounds.Fixed(350, 484, 62, 38),
                "npcs"
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 526, 378, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "GAMEPLAY • SCROLL",
                AtlasUiStyle.LabelFont(11),
                ElementBounds.Fixed(26, 538, 220, 22)
            )
            .AddStaticText(
                "Render on 3D scroll",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 564, 250, 24)
            )
            .AddAtlasSwitch(
                OnRenderOnScrollToggled,
                ElementBounds.Fixed(350, 555, 62, 38),
                "render-on-scroll"
            )
            .AddStaticText(
                "Realtime Weather on Scroll",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 598, 270, 24)
            )
            .AddAtlasSwitch(
                OnScrollRealtimeWeatherToggled,
                ElementBounds.Fixed(350, 589, 62, 38),
                "scroll-realtime-weather"
            )
            .AddStaticText(
                "Handheld instrument",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 632, 250, 24)
            )
            .AddAtlasChoice(
                new[] { "off", "compass", "time" },
                new[] { "Off", "Compass", "Time" },
                HandheldInstrumentChoiceIndex,
                OnHandheldInstrumentChoiceChanged,
                ElementBounds.Fixed(212, 622, 200, 42),
                "handheld-instrument"
            )
            .AddStaticText(
                "Live world sun direction",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 672, 260, 24)
            )
            .AddAtlasSwitch(
                OnLiveLightingToggled,
                ElementBounds.Fixed(350, 663, 62, 38),
                "live-lighting"
            )
            .AddStaticText(
                "Fixed hour (0–24)",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 709, 180, 24)
            )
            .AddAtlasSlider(
                OnFixedSunHourChanged,
                ElementBounds.Fixed(212, 700, 200, 42),
                "fixed-sun-hour"
            )
            .AddAtlasButton(
                "ATLAS VISUAL LAB",
                OpenVisualLab,
                ElementBounds.Fixed(24, 748, 382, 44),
                "visual-lab-open",
                AtlasButtonStyle.Dark
            )
            .Compose();
        settingsModal.GetAtlasSlider("fixed-sun-hour")?.SetValues(
            Math.Clamp(config.FixedSunHour, 0, 24),
            0,
            24,
            1,
            "h"
        );

        ElementBounds performanceRoot = ElementBounds.Fixed(0, 0, 460, 330)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        performanceModal = capi.Gui.CreateCompo(
                "modernatlas-performance",
                performanceRoot
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(0, 0, 460, 330),
                AtlasUiStyle.DrawCard
            )
            .AddStaticText(
                "PERFORMANCE",
                AtlasUiStyle.TitleFont(20),
                ElementBounds.Fixed(72, 24, 300, 30)
            )
            .AddAtlasButton(
                "‹",
                ClosePerformanceModal,
                ElementBounds.Fixed(14, 12, 50, 46),
                "performance-back",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "Reduce atlas GPU work on slower computers.",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(72, 59, 360, 24)
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 88, 408, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "Atlas lighting",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 108, 260, 26)
            )
            .AddAtlasSwitch(
                OnPerformanceLightingToggled,
                ElementBounds.Fixed(370, 101, 64, 40),
                "performance-lighting"
            )
            .AddStaticText(
                "Off uses neutral flat daylight.",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(28, 140, 350, 22)
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 174, 408, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "Hide vegetation",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 198, 260, 26)
            )
            .AddAtlasSwitch(
                OnHideVegetationToggled,
                ElementBounds.Fixed(370, 191, 64, 40),
                "hide-vegetation"
            )
            .AddStaticText(
                "Hides registered plants, bushes and leaves, including mods.",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(28, 233, 404, 42)
            )
            .AddStaticText(
                "Atlas refresh: 60 FPS focused • 12 FPS background.",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(28, 284, 404, 20)
            )
            .Compose(false);

        ElementBounds creativeRoot = ElementBounds.Fixed(0, 0, 440, 294)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        creativeSettingsModal = capi.Gui.CreateCompo(
                "modernatlas-creative-settings",
                creativeRoot
            )
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 440, 294), AtlasUiStyle.DrawCard)
            .AddStaticText(
                "CREATIVE / CHEAT",
                AtlasUiStyle.TitleFont(20),
                ElementBounds.Fixed(26, 24, 300, 30)
            )
            .AddAtlasButton(
                "×",
                CloseCreativeSettingsModal,
                ElementBounds.Fixed(376, 12, 50, 46),
                "creative-settings-close",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "Creative or server-authorized Cheat Mode only.",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(26, 59, 382, 24)
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 88, 388, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "Cave mode (show underground)",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 108, 300, 26)
            )
            .AddAtlasSwitch(
                OnCaveModeToggled,
                ElementBounds.Fixed(358, 101, 64, 40),
                "cave-mode"
            )
            .AddStaticText(
                "Lock camera angle",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 156, 300, 26)
            )
            .AddAtlasSwitch(
                OnCameraAngleLockToggled,
                ElementBounds.Fixed(358, 149, 64, 40),
                "camera-angle-lock"
            )
            .AddStaticText(
                "Rotation remains free while the current tilt is held.",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(28, 202, 382, 22)
            )
            .Compose(false);

        ElementBounds labRoot = ElementBounds.Fixed(0, 0, 470, 310)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        visualLabModal = capi.Gui.CreateCompo("modernatlas-visual-lab", labRoot)
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 470, 310), AtlasUiStyle.DrawCard)
            .AddStaticText(
                "ATLAS VISUAL LAB",
                AtlasUiStyle.TitleFont(20),
                ElementBounds.Fixed(72, 24, 300, 30)
            )
            .AddAtlasButton(
                "‹",
                CloseVisualLab,
                ElementBounds.Fixed(14, 12, 50, 46),
                "visual-lab-back",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "Atlas-only controls; the normal world is never changed.",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(72, 59, 370, 24)
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 88, 418, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "Exposure",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 108, 170, 26)
            )
            .AddAtlasSlider(
                OnAtlasExposureChanged,
                ElementBounds.Fixed(202, 99, 240, 44),
                "atlas-exposure"
            )
            .AddStaticText(
                "Cave mask brightness",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 160, 174, 26)
            )
            .AddAtlasSlider(
                OnCaveMaskBrightnessChanged,
                ElementBounds.Fixed(202, 151, 240, 44),
                "cave-mask-brightness"
            )
            .AddAtlasButton(
                "RESET VISUAL TUNING",
                ResetVisualTuning,
                ElementBounds.Fixed(26, 230, 418, 48),
                "visual-lab-reset",
                AtlasButtonStyle.Dark
            )
            .Compose();
        ConfigureVisualLabSliders();

        SyncSettingsControls();
        SyncPerformanceControls();
        SyncCreativeSettingsControls();
    }
#endif
    }

    private bool OpenSettingsModal()
    {
        // Opening Settings from the toolbar starts at the top; only a return
        // from a sub-panel restores the remembered position.
        settingsScrollOffset = 0;
        settingsSectionScrollOffset = 0;
        RequestBottomPanel(AtlasPanelSection.Settings);
        return true;
    }

    private bool ToggleSettingsModal() => SettingsHierarchyOpen
        ? CloseSettingsModal()
        : OpenSettingsModal();

    private bool IsSettingsButtonPosition(double x, double y) =>
        overlay?.GetAtlasButton("settings-button")?.Bounds.PointInside(x, y) == true;

    private bool CloseSettingsModal()
    {
        RequestBottomPanel(AtlasPanelSection.None);
        return true;
    }

    private bool OpenCreativeSettingsModal()
    {
        // Sub-panels share one scroll offset with Settings. Remember where the
        // reader was so returning does not throw them back to the top.
        settingsSectionScrollOffset = settingsScrollOffset;
        RequestBottomPanel(AtlasPanelSection.Creative);
        return true;
    }

    private bool CloseCreativeSettingsModal()
    {
        settingsScrollOffset = settingsSectionScrollOffset;
        RequestBottomPanel(AtlasPanelSection.Settings);
        return true;
    }

    private bool OpenPerformanceModal()
    {
        // Sub-panels share one scroll offset with Settings. Remember where the
        // reader was so returning does not throw them back to the top.
        settingsSectionScrollOffset = settingsScrollOffset;
        RequestBottomPanel(AtlasPanelSection.Performance);
        return true;
    }

    private bool ClosePerformanceModal()
    {
        settingsScrollOffset = settingsSectionScrollOffset;
        RequestBottomPanel(AtlasPanelSection.Settings);
        return true;
    }

    private bool OpenVisualLab()
    {
        // Sub-panels share one scroll offset with Settings. Remember where the
        // reader was so returning does not throw them back to the top.
        settingsSectionScrollOffset = settingsScrollOffset;
        RequestBottomPanel(AtlasPanelSection.VisualLab);
        return true;
    }

    private bool CloseVisualLab()
    {
        settingsScrollOffset = settingsSectionScrollOffset;
        RequestBottomPanel(AtlasPanelSection.Settings);
        return true;
    }

    private bool ToggleMapOptions()
    {
        RequestBottomPanel(AtlasPanelSection.MapOptions);
        return true;
    }

    private bool CloseMapOptions()
    {
        if (MapPanelOpen) RequestBottomPanel(AtlasPanelSection.None);
        return true;
    }

    private bool ToggleSearchPanel()
    {
        if (!SearchModeActive)
        {
            OpenSettingsModal();
            return true;
        }
        RequestBottomPanel(AtlasPanelSection.Search);
        return true;
    }

    private bool CloseSearchPanel()
    {
        if (SearchPanelOpen) RequestBottomPanel(AtlasPanelSection.None);
        return true;
    }

    private bool ToggleInstrumentPanel()
    {
        RequestBottomPanel(AtlasPanelSection.Instrument);
        return true;
    }

    private bool CloseInstrumentPanel()
    {
        if (InstrumentPanelOpen) RequestBottomPanel(AtlasPanelSection.None);
        return true;
    }

}
