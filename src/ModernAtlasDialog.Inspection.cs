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
            return;
        }

        Entity entity = selected.Value.Entity;
        string name = AtlasSafeDisplayName.ForEntity(entity);

        double dx = entity.Pos.X - capi.World.Player.Entity.Pos.X;
        double dy = entity.Pos.Y - capi.World.Player.Entity.Pos.Y;
        double dz = entity.Pos.Z - capi.World.Player.Entity.Pos.Z;
        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        string health = AtlasEntityInspectionAdapter.TryGetHealth(
            entity,
            out float currentHealth,
            out float maximumHealth
        )
            ? FormattableString.Invariant(
                $"Health: {currentHealth:0.#} / {maximumHealth:0.#} ({Math.Clamp(currentHealth / maximumHealth * 100f, 0, 100):0}%)"
            )
            : "Health: unavailable";
        string category = selected.Value.Kind switch
        {
            AtlasEntityKind.Player => "Player",
            AtlasEntityKind.Animal => "Animal",
            AtlasEntityKind.Mob => "Hostile mob",
            AtlasEntityKind.Npc => "NPC",
            _ => "Living entity"
        };
        string details = FormattableString.Invariant(
            $"{health}\nCategory: {category}\nType: {entity.Code}\nEntity ID: {entity.EntityId}\nPosition: {entity.Pos.X:0.0}, {entity.Pos.Y:0.0}, {entity.Pos.Z:0.0}\nDistance: {distance:0.0} blocks"
        );

        unitPanel?.GetDynamicText("unit-name").SetNewText(name);
        unitPanel?.GetDynamicText("unit-details").SetNewText(details);
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
