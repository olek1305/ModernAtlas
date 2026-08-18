using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Vintage Story does not expose its completed terrain GPU meshes through the
/// public API. This small, version-checked adapter reuses the 1.22.x terrain
/// renderer so connected models, mod blocks, biome colors and engine lighting
/// remain identical to the normal world view. The global entity and particle
/// stages are never invoked; a separate adapter draws only selected living
/// models that are already loaded and authorized for the atlas.
/// </summary>
internal sealed class ExactChunkRendererAdapter : IDisposable
{
    private const string SupportedVersionSeries = "1.22.";
    private const string MinimumSupportedVersion = "1.22.3";
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
    private const string VegetationMipBiasEnvironmentVariable =
        "MODERNATLAS_ATLAS_VEGETATION_MIP_BIAS";
    private const string VegetationAlphaCoverageEnvironmentVariable =
        "MODERNATLAS_ATLAS_VEGETATION_ALPHA_COVERAGE";
    private const string VegetationDebugEnvironmentVariable =
        "MODERNATLAS_ATLAS_VEGETATION_DEBUG";
    private const string VegetationLod0EnvironmentVariable =
        "MODERNATLAS_ATLAS_VEGETATION_PRESERVE_LOD0";
    private const string DisableTransparentEnvironmentVariable =
        "MODERNATLAS_ATLAS_DISABLE_TRANSPARENT";
    private const string DisableCaveFilterEnvironmentVariable =
        "MODERNATLAS_ATLAS_DISABLE_CAVE_FILTER";
    private const string DisableFrustumEnvironmentVariable =
        "MODERNATLAS_ATLAS_DISABLE_FRUSTUM";
    private const string DisableFilteringEnvironmentVariable =
        "MODERNATLAS_ATLAS_DISABLE_FILTERING";
    private static readonly Vec3f CaveConcealmentColor = new(0.24f, 0.25f, 0.25f);
    private static readonly float? DeveloperVegetationMipBiasOverride =
        ReadOptionalFloatEnvironment(
            VegetationMipBiasEnvironmentVariable,
            0f,
            2f
        );
    private static readonly float? DeveloperVegetationAlphaCoverageOverride =
        ReadOptionalFloatEnvironment(
            VegetationAlphaCoverageEnvironmentVariable,
            0f,
            1f
        );
    private static readonly int DeveloperVegetationDebugMode =
        ReadOptionalIntEnvironment(VegetationDebugEnvironmentVariable, 0, 5);
    private static readonly bool DeveloperPreserveVegetationLod0 =
        IsEnvironmentFlagEnabled(VegetationLod0EnvironmentVariable);
    private static readonly bool DeveloperDisableTransparentPass =
        IsEnvironmentFlagEnabled(DisableTransparentEnvironmentVariable);
    private static readonly bool DeveloperDisableCaveFilter =
        IsEnvironmentFlagEnabled(DisableCaveFilterEnvironmentVariable);
    private static readonly bool DeveloperDisableFrustum =
        IsEnvironmentFlagEnabled(DisableFrustumEnvironmentVariable);
    private static readonly bool DeveloperDisableFiltering =
        IsEnvironmentFlagEnabled(DisableFilteringEnvironmentVariable);

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
    private static bool atlasTransparentVisibilityOverride;

    [ThreadStatic]
    private static bool atlasLiquidVisibilityOverride;

    [ThreadStatic]
    private static bool atlasDisclosureCullingOverride;

    [ThreadStatic]
    private static bool atlasVegetationOnlyVisibilityOverride;

    [ThreadStatic]
    private static double atlasDisclosureCenterX;

    [ThreadStatic]
    private static double atlasDisclosureCenterZ;

    [ThreadStatic]
    private static double atlasDisclosureRadius;

    [ThreadStatic]
    private static bool atlasCompleteBoundaryEnabled;

    [ThreadStatic]
    private static int atlasCompleteMinimumChunkX;

    [ThreadStatic]
    private static int atlasCompleteMaximumChunkX;

    [ThreadStatic]
    private static int atlasCompleteMinimumChunkZ;

    [ThreadStatic]
    private static int atlasCompleteMaximumChunkZ;

    [ThreadStatic]
    private static HashSet<(int X, int Z)>? atlasVisibleTerrainColumns;

    [ThreadStatic]
    private static HashSet<(int X, int Z)>? atlasSupportedTerrainColumns;

    [ThreadStatic]
    private static HashSet<(int X, int Y, int Z)>? atlasSupportedSurfaceSections;

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
    private readonly AtlasBoundaryResolver boundaryResolver;
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
    private readonly HashSet<(int X, int Z)> supportedTerrainColumns = new();
    private readonly HashSet<(int X, int Y, int Z)> supportedSurfaceSections = new();
    private readonly HashSet<(int X, int Z)> consideredTerrainColumns = new();
    private readonly Dictionary<(int X, int Y, int Z), bool> liquidChunkCompletion = new();
    private int allowedLiquidLocationCount;
    private int rejectedLiquidLocationCount;
    private Vec3f atlasSunDirection = new(-0.34f, 0.86f, -0.38f);
    private Vec3f atlasSunColor = new(1f, 0.96f, 0.86f);
    private float atlasExposure = 1f;
    private float atlasTextureMipBias;
    private float atlasVegetationMipBias;
    private float atlasVegetationAlphaCoverage;
    private float atlasPixelsPerBlock;
    private bool atlasWindPhaseInitialized;
    private float atlasWindPhase;
    private float atlasWindHighFrequencyPhase;
    private float previousSourceWindPhase;
    private float previousSourceWindHighFrequencyPhase;
    private float atlasCaveMaskBrightness = 1f;
    private readonly Dictionary<EnumShaderProgram, AtlasFilterShaderState> atlasFilterShaders = new();
    private readonly HashSet<EnumShaderProgram> atlasFilterInjectionFailures = new();
    private bool loggedCaveFilterReady;
    private bool loggedUndergroundSafetyFailure;
    private bool loggedOreTextureBindingFailure;
    private bool loggedPreparationClearFailure;
    private bool loggedNormalWorldShaderRestore;
    private bool loggedTerrainCoverage;
    private int loggedCompleteBoundaryRadius = int.MinValue;
    private bool loggedLightingDiagnostics;
    private bool loggedVegetationFilteringDiagnostics;
    private bool disposed;

    public int LastRenderedEntityCount { get; private set; }
    public int LastSuppressedHeldItemCount =>
        entityModelRenderer.LastSuppressedHeldItemCount;
    public IReadOnlyList<AtlasRenderedEntity> LastRenderedEntities =>
        entityModelRenderer.LastRenderedEntities;
    public IReadOnlyCollection<(int X, int Z)> CompletedTerrainColumns =>
        consideredTerrainColumns;
    public int LastRenderedTextureDetailReduction { get; private set; }
    public float LastRenderedVegetationMipBias { get; private set; }
    public float LastRenderedVegetationPixelsPerBlock { get; private set; }
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

    private static float? ReadOptionalFloatEnvironment(
        string name,
        float minimum,
        float maximum
    )
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (!float.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float parsed
        ))
        {
            return null;
        }

        return Math.Clamp(parsed, minimum, maximum);
    }

    private static int ReadOptionalIntEnvironment(
        string name,
        int minimum,
        int maximum
    )
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (!int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int parsed
        ))
        {
            return 0;
        }

        return Math.Clamp(parsed, minimum, maximum);
    }

    private static bool IsEnvironmentFlagEnabled(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static float CalculateAtlasPixelsPerBlock(
        float[] projection,
        int framebufferWidth,
        int framebufferHeight
    )
    {
        if (projection.Length < 6) return 1f;

        // ModernAtlas uses an orthographic projection. The first and sixth
        // matrix entries convert one world block to NDC units; multiplying by
        // half the native framebuffer dimension gives the actual screen
        // footprint. Taking the smaller axis keeps the filter conservative at
        // tilted angles without making close geometry needlessly soft.
        float horizontalPixels =
            MathF.Abs(projection[0]) * Math.Max(1, framebufferWidth) * 0.5f;
        float verticalPixels =
            MathF.Abs(projection[5]) * Math.Max(1, framebufferHeight) * 0.5f;
        return MathF.Max(0.01f, MathF.Min(horizontalPixels, verticalPixels));
    }

    private static float CalculateVegetationMipBias(
        float pixelsPerBlock,
        int configuredTextureSize
    )
    {
        float textureFootprint = Math.Max(
            1f,
            configuredTextureSize / MathF.Max(0.01f, pixelsPerBlock)
        );
        float minification = MathF.Log2(textureFootprint);

        // A block that is still several pixels wide should keep its authored
        // detail. Once a 32px (or user-configured) tile is substantially
        // minified, a small positive bias averages alpha-tested foliage and
        // avoids toggling whole leaf texels on subpixel camera motion. The
        // cap deliberately stays below one for the automatic path; developers
        // can opt into exact 0/1/2 comparisons with the environment override.
        return Math.Clamp((minification - 2.5f) * 0.45f, 0f, 0.75f);
    }

    private static float CalculateVegetationAlphaCoverage(
        float pixelsPerBlock,
        int configuredTextureSize
    )
    {
        float textureFootprint = Math.Max(
            1f,
            configuredTextureSize / MathF.Max(0.01f, pixelsPerBlock)
        );
        float minification = MathF.Log2(textureFootprint);
        return Math.Clamp((minification - 2f) * 0.22f, 0f, 0.5f);
    }

    private static float CalculateAtlasWindDetail(float pixelsPerBlock)
    {
        // Wind moves the vertex position before rasterization. At one screen
        // pixel per block even a subpixel bend changes which alpha-tested leaf
        // texels survive. Keep a small amount of the authored motion only
        // after the projected block footprint becomes large enough to carry
        // it, then restore the native animation for close atlas views.
        float normalized = Math.Clamp(
            (pixelsPerBlock - 1f) / 3f,
            0f,
            1f
        );
        return normalized * normalized * (3f - 2f * normalized);
    }

    private void AdvanceAtlasWindPhase(
        float sourcePhase,
        float sourceHighFrequencyPhase,
        float detail
    )
    {
        if (!atlasWindPhaseInitialized)
        {
            atlasWindPhaseInitialized = true;
            atlasWindPhase = sourcePhase;
            atlasWindHighFrequencyPhase = sourceHighFrequencyPhase;
        }
        else
        {
            // Integrate the source-counter delta instead of multiplying its
            // absolute value. The detail factor changes continuously while
            // zooming; multiplying an old, large counter by that changing
            // factor makes leaves jump through many animation cycles.
            atlasWindPhase += (sourcePhase - previousSourceWindPhase) * detail;
            atlasWindHighFrequencyPhase +=
                (sourceHighFrequencyPhase - previousSourceWindHighFrequencyPhase)
                * detail;
        }

        previousSourceWindPhase = sourcePhase;
        previousSourceWindHighFrequencyPhase = sourceHighFrequencyPhase;
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
        Func<IShaderProgram?> atlasBoundaryShaderProvider,
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
        boundaryResolver = new AtlasBoundaryResolver(
            capi,
            atlasBoundaryShaderProvider
        );
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
        Func<IShaderProgram?> atlasCloudShaderProvider,
        Func<IShaderProgram?> atlasBoundaryShaderProvider
    )
    {
        if (!GameVersion.ShortGameVersion.StartsWith(SupportedVersionSeries, StringComparison.Ordinal))
        {
            capi.Logger.Warning(
                "[ModernAtlas] Exact chunk rendering supports Vintage Story {0} or newer in the 1.22 series; using the compatible atlas renderer on {1}.",
                MinimumSupportedVersion,
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
            MethodInfo atlasVisibilityPostfix = RequireMethod(
                typeof(ExactChunkRendererAdapter),
                nameof(CollectAtlasVisibleTerrain),
                typeof(ModelDataPoolLocation),
                typeof(EnumFrustumCullMode),
                typeof(bool)
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
                prefix: new HarmonyMethod(atlasVisibilityPrefix),
                postfix: new HarmonyMethod(atlasVisibilityPostfix)
            );
            integrationHarmony.Patch(
                bindTexture,
                postfix: new HarmonyMethod(atlasOreTexturePostfix)
            );
            capi.Logger.Notification(
                "[ModernAtlas] Vintage Story 1.22.x exact chunk renderer is available."
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
                atlasBoundaryShaderProvider,
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
        loggedLightingDiagnostics = false;
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
        bool hideUndergroundCaves,
        bool concealSurvivalOres,
        AtlasSurfaceHeightTexture? surfaceHeightTexture,
        AtlasMapLayerTexture? mapLayerTexture,
        float mapLayerOpacity,
        int textureDetailReduction,
        bool performanceLightingEnabled,
        bool hideVegetation,
        float visualExposureMultiplier,
        float caveMaskBrightness,
        float windWaveCounter,
        float windWaveCounterHighFrequency,
        float waterStillCounter,
        float waterFlowCounter,
        bool cloudsEnabled,
        bool liveLightingEnabled,
        int fixedSunHour,
        float pausedCloudAnimationDeltaTime,
        Vec3f? frozenCloudOffset,
        ModernAtlasServerPolicy entityPolicy,
        bool blitToDefault
    )
    {
        if (disabled) return false;
        if (concealSurvivalOres && !oreTextureReplacement.Advance()) return false;
        bool vegetationMaskReady = vegetationTextureMask.Advance();
        if (hideVegetation && !vegetationMaskReady) return false;

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
        Vec3f savedSunPosition = shaderUniforms.SunPosition3D;
        float savedSkyDaylight = (float)(skyDaylightUniformField.GetValue(shaderUniforms) ?? 0f);
        float savedSunsetMod = shaderUniforms.SunsetMod;
        float savedWindWaveCounter = shaderUniforms.WindWaveCounter;
        float savedWindWaveCounterHighFrequency = shaderUniforms.WindWaveCounterHighFreq;
        PropertyInfo? windWaveIntensityProperty = typeof(DefaultShaderUniforms).GetProperty(
            "WindWaveIntensity",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );
        float savedWindWaveIntensity = windWaveIntensityProperty?.GetValue(shaderUniforms)
            is float currentWindWaveIntensity
                ? currentWindWaveIntensity
                : 1f;
        long renderStartedMilliseconds = capi.ElapsedMilliseconds;
        long sceneSetupCompletedMilliseconds = renderStartedMilliseconds;
        long opaqueCompletedMilliseconds = renderStartedMilliseconds;
        long entitiesCompletedMilliseconds = renderStartedMilliseconds;
        long transparentCompletedMilliseconds = renderStartedMilliseconds;
        bool atlasFilterConfigured = false;

        atlasCaveMaskBrightness = Math.Clamp(caveMaskBrightness, 0.5f, 1.5f);
        atlasTextureMipBias = Math.Clamp(textureDetailReduction, 0, 2);
        int configuredTextureSize = 32;
        try
        {
            int engineTextureSize = capi.Settings.Int["textureSize"];
            configuredTextureSize = engineTextureSize > 0
                ? engineTextureSize
                : 32;
        }
        catch
        {
            // The engine setting is available during normal client rendering,
            // but keep the atlas shader path safe during partial teardown.
        }
        FrameBufferRef primaryForFiltering =
            render.FrameBuffers[(int)EnumFrameBuffer.Primary];
        atlasPixelsPerBlock = CalculateAtlasPixelsPerBlock(
            projection,
            primaryForFiltering.Width,
            primaryForFiltering.Height
        );
        float automaticVegetationMipBias = CalculateVegetationMipBias(
            atlasPixelsPerBlock,
            configuredTextureSize
        );
        atlasVegetationMipBias = Math.Clamp(
            DeveloperVegetationMipBiasOverride
                ?? (atlasTextureMipBias + automaticVegetationMipBias),
            0f,
            2f
        );
        atlasVegetationAlphaCoverage = Math.Clamp(
            DeveloperVegetationAlphaCoverageOverride
                ?? CalculateVegetationAlphaCoverage(
                    atlasPixelsPerBlock,
                    configuredTextureSize
                ),
            0f,
            1f
        );
        float atlasWindDetail = CalculateAtlasWindDetail(atlasPixelsPerBlock);
        AdvanceAtlasWindPhase(
            windWaveCounter,
            windWaveCounterHighFrequency,
            atlasWindDetail
        );
        LastRenderedTextureDetailReduction = (int)atlasTextureMipBias;
        LastRenderedVegetationMipBias = atlasVegetationMipBias;
        LastRenderedVegetationPixelsPerBlock = atlasPixelsPerBlock;
        LastRenderedVegetationHidden = hideVegetation;
        LastRenderedPerformanceLightingEnabled = performanceLightingEnabled;

        try
        {
            // World distance fog is based on the elevated atlas eye and would
            // otherwise cover most of the orthographic map with haze.
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
            if (!loggedLightingDiagnostics)
            {
                loggedLightingDiagnostics = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Solar lighting: mode={0}, direction=({1:0.000}, {2:0.000}, {3:0.000}), color=({4:0.000}, {5:0.000}, {6:0.000}), exposure={7:0.000}.",
                    !performanceLightingEnabled
                        ? "neutral"
                        : liveLightingEnabled
                            ? "live"
                            : $"fixed-{Math.Clamp(fixedSunHour, 0, 23):00}:00",
                    atlasSunDirection.X,
                    atlasSunDirection.Y,
                    atlasSunDirection.Z,
                    atlasSunColor.X,
                    atlasSunColor.Y,
                    atlasSunColor.Z,
                    atlasExposure
                );
            }
            if (!loggedVegetationFilteringDiagnostics)
            {
                loggedVegetationFilteringDiagnostics = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Vegetation filtering: {0:0.000} pixels/block, texture={1}px, automatic bias={2:0.000}, mip bias={3:0.000}, alpha coverage={4:0.000}, wind detail={5:0.000}, lod0Fade={6}, debug={7}.",
                    atlasPixelsPerBlock,
                    configuredTextureSize,
                    automaticVegetationMipBias,
                    atlasVegetationMipBias,
                    atlasVegetationAlphaCoverage,
                    atlasWindDetail,
                    DeveloperPreserveVegetationLod0
                        ? "native for wind geometry"
                        : "disabled for exact atlas meshes",
                    DeveloperVegetationDebugMode
                );
            }
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
            // The engine's weak and normal wind modes contain a small amount
            // of motion that does not use WindWaveIntensity. Slow the atlas
            // phase itself at subpixel footprints as well, otherwise those
            // modes still move an alpha-tested quad through different texels
            // every frame. The stable-liquid pass has its own counters and is
            // not affected by this vegetation-only projection reduction.
            shaderUniforms.WindWaveCounter = atlasWindPhase;
            shaderUniforms.WindWaveCounterHighFreq = atlasWindHighFrequencyPhase;
            if (windWaveIntensityProperty?.CanWrite == true)
            {
                windWaveIntensityProperty.SetValue(
                    shaderUniforms,
                    savedWindWaveIntensity * atlasWindDetail
                );
            }

            // The official chunk shaders write to the multi-attachment Primary
            // world framebuffer. Rendering them into the default GUI target
            // produces no color even though the draw call succeeds.
            FrameBufferRef primaryFramebuffer = primaryForFiltering;
            float[] atlasBackground = { 0.035f, 0.075f, 0.11f, 1f };
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
            double[] cullingProjection = (double[])projectionDouble.Clone();

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
                cullingProjection,
                cullingView
            );
            ReplacePoolFrustums(atlasFrustum, changedPools);
            sceneSetupCompletedMilliseconds = capi.ElapsedMilliseconds;
            // The engine's completed mesh locations retain an occlusion flag
            // calculated on a worker thread for the normal player camera.
            // During this draw only, use the atlas frustum without that stale
            // player-camera flag. Hide and LOD/frustum checks still apply.
            atlasVisibilityOverride = true;
            atlasDisclosureCullingOverride = viewDistanceBlocks > 0;
            atlasDisclosureCenterX = capi.World.Player.Entity.Pos.X;
            atlasDisclosureCenterZ = capi.World.Player.Entity.Pos.Z;
            atlasDisclosureRadius = Math.Max(
                GlobalConstants.ChunkSize,
                viewDistanceBlocks
            );
            UpdateCompleteViewBoundary(viewDistanceBlocks);
            render.PMatrix.Push(projectionDouble);
            projectionPushed = true;
            render.CurrentActiveShader?.Stop();
            visibleTerrainColumns.Clear();
            consideredTerrainColumns.Clear();
            atlasVisibleTerrainColumns = visibleTerrainColumns;
            BuildSupportedTerrainColumns(surfaceHeightTexture);
            atlasSupportedTerrainColumns = supportedTerrainColumns.Count > 0
                ? supportedTerrainColumns
                : null;
            atlasSupportedSurfaceSections = supportedSurfaceSections.Count > 0
                ? supportedSurfaceSections
                : null;
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
                        1,
                        viewDistanceBlocks,
                        false
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
                BeginAtlasTextureBindings(
                    concealSurvivalOres,
                    vegetationMaskReady
                );
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

            if (!hideVegetation)
            {
                if (!ConfigureAtlasFilters(
                    surfaceHeightTexture,
                    mapLayerTexture,
                    mapLayerOpacity,
                    cameraPosition,
                    true,
                    hideUndergroundCaves,
                    concealSurvivalOres,
                    false,
                    2,
                    viewDistanceBlocks,
                    true
                ))
                {
                    throw new InvalidOperationException(
                        "The vegetation-only atlas safety filters could not be configured."
                    );
                }

                atlasVegetationOnlyVisibilityOverride = true;
                BeginAtlasTextureBindings(
                    concealSurvivalOres,
                    vegetationMaskReady
                );
                try
                {
                    renderOpaque.Invoke(chunkRenderer, new object[] { deltaTime });
                }
                finally
                {
                    EndAtlasTextureBindings();
                    atlasVegetationOnlyVisibilityOverride = false;
                }
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
            RenderTransparentChunks(
                deltaTime,
                projection,
                view,
                cameraPosition,
                waterStillCounter,
                waterFlowCounter,
                cloudsEnabled,
                pausedCloudAnimationDeltaTime,
                frozenCloudOffset,
                hideUndergroundCaves,
                concealSurvivalOres,
                hideVegetation,
                surfaceHeightTexture,
                mapLayerTexture,
                mapLayerOpacity,
                viewDistanceBlocks,
                blitToDefault
            );
            if (!boundaryResolver.Resolve(
                primaryFramebuffer,
                projection,
                view,
                new Vec3d(oldCameraX, oldCameraY, oldCameraZ),
                capi.World.Player.Entity.Pos.X,
                capi.World.Player.Entity.Pos.Z,
                viewDistanceBlocks,
                surfaceHeightTexture,
                atlasCompleteBoundaryEnabled,
                atlasCompleteMinimumChunkX * (float)GlobalConstants.ChunkSize,
                atlasCompleteMinimumChunkZ * (float)GlobalConstants.ChunkSize,
                (atlasCompleteMaximumChunkX + 1) * (float)GlobalConstants.ChunkSize,
                (atlasCompleteMaximumChunkZ + 1) * (float)GlobalConstants.ChunkSize
            ))
            {
                throw new InvalidOperationException(
                    "The final material-independent atlas boundary could not be resolved."
                );
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
            atlasTransparentVisibilityOverride = false;
            atlasLiquidVisibilityOverride = false;
            atlasDisclosureCullingOverride = false;
            atlasCompleteBoundaryEnabled = false;
            atlasVegetationOnlyVisibilityOverride = false;
            atlasVisibleTerrainColumns = null;
            atlasSupportedTerrainColumns = null;
            atlasSupportedSurfaceSections = null;
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
                        0,
                        0,
                        false
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
            shaderUniforms.SunPosition3D = savedSunPosition;
            skyDaylightUniformField.SetValue(shaderUniforms, savedSkyDaylight);
            shaderUniforms.SunsetMod = savedSunsetMod;
            shaderUniforms.WindWaveCounter = savedWindWaveCounter;
            shaderUniforms.WindWaveCounterHighFreq = savedWindWaveCounterHighFrequency;
            if (windWaveIntensityProperty?.CanWrite == true)
            {
                windWaveIntensityProperty.SetValue(
                    shaderUniforms,
                    savedWindWaveIntensity
                );
            }
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
            shaderUniforms.SunPosition3D = overheadLight;
            skyDaylightUniformField.SetValue(shaderUniforms, 1f);
            shaderUniforms.SunsetMod = 0f;
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
            // Chunk programs read lightPosition for directional face shading
            // and sunPosition for the celestial color contribution. Update
            // both from the same live world calendar so sunrise in the east,
            // sunset in the west and the seasonal north/south arc remain
            // visible instead of retaining the normal camera's stale sun.
            shaderUniforms.SunPosition3D = sunDirection;
            skyDaylightUniformField.SetValue(shaderUniforms, daylight);
            shaderUniforms.SunsetMod = calendar.SunsetMod;
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
            Vec3f fallbackSunDirection = NormalizeDirection(
                liveLightPosition,
                atlasSunDirection
            );
            shaderUniforms.SunPosition3D = fallbackSunDirection;
            atlasSunDirection = fallbackSunDirection;
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
        // Keep the real below/above-horizon solar vector for sky color while
        // the separate face-light vector is safely clamped above terrain.
        // GetSunPosition evaluates the current world date and player location,
        // so a fixed hour still has the world's real east/west and seasonal
        // north/south direction.
        shaderUniforms.SunPosition3D = fixedSunDirection;
        skyDaylightUniformField.SetValue(shaderUniforms, fixedDaylight);
        shaderUniforms.SunsetMod = calendar?.SunsetMod ?? 0f;
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
        boundaryResolver.Dispose();
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
            // and disable only ModernAtlas' conditional paths. Recompiling an
            // engine shader while the normal world is returning from the GUI
            // can invalidate its cached uniform locations and leave chunks
            // black after the second G. The source is restored only when the
            // renderer is disposed during world teardown.
            DisableAtlasFilterUniforms();

            if (!loggedNormalWorldShaderRestore)
            {
                loggedNormalWorldShaderRestore = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Disabled atlas-only chunk shader paths after the atlas closed; no shader recompilation or global reload was used."
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

    public void RenderSurfacePreparationFrame(bool blitToDefault)
    {
        if (disabled) return;

        try
        {
            IRenderAPI render = capi.Render;
            render.CurrentActiveShader?.Stop();
            FrameBufferRef primaryFramebuffer = render.FrameBuffers[(int)EnumFrameBuffer.Primary];
            float[] atlasBackground = { 0.035f, 0.075f, 0.11f, 1f };
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

    public int ResolvedColorTextureId => boundaryResolver.ColorTextureId;

    public FrameBufferRef? ResolvedFramebuffer => boundaryResolver.Framebuffer;

    public bool BoundaryResolvedLastFrame => boundaryResolver.LastResolveSucceeded;

    /// <summary>
    /// Transient per-frame flag set by the tiled screenshot capture: hides
    /// the local player's own model (including its hands) from the atlas
    /// entity stage so the stitched photo is free of the photographer's
    /// character. Reset after every atlas draw.
    /// </summary>
    public bool HideLocalPlayerModel
    {
        get => entityModelRenderer.HideLocalPlayerModel;
        set => entityModelRenderer.HideLocalPlayerModel = value;
    }

    /// <summary>
    /// Live native cloud drift offset. The tiled screenshot pipeline freezes
    /// this value so every captured tile renders identical cloud shapes.
    /// </summary>
    public Vec3f? GetLiveCloudOffset()
    {
        try
        {
            return cloudRenderer?.GetLiveOffset();
        }
        catch
        {
            return null;
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
        int vegetationPass,
        int disclosureRadius,
        bool requireOpaqueDepth
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
                bool applyCaveFilter = hideUndergroundCaves
                    && !DeveloperDisableCaveFilter;
                shader.Uniform("atlasHideCaves", applyCaveFilter ? 1 : 0);
                if (shader.HasUniform("atlasBoundaryEnabled"))
                {
                    shader.Uniform(
                        "atlasBoundaryEnabled",
                        disclosureRadius > 0 ? 1 : 0
                    );
                    shader.Uniform(
                        "atlasDisclosureCenterXZ",
                        (float)capi.World.Player.Entity.Pos.X,
                        (float)capi.World.Player.Entity.Pos.Z
                    );
                    shader.Uniform(
                        "atlasDisclosureRadius",
                        (float)Math.Max(GlobalConstants.ChunkSize, disclosureRadius)
                    );
                }
                if (shader.HasUniform("atlasCompleteBoundaryEnabled"))
                {
                    shader.Uniform(
                        "atlasCompleteBoundaryEnabled",
                        atlasCompleteBoundaryEnabled ? 1 : 0
                    );
                    shader.Uniform(
                        "atlasCompleteBoundaryMinXZ",
                        atlasCompleteMinimumChunkX * (float)GlobalConstants.ChunkSize,
                        atlasCompleteMinimumChunkZ * (float)GlobalConstants.ChunkSize
                    );
                    shader.Uniform(
                        "atlasCompleteBoundaryMaxXZ",
                        (atlasCompleteMaximumChunkX + 1) * (float)GlobalConstants.ChunkSize,
                        (atlasCompleteMaximumChunkZ + 1) * (float)GlobalConstants.ChunkSize
                    );
                }
                if (shader.HasUniform("atlasRequireOpaqueDepth"))
                {
                    bool bindOpaqueDepth = requireOpaqueDepth;
                    FrameBufferRef primaryFramebuffer = capi.Render.FrameBuffers[
                        (int)EnumFrameBuffer.Primary
                    ];
                    bool hasOpaqueDepth = bindOpaqueDepth
                        && primaryFramebuffer.DepthTextureId > 0;
                    shader.Uniform(
                        "atlasRequireOpaqueDepth",
                        hasOpaqueDepth ? 1 : 0
                    );
                    if (hasOpaqueDepth)
                    {
                        shader.BindTexture2D(
                            "atlasOpaqueDepthTex",
                            primaryFramebuffer.DepthTextureId,
                            OpaqueDepthTextureUnit
                        );
                    }
                }
                if (shader.HasUniform("atlasSeaLevel"))
                {
                    shader.Uniform("atlasSeaLevel", (float)capi.World.SeaLevel);
                }
                shader.Uniform(
                    "atlasConcealOres",
                    concealSurvivalOres ? 1 : 0
                );
                if (shader.HasUniform("atlasHideVegetation"))
                {
                    shader.Uniform("atlasHideVegetation", hideVegetation ? 1 : 0);
                }
                if (shader.HasUniform("atlasVegetationPass"))
                {
                    shader.Uniform("atlasVegetationPass", vegetationPass);
                }
                if (shader.HasUniform("atlasVegetationMaskEnabled"))
                {
                    shader.Uniform(
                        "atlasVegetationMaskEnabled",
                        vegetationTextureMask.Ready ? 1 : 0
                    );
                }
                if (shader.HasUniform("atlasTextureMipBias"))
                {
                    shader.Uniform("atlasTextureMipBias", atlasTextureMipBias);
                }
                if (shader.HasUniform("atlasMinimumTerrainBrightness"))
                {
                    shader.Uniform("atlasMinimumTerrainBrightness", 0.20f);
                }
                if (shader.HasUniform("atlasFilteringEnabled"))
                {
                    shader.Uniform(
                        "atlasFilteringEnabled",
                        DeveloperDisableFiltering ? 0 : 1
                    );
                }
                if (shader.HasUniform("atlasVegetationMipBias"))
                {
                    shader.Uniform("atlasVegetationMipBias", atlasVegetationMipBias);
                }
                if (shader.HasUniform("atlasVegetationAlphaCoverage"))
                {
                    shader.Uniform(
                        "atlasVegetationAlphaCoverage",
                        atlasVegetationAlphaCoverage
                    );
                }
                if (shader.HasUniform("atlasVegetationDebugMode"))
                {
                    shader.Uniform(
                        "atlasVegetationDebugMode",
                        DeveloperVegetationDebugMode
                    );
                }
                if (shader.HasUniform("atlasCaveConcealmentColor"))
                {
                    shader.Uniform(
                        "atlasCaveConcealmentColor",
                        ScaleColor(CaveConcealmentColor, atlasCaveMaskBrightness)
                    );
                }
                if (surfaceHeightTexture?.Ready == true
                    && surfaceHeightTexture.TextureId > 0)
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
                            if (shader.HasUniform("atlasLayerContours"))
                            {
                                shader.Uniform(
                                    "atlasLayerContours",
                                    mapLayerTexture.ContoursEnabled ? 1 : 0
                                );
                            }
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
                "[ModernAtlas] Opaque terrain above sea level keeps complete exact walls; below sea level it keeps its {0}-block exterior layer with a {1:0.0}-block neutral band, while transparent and liquid geometry is filtered at every height.",
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
                && shader.HasUniform("atlasBoundaryEnabled")
                && shader.HasUniform("atlasDisclosureCenterXZ")
                && shader.HasUniform("atlasDisclosureRadius")
                && shader.HasUniform("atlasConcealOres")
                && shader.HasUniform("atlasHideVegetation")
                && shader.HasUniform("atlasVegetationMaskTex")
                && shader.HasUniform("atlasFilteringEnabled")
                && shader.HasUniform("atlasVegetationMipBias")
                && shader.HasUniform("atlasVegetationAlphaCoverage")
                && shader.HasUniform("atlasVegetationDebugMode")
                && shader.HasUniform("atlasVegetationPass")
                && shader.HasUniform("atlasVegetationMaskEnabled")
                && shader.HasUniform("atlasRequireOpaqueDepth");
        }

        if (state != null && !ReferenceEquals(shader, state.Shader))
        {
            RestoreAtlasFilterSource(program);
        }

        string? injected = InjectAtlasFilter(
            source,
            supportsBoundaryColor,
            program == EnumShaderProgram.Chunktopsoil
        );
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
            && shader.HasUniform("atlasBoundaryEnabled")
            && shader.HasUniform("atlasDisclosureCenterXZ")
            && shader.HasUniform("atlasDisclosureRadius")
            && shader.HasUniform("atlasConcealOres")
            && shader.HasUniform("atlasHideVegetation")
            && shader.HasUniform("atlasVegetationMaskTex")
            && shader.HasUniform("atlasFilteringEnabled")
            && shader.HasUniform("atlasVegetationMipBias")
            && shader.HasUniform("atlasVegetationAlphaCoverage")
            && shader.HasUniform("atlasVegetationDebugMode")
            && shader.HasUniform("atlasVegetationPass")
            && shader.HasUniform("atlasVegetationMaskEnabled")
            && shader.HasUniform("atlasRequireOpaqueDepth");
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
                if (shader.HasUniform("atlasBoundaryEnabled"))
                {
                    shader.Uniform("atlasBoundaryEnabled", 0);
                }
                if (shader.HasUniform("atlasCompleteBoundaryEnabled"))
                {
                    shader.Uniform("atlasCompleteBoundaryEnabled", 0);
                }
                if (shader.HasUniform("atlasRequireOpaqueDepth"))
                {
                    shader.Uniform("atlasRequireOpaqueDepth", 0);
                }
                if (shader.HasUniform("atlasConcealOres"))
                {
                    shader.Uniform("atlasConcealOres", 0);
                }
                if (shader.HasUniform("atlasHideVegetation"))
                {
                    shader.Uniform("atlasHideVegetation", 0);
                }
                if (shader.HasUniform("atlasFilteringEnabled"))
                {
                    shader.Uniform("atlasFilteringEnabled", 0);
                }
                if (shader.HasUniform("atlasMinimumTerrainBrightness"))
                {
                    shader.Uniform("atlasMinimumTerrainBrightness", 0f);
                }
                if (shader.HasUniform("atlasVegetationMipBias"))
                {
                    shader.Uniform("atlasVegetationMipBias", 0f);
                }
                if (shader.HasUniform("atlasVegetationAlphaCoverage"))
                {
                    shader.Uniform("atlasVegetationAlphaCoverage", 0f);
                }
                if (shader.HasUniform("atlasVegetationDebugMode"))
                {
                    shader.Uniform("atlasVegetationDebugMode", 0);
                }
                if (shader.HasUniform("atlasVegetationPass"))
                {
                    shader.Uniform("atlasVegetationPass", 0);
                }
                if (shader.HasUniform("atlasVegetationMaskEnabled"))
                {
                    shader.Uniform("atlasVegetationMaskEnabled", 0);
                }
                if (shader.HasUniform("atlasLayerEnabled"))
                {
                    shader.Uniform("atlasLayerEnabled", 0);
                }
                if (shader.HasUniform("atlasLayerContours"))
                {
                    shader.Uniform("atlasLayerContours", 0);
                }
                if (shader.HasUniform("atlasDisableHorizonFade"))
                {
                    shader.Uniform("atlasDisableHorizonFade", 0);
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

    private static string? InjectAtlasFilter(
        string source,
        bool supportsBoundaryColor,
        bool usesTopsoilUv
    )
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
        const string topsoilGrassSample =
            "texture(terrainTex, uv2 + vec2(blockTextureSize.x * normal.y, 0))";
        if (renamed.Contains(topsoilGrassSample, StringComparison.Ordinal))
        {
            renamed = renamed.Replace(
                topsoilGrassSample,
                "modernAtlasSampleTerrain(terrainTex, uv2 + vec2(blockTextureSize.x * normal.y, 0))",
                StringComparison.Ordinal
            );
        }

        // chunkopaque/chunktopsoil calculate their vertex alpha from the
        // normal camera's view-distance fade. At the atlas disclosure edge
        // that fade drives otherwise valid foliage to zero, so tiny camera
        // changes make the alpha test pop. Preserve the texture's authored
        // alpha while removing only that camera-distance multiplier inside
        // the atlas shader variant. The ordinary world shader source is not
        // modified by this replacement.
        const string opaqueColorExpression =
            "getColorMapped(terrainTexLinear, modernAtlasSampleTerrain(terrainTex, uv)) * rgba";
        renamed = renamed.Replace(
            opaqueColorExpression,
            "getColorMapped(terrainTexLinear, modernAtlasSampleTerrain(terrainTex, uv))"
                + " * vec4(rgba.rgb, atlasFilteringEnabled > 0 ? 1.0 : rgba.a)",
            StringComparison.Ordinal
        );
        const string topsoilBrownExpression =
            "modernAtlasSampleTerrain(terrainTex, uv) * rgba";
        renamed = renamed.Replace(
            topsoilBrownExpression,
            "modernAtlasSampleTerrain(terrainTex, uv)"
                + " * vec4(rgba.rgb, atlasFilteringEnabled > 0 ? 1.0 : rgba.a)",
            StringComparison.Ordinal
        );
        const string topsoilGrassExpression =
            "getColorMapped(terrainTexLinear, modernAtlasSampleTerrain(terrainTex, uv2 + vec2(blockTextureSize.x * normal.y, 0))) * rgba";
        renamed = renamed.Replace(
            topsoilGrassExpression,
            "getColorMapped(terrainTexLinear, modernAtlasSampleTerrain(terrainTex, uv2 + vec2(blockTextureSize.x * normal.y, 0)))"
                + " * vec4(rgba.rgb, atlasFilteringEnabled > 0 ? 1.0 : rgba.a)",
            StringComparison.Ordinal
        );
        const string transparentColorExpression =
            "rgba * getColorMapped(terrainTex, modernAtlasSampleTerrain(terrainTex, uv))";
        renamed = renamed.Replace(
            transparentColorExpression,
            "vec4(rgba.rgb, atlasFilteringEnabled > 0 ? 1.0 : rgba.a)"
                + " * getColorMapped(terrainTex, modernAtlasSampleTerrain(terrainTex, uv))",
            StringComparison.Ordinal
        );
        renamed = renamed.Insert(
            mainIndex,
            "uniform int atlasFilteringEnabled;\n"
                + "uniform float atlasMinimumTerrainBrightness;\n"
                + "uniform float atlasVegetationMipBias;\n"
                + "uniform float atlasVegetationAlphaCoverage;\n"
                + "uniform int atlasVegetationDebugMode;\n"
                + "uniform int atlasVegetationPass;\n"
                + "uniform int atlasVegetationMaskEnabled;\n"
                + "vec4 modernAtlasSampleTerrain(sampler2D sourceTexture, vec2 sourceUv);\n"
                + "bool modernAtlasIsWindVegetation();\n"
                + "float modernAtlasAlphaTestThreshold(float alphaValue, float baseThreshold);\n"
                + "vec4 modernAtlasApplyFogAndDirectionalWithNormal(vec4 targetColor, float fogAmount, vec3 surfaceNormal, float normalShadeIntensity, float minimumNormalShade, vec3 fragmentWorldPosition);\n"
                + "void modernAtlasClampMinimumBrightness(inout vec4 targetColor);\n"
                + "vec4 modernAtlasVegetationDebugColor(vec4 color, float alphaValue, float threshold, float lodFadeValue);\n\n"
        );
        const string lod0FadeTerm = "- lod0Fade";
        if (renamed.Contains(lod0FadeTerm, StringComparison.Ordinal))
        {
            renamed = renamed.Replace(
                lod0FadeTerm,
                DeveloperPreserveVegetationLod0
                    ? "- (atlasDisableLod0Fade > 0 && !modernAtlasIsWindVegetation() ? 0.0 : lod0Fade)"
                    : "- (atlasDisableLod0Fade > 0 ? 0.0 : lod0Fade)",
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
        // The native fragment shaders still call the perspective camera's
        // shadow-map helpers even when DropShadowIntensity is temporarily
        // zero. Avoid the texture reads altogether in the atlas variant: a
        // stale normal-camera shadow projection produces broad, camera-bound
        // dark bands across otherwise valid chunk meshes. Directional face
        // shading remains active through the engine's normal term.
        const string opaqueShadowSample =
            "float b = getBrightnessFromShadowMap();";
        int opaqueShadowSampleIndex = renamed.IndexOf(
            opaqueShadowSample,
            mainIndex,
            StringComparison.Ordinal
        );
        if (opaqueShadowSampleIndex >= 0)
        {
            renamed = renamed.Remove(
                opaqueShadowSampleIndex,
                opaqueShadowSample.Length
            ).Insert(
                opaqueShadowSampleIndex,
                "float b = atlasFilteringEnabled > 0"
                    + " ? 1.0 : getBrightnessFromShadowMap();"
            );
        }
        renamed = renamed.Replace(
            "min(b, nb), worldPos.xyz",
            "max(min(b, nb), atlasFilteringEnabled > 0"
                + " ? atlasMinimumTerrainBrightness : 0.0), worldPos.xyz",
            StringComparison.Ordinal
        );
        renamed = renamed.Replace(
            "outColor = applyFogAndShadowWithNormal(outColor,",
            "outColor = modernAtlasApplyFogAndDirectionalWithNormal(outColor,",
            StringComparison.Ordinal
        );
        renamed = renamed.Replace(
            "texColor = applyFogAndShadowWithNormal(texColor,",
            "texColor = modernAtlasApplyFogAndDirectionalWithNormal(texColor,",
            StringComparison.Ordinal
        );
        renamed = renamed.Replace(
            "aTest < alphaTest",
            "aTest < modernAtlasAlphaTestThreshold(aTest, alphaTest)",
            StringComparison.Ordinal
        );
        // chunkopaque has a second hard discard on the normal camera's
        // distance-faded vertex alpha. Keep it for the ordinary world, but
        // let the authored texture alpha decide in the atlas variant.
        renamed = renamed.Replace(
            "|| rgba.a < 0.005",
            "|| (atlasFilteringEnabled == 0 && rgba.a < 0.005)",
            StringComparison.Ordinal
        );
        renamed = renamed.Replace(
            "if (rgba.a < 0.005) discard;",
            "if (atlasFilteringEnabled == 0 && rgba.a < 0.005) discard;",
            StringComparison.Ordinal
        );
        const string weakAlphaLine =
            "if ((renderFlags & WindModeBitMask) == WindModeWeakLowAlphaTest) aTest *= 4;";
        if (renamed.Contains(weakAlphaLine, StringComparison.Ordinal))
        {
            renamed = renamed.Replace(
                weakAlphaLine,
                weakAlphaLine
                    + "\n\n"
                    + "\tif (atlasFilteringEnabled > 0\n"
                    + "\t    && atlasVegetationDebugMode > 0\n"
                    + "\t    && modernAtlasIsWindVegetation())\n"
                    + "\t{\n"
                    + "\t\toutColor = modernAtlasVegetationDebugColor(\n"
                    + "\t\t\toutColor, aTest, alphaTest, lod0Fade\n"
                    + "\t\t);\n"
                    + "\t\toutGlow = vec4(0.0);\n"
                    + "\t\treturn;\n"
                    + "\t}",
                StringComparison.Ordinal
            );
        }
        else
        {
            int topsoilAlphaIndex = renamed.IndexOf(
                "float aTest = outColor.a;",
                mainIndex,
                StringComparison.Ordinal
            );
            if (topsoilAlphaIndex >= 0)
            {
                int topsoilDebugIndex = renamed.IndexOf(
                    "#if NORMALVIEW == 0",
                    topsoilAlphaIndex,
                    StringComparison.Ordinal
                );
                if (topsoilDebugIndex >= 0)
                {
                    renamed = renamed.Insert(
                        topsoilDebugIndex,
                        "if (atlasFilteringEnabled > 0\n"
                            + "    && atlasVegetationDebugMode > 0\n"
                            + "    && modernAtlasIsWindVegetation())\n"
                            + "{\n"
                            + "    outColor = modernAtlasVegetationDebugColor(\n"
                            + "        outColor, aTest, alphaTest, 0.0\n"
                            + "    );\n"
                            + "    outGlow = vec4(0.0);\n"
                            + "    return;\n"
                            + "}\n\n"
                    );
                }
            }
        }
        string atlasBrightnessCode = supportsBoundaryColor
            ? "    modernAtlasClampMinimumBrightness(outColor);\n"
            : "";
        string mapLayerCode = supportsBoundaryColor
            ? """
    modernAtlasApplyMapLayer(outColor, modernAtlasAbsoluteWorldPosition);
"""
            : "";
        if (!supportsBoundaryColor)
        {
            const string oitOutput = "OIT(texColor, glowLevel);";
            if (!renamed.Contains(oitOutput, StringComparison.Ordinal))
            {
                return null;
            }
            renamed = renamed.Replace(
                oitOutput,
                "if (atlasFilteringEnabled > 0\n"
                    + "        && atlasVegetationDebugMode > 0\n"
                    + "        && modernAtlasIsWindVegetation())\n"
                    + "    {\n"
                    + "        OIT(\n"
                    + "            modernAtlasVegetationDebugColor(\n"
                    + "                texColor, texColor.a, 0.0, 0.0\n"
                    + "            ),\n"
                    + "            0.0\n"
                    + "        );\n"
                    + "        return;\n"
                    + "    }\n"
                    + "modernAtlasApplyRelativeMapLayer(texColor, worldPos.xyz);\n"
                    + "    OIT(texColor, glowLevel);",
                StringComparison.Ordinal
            );
            renamed = renamed.Insert(
                mainIndex,
                "void modernAtlasApplyRelativeMapLayer(inout vec4 targetColor, vec3 relativeWorldPosition);\n\n"
            );
        }
        string caveFilterCode = supportsBoundaryColor
            ? """
    // Apply the local exterior test at every altitude. A cave entrance in a
    // mountain is still an interior cutout; restricting this to sea level
    // leaves exactly the dark underside bands that flicker at an atlas edge.
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
            if (modernAtlasHasExteriorFloor
                && modernAtlasAbsoluteWorldPosition.y
                >= modernAtlasExteriorFloor - atlasCaveConcealmentDepth)
            {
                // Keep a thin band of real opaque faces near the exterior to
                // quiet clipped cave mouths. The branch below uses the same
                // neutral treatment for deeper existing faces so a cave cutout
                // never exposes the dark Primary background as a moving band.
                modernAtlasOriginalMain();
                outColor = vec4(atlasCaveConcealmentColor, 1.0);
                modernAtlasApplyMapLayer(
                    outColor,
                    modernAtlasAbsoluteWorldPosition
                );
                return;
            }

            // Deeper cave faces are not part of the exterior atlas. Discard
            // the existing fragment so no black underside or underground
            // tunnel can become visible; no replacement geometry is created.
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

        string atlasBoundaryCode = """
    if (atlasCompleteBoundaryEnabled > 0
        && (any(lessThan(
                modernAtlasAbsoluteWorldPosition.xz,
                atlasCompleteBoundaryMinXZ
            ))
            || any(greaterThanEqual(
                modernAtlasAbsoluteWorldPosition.xz,
                atlasCompleteBoundaryMaxXZ
            ))))
    {
        discard;
    }

    // Apply the hard world-space cutoff to the fragment's real block position
    // in every terrain material, including OIT. Never move this test according
    // to height or camera tilt: doing so can translate tall cliff/tree faces
    // from outside the allowed cylinder into it and expose vertical pillars.
    vec2 modernAtlasDisclosureDelta =
        modernAtlasAbsoluteWorldPosition.xz - atlasDisclosureCenterXZ;
    float modernAtlasDisclosureRadiusSquared =
        atlasDisclosureRadius * atlasDisclosureRadius;
    if (atlasBoundaryEnabled > 0
        && dot(
            modernAtlasDisclosureDelta,
            modernAtlasDisclosureDelta
        ) >= modernAtlasDisclosureRadiusSquared)
    {
        discard;
    }

    // The last world chunk is a disclosure seam, not a cutaway wall. Keep
    // only the real exterior surface in that one-chunk ring. This removes
    // deep chunk sides and partial tall objects whose remaining halves would
    // otherwise look like floating terrain, trunks or leaf columns after the
    // hard circular cutoff. Interior geometry keeps its full block models.
    if (atlasHideCaves > 0
        && length(modernAtlasDisclosureDelta)
            >= max(0.0, atlasDisclosureRadius - 32.0))
    {
        ivec2 modernAtlasEdgeSample = ivec2(floor(
            (modernAtlasAbsoluteWorldPosition.xz - atlasSurfaceOriginXZ)
                / atlasSurfaceSampleSize
        ));
        float modernAtlasEdgeSurfaceHeight;
        if (!modernAtlasReadSurfaceHeight(
                modernAtlasEdgeSample,
                modernAtlasEdgeSurfaceHeight
            )
            || modernAtlasAbsoluteWorldPosition.y
                < modernAtlasEdgeSurfaceHeight + 0.85
            || modernAtlasAbsoluteWorldPosition.y
                > modernAtlasEdgeSurfaceHeight + 1.15)
        {
            discard;
        }
    }
""";
        string opaqueDepthCode = """
    // Transparent pools can complete independently of the opaque pool. A
    // column-level cull is therefore insufficient: if no opaque fragment
    // actually wrote Primary depth at this screen pixel, a leaf, flower or
    // liquid-side fragment would float over the atlas background.
    if (atlasRequireOpaqueDepth > 0)
    {
        ivec2 modernAtlasDepthPosition = ivec2(gl_FragCoord.xy);
        ivec2 modernAtlasDepthDimensions = textureSize(
            atlasOpaqueDepthTex,
            0
        );
        if (any(lessThan(modernAtlasDepthPosition, ivec2(0)))
            || any(greaterThanEqual(
                modernAtlasDepthPosition,
                modernAtlasDepthDimensions
            )))
        {
            discard;
        }
        float modernAtlasOpaqueDepth = texelFetch(
            atlasOpaqueDepthTex,
            modernAtlasDepthPosition,
            0
        ).r;
        if (modernAtlasOpaqueDepth >= 0.999999
            || gl_FragCoord.z > modernAtlasOpaqueDepth + 0.0005)
        {
            discard;
        }
        // The dedicated vegetation pass must be backed by a small continuous
        // patch of completed opaque terrain, not merely by one trunk, log or
        // isolated face at the same screen pixel. This removes the sparse
        // leaf/grass fringe beyond ragged loaded-ground edges without
        // changing solid terrain or ordinary world rendering.
        if (atlasVegetationPass == 2)
        {
            for (int modernAtlasOffsetY = -2; modernAtlasOffsetY <= 2; modernAtlasOffsetY++)
            {
                for (int modernAtlasOffsetX = -2; modernAtlasOffsetX <= 2; modernAtlasOffsetX++)
                {
                    ivec2 modernAtlasNeighbor = modernAtlasDepthPosition
                        + ivec2(modernAtlasOffsetX, modernAtlasOffsetY);
                    if (any(lessThan(modernAtlasNeighbor, ivec2(0)))
                        || any(greaterThanEqual(
                            modernAtlasNeighbor,
                            modernAtlasDepthDimensions
                        ))
                        || texelFetch(
                            atlasOpaqueDepthTex,
                            modernAtlasNeighbor,
                            0
                        ).r >= 0.999999)
                    {
                        discard;
                    }
                }
            }
        }
    }
""";

        string vegetationUvMaskExpression = usesTopsoilUv
            ? "\n        || (atlasVegetationMaskEnabled > 0 && modernAtlasIsVegetation(uv2))"
            : "";

        return renamed + """

// MODERNATLAS_SURFACE_AND_BOUNDARY_FILTER
uniform int atlasHideCaves;
uniform int atlasBoundaryEnabled;
uniform vec2 atlasDisclosureCenterXZ;
uniform float atlasDisclosureRadius;
uniform int atlasCompleteBoundaryEnabled;
uniform vec2 atlasCompleteBoundaryMinXZ;
uniform vec2 atlasCompleteBoundaryMaxXZ;
uniform int atlasRequireOpaqueDepth;
uniform sampler2D atlasOpaqueDepthTex;
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
uniform int atlasLayerContours;
uniform int atlasConcealOres;
uniform float atlasTextureMipBias;
uniform sampler2D atlasOreMapTex;
uniform sampler2D atlasStoneTex;
uniform int atlasHideVegetation;
uniform sampler2D atlasVegetationMaskTex;

void modernAtlasClampMinimumBrightness(inout vec4 targetColor)
{
    if (atlasFilteringEnabled <= 0) return;

    float minimumBrightness = clamp(
        atlasMinimumTerrainBrightness,
        0.0,
        1.0
    );
    float luminance = dot(
        max(targetColor.rgb, vec3(0.0)),
        vec3(0.2126, 0.7152, 0.0722)
    );
    if (luminance >= minimumBrightness) return;
    if (luminance > 0.001)
    {
        targetColor.rgb *= minimumBrightness / luminance;
    }
    else
    {
        // A block with no native vertex light is still real loaded geometry.
        // Use the same subdued atlas floor as the cave occlusion material so
        // it cannot become a camera-dependent black strip.
        targetColor.rgb = vec3(minimumBrightness);
    }
}

void modernAtlasApplyMapLayer(
    inout vec4 targetColor,
    vec3 absoluteWorldPosition
)
{
    if (atlasLayerEnabled <= 0) return;

    vec2 layerPosition =
        (absoluteWorldPosition.xz - atlasLayerOriginXZ)
        / atlasLayerSampleSize;
    ivec2 layerDimensions = textureSize(atlasLayerTex, 0);
    if (any(lessThan(layerPosition, vec2(0.0)))
        || any(greaterThanEqual(layerPosition, vec2(layerDimensions))))
    {
        return;
    }

    vec2 layerUv = layerPosition / vec2(layerDimensions);
    vec4 layerColor = texture(atlasLayerTex, layerUv);
    float layerValidity = smoothstep(0.04, 0.22, layerColor.a);
    float layerScalar = clamp((layerColor.a - 0.25) / 0.75, 0.0, 1.0);
    float baseLuminance = dot(
        clamp(targetColor.rgb, vec3(0.0), vec3(1.0)),
        vec3(0.2126, 0.7152, 0.0722)
    );
    vec3 reliefColor = layerColor.rgb * mix(0.68, 1.18, baseLuminance);
    if (atlasLayerContours > 0)
    {
        float bands = layerScalar * 6.0;
        float distanceToLine = abs(fract(bands + 0.5) - 0.5);
        float lineWidth = max(fwidth(bands) * 0.55, 0.025);
        float contour = 1.0 - smoothstep(lineWidth, lineWidth * 2.2, distanceToLine);
        reliefColor *= mix(1.0, 0.86, contour * layerValidity);
    }
    targetColor.rgb = mix(
        targetColor.rgb,
        reliefColor,
        clamp(layerValidity * atlasLayerOpacity, 0.0, 1.0)
    );
}

void modernAtlasApplyRelativeMapLayer(
    inout vec4 targetColor,
    vec3 relativeWorldPosition
)
{
    modernAtlasApplyMapLayer(
        targetColor,
        relativeWorldPosition + atlasWorldOffset
    );
}

bool modernAtlasIsVegetation(vec2 sourceUv)
{
    if (atlasVegetationMaskEnabled <= 0) return false;

    ivec2 dimensions = textureSize(atlasVegetationMaskTex, 0);
    ivec2 position = clamp(
        ivec2(floor(sourceUv * vec2(dimensions))),
        ivec2(0),
        dimensions - ivec2(1)
    );
    return texelFetch(atlasVegetationMaskTex, position, 0).r > 0.5;
}

bool modernAtlasIsWindVegetation()
{
    int windMode = renderFlags & WindModeBitMask;
    // Water surfaces use a separate stable atlas shader. All other engine
    // wind modes represent plant/leaf geometry, including modded blocks that
    // use the public render-flag contract instead of a vanilla block ID.
    return windMode != 0 && windMode != WindModeLiquidWarp;
}

vec4 modernAtlasApplyFogAndDirectionalWithNormal(
    vec4 targetColor,
    float fogAmount,
    vec3 surfaceNormal,
    float normalShadeIntensity,
    float minimumNormalShade,
    vec3 fragmentWorldPosition
)
{
    if (atlasFilteringEnabled <= 0)
    {
        return applyFogAndShadowWithNormal(
            targetColor,
            fogAmount,
            surfaceNormal,
            normalShadeIntensity,
            minimumNormalShade,
            fragmentWorldPosition
        );
    }

    float directionalBrightness = getBrightnessFromNormal(
        surfaceNormal,
        normalShadeIntensity,
        minimumNormalShade
    );
    targetColor *= vec4(
        directionalBrightness,
        directionalBrightness,
        directionalBrightness,
        1.0
    );
    vec4 foggedColor = applyFog(targetColor, fogAmount);
    return applySpheresFog(foggedColor, fogAmount, fragmentWorldPosition);
}

float modernAtlasTerrainMipBias()
{
    if (atlasFilteringEnabled <= 0) return 0.0;
    return modernAtlasIsWindVegetation()
        ? atlasVegetationMipBias
        : atlasTextureMipBias;
}

float modernAtlasAlphaTestThreshold(
    float alphaValue,
    float baseThreshold
)
{
    if (atlasFilteringEnabled <= 0
        || atlasVegetationAlphaCoverage <= 0.0
        || !modernAtlasIsWindVegetation())
    {
        return baseThreshold;
    }

    // Keep a small derivative-sized coverage band instead of making a
    // binary alpha test switch an entire leaf quad as the camera crosses a
    // subpixel boundary. This is projection/footprint based, never temporal.
    float coverageBand = clamp(
        fwidth(alphaValue) * atlasVegetationAlphaCoverage,
        0.0,
        0.25
    );
    return max(0.0001, baseThreshold - coverageBand);
}

vec4 modernAtlasVegetationDebugColor(
    vec4 color,
    float alphaValue,
    float threshold,
    float lodFadeValue
)
{
    float windMode = float((renderFlags & WindModeBitMask) >> 25) / 15.0;
    if (atlasVegetationDebugMode == 1)
    {
        // Wind-flag coverage: red is the encoded engine wind mode.
        return vec4(windMode, 1.0 - windMode, 0.05, 1.0);
    }
    if (atlasVegetationDebugMode == 2)
    {
        // Mip bias and derivative coverage are shown together.
        return vec4(
            clamp(atlasVegetationMipBias / 2.0, 0.0, 1.0),
            clamp(atlasVegetationAlphaCoverage * 2.0, 0.0, 1.0),
            1.0 - clamp(atlasVegetationMipBias / 2.0, 0.0, 1.0),
            1.0
        );
    }
    if (atlasVegetationDebugMode == 3)
    {
        // Alpha before the engine discard, with low alpha kept visible.
        return vec4(clamp(alphaValue * 8.0, 0.0, 1.0), clamp(alphaValue, 0.0, 1.0), 0.0, 1.0);
    }
    if (atlasVegetationDebugMode == 4)
    {
        // Red is the pre-test alpha; green is the effective threshold.
        return vec4(
            clamp(alphaValue * 8.0, 0.0, 1.0),
            clamp(threshold * 100.0, 0.0, 1.0),
            clamp(fwidth(alphaValue) * 8.0, 0.0, 1.0),
            1.0
        );
    }
    if (atlasVegetationDebugMode == 5)
    {
        return vec4(
            clamp(lodFadeValue, 0.0, 1.0),
            0.0,
            1.0 - clamp(lodFadeValue, 0.0, 1.0),
            1.0
        );
    }
    return vec4(color.rgb, 1.0);
}

vec4 modernAtlasSampleTerrain(sampler2D sourceTexture, vec2 sourceUv)
{
    vec4 originalColor = texture(
        sourceTexture,
        sourceUv,
        modernAtlasTerrainMipBias()
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
""" + atlasBoundaryCode + opaqueDepthCode + """
    bool modernAtlasVegetation = modernAtlasIsWindVegetation()
        || (atlasVegetationMaskEnabled > 0 && modernAtlasIsVegetation(uv))
""" + vegetationUvMaskExpression + """
;
    if ((atlasHideVegetation > 0 || atlasVegetationPass == 1)
        && modernAtlasVegetation)
    {
        discard;
    }
    if (atlasVegetationPass == 2 && !modernAtlasVegetation)
    {
        discard;
    }
""" + caveFilterCode + """
    modernAtlasOriginalMain();
""" + atlasBrightnessCode + mapLayerCode + """
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

        if (atlasCompleteBoundaryEnabled
            && __instance.IndicesEnd > __instance.IndicesStart)
        {
            (int X, int Y, int Z) chunk = GetMeshChunk(
                __instance,
                GlobalConstants.ChunkSize
            );
            if (chunk.X < atlasCompleteMinimumChunkX
                || chunk.X > atlasCompleteMaximumChunkX
                || chunk.Z < atlasCompleteMinimumChunkZ
                || chunk.Z > atlasCompleteMaximumChunkZ)
            {
                __result = false;
                return false;
            }
        }

        if (DeveloperDisableFrustum)
        {
            __result = __instance.IndicesEnd > __instance.IndicesStart;
        }
        else
        {
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
                    // Use the same native range/LOD decision as the game for every
                    // atlas pass. The transparent pass below is additionally
                    // constrained to a column that the opaque pass actually
                    // accepted, so OIT cannot expose a floating leaf mesh.
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
        }

        if (__result
            && atlasDisclosureCullingOverride
            && !IsInsideDisclosureBoundary(
                __instance.FrustumCullSphere
            ))
        {
            // The guarded culling projection intentionally admits a small
            // screen-space apron to prevent subpixel pool popping. It must
            // never enlarge the player-anchored disclosure area, however.
            // Reject a whole mesh location unless its complete XZ footprint
            // fits inside the boundary. The fragment shader remains the
            // final per-pixel guard for locations that straddle the edge.
            __result = false;
        }

        if (__result
            && atlasSupportedTerrainColumns != null
            && __instance.IndicesEnd > __instance.IndicesStart)
        {
            (int X, int Y, int Z) supportedChunk = GetMeshChunk(
                __instance,
                GlobalConstants.ChunkSize
            );
            if (!atlasSupportedTerrainColumns.Contains(
                    (supportedChunk.X, supportedChunk.Z)
                ))
            {
                __result = false;
            }
        }

        if (__result
            && atlasSupportedSurfaceSections != null
            && __instance.IndicesEnd > __instance.IndicesStart)
        {
            (int X, int Y, int Z) edgeChunk = GetMeshChunk(
                __instance,
                GlobalConstants.ChunkSize
            );
            double edgeCenterX = (edgeChunk.X + 0.5) * GlobalConstants.ChunkSize;
            double edgeCenterZ = (edgeChunk.Z + 0.5) * GlobalConstants.ChunkSize;
            double edgeDeltaX = edgeCenterX - atlasDisclosureCenterX;
            double edgeDeltaZ = edgeCenterZ - atlasDisclosureCenterZ;
            double surfaceOnlyRadius = Math.Max(
                GlobalConstants.ChunkSize,
                atlasDisclosureRadius - GlobalConstants.ChunkSize * 2
            );
            if (edgeDeltaX * edgeDeltaX + edgeDeltaZ * edgeDeltaZ
                    > surfaceOnlyRadius * surfaceOnlyRadius
                && !atlasSupportedSurfaceSections.Contains(edgeChunk))
            {
                // Near the streaming/disclosure frontier, upper vertical
                // sections can complete before the ground silhouette below
                // them. Do not submit those tree/fluid-only sections. The
                // actual surface section remains exact block geometry.
                __result = false;
            }
        }

        if (__result
            && atlasDisclosureCullingOverride
            && (atlasVegetationOnlyVisibilityOverride
                || atlasTransparentVisibilityOverride
                || atlasLiquidVisibilityOverride)
            && !IsInsideDependentMaterialBoundary(__instance.FrustumCullSphere))
        {
            // Dependent surfaces are allowed only slightly inside the hard
            // edge. Their pools can contain leaves, grass or water after the
            // corresponding solid ground mesh has become unavailable. A
            // half-chunk guard prevents that last material-only fringe while
            // the world-space fragment cutoff still supplies the exact circle.
            __result = false;
        }

        if (__result
            && atlasVegetationOnlyVisibilityOverride
            && __instance.IndicesEnd > __instance.IndicesStart)
        {
            (int X, int Y, int Z) vegetationChunk = GetMeshChunk(
                __instance,
                GlobalConstants.ChunkSize
            );
            if (atlasVisibleTerrainColumns?.Contains(
                    (vegetationChunk.X, vegetationChunk.Z)
                ) != true)
            {
                // The vegetation-only pass may use only columns that the
                // preceding ground pass accepted. This prevents an opaque
                // alpha-tested plant mesh from surviving where its terrain
                // column has no completed atlas mesh.
                __result = false;
            }
        }

        if (__result && __instance.IndicesEnd > __instance.IndicesStart)
        {
            if (atlasLiquidVisibilityOverride
                && atlasLiquidAdapter?.IsCompletedLiquidChunk(__instance) != true)
            {
                __result = false;
            }
            else if (atlasTransparentVisibilityOverride)
            {
                (int X, int Y, int Z) chunk = GetMeshChunk(
                    __instance,
                    GlobalConstants.ChunkSize
                );
                if (atlasVisibleTerrainColumns?.Contains((chunk.X, chunk.Z)) != true)
                {
                    // OIT pools can become available independently from the
                    // opaque pool. Composing those fragments without their
                    // completed terrain column blackens the atlas background.
                    __result = false;
                }
            }
        }

        return false;
    }

    private static bool IsInsideDisclosureBoundary(Sphere sphere)
    {
        // This is only a coarse GPU-pool rejection. The final framebuffer
        // mask enforces the exact material-independent circular boundary.
        double deltaX = sphere.x - atlasDisclosureCenterX;
        double deltaZ = sphere.z - atlasDisclosureCenterZ;
        double horizontalDistance = Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
        return horizontalDistance - sphere.radius <= atlasDisclosureRadius;
    }

    private static bool IsInsideDependentMaterialBoundary(Sphere sphere)
    {
        double allowedCenterRadius = atlasDisclosureRadius
            - GlobalConstants.ChunkSize * 0.5;
        if (allowedCenterRadius <= 0) return false;
        double deltaX = sphere.x - atlasDisclosureCenterX;
        double deltaZ = sphere.z - atlasDisclosureCenterZ;
        return deltaX * deltaX + deltaZ * deltaZ
            <= allowedCenterRadius * allowedCenterRadius;
    }

    private static void CollectAtlasVisibleTerrain(
        ModelDataPoolLocation __instance,
        EnumFrustumCullMode mode,
        bool __result
    )
    {
        if (!atlasVisibilityOverride
            || !atlasTerrainCollectionOverride
            || mode != EnumFrustumCullMode.CullNormal
            || !__result
            || __instance.IndicesEnd <= __instance.IndicesStart)
        {
            return;
        }

        (int X, int Y, int Z) chunk = GetMeshChunk(
            __instance,
            GlobalConstants.ChunkSize
        );
        atlasVisibleTerrainColumns?.Add((chunk.X, chunk.Z));
    }

    private void UpdateCompleteViewBoundary(int viewDistanceBlocks)
    {
        atlasCompleteBoundaryEnabled = false;
        if (viewDistanceBlocks <= 0)
        {
            return;
        }

        int chunkSize = GlobalConstants.ChunkSize;
        int playerChunkX = FloorDiv(
            (int)Math.Floor(capi.World.Player.Entity.Pos.X), chunkSize
        );
        int playerChunkZ = FloorDiv(
            (int)Math.Floor(capi.World.Player.Entity.Pos.Z), chunkSize
        );
        int safeRadius = Math.Max(
            0,
            (int)Math.Floor(viewDistanceBlocks / (Math.Sqrt(2d) * chunkSize)) - 1
        );
        if (safeRadius < 1) return;

        atlasCompleteMinimumChunkX = playerChunkX - safeRadius;
        atlasCompleteMaximumChunkX = playerChunkX + safeRadius;
        atlasCompleteMinimumChunkZ = playerChunkZ - safeRadius;
        atlasCompleteMaximumChunkZ = playerChunkZ + safeRadius;
        atlasCompleteBoundaryEnabled = true;

        if (loggedCompleteBoundaryRadius != safeRadius)
        {
            loggedCompleteBoundaryRadius = safeRadius;
            capi.Logger.Notification(
                "[ModernAtlas] Atlas-only view boundary: {0}x{0} chunks with a one-chunk safety inset (view distance {1} blocks).",
                safeRadius * 2 + 1,
                viewDistanceBlocks
            );
        }
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
        bool bindVegetationMask
    )
    {
        atlasOreTextureBindingAdapter = concealSurvivalOres || bindVegetationMask
            ? this
            : null;
        atlasOreTextureBindingOverride = concealSurvivalOres;
        atlasVegetationTextureBindingOverride = bindVegetationMask;
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
        Vec3f? frozenCloudOffset,
        bool hideUndergroundCaves,
        bool concealSurvivalOres,
        bool hideVegetation,
        AtlasSurfaceHeightTexture? surfaceHeightTexture,
        AtlasMapLayerTexture? mapLayerTexture,
        float mapLayerOpacity,
        int disclosureRadius,
        bool blitToDefault
    )
    {
        if (DeveloperDisableTransparentPass)
        {
            if (!loggedTransparentSuccess)
            {
                loggedTransparentSuccess = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Developer diagnostic disabled the atlas transparent/OIT pass."
                );
            }
            return false;
        }
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
                        0,
                        disclosureRadius,
                        true
            ))
            {
                throw new InvalidOperationException(
                    "The transparent-block safety filters could not be configured."
                );
            }
            BeginAtlasTextureBindings(
                concealSurvivalOres,
                vegetationTextureMask.Ready
            );
            atlasTransparentVisibilityOverride = true;
            try
            {
                renderOit.Invoke(chunkRenderer, new object[] { deltaTime });
            }
            finally
            {
                atlasTransparentVisibilityOverride = false;
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
            BeginAtlasTextureBindings(
                concealSurvivalOres,
                vegetationTextureMask.Ready
            );
            atlasTransparentVisibilityOverride = true;
            try
            {
                renderAfterOit.Invoke(chunkRenderer, new object[] { deltaTime });
            }
            finally
            {
                atlasTransparentVisibilityOverride = false;
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
                disclosureRadius
            );
            if (cloudsEnabled)
            {
                cloudRenderer?.Render(
                    projection,
                    view,
                    pausedCloudAnimationDeltaTime,
                    true,
                    frozenCloudOffset,
                    atlasSunColor,
                    atlasExposure
                );
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
            atlasTransparentVisibilityOverride = false;
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
        activeLiquidShader.Uniform(
            "completeBoundaryEnabled",
            atlasCompleteBoundaryEnabled ? 1 : 0
        );
        activeLiquidShader.Uniform(
            "completeBoundaryMinXZ",
            atlasCompleteMinimumChunkX * (float)GlobalConstants.ChunkSize,
            atlasCompleteMinimumChunkZ * (float)GlobalConstants.ChunkSize
        );
        activeLiquidShader.Uniform(
            "completeBoundaryMaxXZ",
            (atlasCompleteMaximumChunkX + 1) * (float)GlobalConstants.ChunkSize,
            (atlasCompleteMaximumChunkZ + 1) * (float)GlobalConstants.ChunkSize
        );
        bool applySurfaceFilter = hideUndergroundCaves
            && !DeveloperDisableCaveFilter
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
            activeLiquidShader.Uniform(
                "atlasLayerContours",
                mapLayerTexture.ContoursEnabled ? 1 : 0
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

    private void BuildSupportedTerrainColumns(
        AtlasSurfaceHeightTexture? surfaceHeightTexture
    )
    {
        supportedTerrainColumns.Clear();
        supportedSurfaceSections.Clear();
        if (surfaceHeightTexture?.Ready != true
            || poolsByRenderPassField.GetValue(chunkRenderer)
                is not MeshDataPoolManager[][] passes)
        {
            return;
        }

        AddSurfaceSupportFromPass(EnumChunkRenderPass.Opaque);
        AddSurfaceSupportFromPass(EnumChunkRenderPass.OpaqueNoCull);
        AddSurfaceSupportFromPass(EnumChunkRenderPass.TopSoil);

        void AddSurfaceSupportFromPass(EnumChunkRenderPass pass)
        {
            int passIndex = (int)pass;
            if (passIndex < 0 || passIndex >= passes.Length) return;
            foreach (MeshDataPoolManager? manager in passes[passIndex])
            {
                if (manager == null
                    || managerPoolsField.GetValue(manager) is not IEnumerable pools)
                {
                    continue;
                }

                foreach (object? entry in pools)
                {
                    if (entry is not ModelDataPoolLocation location
                        || location.IndicesEnd <= location.IndicesStart)
                    {
                        continue;
                    }

                    (int X, int Y, int Z) chunk = GetMeshChunk(
                        location,
                        GlobalConstants.ChunkSize
                    );
                    if (supportedTerrainColumns.Contains((chunk.X, chunk.Z))) continue;

                    double minimumX = chunk.X * GlobalConstants.ChunkSize;
                    double minimumZ = chunk.Z * GlobalConstants.ChunkSize;
                    double maximumOffset = GlobalConstants.ChunkSize - 1;
                    double halfOffset = GlobalConstants.ChunkSize * 0.5;
                    if (HasSurfaceSection(minimumX + halfOffset, minimumZ + halfOffset)
                        || HasSurfaceSection(minimumX + 1, minimumZ + 1)
                        || HasSurfaceSection(minimumX + maximumOffset, minimumZ + 1)
                        || HasSurfaceSection(minimumX + 1, minimumZ + maximumOffset)
                        || HasSurfaceSection(
                            minimumX + maximumOffset,
                            minimumZ + maximumOffset
                        ))
                    {
                        supportedTerrainColumns.Add((chunk.X, chunk.Z));
                        supportedSurfaceSections.Add(chunk);
                    }

                    bool HasSurfaceSection(double worldX, double worldZ) =>
                        surfaceHeightTexture.TryGetSurfaceHeight(
                            worldX,
                            worldZ,
                            out int surfaceHeight
                        )
                        && FloorDiv(surfaceHeight, GlobalConstants.ChunkSize) == chunk.Y;
                }
            }
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
