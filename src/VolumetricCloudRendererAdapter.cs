using System;
using System.Collections;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Draws a thin atlas-only projection of the live cloud map prepared by
/// VSEssentials. The normal volumetric renderer can be hundreds of blocks
/// thick; viewed from the atlas eye that becomes a wall instead of readable
/// cloud cover. This bridge preserves its X/Z weather pattern and motion while
/// deliberately flattening only its atlas presentation.
/// </summary>
internal sealed class VolumetricCloudRendererAdapter : IDisposable
{
    private const string RendererTypeName = "FluffyClouds.CloudRendererVolumetric";

    private readonly ICoreClientAPI capi;
    private readonly object map;
    private readonly Func<IShaderProgram?> shaderProvider;
    private readonly MeshRef quad;
    private readonly MethodInfo renderCloudMap;
    private readonly MethodInfo tickCloudMap;
    private readonly FieldInfo textureMapField;
    private readonly FieldInfo textureColorField;
    private readonly FieldInfo offsetField;
    private readonly FieldInfo cloudTileLengthField;
    private bool disabled;
    private bool loggedSuccess;
    private bool loggedUnsupportedMode;

    private VolumetricCloudRendererAdapter(
        ICoreClientAPI capi,
        object map,
        Func<IShaderProgram?> shaderProvider,
        MeshRef quad,
        MethodInfo renderCloudMap,
        MethodInfo tickCloudMap,
        FieldInfo textureMapField,
        FieldInfo textureColorField,
        FieldInfo offsetField,
        FieldInfo cloudTileLengthField
    )
    {
        this.capi = capi;
        this.map = map;
        this.shaderProvider = shaderProvider;
        this.quad = quad;
        this.renderCloudMap = renderCloudMap;
        this.tickCloudMap = tickCloudMap;
        this.textureMapField = textureMapField;
        this.textureColorField = textureColorField;
        this.offsetField = offsetField;
        this.cloudTileLengthField = cloudTileLengthField;
    }

    public static VolumetricCloudRendererAdapter? TryCreate(
        ICoreClientAPI capi,
        object game,
        Func<IShaderProgram?> shaderProvider
    )
    {
        try
        {
            object renderer = FindRegisteredRenderer(game, RendererTypeName);
            object map = RequireField(renderer.GetType(), "map").GetValue(renderer)
                ?? throw new InvalidOperationException("The live cloud map is unavailable.");
            MeshRef quad = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());

            VolumetricCloudRendererAdapter adapter = new(
                capi,
                map,
                shaderProvider,
                quad,
                RequireMethod(map.GetType(), "OnRenderFrame", typeof(float), typeof(EnumRenderStage)),
                RequireMethod(map.GetType(), "CloudTick", typeof(float)),
                RequireField(map.GetType(), "TextureMap"),
                RequireField(map.GetType(), "TextureCol"),
                RequireField(map.GetType(), "offset"),
                RequireField(map.GetType(), "CloudTileLength")
            );
            capi.Logger.Notification(
                "[ModernAtlas] Vintage Story live volumetric cloud map is available for the flattened atlas overlay."
            );
            return adapter;
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Live cloud-map overlay is unavailable: {0}",
                exception.Message
            );
            return null;
        }
    }

    public bool Render(float[] projection, double[] view, float pausedAnimationDeltaTime)
    {
        if (disabled) return false;
        if (capi.Settings.Int["cloudRenderMode"] != 1)
        {
            if (!loggedUnsupportedMode)
            {
                loggedUnsupportedMode = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Atlas clouds are enabled, but the game cloud quality is not Volumetric; the atlas respects that graphics setting."
                );
            }
            return false;
        }

        IShaderProgram? shader = shaderProvider();
        if (shader == null || shader.Disposed) return false;

        try
        {
            // During a singleplayer pause only the cloud wind is advanced.
            // Keeping the map's internal undulation clock fixed avoids the
            // distracting breathing/pulsing seen from a top-down camera.
            if (pausedAnimationDeltaTime > 0)
            {
                tickCloudMap.Invoke(map, new object[] { pausedAnimationDeltaTime });
                renderCloudMap.Invoke(map, new object[] { 0f, EnumRenderStage.Opaque });
            }

            int textureMap = (int)(textureMapField.GetValue(map) ?? 0);
            int textureColor = (int)(textureColorField.GetValue(map) ?? 0);
            int cloudMapWidth = (int)(cloudTileLengthField.GetValue(map) ?? 0);
            if (textureMap <= 0 || textureColor <= 0 || cloudMapWidth <= 0) return false;
            Vec3f offset = (Vec3f)(offsetField.GetValue(map)
                ?? throw new InvalidOperationException("The live cloud offset is unavailable."));

            IRenderAPI render = capi.Render;
            FrameBufferRef primary = render.FrameBuffers[(int)EnumFrameBuffer.Primary];
            Vec3d playerCamera = capi.World.Player.Entity.CameraPos;
            float cloudWorldY = Math.Max(
                offset.Y + (float)playerCamera.Y,
                capi.World.BlockAccessor.MapSizeY + 24f
            );

            render.CurrentActiveShader?.Stop();
            render.CurrentFrameBuffer = null;
            render.GLDisableDepthTest();
            render.GLDepthMask(false);
            render.GlToggleBlend(true, EnumBlendMode.Standard);
            shader.Use();
            shader.UniformMatrix("projectionMatrix", projection);
            shader.UniformMatrix("modelViewMatrix", Array.ConvertAll(view, value => (float)value));
            shader.Uniform("cloudOffset", offset);
            shader.Uniform("cloudPlaneY", cloudWorldY - (float)playerCamera.Y);
            shader.Uniform("cloudMapWidth", (float)cloudMapWidth);
            shader.Uniform(
                "depthScale",
                primary.Width / (float)Math.Max(1, render.FrameWidth),
                primary.Height / (float)Math.Max(1, render.FrameHeight)
            );
            shader.BindTexture2D("depthTex", primary.DepthTextureId, 0);
            shader.BindTexture2D("cloudMap", textureMap, 8);
            shader.BindTexture2D("cloudCol", textureColor, 9);
            render.RenderMesh(quad);
            shader.Stop();
            render.GLDepthMask(true);
            render.GlToggleBlend(false, EnumBlendMode.Standard);

            if (!loggedSuccess)
            {
                loggedSuccess = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Rendering flattened live cloud cover from the game's native weather map and world coordinates."
                );
            }
            return true;
        }
        catch (Exception exception)
        {
            disabled = true;
            Exception cause = exception is TargetInvocationException { InnerException: not null }
                ? exception.InnerException
                : exception;
            capi.Logger.Error(
                "[ModernAtlas] Live cloud-map rendering failed and was disabled for this session: {0}",
                cause.Message
            );
            return false;
        }
    }

    public void Dispose()
    {
        capi.Render.DeleteMesh(quad);
    }

    private static object FindRegisteredRenderer(object game, string fullTypeName)
    {
        object eventManager = RequireField(game.GetType(), "eventManager").GetValue(game)
            ?? throw new InvalidOperationException("Client event manager is unavailable.");
        if (RequireField(eventManager.GetType(), "renderersByStage").GetValue(eventManager)
            is not IEnumerable stages)
        {
            throw new InvalidOperationException("Client render-stage registry is unavailable.");
        }

        foreach (object? stage in stages)
        {
            if (stage is not IEnumerable handlers) continue;
            foreach (object? handler in handlers)
            {
                if (handler == null) continue;
                object? candidate = RequireField(handler.GetType(), "Renderer").GetValue(handler);
                if (candidate?.GetType().FullName == fullTypeName) return candidate;
            }
        }

        throw new InvalidOperationException($"Required engine renderer {fullTypeName} is unavailable.");
    }

    private static FieldInfo RequireField(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
            if (field != null) return field;
        }
        throw new MissingFieldException(type.FullName, name);
    }

    private static MethodInfo RequireMethod(Type type, string name, params Type[] parameterTypes)
    {
        return type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            parameterTypes,
            null
        ) ?? throw new MissingMethodException(type.FullName, name);
    }
}
