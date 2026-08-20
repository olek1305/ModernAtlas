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
            .AddDropDown(
                AtlasMapLayerInfo.Values,
                AtlasMapLayerInfo.Names,
                (int)activeMapLayer,
                OnMapLayerChanged,
                ElementBounds.Fixed(selectorX, selectorY, selectorWidth, 44),
                "map-layer"
            );

        if (oreControls)
        {
            GetOreFilterOptions(out string[] values, out string[] names, out int selectedIndex);
            composer
                .AddStaticText(
                    "ORE FILTER",
                    AtlasUiStyle.LabelFont(12),
                    ElementBounds.Fixed(18, compact ? 92 : 62, 124, 24)
                )
                .AddDropDown(
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

    private void GetOreFilterOptions(
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

        values = new string[options.Count + 1];
        names = new string[options.Count + 1];
        values[0] = AllOresFilterValue;
        names[0] = "All ores";
        selectedIndex = 0;
        for (int index = 0; index < options.Count; index++)
        {
            values[index + 1] = options[index].Code;
            names[index + 1] = options[index].Name;
            if (string.Equals(options[index].Code, selectedOreCode, StringComparison.Ordinal))
            {
                selectedIndex = index + 1;
            }
        }
    }

    private void SynchronizeOreFilterOptions()
    {
        if (activeMapLayer != AtlasMapLayer.OreDensity
            || mapLayerTexture.OreCodeRevision == synchronizedOreCodeRevision)
        {
            return;
        }

        GuiElementDropDown? dropdown = mapLayerPanel?.GetDropDown("ore-filter");
        if (dropdown == null) return;
        GetOreFilterOptions(out string[] values, out string[] names, out int selectedIndex);
        synchronizingOreFilterDropdown = true;
        try
        {
            dropdown.SetList(values, names);
            dropdown.SetSelectedIndex(selectedIndex);
            synchronizedOreCodeRevision = mapLayerTexture.OreCodeRevision;
        }
        finally
        {
            synchronizingOreFilterDropdown = false;
        }
    }

    private void OnOreFilterChanged(string value, bool selected)
    {
        if (!selected || synchronizingOreFilterDropdown) return;
        selectedOreCode = string.Equals(value, AllOresFilterValue, StringComparison.Ordinal)
            ? null
            : value;
        ClearOreHover();
        if (IsOpened()) PrepareMapLayer();
    }

    private void OnMapLayerChanged(string value, bool selected)
    {
        if (!selected || synchronizingMapLayerDropdown) return;

        AtlasMapLayer layer = AtlasMapLayerInfo.FromValue(value);
        if (layer.RequiresSpoilerAccess() && !UnitInspectionEnabled)
        {
            capi.TriggerIngameError(
                this,
                "modernatlas-layer-access",
                "Ore density is available only in Creative or server-authorized Cheat Mode."
            );
            SyncMapLayerDropdown();
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
        SyncMapLayerDropdown();
        if (IsOpened()) PrepareMapLayer();
        else mapLayerTexture.Reset();
    }

    private void SyncMapLayerDropdown()
    {
        GuiElementDropDown? dropdown = mapLayerPanel?.GetDropDown("map-layer");
        if (dropdown == null) return;

        synchronizingMapLayerDropdown = true;
        try
        {
            dropdown.SetSelectedIndex((int)activeMapLayer);
        }
        finally
        {
            synchronizingMapLayerDropdown = false;
        }
    }

    private void PrepareMapLayer()
    {
        if (!config.MapLayersEnabled)
        {
            activeMapLayer = AtlasMapLayer.TexturedTerrain;
            mapLayerTexture.Reset();
            SyncMapLayerDropdown();
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
        Vec4f color = result.Kind switch
        {
            AtlasSearchResultKind.Block => new Vec4f(1f, 0.72f, 0.18f, alpha),
            AtlasSearchResultKind.Player => new Vec4f(0.20f, 0.86f, 1f, alpha),
            AtlasSearchResultKind.Animal => new Vec4f(0.34f, 1f, 0.48f, alpha),
            AtlasSearchResultKind.Mob => new Vec4f(1f, 0.22f, 0.18f, alpha),
            AtlasSearchResultKind.Npc => new Vec4f(0.78f, 0.48f, 1f, alpha),
            AtlasSearchResultKind.DroppedItem => new Vec4f(1f, 0.46f, 0.12f, alpha),
            _ => new Vec4f(1f, 1f, 1f, alpha)
        };
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

    private void UpdateOreHover(int mouseX, int mouseY, bool blocked)
    {
        oreHoverMouseX = mouseX;
        oreHoverMouseY = mouseY;
        if (blocked
            || interfaceHidden
            || activeMapLayer != AtlasMapLayer.OreDensity
            || !CreativeCheatSettingsAvailable
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
            && oreHoverInspection != null)
        {
            return;
        }

        oreHoverCellX = cellX;
        oreHoverCellZ = cellZ;
        if (!mapLayerTexture.TryInspectOre(cellX, cellZ, out oreHoverInspection)
            || oreHoverInspection == null)
        {
            ClearOreHover();
            return;
        }
        BuildOreHoverTexture(oreHoverInspection);
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

    private void BuildOreHoverTexture(AtlasOreInspection inspection)
    {
        AtlasOreReading[] visible = SelectVisibleOreReadings(inspection.Readings);
        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        int width = Math.Max(1, (int)Math.Ceiling(390 * scale));
        int rowCount = Math.Max(1, visible.Length);
        int height = Math.Max(1, (int)Math.Ceiling((148 + rowCount * 25) * scale));
        oreHoverTexture ??= new LoadedTexture(capi);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        AtlasUiStyle.Clear(context);
        AtlasUiStyle.DrawRaisedPanel(context, 0, 0, width, height, 13);

        DrawOreHoverText(context, "ORE POTENTIAL", 18, 25, 12, true, 0.97, 0.98, 0.99, scale);
        string position = $"X {inspection.WorldX}  •  Z {inspection.WorldZ}  •  Surface {inspection.SurfaceY}";
        DrawOreHoverText(context, position, 18, 47, 10.5, false, 0.75, 0.81, 0.86, scale);

        double rowY = 76;
        if (visible.Length == 0)
        {
            string empty = inspection.Source == AtlasOreInspectionSource.RegionalOreMaps
                ? "No mapped potential above the trace threshold"
                : "No ore blocks in this loaded column";
            DrawOreHoverText(context, empty, 18, rowY, 11, false, 0.85, 0.87, 0.89, scale);
        }
        else
        {
            foreach (AtlasOreReading reading in visible)
            {
                string name = TruncateLabel(searchController.GetEnglishOreName(reading.Code), 24);
                bool selected = selectedOreCode != null
                    && string.Equals(reading.Code, selectedOreCode, StringComparison.Ordinal);
                if (selected) name = "› " + name;
                string value = reading.IsPotential
                    ? $"{AtlasOrePotential.Grade(reading.Potential)}  {reading.Potential * 100:0.##}%"
                    : reading.BlockCount == 1 ? "1 loaded block" : $"{reading.BlockCount} loaded blocks";
                (double red, double green, double blue) = reading.IsPotential
                    ? GradeColor(reading.Potential)
                    : (0.92, 0.72, 0.30);
                DrawOreHoverText(context, name, 18, rowY, 11, selected, 0.96, 0.97, 0.98, scale);
                DrawOreHoverText(context, value, 210, rowY, 10.5, true, red, green, blue, scale);
                rowY += 25;
            }
        }

        int hiddenCount = Math.Max(0, inspection.Readings.Length - visible.Length);
        if (hiddenCount > 0)
        {
            DrawOreHoverText(context, $"+{hiddenCount} more mapped ores", 18, rowY, 9.5, false, 0.65, 0.70, 0.74, scale);
        }
        double infoY = (148 + rowCount * 25) - 45;
        string rock = searchController.GetEnglishBlockName(inspection.HostRockCode);
        DrawOreHoverText(context, $"Rock: {TruncateLabel(rock, 32)}", 18, infoY, 10, false, 0.76, 0.81, 0.85, scale);
        string source = inspection.Source == AtlasOreInspectionSource.RegionalOreMaps
            ? $"Source: {inspection.SourceMapCount} loaded regional OreMaps"
            : "Source: exact loaded block column • grade unavailable";
        DrawOreHoverText(context, source, 18, infoY + 20, 9.5, false, 0.65, 0.71, 0.75, scale);
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

    private void RenderOreHoverCard()
    {
        if (oreHoverInspection == null
            || oreHoverTexture is not { TextureId: > 0 }
            || interfaceHidden
            || activeMapLayer != AtlasMapLayer.OreDensity
            || !CreativeCheatSettingsAvailable)
        {
            return;
        }

        AtlasViewportBounds viewport = AtlasViewport;
        LoadedTexture hoverTexture = oreHoverTexture!;
        float width = hoverTexture.Width;
        float height = hoverTexture.Height;
        float x = oreHoverMouseX + 18;
        if (x + width > viewport.Right - 8) x = oreHoverMouseX - width - 18;
        x = Math.Clamp(x, viewport.X + 8, Math.Max(viewport.X + 8, viewport.Right - width - 8));
        float y = Math.Clamp(
            oreHoverMouseY + 16,
            viewport.Y + 8,
            Math.Max(viewport.Y + 8, viewport.Bottom - height - 8)
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

    private void ClearOreHover()
    {
        oreHoverInspection = null;
        oreHoverCellX = int.MinValue;
        oreHoverCellZ = int.MinValue;
    }

}
