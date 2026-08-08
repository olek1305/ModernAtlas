using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Vintage Story does not expose its completed terrain GPU meshes through the
/// public API. This small, version-checked adapter reuses the 1.22.6 terrain
/// renderer so connected models, mod blocks, biome colors and engine lighting
/// remain identical to the normal world view. It renders chunk geometry only;
/// entities and particle renderers are never invoked.
/// </summary>
internal sealed class ExactChunkRendererAdapter
{
    private const string SupportedVersion = "1.22.6";

    private readonly ICoreClientAPI capi;
    private readonly object chunkRenderer;
    private readonly object mainCamera;
    private readonly object platform;
    private readonly MethodInfo renderOpaque;
    private readonly MethodInfo renderOit;
    private readonly MethodInfo renderAfterOit;
    private readonly MethodInfo clearFramebuffer;
    private readonly MethodInfo loadFramebuffer;
    private readonly MethodInfo unloadFramebuffer;
    private readonly MethodInfo mergeTransparentRenderPass;
    private readonly MethodInfo blitPrimaryToDefault;
    private readonly FieldInfo offscreenBufferField;
    private readonly FieldInfo cameraMatrixOriginField;
    private readonly FieldInfo poolsByRenderPassField;
    private readonly FieldInfo poolFrustumField;
    private bool disabled;
    private bool transparentPassDisabled;
    private bool loggedSuccess;
    private bool loggedTransparentSuccess;

    private ExactChunkRendererAdapter(
        ICoreClientAPI capi,
        object chunkRenderer,
        object mainCamera,
        object platform,
        MethodInfo renderOpaque,
        MethodInfo renderOit,
        MethodInfo renderAfterOit,
        MethodInfo clearFramebuffer,
        MethodInfo loadFramebuffer,
        MethodInfo unloadFramebuffer,
        MethodInfo mergeTransparentRenderPass,
        MethodInfo blitPrimaryToDefault,
        FieldInfo offscreenBufferField,
        FieldInfo cameraMatrixOriginField,
        FieldInfo poolsByRenderPassField,
        FieldInfo poolFrustumField
    )
    {
        this.capi = capi;
        this.chunkRenderer = chunkRenderer;
        this.mainCamera = mainCamera;
        this.platform = platform;
        this.renderOpaque = renderOpaque;
        this.renderOit = renderOit;
        this.renderAfterOit = renderAfterOit;
        this.clearFramebuffer = clearFramebuffer;
        this.loadFramebuffer = loadFramebuffer;
        this.unloadFramebuffer = unloadFramebuffer;
        this.mergeTransparentRenderPass = mergeTransparentRenderPass;
        this.blitPrimaryToDefault = blitPrimaryToDefault;
        this.offscreenBufferField = offscreenBufferField;
        this.cameraMatrixOriginField = cameraMatrixOriginField;
        this.poolsByRenderPassField = poolsByRenderPassField;
        this.poolFrustumField = poolFrustumField;
    }

    public static ExactChunkRendererAdapter? TryCreate(ICoreClientAPI capi)
    {
        if (!GameVersion.ShortGameVersion.StartsWith(SupportedVersion, StringComparison.Ordinal))
        {
            capi.Logger.Warning(
                "[ModernAtlas] Exact chunk rendering supports Vintage Story {0}; using the compatible atlas renderer on {1}.",
                SupportedVersion,
                GameVersion.ShortGameVersion
            );
            return null;
        }

        try
        {
            FieldInfo gameField = RequireField(capi.GetType(), "game");
            object game = gameField.GetValue(capi)
                ?? throw new InvalidOperationException("Client game instance is unavailable.");
            FieldInfo rendererField = RequireField(game.GetType(), "chunkRenderer");
            object renderer = rendererField.GetValue(game)
                ?? throw new InvalidOperationException("Chunk renderer is unavailable.");
            FieldInfo cameraField = RequireField(game.GetType(), "MainCamera");
            object camera = cameraField.GetValue(game)
                ?? throw new InvalidOperationException("Player camera is unavailable.");
            FieldInfo platformField = RequireField(game.GetType(), "Platform");
            object platform = platformField.GetValue(game)
                ?? throw new InvalidOperationException("Client platform is unavailable.");
            MethodInfo opaque = renderer.GetType().GetMethod(
                "RenderOpaque",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(float) },
                null
            ) ?? throw new MissingMethodException(renderer.GetType().FullName, "RenderOpaque(float)");
            MethodInfo oit = renderer.GetType().GetMethod(
                "RenderOIT",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(float) },
                null
            ) ?? throw new MissingMethodException(renderer.GetType().FullName, "RenderOIT(float)");
            MethodInfo afterOit = renderer.GetType().GetMethod(
                "RenderAfterOIT",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(float) },
                null
            ) ?? throw new MissingMethodException(renderer.GetType().FullName, "RenderAfterOIT(float)");
            MethodInfo clear = platform.GetType().GetMethod(
                "ClearFrameBuffer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(EnumFrameBuffer) },
                null
            ) ?? throw new MissingMethodException(
                platform.GetType().FullName,
                "ClearFrameBuffer(EnumFrameBuffer)"
            );
            MethodInfo load = RequireMethod(platform.GetType(), "LoadFrameBuffer", typeof(EnumFrameBuffer));
            MethodInfo unload = RequireMethod(platform.GetType(), "UnloadFrameBuffer", typeof(EnumFrameBuffer));
            MethodInfo mergeTransparent = RequireMethod(platform.GetType(), "MergeTransparentRenderPass");
            MethodInfo blit = platform.GetType().GetMethod(
                "BlitPrimaryToDefault",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null
            ) ?? throw new MissingMethodException(
                platform.GetType().FullName,
                "BlitPrimaryToDefault()"
            );
            FieldInfo cameraMatrix = RequireField(camera.GetType(), "CameraMatrixOrigin");
            FieldInfo offscreenBuffer = RequireField(platform.GetType(), "OffscreenBuffer");
            FieldInfo pools = RequireField(renderer.GetType(), "poolsByRenderPass");
            FieldInfo poolFrustum = RequireField(typeof(MeshDataPoolManager), "frustumCuller");

            capi.Logger.Notification(
                "[ModernAtlas] Vintage Story 1.22.6 exact chunk renderer is available."
            );
            return new ExactChunkRendererAdapter(
                capi,
                renderer,
                camera,
                platform,
                opaque,
                oit,
                afterOit,
                clear,
                load,
                unload,
                mergeTransparent,
                blit,
                offscreenBuffer,
                cameraMatrix,
                pools,
                poolFrustum
            );
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Exact chunk renderer is unavailable; using the compatible atlas renderer: {0}",
                exception.Message
            );
            return null;
        }
    }

    public bool Render(
        float deltaTime,
        float[] projection,
        double centerX,
        double centerY,
        double centerZ,
        float yawRadians,
        float pitchRadians,
        int radiusBlocks,
        bool fogEnabled
    )
    {
        if (disabled) return false;

        IRenderAPI render = capi.Render;
        Vec3d cameraPosition = capi.World.Player.Entity.CameraPos;
        double oldCameraX = cameraPosition.X;
        double oldCameraY = cameraPosition.Y;
        double oldCameraZ = cameraPosition.Z;
        double[] cameraMatrix = (double[])(cameraMatrixOriginField.GetValue(mainCamera)
            ?? throw new InvalidOperationException("Camera origin matrix is unavailable."));
        double[] savedCameraMatrix = (double[])cameraMatrix.Clone();
        List<(object Pool, object? Frustum)> changedPools = new();
        bool projectionPushed = false;

        try
        {
            // The normal world has already rendered before this HUD dialog.
            // Clear it so weather particles such as rain cannot leak through
            // transparent atlas pixels. ModernAtlas then draws chunk meshes
            // through Primary and overlays only its own fog and GUI.
            clearFramebuffer.Invoke(platform, new object[] { EnumFrameBuffer.Default });

            // The official chunk shaders write to the multi-attachment Primary
            // world framebuffer. Rendering them into the default GUI target
            // produces no color even though the draw call succeeds.
            FrameBufferRef primaryFramebuffer = render.FrameBuffers[(int)EnumFrameBuffer.Primary];
            float[] atlasBackground = fogEnabled
                ? new[] { 0.32f, 0.38f, 0.40f, 1f }
                : new[] { 0.035f, 0.075f, 0.11f, 1f };
            render.ClearFrameBuffer(primaryFramebuffer, atlasBackground, true, true);

            // Keep the eye in front of the entire requested atlas volume.
            // The old fixed distance intersected large maps at tilted angles,
            // which cut off the upper or lower part of the terrain.
            double distance = Math.Max(640, radiusBlocks * 2.5);
            double horizontal = Math.Cos(pitchRadians) * distance;
            double eyeX = centerX + Math.Sin(yawRadians) * horizontal;
            double eyeY = centerY + Math.Sin(pitchRadians) * distance;
            double eyeZ = centerZ + Math.Cos(yawRadians) * horizontal;

            double[] view = Mat4d.Create();
            Mat4d.LookAt(
                view,
                new[]
                {
                    eyeX - oldCameraX,
                    eyeY - oldCameraY,
                    eyeZ - oldCameraZ
                },
                new[]
                {
                    centerX - oldCameraX,
                    centerY - oldCameraY,
                    centerZ - oldCameraZ
                },
                new[] { 0d, 1d, 0d }
            );
            double[] cullingView = Mat4d.Create();
            Mat4d.LookAt(
                cullingView,
                new[] { eyeX, eyeY, eyeZ },
                new[] { centerX, centerY, centerZ },
                new[] { 0d, 1d, 0d }
            );
            double[] projectionDouble = Array.ConvertAll(projection, value => (double)value);

            Array.Copy(view, cameraMatrix, 16);

            FrustumCulling atlasFrustum = new();
            // FrustumCulling measures from the elevated atlas eye, not from
            // the map center. At a 45-degree tilt the far edge is farther than
            // three radii from that eye and the old limit removed roughly half
            // of the terrain. This is only a GPU-mesh visibility limit; it
            // does not alter the player position or request distant chunks.
            float cullingDistance = Math.Max(
                2048,
                (float)(distance + radiusBlocks * 1.75 + 384)
            );
            atlasFrustum.UpdateViewDistance((int)cullingDistance);
            // A newly constructed culler has a zero LOD0 range. In that state
            // Vintage Story rejects every full-detail terrain pool even when
            // it is geometrically inside the atlas frustum.
            atlasFrustum.lod0BiasSq = cullingDistance * cullingDistance;
            atlasFrustum.lod2BiasSq = (double)cullingDistance * cullingDistance;
            atlasFrustum.CalcFrustumEquations(
                new BlockPos((int)Math.Floor(eyeX), (int)Math.Floor(eyeY), (int)Math.Floor(eyeZ)),
                projectionDouble,
                cullingView
            );
            ReplacePoolFrustums(atlasFrustum, changedPools);

            render.PMatrix.Push(projectionDouble);
            projectionPushed = true;
            render.CurrentActiveShader?.Stop();
            renderOpaque.Invoke(chunkRenderer, new object[] { deltaTime });
            if (!RenderTransparentChunks(deltaTime))
            {
                blitPrimaryToDefault.Invoke(platform, Array.Empty<object>());
            }

            if (!loggedSuccess)
            {
                loggedSuccess = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Rendering the atlas from the game's completed chunk meshes and materials."
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
                "[ModernAtlas] Exact chunk rendering failed and was disabled for this session: {0}",
                cause.Message
            );
            return false;
        }
        finally
        {
            for (int index = changedPools.Count - 1; index >= 0; index--)
            {
                (object pool, object? frustum) = changedPools[index];
                poolFrustumField.SetValue(pool, frustum);
            }
            Array.Copy(savedCameraMatrix, cameraMatrix, 16);
            cameraPosition.Set(oldCameraX, oldCameraY, oldCameraZ);
            if (projectionPushed) render.PMatrix.Pop();
            render.CurrentActiveShader?.Stop();
        }
    }

    private bool RenderTransparentChunks(float deltaTime)
    {
        if (transparentPassDisabled) return false;

        bool framebufferLoaded = false;
        bool? savedOffscreenBuffer = null;
        try
        {
            loadFramebuffer.Invoke(platform, new object[] { EnumFrameBuffer.Transparent });
            framebufferLoaded = true;
            clearFramebuffer.Invoke(platform, new object[] { EnumFrameBuffer.Transparent });
            renderOit.Invoke(chunkRenderer, new object[] { deltaTime });
            unloadFramebuffer.Invoke(platform, new object[] { EnumFrameBuffer.Transparent });
            framebufferLoaded = false;

            // Water plants still need Primary's depth buffer. Draw them there,
            // copy the completed opaque scene to the GUI target, then compose
            // liquid OIT directly onto that target. During GUI rendering the
            // engine's regular Primary composition can produce liquid shadows
            // without the liquid color reaching the window.
            renderAfterOit.Invoke(chunkRenderer, new object[] { deltaTime });
            blitPrimaryToDefault.Invoke(platform, Array.Empty<object>());
            savedOffscreenBuffer = (bool)offscreenBufferField.GetValue(platform)!;
            offscreenBufferField.SetValue(platform, false);
            mergeTransparentRenderPass.Invoke(platform, Array.Empty<object>());

            if (!loggedTransparentSuccess)
            {
                loggedTransparentSuccess = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Rendering animated liquids and transparent chunk materials through the game's OIT pass."
                );
            }
            return true;
        }
        catch (Exception exception)
        {
            transparentPassDisabled = true;
            Exception cause = exception is TargetInvocationException { InnerException: not null }
                ? exception.InnerException
                : exception;
            capi.Logger.Error(
                "[ModernAtlas] Liquid and transparent rendering failed and was disabled for this session: {0}",
                cause.Message
            );
            return false;
        }
        finally
        {
            if (savedOffscreenBuffer.HasValue)
            {
                offscreenBufferField.SetValue(platform, savedOffscreenBuffer.Value);
            }
            if (framebufferLoaded)
            {
                try
                {
                    unloadFramebuffer.Invoke(platform, new object[] { EnumFrameBuffer.Transparent });
                }
                catch
                {
                    // Preserve the opaque atlas even if framebuffer cleanup is
                    // unavailable after an OIT failure.
                }
            }
            // GUI elements must always continue on the actual window target,
            // including after a transparent-pass exception.
            capi.Render.CurrentFrameBuffer = null;
        }
    }

    private void ReplacePoolFrustums(
        FrustumCulling atlasFrustum,
        List<(object Pool, object? Frustum)> changedPools
    )
    {
        if (poolsByRenderPassField.GetValue(chunkRenderer) is not IEnumerable passes) return;

        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        foreach (object? pass in passes)
        {
            if (pass is not IEnumerable managers) continue;
            foreach (object? manager in managers)
            {
                if (manager == null || !visited.Add(manager)) continue;
                object? previous = poolFrustumField.GetValue(manager);
                changedPools.Add((manager, previous));
                poolFrustumField.SetValue(manager, atlasFrustum);
            }
        }
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
