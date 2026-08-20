using System;
using System.Collections;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Draws a bounded atlas-only cloud volume from the live cloud map prepared by
/// VSEssentials. The normal volumetric renderer can be hundreds of blocks
/// thick; viewed from the atlas eye that becomes a wall instead of readable
/// cloud cover. This bridge preserves its X/Z weather pattern and motion while
/// keeping the atlas presentation in a short, stable layer above the world.
/// </summary>
internal sealed class VolumetricCloudRendererAdapter : IDisposable
{
    private const string RendererTypeName = "FluffyClouds.CloudRendererVolumetric";
    private const string MapRendererTypeName = "FluffyClouds.CloudRendererMap";
    private const int SimpleCloudRenderMode = 2;
    private const int VolumetricCloudRenderMode = 1;

    private readonly ICoreClientAPI capi;
    private readonly object map;
    private readonly Func<IShaderProgram?> shaderProvider;
    private readonly MeshRef quad;
    private readonly MethodInfo tickCloudMap;
    private readonly FieldInfo textureMapField;
    private readonly FieldInfo textureColorField;
    private readonly FieldInfo offsetField;
    private readonly FieldInfo cloudTileLengthField;
    private readonly FieldInfo renderCloudMapField;
    private readonly bool originalRenderCloudMap;
    private bool disabled;
    private bool loggedSuccess;
    private bool loggedUnsupportedMode;

    private VolumetricCloudRendererAdapter(
        ICoreClientAPI capi,
        object map,
        Func<IShaderProgram?> shaderProvider,
        MeshRef quad,
        MethodInfo tickCloudMap,
        FieldInfo textureMapField,
        FieldInfo textureColorField,
        FieldInfo offsetField,
        FieldInfo cloudTileLengthField,
        FieldInfo renderCloudMapField,
        bool originalRenderCloudMap
    )
    {
        this.capi = capi;
        this.map = map;
        this.shaderProvider = shaderProvider;
        this.quad = quad;
        this.tickCloudMap = tickCloudMap;
        this.textureMapField = textureMapField;
        this.textureColorField = textureColorField;
        this.offsetField = offsetField;
        this.cloudTileLengthField = cloudTileLengthField;
        this.renderCloudMapField = renderCloudMapField;
        this.originalRenderCloudMap = originalRenderCloudMap;
    }

    public static VolumetricCloudRendererAdapter? TryCreate(
        ICoreClientAPI capi,
        object game,
        Func<IShaderProgram?> shaderProvider
    )
    {
        if (!IsEnabledByGraphicsSettings(capi))
        {
            capi.Logger.Notification(
                "[ModernAtlas] Live atlas clouds are unavailable because Vintage Story clouds are Off."
            );
            return null;
        }
        object? activatedMap = null;
        FieldInfo? activatedMapField = null;
        bool originalRenderCloudMap = false;
        try
        {
            object map;
            try
            {
                // In 1.22.6 the texture-producing cloud map is its own render
                // handler. It remains the authoritative live weather source;
                // the volumetric handler merely consumes it for the normal
                // camera and is not guaranteed to be registered yet.
                map = FindRegisteredRenderer(game, MapRendererTypeName);
            }
            catch (InvalidOperationException)
            {
                object renderer = FindRegisteredRenderer(game, RendererTypeName);
                map = RequireField(renderer.GetType(), "map").GetValue(renderer)
                    ?? throw new InvalidOperationException("The live cloud map is unavailable.");
            }
            FieldInfo renderCloudMapField = RequireField(map.GetType(), "renderCloudMap");
            originalRenderCloudMap = (bool)(renderCloudMapField.GetValue(map) ?? false);
            MethodInfo tickCloudMap = RequireMethod(map.GetType(), "CloudTick", typeof(float));
            FieldInfo textureMapField = RequireField(map.GetType(), "TextureMap");
            FieldInfo textureColorField = RequireField(map.GetType(), "TextureCol");
            FieldInfo offsetField = RequireField(map.GetType(), "offset");
            FieldInfo cloudTileLengthField = RequireField(map.GetType(), "CloudTileLength");
            // Simple clouds use the same live tile state but normally skip the
            // two GPU map textures consumed by the volumetric renderer. Keep
            // those textures active for the atlas bridge and restore the
            // engine field when the world renderer is released.
            renderCloudMapField.SetValue(map, true);
            activatedMap = map;
            activatedMapField = renderCloudMapField;
            MeshRef quad = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());

            VolumetricCloudRendererAdapter adapter = new(
                capi,
                map,
                shaderProvider,
                quad,
                tickCloudMap,
                textureMapField,
                textureColorField,
                offsetField,
                cloudTileLengthField,
                renderCloudMapField,
                originalRenderCloudMap
            );
            capi.Logger.Notification(
                "[ModernAtlas] Vintage Story live cloud map is available for the bounded atlas 3D cloud layer."
            );
            return adapter;
        }
        catch (Exception exception)
        {
            activatedMapField?.SetValue(activatedMap, originalRenderCloudMap);
            capi.Logger.Warning(
                "[ModernAtlas] Live cloud-map overlay is unavailable: {0}",
                exception.Message
            );
            return null;
        }
    }

    internal static bool IsEnabledByGraphicsSettings(ICoreClientAPI capi) =>
        IsSupportedCloudMode(capi.Settings.Int["cloudRenderMode"]);

    /// <summary>The current live wind-drift offset of the native cloud map.</summary>
    public Vec3f? GetLiveOffset()
    {
        if (disabled) return null;
        try
        {
            return (Vec3f?)offsetField.GetValue(map);
        }
        catch
        {
            return null;
        }
    }

    public bool Render(
        float[] projection,
        double[] view,
        float pausedAnimationDeltaTime,
        bool renderIntoPrimary = false,
        Vec3f? frozenOffset = null,
        Vec3f? atlasLightColor = null,
        float atlasExposure = 1f
    )
    {
        if (disabled) return false;
        if (!IsSupportedCloudMode(capi.Settings.Int["cloudRenderMode"]))
        {
            if (!loggedUnsupportedMode)
            {
                loggedUnsupportedMode = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Atlas clouds are enabled, but game clouds are Off; the atlas respects that graphics setting."
                );
            }
            return false;
        }

        IShaderProgram? shader = shaderProvider();
        if (shader == null || shader.Disposed) return false;

        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        try
        {
            // During a singleplayer pause only the already generated cloud
            // map's wind offset is advanced. Regenerating TextureMap here
            // replaces weather tiles every few seconds, which is perceived as
            // pixels popping in and out. The native snapshot stays fixed while
            // its whole shape moves smoothly, with no undulation clock.
            if (pausedAnimationDeltaTime > 0)
            {
                tickCloudMap.Invoke(map, new object[] { pausedAnimationDeltaTime });
            }

            int textureMap = (int)(textureMapField.GetValue(map) ?? 0);
            int textureColor = (int)(textureColorField.GetValue(map) ?? 0);
            int cloudMapWidth = (int)(cloudTileLengthField.GetValue(map) ?? 0);
            if (textureMap <= 0 || textureColor <= 0 || cloudMapWidth <= 0) return false;
            // A frozen offset (tiled screenshot capture) pins the drifting
            // cloud layer to one captured frame so the stitched tiles share
            // identical cloud shapes at every seam.
            Vec3f offset = frozenOffset ?? (Vec3f)(offsetField.GetValue(map)
                ?? throw new InvalidOperationException("The live cloud offset is unavailable."));

            FrameBufferRef primary = render.FrameBuffers[(int)EnumFrameBuffer.Primary];
            Vec3d playerCamera = capi.World.Player.Entity.CameraPos;
            float cloudBaseWorldY = Math.Max(
                offset.Y + (float)playerCamera.Y,
                capi.World.BlockAccessor.MapSizeY + 24f
            );

            render.CurrentActiveShader?.Stop();
            render.CurrentFrameBuffer = renderIntoPrimary ? primary : null;
            render.GLDisableDepthTest();
            render.GLDepthMask(false);
            render.GlToggleBlend(true, EnumBlendMode.Standard);
            shader.Use();
            shader.UniformMatrix("projectionMatrix", projection);
            shader.UniformMatrix("modelViewMatrix", Array.ConvertAll(view, value => (float)value));
            shader.Uniform("cloudOffset", offset);
            shader.Uniform("cloudBaseY", cloudBaseWorldY - (float)playerCamera.Y);
            shader.Uniform("cloudThickness", 64f);
            shader.Uniform("cloudMapWidth", (float)cloudMapWidth);
            Vec3f safeLightColor = atlasLightColor ?? new Vec3f(1f, 1f, 1f);
            shader.Uniform(
                "atlasCloudLightColor",
                safeLightColor.X,
                safeLightColor.Y,
                safeLightColor.Z
            );
            shader.Uniform(
                "atlasCloudExposure",
                Math.Clamp(atlasExposure, 0.04f, 1.5f)
            );
            shader.Uniform(
                "depthScale",
                renderIntoPrimary
                    ? 1f
                    : primary.Width / (float)Math.Max(1, render.FrameWidth),
                renderIntoPrimary
                    ? 1f
                    : primary.Height / (float)Math.Max(1, render.FrameHeight)
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
                    "[ModernAtlas] Rendering a bounded 3D cloud layer from the game's native weather map and world coordinates."
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
        finally
        {
            // Cloud blending is atlas-owned. Restore the target and program
            // even when a weather texture or mesh draw fails.
            // The caller owns the subsequent Primary/boundary handoff.
            renderState.RestoreCapturedState();
        }
    }

    public void Dispose()
    {
        renderCloudMapField.SetValue(map, originalRenderCloudMap);
        capi.Render.DeleteMesh(quad);
    }

    private static bool IsSupportedCloudMode(int mode) =>
        mode == VolumetricCloudRenderMode || mode == SimpleCloudRenderMode;

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
