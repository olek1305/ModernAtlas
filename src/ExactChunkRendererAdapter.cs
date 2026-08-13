using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
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
    private const string VisibilityPatchId = "modernatlas.exactchunkvisibility";
    private const string AtlasFilterMarker = "// MODERNATLAS_SURFACE_AND_BOUNDARY_FILTER";
    private const int CaveFilterTextureUnit = 12;
    private const int MapLayerTextureUnit = 13;
    private const int OreMappingTextureUnit = 14;
    private const int OreStoneTextureUnit = 15;
    private const int VegetationMaskTextureUnit = 10;
    private const int OpaqueDepthTextureUnit = 11;
    private const float VisibleSubsurfaceDepth = 3f;
    private const float CaveEntranceConcealmentDepth = 1.5f;
    private static readonly Vec3f CaveConcealmentColor = new(0.24f, 0.25f, 0.25f);

    private static readonly EnumShaderProgram[] AtlasFilterPrograms =
    {
        EnumShaderProgram.Chunkopaque,
        EnumShaderProgram.Chunktopsoil,
        EnumShaderProgram.Chunktransparent
    };

    [ThreadStatic]
    private static bool atlasVisibilityOverride;

    [ThreadStatic]
    private static bool atlasTerrainCollectionOverride;

    [ThreadStatic]
    private static bool atlasLiquidVisibilityOverride;

    [ThreadStatic]
    private static HashSet<(int X, int Z)>? atlasVisibleTerrainColumns;

    [ThreadStatic]
    private static HashSet<(int X, int Z)>? atlasConsideredTerrainColumns;

    [ThreadStatic]
    private static ExactChunkRendererAdapter? atlasLiquidAdapter;

    [ThreadStatic]
    private static bool atlasOreTextureBindingOverride;

    [ThreadStatic]
    private static bool atlasVegetationTextureBindingOverride;

    [ThreadStatic]
    private static bool atlasOreTextureBindingRecursion;

    [ThreadStatic]
    private static ExactChunkRendererAdapter? atlasOreTextureBindingAdapter;

    private readonly ICoreClientAPI capi;
    private readonly object game;
    private readonly FieldInfo chunkRendererField;
    private readonly object chunkRenderer;
    private readonly object mainCamera;
    private readonly object platform;
    private readonly object beforeOitRenderer;
    private readonly object afterOitRenderer;
    private readonly VolumetricCloudRendererAdapter? cloudRenderer;
    private readonly AtlasEntityModelRendererAdapter entityModelRenderer;
    private readonly AtlasOreTextureReplacement oreTextureReplacement;
    private readonly AtlasVegetationTextureMask vegetationTextureMask;
    private readonly Func<IShaderProgram?> stableLiquidShaderProvider;
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
    private readonly Harmony visibilityHarmony;
    private bool disabled;
    private bool transparentPassDisabled;
    private bool loggedSuccess;
    private bool loggedTransparentSuccess;
    private bool loggedStableLiquidDiagnostics;
    private readonly HashSet<(int X, int Z)> visibleTerrainColumns = new();
    private readonly HashSet<(int X, int Z)> consideredTerrainColumns = new();
    private readonly Dictionary<(int X, int Y, int Z), bool> liquidChunkCompletion = new();
    private int allowedLiquidLocationCount;
    private int rejectedLiquidLocationCount;
    private Vec3f atlasSunDirection = new(-0.34f, 0.86f, -0.38f);
    private Vec3f atlasSunColor = new(1f, 0.96f, 0.86f);
    private float atlasExposure = 1f;
    private float atlasTextureMipBias;
    private Vec3f atlasFogColor = new(0.32f, 0.38f, 0.40f);
    private float atlasBoundarySoftness = 1f;
    private float atlasCaveMaskBrightness = 1f;
    private readonly Dictionary<EnumShaderProgram, AtlasFilterShaderState> atlasFilterShaders = new();
    private readonly HashSet<EnumShaderProgram> atlasFilterInjectionFailures = new();
    private bool loggedCaveFilterReady;
    private bool loggedUndergroundSafetyFailure;
    private bool loggedOreTextureBindingFailure;
    private bool loggedPreparationClearFailure;
    private bool loggedNormalWorldShaderRestore;
    private bool loggedTerrainCoverage;
    private bool disposed;

    public int LastRenderedEntityCount { get; private set; }
    public int LastSuppressedHeldItemCount =>
        entityModelRenderer.LastSuppressedHeldItemCount;
    public IReadOnlyList<AtlasRenderedEntity> LastRenderedEntities =>
        entityModelRenderer.LastRenderedEntities;
    public IReadOnlyCollection<(int X, int Z)> CompletedTerrainColumns =>
        consideredTerrainColumns;
    public int LastRenderedTextureDetailReduction { get; private set; }
    public bool LastRenderedVegetationHidden { get; private set; }
    public bool LastRenderedPerformanceLightingEnabled { get; private set; } = true;

    private sealed class AtlasFilterShaderState
    {
        public IShaderProgram Shader { get; }
        public string OriginalFragmentCode { get; }

        public AtlasFilterShaderState(IShaderProgram shader, string originalFragmentCode)
        {
            Shader = shader;
            OriginalFragmentCode = originalFragmentCode;
        }
    }

    private ExactChunkRendererAdapter(
        ICoreClientAPI capi,
        object game,
        FieldInfo chunkRendererField,
        object chunkRenderer,
        object mainCamera,
        object platform,
        object beforeOitRenderer,
        object afterOitRenderer,
        VolumetricCloudRendererAdapter? cloudRenderer,
        Func<IShaderProgram?> stableLiquidShaderProvider,
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
        PropertyInfo ambientSceneBrightnessProperty,
        Harmony visibilityHarmony
    )
    {
        this.capi = capi;
        this.game = game;
        this.chunkRendererField = chunkRendererField;
        this.chunkRenderer = chunkRenderer;
        this.mainCamera = mainCamera;
        this.platform = platform;
        this.beforeOitRenderer = beforeOitRenderer;
        this.afterOitRenderer = afterOitRenderer;
        this.cloudRenderer = cloudRenderer;
        entityModelRenderer = new AtlasEntityModelRendererAdapter(capi);
        oreTextureReplacement = new AtlasOreTextureReplacement(capi);
        vegetationTextureMask = new AtlasVegetationTextureMask(capi);
        this.stableLiquidShaderProvider = stableLiquidShaderProvider;
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
        this.visibilityHarmony = visibilityHarmony;
    }

    public static ExactChunkRendererAdapter? TryCreate(
        ICoreClientAPI capi,
        Func<IShaderProgram?> stableLiquidShaderProvider,
        Func<IShaderProgram?> atlasCloudShaderProvider
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

        Harmony? integrationHarmony = null;
        VolumetricCloudRendererAdapter? cloudRenderer = null;
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
            cloudRenderer = VolumetricCloudRendererAdapter.TryCreate(
                capi,
                game,
                atlasCloudShaderProvider
            );
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
            MethodInfo modelVisibility = RequireMethod(
                typeof(ModelDataPoolLocation),
                nameof(ModelDataPoolLocation.IsVisible),
                typeof(EnumFrustumCullMode),
                typeof(FrustumCulling)
            );
            MethodInfo atlasVisibilityPrefix = RequireMethod(
                typeof(ExactChunkRendererAdapter),
                nameof(UseAtlasVisibility),
                typeof(ModelDataPoolLocation),
                typeof(EnumFrustumCullMode),
                typeof(FrustumCulling),
                typeof(bool).MakeByRefType()
            );
            IShaderProgram chunkShader = capi.Render.GetEngineShader(
                EnumShaderProgram.Chunkopaque
            );
            MethodInfo bindTexture = RequireImplementedMethod(
                chunkShader.GetType(),
                nameof(IShaderProgram.BindTexture2D),
                typeof(string),
                typeof(int),
                typeof(int)
            );
            MethodInfo atlasOreTexturePostfix = RequireMethod(
                typeof(ExactChunkRendererAdapter),
                nameof(BindAtlasOreTexturesAfterTerrainTexture),
                typeof(object),
                typeof(object[])
            );
            integrationHarmony = new Harmony(VisibilityPatchId);
            integrationHarmony.Patch(
                modelVisibility,
                prefix: new HarmonyMethod(atlasVisibilityPrefix)
            );
            integrationHarmony.Patch(
                bindTexture,
                postfix: new HarmonyMethod(atlasOreTexturePostfix)
            );
            capi.Logger.Notification(
                "[ModernAtlas] Vintage Story 1.22.6 exact chunk renderer is available."
            );
            return new ExactChunkRendererAdapter(
                capi,
                game,
                rendererField,
                renderer,
                camera,
                platform,
                beforeOitRenderer,
                afterOitRenderer,
                cloudRenderer,
                stableLiquidShaderProvider,
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
                ambientSceneBrightness,
                integrationHarmony
            );
        }
        catch (Exception exception)
        {
            integrationHarmony?.UnpatchAll(VisibilityPatchId);
            cloudRenderer?.Dispose();
            capi.Logger.Warning(
                "[ModernAtlas] Exact chunk renderer is unavailable; using the compatible atlas renderer: {0}",
                exception.Message
            );
            return null;
        }
    }

    public bool ReferencesCurrentChunkRenderer()
    {
        return !disposed
            && ReferenceEquals(chunkRendererField.GetValue(game), chunkRenderer);
    }

    public void ResetTerrainCoverageDiagnostics()
    {
        loggedTerrainCoverage = false;
        loggedStableLiquidDiagnostics = false;
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
        bool hideUndergroundCaves,
        bool concealSurvivalOres,
        AtlasSurfaceHeightTexture? surfaceHeightTexture,
        AtlasMapLayerTexture? mapLayerTexture,
        float mapLayerOpacity,
        int textureDetailReduction,
        bool performanceLightingEnabled,
        bool hideVegetation,
        Vec3f fogColor,
        float visualExposureMultiplier,
        float boundarySoftness,
        float caveMaskBrightness,
        float windWaveCounter,
        float windWaveCounterHighFrequency,
        float waterStillCounter,
        float waterFlowCounter,
        bool cloudsEnabled,
        bool liveLightingEnabled,
        int fixedSunHour,
        float pausedCloudAnimationDeltaTime,
        ModernAtlasServerPolicy entityPolicy,
        bool blitToDefault
    )
    {
        if (disabled) return false;
        if (concealSurvivalOres && !oreTextureReplacement.Advance()) return false;
        if (hideVegetation && !vegetationTextureMask.Advance()) return false;

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
        long renderStartedMilliseconds = capi.ElapsedMilliseconds;
        long sceneSetupCompletedMilliseconds = renderStartedMilliseconds;
        long opaqueCompletedMilliseconds = renderStartedMilliseconds;
        long entitiesCompletedMilliseconds = renderStartedMilliseconds;
        long transparentCompletedMilliseconds = renderStartedMilliseconds;
        bool atlasFilterConfigured = false;

        atlasFogColor = new Vec3f(
            Math.Clamp(fogColor.X, 0f, 1f),
            Math.Clamp(fogColor.Y, 0f, 1f),
            Math.Clamp(fogColor.Z, 0f, 1f)
        );
        atlasBoundarySoftness = Math.Clamp(boundarySoftness, 0.25f, 2f);
        atlasCaveMaskBrightness = Math.Clamp(caveMaskBrightness, 0.5f, 1.5f);
        atlasTextureMipBias = Math.Clamp(textureDetailReduction, 0, 2);
        LastRenderedTextureDetailReduction = (int)atlasTextureMipBias;
        LastRenderedVegetationHidden = hideVegetation;
        LastRenderedPerformanceLightingEnabled = performanceLightingEnabled;

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
                performanceLightingEnabled,
                liveLightingEnabled,
                fixedSunHour,
                savedAmbientColor,
                savedSceneBrightness,
                savedLightPosition,
                savedSkyDaylight,
                visualExposureMultiplier
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

            // The official chunk shaders write to the multi-attachment Primary
            // world framebuffer. Rendering them into the default GUI target
            // produces no color even though the draw call succeeds.
            FrameBufferRef primaryFramebuffer = render.FrameBuffers[(int)EnumFrameBuffer.Primary];
            float[] atlasBackground = fogEnabled
                ? new[] { atlasFogColor.X, atlasFogColor.Y, atlasFogColor.Z, 1f }
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
            sceneSetupCompletedMilliseconds = capi.ElapsedMilliseconds;
            // The engine's completed mesh locations retain an occlusion flag
            // calculated on a worker thread for the normal player camera.
            // During this draw only, use the atlas frustum without that stale
            // player-camera flag. Hide and LOD/frustum checks still apply.
            atlasVisibilityOverride = true;

            render.PMatrix.Push(projectionDouble);
            projectionPushed = true;
            render.CurrentActiveShader?.Stop();
            visibleTerrainColumns.Clear();
            consideredTerrainColumns.Clear();
            atlasVisibleTerrainColumns = visibleTerrainColumns;
            atlasConsideredTerrainColumns = consideredTerrainColumns;
            atlasTerrainCollectionOverride = true;
            try
            {
                atlasFilterConfigured = true;
                if ((hideUndergroundCaves && surfaceHeightTexture?.Ready != true)
                    || !ConfigureAtlasFilters(
                        surfaceHeightTexture,
                        mapLayerTexture,
                        mapLayerOpacity,
                        cameraPosition,
                        true,
                        hideUndergroundCaves,
                        concealSurvivalOres,
                        hideVegetation,
                        fogEnabled,
                        viewDistanceBlocks
                    ))
                {
                    if (!loggedUndergroundSafetyFailure)
                    {
                        loggedUndergroundSafetyFailure = true;
                        capi.Logger.Error(
                            "[ModernAtlas] The terrain safety filters are unavailable; terrain drawing was skipped to avoid revealing caves or terrain beyond the disclosure boundary."
                        );
                    }
                    return false;
                }
                BeginAtlasTextureBindings(concealSurvivalOres, hideVegetation);
                try
                {
                    renderOpaque.Invoke(chunkRenderer, new object[] { deltaTime });
                }
                finally
                {
                    EndAtlasTextureBindings();
                }
            }
            finally
            {
                atlasTerrainCollectionOverride = false;
            }
            opaqueCompletedMilliseconds = capi.ElapsedMilliseconds;
            LogTerrainCoverage(viewDistanceBlocks);
            LastRenderedEntityCount = entityModelRenderer.Render(
                deltaTime,
                view,
                projection,
                viewDistanceBlocks,
                entityPolicy,
                hideUndergroundCaves ? surfaceHeightTexture : null
            );
            entitiesCompletedMilliseconds = capi.ElapsedMilliseconds;
            if (!RenderTransparentChunks(
                deltaTime,
                projection,
                view,
                cameraPosition,
                waterStillCounter,
                waterFlowCounter,
                cloudsEnabled,
                pausedCloudAnimationDeltaTime,
                hideUndergroundCaves,
                concealSurvivalOres,
                hideVegetation,
                surfaceHeightTexture,
                mapLayerTexture,
                mapLayerOpacity,
                fogEnabled,
                viewDistanceBlocks,
                blitToDefault
            ))
            {
                if (blitToDefault)
                {
                    blitPrimaryToDefault.Invoke(platform, Array.Empty<object>());
                }
            }
            transparentCompletedMilliseconds = capi.ElapsedMilliseconds;

            if (!loggedSuccess)
            {
                loggedSuccess = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Rendering the atlas from the game's completed chunk meshes and materials."
                );
                capi.Logger.Notification(
                    "[ModernAtlas] Disabled the normal camera sky-horizon tint only inside the atlas terrain shader."
                );
                capi.Logger.Notification(
                    "[ModernAtlas] First atlas render timing: setup {0} ms, opaque terrain {1} ms, living entities {2} ms, transparent/liquids/clouds {3} ms, total {4} ms.",
                    sceneSetupCompletedMilliseconds - renderStartedMilliseconds,
                    opaqueCompletedMilliseconds - sceneSetupCompletedMilliseconds,
                    entitiesCompletedMilliseconds - opaqueCompletedMilliseconds,
                    transparentCompletedMilliseconds - entitiesCompletedMilliseconds,
                    transparentCompletedMilliseconds - renderStartedMilliseconds
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
            atlasTerrainCollectionOverride = false;
            atlasLiquidVisibilityOverride = false;
            atlasVisibleTerrainColumns = null;
            atlasConsideredTerrainColumns = null;
            atlasLiquidAdapter = null;
            EndAtlasTextureBindings();
            atlasVisibilityOverride = false;
            if (atlasFilterConfigured)
            {
                try
                {
                    ConfigureAtlasFilters(
                        null,
                        null,
                        0,
                        cameraPosition,
                        false,
                        false,
                        false,
                        false,
                        false,
                        0
                    );
                }
                catch (Exception exception)
                {
                    disabled = true;
                    capi.Logger.Error(
                        "[ModernAtlas] Failed to restore the normal terrain shader state; exact atlas rendering was disabled: {0}",
                        exception.Message
                    );
                }
            }
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
        bool performanceLightingEnabled,
        bool liveLightingEnabled,
        int fixedSunHour,
        Vec3f liveAmbientColor,
        float liveSceneBrightness,
        Vec3f liveLightPosition,
        float liveSkyDaylight,
        float visualExposureMultiplier
    )
    {
        float visualExposure = Math.Clamp(visualExposureMultiplier, 0.5f, 1.5f);
        if (!performanceLightingEnabled)
        {
            Vec3f neutralLight = new(1f, 1f, 1f);
            Vec3f overheadLight = new(0.08f, 0.99f, -0.10f);
            ambientColorProperty.SetValue(
                ambient,
                ScaleColor(neutralLight, visualExposure)
            );
            ambientSceneBrightnessProperty.SetValue(
                ambient,
                Math.Clamp(visualExposure, 0.5f, 1.5f)
            );
            shaderUniforms.LightPosition3D = overheadLight;
            skyDaylightUniformField.SetValue(shaderUniforms, 1f);
            atlasSunDirection = overheadLight;
            atlasSunColor = neutralLight;
            atlasExposure = visualExposure;
            return;
        }
        IClientGameCalendar? calendar = capi.World.Calendar as IClientGameCalendar;
        if (liveLightingEnabled && calendar != null)
        {
            float daylight = Math.Clamp(calendar.DayLightStrength, 0f, 1f);
            Vec3f sunDirection = NormalizeDirection(
                calendar.SunPositionNormalized,
                liveLightPosition
            );
            bool moonlit = sunDirection.Y < -0.04f
                && calendar.MoonLightStrength > calendar.SunLightStrength;
            Vec3f lightDirection = moonlit
                ? NormalizeDirection(calendar.MoonPosition, liveLightPosition)
                : sunDirection;
            lightDirection = KeepDirectionalLightAboveTerrain(lightDirection);
            Vec3f atmosphericColor = moonlit
                ? NightLightColor
                : GetAtmosphericLightColor(
                    daylight,
                    sunDirection.Y,
                    calendar.Dusk,
                    calendar.SunColor
                );
            Vec3f tintedAmbient = TintAmbientColor(
                liveAmbientColor,
                atmosphericColor,
                0.52f
            );
            ambientColorProperty.SetValue(
                ambient,
                ScaleColor(tintedAmbient, visualExposure)
            );
            ambientSceneBrightnessProperty.SetValue(
                ambient,
                Math.Clamp(liveSceneBrightness * visualExposure, 0.02f, 1.5f)
            );
            shaderUniforms.LightPosition3D = lightDirection;
            skyDaylightUniformField.SetValue(shaderUniforms, daylight);
            atlasSunDirection = lightDirection;
            atlasSunColor = atmosphericColor;
            atlasExposure = Math.Clamp(
                Math.Max(0.12f, daylight) * Math.Max(0.2f, liveSceneBrightness)
                    * visualExposure,
                0.04f,
                1.5f
            );
            return;
        }
        if (liveLightingEnabled)
        {
            ambientColorProperty.SetValue(
                ambient,
                ScaleColor(liveAmbientColor, visualExposure)
            );
            ambientSceneBrightnessProperty.SetValue(
                ambient,
                Math.Clamp(liveSceneBrightness * visualExposure, 0.02f, 1.5f)
            );
            shaderUniforms.LightPosition3D = liveLightPosition;
            atlasSunDirection = NormalizeDirection(
                liveLightPosition,
                atlasSunDirection
            );
            atlasSunColor = DayLightColor;
            atlasExposure = Math.Clamp(
                liveSkyDaylight * Math.Max(0.2f, liveSceneBrightness)
                    * visualExposure,
                0.04f,
                1.5f
            );
            return;
        }

        int hour = Math.Clamp(fixedSunHour, 0, 23);
        Vec3f fixedSunDirection;
        Vec3f fixedMoonDirection;
        if (calendar != null)
        {
            double hoursPerDay = Math.Max(1.0, calendar.HoursPerDay);
            double fixedTotalDays = calendar.TotalDays
                + (hour - calendar.HourOfDay) / hoursPerDay;
            Vec3d playerPosition = new(
                capi.World.Player.Entity.Pos.X,
                capi.World.Player.Entity.Pos.Y,
                capi.World.Player.Entity.Pos.Z
            );
            fixedSunDirection = NormalizeDirection(
                calendar.GetSunPosition(playerPosition, fixedTotalDays),
                liveLightPosition
            );
            fixedMoonDirection = NormalizeDirection(
                calendar.GetMoonPosition(playerPosition, fixedTotalDays),
                new Vec3f(
                    -fixedSunDirection.X,
                    Math.Abs(fixedSunDirection.Y),
                    -fixedSunDirection.Z
                )
            );
        }
        else
        {
            float phase = (hour - 6f) / 24f * GameMath.TWOPI;
            fixedSunDirection = NormalizeDirection(
                new Vec3f(
                    MathF.Cos(phase + 0.45f),
                    MathF.Sin(phase),
                    MathF.Sin(phase + 0.45f)
                ),
                liveLightPosition
            );
            fixedMoonDirection = new Vec3f(
                -fixedSunDirection.X,
                Math.Abs(fixedSunDirection.Y),
                -fixedSunDirection.Z
            );
        }

        float fixedDaylight = SmoothStep(-0.10f, 0.20f, fixedSunDirection.Y);
        bool fixedDusk = hour >= 12;
        Vec3f fixedLightColor = GetAtmosphericLightColor(
            fixedDaylight,
            fixedSunDirection.Y,
            fixedDusk,
            null
        );
        Vec3f fixedLightDirection = fixedSunDirection.Y < -0.04f
            ? fixedMoonDirection
            : fixedSunDirection;
        fixedLightDirection = KeepDirectionalLightAboveTerrain(fixedLightDirection);
        shaderUniforms.LightPosition3D = fixedLightDirection;
        skyDaylightUniformField.SetValue(shaderUniforms, fixedDaylight);
        atlasSunDirection = fixedLightDirection;
        atlasSunColor = fixedLightColor;
        float fixedBrightness = 0.20f + fixedDaylight * 0.80f;
        atlasExposure = Math.Clamp(
            fixedBrightness * visualExposure,
            0.04f,
            1.5f
        );
        ambientColorProperty.SetValue(
            ambient,
            ScaleColor(fixedLightColor, visualExposure)
        );
        ambientSceneBrightnessProperty.SetValue(
            ambient,
            Math.Clamp(fixedBrightness * visualExposure, 0.02f, 1.5f)
        );
    }

    private static readonly Vec3f NightLightColor = new(0.30f, 0.42f, 0.72f);
    private static readonly Vec3f DayLightColor = new(1.00f, 0.97f, 0.88f);
    private static readonly Vec3f DawnLightColor = new(1.00f, 0.68f, 0.34f);
    private static readonly Vec3f DuskLightColor = new(1.00f, 0.43f, 0.18f);

    private static Vec3f GetAtmosphericLightColor(
        float daylight,
        float solarAltitude,
        bool dusk,
        Vec3f? nativeSunColor
    )
    {
        float dayAmount = Math.Clamp(daylight, 0f, 1f);
        Vec3f color = MixColor(NightLightColor, DayLightColor, dayAmount);
        float horizonAmount = GetHorizonAmount(dayAmount, solarAltitude);
        color = MixColor(
            color,
            dusk ? DuskLightColor : DawnLightColor,
            horizonAmount * 0.82f
        );
        if (nativeSunColor != null && dayAmount > 0.02f)
        {
            Vec3f normalizedNativeColor = NormalizeColor(nativeSunColor);
            color = MixColor(color, normalizedNativeColor, dayAmount * 0.38f);
        }
        return color;
    }

    private static float GetHorizonAmount(float daylight, float solarAltitude) =>
        Math.Clamp(daylight, 0f, 1f)
            * (1f - SmoothStep(0.08f, 0.55f, solarAltitude));

    private static Vec3f TintAmbientColor(Vec3f ambient, Vec3f tint, float amount)
    {
        Vec3f tinted = new(
            ambient.X * (0.5f + 0.5f * tint.X),
            ambient.Y * (0.5f + 0.5f * tint.Y),
            ambient.Z * (0.5f + 0.5f * tint.Z)
        );
        return MixColor(ambient, tinted, Math.Clamp(amount, 0f, 1f));
    }

    private static Vec3f NormalizeDirection(Vec3f direction, Vec3f fallback)
    {
        float length = MathF.Sqrt(
            direction.X * direction.X
                + direction.Y * direction.Y
                + direction.Z * direction.Z
        );
        if (length < 0.0001f) return fallback;
        return new Vec3f(
            direction.X / length,
            direction.Y / length,
            direction.Z / length
        );
    }

    private static Vec3f KeepDirectionalLightAboveTerrain(Vec3f direction)
    {
        // The native chunk programs expect the directional light to illuminate
        // terrain from above. A calendar moon can legitimately be below the
        // horizon; feeding that vector into block-face lighting produces large
        // dark mesh patches that resemble square chunk shadows. Preserve the
        // real azimuth while using a shallow, stable sky elevation.
        return NormalizeDirection(
            new Vec3f(direction.X, Math.Max(0.16f, Math.Abs(direction.Y)), direction.Z),
            new Vec3f(-0.34f, 0.86f, -0.38f)
        );
    }

    private static Vec3f NormalizeColor(Vec3f color)
    {
        float maximum = Math.Max(0.001f, Math.Max(color.X, Math.Max(color.Y, color.Z)));
        if (maximum <= 0.001f) return DayLightColor;
        return new Vec3f(
            Math.Clamp(color.X / maximum, 0f, 1f),
            Math.Clamp(color.Y / maximum, 0f, 1f),
            Math.Clamp(color.Z / maximum, 0f, 1f)
        );
    }

    private static Vec3f MixColor(Vec3f from, Vec3f to, float amount)
    {
        float weight = Math.Clamp(amount, 0f, 1f);
        return new Vec3f(
            from.X + (to.X - from.X) * weight,
            from.Y + (to.Y - from.Y) * weight,
            from.Z + (to.Z - from.Z) * weight
        );
    }

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        float progress = Math.Clamp((value - minimum) / (maximum - minimum), 0f, 1f);
        return progress * progress * (3f - 2f * progress);
    }

    private static Vec3f ScaleColor(Vec3f color, float scale) => new(
        Math.Clamp(color.X * scale, 0f, 1.5f),
        Math.Clamp(color.Y * scale, 0f, 1.5f),
        Math.Clamp(color.Z * scale, 0f, 1.5f)
    );

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        // LeaveWorld is raised after the engine has started clearing its
        // DefaultShaderUniforms arrays. Activating an engine chunk shader at
        // that point makes ShaderProgramBase.Use upload a null
        // colorMapRects[40] array and can crash Mesa inside glUniform4fv.
        // Every atlas render disables its switches in a finally block.
        // Teardown therefore restores only managed shader source and Harmony
        // state; doing shader work from OnGuiClosed would interrupt the
        // engine's active GUI render pass.
        RestoreAtlasFilterSources();
        visibilityHarmony.UnpatchAll(VisibilityPatchId);
        oreTextureReplacement.Dispose();
        vegetationTextureMask.Dispose();
        cloudRenderer?.Dispose();
    }

    public void RestoreNormalWorldShaders()
    {
        if (disposed || atlasFilterShaders.Count == 0) return;

        DefaultShaderUniforms uniforms = capi.Render.ShaderUniforms;
        if (uniforms.ColorMapRects4 == null
            || uniforms.ColorMapRects4.Length < 40 * 4)
        {
            return;
        }

        try
        {
            // Keep the three already compiled chunk programs for this world
            // and disable only ModernAtlas' conditional paths. Restoring the
            // source and calling ReloadShaders() recompiles every game and mod
            // shader, which caused a visible freeze whenever G was closed.
            DisableAtlasFilterUniforms();

            if (!loggedNormalWorldShaderRestore)
            {
                loggedNormalWorldShaderRestore = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Disabled atlas-only chunk shader paths without reloading world shaders."
                );
            }
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not disable the atlas-only chunk shader paths: {0}",
                exception.Message
            );
        }
    }

    public void RenderSurfacePreparationFrame(
        bool fogEnabled,
        Vec3f fogColor,
        bool blitToDefault
    )
    {
        if (disabled) return;

        try
        {
            IRenderAPI render = capi.Render;
            render.CurrentActiveShader?.Stop();
            FrameBufferRef primaryFramebuffer = render.FrameBuffers[(int)EnumFrameBuffer.Primary];
            float[] atlasBackground = fogEnabled
                ? new[] { fogColor.X, fogColor.Y, fogColor.Z, 1f }
                : new[] { 0.035f, 0.075f, 0.11f, 1f };
            render.ClearFrameBuffer(primaryFramebuffer, atlasBackground, true, true);
            if (blitToDefault)
            {
                blitPrimaryToDefault.Invoke(platform, Array.Empty<object>());
            }
            render.CurrentFrameBuffer = null;
        }
        catch (Exception exception)
        {
            if (!loggedPreparationClearFailure)
            {
                loggedPreparationClearFailure = true;
                capi.Logger.Warning(
                    "[ModernAtlas] Could not draw the atlas preparation frame: {0}",
                    exception.Message
                );
            }
        }
    }

    public bool AdvanceSurvivalOreConcealment() =>
        !disabled && oreTextureReplacement.Advance();

    public bool ValidateSurvivalOreConcealment(out string diagnostic) =>
        oreTextureReplacement.Validate(out diagnostic);

    public bool AdvanceVegetationMask() =>
        !disabled && vegetationTextureMask.Advance();

    public bool ValidateVegetationMask(out string diagnostic) =>
        vegetationTextureMask.Validate(out diagnostic);

    public int PrimaryColorTextureId
    {
        get
        {
            FrameBufferRef primary = capi.Render.FrameBuffers[(int)EnumFrameBuffer.Primary];
            return primary.ColorTextureIds is { Length: > 0 }
                ? primary.ColorTextureIds[0]
                : 0;
        }
    }

    private bool ConfigureAtlasFilters(
        AtlasSurfaceHeightTexture? surfaceHeightTexture,
        AtlasMapLayerTexture? mapLayerTexture,
        float mapLayerOpacity,
        Vec3d cameraPosition,
        bool enabled,
        bool hideUndergroundCaves,
        bool concealSurvivalOres,
        bool hideVegetation,
        bool fogEnabled,
        int disclosureRadius
    )
    {
        if (!enabled)
        {
            DisableAtlasFilterUniforms();
            return true;
        }

        if ((hideUndergroundCaves
                && (surfaceHeightTexture?.Ready != true
                    || surfaceHeightTexture.TextureId <= 0))
            || (concealSurvivalOres && !oreTextureReplacement.Ready)
            || (hideVegetation && !vegetationTextureMask.Ready)
            || !EnsureAtlasFilterShaders())
        {
            return false;
        }

        bool applyMapLayer = mapLayerTexture?.Ready == true
            && mapLayerTexture.TextureId > 0
            && mapLayerTexture.Layer != AtlasMapLayer.TexturedTerrain;

        try
        {
            foreach (EnumShaderProgram program in AtlasFilterPrograms)
            {
                IShaderProgram shader = atlasFilterShaders[program].Shader;
                capi.Render.CurrentActiveShader?.Stop();
                shader.Use();
                shader.Uniform("atlasHideCaves", hideUndergroundCaves ? 1 : 0);
                shader.Uniform(
                    "atlasConcealOres",
                    concealSurvivalOres ? 1 : 0
                );
                if (shader.HasUniform("atlasHideVegetation"))
                {
                    shader.Uniform("atlasHideVegetation", hideVegetation ? 1 : 0);
                }
                if (shader.HasUniform("atlasTextureMipBias"))
                {
                    shader.Uniform("atlasTextureMipBias", atlasTextureMipBias);
                }
                if (shader.HasUniform("atlasCaveConcealmentColor"))
                {
                    shader.Uniform(
                        "atlasCaveConcealmentColor",
                        ScaleColor(CaveConcealmentColor, atlasCaveMaskBrightness)
                    );
                }
                if (hideUndergroundCaves && surfaceHeightTexture != null)
                {
                    shader.BindTexture2D(
                        "atlasSurfaceHeightTex",
                        surfaceHeightTexture.TextureId,
                        CaveFilterTextureUnit
                    );
                    shader.Uniform(
                        "atlasSurfaceOriginXZ",
                        (float)surfaceHeightTexture.OriginX,
                        (float)surfaceHeightTexture.OriginZ
                    );
                    shader.Uniform(
                        "atlasSurfaceSampleSize",
                        (float)AtlasSurfaceHeightTexture.HorizontalSampleSize
                    );
                    shader.Uniform("atlasVisibleSubsurfaceDepth", VisibleSubsurfaceDepth);
                    if (shader.HasUniform("atlasCaveConcealmentDepth"))
                    {
                        shader.Uniform(
                            "atlasCaveConcealmentDepth",
                            CaveEntranceConcealmentDepth
                        );
                    }
                }
                if (shader.HasUniform("atlasLayerEnabled"))
                {
                    shader.Uniform("atlasLayerEnabled", applyMapLayer ? 1 : 0);
                    if (applyMapLayer && mapLayerTexture != null)
                    {
                        shader.BindTexture2D(
                            "atlasLayerTex",
                            mapLayerTexture.TextureId,
                            MapLayerTextureUnit
                        );
                        shader.Uniform(
                            "atlasLayerOriginXZ",
                            (float)mapLayerTexture.OriginX,
                            (float)mapLayerTexture.OriginZ
                        );
                        shader.Uniform(
                            "atlasLayerSampleSize",
                            (float)AtlasMapLayerTexture.HorizontalSampleSize
                        );
                        shader.Uniform(
                            "atlasLayerOpacity",
                            Math.Clamp(mapLayerOpacity, 0f, 1f)
                        );
                    }
                }
                shader.Uniform(
                    "atlasWorldOffset",
                    (float)cameraPosition.X,
                    (float)cameraPosition.Y,
                    (float)cameraPosition.Z
                );
                if (shader.HasUniform("atlasDisableHorizonFade"))
                {
                    shader.Uniform("atlasDisableHorizonFade", 1);
                }
                if (shader.HasUniform("atlasDisableLod0Fade"))
                {
                    // Leaves and other exact LOD0 meshes are already bounded
                    // by the game's loaded chunk set. The normal perspective
                    // shader fade must not remove them from a distant atlas
                    // view while leaving their non-LOD trunks behind.
                    shader.Uniform("atlasDisableLod0Fade", 1);
                }
                shader.Stop();
            }
            return true;
        }
        catch
        {
            DisableAtlasFilterUniforms();
            throw;
        }
    }

    private bool EnsureAtlasFilterShaders()
    {
        foreach (EnumShaderProgram program in AtlasFilterPrograms)
        {
            if (!EnsureAtlasFilterShader(
                program,
                program != EnumShaderProgram.Chunktransparent
            ))
            {
                return false;
            }
        }

        if (!loggedCaveFilterReady)
        {
            loggedCaveFilterReady = true;
            capi.Logger.Notification(
                "[ModernAtlas] Cave safety uses the local surface filter at every height: opaque terrain keeps its {0}-block exterior layer with a {1:0.0}-block neutral band, while deeper opaque, transparent and liquid geometry is discarded.",
                VisibleSubsurfaceDepth,
                CaveEntranceConcealmentDepth
            );
        }
        return true;
    }

    private bool EnsureAtlasFilterShader(
        EnumShaderProgram program,
        bool supportsBoundaryColor
    )
    {
        if (atlasFilterInjectionFailures.Contains(program)) return false;

        IShaderProgram shader = capi.Render.GetEngineShader(program);
        if (shader.Disposed || shader.FragmentShader == null) return false;
        string source = shader.FragmentShader.Code ?? "";
        if (atlasFilterShaders.TryGetValue(program, out AtlasFilterShaderState? state)
            && ReferenceEquals(shader, state.Shader)
            && source.Contains(AtlasFilterMarker, StringComparison.Ordinal))
        {
            return shader.HasUniform("atlasHideCaves")
                && shader.HasUniform("atlasConcealOres")
                && shader.HasUniform("atlasHideVegetation")
                && shader.HasUniform("atlasVegetationMaskTex");
        }

        if (state != null && !ReferenceEquals(shader, state.Shader))
        {
            RestoreAtlasFilterSource(program);
        }

        string? injected = InjectAtlasFilter(source, supportsBoundaryColor);
        if (injected == null)
        {
            atlasFilterInjectionFailures.Add(program);
            capi.Logger.Error(
                "[ModernAtlas] Could not locate the main function in engine shader {0}.",
                program
            );
            return false;
        }

        capi.Render.CurrentActiveShader?.Stop();
        shader.FragmentShader.Code = injected;
        if (!shader.Compile())
        {
            shader.FragmentShader.Code = source;
            shader.Compile();
            atlasFilterInjectionFailures.Add(program);
            capi.Logger.Error(
                "[ModernAtlas] Failed to compile the atlas safety filter for engine shader {0}; the original shader was restored.",
                program
            );
            return false;
        }

        atlasFilterShaders[program] = new AtlasFilterShaderState(shader, source);
        return shader.HasUniform("atlasHideCaves")
            && shader.HasUniform("atlasConcealOres")
            && shader.HasUniform("atlasHideVegetation")
            && shader.HasUniform("atlasVegetationMaskTex");
    }

    private void DisableAtlasFilterUniforms()
    {
        DefaultShaderUniforms uniforms = capi.Render.ShaderUniforms;
        if (uniforms.ColorMapRects4 == null
            || uniforms.ColorMapRects4.Length < 40 * 4)
        {
            return;
        }

        foreach (AtlasFilterShaderState state in atlasFilterShaders.Values)
        {
            IShaderProgram shader = state.Shader;
            if (shader.Disposed) continue;
            try
            {
                capi.Render.CurrentActiveShader?.Stop();
                shader.Use();
                if (shader.HasUniform("atlasHideCaves"))
                {
                    shader.Uniform("atlasHideCaves", 0);
                }
                if (shader.HasUniform("atlasConcealOres"))
                {
                    shader.Uniform("atlasConcealOres", 0);
                }
                if (shader.HasUniform("atlasHideVegetation"))
                {
                    shader.Uniform("atlasHideVegetation", 0);
                }
                if (shader.HasUniform("atlasLayerEnabled"))
                {
                    shader.Uniform("atlasLayerEnabled", 0);
                }
                if (shader.HasUniform("atlasDisableHorizonFade"))
                {
                    // Compiling the atlas variant makes Vintage Story's
                    // camera-dependent haxyFade path produce a bright red
                    // chunk silhouette after the atlas closes. Keep only this
                    // faulty path disabled for the lifetime of the compiled
                    // variant; every other atlas branch is reset above.
                    shader.Uniform("atlasDisableHorizonFade", 1);
                }
                if (shader.HasUniform("atlasDisableLod0Fade"))
                {
                    shader.Uniform("atlasDisableLod0Fade", 0);
                }
                shader.Stop();
            }
            catch
            {
                // The client may already be tearing down its GL context.
            }
        }
    }

    private void RestoreAtlasFilterSources()
    {
        foreach (EnumShaderProgram program in AtlasFilterPrograms)
        {
            RestoreAtlasFilterSource(program);
        }
    }

    private void RestoreAtlasFilterSource(EnumShaderProgram program)
    {
        if (!atlasFilterShaders.Remove(program, out AtlasFilterShaderState? state)) return;
        if (state.Shader.FragmentShader != null
            && state.Shader.FragmentShader.Code?.Contains(
                AtlasFilterMarker,
                StringComparison.Ordinal
            ) == true)
        {
            // The compiled program remains safe because both atlas switches
            // were set to zero. Restoring the source ensures a later global
            // shader reload compiles the unmodified game shader.
            state.Shader.FragmentShader.Code = state.OriginalFragmentCode;
        }
    }

    private static string? InjectAtlasFilter(string source, bool supportsBoundaryColor)
    {
        int mainIndex = source.LastIndexOf("void main", StringComparison.Ordinal);
        if (mainIndex < 0) return null;

        int nameIndex = mainIndex + "void ".Length;
        if (nameIndex + "main".Length > source.Length
            || !source.AsSpan(nameIndex, "main".Length).SequenceEqual("main".AsSpan()))
        {
            return null;
        }

        string renamed = source.Remove(nameIndex, "main".Length)
            .Insert(nameIndex, "modernAtlasOriginalMain");
        const string terrainSample = "texture(terrainTex, uv)";
        if (!renamed.Contains(terrainSample, StringComparison.Ordinal))
        {
            return null;
        }
        renamed = renamed.Replace(
            terrainSample,
            "modernAtlasSampleTerrain(terrainTex, uv)",
            StringComparison.Ordinal
        );
        renamed = renamed.Insert(
            mainIndex,
            "vec4 modernAtlasSampleTerrain(sampler2D sourceTexture, vec2 sourceUv);\n\n"
        );
        const string lod0FadeTerm = "- lod0Fade";
        if (renamed.Contains(lod0FadeTerm, StringComparison.Ordinal))
        {
            renamed = renamed.Replace(
                lod0FadeTerm,
                "- (atlasDisableLod0Fade > 0 ? 0.0 : lod0Fade)",
                StringComparison.Ordinal
            );
            renamed = renamed.Insert(
                mainIndex,
                "uniform int atlasDisableLod0Fade;\n\n"
            );
        }
        const string horizonFadeCondition = "if (haxyFade > 0)";
        if (renamed.Contains(horizonFadeCondition, StringComparison.Ordinal))
        {
            renamed = renamed.Replace(
                horizonFadeCondition,
                "if (haxyFade > 0 && atlasDisableHorizonFade == 0)",
                StringComparison.Ordinal
            );
            renamed = renamed.Insert(
                mainIndex,
                "uniform int atlasDisableHorizonFade;\n\n"
            );
        }
        string mapLayerCode = supportsBoundaryColor
            ? """
    if (atlasLayerEnabled > 0)
    {
        vec2 layerPosition =
            (modernAtlasAbsoluteWorldPosition.xz - atlasLayerOriginXZ)
            / atlasLayerSampleSize;
        ivec2 layerDimensions = textureSize(atlasLayerTex, 0);
        if (all(greaterThanEqual(layerPosition, vec2(0.0)))
            && all(lessThan(layerPosition, vec2(layerDimensions))))
        {
            vec2 layerUv = layerPosition / vec2(layerDimensions);
            vec4 layerColor = texture(atlasLayerTex, layerUv);
            float baseLuminance = dot(
                clamp(outColor.rgb, vec3(0.0), vec3(1.0)),
                vec3(0.2126, 0.7152, 0.0722)
            );
            vec3 reliefColor = layerColor.rgb * mix(0.68, 1.18, baseLuminance);
            outColor.rgb = mix(
                outColor.rgb,
                reliefColor,
                clamp(layerColor.a * atlasLayerOpacity, 0.0, 1.0)
            );
        }
    }
"""
            : "";
        string caveFilterCode = supportsBoundaryColor
            ? """
    // Apply the same local surface test above and below sea level. A cave
    // entrance in a mountain is still an interior cutout; the old sea-level
    // exception let those walls and tunnels appear while chunks streamed in.
    if (atlasHideCaves > 0)
    {
        float modernAtlasExteriorFloor;
        bool modernAtlasHasExteriorFloor = modernAtlasReadExteriorFloor(
            modernAtlasAbsoluteWorldPosition,
            normal,
            modernAtlasExteriorFloor
        );
        if (!modernAtlasHasExteriorFloor
            || modernAtlasAbsoluteWorldPosition.y < modernAtlasExteriorFloor)
        {
            if (modernAtlasHasExteriorFloor && normal.y < -0.25)
            {
                // Completed downward-facing terrain is the visible underside
                // of a valley, overhang or mountain when the atlas is tilted
                // below it. Keep its authored block texture instead of making
                // the underside transparent. The surface sample is still
                // required, so an unloaded column cannot reveal a cave.
                modernAtlasOriginalMain();
                return;
            }
            if (modernAtlasHasExteriorFloor
                && modernAtlasAbsoluteWorldPosition.y
                >= modernAtlasExteriorFloor - atlasCaveConcealmentDepth)
            {
                // Keep only a thin band of real opaque faces near the exterior
                // to quiet clipped cave mouths. Deeper cave walls are discarded
                // so they cannot trace an underground tunnel network from the
                // side. This uses no generated shell or persistent geometry.
                modernAtlasOriginalMain();
                outColor = vec4(atlasCaveConcealmentColor, 1.0);
                return;
            }
            discard;
        }
    }
"""
            : """
    if (atlasHideCaves > 0)
    {
        float modernAtlasExteriorFloor;
        bool modernAtlasHasExteriorFloor = modernAtlasReadExteriorFloor(
            modernAtlasAbsoluteWorldPosition,
            normal,
            modernAtlasExteriorFloor
        );
        if (!modernAtlasHasExteriorFloor
            || modernAtlasAbsoluteWorldPosition.y < modernAtlasExteriorFloor)
        {
            // Transparent chunk geometry has no neutral concealment pass. It
            // must not remain below the same real exterior safety layer.
            discard;
        }
    }
""";

        return renamed + """

// MODERNATLAS_SURFACE_AND_BOUNDARY_FILTER
uniform int atlasHideCaves;
uniform sampler2D atlasSurfaceHeightTex;
uniform vec2 atlasSurfaceOriginXZ;
uniform float atlasSurfaceSampleSize;
uniform float atlasVisibleSubsurfaceDepth;
uniform float atlasCaveConcealmentDepth;
uniform vec3 atlasWorldOffset;
uniform vec3 atlasCaveConcealmentColor;
uniform int atlasLayerEnabled;
uniform sampler2D atlasLayerTex;
uniform vec2 atlasLayerOriginXZ;
uniform float atlasLayerSampleSize;
uniform float atlasLayerOpacity;
uniform int atlasConcealOres;
uniform float atlasTextureMipBias;
uniform sampler2D atlasOreMapTex;
uniform sampler2D atlasStoneTex;
uniform int atlasHideVegetation;
uniform sampler2D atlasVegetationMaskTex;

bool modernAtlasIsVegetation(vec2 sourceUv)
{
    ivec2 dimensions = textureSize(atlasVegetationMaskTex, 0);
    ivec2 position = clamp(
        ivec2(floor(sourceUv * vec2(dimensions))),
        ivec2(0),
        dimensions - ivec2(1)
    );
    return texelFetch(atlasVegetationMaskTex, position, 0).r > 0.5;
}

vec4 modernAtlasSampleTerrain(sampler2D sourceTexture, vec2 sourceUv)
{
    vec4 originalColor = texture(
        sourceTexture,
        sourceUv,
        atlasTextureMipBias
    );
    if (atlasConcealOres <= 0) return originalColor;

    ivec2 mappingDimensions = textureSize(atlasOreMapTex, 0);
    vec2 mappingPosition = sourceUv * vec2(mappingDimensions);
    ivec2 lookupPosition = clamp(
        ivec2(floor(mappingPosition)),
        ivec2(0),
        mappingDimensions - ivec2(1)
    );
    vec4 encodedTarget = texelFetch(atlasOreMapTex, lookupPosition, 0);
    int targetX = int(floor(encodedTarget.r * 255.0 + 0.5)) * 256
        + int(floor(encodedTarget.g * 255.0 + 0.5));
    int targetY = int(floor(encodedTarget.b * 255.0 + 0.5)) * 256
        + int(floor(encodedTarget.a * 255.0 + 0.5));
    if (targetX <= 0 || targetY <= 0) return originalColor;

    ivec2 stoneDimensions = textureSize(atlasStoneTex, 0);
    vec2 subpixelOffset = (fract(mappingPosition) - vec2(0.5)) * 4.0;
    vec2 stonePixel = vec2(targetX - 1, targetY - 1) + subpixelOffset;
    vec2 stoneUv = (stonePixel + vec2(0.5)) / vec2(stoneDimensions);
    vec4 stoneColor = texture(atlasStoneTex, stoneUv);
    return vec4(stoneColor.rgb, originalColor.a);
}

bool modernAtlasReadSurfaceHeight(ivec2 samplePosition, out float surfaceHeight)
{
    ivec2 dimensions = textureSize(atlasSurfaceHeightTex, 0);
    if (any(lessThan(samplePosition, ivec2(0)))
        || any(greaterThanEqual(samplePosition, dimensions)))
    {
        return false;
    }

    vec4 encodedHeight = texelFetch(atlasSurfaceHeightTex, samplePosition, 0);
    if (encodedHeight.b < 0.5) return false;

    surfaceHeight = floor(encodedHeight.r * 255.0 + 0.5) * 256.0
        + floor(encodedHeight.g * 255.0 + 0.5);
    return true;
}

void modernAtlasIncludeLowerSurfaceHeight(
    ivec2 samplePosition,
    inout float minimumSurfaceHeight
)
{
    float candidateHeight;
    if (modernAtlasReadSurfaceHeight(samplePosition, candidateHeight))
    {
        minimumSurfaceHeight = min(minimumSurfaceHeight, candidateHeight);
    }
}

bool modernAtlasReadExteriorFloor(
    vec3 absoluteWorldPosition,
    vec3 surfaceNormal,
    out float exteriorFloor
)
{
    ivec2 samplePosition = ivec2(floor(
        (absoluteWorldPosition.xz - atlasSurfaceOriginXZ) / atlasSurfaceSampleSize
    ));
    float exteriorSurfaceHeight;
    if (!modernAtlasReadSurfaceHeight(samplePosition, exteriorSurfaceHeight))
    {
        return false;
    }

    // A height map alone classifies the underside of a natural overhang as a
    // deep cave. For vertical faces, inspect both immediately adjacent columns
    // along the face axis. A second two-block sample is used only when neither
    // immediate column lowers the surface; this handles a face whose fragment
    // lands on the neighboring heightmap column without allowing a distant low
    // column to rescue an interior mine wall. For downward faces, inspect only
    // a local ring. This keeps real completed mesh faces on an exterior
    // silhouette without using air connectivity, a terrain shell or persistent
    // geometry.
    if (abs(surfaceNormal.y) < 0.75)
    {
        ivec2 exteriorStep = abs(surfaceNormal.x) >= abs(surfaceNormal.z)
            ? ivec2(surfaceNormal.x >= 0.0 ? 1 : -1, 0)
            : ivec2(0, surfaceNormal.z >= 0.0 ? 1 : -1);
        float originalSurfaceHeight = exteriorSurfaceHeight;
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + exteriorStep,
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition - exteriorStep,
            exteriorSurfaceHeight
        );
        if (exteriorSurfaceHeight >= originalSurfaceHeight)
        {
            modernAtlasIncludeLowerSurfaceHeight(
                samplePosition + exteriorStep * 2,
                exteriorSurfaceHeight
            );
            modernAtlasIncludeLowerSurfaceHeight(
                samplePosition - exteriorStep * 2,
                exteriorSurfaceHeight
            );
        }
    }

    if (surfaceNormal.y < -0.25)
    {
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2( 1,  0),
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2(-1,  0),
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2( 0,  1),
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2( 0, -1),
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2( 2,  0),
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2(-2,  0),
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2( 0,  2),
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2( 0, -2),
            exteriorSurfaceHeight
        );
    }

    exteriorFloor = exteriorSurfaceHeight - atlasVisibleSubsurfaceDepth;
    return true;
}

void main()
{
    vec3 modernAtlasAbsoluteWorldPosition = worldPos.xyz + atlasWorldOffset;
    if (atlasHideVegetation > 0 && modernAtlasIsVegetation(uv))
    {
        discard;
    }
""" + caveFilterCode + """
    modernAtlasOriginalMain();
""" + mapLayerCode + """
}
""";
    }

    private static bool UseAtlasVisibility(
        ModelDataPoolLocation __instance,
        EnumFrustumCullMode mode,
        FrustumCulling culler,
        ref bool __result
    )
    {
        if (!atlasVisibilityOverride) return true;

        if (atlasTerrainCollectionOverride
            && __instance.IndicesEnd > __instance.IndicesStart)
        {
            (int X, int Y, int Z) consideredChunk = GetMeshChunk(
                __instance,
                GlobalConstants.ChunkSize
            );
            atlasConsideredTerrainColumns?.Add((consideredChunk.X, consideredChunk.Z));
        }

        if (__instance.Hide)
        {
            __result = false;
            return false;
        }

        switch (mode)
        {
            case EnumFrustumCullMode.CullInstant:
                __result = culler.InFrustum(__instance.FrustumCullSphere);
                break;
            case EnumFrustumCullMode.CullInstantShadowPassNear:
                __result = culler.InFrustumShadowPass(__instance.FrustumCullSphere);
                break;
            case EnumFrustumCullMode.CullInstantShadowPassFar:
                __result = __instance.LodLevel >= 1
                    && culler.InFrustumShadowPass(__instance.FrustumCullSphere);
                break;
            case EnumFrustumCullMode.CullNormal:
                __result = culler.InFrustumAndRange(
                    __instance.FrustumCullSphere,
                    __instance.FrustumVisible,
                    __instance.LodLevel
                );
                __instance.FrustumVisible = __result;
                break;
            default:
                __result = true;
                break;
        }

        if (__result && __instance.IndicesEnd > __instance.IndicesStart)
        {
            if (atlasTerrainCollectionOverride)
            {
                (int X, int Y, int Z) chunk = GetMeshChunk(
                    __instance,
                    GlobalConstants.ChunkSize
                );
                atlasVisibleTerrainColumns?.Add((chunk.X, chunk.Z));
            }
            else if (atlasLiquidVisibilityOverride
                && atlasLiquidAdapter?.IsCompletedLiquidChunk(__instance) != true)
            {
                __result = false;
            }
        }

        return false;
    }

    private void LogTerrainCoverage(int viewDistanceBlocks)
    {
        if (loggedTerrainCoverage) return;
        loggedTerrainCoverage = true;

        int chunkSize = GlobalConstants.ChunkSize;
        int playerChunkX = (int)Math.Floor(
            capi.World.Player.Entity.Pos.X / chunkSize
        );
        int playerChunkZ = (int)Math.Floor(
            capi.World.Player.Entity.Pos.Z / chunkSize
        );
        int chunkRadius = Math.Max(1, (viewDistanceBlocks + chunkSize - 1) / chunkSize);
        int verticalChunkCount = Math.Max(
            1,
            (capi.World.BlockAccessor.MapSizeY + chunkSize - 1) / chunkSize
        );
        int dimensionOffset = capi.World.Player.Entity.Pos.Dimension
            * GlobalConstants.DimensionSizeInChunks;
        HashSet<(int X, int Z)> loadedColumns = new();
        for (int chunkZ = playerChunkZ - chunkRadius;
            chunkZ <= playerChunkZ + chunkRadius;
            chunkZ++)
        {
            for (int chunkX = playerChunkX - chunkRadius;
                chunkX <= playerChunkX + chunkRadius;
                chunkX++)
            {
                for (int chunkY = 0; chunkY < verticalChunkCount; chunkY++)
                {
                    if (capi.World.BlockAccessor.GetChunk(
                        chunkX,
                        chunkY + dimensionOffset,
                        chunkZ
                    ) is not IClientChunk { LoadedFromServer: true })
                    {
                        continue;
                    }

                    loadedColumns.Add((chunkX, chunkZ));
                    break;
                }
            }
        }

        List<string> missingMeshSamples = new();
        int loadedWithoutMesh = 0;
        foreach ((int X, int Z) column in loadedColumns)
        {
            if (consideredTerrainColumns.Contains(column)) continue;
            loadedWithoutMesh++;
            if (missingMeshSamples.Count < 12)
            {
                missingMeshSamples.Add(
                    $"{column.X - playerChunkX:+0;-0;0},{column.Z - playerChunkZ:+0;-0;0}"
                );
            }
        }

        int meshOutsideAtlasFrustum = 0;
        foreach ((int X, int Z) column in consideredTerrainColumns)
        {
            if (!visibleTerrainColumns.Contains(column)) meshOutsideAtlasFrustum++;
        }

        capi.Logger.Notification(
            "[ModernAtlas] Exact terrain coverage: client-loaded columns={0}, completed mesh columns={1}, atlas-visible mesh columns={2}, loaded without completed mesh={3}, mesh columns outside atlas frustum={4}; missing mesh offsets={5}.",
            loadedColumns.Count,
            consideredTerrainColumns.Count,
            visibleTerrainColumns.Count,
            loadedWithoutMesh,
            meshOutsideAtlasFrustum,
            missingMeshSamples.Count == 0 ? "none" : string.Join(" ", missingMeshSamples)
        );
    }

    private void BeginAtlasTextureBindings(
        bool concealSurvivalOres,
        bool hideVegetation
    )
    {
        atlasOreTextureBindingAdapter = concealSurvivalOres || hideVegetation
            ? this
            : null;
        atlasOreTextureBindingOverride = concealSurvivalOres;
        atlasVegetationTextureBindingOverride = hideVegetation;
        atlasOreTextureBindingRecursion = false;
    }

    private static void EndAtlasTextureBindings()
    {
        atlasOreTextureBindingRecursion = false;
        atlasOreTextureBindingOverride = false;
        atlasVegetationTextureBindingOverride = false;
        atlasOreTextureBindingAdapter = null;
    }

    private static void BindAtlasOreTexturesAfterTerrainTexture(
        object __instance,
        object[] __args
    )
    {
        if ((!atlasOreTextureBindingOverride && !atlasVegetationTextureBindingOverride)
            || atlasOreTextureBindingRecursion
            || atlasOreTextureBindingAdapter is not ExactChunkRendererAdapter adapter
            || __instance is not IShaderProgram shader
            || __args.Length < 3
            || __args[0] is not string samplerName
            || !string.Equals(samplerName, "terrainTex", StringComparison.Ordinal)
            || __args[1] is not int terrainTextureId
            || !shader.HasUniform("atlasConcealOres"))
        {
            return;
        }

        atlasOreTextureBindingRecursion = true;
        try
        {
            if (atlasOreTextureBindingOverride
                && !adapter.oreTextureReplacement.BindForTerrainTexture(
                    shader,
                    terrainTextureId,
                    OreMappingTextureUnit,
                    OreStoneTextureUnit
                )
                && !adapter.loggedOreTextureBindingFailure)
            {
                adapter.loggedOreTextureBindingFailure = true;
                adapter.capi.Logger.Error(
                    "[ModernAtlas] Survival ore concealment could not bind its atlas textures."
                );
            }
            if (atlasVegetationTextureBindingOverride
                && !adapter.vegetationTextureMask.BindForTerrainTexture(
                    shader,
                    terrainTextureId,
                    VegetationMaskTextureUnit
                ))
            {
                throw new InvalidOperationException(
                    "The atlas vegetation mask could not bind its texture."
                );
            }
        }
        finally
        {
            atlasOreTextureBindingRecursion = false;
        }
    }

    private bool RenderTransparentChunks(
        float deltaTime,
        float[] projection,
        double[] view,
        Vec3d cameraPosition,
        float waterStillCounter,
        float waterFlowCounter,
        bool cloudsEnabled,
        float pausedCloudAnimationDeltaTime,
        bool hideUndergroundCaves,
        bool concealSurvivalOres,
        bool hideVegetation,
        AtlasSurfaceHeightTexture? surfaceHeightTexture,
        AtlasMapLayerTexture? mapLayerTexture,
        float mapLayerOpacity,
        bool fogEnabled,
        int disclosureRadius,
        bool blitToDefault
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
            if (!ConfigureAtlasFilters(
                surfaceHeightTexture,
                mapLayerTexture,
                mapLayerOpacity,
                cameraPosition,
                true,
                hideUndergroundCaves,
                concealSurvivalOres,
                hideVegetation,
                fogEnabled,
                disclosureRadius
            ))
            {
                throw new InvalidOperationException(
                    "The transparent-block safety filters could not be configured."
                );
            }
            BeginAtlasTextureBindings(concealSurvivalOres, hideVegetation);
            try
            {
                renderOit.Invoke(chunkRenderer, new object[] { deltaTime });
            }
            finally
            {
                EndAtlasTextureBindings();
            }
            RestoreLiquidPools(hiddenLiquidPools);
            runAfterOit.Invoke(
                afterOitRenderer,
                new object[] { deltaTime, EnumRenderStage.OIT }
            );
            unloadFramebuffer.Invoke(platform, new object[] { EnumFrameBuffer.Transparent });
            framebufferLoaded = false;

            // Compose at the Primary framebuffer's own resolution before the
            // final blit. OIT uses texelFetch(gl_FragCoord), so composing it
            // directly onto a differently sized window target under SSAA
            // makes transparent blocks slide relative to terrain while the
            // camera pans.
            mergeTransparentRenderPass.Invoke(platform, Array.Empty<object>());
            BeginAtlasTextureBindings(concealSurvivalOres, hideVegetation);
            try
            {
                renderAfterOit.Invoke(chunkRenderer, new object[] { deltaTime });
            }
            finally
            {
                EndAtlasTextureBindings();
            }

            // Resolve every real transparent block before blending stable
            // liquids into Primary. This preserves the world's authored
            // texture colors below water, including the yellow and orange
            // bacterial mats in hot springs, instead of compositing those
            // surfaces at full strength on top of the water afterward.
            RenderStableLiquidBlocks(
                deltaTime,
                projection,
                view,
                cameraPosition,
                waterStillCounter,
                waterFlowCounter,
                hideUndergroundCaves,
                surfaceHeightTexture,
                mapLayerTexture,
                mapLayerOpacity,
                fogEnabled,
                disclosureRadius
            );
            if (cloudsEnabled && !blitToDefault)
            {
                cloudRenderer?.Render(
                    projection,
                    view,
                    pausedCloudAnimationDeltaTime,
                    true
                );
            }
            if (blitToDefault)
            {
                blitPrimaryToDefault.Invoke(platform, Array.Empty<object>());
                if (cloudsEnabled)
                {
                    cloudRenderer?.Render(
                        projection,
                        view,
                        pausedCloudAnimationDeltaTime
                    );
                }
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
        float waterFlowCounter,
        bool hideUndergroundCaves,
        AtlasSurfaceHeightTexture? surfaceHeightTexture,
        AtlasMapLayerTexture? mapLayerTexture,
        float mapLayerOpacity,
        bool fogEnabled,
        int disclosureRadius
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
        activeLiquidShader.Uniform(
            "disclosureCenterXZ",
            (float)capi.World.Player.Entity.Pos.X,
            (float)capi.World.Player.Entity.Pos.Z
        );
        activeLiquidShader.Uniform(
            "disclosureRadius",
            (float)Math.Max(GlobalConstants.ChunkSize, disclosureRadius)
        );
        float radius = Math.Max(GlobalConstants.ChunkSize, disclosureRadius);
        float baseBoundaryFeather = fogEnabled
            ? Math.Min(
                radius * 0.25f,
                Math.Max(GlobalConstants.ChunkSize * 2f, radius * 0.10f)
            )
            : Math.Min(
                radius * 0.25f,
                Math.Max(GlobalConstants.ChunkSize * 0.5f, radius * 0.03f)
            );
        float boundaryFeather = Math.Min(
            radius * 0.40f,
            baseBoundaryFeather * atlasBoundarySoftness
        );
        activeLiquidShader.Uniform("disclosureFeather", boundaryFeather);
        activeLiquidShader.Uniform(
            "boundaryFogColor",
            fogEnabled
                ? atlasFogColor
                : new Vec3f(0.035f, 0.075f, 0.11f)
        );
        bool applySurfaceFilter = hideUndergroundCaves
            && surfaceHeightTexture?.Ready == true
            && surfaceHeightTexture.TextureId > 0;
        activeLiquidShader.Uniform("atlasHideCaves", applySurfaceFilter ? 1 : 0);
        if (applySurfaceFilter && surfaceHeightTexture != null)
        {
            activeLiquidShader.BindTexture2D(
                "atlasSurfaceHeightTex",
                surfaceHeightTexture.TextureId,
                CaveFilterTextureUnit
            );
            activeLiquidShader.Uniform(
                "atlasSurfaceOriginXZ",
                (float)surfaceHeightTexture.OriginX,
                (float)surfaceHeightTexture.OriginZ
            );
            activeLiquidShader.Uniform(
                "atlasSurfaceSampleSize",
                (float)AtlasSurfaceHeightTexture.HorizontalSampleSize
            );
            activeLiquidShader.Uniform("atlasVisibleSubsurfaceDepth", VisibleSubsurfaceDepth);
        }
        bool applyMapLayer = mapLayerTexture?.Ready == true
            && mapLayerTexture.TextureId > 0
            && mapLayerTexture.Layer != AtlasMapLayer.TexturedTerrain;
        activeLiquidShader.Uniform("atlasLayerEnabled", applyMapLayer ? 1 : 0);
        if (applyMapLayer && mapLayerTexture != null)
        {
            activeLiquidShader.BindTexture2D(
                "atlasLayerTex",
                mapLayerTexture.TextureId,
                MapLayerTextureUnit
            );
            activeLiquidShader.Uniform(
                "atlasLayerOriginXZ",
                (float)mapLayerTexture.OriginX,
                (float)mapLayerTexture.OriginZ
            );
            activeLiquidShader.Uniform(
                "atlasLayerSampleSize",
                (float)AtlasMapLayerTexture.HorizontalSampleSize
            );
            activeLiquidShader.Uniform(
                "atlasLayerOpacity",
                Math.Clamp(mapLayerOpacity, 0f, 1f)
            );
        }
        activeLiquidShader.Uniform("atlasSunDirection", atlasSunDirection);
        activeLiquidShader.Uniform("atlasSunColor", atlasSunColor);
        activeLiquidShader.Uniform("atlasExposure", atlasExposure);
        activeLiquidShader.Uniform("atlasTextureMipBias", atlasTextureMipBias);
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
        FrameBufferRef primaryFramebuffer = render.FrameBuffers[
            (int)EnumFrameBuffer.Primary
        ];
        if (primaryFramebuffer.DepthTextureId <= 0)
        {
            throw new InvalidOperationException(
                "The Primary opaque depth texture is unavailable for atlas liquid clipping."
            );
        }
        activeLiquidShader.BindTexture2D(
            "opaqueDepthTex",
            primaryFramebuffer.DepthTextureId,
            OpaqueDepthTextureUnit
        );

        MeshDataPoolManager[] managers = passes[(int)EnumChunkRenderPass.Liquid];
        long renderedTriangles = 0;
        long allocatedTriangles = 0;
        int activeManagers = 0;
        liquidChunkCompletion.Clear();
        allowedLiquidLocationCount = 0;
        rejectedLiquidLocationCount = 0;
        atlasLiquidAdapter = this;
        atlasLiquidVisibilityOverride = true;
        try
        {
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
        }
        finally
        {
            atlasLiquidVisibilityOverride = false;
            atlasLiquidAdapter = null;
        }
        activeLiquidShader.Stop();
        render.GLDepthMask(true);
        render.GlToggleBlend(false, EnumBlendMode.Standard);
        render.GlEnableCullFace();

        if (!loggedStableLiquidDiagnostics)
        {
            loggedStableLiquidDiagnostics = true;
            capi.Logger.Notification(
                "[ModernAtlas] Stable liquid draw: shader pass {0}, {1} atlas managers, {2} rendered triangles, {3} allocated triangles; {4} completed liquid mesh locations allowed and {5} rejected; texture detail reduction={6}x.",
                activeLiquidShader.PassId,
                activeManagers,
                renderedTriangles,
                allocatedTriangles,
                allowedLiquidLocationCount,
                rejectedLiquidLocationCount,
                1 << LastRenderedTextureDetailReduction
            );
        }
    }

    private bool IsCompletedLiquidChunk(ModelDataPoolLocation location)
    {
        int chunkSize = GlobalConstants.ChunkSize;
        (int chunkX, int chunkY, int chunkZ) chunk = GetMeshChunk(location, chunkSize);
        if (liquidChunkCompletion.TryGetValue(chunk, out bool cached)) return cached;

        // Match liquids to a visible, completed terrain column rather than
        // demanding same-height terrain and a fully loaded 3x3 guard band.
        // Ocean floors may be several vertical chunks below their surface,
        // and the old guard band removed valid edge water before terrain.
        bool completed = visibleTerrainColumns.Contains((chunk.chunkX, chunk.chunkZ))
            && capi.World.BlockAccessor.GetChunk(chunk.chunkX, chunk.chunkY, chunk.chunkZ)
                is IClientChunk { LoadedFromServer: true };
        liquidChunkCompletion[chunk] = completed;
        if (completed) allowedLiquidLocationCount++;
        else rejectedLiquidLocationCount++;
        return completed;
    }

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : (value - divisor + 1) / divisor;

    private static (int X, int Y, int Z) GetMeshChunk(
        ModelDataPoolLocation location,
        int chunkSize
    ) =>
    (
        FloorDiv((int)Math.Floor(location.FrustumCullSphere.x), chunkSize),
        FloorDiv((int)Math.Floor(location.FrustumCullSphere.y), chunkSize),
        FloorDiv((int)Math.Floor(location.FrustumCullSphere.z), chunkSize)
    );

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
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            parameterTypes,
            null
        ) ?? throw new MissingMethodException(type.FullName, name);
    }

    private static MethodInfo RequireImplementedMethod(
        Type type,
        string name,
        params Type[] parameterTypes
    )
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            MethodInfo? method = current.GetMethod(
                name,
                BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly,
                null,
                parameterTypes,
                null
            );
            if (method != null && !method.IsAbstract && method.GetMethodBody() != null)
            {
                return method;
            }
        }

        throw new MissingMethodException(
            type.FullName,
            $"{name} with an implemented method body"
        );
    }
}
