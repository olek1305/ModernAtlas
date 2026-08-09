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
/// remain identical to the normal world view. The global entity and particle
/// stages are never invoked; a separate adapter draws only selected living
/// models that are already loaded and authorized for the atlas.
/// </summary>
internal sealed class ExactChunkRendererAdapter : IDisposable
{
    private const string SupportedVersion = "1.22.6";

    private readonly ICoreClientAPI capi;
    private readonly object chunkRenderer;
    private readonly object mainCamera;
    private readonly object platform;
    private readonly object beforeOitRenderer;
    private readonly object afterOitRenderer;
    private readonly VolumetricCloudRendererAdapter? cloudRenderer;
    private readonly AtlasEntityModelRendererAdapter entityModelRenderer;
    private readonly Func<IShaderProgram?> stableLiquidShaderProvider;
    private readonly AtlasSurfaceCache surfaceCache;
    private readonly MethodInfo renderOpaque;
    private readonly MethodInfo renderOit;
    private readonly MethodInfo renderAfterOit;
    private readonly MethodInfo runBeforeOit;
    private readonly MethodInfo runAfterOit;
    private readonly MethodInfo clearFramebuffer;
    private readonly MethodInfo loadFramebuffer;
    private readonly MethodInfo unloadFramebuffer;
    private readonly MethodInfo mergeTransparentRenderPass;
    private readonly MethodInfo blitPrimaryToDefault;
    private readonly FieldInfo cameraMatrixOriginField;
    private readonly FieldInfo poolsByRenderPassField;
    private readonly FieldInfo poolFrustumField;
    private readonly FieldInfo managerPoolsField;
    private readonly FieldInfo textureIdsField;
    private readonly FieldInfo skyDaylightUniformField;
    private readonly PropertyInfo ambientFogDensityProperty;
    private readonly PropertyInfo ambientFogMinimumProperty;
    private readonly PropertyInfo ambientColorProperty;
    private readonly PropertyInfo ambientSceneBrightnessProperty;
    private bool disabled;
    private bool transparentPassDisabled;
    private bool loggedSuccess;
    private bool loggedTransparentSuccess;
    private bool loggedStableLiquidDiagnostics;
    private LoadedTexture? liquidChunkMask;
    private int liquidMaskOriginX;
    private int liquidMaskOriginZ;
    private int liquidMaskSize;
    private long lastLiquidMaskUpdateMilliseconds;
    private Vec3f atlasSunDirection = new(-0.34f, 0.86f, -0.38f);
    private float atlasExposure = 1f;

    public int LastRenderedEntityCount { get; private set; }

    private ExactChunkRendererAdapter(
        ICoreClientAPI capi,
        object chunkRenderer,
        object mainCamera,
        object platform,
        object beforeOitRenderer,
        object afterOitRenderer,
        VolumetricCloudRendererAdapter? cloudRenderer,
        Func<IShaderProgram?> stableLiquidShaderProvider,
        AtlasSurfaceCache surfaceCache,
        MethodInfo renderOpaque,
        MethodInfo renderOit,
        MethodInfo renderAfterOit,
        MethodInfo runBeforeOit,
        MethodInfo runAfterOit,
        MethodInfo clearFramebuffer,
        MethodInfo loadFramebuffer,
        MethodInfo unloadFramebuffer,
        MethodInfo mergeTransparentRenderPass,
        MethodInfo blitPrimaryToDefault,
        FieldInfo cameraMatrixOriginField,
        FieldInfo poolsByRenderPassField,
        FieldInfo poolFrustumField,
        FieldInfo managerPoolsField,
        FieldInfo textureIdsField,
        FieldInfo skyDaylightUniformField,
        PropertyInfo ambientFogDensityProperty,
        PropertyInfo ambientFogMinimumProperty,
        PropertyInfo ambientColorProperty,
        PropertyInfo ambientSceneBrightnessProperty
    )
    {
        this.capi = capi;
        this.chunkRenderer = chunkRenderer;
        this.mainCamera = mainCamera;
        this.platform = platform;
        this.beforeOitRenderer = beforeOitRenderer;
        this.afterOitRenderer = afterOitRenderer;
        this.cloudRenderer = cloudRenderer;
        entityModelRenderer = new AtlasEntityModelRendererAdapter(capi);
        this.stableLiquidShaderProvider = stableLiquidShaderProvider;
        this.surfaceCache = surfaceCache;
        this.renderOpaque = renderOpaque;
        this.renderOit = renderOit;
        this.renderAfterOit = renderAfterOit;
        this.runBeforeOit = runBeforeOit;
        this.runAfterOit = runAfterOit;
        this.clearFramebuffer = clearFramebuffer;
        this.loadFramebuffer = loadFramebuffer;
        this.unloadFramebuffer = unloadFramebuffer;
        this.mergeTransparentRenderPass = mergeTransparentRenderPass;
        this.blitPrimaryToDefault = blitPrimaryToDefault;
        this.cameraMatrixOriginField = cameraMatrixOriginField;
        this.poolsByRenderPassField = poolsByRenderPassField;
        this.poolFrustumField = poolFrustumField;
        this.managerPoolsField = managerPoolsField;
        this.textureIdsField = textureIdsField;
        this.skyDaylightUniformField = skyDaylightUniformField;
        this.ambientFogDensityProperty = ambientFogDensityProperty;
        this.ambientFogMinimumProperty = ambientFogMinimumProperty;
        this.ambientColorProperty = ambientColorProperty;
        this.ambientSceneBrightnessProperty = ambientSceneBrightnessProperty;
    }

    public static ExactChunkRendererAdapter? TryCreate(
        ICoreClientAPI capi,
        Func<IShaderProgram?> stableLiquidShaderProvider,
        Func<IShaderProgram?> atlasCloudShaderProvider,
        AtlasSurfaceCache surfaceCache
    )
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
            object beforeOitRenderer = FindRegisteredRenderer(
                game,
                "Vintagestory.Client.NoObf.SystemRenderOITLayers+BeforeOIT"
            );
            object afterOitRenderer = FindRegisteredRenderer(
                game,
                "Vintagestory.Client.NoObf.SystemRenderOITLayers+AfterOIT"
            );
            VolumetricCloudRendererAdapter? cloudRenderer =
                VolumetricCloudRendererAdapter.TryCreate(capi, game, atlasCloudShaderProvider);
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
            MethodInfo runBeforeOit = RequireMethod(
                beforeOitRenderer.GetType(),
                "OnRenderFrame",
                typeof(float),
                typeof(EnumRenderStage)
            );
            MethodInfo runAfterOit = RequireMethod(
                afterOitRenderer.GetType(),
                "OnRenderFrame",
                typeof(float),
                typeof(EnumRenderStage)
            );
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
            FieldInfo pools = RequireField(renderer.GetType(), "poolsByRenderPass");
            FieldInfo poolFrustum = RequireField(typeof(MeshDataPoolManager), "frustumCuller");
            FieldInfo managerPools = RequireField(typeof(MeshDataPoolManager), "pools");
            FieldInfo textureIds = RequireField(renderer.GetType(), "textureIds");
            FieldInfo skyDaylightUniform = RequireField(
                typeof(DefaultShaderUniforms),
                "SkyDaylight"
            );
            PropertyInfo ambientFogDensity = RequireWritableProperty(
                capi.Ambient.GetType(),
                nameof(IAmbientManager.BlendedFogDensity)
            );
            PropertyInfo ambientFogMinimum = RequireWritableProperty(
                capi.Ambient.GetType(),
                nameof(IAmbientManager.BlendedFogMin)
            );
            PropertyInfo ambientColor = RequireWritableProperty(
                capi.Ambient.GetType(),
                nameof(IAmbientManager.BlendedAmbientColor)
            );
            PropertyInfo ambientSceneBrightness = RequireWritableProperty(
                capi.Ambient.GetType(),
                nameof(IAmbientManager.BlendedSceneBrightness)
            );

            capi.Logger.Notification(
                "[ModernAtlas] Vintage Story 1.22.6 exact chunk renderer is available."
            );
            return new ExactChunkRendererAdapter(
                capi,
                renderer,
                camera,
                platform,
                beforeOitRenderer,
                afterOitRenderer,
                cloudRenderer,
                stableLiquidShaderProvider,
                surfaceCache,
                opaque,
                oit,
                afterOit,
                runBeforeOit,
                runAfterOit,
                clear,
                load,
                unload,
                mergeTransparent,
                blit,
                cameraMatrix,
                pools,
                poolFrustum,
                managerPools,
                textureIds,
                skyDaylightUniform,
                ambientFogDensity,
                ambientFogMinimum,
                ambientColor,
                ambientSceneBrightness
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
        int viewDistanceBlocks,
        bool fogEnabled,
        float windWaveCounter,
        float windWaveCounterHighFrequency,
        float waterStillCounter,
        float waterFlowCounter,
        bool cloudsEnabled,
        bool liveLightingEnabled,
        int fixedSunHour,
        float pausedCloudAnimationDeltaTime,
        ModernAtlasServerPolicy entityPolicy
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
        IAmbientManager ambient = capi.Ambient;
        float savedFogDensity = ambient.BlendedFogDensity;
        float savedFlatFogDensity = ambient.BlendedFlatFogDensity;
        float savedFogMinimum = ambient.BlendedFogMin;
        Vec3f savedAmbientColor = ambient.BlendedAmbientColor;
        float savedSceneBrightness = ambient.BlendedSceneBrightness;
        DefaultShaderUniforms shaderUniforms = render.ShaderUniforms;
        float savedCameraUnderwater = shaderUniforms.CameraUnderwater;
        int savedFogSphereQuantity = shaderUniforms.FogSphereQuantity;
        float savedFlagFogDensity = shaderUniforms.FlagFogDensity;
        float savedNightVisionStrength = shaderUniforms.NightVisionStrength;
        float savedPsychedelicStrength = shaderUniforms.PsychedelicStrength;
        float savedGlitchStrength = shaderUniforms.GlitchStrength;
        float savedGlobalWorldWarp = shaderUniforms.GlobalWorldWarp;
        float savedPerceptionEffectIntensity = shaderUniforms.PerceptionEffectIntensity;
        int savedPointLightsCount = shaderUniforms.PointLightsCount;
        float savedDropShadowIntensity = shaderUniforms.DropShadowIntensity;
        Vec3f savedLightPosition = shaderUniforms.LightPosition3D;
        float savedSkyDaylight = (float)(skyDaylightUniformField.GetValue(shaderUniforms) ?? 0f);
        float savedSunsetMod = shaderUniforms.SunsetMod;
        float savedWindWaveCounter = shaderUniforms.WindWaveCounter;
        float savedWindWaveCounterHighFrequency = shaderUniforms.WindWaveCounterHighFreq;

        try
        {
            // The atlas already has an explicit unexplored-area mask. World
            // distance fog is based on the elevated atlas eye and otherwise
            // covers most of the orthographic map with haze.
            ambientFogDensityProperty.SetValue(ambient, 0f);
            ambient.BlendedFlatFogDensity = 0;
            ambientFogMinimumProperty.SetValue(ambient, 0f);
            ApplyAtlasLighting(
                ambient,
                shaderUniforms,
                liveLightingEnabled,
                fixedSunHour,
                savedAmbientColor,
                savedSceneBrightness,
                savedLightPosition,
                savedSkyDaylight
            );
            shaderUniforms.CameraUnderwater = 0;
            shaderUniforms.FogSphereQuantity = 0;
            shaderUniforms.FlagFogDensity = 0;
            shaderUniforms.NightVisionStrength = 0;
            shaderUniforms.PsychedelicStrength = 0;
            shaderUniforms.GlitchStrength = 0;
            shaderUniforms.GlobalWorldWarp = 0;
            shaderUniforms.PerceptionEffectIntensity = 0;
            shaderUniforms.PointLightsCount = 0;
            // The world's shadow map belongs to the normal perspective camera.
            // Sampling it from the atlas camera causes invalid GL operations
            // and severe frame drops. Keep native directional daylight, but
            // disable only that camera-dependent shadow texture in the atlas.
            shaderUniforms.DropShadowIntensity = 0;
            // The atlas uses either the live game sun/weather state or a
            // deterministic fixed-hour direction selected in Settings.
            // Keep the game's sky-daylight and sunset inputs so atlas terrain
            // follows the native time-of-day color without sampling shadows.

            // The dialog supplies either live render-only counters or one
            // captured frame when atlas animations are paused. Restore the
            // engine values after this draw so normal gameplay is unaffected.
            shaderUniforms.WindWaveCounter = windWaveCounter;
            shaderUniforms.WindWaveCounterHighFreq = windWaveCounterHighFrequency;

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
            double distance = Math.Max(640, viewDistanceBlocks * 2.5);
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
                (float)(distance + viewDistanceBlocks * 1.75 + 384)
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
            surfaceCache.Render(
                projection,
                cullingView,
                centerX,
                centerZ,
                viewDistanceBlocks
            );
            render.CurrentActiveShader?.Stop();
            renderOpaque.Invoke(chunkRenderer, new object[] { deltaTime });
            LastRenderedEntityCount = entityModelRenderer.Render(
                deltaTime,
                view,
                projection,
                viewDistanceBlocks,
                entityPolicy
            );
            if (!RenderTransparentChunks(
                deltaTime,
                projection,
                view,
                cameraPosition,
                waterStillCounter,
                waterFlowCounter,
                cloudsEnabled,
                pausedCloudAnimationDeltaTime
            ))
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
            ambientFogDensityProperty.SetValue(ambient, savedFogDensity);
            ambient.BlendedFlatFogDensity = savedFlatFogDensity;
            ambientFogMinimumProperty.SetValue(ambient, savedFogMinimum);
            ambientColorProperty.SetValue(ambient, savedAmbientColor);
            ambientSceneBrightnessProperty.SetValue(ambient, savedSceneBrightness);
            shaderUniforms.CameraUnderwater = savedCameraUnderwater;
            shaderUniforms.FogSphereQuantity = savedFogSphereQuantity;
            shaderUniforms.FlagFogDensity = savedFlagFogDensity;
            shaderUniforms.NightVisionStrength = savedNightVisionStrength;
            shaderUniforms.PsychedelicStrength = savedPsychedelicStrength;
            shaderUniforms.GlitchStrength = savedGlitchStrength;
            shaderUniforms.GlobalWorldWarp = savedGlobalWorldWarp;
            shaderUniforms.PerceptionEffectIntensity = savedPerceptionEffectIntensity;
            shaderUniforms.PointLightsCount = savedPointLightsCount;
            shaderUniforms.DropShadowIntensity = savedDropShadowIntensity;
            shaderUniforms.LightPosition3D = savedLightPosition;
            skyDaylightUniformField.SetValue(shaderUniforms, savedSkyDaylight);
            shaderUniforms.SunsetMod = savedSunsetMod;
            shaderUniforms.WindWaveCounter = savedWindWaveCounter;
            shaderUniforms.WindWaveCounterHighFreq = savedWindWaveCounterHighFrequency;
            if (projectionPushed) render.PMatrix.Pop();
            render.CurrentActiveShader?.Stop();
        }
    }

    private void ApplyAtlasLighting(
        IAmbientManager ambient,
        DefaultShaderUniforms shaderUniforms,
        bool liveLightingEnabled,
        int fixedSunHour,
        Vec3f liveAmbientColor,
        float liveSceneBrightness,
        Vec3f liveLightPosition,
        float liveSkyDaylight
    )
    {
        if (liveLightingEnabled)
        {
            ambientColorProperty.SetValue(ambient, liveAmbientColor);
            ambientSceneBrightnessProperty.SetValue(ambient, liveSceneBrightness);
            shaderUniforms.LightPosition3D = liveLightPosition;
            atlasSunDirection = liveLightPosition;
            atlasExposure = Math.Clamp(
                liveSkyDaylight * Math.Max(0.2f, liveSceneBrightness),
                0.08f,
                1f
            );
            return;
        }

        float phase = (Math.Clamp(fixedSunHour, 0, 23) - 6f) / 24f * GameMath.TWOPI;
        float daylight = Math.Clamp(MathF.Sin(phase), 0f, 1f);
        float lightY = Math.Max(0.16f, daylight);
        float horizontal = MathF.Sqrt(Math.Max(0f, 1f - lightY * lightY));
        float azimuth = phase + 0.45f;
        shaderUniforms.LightPosition3D = new Vec3f(
            MathF.Cos(azimuth) * horizontal,
            lightY,
            MathF.Sin(azimuth) * horizontal
        );
        atlasSunDirection = shaderUniforms.LightPosition3D;
        atlasExposure = daylight;
        ambientColorProperty.SetValue(
            ambient,
            new Vec3f(
                0.24f + daylight * 0.66f,
                0.29f + daylight * 0.61f,
                0.42f + daylight * 0.48f
            )
        );
        ambientSceneBrightnessProperty.SetValue(ambient, 0.22f + daylight * 0.78f);
    }

    public void Dispose()
    {
        cloudRenderer?.Dispose();
        liquidChunkMask?.Dispose();
        liquidChunkMask = null;
    }

    private bool RenderTransparentChunks(
        float deltaTime,
        float[] projection,
        double[] view,
        Vec3d cameraPosition,
        float waterStillCounter,
        float waterFlowCounter,
        bool cloudsEnabled,
        float pausedCloudAnimationDeltaTime
    )
    {
        if (transparentPassDisabled) return false;

        bool framebufferLoaded = false;
        List<(object Manager, object Pools)> hiddenLiquidPools = new();
        try
        {
            loadFramebuffer.Invoke(platform, new object[] { EnumFrameBuffer.Transparent });
            framebufferLoaded = true;
            clearFramebuffer.Invoke(platform, new object[] { EnumFrameBuffer.Transparent });
            // VS 1.22.6 adds layered OIT attachments beyond the legacy
            // accumulation/reveal buffers. Invoke only the engine's two OIT
            // setup renderers around chunk liquids; do not trigger the global
            // render stage, which would also draw entities and particles.
            runBeforeOit.Invoke(
                beforeOitRenderer,
                new object[] { deltaTime, EnumRenderStage.OIT }
            );
            HideLiquidPools(hiddenLiquidPools);
            renderOit.Invoke(chunkRenderer, new object[] { deltaTime });
            RestoreLiquidPools(hiddenLiquidPools);
            runAfterOit.Invoke(
                afterOitRenderer,
                new object[] { deltaTime, EnumRenderStage.OIT }
            );
            unloadFramebuffer.Invoke(platform, new object[] { EnumFrameBuffer.Transparent });
            framebufferLoaded = false;

            RenderStableLiquidBlocks(
                deltaTime,
                projection,
                view,
                cameraPosition,
                waterStillCounter,
                waterFlowCounter
            );

            // Compose at the Primary framebuffer's own resolution before the
            // final blit. OIT uses texelFetch(gl_FragCoord), so composing it
            // directly onto a differently sized window target under SSAA
            // makes fluids slide relative to blocks while the camera pans.
            mergeTransparentRenderPass.Invoke(platform, Array.Empty<object>());
            renderAfterOit.Invoke(chunkRenderer, new object[] { deltaTime });
            blitPrimaryToDefault.Invoke(platform, Array.Empty<object>());
            if (cloudsEnabled)
            {
                cloudRenderer?.Render(projection, view, pausedCloudAnimationDeltaTime);
            }

            if (!loggedTransparentSuccess)
            {
                loggedTransparentSuccess = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Rendering stable liquid block surfaces and engine OIT transparent materials."
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
            RestoreLiquidPools(hiddenLiquidPools);
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

    private void HideLiquidPools(List<(object Manager, object Pools)> hidden)
    {
        if (poolsByRenderPassField.GetValue(chunkRenderer)
            is not MeshDataPoolManager[][] passes) return;

        foreach (MeshDataPoolManager? manager in passes[(int)EnumChunkRenderPass.Liquid])
        {
            if (manager == null) continue;
            object? pools = managerPoolsField.GetValue(manager);
            if (pools == null) continue;
            object emptyPools = Activator.CreateInstance(pools.GetType())
                ?? throw new InvalidOperationException("Could not create an empty liquid pool list.");
            hidden.Add((manager, pools));
            managerPoolsField.SetValue(manager, emptyPools);
        }
    }

    private void RestoreLiquidPools(List<(object Manager, object Pools)> hidden)
    {
        for (int index = hidden.Count - 1; index >= 0; index--)
        {
            (object manager, object pools) = hidden[index];
            managerPoolsField.SetValue(manager, pools);
        }
        hidden.Clear();
    }

    private void RenderStableLiquidBlocks(
        float deltaTime,
        float[] projection,
        double[] view,
        Vec3d cameraPosition,
        float waterStillCounter,
        float waterFlowCounter
    )
    {
        IShaderProgram? activeLiquidShader = stableLiquidShaderProvider();
        if (activeLiquidShader == null || activeLiquidShader.Disposed)
        {
            if (!loggedStableLiquidDiagnostics)
            {
                loggedStableLiquidDiagnostics = true;
                capi.Logger.Error("[ModernAtlas] Stable liquid shader is not loaded.");
            }
            return;
        }
        if (poolsByRenderPassField.GetValue(chunkRenderer)
            is not MeshDataPoolManager[][] passes) return;
        if (textureIdsField.GetValue(chunkRenderer) is not int[] textureIds) return;

        IRenderAPI render = capi.Render;
        UpdateLiquidChunkMask();
        LoadedTexture? activeChunkMask = liquidChunkMask;
        if (activeChunkMask == null || activeChunkMask.TextureId <= 0) return;
        render.CurrentActiveShader?.Stop();
        render.GLEnableDepthTest();
        render.GLDepthMask(false);
        render.GlToggleBlend(true, EnumBlendMode.Standard);
        render.GlDisableCullFace();

        activeLiquidShader.Use();
        activeLiquidShader.UniformMatrix("projectionMatrix", projection);
        activeLiquidShader.UniformMatrix(
            "modelViewMatrix",
            Array.ConvertAll(view, value => (float)value)
        );
        activeLiquidShader.Uniform(
            "playerpos",
            (float)cameraPosition.X,
            (float)cameraPosition.Y,
            (float)cameraPosition.Z
        );
        activeLiquidShader.BindTexture2D("loadedChunkMask", activeChunkMask.TextureId, 7);
        activeLiquidShader.Uniform(
            "maskChunkOrigin",
            (float)liquidMaskOriginX,
            (float)liquidMaskOriginZ
        );
        activeLiquidShader.Uniform("maskSize", (float)liquidMaskSize);
        activeLiquidShader.Uniform("chunkSize", (float)GlobalConstants.ChunkSize);
        activeLiquidShader.Uniform("atlasSunDirection", atlasSunDirection);
        activeLiquidShader.Uniform("atlasExposure", atlasExposure);
        int blockTexturePixels = capi.Settings.Int["textureSize"];
        if (blockTexturePixels <= 0) blockTexturePixels = 32;
        float atlasPixels = capi.BlockTextureAtlas.Size.Width;
        float blockTextureUv = blockTexturePixels / atlasPixels;
        activeLiquidShader.Uniform("blockTextureSize", blockTextureUv, blockTextureUv);
        activeLiquidShader.Uniform("textureAtlasSize", atlasPixels, atlasPixels);
        activeLiquidShader.Uniform(
            "waterStillCounter",
            waterStillCounter
        );
        activeLiquidShader.Uniform(
            "waterFlowCounter",
            waterFlowCounter
        );

        MeshDataPoolManager[] managers = passes[(int)EnumChunkRenderPass.Liquid];
        long renderedTriangles = 0;
        long allocatedTriangles = 0;
        int activeManagers = 0;
        for (int index = 0; index < managers.Length && index < textureIds.Length; index++)
        {
            if (managers[index] == null) continue;
            activeManagers++;
            activeLiquidShader.BindTexture2D("terrainTex", textureIds[index], 0);
            managers[index].Render(cameraPosition, "origin", EnumFrustumCullMode.CullInstant);
            long usedVideoMemory = 0;
            long managerRenderedTriangles = 0;
            long managerAllocatedTriangles = 0;
            managers[index].GetStats(
                ref usedVideoMemory,
                ref managerRenderedTriangles,
                ref managerAllocatedTriangles
            );
            renderedTriangles += managerRenderedTriangles;
            allocatedTriangles += managerAllocatedTriangles;
        }
        activeLiquidShader.Stop();
        render.GLDepthMask(true);
        render.GlToggleBlend(false, EnumBlendMode.Standard);
        render.GlEnableCullFace();

        if (!loggedStableLiquidDiagnostics)
        {
            loggedStableLiquidDiagnostics = true;
            capi.Logger.Notification(
                "[ModernAtlas] Stable liquid draw: shader pass {0}, {1} atlas managers, {2} rendered triangles, {3} allocated triangles.",
                activeLiquidShader.PassId,
                activeManagers,
                renderedTriangles,
                allocatedTriangles
            );
        }
    }

    private void UpdateLiquidChunkMask()
    {
        int chunkSize = GlobalConstants.ChunkSize;
        int playerChunkX = FloorDiv(
            (int)Math.Floor(capi.World.Player.Entity.Pos.X),
            chunkSize
        );
        int playerChunkZ = FloorDiv(
            (int)Math.Floor(capi.World.Player.Entity.Pos.Z),
            chunkSize
        );
        int radius = Math.Clamp(capi.Settings.Int["viewDistance"] / chunkSize + 2, 2, 64);
        int size = radius * 2 + 1;
        int originX = playerChunkX - radius;
        int originZ = playerChunkZ - radius;
        long now = capi.ElapsedMilliseconds;
        if (liquidChunkMask != null
            && liquidMaskOriginX == originX
            && liquidMaskOriginZ == originZ
            && liquidMaskSize == size
            && now - lastLiquidMaskUpdateMilliseconds < 1000)
        {
            return;
        }

        int[] pixels = new int[size * size];
        int center = chunkSize / 2;
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int chunkX = originX + x;
                int chunkZ = originZ + z;
                IMapChunk? mapChunk = capi.World.BlockAccessor.GetMapChunk(chunkX, chunkZ);
                if (mapChunk == null) continue;

                int heightIndex = center * chunkSize + center;
                ushort[] rainHeights = mapChunk.RainHeightMap;
                int surfaceY = heightIndex < rainHeights.Length
                    ? rainHeights[heightIndex]
                    : capi.World.SeaLevel;
                IWorldChunk? surfaceChunk = capi.World.BlockAccessor.GetChunk(
                    chunkX,
                    surfaceY / chunkSize,
                    chunkZ
                );
                if (surfaceChunk is IClientChunk { LoadedFromServer: true })
                {
                    pixels[z * size + x] = unchecked((int)0xffffffff);
                }
            }
        }

        if (liquidChunkMask == null || liquidChunkMask.Width != size || liquidChunkMask.Height != size)
        {
            liquidChunkMask?.Dispose();
            liquidChunkMask = new LoadedTexture(capi, 0, size, size);
        }
        capi.Render.LoadOrUpdateTextureFromRgba(pixels, false, 0, ref liquidChunkMask);
        liquidMaskOriginX = originX;
        liquidMaskOriginZ = originZ;
        liquidMaskSize = size;
        lastLiquidMaskUpdateMilliseconds = now;
    }

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : (value - divisor + 1) / divisor;

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

    private static PropertyInfo RequireWritableProperty(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            PropertyInfo? property = current.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
            if (property?.SetMethod != null) return property;
        }
        throw new MissingMemberException(type.FullName, name);
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
                object? renderer = RequireField(handler.GetType(), "Renderer").GetValue(handler);
                if (renderer?.GetType().FullName == fullTypeName) return renderer;
            }
        }

        throw new InvalidOperationException($"Required engine renderer {fullTypeName} is unavailable.");
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
