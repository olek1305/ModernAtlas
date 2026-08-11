using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ModernAtlas;

/// <summary>
/// Isolates the Vintage Story game-content behavior that owns the player's
/// composed skin texture. The base Seraph texture is only a pale tint mask;
/// the clothing behavior's texture source maps that key to the current
/// composed player appearance in the entity atlas.
/// </summary>
internal static class PlayerSkinTextureAdapter
{
    private static readonly PropertyInfo? BehaviorSkinPositionProperty =
        typeof(EntityBehaviorTexturedClothing).GetProperty(
            "skinTexPos",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );
    private static readonly FieldInfo? RendererSkinPositionField =
        typeof(EntityShapeRenderer).GetField(
            "skinTexPos",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );
    private static readonly MethodInfo? RendererTextureSourceMethod =
        typeof(EntityShapeRenderer).GetMethod(
            "GetTextureSource",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );
    private static readonly FieldInfo? RendererColorField =
        typeof(EntityShapeRenderer).GetField(
            "color",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

    public static bool TryGet(
        EntityPlayer player,
        out ITexPositionSource? textureSource,
        out int textureId,
        out Vec4f color
    )
    {
        textureSource = null;
        textureId = 0;
        color = new Vec4f(1f, 1f, 1f, 1f);

        EntityBehaviorTexturedClothing? behavior =
            player.GetBehavior<EntityBehaviorTexturedClothing>();
        if (behavior != null)
        {
            EnumHandling handling = EnumHandling.PassThrough;
            ITexPositionSource? behaviorTextureSource =
                behavior.GetTextureSource(ref handling);
            TextureAtlasPosition? skinPosition =
                BehaviorSkinPositionProperty?.GetValue(behavior)
                    as TextureAtlasPosition;
            if (behaviorTextureSource != null && skinPosition != null)
            {
                textureSource = new ComposedSkinTextureSource(
                    behaviorTextureSource,
                    skinPosition
                );
            }
            textureId = skinPosition?.atlasTextureId ?? 0;
            if (textureSource != null && textureId > 0) return true;
        }

        EntityRenderer? renderer = player.Properties.Client.Renderer;
        if (renderer is EntityShapeRenderer shapeRenderer)
        {
            textureSource = RendererTextureSourceMethod?.Invoke(
                shapeRenderer,
                null
            ) as ITexPositionSource;
            TextureAtlasPosition? rendererSkinPosition =
                RendererSkinPositionField?.GetValue(shapeRenderer)
                    as TextureAtlasPosition;
            textureId = rendererSkinPosition?.atlasTextureId ?? 0;
            if (RendererColorField?.GetValue(shapeRenderer) is Vec4f rendererColor)
            {
                color = rendererColor.Clone();
            }
            if (textureSource != null && textureId > 0) return true;
        }
        return false;
    }

    private sealed class ComposedSkinTextureSource : ITexPositionSource
    {
        private readonly ITexPositionSource fallback;
        private readonly TextureAtlasPosition skinPosition;

        public ComposedSkinTextureSource(
            ITexPositionSource fallback,
            TextureAtlasPosition skinPosition
        )
        {
            this.fallback = fallback;
            this.skinPosition = skinPosition;
        }

        public TextureAtlasPosition this[string textureCode] =>
            string.Equals(textureCode, "seraph", StringComparison.OrdinalIgnoreCase)
                ? skinPosition
                : fallback[textureCode] ?? skinPosition;

        public Size2i AtlasSize => fallback.AtlasSize!;
    }
}
