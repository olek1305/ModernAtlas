using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
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
internal sealed partial class ExactChunkRendererAdapter : IDisposable
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
    private readonly PropertyInfo? platformIsFocusedProperty;
    private readonly object beforeOitRenderer;
    private readonly object afterOitRenderer;
    private readonly VolumetricCloudRendererAdapter? cloudRenderer;
    private readonly AtlasEntityModelRendererAdapter entityModelRenderer;
    private readonly AtlasMechanicalRendererAdapter mechanicalRenderer;
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
    private float atlasSkyDaylight = 1f;
    private float atlasSceneBrightness = 1f;
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
    private bool atlasUniformsActive;
    private readonly float[] lastAtlasProjection = Mat4f.Create();
    private readonly double[] lastAtlasView = Mat4d.Create();
    private bool lastAtlasCameraReady;

    public int LastRenderedEntityCount { get; private set; }
    public int LastRenderedMechanicalDeviceCount { get; private set; }
    public int LastLoadedMechanicalDeviceCount =>
        mechanicalRenderer.LastLoadedDeviceCount;
    public bool ScreenshotAnimationFreezeActive =>
        mechanicalRenderer.ScreenshotFreezeActive;
    public int ScreenshotFrozenMechanicalDeviceCount =>
        mechanicalRenderer.ScreenshotFrozenDeviceCount;
    public int LastScreenshotFrozenMechanicalAngleReadCount =>
        mechanicalRenderer.LastScreenshotFrozenAngleReadCount;
    public int ScreenshotFrozenMechanicalAngleReadCount =>
        mechanicalRenderer.ScreenshotFrozenAngleReadCount;
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

    public bool AtlasStateIsClear =>
        !atlasUniformsActive
        && mechanicalRenderer.NativeCollectionsRestored
        && !atlasVisibilityOverride
        && !atlasTerrainCollectionOverride
        && !atlasTransparentVisibilityOverride
        && !atlasLiquidVisibilityOverride
        && !atlasDisclosureCullingOverride
        && !atlasCompleteBoundaryEnabled
        && !atlasVegetationOnlyVisibilityOverride
        && atlasVisibleTerrainColumns == null
        && atlasSupportedTerrainColumns == null
        && atlasSupportedSurfaceSections == null
        && atlasConsideredTerrainColumns == null
        && atlasLiquidAdapter == null
        && !atlasOreTextureBindingOverride
        && !atlasVegetationTextureBindingOverride
        && !atlasOreTextureBindingRecursion
        && atlasOreTextureBindingAdapter == null;

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
        platformIsFocusedProperty = platform.GetType().GetProperty(
            "IsFocused",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );
        this.beforeOitRenderer = beforeOitRenderer;
        this.afterOitRenderer = afterOitRenderer;
        this.cloudRenderer = cloudRenderer;
        entityModelRenderer = new AtlasEntityModelRendererAdapter(capi);
        mechanicalRenderer = new AtlasMechanicalRendererAdapter(
            capi,
            visibilityHarmony
        );
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

    /// <summary>
    /// Reads the native client window focus without changing it. Focus is an
    /// optional performance hint only; an unavailable compatibility member
    /// must keep the foreground-safe smooth cadence.
    /// </summary>
    public bool IsGameWindowFocused
    {
        get
        {
            try
            {
                return platformIsFocusedProperty?.GetValue(platform)
                    is not bool focused
                    || focused;
            }
            catch
            {
                return true;
            }
        }
    }

    public bool TryBeginScreenshotAnimationFreeze(out string diagnostic)
    {
        if (disposed)
        {
            diagnostic = "the exact atlas renderer is already disposed";
            return false;
        }
        return mechanicalRenderer.TryBeginScreenshotFreeze(out diagnostic);
    }

    public void EndScreenshotAnimationFreeze()
    {
        mechanicalRenderer.EndScreenshotFreeze();
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
        bool animationsEnabled,
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
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
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
                    "[ModernAtlas] Solar lighting: mode={0}, direction=({1:0.000}, {2:0.000}, {3:0.000}), color=({4:0.000}, {5:0.000}, {6:0.000}), skyDaylight={7:0.000}, sceneBrightness={8:0.000}, exposure={9:0.000}.",
                    !performanceLightingEnabled
                        ? "neutral"
                        : liveLightingEnabled
                            ? "live"
                            : $"fixed-{Math.Clamp(fixedSunHour, 0, 24):00}:00",
                    atlasSunDirection.X,
                    atlasSunDirection.Y,
                    atlasSunDirection.Z,
                    atlasSunColor.X,
                    atlasSunColor.Y,
                    atlasSunColor.Z,
                    atlasSkyDaylight,
                    atlasSceneBrightness,
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
            Array.Copy(projection, lastAtlasProjection, 16);
            Array.Copy(view, lastAtlasView, 16);
            lastAtlasCameraReady = true;
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
                    // The native depth test already places leaves against
                    // terrain and trunks. Requiring an existing opaque pixel
                    // behind every leaf instead erases every tree silhouette
                    // against the atlas sky, and which leaves disappear then
                    // changes with camera yaw. Completed surface columns,
                    // world-space disclosure filters and the final boundary
                    // resolve remain authoritative for this pass.
                    false
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
            LastRenderedMechanicalDeviceCount = mechanicalRenderer.Render(
                deltaTime,
                view,
                projection,
                capi.World.Player.Entity.Pos.X,
                capi.World.Player.Entity.Pos.Z,
                viewDistanceBlocks,
                hideUndergroundCaves ? surfaceHeightTexture : null,
                animationsEnabled
            );
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
            atlasUniformsActive = false;
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
            renderState.RestoreCapturedState();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lastAtlasCameraReady = false;
        mechanicalRenderer.ClearAnimationFreezes();

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
            AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(
                capi.Render
            );
            try
            {
                DisableAtlasFilterUniforms();
            }
            finally
            {
                renderState.RestoreCapturedState();
            }

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
            AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
            try
            {
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
            finally
            {
                renderState.RestoreCapturedState();
            }
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

    public int BoundaryValidityTextureId => boundaryResolver.ValidityTextureId;

    public int PrimaryDepthTextureId
    {
        get
        {
            if (disposed) return 0;
            try
            {
                return capi.Render.FrameBuffers[(int)EnumFrameBuffer.Primary]
                    .DepthTextureId;
            }
            catch
            {
                return 0;
            }
        }
    }

    public FrameBufferRef? ResolvedFramebuffer => boundaryResolver.Framebuffer;

    public bool BoundaryDrawBufferStateCheckPassed =>
        boundaryResolver.LastDrawBufferStateCheckPassed;

    public bool BoundaryDrawBufferStateCheckPerformed =>
        boundaryResolver.LastDrawBufferStateCheckPerformed;

    public string BoundaryDrawBufferStateDiagnostic =>
        boundaryResolver.LastDrawBufferStateDiagnostic;

    public bool TryReadBoundaryValidityMask(
        out byte[] mask,
        out AtlasValidityMaskDiagnostics diagnostics
    ) => boundaryResolver.TryReadValidityMask(out mask, out diagnostics);

    public bool BoundaryResolvedLastFrame => boundaryResolver.LastResolveSucceeded;

    public bool TryGetLastAtlasCamera(
        out float[] atlasProjection,
        out double[] atlasView
    )
    {
        if (disposed || !lastAtlasCameraReady)
        {
            atlasProjection = Array.Empty<float>();
            atlasView = Array.Empty<double>();
            return false;
        }

        atlasProjection = (float[])lastAtlasProjection.Clone();
        atlasView = (double[])lastAtlasView.Clone();
        return true;
    }

    /// <summary>
    /// Reads the boundary-resolved atlas target before any window-opacity or
    /// parchment composition. The readback is used only by the opt-in smoke
    /// test; normal atlas frames never stall on CPU pixel inspection.
    /// </summary>
    public bool ValidateResolvedAtlasFramebuffer(out string diagnostic)
    {
        FrameBufferRef? resolved = boundaryResolver.Framebuffer;
        if (resolved == null
            || resolved.Disposed
            || resolved.ColorTextureIds is not { Length: > 0 }
            || resolved.ColorTextureIds[0] <= 0)
        {
            diagnostic = "resolved framebuffer or color attachment is unavailable";
            return false;
        }
        if (!BoundaryDrawBufferStateCheckPerformed
            || !BoundaryDrawBufferStateCheckPassed)
        {
            diagnostic =
                $"boundary draw-buffer configuration was not preserved: {BoundaryDrawBufferStateDiagnostic}";
            return false;
        }

        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        try
        {
            render.CurrentActiveShader?.Stop();
            render.CurrentFrameBuffer = resolved;
            render.GlViewport(0, 0, resolved.Width, resolved.Height);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            using BitmapRef screenshot = render.GrabScreenshot(
                resolved.Width,
                resolved.Height,
                false,
                true,
                true
            );

            int minimumAlpha = 255;
            int belowOpaque = 0;
            int backgroundPixels = 0;
            int nonBackgroundPixels = 0;
            // BitmapRef stores pixels as AARRGGBB (the low byte is blue).
            // The validation readback exposes the linear clear color
            // (0.035, 0.075, 0.11) as #09131C; PNG encoding later applies the
            // display transform and shows the same background as #060D13.
            const int backgroundRed = 9;
            const int backgroundGreen = 19;
            const int backgroundBlue = 28;
            foreach (int pixel in screenshot.Pixels)
            {
                int alpha = (pixel >> 24) & 0xff;
                minimumAlpha = Math.Min(minimumAlpha, alpha);
                if (alpha < 255) belowOpaque++;

                int blue = pixel & 0xff;
                int green = (pixel >> 8) & 0xff;
                int red = (pixel >> 16) & 0xff;
                bool isBackground = Math.Abs(red - backgroundRed) <= 8
                    && Math.Abs(green - backgroundGreen) <= 8
                    && Math.Abs(blue - backgroundBlue) <= 8;
                if (isBackground) backgroundPixels++;
                else nonBackgroundPixels++;
            }

            bool sensibleCoverage = nonBackgroundPixels >= 64
                && (nonBackgroundPixels >= 512
                    || (screenshot.Pixels.Length > 0
                        && (double)nonBackgroundPixels / screenshot.Pixels.Length
                            >= 0.00025d));
            bool valid = screenshot.Pixels.Length > 0
                && minimumAlpha == 255
                && belowOpaque == 0
                && sensibleCoverage;
            string? smokePrefix = Environment.GetEnvironmentVariable(
                "MODERNATLAS_SMOKE_SCREENSHOT"
            );
            if (!string.IsNullOrWhiteSpace(smokePrefix))
            {
                string resolvedPath = smokePrefix + "-resolved-atlas.png";
                try
                {
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(resolvedPath) ?? "."
                    );
                    screenshot.Save(resolvedPath);
                }
                catch (Exception saveException)
                {
                    capi.Logger.Warning(
                        "[ModernAtlas] Could not save resolved atlas validation image: {0}",
                        saveException.Message
                    );
                }
            }
            int firstPixel = screenshot.Pixels.Length > 0
                ? screenshot.Pixels[0]
                : 0;
            diagnostic = string.Format(
                "size={0}x{1}, minAlpha={2}, belowOpaque={3}, backgroundPixels={4}, nonBackgroundPixels={5}, nonBackgroundRatio={6:0.####}, sensibleCoverage={7}, drawBuffersChecked={8}, drawBuffersStable={9}, drawBuffers={10}, firstPacked=0x{11:X8}",
                resolved.Width,
                resolved.Height,
                minimumAlpha,
                belowOpaque,
                backgroundPixels,
                nonBackgroundPixels,
                screenshot.Pixels.Length > 0
                    ? (double)nonBackgroundPixels / screenshot.Pixels.Length
                    : 0d,
                sensibleCoverage,
                BoundaryDrawBufferStateCheckPerformed,
                BoundaryDrawBufferStateCheckPassed,
                BoundaryDrawBufferStateDiagnostic,
                firstPixel
            );
            return valid;
        }
        catch (Exception exception)
        {
            diagnostic = $"resolved framebuffer readback failed: {exception.Message}";
            return false;
        }
        finally
        {
            renderState.RestoreCapturedState();
            try
            {
                GL.ReadBuffer(
                    render.CurrentFrameBuffer == null
                        ? ReadBufferMode.Back
                        : ReadBufferMode.ColorAttachment0
                );
            }
            catch
            {
                // The client may already be leaving the world.
            }
        }
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
