using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace VoxelAtlas;

/// <summary>
/// Registers an additional client-side relief layer with the vanilla map.
/// The vanilla map remains the owner of exploration data and waypoints.
/// </summary>
public sealed class VoxelAtlasSystem : ModSystem
{
    private WorldMapManager? worldMap;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        worldMap = api.ModLoader.GetModSystem<WorldMapManager>();
        RegisterReliefAfterVanillaTerrain(worldMap);

        api.Input.RegisterHotKey(
            "voxelatlas-open",
            Lang.Get("voxelatlas:hotkey-open-map"),
            GlKeys.G,
            HotkeyType.HelpAndOverlays
        );
        api.Input.SetHotKeyHandler("voxelatlas-open", OnOpenMap);

        api.Logger.Notification(
            "[VoxelAtlas] Registered non-destructive relief layer. Vanilla map data remains unchanged."
        );
    }

    private bool OnOpenMap(KeyCombination keyCombination)
    {
        worldMap?.ToggleMap(EnumDialogType.Dialog);
        return true;
    }

    private static void RegisterReliefAfterVanillaTerrain(WorldMapManager manager)
    {
        // WorldMapManager creates layer instances later, during LevelFinalize.
        // Establish the render order now rather than mutating MapLayers while
        // its tick loop is enumerating that collection.
        Dictionary<string, Type> ordered = new();
        foreach (KeyValuePair<string, Type> entry in manager.MapLayerRegistry)
        {
            ordered[entry.Key] = entry.Value;
            if (entry.Key == "chunks")
            {
                ordered["voxelatlasrelief"] = typeof(ReliefMapLayer);
            }
        }

        manager.MapLayerRegistry.Clear();
        foreach (KeyValuePair<string, Type> entry in ordered)
        {
            manager.MapLayerRegistry[entry.Key] = entry.Value;
        }
    }
}
