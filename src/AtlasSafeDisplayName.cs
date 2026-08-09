using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace ModernAtlas;

/// <summary>
/// Produces atlas labels without entering Vintage Story's global translation
/// service. The survival handbook populates translation diagnostics on a
/// worker thread during world startup, so calling Entity.GetName or
/// ItemStack.GetName concurrently can corrupt the engine's non-concurrent
/// diagnostic collections.
/// </summary>
internal static class AtlasSafeDisplayName
{
    private static readonly string[] CustomNameAttributeKeys =
    {
        "customName",
        "customname",
        "name",
        "nametag"
    };

    public static string ForEntity(Entity entity)
    {
        if (entity is EntityPlayer player)
        {
            try
            {
                string? playerName = player.Player?.PlayerName;
                if (!string.IsNullOrWhiteSpace(playerName)) return playerName;
            }
            catch
            {
                // The player registry may be changing during world teardown.
            }
        }

        foreach (string key in CustomNameAttributeKeys)
        {
            string customName = entity.WatchedAttributes.GetString(key, "");
            if (!string.IsNullOrWhiteSpace(customName)) return customName;
        }

        return HumanizeAssetPath(entity.Code?.Path, "Living entity");
    }

    public static string ForItemStack(ItemStack? stack, string fallback)
    {
        return HumanizeAssetPath(stack?.Collectible?.Code?.Path, fallback);
    }

    private static string HumanizeAssetPath(string? path, string fallback)
    {
        if (string.IsNullOrWhiteSpace(path)) return fallback;

        var builder = new StringBuilder(path.Length);
        bool capitalize = true;
        foreach (char character in path)
        {
            if (character is '-' or '_' or '/')
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }
                capitalize = true;
                continue;
            }

            builder.Append(capitalize
                ? char.ToUpperInvariant(character)
                : character);
            capitalize = false;
        }

        return builder.Length == 0 ? fallback : builder.ToString();
    }
}
