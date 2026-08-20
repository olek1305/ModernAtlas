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
/// Atlas unit inspection: entity picking from the rendered atlas frame and
/// the public detail panel shown for the selected living model.
/// </summary>
public sealed partial class ModernAtlasDialog
{
    private bool CloseUnitInspection()
    {
        selectedEntityId = null;
        if (bottomPanelSection == AtlasPanelSection.Unit)
        {
            RequestBottomPanel(AtlasPanelSection.None);
        }
        return true;
    }

    private void RenderUnitInspection(float deltaTime)
    {
        UpdateUnitInspectionText();
        if (unitPanel != null) unitPanel.Render(deltaTime);
    }

    private void UpdateUnitInspectionText()
    {
        if (selectedEntityId == null) return;
        if (!UnitInspectionEnabled || exactChunkRenderer == null)
        {
            selectedEntityId = null;
            return;
        }

        // Only a model the atlas is allowed to draw and actually drew this
        // frame may be inspected; the selection drops as soon as it leaves.
        AtlasRenderedEntity? selected = null;
        foreach (AtlasRenderedEntity candidate in exactChunkRenderer.LastRenderedEntities)
        {
            if (candidate.Entity.EntityId == selectedEntityId.Value)
            {
                selected = candidate;
                break;
            }
        }
        if (selected == null)
        {
            selectedEntityId = null;
            unitHealthReading = default;
            return;
        }

        Entity entity = selected.Value.Entity;
        // AtlasSafeDisplayName deliberately avoids Entity.GetName and the
        // global translation service, which is not safe during world startup.
        string name = AtlasSafeDisplayName.ForEntity(entity);

        double dx = entity.Pos.X - capi.World.Player.Entity.Pos.X;
        double dy = entity.Pos.Y - capi.World.Player.Entity.Pos.Y;
        double dz = entity.Pos.Z - capi.World.Player.Entity.Pos.Z;
        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        bool hasHealth = AtlasEntityInspectionAdapter.TryGetHealth(
            entity,
            out float currentHealth,
            out float maximumHealth
        );
        unitHealthReading = AtlasUnitHealth.Resolve(
            hasHealth,
            currentHealth,
            maximumHealth
        );
        string category = selected.Value.Kind switch
        {
            AtlasEntityKind.Player => "Player",
            AtlasEntityKind.Animal => "Animal",
            AtlasEntityKind.Mob => "Hostile mob",
            AtlasEntityKind.Npc => "NPC",
            _ => "Living entity"
        };

        // Fixed row order, so the same fact is always in the same place.
        string[] labels = UnitRowLabels.Split('\n');
        string[] values = FormattableString.Invariant(
            $"{category}\n{unitHealthReading.Text}\n{entity.Pos.X:0.0}, {entity.Pos.Y:0.0}, {entity.Pos.Z:0.0}\n{distance:0.0} blocks\n{entity.Code}\n{entity.EntityId}"
        ).Split('\n');

        int firstColumn = unitRowsSplit ? (labels.Length + 1) / 2 : labels.Length;
        unitPanel?.GetDynamicText("unit-name").SetNewText(name);
        unitPanel?.GetDynamicText("unit-labels")?.SetNewText(
            string.Join('\n', labels[..firstColumn])
        );
        unitPanel?.GetDynamicText("unit-details").SetNewText(
            string.Join('\n', values[..firstColumn])
        );
        unitPanel?.GetDynamicText("unit-labels-2")?.SetNewText(
            string.Join('\n', labels[firstColumn..])
        );
        unitPanel?.GetDynamicText("unit-details-2")?.SetNewText(
            string.Join('\n', values[firstColumn..])
        );
    }

    /// <summary>
    /// Row order of the unit panel. Category, Health and Position lead; the
    /// rest are public identifiers that disclose nothing the atlas is not
    /// already drawing.
    /// </summary>
    internal const string UnitRowLabels =
        "Category\nHealth\nPosition\nDistance\nType\nEntity ID";

    /// <summary>
    /// Draws the health bar over the unit panel: a filled track plus the
    /// current/maximum caption, so the state never depends on color alone.
    /// </summary>
    private void EnsureUnitHealthBarTexture(double panelWidth)
    {
        string key = FormattableString.Invariant(
            $"{panelWidth:0}|{unitHealthReading.Available}|{unitHealthReading.Fraction:0.###}|{unitHealthReading.Text}"
        );
        if (unitHealthBarTexture is { TextureId: > 0 }
            && string.Equals(unitHealthBarSignature, key, StringComparison.Ordinal))
        {
            return;
        }

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        int width = Math.Max(1, (int)Math.Ceiling(panelWidth * scale));
        int height = Math.Max(1, (int)Math.Ceiling(UnitHealthBarHeight * scale));
        unitHealthBarTexture ??= new LoadedTexture(capi);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        AtlasUiStyle.Clear(context);

        double radius = height * 0.5;
        AtlasUiStyle.RoundedRectangle(context, 0, 0, width, height, radius);
        context.SetSourceRGBA(1, 1, 1, 0.16);
        context.Fill();
        if (unitHealthReading.Available && unitHealthReading.Fraction > 0)
        {
            double filled = Math.Max(height, width * unitHealthReading.Fraction);
            AtlasUiStyle.RoundedRectangle(context, 0, 0, filled, height, radius);
            // Green above half, amber below, red when nearly gone - always
            // together with the caption below.
            (double red, double green, double blue) = unitHealthReading.Fraction switch
            {
                >= 0.5f => (0.36, 0.85, 0.44),
                >= 0.25f => (0.95, 0.76, 0.28),
                _ => (0.94, 0.36, 0.30)
            };
            context.SetSourceRGBA(red, green, blue, 0.92);
            context.Fill();
        }

        context.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
        context.SetFontSize(9.5 * scale);
        string caption = unitHealthReading.Available
            ? unitHealthReading.Text
            : "Health unavailable";
        TextExtents extents = context.TextExtents(caption);
        context.SetSourceRGBA(0.06, 0.08, 0.09, 0.92);
        context.MoveTo(8 * scale + scale, height * 0.5 + extents.Height * 0.5 + scale);
        context.ShowText(caption);
        context.SetSourceRGBA(0.99, 0.99, 0.99, 0.96);
        context.MoveTo(8 * scale, height * 0.5 + extents.Height * 0.5);
        context.ShowText(caption);

        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref unitHealthBarTexture);
        unitHealthBarSignature = key;
    }

    /// <summary>
    /// Bar placement inside the unit panel, shared with the automated test.
    /// </summary>
    private bool TryResolveUnitHealthBarPlacement(
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
        if (bottomPanelSection != AtlasPanelSection.Unit
            || selectedEntityId == null
            || !hasBottomPanelGeometry)
        {
            return false;
        }

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double contentWidth = Math.Clamp(
            bottomPanelGeometry.Width / scale - 32,
            60,
            UnitHealthBarMaximumWidth
        );
        EnsureUnitHealthBarTexture(contentWidth);
        if (unitHealthBarTexture is not { TextureId: > 0 }) return false;

        x = (float)(bottomPanelGeometry.X + 16 * scale);
        y = (float)(bottomPanelGeometry.Y + UnitHealthBarTop * scale);
        width = unitHealthBarTexture.Width;
        height = unitHealthBarTexture.Height;
        return true;
    }

    private void RenderUnitHealthBar()
    {
        if (interfaceHidden) return;
        if (!TryResolveUnitHealthBarPlacement(
            out float x,
            out float y,
            out float width,
            out float height
        ))
        {
            return;
        }
        capi.Render.Render2DTexture(
            unitHealthBarTexture!.TextureId,
            x,
            y,
            width,
            height,
            52,
            ColorUtil.WhiteArgbVec
        );
    }

    private bool TrySelectRenderedEntity(int mouseX, int mouseY)
    {
        if (!UnitInspectionEnabled || exactChunkRenderer == null) return false;

        AtlasRenderedEntity? best = null;
        double bestDistanceSquared = double.MaxValue;
        double bestDepth = double.MaxValue;
        double guiScale = Math.Max(0.5, RuntimeEnv.GUIScale);
        foreach (AtlasRenderedEntity candidate in exactChunkRenderer.LastRenderedEntities)
        {
            Entity entity = candidate.Entity;
            if (!entity.Alive) continue;

            float selectionMiddle = (entity.SelectionBox.Y1 + entity.SelectionBox.Y2) * 0.5f;
            if (!TryProjectAtlasPosition(
                entity.Pos.X,
                entity.Pos.Y + selectionMiddle,
                entity.Pos.Z,
                out double screenX,
                out double screenY,
                out double depth
            ))
            {
                continue;
            }

            double modelHeight = Math.Max(0.5, entity.SelectionBox.Y2 - entity.SelectionBox.Y1);
            double pixelsPerBlock = AtlasViewport.Height / (2.0 * zoom);
            double hitRadius = Math.Clamp(
                modelHeight * pixelsPerBlock * 0.5 + 10 * guiScale,
                14 * guiScale,
                36 * guiScale
            );
            double deltaX = mouseX - screenX;
            double deltaY = mouseY - screenY;
            double distanceSquared = deltaX * deltaX + deltaY * deltaY;
            if (distanceSquared > hitRadius * hitRadius
                || distanceSquared > bestDistanceSquared + 0.01
                || (Math.Abs(distanceSquared - bestDistanceSquared) <= 0.01
                    && depth >= bestDepth))
            {
                continue;
            }

            best = candidate;
            bestDistanceSquared = distanceSquared;
            bestDepth = depth;
        }

        if (best == null)
        {
            selectedEntityId = null;
            return false;
        }

        selectedEntityId = best.Value.Entity.EntityId;
        capi.Logger.Notification(
            "[ModernAtlas] Selected loaded {0} entity {1} for atlas inspection.",
            best.Value.Kind,
            selectedEntityId.Value
        );
        RequestBottomPanel(AtlasPanelSection.Unit);
        return true;
    }

    private bool TryProjectAtlasPosition(
        double worldX,
        double worldY,
        double worldZ,
        out double screenX,
        out double screenY,
        out double depth
    )
    {
        double deltaX = worldX - centerX;
        double deltaY = worldY - centerY;
        double deltaZ = worldZ - centerZ;
        double yaw = yawDegrees * GameMath.DEG2RAD;
        double pitch = pitchDegrees * GameMath.DEG2RAD;
        double sinYaw = Math.Sin(yaw);
        double cosYaw = Math.Cos(yaw);
        double sinPitch = Math.Sin(pitch);
        double cosPitch = Math.Cos(pitch);
        double projectedRight = deltaX * cosYaw - deltaZ * sinYaw;
        double projectedUp = -deltaX * sinYaw * sinPitch
            + deltaY * cosPitch
            - deltaZ * cosYaw * sinPitch;
        depth = deltaX * -sinYaw * cosPitch
            + deltaY * -sinPitch
            + deltaZ * -cosYaw * cosPitch;
        AtlasViewportBounds viewport = AtlasViewport;
        double pixelsPerBlock = viewport.Height / (2.0 * zoom);
        screenX = viewport.X + viewport.Width / 2.0
            + projectedRight * pixelsPerBlock;
        screenY = viewport.Y + viewport.Height / 2.0
            - projectedUp * pixelsPerBlock;
        return screenX >= viewport.X
            && screenX <= viewport.Right
            && screenY >= viewport.Y
            && screenY <= viewport.Bottom;
    }

}
