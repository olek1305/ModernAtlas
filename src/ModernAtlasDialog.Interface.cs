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
        SyncMapLayerDropdown();
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
        SyncMapLayerDropdown();
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
        SyncMapLayerDropdown();
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
                "SF",
                TakeQuickScreenshot,
                ElementBounds.Fixed(buttonX, pairY, pairWidth, rowHeight),
                "quick-screenshot-button",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "SP",
                ToggleScreenshotOptions,
                ElementBounds.Fixed(buttonX + pairWidth + 4, pairY, pairWidth, rowHeight),
                "screenshot-options-button",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "Map",
                ToggleMapOptions,
                ElementBounds.Fixed(buttonX, mapY, buttonWidth, rowHeight),
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

    private void SyncToolbarControls()
    {
        if (overlay == null) return;

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
            ("screenshot-options-button", "Screenshot options"),
            ("map-options-button", "Map options"),
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
            AtlasPanelSection.MapOptions => 158,
            AtlasPanelSection.Search => 124,
            AtlasPanelSection.Instrument => 118,
            AtlasPanelSection.Performance => 152,
            AtlasPanelSection.Creative => 132,
            AtlasPanelSection.VisualLab => 158,
            AtlasPanelSection.Unit => 188,
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
        SyncMapLayerDropdown();
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
        }
        finally
        {
            render.GlScissorFlag(false);
        }
    }

    private void ComposeSettingsBottomPanel(
        GuiComposer composer,
        double width,
        double height
    )
    {
        double row = height < 180 ? 22 : 28;
        double left = 14;
        double top = 38;
        double column = Math.Max(132, (width - 42) / 3);
        double switchWidth = Math.Min(50, Math.Max(42, column * 0.22));
        double labelWidth = Math.Max(70, column - switchWidth - 6);
        double contentOffset = Math.Clamp(settingsScrollOffset, 0, SettingsScrollMaximum(width, height));
        composer
            .AddStaticText(
                "SETTINGS",
                AtlasUiStyle.TitleFont(15),
                ElementBounds.Fixed(left, 9, 190, 24)
            )
            .AddAtlasButton(
                "×",
                CloseSettingsModal,
                ElementBounds.Fixed(width - 42, 7, 32, 28),
                "settings-close",
                AtlasButtonStyle.Icon
            );

        AddSettingSwitch(composer, "Map layers", OnMapLayersToggled, "map-layers", left, top - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Search loaded data", OnSearchModeToggled, "search-mode", left, top + row - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Animations", OnAnimationsToggled, "animations", left, top + row * 2 - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Skip transitions", OnSkipOpeningAnimationToggled, "skip-opening-animation", left, top + row * 3 - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Live clouds", OnCloudsToggled, "clouds", left, top + row * 4 - contentOffset, column, row, labelWidth, switchWidth);

        double middle = left + column + 8;
        AddSettingSwitch(composer, "3D scroll", OnRenderOnScrollToggled, "render-on-scroll", middle, top - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Scroll weather", OnScrollRealtimeWeatherToggled, "scroll-realtime-weather", middle, top + row - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Live sun", OnLiveLightingToggled, "live-lighting", middle, top + row * 2 - contentOffset, column, row, labelWidth, switchWidth);
        composer
            .AddStaticText("Fixed sun hour", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(middle, top + row * 3 + 3 - contentOffset, labelWidth, row - 4))
            .AddAtlasSlider(OnFixedSunHourChanged, ElementBounds.Fixed(middle + labelWidth - 4, top + row * 3 - contentOffset, column - labelWidth + 4, row), "fixed-sun-hour");
        composer
            .AddStaticText("Advanced", AtlasUiStyle.LabelFont(10), ElementBounds.Fixed(middle, top + row * 4 - contentOffset, 90, row))
            .AddAtlasButton("Performance", OpenPerformanceModal, ElementBounds.Fixed(middle + 72, top + row * 4 - contentOffset, Math.Max(70, column - 76), row), "performance-open", AtlasButtonStyle.Compact);
        composer.AddAtlasButton("Visual lab", OpenVisualLab, ElementBounds.Fixed(middle, top + row * 5 - contentOffset, column, row), "visual-lab-open", AtlasButtonStyle.Compact);

        double right = middle + column + 8;
        AddSettingSwitch(composer, "Living models", OnLivingEntitiesToggled, "entities", right, top - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Players", OnPlayersToggled, "players", right, top + row - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Animals", OnAnimalsToggled, "animals", right, top + row * 2 - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Hostile mobs", OnMobsToggled, "mobs", right, top + row * 3 - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "NPCs", OnNpcsToggled, "npcs", right, top + row * 4 - contentOffset, column, row, labelWidth, switchWidth);
        if (CreativeCheatSettingsAvailable)
        {
            composer.AddAtlasButton("Creative / Cheat", OpenCreativeSettingsModal, ElementBounds.Fixed(right, top + row * 5 - contentOffset, column, row), "creative-settings-button", AtlasButtonStyle.Compact);
        }
        composer.AddDynamicText(
            SettingsScrollMaximum(width, height) > 0 ? "Scroll panel for more controls" : "",
            AtlasUiStyle.DetailFont(9),
            ElementBounds.Fixed(left, height - 19, Math.Max(120, width - 60), 14),
            "settings-scroll-status"
        );
    }

    private double SettingsScrollMaximum(double width, double height) => height < 180 ? 74 : 0;

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
        double selectorX = compact ? 16 : 82;
        double selectorY = compact ? 42 : 38;
        double selectorWidth = Math.Max(120, width - (compact ? 32 : 98));
        double statusY = compact ? 82 : 78;
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
        composer
            .AddDropDown(AtlasMapLayerInfo.Values, AtlasMapLayerInfo.Names, (int)activeMapLayer, OnMapLayerChanged, ElementBounds.Fixed(selectorX, selectorY, selectorWidth, 34), "map-layer")
            .AddDynamicText("", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, statusY, Math.Max(120, width - 32), 16), "layer-status")
            .AddDynamicText(activeMapLayer.DetailedLegend(), AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, statusY + 16, Math.Max(120, width - 32), 16), "layer-legend");
        if (activeMapLayer == AtlasMapLayer.OreDensity && CreativeCheatSettingsAvailable)
        {
            GetOreFilterOptions(out string[] values, out string[] names, out int selectedIndex);
            composer
                .AddStaticText("Ore", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(16, compact ? 137 : 114, 64, 20))
                .AddDropDown(values, names, selectedIndex, OnOreFilterChanged, ElementBounds.Fixed(78, compact ? 132 : 109, Math.Max(120, width - 94), 34), "ore-filter");
        }
    }

    private void ComposeSearchBottomPanel(GuiComposer composer, double width, double height)
    {
        composer
            .AddStaticText("SEARCH", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(14, 9, 130, 24))
            .AddAtlasButton("×", CloseSearchPanel, ElementBounds.Fixed(width - 42, 7, 32, 28), "search-close", AtlasButtonStyle.Icon)
            .AddAtlasTextInput(ElementBounds.Fixed(16, 38, Math.Max(120, width - 82), 34), OnSearchTextChanged, AtlasUiStyle.InputFont(12), "search-input")
            .AddAtlasButton("×", ClearSearch, ElementBounds.Fixed(width - 58, 38, 42, 34), "search-clear", AtlasButtonStyle.Icon)
            .AddDynamicText("", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, height - 30, Math.Max(120, width - 32), 22), "search-status");
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
        composer
            .AddStaticText("ATLAS VISUAL LAB", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(44, 9, 220, 24))
            .AddAtlasButton("‹", CloseVisualLab, ElementBounds.Fixed(10, 7, 32, 28), "visual-lab-back", AtlasButtonStyle.Icon)
            .AddStaticText("Exposure", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(16, 45, 100, 20))
            .AddAtlasSlider(OnAtlasExposureChanged, ElementBounds.Fixed(118, 38, Math.Max(120, width - 134), 32), "atlas-exposure")
            .AddStaticText("Cave mask", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(16, 82, 100, 20))
            .AddAtlasSlider(OnCaveMaskBrightnessChanged, ElementBounds.Fixed(118, 75, Math.Max(120, width - 134), 32), "cave-mask-brightness")
            .AddAtlasButton("Reset tuning", ResetVisualTuning, ElementBounds.Fixed(16, height - 35, Math.Max(120, width - 32), 28), "visual-lab-reset", AtlasButtonStyle.Compact);
        ConfigureVisualLabSliders();
    }

    private void ComposeUnitBottomPanel(GuiComposer composer, double width, double height)
    {
        composer
            .AddStaticText("UNIT", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(14, 9, 160, 24))
            .AddAtlasButton("×", CloseUnitInspection, ElementBounds.Fixed(width - 42, 7, 32, 28), "unit-close", AtlasButtonStyle.Icon)
            .AddDynamicText("", AtlasUiStyle.LabelFont(12), ElementBounds.Fixed(16, 42, Math.Max(120, width - 32), 20), "unit-name")
            .AddDynamicText("", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(16, 68, Math.Max(120, width - 32), Math.Max(28, height - 82)), "unit-details");
    }

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
                "Sun hour (fixed)",
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
            Math.Clamp(config.FixedSunHour, 0, 23),
            0,
            23,
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
                "Atlas refresh: 60 FPS moving • 12 FPS idle.",
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
        RequestBottomPanel(AtlasPanelSection.Creative);
        return true;
    }

    private bool CloseCreativeSettingsModal()
    {
        RequestBottomPanel(AtlasPanelSection.Settings);
        return true;
    }

    private bool OpenPerformanceModal()
    {
        RequestBottomPanel(AtlasPanelSection.Performance);
        return true;
    }

    private bool ClosePerformanceModal()
    {
        RequestBottomPanel(AtlasPanelSection.Settings);
        return true;
    }

    private bool OpenVisualLab()
    {
        RequestBottomPanel(AtlasPanelSection.VisualLab);
        return true;
    }

    private bool CloseVisualLab()
    {
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
