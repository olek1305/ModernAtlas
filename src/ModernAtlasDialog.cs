using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Full-screen, independently rendered 3D atlas. It does not reuse the flat
/// vanilla GuiElementMap; the vanilla map remains available through its own
/// configured hotkey.
/// </summary>
public sealed class ModernAtlasDialog : GuiDialog
{
    private const float StandardMinimumPitchDegrees = 20;
    private const float UnlockedMinimumPitchDegrees = 0;
    private const int DefaultViewDistance = 500;
    private const int MaximumGameViewDistance = 2048;
    private const float MaximumZoomIn = 8;
    private const float MaximumZoomScreenshotSettleSeconds = 2;
    private const int MovingAtlasRefreshMilliseconds = 16;
    private const int IdleAtlasRefreshMilliseconds = 83;
    private const int OreHoverRefreshMilliseconds = 100;
    private const int PresentationChangeDebounceMilliseconds = 125;
    private const int BottomPanelAnimationMilliseconds = 170;
    private const int AutomatedUiScreenshotPhaseCount = 9;
    private const string AllOresFilterValue = "__all__";
    private const string SmokeScreenshotEnvironmentVariable =
        "MODERNATLAS_SMOKE_SCREENSHOT";
    private const string SmokeFixedSunHourEnvironmentVariable =
        "MODERNATLAS_SMOKE_FIXED_SUN_HOUR";
    private const string SmokeScreenshotScaleEnvironmentVariable =
        "MODERNATLAS_SMOKE_SCREENSHOT_SCALE";
    private const string SmokeScreenshotCaptureAreaEnvironmentVariable =
        "MODERNATLAS_SMOKE_CAPTURE_AREA";
    private const string SmokeScreenshotSequenceEnvironmentVariable =
        "MODERNATLAS_SMOKE_SCREENSHOT_SEQUENCE";
    private const string SmokeScreenshotCancelEnvironmentVariable =
        "MODERNATLAS_SMOKE_SCREENSHOT_CANCEL";
    private const string SmokeDisableCloudsEnvironmentVariable =
        "MODERNATLAS_SMOKE_DISABLE_CLOUDS";
    private const string SmokeExpectedViewDistanceEnvironmentVariable =
        "MODERNATLAS_SMOKE_EXPECT_VIEW_DISTANCE";
    private const string SmokeYawEnvironmentVariable =
        "MODERNATLAS_SMOKE_YAW";
    private const string SmokePitchEnvironmentVariable =
        "MODERNATLAS_SMOKE_PITCH";

    private ExactChunkRendererAdapter? exactChunkRenderer;
    private ExactChunkRendererAdapter? pendingNormalWorldShaderRestore;
    private long normalWorldShaderRestoreGeneration;
    private bool worldTeardownStarted;
    private readonly float[] projection = Mat4f.Create();
    private readonly ModernAtlasConfig config;
    private readonly ModernAtlasServerPolicy serverPolicy;
    private readonly ModernAtlasServerPolicy visibleEntityPolicy = new();
    private readonly Action saveConfig;
    private readonly Func<bool> requestClose;
    private readonly Func<IShaderProgram?> stableLiquidShaderProvider;
    private readonly Func<IShaderProgram?> atlasCloudShaderProvider;
    private readonly Func<IShaderProgram?> atlasBoundaryShaderProvider;
    private readonly Func<IShaderProgram?> atlasOpacityShaderProvider;
    private readonly AtlasScrollViewportRenderer scrollViewportRenderer;
    private readonly AtlasScrollRealtimeWeather scrollRealtimeWeather;
    private readonly AtlasCompassRenderer compassRenderer;
    private readonly AtlasTiledScreenshot tileScreenshot;
    private readonly AtlasSurfaceHeightTexture surfaceHeightTexture;
    private readonly AtlasMapLayerTexture mapLayerTexture;
    private readonly AtlasSearchController searchController;
    private readonly AtlasSoundController soundController;

    private GuiComposer? overlay;
    private GuiComposer? searchPanel;
    private GuiComposer? mapLayerPanel;
    private GuiComposer? creativeSettingsShortcut;
    private GuiComposer? settingsModal;
    private GuiComposer? performanceModal;
    private GuiComposer? creativeSettingsModal;
    private GuiComposer? visualLabModal;
    private GuiComposer? screenshotPanel;
    private GuiComposer? screenshotProgressModal;
    private GuiComposer? unitPanel;
    private bool settingsModalOpen;
    private bool performanceModalOpen;
    private bool creativeSettingsModalOpen;
    private bool visualLabModalOpen;
    private bool interfaceHidden;
    private long lastInterfaceRestoreMilliseconds = -10000;
    private LoadedTexture? searchMarkerTexture;
    private LoadedTexture? oreHoverTexture;
    private LoadedTexture? atlasFrameCacheTexture;
    private LoadedTexture? atlasFrameStagingTexture;
    private LoadedTexture? primaryAtlasSourceTexture;
    private int primaryAtlasSourceTextureId;
    private long lastAtlasWorldRenderMilliseconds;
    private MeshRef? opacityQuad;
    private bool leftDragging;
    private bool rightDragging;
    private double leftDragDistance;
    private long? selectedEntityId;
    private double centerX;
    private double centerY;
    private double centerZ;
    private double targetCenterX;
    private double targetCenterZ;
    private float yawDegrees = 42;
    private float pitchDegrees = 72;
    private float zoom = 180;
    private float targetYawDegrees = 42;
    private float targetPitchDegrees = 72;
    private float targetZoom = 180;
    private bool loggedFirstRender;
    private float atlasAnimationSeconds;
    private float frozenWindWaveCounter;
    private float frozenWindWaveCounterHighFrequency;
    private float frozenWaterStillCounter;
    private float frozenWaterFlowCounter;
    private long lastAtlasFrameMilliseconds;
    private float atlasRealDeltaTime;
    private bool loggedEntityModels;
    private bool loggedAtlasRefreshThrottle;
    private bool loggedScrollWeatherDiagnostic;
    private int preparedGameViewDistance = -1;
    private bool cheatModeEnabled;
    private bool preparingSurfaceFilter;
    private bool preparingOreConcealment;
    private bool preparingVegetationMask;
    private bool automatedSmokeTestActive;
    private float automatedSmokeTestElapsedSeconds;
    private Action<bool>? automatedSmokeTestCompletion;
    private bool automatedSmokeTestRequiresUnlockedPitch;
    private bool automatedSmokeTestRenderedAtPitchFloor;
    private bool automatedSmokeTestInterfaceControlsAttempted;
    private bool automatedSmokeTestInterfaceControlsPassed;
    private bool automatedSmokeTestUnitInspectionAttempted;
    private bool automatedSmokeTestUnitInspectionPassed;
    private bool automatedSmokeTestSearchInputAttempted;
    private bool automatedSmokeTestSearchInputPassed;
    private bool automatedScreenshotCaptureRequested;
    private bool automatedScreenshotCapturePassed;
    private bool automatedScreenshotSequenceEnabled;
    private bool automatedScreenshotSequenceFailed;
    private bool automatedScreenshotOutputValidationPassed;
    private long automatedScreenshotDiagnosticNextMilliseconds;
    private bool automatedScreenshotCancelMode;
    private bool automatedScreenshotCancelTriggered;
    private bool automatedScreenshotCancelPassed;
    private int automatedScreenshotSequenceStep;
    private string? automatedFirstScreenshotPath;
    private string? automatedSecondScreenshotPath;
    private HashSet<string>? automatedScreenshotPublicEntriesBefore;
    private HashSet<string>? automatedScreenshotJobEntriesBefore;
    private bool automatedSmokeTestBilingualSearchPassed;
    private bool automatedSmokeTestOreConcealmentPassed;
    private bool automatedSmokeTestCreativeOreRevealFrameRendered;
    private bool automatedSmokeTestForceSurvivalOreConcealment;
    private bool automatedSmokeTestSafeSurfaceFrameRendered;
    private bool automatedSmokeTestSafeSurfaceScreenshotHandled;
    private bool automatedSmokeTestBorderTopDownPending;
    private bool automatedSmokeTestBorderTopDownFrameRendered;
    private bool automatedSmokeTestMaximumZoomPending;
    private bool automatedSmokeTestMaximumZoomFrameRendered;
    private bool automatedSmokeTestPartialZoomPending;
    private bool automatedSmokeTestPartialZoomFrameRendered;
    private float automatedSmokeTestZoomBeforeMaximum;
    private float automatedSmokeTestPitchBeforeMaximum;
    private float automatedSmokeTestMaximumZoomStartedSeconds;
    private float automatedSmokeTestPartialZoomStartedSeconds;
    private float automatedSmokeTestBorderTopDownStartedSeconds;
    private int automatedSmokeTestSearchPhase;
    private bool automatedSmokeTestSearchPassed;
    private double automatedLanternBlockX;
    private double automatedLanternBlockY;
    private double automatedLanternBlockZ;
    private int automatedSmokeTestMapLayerPhase;
    private float automatedSmokeTestOreLayerZoom;
    private bool automatedSmokeTestMapLayerPassed;
    private string? pendingAutomatedMapLayerScreenshotSuffix;
    private bool automatedSmokeTestPerformanceModeSelected;
    private bool automatedSmokeTestPerformanceModeRendered;
    private bool automatedSmokeTestPresentationPassed;
    private bool automatedSmokeTestResolvedAtlasAlphaChecked;
    private bool automatedSmokeTestResolvedAtlasAlphaPassed;
    private bool automatedSmokeTestViewDistancePassed = true;
    private bool automatedSmokeTestViewDistanceUnchanged = true;
    private int automatedSmokeTestInitialViewDistance = -1;
    private bool automatedSmokeCloseStatePassed = true;
    private int automatedSmokeTestPresentationPhase;
    private GuiComposer? automatedSmokeTestSettingsComposer;
    private GuiComposer? automatedSmokeTestPerformanceComposer;
    private GuiComposer? automatedSmokeTestCreativeComposer;
    private GuiComposer? automatedSmokeTestVisualLabComposer;
    private bool automatedSmokeTestPreferencesCaptured;
    private int automatedSmokeScreenshotPhase;
    private bool automatedOriginalMapLayersEnabled;
    private bool automatedOriginalCaveModeEnabled;
    private bool automatedOriginalSearchModeEnabled;
    private bool automatedOriginalCameraAngleLocked;
    private bool automatedOriginalRenderOnScroll;
    private bool automatedOriginalLiveLightingEnabled;
    private bool automatedOriginalCloudsEnabled;
    private int automatedOriginalFixedSunHour;
    private bool automatedOriginalShowPlayerCompass;
    private string automatedOriginalHandheldInstrumentMode = "compass";
    private bool automatedOriginalPerformanceLightingEnabled;
    private bool automatedOriginalHideVegetation;
    private bool automatedOriginalCheatModeEnabled;
    private int automatedOriginalScreenshotScale = 2;
    private int automatedOriginalScreenshotCaptureAreaPercent = 100;
    private bool pendingInterfaceRecompose;
    private bool pendingScreenshotRequest;
    private long screenshotStatusShownUntilMilliseconds = -10000;
    private string? screenshotStatusOverride;
    private bool screenshotCameraSnapshotted;
    private float screenshotBaselineZoom;
    private float screenshotBaselineTargetZoom;
    private double screenshotBaselineCenterX;
    private double screenshotBaselineCenterY;
    private double screenshotBaselineCenterZ;
    private double screenshotBaselineTargetCenterX;
    private double screenshotBaselineTargetCenterZ;
    private float screenshotFrozenWindWaveCounter;
    private float screenshotFrozenWindWaveCounterHighFrequency;
    private float screenshotFrozenWaterStillCounter;
    private float screenshotFrozenWaterFlowCounter;
    private Vec3f? screenshotFrozenCloudOffset;
    private float automatedScreenshotBaselineZoom;
    private float automatedScreenshotBaselineTargetZoom;
    private double automatedScreenshotBaselineCenterX;
    private double automatedScreenshotBaselineCenterY;
    private double automatedScreenshotBaselineCenterZ;
    private bool screenshotPanelCollapsed = true;
    private bool mapLayerPanelCollapsed;
    private bool pendingScreenshotPanelRecompose;
    private bool pendingMapLayerPanelRecompose;
    private AtlasMapLayer activeMapLayer = AtlasMapLayer.TexturedTerrain;
    private string? selectedOreCode;
    private AtlasOreInspection? oreHoverInspection;
    private ElementBounds? searchPanelBounds;
    private ElementBounds? mapLayerPanelBounds;
    private ElementBounds? screenshotPanelBounds;
    private int oreHoverMouseX;
    private int oreHoverMouseY;
    private int oreHoverCellX = int.MinValue;
    private int oreHoverCellZ = int.MinValue;
    private long lastOreHoverUpdateMilliseconds;
    private int synchronizedOreCodeRevision = -1;
    private bool synchronizingMapLayerDropdown;
    private bool synchronizingOreFilterDropdown;
    private bool synchronizingPerformanceControls;
    private int composedFrameWidth;
    private int composedFrameHeight;
    private double composedGuiScale;
    private long lastResizeRecomposeMilliseconds;

    private enum AtlasPanelSection
    {
        None,
        Settings,
        ScreenshotOptions,
        MapOptions,
        Search,
        Instrument,
        Performance,
        Creative,
        VisualLab,
        Unit
    }

    private readonly record struct AtlasPanelGeometry(
        int X,
        int Y,
        int Width,
        int Height
    )
    {
        public int Bottom => Y + Height;

        public bool Contains(int x, int y) =>
            x >= X && x <= X + Width && y >= Y && y <= Bottom;
    }

    // All non-modal atlas controls share this one composer and one bottom
    // panel location. The legacy field names remain as aliases while the
    // existing smoke-test helpers are migrated to the compact layout.
    private GuiComposer? bottomPanel;
    private AtlasPanelSection bottomPanelSection;
    private AtlasPanelSection queuedBottomPanelSection;
    private float bottomPanelProgress;
    private float bottomPanelAnimationStart;
    private float bottomPanelAnimationTarget;
    private long bottomPanelAnimationStartedMilliseconds;
    private bool bottomPanelAnimationActive;
    private AtlasPanelGeometry bottomPanelGeometry;
    private bool hasBottomPanelGeometry;
    private ElementBounds? bottomPanelBounds;
    private string toolbarTooltipText = "";
    private double toolbarTooltipLocalY;
    private double settingsScrollOffset;

    private readonly PresentationChangeCoordinator presentationChangeCoordinator = new();

    internal bool AutomatedSmokeTestRenderedExactWorld { get; private set; }
    internal bool AutomatedSmokeCloseStatePassed => automatedSmokeCloseStatePassed;
    internal bool CheatModeEnabledForAutomation => cheatModeEnabled;

    /// <summary>
    /// Coalesces presentation clicks without changing the committed viewport
    /// until the player has stopped clicking for a short real-time interval.
    /// Keeping this state separate from the saved configuration also keeps the
    /// existing Settings composer alive during the debounce window.
    /// </summary>
    private sealed class PresentationChangeCoordinator
    {
        private bool pending;
        private bool requestedRenderOnScroll;
        private long applyAfterMilliseconds;

        public bool HasPending => pending;
        public bool RequestedRenderOnScroll => requestedRenderOnScroll;

        public void Reset(bool committedRenderOnScroll)
        {
            pending = false;
            requestedRenderOnScroll = committedRenderOnScroll;
            applyAfterMilliseconds = 0;
        }

        public void Request(
            bool requestedRenderOnScroll,
            long nowMilliseconds,
            int debounceMilliseconds
        )
        {
            this.requestedRenderOnScroll = requestedRenderOnScroll;
            applyAfterMilliseconds = nowMilliseconds + debounceMilliseconds;
            pending = true;
        }

        public bool TryTake(
            long nowMilliseconds,
            out bool requestedRenderOnScroll
        )
        {
            requestedRenderOnScroll = this.requestedRenderOnScroll;
            if (!pending || nowMilliseconds < applyAfterMilliseconds)
            {
                return false;
            }

            pending = false;
            return true;
        }

        public bool TryTakePending(out bool requestedRenderOnScroll)
        {
            requestedRenderOnScroll = this.requestedRenderOnScroll;
            if (!pending) return false;

            pending = false;
            return true;
        }

        public bool DisplayedValue(bool committedRenderOnScroll) => pending
            ? requestedRenderOnScroll
            : committedRenderOnScroll;
    }

    private string HandheldInstrumentMode => string.Equals(
        config.HandheldInstrumentMode,
        "time",
        StringComparison.OrdinalIgnoreCase
    )
        ? "time"
        : "compass";

    private int HandheldInstrumentChoiceIndex => !config.ShowPlayerCompass
        ? 0
        : HandheldInstrumentMode == "time"
            ? 2
            : 1;

    private float HandheldInstrumentHour
    {
        get
        {
            IGameCalendar? calendar = capi.World.Calendar;
            return calendar == null
                ? 0f
                : (float)calendar.HourOfDay;
        }
    }

    private bool HandheldInstrumentShowsTime
    {
        get
        {
            IGameCalendar? calendar = capi.World.Calendar;
            return calendar != null
                && AtlasCompassRenderer.IsSundialTimeVisible(
                    (float)calendar.HourOfDay
                );
        }
    }

    private int GameViewDistance
    {
        get
        {
            int viewDistance = capi.Settings.Int["viewDistance"];
            // Use Vintage Story's configured view distance directly as the
            // player-anchored atlas radius. Incomplete outer columns remain
            // absent against the opaque atlas background.
            int configuredDistance = viewDistance > 0
                ? Math.Clamp(viewDistance, GlobalConstants.ChunkSize * 2, MaximumGameViewDistance)
                : DefaultViewDistance;
            return Math.Max(
                GlobalConstants.ChunkSize,
                configuredDistance
            );
        }
    }
    private bool CreativeCheatSettingsAvailable
    {
        get
        {
            if (cheatModeEnabled
                && (capi.IsSinglePlayer || serverPolicy.CheatModeAllowed))
            {
                return true;
            }
            if (!capi.IsSinglePlayer) return false;
            try
            {
                return capi.World.Player?.WorldData.CurrentGameMode == EnumGameMode.Creative;
            }
            catch
            {
                // The dialog is composed before a world/player necessarily
                // exists. Creative access becomes visible after level finalize.
                return false;
            }
        }
    }
    private bool IsCreativeMode
    {
        get
        {
            try
            {
                return capi.World.Player?.WorldData.CurrentGameMode
                    == EnumGameMode.Creative;
            }
            catch
            {
                return false;
            }
        }
    }
    private bool SurfaceSafetyEnabled => !CreativeCheatSettingsAvailable
        || !config.CaveModeEnabled;
    private bool SurvivalOreConcealmentEnabled =>
        !CreativeCheatSettingsAvailable
        || automatedSmokeTestForceSurvivalOreConcealment;
    private bool HasUnlockedCameraPitch => CreativeCheatSettingsAvailable;
    private bool UnitInspectionEnabled => CreativeCheatSettingsAvailable;
    private bool SearchModeActive => CreativeCheatSettingsAvailable
        && config.SearchModeEnabled;
    private bool MapLayerControlsVisible => config.MapLayersEnabled;
    private bool SettingsHierarchyOpen => settingsModalOpen
        || performanceModalOpen
        || creativeSettingsModalOpen
        || visualLabModalOpen;
    private GuiComposer? ActiveBottomPanelComposer => bottomPanel;
    private bool BottomPanelOpen => bottomPanelSection != AtlasPanelSection.None;
    private bool BottomPanelClosing => bottomPanelAnimationActive
        && bottomPanelAnimationTarget <= bottomPanelAnimationStart;
    private bool BottomPanelInputVisible => hasBottomPanelGeometry
        && BottomPanelOpen
        && !BottomPanelClosing
        && bottomPanelProgress > 0.65f;
    private bool SearchPanelOpen => bottomPanelSection == AtlasPanelSection.Search;
    private bool MapPanelOpen => bottomPanelSection == AtlasPanelSection.MapOptions;
    private bool ScreenshotOptionsOpen =>
        bottomPanelSection == AtlasPanelSection.ScreenshotOptions;
    private bool InstrumentPanelOpen => bottomPanelSection == AtlasPanelSection.Instrument;
    private float MinimumPitchDegrees => HasUnlockedCameraPitch
        ? UnlockedMinimumPitchDegrees
        : StandardMinimumPitchDegrees;
    private GuiComposer? ActiveKeyboardComposer => interfaceHidden
        ? null
        : screenshotProgressModal != null
            ? screenshotProgressModal
            : bottomPanel?.GetTextInput("search-input")?.HasFocus == true
                ? bottomPanel
                : bottomPanelSection != AtlasPanelSection.None
                    && !BottomPanelClosing
                    ? bottomPanel
                    : overlay;
    internal bool SearchInputHasFocus => IsOpened()
        && !interfaceHidden
        && SearchModeActive
        && !settingsModalOpen
        && !performanceModalOpen
        && !creativeSettingsModalOpen
        && !visualLabModalOpen
        && screenshotProgressModal == null
        && bottomPanelSection == AtlasPanelSection.Search
        && bottomPanel?.GetTextInput("search-input")?.HasFocus == true;

    /// <summary>
    /// Releases UI state that belongs to the preceding physical-scroll dialog
    /// before this dialog starts receiving keyboard and mouse events.
    /// </summary>
    internal void PrepareForTransitionHandoff()
    {
        ResetPointerDrag();
        overlay?.UnfocusOwnElements();
        searchPanel?.UnfocusOwnElements();
        mapLayerPanel?.UnfocusOwnElements();
        settingsModal?.UnfocusOwnElements();
        performanceModal?.UnfocusOwnElements();
        creativeSettingsModal?.UnfocusOwnElements();
        visualLabModal?.UnfocusOwnElements();
        screenshotPanel?.UnfocusOwnElements();
        screenshotProgressModal?.UnfocusOwnElements();
    }
    private float EffectiveMapLayerOpacity
    {
        get
        {
            float configured = Math.Clamp(config.MapLayerOpacityPercent, 0, 100) / 100f;
            // A perceptual response makes middle slider values visibly useful
            // while preserving exact zero and full-strength endpoints. Boost
            // the final tint so terrain materials cannot overpower layer colors.
            float perceptual = 1f - MathF.Pow(1f - configured, 1.35f);
            return Math.Min(1f, perceptual * 1.3f);
        }
    }
    private AtlasViewportBounds AtlasViewport
    {
        get
        {
            int frameWidth = Math.Max(1, capi.Render.FrameWidth);
            int frameHeight = Math.Max(1, capi.Render.FrameHeight);
            if (!config.RenderOnScroll)
            {
                return new AtlasViewportBounds(0, 0, frameWidth, frameHeight);
            }

            int x = Math.Max(48, (int)Math.Round(frameWidth * 0.075));
            int y = Math.Max(44, (int)Math.Round(frameHeight * 0.095));
            return new AtlasViewportBounds(
                x,
                y,
                Math.Max(320, frameWidth - x * 2),
                Math.Max(240, frameHeight - y * 2)
            );
        }
    }

    public override string ToggleKeyCombinationCode => "modernatlas-open";
    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override double DrawOrder => 0.98;
    public override double InputOrder => 0.05;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;

    internal ModernAtlasDialog(
        ICoreClientAPI capi,
        ModernAtlasConfig config,
        ModernAtlasServerPolicy serverPolicy,
        Action saveConfig,
        Func<bool> requestClose,
        Func<IShaderProgram?> stableLiquidShaderProvider,
        Func<IShaderProgram?> atlasCloudShaderProvider,
        Func<IShaderProgram?> atlasBoundaryShaderProvider,
        Func<IShaderProgram?> atlasOpacityShaderProvider,
        Func<IShaderProgram?> atlasScrollShaderProvider,
        AtlasSoundController soundController
    ) : base(capi)
    {
        this.config = config;
        this.serverPolicy = serverPolicy;
        this.saveConfig = saveConfig;
        this.requestClose = requestClose;
        this.stableLiquidShaderProvider = stableLiquidShaderProvider;
        this.atlasCloudShaderProvider = atlasCloudShaderProvider;
        this.atlasBoundaryShaderProvider = atlasBoundaryShaderProvider;
        this.atlasOpacityShaderProvider = atlasOpacityShaderProvider;
        this.soundController = soundController;
        scrollViewportRenderer = new AtlasScrollViewportRenderer(
            capi,
            atlasScrollShaderProvider
        );
        scrollRealtimeWeather = new AtlasScrollRealtimeWeather(capi);
        compassRenderer = new AtlasCompassRenderer(capi, atlasScrollShaderProvider);
        tileScreenshot = new AtlasTiledScreenshot(capi);
        surfaceHeightTexture = new AtlasSurfaceHeightTexture(capi);
        mapLayerTexture = new AtlasMapLayerTexture(capi);
        searchController = new AtlasSearchController(capi);
        RefreshVisibleEntityPolicy();
        ComposeOverlay();
    }

    public override void OnGuiOpened()
    {
        FlushPendingNormalWorldShaderRestore();
        worldTeardownStarted = false;
        base.OnGuiOpened();
        presentationChangeCoordinator.Reset(config.RenderOnScroll);
        // The dialog is constructed before a world/player necessarily exists.
        // Recompose now that Survival, Creative and accepted Cheat Mode access
        // can be resolved, so unavailable controls leave no empty slot.
        ResetBottomPanelState();
        RecomposeInterface();
        // Opening with G is a map action, not an implicit request to type.
        // Search receives focus only after the player clicks its text box.
        overlay?.UnfocusOwnElements();
        searchPanel?.UnfocusOwnElements();
        settingsModalOpen = false;
        performanceModalOpen = false;
        creativeSettingsModalOpen = false;
        visualLabModalOpen = false;
        interfaceHidden = false;
        compassRenderer.ResetForAtlasOpen(config.ShowPlayerCompass);
        lastInterfaceRestoreMilliseconds = -10000;
        selectedEntityId = null;
        ClearOreHover();
        ClearSearch();
        ResetPointerDrag();
        tileScreenshot.ClearStatus();
        pendingScreenshotRequest = false;
        screenshotStatusOverride = null;
        screenshotCameraSnapshotted = false;
        screenshotFrozenCloudOffset = null;
        screenshotProgressModal?.Dispose();
        screenshotProgressModal = null;
        atlasAnimationSeconds = 0;
        loggedEntityModels = false;
        loggedScrollWeatherDiagnostic = false;
        CaptureAnimationFrame();
        lastAtlasFrameMilliseconds = capi.ElapsedMilliseconds;
        lastAtlasWorldRenderMilliseconds = 0;
        ReleaseAtlasFrameCache();
        EnsureExactChunkRenderer();
        centerX = capi.World.Player.Entity.Pos.X;
        centerZ = capi.World.Player.Entity.Pos.Z;
        centerY = capi.World.Player.Entity.Pos.Y;
        targetCenterX = centerX;
        targetCenterZ = centerZ;
        targetYawDegrees = yawDegrees;
        targetPitchDegrees = pitchDegrees;
        ClampPitchToAccessLevel(true);
        if (automatedSmokeTestActive && HasUnlockedCameraPitch)
        {
            automatedSmokeTestRequiresUnlockedPitch = true;
            targetPitchDegrees = StandardMinimumPitchDegrees;
            pitchDegrees = StandardMinimumPitchDegrees;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test is rendering the Survival-safe 20-degree camera floor before exercising the unlocked 0-degree pitch."
            );
        }
        string? forcedSmokeYaw = Environment.GetEnvironmentVariable(
            SmokeYawEnvironmentVariable
        );
        if (automatedSmokeTestActive
            && float.TryParse(
                forcedSmokeYaw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float parsedSmokeYaw
            ))
        {
            yawDegrees = NormalizeDegrees(parsedSmokeYaw);
            targetYawDegrees = yawDegrees;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test selected atlas yaw {0:0.0} degrees without changing world camera state.",
                yawDegrees
            );
        }
        string? forcedSmokePitch = Environment.GetEnvironmentVariable(
            SmokePitchEnvironmentVariable
        );
        if (automatedSmokeTestActive
            && float.TryParse(
                forcedSmokePitch,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float parsedSmokePitch
            ))
        {
            pitchDegrees = Math.Clamp(
                parsedSmokePitch,
                MinimumPitchDegrees,
                86f
            );
            targetPitchDegrees = pitchDegrees;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test selected atlas pitch {0:0.0} degrees without changing world camera state.",
                pitchDegrees
            );
        }
        FocusOnExteriorSurface();
        FitLoadedTerrain();
        zoom = targetZoom;
        PrepareSurfaceSafetyFilter();
        PrepareMapLayer();
        SyncSettingsControls();
        SyncCreativeSettingsControls();
        capi.Logger.Notification(
            "[ModernAtlas] Opened independent 3D atlas GUI at exterior surface height {0:0.0}; singleplayer paused: {1}.",
            centerY,
            capi.IsSinglePlayer && capi.IsGamePaused
        );
    }

    public override void OnRenderGUI(float deltaTime)
    {
        IRenderAPI renderStateApi = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(
            renderStateApi
        );
        try
        {
        AdvancePresentationChange();
        AdvanceBottomPanelAnimation();
        bool viewportChanged = capi.Render.FrameWidth != composedFrameWidth
            || capi.Render.FrameHeight != composedFrameHeight
            || Math.Abs(RuntimeEnv.GUIScale - composedGuiScale) > 0.001;
        if (viewportChanged
            && capi.ElapsedMilliseconds - lastResizeRecomposeMilliseconds >= 80)
        {
            lastResizeRecomposeMilliseconds = capi.ElapsedMilliseconds;
            pendingInterfaceRecompose = false;
            ResetPointerDrag();
            RecomposeInterface();
        }
        if (EnsureExactChunkRenderer())
        {
            // Vintage Story may rebuild its chunk renderer when view distance
            // changes. Rebuild only the transient atlas filters against the
            // replacement renderer; the game remains the sole mesh owner.
            PrepareSurfaceSafetyFilter();
            PrepareMapLayer();
        }
        SynchronizeGameViewDistance();
        if (automatedSmokeTestActive
            && automatedSmokeTestInitialViewDistance > 0
            && GameViewDistance != automatedSmokeTestInitialViewDistance)
        {
            automatedSmokeTestViewDistanceUnchanged = false;
        }
        if (pendingInterfaceRecompose)
        {
            pendingInterfaceRecompose = false;
            RecomposeInterface();
        }
        if (pendingMapLayerPanelRecompose)
        {
            pendingMapLayerPanelRecompose = false;
            RecomposeMapLayerPanel();
        }
        if (pendingScreenshotPanelRecompose)
        {
            pendingScreenshotPanelRecompose = false;
            RecomposeScreenshotPanel();
        }
        AdvancePausedAnimation();
        AdvanceCamera(atlasRealDeltaTime);
        mapLayerTexture.Advance();
        SynchronizeOreFilterOptions();
        bool pointerOverMapPanel = searchPanelBounds?.PointInside(
                capi.Input.MouseX,
                capi.Input.MouseY
            ) == true
            || mapLayerPanelBounds?.PointInside(
                capi.Input.MouseX,
                capi.Input.MouseY
            ) == true
            || screenshotPanelBounds?.PointInside(
                capi.Input.MouseX,
                capi.Input.MouseY
            ) == true;
        UpdateOreHover(capi.Input.MouseX, capi.Input.MouseY, pointerOverMapPanel);
        if (!loggedFirstRender)
        {
            loggedFirstRender = true;
            capi.Logger.Notification("[ModernAtlas] First 3D atlas GUI frame rendered.");
        }
        bool freshAtlasFrame = ShouldRenderFreshAtlasFrame();
        bool rendered = freshAtlasFrame
            ? RenderLiveWorld(deltaTime)
            : atlasFrameCacheTexture?.TextureId > 0;
        if (freshAtlasFrame && rendered)
        {
            lastAtlasWorldRenderMilliseconds = capi.ElapsedMilliseconds;
            if (automatedSmokeTestActive
                && !automatedSmokeTestResolvedAtlasAlphaChecked
                && exactChunkRenderer?.BoundaryResolvedLastFrame == true)
            {
                automatedSmokeTestResolvedAtlasAlphaChecked = true;
                automatedSmokeTestResolvedAtlasAlphaPassed =
                    exactChunkRenderer.ValidateResolvedAtlasFramebuffer(
                        out string resolvedAlphaDiagnostic
                    );
                if (automatedSmokeTestResolvedAtlasAlphaPassed)
                {
                    capi.Logger.Notification(
                        "[ModernAtlas] Automated resolved-atlas alpha validation passed: {0}.",
                        resolvedAlphaDiagnostic
                    );
                }
                else
                {
                    capi.Logger.Error(
                        "[ModernAtlas] Automated resolved-atlas alpha validation failed: {0}.",
                        resolvedAlphaDiagnostic
                    );
                }
            }
            if (pendingScreenshotRequest)
            {
                AdvanceTileScreenshot();
            }
        }
        if (SearchModeActive)
        {
            AdvanceSearch();
        }
        if (freshAtlasFrame && rendered && automatedSmokeTestActive)
        {
            bool safeSurfaceFrameWasAlreadyRendered =
                automatedSmokeTestSafeSurfaceFrameRendered;
            AutomatedSmokeTestRenderedExactWorld = true;
            if (automatedSmokeTestRequiresUnlockedPitch
                && pitchDegrees <= UnlockedMinimumPitchDegrees + 0.01f)
            {
                automatedSmokeTestRenderedAtPitchFloor = true;
            }
            automatedSmokeTestSafeSurfaceFrameRendered |= SurfaceSafetyEnabled;
            if (SurvivalOreConcealmentEnabled
                && !automatedSmokeTestOreConcealmentPassed
                && exactChunkRenderer?.ValidateSurvivalOreConcealment(
                    out string oreDiagnostic
                ) == true)
            {
                automatedSmokeTestOreConcealmentPassed = true;
                automatedSmokeTestForceSurvivalOreConcealment = false;
                capi.Logger.Notification(
                    "[ModernAtlas] Automated Survival ore-concealment check passed: {0}.",
                    oreDiagnostic
                );
            }
            if (automatedSmokeTestOreConcealmentPassed
                && CreativeCheatSettingsAvailable
                && !SurvivalOreConcealmentEnabled
                && !automatedSmokeTestCreativeOreRevealFrameRendered)
            {
                automatedSmokeTestCreativeOreRevealFrameRendered = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Automated Creative/Cheat ore-visibility frame rendered with Survival concealment disabled."
                );
            }
            if (automatedSmokeTestPerformanceModeSelected
                && !automatedSmokeTestPerformanceModeRendered
                && exactChunkRenderer?.LastRenderedVegetationHidden == true
                && !exactChunkRenderer.LastRenderedPerformanceLightingEnabled
                && exactChunkRenderer.ValidateVegetationMask(
                    out string vegetationDiagnostic
                ))
            {
                automatedSmokeTestPerformanceModeRendered = true;
                OnPerformanceLightingToggled(
                    automatedOriginalPerformanceLightingEnabled
                );
                OnHideVegetationToggled(automatedOriginalHideVegetation);
                capi.Logger.Notification(
                    "[ModernAtlas] Automated performance check rendered exact terrain with flat lighting and the registered vegetation filter, then restored the original settings: {0}.",
                    vegetationDiagnostic
                );
            }
            if (safeSurfaceFrameWasAlreadyRendered
                && automatedSmokeTestSafeSurfaceScreenshotHandled
                && automatedSmokeTestBorderTopDownFrameRendered
                && !automatedSmokeTestBorderTopDownPending
                && automatedSmokeTestPartialZoomFrameRendered
                && !automatedSmokeTestPartialZoomPending
                && automatedSmokeTestMaximumZoomFrameRendered
                && !automatedSmokeTestMaximumZoomPending)
            {
                ExerciseAutomatedInterfaceControls();
                ExerciseAutomatedSearchInput();
                ExerciseAutomatedUnitInspection();
                ExerciseAutomatedSearch();
                ExerciseAutomatedMapLayers();
            }
        }
        if (rendered && !loggedEntityModels && visibleEntityPolicy.AnyEntityModels)
        {
            loggedEntityModels = true;
            capi.Logger.Notification(
                "[ModernAtlas] Rendered {0} live 3D living entity models from client-loaded entities; held-item rendering suppressed for {1} models.",
                exactChunkRenderer?.LastRenderedEntityCount ?? 0,
                exactChunkRenderer?.LastSuppressedHeldItemCount ?? 0
            );
        }
        if (config.RenderOnScroll)
        {
            if (freshAtlasFrame && rendered)
            {
                CaptureAtlasFrameCache();
            }
            capi.Render.CurrentFrameBuffer = null;
            scrollViewportRenderer.Render(
                atlasFrameCacheTexture?.TextureId ?? 0,
                0,
                AtlasViewport
            );
            // Eligibility is checked before touching the weather system so
            // Creative, Fullscreen and the disabled preference have zero
            // scroll-weather sampling or rendering cost.
            bool creativeMode = IsCreativeMode;
            if (config.ScrollRealtimeWeatherEnabled && !creativeMode)
            {
                bool weatherActive = scrollRealtimeWeather.TryGetExposedWeather(
                    out AtlasScrollWeatherState scrollWeather
                );
                if (!loggedScrollWeatherDiagnostic)
                {
                    loggedScrollWeatherDiagnostic = true;
                    capi.Logger.Notification(
                        "[ModernAtlas] Scroll realtime weather sample: {0}.",
                        scrollRealtimeWeather.LastDiagnostic
                    );
                }
                if (weatherActive)
                {
                    scrollViewportRenderer.RenderWeatherOverlay(
                        AtlasViewport,
                        scrollWeather,
                        (capi.ElapsedMilliseconds % 3_600_000L) / 1000f
                    );
                }
            }
            else if (!loggedScrollWeatherDiagnostic)
            {
                loggedScrollWeatherDiagnostic = true;
                capi.Logger.Notification(
                    creativeMode
                        ? "[ModernAtlas] Scroll realtime weather skipped in Creative mode."
                        : "[ModernAtlas] Scroll realtime weather is disabled in Gameplay settings."
                );
            }
        }
        else
        {
            if (freshAtlasFrame && rendered)
            {
                CaptureAtlasFrameCache();
            }
            // Even before the first complete cache frame exists, cover the
            // window with the atlas renderer's opaque neutral placeholder.
            // Leaving the previous world framebuffer visible here makes a
            // failed or throttled atlas frame look like a one-frame world leak.
            RenderCachedAtlasFullscreen();
        }
        capi.Render.GetEngineShader(EnumShaderProgram.Gui).Use();
        capi.Render.GLDepthMask(false);
        capi.Render.GLDisableDepthTest();
        capi.Render.GlDisableCullFace();
        capi.Render.GlToggleBlend(true, EnumBlendMode.Standard);
        if (!interfaceHidden && SearchModeActive)
        {
            RenderSearchMarkers();
        }
        RenderOreHoverCard();
        overlay?.GetDynamicText("status").SetNewText(
            $"View distance: {GameViewDistance}"
        );
        searchPanel?.GetDynamicText("search-status").SetNewText(searchController.StatusText);
        mapLayerPanel?.GetDynamicText("layer-status")?.SetNewText(mapLayerTexture.StatusText);
        mapLayerPanel?.GetDynamicText("layer-legend")?.SetNewText(
            activeMapLayer.DetailedLegend()
        );
        // The handheld instrument belongs to the atlas scene, but the atlas
        // controls must remain on top of it. Render the instrument before the
        // GUI composers so an open bottom panel can never be obscured by the
        // local hand or wooden shell.
        if (!pendingScreenshotRequest)
        {
            compassRenderer.Render(
                config.ShowPlayerCompass,
                HandheldInstrumentMode == "time",
                HandheldInstrumentHour,
                HandheldInstrumentShowsTime,
                yawDegrees,
                AtlasViewport,
                atlasRealDeltaTime
            );
        }
        capi.Render.GetEngineShader(EnumShaderProgram.Gui).Use();
        capi.Render.GLDepthMask(false);
        capi.Render.GLDisableDepthTest();
        capi.Render.GlDisableCullFace();
        capi.Render.GlToggleBlend(true, EnumBlendMode.Standard);
        if (!interfaceHidden)
        {
            UpdateToolbarTooltip(capi.Input.MouseX, capi.Input.MouseY);
            overlay?.Render(deltaTime);
            RenderActiveBottomPanel(deltaTime);
            screenshotProgressModal?.Render(deltaTime);
        }
        if (config.RenderOnScroll)
        {
            scrollViewportRenderer.RenderRollersOverlay(AtlasViewport);
        }
        // Restore the GUI shader after the physical scroll rollers too. The
        // next HUD renderer assumes the engine GUI program is active.
        ForceOpaqueWindowAlpha();
        capi.Render.GetEngineShader(EnumShaderProgram.Gui).Use();
        UpdateScreenshotStatusText();
        CaptureAutomatedSmokeScreenshot();
        AdvanceAutomatedPresentationSwitchTest();
        AdvanceAutomatedSmokeTest();
        }
        finally
        {
            try
            {
                renderState.RestoreGuiHandoff();
            }
            finally
            {
                // The following GUI renderer owns this handoff; it is set only
                // after the atlas scope has released its target and shader.
                try
                {
                    renderStateApi.GetEngineShader(EnumShaderProgram.Gui).Use();
                }
                catch
                {
                    // The client may already be tearing down its GUI context.
                }
            }
        }
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (!interfaceHidden && screenshotProgressModal != null)
        {
            screenshotProgressModal.OnMouseDown(args);
            args.Handled = true;
            return;
        }
        if (!interfaceHidden)
        {
            UnfocusSearchOutsideInput(args);
            overlay?.OnMouseDown(args);
            if (args.Handled) return;
            if (PanelCoversPoint(args.X, args.Y))
            {
                bottomPanel?.OnMouseDown(args);
                // The shared panel owns its translucent background as well as
                // its controls; an empty panel click must not start a map drag.
                args.Handled = true;
                if (args.Handled) return;
            }
        }

        if (!AtlasViewport.Contains(args.X, args.Y))
        {
            args.Handled = true;
            return;
        }

        ClearOreHover();
        soundController.PlayPageTouch();

        if (args.Button == EnumMouseButton.Left)
        {
            leftDragging = true;
            leftDragDistance = 0;
        }
        if (args.Button == EnumMouseButton.Right)
        {
            rightDragging = true;
        }
        if (args.Button == EnumMouseButton.Middle) ResetView();
        args.Handled = true;
    }

    public override void OnMouseUp(MouseEvent args)
    {
        if (!interfaceHidden && screenshotProgressModal != null)
        {
            screenshotProgressModal.OnMouseUp(args);
            args.Handled = true;
            return;
        }
        // Once a map drag begins, keep ownership of the gesture even if the
        // pointer crosses the settings panel. Letting the overlay consume the
        // release leaves the drag latched and the next move jumps the camera.
        if (args.Button == EnumMouseButton.Left && leftDragging)
        {
            leftDragging = false;
            if (leftDragDistance <= 5)
            {
                TrySelectRenderedEntity(args.X, args.Y);
            }
            args.Handled = true;
            return;
        }
        if (args.Button == EnumMouseButton.Right && rightDragging)
        {
            rightDragging = false;
            args.Handled = true;
            return;
        }

        if (!interfaceHidden)
        {
            overlay?.OnMouseUp(args);
            if (args.Handled) return;
            if (PanelCoversPoint(args.X, args.Y))
            {
                bottomPanel?.OnMouseUp(args);
                args.Handled = true;
                if (args.Handled) return;
            }
        }
        args.Handled = true;
    }

    public override void OnMouseMove(MouseEvent args)
    {
        if (!interfaceHidden && screenshotProgressModal != null)
        {
            ClearOreHover();
            screenshotProgressModal.OnMouseMove(args);
            args.Handled = true;
            return;
        }
        if (leftDragging || rightDragging)
        {
            ClearOreHover();
            // Preserve the engine's relative delta so long pulls are not
            // truncated at a window edge. Gesture ownership below prevents a
            // settings control from leaving this relative drag latched.
            double deltaX = args.DeltaX;
            double deltaY = args.DeltaY;

            if (rightDragging)
            {
                ApplyRotationDrag(deltaX, deltaY);
            }

            if (leftDragging)
            {
                leftDragDistance += Math.Abs(deltaX) + Math.Abs(deltaY);
                double worldPerPixel = targetZoom * 2.0 / Math.Max(1, AtlasViewport.Height);
                double yaw = targetYawDegrees * GameMath.DEG2RAD;
                double rightX = Math.Cos(yaw);
                double rightZ = -Math.Sin(yaw);
                double forwardX = Math.Sin(yaw);
                double forwardZ = Math.Cos(yaw);
                // Drag the map in the same screen-space direction as the mouse.
                // The vertical sign must not flip when the camera yaw changes.
                targetCenterX -= (deltaX * rightX + deltaY * forwardX) * worldPerPixel;
                targetCenterZ -= (deltaX * rightZ + deltaY * forwardZ) * worldPerPixel;
            }

            args.Handled = true;
            return;
        }

        if (!interfaceHidden)
        {
            overlay?.OnMouseMove(args);
            if (PanelCoversPoint(args.X, args.Y))
            {
                bottomPanel?.OnMouseMove(args);
                args.Handled = true;
            }
        }
        UpdateToolbarTooltip(args.X, args.Y);
        bool overPanel = PanelCoversPoint(args.X, args.Y);
        UpdateOreHover(args.X, args.Y, args.Handled || overPanel);
        // The atlas covers the entire screen. Do not leak hover interaction to
        // hotbar slots, creative inventory elements or dialogs underneath it.
        args.Handled = true;
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (!interfaceHidden && screenshotProgressModal != null)
        {
            screenshotProgressModal.OnMouseWheel(args);
            args.SetHandled();
            return;
        }
        if (!interfaceHidden)
        {
            overlay?.OnMouseWheel(args);
            if (args.IsHandled) return;
            if (PanelCoversPoint(capi.Input.MouseX, capi.Input.MouseY))
            {
                bottomPanel?.OnMouseWheel(args);
                if (!args.IsHandled
                    && bottomPanelSection == AtlasPanelSection.Settings)
                {
                    float wheel = args.deltaPrecise != 0
                        ? args.deltaPrecise
                        : args.delta;
                    double maximum = SettingsScrollMaximum(
                        bottomPanelBounds?.fixedWidth ?? 0,
                        bottomPanelBounds?.fixedHeight ?? 0
                    );
                    settingsScrollOffset = Math.Clamp(
                        settingsScrollOffset - wheel * 18,
                        0,
                        maximum
                    );
                    pendingInterfaceRecompose = true;
                }
                args.SetHandled();
                return;
            }
        }

        if (!AtlasViewport.Contains(capi.Input.MouseX, capi.Input.MouseY))
        {
            args.SetHandled();
            return;
        }

        ApplyZoomWheel(args.deltaPrecise != 0 ? args.deltaPrecise : args.delta, true);
        args.SetHandled();
    }

    private void ApplyZoomWheel(float wheel, bool playSound)
    {
        if (playSound) soundController.PlayPageTouch();
        targetZoom = Math.Clamp(
            targetZoom * MathF.Pow(0.84f, wheel),
            MaximumZoomIn,
            30000
        );
    }

    public override void OnKeyDown(KeyEvent args)
    {
        if (args.KeyCode == (int)GlKeys.Escape)
        {
            HandleEscape();
            args.Handled = true;
            return;
        }

        ActiveKeyboardComposer?.OnKeyDown(args, false);
        if (args.Handled) return;

        // Printable characters arrive through OnKeyPress, but their preceding
        // key-down must still remain owned by the focused text box. Otherwise
        // G closes the atlas and WASD pans it while the player is typing.
        if (SearchInputHasFocus)
        {
            args.Handled = true;
            return;
        }

        // Modal controls also own the keyboard even when a particular key has
        // no widget action. Do not move the atlas behind an open modal.
        if (visualLabModalOpen
            || performanceModalOpen
            || creativeSettingsModalOpen
            || settingsModalOpen
            || screenshotProgressModal != null)
        {
            args.Handled = true;
            return;
        }

        if (args.KeyCode == (int)GlKeys.G)
        {
            CloseAtlas();
            args.Handled = true;
            return;
        }

        float pan = Math.Max(1, targetZoom * 0.08f);
        double yaw = targetYawDegrees * GameMath.DEG2RAD;
        double forwardX = Math.Sin(yaw);
        double forwardZ = Math.Cos(yaw);
        double rightX = Math.Cos(yaw);
        double rightZ = -Math.Sin(yaw);

        if (args.KeyCode == (int)GlKeys.W) Pan(forwardX, forwardZ, pan, args);
        else if (args.KeyCode == (int)GlKeys.S) Pan(-forwardX, -forwardZ, pan, args);
        else if (args.KeyCode == (int)GlKeys.A) Pan(-rightX, -rightZ, pan, args);
        else if (args.KeyCode == (int)GlKeys.D) Pan(rightX, rightZ, pan, args);
        else if (args.KeyCode == (int)GlKeys.Q) Rotate(-6, args);
        else if (args.KeyCode == (int)GlKeys.E) Rotate(6, args);
        else if (args.KeyCode == (int)GlKeys.R)
        {
            if (!config.CameraAngleLocked || !CreativeCheatSettingsAvailable)
            {
                targetPitchDegrees = Math.Clamp(targetPitchDegrees + 4, MinimumPitchDegrees, 86);
            }
            args.Handled = true;
        }
        else if (args.KeyCode == (int)GlKeys.F)
        {
            if (!config.CameraAngleLocked || !CreativeCheatSettingsAvailable)
            {
                targetPitchDegrees = Math.Clamp(targetPitchDegrees - 4, MinimumPitchDegrees, 86);
            }
            args.Handled = true;
        }
        else if (args.KeyCode == (int)GlKeys.Space)
        {
            CenterOnPlayer();
            args.Handled = true;
        }
        else if (args.KeyCode == (int)GlKeys.Home)
        {
            ResetView();
            SyncSettingsControls();
            args.Handled = true;
        }
        else
        {
            // CaptureAllInputs promises that the full-screen atlas will not
            // leak unused keys into gameplay or dialogs underneath it.
            args.Handled = true;
        }
    }

    public override void OnKeyPress(KeyEvent args)
    {
        ActiveKeyboardComposer?.OnKeyPress(args);
        // GuiComposer inserts text during OnKeyPress. Whether or not a widget
        // used this character, the full-screen atlas owns the event.
        args.Handled = true;
    }

    public override void OnKeyUp(KeyEvent args)
    {
        ActiveKeyboardComposer?.OnKeyUp(args);
        args.Handled = true;
    }

    public override bool CaptureAllInputs() => true;
    public override bool CaptureRawMouse() => true;
    public override bool ShouldReceiveKeyboardEvents() => IsOpened();
    public override bool ShouldReceiveMouseEvents() => IsOpened();
    public override bool OnEscapePressed() => HandleEscape();

    public override void OnGuiClosed()
    {
        CommitPendingPresentationPreference();
        CancelScreenshotCapture();
        presentationChangeCoordinator.Reset(config.RenderOnScroll);
        overlay?.UnfocusOwnElements();
        ResetBottomPanelState();
        interfaceHidden = false;
        selectedEntityId = null;
        searchController.Clear();
        mapLayerTexture.Reset();
        preparingSurfaceFilter = false;
        // Keep the small transient surface-safety samples for this world.
        // Changing graphics view distance happens while G is closed; retaining
        // the overlap prevents already known exact mesh columns from becoming
        // unknown when the resized filter is rebuilt on the next open. World
        // leave still releases all of this non-persistent data.
        ResetPointerDrag();
        compassRenderer.Dispose();
        ReleaseAtlasFrameCache();
        if (!worldTeardownStarted)
        {
            ScheduleNormalWorldShaderRestore();
        }
        else
        {
            // Render finally blocks already clear atlas switches before world
            // teardown. Never activate an engine shader after DefaultShader-
            // Uniforms has started being replaced.
            pendingNormalWorldShaderRestore = null;
            normalWorldShaderRestoreGeneration++;
        }
        if (!worldTeardownStarted)
        {
            bool atlasFlagsClear = exactChunkRenderer?.AtlasStateIsClear ?? true;
            bool framebufferClear = capi.Render.CurrentFrameBuffer == null;
            bool statePassed = atlasFlagsClear && framebufferClear;
            automatedSmokeCloseStatePassed &= statePassed;
            if (statePassed)
            {
                capi.Logger.Notification(
                    "[ModernAtlas] Atlas close state check passed: atlas flags/uniforms clear={0}, current framebuffer null={1}.",
                    atlasFlagsClear,
                    framebufferClear
                );
            }
            else
            {
                capi.Logger.Error(
                    "[ModernAtlas] Atlas close state check failed: atlas flags/uniforms clear={0}, current framebuffer null={1}.",
                    atlasFlagsClear,
                    framebufferClear
                );
            }
        }
        base.OnGuiClosed();
    }

    internal bool CaptureNormalWorldSnapshotBeforeTransition()
    {
        // The scroll renderer owns an opaque stationary backdrop and a neutral
        // placeholder, so the transition never needs a synchronous screenshot
        // of the normal world framebuffer.
        return true;
    }

    private void AdvancePresentationChange()
    {
        if (!presentationChangeCoordinator.TryTake(
            capi.ElapsedMilliseconds,
            out bool requestedRenderOnScroll
        ))
        {
            return;
        }

        if (config.RenderOnScroll == requestedRenderOnScroll)
        {
            // A rapid round trip back to the committed value still counts as
            // the last selection, but it does not write the configuration or
            // disturb any GUI composer.
            SyncSettingsControls();
            return;
        }

        config.RenderOnScroll = requestedRenderOnScroll;
        ResetPointerDrag();
        selectedEntityId = null;
        FitLoadedTerrain();
        RecomposeViewportInterface();
        saveConfig();
        SyncSettingsControls();
        capi.Logger.Notification(
            requestedRenderOnScroll
                ? "[ModernAtlas] Applied the final debounced presentation choice: interactive 3D scroll."
                : "[ModernAtlas] Applied the final debounced presentation choice: full screen."
        );
    }

    private void CommitPendingPresentationPreference()
    {
        // Closing during the debounce interval must not silently discard the
        // final visible switch value. Automated smoke preferences are restored
        // separately and must never be persisted as player settings.
        if (automatedSmokeTestActive
            || !presentationChangeCoordinator.TryTakePending(
                out bool requestedRenderOnScroll
            )
            || config.RenderOnScroll == requestedRenderOnScroll)
        {
            return;
        }

        config.RenderOnScroll = requestedRenderOnScroll;
        saveConfig();
    }

    private bool ShouldRenderFreshAtlasFrame()
    {
        if (atlasFrameCacheTexture?.TextureId <= 0) return true;

        int interval = IsAtlasCameraMoving()
            ? MovingAtlasRefreshMilliseconds
            : IdleAtlasRefreshMilliseconds;
        return capi.ElapsedMilliseconds - lastAtlasWorldRenderMilliseconds >= interval;
    }

    private bool IsAtlasCameraMoving() =>
        leftDragging
        || rightDragging
        || Math.Abs(targetCenterX - centerX) > 0.001
        || Math.Abs(targetCenterZ - centerZ) > 0.001
        || Math.Abs(NormalizeSignedDegrees(targetYawDegrees - yawDegrees)) > 0.001f
        || Math.Abs(targetPitchDegrees - pitchDegrees) > 0.001f
        || Math.Abs(targetZoom - zoom) > 0.001f;

    private void CaptureAtlasFrameCache()
    {
        int textureId = exactChunkRenderer?.ResolvedColorTextureId ?? 0;
        if (textureId <= 0) return;

        IRenderAPI render = capi.Render;
        FrameBufferRef? resolved = exactChunkRenderer?.ResolvedFramebuffer;
        if (resolved == null) return;
        int width = Math.Max(1, resolved.Width);
        int height = Math.Max(1, resolved.Height);
        try
        {
            if (atlasFrameStagingTexture == null
                || atlasFrameStagingTexture.TextureId <= 0
                || atlasFrameStagingTexture.Width != width
                || atlasFrameStagingTexture.Height != height)
            {
                atlasFrameStagingTexture?.Dispose();
                atlasFrameStagingTexture = new LoadedTexture(capi)
                {
                    Width = width,
                    Height = height
                };
                int[] emptyPixels = new int[checked(width * height)];
                render.LoadOrUpdateTextureFromBgra(
                    emptyPixels,
                    true,
                    0,
                    ref atlasFrameStagingTexture
                );
            }

            LoadedTexture stagingTexture = atlasFrameStagingTexture
                ?? throw new InvalidOperationException(
                    "Atlas frame staging texture was not created."
                );

            if (primaryAtlasSourceTexture == null
                || primaryAtlasSourceTextureId != textureId
                || primaryAtlasSourceTexture.Width != width
                || primaryAtlasSourceTexture.Height != height)
            {
                primaryAtlasSourceTexture = new LoadedTexture(
                    capi,
                    textureId,
                    width,
                    height
                )
                {
                    // This wrapper references an engine-owned framebuffer
                    // texture and must never delete it.
                    IgnoreUndisposed = true
                };
                primaryAtlasSourceTextureId = textureId;
            }

            render.RenderTextureIntoTexture(
                primaryAtlasSourceTexture,
                0,
                0,
                width,
                height,
                stagingTexture,
                0,
                0,
                0
            );
            render.CurrentFrameBuffer = null;
            // Publish only after the GPU copy completed. The old published
            // texture remains the presentation source if any staging step
            // throws, so Primary can never become an accidental fallback.
            LoadedTexture? previousCompleteFrame = atlasFrameCacheTexture;
            atlasFrameCacheTexture = stagingTexture;
            atlasFrameStagingTexture = previousCompleteFrame;
            if (!loggedAtlasRefreshThrottle)
            {
                loggedAtlasRefreshThrottle = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Atlas refresh throttling is active at up to 60 FPS while the camera moves and 12 FPS while idle; world ticks and client chunk streaming remain active."
                );
            }
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not publish a complete throttled atlas frame; the previous complete frame remains active: {0}",
                exception.Message
            );
        }
    }

    private void RenderCachedAtlasFullscreen()
    {
        scrollViewportRenderer.RenderAtlasFullscreen(
            atlasFrameCacheTexture?.TextureId ?? 0
        );
    }

    private void ReleaseAtlasFrameCache()
    {
        atlasFrameCacheTexture?.Dispose();
        if (!ReferenceEquals(atlasFrameStagingTexture, atlasFrameCacheTexture))
        {
            atlasFrameStagingTexture?.Dispose();
        }
        atlasFrameCacheTexture = null;
        atlasFrameStagingTexture = null;
        primaryAtlasSourceTexture = null;
        primaryAtlasSourceTextureId = 0;
        lastAtlasWorldRenderMilliseconds = 0;
    }

    public void ScheduleNormalWorldShaderRestore()
    {
        ExactChunkRendererAdapter? rendererToRestore = exactChunkRenderer;
        if (rendererToRestore == null) return;

        pendingNormalWorldShaderRestore = rendererToRestore;
        normalWorldShaderRestoreGeneration++;
        // The ordinary world must not render one frame with atlas branches
        // still enabled. Restore synchronously while the live renderer and
        // DefaultShaderUniforms are valid; Dispose remains shader-free during
        // world teardown.
        pendingNormalWorldShaderRestore = null;
        rendererToRestore.RestoreNormalWorldShaders();
        capi.Logger.Notification(
            "[ModernAtlas] Synchronously restored ordinary-world shader state after atlas close: atlas flags clear={0}, current framebuffer null={1}.",
            rendererToRestore.AtlasStateIsClear,
            capi.Render.CurrentFrameBuffer == null
        );
    }

    private void FlushPendingNormalWorldShaderRestore()
    {
        ExactChunkRendererAdapter? rendererToRestore =
            pendingNormalWorldShaderRestore;
        if (rendererToRestore == null) return;

        pendingNormalWorldShaderRestore = null;
        normalWorldShaderRestoreGeneration++;
        rendererToRestore.RestoreNormalWorldShaders();
    }

    public void OnServerPolicyChanged()
    {
        if (!capi.IsSinglePlayer && !serverPolicy.CheatModeAllowed)
        {
            SetCheatMode(false);
        }
        RefreshVisibleEntityPolicy();
        SyncSettingsControls();
        SyncToolbarControls();
    }

    public void SetCheatMode(bool enabled)
    {
        bool accessWasAvailable = CreativeCheatSettingsAvailable;
        cheatModeEnabled = enabled
            && (capi.IsSinglePlayer || serverPolicy.CheatModeAllowed);
        if (activeMapLayer.RequiresSpoilerAccess() && !CreativeCheatSettingsAvailable)
        {
            SetMapLayer(AtlasMapLayer.TexturedTerrain);
        }
        if (!CreativeCheatSettingsAvailable)
        {
            if (bottomPanelSection == AtlasPanelSection.Creative)
            {
                RequestBottomPanel(AtlasPanelSection.None);
            }
            creativeSettingsModalOpen = false;
            ClearSearch();
            selectedOreCode = null;
            ClearOreHover();
        }
        ClampPitchToAccessLevel(!IsOpened());
        if (accessWasAvailable != CreativeCheatSettingsAvailable)
        {
            pendingInterfaceRecompose = true;
        }
        SyncCreativeSettingsControls();
        if (!IsOpened()) return;

        PrepareSurfaceSafetyFilter();
    }

    internal bool PrepareOpeningTransitionFrame()
    {
        EnsureExactChunkRenderer();
        if (exactChunkRenderer == null) return false;
        bool oreReady = !SurvivalOreConcealmentEnabled
            || exactChunkRenderer.AdvanceSurvivalOreConcealment();
        bool vegetationReady = !config.HideVegetation
            || exactChunkRenderer.AdvanceVegetationMask();
        return oreReady && vegetationReady;
    }

    private bool EnsureExactChunkRenderer()
    {
        if (exactChunkRenderer != null
            && !exactChunkRenderer.ReferencesCurrentChunkRenderer())
        {
            capi.Logger.Notification(
                "[ModernAtlas] Vintage Story replaced its chunk renderer; rebinding the atlas to the current exact chunk mesh pools."
            );
            exactChunkRenderer.Dispose();
            exactChunkRenderer = null;
        }

        if (exactChunkRenderer != null) return false;

        exactChunkRenderer = ExactChunkRendererAdapter.TryCreate(
            capi,
            stableLiquidShaderProvider,
            atlasCloudShaderProvider,
            atlasBoundaryShaderProvider
        );
        return exactChunkRenderer != null;
    }

    private void SynchronizeGameViewDistance()
    {
        int currentViewDistance = GameViewDistance;
        if (preparedGameViewDistance < 0)
        {
            preparedGameViewDistance = currentViewDistance;
            return;
        }
        if (preparedGameViewDistance == currentViewDistance) return;

        int previousViewDistance = preparedGameViewDistance;
        preparedGameViewDistance = currentViewDistance;
        capi.Logger.Notification(
            "[ModernAtlas] Game view distance changed from {0} to {1} blocks; rebuilding transient atlas filters from current loaded data and exact mesh pools.",
            previousViewDistance,
            currentViewDistance
        );
        exactChunkRenderer?.ResetTerrainCoverageDiagnostics();
        FitLoadedTerrain();
        PrepareSurfaceSafetyFilter();
        PrepareMapLayer();
    }

    private void ClampPitchToAccessLevel(bool immediate)
    {
        float minimumPitch = MinimumPitchDegrees;
        targetPitchDegrees = Math.Clamp(targetPitchDegrees, minimumPitch, 86);
        if (immediate)
        {
            pitchDegrees = Math.Clamp(pitchDegrees, minimumPitch, 86);
        }
    }

    internal void BeginAutomatedSmokeTest(Action<bool> completion)
    {
        presentationChangeCoordinator.Reset(config.RenderOnScroll);
        automatedOriginalMapLayersEnabled = config.MapLayersEnabled;
        automatedOriginalCaveModeEnabled = config.CaveModeEnabled;
        automatedOriginalSearchModeEnabled = config.SearchModeEnabled;
        automatedOriginalCameraAngleLocked = config.CameraAngleLocked;
        automatedOriginalRenderOnScroll = config.RenderOnScroll;
        automatedOriginalLiveLightingEnabled = config.LiveLightingEnabled;
        automatedOriginalCloudsEnabled = config.CloudsEnabled;
        automatedOriginalFixedSunHour = config.FixedSunHour;
        automatedOriginalShowPlayerCompass = config.ShowPlayerCompass;
        automatedOriginalHandheldInstrumentMode = HandheldInstrumentMode;
        automatedOriginalPerformanceLightingEnabled =
            config.PerformanceLightingEnabled;
        automatedOriginalHideVegetation = config.HideVegetation;
        automatedOriginalCheatModeEnabled = cheatModeEnabled;
        automatedOriginalScreenshotScale = config.ScreenshotScale;
        automatedOriginalScreenshotCaptureAreaPercent =
            config.ScreenshotCaptureAreaPercent;
        automatedScreenshotSequenceEnabled = IsEnvironmentFlagEnabled(
            SmokeScreenshotSequenceEnvironmentVariable
        );
        automatedScreenshotCancelMode = !automatedScreenshotSequenceEnabled
            && IsEnvironmentFlagEnabled(SmokeScreenshotCancelEnvironmentVariable);
        automatedScreenshotCancelTriggered = false;
        automatedScreenshotCancelPassed = false;
        automatedScreenshotSequenceFailed = false;
        automatedScreenshotOutputValidationPassed = false;
        automatedScreenshotDiagnosticNextMilliseconds = 0;
        automatedScreenshotSequenceStep = 0;
        automatedFirstScreenshotPath = null;
        automatedSecondScreenshotPath = null;
        automatedScreenshotPublicEntriesBefore =
            CapturePublicScreenshotEntries();
        automatedScreenshotJobEntriesBefore = CaptureScreenshotJobEntries();
        // The default automated capture uses a 2x2 tile grid so the tiled
        // pipeline is exercised without a large multi-frame sequence. The
        // optional environment overrides are used for targeted large-capture
        // regression runs, for example 8x at 25% area.
        int smokeScreenshotScale = automatedScreenshotSequenceEnabled ? 1 : 2;
        string? forcedScreenshotScale = Environment.GetEnvironmentVariable(
            SmokeScreenshotScaleEnvironmentVariable
        );
        if (int.TryParse(forcedScreenshotScale, out int parsedScreenshotScale))
        {
            smokeScreenshotScale = Math.Clamp(parsedScreenshotScale, 1, 8);
        }
        int smokeScreenshotCaptureArea = automatedScreenshotSequenceEnabled
            ? 25
            : 100;
        string? forcedScreenshotCaptureArea =
            Environment.GetEnvironmentVariable(
                SmokeScreenshotCaptureAreaEnvironmentVariable
            );
        if (int.TryParse(
            forcedScreenshotCaptureArea,
            out int parsedScreenshotCaptureArea
        ))
        {
            smokeScreenshotCaptureArea =
                AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
                    parsedScreenshotCaptureArea
                );
        }
        if ((!automatedScreenshotSequenceEnabled
                && (smokeScreenshotScale != 2
                    || smokeScreenshotCaptureArea != 100))
            || (automatedScreenshotSequenceEnabled
                && (smokeScreenshotScale != 1
                    || smokeScreenshotCaptureArea != 25)))
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test will capture the targeted {0}x/{1}% screenshot configuration.",
                smokeScreenshotScale,
                smokeScreenshotCaptureArea
            );
        }
        if (automatedScreenshotSequenceEnabled)
        {
            // The regression sequence intentionally covers both ends of the
            // reported failure: one 1x/25% job followed by one 8x/25% job in
            // the same atlas session.
            smokeScreenshotScale = 1;
            smokeScreenshotCaptureArea = 25;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test will run the consecutive screenshot sequence 1x/25% -> 8x/25%."
            );
        }
        if (automatedScreenshotCancelMode)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test will close the screenshot modal during stitching and verify exact-job cancellation cleanup."
            );
        }
        config.ScreenshotScale = smokeScreenshotScale;
        config.ScreenshotCaptureAreaPercent = smokeScreenshotCaptureArea;
        automatedSmokeTestPreferencesCaptured = true;
        if (capi.IsSinglePlayer)
        {
            cheatModeEnabled = true;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test temporarily accepted Cheat Mode for Creative-only atlas checks."
            );
        }
        capi.Logger.Notification(
            "[ModernAtlas] Automated smoke player game mode: {0}.",
            capi.World.Player?.WorldData.CurrentGameMode.ToString() ?? "unavailable"
        );
        config.MapLayersEnabled = true;
        config.CaveModeEnabled = false;
        config.SearchModeEnabled = true;
        config.CameraAngleLocked = false;
        config.RenderOnScroll = true;
        config.CloudsEnabled = !IsEnvironmentFlagEnabled(
            SmokeDisableCloudsEnvironmentVariable
        );
        if (!config.CloudsEnabled)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test disabled atlas clouds for the rendering A/B diagnostic."
            );
        }
        automatedSmokeTestInitialViewDistance = GameViewDistance;
        automatedSmokeTestViewDistancePassed = true;
        automatedSmokeTestViewDistanceUnchanged = true;
        string? expectedSmokeViewDistance = Environment.GetEnvironmentVariable(
            SmokeExpectedViewDistanceEnvironmentVariable
        );
        if (!string.IsNullOrWhiteSpace(expectedSmokeViewDistance))
        {
            if (int.TryParse(expectedSmokeViewDistance, out int expectedDistance))
            {
                automatedSmokeTestViewDistancePassed =
                    automatedSmokeTestInitialViewDistance == expectedDistance;
                if (automatedSmokeTestViewDistancePassed)
                {
                    capi.Logger.Notification(
                        "[ModernAtlas] Automated smoke view-distance probe passed: configured={0}; atlas read the same value without changing it.",
                        automatedSmokeTestInitialViewDistance
                    );
                }
                else
                {
                    capi.Logger.Error(
                        "[ModernAtlas] Automated smoke view-distance probe failed: expected configured value {0}, read {1}; the test never changes viewDistance.",
                        expectedDistance,
                        automatedSmokeTestInitialViewDistance
                    );
                }
            }
            else
            {
                automatedSmokeTestViewDistancePassed = false;
                capi.Logger.Error(
                    "[ModernAtlas] Automated smoke view-distance probe could not parse MODERNATLAS_SMOKE_EXPECT_VIEW_DISTANCE={0}.",
                    expectedSmokeViewDistance
                );
            }
        }
        else
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke view-distance probe: configured={0}; no setting mutation is performed.",
                automatedSmokeTestInitialViewDistance
            );
        }
        // Capture the resized compass in the early smoke frames. The normal
        // interface exercise later selects Time, so one run now covers both
        // instrument faces before restoring the player's original choice.
        config.ShowPlayerCompass = true;
        config.HandheldInstrumentMode = "compass";
        string? forcedSunHour = Environment.GetEnvironmentVariable(
            SmokeFixedSunHourEnvironmentVariable
        );
        if (int.TryParse(forcedSunHour, out int parsedSunHour))
        {
            config.LiveLightingEnabled = false;
            config.FixedSunHour = Math.Clamp(parsedSunHour, 0, 23);
            config.PerformanceLightingEnabled = true;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test forced the atlas sun to {0}:00 for lighting inspection.",
                config.FixedSunHour
            );
        }
        automatedSmokeTestActive = true;
        automatedSmokeTestElapsedSeconds = 0;
        automatedSmokeTestCompletion = completion;
        automatedSmokeTestRequiresUnlockedPitch = false;
        automatedSmokeTestRenderedAtPitchFloor = false;
        automatedSmokeTestInterfaceControlsAttempted = false;
        automatedSmokeTestInterfaceControlsPassed = false;
        automatedSmokeTestUnitInspectionAttempted = false;
        automatedSmokeTestUnitInspectionPassed = false;
        automatedSmokeTestSearchInputAttempted = false;
        automatedSmokeTestSearchInputPassed = false;
        automatedScreenshotCaptureRequested = false;
        automatedScreenshotCapturePassed = false;
        automatedScreenshotBaselineZoom = 0;
        automatedScreenshotBaselineTargetZoom = 0;
        automatedScreenshotBaselineCenterX = 0;
        automatedScreenshotBaselineCenterY = 0;
        automatedScreenshotBaselineCenterZ = 0;
        automatedSmokeTestBilingualSearchPassed =
            searchController.ValidateBilingualSearchForAutomatedTest(
                out string languageDiagnostic
            );
        automatedSmokeTestOreConcealmentPassed = false;
        automatedSmokeTestCreativeOreRevealFrameRendered = false;
        automatedSmokeTestForceSurvivalOreConcealment = true;
        if (automatedSmokeTestBilingualSearchPassed)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated bilingual search check passed: {0}.",
                languageDiagnostic
            );
        }
        else
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated bilingual search check failed: {0}.",
                languageDiagnostic
            );
        }
        automatedSmokeTestSafeSurfaceFrameRendered = false;
        automatedSmokeTestSafeSurfaceScreenshotHandled = false;
        automatedSmokeTestBorderTopDownPending = false;
        automatedSmokeTestBorderTopDownFrameRendered = false;
        automatedSmokeTestMaximumZoomPending = false;
        automatedSmokeTestMaximumZoomFrameRendered = false;
        automatedSmokeTestPartialZoomPending = false;
        automatedSmokeTestPartialZoomFrameRendered = false;
        automatedSmokeTestZoomBeforeMaximum = 0;
        automatedSmokeTestPitchBeforeMaximum = 0;
        automatedSmokeTestMaximumZoomStartedSeconds = 0;
        automatedSmokeTestPartialZoomStartedSeconds = 0;
        automatedSmokeTestBorderTopDownStartedSeconds = 0;
        automatedSmokeTestSearchPhase = 0;
        automatedSmokeTestSearchPassed = false;
        automatedLanternBlockX = 0;
        automatedLanternBlockY = 0;
        automatedLanternBlockZ = 0;
        automatedSmokeTestMapLayerPhase = 0;
        automatedSmokeTestOreLayerZoom = 0;
        automatedSmokeTestMapLayerPassed = false;
        pendingAutomatedMapLayerScreenshotSuffix = null;
        automatedSmokeTestPerformanceModeSelected = false;
        automatedSmokeTestPerformanceModeRendered = false;
        automatedSmokeTestPresentationPassed = false;
        automatedSmokeTestResolvedAtlasAlphaChecked = false;
        automatedSmokeTestResolvedAtlasAlphaPassed = false;
        automatedSmokeCloseStatePassed = true;
        automatedSmokeTestPresentationPhase = 0;
        automatedSmokeTestSettingsComposer = null;
        automatedSmokeTestPerformanceComposer = null;
        automatedSmokeTestCreativeComposer = null;
        automatedSmokeTestVisualLabComposer = null;
        automatedSmokeScreenshotPhase = 0;
        AutomatedSmokeTestRenderedExactWorld = false;
    }

    private void AdvanceAutomatedSmokeTest()
    {
        if (!automatedSmokeTestActive) return;

        automatedSmokeTestElapsedSeconds += atlasRealDeltaTime;
        if (automatedScreenshotCancelMode
            && automatedScreenshotCaptureRequested
            && !automatedScreenshotCancelTriggered
            && tileScreenshot.State == AtlasScreenshotJobState.Stitching)
        {
            automatedScreenshotCancelTriggered = true;
            tileScreenshot.Cancel();
            RestoreScreenshotCamera();
            pendingScreenshotRequest = false;
            CloseScreenshotProgressModal();
            capi.Logger.Notification(
                "[ModernAtlas] Automated screenshot modal was closed during Stitching; waiting for exact-job cleanup."
            );
        }
        if (automatedScreenshotCancelMode
            && automatedScreenshotCancelTriggered
            && !automatedScreenshotCancelPassed
            && !tileScreenshot.Busy)
        {
            bool cameraRestored = !pendingScreenshotRequest
                && (Math.Abs(zoom - automatedScreenshotBaselineZoom) < 0.001f
                    || Math.Abs(zoom - automatedScreenshotBaselineTargetZoom) < 0.001f)
                && Math.Abs(targetZoom - automatedScreenshotBaselineTargetZoom) < 0.001f
                && Math.Abs(centerX - automatedScreenshotBaselineCenterX) < 0.001
                && Math.Abs(centerY - automatedScreenshotBaselineCenterY) < 0.001
                && Math.Abs(centerZ - automatedScreenshotBaselineCenterZ) < 0.001;
            if (tileScreenshot.State == AtlasScreenshotJobState.Cancelled
                && tileScreenshot.LastSavedPath == null
                && cameraRestored
                && ValidateCancelledScreenshotOutputs())
            {
                automatedScreenshotCancelPassed = true;
                automatedScreenshotCapturePassed = false;
                capi.Logger.Notification(
                    "[ModernAtlas] Automated screenshot cancellation cleanup passed: no final PNG or public sidecar was exposed and the camera was restored."
                );
            }
            else if (tileScreenshot.LastError != null)
            {
                automatedScreenshotSequenceFailed = true;
                capi.Logger.Error(
                    "[ModernAtlas] Automated screenshot cancellation test failed: {0}.",
                    tileScreenshot.LastError
                );
            }
        }
        if (automatedScreenshotCaptureRequested && !automatedScreenshotCapturePassed)
        {
            string? capturedPath = tileScreenshot.LastSavedPath;
            string? capturedError = tileScreenshot.LastError;
            bool cameraRestored = !pendingScreenshotRequest
                && (Math.Abs(zoom - automatedScreenshotBaselineZoom) < 0.001f
                    || Math.Abs(zoom - automatedScreenshotBaselineTargetZoom) < 0.001f)
                && Math.Abs(targetZoom - automatedScreenshotBaselineTargetZoom) < 0.001f
                && Math.Abs(centerX - automatedScreenshotBaselineCenterX) < 0.001
                && Math.Abs(centerY - automatedScreenshotBaselineCenterY) < 0.001
                && Math.Abs(centerZ - automatedScreenshotBaselineCenterZ) < 0.001;
            if (capi.ElapsedMilliseconds >= automatedScreenshotDiagnosticNextMilliseconds)
            {
                automatedScreenshotDiagnosticNextMilliseconds =
                    capi.ElapsedMilliseconds + 1000;
                capi.Logger.Notification(
                    "[ModernAtlas] Automated screenshot completion diagnostic: path={0}, exists={1}, busy={2}, state={3}, pending={4}, cameraRestored={5}, zoom={6}/{7}, targetZoom={8}/{9}, center=({10:0.###},{11:0.###},{12:0.###})/({13:0.###},{14:0.###},{15:0.###}).",
                    capturedPath ?? "<null>",
                    capturedPath != null && File.Exists(capturedPath),
                    tileScreenshot.Busy,
                    tileScreenshot.State,
                    pendingScreenshotRequest,
                    cameraRestored,
                    zoom,
                    automatedScreenshotBaselineZoom,
                    targetZoom,
                    automatedScreenshotBaselineTargetZoom,
                    centerX,
                    centerY,
                    centerZ,
                    automatedScreenshotBaselineCenterX,
                    automatedScreenshotBaselineCenterY,
                    automatedScreenshotBaselineCenterZ
                );
            }
            if (capturedPath != null
                && File.Exists(capturedPath)
                && !tileScreenshot.Busy
                && cameraRestored)
            {
                try
                {
                    AtlasTiledScreenshot.ValidateCommittedPngForAutomation(
                        capturedPath
                    );
                    if (automatedScreenshotSequenceEnabled
                        && automatedScreenshotSequenceStep == 0)
                    {
                        automatedFirstScreenshotPath = capturedPath;
                        automatedScreenshotSequenceStep = 1;
                        automatedScreenshotCaptureRequested = false;
                        automatedScreenshotCapturePassed = false;
                        config.ScreenshotScale = 8;
                        config.ScreenshotCaptureAreaPercent = 25;
                        SyncScreenshotSettingsControls();
                        CloseScreenshotProgressModal();
                        capi.Logger.Notification(
                            "[ModernAtlas] Automated consecutive screenshot job 1x/25% passed: {0}; the camera was restored. Queueing 8x/25%.",
                            capturedPath
                        );
                    }
                    else
                    {
                        automatedSecondScreenshotPath =
                            automatedScreenshotSequenceEnabled
                                ? capturedPath
                                : automatedSecondScreenshotPath;
                        automatedScreenshotSequenceStep =
                            automatedScreenshotSequenceEnabled ? 2 : 1;
                        automatedScreenshotCapturePassed = true;
                        CloseScreenshotProgressModal();
                        capi.Logger.Notification(
                            "[ModernAtlas] Automated tiled-screenshot capture passed: {0}; the atlas camera was restored.",
                            capturedPath
                        );
                    }
                }
                catch (Exception exception)
                {
                    automatedScreenshotCapturePassed = false;
                    automatedScreenshotSequenceFailed = true;
                    automatedScreenshotCaptureRequested = true;
                    CloseScreenshotProgressModal();
                    capi.Logger.Error(
                        "[ModernAtlas] Automated screenshot PNG validation failed for {0}: {1}.",
                        capturedPath,
                        exception.Message
                    );
                }
            }
            else if (capturedError != null)
            {
                automatedScreenshotCapturePassed = false;
                automatedScreenshotSequenceFailed = true;
                // Keep the request latched in the failed state. Releasing it
                // here would immediately launch the same broken capture again
                // on the next frame and hide the original diagnostic.
                automatedScreenshotCaptureRequested = true;
                CloseScreenshotProgressModal();
                capi.Logger.Error(
                    "[ModernAtlas] Automated tiled-screenshot capture failed: {0}.",
                    capturedError
                );
            }
        }
        // Every other automated step must be complete before the tiled
        // capture starts. The map-layer and presentation exercises move the
        // camera; queueing earlier would make the camera-restore comparison
        // race their legitimate zoom changes.
        bool smokeStepsComplete = AutomatedSmokeTestRenderedExactWorld
            && exactChunkRenderer?.BoundaryResolvedLastFrame == true
            && automatedSmokeTestSafeSurfaceFrameRendered
            && (!automatedSmokeTestRequiresUnlockedPitch
                || automatedSmokeTestRenderedAtPitchFloor)
            && automatedSmokeTestInterfaceControlsPassed
            && (!automatedSmokeTestUnitInspectionAttempted
                || automatedSmokeTestUnitInspectionPassed)
            && automatedSmokeTestSearchInputPassed
            && automatedSmokeTestBilingualSearchPassed
            && automatedSmokeTestOreConcealmentPassed
            && automatedSmokeTestCreativeOreRevealFrameRendered
            && automatedSmokeTestPartialZoomFrameRendered
            && automatedSmokeTestMaximumZoomFrameRendered
            && automatedSmokeTestSearchPassed
            && automatedSmokeTestMapLayerPassed
            && automatedSmokeTestPerformanceModeRendered
            && automatedSmokeTestPresentationPassed
            && automatedSmokeTestResolvedAtlasAlphaPassed
            && automatedSmokeTestViewDistancePassed
            && automatedSmokeTestViewDistanceUnchanged;
        if (smokeStepsComplete
            && !automatedScreenshotCaptureRequested
            && !automatedScreenshotSequenceFailed
            && (!automatedScreenshotCancelMode
                || !automatedScreenshotCancelTriggered))
        {
            if (!config.RenderOnScroll)
            {
                // The presentation test ends in fullscreen mode. Return to
                // the scroll viewport first so the capture exercises the
                // scroll-aspect tile cropping as well. Request only once:
                // re-requesting every frame would restart the debounce timer.
                if (!presentationChangeCoordinator.HasPending)
                {
                    OnRenderOnScrollToggled(true);
                }
            }
            else if (!presentationChangeCoordinator.HasPending)
            {
                automatedScreenshotBaselineZoom = zoom;
                automatedScreenshotBaselineTargetZoom = targetZoom;
                automatedScreenshotBaselineCenterX = centerX;
                automatedScreenshotBaselineCenterY = centerY;
                automatedScreenshotBaselineCenterZ = centerZ;
                // Save the live viewport frame right before the capture, so
                // the stitched PNG can be compared against this reference to
                // prove the tile order and orientation.
                string? referencePath = Environment.GetEnvironmentVariable(
                    SmokeScreenshotEnvironmentVariable
                );
                if (!string.IsNullOrWhiteSpace(referencePath))
                {
                    string prefix = referencePath.EndsWith(
                        ".png",
                        StringComparison.OrdinalIgnoreCase
                    )
                        ? referencePath[..^4]
                        : referencePath;
                    TrySaveAutomatedSmokeScreenshot(
                        $"{prefix}-screenshot-view.png"
                    );
                }
                bool takeAccepted = TakeScreenshot();
                automatedScreenshotCaptureRequested = pendingScreenshotRequest;
                if (!takeAccepted || !automatedScreenshotCaptureRequested)
                {
                    automatedScreenshotSequenceFailed = true;
                    automatedScreenshotCaptureRequested = true;
                    capi.Logger.Error(
                        "[ModernAtlas] Automated tiled-screenshot job was rejected before capture began."
                    );
                }
                capi.Logger.Notification(
                    "[ModernAtlas] Automated smoke test queued a {0}x{0} tiled atlas screenshot of the centered {1}% area.",
                    Math.Clamp(config.ScreenshotScale, 1, 8),
                    AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
                        config.ScreenshotCaptureAreaPercent
                    )
                );
            }
        }
        bool screenshotPassed = automatedScreenshotCancelMode
            ? automatedScreenshotCancelPassed
            : automatedScreenshotCaptureRequested
                && automatedScreenshotCapturePassed;
        bool passed = smokeStepsComplete
            && screenshotPassed
            && !automatedScreenshotSequenceFailed
            && (!automatedScreenshotSequenceEnabled
                || automatedScreenshotSequenceStep >= 2);
        if (passed && automatedSmokeTestElapsedSeconds < 3f) return;
        // Exact loaded-data search and map-layer preparation are budgeted over
        // many frames. A large standard world can legitimately need more than
        // one minute before those phases finish, especially after a second
        // atlas cycle. Keep the timeout finite, but avoid treating ordinary
        // client streaming variance as a rendering failure.
        float screenshotTimeoutSeconds = config.ScreenshotScale >= 8
            ? 180f
            : 120f;
        if (!passed && automatedSmokeTestElapsedSeconds < screenshotTimeoutSeconds)
            return;

        if (passed
            && !automatedScreenshotCancelMode
            && !automatedScreenshotOutputValidationPassed)
        {
            automatedScreenshotOutputValidationPassed =
                ValidateAutomatedScreenshotOutputs(out string outputDiagnostic);
            if (!automatedScreenshotOutputValidationPassed)
            {
                passed = false;
                capi.Logger.Error(
                    "[ModernAtlas] Automated screenshot output validation failed: {0}.",
                    outputDiagnostic
                );
            }
        }

        if (!passed)
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated smoke summary: exact={0}, boundary={1}, resolvedAlpha={2}(checked={3}), viewDistance={4}(unchanged={5}, initial={6}), safeSurface={7}, pitch={8}/{9}, interface={10}, unit={11}/{12}, searchInput={13}, bilingual={14}, ore={15}, creativeOre={16}, partialZoom={17}, maximumZoom={18}, search={19}(phase={20}), layers={21}(phase={22}), performance={23}(selected={24}, renderedVegetationHidden={25}, flatLighting={26}), presentation={27}, screenshotCapture={28}(requested={29}, sequenceStep={30}, outputs={31}, cancel={32}/{33}).",
                AutomatedSmokeTestRenderedExactWorld,
                exactChunkRenderer?.BoundaryResolvedLastFrame == true,
                automatedSmokeTestResolvedAtlasAlphaPassed,
                automatedSmokeTestResolvedAtlasAlphaChecked,
                automatedSmokeTestViewDistancePassed,
                automatedSmokeTestViewDistanceUnchanged,
                automatedSmokeTestInitialViewDistance,
                automatedSmokeTestSafeSurfaceFrameRendered,
                automatedSmokeTestRenderedAtPitchFloor,
                automatedSmokeTestRequiresUnlockedPitch,
                automatedSmokeTestInterfaceControlsPassed,
                automatedSmokeTestUnitInspectionPassed,
                automatedSmokeTestUnitInspectionAttempted,
                automatedSmokeTestSearchInputPassed,
                automatedSmokeTestBilingualSearchPassed,
                automatedSmokeTestOreConcealmentPassed,
                automatedSmokeTestCreativeOreRevealFrameRendered,
                automatedSmokeTestPartialZoomFrameRendered,
                automatedSmokeTestMaximumZoomFrameRendered,
                automatedSmokeTestSearchPassed,
                automatedSmokeTestSearchPhase,
                automatedSmokeTestMapLayerPassed,
                automatedSmokeTestMapLayerPhase,
                automatedSmokeTestPerformanceModeRendered,
                automatedSmokeTestPerformanceModeSelected,
                exactChunkRenderer?.LastRenderedVegetationHidden ?? false,
                !(exactChunkRenderer?.LastRenderedPerformanceLightingEnabled ?? true),
                automatedSmokeTestPresentationPassed,
                automatedScreenshotCapturePassed,
                automatedScreenshotCaptureRequested,
                automatedScreenshotSequenceStep,
                automatedScreenshotOutputValidationPassed,
                automatedScreenshotCancelPassed,
                automatedScreenshotCancelMode
            );
        }

        automatedSmokeTestActive = false;
        Action<bool>? completion = automatedSmokeTestCompletion;
        automatedSmokeTestCompletion = null;
        RestoreAutomatedSmokeTestPreferences();
        completion?.Invoke(passed);
    }

    private void ExerciseAutomatedInterfaceControls()
    {
        if (automatedSmokeTestInterfaceControlsAttempted) return;
        automatedSmokeTestInterfaceControlsAttempted = true;

        bool accessAvailable = CreativeCheatSettingsAvailable;
        bool toolbarPresent = overlay?.GetElement("settings-button")
                is GuiElementAtlasButton
            && overlay?.GetElement("quick-screenshot-button")
                is GuiElementAtlasButton
            && overlay?.GetElement("screenshot-options-button")
                is GuiElementAtlasButton
            && overlay?.GetElement("map-options-button")
                is GuiElementAtlasButton
            && overlay?.GetElement("search-button")
                is GuiElementAtlasButton
            && overlay?.GetElement("instrument-button")
                is GuiElementAtlasButton;
        bool initialFocusReleased = searchPanel?.GetTextInput("search-input")?.HasFocus != true;

        bool settingsOpenedByClick = ClickAtlasControlForAutomatedTest(
            overlay,
            "settings-button"
        ) && settingsModalOpen;
        GuiComposer? settingsComposer = settingsModal;
        bool settingsControlsPresent = settingsComposer?.GetElement("map-layers")
                is GuiElementAtlasSwitch
            && settingsComposer.GetElement("performance-open")
                is GuiElementAtlasButton
            && settingsComposer.GetElement("search-mode")
                is GuiElementAtlasSwitch;

        bool mapLayersBefore = config.MapLayersEnabled;
        bool mapLayersToggled = settingsOpenedByClick
            && ClickAtlasControlForAutomatedTest(settingsComposer, "map-layers")
            && config.MapLayersEnabled != mapLayersBefore;
        bool mapLayersRestored = mapLayersToggled
            && ClickAtlasControlForAutomatedTest(settingsComposer, "map-layers")
            && config.MapLayersEnabled == mapLayersBefore;
        bool skipBefore = config.SkipOpeningAnimation;
        bool skipToggled = ClickAtlasControlForAutomatedTest(
                settingsComposer,
                "skip-opening-animation"
            )
            && config.SkipOpeningAnimation != skipBefore;
        bool skipRestored = skipToggled
            && ClickAtlasControlForAutomatedTest(
                settingsComposer,
                "skip-opening-animation"
            )
            && config.SkipOpeningAnimation == skipBefore;

        bool performanceQueued = ClickAtlasControlForAutomatedTest(
                settingsComposer,
                "performance-open"
            )
            && queuedBottomPanelSection == AtlasPanelSection.Performance;
        OpenBottomPanelImmediately(AtlasPanelSection.Performance);
        bool performanceOpened = performanceQueued && performanceModalOpen;
        bool flatLightingSelected = performanceOpened
            && ClickAtlasControlForAutomatedTest(
                performanceModal,
                "performance-lighting"
            );
        bool vegetationSelected = performanceOpened
            && ClickAtlasControlForAutomatedTest(
                performanceModal,
                "hide-vegetation"
            );
        automatedSmokeTestPerformanceModeSelected = performanceOpened
            && flatLightingSelected
            && vegetationSelected;
        if (automatedSmokeTestPerformanceModeSelected)
        {
            OnPerformanceLightingToggled(false);
            OnHideVegetationToggled(true);
            lastAtlasWorldRenderMilliseconds = 0;
        }
        bool performanceBack = ClickAtlasControlForAutomatedTest(
                performanceModal,
                "performance-back"
            )
            && queuedBottomPanelSection == AtlasPanelSection.Settings;
        OpenBottomPanelImmediately(AtlasPanelSection.Settings);
        settingsComposer = settingsModal;
        bool presentationControlPresent = settingsComposer?.GetElement(
                "render-on-scroll"
            ) is GuiElementAtlasSwitch;
        bool fixedLightingPrepared = true;
        if (config.LiveLightingEnabled)
        {
            fixedLightingPrepared = ClickAtlasControlForAutomatedTest(
                settingsComposer,
                "live-lighting"
            ) && !config.LiveLightingEnabled;
        }
        int fixedHourBefore = config.FixedSunHour;
        bool sliderBoundaryHandled = DragAtlasSliderBeyondBoundsForAutomatedTest(
            settingsComposer,
            "fixed-sun-hour",
            fixedHourBefore != 23
        );
        bool sliderBoundaryClamped = config.FixedSunHour == (fixedHourBefore != 23 ? 23 : 0);
        OnFixedSunHourChanged(fixedHourBefore);
        settingsComposer?.GetAtlasSlider("fixed-sun-hour")?.SetValue(fixedHourBefore);
        bool liveLightingRestored = !config.LiveLightingEnabled
            || (ClickAtlasControlForAutomatedTest(settingsComposer, "live-lighting")
                && config.LiveLightingEnabled);
        if (automatedSmokeTestPerformanceModeSelected)
        {
            // The live/fixed lighting controls deliberately re-enable
            // directional lighting. Re-apply the already-tested developer
            // visual state so the next real atlas frame observes flat
            // lighting and hidden vegetation before the test continues.
            OnPerformanceLightingToggled(false);
            OnHideVegetationToggled(true);
            lastAtlasWorldRenderMilliseconds = 0;
        }
        OpenBottomPanelImmediately(AtlasPanelSection.Instrument);
        bool timeInstrumentSelected = bottomPanel?.GetElement(
                "handheld-instrument"
            ) is GuiElementAtlasChoice
            && ClickAtlasControlForAutomatedTest(
                bottomPanel,
                "handheld-instrument"
            )
            && config.ShowPlayerCompass
            && HandheldInstrumentMode == "time";
        OpenBottomPanelImmediately(AtlasPanelSection.Settings);
        settingsComposer = settingsModal;

        // The toolbar request intentionally goes through the same close-then-
        // open state machine used by real clicks. The direct immediate call
        // below only settles the already-tested transition for the next UI
        // assertions; it never creates a second active composer.
        bool settingsClosed = ClickAtlasControlForAutomatedTest(
            settingsComposer,
            "settings-close"
        ) && !settingsModalOpen;
        bool screenshotQueued = ClickAtlasControlForAutomatedTest(
                overlay,
                "screenshot-options-button"
            )
            && queuedBottomPanelSection == AtlasPanelSection.ScreenshotOptions;
        OpenBottomPanelImmediately(AtlasPanelSection.ScreenshotOptions);
        GuiComposer? screenshotComposer = screenshotPanel;
        bool screenshotControlsPresent = screenshotQueued
            && screenshotComposer?.GetElement("shot-scale") is GuiElementAtlasChoice
            && screenshotComposer.GetElement("shot-area") is GuiElementAtlasChoice
            && screenshotComposer.GetElement("shot-preview") != null
            && screenshotComposer.GetElement("shot-take") is GuiElementAtlasButton;
        int screenshotScaleBefore = config.ScreenshotScale;
        bool screenshotScaleChanged = screenshotControlsPresent
            && ClickAtlasControlForAutomatedTest(screenshotComposer, "shot-scale");
        if (screenshotScaleChanged)
        {
            OnScreenshotScaleChanged(
                screenshotScaleBefore == 4 ? "1" : "4",
                true
            );
            screenshotScaleChanged = config.ScreenshotScale != screenshotScaleBefore;
            OnScreenshotScaleChanged(screenshotScaleBefore.ToString(), true);
        }
        int captureAreaBefore = AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
            config.ScreenshotCaptureAreaPercent
        );
        bool captureAreaChanged = screenshotControlsPresent
            && ClickAtlasControlForAutomatedTest(screenshotComposer, "shot-area");
        if (captureAreaChanged)
        {
            int alternateArea = captureAreaBefore == 25 ? 100 : 25;
            OnScreenshotCaptureAreaChanged(alternateArea.ToString(), true);
            captureAreaChanged = config.ScreenshotCaptureAreaPercent != captureAreaBefore;
            OnScreenshotCaptureAreaChanged(captureAreaBefore.ToString(), true);
        }
        bool screenshotPreviewValid = screenshotControlsPresent
            && ValidateScreenshotPreviewRange();

        // Quick screenshot is a thin binding to the same tiled job. Start it
        // once, then cancel it immediately so this UI test leaves no PNG and
        // the later full capture still exercises the normal pipeline.
        bool quickButtonPresent = overlay?.GetElement("quick-screenshot-button")
            is GuiElementAtlasButton;
        bool quickAccepted = quickButtonPresent && TakeQuickScreenshot();
        bool quickStarted = pendingScreenshotRequest;
        if (quickStarted) CancelScreenshotCapture();

        bool mapQueued = ClickAtlasControlForAutomatedTest(
                overlay,
                "map-options-button"
            )
            && queuedBottomPanelSection == AtlasPanelSection.MapOptions;
        OpenBottomPanelImmediately(AtlasPanelSection.MapOptions);
        GuiComposer? mapComposer = mapLayerPanel;
        bool mapControlsPresent = mapQueued
            && mapComposer?.GetDropDown("map-layer") != null
            && mapComposer.GetElement("map-layers") == null;
        bool mapLayerResetToTextured = false;
        if (mapControlsPresent)
        {
            // Textured terrain is the explicit neutral state; no second
            // Layers ON/OFF switch is needed in this panel.
            OnMapLayerChanged("moisture", true);
            bool overlaySelected = activeMapLayer == AtlasMapLayer.Moisture;
            OnMapLayerChanged("textured", true);
            mapLayerResetToTextured = overlaySelected
                && activeMapLayer == AtlasMapLayer.TexturedTerrain;
        }
        bool mapClosed = ClickAtlasControlForAutomatedTest(mapComposer, "map-layer-toggle")
            && bottomPanelSection == AtlasPanelSection.MapOptions;
        OpenBottomPanelImmediately(AtlasPanelSection.MapOptions);

        bool instrumentOpened = ClickAtlasControlForAutomatedTest(
                overlay,
                "instrument-button"
            );
        OpenBottomPanelImmediately(AtlasPanelSection.Instrument);
        bool instrumentControlsPresent = instrumentOpened
            && bottomPanelSection == AtlasPanelSection.Instrument
            && bottomPanel?.GetElement("handheld-instrument") is GuiElementAtlasChoice;

        bool searchOpened = false;
        bool searchControlsPresent = false;
        if (SearchModeActive)
        {
            ResetBottomPanelState();
            searchOpened = ClickAtlasControlForAutomatedTest(overlay, "search-button");
            searchControlsPresent = searchOpened
                && searchPanel?.GetTextInput("search-input") != null;
        }

        bool creativeOpened = false;
        bool creativeClosed = false;
        bool visualOpened = false;
        bool visualClosed = false;
        if (accessAvailable)
        {
            OpenBottomPanelImmediately(AtlasPanelSection.Settings);
            bool creativeQueued = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "creative-settings-button"
                )
                && queuedBottomPanelSection == AtlasPanelSection.Creative;
            OpenBottomPanelImmediately(AtlasPanelSection.Creative);
            creativeOpened = creativeQueued
                && creativeSettingsModalOpen
                && creativeSettingsModal?.GetElement("camera-angle-lock")
                    is GuiElementAtlasSwitch;
            creativeClosed = creativeOpened
                && ClickAtlasControlForAutomatedTest(
                    creativeSettingsModal,
                    "creative-settings-close"
                )
                && queuedBottomPanelSection == AtlasPanelSection.Settings;
            OpenBottomPanelImmediately(AtlasPanelSection.Settings);
            visualOpened = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "visual-lab-open"
                )
                && queuedBottomPanelSection == AtlasPanelSection.VisualLab;
            OpenBottomPanelImmediately(AtlasPanelSection.VisualLab);
            visualOpened = visualOpened
                && visualLabModal?.GetAtlasSlider("atlas-exposure") != null
                && visualLabModal?.GetAtlasSlider("cave-mask-brightness") != null;
            visualClosed = visualOpened
                && ClickAtlasControlForAutomatedTest(
                    visualLabModal,
                    "visual-lab-back"
                )
                && queuedBottomPanelSection == AtlasPanelSection.Settings;
        }

        float originalYaw = targetYawDegrees;
        float originalPitch = targetPitchDegrees;
        config.CameraAngleLocked = true;
        ApplyRotationDrag(8, 24);
        bool cameraAngleStayedLocked = Math.Abs(targetPitchDegrees - originalPitch) < 0.001f;
        bool cameraYawStayedFree = Math.Abs(
            NormalizeSignedDegrees(targetYawDegrees - originalYaw)
        ) > 0.001f;
        targetYawDegrees = originalYaw;
        targetPitchDegrees = originalPitch;
        config.CameraAngleLocked = false;
        bool safeSurfaceWasActive = SurfaceSafetyEnabled;
        config.CaveModeEnabled = true;
        bool caveModeActivated = !SurfaceSafetyEnabled;
        PrepareSurfaceSafetyFilter();

        UpdateToolbarTooltip(
            (int)Math.Round(overlay?.GetAtlasButton("settings-button")?.Bounds.absX ?? 0),
            (int)Math.Round(overlay?.GetAtlasButton("settings-button")?.Bounds.absY ?? 0)
        );
        bool tooltipPassed = toolbarTooltipText == "Settings";
        UpdateToolbarTooltip(-1, -1);
        int bottomPanelAliasCount = 0;
        if (ReferenceEquals(settingsModal, bottomPanel)) bottomPanelAliasCount++;
        if (ReferenceEquals(performanceModal, bottomPanel)) bottomPanelAliasCount++;
        if (ReferenceEquals(creativeSettingsModal, bottomPanel)) bottomPanelAliasCount++;
        if (ReferenceEquals(visualLabModal, bottomPanel)) bottomPanelAliasCount++;
        if (ReferenceEquals(searchPanel, bottomPanel)) bottomPanelAliasCount++;
        if (ReferenceEquals(mapLayerPanel, bottomPanel)) bottomPanelAliasCount++;
        if (ReferenceEquals(screenshotPanel, bottomPanel)) bottomPanelAliasCount++;
        if (ReferenceEquals(unitPanel, bottomPanel)) bottomPanelAliasCount++;
        bool oneBottomPanel = bottomPanel != null && bottomPanelAliasCount == 1;
        bool responsiveBounds = overlay?.GetAtlasButton("settings-button") != null
            && (!hasBottomPanelGeometry
                || (bottomPanelGeometry.Height <= capi.Render.FrameHeight * 0.5
                    && bottomPanelGeometry.Width > 0));
        int renderedEntityCount = exactChunkRenderer?.LastRenderedEntityCount ?? 0;
        bool heldItemsSuppressed = renderedEntityCount > 0
            && exactChunkRenderer?.LastSuppressedHeldItemCount == renderedEntityCount;

        ResetBottomPanelState();
        automatedSmokeTestInterfaceControlsPassed = toolbarPresent
            && accessAvailable
            && initialFocusReleased
            && settingsOpenedByClick
            && settingsControlsPresent
            && mapLayersToggled
            && mapLayersRestored
            && skipToggled
            && skipRestored
            && performanceBack
            && performanceOpened
            && automatedSmokeTestPerformanceModeSelected
            && presentationControlPresent
            && fixedLightingPrepared
            && sliderBoundaryHandled
            && sliderBoundaryClamped
            && liveLightingRestored
            && timeInstrumentSelected
            && settingsClosed
            && screenshotControlsPresent
            && screenshotScaleChanged
            && captureAreaChanged
            && screenshotPreviewValid
            && quickAccepted
            && (quickStarted || tileScreenshot.State == AtlasScreenshotJobState.Cancelled)
            && mapControlsPresent
            && mapLayerResetToTextured
            && mapClosed
            && instrumentControlsPresent
            && (!SearchModeActive || (searchOpened && searchControlsPresent))
            && (!accessAvailable || (creativeOpened && creativeClosed && visualOpened && visualClosed))
            && tooltipPassed
            && oneBottomPanel
            && responsiveBounds
            && caveModeActivated
            && cameraAngleStayedLocked
            && cameraYawStayedFree
            && heldItemsSuppressed;
        if (automatedSmokeTestInterfaceControlsPassed)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated compact UI test passed: toolbar, tooltips, one shared bottom panel, rapid section switching, Settings, screenshot options, quick screenshot binding, map options, search focus, instrument, Creative/Cheat controls, Hide UI/Escape and responsive bounds were exercised for {0} living models.",
                renderedEntityCount
            );
        }
        else
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated compact UI test failed: toolbar={0}, settings={1}/{2}, screenshot={3}, quick={4}/{5}, map={6}/{7}, instrument={8}, search={9}, creative={10}/{11}, visual={12}/{13}, tooltip={14}, onePanel={15}, bounds={16}, performance={17}, heldItems={18}/{19}.",
                toolbarPresent,
                settingsOpenedByClick,
                settingsControlsPresent,
                screenshotControlsPresent,
                quickAccepted,
                quickStarted,
                mapControlsPresent,
                mapLayerResetToTextured,
                instrumentControlsPresent,
                searchOpened && searchControlsPresent,
                creativeOpened,
                creativeClosed,
                visualOpened,
                visualClosed,
                tooltipPassed,
                oneBottomPanel,
                responsiveBounds,
                automatedSmokeTestPerformanceModeSelected,
                exactChunkRenderer?.LastSuppressedHeldItemCount ?? 0,
                renderedEntityCount
            );
        }
        return;

#if false
        if (automatedSmokeTestInterfaceControlsAttempted) return;
        automatedSmokeTestInterfaceControlsAttempted = true;

        bool accessAvailable = CreativeCheatSettingsAvailable;
        bool settingsOpenedByClick = ClickAtlasControlForAutomatedTest(
                overlay,
                "settings-button"
            )
            && settingsModalOpen;
        bool settingsClosedByToggle = settingsOpenedByClick
            && ClickAtlasControlForAutomatedTest(overlay, "settings-button")
            && !SettingsHierarchyOpen;
        bool settingsReopenedByToggle = settingsClosedByToggle
            && ClickAtlasControlForAutomatedTest(overlay, "settings-button")
            && settingsModalOpen;
        bool mapLayersBeforeClick = config.MapLayersEnabled;
        bool settingsSwitchClicked = settingsReopenedByToggle
            && ClickAtlasControlForAutomatedTest(settingsModal, "map-layers")
            && config.MapLayersEnabled != mapLayersBeforeClick;
        bool settingsSwitchRestored = settingsSwitchClicked
            && ClickAtlasControlForAutomatedTest(settingsModal, "map-layers")
            && config.MapLayersEnabled == mapLayersBeforeClick;
        bool skipOpeningBeforeClick = config.SkipOpeningAnimation;
        bool skipOpeningClicked = settingsOpenedByClick
            && ClickAtlasControlForAutomatedTest(
                settingsModal,
                "skip-opening-animation"
            )
            && config.SkipOpeningAnimation != skipOpeningBeforeClick;
        bool skipOpeningRestored = skipOpeningClicked
            && ClickAtlasControlForAutomatedTest(
                settingsModal,
                "skip-opening-animation"
            )
            && config.SkipOpeningAnimation == skipOpeningBeforeClick;
        bool performanceOpenedByClick = settingsOpenedByClick
            && ClickAtlasControlForAutomatedTest(settingsModal, "performance-open")
            && performanceModalOpen;
        if (!config.PerformanceLightingEnabled)
        {
            OnPerformanceLightingToggled(true);
        }
        bool flatLightingSelected = performanceOpenedByClick
            && ClickAtlasControlForAutomatedTest(
                performanceModal,
                "performance-lighting"
            )
            && !config.PerformanceLightingEnabled;
        if (config.HideVegetation)
        {
            OnHideVegetationToggled(false);
        }
        bool vegetationSelected = performanceOpenedByClick
            && ClickAtlasControlForAutomatedTest(
                performanceModal,
                "hide-vegetation"
            )
            && config.HideVegetation;
        automatedSmokeTestPerformanceModeSelected =
            flatLightingSelected
            && vegetationSelected
            && performanceModal?.GetElement("performance-lighting")
                is GuiElementAtlasSwitch;
        if (automatedSmokeTestPerformanceModeSelected)
        {
            // The next frame must render the selected visual controls before
            // the screenshot/modal exercise changes presentation again.
            lastAtlasWorldRenderMilliseconds = 0;
        }
        bool performanceClosedByClick = performanceOpenedByClick
            && ClickAtlasControlForAutomatedTest(
                performanceModal,
                "performance-back"
            )
            && settingsModalOpen
            && !performanceModalOpen;
        bool presentationSwitchAvailable = performanceClosedByClick
            && settingsModal?.GetElement("render-on-scroll")
                is GuiElementAtlasSwitch;
        bool liveLightingBeforeSliderTest = config.LiveLightingEnabled;
        bool fixedLightingPrepared = !liveLightingBeforeSliderTest
            || (settingsOpenedByClick
                && ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "live-lighting"
                )
                && !config.LiveLightingEnabled);
        int fixedSunHourBeforeDrag = config.FixedSunHour;
        bool dragSliderToMaximum = fixedSunHourBeforeDrag != 23;
        bool sliderBoundaryDragHandled = fixedLightingPrepared
            && DragAtlasSliderBeyondBoundsForAutomatedTest(
                settingsModal,
                "fixed-sun-hour",
                dragSliderToMaximum
            );
        bool sliderBoundaryDragClamped = config.FixedSunHour
            == (dragSliderToMaximum ? 23 : 0);
        OnFixedSunHourChanged(fixedSunHourBeforeDrag);
        settingsModal?.GetAtlasSlider("fixed-sun-hour")?.SetValue(fixedSunHourBeforeDrag);
        bool liveLightingRestored = !liveLightingBeforeSliderTest
            || (ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "live-lighting"
                )
                && config.LiveLightingEnabled);
        OnHandheldInstrumentChoiceChanged("compass", true);
        bool timeInstrumentSelected = ClickAtlasControlForAutomatedTest(
                settingsModal,
                "handheld-instrument"
            )
            && config.ShowPlayerCompass
            && HandheldInstrumentMode == "time";
        if (automatedSmokeTestPerformanceModeSelected)
        {
            // Live/fixed lighting controls intentionally re-enable directional
            // lighting. Re-apply the already-tested developer visual choice so
            // one real atlas frame observes flat lighting and hidden vegetation
            // before the smoke test restores the player's preferences.
            OnPerformanceLightingToggled(false);
            OnHideVegetationToggled(true);
            lastAtlasWorldRenderMilliseconds = 0;
        }
        bool settingsClosedByClick = settingsOpenedByClick
            && ClickAtlasControlForAutomatedTest(settingsModal, "settings-close")
            && !settingsModalOpen;
        // The screenshot panel lives in the main atlas UI under the modals.
        // Its clicks are only valid while the settings modal is closed, and
        // the collapsed panel must expand first. The toggle defers its own
        // recomposition, so the exercise applies it synchronously.
        bool screenshotToggleClicked = settingsClosedByClick
            && ClickAtlasControlForAutomatedTest(screenshotPanel, "shot-toggle")
            && ScreenshotPanelExpanded;
        if (screenshotToggleClicked)
        {
            pendingScreenshotPanelRecompose = false;
            RecomposeScreenshotPanel();
        }
        bool screenshotPanelExpanded = screenshotToggleClicked
            && screenshotPanel?.GetElement("shot-scale") is GuiElementAtlasChoice;
        bool screenshotAreaControlPresent = screenshotPanelExpanded
            && screenshotPanel?.GetElement("shot-area") is GuiElementAtlasChoice
            && screenshotPanel?.GetElement("shot-preview") != null;
        int screenshotScaleBefore = config.ScreenshotScale;
        bool screenshotScaleChanged = false;
        if (screenshotPanelExpanded
            && ClickAtlasControlForAutomatedTest(screenshotPanel, "shot-scale"))
        {
            // The dropdown list itself cannot be driven without real mouse
            // input; invoke its handler the same way the player's selection
            // would, then restore the original value.
            OnScreenshotScaleChanged(
                screenshotScaleBefore == 4 ? "1" : "4",
                true
            );
            screenshotScaleChanged = config.ScreenshotScale != screenshotScaleBefore;
            OnScreenshotScaleChanged(screenshotScaleBefore.ToString(), true);
        }
        bool screenshotScaleRestored = screenshotScaleChanged
            && config.ScreenshotScale == screenshotScaleBefore;
        int screenshotAreaBefore =
            AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
                config.ScreenshotCaptureAreaPercent
            );
        bool screenshotAreaChanged = false;
        if (screenshotAreaControlPresent
            && ClickAtlasControlForAutomatedTest(screenshotPanel, "shot-area"))
        {
            int alternateArea = screenshotAreaBefore == 25 ? 100 : 25;
            OnScreenshotCaptureAreaChanged(alternateArea.ToString(), true);
            screenshotAreaChanged = config.ScreenshotCaptureAreaPercent
                == alternateArea;
            OnScreenshotCaptureAreaChanged(screenshotAreaBefore.ToString(), true);
        }
        bool screenshotAreaRestored = screenshotAreaChanged
            && config.ScreenshotCaptureAreaPercent == screenshotAreaBefore;
        bool screenshotPreviewRangePassed = screenshotAreaControlPresent
            && ValidateScreenshotPreviewRange();
        // Collapse and re-expand the map-layer panel. The collapsed panel
        // lacks the dropdown and status elements, which previously crashed
        // the per-frame status updates.
        bool mapLayerToggleClicked = settingsClosedByClick
            && ClickAtlasControlForAutomatedTest(mapLayerPanel, "map-layer-toggle")
            && mapLayerPanelCollapsed;
        if (mapLayerToggleClicked)
        {
            pendingMapLayerPanelRecompose = false;
            RecomposeMapLayerPanel();
        }
        bool mapLayerCollapsedSafe = mapLayerToggleClicked
            && mapLayerPanel?.GetElement("map-layer") == null;
        bool mapLayerReExpanded = mapLayerCollapsedSafe
            && ClickAtlasControlForAutomatedTest(mapLayerPanel, "map-layer-toggle")
            && !mapLayerPanelCollapsed;
        if (mapLayerReExpanded)
        {
            pendingMapLayerPanelRecompose = false;
            RecomposeMapLayerPanel();
        }
        mapLayerReExpanded = mapLayerReExpanded
            && mapLayerPanel?.GetElement("map-layer") is GuiElementDropDown;
        bool creativeOpenedByClick = accessAvailable
            && ClickAtlasControlForAutomatedTest(
                creativeSettingsShortcut,
                "creative-settings-button"
            )
            && creativeSettingsModalOpen;
        bool creativeClosedByClick = creativeOpenedByClick
            && ClickAtlasControlForAutomatedTest(
                creativeSettingsModal,
                "creative-settings-close"
            )
            && !creativeSettingsModalOpen;

        float originalYaw = targetYawDegrees;
        float originalPitch = targetPitchDegrees;
        config.CameraAngleLocked = true;
        ApplyRotationDrag(8, 24);
        bool cameraAngleStayedLocked = Math.Abs(targetPitchDegrees - originalPitch) < 0.001f;
        bool cameraYawStayedFree = Math.Abs(
            NormalizeSignedDegrees(targetYawDegrees - originalYaw)
        ) > 0.001f;
        targetYawDegrees = originalYaw;
        targetPitchDegrees = originalPitch;
        config.CameraAngleLocked = false;

        bool safeSurfaceWasActive = SurfaceSafetyEnabled;
        config.CaveModeEnabled = true;
        bool caveModeActivated = !SurfaceSafetyEnabled;
        PrepareSurfaceSafetyFilter();

        bool hidden = ClickAtlasControlForAutomatedTest(overlay, "hide-ui-button")
            && interfaceHidden;
        // Only press Escape when the UI really is hidden. Otherwise this
        // destructive call could close the atlas or an open modal instead of
        // restoring the interface.
        bool restored = hidden && HandleEscape() && !interfaceHidden && IsOpened();
        bool neumorphicControls = overlay?.GetElement("settings-button")
                is GuiElementAtlasButton
            && settingsModal?.GetElement("map-layers") is GuiElementAtlasSwitch
            && settingsModal?.GetElement("skip-opening-animation")
                is GuiElementAtlasSwitch
            && settingsModal?.GetElement("scroll-realtime-weather")
                is GuiElementAtlasSwitch
            && settingsModal?.GetElement("performance-open")
                is GuiElementAtlasButton
            && settingsModal?.GetElement("handheld-instrument")
                is GuiElementAtlasChoice
            && performanceModal?.GetElement("hide-vegetation")
                is GuiElementAtlasSwitch
            && creativeSettingsModal?.GetElement("camera-angle-lock")
                is GuiElementAtlasSwitch
            && screenshotPanel?.GetElement("shot-scale")
                is GuiElementAtlasChoice
            && screenshotPanel?.GetElement("shot-area")
                is GuiElementAtlasChoice
            && screenshotPanel?.GetElement("shot-preview") != null
            && screenshotPanel?.GetElement("shot-take")
                is GuiElementAtlasButton
            && mapLayerPanel?.GetElement("map-layer") is GuiElementDropDown;
        int renderedEntityCount = exactChunkRenderer?.LastRenderedEntityCount ?? 0;
        bool heldItemsSuppressed = renderedEntityCount > 0
            && exactChunkRenderer?.LastSuppressedHeldItemCount == renderedEntityCount;
        automatedSmokeTestInterfaceControlsPassed = accessAvailable
            && settingsOpenedByClick
            && settingsClosedByToggle
            && settingsReopenedByToggle
            && settingsSwitchClicked
            && settingsSwitchRestored
            && skipOpeningClicked
            && skipOpeningRestored
            && automatedSmokeTestPerformanceModeSelected
            && performanceClosedByClick
            && presentationSwitchAvailable
            && fixedLightingPrepared
            && sliderBoundaryDragHandled
            && sliderBoundaryDragClamped
            && liveLightingRestored
            && timeInstrumentSelected
            && screenshotPanelExpanded
            && screenshotScaleChanged
            && screenshotScaleRestored
            && screenshotAreaChanged
            && screenshotAreaRestored
            && screenshotPreviewRangePassed
            && mapLayerToggleClicked
            && mapLayerCollapsedSafe
            && mapLayerReExpanded
            && settingsClosedByClick
            && creativeOpenedByClick
            && creativeClosedByClick
            && MapLayerControlsVisible
            && SearchModeActive
            && safeSurfaceWasActive
            && caveModeActivated
            && cameraAngleStayedLocked
            && cameraYawStayedFree
            && hidden
            && restored
            && neumorphicControls
            && heldItemsSuppressed;
        if (automatedSmokeTestInterfaceControlsPassed)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test exercised the toggleable Settings panel, compact neumorphic controls, Compass/Time instrument selector, map-layer and skip-opening switches, Creative/Cheat cave/search/camera controls, the independent Screenshot resolution and capture-area selectors with 2x-8x preview validation and a queued tiled capture, Escape-restored hidden UI and held-item suppression for {0} living models; debounced presentation coverage is running next.",
                renderedEntityCount
            );
        }
        else
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated interface-controls test failed: access={0}, settingsOpen/toggle/reopen={1}/{2}/{3}, mapLayerSwitch={4}/{5}, skipOpening={6}/{7}, presentationControl={8}, fixedLighting={9}/{10}, sliderBoundary={11}/{12}, timeInstrument={13}, screenshotPanel={14}, screenshotScale={15}/{16}, screenshotArea={17}/{18}, screenshotPreviewRange={19}, mapLayerToggle={20}/{21}/{22}, settingsClose={23}, creativeOpen={24}, creativeClose={25}, layers={26}, search={27}, safeSurface={28}, cave={29}, angleLock={30}, yaw={31}, hidden={32}, restored={33}, neumorphic={34}, heldItems={35}/{36}.",
                accessAvailable,
                settingsOpenedByClick,
                settingsClosedByToggle,
                settingsReopenedByToggle,
                settingsSwitchClicked,
                settingsSwitchRestored,
                skipOpeningClicked,
                skipOpeningRestored,
                presentationSwitchAvailable,
                fixedLightingPrepared,
                liveLightingRestored,
                sliderBoundaryDragHandled,
                sliderBoundaryDragClamped,
                timeInstrumentSelected,
                screenshotPanelExpanded,
                screenshotScaleChanged,
                screenshotScaleRestored,
                screenshotAreaChanged,
                screenshotAreaRestored,
                screenshotPreviewRangePassed,
                mapLayerToggleClicked,
                mapLayerCollapsedSafe,
                mapLayerReExpanded,
                settingsClosedByClick,
                creativeOpenedByClick,
                creativeClosedByClick,
                MapLayerControlsVisible,
                SearchModeActive,
                safeSurfaceWasActive,
                caveModeActivated,
                cameraAngleStayedLocked,
                cameraYawStayedFree,
                hidden,
                restored,
                neumorphicControls,
                exactChunkRenderer?.LastSuppressedHeldItemCount ?? 0,
                renderedEntityCount
            );
        }
    }
#endif
    }

    private bool ValidateScreenshotPreviewRange()
    {
        AtlasViewportBounds viewport = AtlasViewport;
        float viewportAspect = viewport.Width
            / (float)Math.Max(1, viewport.Height);
        int[] captureAreas = { 100, 75, 50, 25 };
        int previewCount = 0;
        int limitedCount = 0;
        bool passed = true;
        for (
            int resolutionScale = 2;
            resolutionScale <= AtlasTiledScreenshot.MaximumResolutionScale;
            resolutionScale++
        )
        {
            foreach (int captureAreaPercent in captureAreas)
            {
                AtlasScreenshotPreview preview = tileScreenshot.GetPreview(
                    resolutionScale,
                    captureAreaPercent,
                    viewportAspect
                );
                float expectedDetail = resolutionScale
                    * (100f / captureAreaPercent);
                passed = passed
                    && preview.IsValid
                    && preview.OutputWidth > 0
                    && preview.OutputHeight > 0
                    && preview.OutputPixels
                        <= AtlasTiledScreenshot.MaximumStitchedPixels
                    && Math.Abs(
                        preview.RequestedDetailFactor - expectedDetail
                    ) < 0.001f;
                previewCount++;
                if (preview.WasDownsampled) limitedCount++;
            }
        }

        if (passed)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated screenshot preview range passed for {0} combinations (2x-8x resolution, 100/75/50/25% capture area); {1} combinations are explicitly marked as pixel-budget limited.",
                previewCount,
                limitedCount
            );
        }
        else
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated screenshot preview range failed for one or more of {0} resolution/area combinations.",
                previewCount
            );
        }
        return passed;
    }

    private static bool IsEnvironmentFlagEnabled(string variableName)
    {
        string? value = Environment.GetEnvironmentVariable(variableName);
        return value == "1"
            || value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
            || value?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
    }

    private HashSet<string> CapturePublicScreenshotEntries()
    {
        string folder = capi.GetOrCreateDataPath(
            System.IO.Path.Combine("Screenshots", "ModernAtlas")
        );
        HashSet<string> entries = new(StringComparer.Ordinal);
        if (!Directory.Exists(folder)) return entries;
        foreach (string entry in Directory.GetFileSystemEntries(folder))
        {
            entries.Add(System.IO.Path.GetFullPath(entry));
        }
        return entries;
    }

    private bool ValidateAutomatedScreenshotOutputs(out string diagnostic)
    {
        int expectedCount = automatedScreenshotSequenceEnabled ? 2 : 1;
        List<string> outputPaths = new();
        if (automatedFirstScreenshotPath != null)
        {
            outputPaths.Add(automatedFirstScreenshotPath);
        }
        if (automatedScreenshotSequenceEnabled
            && automatedSecondScreenshotPath != null)
        {
            outputPaths.Add(automatedSecondScreenshotPath);
        }
        if (!automatedScreenshotSequenceEnabled
            && tileScreenshot.LastSavedPath != null)
        {
            outputPaths.Clear();
            outputPaths.Add(tileScreenshot.LastSavedPath);
        }

        HashSet<string> uniquePaths = new(StringComparer.Ordinal);
        foreach (string path in outputPaths)
        {
            if (!uniquePaths.Add(System.IO.Path.GetFullPath(path)))
            {
                diagnostic = "Two screenshot jobs resolved to the same output path.";
                return false;
            }
            if (!File.Exists(path))
            {
                diagnostic = $"Committed PNG is missing: {path}";
                return false;
            }
            try
            {
                AtlasTiledScreenshot.ValidateCommittedPngForAutomation(path);
            }
            catch (Exception exception)
            {
                diagnostic = $"PNG cannot be read completely ({path}): {exception.Message}";
                return false;
            }
        }
        if (outputPaths.Count != expectedCount)
        {
            diagnostic =
                $"Expected {expectedCount} committed PNG files, observed {outputPaths.Count}.";
            return false;
        }

        HashSet<string> before = automatedScreenshotPublicEntriesBefore
            ?? new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> after = CapturePublicScreenshotEntries();
        List<string> newEntries = new();
        foreach (string entry in after)
        {
            if (!before.Contains(entry)) newEntries.Add(entry);
        }
        int newPngCount = 0;
        foreach (string entry in newEntries)
        {
            if (Directory.Exists(entry))
            {
                diagnostic = $"A new directory appeared in the public folder: {entry}";
                return false;
            }
            if (!entry.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                diagnostic = $"A new public sidecar appeared: {entry}";
                return false;
            }
            newPngCount++;
        }
        if (newPngCount != expectedCount)
        {
            diagnostic =
                $"Expected exactly {expectedCount} new public PNG files, observed {newPngCount}.";
            return false;
        }

        diagnostic =
            $"{expectedCount} unique PNG files are readable and no new public sidecars were created.";
        capi.Logger.Notification(
            "[ModernAtlas] Automated screenshot output validation passed: {0}",
            diagnostic
        );
        return true;
    }

    private bool ValidateCancelledScreenshotOutputs()
    {
        HashSet<string> before = automatedScreenshotPublicEntriesBefore
            ?? new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> after = CapturePublicScreenshotEntries();
        if (before.Count != after.Count) return false;
        foreach (string entry in before)
        {
            if (!after.Contains(entry)) return false;
        }
        HashSet<string> beforeJobs = automatedScreenshotJobEntriesBefore
            ?? new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> afterJobs = CaptureScreenshotJobEntries();
        if (beforeJobs.Count != afterJobs.Count)
        {
            return false;
        }
        foreach (string entry in beforeJobs)
        {
            if (!afterJobs.Contains(entry)) return false;
        }
        return tileScreenshot.State == AtlasScreenshotJobState.Cancelled;
    }

    private HashSet<string> CaptureScreenshotJobEntries()
    {
        string jobRoot = capi.GetOrCreateDataPath(
            System.IO.Path.Combine(
                "ModData",
                "ModernAtlas",
                "ScreenshotJobs"
            )
        );
        HashSet<string> entries = new(StringComparer.Ordinal);
        if (!Directory.Exists(jobRoot)) return entries;
        foreach (string entry in Directory.GetFileSystemEntries(jobRoot))
        {
            entries.Add(System.IO.Path.GetFullPath(entry));
        }
        return entries;
    }

    private void AdvanceAutomatedPresentationSwitchTest()
    {
        if (!automatedSmokeTestActive
            || !automatedSmokeTestInterfaceControlsAttempted
            || !automatedSmokeTestInterfaceControlsPassed
            || automatedSmokeScreenshotPhase < AutomatedUiScreenshotPhaseCount
            || automatedSmokeTestPresentationPassed
            || automatedSmokeTestPresentationPhase < 0
            || automatedSmokeTestPresentationPhase >= 5)
        {
            return;
        }

        switch (automatedSmokeTestPresentationPhase)
        {
            case 0:
            {
                if (!settingsModalOpen && bottomPanelAnimationActive)
                {
                    // The UI screenshot sequence ends by requesting Settings
                    // from the Visual Lab. The shared-panel state machine
                    // must finish that close-then-open transition before the
                    // debounce exercise starts; a valid queued request is not
                    // an error just because the logical Settings flag is
                    // still false for one or two render frames.
                    if (BottomPanelClosing
                        && queuedBottomPanelSection == AtlasPanelSection.Settings)
                    {
                        return;
                    }
                    return;
                }

                if (!settingsModalOpen
                    && (!ClickAtlasControlForAutomatedTest(
                            overlay,
                            "settings-button"
                        )
                        || !settingsModalOpen))
                {
                    FailAutomatedPresentationSwitchTest(
                        "could not open Settings before the first rapid sequence"
                    );
                    return;
                }

                automatedSmokeTestSettingsComposer = settingsModal;
                automatedSmokeTestPerformanceComposer = performanceModal;
                automatedSmokeTestCreativeComposer = creativeSettingsModal;
                automatedSmokeTestVisualLabComposer = visualLabModal;
                bool startedOnScroll = config.RenderOnScroll;
                bool firstClick = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "render-on-scroll"
                );
                bool secondClick = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "render-on-scroll"
                );
                bool queuedScrollFullscreenScroll = startedOnScroll
                    && firstClick
                    && secondClick
                    && config.RenderOnScroll
                    && presentationChangeCoordinator.HasPending
                    && presentationChangeCoordinator.RequestedRenderOnScroll;
                bool pendingFrameCaptured = queuedScrollFullscreenScroll
                    && CaptureAutomatedPresentationFrame(
                        "presentation-scroll-fullscreen-scroll-pending"
                    );
                if (!pendingFrameCaptured)
                {
                    FailAutomatedPresentationSwitchTest(
                        "Scroll -> Fullscreen -> Scroll did not retain the committed scroll view while pending"
                    );
                    return;
                }

                automatedSmokeTestPresentationPhase = 1;
                return;
            }

            case 1:
            {
                if (presentationChangeCoordinator.HasPending) return;

                bool settingsComposerKept = ReferenceEquals(
                    settingsModal,
                    automatedSmokeTestSettingsComposer
                );
                bool scrollCommittedState = config.RenderOnScroll
                    && settingsModalOpen
                    && settingsComposerKept;
                bool scrollCommitted = scrollCommittedState
                    && CaptureAutomatedPresentationFrame(
                        "presentation-scroll-fullscreen-scroll"
                    );
                if (!scrollCommitted)
                {
                    capi.Logger.Error(
                        "[ModernAtlas] Presentation phase diagnostic: committedScroll={0}, settingsOpen={1}, settingsComposerKept={2}, pending={3}, requestedScroll={4}, screenshot={5}.",
                        config.RenderOnScroll,
                        settingsModalOpen,
                        settingsComposerKept,
                        presentationChangeCoordinator.HasPending,
                        presentationChangeCoordinator.RequestedRenderOnScroll,
                        scrollCommittedState
                    );
                    FailAutomatedPresentationSwitchTest(
                        "the final Scroll choice was not committed after the debounce interval"
                    );
                    return;
                }

                bool fullscreenClick = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "render-on-scroll"
                );
                bool fullscreenQueued = fullscreenClick
                    && config.RenderOnScroll
                    && presentationChangeCoordinator.HasPending
                    && !presentationChangeCoordinator.RequestedRenderOnScroll
                    && CaptureAutomatedPresentationFrame(
                        "presentation-fullscreen-pending"
                    );
                if (!fullscreenQueued)
                {
                    FailAutomatedPresentationSwitchTest(
                        "could not queue the fullscreen presentation without rebuilding Settings"
                    );
                    return;
                }

                automatedSmokeTestPresentationPhase = 2;
                return;
            }

            case 2:
            {
                if (presentationChangeCoordinator.HasPending) return;

                bool fullscreenCommitted = !config.RenderOnScroll
                    && settingsModalOpen
                    && ReferenceEquals(
                        settingsModal,
                        automatedSmokeTestSettingsComposer
                    )
                    && CaptureAutomatedPresentationFrame(
                        "presentation-fullscreen"
                    );
                if (!fullscreenCommitted)
                {
                    FailAutomatedPresentationSwitchTest(
                        "the fullscreen presentation did not replace the scroll viewport after commit"
                    );
                    return;
                }

                bool firstReturnToScroll = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "render-on-scroll"
                );
                bool secondReturnToScroll = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "render-on-scroll"
                );
                bool queuedFullscreenScrollFullscreen = !config.RenderOnScroll
                    && firstReturnToScroll
                    && secondReturnToScroll
                    && presentationChangeCoordinator.HasPending
                    && !presentationChangeCoordinator.RequestedRenderOnScroll
                    && CaptureAutomatedPresentationFrame(
                        "presentation-fullscreen-scroll-fullscreen-pending"
                    );
                if (!queuedFullscreenScrollFullscreen)
                {
                    FailAutomatedPresentationSwitchTest(
                        "Fullscreen -> Scroll -> Fullscreen did not keep the latest fullscreen request pending"
                    );
                    return;
                }

                automatedSmokeTestPresentationPhase = 3;
                return;
            }

            case 3:
            {
                if (presentationChangeCoordinator.HasPending) return;

                bool finalFullscreen = !config.RenderOnScroll
                    && settingsModalOpen
                    && ReferenceEquals(
                        settingsModal,
                        automatedSmokeTestSettingsComposer
                    )
                    && ReferenceEquals(
                        performanceModal,
                        automatedSmokeTestPerformanceComposer
                    )
                    && ReferenceEquals(
                        creativeSettingsModal,
                        automatedSmokeTestCreativeComposer
                    )
                    && ReferenceEquals(
                        visualLabModal,
                        automatedSmokeTestVisualLabComposer
                    )
                    && CaptureAutomatedPresentationFrame(
                        "presentation-fullscreen-scroll-fullscreen"
                    );
                bool weatherBefore = config.ScrollRealtimeWeatherEnabled;
                bool weatherToggled = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "scroll-realtime-weather"
                )
                    && config.ScrollRealtimeWeatherEnabled != weatherBefore;
                bool weatherRestored = weatherToggled
                    && ClickAtlasControlForAutomatedTest(
                        settingsModal,
                        "scroll-realtime-weather"
                    )
                    && config.ScrollRealtimeWeatherEnabled == weatherBefore;
                bool otherBefore = config.SkipOpeningAnimation;
                bool otherToggled = ClickAtlasControlForAutomatedTest(
                    settingsModal,
                    "skip-opening-animation"
                )
                    && config.SkipOpeningAnimation != otherBefore;
                bool otherRestored = otherToggled
                    && ClickAtlasControlForAutomatedTest(
                        settingsModal,
                        "skip-opening-animation"
                    )
                    && config.SkipOpeningAnimation == otherBefore;
                bool settingsStillActive = settingsModalOpen
                    && ReferenceEquals(
                        settingsModal,
                        automatedSmokeTestSettingsComposer
                    );
                bool closed = settingsStillActive
                    && ClickAtlasControlForAutomatedTest(
                        settingsModal,
                        "settings-close"
                    )
                    && !settingsModalOpen;

                automatedSmokeTestPresentationPassed = finalFullscreen
                    && weatherToggled
                    && weatherRestored
                    && otherToggled
                    && otherRestored
                    && settingsStillActive
                    && closed;
                automatedSmokeTestPresentationPhase = 5;
                if (automatedSmokeTestPresentationPassed)
                {
                    capi.Logger.Notification(
                        "[ModernAtlas] Automated presentation debounce check passed: Scroll -> Fullscreen -> Scroll and Fullscreen -> Scroll -> Fullscreen kept the last choice, retained Settings/modal composers, and produced opaque transition frames."
                    );
                }
                else
                {
                    capi.Logger.Error(
                        "[ModernAtlas] Automated presentation debounce check failed: finalFullscreen={0}, weather={1}/{2}, other={3}/{4}, settingsActive={5}, closed={6}.",
                        finalFullscreen,
                        weatherToggled,
                        weatherRestored,
                        otherToggled,
                        otherRestored,
                        settingsStillActive,
                        closed
                    );
                }
                return;
            }
        }
    }

    private void FailAutomatedPresentationSwitchTest(string diagnostic)
    {
        automatedSmokeTestPresentationPhase = -1;
        automatedSmokeTestPresentationPassed = false;
        capi.Logger.Error(
            "[ModernAtlas] Automated presentation debounce check failed: {0}.",
            diagnostic
        );
    }

    private bool CaptureAutomatedPresentationFrame(string suffix)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(
            SmokeScreenshotEnvironmentVariable
        );
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return ValidateAutomatedSmokeWindowAlpha();
        }

        string prefix = configuredPath.EndsWith(
            ".png",
            StringComparison.OrdinalIgnoreCase
        )
            ? configuredPath[..^4]
            : configuredPath;
        return TrySaveAutomatedSmokeScreenshot($"{prefix}-{suffix}.png");
    }

    private bool ClickAtlasControlForAutomatedTest(GuiComposer? composer, string key)
    {
        if (composer == null) return false;
        GuiElement? element = composer.GetElement(key);
        if (element == null) return false;

        int x = (int)Math.Round(element.Bounds.absX + element.Bounds.OuterWidth * 0.5);
        int y = (int)Math.Round(element.Bounds.absY + element.Bounds.OuterHeight * 0.5);
        MouseEvent down = new(x, y, EnumMouseButton.Left, 0);
        // Exercise the same composer-owned press/release path as a real
        // dialog click. Calling the outer dialog dispatcher here lets the
        // full-screen overlay compete with a panel control at the same
        // coordinate, which can drop a valid Settings button callback.
        composer.OnMouseDown(down);
        MouseEvent up = new(x, y, EnumMouseButton.Left, 0);
        composer.OnMouseUp(up);
        return down.Handled && up.Handled;
    }

    private bool DragAtlasSliderBeyondBoundsForAutomatedTest(
        GuiComposer? composer,
        string key,
        bool towardMaximum
    )
    {
        GuiElementAtlasSlider? slider = composer?.GetElement(key) as GuiElementAtlasSlider;
        if (slider == null) return false;

        int startX = (int)Math.Round(slider.Bounds.absX + slider.Bounds.OuterWidth * 0.5);
        int startY = (int)Math.Round(slider.Bounds.absY + slider.Bounds.OuterHeight * 0.5);
        int endX = (int)Math.Round(
            towardMaximum
                ? slider.Bounds.absX + slider.Bounds.OuterWidth + 160
                : slider.Bounds.absX - 160
        );
        int endY = (int)Math.Round(slider.Bounds.absY - slider.Bounds.OuterHeight - 80);

        MouseEvent down = new(startX, startY, EnumMouseButton.Left, 0);
        OnMouseDown(down);
        MouseEvent move = new(endX, endY, endX - startX, endY - startY);
        OnMouseMove(move);
        MouseEvent up = new(endX, endY, EnumMouseButton.Left, 0);
        OnMouseUp(up);
        return down.Handled && move.Handled && up.Handled;
    }

    private void CaptureAutomatedSmokeScreenshot()
    {
        if (!automatedSmokeTestActive
            || !AutomatedSmokeTestRenderedExactWorld)
        {
            return;
        }

        // Leave the live atlas undisturbed long enough to validate a stable
        // post-opening frame instead of capturing the first transient draw.
        if (!automatedSmokeTestSafeSurfaceScreenshotHandled
            && automatedSmokeTestElapsedSeconds < 2f)
        {
            return;
        }

        string? configuredPath = Environment.GetEnvironmentVariable(
            SmokeScreenshotEnvironmentVariable
        );
        if (pendingAutomatedMapLayerScreenshotSuffix != null)
        {
            string layerSuffix = pendingAutomatedMapLayerScreenshotSuffix;
            pendingAutomatedMapLayerScreenshotSuffix = null;
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string layerPrefix = configuredPath.EndsWith(
                    ".png",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? configuredPath[..^4]
                    : configuredPath;
                TrySaveAutomatedSmokeScreenshot($"{layerPrefix}-{layerSuffix}.png");
            }
            return;
        }
        if (!automatedSmokeTestSafeSurfaceScreenshotHandled)
        {
            automatedSmokeTestSafeSurfaceScreenshotHandled = true;
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string safePrefix = configuredPath.EndsWith(
                    ".png",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? configuredPath[..^4]
                    : configuredPath;
                TrySaveAutomatedSmokeScreenshot(
                    $"{safePrefix}-survival-safe.png"
                );
            }
            automatedSmokeTestZoomBeforeMaximum = targetZoom;
            automatedSmokeTestPitchBeforeMaximum = targetPitchDegrees;
            targetPitchDegrees = 72;
            pitchDegrees = 72;
            zoom = targetZoom;
            automatedSmokeTestBorderTopDownStartedSeconds =
                automatedSmokeTestElapsedSeconds;
            automatedSmokeTestBorderTopDownPending = true;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test is holding a fitted top-down standard-world view for border inspection before layers and Cave Mode."
            );
            return;
        }

        if (automatedSmokeTestBorderTopDownPending)
        {
            if (automatedSmokeTestElapsedSeconds
                - automatedSmokeTestBorderTopDownStartedSeconds
                < MaximumZoomScreenshotSettleSeconds)
            {
                return;
            }

            automatedSmokeTestBorderTopDownPending = false;
            automatedSmokeTestBorderTopDownFrameRendered = true;
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string borderPrefix = configuredPath.EndsWith(
                    ".png",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? configuredPath[..^4]
                    : configuredPath;
                automatedSmokeTestBorderTopDownFrameRendered =
                    TrySaveAutomatedSmokeScreenshot(
                        $"{borderPrefix}-border-topdown.png"
                    );
            }
            ApplyZoomWheel(1, false);
            ApplyZoomWheel(1, false);
            // The automated check validates a settled zoom level. Snap only
            // its synthetic wheel input to the target so live world frames
            // and newly completed chunk meshes cannot make the two-second
            // capture race camera interpolation.
            zoom = targetZoom;
            automatedSmokeTestPartialZoomStartedSeconds =
                automatedSmokeTestElapsedSeconds;
            automatedSmokeTestPartialZoomPending = true;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test is holding partial scroll zoom for 2 seconds before capture."
            );
            return;
        }

        if (automatedSmokeTestPartialZoomPending)
        {
            if (automatedSmokeTestElapsedSeconds
                - automatedSmokeTestPartialZoomStartedSeconds
                < MaximumZoomScreenshotSettleSeconds)
            {
                return;
            }

            automatedSmokeTestPartialZoomPending = false;
            automatedSmokeTestPartialZoomFrameRendered = config.RenderOnScroll
                && zoom > MaximumZoomIn + 0.001f
                && zoom < automatedSmokeTestZoomBeforeMaximum - 0.001f;
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string partialZoomPrefix = configuredPath.EndsWith(
                    ".png",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? configuredPath[..^4]
                    : configuredPath;
                automatedSmokeTestPartialZoomFrameRendered &=
                    TrySaveAutomatedSmokeScreenshot(
                        $"{partialZoomPrefix}-scroll-partial-zoom.png"
                    );
            }
            targetZoom = MaximumZoomIn;
            zoom = MaximumZoomIn;
            automatedSmokeTestMaximumZoomStartedSeconds =
                automatedSmokeTestElapsedSeconds;
            automatedSmokeTestMaximumZoomPending = true;
            capi.Logger.Notification(
                automatedSmokeTestPartialZoomFrameRendered
                    ? "[ModernAtlas] Automated partial scroll zoom capture passed; holding maximum zoom for 2 seconds."
                    : "[ModernAtlas] Automated partial scroll zoom capture failed; holding maximum zoom for 2 seconds."
            );
            return;
        }

        if (automatedSmokeTestMaximumZoomPending)
        {
            if (automatedSmokeTestElapsedSeconds
                - automatedSmokeTestMaximumZoomStartedSeconds
                < MaximumZoomScreenshotSettleSeconds)
            {
                return;
            }

            automatedSmokeTestMaximumZoomPending = false;
            // Streaming continues while the atlas is open, so a newly
            // completed edge mesh can make a frame more expensive and leave
            // the smoothed value a fraction behind its already clamped
            // target. Capture the exact target used by real wheel input.
            zoom = targetZoom;
            automatedSmokeTestMaximumZoomFrameRendered = config.RenderOnScroll
                && Math.Abs(targetZoom - MaximumZoomIn) < 0.001f;
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string maximumZoomPrefix = configuredPath.EndsWith(
                    ".png",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? configuredPath[..^4]
                    : configuredPath;
                automatedSmokeTestMaximumZoomFrameRendered &=
                    TrySaveAutomatedSmokeScreenshot(
                        $"{maximumZoomPrefix}-scroll-maximum-zoom.png"
                    );
            }
            targetZoom = automatedSmokeTestZoomBeforeMaximum;
            zoom = automatedSmokeTestZoomBeforeMaximum;
            targetPitchDegrees = automatedSmokeTestPitchBeforeMaximum;
            pitchDegrees = automatedSmokeTestPitchBeforeMaximum;
            if (automatedSmokeTestMaximumZoomFrameRendered)
            {
                capi.Logger.Notification(
                    "[ModernAtlas] Automated scroll clipping check passed at maximum zoom."
                );
            }
            else
            {
                capi.Logger.Error(
                    "[ModernAtlas] Automated scroll clipping check failed at maximum zoom."
                );
            }
            if (automatedSmokeTestRequiresUnlockedPitch)
            {
                targetPitchDegrees = UnlockedMinimumPitchDegrees;
                pitchDegrees = UnlockedMinimumPitchDegrees;
                FitLoadedTerrain();
                zoom = targetZoom;
                capi.Logger.Notification(
                    "[ModernAtlas] Automated smoke test is now exercising the unlocked 0-degree camera pitch."
                );
            }
            return;
        }

        if (!automatedSmokeTestInterfaceControlsPassed
            || automatedSmokeScreenshotPhase >= AutomatedUiScreenshotPhaseCount)
        {
            return;
        }
        if (automatedSmokeScreenshotPhase == 0 && bottomPanel != null)
        {
            // The unit-inspection exercise intentionally opens its own panel.
            // The first comparison frame is the neutral atlas view, so close
            // that panel before saving the navbar-only screenshot. Return and
            // wait one complete render frame so the old panel cannot remain in
            // the framebuffer captured below.
            ResetBottomPanelState();
            return;
        }
        // Wait for the shared bottom panel to settle before storing each UI
        // comparison frame. This keeps the screenshot sequence deterministic
        // while the real UI still uses the short render-time slide animation.
        if (bottomPanelAnimationActive)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            // Screenshot files are optional. Complete the comparison
            // sequence when no output prefix was requested so the later
            // presentation and tiled-capture checks are not blocked forever.
            automatedSmokeScreenshotPhase = AutomatedUiScreenshotPhaseCount;
            return;
        }

        string prefix = configuredPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? configuredPath[..^4]
            : configuredPath;
        string suffix = automatedSmokeScreenshotPhase switch
        {
            0 => "atlas",
            1 => "settings",
            2 => "screenshot-options",
            3 => "map-options",
            4 => "search",
            5 => "instrument",
            6 => "performance",
            7 => "creative",
            _ => "visual-lab"
        };
        string path = $"{prefix}-{suffix}.png";
        if (!TrySaveAutomatedSmokeScreenshot(path))
        {
            automatedSmokeScreenshotPhase = 5;
            return;
        }

        automatedSmokeScreenshotPhase++;
        switch (automatedSmokeScreenshotPhase)
        {
            case 1:
                OpenSettingsModal();
                break;
            case 2:
                ToggleScreenshotOptions();
                break;
            case 3:
                ToggleMapOptions();
                break;
            case 4:
                ToggleSearchPanel();
                break;
            case 5:
                ToggleInstrumentPanel();
                break;
            case 6:
                OpenPerformanceModal();
                break;
            case 7:
                OpenCreativeSettingsModal();
                break;
            case 8:
                OpenVisualLab();
                break;
            default:
                CloseVisualLab();
                break;
        }
    }

    private bool ValidateAutomatedSmokeWindowAlpha()
    {
        try
        {
            using BitmapRef screenshot = capi.Render.GrabScreenshot(
                capi.Render.FrameWidth,
                capi.Render.FrameHeight,
                false,
                true,
                true
            );
            int minimumAlpha = 255;
            foreach (int pixel in screenshot.Pixels)
            {
                minimumAlpha = Math.Min(minimumAlpha, (pixel >> 24) & 0xff);
            }
            if (minimumAlpha < 255)
            {
                capi.Logger.Error(
                    "[ModernAtlas] Automated presentation frame retained transparent window pixels: minimum alpha {0}.",
                    minimumAlpha
                );
                return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated presentation alpha validation failed: {0}",
                exception.Message
            );
            return false;
        }
    }

    private bool TrySaveAutomatedSmokeScreenshot(string path)
    {
        try
        {
            using BitmapRef screenshot = capi.Render.GrabScreenshot(
                capi.Render.FrameWidth,
                capi.Render.FrameHeight,
                false,
                true,
                true
            );
            int minimumAlpha = 255;
            foreach (int pixel in screenshot.Pixels)
            {
                minimumAlpha = Math.Min(minimumAlpha, (pixel >> 24) & 0xff);
            }
            if (minimumAlpha < 255)
            {
                capi.Logger.Error(
                    "[ModernAtlas] Automated atlas UI screenshot retained transparent window pixels: minimum alpha {0}.",
                    minimumAlpha
                );
                return false;
            }
            screenshot.Save(path);
            capi.Logger.Notification(
                "[ModernAtlas] Saved opaque automated atlas UI screenshot: {0}",
                path
            );
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated atlas UI screenshot failed for {0}: {1}",
                path,
                exception.Message
            );
            return false;
        }
    }

    /// <summary>
    /// Captures the ordinary Vintage Story frame while the atlas is closed.
    /// This deliberately does not call <see cref="ForceOpaqueWindowAlpha"/>
    /// or any atlas compositor: the screenshot must observe the real world
    /// handoff after the atlas scope has ended.
    /// </summary>
    internal bool CaptureAutomatedOrdinaryWorldScreenshot(string suffix)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(
            SmokeScreenshotEnvironmentVariable
        );
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return true;
        }

        string prefix = configuredPath.EndsWith(
            ".png",
            StringComparison.OrdinalIgnoreCase
        )
            ? configuredPath[..^4]
            : configuredPath;
        string path = $"{prefix}-{suffix}.png";
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using BitmapRef screenshot = capi.Render.GrabScreenshot(
                capi.Render.FrameWidth,
                capi.Render.FrameHeight,
                false,
                true,
                true
            );
            screenshot.Save(path);
            capi.Logger.Notification(
                "[ModernAtlas] Saved ordinary-world smoke screenshot after atlas state handoff: {0}",
                path
            );
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "[ModernAtlas] Ordinary-world smoke screenshot failed for {0}: {1}",
                path,
                exception.Message
            );
            return false;
        }
    }

    private void RestoreAutomatedSmokeTestPreferences()
    {
        if (!automatedSmokeTestPreferencesCaptured) return;

        presentationChangeCoordinator.Reset(config.RenderOnScroll);

        if (automatedSmokeTestPartialZoomPending
            || automatedSmokeTestMaximumZoomPending
            || automatedSmokeTestBorderTopDownPending)
        {
            targetZoom = automatedSmokeTestZoomBeforeMaximum;
            zoom = automatedSmokeTestZoomBeforeMaximum;
            targetPitchDegrees = automatedSmokeTestPitchBeforeMaximum;
            pitchDegrees = automatedSmokeTestPitchBeforeMaximum;
            automatedSmokeTestPartialZoomPending = false;
            automatedSmokeTestMaximumZoomPending = false;
            automatedSmokeTestBorderTopDownPending = false;
        }

        automatedSmokeTestPreferencesCaptured = false;
        bool accessChanged = cheatModeEnabled != automatedOriginalCheatModeEnabled;
        cheatModeEnabled = automatedOriginalCheatModeEnabled;
        config.ScreenshotScale = automatedOriginalScreenshotScale;
        config.ScreenshotCaptureAreaPercent =
            automatedOriginalScreenshotCaptureAreaPercent;
        SyncScreenshotSettingsControls();
        config.MapLayersEnabled = automatedOriginalMapLayersEnabled;
        config.CaveModeEnabled = automatedOriginalCaveModeEnabled;
        config.SearchModeEnabled = automatedOriginalSearchModeEnabled;
        config.CameraAngleLocked = automatedOriginalCameraAngleLocked;
        config.LiveLightingEnabled = automatedOriginalLiveLightingEnabled;
        config.CloudsEnabled = automatedOriginalCloudsEnabled;
        config.FixedSunHour = automatedOriginalFixedSunHour;
        config.ShowPlayerCompass = automatedOriginalShowPlayerCompass;
        config.HandheldInstrumentMode = automatedOriginalHandheldInstrumentMode;
        config.PerformanceLightingEnabled = automatedOriginalPerformanceLightingEnabled;
        if (accessChanged)
        {
            ClampPitchToAccessLevel(immediate: true);
        }
        config.HideVegetation = automatedOriginalHideVegetation;
        if (config.RenderOnScroll != automatedOriginalRenderOnScroll)
        {
            config.RenderOnScroll = automatedOriginalRenderOnScroll;
            pendingInterfaceRecompose = true;
        }
        screenshotPanelCollapsed = true;
        mapLayerPanelCollapsed = false;
        if (!SearchModeActive)
        {
            ClearSearch();
        }
        if (!config.MapLayersEnabled)
        {
            activeMapLayer = AtlasMapLayer.TexturedTerrain;
            mapLayerTexture.Reset();
        }
        SyncSettingsControls();
        SyncPerformanceControls();
        SyncCreativeSettingsControls();
        saveConfig();
    }

    private void ExerciseAutomatedSearchInput()
    {
        if (automatedSmokeTestSearchInputAttempted) return;

        automatedSmokeTestSearchInputAttempted = true;
        if (SearchModeActive && searchPanel == null)
        {
            OpenBottomPanelImmediately(AtlasPanelSection.Search);
        }
        GuiElementTextInput? input = searchPanel?.GetTextInput("search-input");
        bool initiallyUnfocused = input?.HasFocus == false;
        bool focused = input != null
            && searchPanel?.FocusElement(input.TabIndex) == true;
        if (input == null || !focused)
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated search-input test could not focus the atlas text box."
            );
            return;
        }

        input.SetValue("", true);
        searchController.Clear();
        KeyEvent keyDown = new()
        {
            KeyCode = (int)GlKeys.G,
            KeyChar = 'g'
        };
        OnKeyDown(keyDown);
        bool remainedOpen = IsOpened();
        KeyEvent keyPress = new()
        {
            KeyCode = (int)GlKeys.G,
            KeyChar = 'g'
        };
        OnKeyPress(keyPress);

        string enteredText = input.GetText();
        string enteredQuery = searchController.Query;
        input.SetValue("", true);
        searchController.Clear();
        searchPanel?.UnfocusOwnElements();
        bool focusReleased = !input.HasFocus;
        automatedSmokeTestSearchInputPassed = initiallyUnfocused
            && remainedOpen
            && keyDown.Handled
            && keyPress.Handled
            && string.Equals(enteredText, "g", StringComparison.Ordinal)
            && string.Equals(enteredQuery, "g", StringComparison.Ordinal)
            && focusReleased;
        if (automatedSmokeTestSearchInputPassed)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test opened search unfocused, typed G after focusing it, then released focus without closing the atlas."
            );
        }
        else
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated search-input test failed: initiallyUnfocused={0}, open={1}, keyDown={2}, keyPress={3}, focusReleased={4}, text='{5}', query='{6}'.",
                initiallyUnfocused,
                remainedOpen,
                keyDown.Handled,
                keyPress.Handled,
                focusReleased,
                enteredText,
                enteredQuery
            );
        }
    }

    private void UnfocusSearchOutsideInput(MouseEvent args)
    {
        GuiElementTextInput? input = searchPanel?.GetTextInput("search-input");
        if (input?.HasFocus == true && !input.IsPositionInside(args.X, args.Y))
        {
            searchPanel?.UnfocusOwnElements();
        }
    }

    private void ExerciseAutomatedUnitInspection()
    {
        if (automatedSmokeTestUnitInspectionAttempted
            || !UnitInspectionEnabled
            || exactChunkRenderer == null
            || exactChunkRenderer.LastRenderedEntities.Count == 0)
        {
            return;
        }

        automatedSmokeTestUnitInspectionAttempted = true;
        AtlasRenderedEntity candidate = exactChunkRenderer.LastRenderedEntities[0];
        foreach (AtlasRenderedEntity renderedEntity in exactChunkRenderer.LastRenderedEntities)
        {
            if (renderedEntity.Entity.EntityId == capi.World.Player.Entity.EntityId)
            {
                candidate = renderedEntity;
                break;
            }
        }

        Entity entity = candidate.Entity;
        float selectionMiddle = (entity.SelectionBox.Y1 + entity.SelectionBox.Y2) * 0.5f;
        bool projected = TryProjectAtlasPosition(
            entity.Pos.X,
            entity.Pos.Y + selectionMiddle,
            entity.Pos.Z,
            out double screenX,
            out double screenY,
            out _
        );
        bool selected = projected
            && TrySelectRenderedEntity((int)Math.Round(screenX), (int)Math.Round(screenY));
        bool healthAvailable = AtlasEntityInspectionAdapter.TryGetHealth(
            entity,
            out _,
            out _
        );
        automatedSmokeTestUnitInspectionPassed = selected && healthAvailable;
        if (automatedSmokeTestUnitInspectionPassed)
        {
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test selected a rendered living model and read its unit-frame health."
            );
        }
        else
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated unit-frame test failed: projected={0}, selected={1}, health={2}.",
                projected,
                selected,
                healthAvailable
            );
        }
    }

    private void ExerciseAutomatedSearch()
    {
        if (automatedSmokeTestSearchPassed
            || !automatedSmokeTestUnitInspectionPassed)
        {
            return;
        }

        if (automatedSmokeTestSearchPhase == 0)
        {
            searchController.SetQueryImmediatelyForAutomatedTest("player");
            automatedSmokeTestSearchPhase = 1;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test started a loaded-entity atlas search."
            );
            return;
        }

        if (automatedSmokeTestSearchPhase == 1)
        {
            bool foundPlayer = false;
            foreach (AtlasSearchResult result in searchController.DynamicResults)
            {
                if (result.Kind != AtlasSearchResultKind.Player) continue;
                foundPlayer = true;
                break;
            }
            if (!foundPlayer) return;

            if (!TryGetAutomatedBlockSearchQuery(out string blockQuery))
            {
                automatedSmokeTestSearchPhase = -1;
                capi.Logger.Error(
                    "[ModernAtlas] Automated atlas search test could not find a loaded surface block near the player."
                );
                return;
            }

            searchController.SetQueryImmediatelyForAutomatedTest(blockQuery);
            automatedSmokeTestSearchPhase = 2;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test found a loaded entity marker and started an incremental block search for '{0}'.",
                blockQuery
            );
            return;
        }

        if (automatedSmokeTestSearchPhase == 2
            && searchController.BlockResults.Count > 0)
        {
            searchController.SetQueryImmediatelyForAutomatedTest("lantern");
            automatedSmokeTestSearchPhase = 3;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test found loaded entity and block markers and started a real placed-lantern search."
            );
            return;
        }
        if (automatedSmokeTestSearchPhase == 2
            && searchController.BlockScanComplete)
        {
            automatedSmokeTestSearchPhase = -1;
            capi.Logger.Error(
                "[ModernAtlas] Automated atlas block search completed without finding its known loaded block: {0}.",
                searchController.DiagnosticSummary
            );
            return;
        }

        if (automatedSmokeTestSearchPhase == 3)
        {
            if (!TrySelectAutomatedLanternBlock(out string activeQuery))
            {
                if (searchController.BlockScanComplete)
                {
                    automatedSmokeTestSearchPassed = true;
                    capi.Logger.Notification(
                        "[ModernAtlas] Automated placed-lantern marker comparison was not exercised because the loaded test-world chunks contain no lantern marker; the real registered attribute-backed lantern block alias check passed."
                    );
                }
                return;
            }

            searchController.SetQueryImmediatelyForAutomatedTest(activeQuery);
            automatedSmokeTestSearchPhase = 4;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test found a real placed-lantern marker in English and started the active-language query for the same loaded block."
            );
            return;
        }

        if (automatedSmokeTestSearchPhase != 4) return;
        if (HasAutomatedLanternBlockMarker())
        {
            automatedSmokeTestSearchPassed = true;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test found the same real placed-lantern marker in English and the active game language."
            );
        }
        else if (searchController.BlockScanComplete)
        {
            automatedSmokeTestSearchPhase = -1;
            capi.Logger.Error(
                "[ModernAtlas] Automated active-language placed-lantern search completed without the marker found by the English query."
            );
        }
    }

    private bool TrySelectAutomatedLanternBlock(out string activeQuery)
    {
        activeQuery = "";
        foreach (AtlasSearchResult result in searchController.BlockResults)
        {
            if (result.Kind != AtlasSearchResultKind.Block) continue;
            var position = new BlockPos(
                (int)Math.Floor(result.X),
                (int)Math.Floor(result.Y),
                (int)Math.Floor(result.Z)
            );
            Block block = capi.World.BlockAccessor.GetBlock(position);
            if (!searchController.TryGetActiveLanguageQueryForLanternBlock(
                    block,
                    out activeQuery
                ))
            {
                continue;
            }

            automatedLanternBlockX = result.X;
            automatedLanternBlockY = result.Y;
            automatedLanternBlockZ = result.Z;
            return true;
        }
        return false;
    }

    private bool HasAutomatedLanternBlockMarker()
    {
        foreach (AtlasSearchResult result in searchController.BlockResults)
        {
            if (result.Kind == AtlasSearchResultKind.Block
                && Math.Abs(result.X - automatedLanternBlockX) <= 0.01
                && Math.Abs(result.Y - automatedLanternBlockY) <= 0.01
                && Math.Abs(result.Z - automatedLanternBlockZ) <= 0.01)
            {
                return true;
            }
        }
        return false;
    }

    private bool TryGetAutomatedBlockSearchQuery(out string query)
    {
        query = "";
        int worldX = (int)Math.Floor(capi.World.Player.Entity.Pos.X);
        int worldZ = (int)Math.Floor(capi.World.Player.Entity.Pos.Z);
        int surfaceY = capi.World.BlockAccessor.GetRainMapHeightAt(worldX, worldZ);
        int startY = Math.Clamp(
            surfaceY > 0
                ? surfaceY
                : (int)Math.Floor(capi.World.Player.Entity.Pos.Y) - 1,
            0,
            capi.World.BlockAccessor.MapSizeY - 1
        );
        BlockPos position = new(worldX, startY, worldZ);
        for (int offset = 0; offset <= 32 && position.Y >= 0; offset++, position.Y--)
        {
            Block block = capi.World.BlockAccessor.GetBlock(position);
            string path = block.Code?.Path ?? "";
            if (block.Id == 0 || path.Length < 2) continue;
            query = path;
            return true;
        }
        return false;
    }

    private void ExerciseAutomatedMapLayers()
    {
        if (automatedSmokeTestMapLayerPassed || !automatedSmokeTestSearchPassed)
        {
            return;
        }

        if (automatedSmokeTestMapLayerPhase == 0)
        {
            targetPitchDegrees = 72;
            pitchDegrees = 72;
            FitLoadedTerrain();
            zoom = targetZoom;
            SetMapLayer(AtlasMapLayer.Moisture);
            automatedSmokeTestMapLayerPhase = 1;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test started the loaded-data moisture layer."
            );
            return;
        }

        if (automatedSmokeTestMapLayerPhase == 1)
        {
            if (!mapLayerTexture.Ready) return;
            if (mapLayerTexture.TextureId <= 0)
            {
                automatedSmokeTestMapLayerPhase = -1;
                capi.Logger.Error(
                    "[ModernAtlas] Automated map-layer test prepared moisture data without a GPU texture."
                );
                return;
            }
            if (QueueAutomatedMapLayerScreenshot("layer-moisture"))
            {
                automatedSmokeTestMapLayerPhase = 10;
                return;
            }
            automatedSmokeTestMapLayerPhase = 10;
        }

        if (automatedSmokeTestMapLayerPhase == 10)
        {
            if (pendingAutomatedMapLayerScreenshotSuffix != null) return;
            SetMapLayer(AtlasMapLayer.OreDensity);
            automatedSmokeTestMapLayerPhase = 2;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test rendered the moisture layer and started the Creative/Cheat ore-density layer."
            );
            return;
        }

        if (automatedSmokeTestMapLayerPhase == 2 && mapLayerTexture.Ready)
        {
            if (!AtlasOrePotential.ValidateThresholds())
            {
                automatedSmokeTestMapLayerPhase = -1;
                capi.Logger.Error(
                    "[ModernAtlas] Automated ore-potential test rejected the vanilla grade thresholds."
                );
                return;
            }

            int sampleX = (int)Math.Floor(
                capi.World.Player.Entity.Pos.X / AtlasMapLayerTexture.HorizontalSampleSize
            ) * AtlasMapLayerTexture.HorizontalSampleSize
                + AtlasMapLayerTexture.HorizontalSampleSize / 2;
            int sampleZ = (int)Math.Floor(
                capi.World.Player.Entity.Pos.Z / AtlasMapLayerTexture.HorizontalSampleSize
            ) * AtlasMapLayerTexture.HorizontalSampleSize
                + AtlasMapLayerTexture.HorizontalSampleSize / 2;
            if (!mapLayerTexture.TryInspectOre(
                sampleX,
                sampleZ,
                out AtlasOreInspection? automatedInspection
            ) || automatedInspection == null)
            {
                automatedSmokeTestMapLayerPhase = -1;
                capi.Logger.Error(
                    "[ModernAtlas] Automated ore-potential test could not inspect the loaded player column."
                );
                return;
            }

            oreHoverInspection = automatedInspection;
            oreHoverCellX = sampleX;
            oreHoverCellZ = sampleZ;
            oreHoverMouseX = AtlasViewport.X + AtlasViewport.Width / 2;
            oreHoverMouseY = AtlasViewport.Y + AtlasViewport.Height / 2;
            BuildOreHoverTexture(automatedInspection);
            if (QueueAutomatedMapLayerScreenshot("layer-ore-overview"))
            {
                automatedSmokeTestMapLayerPhase = 20;
                return;
            }
            automatedSmokeTestMapLayerPhase = 20;
        }

        if (automatedSmokeTestMapLayerPhase == 20)
        {
            if (pendingAutomatedMapLayerScreenshotSuffix != null) return;

            if (mapLayerTexture.DiscoveredOreCodes.Count > 0)
            {
                selectedOreCode = mapLayerTexture.DiscoveredOreCodes[0];
                PrepareMapLayer();
                automatedSmokeTestMapLayerPhase = 3;
                capi.Logger.Notification(
                    "[ModernAtlas] Automated smoke test selected the loaded ore filter {0}.",
                    selectedOreCode
                );
                return;
            }
        }

        if (automatedSmokeTestMapLayerPhase == 3)
        {
            if (!mapLayerTexture.Ready) return;
            if (mapLayerTexture.TryInspectOre(
                oreHoverCellX,
                oreHoverCellZ,
                out AtlasOreInspection? filteredInspection
            ) && filteredInspection != null)
            {
                oreHoverInspection = filteredInspection;
                BuildOreHoverTexture(filteredInspection);
            }
            if (QueueAutomatedMapLayerScreenshot("layer-ore-filtered"))
            {
                automatedSmokeTestMapLayerPhase = 30;
                return;
            }
            automatedSmokeTestMapLayerPhase = 30;
        }
        if (automatedSmokeTestMapLayerPhase == 30
            && pendingAutomatedMapLayerScreenshotSuffix != null)
        {
            return;
        }
        if (automatedSmokeTestMapLayerPhase == 30)
        {
            automatedSmokeTestOreLayerZoom = zoom;
            targetZoom = Math.Clamp(
                zoom * 0.62f,
                MaximumZoomIn,
                30000
            );
            automatedSmokeTestMapLayerPhase = 31;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test is zooming the filtered ore layer while keeping its transient data texture active."
            );
            return;
        }
        if (automatedSmokeTestMapLayerPhase == 31)
        {
            if (Math.Abs(targetZoom - zoom) > 0.01f) return;
            if (QueueAutomatedMapLayerScreenshot("layer-ore-filtered-zoom"))
            {
                automatedSmokeTestMapLayerPhase = 32;
                return;
            }
            automatedSmokeTestMapLayerPhase = 32;
        }
        if (automatedSmokeTestMapLayerPhase == 32
            && pendingAutomatedMapLayerScreenshotSuffix != null)
        {
            return;
        }
        if (automatedSmokeTestMapLayerPhase is not (20 or 32)
            || !mapLayerTexture.Ready)
        {
            return;
        }
        if (mapLayerTexture.TextureId <= 0)
        {
            automatedSmokeTestMapLayerPhase = -1;
            capi.Logger.Error(
                "[ModernAtlas] Automated map-layer test prepared ore data without a GPU texture."
            );
            return;
        }

        automatedSmokeTestMapLayerPassed = true;
        targetZoom = automatedSmokeTestOreLayerZoom > 0
            ? automatedSmokeTestOreLayerZoom
            : targetZoom;
        zoom = targetZoom;
        selectedOreCode = null;
        SetMapLayer(AtlasMapLayer.TexturedTerrain);
        capi.Logger.Notification(
            "[ModernAtlas] Automated smoke test rendered climate and Creative/Cheat ore map layers from loaded data and retained the selected ore colors through zoom."
        );
    }

    private bool QueueAutomatedMapLayerScreenshot(string suffix)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            SmokeScreenshotEnvironmentVariable
        )))
        {
            return false;
        }
        pendingAutomatedMapLayerScreenshotSuffix = suffix;
        return true;
    }

    internal void OnWorldLeave()
    {
        worldTeardownStarted = true;
        CommitPendingPresentationPreference();
        presentationChangeCoordinator.Reset(config.RenderOnScroll);
        RestoreAutomatedSmokeTestPreferences();
        automatedSmokeTestActive = false;
        automatedSmokeTestElapsedSeconds = 0;
        automatedSmokeTestCompletion = null;
        automatedSmokeTestRequiresUnlockedPitch = false;
        automatedSmokeTestRenderedAtPitchFloor = false;
        automatedSmokeTestInterfaceControlsAttempted = false;
        automatedSmokeTestInterfaceControlsPassed = false;
        automatedSmokeTestUnitInspectionAttempted = false;
        automatedSmokeTestUnitInspectionPassed = false;
        automatedSmokeTestSearchInputAttempted = false;
        automatedSmokeTestSearchInputPassed = false;
        automatedScreenshotCaptureRequested = false;
        automatedScreenshotCapturePassed = false;
        automatedScreenshotBaselineZoom = 0;
        automatedScreenshotBaselineTargetZoom = 0;
        automatedScreenshotBaselineCenterX = 0;
        automatedScreenshotBaselineCenterY = 0;
        automatedScreenshotBaselineCenterZ = 0;
        automatedSmokeTestBilingualSearchPassed = false;
        automatedSmokeTestOreConcealmentPassed = false;
        automatedSmokeTestForceSurvivalOreConcealment = false;
        automatedSmokeTestSafeSurfaceFrameRendered = false;
        automatedSmokeTestSafeSurfaceScreenshotHandled = false;
        automatedSmokeTestSearchPhase = 0;
        automatedSmokeTestSearchPassed = false;
        automatedLanternBlockX = 0;
        automatedLanternBlockY = 0;
        automatedLanternBlockZ = 0;
        automatedSmokeTestMapLayerPhase = 0;
        automatedSmokeTestMapLayerPassed = false;
        pendingAutomatedMapLayerScreenshotSuffix = null;
        automatedSmokeTestPresentationPassed = false;
        automatedSmokeTestResolvedAtlasAlphaChecked = false;
        automatedSmokeTestResolvedAtlasAlphaPassed = false;
        automatedSmokeTestViewDistancePassed = true;
        automatedSmokeTestViewDistanceUnchanged = true;
        automatedSmokeTestInitialViewDistance = -1;
        automatedSmokeCloseStatePassed = true;
        automatedSmokeTestPresentationPhase = 0;
        automatedSmokeTestSettingsComposer = null;
        automatedSmokeTestPerformanceComposer = null;
        automatedSmokeTestCreativeComposer = null;
        automatedSmokeTestVisualLabComposer = null;
        AutomatedSmokeTestRenderedExactWorld = false;
        cheatModeEnabled = false;
        ResetBottomPanelState();
        interfaceHidden = false;
        selectedEntityId = null;
        searchController.Clear();
        pendingScreenshotRequest = false;
        screenshotStatusOverride = null;
        tileScreenshot.Cancel();
        RestoreScreenshotCamera();
        screenshotProgressModal?.Dispose();
        screenshotProgressModal = null;
        activeMapLayer = AtlasMapLayer.TexturedTerrain;
        selectedOreCode = null;
        ClearOreHover();
        mapLayerTexture.Reset();
        preparingSurfaceFilter = false;
        preparingOreConcealment = false;
        preparingVegetationMask = false;
        ResetPointerDrag();

        if (IsOpened())
        {
            try
            {
                TryClose();
            }
            catch (Exception exception)
            {
                capi.Logger.Warning(
                    "[ModernAtlas] Could not close the atlas normally while leaving the world: {0}",
                    exception.Message
                );
            }
        }

        try
        {
            exactChunkRenderer?.Dispose();
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] World-specific renderer cleanup completed with a recoverable error: {0}",
                exception.Message
            );
        }
        finally
        {
            pendingNormalWorldShaderRestore = null;
            normalWorldShaderRestoreGeneration++;
            exactChunkRenderer = null;
        }
        preparedGameViewDistance = -1;
        surfaceHeightTexture.Reset();
        ReleaseAtlasFrameCache();
        capi.Logger.Notification(
            "[ModernAtlas] Released world-specific atlas rendering resources."
        );
    }

    public override void Dispose()
    {
        worldTeardownStarted = true;
        presentationChangeCoordinator.Reset(config.RenderOnScroll);
        RestoreAutomatedSmokeTestPreferences();
        automatedSmokeTestActive = false;
        automatedSmokeTestCompletion = null;
        pendingNormalWorldShaderRestore = null;
        normalWorldShaderRestoreGeneration++;
        exactChunkRenderer?.Dispose();
        exactChunkRenderer = null;
        surfaceHeightTexture.Dispose();
        mapLayerTexture.Dispose();
        searchMarkerTexture?.Dispose();
        searchMarkerTexture = null;
        oreHoverTexture?.Dispose();
        oreHoverTexture = null;
        compassRenderer.Dispose();
        ReleaseAtlasFrameCache();
        opacityQuad?.Dispose();
        opacityQuad = null;
        scrollViewportRenderer.Dispose();
        scrollRealtimeWeather.Dispose();
        overlay?.Dispose();
        overlay = null;
        ResetBottomPanelState();
        creativeSettingsShortcut?.Dispose();
        creativeSettingsShortcut = null;
        screenshotProgressModal?.Dispose();
        screenshotProgressModal = null;
        tileScreenshot.Dispose();
        base.Dispose();
    }

    private void RecomposeInterface()
    {
        AtlasPanelSection section = bottomPanelSection != AtlasPanelSection.None
            ? bottomPanelSection
            : queuedBottomPanelSection;
        bool panelWasVisible = bottomPanelProgress > 0.01f
            || section != AtlasPanelSection.None;
        DisposeBottomPanelComposer();
        ComposeOverlay();
        if (panelWasVisible && section != AtlasPanelSection.None)
        {
            OpenBottomPanelImmediately(section);
        }
        composedFrameWidth = capi.Render.FrameWidth;
        composedFrameHeight = capi.Render.FrameHeight;
        composedGuiScale = RuntimeEnv.GUIScale;
        SyncToolbarControls();
        SyncSettingsControls();
        SyncPerformanceControls();
        SyncCreativeSettingsControls();
        SyncMapLayerDropdown();
        SyncScreenshotSettingsControls();
        return;

#if false
        overlay?.Dispose();
        searchPanel?.Dispose();
        mapLayerPanel?.Dispose();
        creativeSettingsShortcut?.Dispose();
        settingsModal?.Dispose();
        performanceModal?.Dispose();
        creativeSettingsModal?.Dispose();
        visualLabModal?.Dispose();
        screenshotPanel?.Dispose();
        screenshotProgressModal?.Dispose();
        unitPanel?.Dispose();
        overlay = null;
        searchPanel = null;
        mapLayerPanel = null;
        creativeSettingsShortcut = null;
        settingsModal = null;
        performanceModal = null;
        creativeSettingsModal = null;
        visualLabModal = null;
        screenshotPanel = null;
        screenshotProgressModal = null;
        unitPanel = null;
        if (tileScreenshot.Busy)
        {
            // A presentation recomposition cannot happen through user input
            // while the capture modal owns the pointer, but automated tests
            // may drive both; never leave the camera pinned to a tile.
            tileScreenshot.Cancel();
            RestoreScreenshotCamera();
            pendingScreenshotRequest = false;
        }
        ComposeOverlay();
        composedFrameWidth = capi.Render.FrameWidth;
        composedFrameHeight = capi.Render.FrameHeight;
        composedGuiScale = RuntimeEnv.GUIScale;
        if (searchController.Query.Length > 0)
        {
            searchPanel?.GetTextInput("search-input")?.SetValue(
                searchController.Query,
                true
            );
        }
        SyncSettingsControls();
        SyncPerformanceControls();
        SyncCreativeSettingsControls();
        SyncMapLayerDropdown();
    }
#endif
    }

    private void RecomposeViewportInterface()
    {
        overlay?.Dispose();
        overlay = null;
        ComposeOverlay(true);
        RepositionActiveBottomPanel();
        composedFrameWidth = capi.Render.FrameWidth;
        composedFrameHeight = capi.Render.FrameHeight;
        composedGuiScale = RuntimeEnv.GUIScale;
        return;

#if false
        // Presentation changes alter only the map-relative controls. Keep
        // Settings and every modal child alive so a switch cannot receive its
        // MouseUp on a newly-created composer and so modal input remains valid.
        overlay?.UnfocusOwnElements();
        searchPanel?.UnfocusOwnElements();
        mapLayerPanel?.UnfocusOwnElements();
        creativeSettingsShortcut?.UnfocusOwnElements();
        unitPanel?.UnfocusOwnElements();
        overlay?.Dispose();
        searchPanel?.Dispose();
        mapLayerPanel?.Dispose();
        creativeSettingsShortcut?.Dispose();
        unitPanel?.Dispose();
        overlay = null;
        searchPanel = null;
        mapLayerPanel = null;
        creativeSettingsShortcut = null;
        unitPanel = null;

        ComposeOverlay(true);
        composedFrameWidth = capi.Render.FrameWidth;
        composedFrameHeight = capi.Render.FrameHeight;
        composedGuiScale = RuntimeEnv.GUIScale;
        if (searchController.Query.Length > 0)
        {
            searchPanel?.GetTextInput("search-input")?.SetValue(
                searchController.Query,
                true
            );
        }
        SyncMapLayerDropdown();
    }
#endif
    }

    private void ComposeCompactOverlay()
    {
        overlay?.Dispose();
        overlay = null;

        AtlasViewportBounds viewport = AtlasViewport;
        double guiScale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double contentX = config.RenderOnScroll ? viewport.X / guiScale : 0;
        double contentY = config.RenderOnScroll ? viewport.Y / guiScale : 0;
        double guiWidth = viewport.Width / guiScale;
        double guiHeight = viewport.Height / guiScale;
        double toolbarWidth = ToolbarWidthGui(guiWidth);
        double toolbarX = contentX + 10;
        double toolbarY = contentY + 10;
        double toolbarHeight = Math.Max(170, guiHeight - 20);
        double rowHeight = Math.Clamp((toolbarHeight - 62) / 7, 24, 30);
        double rowGap = Math.Max(2, rowHeight * 0.12);
        double buttonX = toolbarX + 10;
        double buttonWidth = toolbarWidth - 20;
        // Leave enough breathing room for the larger version and view
        // distance labels without letting the first button overlap them.
        double sectionY = toolbarY + 66;
        double pairWidth = Math.Max(24, (buttonWidth - 4) * 0.5);
        double pairY = sectionY + rowHeight + rowGap;
        double mapY = pairY + rowHeight + rowGap;
        double searchY = mapY + rowHeight + rowGap;
        double instrumentY = searchY + rowHeight + rowGap;
        double exitY = toolbarY + toolbarHeight - rowHeight - 9;
        double hideY = exitY - rowHeight - rowGap;

        ElementBounds tooltipBounds = ElementBounds.Fixed(
            toolbarX + toolbarWidth + 8,
            contentY,
            Math.Max(120, guiWidth - toolbarWidth - 20),
            guiHeight
        );

        overlay = capi.Gui.CreateCompo("modernatlas-compact-overlay", ElementBounds.Fill)
            .AddStaticCustomDraw(
                ElementBounds.Fixed(toolbarX, toolbarY, toolbarWidth, toolbarHeight),
                AtlasUiStyle.DrawToolbarPanel
            )
            .AddStaticText(
                "ModernAtlas",
                AtlasUiStyle.TitleFont(13),
                ElementBounds.Fixed(toolbarX + 10, toolbarY + 10, toolbarWidth - 20, 18)
            )
            .AddStaticText(
                "v0.6.7",
                AtlasUiStyle.DetailFont(14),
                ElementBounds.Fixed(toolbarX + 10, toolbarY + 27, toolbarWidth - 20, 18)
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(toolbarX + 10, toolbarY + 46, toolbarWidth - 20, 16),
                "status"
            )
            .AddAtlasButton(
                "Settings",
                ToggleSettingsModal,
                ElementBounds.Fixed(buttonX, sectionY, buttonWidth, rowHeight),
                "settings-button",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "SF",
                TakeQuickScreenshot,
                ElementBounds.Fixed(buttonX, pairY, pairWidth, rowHeight),
                "quick-screenshot-button",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "SP",
                ToggleScreenshotOptions,
                ElementBounds.Fixed(buttonX + pairWidth + 4, pairY, pairWidth, rowHeight),
                "screenshot-options-button",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "Map",
                ToggleMapOptions,
                ElementBounds.Fixed(buttonX, mapY, buttonWidth, rowHeight),
                "map-options-button",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "Search",
                ToggleSearchPanel,
                ElementBounds.Fixed(buttonX, searchY, buttonWidth, rowHeight),
                "search-button",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "Hand",
                ToggleInstrumentPanel,
                ElementBounds.Fixed(buttonX, instrumentY, buttonWidth, rowHeight),
                "instrument-button",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "Hide",
                HideInterface,
                ElementBounds.Fixed(buttonX, hideY, buttonWidth, rowHeight),
                "hide-ui-button",
                AtlasButtonStyle.Compact
            )
            .AddAtlasButton(
                "Exit",
                CloseAtlas,
                ElementBounds.Fixed(buttonX, exitY, buttonWidth, rowHeight),
                "exit-button",
                AtlasButtonStyle.Compact
            )
            .AddDynamicCustomDraw(
                tooltipBounds,
                (context, surface, bounds) => AtlasUiStyle.DrawTooltip(
                    context,
                    surface,
                    bounds,
                    toolbarTooltipText,
                    toolbarTooltipLocalY
                ),
                "toolbar-tooltip"
            )
            .Compose(false);

        toolbarTooltipText = "";
        toolbarTooltipLocalY = 0;
        SyncToolbarControls();
    }

    private void SyncToolbarControls()
    {
        if (overlay == null) return;

        bool settingsActive = SettingsHierarchyOpen;
        overlay.GetAtlasButton("settings-button")?.SetActive(settingsActive);
        overlay.GetAtlasButton("screenshot-options-button")?.SetActive(
            ScreenshotOptionsOpen
        );
        overlay.GetAtlasButton("map-options-button")?.SetActive(MapPanelOpen);
        overlay.GetAtlasButton("search-button")?.SetActive(SearchPanelOpen);
        overlay.GetAtlasButton("instrument-button")?.SetActive(InstrumentPanelOpen);
        overlay.GetAtlasButton("search-button")!.Enabled = SearchModeActive;
        overlay.GetAtlasButton("quick-screenshot-button")!.Enabled =
            !tileScreenshot.Busy;
    }

    private void UpdateToolbarTooltip(int mouseX, int mouseY)
    {
        toolbarTooltipText = "";
        toolbarTooltipLocalY = 0;
        if (interfaceHidden || overlay == null) return;

        (string Key, string Text)[] tooltips =
        {
            ("settings-button", "Settings"),
            ("quick-screenshot-button", "Quick screenshot"),
            ("screenshot-options-button", "Screenshot options"),
            ("map-options-button", "Map options"),
            ("search-button", "Search loaded data"),
            ("instrument-button", "Handheld instrument"),
            ("hide-ui-button", "Hide atlas UI"),
            ("exit-button", "Exit atlas")
        };
        foreach ((string key, string text) in tooltips)
        {
            GuiElementAtlasButton? button = overlay.GetAtlasButton(key);
            if (button == null || !button.Bounds.PointInside(mouseX, mouseY))
            {
                continue;
            }

            toolbarTooltipText = text;
            toolbarTooltipLocalY = mouseY - (config.RenderOnScroll ? AtlasViewport.Y : 0);
            return;
        }
    }

    private double ToolbarWidthGui(double guiWidth) => Math.Clamp(guiWidth * 0.12, 128, 148);

    private double GetBottomPanelHeightGui(
        AtlasPanelSection section,
        double guiHeight
    )
    {
        double desired = section switch
        {
            AtlasPanelSection.Settings => 236,
            AtlasPanelSection.ScreenshotOptions => 190,
            AtlasPanelSection.MapOptions => 158,
            AtlasPanelSection.Search => 124,
            AtlasPanelSection.Instrument => 118,
            AtlasPanelSection.Performance => 152,
            AtlasPanelSection.Creative => 132,
            AtlasPanelSection.VisualLab => 158,
            AtlasPanelSection.Unit => 188,
            _ => 0
        };
        // Keep the panel below the requested 35% visual budget. At very large
        // GUI scales the content uses the same composer with a small internal
        // wheel-scroll offset rather than expanding over the map.
        return Math.Max(96, Math.Min(desired, guiHeight * 0.35));
    }

    private ElementBounds GetBottomPanelRoot(
        AtlasPanelSection section,
        out AtlasPanelGeometry geometry
    )
    {
        double guiScale = Math.Max(0.5, RuntimeEnv.GUIScale);
        AtlasViewportBounds viewport = AtlasViewport;
        double contentX = config.RenderOnScroll ? viewport.X / guiScale : 0;
        double contentY = config.RenderOnScroll ? viewport.Y / guiScale : 0;
        double guiWidth = viewport.Width / guiScale;
        double guiHeight = viewport.Height / guiScale;
        double toolbarWidth = ToolbarWidthGui(guiWidth);
        double panelWidth = Math.Max(180, guiWidth - toolbarWidth - 30);
        panelWidth = Math.Min(panelWidth, Math.Max(180, guiWidth - 8));
        double panelHeight = GetBottomPanelHeightGui(section, guiHeight);
        double panelX = contentX + toolbarWidth + 18;
        double panelY = contentY + guiHeight - panelHeight - 8;
        geometry = new AtlasPanelGeometry(
            (int)Math.Round(panelX * guiScale),
            (int)Math.Round(panelY * guiScale),
            Math.Max(1, (int)Math.Round(panelWidth * guiScale)),
            Math.Max(1, (int)Math.Round(panelHeight * guiScale))
        );
        bottomPanelGeometry = geometry;
        hasBottomPanelGeometry = true;
        ElementBounds root = ElementBounds.Fixed(
            panelX,
            panelY,
            panelWidth,
            panelHeight
        );
        bottomPanelBounds = root;
        searchPanelBounds = section == AtlasPanelSection.Search ? root : null;
        mapLayerPanelBounds = section == AtlasPanelSection.MapOptions ? root : null;
        screenshotPanelBounds = section == AtlasPanelSection.ScreenshotOptions
            ? root
            : null;
        return root;
    }

    private void SetBottomPanelAliases(AtlasPanelSection section)
    {
        settingsModal = section == AtlasPanelSection.Settings ? bottomPanel : null;
        performanceModal = section == AtlasPanelSection.Performance
            ? bottomPanel
            : null;
        creativeSettingsModal = section == AtlasPanelSection.Creative
            ? bottomPanel
            : null;
        visualLabModal = section == AtlasPanelSection.VisualLab
            ? bottomPanel
            : null;
        searchPanel = section == AtlasPanelSection.Search ? bottomPanel : null;
        mapLayerPanel = section == AtlasPanelSection.MapOptions ? bottomPanel : null;
        screenshotPanel = section == AtlasPanelSection.ScreenshotOptions
            ? bottomPanel
            : null;
        unitPanel = section == AtlasPanelSection.Unit ? bottomPanel : null;
    }

    private GuiComposer? GetBottomPanelComposer(AtlasPanelSection section) =>
        bottomPanelSection == section || bottomPanelVisualSection == section
            ? bottomPanel
            : null;

    private AtlasPanelSection bottomPanelVisualSection => bottomPanel == null
        ? AtlasPanelSection.None
        : settingsModal != null
            ? AtlasPanelSection.Settings
            : performanceModal != null
                ? AtlasPanelSection.Performance
                : creativeSettingsModal != null
                    ? AtlasPanelSection.Creative
                    : visualLabModal != null
                        ? AtlasPanelSection.VisualLab
                        : searchPanel != null
                            ? AtlasPanelSection.Search
                            : mapLayerPanel != null
                                ? AtlasPanelSection.MapOptions
                                : screenshotPanel != null
                                    ? AtlasPanelSection.ScreenshotOptions
                                    : unitPanel != null
                                        ? AtlasPanelSection.Unit
                                        : AtlasPanelSection.None;

    private void DisposeBottomPanelComposer()
    {
        bottomPanel?.UnfocusOwnElements();
        bottomPanel?.Dispose();
        bottomPanel = null;
        SetBottomPanelAliases(AtlasPanelSection.None);
        bottomPanelSection = AtlasPanelSection.None;
        queuedBottomPanelSection = AtlasPanelSection.None;
        bottomPanelProgress = 0;
        bottomPanelAnimationActive = false;
        bottomPanelBounds = null;
        searchPanelBounds = null;
        mapLayerPanelBounds = null;
        screenshotPanelBounds = null;
        hasBottomPanelGeometry = false;
    }

    private void ResetBottomPanelState()
    {
        DisposeBottomPanelComposer();
        settingsModalOpen = false;
        performanceModalOpen = false;
        creativeSettingsModalOpen = false;
        visualLabModalOpen = false;
        toolbarTooltipText = "";
        toolbarTooltipLocalY = 0;
        settingsScrollOffset = 0;
    }

    private void SetLogicalBottomPanel(AtlasPanelSection section)
    {
        settingsModalOpen = section == AtlasPanelSection.Settings;
        performanceModalOpen = section == AtlasPanelSection.Performance;
        creativeSettingsModalOpen = section == AtlasPanelSection.Creative;
        visualLabModalOpen = section == AtlasPanelSection.VisualLab;
    }

    private GuiComposer CreateBottomPanelComposer(
        AtlasPanelSection section,
        ElementBounds root
    )
    {
        double width = root.fixedWidth;
        double height = root.fixedHeight;
        GuiComposer composer = capi.Gui.CreateCompo(
            "modernatlas-bottom-panel",
            root
        ).AddStaticCustomDraw(
            ElementBounds.Fixed(0, 0, width, height),
            AtlasUiStyle.DrawCard
        );

        switch (section)
        {
            case AtlasPanelSection.Settings:
                ComposeSettingsBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.ScreenshotOptions:
                ComposeScreenshotBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.MapOptions:
                ComposeMapBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.Search:
                ComposeSearchBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.Instrument:
                ComposeInstrumentBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.Performance:
                ComposePerformanceBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.Creative:
                ComposeCreativeBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.VisualLab:
                ComposeVisualLabBottomPanel(composer, width, height);
                break;
            case AtlasPanelSection.Unit:
                ComposeUnitBottomPanel(composer, width, height);
                break;
        }
        return composer.Compose(false);
    }

    private void ComposeBottomPanel(AtlasPanelSection section, bool animate)
    {
        DisposeBottomPanelComposer();
        ElementBounds root = GetBottomPanelRoot(section, out _);
        bottomPanelSection = section;
        bottomPanel = CreateBottomPanelComposer(section, root);
        SetBottomPanelAliases(section);
        SetLogicalBottomPanel(section);
        bottomPanelProgress = animate ? 0 : 1;
        bottomPanelAnimationStart = bottomPanelProgress;
        bottomPanelAnimationTarget = 1;
        bottomPanelAnimationStartedMilliseconds = capi.ElapsedMilliseconds;
        bottomPanelAnimationActive = animate;
        SyncSettingsControls();
        SyncPerformanceControls();
        SyncCreativeSettingsControls();
        SyncMapLayerDropdown();
        SyncScreenshotSettingsControls();
        SyncToolbarControls();
    }

    private void OpenBottomPanelImmediately(AtlasPanelSection section)
    {
        ComposeBottomPanel(section, false);
    }

    private void StartClosingBottomPanel(AtlasPanelSection nextSection)
    {
        queuedBottomPanelSection = nextSection;
        SetLogicalBottomPanel(AtlasPanelSection.None);
        bottomPanelAnimationStart = bottomPanelProgress;
        bottomPanelAnimationTarget = 0;
        bottomPanelAnimationStartedMilliseconds = capi.ElapsedMilliseconds;
        bottomPanelAnimationActive = true;
        SyncToolbarControls();
    }

    private void RequestBottomPanel(AtlasPanelSection section)
    {
        if (section == AtlasPanelSection.None)
        {
            if (bottomPanelSection != AtlasPanelSection.None)
            {
                StartClosingBottomPanel(AtlasPanelSection.None);
            }
            return;
        }
        if (section == AtlasPanelSection.Search && !SearchModeActive) return;
        if (section == AtlasPanelSection.Creative
            && !CreativeCheatSettingsAvailable)
        {
            return;
        }

        ResetPointerDrag();
        if (section != AtlasPanelSection.Unit) selectedEntityId = null;

        if (bottomPanelAnimationActive && BottomPanelClosing)
        {
            if (section == bottomPanelSection)
            {
                // A second click on the active toolbar shortcut reverses the
                // close animation instead of waiting for a stale queued open.
                queuedBottomPanelSection = AtlasPanelSection.None;
                SetLogicalBottomPanel(section);
                bottomPanelAnimationStart = bottomPanelProgress;
                bottomPanelAnimationTarget = 1;
                bottomPanelAnimationStartedMilliseconds = capi.ElapsedMilliseconds;
                bottomPanelAnimationActive = true;
                SyncToolbarControls();
            }
            else
            {
                queuedBottomPanelSection = section;
            }
            return;
        }

        if (bottomPanelSection == section && !BottomPanelClosing)
        {
            StartClosingBottomPanel(AtlasPanelSection.None);
            return;
        }

        if (bottomPanelSection != AtlasPanelSection.None)
        {
            StartClosingBottomPanel(section);
            return;
        }

        OpenBottomPanel(section);
    }

    private void OpenBottomPanel(AtlasPanelSection section)
    {
        OpenBottomPanelImmediately(section);
        bottomPanelProgress = 0;
        bottomPanelAnimationStart = 0;
        bottomPanelAnimationTarget = 1;
        bottomPanelAnimationStartedMilliseconds = capi.ElapsedMilliseconds;
        bottomPanelAnimationActive = true;
    }

    private void AdvanceBottomPanelAnimation()
    {
        if (!bottomPanelAnimationActive) return;

        float elapsed = Math.Max(
            0,
            capi.ElapsedMilliseconds - bottomPanelAnimationStartedMilliseconds
        );
        float progress = Math.Clamp(
            elapsed / BottomPanelAnimationMilliseconds,
            0,
            1
        );
        float eased = 1f - MathF.Pow(1f - progress, 3f);
        bottomPanelProgress = bottomPanelAnimationStart
            + (bottomPanelAnimationTarget - bottomPanelAnimationStart) * eased;
        if (progress < 1f) return;

        bottomPanelProgress = bottomPanelAnimationTarget;
        bottomPanelAnimationActive = false;
        if (bottomPanelProgress > 0.5f) return;

        AtlasPanelSection next = queuedBottomPanelSection;
        queuedBottomPanelSection = AtlasPanelSection.None;
        DisposeBottomPanelComposer();
        if (next != AtlasPanelSection.None)
        {
            OpenBottomPanel(next);
        }
        SyncToolbarControls();
    }

    private void RepositionActiveBottomPanel()
    {
        if (bottomPanel == null || bottomPanelSection == AtlasPanelSection.None)
        {
            return;
        }

        AtlasPanelSection section = bottomPanelSection;
        ElementBounds? existingRoot = bottomPanelBounds;
        ElementBounds newRoot = GetBottomPanelRoot(section, out _);
        if (existingRoot == null) return;
        existingRoot.fixedX = newRoot.fixedX;
        existingRoot.fixedY = newRoot.fixedY;
        existingRoot.fixedWidth = newRoot.fixedWidth;
        existingRoot.fixedHeight = newRoot.fixedHeight;
        bottomPanelBounds = existingRoot;
        searchPanelBounds = section == AtlasPanelSection.Search ? existingRoot : null;
        mapLayerPanelBounds = section == AtlasPanelSection.MapOptions ? existingRoot : null;
        screenshotPanelBounds = section == AtlasPanelSection.ScreenshotOptions
            ? existingRoot
            : null;
        bottomPanel.ReCompose();
    }

    private bool PanelCoversPoint(int x, int y)
    {
        if (!hasBottomPanelGeometry || bottomPanel == null) return false;
        if (bottomPanelProgress <= 0.01f) return false;
        int visibleHeight = Math.Max(
            1,
            (int)Math.Round(bottomPanelGeometry.Height * bottomPanelProgress)
        );
        int visibleTop = bottomPanelGeometry.Bottom - visibleHeight;
        return x >= bottomPanelGeometry.X
            && x <= bottomPanelGeometry.X + bottomPanelGeometry.Width
            && y >= visibleTop
            && y <= bottomPanelGeometry.Bottom;
    }

    private void RenderActiveBottomPanel(float deltaTime)
    {
        if (bottomPanel == null || bottomPanelProgress <= 0.01f) return;
        if (bottomPanelSection == AtlasPanelSection.Unit)
        {
            UpdateUnitInspectionText();
        }

        int visibleHeight = Math.Max(
            1,
            (int)Math.Round(bottomPanelGeometry.Height * bottomPanelProgress)
        );
        int visibleTop = bottomPanelGeometry.Bottom - visibleHeight;
        IRenderAPI render = capi.Render;
        render.GlScissor(
            bottomPanelGeometry.X,
            Math.Max(0, render.FrameHeight - bottomPanelGeometry.Bottom),
            bottomPanelGeometry.Width,
            visibleHeight
        );
        render.GlScissorFlag(true);
        try
        {
            bottomPanel.Render(deltaTime);
        }
        finally
        {
            render.GlScissorFlag(false);
        }
    }

    private void ComposeSettingsBottomPanel(
        GuiComposer composer,
        double width,
        double height
    )
    {
        double row = height < 180 ? 22 : 28;
        double left = 14;
        double top = 38;
        double column = Math.Max(132, (width - 42) / 3);
        double switchWidth = Math.Min(50, Math.Max(42, column * 0.22));
        double labelWidth = Math.Max(70, column - switchWidth - 6);
        double contentOffset = Math.Clamp(settingsScrollOffset, 0, SettingsScrollMaximum(width, height));
        composer
            .AddStaticText(
                "SETTINGS",
                AtlasUiStyle.TitleFont(15),
                ElementBounds.Fixed(left, 9, 190, 24)
            )
            .AddAtlasButton(
                "×",
                CloseSettingsModal,
                ElementBounds.Fixed(width - 42, 7, 32, 28),
                "settings-close",
                AtlasButtonStyle.Icon
            );

        AddSettingSwitch(composer, "Map layers", OnMapLayersToggled, "map-layers", left, top - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Search loaded data", OnSearchModeToggled, "search-mode", left, top + row - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Animations", OnAnimationsToggled, "animations", left, top + row * 2 - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Skip transitions", OnSkipOpeningAnimationToggled, "skip-opening-animation", left, top + row * 3 - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Live clouds", OnCloudsToggled, "clouds", left, top + row * 4 - contentOffset, column, row, labelWidth, switchWidth);

        double middle = left + column + 8;
        AddSettingSwitch(composer, "3D scroll", OnRenderOnScrollToggled, "render-on-scroll", middle, top - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Scroll weather", OnScrollRealtimeWeatherToggled, "scroll-realtime-weather", middle, top + row - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Live sun", OnLiveLightingToggled, "live-lighting", middle, top + row * 2 - contentOffset, column, row, labelWidth, switchWidth);
        composer
            .AddStaticText("Fixed sun hour", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(middle, top + row * 3 + 3 - contentOffset, labelWidth, row - 4))
            .AddAtlasSlider(OnFixedSunHourChanged, ElementBounds.Fixed(middle + labelWidth - 4, top + row * 3 - contentOffset, column - labelWidth + 4, row), "fixed-sun-hour");
        composer
            .AddStaticText("Advanced", AtlasUiStyle.LabelFont(10), ElementBounds.Fixed(middle, top + row * 4 - contentOffset, 90, row))
            .AddAtlasButton("Performance", OpenPerformanceModal, ElementBounds.Fixed(middle + 72, top + row * 4 - contentOffset, Math.Max(70, column - 76), row), "performance-open", AtlasButtonStyle.Compact);
        composer.AddAtlasButton("Visual lab", OpenVisualLab, ElementBounds.Fixed(middle, top + row * 5 - contentOffset, column, row), "visual-lab-open", AtlasButtonStyle.Compact);

        double right = middle + column + 8;
        AddSettingSwitch(composer, "Living models", OnLivingEntitiesToggled, "entities", right, top - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Players", OnPlayersToggled, "players", right, top + row - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Animals", OnAnimalsToggled, "animals", right, top + row * 2 - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "Hostile mobs", OnMobsToggled, "mobs", right, top + row * 3 - contentOffset, column, row, labelWidth, switchWidth);
        AddSettingSwitch(composer, "NPCs", OnNpcsToggled, "npcs", right, top + row * 4 - contentOffset, column, row, labelWidth, switchWidth);
        if (CreativeCheatSettingsAvailable)
        {
            composer.AddAtlasButton("Creative / Cheat", OpenCreativeSettingsModal, ElementBounds.Fixed(right, top + row * 5 - contentOffset, column, row), "creative-settings-button", AtlasButtonStyle.Compact);
        }
        composer.AddDynamicText(
            SettingsScrollMaximum(width, height) > 0 ? "Scroll panel for more controls" : "",
            AtlasUiStyle.DetailFont(9),
            ElementBounds.Fixed(left, height - 19, Math.Max(120, width - 60), 14),
            "settings-scroll-status"
        );
    }

    private double SettingsScrollMaximum(double width, double height) => height < 180 ? 74 : 0;

    private static void AddSettingSwitch(
        GuiComposer composer,
        string label,
        Action<bool> callback,
        string key,
        double x,
        double y,
        double columnWidth,
        double rowHeight,
        double labelWidth,
        double switchWidth
    )
    {
        composer
            .AddStaticText(label, AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(x, y + 4, labelWidth, rowHeight - 6))
            .AddAtlasSwitch(callback, ElementBounds.Fixed(x + columnWidth - switchWidth, y, switchWidth, rowHeight), key);
    }

    private void ComposeScreenshotBottomPanel(GuiComposer composer, double width, double height)
    {
        bool compact = width < 460;
        composer
            .AddStaticText("SCREENSHOT OPTIONS", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(14, 9, 240, 24))
            .AddAtlasButton("×", CloseScreenshotOptions, ElementBounds.Fixed(width - 42, 7, 32, 28), "shot-toggle", AtlasButtonStyle.Icon)
            .AddStaticText("Resolution", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(16, 43, 72, 20))
            .AddAtlasChoice(new[] { "1", "2", "3", "4", "5", "6", "7", "8" }, new[] { "1x", "2x", "3x", "4x", "5x", "6x", "7x", "8x" }, ScreenshotScaleIndex, OnScreenshotScaleChanged, ElementBounds.Fixed(86, 36, compact ? 130 : 150, 34), "shot-scale")
            .AddStaticText("Capture area", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(compact ? 16 : 254, 43, 76, 20))
            .AddAtlasChoice(new[] { "100", "75", "50", "25" }, new[] { "100%", "75%", "50%", "25%" }, ScreenshotCaptureAreaIndex, OnScreenshotCaptureAreaChanged, ElementBounds.Fixed(compact ? 86 : 330, 36, compact ? 130 : 150, 34), "shot-area")
            .AddAtlasButton("TAKE", TakeScreenshot, ElementBounds.Fixed(width - 104, 36, 88, 34), "shot-take", AtlasButtonStyle.Compact)
            .AddDynamicText("", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, 76, Math.Max(140, width - 32), Math.Max(38, height - 96)), "shot-preview")
            .AddDynamicText("", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, height - 18, Math.Max(140, width - 32), 14), "shot-status");
    }

    private void ComposeMapBottomPanel(GuiComposer composer, double width, double height)
    {
        bool compact = width < 460;
        double selectorX = compact ? 16 : 82;
        double selectorY = compact ? 42 : 38;
        double selectorWidth = Math.Max(120, width - (compact ? 32 : 98));
        double statusY = compact ? 82 : 78;
        composer
            .AddStaticText("MAP OPTIONS", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(14, 9, 220, 24))
            .AddAtlasButton("×", CloseMapOptions, ElementBounds.Fixed(width - 42, 7, 32, 28), "map-layer-toggle", AtlasButtonStyle.Icon);
        if (!compact)
        {
            composer.AddStaticText(
                "Layer",
                AtlasUiStyle.DetailFont(10),
                ElementBounds.Fixed(16, 43, 58, 20)
            );
        }
        composer
            .AddDropDown(AtlasMapLayerInfo.Values, AtlasMapLayerInfo.Names, (int)activeMapLayer, OnMapLayerChanged, ElementBounds.Fixed(selectorX, selectorY, selectorWidth, 34), "map-layer")
            .AddDynamicText("", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, statusY, Math.Max(120, width - 32), 16), "layer-status")
            .AddDynamicText(activeMapLayer.DetailedLegend(), AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, statusY + 16, Math.Max(120, width - 32), 16), "layer-legend");
        if (activeMapLayer == AtlasMapLayer.OreDensity && CreativeCheatSettingsAvailable)
        {
            GetOreFilterOptions(out string[] values, out string[] names, out int selectedIndex);
            composer
                .AddStaticText("Ore", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(16, compact ? 137 : 114, 64, 20))
                .AddDropDown(values, names, selectedIndex, OnOreFilterChanged, ElementBounds.Fixed(78, compact ? 132 : 109, Math.Max(120, width - 94), 34), "ore-filter");
        }
    }

    private void ComposeSearchBottomPanel(GuiComposer composer, double width, double height)
    {
        composer
            .AddStaticText("SEARCH", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(14, 9, 130, 24))
            .AddAtlasButton("×", CloseSearchPanel, ElementBounds.Fixed(width - 42, 7, 32, 28), "search-close", AtlasButtonStyle.Icon)
            .AddAtlasTextInput(ElementBounds.Fixed(16, 38, Math.Max(120, width - 82), 34), OnSearchTextChanged, AtlasUiStyle.InputFont(12), "search-input")
            .AddAtlasButton("×", ClearSearch, ElementBounds.Fixed(width - 58, 38, 42, 34), "search-clear", AtlasButtonStyle.Icon)
            .AddDynamicText("", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, height - 30, Math.Max(120, width - 32), 22), "search-status");
        composer.GetTextInput("search-input")?.SetMaxLength(80);
        composer.GetTextInput("search-input")?.SetPlaceHolderText(
            $"Block, creature, player or item — {searchController.SearchLanguageSummary}"
        );
    }

    private void ComposeInstrumentBottomPanel(GuiComposer composer, double width, double height)
    {
        composer
            .AddStaticText("INSTRUMENT", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(14, 9, 160, 24))
            .AddAtlasButton("×", CloseInstrumentPanel, ElementBounds.Fixed(width - 42, 7, 32, 28), "instrument-close", AtlasButtonStyle.Icon)
            .AddStaticText("Handheld display", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(16, 48, 130, 20))
            .AddAtlasChoice(new[] { "off", "compass", "time" }, new[] { "Off", "Compass", "Time" }, HandheldInstrumentChoiceIndex, OnHandheldInstrumentChoiceChanged, ElementBounds.Fixed(154, 40, Math.Max(120, width - 170), 34), "handheld-instrument")
            .AddStaticText("Compass and Time share one physical wooden shell.", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, height - 26, Math.Max(120, width - 32), 18), "instrument-note");
    }

    private void ComposePerformanceBottomPanel(GuiComposer composer, double width, double height)
    {
        composer
            .AddStaticText("PERFORMANCE", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(44, 9, 180, 24))
            .AddAtlasButton("‹", ClosePerformanceModal, ElementBounds.Fixed(10, 7, 32, 28), "performance-back", AtlasButtonStyle.Icon)
            .AddStaticText("Atlas lighting", AtlasUiStyle.DetailFont(11), ElementBounds.Fixed(16, 45, width - 84, 22))
            .AddAtlasSwitch(OnPerformanceLightingToggled, ElementBounds.Fixed(width - 62, 40, 50, 28), "performance-lighting")
            .AddStaticText("Hide vegetation", AtlasUiStyle.DetailFont(11), ElementBounds.Fixed(16, 78, width - 84, 22))
            .AddAtlasSwitch(OnHideVegetationToggled, ElementBounds.Fixed(width - 62, 73, 50, 28), "hide-vegetation")
            .AddStaticText("Developer visual controls only affect the atlas framebuffer.", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, height - 25, Math.Max(120, width - 32), 18), "performance-note");
    }

    private void ComposeCreativeBottomPanel(GuiComposer composer, double width, double height)
    {
        composer
            .AddStaticText("CREATIVE / CHEAT", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(14, 9, 220, 24))
            .AddAtlasButton("×", CloseCreativeSettingsModal, ElementBounds.Fixed(width - 42, 7, 32, 28), "creative-settings-close", AtlasButtonStyle.Icon)
            .AddStaticText("Cave mode", AtlasUiStyle.DetailFont(11), ElementBounds.Fixed(16, 46, width - 84, 22))
            .AddAtlasSwitch(OnCaveModeToggled, ElementBounds.Fixed(width - 62, 41, 50, 28), "cave-mode")
            .AddStaticText("Lock camera angle", AtlasUiStyle.DetailFont(11), ElementBounds.Fixed(16, 79, width - 84, 22))
            .AddAtlasSwitch(OnCameraAngleLockToggled, ElementBounds.Fixed(width - 62, 74, 50, 28), "camera-angle-lock")
            .AddStaticText("Available only in Creative or accepted Cheat Mode.", AtlasUiStyle.DetailFont(9), ElementBounds.Fixed(16, height - 24, Math.Max(120, width - 32), 17), "creative-note");
    }

    private void ComposeVisualLabBottomPanel(GuiComposer composer, double width, double height)
    {
        composer
            .AddStaticText("ATLAS VISUAL LAB", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(44, 9, 220, 24))
            .AddAtlasButton("‹", CloseVisualLab, ElementBounds.Fixed(10, 7, 32, 28), "visual-lab-back", AtlasButtonStyle.Icon)
            .AddStaticText("Exposure", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(16, 45, 100, 20))
            .AddAtlasSlider(OnAtlasExposureChanged, ElementBounds.Fixed(118, 38, Math.Max(120, width - 134), 32), "atlas-exposure")
            .AddStaticText("Cave mask", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(16, 82, 100, 20))
            .AddAtlasSlider(OnCaveMaskBrightnessChanged, ElementBounds.Fixed(118, 75, Math.Max(120, width - 134), 32), "cave-mask-brightness")
            .AddAtlasButton("Reset tuning", ResetVisualTuning, ElementBounds.Fixed(16, height - 35, Math.Max(120, width - 32), 28), "visual-lab-reset", AtlasButtonStyle.Compact);
        ConfigureVisualLabSliders();
    }

    private void ComposeUnitBottomPanel(GuiComposer composer, double width, double height)
    {
        composer
            .AddStaticText("UNIT", AtlasUiStyle.TitleFont(15), ElementBounds.Fixed(14, 9, 160, 24))
            .AddAtlasButton("×", CloseUnitInspection, ElementBounds.Fixed(width - 42, 7, 32, 28), "unit-close", AtlasButtonStyle.Icon)
            .AddDynamicText("", AtlasUiStyle.LabelFont(12), ElementBounds.Fixed(16, 42, Math.Max(120, width - 32), 20), "unit-name")
            .AddDynamicText("", AtlasUiStyle.DetailFont(10), ElementBounds.Fixed(16, 68, Math.Max(120, width - 32), Math.Max(28, height - 82)), "unit-details");
    }

    private void ComposeOverlay(bool viewportOnly = false)
    {
        ComposeCompactOverlay();
        return;

#if false
        ElementBounds root = ElementBounds.Fill;
        double guiScale = Math.Max(0.5, RuntimeEnv.GUIScale);
        AtlasViewportBounds viewport = AtlasViewport;
        double contentX = config.RenderOnScroll ? viewport.X / guiScale : 0;
        double contentY = config.RenderOnScroll ? viewport.Y / guiScale : 0;
        double guiWidth = viewport.Width / guiScale;
        double relativeActionX = Math.Max(18, guiWidth - 232);
        double actionX = contentX + relativeActionX;
        double compassY = contentY + 56;
        double hideUiY = contentY + (CreativeCheatSettingsAvailable ? 148 : 102);
        double screenshotY = hideUiY + 46;
        double headerWidth = Math.Min(
            430,
            Math.Max(1, relativeActionX - 20)
        );
        bool compactHeader = headerWidth < 390;
        bool narrowHeader = headerWidth < 230;
        double headerHeight = narrowHeader ? 92 : compactHeader ? 78 : 50;
        double titleY = narrowHeader ? 20 : 25;
        double versionX = narrowHeader ? 30 : 174;
        double versionY = narrowHeader ? 48 : 18;
        double statusX = compactHeader ? 30 : 234;
        double statusY = narrowHeader ? 68 : compactHeader ? 52 : 28;
        double statusWidth = Math.Max(
            54,
            headerWidth - statusX - 18
        );

        overlay = capi.Gui.CreateCompo("modernatlas-3d", root)
            .AddStaticCustomDraw(
                ElementBounds.Fixed(contentX + 12, contentY + 10, headerWidth, headerHeight),
                AtlasUiStyle.DrawCard
            )
            .AddStaticText(
                "MODERNATLAS",
                AtlasUiStyle.TitleFont(narrowHeader ? 16 : 20),
                ElementBounds.Fixed(
                    contentX + 30,
                    contentY + titleY,
                    Math.Max(94, headerWidth - 48),
                    30
                )
            )
            .AddStaticText(
                "v0.6.7",
                AtlasUiStyle.DetailFont(9),
                ElementBounds.Fixed(contentX + versionX, contentY + versionY, 52, 18)
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(
                    contentX + statusX,
                    contentY + statusY,
                    statusWidth,
                    24
                ),
                "status"
            )
            .AddAtlasButton(
                "SETTINGS",
                ToggleSettingsModal,
                ElementBounds.Fixed(actionX, contentY + 10, 128, 44),
                "settings-button"
            )
            .AddAtlasButton(
                "EXIT",
                CloseAtlas,
                ElementBounds.Fixed(actionX + 136, contentY + 10, 78, 44),
                "exit-button"
            )
            .AddAtlasButton(
                "INSTRUMENT",
                ToggleCompass,
                ElementBounds.Fixed(actionX, compassY, 214, 44),
                "compass-button",
                AtlasButtonStyle.Dark
            )
            .AddAtlasButton(
                "HIDE UI",
                HideInterface,
                ElementBounds.Fixed(actionX, hideUiY, 214, 44),
                "hide-ui-button",
                AtlasButtonStyle.Dark
            )
            .AddAtlasButton(
                "SCREENSHOT",
                TakeScreenshot,
                ElementBounds.Fixed(actionX, screenshotY, 214, 44),
                "screenshot-button",
                AtlasButtonStyle.Dark
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(9),
                ElementBounds.Fixed(actionX, screenshotY + 50, 214, 60),
                "screenshot-status"
            )
            .Compose(false);

        creativeSettingsShortcut = capi.Gui.CreateCompo(
                "modernatlas-creative-settings-shortcut",
                ElementBounds.Fill
            )
            .AddAtlasButton(
                "CREATIVE / CHEAT SETTINGS",
                OpenCreativeSettingsModal,
                ElementBounds.Fixed(actionX, contentY + 102, 214, 44),
                "creative-settings-button"
            )
            .Compose(false);

        ElementBounds searchRoot = ElementBounds.Fixed(
            contentX + 18,
            contentY + 112,
            560,
            82
        );
        searchPanelBounds = searchRoot;
        searchPanel = capi.Gui.CreateCompo("modernatlas-search", searchRoot)
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 560, 82), AtlasUiStyle.DrawCard)
            .AddStaticText(
                "SEARCH",
                AtlasUiStyle.LabelFont(12),
                ElementBounds.Fixed(18, 17, 124, 24)
            )
            .AddAtlasTextInput(
                ElementBounds.Fixed(142, 10, 328, 40),
                OnSearchTextChanged,
                AtlasUiStyle.InputFont(13),
                "search-input"
            )
            .AddAtlasButton(
                "×",
                ClearSearch,
                ElementBounds.Fixed(478, 8, 64, 44),
                "search-clear",
                AtlasButtonStyle.Icon
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(18, 55, 524, 20),
                "search-status"
            )
            .Compose(false);
        searchPanel.GetTextInput("search-input")?.SetMaxLength(80);
        searchPanel.GetTextInput("search-input")?.SetPlaceHolderText(
            $"Block, creature, player or item — {searchController.SearchLanguageSummary}"
        );

        ComposeMapLayerPanel(
            contentX,
            contentY + (SearchModeActive ? 202 : headerHeight + 22),
            Math.Min(560, Math.Max(200, relativeActionX - 36))
        );

        // The screenshot panel stacks in the left column below the map-layer
        // panel (or the search panel / header when those are hidden).
        double screenshotPanelY = contentY + headerHeight + 30;
        if (MapLayerControlsVisible && mapLayerPanelBounds != null)
        {
            screenshotPanelY = mapLayerPanelBounds.fixedY
                + mapLayerPanelBounds.fixedHeight + 8;
        }
        else if (SearchModeActive && searchPanelBounds != null)
        {
            screenshotPanelY = searchPanelBounds.fixedY
                + searchPanelBounds.fixedHeight + 8;
        }
        ComposeScreenshotPanel(contentX, screenshotPanelY);

        ElementBounds unitRoot = config.RenderOnScroll
            ? ElementBounds.Fixed(
                contentX + guiWidth - 394,
                contentY + Math.Max(150, viewport.Height / guiScale * 0.5 - 129),
                370,
                258
            )
            : ElementBounds.Fixed(0, 0, 370, 258)
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedOffset(-24, 0);
        unitPanel = capi.Gui.CreateCompo("modernatlas-unit-inspection", unitRoot)
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 370, 258), AtlasUiStyle.DrawCard)
            .AddDynamicText(
                "",
                AtlasUiStyle.TitleFont(18),
                ElementBounds.Fixed(24, 22, 260, 32),
                "unit-name"
            )
            .AddAtlasButton(
                "×",
                CloseUnitInspection,
                ElementBounds.Fixed(310, 12, 48, 44),
                "unit-close",
                AtlasButtonStyle.Icon
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(22, 62, 326, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(24, 76, 322, 160),
                "unit-details"
            )
            .Compose();

        if (viewportOnly) return;

        ElementBounds modalRoot = ElementBounds.Fixed(0, 0, 430, 800)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        settingsModal = capi.Gui.CreateCompo("modernatlas-settings", modalRoot)
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 430, 800), AtlasUiStyle.DrawCard)
            .AddStaticText(
                "SETTINGS",
                AtlasUiStyle.TitleFont(20),
                ElementBounds.Fixed(26, 24, 260, 30)
            )
            .AddAtlasButton(
                "×",
                CloseSettingsModal,
                ElementBounds.Fixed(366, 12, 50, 46),
                "settings-close",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "DISPLAY",
                AtlasUiStyle.LabelFont(11),
                ElementBounds.Fixed(26, 66, 180, 22)
            )
            .AddStaticText(
                "Map layer",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 94, 240, 26)
            )
            .AddAtlasSwitch(
                OnMapLayersToggled,
                ElementBounds.Fixed(350, 88, 62, 38),
                "map-layers"
            )
            .AddStaticText(
                "Search loaded map (Creative/Cheat)",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 130, 310, 26)
            )
            .AddAtlasSwitch(
                OnSearchModeToggled,
                ElementBounds.Fixed(350, 124, 62, 38),
                "search-mode"
            )
            .AddStaticText(
                "Atlas animations",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 166, 240, 26)
            )
            .AddAtlasSwitch(
                OnAnimationsToggled,
                ElementBounds.Fixed(350, 160, 62, 38),
                "animations"
            )
            .AddStaticText(
                "Performance",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 202, 170, 26)
            )
            .AddAtlasButton(
                "OPEN",
                OpenPerformanceModal,
                ElementBounds.Fixed(198, 194, 214, 40),
                "performance-open",
                AtlasButtonStyle.Dark
            )
            .AddStaticText(
                "Skip scroll transitions",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 238, 260, 26)
            )
            .AddAtlasSwitch(
                OnSkipOpeningAnimationToggled,
                ElementBounds.Fixed(350, 232, 62, 38),
                "skip-opening-animation"
            )
            .AddStaticText(
                "Live 3D clouds",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 274, 240, 26)
            )
            .AddAtlasSwitch(
                OnCloudsToggled,
                ElementBounds.Fixed(350, 268, 62, 38),
                "clouds"
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 311, 378, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "LIVING MODELS",
                AtlasUiStyle.LabelFont(11),
                ElementBounds.Fixed(26, 324, 200, 22)
            )
            .AddStaticText(
                "Living entities",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 352, 240, 26)
            )
            .AddAtlasSwitch(
                OnLivingEntitiesToggled,
                ElementBounds.Fixed(350, 346, 62, 38),
                "entities"
            )
            .AddStaticText(
                "Players",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 388, 200, 24)
            )
            .AddAtlasSwitch(
                OnPlayersToggled,
                ElementBounds.Fixed(350, 382, 62, 38),
                "players"
            )
            .AddStaticText(
                "Animals",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 422, 200, 24)
            )
            .AddAtlasSwitch(
                OnAnimalsToggled,
                ElementBounds.Fixed(350, 416, 62, 38),
                "animals"
            )
            .AddStaticText(
                "Hostile mobs",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 456, 200, 24)
            )
            .AddAtlasSwitch(
                OnMobsToggled,
                ElementBounds.Fixed(350, 450, 62, 38),
                "mobs"
            )
            .AddStaticText(
                "NPCs",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 490, 200, 24)
            )
            .AddAtlasSwitch(
                OnNpcsToggled,
                ElementBounds.Fixed(350, 484, 62, 38),
                "npcs"
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 526, 378, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "GAMEPLAY • SCROLL",
                AtlasUiStyle.LabelFont(11),
                ElementBounds.Fixed(26, 538, 220, 22)
            )
            .AddStaticText(
                "Render on 3D scroll",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 564, 250, 24)
            )
            .AddAtlasSwitch(
                OnRenderOnScrollToggled,
                ElementBounds.Fixed(350, 555, 62, 38),
                "render-on-scroll"
            )
            .AddStaticText(
                "Realtime Weather on Scroll",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 598, 270, 24)
            )
            .AddAtlasSwitch(
                OnScrollRealtimeWeatherToggled,
                ElementBounds.Fixed(350, 589, 62, 38),
                "scroll-realtime-weather"
            )
            .AddStaticText(
                "Handheld instrument",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 632, 250, 24)
            )
            .AddAtlasChoice(
                new[] { "off", "compass", "time" },
                new[] { "Off", "Compass", "Time" },
                HandheldInstrumentChoiceIndex,
                OnHandheldInstrumentChoiceChanged,
                ElementBounds.Fixed(212, 622, 200, 42),
                "handheld-instrument"
            )
            .AddStaticText(
                "Live world sun direction",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 672, 260, 24)
            )
            .AddAtlasSwitch(
                OnLiveLightingToggled,
                ElementBounds.Fixed(350, 663, 62, 38),
                "live-lighting"
            )
            .AddStaticText(
                "Sun hour (fixed)",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 709, 180, 24)
            )
            .AddAtlasSlider(
                OnFixedSunHourChanged,
                ElementBounds.Fixed(212, 700, 200, 42),
                "fixed-sun-hour"
            )
            .AddAtlasButton(
                "ATLAS VISUAL LAB",
                OpenVisualLab,
                ElementBounds.Fixed(24, 748, 382, 44),
                "visual-lab-open",
                AtlasButtonStyle.Dark
            )
            .Compose();
        settingsModal.GetAtlasSlider("fixed-sun-hour")?.SetValues(
            Math.Clamp(config.FixedSunHour, 0, 23),
            0,
            23,
            1,
            "h"
        );

        ElementBounds performanceRoot = ElementBounds.Fixed(0, 0, 460, 330)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        performanceModal = capi.Gui.CreateCompo(
                "modernatlas-performance",
                performanceRoot
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(0, 0, 460, 330),
                AtlasUiStyle.DrawCard
            )
            .AddStaticText(
                "PERFORMANCE",
                AtlasUiStyle.TitleFont(20),
                ElementBounds.Fixed(72, 24, 300, 30)
            )
            .AddAtlasButton(
                "‹",
                ClosePerformanceModal,
                ElementBounds.Fixed(14, 12, 50, 46),
                "performance-back",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "Reduce atlas GPU work on slower computers.",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(72, 59, 360, 24)
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 88, 408, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "Atlas lighting",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 108, 260, 26)
            )
            .AddAtlasSwitch(
                OnPerformanceLightingToggled,
                ElementBounds.Fixed(370, 101, 64, 40),
                "performance-lighting"
            )
            .AddStaticText(
                "Off uses neutral flat daylight.",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(28, 140, 350, 22)
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 174, 408, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "Hide vegetation",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 198, 260, 26)
            )
            .AddAtlasSwitch(
                OnHideVegetationToggled,
                ElementBounds.Fixed(370, 191, 64, 40),
                "hide-vegetation"
            )
            .AddStaticText(
                "Hides registered plants, bushes and leaves, including mods.",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(28, 233, 404, 42)
            )
            .AddStaticText(
                "Atlas refresh: 60 FPS moving • 12 FPS idle.",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(28, 284, 404, 20)
            )
            .Compose(false);

        ElementBounds creativeRoot = ElementBounds.Fixed(0, 0, 440, 294)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        creativeSettingsModal = capi.Gui.CreateCompo(
                "modernatlas-creative-settings",
                creativeRoot
            )
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 440, 294), AtlasUiStyle.DrawCard)
            .AddStaticText(
                "CREATIVE / CHEAT",
                AtlasUiStyle.TitleFont(20),
                ElementBounds.Fixed(26, 24, 300, 30)
            )
            .AddAtlasButton(
                "×",
                CloseCreativeSettingsModal,
                ElementBounds.Fixed(376, 12, 50, 46),
                "creative-settings-close",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "Creative or server-authorized Cheat Mode only.",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(26, 59, 382, 24)
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 88, 388, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "Cave mode (show underground)",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 108, 300, 26)
            )
            .AddAtlasSwitch(
                OnCaveModeToggled,
                ElementBounds.Fixed(358, 101, 64, 40),
                "cave-mode"
            )
            .AddStaticText(
                "Lock camera angle",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 156, 300, 26)
            )
            .AddAtlasSwitch(
                OnCameraAngleLockToggled,
                ElementBounds.Fixed(358, 149, 64, 40),
                "camera-angle-lock"
            )
            .AddStaticText(
                "Rotation remains free while the current tilt is held.",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(28, 202, 382, 22)
            )
            .Compose(false);

        ElementBounds labRoot = ElementBounds.Fixed(0, 0, 470, 310)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        visualLabModal = capi.Gui.CreateCompo("modernatlas-visual-lab", labRoot)
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 470, 310), AtlasUiStyle.DrawCard)
            .AddStaticText(
                "ATLAS VISUAL LAB",
                AtlasUiStyle.TitleFont(20),
                ElementBounds.Fixed(72, 24, 300, 30)
            )
            .AddAtlasButton(
                "‹",
                CloseVisualLab,
                ElementBounds.Fixed(14, 12, 50, 46),
                "visual-lab-back",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "Atlas-only controls; the normal world is never changed.",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(72, 59, 370, 24)
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 88, 418, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "Exposure",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 108, 170, 26)
            )
            .AddAtlasSlider(
                OnAtlasExposureChanged,
                ElementBounds.Fixed(202, 99, 240, 44),
                "atlas-exposure"
            )
            .AddStaticText(
                "Cave mask brightness",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 160, 174, 26)
            )
            .AddAtlasSlider(
                OnCaveMaskBrightnessChanged,
                ElementBounds.Fixed(202, 151, 240, 44),
                "cave-mask-brightness"
            )
            .AddAtlasButton(
                "RESET VISUAL TUNING",
                ResetVisualTuning,
                ElementBounds.Fixed(26, 230, 418, 48),
                "visual-lab-reset",
                AtlasButtonStyle.Dark
            )
            .Compose();
        ConfigureVisualLabSliders();

        SyncSettingsControls();
        SyncPerformanceControls();
        SyncCreativeSettingsControls();
    }
#endif
    }

    /// <summary>
    /// Compact collapsible screenshot panel for the main atlas UI, placed in
    /// the left column under the search and map-layer panels. Collapsed it is
    /// a slim bar with a quick TAKE action; expanded it exposes the full
    /// styling controls.
    /// </summary>
    private void ComposeScreenshotPanel(double contentX, double contentY)
    {
        const double panelWidth = 560;
        if (screenshotPanelCollapsed)
        {
            ElementBounds root = ElementBounds.Fixed(contentX + 18, contentY, panelWidth, 52);
            screenshotPanelBounds = root;
            screenshotPanel = capi.Gui.CreateCompo("modernatlas-screenshot", root)
                .AddStaticCustomDraw(
                    ElementBounds.Fixed(0, 0, panelWidth, 52),
                    AtlasUiStyle.DrawCard
                )
                .AddAtlasButton(
                    "▸",
                    ToggleScreenshotPanel,
                    ElementBounds.Fixed(8, 6, 40, 40),
                    "shot-toggle",
                    AtlasButtonStyle.Icon
                )
                .AddStaticText(
                    "SCREENSHOT",
                    AtlasUiStyle.LabelFont(12),
                    ElementBounds.Fixed(58, 14, 190, 24)
                )
                .AddDynamicText(
                    "",
                    AtlasUiStyle.DetailFont(9),
                    ElementBounds.Fixed(250, 8, 196, 36),
                    "shot-collapsed-status"
                )
                .AddAtlasButton(
                    "TAKE",
                    TakeScreenshot,
                    ElementBounds.Fixed(450, 6, 104, 40),
                    "shot-take",
                    AtlasButtonStyle.Dark
                )
                .Compose();
            screenshotPanel.GetDynamicText("shot-collapsed-status")
                ?.SetNewText(BuildScreenshotCollapsedStatus());
            return;
        }

        ElementBounds rootExpanded = ElementBounds.Fixed(contentX + 18, contentY, panelWidth, 238);
        screenshotPanelBounds = rootExpanded;
        screenshotPanel = capi.Gui.CreateCompo("modernatlas-screenshot", rootExpanded)
            .AddStaticCustomDraw(
                ElementBounds.Fixed(0, 0, panelWidth, 238),
                AtlasUiStyle.DrawCard
            )
            .AddAtlasButton(
                "▾",
                ToggleScreenshotPanel,
                ElementBounds.Fixed(8, 6, 40, 40),
                "shot-toggle",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "SCREENSHOT",
                AtlasUiStyle.LabelFont(12),
                ElementBounds.Fixed(58, 14, 84, 24)
            )
            .AddStaticText(
                "Resolution",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(148, 18, 70, 24)
            )
            .AddAtlasChoice(
                new[] { "1", "2", "3", "4", "5", "6", "7", "8" },
                new[]
                {
                    "1x", "2x", "3x", "4x", "5x", "6x", "7x", "8x"
                },
                ScreenshotScaleIndex,
                OnScreenshotScaleChanged,
                ElementBounds.Fixed(218, 8, 120, 44),
                "shot-scale"
            )
            .AddStaticText(
                "Capture area",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(18, 66, 100, 24)
            )
            .AddAtlasChoice(
                new[] { "100", "75", "50", "25" },
                new[] { "100%", "75%", "50%", "25%" },
                ScreenshotCaptureAreaIndex,
                OnScreenshotCaptureAreaChanged,
                ElementBounds.Fixed(120, 56, 154, 44),
                "shot-area"
            )
            .AddAtlasButton(
                "TAKE",
                TakeScreenshot,
                ElementBounds.Fixed(450, 56, 104, 44),
                "shot-take",
                AtlasButtonStyle.Dark
            )
            .AddStaticText(
                "Resolution controls the tile grid. Capture area selects a centered part of the current view; the camera returns when done.",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(18, 108, 524, 38)
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(10),
                ElementBounds.Fixed(18, 148, 524, 66),
                "shot-preview"
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(9),
                ElementBounds.Fixed(18, 214, 524, 18),
                "shot-status"
            )
            .Compose();
        ConfigureScreenshotSettingsControls();
    }

    private void ConfigureScreenshotSettingsControls()
    {
        screenshotPanel?.GetAtlasChoice("shot-scale")?.SetSelectedIndex(
            ScreenshotScaleIndex
        );
        screenshotPanel?.GetAtlasChoice("shot-area")?.SetSelectedIndex(
            ScreenshotCaptureAreaIndex
        );
        screenshotPanel?.GetDynamicText("shot-preview")?.SetNewText(
            BuildScreenshotPreviewText()
        );
    }

    private void SyncScreenshotSettingsControls()
    {
        ConfigureScreenshotSettingsControls();
    }

    private bool ToggleScreenshotPanel()
    {
        ToggleScreenshotOptions();
        return true;
    }

    private bool ToggleScreenshotOptions()
    {
        RequestBottomPanel(AtlasPanelSection.ScreenshotOptions);
        return true;
    }

    private bool CloseScreenshotOptions()
    {
        if (ScreenshotOptionsOpen) RequestBottomPanel(AtlasPanelSection.None);
        return true;
    }

    internal bool ScreenshotPanelExpanded => ScreenshotOptionsOpen;

    private string BuildScreenshotCollapsedStatus()
    {
        int scale = Math.Clamp(
            config.ScreenshotScale,
            AtlasTiledScreenshot.MinimumResolutionScale,
            AtlasTiledScreenshot.MaximumResolutionScale
        );
        int area = AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
            config.ScreenshotCaptureAreaPercent
        );
        return $"{scale}x / {area}% area";
    }

    private void RecomposeScreenshotPanel()
    {
        if (ScreenshotOptionsOpen)
        {
            OpenBottomPanelImmediately(AtlasPanelSection.ScreenshotOptions);
        }
    }

    private int ScreenshotScaleIndex => Math.Clamp(
        config.ScreenshotScale,
        AtlasTiledScreenshot.MinimumResolutionScale,
        AtlasTiledScreenshot.MaximumResolutionScale
    ) - AtlasTiledScreenshot.MinimumResolutionScale;

    private int ScreenshotCaptureAreaIndex =>
        AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
            config.ScreenshotCaptureAreaPercent
        ) switch
        {
            100 => 0,
            75 => 1,
            50 => 2,
            _ => 3
        };

    private bool OpenSettingsModal()
    {
        RequestBottomPanel(AtlasPanelSection.Settings);
        return true;
    }

    private bool ToggleSettingsModal() => SettingsHierarchyOpen
        ? CloseSettingsModal()
        : OpenSettingsModal();

    private bool IsSettingsButtonPosition(double x, double y) =>
        overlay?.GetAtlasButton("settings-button")?.Bounds.PointInside(x, y) == true;

    private bool CloseSettingsModal()
    {
        RequestBottomPanel(AtlasPanelSection.None);
        return true;
    }

    private bool OpenCreativeSettingsModal()
    {
        RequestBottomPanel(AtlasPanelSection.Creative);
        return true;
    }

    private bool CloseCreativeSettingsModal()
    {
        RequestBottomPanel(AtlasPanelSection.Settings);
        return true;
    }

    private bool OpenPerformanceModal()
    {
        RequestBottomPanel(AtlasPanelSection.Performance);
        return true;
    }

    private bool ClosePerformanceModal()
    {
        RequestBottomPanel(AtlasPanelSection.Settings);
        return true;
    }

    private bool OpenVisualLab()
    {
        RequestBottomPanel(AtlasPanelSection.VisualLab);
        return true;
    }

    private bool CloseVisualLab()
    {
        RequestBottomPanel(AtlasPanelSection.Settings);
        return true;
    }

    private bool ToggleMapOptions()
    {
        RequestBottomPanel(AtlasPanelSection.MapOptions);
        return true;
    }

    private bool CloseMapOptions()
    {
        if (MapPanelOpen) RequestBottomPanel(AtlasPanelSection.None);
        return true;
    }

    private bool ToggleSearchPanel()
    {
        if (!SearchModeActive)
        {
            OpenSettingsModal();
            return true;
        }
        RequestBottomPanel(AtlasPanelSection.Search);
        return true;
    }

    private bool CloseSearchPanel()
    {
        if (SearchPanelOpen) RequestBottomPanel(AtlasPanelSection.None);
        return true;
    }

    private bool ToggleInstrumentPanel()
    {
        RequestBottomPanel(AtlasPanelSection.Instrument);
        return true;
    }

    private bool CloseInstrumentPanel()
    {
        if (InstrumentPanelOpen) RequestBottomPanel(AtlasPanelSection.None);
        return true;
    }

    private void ConfigureVisualLabSliders()
    {
        visualLabModal.GetAtlasSlider("atlas-exposure")?.SetValues(
            Math.Clamp(config.AtlasExposurePercent, 50, 150),
            50,
            150,
            5,
            "%"
        );
        visualLabModal.GetAtlasSlider("cave-mask-brightness")?.SetValues(
            Math.Clamp(config.CaveMaskBrightnessPercent, 50, 150),
            50,
            150,
            5,
            "%"
        );
    }

    private void SyncVisualLabControls()
    {
        ConfigureVisualLabSliders();
    }

    private bool OnAtlasExposureChanged(int value)
    {
        config.AtlasExposurePercent = Math.Clamp(value, 50, 150);
        saveConfig();
        return true;
    }

    private bool OnCaveMaskBrightnessChanged(int value)
    {
        config.CaveMaskBrightnessPercent = Math.Clamp(value, 50, 150);
        saveConfig();
        return true;
    }

    private bool ResetVisualTuning()
    {
        config.AtlasExposurePercent = 150;
        config.MapLayerOpacityPercent = 75;
        config.CaveMaskBrightnessPercent = 100;
        saveConfig();
        SyncVisualLabControls();
        return true;
    }

    private void OnScreenshotScaleChanged(string value, bool selected)
    {
        if (!selected) return;
        if (int.TryParse(value, out int scale))
        {
            config.ScreenshotScale = Math.Clamp(
                scale,
                AtlasTiledScreenshot.MinimumResolutionScale,
                AtlasTiledScreenshot.MaximumResolutionScale
            );
            saveConfig();
        }
        SyncScreenshotSettingsControls();
    }

    private void OnScreenshotCaptureAreaChanged(string value, bool selected)
    {
        if (!selected) return;
        if (int.TryParse(value, out int captureAreaPercent))
        {
            config.ScreenshotCaptureAreaPercent =
                AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
                    captureAreaPercent
                );
            saveConfig();
        }
        SyncScreenshotSettingsControls();
    }

    private string BuildScreenshotPreviewText()
    {
        int resolutionScale = Math.Clamp(
            config.ScreenshotScale,
            AtlasTiledScreenshot.MinimumResolutionScale,
            AtlasTiledScreenshot.MaximumResolutionScale
        );
        int captureAreaPercent =
            AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
                config.ScreenshotCaptureAreaPercent
            );
        AtlasViewportBounds viewport = AtlasViewport;
        AtlasScreenshotPreview preview = tileScreenshot.GetPreview(
            resolutionScale,
            captureAreaPercent,
            viewport.Width / (float)Math.Max(1, viewport.Height)
        );
        if (!preview.IsValid)
        {
            return "PNG preview unavailable: the game window is too small.";
        }

        string firstLine =
            $"PNG: {preview.OutputWidth} × {preview.OutputHeight} — {preview.OutputMegapixels:0.0} MP";
        string detailLine = preview.WasDownsampled
            ? $"Detail: {preview.EffectiveDetailFactor:0.#}× effective ({preview.RequestedDetailFactor:0.#}× requested) | Area: {captureAreaPercent}%"
            : $"Detail: {preview.RequestedDetailFactor:0.#}× | Area: {captureAreaPercent}% | Grid: {resolutionScale}×{resolutionScale}";
        if (!preview.WasDownsampled)
        {
            return $"{firstLine}\n{detailLine}";
        }

        return $"{firstLine}\n{detailLine}\nWARNING: requested {preview.RequestedWidth} × {preview.RequestedHeight} ({preview.RequestedMegapixels:0.0} MP) exceeds the {AtlasTiledScreenshot.MaximumStitchedPixels / 1_000_000d:0} MP safety limit; the PNG will be reduced.";
    }

    /// <summary>
    /// Starts the tiled capture of the current camera view. Tile zero's
    /// camera is applied immediately; every following tile is applied after
    /// the previous tile's frame was read back.
    /// </summary>
    private bool TakeScreenshot() => StartScreenshotCapture(
        Math.Clamp(
            config.ScreenshotScale,
            AtlasTiledScreenshot.MinimumResolutionScale,
            AtlasTiledScreenshot.MaximumResolutionScale
        ),
        AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
            config.ScreenshotCaptureAreaPercent
        ),
        "Screenshot"
    );

    private bool TakeQuickScreenshot() => StartScreenshotCapture(1, 100, "Quick screenshot");

    private bool StartScreenshotCapture(
        int gridSize,
        int captureAreaPercent,
        string captureLabel
    )
    {
        if (tileScreenshot.Busy)
        {
            screenshotStatusOverride = "Previous screenshot is still being processed…";
            screenshotStatusShownUntilMilliseconds = capi.ElapsedMilliseconds + 4000;
            return true;
        }
        if (atlasFrameCacheTexture?.TextureId <= 0)
        {
            screenshotStatusOverride = "Atlas frame is not ready yet.";
            screenshotStatusShownUntilMilliseconds = capi.ElapsedMilliseconds + 4000;
            return true;
        }

        gridSize = Math.Clamp(
            gridSize,
            AtlasTiledScreenshot.MinimumResolutionScale,
            AtlasTiledScreenshot.MaximumResolutionScale
        );
        captureAreaPercent = AtlasTiledScreenshot.NormalizeCaptureAreaPercent(
            captureAreaPercent
        );
        AtlasViewportBounds viewport = AtlasViewport;
        float viewportAspect = viewport.Width / (float)Math.Max(1, viewport.Height);
        if (!tileScreenshot.StartCapture(
            gridSize,
            captureAreaPercent,
            zoom,
            yawDegrees,
            pitchDegrees,
            viewportAspect
        ))
        {
            screenshotStatusOverride = "Screenshot failed to start; see the log for details.";
            screenshotStatusShownUntilMilliseconds = capi.ElapsedMilliseconds + 4000;
            return true;
        }

        screenshotBaselineZoom = zoom;
        screenshotBaselineTargetZoom = targetZoom;
        screenshotBaselineCenterX = centerX;
        screenshotBaselineCenterY = centerY;
        screenshotBaselineCenterZ = centerZ;
        screenshotBaselineTargetCenterX = targetCenterX;
        screenshotBaselineTargetCenterZ = targetCenterZ;
        screenshotCameraSnapshotted = true;

        // Pin wind, water and cloud counters to one captured frame so every
        // tile renders identical animated surfaces and the stitched image
        // has no moving-water, drifting-cloud or waving-vegetation seams.
        CaptureAnimationFrame();
        screenshotFrozenWindWaveCounter = frozenWindWaveCounter;
        screenshotFrozenWindWaveCounterHighFrequency =
            frozenWindWaveCounterHighFrequency;
        screenshotFrozenWaterStillCounter = frozenWaterStillCounter;
        screenshotFrozenWaterFlowCounter = frozenWaterFlowCounter;
        screenshotFrozenCloudOffset = exactChunkRenderer?.GetLiveCloudOffset();

        pendingScreenshotRequest = true;
        ApplyTileCamera(0);
        // Force a fresh world frame for every tile instead of the throttled
        // idle interval.
        lastAtlasWorldRenderMilliseconds = 0;
        OpenScreenshotProgressModal();
        soundController.PlayPageTouch();
        capi.Logger.Notification(
            "[ModernAtlas] Queued {3}: {0}x{0} tiled atlas screenshot of the centered {1}% area: {2}.",
            gridSize,
            captureAreaPercent,
            BuildScreenshotPreviewText().Replace('\n', ' '),
            captureLabel
        );
        return true;
    }

    /// <summary>
    /// Runs once per rendered atlas frame while the tile grid is captured.
    /// Reads the Primary sub-region for the just-rendered tile, then snaps
    /// the camera to the next tile or restores the original view after the
    /// last one.
    /// </summary>
    private void AdvanceTileScreenshot()
    {
        FrameBufferRef? resolvedFramebuffer = exactChunkRenderer?.ResolvedFramebuffer;
        if (resolvedFramebuffer == null
            || !tileScreenshot.CaptureCurrentFrame(resolvedFramebuffer))
        {
            tileScreenshot.Cancel();
            RestoreScreenshotCamera();
            pendingScreenshotRequest = false;
            screenshotStatusOverride = "Screenshot failed; see the log for details.";
            screenshotStatusShownUntilMilliseconds = capi.ElapsedMilliseconds + 4000;
            CloseScreenshotProgressModal();
            return;
        }

        if (tileScreenshot.CaptureActive)
        {
            ApplyTileCamera(tileScreenshot.CurrentTile);
            lastAtlasWorldRenderMilliseconds = 0;
            return;
        }

        // The last tile is stored; the background stitcher now assembles the
        // image while the camera returns to the player's original view.
        RestoreScreenshotCamera();
        pendingScreenshotRequest = false;
        capi.Logger.Notification(
            "[ModernAtlas] Captured {0} atlas tiles at {1}; stitching in the background.",
            tileScreenshot.TotalTiles,
            tileScreenshot.PendingPath
        );
    }

    private void ApplyTileCamera(int tileIndex)
    {
        tileScreenshot.GetTileCamera(
            tileIndex,
            out float tileZoom,
            out double offsetX,
            out double offsetZ,
            out double offsetY
        );
        zoom = tileZoom;
        targetZoom = tileZoom;
        centerX = screenshotBaselineCenterX + offsetX;
        centerY = screenshotBaselineCenterY + offsetY;
        centerZ = screenshotBaselineCenterZ + offsetZ;
        targetCenterX = centerX;
        targetCenterZ = centerZ;
    }

    private void RestoreScreenshotCamera()
    {
        if (!screenshotCameraSnapshotted) return;
        screenshotCameraSnapshotted = false;
        zoom = screenshotBaselineZoom;
        targetZoom = screenshotBaselineTargetZoom;
        centerX = screenshotBaselineCenterX;
        centerY = screenshotBaselineCenterY;
        centerZ = screenshotBaselineCenterZ;
        targetCenterX = screenshotBaselineTargetCenterX;
        targetCenterZ = screenshotBaselineTargetCenterZ;
    }

    private void CancelScreenshotCapture()
    {
        if (tileScreenshot.Busy)
        {
            tileScreenshot.Cancel();
        }
        RestoreScreenshotCamera();
        pendingScreenshotRequest = false;
        CloseScreenshotProgressModal();
    }

    private void OpenScreenshotProgressModal()
    {
        screenshotProgressModal?.Dispose();
        ElementBounds root = ElementBounds.Fixed(0, 0, 420, 190)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        screenshotProgressModal = capi.Gui.CreateCompo(
                "modernatlas-screenshot-progress",
                root
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(0, 0, 420, 190),
                AtlasUiStyle.DrawCard
            )
            .AddStaticText(
                "SCREENSHOT",
                AtlasUiStyle.TitleFont(17),
                ElementBounds.Fixed(58, 18, 300, 26)
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(22, 54, 376, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddDynamicText(
                "Taking screenshots, please wait…",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(22, 70, 376, 40),
                "shot-progress"
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(9),
                ElementBounds.Fixed(22, 112, 376, 34),
                "shot-result"
            )
            .AddAtlasButton(
                "CANCEL / CLOSE",
                CloseScreenshotProgressModal,
                ElementBounds.Fixed(136, 153, 148, 30),
                "shot-modal-close",
                AtlasButtonStyle.Compact
            )
            .Compose(false);
    }

    /// <summary>Cancels a running capture or dismisses the finished status.</summary>
    private bool CloseScreenshotProgressModal()
    {
        if (screenshotProgressModal == null) return true;
        if (tileScreenshot.Busy)
        {
            tileScreenshot.Cancel();
            RestoreScreenshotCamera();
            pendingScreenshotRequest = false;
            capi.Logger.Notification(
                "[ModernAtlas] The tiled atlas screenshot was cancelled; the camera was restored."
            );
        }
        screenshotProgressModal.Dispose();
        screenshotProgressModal = null;
        return true;
    }

    private void UpdateScreenshotStatusText()
    {
        string? text;
        if (screenshotProgressModal != null)
        {
            AtlasScreenshotJobState screenshotState = tileScreenshot.State;
            if (tileScreenshot.CaptureActive)
            {
                text = $"Taking screenshots, please wait… (tile {tileScreenshot.CurrentTile + 1} of {tileScreenshot.TotalTiles})";
            }
            else if (screenshotState == AtlasScreenshotJobState.Failed)
            {
                text = $"Screenshot failed: {tileScreenshot.LastError ?? "see the log"}";
            }
            else if (screenshotState == AtlasScreenshotJobState.Cancelled)
            {
                text = "Screenshot cancelled; private staging is being removed…";
            }
            else if (screenshotState == AtlasScreenshotJobState.Committed)
            {
                text = tileScreenshot.Busy
                    ? "PNG committed; removing private staging…"
                    : "Screenshot committed.";
            }
            else if (screenshotState == AtlasScreenshotJobState.Validating)
            {
                text = "Validating PNG before commit; please wait…";
            }
            else if (tileScreenshot.Busy)
            {
                AtlasScreenshotPreview preview = tileScreenshot.ActivePreview;
                text = preview.IsValid
                    ? $"Stitching {preview.OutputWidth} × {preview.OutputHeight} PNG; please wait for completion…"
                    : "Stitching and saving the screenshot…";
            }
            else
            {
                text = "Screenshot finished.";
            }
        }
        else
        {
            string? lastPath = tileScreenshot.LastSavedPath;
            string? lastError = tileScreenshot.LastError;
            if (lastPath != null)
            {
                // The saved location stays readable until the atlas closes or
                // a new capture replaces it; a short toast was not readable.
                text = FormatScreenshotStatus("Saved:", lastPath);
            }
            else if (lastError != null)
            {
                text = $"Screenshot error: {lastError}";
            }
            else if (capi.ElapsedMilliseconds < screenshotStatusShownUntilMilliseconds)
            {
                text = screenshotStatusOverride;
            }
            else
            {
                text = null;
            }
        }
        overlay?.GetDynamicText("screenshot-status")?.SetNewText(text ?? "");
        screenshotPanel?.GetDynamicText("shot-status")?.SetNewText(text ?? "");

        if (screenshotProgressModal == null) return;
        screenshotProgressModal.GetDynamicText("shot-progress")?.SetNewText(
            text ?? ""
        );
        string result = "";
        if (!tileScreenshot.Busy)
        {
            if (tileScreenshot.LastSavedPath != null)
            {
                result = FormatScreenshotStatus("Saved:", tileScreenshot.LastSavedPath);
            }
            else if (tileScreenshot.LastError != null)
            {
                result = $"Error: {tileScreenshot.LastError}";
            }
        }
        else if (tileScreenshot.State == AtlasScreenshotJobState.Failed)
        {
            result = $"Error: {tileScreenshot.LastError ?? "see the log"}";
        }
        screenshotProgressModal.GetDynamicText("shot-result")?.SetNewText(result);
    }

    /// <summary>
    /// Wraps the full file path into short lines inside the atlas button
    /// column so the text never extends under the physical scroll roller.
    /// </summary>
    private static string FormatScreenshotStatus(string prefix, string path)
    {
        const int maximumLineLength = 36;
        string[] wrapped = WrapScreenshotPath(path, maximumLineLength);
        if (wrapped.Length == 0) return prefix;
        if (wrapped.Length == 1) return $"{prefix} {wrapped[0]}";

        StringBuilder builder = new(prefix);
        builder.Append(' ');
        builder.Append(wrapped[0]);
        for (int i = 1; i < wrapped.Length; i++)
        {
            builder.Append('\n');
            builder.Append(wrapped[i]);
        }
        return builder.ToString();
    }

    private static string[] WrapScreenshotPath(string path, int maximumLineLength)
    {
        if (string.IsNullOrEmpty(path)) return Array.Empty<string>();

        List<string> lines = new();
        string remaining = path;
        while (remaining.Length > maximumLineLength)
        {
            int cut = remaining.LastIndexOf(
                System.IO.Path.DirectorySeparatorChar,
                maximumLineLength - 1
            );
            if (cut <= 0) cut = maximumLineLength;
            lines.Add(remaining[..cut]);
            remaining = remaining[cut..];
        }
        if (remaining.Length > 0) lines.Add(remaining);
        return lines.ToArray();
    }

    private bool CloseUnitInspection()
    {
        selectedEntityId = null;
        if (bottomPanelSection == AtlasPanelSection.Unit)
        {
            RequestBottomPanel(AtlasPanelSection.None);
        }
        return true;
    }

    private void RenderUnitInspection(float deltaTime)
    {
        UpdateUnitInspectionText();
        if (unitPanel != null) unitPanel.Render(deltaTime);
    }

    private void UpdateUnitInspectionText()
    {
        if (selectedEntityId == null) return;
        if (!UnitInspectionEnabled || exactChunkRenderer == null)
        {
            selectedEntityId = null;
            return;
        }

        AtlasRenderedEntity? selected = null;
        foreach (AtlasRenderedEntity candidate in exactChunkRenderer.LastRenderedEntities)
        {
            if (candidate.Entity.EntityId == selectedEntityId.Value)
            {
                selected = candidate;
                break;
            }
        }
        if (selected == null)
        {
            selectedEntityId = null;
            return;
        }

        Entity entity = selected.Value.Entity;
        string name = AtlasSafeDisplayName.ForEntity(entity);

        double dx = entity.Pos.X - capi.World.Player.Entity.Pos.X;
        double dy = entity.Pos.Y - capi.World.Player.Entity.Pos.Y;
        double dz = entity.Pos.Z - capi.World.Player.Entity.Pos.Z;
        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        string health = AtlasEntityInspectionAdapter.TryGetHealth(
            entity,
            out float currentHealth,
            out float maximumHealth
        )
            ? FormattableString.Invariant(
                $"Health: {currentHealth:0.#} / {maximumHealth:0.#} ({Math.Clamp(currentHealth / maximumHealth * 100f, 0, 100):0}%)"
            )
            : "Health: unavailable";
        string category = selected.Value.Kind switch
        {
            AtlasEntityKind.Player => "Player",
            AtlasEntityKind.Animal => "Animal",
            AtlasEntityKind.Mob => "Hostile mob",
            AtlasEntityKind.Npc => "NPC",
            _ => "Living entity"
        };
        string details = FormattableString.Invariant(
            $"{health}\nCategory: {category}\nType: {entity.Code}\nEntity ID: {entity.EntityId}\nPosition: {entity.Pos.X:0.0}, {entity.Pos.Y:0.0}, {entity.Pos.Z:0.0}\nDistance: {distance:0.0} blocks"
        );

        unitPanel?.GetDynamicText("unit-name").SetNewText(name);
        unitPanel?.GetDynamicText("unit-details").SetNewText(details);
    }

    private bool TrySelectRenderedEntity(int mouseX, int mouseY)
    {
        if (!UnitInspectionEnabled || exactChunkRenderer == null) return false;

        AtlasRenderedEntity? best = null;
        double bestDistanceSquared = double.MaxValue;
        double bestDepth = double.MaxValue;
        double guiScale = Math.Max(0.5, RuntimeEnv.GUIScale);
        foreach (AtlasRenderedEntity candidate in exactChunkRenderer.LastRenderedEntities)
        {
            Entity entity = candidate.Entity;
            if (!entity.Alive) continue;

            float selectionMiddle = (entity.SelectionBox.Y1 + entity.SelectionBox.Y2) * 0.5f;
            if (!TryProjectAtlasPosition(
                entity.Pos.X,
                entity.Pos.Y + selectionMiddle,
                entity.Pos.Z,
                out double screenX,
                out double screenY,
                out double depth
            ))
            {
                continue;
            }

            double modelHeight = Math.Max(0.5, entity.SelectionBox.Y2 - entity.SelectionBox.Y1);
            double pixelsPerBlock = AtlasViewport.Height / (2.0 * zoom);
            double hitRadius = Math.Clamp(
                modelHeight * pixelsPerBlock * 0.5 + 10 * guiScale,
                14 * guiScale,
                36 * guiScale
            );
            double deltaX = mouseX - screenX;
            double deltaY = mouseY - screenY;
            double distanceSquared = deltaX * deltaX + deltaY * deltaY;
            if (distanceSquared > hitRadius * hitRadius
                || distanceSquared > bestDistanceSquared + 0.01
                || (Math.Abs(distanceSquared - bestDistanceSquared) <= 0.01
                    && depth >= bestDepth))
            {
                continue;
            }

            best = candidate;
            bestDistanceSquared = distanceSquared;
            bestDepth = depth;
        }

        if (best == null)
        {
            selectedEntityId = null;
            return false;
        }

        selectedEntityId = best.Value.Entity.EntityId;
        capi.Logger.Notification(
            "[ModernAtlas] Selected loaded {0} entity {1} for atlas inspection.",
            best.Value.Kind,
            selectedEntityId.Value
        );
        RequestBottomPanel(AtlasPanelSection.Unit);
        return true;
    }

    private bool TryProjectAtlasPosition(
        double worldX,
        double worldY,
        double worldZ,
        out double screenX,
        out double screenY,
        out double depth
    )
    {
        double deltaX = worldX - centerX;
        double deltaY = worldY - centerY;
        double deltaZ = worldZ - centerZ;
        double yaw = yawDegrees * GameMath.DEG2RAD;
        double pitch = pitchDegrees * GameMath.DEG2RAD;
        double sinYaw = Math.Sin(yaw);
        double cosYaw = Math.Cos(yaw);
        double sinPitch = Math.Sin(pitch);
        double cosPitch = Math.Cos(pitch);
        double projectedRight = deltaX * cosYaw - deltaZ * sinYaw;
        double projectedUp = -deltaX * sinYaw * sinPitch
            + deltaY * cosPitch
            - deltaZ * cosYaw * sinPitch;
        depth = deltaX * -sinYaw * cosPitch
            + deltaY * -sinPitch
            + deltaZ * -cosYaw * cosPitch;
        AtlasViewportBounds viewport = AtlasViewport;
        double pixelsPerBlock = viewport.Height / (2.0 * zoom);
        screenX = viewport.X + viewport.Width / 2.0
            + projectedRight * pixelsPerBlock;
        screenY = viewport.Y + viewport.Height / 2.0
            - projectedUp * pixelsPerBlock;
        return screenX >= viewport.X
            && screenX <= viewport.Right
            && screenY >= viewport.Y
            && screenY <= viewport.Bottom;
    }

    private void OnSearchTextChanged(string text)
    {
        searchController.SetQuery(text);
    }

    private bool ClearSearch()
    {
        searchController.Clear();
        GuiElementTextInput? input = searchPanel?.GetTextInput("search-input");
        if (input != null && input.GetText().Length > 0)
        {
            input.SetValue("", true);
        }
        return true;
    }

    private bool CloseAtlas()
    {
        return compassRenderer.BeginStowingForClose(() => { requestClose(); })
            || requestClose();
    }

    private bool HideInterface()
    {
        ResetPointerDrag();
        ClearOreHover();
        bottomPanel?.UnfocusOwnElements();
        selectedEntityId = null;
        if (bottomPanelSection != AtlasPanelSection.None)
        {
            StartClosingBottomPanel(AtlasPanelSection.None);
        }
        interfaceHidden = true;
        return true;
    }

    private bool HandleEscape()
    {
        if (screenshotProgressModal != null)
        {
            CloseScreenshotProgressModal();
            return true;
        }
        if (interfaceHidden)
        {
            interfaceHidden = false;
            lastInterfaceRestoreMilliseconds = capi.ElapsedMilliseconds;
            ResetPointerDrag();
            return true;
        }
        if (capi.ElapsedMilliseconds - lastInterfaceRestoreMilliseconds < 200)
        {
            // Some input paths dispatch both OnKeyDown and OnEscapePressed for
            // one key stroke. Do not restore the UI and close the atlas from
            // that same Escape press.
            return true;
        }
        return CloseAtlas();
    }

    private void ComposeMapLayerPanel(
        double contentX,
        double contentY,
        double panelWidth
    )
    {
        bool oreControls = activeMapLayer == AtlasMapLayer.OreDensity
            && CreativeCheatSettingsAvailable;
        bool compact = panelWidth < 420;
        double height = compact
            ? oreControls ? 218 : 136
            : oreControls ? 150 : 104;
        double selectorX = compact ? 18 : 142;
        double selectorY = compact ? 40 : 8;
        double selectorWidth = compact
            ? Math.Max(90, panelWidth - 36)
            : Math.Max(90, panelWidth - 160);
        if (mapLayerPanelCollapsed)
        {
            ElementBounds collapsedRoot = ElementBounds.Fixed(
                contentX + 18,
                contentY,
                panelWidth,
                52
            );
            mapLayerPanelBounds = collapsedRoot;
            mapLayerPanel = capi.Gui.CreateCompo(
                    "modernatlas-map-layer",
                    collapsedRoot
                )
                .AddStaticCustomDraw(
                    ElementBounds.Fixed(0, 0, panelWidth, 52),
                    AtlasUiStyle.DrawCard
                )
                .AddAtlasButton(
                    "▸",
                    ToggleMapLayerPanel,
                    ElementBounds.Fixed(8, 6, 40, 40),
                    "map-layer-toggle",
                    AtlasButtonStyle.Icon
                )
                .AddStaticText(
                    "MAP LAYER",
                    AtlasUiStyle.LabelFont(12),
                    ElementBounds.Fixed(58, 14, 190, 24)
                )
                .AddStaticText(
                    activeMapLayer.DisplayName(),
                    AtlasUiStyle.DetailFont(10),
                    ElementBounds.Fixed(250, 14, Math.Max(90, panelWidth - 268), 20)
                )
                .Compose(false);
            synchronizedOreCodeRevision = mapLayerTexture.OreCodeRevision;
            return;
        }

        ElementBounds root = ElementBounds.Fixed(
            contentX + 18,
            contentY,
            panelWidth,
            height
        );
        mapLayerPanelBounds = root;
        GuiComposer composer = capi.Gui.CreateCompo("modernatlas-map-layer", root)
            .AddStaticCustomDraw(
                ElementBounds.Fixed(0, 0, panelWidth, height),
                AtlasUiStyle.DrawCard
            )
            .AddAtlasButton(
                "▾",
                ToggleMapLayerPanel,
                ElementBounds.Fixed(8, 8, 40, 40),
                "map-layer-toggle",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "MAP LAYER",
                AtlasUiStyle.LabelFont(12),
                ElementBounds.Fixed(58, 17, 124, 24)
            )
            .AddDropDown(
                AtlasMapLayerInfo.Values,
                AtlasMapLayerInfo.Names,
                (int)activeMapLayer,
                OnMapLayerChanged,
                ElementBounds.Fixed(selectorX, selectorY, selectorWidth, 44),
                "map-layer"
            );

        if (oreControls)
        {
            GetOreFilterOptions(out string[] values, out string[] names, out int selectedIndex);
            composer
                .AddStaticText(
                    "ORE FILTER",
                    AtlasUiStyle.LabelFont(12),
                    ElementBounds.Fixed(18, compact ? 92 : 62, 124, 24)
                )
                .AddDropDown(
                    values,
                    names,
                    selectedIndex,
                    OnOreFilterChanged,
                    ElementBounds.Fixed(
                        selectorX,
                        compact ? 118 : 53,
                        selectorWidth,
                        44
                    ),
                    "ore-filter"
                );
        }

        double statusY = compact
            ? oreControls ? 170 : 88
            : oreControls ? 102 : 55;
        mapLayerPanel = composer
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(18, statusY, Math.Max(90, panelWidth - 36), 20),
                "layer-status"
            )
            .AddDynamicText(
                activeMapLayer.DetailedLegend(),
                AtlasUiStyle.DetailFont(10),
                ElementBounds.Fixed(
                    18,
                    statusY + 21,
                    Math.Max(90, panelWidth - 36),
                    20
                ),
                "layer-legend"
            )
            .Compose(false);
        synchronizedOreCodeRevision = mapLayerTexture.OreCodeRevision;
    }

    private bool ToggleMapLayerPanel()
    {
        ToggleMapOptions();
        return true;
    }

    private void RecomposeMapLayerPanel()
    {
        if (MapPanelOpen)
        {
            OpenBottomPanelImmediately(AtlasPanelSection.MapOptions);
        }
    }

    private void GetOreFilterOptions(
        out string[] values,
        out string[] names,
        out int selectedIndex
    )
    {
        var options = new List<(string Code, string Name)>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string code in mapLayerTexture.DiscoveredOreCodes)
        {
            if (unique.Add(code))
            {
                options.Add((code, searchController.GetEnglishOreName(code)));
            }
        }
        if (selectedOreCode != null && unique.Add(selectedOreCode))
        {
            options.Add((
                selectedOreCode,
                searchController.GetEnglishOreName(selectedOreCode)
            ));
        }
        options.Sort((left, right) => string.Compare(
            left.Name,
            right.Name,
            StringComparison.OrdinalIgnoreCase
        ));

        values = new string[options.Count + 1];
        names = new string[options.Count + 1];
        values[0] = AllOresFilterValue;
        names[0] = "All ores";
        selectedIndex = 0;
        for (int index = 0; index < options.Count; index++)
        {
            values[index + 1] = options[index].Code;
            names[index + 1] = options[index].Name;
            if (string.Equals(options[index].Code, selectedOreCode, StringComparison.Ordinal))
            {
                selectedIndex = index + 1;
            }
        }
    }

    private void SynchronizeOreFilterOptions()
    {
        if (activeMapLayer != AtlasMapLayer.OreDensity
            || mapLayerTexture.OreCodeRevision == synchronizedOreCodeRevision)
        {
            return;
        }

        GuiElementDropDown? dropdown = mapLayerPanel?.GetDropDown("ore-filter");
        if (dropdown == null) return;
        GetOreFilterOptions(out string[] values, out string[] names, out int selectedIndex);
        synchronizingOreFilterDropdown = true;
        try
        {
            dropdown.SetList(values, names);
            dropdown.SetSelectedIndex(selectedIndex);
            synchronizedOreCodeRevision = mapLayerTexture.OreCodeRevision;
        }
        finally
        {
            synchronizingOreFilterDropdown = false;
        }
    }

    private void OnOreFilterChanged(string value, bool selected)
    {
        if (!selected || synchronizingOreFilterDropdown) return;
        selectedOreCode = string.Equals(value, AllOresFilterValue, StringComparison.Ordinal)
            ? null
            : value;
        ClearOreHover();
        if (IsOpened()) PrepareMapLayer();
    }

    private void OnMapLayerChanged(string value, bool selected)
    {
        if (!selected || synchronizingMapLayerDropdown) return;

        AtlasMapLayer layer = AtlasMapLayerInfo.FromValue(value);
        if (layer.RequiresSpoilerAccess() && !UnitInspectionEnabled)
        {
            capi.TriggerIngameError(
                this,
                "modernatlas-layer-access",
                "Ore density is available only in Creative or server-authorized Cheat Mode."
            );
            SyncMapLayerDropdown();
            return;
        }
        SetMapLayer(layer);
    }

    private void SetMapLayer(AtlasMapLayer layer)
    {
        AtlasMapLayer previousLayer = activeMapLayer;
        if (!config.MapLayersEnabled)
        {
            layer = AtlasMapLayer.TexturedTerrain;
        }
        if (layer.RequiresSpoilerAccess() && !UnitInspectionEnabled)
        {
            layer = AtlasMapLayer.TexturedTerrain;
        }
        activeMapLayer = layer;
        ClearOreHover();
        if (previousLayer != activeMapLayer && mapLayerPanel != null)
        {
            // Rebuild after the dropdown event has returned to GuiComposer;
            // disposing the shared bottom composer from inside its callback
            // would invalidate the current element iteration.
            pendingMapLayerPanelRecompose = true;
        }
        SyncMapLayerDropdown();
        if (IsOpened()) PrepareMapLayer();
        else mapLayerTexture.Reset();
    }

    private void SyncMapLayerDropdown()
    {
        GuiElementDropDown? dropdown = mapLayerPanel?.GetDropDown("map-layer");
        if (dropdown == null) return;

        synchronizingMapLayerDropdown = true;
        try
        {
            dropdown.SetSelectedIndex((int)activeMapLayer);
        }
        finally
        {
            synchronizingMapLayerDropdown = false;
        }
    }

    private void PrepareMapLayer()
    {
        if (!config.MapLayersEnabled)
        {
            activeMapLayer = AtlasMapLayer.TexturedTerrain;
            mapLayerTexture.Reset();
            SyncMapLayerDropdown();
            return;
        }
        mapLayerTexture.Begin(
            activeMapLayer,
            capi.World.Player.Entity.Pos.X,
            capi.World.Player.Entity.Pos.Z,
            GameViewDistance,
            UnitInspectionEnabled,
            selectedOreCode
        );
    }

    private void AdvanceSearch()
    {
        if (!SearchModeActive) return;

        IReadOnlyList<AtlasRenderedEntity> renderedEntities = exactChunkRenderer
            ?.LastRenderedEntities ?? Array.Empty<AtlasRenderedEntity>();
        searchController.Advance(
            GameViewDistance,
            SurfaceSafetyEnabled,
            surfaceHeightTexture,
            renderedEntities,
            UnitInspectionEnabled
        );
    }

    private void RenderSearchMarkers()
    {
        if (!SearchModeActive || interfaceHidden || !searchController.HasActiveQuery) return;
        EnsureSearchMarkerTexture();
        if (searchMarkerTexture?.TextureId <= 0) return;

        bool clipped = BeginScrollContentClip();
        try
        {
            float guiScale = (float)Math.Max(0.5, RuntimeEnv.GUIScale);
            float pulse = 0.5f + 0.5f * MathF.Sin(capi.ElapsedMilliseconds / 230f);
            float markerSize = Math.Clamp((28f + pulse * 6f) * guiScale, 24f, 58f);
            foreach (AtlasSearchResult result in searchController.BlockResults)
            {
                RenderSearchMarker(result, markerSize, pulse);
            }
            foreach (AtlasSearchResult result in searchController.DynamicResults)
            {
                RenderSearchMarker(result, markerSize + 4f, pulse);
            }
        }
        finally
        {
            EndScrollContentClip(clipped);
        }
    }

    private void RenderSearchMarker(
        AtlasSearchResult result,
        float markerSize,
        float pulse
    )
    {
        double dx = result.X - capi.World.Player.Entity.Pos.X;
        double dz = result.Z - capi.World.Player.Entity.Pos.Z;
        double clearRadius = GameViewDistance;
        if (dx * dx + dz * dz > clearRadius * clearRadius) return;
        if (!TryProjectAtlasPosition(
            result.X,
            result.Y,
            result.Z,
            out double screenX,
            out double screenY,
            out _
        ))
        {
            return;
        }

        float alpha = 0.78f + pulse * 0.18f;
        Vec4f color = result.Kind switch
        {
            AtlasSearchResultKind.Block => new Vec4f(1f, 0.72f, 0.18f, alpha),
            AtlasSearchResultKind.Player => new Vec4f(0.20f, 0.86f, 1f, alpha),
            AtlasSearchResultKind.Animal => new Vec4f(0.34f, 1f, 0.48f, alpha),
            AtlasSearchResultKind.Mob => new Vec4f(1f, 0.22f, 0.18f, alpha),
            AtlasSearchResultKind.Npc => new Vec4f(0.78f, 0.48f, 1f, alpha),
            AtlasSearchResultKind.DroppedItem => new Vec4f(1f, 0.46f, 0.12f, alpha),
            _ => new Vec4f(1f, 1f, 1f, alpha)
        };
        capi.Render.Render2DTexture(
            searchMarkerTexture!.TextureId,
            (float)screenX - markerSize * 0.5f,
            (float)screenY - markerSize * 0.5f,
            markerSize,
            markerSize,
            45,
            color
        );
    }

    private void EnsureSearchMarkerTexture()
    {
        if (searchMarkerTexture?.TextureId > 0) return;

        const int size = 64;
        searchMarkerTexture ??= new LoadedTexture(capi);
        using ImageSurface surface = new(Format.Argb32, size, size);
        using Context context = new(surface);
        context.Operator = Operator.Source;
        context.SetSourceRGBA(0, 0, 0, 0);
        context.Paint();
        context.Operator = Operator.Over;

        for (int radius = 28; radius >= 18; radius -= 2)
        {
            double progress = (28 - radius) / 10.0;
            context.SetSourceRGBA(1, 1, 1, 0.025 + progress * 0.018);
            context.Arc(32, 32, radius, 0, Math.PI * 2);
            context.Fill();
        }
        context.SetSourceRGBA(1, 1, 1, 0.96);
        context.LineWidth = 3;
        context.Arc(32, 32, 17, 0, Math.PI * 2);
        context.Stroke();
        context.LineWidth = 2;
        context.MoveTo(32, 7);
        context.LineTo(32, 20);
        context.MoveTo(32, 44);
        context.LineTo(32, 57);
        context.MoveTo(7, 32);
        context.LineTo(20, 32);
        context.MoveTo(44, 32);
        context.LineTo(57, 32);
        context.Stroke();
        context.Arc(32, 32, 3.5, 0, Math.PI * 2);
        context.Fill();

        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref searchMarkerTexture);
    }

    private void UpdateOreHover(int mouseX, int mouseY, bool blocked)
    {
        oreHoverMouseX = mouseX;
        oreHoverMouseY = mouseY;
        if (blocked
            || interfaceHidden
            || activeMapLayer != AtlasMapLayer.OreDensity
            || !CreativeCheatSettingsAvailable
            || settingsModalOpen
            || performanceModalOpen
            || creativeSettingsModalOpen
            || visualLabModalOpen
            || leftDragging
            || rightDragging
            || !AtlasViewport.Contains(mouseX, mouseY))
        {
            ClearOreHover();
            return;
        }

        long now = capi.ElapsedMilliseconds;
        if (now - lastOreHoverUpdateMilliseconds < OreHoverRefreshMilliseconds)
        {
            return;
        }
        lastOreHoverUpdateMilliseconds = now;

        if (!TryResolvePointerSurface(mouseX, mouseY, out int worldX, out int worldZ))
        {
            ClearOreHover();
            return;
        }

        int cellX = (int)Math.Floor(worldX / (double)AtlasMapLayerTexture.HorizontalSampleSize)
            * AtlasMapLayerTexture.HorizontalSampleSize
            + AtlasMapLayerTexture.HorizontalSampleSize / 2;
        int cellZ = (int)Math.Floor(worldZ / (double)AtlasMapLayerTexture.HorizontalSampleSize)
            * AtlasMapLayerTexture.HorizontalSampleSize
            + AtlasMapLayerTexture.HorizontalSampleSize / 2;
        if (cellX == oreHoverCellX && cellZ == oreHoverCellZ
            && oreHoverInspection != null)
        {
            return;
        }

        oreHoverCellX = cellX;
        oreHoverCellZ = cellZ;
        if (!mapLayerTexture.TryInspectOre(cellX, cellZ, out oreHoverInspection)
            || oreHoverInspection == null)
        {
            ClearOreHover();
            return;
        }
        BuildOreHoverTexture(oreHoverInspection);
    }

    private bool TryResolvePointerSurface(
        int mouseX,
        int mouseY,
        out int worldX,
        out int worldZ
    )
    {
        worldX = 0;
        worldZ = 0;
        AtlasViewportBounds viewport = AtlasViewport;
        if (!viewport.Contains(mouseX, mouseY)) return false;

        double pixelsPerBlock = viewport.Height / (2.0 * Math.Max(0.001f, zoom));
        double projectedRight = (mouseX - (viewport.X + viewport.Width * 0.5))
            / pixelsPerBlock;
        double projectedUp = ((viewport.Y + viewport.Height * 0.5) - mouseY)
            / pixelsPerBlock;
        double yaw = yawDegrees * GameMath.DEG2RAD;
        double pitch = pitchDegrees * GameMath.DEG2RAD;
        double sinYaw = Math.Sin(yaw);
        double cosYaw = Math.Cos(yaw);
        double sinPitch = Math.Sin(pitch);
        double cosPitch = Math.Cos(pitch);

        double rightX = cosYaw;
        double rightZ = -sinYaw;
        double upX = -sinYaw * sinPitch;
        double upY = cosPitch;
        double upZ = -cosYaw * sinPitch;
        double forwardX = -sinYaw * cosPitch;
        double forwardY = -sinPitch;
        double forwardZ = -cosYaw * cosPitch;
        double rayDistance = Math.Max(640, GameViewDistance * 2.5);
        double originX = centerX - forwardX * rayDistance
            + rightX * projectedRight + upX * projectedUp;
        double originY = centerY - forwardY * rayDistance + upY * projectedUp;
        double originZ = centerZ - forwardZ * rayDistance
            + rightZ * projectedRight + upZ * projectedUp;
        double maximumTravel = rayDistance * 2.0;
        const double step = 4.0;
        int mapSizeX = capi.World.BlockAccessor.MapSizeX;
        int mapSizeZ = capi.World.BlockAccessor.MapSizeZ;
        int chunkSize = GlobalConstants.ChunkSize;
        double disclosureRadiusSquared = (double)GameViewDistance * GameViewDistance;
        double playerX = capi.World.Player.Entity.Pos.X;
        double playerZ = capi.World.Player.Entity.Pos.Z;

        for (double travel = 0; travel <= maximumTravel; travel += step)
        {
            double x = originX + forwardX * travel;
            double y = originY + forwardY * travel;
            double z = originZ + forwardZ * travel;
            int xInt = (int)Math.Floor(x);
            int zInt = (int)Math.Floor(z);
            if (xInt < 0 || zInt < 0 || xInt >= mapSizeX || zInt >= mapSizeZ)
            {
                continue;
            }
            double dx = x - playerX;
            double dz = z - playerZ;
            if (dx * dx + dz * dz > disclosureRadiusSquared) continue;

            int chunkX = xInt / chunkSize;
            int chunkZ = zInt / chunkSize;
            IMapChunk? mapChunk = capi.World.BlockAccessor.GetMapChunk(chunkX, chunkZ);
            ushort[]? heightMap = mapChunk?.WorldGenTerrainHeightMap;
            if (heightMap == null || heightMap.Length < chunkSize * chunkSize) continue;
            int localX = xInt - chunkX * chunkSize;
            int localZ = zInt - chunkZ * chunkSize;
            int surfaceY = heightMap[localZ * chunkSize + localX];
            if (y > surfaceY + 1.0) continue;

            worldX = xInt;
            worldZ = zInt;
            return true;
        }
        return false;
    }

    private void BuildOreHoverTexture(AtlasOreInspection inspection)
    {
        AtlasOreReading[] visible = SelectVisibleOreReadings(inspection.Readings);
        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        int width = Math.Max(1, (int)Math.Ceiling(390 * scale));
        int rowCount = Math.Max(1, visible.Length);
        int height = Math.Max(1, (int)Math.Ceiling((148 + rowCount * 25) * scale));
        oreHoverTexture ??= new LoadedTexture(capi);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        AtlasUiStyle.Clear(context);
        AtlasUiStyle.DrawRaisedPanel(context, 0, 0, width, height, 13);

        DrawOreHoverText(context, "ORE POTENTIAL", 18, 25, 12, true, 0.97, 0.98, 0.99, scale);
        string position = $"X {inspection.WorldX}  •  Z {inspection.WorldZ}  •  Surface {inspection.SurfaceY}";
        DrawOreHoverText(context, position, 18, 47, 10.5, false, 0.75, 0.81, 0.86, scale);

        double rowY = 76;
        if (visible.Length == 0)
        {
            string empty = inspection.Source == AtlasOreInspectionSource.RegionalOreMaps
                ? "No mapped potential above the trace threshold"
                : "No ore blocks in this loaded column";
            DrawOreHoverText(context, empty, 18, rowY, 11, false, 0.85, 0.87, 0.89, scale);
        }
        else
        {
            foreach (AtlasOreReading reading in visible)
            {
                string name = TruncateLabel(searchController.GetEnglishOreName(reading.Code), 24);
                bool selected = selectedOreCode != null
                    && string.Equals(reading.Code, selectedOreCode, StringComparison.Ordinal);
                if (selected) name = "› " + name;
                string value = reading.IsPotential
                    ? $"{AtlasOrePotential.Grade(reading.Potential)}  {reading.Potential * 100:0.##}%"
                    : reading.BlockCount == 1 ? "1 loaded block" : $"{reading.BlockCount} loaded blocks";
                (double red, double green, double blue) = reading.IsPotential
                    ? GradeColor(reading.Potential)
                    : (0.92, 0.72, 0.30);
                DrawOreHoverText(context, name, 18, rowY, 11, selected, 0.96, 0.97, 0.98, scale);
                DrawOreHoverText(context, value, 210, rowY, 10.5, true, red, green, blue, scale);
                rowY += 25;
            }
        }

        int hiddenCount = Math.Max(0, inspection.Readings.Length - visible.Length);
        if (hiddenCount > 0)
        {
            DrawOreHoverText(context, $"+{hiddenCount} more mapped ores", 18, rowY, 9.5, false, 0.65, 0.70, 0.74, scale);
        }
        double infoY = (148 + rowCount * 25) - 45;
        string rock = searchController.GetEnglishBlockName(inspection.HostRockCode);
        DrawOreHoverText(context, $"Rock: {TruncateLabel(rock, 32)}", 18, infoY, 10, false, 0.76, 0.81, 0.85, scale);
        string source = inspection.Source == AtlasOreInspectionSource.RegionalOreMaps
            ? $"Source: {inspection.SourceMapCount} loaded regional OreMaps"
            : "Source: exact loaded block column • grade unavailable";
        DrawOreHoverText(context, source, 18, infoY + 20, 9.5, false, 0.65, 0.71, 0.75, scale);
        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref oreHoverTexture);
    }

    private AtlasOreReading[] SelectVisibleOreReadings(AtlasOreReading[] readings)
    {
        const int maximum = 5;
        if (readings.Length <= maximum) return readings;
        var selected = new List<AtlasOreReading>(maximum);
        if (selectedOreCode != null)
        {
            foreach (AtlasOreReading reading in readings)
            {
                if (!string.Equals(reading.Code, selectedOreCode, StringComparison.Ordinal)) continue;
                selected.Add(reading);
                break;
            }
        }
        foreach (AtlasOreReading reading in readings)
        {
            if (selected.Count >= maximum) break;
            if (selectedOreCode != null
                && string.Equals(reading.Code, selectedOreCode, StringComparison.Ordinal))
            {
                continue;
            }
            selected.Add(reading);
        }
        return selected.ToArray();
    }

    private static (double R, double G, double B) GradeColor(float potential) =>
        potential switch
        {
            >= 0.6666667f => (1.00, 0.91, 0.16),
            >= 0.5333334f => (1.00, 0.60, 0.14),
            >= 0.4f => (1.00, 0.35, 0.28),
            >= 0.2666667f => (0.95, 0.30, 0.66),
            >= 0.1333334f => (0.72, 0.40, 0.94),
            >= AtlasOrePotential.CategorizedThreshold => (0.52, 0.55, 0.88),
            _ => (0.64, 0.69, 0.75)
        };

    private static string TruncateLabel(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";

    private static void DrawOreHoverText(
        Context context,
        string text,
        double x,
        double y,
        double size,
        bool bold,
        double red,
        double green,
        double blue,
        double scale
    )
    {
        context.SelectFontFace(
            "Sans",
            FontSlant.Normal,
            bold ? FontWeight.Bold : FontWeight.Normal
        );
        context.SetFontSize(size * scale);
        context.SetSourceRGBA(red, green, blue, 0.98);
        context.MoveTo(x * scale, y * scale);
        context.ShowText(text);
    }

    private void RenderOreHoverCard()
    {
        if (oreHoverInspection == null
            || oreHoverTexture?.TextureId <= 0
            || interfaceHidden
            || activeMapLayer != AtlasMapLayer.OreDensity
            || !CreativeCheatSettingsAvailable)
        {
            return;
        }

        AtlasViewportBounds viewport = AtlasViewport;
        LoadedTexture hoverTexture = oreHoverTexture!;
        float width = hoverTexture.Width;
        float height = hoverTexture.Height;
        float x = oreHoverMouseX + 18;
        if (x + width > viewport.Right - 8) x = oreHoverMouseX - width - 18;
        x = Math.Clamp(x, viewport.X + 8, Math.Max(viewport.X + 8, viewport.Right - width - 8));
        float y = Math.Clamp(
            oreHoverMouseY + 16,
            viewport.Y + 8,
            Math.Max(viewport.Y + 8, viewport.Bottom - height - 8)
        );
        capi.Render.Render2DTexture(
            hoverTexture.TextureId,
            x,
            y,
            width,
            height,
            48,
            ColorUtil.WhiteArgbVec
        );
    }

    private void ClearOreHover()
    {
        oreHoverInspection = null;
        oreHoverCellX = int.MinValue;
        oreHoverCellZ = int.MinValue;
    }

    private bool RenderLiveWorld(float deltaTime)
    {
        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        try
        {
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);

        preparingSurfaceFilter = !surfaceHeightTexture.Advance();
        preparingOreConcealment = SurvivalOreConcealmentEnabled
            && exactChunkRenderer != null
            && !exactChunkRenderer.AdvanceSurvivalOreConcealment();
        preparingVegetationMask = config.HideVegetation
            && exactChunkRenderer != null
            && !exactChunkRenderer.AdvanceVegetationMask();
        if (preparingSurfaceFilter
            || preparingOreConcealment
            || preparingVegetationMask)
        {
            ClearSurfacePreparationFrame();
            return false;
        }

        AtlasViewportBounds viewport = AtlasViewport;
        float aspect = viewport.Width / (float)Math.Max(1, viewport.Height);
        float farPlane = Math.Max(2000, GameViewDistance * 6);
        bool captureProjection = pendingScreenshotRequest;
        float verticalHalfExtent = zoom;
        float horizontalHalfExtent = zoom * aspect;
        if (captureProjection)
        {
            // Tile capture: cover the tile's world area with square world
            // pixels in Primary, then crop the sub-region on readback. In
            // scroll presentation the map viewport is taller than the window
            // aspect, so the vertical half-extent grows accordingly.
            verticalHalfExtent = zoom * tileScreenshot.ProjectionVerticalFactor;
            horizontalHalfExtent = verticalHalfExtent
                * (render.FrameWidth / (float)Math.Max(1, render.FrameHeight));
        }
        Mat4f.Ortho(
            projection,
            -horizontalHalfExtent,
            horizontalHalfExtent,
            -verticalHalfExtent,
            verticalHalfExtent,
            0.1f,
            farPlane
        );
        float yaw = yawDegrees * GameMath.DEG2RAD;
        float pitch = pitchDegrees * GameMath.DEG2RAD;
        DefaultShaderUniforms uniforms = capi.Render.ShaderUniforms;
        float animationOffset = !captureProjection
            && capi.IsSinglePlayer
            && capi.IsGamePaused
                ? atlasAnimationSeconds
                : 0;
        // Every tile must render identical wind, water and cloud counters so
        // the stitched image has no animated seams.
        float windWaveCounter = captureProjection
            ? screenshotFrozenWindWaveCounter
            : config.AnimationsEnabled
                ? uniforms.WindWaveCounter + animationOffset
                : frozenWindWaveCounter;
        float windWaveCounterHighFrequency = captureProjection
            ? screenshotFrozenWindWaveCounterHighFrequency
            : config.AnimationsEnabled
                ? uniforms.WindWaveCounterHighFreq + animationOffset
                : frozenWindWaveCounterHighFrequency;
        float waterStillCounter = captureProjection
            ? screenshotFrozenWaterStillCounter
            : config.AnimationsEnabled
                ? uniforms.WaterStillCounter + animationOffset
                : frozenWaterStillCounter;
        float waterFlowCounter = captureProjection
            ? screenshotFrozenWaterFlowCounter
            : config.AnimationsEnabled
                ? uniforms.WaterFlowCounter + animationOffset
                : frozenWaterFlowCounter;
        float renderAnimationOffset = !captureProjection
            && config.AnimationsEnabled
            && capi.IsSinglePlayer
            && capi.IsGamePaused
                ? atlasRealDeltaTime
                : 0;

        // The tiled capture hides the local player's own model so the stitched
        // photo shows the map without the photographer's character (and its
        // hands). Restore the flag even when the render throws.
        if (exactChunkRenderer != null)
        {
            exactChunkRenderer.HideLocalPlayerModel = captureProjection;
        }
        bool rendered;
        try
        {
            rendered = exactChunkRenderer?.Render(
                deltaTime,
                projection,
                centerX,
                centerY,
                centerZ,
                yaw,
                pitch,
                GameViewDistance,
                SurfaceSafetyEnabled,
                SurvivalOreConcealmentEnabled,
                surfaceHeightTexture,
                mapLayerTexture,
                EffectiveMapLayerOpacity,
                0,
                config.PerformanceLightingEnabled,
                config.HideVegetation,
                Math.Clamp(config.AtlasExposurePercent, 50, 150) / 100f,
                Math.Clamp(config.CaveMaskBrightnessPercent, 50, 150) / 100f,
                windWaveCounter,
                windWaveCounterHighFrequency,
                waterStillCounter,
                waterFlowCounter,
                config.CloudsEnabled,
                config.LiveLightingEnabled,
                config.FixedSunHour,
                renderAnimationOffset,
                captureProjection ? screenshotFrozenCloudOffset : null,
                visibleEntityPolicy,
                false
            ) == true;
        }
        finally
        {
            if (exactChunkRenderer != null)
            {
                exactChunkRenderer.HideLocalPlayerModel = false;
            }
        }
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
        return rendered;
        }
        finally
        {
            renderState.RestoreGuiHandoff();
        }
    }

    private void ResetView()
    {
        CenterOnPlayer();
        targetYawDegrees = 42;
        if (!config.CameraAngleLocked || !CreativeCheatSettingsAvailable)
        {
            targetPitchDegrees = 72;
        }
        FitLoadedTerrain();
    }

    private void CenterOnPlayer()
    {
        targetCenterX = capi.World.Player.Entity.Pos.X;
        targetCenterZ = capi.World.Player.Entity.Pos.Z;
        centerY = capi.World.Player.Entity.Pos.Y;
        FocusOnExteriorSurface();
    }

    private void Pan(double x, double z, float amount, KeyEvent args)
    {
        targetCenterX += x * amount;
        targetCenterZ += z * amount;
        args.Handled = true;
    }

    private void ApplyRotationDrag(double deltaX, double deltaY)
    {
        targetYawDegrees = NormalizeDegrees(targetYawDegrees + (float)deltaX * 0.42f);
        if (!config.CameraAngleLocked || !CreativeCheatSettingsAvailable)
        {
            targetPitchDegrees = Math.Clamp(
                targetPitchDegrees - (float)deltaY * 0.32f,
                MinimumPitchDegrees,
                86
            );
        }
    }

    private void ResetPointerDrag()
    {
        leftDragging = false;
        rightDragging = false;
        leftDragDistance = 0;
    }

    private void PrepareSurfaceSafetyFilter()
    {
        preparedGameViewDistance = GameViewDistance;
        double playerX = capi.World.Player.Entity.Pos.X;
        double playerZ = capi.World.Player.Entity.Pos.Z;
        if (surfaceHeightTexture.CoversArea(
                playerX,
                playerZ,
                GameViewDistance
            ))
        {
            // This is transient world data, not a persistent terrain cache.
            // Keep it across G close/open while it still covers the required
            // loaded-world footprint; rebuilding it here creates a blank
            // preparation interval on every second atlas open.
            preparingSurfaceFilter = false;
            return;
        }

        preparingSurfaceFilter = true;

        surfaceHeightTexture.Begin(
            playerX,
            playerZ,
            GameViewDistance
        );
    }

    private void ClearSurfacePreparationFrame()
    {
        // Draw an opaque Primary frame while the small surface texture is
        // prepared. Clearing the default framebuffer directly can leave the
        // native Linux window transparent after the atlas closes.
        exactChunkRenderer?.RenderSurfacePreparationFrame(!config.RenderOnScroll);
    }

    private void FitLoadedTerrain()
    {
        float radius = GameViewDistance;
        AtlasViewportBounds viewport = AtlasViewport;
        float aspect = viewport.Width / (float)Math.Max(1, viewport.Height);
        float yaw = targetYawDegrees * GameMath.DEG2RAD;
        float pitch = targetPitchDegrees * GameMath.DEG2RAD;

        // The engine's completed chunks rarely form a perfect circle while it
        // is streaming. Measure only client-loaded columns and project every
        // chunk corner through the selected yaw, so an asymmetric or diagonal
        // edge is still completely visible after reset or opening.
        bool measuredLoadedTerrain = TryMeasureLoadedTerrainFootprint(
            yaw,
            out float horizontalExtent,
            out float forwardExtent
        );
        if (!measuredLoadedTerrain)
        {
            horizontalExtent = radius;
            forwardExtent = radius;
        }

        // Add vertical headroom for trees, buildings and hills so neither the
        // top nor bottom edge is clipped at low camera angles.
        float horizontalFit = horizontalExtent / Math.Max(0.5f, aspect);
        float verticalFit = forwardExtent * Math.Abs(MathF.Sin(pitch))
            + Math.Min(192, radius * 0.3f) * Math.Abs(MathF.Cos(pitch));
        targetZoom = Math.Clamp(Math.Max(horizontalFit, verticalFit) * 1.08f, 80, 30000);
    }

    private bool TryMeasureLoadedTerrainFootprint(
        float yaw,
        out float horizontalExtent,
        out float forwardExtent
    )
    {
        horizontalExtent = 0;
        forwardExtent = 0;

        int chunkSize = GlobalConstants.ChunkSize;
        int radius = GameViewDistance;
        int minimumChunkX = (int)Math.Floor(
            (capi.World.Player.Entity.Pos.X - radius) / chunkSize
        );
        int maximumChunkX = (int)Math.Floor(
            (capi.World.Player.Entity.Pos.X + radius) / chunkSize
        );
        int minimumChunkZ = (int)Math.Floor(
            (capi.World.Player.Entity.Pos.Z - radius) / chunkSize
        );
        int maximumChunkZ = (int)Math.Floor(
            (capi.World.Player.Entity.Pos.Z + radius) / chunkSize
        );
        int verticalChunkCount = Math.Max(
            1,
            (capi.World.BlockAccessor.MapSizeY + chunkSize - 1) / chunkSize
        );
        double originX = targetCenterX;
        double originZ = targetCenterZ;
        float sinYaw = MathF.Sin(yaw);
        float cosYaw = MathF.Cos(yaw);
        float measuredHorizontalExtent = 0;
        float measuredForwardExtent = 0;
        bool found = false;

        IReadOnlyCollection<(int X, int Z)> completedColumns = exactChunkRenderer
            ?.CompletedTerrainColumns ?? Array.Empty<(int X, int Z)>();
        foreach ((int X, int Z) column in completedColumns)
        {
            if (column.X < minimumChunkX || column.X > maximumChunkX
                || column.Z < minimumChunkZ || column.Z > maximumChunkZ)
            {
                continue;
            }
            MeasureChunk(column.X, column.Z);
        }
        if (found)
        {
            horizontalExtent = measuredHorizontalExtent;
            forwardExtent = measuredForwardExtent;
            return true;
        }

        // Before the first atlas frame there is no completed-mesh snapshot.
        // Fall back to loaded columns only for that initial fit. Later resets
        // use actual GPU mesh coverage so a view-distance increase cannot zoom
        // out to thousands of queued, not-yet-renderable chunks.
        for (int chunkZ = minimumChunkZ; chunkZ <= maximumChunkZ; chunkZ++)
        {
            for (int chunkX = minimumChunkX; chunkX <= maximumChunkX; chunkX++)
            {
                bool loaded = false;
                for (int chunkY = 0; chunkY < verticalChunkCount; chunkY++)
                {
                    if (capi.World.BlockAccessor.GetChunk(chunkX, chunkY, chunkZ)
                        is IClientChunk { LoadedFromServer: true })
                    {
                        loaded = true;
                        break;
                    }
                }
                if (!loaded) continue;

                MeasureChunk(chunkX, chunkZ);
            }
        }

        horizontalExtent = measuredHorizontalExtent;
        forwardExtent = measuredForwardExtent;
        return found;

        void MeasureChunk(int chunkX, int chunkZ)
        {
            found = true;
            double minimumX = chunkX * (double)chunkSize - originX;
            double maximumX = minimumX + chunkSize;
            double minimumZ = chunkZ * (double)chunkSize - originZ;
            double maximumZ = minimumZ + chunkSize;
            MeasureProjectedCorner(minimumX, minimumZ);
            MeasureProjectedCorner(minimumX, maximumZ);
            MeasureProjectedCorner(maximumX, minimumZ);
            MeasureProjectedCorner(maximumX, maximumZ);
        }

        void MeasureProjectedCorner(double x, double z)
        {
            float right = (float)(x * cosYaw - z * sinYaw);
            float forward = (float)(x * sinYaw + z * cosYaw);
            measuredHorizontalExtent = Math.Max(measuredHorizontalExtent, Math.Abs(right));
            measuredForwardExtent = Math.Max(measuredForwardExtent, Math.Abs(forward));
        }
    }

    private void OnRenderOnScrollToggled(bool enabled)
    {
        presentationChangeCoordinator.Request(
            enabled,
            capi.ElapsedMilliseconds,
            PresentationChangeDebounceMilliseconds
        );
        // Keep the requested state visible in the surviving Settings composer
        // while the committed map viewport continues to render unchanged.
        SyncSettingsControls();
        SyncToolbarControls();
    }

    private void OnScrollRealtimeWeatherToggled(bool enabled)
    {
        config.ScrollRealtimeWeatherEnabled = enabled;
        saveConfig();
        SyncSettingsControls();
        SyncToolbarControls();
    }

    private void OnPlayerCompassToggled(bool enabled)
    {
        config.ShowPlayerCompass = enabled;
        saveConfig();
        SyncSettingsControls();
        SyncToolbarControls();
    }

    private bool ToggleCompass()
    {
        OnPlayerCompassToggled(!config.ShowPlayerCompass);
        return true;
    }

    private void OnHandheldInstrumentChoiceChanged(string value, bool selected)
    {
        if (!selected) return;
        if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            config.ShowPlayerCompass = false;
        }
        else
        {
            config.HandheldInstrumentMode = string.Equals(
                value,
                "time",
                StringComparison.OrdinalIgnoreCase
            )
                ? "time"
                : "compass";
            config.ShowPlayerCompass = true;
        }
        saveConfig();
        SyncSettingsControls();
        SyncToolbarControls();
    }

    private void OnMapLayersToggled(bool enabled)
    {
        config.MapLayersEnabled = enabled;
        if (enabled)
        {
            if (IsOpened()) PrepareMapLayer();
        }
        else
        {
            SetMapLayer(AtlasMapLayer.TexturedTerrain);
        }
        saveConfig();
        SyncSettingsControls();
    }

    private void OnCaveModeToggled(bool enabled)
    {
        if (!CreativeCheatSettingsAvailable) return;

        config.CaveModeEnabled = enabled;
        selectedEntityId = null;
        PrepareSurfaceSafetyFilter();
        saveConfig();
        SyncCreativeSettingsControls();
    }

    private void OnSearchModeToggled(bool enabled)
    {
        if (!CreativeCheatSettingsAvailable)
        {
            SyncSettingsControls();
            return;
        }

        config.SearchModeEnabled = enabled;
        if (!enabled)
        {
            searchPanel?.UnfocusOwnElements();
            ClearSearch();
        }
        saveConfig();
        SyncSettingsControls();
        SyncCreativeSettingsControls();
        SyncToolbarControls();
    }

    private void OnCameraAngleLockToggled(bool enabled)
    {
        if (!CreativeCheatSettingsAvailable) return;

        config.CameraAngleLocked = enabled;
        if (enabled)
        {
            targetPitchDegrees = pitchDegrees;
        }
        saveConfig();
        SyncCreativeSettingsControls();
    }

    private void OnAnimationsToggled(bool enabled)
    {
        if (!enabled && config.AnimationsEnabled)
        {
            CaptureAnimationFrame();
        }
        config.AnimationsEnabled = enabled;
        saveConfig();
        SyncSettingsControls();
    }

    private void OnPerformanceLightingToggled(bool enabled)
    {
        if (synchronizingPerformanceControls) return;
        config.PerformanceLightingEnabled = enabled;
        saveConfig();
        SyncPerformanceControls();
    }

    private void OnHideVegetationToggled(bool enabled)
    {
        if (synchronizingPerformanceControls) return;
        config.HideVegetation = enabled;
        saveConfig();
        SyncPerformanceControls();
    }

    private void OnSkipOpeningAnimationToggled(bool enabled)
    {
        config.SkipOpeningAnimation = enabled;
        saveConfig();
        SyncSettingsControls();
    }

    private void OnCloudsToggled(bool enabled)
    {
        config.CloudsEnabled = enabled;
        saveConfig();
        SyncSettingsControls();
    }

    private void OnLivingEntitiesToggled(bool enabled)
    {
        config.LivingEntitiesEnabled = enabled;
        SaveEntitySettings();
    }

    private void OnPlayersToggled(bool enabled)
    {
        config.ShowPlayers = enabled;
        SaveEntitySettings();
    }

    private void OnAnimalsToggled(bool enabled)
    {
        config.ShowAnimals = enabled;
        SaveEntitySettings();
    }

    private void OnMobsToggled(bool enabled)
    {
        config.ShowMobs = enabled;
        SaveEntitySettings();
    }

    private void OnNpcsToggled(bool enabled)
    {
        config.ShowNpcs = enabled;
        SaveEntitySettings();
    }

    private void OnLiveLightingToggled(bool enabled)
    {
        config.LiveLightingEnabled = enabled;
        // Live and fixed solar controls are meaningful only with directional
        // atlas lighting enabled. Selecting either mode must not silently
        // leave the old performance override on neutral flat daylight.
        config.PerformanceLightingEnabled = true;
        saveConfig();
        SyncSettingsControls();
        SyncPerformanceControls();
    }

    private bool OnFixedSunHourChanged(int hour)
    {
        config.FixedSunHour = Math.Clamp(hour, 0, 23);
        config.LiveLightingEnabled = false;
        config.PerformanceLightingEnabled = true;
        saveConfig();
        SyncSettingsControls();
        SyncPerformanceControls();
        return true;
    }

    private void SaveEntitySettings()
    {
        RefreshVisibleEntityPolicy();
        saveConfig();
        SyncSettingsControls();
    }

    private void RefreshVisibleEntityPolicy()
    {
        bool masterEnabled = config.LivingEntitiesEnabled;
        visibleEntityPolicy.ShowPlayers = masterEnabled
            && config.ShowPlayers
            && (capi.IsSinglePlayer || serverPolicy.ShowPlayers);
        visibleEntityPolicy.ShowAnimals = masterEnabled
            && config.ShowAnimals
            && (capi.IsSinglePlayer || serverPolicy.ShowAnimals);
        visibleEntityPolicy.ShowMobs = masterEnabled
            && config.ShowMobs
            && (capi.IsSinglePlayer || serverPolicy.ShowMobs);
        visibleEntityPolicy.ShowNpcs = masterEnabled
            && config.ShowNpcs
            && (capi.IsSinglePlayer || serverPolicy.ShowNpcs);
    }

    private void SyncSettingsControls()
    {
        if (settingsModal == null) return;
        settingsModal.GetAtlasSwitch("render-on-scroll")?.SetValue(
            presentationChangeCoordinator.DisplayedValue(config.RenderOnScroll)
        );
        settingsModal.GetAtlasSwitch("scroll-realtime-weather")?.SetValue(
            config.ScrollRealtimeWeatherEnabled
        );
        settingsModal.GetAtlasChoice("handheld-instrument")?.SetSelectedIndex(
            HandheldInstrumentChoiceIndex
        );
        settingsModal.GetAtlasSwitch("map-layers")?.SetValue(config.MapLayersEnabled);
        settingsModal.GetAtlasSwitch("search-mode")?.SetValue(
            CreativeCheatSettingsAvailable && config.SearchModeEnabled
        );
        settingsModal.GetAtlasSwitch("search-mode")!.Enabled =
            CreativeCheatSettingsAvailable;
        settingsModal.GetAtlasSwitch("animations")?.SetValue(config.AnimationsEnabled);
        settingsModal.GetAtlasSwitch("skip-opening-animation")?.SetValue(
            config.SkipOpeningAnimation
        );
        bool liveCloudsAvailable =
            VolumetricCloudRendererAdapter.IsEnabledByGraphicsSettings(capi);
        settingsModal.GetAtlasSwitch("clouds")?.SetValue(
            liveCloudsAvailable && config.CloudsEnabled
        );
        settingsModal.GetAtlasSwitch("clouds")!.Enabled = liveCloudsAvailable;
        bool solarLightingActive = config.PerformanceLightingEnabled;
        settingsModal.GetAtlasSwitch("live-lighting")?.SetValue(
            solarLightingActive && config.LiveLightingEnabled
        );
        // When neutral flat lighting is active, allow either solar control to
        // opt back into directional lighting. Do not show a live switch as on
        // while the performance override is silently suppressing it.
        settingsModal.GetAtlasSlider("fixed-sun-hour")!.Enabled =
            !solarLightingActive || !config.LiveLightingEnabled;
        bool serverAllowsAny = capi.IsSinglePlayer || serverPolicy.AnyEntityModels;
        settingsModal.GetAtlasSwitch("entities")?.SetValue(config.LivingEntitiesEnabled && serverAllowsAny);
        settingsModal.GetAtlasSwitch("entities")!.Enabled = serverAllowsAny;
        SyncEntityCategorySwitch("players", config.ShowPlayers, serverPolicy.ShowPlayers);
        SyncEntityCategorySwitch("animals", config.ShowAnimals, serverPolicy.ShowAnimals);
        SyncEntityCategorySwitch("mobs", config.ShowMobs, serverPolicy.ShowMobs);
        SyncEntityCategorySwitch("npcs", config.ShowNpcs, serverPolicy.ShowNpcs);
    }

    private void SyncPerformanceControls()
    {
        if (performanceModal == null) return;

        synchronizingPerformanceControls = true;
        try
        {
            performanceModal.GetAtlasSwitch("performance-lighting")?.SetValue(
                config.PerformanceLightingEnabled
            );
            performanceModal.GetAtlasSwitch("hide-vegetation")?.SetValue(
                config.HideVegetation
            );
        }
        finally
        {
            synchronizingPerformanceControls = false;
        }
    }

    private void SyncCreativeSettingsControls()
    {
        if (creativeSettingsModal == null) return;

        bool available = CreativeCheatSettingsAvailable;
        creativeSettingsModal.GetAtlasSwitch("cave-mode")?.SetValue(
            available && config.CaveModeEnabled
        );
        creativeSettingsModal.GetAtlasSwitch("camera-angle-lock")?.SetValue(
            available && config.CameraAngleLocked
        );
        creativeSettingsModal.GetAtlasSwitch("cave-mode")!.Enabled = available;
        creativeSettingsModal.GetAtlasSwitch("camera-angle-lock")!.Enabled = available;
    }

    private void SyncEntityCategorySwitch(string key, bool clientEnabled, bool serverEnabled)
    {
        bool categoryAllowed = capi.IsSinglePlayer || serverEnabled;
        settingsModal.GetAtlasSwitch(key)?.SetValue(clientEnabled && categoryAllowed);
        settingsModal.GetAtlasSwitch(key)!.Enabled = config.LivingEntitiesEnabled && categoryAllowed;
    }

    private void FocusOnExteriorSurface()
    {
        int x = (int)Math.Floor(targetCenterX);
        int z = (int)Math.Floor(targetCenterZ);
        BlockPos position = new(x, 0, z);
        if (capi.World.BlockAccessor.GetMapChunkAtBlockPos(position) == null) return;

        int surfaceY = capi.World.BlockAccessor.GetRainMapHeightAt(position);
        if (surfaceY > 0)
        {
            centerY = surfaceY + 0.5;
        }
    }

    private void AdvancePausedAnimation()
    {
        long now = capi.ElapsedMilliseconds;
        float realDeltaTime = Math.Clamp(
            (now - lastAtlasFrameMilliseconds) / 1000f,
            0,
            0.25f
        );
        atlasRealDeltaTime = realDeltaTime;
        lastAtlasFrameMilliseconds = now;

        if (config.AnimationsEnabled && capi.IsSinglePlayer && capi.IsGamePaused)
        {
            atlasAnimationSeconds += realDeltaTime;
        }
    }

    private void AdvanceCamera(float realDeltaTime)
    {
        float blend = 1f - MathF.Exp(-14f * Math.Clamp(realDeltaTime, 0, 0.1f));
        if (blend <= 0) return;

        double oldCenterX = centerX;
        double oldCenterZ = centerZ;
        float oldYaw = yawDegrees;
        float oldPitch = pitchDegrees;
        float oldZoom = zoom;

        centerX += (targetCenterX - centerX) * blend;
        centerZ += (targetCenterZ - centerZ) * blend;
        float yawDelta = NormalizeSignedDegrees(targetYawDegrees - yawDegrees);
        yawDegrees = NormalizeDegrees(yawDegrees + yawDelta * blend);
        pitchDegrees += (targetPitchDegrees - pitchDegrees) * blend;
        zoom += (targetZoom - zoom) * blend;

        if (Math.Abs(targetCenterX - centerX) < 0.001) centerX = targetCenterX;
        if (Math.Abs(targetCenterZ - centerZ) < 0.001) centerZ = targetCenterZ;
        if (Math.Abs(NormalizeSignedDegrees(targetYawDegrees - yawDegrees)) < 0.001f)
        {
            yawDegrees = targetYawDegrees;
        }
        if (Math.Abs(targetPitchDegrees - pitchDegrees) < 0.001f) pitchDegrees = targetPitchDegrees;
        if (Math.Abs(targetZoom - zoom) < 0.001f) zoom = targetZoom;

        if (Math.Abs(centerX - oldCenterX) > 0.0001
            || Math.Abs(centerZ - oldCenterZ) > 0.0001
            || Math.Abs(NormalizeSignedDegrees(yawDegrees - oldYaw)) > 0.0001f
            || Math.Abs(pitchDegrees - oldPitch) > 0.0001f
            || Math.Abs(zoom - oldZoom) > 0.0001f)
        {
        }
    }

    private void CaptureAnimationFrame()
    {
        DefaultShaderUniforms uniforms = capi.Render.ShaderUniforms;
        float animationOffset = capi.IsSinglePlayer && capi.IsGamePaused
            ? atlasAnimationSeconds
            : 0;
        frozenWindWaveCounter = uniforms.WindWaveCounter + animationOffset;
        frozenWindWaveCounterHighFrequency = uniforms.WindWaveCounterHighFreq + animationOffset;
        frozenWaterStillCounter = uniforms.WaterStillCounter + animationOffset;
        frozenWaterFlowCounter = uniforms.WaterFlowCounter + animationOffset;
    }

    private bool BeginScrollContentClip()
    {
        if (!config.RenderOnScroll) return false;

        AtlasViewportBounds clip = AtlasViewport.Inset(2);
        capi.Render.GlScissor(
            clip.X,
            Math.Max(0, capi.Render.FrameHeight - clip.Bottom),
            clip.Width,
            clip.Height
        );
        capi.Render.GlScissorFlag(true);
        return true;
    }

    private void EndScrollContentClip(bool clipped)
    {
        if (clipped) capi.Render.GlScissorFlag(false);
    }

    private void ForceOpaqueWindowAlpha()
    {
        IShaderProgram? shader = atlasOpacityShaderProvider();
        if (shader == null || shader.Disposed) return;

        opacityQuad ??= capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        try
        {
            render.CurrentFrameBuffer = null;
            render.CurrentActiveShader?.Stop();
            render.GLDisableDepthTest();
            render.GLDepthMask(false);
            render.GlToggleBlend(false, EnumBlendMode.Standard);
            render.GlColorMask(false, false, false, true);
            shader.Use();
            render.RenderMesh(opacityQuad);
        }
        finally
        {
            renderState.RestoreGuiHandoff(true);
            try
            {
                capi.Render.GetEngineShader(EnumShaderProgram.Gui).Use();
            }
            catch
            {
                // The GUI shader may already be unavailable during teardown.
            }
        }
    }

    private void Rotate(float degrees, KeyEvent args)
    {
        targetYawDegrees = NormalizeDegrees(targetYawDegrees + degrees);
        args.Handled = true;
    }

    private static float NormalizeDegrees(float value)
    {
        value %= 360;
        return value < 0 ? value + 360 : value;
    }

    private static float NormalizeSignedDegrees(float value)
    {
        value = NormalizeDegrees(value);
        return value > 180 ? value - 360 : value;
    }
}
