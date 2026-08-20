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
/// Selectable data layers, loaded-data search markers and the ore hover card.
/// </summary>
public sealed partial class ModernAtlasDialog
{
    private void ComposeMapLayerPanel(
        double contentX,
        double contentY,
        double panelWidth
    )
    {
        bool oreControls = activeMapLayer == AtlasMapLayer.OreDensity
            && CreativeCheatSettingsAvailable;
        bool compact = panelWidth < 420;
        double height = compact
            ? oreControls ? 218 : 136
            : oreControls ? 150 : 104;
        double selectorX = compact ? 18 : 142;
        double selectorY = compact ? 40 : 8;
        double selectorWidth = compact
            ? Math.Max(90, panelWidth - 36)
            : Math.Max(90, panelWidth - 160);
        if (mapLayerPanelCollapsed)
        {
            ElementBounds collapsedRoot = ElementBounds.Fixed(
                contentX + 18,
                contentY,
                panelWidth,
                52
            );
            mapLayerPanelBounds = collapsedRoot;
            mapLayerPanel = capi.Gui.CreateCompo(
                    "modernatlas-map-layer",
                    collapsedRoot
                )
                .AddStaticCustomDraw(
                    ElementBounds.Fixed(0, 0, panelWidth, 52),
                    AtlasUiStyle.DrawCard
                )
                .AddAtlasButton(
                    "▸",
                    ToggleMapLayerPanel,
                    ElementBounds.Fixed(8, 6, 40, 40),
                    "map-layer-toggle",
                    AtlasButtonStyle.Icon
                )
                .AddStaticText(
                    "MAP LAYER",
                    AtlasUiStyle.LabelFont(12),
                    ElementBounds.Fixed(58, 14, 190, 24)
                )
                .AddStaticText(
                    activeMapLayer.DisplayName(),
                    AtlasUiStyle.DetailFont(10),
                    ElementBounds.Fixed(250, 14, Math.Max(90, panelWidth - 268), 20)
                )
                .Compose(false);
            synchronizedOreCodeRevision = mapLayerTexture.OreCodeRevision;
            return;
        }

        ElementBounds root = ElementBounds.Fixed(
            contentX + 18,
            contentY,
            panelWidth,
            height
        );
        mapLayerPanelBounds = root;
        GetMapLayerChoiceOptions(
            selectorWidth < 220,
            out string[] legacyLayerValues,
            out string[] legacyLayerNames,
            out int legacyLayerIndex
        );
        GuiComposer composer = capi.Gui.CreateCompo("modernatlas-map-layer", root)
            .AddStaticCustomDraw(
                ElementBounds.Fixed(0, 0, panelWidth, height),
                AtlasUiStyle.DrawCard
            )
            .AddAtlasButton(
                "▾",
                ToggleMapLayerPanel,
                ElementBounds.Fixed(8, 8, 40, 40),
                "map-layer-toggle",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "MAP LAYER",
                AtlasUiStyle.LabelFont(12),
                ElementBounds.Fixed(58, 17, 124, 24)
            )
            .AddAtlasChoice(
                legacyLayerValues,
                legacyLayerNames,
                legacyLayerIndex,
                OnMapLayerChanged,
                ElementBounds.Fixed(selectorX, selectorY, selectorWidth, 44),
                "map-layer"
            );

        if (oreControls)
        {
            oreFilterLabelLimit = OreFilterLabelLimit(selectorWidth);
            GetOreFilterOptions(
                oreFilterLabelLimit,
                out string[] values,
                out string[] names,
                out int selectedIndex
            );
            composer
                .AddStaticText(
                    "ORE FILTER",
                    AtlasUiStyle.LabelFont(12),
                    ElementBounds.Fixed(18, compact ? 92 : 62, 124, 24)
                )
                .AddAtlasChoice(
                    values,
                    names,
                    selectedIndex,
                    OnOreFilterChanged,
                    ElementBounds.Fixed(
                        selectorX,
                        compact ? 118 : 53,
                        selectorWidth,
                        44
                    ),
                    "ore-filter"
                );
        }

        double statusY = compact
            ? oreControls ? 170 : 88
            : oreControls ? 102 : 55;
        mapLayerPanel = composer
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(18, statusY, Math.Max(90, panelWidth - 36), 20),
                "layer-status"
            )
            .AddDynamicText(
                activeMapLayer.DetailedLegend(),
                AtlasUiStyle.DetailFont(10),
                ElementBounds.Fixed(
                    18,
                    statusY + 21,
                    Math.Max(90, panelWidth - 36),
                    20
                ),
                "layer-legend"
            )
            .Compose(false);
        synchronizedOreCodeRevision = mapLayerTexture.OreCodeRevision;
    }

    /// <summary>
    /// Draws the legend ramp with the exact colors the overlay paints, so the
    /// bar and the map can never disagree.
    /// </summary>
    private static void DrawLayerLegendRamp(
        Context context,
        ElementBounds bounds,
        AtlasMapLayer layer
    )
    {
        double width = bounds.InnerWidth;
        double height = bounds.InnerHeight;
        if (width <= 0 || height <= 0) return;

        int steps = Math.Max(2, (int)Math.Ceiling(width));
        double stepWidth = width / steps;
        for (int index = 0; index < steps; index++)
        {
            float value = steps == 1 ? 0f : index / (float)(steps - 1);
            (float red, float green, float blue) = AtlasMapLayerPalette.Color(layer, value);
            context.SetSourceRGBA(red, green, blue, 0.96);
            context.Rectangle(
                bounds.drawX + index * stepWidth,
                bounds.drawY,
                stepWidth + 1,
                height
            );
            context.Fill();
        }
        context.SetSourceRGBA(1, 1, 1, 0.28);
        context.LineWidth = Math.Max(1, RuntimeEnv.GUIScale * 0.7);
        context.Rectangle(bounds.drawX, bounds.drawY, width, height);
        context.Stroke();

        // Mark the middle stop, the one value the labels name inside the bar.
        double middleX = bounds.drawX + width * 0.5;
        context.MoveTo(middleX, bounds.drawY + height * 0.5);
        context.LineTo(middleX, bounds.drawY + height);
        context.SetSourceRGBA(0.05, 0.06, 0.07, 0.55);
        context.Stroke();
    }

    /// <summary>
    /// The separate "no loaded data" state of the ore layer. It is not a step
    /// on the potential ramp and must not be mistaken for one.
    /// </summary>
    private static void DrawUnavailableLegendSwatch(
        Context context,
        ImageSurface surface,
        ElementBounds bounds
    )
    {
        if (bounds.InnerWidth <= 0 || bounds.InnerHeight <= 0) return;
        (float red, float green, float blue) = AtlasMapLayerPalette.Unavailable;
        context.SetSourceRGBA(red, green, blue, 0.96);
        context.Rectangle(bounds.drawX, bounds.drawY, bounds.InnerWidth, bounds.InnerHeight);
        context.Fill();
        context.SetSourceRGBA(1, 1, 1, 0.34);
        context.LineWidth = Math.Max(1, RuntimeEnv.GUIScale * 0.7);
        context.Rectangle(bounds.drawX, bounds.drawY, bounds.InnerWidth, bounds.InnerHeight);
        context.Stroke();
    }

    private bool OnMapLayerOpacityChanged(int value)
    {
        config.MapLayerOpacityPercent = Math.Clamp(value, 0, 100);
        saveConfig();
        return true;
    }

    /// <summary>
    /// Keeps the Map options controls in step with the active layer: the
    /// arrow selector's index and the overlay opacity slider.
    /// </summary>
    private void SyncMapLayerControls()
    {
        SyncMapLayerChoice();
        mapLayerPanel.GetAtlasSlider("layer-opacity")?.SetValues(
            Math.Clamp(config.MapLayerOpacityPercent, 0, 100),
            0,
            100,
            5,
            "%"
        );
    }

    /// <summary>
    /// Refreshes the Map options read-outs that depend on streaming data: the
    /// status line, the legend caption and the ore ramp's stop labels.
    /// </summary>
    private void UpdateMapLayerPanelText()
    {
        mapLayerPanel?.GetDynamicText("layer-status")?.SetNewText(MapLayerStatusText);
        mapLayerPanel?.GetDynamicText("layer-legend")?.SetNewText(MapLayerLegendCaption);
        (string low, string middle, string high) = MapLayerLegendStops();
        mapLayerPanel?.GetDynamicText("legend-low")?.SetNewText(low);
        mapLayerPanel?.GetDynamicText("legend-middle")?.SetNewText(middle);
        mapLayerPanel?.GetDynamicText("legend-high")?.SetNewText(high);
    }

    /// <summary>
    /// Status line for the active layer. The ore layer names its data sources
    /// explicitly and reports both groups when a radius carries regional maps
    /// and loaded columns at once: one found OreMap must not hide the columns
    /// that filled the rest.
    /// </summary>
    private string MapLayerStatusText
    {
        get
        {
            if (activeMapLayer != AtlasMapLayer.OreDensity)
            {
                return mapLayerTexture.StatusText;
            }
            return AtlasOreStatusText.Compose(
                mapLayerTexture.Failed,
                mapLayerTexture.Ready,
                mapLayerTexture.ProgressPercent,
                mapLayerTexture.OreLayerSource,
                mapLayerTexture.OreMapCount,
                mapLayerTexture.OreRegionCount,
                mapLayerTexture.OreColumnSampleCount,
                mapLayerTexture.OreBlockHitCount,
                selectedOreCode == null
                    ? "All ores"
                    : searchController.GetEnglishOreName(selectedOreCode),
                AtlasMapLayerTexture.HorizontalSampleSize
            );
        }
    }

    private bool ToggleMapLayerPanel()
    {
        ToggleMapOptions();
        return true;
    }

    private void RecomposeMapLayerPanel()
    {
        if (MapPanelOpen)
        {
            OpenBottomPanelImmediately(AtlasPanelSection.MapOptions);
        }
    }

    /// <summary>
    /// Builds the ore filter options: "All ores" first, then every discovered
    /// ore. Labels are truncated to the control's width so a long ore name
    /// cannot run under the arrows; the asset code, the status text and the
    /// hover card keep the full name.
    /// </summary>
    private void GetOreFilterOptions(
        int maximumLabelLength,
        out string[] values,
        out string[] names,
        out int selectedIndex
    )
    {
        var options = new List<(string Code, string Name)>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string code in mapLayerTexture.DiscoveredOreCodes)
        {
            if (unique.Add(code))
            {
                options.Add((code, searchController.GetEnglishOreName(code)));
            }
        }
        if (selectedOreCode != null && unique.Add(selectedOreCode))
        {
            options.Add((
                selectedOreCode,
                searchController.GetEnglishOreName(selectedOreCode)
            ));
        }
        options.Sort((left, right) => string.Compare(
            left.Name,
            right.Name,
            StringComparison.OrdinalIgnoreCase
        ));

        int total = options.Count + 1;
        values = new string[total];
        names = new string[total];
        values[0] = AllOresFilterValue;
        // The counter tells the reader how far into the list this option is,
        // so a single visible label no longer hides the list's size.
        names[0] = FilterLabel("All", 1, total, maximumLabelLength);
        selectedIndex = 0;
        for (int index = 0; index < options.Count; index++)
        {
            values[index + 1] = options[index].Code;
            names[index + 1] = FilterLabel(
                options[index].Name,
                index + 2,
                total,
                maximumLabelLength
            );
            if (string.Equals(options[index].Code, selectedOreCode, StringComparison.Ordinal))
            {
                selectedIndex = index + 1;
            }
        }
    }

    /// <summary>
    /// "Cassiterite  4/17": the readable name plus its position in the filter
    /// list. The counter is never truncated; the name yields space to it.
    /// </summary>
    private static string FilterLabel(
        string name,
        int position,
        int total,
        int maximumLabelLength
    )
    {
        string counter = $"  {position}/{total}";
        int nameBudget = Math.Max(4, maximumLabelLength - counter.Length);
        return TruncateLabel(name, nameBudget) + counter;
    }

    private void SynchronizeOreFilterOptions()
    {
        if (activeMapLayer != AtlasMapLayer.OreDensity
            || mapLayerTexture.OreCodeRevision == synchronizedOreCodeRevision)
        {
            return;
        }

        GuiElementAtlasChoice? choice = mapLayerPanel?.GetAtlasChoice("ore-filter");
        if (choice == null) return;
        GetOreFilterOptions(
            oreFilterLabelLimit,
            out string[] values,
            out string[] names,
            out int selectedIndex
        );
        synchronizingOreFilterChoice = true;
        try
        {
            // The list grows while ore data streams in. SetList only clamps the
            // index to the new range; the selection is preserved because the
            // provider keeps the chosen ore in the options and resolves its
            // index, which the following SetSelectedIndex applies. When the
            // code cannot be represented, that index is "All ores".
            choice.SetList(values, names);
            choice.SetSelectedIndex(selectedIndex);
            synchronizedOreCodeRevision = mapLayerTexture.OreCodeRevision;
        }
        finally
        {
            synchronizingOreFilterChoice = false;
        }
    }

    private void OnOreFilterChanged(string value, bool selected)
    {
        if (!selected || synchronizingOreFilterChoice) return;
        string? requested = string.Equals(value, AllOresFilterValue, StringComparison.Ordinal)
            ? null
            : value;
        // An arrow click on a single-option list reports the same value again.
        // Do not restart the budgeted ore build for an unchanged selection.
        if (string.Equals(requested, selectedOreCode, StringComparison.Ordinal)) return;
        selectedOreCode = requested;
        ClearOreHover();
        if (IsOpened()) PrepareMapLayer();
    }

    /// <summary>
    /// Builds the arrow selector's option list. Ore analysis is present only
    /// with spoiler access, so a plain arrow click can never wrap onto a layer
    /// that <see cref="OnMapLayerChanged"/> would have to reject with an
    /// in-game error. Narrow controls use short labels because the centered
    /// name would otherwise overlap the arrow zones; the full display names
    /// and every legend stay unchanged outside this control.
    /// </summary>
    private void GetMapLayerChoiceOptions(
        bool shortNames,
        out string[] values,
        out string[] names,
        out int selectedIndex
    )
    {
        var allowed = new List<AtlasMapLayer>();
        foreach (AtlasMapLayer layer in MapLayerChoiceOrder)
        {
            if (layer.RequiresSpoilerAccess() && !UnitInspectionEnabled) continue;
            allowed.Add(layer);
        }

        values = new string[allowed.Count];
        names = new string[allowed.Count];
        selectedIndex = 0;
        for (int index = 0; index < allowed.Count; index++)
        {
            values[index] = AtlasMapLayerInfo.Values[(int)allowed[index]];
            names[index] = MapLayerChoiceLabel(allowed[index], shortNames);
            if (allowed[index] == activeMapLayer) selectedIndex = index;
        }
    }

    /// <summary>
    /// Label budget for an arrow selector: the centered name may use the space
    /// between the two arrow zones, which cover the outer 18 percent each.
    /// </summary>
    private static int OreFilterLabelLimit(double selectorWidth) =>
        (int)Math.Clamp(selectorWidth * 0.64 / 6.5, 8, 40);

    private static string MapLayerChoiceLabel(AtlasMapLayer layer, bool shortNames)
    {
        if (!shortNames) return layer.DisplayName();
        return layer switch
        {
            AtlasMapLayer.TexturedTerrain => "Terrain",
            AtlasMapLayer.SoilFertility => "Fertility",
            AtlasMapLayer.OreDensity => "Ore",
            _ => layer.DisplayName()
        };
    }

    /// <summary>
    /// Corrects a layer that lost its spoiler access without an atlas event.
    /// Leaving Creative through a console command does not raise a cheat-mode
    /// change, so the renderer could stay on Ore analysis while the selector no
    /// longer offers it. Reset the layer first; SetMapLayer then rebuilds the
    /// panel with the reduced option list.
    /// </summary>
    private void EnforceMapLayerAccess()
    {
        if (activeMapLayer.RequiresSpoilerAccess() && !UnitInspectionEnabled)
        {
            SetMapLayer(AtlasMapLayer.TexturedTerrain);
        }
    }

    private void OnMapLayerChanged(string value, bool selected)
    {
        if (!selected || synchronizingMapLayerChoice) return;

        AtlasMapLayer layer = AtlasMapLayerInfo.FromValue(value);
        if (layer.RequiresSpoilerAccess() && !UnitInspectionEnabled)
        {
            capi.TriggerIngameError(
                this,
                "modernatlas-layer-access",
                "Ore analysis is available only in Creative or server-authorized Cheat Mode."
            );
            SyncMapLayerChoice();
            return;
        }
        SetMapLayer(layer);
    }

    private void SetMapLayer(AtlasMapLayer layer)
    {
        AtlasMapLayer previousLayer = activeMapLayer;
        if (!config.MapLayersEnabled)
        {
            layer = AtlasMapLayer.TexturedTerrain;
        }
        if (layer.RequiresSpoilerAccess() && !UnitInspectionEnabled)
        {
            layer = AtlasMapLayer.TexturedTerrain;
        }
        activeMapLayer = layer;
        ClearOreHover();
        if (previousLayer != activeMapLayer && mapLayerPanel != null)
        {
            // Rebuild after the dropdown event has returned to GuiComposer;
            // disposing the shared bottom composer from inside its callback
            // would invalidate the current element iteration.
            pendingMapLayerPanelRecompose = true;
        }
        SyncMapLayerChoice();
        // The toolbar dot reports the active layer, so it repaints here too.
        RefreshToolbarLayerIndicator();
        if (IsOpened()) PrepareMapLayer();
        else mapLayerTexture.Reset();
    }

    private void SyncMapLayerChoice()
    {
        GuiElementAtlasChoice? choice = mapLayerPanel?.GetAtlasChoice("map-layer");
        if (choice == null) return;

        // Every access change routes through SetMapLayer and a panel rebuild,
        // so the element already holds the matching option list here. Only the
        // selected entry is synchronized, and the index is resolved through the
        // filtered list because it is not the AtlasMapLayer enum value.
        GetMapLayerChoiceOptions(false, out _, out _, out int selectedIndex);
        synchronizingMapLayerChoice = true;
        try
        {
            choice.SetSelectedIndex(selectedIndex);
        }
        finally
        {
            synchronizingMapLayerChoice = false;
        }
    }

    private void PrepareMapLayer()
    {
        if (!config.MapLayersEnabled)
        {
            activeMapLayer = AtlasMapLayer.TexturedTerrain;
            mapLayerTexture.Reset();
            SyncMapLayerChoice();
            return;
        }
        mapLayerTexture.Begin(
            activeMapLayer,
            capi.World.Player.Entity.Pos.X,
            capi.World.Player.Entity.Pos.Z,
            GameViewDistance,
            UnitInspectionEnabled,
            selectedOreCode
        );
    }

    private void AdvanceSearch()
    {
        if (!SearchModeActive) return;

        IReadOnlyList<AtlasRenderedEntity> renderedEntities = exactChunkRenderer
            ?.LastRenderedEntities ?? Array.Empty<AtlasRenderedEntity>();
        searchController.Advance(
            GameViewDistance,
            SurfaceSafetyEnabled,
            surfaceHeightTexture,
            renderedEntities,
            UnitInspectionEnabled
        );
    }

    private void RenderSearchMarkers()
    {
        if (!SearchModeActive || interfaceHidden || !searchController.HasActiveQuery) return;
        EnsureSearchMarkerTexture();
        if (searchMarkerTexture is not { TextureId: > 0 }) return;

        bool clipped = BeginScrollContentClip();
        try
        {
            float guiScale = (float)Math.Max(0.5, RuntimeEnv.GUIScale);
            float pulse = 0.5f + 0.5f * MathF.Sin(capi.ElapsedMilliseconds / 230f);
            float markerSize = Math.Clamp((28f + pulse * 6f) * guiScale, 24f, 58f);
            foreach (AtlasSearchResult result in searchController.BlockResults)
            {
                RenderSearchMarker(result, markerSize, pulse);
            }
            foreach (AtlasSearchResult result in searchController.DynamicResults)
            {
                RenderSearchMarker(result, markerSize + 4f, pulse);
            }
        }
        finally
        {
            EndScrollContentClip(clipped);
        }
    }

    private void RenderSearchMarker(
        AtlasSearchResult result,
        float markerSize,
        float pulse
    )
    {
        double dx = result.X - capi.World.Player.Entity.Pos.X;
        double dz = result.Z - capi.World.Player.Entity.Pos.Z;
        double clearRadius = GameViewDistance;
        if (dx * dx + dz * dz > clearRadius * clearRadius) return;
        if (!TryProjectAtlasPosition(
            result.X,
            result.Y,
            result.Z,
            out double screenX,
            out double screenY,
            out _
        ))
        {
            return;
        }

        float alpha = 0.78f + pulse * 0.18f;
        // Same palette the panel legend reads, so a swatch always matches its
        // marker.
        (float red, float green, float blue) = AtlasSearchMarkerPalette.Color(result.Kind);
        Vec4f color = new(red, green, blue, alpha);
        capi.Render.Render2DTexture(
            searchMarkerTexture!.TextureId,
            (float)screenX - markerSize * 0.5f,
            (float)screenY - markerSize * 0.5f,
            markerSize,
            markerSize,
            45,
            color
        );
    }

    private void EnsureSearchMarkerTexture()
    {
        if (searchMarkerTexture?.TextureId > 0) return;

        const int size = 64;
        searchMarkerTexture ??= new LoadedTexture(capi);
        using ImageSurface surface = new(Format.Argb32, size, size);
        using Context context = new(surface);
        context.Operator = Operator.Source;
        context.SetSourceRGBA(0, 0, 0, 0);
        context.Paint();
        context.Operator = Operator.Over;

        for (int radius = 28; radius >= 18; radius -= 2)
        {
            double progress = (28 - radius) / 10.0;
            context.SetSourceRGBA(1, 1, 1, 0.025 + progress * 0.018);
            context.Arc(32, 32, radius, 0, Math.PI * 2);
            context.Fill();
        }
        context.SetSourceRGBA(1, 1, 1, 0.96);
        context.LineWidth = 3;
        context.Arc(32, 32, 17, 0, Math.PI * 2);
        context.Stroke();
        context.LineWidth = 2;
        context.MoveTo(32, 7);
        context.LineTo(32, 20);
        context.MoveTo(32, 44);
        context.LineTo(32, 57);
        context.MoveTo(7, 32);
        context.LineTo(20, 32);
        context.MoveTo(44, 32);
        context.LineTo(57, 32);
        context.Stroke();
        context.Arc(32, 32, 3.5, 0, Math.PI * 2);
        context.Fill();

        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref searchMarkerTexture);
    }

    /// <summary>
    /// Pointer read-out for every data layer. The card reports the sample the
    /// active layer already holds; no chunk is requested and nothing is
    /// cached beyond the hovered cell.
    /// </summary>
    /// <summary>
    /// Result counts per category, taken only from the markers the search has
    /// already produced. Those passed the entity-disclosure policy, so no
    /// category can appear here that the atlas is not allowed to show. Empty
    /// categories are left out.
    /// </summary>
    private List<(AtlasSearchResultKind Kind, int Count)> SearchCategoryCounts()
    {
        var counts = new Dictionary<AtlasSearchResultKind, int>();
        foreach (AtlasSearchResult result in searchController.BlockResults)
        {
            counts.TryGetValue(result.Kind, out int existing);
            counts[result.Kind] = existing + 1;
        }
        foreach (AtlasSearchResult result in searchController.DynamicResults)
        {
            counts.TryGetValue(result.Kind, out int existing);
            counts[result.Kind] = existing + 1;
        }

        var ordered = new List<(AtlasSearchResultKind Kind, int Count)>();
        foreach (AtlasSearchResultKind kind in AtlasSearchMarkerPalette.Order)
        {
            if (counts.TryGetValue(kind, out int count) && count > 0)
            {
                ordered.Add((kind, count));
            }
        }
        return ordered;
    }

    /// <summary>
    /// Search status: scanning, results, or an explicit empty result. The
    /// controller keeps the detailed stage text for the scanning case.
    /// </summary>
    private string SearchStatusText => AtlasSearchStatusText.Compose(
        searchController.HasActiveQuery,
        searchController.Scanning,
        searchController.ScanPercent,
        searchController.MarkerCount,
        searchController.StatusText
    );

    /// <summary>
    /// Builds the marker legend as one texture drawn over the search panel.
    /// It reads <see cref="AtlasSearchMarkerPalette"/>, the same source the
    /// markers use.
    /// </summary>
    private void EnsureSearchLegendTexture(double panelWidth)
    {
        List<(AtlasSearchResultKind Kind, int Count)> counts = SearchCategoryCounts();
        var signature = new System.Text.StringBuilder();
        signature.Append(panelWidth.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        foreach ((AtlasSearchResultKind kind, int count) in counts)
        {
            signature.Append('|').Append(kind).Append(':').Append(count);
        }
        string key = signature.ToString();
        if (searchLegendTexture is { TextureId: > 0 }
            && string.Equals(searchLegendSignature, key, StringComparison.Ordinal))
        {
            return;
        }

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        int width = Math.Max(1, (int)Math.Ceiling(panelWidth * scale));
        double lineHeight = 15 * scale;
        double swatch = 8 * scale;
        double gap = 14 * scale;
        double x = 0;
        double y = 0;
        var placements = new List<(double X, double Y, string Text, AtlasSearchResultKind Kind)>();
        using (ImageSurface measureSurface = new(Format.Argb32, 4, 4))
        using (Context measure = new(measureSurface))
        {
            measure.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Normal);
            measure.SetFontSize(9.5 * scale);
            foreach ((AtlasSearchResultKind kind, int count) in counts)
            {
                string text = FormattableString.Invariant(
                    $"{AtlasSearchMarkerPalette.DisplayName(kind)} {count}"
                );
                double itemWidth = swatch + 5 * scale + measure.TextExtents(text).Width;
                if (x > 0 && x + itemWidth > width)
                {
                    x = 0;
                    y += lineHeight;
                }
                placements.Add((x, y, text, kind));
                x += itemWidth + gap;
            }
        }

        int height = Math.Max(1, (int)Math.Ceiling(y + lineHeight));
        searchLegendTexture ??= new LoadedTexture(capi);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        AtlasUiStyle.Clear(context);
        context.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Normal);
        context.SetFontSize(9.5 * scale);
        foreach ((double itemX, double itemY, string text, AtlasSearchResultKind kind) in placements)
        {
            (float red, float green, float blue) = AtlasSearchMarkerPalette.Color(kind);
            double centerY = itemY + lineHeight * 0.5;
            context.Arc(itemX + swatch * 0.5, centerY, swatch * 0.5, 0, Math.PI * 2);
            context.SetSourceRGBA(red, green, blue, 0.96);
            context.FillPreserve();
            context.SetSourceRGBA(1, 1, 1, 0.45);
            context.LineWidth = Math.Max(1, scale * 0.6);
            context.Stroke();

            TextExtents extents = context.TextExtents(text);
            context.SetSourceRGBA(0.85, 0.88, 0.91, 0.98);
            context.MoveTo(
                itemX + swatch + 5 * scale,
                centerY + extents.Height * 0.5
            );
            context.ShowText(text);
        }

        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref searchLegendTexture);
        searchLegendSignature = key;
    }

    /// <summary>
    /// Places the legend under the status line inside the search panel.
    /// Shared with the automated test, which asserts that the texture exists
    /// and stays within the panel.
    /// </summary>
    private bool TryResolveSearchLegendPlacement(
        out float x,
        out float y,
        out float width,
        out float height
    )
    {
        x = 0;
        y = 0;
        width = 0;
        height = 0;
        if (bottomPanelSection != AtlasPanelSection.Search
            || !hasBottomPanelGeometry
            || SearchCategoryCounts().Count == 0)
        {
            return false;
        }

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double contentWidth = Math.Max(60, bottomPanelGeometry.Width / scale - 32);
        EnsureSearchLegendTexture(contentWidth);
        if (searchLegendTexture is not { TextureId: > 0 }) return false;

        x = (float)(bottomPanelGeometry.X + 16 * scale);
        y = (float)(bottomPanelGeometry.Y + 98 * scale);
        width = searchLegendTexture.Width;
        height = searchLegendTexture.Height;
        return true;
    }

    private void RenderSearchLegend()
    {
        if (interfaceHidden) return;
        if (!TryResolveSearchLegendPlacement(
            out float x,
            out float y,
            out float width,
            out float height
        ))
        {
            return;
        }
        capi.Render.Render2DTexture(
            searchLegendTexture!.TextureId,
            x,
            y,
            width,
            height,
            52,
            ColorUtil.WhiteArgbVec
        );
    }

    private void UpdateLayerHover(int mouseX, int mouseY, bool blocked)
    {
        // The automated card checks arm a read-out deliberately and then wait
        // for a capture; the live pointer must not clear it in between.
        if (automatedSmokeTestHoldLayerHover) return;
        oreHoverMouseX = mouseX;
        oreHoverMouseY = mouseY;
        if (blocked
            || interfaceHidden
            || !LayerHoverAvailable
            || settingsModalOpen
            || performanceModalOpen
            || creativeSettingsModalOpen
            || visualLabModalOpen
            || leftDragging
            || rightDragging
            || !AtlasViewport.Contains(mouseX, mouseY))
        {
            ClearOreHover();
            return;
        }

        long now = capi.ElapsedMilliseconds;
        if (now - lastOreHoverUpdateMilliseconds < OreHoverRefreshMilliseconds)
        {
            return;
        }
        lastOreHoverUpdateMilliseconds = now;

        if (!TryResolvePointerSurface(mouseX, mouseY, out int worldX, out int worldZ))
        {
            ClearOreHover();
            return;
        }

        int cellX = (int)Math.Floor(worldX / (double)AtlasMapLayerTexture.HorizontalSampleSize)
            * AtlasMapLayerTexture.HorizontalSampleSize
            + AtlasMapLayerTexture.HorizontalSampleSize / 2;
        int cellZ = (int)Math.Floor(worldZ / (double)AtlasMapLayerTexture.HorizontalSampleSize)
            * AtlasMapLayerTexture.HorizontalSampleSize
            + AtlasMapLayerTexture.HorizontalSampleSize / 2;
        if (cellX == oreHoverCellX && cellZ == oreHoverCellZ
            && (oreHoverInspection != null || climateHoverInspection != null))
        {
            return;
        }

        oreHoverCellX = cellX;
        oreHoverCellZ = cellZ;
        if (activeMapLayer == AtlasMapLayer.OreDensity)
        {
            climateHoverInspection = null;
            if (!mapLayerTexture.TryInspectOre(cellX, cellZ, out oreHoverInspection)
                || oreHoverInspection == null)
            {
                ClearOreHover();
                return;
            }
            BuildOreHoverTexture(oreHoverInspection);
            return;
        }

        oreHoverInspection = null;
        if (!mapLayerTexture.TryInspectClimate(
            cellX,
            cellZ,
            out AtlasClimateInspection? climate
        ) || climate == null)
        {
            ClearOreHover();
            return;
        }
        climateHoverInspection = climate;
        BuildClimateHoverTexture(climate.Value);
    }

    /// <summary>
    /// Ore stays behind the spoiler gate; the climate layers are readable
    /// whenever they are the active layer.
    /// </summary>
    private bool LayerHoverAvailable => activeMapLayer switch
    {
        AtlasMapLayer.OreDensity => CreativeCheatSettingsAvailable,
        AtlasMapLayer.TexturedTerrain => false,
        _ => true
    };

    /// <summary>
    /// The climate read-out. It names the world-generation source explicitly
    /// so the value is never read as current weather.
    /// </summary>
    private void BuildClimateHoverTexture(AtlasClimateInspection inspection)
    {
        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        int width = Math.Max(1, (int)Math.Ceiling(320 * scale));
        int height = Math.Max(1, (int)Math.Ceiling(150 * scale));
        oreHoverTexture ??= new LoadedTexture(capi);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        AtlasUiStyle.Clear(context);
        AtlasUiStyle.DrawRaisedPanel(context, 0, 0, width, height, 13);

        DrawOreHoverText(
            context,
            inspection.Layer.DisplayName().ToUpperInvariant(),
            18,
            27,
            12,
            true,
            0.97,
            0.98,
            0.99,
            scale
        );
        (float red, float green, float blue) = AtlasMapLayerPalette.Color(
            inspection.Layer,
            inspection.Normalized
        );
        context.SetSourceRGBA(red, green, blue, 0.96);
        context.Rectangle(18 * scale, 38 * scale, 13 * scale, 13 * scale);
        context.Fill();
        DrawOreHoverText(
            context,
            $"{inspection.Grade}  ·  {inspection.ValueText}",
            38,
            50,
            12,
            true,
            0.96,
            0.97,
            0.98,
            scale
        );
        DrawOreHoverText(
            context,
            $"X {inspection.WorldX}  ·  Z {inspection.WorldZ}  ·  Surface Y {inspection.SurfaceY}",
            18,
            82,
            10.5,
            false,
            0.78,
            0.83,
            0.88,
            scale
        );
        DrawOreHoverText(
            context,
            inspection.Layer.Description(),
            18,
            104,
            10,
            false,
            0.72,
            0.78,
            0.83,
            scale
        );
        DrawOreHoverText(
            context,
            $"{AtlasMapLayerTexture.HorizontalSampleSize} × {AtlasMapLayerTexture.HorizontalSampleSize} block sample  ·  loaded data only",
            18,
            126,
            9.5,
            false,
            0.65,
            0.71,
            0.75,
            scale
        );
        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref oreHoverTexture);
    }

    private bool TryResolvePointerSurface(
        int mouseX,
        int mouseY,
        out int worldX,
        out int worldZ
    )
    {
        worldX = 0;
        worldZ = 0;
        AtlasViewportBounds viewport = AtlasViewport;
        if (!viewport.Contains(mouseX, mouseY)) return false;

        double pixelsPerBlock = viewport.Height / (2.0 * Math.Max(0.001f, zoom));
        double projectedRight = (mouseX - (viewport.X + viewport.Width * 0.5))
            / pixelsPerBlock;
        double projectedUp = ((viewport.Y + viewport.Height * 0.5) - mouseY)
            / pixelsPerBlock;
        double yaw = yawDegrees * GameMath.DEG2RAD;
        double pitch = pitchDegrees * GameMath.DEG2RAD;
        double sinYaw = Math.Sin(yaw);
        double cosYaw = Math.Cos(yaw);
        double sinPitch = Math.Sin(pitch);
        double cosPitch = Math.Cos(pitch);

        double rightX = cosYaw;
        double rightZ = -sinYaw;
        double upX = -sinYaw * sinPitch;
        double upY = cosPitch;
        double upZ = -cosYaw * sinPitch;
        double forwardX = -sinYaw * cosPitch;
        double forwardY = -sinPitch;
        double forwardZ = -cosYaw * cosPitch;
        double rayDistance = Math.Max(640, GameViewDistance * 2.5);
        double originX = centerX - forwardX * rayDistance
            + rightX * projectedRight + upX * projectedUp;
        double originY = centerY - forwardY * rayDistance + upY * projectedUp;
        double originZ = centerZ - forwardZ * rayDistance
            + rightZ * projectedRight + upZ * projectedUp;
        double maximumTravel = rayDistance * 2.0;
        const double step = 4.0;
        int mapSizeX = capi.World.BlockAccessor.MapSizeX;
        int mapSizeZ = capi.World.BlockAccessor.MapSizeZ;
        int chunkSize = GlobalConstants.ChunkSize;
        double disclosureRadiusSquared = (double)GameViewDistance * GameViewDistance;
        double playerX = capi.World.Player.Entity.Pos.X;
        double playerZ = capi.World.Player.Entity.Pos.Z;

        for (double travel = 0; travel <= maximumTravel; travel += step)
        {
            double x = originX + forwardX * travel;
            double y = originY + forwardY * travel;
            double z = originZ + forwardZ * travel;
            int xInt = (int)Math.Floor(x);
            int zInt = (int)Math.Floor(z);
            if (xInt < 0 || zInt < 0 || xInt >= mapSizeX || zInt >= mapSizeZ)
            {
                continue;
            }
            double dx = x - playerX;
            double dz = z - playerZ;
            if (dx * dx + dz * dz > disclosureRadiusSquared) continue;

            int chunkX = xInt / chunkSize;
            int chunkZ = zInt / chunkSize;
            IMapChunk? mapChunk = capi.World.BlockAccessor.GetMapChunk(chunkX, chunkZ);
            ushort[]? heightMap = mapChunk?.WorldGenTerrainHeightMap;
            if (heightMap == null || heightMap.Length < chunkSize * chunkSize) continue;
            int localX = xInt - chunkX * chunkSize;
            int localZ = zInt - chunkZ * chunkSize;
            int surfaceY = heightMap[localZ * chunkSize + localX];
            if (y > surfaceY + 1.0) continue;

            worldX = xInt;
            worldZ = zInt;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The ore read-out. It always states which of the two sources it speaks
    /// for, because a mapped regional potential and a count of blocks seen in
    /// one loaded column are different claims.
    /// </summary>
    private void BuildOreHoverTexture(AtlasOreInspection inspection)
    {
        bool regional = inspection.Source == AtlasOreInspectionSource.RegionalOreMaps;
        AtlasOreReading[] visible = SelectVisibleOreReadings(inspection.Readings);
        int hiddenCount = Math.Max(0, inspection.Readings.Length - visible.Length);
        string? aggregateNote = selectedOreCode == null && inspection.Readings.Length > 1
            ? regional
                ? "All ores: ranked by potential; the map color is the highest, not a sum"
                : "All ores: blocks counted per ore; the map color is the strongest, not a sum"
            : null;

        const double rowHeight = 30;
        double headerHeight = aggregateNote == null ? 78 : 96;
        int rowCount = Math.Max(1, visible.Length);
        double contentHeight = headerHeight
            + rowCount * rowHeight
            + (hiddenCount > 0 ? 16 : 0)
            + 56;
        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        int width = Math.Max(1, (int)Math.Ceiling(430 * scale));
        int height = Math.Max(1, (int)Math.Ceiling(contentHeight * scale));
        oreHoverTexture ??= new LoadedTexture(capi);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        AtlasUiStyle.Clear(context);
        AtlasUiStyle.DrawRaisedPanel(context, 0, 0, width, height, 13);

        DrawOreHoverText(
            context,
            regional ? "REGIONAL POTENTIAL" : "LOADED COLUMN",
            18,
            26,
            12,
            true,
            regional ? 0.72 : 0.95,
            regional ? 0.86 : 0.80,
            regional ? 1.00 : 0.42,
            scale
        );
        string position = $"X {inspection.WorldX}  ·  Z {inspection.WorldZ}  ·  Surface Y {inspection.SurfaceY}";
        DrawOreHoverText(context, position, 18, 48, 10.5, false, 0.78, 0.83, 0.88, scale);
        if (aggregateNote != null)
        {
            DrawOreHoverText(context, aggregateNote, 18, 68, 9.5, false, 0.68, 0.74, 0.79, scale);
        }

        double rowY = headerHeight;
        if (visible.Length == 0)
        {
            string empty = regional
                ? "No mapped potential above the trace threshold"
                : "No ore blocks in this loaded column";
            DrawOreHoverText(context, empty, 18, rowY, 11, false, 0.85, 0.87, 0.89, scale);
        }
        else
        {
            foreach (AtlasOreReading reading in visible)
            {
                bool selected = selectedOreCode != null
                    && string.Equals(reading.Code, selectedOreCode, StringComparison.Ordinal);
                string name = TruncateLabel(
                    searchController.GetEnglishOreName(reading.Code),
                    26
                );
                if (selected) name = "› " + name;
                string value = reading.IsPotential
                    ? $"{AtlasOrePotential.Grade(reading.Potential)}  {reading.Potential * 100:0.##}%"
                    : reading.BlockCount == 1
                        ? "1 observed ore block"
                        : $"{reading.BlockCount} observed ore blocks";
                (double red, double green, double blue) = reading.IsPotential
                    ? GradeColor(reading.Potential)
                    : (0.92, 0.72, 0.30);
                DrawOreHoverText(context, name, 18, rowY, 11, selected, 0.96, 0.97, 0.98, scale);
                DrawOreHoverText(context, value, 240, rowY, 10.5, true, red, green, blue, scale);
                // The asset code stays available but subordinate to the name.
                DrawOreHoverText(
                    context,
                    TruncateLabel(reading.Code, 44),
                    18,
                    rowY + 12,
                    8,
                    false,
                    0.58,
                    0.63,
                    0.68,
                    scale
                );
                rowY += rowHeight;
            }
        }

        if (hiddenCount > 0)
        {
            DrawOreHoverText(
                context,
                regional
                    ? $"+{hiddenCount} more mapped ores"
                    : $"+{hiddenCount} more ores in this column",
                18,
                rowY + 2,
                9.5,
                false,
                0.65,
                0.70,
                0.74,
                scale
            );
            rowY += 16;
        }

        double infoY = rowY + 18;
        string rock = inspection.HostRockCode == null
            ? "unavailable in loaded blocks"
            : TruncateLabel(searchController.GetEnglishBlockName(inspection.HostRockCode), 32);
        DrawOreHoverText(context, $"Host rock: {rock}", 18, infoY, 10, false, 0.76, 0.81, 0.85, scale);
        string source = regional
            ? $"Source: {inspection.SourceMapCount} loaded regional OreMaps · loaded data only"
            : "Source: exact loaded block column · potential grade unavailable · loaded data only";
        DrawOreHoverText(context, source, 18, infoY + 18, 9, false, 0.65, 0.71, 0.75, scale);
        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref oreHoverTexture);
    }

    private AtlasOreReading[] SelectVisibleOreReadings(AtlasOreReading[] readings)
    {
        const int maximum = 5;
        if (readings.Length <= maximum) return readings;
        var selected = new List<AtlasOreReading>(maximum);
        if (selectedOreCode != null)
        {
            foreach (AtlasOreReading reading in readings)
            {
                if (!string.Equals(reading.Code, selectedOreCode, StringComparison.Ordinal)) continue;
                selected.Add(reading);
                break;
            }
        }
        foreach (AtlasOreReading reading in readings)
        {
            if (selected.Count >= maximum) break;
            if (selectedOreCode != null
                && string.Equals(reading.Code, selectedOreCode, StringComparison.Ordinal))
            {
                continue;
            }
            selected.Add(reading);
        }
        return selected.ToArray();
    }

    private static (double R, double G, double B) GradeColor(float potential) =>
        potential switch
        {
            >= 0.6666667f => (1.00, 0.91, 0.16),
            >= 0.5333334f => (1.00, 0.60, 0.14),
            >= 0.4f => (1.00, 0.35, 0.28),
            >= 0.2666667f => (0.95, 0.30, 0.66),
            >= 0.1333334f => (0.72, 0.40, 0.94),
            >= AtlasOrePotential.CategorizedThreshold => (0.52, 0.55, 0.88),
            _ => (0.64, 0.69, 0.75)
        };

    private static string TruncateLabel(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";

    private static void DrawOreHoverText(
        Context context,
        string text,
        double x,
        double y,
        double size,
        bool bold,
        double red,
        double green,
        double blue,
        double scale
    )
    {
        context.SelectFontFace(
            "Sans",
            FontSlant.Normal,
            bold ? FontWeight.Bold : FontWeight.Normal
        );
        context.SetFontSize(size * scale);
        context.SetSourceRGBA(red, green, blue, 0.98);
        context.MoveTo(x * scale, y * scale);
        context.ShowText(text);
    }

    private void RenderLayerHoverCard()
    {
        if ((oreHoverInspection == null && climateHoverInspection == null)
            || oreHoverTexture is not { TextureId: > 0 }
            || interfaceHidden
            || !LayerHoverAvailable)
        {
            return;
        }

        LoadedTexture hoverTexture = oreHoverTexture!;
        float width = hoverTexture.Width;
        float height = hoverTexture.Height;
        ResolveLayerHoverCardPlacement(
            width,
            height,
            oreHoverMouseX,
            oreHoverMouseY,
            out float x,
            out float y
        );
        capi.Render.Render2DTexture(
            hoverTexture.TextureId,
            x,
            y,
            width,
            height,
            48,
            ColorUtil.WhiteArgbVec
        );
    }

    /// <summary>
    /// Places the hover card next to the pointer and keeps it inside the map
    /// viewport, so a card opened at an edge cannot be cut off.
    /// </summary>
    private void ResolveLayerHoverCardPlacement(
        float width,
        float height,
        int mouseX,
        int mouseY,
        out float x,
        out float y
    )
    {
        AtlasViewportBounds viewport = AtlasViewport;
        x = mouseX + 18;
        if (x + width > viewport.Right - 8) x = mouseX - width - 18;
        x = Math.Clamp(x, viewport.X + 8, Math.Max(viewport.X + 8, viewport.Right - width - 8));
        y = Math.Clamp(
            mouseY + 16,
            viewport.Y + 8,
            Math.Max(viewport.Y + 8, viewport.Bottom - height - 8)
        );
    }

    private void ClearOreHover()
    {
        oreHoverInspection = null;
        climateHoverInspection = null;
        oreHoverCellX = int.MinValue;
        oreHoverCellZ = int.MinValue;
    }

}
