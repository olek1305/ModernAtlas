using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
namespace ModernAtlas;

/// <summary>
/// The Settings panel layout: five named sections flowed into one, two or
/// three columns depending on the panel's width. Heights and the scroll range
/// are measured from this model, so a control can never end up unreachable
/// because a constant went stale.
/// </summary>
public sealed partial class ModernAtlasDialog
{
    private const double SettingsBodyTop = 34;
    private const double SettingsFooterHeight = 17;
    private const double SettingsSidePadding = 14;
    // Keep the scrollbar in its own gutter so the last settings column and
    // the close button never sit underneath the interactive track.
    private const double SettingsScrollbarGutter = 26;
    private const double SettingsScrollbarWidth = 14;
    private const double SettingsScrollbarGap = 8;
    private const string SettingsScrollbarKey = "settings-scrollbar";
    private const double SettingsColumnGap = 10;
    private const double SettingsSectionHeaderHeight = 16;
    private const double SettingsSectionGap = 8;
    private const double SettingsRowGap = 2;
    private const double SettingsReasonHeight = 11;

    private const string SettingsReasonCreativeOnly = "Creative/Cheat only";
    private const string SettingsReasonServerPolicy = "Disabled by server policy";
    private const string SettingsReasonVolumetricClouds = "Requires volumetric clouds";

    /// <summary>One control plus the space its optional reason line needs.</summary>
    private sealed record AtlasSettingsRow(
        string Key,
        double Height,
        Action<GuiComposer, double, double, double> Place
    );

    private sealed record AtlasSettingsSection(
        string Title,
        IReadOnlyList<AtlasSettingsRow> Rows
    )
    {
        public double Height
        {
            get
            {
                double total = SettingsSectionHeaderHeight;
                foreach (AtlasSettingsRow row in Rows) total += row.Height + SettingsRowGap;
                return total + SettingsSectionGap;
            }
        }
    }

    /// <summary>
    /// Three columns on a wide panel, two on a medium one, a single scrolling
    /// column when the viewport is narrow.
    /// </summary>
    private static int SettingsColumnCount(double width) =>
        width >= 700 ? 3 : width >= 460 ? 2 : 1;

    private static double SettingsRowHeight(double height) => height < 150 ? 21 : 25;

    private List<AtlasSettingsSection> BuildSettingsSections(double rowHeight)
    {
        bool access = CreativeCheatSettingsAvailable;
        bool settingsAllowed = SettingsAccessAllowed;
        bool cloudsAvailable = VolumetricCloudRendererAdapter.IsEnabledByGraphicsSettings(capi);
        bool serverAllowsAny = capi.IsSinglePlayer || serverPolicy.AnyEntityModels;

        string? SettingsReason() => settingsAllowed
            ? null
            : SettingsReasonServerPolicy;

        // A category is gated by the server only in multiplayer; singleplayer
        // always allows it, exactly as SyncEntityCategorySwitch decides.
        string? CategoryReason(bool serverEnabled) =>
            !settingsAllowed
                ? SettingsReasonServerPolicy
                : !serverAllowsAny || !(capi.IsSinglePlayer || serverEnabled)
                ? SettingsReasonServerPolicy
                : null;

        return new List<AtlasSettingsSection>
        {
            new(
                "Map & Data",
                new List<AtlasSettingsRow>
                {
                    SwitchRow(
                        "Map layers",
                        OnMapLayersToggled,
                        "map-layers",
                        rowHeight,
                        SettingsReason()
                    ),
                    SwitchRow(
                        "Search loaded data",
                        OnSearchModeToggled,
                        "search-mode",
                        rowHeight,
                        !settingsAllowed
                            ? SettingsReasonServerPolicy
                            : access ? null : SettingsReasonCreativeOnly
                    )
                }
            ),
            new(
                "Presentation & Lighting",
                new List<AtlasSettingsRow>
                {
                    SwitchRow("3D scroll", OnRenderOnScrollToggled, "render-on-scroll", rowHeight, SettingsReason()),
                    SwitchRow("Scroll weather", OnScrollRealtimeWeatherToggled, "scroll-realtime-weather", rowHeight, SettingsReason()),
                    SwitchRow("Animations", OnAnimationsToggled, "animations", rowHeight, SettingsReason()),
                    SwitchRow("Skip transitions", OnSkipOpeningAnimationToggled, "skip-opening-animation", rowHeight, SettingsReason()),
                    SwitchRow(
                        "Live clouds",
                        OnCloudsToggled,
                        "clouds",
                        rowHeight,
                        !settingsAllowed
                            ? SettingsReasonServerPolicy
                            : cloudsAvailable ? null : SettingsReasonVolumetricClouds
                    ),
                    SwitchRow("Live sun", OnLiveLightingToggled, "live-lighting", rowHeight, SettingsReason()),
                    SliderRow("Fixed hour (0–24)", OnFixedSunHourChanged, "fixed-sun-hour", rowHeight, SettingsReason())
                }
            ),
            new(
                "Entities",
                new List<AtlasSettingsRow>
                {
                    SwitchRow(
                        "Living models",
                        OnLivingEntitiesToggled,
                        "entities",
                        rowHeight,
                        !settingsAllowed || !serverAllowsAny
                            ? SettingsReasonServerPolicy
                            : null
                    ),
                    SwitchRow("Players", OnPlayersToggled, "players", rowHeight, CategoryReason(serverPolicy.ShowPlayers)),
                    SwitchRow("Animals", OnAnimalsToggled, "animals", rowHeight, CategoryReason(serverPolicy.ShowAnimals)),
                    SwitchRow("Hostile mobs", OnMobsToggled, "mobs", rowHeight, CategoryReason(serverPolicy.ShowMobs)),
                    SwitchRow("NPCs", OnNpcsToggled, "npcs", rowHeight, CategoryReason(serverPolicy.ShowNpcs))
                }
            ),
            new(
                "Safety",
                new List<AtlasSettingsRow>
                {
                    SwitchRow(
                        "Close atlas when taking damage",
                        OnCloseAtlasOnDamageToggled,
                        "close-on-damage",
                        rowHeight,
                        SettingsReason()
                    )
                }
            ),
            new(
                "Advanced",
                new List<AtlasSettingsRow>
                {
                    ButtonRow("Performance", OpenPerformanceModal, "performance-open", rowHeight, SettingsReason()),
                    ButtonRow("Visual lab", OpenVisualLab, "visual-lab-open", rowHeight, SettingsReason()),
                    ButtonRow(
                        "Creative / Cheat",
                        OpenCreativeSettingsModal,
                        "creative-settings-button",
                        rowHeight,
                        !settingsAllowed
                            ? SettingsReasonServerPolicy
                            : access ? null : SettingsReasonCreativeOnly
                    )
                }
            )
        };
    }

    private static AtlasSettingsRow SwitchRow(
        string label,
        Action<bool> callback,
        string key,
        double rowHeight,
        string? reason = null
    ) => new(
        key,
        rowHeight + (reason == null ? 0 : SettingsReasonHeight),
        (composer, x, y, width) =>
        {
            double switchWidth = Math.Clamp(width * 0.24, 40, 50);
            double switchHeight = Math.Min(26, rowHeight);
            composer
                .AddStaticText(
                    label,
                    AtlasUiStyle.DetailFont(10),
                    ElementBounds.Fixed(x, y + 3, Math.Max(50, width - switchWidth - 6), rowHeight - 4)
                )
                .AddAtlasSwitch(
                    callback,
                    ElementBounds.Fixed(
                        x + width - switchWidth,
                        y + (rowHeight - switchHeight) * 0.5,
                        switchWidth,
                        switchHeight
                    ),
                    key
                );
            AddSettingsReason(composer, reason, x, y + rowHeight - 2, width);
        }
    );

    private static AtlasSettingsRow SliderRow(
        string label,
        ActionConsumable<int> callback,
        string key,
        double rowHeight,
        string? reason = null
    ) => new(
        key,
        rowHeight + (reason == null ? 0 : SettingsReasonHeight),
        (composer, x, y, width) =>
        {
            double labelWidth = Math.Max(58, width * 0.44);
            composer
                .AddStaticText(
                    label,
                    AtlasUiStyle.DetailFont(10),
                    ElementBounds.Fixed(x, y + 3, labelWidth - 4, rowHeight - 4)
                )
                .AddAtlasSlider(
                    callback,
                    ElementBounds.Fixed(x + labelWidth, y, Math.Max(60, width - labelWidth), rowHeight),
                    key
                );
            AddSettingsReason(composer, reason, x, y + rowHeight - 2, width);
        }
    );

    private static AtlasSettingsRow ButtonRow(
        string label,
        ActionConsumable callback,
        string key,
        double rowHeight,
        string? reason = null
    ) => new(
        key,
        rowHeight + (reason == null ? 0 : SettingsReasonHeight),
        (composer, x, y, width) =>
        {
            composer.AddAtlasButton(
                label,
                callback,
                ElementBounds.Fixed(x, y, width, rowHeight),
                key,
                AtlasButtonStyle.Compact
            );
            AddSettingsReason(composer, reason, x, y + rowHeight - 2, width);
        }
    );

    /// <summary>
    /// A disabled control has to say why, otherwise it reads as broken.
    /// </summary>
    private static void AddSettingsReason(
        GuiComposer composer,
        string? reason,
        double x,
        double y,
        double width
    )
    {
        if (reason == null) return;
        composer.AddStaticText(
            reason,
            AtlasUiStyle.DetailFont(8),
            ElementBounds.Fixed(x, y, Math.Max(50, width), SettingsReasonHeight)
        );
    }

    /// <summary>
    /// Flows the sections into columns, keeping their order and filling the
    /// shortest column first, then reports what that layout occupies.
    /// </summary>
    private void MeasureSettingsLayout(
        double width,
        double height,
        out List<AtlasSettingsSection>[] columns,
        out double columnWidth,
        out double columnStride,
        out double contentHeight,
        out double visibleHeight
    )
    {
        int columnCount = SettingsColumnCount(width);
        double usable = Math.Max(
            120,
            width - SettingsSidePadding * 2 - SettingsScrollbarGutter
        );
        // A column wider than this only pushes a switch far away from its own
        // label; the leftover space becomes spacing between columns instead.
        columnWidth = Math.Clamp(
            (usable - SettingsColumnGap * (columnCount - 1)) / columnCount,
            110,
            300
        );
        columnStride = columnCount > 1
            ? Math.Max(
                columnWidth + SettingsColumnGap,
                (usable - columnWidth) / (columnCount - 1)
            )
            : columnWidth;
        columns = new List<AtlasSettingsSection>[columnCount];
        var columnHeights = new double[columnCount];
        for (int index = 0; index < columnCount; index++)
        {
            columns[index] = new List<AtlasSettingsSection>();
        }

        foreach (AtlasSettingsSection section in BuildSettingsSections(SettingsRowHeight(height)))
        {
            int target = 0;
            for (int index = 1; index < columnCount; index++)
            {
                if (columnHeights[index] < columnHeights[target] - 0.01)
                {
                    target = index;
                }
            }
            columns[target].Add(section);
            columnHeights[target] += section.Height;
        }

        contentHeight = 0;
        foreach (double columnHeight in columnHeights)
        {
            contentHeight = Math.Max(contentHeight, columnHeight);
        }
        visibleHeight = Math.Max(24, height - SettingsBodyTop - SettingsFooterHeight);
    }

    private double SettingsScrollMaximum(double width, double height)
    {
        MeasureSettingsLayout(
            width,
            height,
            out _,
            out _,
            out _,
            out double contentHeight,
            out double visibleHeight
        );
        return Math.Max(0, contentHeight - visibleHeight);
    }

    /// <summary>
    /// Vertical extent of every control in panel coordinates, before the
    /// scroll offset is applied. The automated test uses it to prove that the
    /// first and the last control can both be scrolled into view.
    /// </summary>
    private List<(string Key, double Top, double Bottom)> SettingsControlExtents(
        double width,
        double height
    )
    {
        MeasureSettingsLayout(
            width,
            height,
            out List<AtlasSettingsSection>[] columns,
            out _,
            out _,
            out _,
            out _
        );
        var extents = new List<(string Key, double Top, double Bottom)>();
        foreach (List<AtlasSettingsSection> column in columns)
        {
            double y = SettingsBodyTop;
            foreach (AtlasSettingsSection section in column)
            {
                y += SettingsSectionHeaderHeight;
                foreach (AtlasSettingsRow row in section.Rows)
                {
                    extents.Add((row.Key, y, y + row.Height));
                    y += row.Height + SettingsRowGap;
                }
                y += SettingsSectionGap;
            }
        }
        return extents;
    }

    /// <summary>
    /// True when every control can be brought fully inside the panel body by
    /// scrolling within the available range.
    /// </summary>
    private bool SettingsControlsReachable(double width, double height, out string? failure)
    {
        failure = null;
        double maximum = SettingsScrollMaximum(width, height);
        MeasureSettingsLayout(width, height, out _, out _, out _, out _, out double visibleHeight);
        foreach ((string key, double top, double bottom) in SettingsControlExtents(width, height))
        {
            double offset = Math.Clamp(bottom - SettingsBodyTop - visibleHeight, 0, maximum);
            double visibleTop = top - offset;
            double visibleBottom = bottom - offset;
            if (visibleTop >= SettingsBodyTop - 0.5
                && visibleBottom <= SettingsBodyTop + visibleHeight + 0.5)
            {
                continue;
            }
            failure = FormattableString.Invariant(
                $"{key} stays out of view at {width:0}x{height:0}: rows {top:0}-{bottom:0}, offset {offset:0}, body {SettingsBodyTop:0}+{visibleHeight:0}, maximum {maximum:0}"
            );
            return false;
        }
        return true;
    }

    private bool IsSettingsScrollbarPoint(int x, int y)
    {
        if (bottomPanelSection != AtlasPanelSection.Settings
            || bottomPanelProgress <= 0.01f)
        {
            return false;
        }
        if (!PanelCoversPoint(x, y)) return false;
        return bottomPanel?.GetScrollbar(SettingsScrollbarKey)?.Bounds.PointInside(x, y)
            == true;
    }

    private void ComposeSettingsBottomPanel(
        GuiComposer composer,
        double width,
        double height
    )
    {
        MeasureSettingsLayout(
            width,
            height,
            out List<AtlasSettingsSection>[] columns,
            out double columnWidth,
            out double columnStride,
            out double contentHeight,
            out double visibleHeight
        );
        double maximum = Math.Max(0, contentHeight - visibleHeight);
        double offset = Math.Clamp(settingsScrollOffset, 0, maximum);

        // Content first: the header band is added afterwards so it paints over
        // rows that scrolled up behind it. The panel clips at its own edge but
        // has no per-element clip, so this ordering is what keeps the header
        // readable while the body scrolls.
        for (int index = 0; index < columns.Length; index++)
        {
            double x = SettingsSidePadding + index * columnStride;
            double y = SettingsBodyTop - offset;
            foreach (AtlasSettingsSection section in columns[index])
            {
                composer.AddStaticText(
                    section.Title.ToUpperInvariant(),
                    AtlasUiStyle.LabelFont(9),
                    ElementBounds.Fixed(x, y, columnWidth, SettingsSectionHeaderHeight - 2)
                );
                y += SettingsSectionHeaderHeight;
                foreach (AtlasSettingsRow row in section.Rows)
                {
                    row.Place(composer, x, y, columnWidth);
                    y += row.Height + SettingsRowGap;
                }
                y += SettingsSectionGap;
            }
        }

        // The band has to be an interactive custom draw added after the rows:
        // switches and buttons are painted in the interactive pass, so a
        // static strip could never cover a control scrolled up behind it. Its
        // caption is drawn inside the same pass for the same reason.
        string scrollHint = maximum > 0 ? "Scroll for more controls" : "";
        composer
            .AddDynamicCustomDraw(
                ElementBounds.Fixed(0, 0, width, SettingsBodyTop - 4),
                (context, surface, bounds) =>
                    DrawSettingsHeaderBand(context, bounds, scrollHint),
                "settings-header"
            )
            .AddAtlasButton(
                "×",
                CloseSettingsModal,
                ElementBounds.Fixed(width - 42, 7, 32, 28),
                "settings-close",
                AtlasButtonStyle.Icon
            );

        if (maximum <= 0) return;

        // GuiElementScrollbar is the native proportional track/thumb. Use the
        // full track variant here rather than the compact presentation so the
        // affordance is obvious in this otherwise dense panel. Configure this
        // interactive element before the composer is composed; its SetHeights
        // call emits an initial callback, which must not reset the scroll
        // position we are restoring here.
        bool configuringScrollbar = true;
        composer.AddVerticalScrollbar(
            value =>
            {
                if (configuringScrollbar) return;
                double nextOffset = Math.Clamp(value, 0, maximum);
                if (Math.Abs(settingsScrollOffset - nextOffset) < 0.01) return;
                settingsScrollOffset = nextOffset;
                // Keep the native scrollbar's mouse capture until MouseUp.
                // Rebuilding the composer during a drag would dispose the
                // element that owns that capture; wheel recomposes immediately,
                // while track clicks and drags recompose after release.
                if (!settingsScrollbarPointerDown)
                {
                    pendingInterfaceRecompose = true;
                }
            },
            ElementBounds.Fixed(
                width - SettingsSidePadding - SettingsScrollbarWidth,
                SettingsBodyTop,
                SettingsScrollbarWidth,
                visibleHeight
            ),
            SettingsScrollbarKey
        );

        GuiElementScrollbar? scrollbar = composer.GetScrollbar(SettingsScrollbarKey);
        if (scrollbar == null)
        {
            configuringScrollbar = false;
            return;
        }

        try
        {
            scrollbar.SetHeights((float)visibleHeight, (float)contentHeight);
            scrollbar.CurrentYPosition = (float)offset;
        }
        finally
        {
            configuringScrollbar = false;
        }
    }

    /// <summary>
    /// Solid strip carrying the panel title and the scroll hint. Scrolling
    /// content passes under it, so it is opaque and it draws its own text.
    /// </summary>
    private static void DrawSettingsHeaderBand(
        Context context,
        ElementBounds bounds,
        string scrollHint
    )
    {
        if (bounds.InnerWidth <= 0 || bounds.InnerHeight <= 0) return;

        context.Rectangle(bounds.drawX, bounds.drawY, bounds.InnerWidth, bounds.InnerHeight);
        context.SetSourceRGBA(0.043, 0.055, 0.068, 0.99);
        context.Fill();
        context.MoveTo(bounds.drawX, bounds.drawY + bounds.InnerHeight);
        context.LineTo(bounds.drawX + bounds.InnerWidth, bounds.drawY + bounds.InnerHeight);
        context.SetSourceRGBA(1, 1, 1, 0.14);
        context.LineWidth = Math.Max(1, RuntimeEnv.GUIScale * 0.7);
        context.Stroke();

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        using (CairoFont title = AtlasUiStyle.TitleFont(15))
        {
            title.SetupContext(context);
            TextExtents extents = title.GetTextExtents("SETTINGS");
            context.MoveTo(
                bounds.drawX + SettingsSidePadding * scale - extents.XBearing,
                bounds.drawY + (bounds.InnerHeight - extents.Height) * 0.5 - extents.YBearing
            );
            context.ShowText("SETTINGS");
        }
        if (string.IsNullOrEmpty(scrollHint)) return;

        using CairoFont hint = AtlasUiStyle.DetailFont(9);
        hint.SetupContext(context);
        TextExtents hintExtents = hint.GetTextExtents(scrollHint);
        context.MoveTo(
            bounds.drawX + bounds.InnerWidth - 46 * scale - hintExtents.Width - hintExtents.XBearing,
            bounds.drawY + (bounds.InnerHeight - hintExtents.Height) * 0.5 - hintExtents.YBearing
        );
        context.ShowText(scrollHint);
    }
}
