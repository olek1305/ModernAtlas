using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
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
public sealed partial class ModernAtlasDialog : GuiDialog
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
    private const string SmokeScreenshotMaskEnvironmentVariable =
        "MODERNATLAS_SMOKE_SCREENSHOT_MASK";
    private const string SmokeFixedSunHourEnvironmentVariable =
        "MODERNATLAS_SMOKE_FIXED_SUN_HOUR";
    private const string SmokeScreenshotScaleEnvironmentVariable =
        "MODERNATLAS_SMOKE_SCREENSHOT_SCALE";
    private const string SmokeScreenshotCaptureAreaEnvironmentVariable =
        "MODERNATLAS_SMOKE_CAPTURE_AREA";
    private const string SmokeScreenshotSequenceEnvironmentVariable =
        "MODERNATLAS_SMOKE_SCREENSHOT_SEQUENCE";
    private const string SmokeScreenshotIntensityZeroEnvironmentVariable =
        "MODERNATLAS_SMOKE_SCREENSHOT_INTENSITY_ZERO";
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
    private readonly Func<IShaderProgram?> atlasScreenshotFilterShaderProvider;
    private readonly Func<IShaderProgram?> atlasOpacityShaderProvider;
    private readonly AtlasScrollViewportRenderer scrollViewportRenderer;
    private readonly AtlasScrollRealtimeWeather scrollRealtimeWeather;
    private readonly AtlasCompassRenderer compassRenderer;
    private readonly AtlasTiledScreenshot tileScreenshot;
    private readonly AtlasScreenshotFilterPass screenshotFilterPass;
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
    private GuiComposer? screenshotFilterTuningPanel;
    private GuiComposer? screenshotProgressModal;
    private GuiComposer? screenshotPreviewModal;
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
    private long atlasRenderFrameId;
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
    private bool automatedScreenshotMaskDiagnosticsEnabled;
    private bool automatedScreenshotMaskValidationPassed;
    private bool automatedScreenshotMaskCoverageFailure;
    private bool automatedScreenshotFilterValidationPassed;
    private bool automatedScreenshotFilterReadbackFailure;
    private bool automatedScreenshotIntensityZeroEnabled;
    private int automatedScreenshotFilterComparedTileCount;
    private long automatedScreenshotFilterChangedPixelCount;
    private long automatedScreenshotFilterComparedPixelCount;
    private double automatedScreenshotFilterMinimumChangedRatio;
    private double automatedScreenshotFilterMaximumChangedRatio;
    private int automatedScreenshotFilterMaximumChannelDelta;
    private long automatedScreenshotDiagnosticNextMilliseconds;
    private bool automatedScreenshotCancelMode;
    private bool automatedScreenshotCancelTriggered;
    private bool automatedScreenshotCancelPassed;
    private bool automatedSmokeScreenshotPreviewPending;
    private bool automatedSmokeScreenshotPreviewPassed;
    private bool automatedSmokeScreenshotPreviewTakeCancelPassed;
    private int automatedSmokeScreenshotScale = 2;
    private int automatedSmokeScreenshotCaptureAreaPercent = 100;
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
    private ScreenshotFilterPreferences automatedOriginalScreenshotFilters;
    private bool automatedScreenshotPitchSnapshotted;
    private float automatedScreenshotPitchBeforeCapture;
    private float automatedScreenshotTargetPitchBeforeCapture;
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
    private bool screenshotPreviewOpen;
    private bool screenshotPreviewOpening;
    private bool screenshotPreviewFilterDirty;
    private bool pendingScreenshotPreviewRecompose;
    private bool screenshotPreviewFilterBlocked;
    private int screenshotPreviewExpectedEntityCount;
    private int screenshotPreviewRenderedEntityCount;
    private long screenshotPreviewLastFilterFrame = -1;
    private long lastScreenshotPreviewCloseMilliseconds = -10000;
    private double screenshotPreviewScrollOffset;
    private string? screenshotPreviewPressedButton;
    private string? screenshotPreviewFilterDiagnostic;
    private int screenshotPreviewSourceTextureId;
    private int screenshotPreviewAfterTextureId;
    private FrameBufferRef? screenshotPreviewSourceFramebuffer;
    private int screenshotPreviewSourceDepthTextureId;
    private int screenshotPreviewSourceValidityTextureId;
    private float[] screenshotPreviewProjection = Array.Empty<float>();
    private double[] screenshotPreviewView = Array.Empty<double>();
    private AtlasScreenshotCaptureLayout screenshotPreviewLayout;
    private AtlasScreenshotFilterSettings screenshotPreviewRequestedSettings;
    private AtlasScreenshotFilterSettings screenshotPreviewEffectiveSettings;
    private ElementBounds? screenshotPreviewBeforeBounds;
    private ElementBounds? screenshotPreviewAfterBounds;
    private ElementBounds? screenshotPreviewBounds;
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
    // Field initializers of this partial class stay in the main file so their
    // relative order cannot depend on compilation order (AGENTS.md).
    private static readonly AtlasMapLayer[] MapLayerChoiceOrder =
    {
        AtlasMapLayer.TexturedTerrain,
        AtlasMapLayer.SoilFertility,
        AtlasMapLayer.Moisture,
        AtlasMapLayer.Temperature,
        AtlasMapLayer.OreDensity
    };
    private bool synchronizingMapLayerChoice;
    private bool synchronizingOreFilterChoice;
    private int oreFilterLabelLimit = 40;
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
        ScreenshotFilterTuning,
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

    private readonly record struct ScreenshotFilterPreferences(
        bool Enabled,
        string Preset,
        int Intensity,
        int Saturation,
        int Contrast,
        int Temperature,
        int ShadowTint,
        int AmbientOcclusion,
        int IndirectLight,
        int Bloom,
        int ShadowRed,
        int ShadowGreen,
        int ShadowBlue,
        int HighlightRed,
        int HighlightGreen,
        int HighlightBlue,
        int RedBalance,
        int GreenBalance,
        int BlueBalance
    )
    {
        public ScreenshotFilterPreferences(ModernAtlasConfig config)
            : this(
                config.ScreenshotFiltersEnabled,
                config.ScreenshotFilterPreset,
                config.ScreenshotFilterIntensityPercent,
                config.ScreenshotSaturationPercent,
                config.ScreenshotContrastPercent,
                config.ScreenshotTemperaturePercent,
                config.ScreenshotShadowTintStrengthPercent,
                config.ScreenshotAmbientOcclusionPercent,
                config.ScreenshotIndirectLightPercent,
                config.ScreenshotBloomPercent,
                config.ScreenshotShadowRedPercent,
                config.ScreenshotShadowGreenPercent,
                config.ScreenshotShadowBluePercent,
                config.ScreenshotHighlightRedPercent,
                config.ScreenshotHighlightGreenPercent,
                config.ScreenshotHighlightBluePercent,
                config.ScreenshotRedBalancePercent,
                config.ScreenshotGreenBalancePercent,
                config.ScreenshotBlueBalancePercent
            )
        {
        }

        public void Restore(ModernAtlasConfig config)
        {
            config.ScreenshotFiltersEnabled = Enabled;
            config.ScreenshotFilterPreset = Preset;
            config.ScreenshotFilterIntensityPercent = Intensity;
            config.ScreenshotSaturationPercent = Saturation;
            config.ScreenshotContrastPercent = Contrast;
            config.ScreenshotTemperaturePercent = Temperature;
            config.ScreenshotShadowTintStrengthPercent = ShadowTint;
            config.ScreenshotAmbientOcclusionPercent = AmbientOcclusion;
            config.ScreenshotIndirectLightPercent = IndirectLight;
            config.ScreenshotBloomPercent = Bloom;
            config.ScreenshotShadowRedPercent = ShadowRed;
            config.ScreenshotShadowGreenPercent = ShadowGreen;
            config.ScreenshotShadowBluePercent = ShadowBlue;
            config.ScreenshotHighlightRedPercent = HighlightRed;
            config.ScreenshotHighlightGreenPercent = HighlightGreen;
            config.ScreenshotHighlightBluePercent = HighlightBlue;
            config.ScreenshotRedBalancePercent = RedBalance;
            config.ScreenshotGreenBalancePercent = GreenBalance;
            config.ScreenshotBlueBalancePercent = BlueBalance;
        }
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
    private bool ScreenshotFilterTuningOpen =>
        bottomPanelSection == AtlasPanelSection.ScreenshotFilterTuning;

    private bool ScreenshotFilterUsesCustomPreset =>
        AtlasScreenshotFilterSettings.NormalizePreset(
            config.ScreenshotFilterPreset
        ) == "custom";
    private bool InstrumentPanelOpen => bottomPanelSection == AtlasPanelSection.Instrument;
    private float MinimumPitchDegrees => HasUnlockedCameraPitch
        ? UnlockedMinimumPitchDegrees
        : StandardMinimumPitchDegrees;
    private GuiComposer? ActiveKeyboardComposer => screenshotPreviewModal != null
        ? screenshotPreviewModal
        : interfaceHidden
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
        && screenshotPreviewModal == null
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
        screenshotPreviewModal?.UnfocusOwnElements();
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
        Func<IShaderProgram?> atlasScreenshotFilterShaderProvider,
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
        this.atlasScreenshotFilterShaderProvider = atlasScreenshotFilterShaderProvider;
        this.atlasOpacityShaderProvider = atlasOpacityShaderProvider;
        this.soundController = soundController;
        scrollViewportRenderer = new AtlasScrollViewportRenderer(
            capi,
            atlasScrollShaderProvider
        );
        scrollRealtimeWeather = new AtlasScrollRealtimeWeather(capi);
        compassRenderer = new AtlasCompassRenderer(capi, atlasScrollShaderProvider);
        tileScreenshot = new AtlasTiledScreenshot(capi);
        screenshotFilterPass = new AtlasScreenshotFilterPass(
            capi,
            atlasScreenshotFilterShaderProvider
        );
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
        DisposeScreenshotPreviewState();
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
        atlasRenderFrameId++;
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
            if (screenshotPreviewOpen || screenshotPreviewOpening)
            {
                CloseScreenshotPreview(true);
            }
            RecomposeInterface();
        }
        bool rendererRebound = EnsureExactChunkRenderer();
        if (rendererRebound)
        {
            // Vintage Story may rebuild its chunk renderer when view distance
            // changes. Rebuild only the transient atlas filters against the
            // replacement renderer; the game remains the sole mesh owner.
            PrepareSurfaceSafetyFilter();
            PrepareMapLayer();
            if (screenshotPreviewOpen || screenshotPreviewOpening)
            {
                CloseScreenshotPreview(true);
            }
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
        if (pendingScreenshotPreviewRecompose && screenshotPreviewOpen)
        {
            pendingScreenshotPreviewRecompose = false;
            screenshotPreviewLayout = tileScreenshot.GetCaptureLayout(
                config.ScreenshotScale,
                config.ScreenshotCaptureAreaPercent,
                AtlasViewport.Width / (float)Math.Max(1, AtlasViewport.Height)
            );
            ComposeScreenshotPreviewModal();
            screenshotPreviewFilterDirty = true;
        }
        AdvancePausedAnimation();
        if (!screenshotPreviewOpen && !screenshotPreviewOpening)
        {
            AdvanceCamera(atlasRealDeltaTime);
        }
        // Enforce access before the layer task advances: a revoked Ore density
        // must not run even one more preparation step.
        EnforceMapLayerAccess();
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
        if (!screenshotPreviewOpen && !screenshotPreviewOpening)
        {
            UpdateOreHover(capi.Input.MouseX, capi.Input.MouseY, pointerOverMapPanel);
        }
        if (!loggedFirstRender)
        {
            loggedFirstRender = true;
            capi.Logger.Notification("[ModernAtlas] First 3D atlas GUI frame rendered.");
        }
        bool previewFrozen = screenshotPreviewOpen;
        bool freshAtlasFrame = !previewFrozen && ShouldRenderFreshAtlasFrame();
        bool rendered = previewFrozen
            ? screenshotPreviewSourceTextureId > 0
            : freshAtlasFrame
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
        if (screenshotPreviewOpening && freshAtlasFrame && rendered)
        {
            BeginScreenshotPreviewFromFreshFrame();
        }
        if (screenshotPreviewOpen)
        {
            UpdateScreenshotPreviewFilter();
        }
        capi.Render.GetEngineShader(EnumShaderProgram.Gui).Use();
        capi.Render.GLDepthMask(false);
        capi.Render.GLDisableDepthTest();
        capi.Render.GlDisableCullFace();
        capi.Render.GlToggleBlend(true, EnumBlendMode.Standard);
        if (!interfaceHidden && !screenshotPreviewOpen && SearchModeActive)
        {
            RenderSearchMarkers();
        }
        if (!screenshotPreviewOpen)
        {
            RenderOreHoverCard();
        }
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
        if (!pendingScreenshotRequest && !screenshotPreviewOpen)
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
        if (!interfaceHidden && !screenshotPreviewOpen && !screenshotPreviewOpening)
        {
            UpdateToolbarTooltip(capi.Input.MouseX, capi.Input.MouseY);
            overlay?.Render(deltaTime);
            RenderActiveBottomPanel(deltaTime);
            screenshotProgressModal?.Render(deltaTime);
        }
        if (screenshotPreviewOpen)
        {
            screenshotPreviewModal?.Render(deltaTime);
            RenderScreenshotPreviewTextures();
        }
        if (config.RenderOnScroll && !screenshotPreviewOpen)
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
        if (screenshotPreviewOpen || screenshotPreviewOpening)
        {
            screenshotPreviewPressedButton = null;
            if (args.Button == EnumMouseButton.Left
                && screenshotPreviewModal != null
                && TryGetScreenshotPreviewButtonAt(
                    args.X,
                    args.Y,
                    out string previewButtonKey
                ))
            {
                // Keep the modal's action buttons on a private press/release
                // path. Some Vintage Story HUD dispatchers mark the release
                // handled before it reaches a centered child composer; that
                // used to make BACK and TAKE appear inert to real clicks.
                screenshotPreviewPressedButton = previewButtonKey;
                args.Handled = true;
                return;
            }

            args.Handled = false;
            screenshotPreviewModal?.OnMouseDown(args);
            args.Handled = true;
            return;
        }
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
        if (screenshotPreviewOpen || screenshotPreviewOpening)
        {
            string? pressedButton = screenshotPreviewPressedButton;
            screenshotPreviewPressedButton = null;
            if (pressedButton != null
                && args.Button == EnumMouseButton.Left
                && screenshotPreviewModal?.GetAtlasButton(pressedButton) is GuiElementAtlasButton button
                && button.Bounds.PointInside(args.X, args.Y))
            {
                button.InvokeFromOwner();
                args.Handled = true;
                return;
            }
            args.Handled = false;
            screenshotPreviewModal?.OnMouseUp(args);
            args.Handled = true;
            return;
        }
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
        if (screenshotPreviewOpen || screenshotPreviewOpening)
        {
            args.Handled = false;
            screenshotPreviewModal?.OnMouseMove(args);
            args.Handled = true;
            return;
        }
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
        if (screenshotPreviewOpen || screenshotPreviewOpening)
        {
            args.SetHandled(false);
            screenshotPreviewModal?.OnMouseWheel(args);
            if (!args.IsHandled && screenshotPreviewModal != null)
            {
                float wheel = args.deltaPrecise != 0
                    ? args.deltaPrecise
                    : args.delta;
                screenshotPreviewScrollOffset = Math.Clamp(
                    screenshotPreviewScrollOffset - wheel * 18,
                    0,
                    ScreenshotPreviewScrollMaximum(
                        screenshotPreviewBounds?.fixedWidth ?? 0,
                        screenshotPreviewBounds?.fixedHeight ?? 0
                    )
                );
                // The scroll offset is baked into the slider element bounds
                // during composition. ReCompose() only repaints the existing
                // element tree, so rebuild the modal to move every control
                // while retaining the frozen GPU source and current config.
                ComposeScreenshotPreviewModal();
            }
            args.SetHandled();
            return;
        }
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
                    && (bottomPanelSection == AtlasPanelSection.Settings
                        || bottomPanelSection
                            == AtlasPanelSection.ScreenshotOptions
                        || bottomPanelSection
                            == AtlasPanelSection.ScreenshotFilterTuning))
                {
                    float wheel = args.deltaPrecise != 0
                        ? args.deltaPrecise
                        : args.delta;
                    double width = bottomPanelBounds?.fixedWidth ?? 0;
                    double height = bottomPanelBounds?.fixedHeight ?? 0;
                    double maximum = bottomPanelSection switch
                    {
                        AtlasPanelSection.ScreenshotOptions =>
                            ScreenshotOptionsScrollMaximum(width, height),
                        AtlasPanelSection.ScreenshotFilterTuning =>
                            ScreenshotFilterTuningScrollMaximum(width, height),
                        _ => SettingsScrollMaximum(width, height)
                    };
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
            if (screenshotPreviewOpen || screenshotPreviewOpening)
            {
                CloseScreenshotPreview(true);
                args.Handled = true;
                return;
            }
            HandleEscape();
            args.Handled = true;
            return;
        }

        ActiveKeyboardComposer?.OnKeyDown(args, false);
        if (args.Handled) return;

        if (screenshotPreviewOpen || screenshotPreviewOpening)
        {
            if (args.KeyCode == (int)GlKeys.G)
            {
                CloseScreenshotPreview(false);
                CloseAtlas();
            }
            args.Handled = true;
            return;
        }

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
        CloseScreenshotPreview(false);
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
        // The transition dialog owns the readback and blur. This callback only
        // confirms that the ordinary world completed its handoff; it never
        // changes an ordinary chunk shader, framebuffer or camera.
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
        if (atlasFrameCacheTexture is not { TextureId: > 0 }) return true;

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
        automatedSmokeScreenshotPreviewPending = false;
        automatedSmokeScreenshotPreviewPassed = false;
        automatedSmokeScreenshotPreviewTakeCancelPassed = false;
        automatedSmokeScreenshotScale = 2;
        automatedSmokeScreenshotCaptureAreaPercent = 100;
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
        CloseScreenshotPreview(false);
        tileScreenshot.Cancel();
        screenshotFilterPass.ResetWorldResources();
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
        // The ore hover card keeps its own inspection state. Clearing it here
        // keeps that state consistent with the released textures so a frame
        // drawn during world teardown cannot reference a released card.
        ClearOreHover();
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
        DisposeScreenshotPreviewState();
        tileScreenshot.Dispose();
        screenshotFilterPass.Dispose();
        base.Dispose();
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
        if (screenshotPreviewOpen || screenshotPreviewOpening)
        {
            CloseScreenshotPreview(true);
            return true;
        }
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
        if (capi.ElapsedMilliseconds - lastScreenshotPreviewCloseMilliseconds < 200)
        {
            // OnEscapePressed can follow OnKeyDown for the same physical key.
            // The first Escape must return from Screenshot Preview only; the
            // next physical Escape remains the normal atlas close action.
            return true;
        }
        return CloseAtlas();
    }

}
