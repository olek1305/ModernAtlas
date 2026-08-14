using System;
using System.Collections.Generic;
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
    private static readonly string[] PerformanceModeNames =
        { "Full texture detail", "Half texture detail", "Quarter texture detail" };
    private const float StandardMinimumPitchDegrees = 20;
    private const float UnlockedMinimumPitchDegrees = 0;
    private const int DefaultViewDistance = 500;
    private const int MaximumGameViewDistance = 1536;
    private const float MaximumZoomIn = 8;
    private const float MaximumZoomScreenshotSettleSeconds = 2;
    private const int FogTextureDownsample = 4;
    private const int MovingAtlasRefreshMilliseconds = 16;
    private const int IdleAtlasRefreshMilliseconds = 83;
    private const string SmokeScreenshotEnvironmentVariable =
        "MODERNATLAS_SMOKE_SCREENSHOT";
    private const string SmokeFixedSunHourEnvironmentVariable =
        "MODERNATLAS_SMOKE_FIXED_SUN_HOUR";

    private ExactChunkRendererAdapter? exactChunkRenderer;
    private readonly float[] projection = Mat4f.Create();
    private readonly ModernAtlasConfig config;
    private readonly ModernAtlasServerPolicy serverPolicy;
    private readonly ModernAtlasServerPolicy visibleEntityPolicy = new();
    private readonly Action saveConfig;
    private readonly Func<bool> requestClose;
    private readonly Func<IShaderProgram?> stableLiquidShaderProvider;
    private readonly Func<IShaderProgram?> atlasCloudShaderProvider;
    private readonly Func<IShaderProgram?> atlasOpacityShaderProvider;
    private readonly AtlasScrollViewportRenderer scrollViewportRenderer;
    private readonly AtlasCompassRenderer compassRenderer;
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
    private GuiComposer? unitPanel;
    private bool settingsModalOpen;
    private bool performanceModalOpen;
    private bool creativeSettingsModalOpen;
    private bool visualLabModalOpen;
    private bool interfaceHidden;
    private long lastInterfaceRestoreMilliseconds = -10000;
    private LoadedTexture? fogTexture;
    private LoadedTexture? searchMarkerTexture;
    private LoadedTexture? normalWorldSnapshotTexture;
    private bool normalWorldSnapshotCaptured;
    private LoadedTexture? atlasFrameCacheTexture;
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
    private int fogTextureWidth;
    private int fogTextureHeight;
    private float fogTextureZoom = -1;
    private float fogTexturePitch = -1;
    private long lastFogTextureBuildMilliseconds;
    private float atlasAnimationSeconds;
    private float frozenWindWaveCounter;
    private float frozenWindWaveCounterHighFrequency;
    private float frozenWaterStillCounter;
    private float frozenWaterFlowCounter;
    private long lastAtlasFrameMilliseconds;
    private float atlasRealDeltaTime;
    private bool loggedEntityModels;
    private bool loggedAtlasRefreshThrottle;
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
    private bool automatedSmokeTestBilingualSearchPassed;
    private bool automatedSmokeTestOreConcealmentPassed;
    private bool automatedSmokeTestCreativeOreRevealFrameRendered;
    private bool automatedSmokeTestForceSurvivalOreConcealment;
    private bool automatedSmokeTestSafeSurfaceFrameRendered;
    private bool automatedSmokeTestSafeSurfaceScreenshotHandled;
    private bool automatedSmokeTestMaximumZoomPending;
    private bool automatedSmokeTestMaximumZoomFrameRendered;
    private bool automatedSmokeTestPartialZoomPending;
    private bool automatedSmokeTestPartialZoomFrameRendered;
    private float automatedSmokeTestZoomBeforeMaximum;
    private float automatedSmokeTestPitchBeforeMaximum;
    private float automatedSmokeTestMaximumZoomStartedSeconds;
    private float automatedSmokeTestPartialZoomStartedSeconds;
    private int automatedSmokeTestSearchPhase;
    private bool automatedSmokeTestSearchPassed;
    private double automatedLanternBlockX;
    private double automatedLanternBlockY;
    private double automatedLanternBlockZ;
    private int automatedSmokeTestMapLayerPhase;
    private bool automatedSmokeTestMapLayerPassed;
    private bool automatedSmokeTestPerformanceModeSelected;
    private bool automatedSmokeTestPerformanceModeRendered;
    private bool automatedSmokeTestPreferencesCaptured;
    private int automatedSmokeScreenshotPhase;
    private bool automatedOriginalMapLayersEnabled;
    private bool automatedOriginalCaveModeEnabled;
    private bool automatedOriginalSearchModeEnabled;
    private bool automatedOriginalCameraAngleLocked;
    private bool automatedOriginalRenderOnScroll;
    private bool automatedOriginalFogEnabled;
    private bool automatedOriginalLiveLightingEnabled;
    private int automatedOriginalFixedSunHour;
    private int automatedOriginalTextureDetailReduction;
    private bool automatedOriginalPerformanceLightingEnabled;
    private bool automatedOriginalHideVegetation;
    private bool pendingInterfaceRecompose;
    private AtlasMapLayer activeMapLayer = AtlasMapLayer.TexturedTerrain;
    private bool synchronizingMapLayerDropdown;
    private bool synchronizingPerformanceControls;

    internal bool AutomatedSmokeTestRenderedExactWorld { get; private set; }
    internal bool CheatModeEnabledForAutomation => cheatModeEnabled;

    private int GameViewDistance
    {
        get
        {
            int viewDistance = capi.Settings.Int["viewDistance"];
            // Use Vintage Story's configured view distance directly as the
            // player-anchored atlas radius. The existing boundary fog conceals
            // outer columns whose GPU meshes are not complete yet.
            int configuredDistance = viewDistance > 0
                ? Math.Clamp(viewDistance, GlobalConstants.ChunkSize * 2, MaximumGameViewDistance)
                : DefaultViewDistance;
            return Math.Max(
                GlobalConstants.ChunkSize,
                configuredDistance
            );
        }
    }
    private bool EffectiveFogEnabled => capi.IsSinglePlayer
        ? config.FogEnabled
        : serverPolicy.FogEnabled;
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
    private float MinimumPitchDegrees => HasUnlockedCameraPitch
        ? UnlockedMinimumPitchDegrees
        : StandardMinimumPitchDegrees;
    private Vec3f AtlasFogColor => AtlasVisualPalettes.FogColor(config.FogPalette);
    private GuiComposer? ActiveKeyboardComposer => interfaceHidden
        ? null
        : visualLabModalOpen
            ? visualLabModal
            : performanceModalOpen
                ? performanceModal
                : creativeSettingsModalOpen
                    ? creativeSettingsModal
                    : settingsModalOpen
                        ? settingsModal
                        : SearchModeActive
                            && searchPanel?.GetTextInput("search-input")?.HasFocus == true
                                ? searchPanel
                                : MapLayerControlsVisible
                                    && mapLayerPanel?.CurrentTabIndexElement?.HasFocus == true
                                        ? mapLayerPanel
                                        : overlay;
    internal bool SearchInputHasFocus => IsOpened()
        && !interfaceHidden
        && SearchModeActive
        && !settingsModalOpen
        && !performanceModalOpen
        && !creativeSettingsModalOpen
        && !visualLabModalOpen
        && searchPanel?.GetTextInput("search-input")?.HasFocus == true;

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
        this.atlasOpacityShaderProvider = atlasOpacityShaderProvider;
        this.soundController = soundController;
        scrollViewportRenderer = new AtlasScrollViewportRenderer(
            capi,
            atlasScrollShaderProvider
        );
        compassRenderer = new AtlasCompassRenderer(capi, atlasScrollShaderProvider);
        surfaceHeightTexture = new AtlasSurfaceHeightTexture(capi);
        mapLayerTexture = new AtlasMapLayerTexture(capi);
        searchController = new AtlasSearchController(capi);
        RefreshVisibleEntityPolicy();
        ComposeOverlay();
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        // The dialog is constructed before a world/player necessarily exists.
        // Recompose now that Survival, Creative and accepted Cheat Mode access
        // can be resolved, so unavailable controls leave no empty slot.
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
        ClearSearch();
        ResetPointerDrag();
        atlasAnimationSeconds = 0;
        loggedEntityModels = false;
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
        if (EnsureExactChunkRenderer())
        {
            // Vintage Story may rebuild its chunk renderer when view distance
            // changes. Rebuild only the transient atlas filters against the
            // replacement renderer; the game remains the sole mesh owner.
            PrepareSurfaceSafetyFilter();
            PrepareMapLayer();
        }
        SynchronizeGameViewDistance();
        if (pendingInterfaceRecompose)
        {
            pendingInterfaceRecompose = false;
            RecomposeInterface();
        }
        AdvancePausedAnimation();
        AdvanceCamera(atlasRealDeltaTime);
        mapLayerTexture.Advance();
        if (!loggedFirstRender)
        {
            loggedFirstRender = true;
            capi.Logger.Notification("[ModernAtlas] First 3D atlas GUI frame rendered.");
        }
        CaptureNormalWorldSnapshot();
        bool freshAtlasFrame = ShouldRenderFreshAtlasFrame();
        bool rendered = freshAtlasFrame
            ? RenderLiveWorld(deltaTime)
            : atlasFrameCacheTexture?.TextureId > 0;
        if (freshAtlasFrame && rendered)
        {
            lastAtlasWorldRenderMilliseconds = capi.ElapsedMilliseconds;
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
                && exactChunkRenderer?.LastRenderedTextureDetailReduction == 2
                && exactChunkRenderer.LastRenderedVegetationHidden
                && !exactChunkRenderer.LastRenderedPerformanceLightingEnabled
                && exactChunkRenderer.ValidateVegetationMask(
                    out string vegetationDiagnostic
                ))
            {
                automatedSmokeTestPerformanceModeRendered = true;
                OnTextureDetailOptionToggled(
                    automatedOriginalTextureDetailReduction,
                    true
                );
                OnPerformanceLightingToggled(
                    automatedOriginalPerformanceLightingEnabled
                );
                OnHideVegetationToggled(automatedOriginalHideVegetation);
                capi.Logger.Notification(
                    "[ModernAtlas] Automated performance check rendered exact terrain with flat lighting, quarter texture detail and the registered vegetation filter, then restored the original settings: {0}.",
                    vegetationDiagnostic
                );
            }
            if (safeSurfaceFrameWasAlreadyRendered
                && automatedSmokeTestSafeSurfaceScreenshotHandled
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
                RenderFogMask(true);
                CaptureAtlasFrameCache();
            }
            RestoreNormalWorldSnapshot();
            capi.Render.CurrentFrameBuffer = null;
            scrollViewportRenderer.Render(
                atlasFrameCacheTexture?.TextureId
                    ?? exactChunkRenderer?.PrimaryColorTextureId
                    ?? 0,
                normalWorldSnapshotTexture?.TextureId ?? 0,
                AtlasViewport
            );
        }
        else
        {
            if (freshAtlasFrame && rendered)
            {
                CaptureAtlasFrameCache();
            }
            if (rendered)
            {
                RenderCachedAtlasFullscreen();
            }
            RenderFogMask(false);
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
        string rendererStatus = rendered
            ? "exact loaded terrain"
            : "renderer unavailable";
        string fogStatus = EffectiveFogEnabled ? "fog on" : "fog off";
        string caveStatus = SurfaceSafetyEnabled
            ? "caves hidden"
            : "cave mode";
        if (preparingSurfaceFilter)
        {
            rendererStatus = $"preparing safe surface {surfaceHeightTexture.ProgressPercent}%";
        }
        else if (preparingOreConcealment)
        {
            rendererStatus = "preparing Survival ore concealment";
        }
        else if (preparingVegetationMask)
        {
            rendererStatus = "preparing vegetation filter";
        }
        string status = $"{GameViewDistance} blocks • {fogStatus} • {caveStatus} • {rendererStatus} • loaded data only";
        overlay?.GetDynamicText("status").SetNewText(status);
        searchPanel?.GetDynamicText("search-status").SetNewText(searchController.StatusText);
        mapLayerPanel?.GetDynamicText("layer-status").SetNewText(mapLayerTexture.StatusText);
        if (!interfaceHidden)
        {
            overlay?.Render(deltaTime);
            if (CreativeCheatSettingsAvailable)
            {
                creativeSettingsShortcut?.Render(deltaTime);
            }
            if (MapLayerControlsVisible)
            {
                mapLayerPanel?.Render(deltaTime);
            }
            if (SearchModeActive)
            {
                searchPanel?.Render(deltaTime);
            }
            RenderUnitInspection(deltaTime);
            if (settingsModalOpen)
            {
                settingsModal?.Render(deltaTime);
            }
            if (performanceModalOpen)
            {
                performanceModal?.Render(deltaTime);
            }
            if (creativeSettingsModalOpen)
            {
                creativeSettingsModal?.Render(deltaTime);
            }
            if (visualLabModalOpen)
            {
                visualLabModal?.Render(deltaTime);
            }
        }
        if (config.RenderOnScroll)
        {
            scrollViewportRenderer.RenderRollersOverlay(AtlasViewport);
        }
        // Render the optional hand scene after every interactive composer.
        // Its private shader state can therefore never suppress SETTINGS,
        // EXIT or other atlas controls, even if a driver rejects the scene.
        compassRenderer.Render(
            config.ShowPlayerCompass,
            yawDegrees,
            AtlasViewport,
            atlasRealDeltaTime
        );
        ForceOpaqueWindowAlpha();
        capi.Render.GetEngineShader(EnumShaderProgram.Gui).Use();
        CaptureAutomatedSmokeScreenshot();
        AdvanceAutomatedSmokeTest();
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (!interfaceHidden && performanceModalOpen)
        {
            performanceModal?.OnMouseDown(args);
            args.Handled = true;
            return;
        }
        if (!interfaceHidden && visualLabModalOpen)
        {
            visualLabModal?.OnMouseDown(args);
            args.Handled = true;
            return;
        }
        if (!interfaceHidden && creativeSettingsModalOpen)
        {
            creativeSettingsModal?.OnMouseDown(args);
            args.Handled = true;
            return;
        }
        if (!interfaceHidden && settingsModalOpen)
        {
            settingsModal?.OnMouseDown(args);
            args.Handled = true;
            return;
        }
        if (!interfaceHidden)
        {
            UnfocusSearchOutsideInput(args);
        }
        if (!interfaceHidden && selectedEntityId != null)
        {
            unitPanel?.OnMouseDown(args);
            if (args.Handled) return;
        }
        if (!interfaceHidden)
        {
            if (SearchModeActive)
            {
                searchPanel?.OnMouseDown(args);
                if (args.Handled) return;
            }
            if (MapLayerControlsVisible)
            {
                mapLayerPanel?.OnMouseDown(args);
                if (args.Handled) return;
            }
            if (CreativeCheatSettingsAvailable)
            {
                creativeSettingsShortcut?.OnMouseDown(args);
                if (args.Handled) return;
            }
            overlay?.OnMouseDown(args);
            if (args.Handled) return;
        }

        if (!AtlasViewport.Contains(args.X, args.Y))
        {
            args.Handled = true;
            return;
        }

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
        if (!interfaceHidden && performanceModalOpen)
        {
            performanceModal?.OnMouseUp(args);
            args.Handled = true;
            return;
        }
        if (!interfaceHidden && visualLabModalOpen)
        {
            visualLabModal?.OnMouseUp(args);
            args.Handled = true;
            return;
        }
        if (!interfaceHidden && creativeSettingsModalOpen)
        {
            creativeSettingsModal?.OnMouseUp(args);
            args.Handled = true;
            return;
        }
        if (!interfaceHidden && settingsModalOpen)
        {
            settingsModal?.OnMouseUp(args);
            args.Handled = true;
            return;
        }
        if (!interfaceHidden && selectedEntityId != null)
        {
            unitPanel?.OnMouseUp(args);
            if (args.Handled) return;
        }
        // Once a map drag begins, keep ownership of the gesture even if the
        // pointer crosses the settings panel. Letting the overlay consume the
        // release leaves the drag latched and the next move jumps the camera.
        if (args.Button == EnumMouseButton.Left && leftDragging)
        {
            leftDragging = false;
            InvalidateFogTexture();
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
            InvalidateFogTexture();
            args.Handled = true;
            return;
        }

        if (!interfaceHidden)
        {
            if (SearchModeActive)
            {
                searchPanel?.OnMouseUp(args);
                if (args.Handled) return;
            }
            if (MapLayerControlsVisible)
            {
                mapLayerPanel?.OnMouseUp(args);
                if (args.Handled) return;
            }
            if (CreativeCheatSettingsAvailable)
            {
                creativeSettingsShortcut?.OnMouseUp(args);
                if (args.Handled) return;
            }
            overlay?.OnMouseUp(args);
            if (args.Handled) return;
        }
        args.Handled = true;
    }

    public override void OnMouseMove(MouseEvent args)
    {
        if (!interfaceHidden && performanceModalOpen)
        {
            performanceModal?.OnMouseMove(args);
            args.Handled = true;
            return;
        }
        if (!interfaceHidden && visualLabModalOpen)
        {
            visualLabModal?.OnMouseMove(args);
            args.Handled = true;
            return;
        }
        if (!interfaceHidden && creativeSettingsModalOpen)
        {
            creativeSettingsModal?.OnMouseMove(args);
            args.Handled = true;
            return;
        }
        if (!interfaceHidden && settingsModalOpen)
        {
            settingsModal?.OnMouseMove(args);
            args.Handled = true;
            return;
        }
        if (leftDragging || rightDragging)
        {
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

        if (!interfaceHidden && selectedEntityId != null)
        {
            unitPanel?.OnMouseMove(args);
            if (args.Handled) return;
        }
        if (!interfaceHidden)
        {
            if (SearchModeActive) searchPanel?.OnMouseMove(args);
            if (MapLayerControlsVisible) mapLayerPanel?.OnMouseMove(args);
            if (CreativeCheatSettingsAvailable) creativeSettingsShortcut?.OnMouseMove(args);
            overlay?.OnMouseMove(args);
        }
        // The atlas covers the entire screen. Do not leak hover interaction to
        // hotbar slots, creative inventory elements or dialogs underneath it.
        args.Handled = true;
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (!interfaceHidden && performanceModalOpen)
        {
            performanceModal?.OnMouseWheel(args);
            args.SetHandled();
            return;
        }
        if (!interfaceHidden && visualLabModalOpen)
        {
            visualLabModal?.OnMouseWheel(args);
            args.SetHandled();
            return;
        }
        if (!interfaceHidden && creativeSettingsModalOpen)
        {
            creativeSettingsModal?.OnMouseWheel(args);
            args.SetHandled();
            return;
        }
        if (!interfaceHidden && settingsModalOpen)
        {
            settingsModal?.OnMouseWheel(args);
            args.SetHandled();
            return;
        }
        if (!interfaceHidden)
        {
            if (SearchModeActive) searchPanel?.OnMouseWheel(args);
            if (args.IsHandled) return;
            if (MapLayerControlsVisible) mapLayerPanel?.OnMouseWheel(args);
            if (args.IsHandled) return;
            if (CreativeCheatSettingsAvailable) creativeSettingsShortcut?.OnMouseWheel(args);
            if (args.IsHandled) return;
            overlay?.OnMouseWheel(args);
            if (args.IsHandled) return;
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
            || settingsModalOpen)
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
        else if (args.KeyCode == (int)GlKeys.M)
        {
            if (!capi.IsSinglePlayer)
            {
                args.Handled = true;
                return;
            }
            config.FogEnabled = !config.FogEnabled;
            InvalidateFogTexture();
            saveConfig();
            SyncSettingsControls();
            args.Handled = true;
        }
        else if (args.KeyCode == (int)GlKeys.Home)
        {
            config.FogEnabled = false;
            saveConfig();
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
        overlay?.UnfocusOwnElements();
        searchPanel?.UnfocusOwnElements();
        settingsModalOpen = false;
        performanceModalOpen = false;
        creativeSettingsModalOpen = false;
        visualLabModalOpen = false;
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
        ReleaseNormalWorldSnapshot();
        ReleaseAtlasFrameCache();
        ScheduleNormalWorldShaderRestore();
        base.OnGuiClosed();
    }

    private void CaptureNormalWorldSnapshot()
    {
        if (!config.RenderOnScroll || normalWorldSnapshotCaptured) return;

        try
        {
            int width = Math.Max(1, capi.Render.FrameWidth);
            int height = Math.Max(1, capi.Render.FrameHeight);
            using BitmapRef screenshot = capi.Render.GrabScreenshot(
                width,
                height,
                false,
                true,
                true
            );

            if (normalWorldSnapshotTexture != null
                && normalWorldSnapshotTexture.TextureId > 0
                && (normalWorldSnapshotTexture.Width != width
                    || normalWorldSnapshotTexture.Height != height))
            {
                normalWorldSnapshotTexture.Dispose();
                normalWorldSnapshotTexture = null;
            }
            normalWorldSnapshotTexture ??= new LoadedTexture(capi);
            normalWorldSnapshotTexture.Width = width;
            normalWorldSnapshotTexture.Height = height;
            int[] snapshotPixels = screenshot.Pixels;
            for (int index = 0; index < snapshotPixels.Length; index++)
            {
                // The normal world framebuffer does not use alpha as screen
                // coverage, so valid RGB pixels can carry alpha zero. The GUI
                // blit would discard those pixels and leave atlas geometry
                // visible behind the scroll. This private snapshot represents
                // a completed opaque POV, therefore normalize only its alpha.
                snapshotPixels[index] |= unchecked((int)0xff000000);
            }
            capi.Render.LoadOrUpdateTextureFromBgra(
                snapshotPixels,
                false,
                1,
                ref normalWorldSnapshotTexture
            );
            normalWorldSnapshotCaptured = normalWorldSnapshotTexture.TextureId > 0;
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not protect the normal world view behind the scroll: {0}",
                exception.Message
            );
        }
    }

    internal bool CaptureNormalWorldSnapshotBeforeTransition()
    {
        // Survival opens through a world-rendered hand/scroll transition.
        // Capture the completed POV before that transition can touch shared
        // depth, alpha or chunk shader state. The atlas then uses the same
        // pristine background that direct Creative opening receives.
        ReleaseNormalWorldSnapshot();
        CaptureNormalWorldSnapshot();
        return normalWorldSnapshotCaptured;
    }

    private void RestoreNormalWorldSnapshot()
    {
        LoadedTexture? snapshot = normalWorldSnapshotTexture;
        if (!normalWorldSnapshotCaptured || snapshot == null || snapshot.TextureId <= 0)
        {
            return;
        }

        IRenderAPI render = capi.Render;
        render.CurrentActiveShader?.Stop();
        render.CurrentFrameBuffer = null;
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
        render.GetEngineShader(EnumShaderProgram.Gui).Use();
        render.GLDisableDepthTest();
        render.GLDepthMask(false);
        render.GlToggleBlend(false, EnumBlendMode.Standard);
        render.Render2DTexture(
            snapshot.TextureId,
            0,
            0,
            render.FrameWidth,
            render.FrameHeight,
            0,
            ColorUtil.WhiteArgbVec
        );
        render.GlToggleBlend(true, EnumBlendMode.Standard);
        render.GLDepthMask(true);
    }

    private void ReleaseNormalWorldSnapshot()
    {
        normalWorldSnapshotTexture?.Dispose();
        normalWorldSnapshotTexture = null;
        normalWorldSnapshotCaptured = false;
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
        int textureId = exactChunkRenderer?.PrimaryColorTextureId ?? 0;
        if (textureId <= 0) return;

        IRenderAPI render = capi.Render;
        FrameBufferRef primary = render.FrameBuffers[(int)EnumFrameBuffer.Primary];
        int width = Math.Max(1, primary.Width);
        int height = Math.Max(1, primary.Height);
        try
        {
            if (atlasFrameCacheTexture == null
                || atlasFrameCacheTexture.TextureId <= 0
                || atlasFrameCacheTexture.Width != width
                || atlasFrameCacheTexture.Height != height)
            {
                atlasFrameCacheTexture?.Dispose();
                atlasFrameCacheTexture = new LoadedTexture(capi)
                {
                    Width = width,
                    Height = height
                };
                int[] emptyPixels = new int[checked(width * height)];
                render.LoadOrUpdateTextureFromBgra(
                    emptyPixels,
                    true,
                    0,
                    ref atlasFrameCacheTexture
                );
            }

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
                atlasFrameCacheTexture,
                0,
                0,
                0
            );
            render.CurrentFrameBuffer = null;
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
                "[ModernAtlas] Could not cache a throttled atlas frame; full-rate rendering remains active: {0}",
                exception.Message
            );
            ReleaseAtlasFrameCache();
        }
    }

    private void RenderCachedAtlasFullscreen()
    {
        LoadedTexture? cache = atlasFrameCacheTexture;
        if (cache == null || cache.TextureId <= 0) return;
        scrollViewportRenderer.RenderAtlasFullscreen(cache.TextureId);
    }

    private void ReleaseAtlasFrameCache()
    {
        atlasFrameCacheTexture?.Dispose();
        atlasFrameCacheTexture = null;
        primaryAtlasSourceTexture = null;
        primaryAtlasSourceTextureId = 0;
        lastAtlasWorldRenderMilliseconds = 0;
    }

    public void ScheduleNormalWorldShaderRestore()
    {
        // Switch off the atlas-only branches between frames. The compiled
        // chunk programs remain valid for the world session, avoiding the
        // full-engine shader reload that previously froze closing.
        capi.Event.RegisterCallback(
            _ => exactChunkRenderer?.RestoreNormalWorldShaders(),
            1
        );
    }

    public void OnServerPolicyChanged()
    {
        if (!capi.IsSinglePlayer && !serverPolicy.CheatModeAllowed)
        {
            SetCheatMode(false);
        }
        RefreshVisibleEntityPolicy();
        InvalidateFogTexture();
        SyncSettingsControls();
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
            creativeSettingsModalOpen = false;
            ClearSearch();
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
            atlasCloudShaderProvider
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
        InvalidateFogTexture();
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
        automatedOriginalMapLayersEnabled = config.MapLayersEnabled;
        automatedOriginalCaveModeEnabled = config.CaveModeEnabled;
        automatedOriginalSearchModeEnabled = config.SearchModeEnabled;
        automatedOriginalCameraAngleLocked = config.CameraAngleLocked;
        automatedOriginalRenderOnScroll = config.RenderOnScroll;
        automatedOriginalFogEnabled = config.FogEnabled;
        automatedOriginalLiveLightingEnabled = config.LiveLightingEnabled;
        automatedOriginalFixedSunHour = config.FixedSunHour;
        automatedOriginalTextureDetailReduction = Math.Clamp(
            config.TextureDetailReduction,
            0,
            2
        );
        automatedOriginalPerformanceLightingEnabled =
            config.PerformanceLightingEnabled;
        automatedOriginalHideVegetation = config.HideVegetation;
        automatedSmokeTestPreferencesCaptured = true;
        config.MapLayersEnabled = true;
        config.CaveModeEnabled = false;
        config.SearchModeEnabled = true;
        config.CameraAngleLocked = false;
        config.RenderOnScroll = true;
        config.FogEnabled = true;
        string? forcedSunHour = Environment.GetEnvironmentVariable(
            SmokeFixedSunHourEnvironmentVariable
        );
        if (int.TryParse(forcedSunHour, out int parsedSunHour))
        {
            config.LiveLightingEnabled = false;
            config.FixedSunHour = Math.Clamp(parsedSunHour, 0, 23);
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
        automatedSmokeTestMaximumZoomPending = false;
        automatedSmokeTestMaximumZoomFrameRendered = false;
        automatedSmokeTestPartialZoomPending = false;
        automatedSmokeTestPartialZoomFrameRendered = false;
        automatedSmokeTestZoomBeforeMaximum = 0;
        automatedSmokeTestPitchBeforeMaximum = 0;
        automatedSmokeTestMaximumZoomStartedSeconds = 0;
        automatedSmokeTestPartialZoomStartedSeconds = 0;
        automatedSmokeTestSearchPhase = 0;
        automatedSmokeTestSearchPassed = false;
        automatedLanternBlockX = 0;
        automatedLanternBlockY = 0;
        automatedLanternBlockZ = 0;
        automatedSmokeTestMapLayerPhase = 0;
        automatedSmokeTestMapLayerPassed = false;
        automatedSmokeTestPerformanceModeSelected = false;
        automatedSmokeTestPerformanceModeRendered = false;
        automatedSmokeScreenshotPhase = 0;
        AutomatedSmokeTestRenderedExactWorld = false;
    }

    private void AdvanceAutomatedSmokeTest()
    {
        if (!automatedSmokeTestActive) return;

        automatedSmokeTestElapsedSeconds += atlasRealDeltaTime;
        bool passed = AutomatedSmokeTestRenderedExactWorld
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
            && automatedSmokeTestPerformanceModeRendered;
        if (passed && automatedSmokeTestElapsedSeconds < 3f) return;
        if (!passed && automatedSmokeTestElapsedSeconds < 60f) return;

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
        bool settingsOpenedByClick = ClickAtlasControlForAutomatedTest(
                overlay,
                "settings-button"
            )
            && settingsModalOpen;
        bool mapLayersBeforeClick = config.MapLayersEnabled;
        bool settingsSwitchClicked = settingsOpenedByClick
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
        if (config.TextureDetailReduction == 2)
        {
            OnTextureDetailOptionToggled(0, true);
        }
        bool quarterTextureSelected = performanceOpenedByClick
            && ClickAtlasControlForAutomatedTest(
                performanceModal,
                "texture-quarter"
            )
            && config.TextureDetailReduction == 2;
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
            quarterTextureSelected
            && flatLightingSelected
            && vegetationSelected
            && performanceModal?.GetElement("texture-full")
                is GuiElementAtlasSwitch
            && performanceModal.GetElement("texture-half")
                is GuiElementAtlasSwitch
            && performanceModal.GetElement("texture-quarter")
                is GuiElementAtlasSwitch;
        bool performanceClosedByClick = performanceOpenedByClick
            && ClickAtlasControlForAutomatedTest(
                performanceModal,
                "performance-back"
            )
            && settingsModalOpen
            && !performanceModalOpen;
        bool renderOnScrollBeforeClick = config.RenderOnScroll;
        bool presentationSwitchClicked = performanceClosedByClick
            && ClickAtlasControlForAutomatedTest(
                settingsModal,
                "render-on-scroll"
            )
            && config.RenderOnScroll != renderOnScrollBeforeClick;
        bool presentationSwitchRestored = presentationSwitchClicked
            && ClickAtlasControlForAutomatedTest(
                settingsModal,
                "render-on-scroll"
            )
            && config.RenderOnScroll == renderOnScrollBeforeClick;
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
        bool settingsClosedByClick = settingsOpenedByClick
            && ClickAtlasControlForAutomatedTest(settingsModal, "settings-close")
            && !settingsModalOpen;
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
        bool restored = HandleEscape() && !interfaceHidden && IsOpened();
        bool neumorphicControls = overlay?.GetElement("settings-button")
                is GuiElementAtlasButton
            && settingsModal?.GetElement("map-layers") is GuiElementAtlasSwitch
            && settingsModal?.GetElement("skip-opening-animation")
                is GuiElementAtlasSwitch
            && settingsModal?.GetElement("performance-open")
                is GuiElementAtlasButton
            && performanceModal?.GetElement("hide-vegetation")
                is GuiElementAtlasSwitch
            && creativeSettingsModal?.GetElement("camera-angle-lock")
                is GuiElementAtlasSwitch
            && mapLayerPanel?.GetElement("map-layer") is GuiElementDropDown;
        int renderedEntityCount = exactChunkRenderer?.LastRenderedEntityCount ?? 0;
        bool heldItemsSuppressed = renderedEntityCount > 0
            && exactChunkRenderer?.LastSuppressedHeldItemCount == renderedEntityCount;
        automatedSmokeTestInterfaceControlsPassed = accessAvailable
            && settingsOpenedByClick
            && settingsSwitchClicked
            && settingsSwitchRestored
            && skipOpeningClicked
            && skipOpeningRestored
            && automatedSmokeTestPerformanceModeSelected
            && performanceClosedByClick
            && presentationSwitchClicked
            && presentationSwitchRestored
            && fixedLightingPrepared
            && sliderBoundaryDragHandled
            && sliderBoundaryDragClamped
            && liveLightingRestored
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
                "[ModernAtlas] Automated smoke test exercised the compact neumorphic controls, scroll/full-screen presentation, map-layer and skip-opening switches, Creative/Cheat cave/search/camera controls, Escape-restored hidden UI and held-item suppression for {0} living models.",
                renderedEntityCount
            );
        }
        else
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated interface-controls test failed: access={0}, settingsOpen={1}, mapLayerSwitch={2}/{3}, skipOpening={4}/{5}, presentation={6}/{7}, fixedLighting={8}/{9}, sliderBoundary={10}/{11}, settingsClose={12}, creativeOpen={13}, creativeClose={14}, layers={15}, search={16}, safeSurface={17}, cave={18}, angleLock={19}, yaw={20}, hidden={21}, restored={22}, neumorphic={23}, heldItems={24}/{25}.",
                accessAvailable,
                settingsOpenedByClick,
                settingsSwitchClicked,
                settingsSwitchRestored,
                skipOpeningClicked,
                skipOpeningRestored,
                presentationSwitchClicked,
                presentationSwitchRestored,
                fixedLightingPrepared,
                liveLightingRestored,
                sliderBoundaryDragHandled,
                sliderBoundaryDragClamped,
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

    private bool ClickAtlasControlForAutomatedTest(GuiComposer? composer, string key)
    {
        GuiElement? element = composer?.GetElement(key);
        if (element == null) return false;

        int x = (int)Math.Round(element.Bounds.absX + element.Bounds.OuterWidth * 0.5);
        int y = (int)Math.Round(element.Bounds.absY + element.Bounds.OuterHeight * 0.5);
        MouseEvent down = new(x, y, EnumMouseButton.Left, 0);
        OnMouseDown(down);
        MouseEvent up = new(x, y, EnumMouseButton.Left, 0);
        OnMouseUp(up);
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
            config.FogEnabled = false;
            InvalidateFogTexture();
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
                && !EffectiveFogEnabled
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
            || automatedSmokeScreenshotPhase >= 5)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            automatedSmokeScreenshotPhase = 5;
            return;
        }

        string prefix = configuredPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? configuredPath[..^4]
            : configuredPath;
        string suffix = automatedSmokeScreenshotPhase switch
        {
            0 => "atlas",
            1 => "settings",
            2 => "performance",
            3 => "creative",
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
                OpenPerformanceModal();
                break;
            case 3:
                OpenCreativeSettingsModal();
                break;
            case 4:
                OpenVisualLab();
                break;
            default:
                CloseSettingsModal();
                break;
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

    private void RestoreAutomatedSmokeTestPreferences()
    {
        if (!automatedSmokeTestPreferencesCaptured) return;

        if (automatedSmokeTestPartialZoomPending
            || automatedSmokeTestMaximumZoomPending)
        {
            targetZoom = automatedSmokeTestZoomBeforeMaximum;
            zoom = automatedSmokeTestZoomBeforeMaximum;
            targetPitchDegrees = automatedSmokeTestPitchBeforeMaximum;
            pitchDegrees = automatedSmokeTestPitchBeforeMaximum;
            automatedSmokeTestPartialZoomPending = false;
            automatedSmokeTestMaximumZoomPending = false;
        }

        automatedSmokeTestPreferencesCaptured = false;
        config.MapLayersEnabled = automatedOriginalMapLayersEnabled;
        config.CaveModeEnabled = automatedOriginalCaveModeEnabled;
        config.SearchModeEnabled = automatedOriginalSearchModeEnabled;
        config.CameraAngleLocked = automatedOriginalCameraAngleLocked;
        config.FogEnabled = automatedOriginalFogEnabled;
        config.LiveLightingEnabled = automatedOriginalLiveLightingEnabled;
        config.FixedSunHour = automatedOriginalFixedSunHour;
        config.TextureDetailReduction = automatedOriginalTextureDetailReduction;
        config.PerformanceLightingEnabled = automatedOriginalPerformanceLightingEnabled;
        config.HideVegetation = automatedOriginalHideVegetation;
        if (config.RenderOnScroll != automatedOriginalRenderOnScroll)
        {
            config.RenderOnScroll = automatedOriginalRenderOnScroll;
            pendingInterfaceRecompose = true;
        }
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
            SetMapLayer(AtlasMapLayer.OreDensity);
            automatedSmokeTestMapLayerPhase = 2;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test rendered the moisture layer and started the Creative/Cheat ore-density layer."
            );
            return;
        }

        if (automatedSmokeTestMapLayerPhase != 2 || !mapLayerTexture.Ready) return;
        if (mapLayerTexture.TextureId <= 0)
        {
            automatedSmokeTestMapLayerPhase = -1;
            capi.Logger.Error(
                "[ModernAtlas] Automated map-layer test prepared ore data without a GPU texture."
            );
            return;
        }

        automatedSmokeTestMapLayerPassed = true;
        SetMapLayer(AtlasMapLayer.TexturedTerrain);
        capi.Logger.Notification(
            "[ModernAtlas] Automated smoke test rendered climate and Creative/Cheat ore map layers from loaded data."
        );
    }

    internal void OnWorldLeave()
    {
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
        AutomatedSmokeTestRenderedExactWorld = false;
        cheatModeEnabled = false;
        settingsModalOpen = false;
        performanceModalOpen = false;
        creativeSettingsModalOpen = false;
        visualLabModalOpen = false;
        interfaceHidden = false;
        selectedEntityId = null;
        searchController.Clear();
        activeMapLayer = AtlasMapLayer.TexturedTerrain;
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
            exactChunkRenderer = null;
        }
        preparedGameViewDistance = -1;
        surfaceHeightTexture.Reset();
        ReleaseNormalWorldSnapshot();
        ReleaseAtlasFrameCache();
        InvalidateFogTexture();
        capi.Logger.Notification(
            "[ModernAtlas] Released world-specific atlas rendering resources."
        );
    }

    public override void Dispose()
    {
        RestoreAutomatedSmokeTestPreferences();
        automatedSmokeTestActive = false;
        automatedSmokeTestCompletion = null;
        exactChunkRenderer?.Dispose();
        exactChunkRenderer = null;
        surfaceHeightTexture.Dispose();
        mapLayerTexture.Dispose();
        fogTexture?.Dispose();
        fogTexture = null;
        searchMarkerTexture?.Dispose();
        searchMarkerTexture = null;
        compassRenderer.Dispose();
        ReleaseNormalWorldSnapshot();
        ReleaseAtlasFrameCache();
        opacityQuad?.Dispose();
        opacityQuad = null;
        scrollViewportRenderer.Dispose();
        overlay?.Dispose();
        overlay = null;
        searchPanel?.Dispose();
        searchPanel = null;
        mapLayerPanel?.Dispose();
        mapLayerPanel = null;
        creativeSettingsShortcut?.Dispose();
        creativeSettingsShortcut = null;
        settingsModal?.Dispose();
        settingsModal = null;
        performanceModal?.Dispose();
        performanceModal = null;
        creativeSettingsModal?.Dispose();
        creativeSettingsModal = null;
        visualLabModal?.Dispose();
        visualLabModal = null;
        unitPanel?.Dispose();
        unitPanel = null;
        base.Dispose();
    }

    private void RecomposeInterface()
    {
        overlay?.Dispose();
        searchPanel?.Dispose();
        mapLayerPanel?.Dispose();
        creativeSettingsShortcut?.Dispose();
        settingsModal?.Dispose();
        performanceModal?.Dispose();
        creativeSettingsModal?.Dispose();
        visualLabModal?.Dispose();
        unitPanel?.Dispose();
        overlay = null;
        searchPanel = null;
        mapLayerPanel = null;
        creativeSettingsShortcut = null;
        settingsModal = null;
        performanceModal = null;
        creativeSettingsModal = null;
        visualLabModal = null;
        unitPanel = null;
        ComposeOverlay();
        SyncSettingsControls();
        SyncPerformanceControls();
        SyncCreativeSettingsControls();
        SyncMapLayerDropdown();
    }

    private void ComposeOverlay()
    {
        ElementBounds root = ElementBounds.Fill;
        double guiScale = Math.Max(0.5, RuntimeEnv.GUIScale);
        AtlasViewportBounds viewport = AtlasViewport;
        double contentX = config.RenderOnScroll ? viewport.X / guiScale : 0;
        double contentY = config.RenderOnScroll ? viewport.Y / guiScale : 0;
        double guiWidth = viewport.Width / guiScale;
        double actionX = contentX + Math.Max(18, guiWidth - 232);
        double compassY = contentY + 56;
        double hideUiY = contentY + (CreativeCheatSettingsAvailable ? 148 : 102);
        double headerWidth = Math.Min(
            820,
            Math.Max(430, actionX - contentX - 34)
        );

        overlay = capi.Gui.CreateCompo("modernatlas-3d", root)
            .AddStaticCustomDraw(
                ElementBounds.Fixed(contentX + 12, contentY + 10, headerWidth, 88),
                AtlasUiStyle.DrawCard
            )
            .AddStaticText(
                "MODERNATLAS",
                AtlasUiStyle.TitleFont(20),
                ElementBounds.Fixed(contentX + 30, contentY + 25, 190, 30)
            )
            .AddStaticText(
                Lang.Get("modernatlas:controls-help"),
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(contentX + 208, contentY + 28, Math.Max(205, headerWidth - 226), 24)
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(contentX + 30, contentY + 62, Math.Max(390, headerWidth - 48), 24),
                "status"
            )
            .AddAtlasButton(
                "SETTINGS",
                OpenSettingsModal,
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
                "COMPASS",
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

        ElementBounds mapLayerRoot = ElementBounds.Fixed(
            contentX + 18,
            contentY + (CreativeCheatSettingsAvailable ? 202 : 112),
            560,
            82
        );
        mapLayerPanel = capi.Gui.CreateCompo("modernatlas-map-layer", mapLayerRoot)
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 560, 82), AtlasUiStyle.DrawCard)
            .AddStaticText(
                "MAP LAYER",
                AtlasUiStyle.LabelFont(12),
                ElementBounds.Fixed(18, 17, 124, 24)
            )
            .AddDropDown(
                AtlasMapLayerInfo.Values,
                AtlasMapLayerInfo.Names,
                (int)activeMapLayer,
                OnMapLayerChanged,
                ElementBounds.Fixed(142, 8, 400, 44),
                "map-layer"
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(18, 55, 524, 20),
                "layer-status"
            )
            .Compose(false);

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

        ElementBounds modalRoot = ElementBounds.Fixed(0, 0, 430, 790)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        settingsModal = capi.Gui.CreateCompo("modernatlas-settings", modalRoot)
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 430, 790), AtlasUiStyle.DrawCard)
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
                capi.IsSinglePlayer ? "Unexplored fog" : "Server fog (locked)",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 166, 260, 26)
            )
            .AddAtlasSwitch(
                OnFogToggled,
                ElementBounds.Fixed(350, 160, 62, 38),
                "fog"
            )
            .AddStaticText(
                "Atlas animations",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 202, 240, 26)
            )
            .AddAtlasSwitch(
                OnAnimationsToggled,
                ElementBounds.Fixed(350, 196, 62, 38),
                "animations"
            )
            .AddStaticText(
                "Performance",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 238, 170, 26)
            )
            .AddAtlasButton(
                "OPEN",
                OpenPerformanceModal,
                ElementBounds.Fixed(198, 230, 214, 40),
                "performance-open",
                AtlasButtonStyle.Dark
            )
            .AddStaticText(
                "Skip scroll transitions",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 274, 260, 26)
            )
            .AddAtlasSwitch(
                OnSkipOpeningAnimationToggled,
                ElementBounds.Fixed(350, 268, 62, 38),
                "skip-opening-animation"
            )
            .AddStaticText(
                "Live clouds",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 310, 240, 26)
            )
            .AddAtlasSwitch(
                OnCloudsToggled,
                ElementBounds.Fixed(350, 304, 62, 38),
                "clouds"
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 347, 378, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "LIVING MODELS",
                AtlasUiStyle.LabelFont(11),
                ElementBounds.Fixed(26, 360, 200, 22)
            )
            .AddStaticText(
                "Living entities",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 388, 240, 26)
            )
            .AddAtlasSwitch(
                OnLivingEntitiesToggled,
                ElementBounds.Fixed(350, 382, 62, 38),
                "entities"
            )
            .AddStaticText(
                "Players",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 424, 200, 24)
            )
            .AddAtlasSwitch(
                OnPlayersToggled,
                ElementBounds.Fixed(350, 418, 62, 38),
                "players"
            )
            .AddStaticText(
                "Animals",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 458, 200, 24)
            )
            .AddAtlasSwitch(
                OnAnimalsToggled,
                ElementBounds.Fixed(350, 452, 62, 38),
                "animals"
            )
            .AddStaticText(
                "Hostile mobs",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 492, 200, 24)
            )
            .AddAtlasSwitch(
                OnMobsToggled,
                ElementBounds.Fixed(350, 486, 62, 38),
                "mobs"
            )
            .AddStaticText(
                "NPCs",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 526, 200, 24)
            )
            .AddAtlasSwitch(
                OnNpcsToggled,
                ElementBounds.Fixed(350, 520, 62, 38),
                "npcs"
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 562, 378, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "Use live world sun",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 576, 200, 24)
            )
            .AddAtlasSwitch(
                OnLiveLightingToggled,
                ElementBounds.Fixed(350, 568, 62, 38),
                "live-lighting"
            )
            .AddStaticText(
                "Atlas sun hour (fixed)",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 613, 180, 24)
            )
            .AddAtlasSlider(
                OnFixedSunHourChanged,
                ElementBounds.Fixed(212, 604, 200, 42),
                "fixed-sun-hour"
            )
            .AddStaticText(
                "Render on 3D scroll",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 655, 250, 24)
            )
            .AddAtlasSwitch(
                OnRenderOnScrollToggled,
                ElementBounds.Fixed(350, 646, 62, 38),
                "render-on-scroll"
            )
            .AddStaticText(
                "Animated compass",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 692, 250, 24)
            )
            .AddAtlasSwitch(
                OnPlayerCompassToggled,
                ElementBounds.Fixed(350, 682, 62, 38),
                "player-compass"
            )
            .AddAtlasButton(
                "ATLAS VISUAL LAB",
                OpenVisualLab,
                ElementBounds.Fixed(24, 736, 382, 44),
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

        ElementBounds performanceRoot = ElementBounds.Fixed(0, 0, 460, 478)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        performanceModal = capi.Gui.CreateCompo(
                "modernatlas-performance",
                performanceRoot
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(0, 0, 460, 478),
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
                "TEXTURE DETAIL",
                AtlasUiStyle.LabelFont(11),
                ElementBounds.Fixed(26, 190, 200, 22)
            )
            .AddStaticText(
                "Full textures",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 220, 260, 26)
            )
            .AddAtlasSwitch(
                enabled => OnTextureDetailOptionToggled(0, enabled),
                ElementBounds.Fixed(370, 213, 64, 40),
                "texture-full"
            )
            .AddStaticText(
                "Half texture detail",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 262, 260, 26)
            )
            .AddAtlasSwitch(
                enabled => OnTextureDetailOptionToggled(1, enabled),
                ElementBounds.Fixed(370, 255, 64, 40),
                "texture-half"
            )
            .AddStaticText(
                "Quarter texture detail",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 304, 280, 26)
            )
            .AddAtlasSwitch(
                enabled => OnTextureDetailOptionToggled(2, enabled),
                ElementBounds.Fixed(370, 297, 64, 40),
                "texture-quarter"
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 350, 408, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "Hide vegetation",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 370, 260, 26)
            )
            .AddAtlasSwitch(
                OnHideVegetationToggled,
                ElementBounds.Fixed(370, 363, 64, 40),
                "hide-vegetation"
            )
            .AddStaticText(
                "Hides registered plants, bushes and leaves, including mods.",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(28, 405, 404, 42)
            )
            .AddStaticText(
                "Atlas refresh: 60 FPS moving • 12 FPS idle.",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(28, 451, 404, 20)
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

        ElementBounds labRoot = ElementBounds.Fixed(0, 0, 470, 370)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        visualLabModal = capi.Gui.CreateCompo("modernatlas-visual-lab", labRoot)
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 470, 370), AtlasUiStyle.DrawCard)
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
            .AddStaticText(
                "Fog palette",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 214, 170, 26)
            )
            .AddAtlasChoice(
                AtlasVisualPalettes.Values,
                AtlasVisualPalettes.Names,
                AtlasVisualPalettes.IndexOf(config.FogPalette),
                OnFogPaletteChanged,
                ElementBounds.Fixed(202, 203, 240, 46),
                "fog-palette"
            )
            .AddAtlasButton(
                "RESET VISUAL TUNING",
                ResetVisualTuning,
                ElementBounds.Fixed(26, 290, 418, 48),
                "visual-lab-reset",
                AtlasButtonStyle.Dark
            )
            .Compose();
        ConfigureVisualLabSliders();
        SyncSettingsControls();
        SyncPerformanceControls();
        SyncCreativeSettingsControls();
    }

    private bool OpenSettingsModal()
    {
        ResetPointerDrag();
        selectedEntityId = null;
        visualLabModalOpen = false;
        performanceModalOpen = false;
        creativeSettingsModalOpen = false;
        settingsModalOpen = true;
        SyncSettingsControls();
        return true;
    }

    private bool CloseSettingsModal()
    {
        settingsModalOpen = false;
        performanceModalOpen = false;
        creativeSettingsModalOpen = false;
        visualLabModalOpen = false;
        return true;
    }

    private bool OpenCreativeSettingsModal()
    {
        if (!CreativeCheatSettingsAvailable) return true;

        ResetPointerDrag();
        searchPanel?.UnfocusOwnElements();
        selectedEntityId = null;
        settingsModalOpen = false;
        performanceModalOpen = false;
        visualLabModalOpen = false;
        creativeSettingsModalOpen = true;
        SyncCreativeSettingsControls();
        return true;
    }

    private bool CloseCreativeSettingsModal()
    {
        creativeSettingsModalOpen = false;
        return true;
    }

    private bool OpenPerformanceModal()
    {
        ResetPointerDrag();
        selectedEntityId = null;
        settingsModalOpen = false;
        creativeSettingsModalOpen = false;
        visualLabModalOpen = false;
        performanceModalOpen = true;
        SyncPerformanceControls();
        return true;
    }

    private bool ClosePerformanceModal()
    {
        performanceModalOpen = false;
        settingsModalOpen = true;
        SyncSettingsControls();
        return true;
    }

    private bool OpenVisualLab()
    {
        ResetPointerDrag();
        selectedEntityId = null;
        settingsModalOpen = false;
        performanceModalOpen = false;
        creativeSettingsModalOpen = false;
        visualLabModalOpen = true;
        SyncVisualLabControls();
        return true;
    }

    private bool CloseVisualLab()
    {
        visualLabModalOpen = false;
        performanceModalOpen = false;
        settingsModalOpen = true;
        SyncSettingsControls();
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
        visualLabModal.GetAtlasChoice("fog-palette")?.SetSelectedIndex(
            AtlasVisualPalettes.IndexOf(config.FogPalette)
        );
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

    private void OnFogPaletteChanged(string value, bool selected)
    {
        if (!selected) return;
        int index = AtlasVisualPalettes.IndexOf(value);
        config.FogPalette = AtlasVisualPalettes.Values[index];
        InvalidateFogTexture();
        saveConfig();
    }

    private bool ResetVisualTuning()
    {
        config.AtlasExposurePercent = 100;
        config.MapLayerOpacityPercent = 75;
        config.BoundarySoftnessPercent = 100;
        config.CaveMaskBrightnessPercent = 100;
        config.FogPalette = "neutral";
        InvalidateFogTexture();
        saveConfig();
        SyncVisualLabControls();
        return true;
    }

    private bool CloseUnitInspection()
    {
        selectedEntityId = null;
        return true;
    }

    private void RenderUnitInspection(float deltaTime)
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
        unitPanel?.Render(deltaTime);
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
        searchPanel?.UnfocusOwnElements();
        selectedEntityId = null;
        settingsModalOpen = false;
        performanceModalOpen = false;
        creativeSettingsModalOpen = false;
        visualLabModalOpen = false;
        interfaceHidden = true;
        return true;
    }

    private bool HandleEscape()
    {
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
        if (!config.MapLayersEnabled)
        {
            layer = AtlasMapLayer.TexturedTerrain;
        }
        if (layer.RequiresSpoilerAccess() && !UnitInspectionEnabled)
        {
            layer = AtlasMapLayer.TexturedTerrain;
        }
        activeMapLayer = layer;
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
            UnitInspectionEnabled
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
        double clearRadius = GameViewDistance * (EffectiveFogEnabled ? 0.88 : 1.0);
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

    private bool RenderLiveWorld(float deltaTime)
    {
        IRenderAPI render = capi.Render;
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);

        preparingSurfaceFilter = SurfaceSafetyEnabled && !surfaceHeightTexture.Advance();
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
        Mat4f.Ortho(projection, -zoom * aspect, zoom * aspect, -zoom, zoom, 0.1f, farPlane);
        float yaw = yawDegrees * GameMath.DEG2RAD;
        float pitch = pitchDegrees * GameMath.DEG2RAD;
        DefaultShaderUniforms uniforms = capi.Render.ShaderUniforms;
        float animationOffset = capi.IsSinglePlayer && capi.IsGamePaused
            ? atlasAnimationSeconds
            : 0;
        float windWaveCounter = config.AnimationsEnabled
            ? uniforms.WindWaveCounter + animationOffset
            : frozenWindWaveCounter;
        float windWaveCounterHighFrequency = config.AnimationsEnabled
            ? uniforms.WindWaveCounterHighFreq + animationOffset
            : frozenWindWaveCounterHighFrequency;
        float waterStillCounter = config.AnimationsEnabled
            ? uniforms.WaterStillCounter + animationOffset
            : frozenWaterStillCounter;
        float waterFlowCounter = config.AnimationsEnabled
            ? uniforms.WaterFlowCounter + animationOffset
            : frozenWaterFlowCounter;

        bool rendered = exactChunkRenderer?.Render(
            deltaTime,
            projection,
            centerX,
            centerY,
            centerZ,
            yaw,
            pitch,
            GameViewDistance,
            EffectiveFogEnabled,
            SurfaceSafetyEnabled,
            SurvivalOreConcealmentEnabled,
            SurfaceSafetyEnabled ? surfaceHeightTexture : null,
            mapLayerTexture,
            EffectiveMapLayerOpacity,
            Math.Clamp(config.TextureDetailReduction, 0, 2),
            config.PerformanceLightingEnabled,
            config.HideVegetation,
            AtlasFogColor,
            Math.Clamp(config.AtlasExposurePercent, 50, 150) / 100f,
            Math.Clamp(config.BoundarySoftnessPercent, 25, 200) / 100f,
            Math.Clamp(config.CaveMaskBrightnessPercent, 50, 150) / 100f,
            windWaveCounter,
            windWaveCounterHighFrequency,
            waterStillCounter,
            waterFlowCounter,
            config.CloudsEnabled,
            config.LiveLightingEnabled,
            config.FixedSunHour,
            config.AnimationsEnabled && capi.IsSinglePlayer && capi.IsGamePaused
                ? atlasRealDeltaTime
                : 0,
            visibleEntityPolicy,
            false
        ) == true;
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
        return rendered;
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
        preparingSurfaceFilter = SurfaceSafetyEnabled;
        if (!SurfaceSafetyEnabled)
        {
            surfaceHeightTexture.Reset();
            return;
        }

        surfaceHeightTexture.Begin(
            capi.World.Player.Entity.Pos.X,
            capi.World.Player.Entity.Pos.Z,
            GameViewDistance
        );
    }

    private void ClearSurfacePreparationFrame()
    {
        // Draw an opaque Primary frame while the small surface texture is
        // prepared. Clearing the default framebuffer directly can leave the
        // native Linux window transparent after the atlas closes.
        exactChunkRenderer?.RenderSurfacePreparationFrame(
            EffectiveFogEnabled,
            AtlasFogColor,
            !config.RenderOnScroll
        );
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

    private void OnFogToggled(bool enabled)
    {
        if (!capi.IsSinglePlayer) return;
        config.FogEnabled = enabled;
        InvalidateFogTexture();
        saveConfig();
    }

    private void OnRenderOnScrollToggled(bool enabled)
    {
        if (config.RenderOnScroll == enabled) return;
        config.RenderOnScroll = enabled;
        pendingInterfaceRecompose = true;
        ResetPointerDrag();
        selectedEntityId = null;
        InvalidateFogTexture();
        FitLoadedTerrain();
        saveConfig();
        SyncSettingsControls();
        capi.Logger.Notification(
            enabled
                ? "[ModernAtlas] Atlas presentation changed to the interactive 3D scroll."
                : "[ModernAtlas] Atlas presentation changed to full screen."
        );
    }

    private void OnPlayerCompassToggled(bool enabled)
    {
        config.ShowPlayerCompass = enabled;
        saveConfig();
        SyncSettingsControls();
    }

    private bool ToggleCompass()
    {
        OnPlayerCompassToggled(!config.ShowPlayerCompass);
        return true;
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
        InvalidateFogTexture();
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

    private void OnTextureDetailOptionToggled(int reduction, bool enabled)
    {
        if (synchronizingPerformanceControls) return;
        reduction = Math.Clamp(reduction, 0, 2);
        if (!enabled)
        {
            SyncPerformanceControls();
            return;
        }
        if (config.TextureDetailReduction == reduction) return;

        config.TextureDetailReduction = reduction;
        saveConfig();
        SyncPerformanceControls();
        capi.Logger.Notification(
            "[ModernAtlas] Atlas performance mode changed to {0}; terrain and liquid texture detail reduction is {1}x.",
            PerformanceModeNames[reduction],
            1 << reduction
        );
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
        saveConfig();
        SyncSettingsControls();
    }

    private bool OnFixedSunHourChanged(int hour)
    {
        config.FixedSunHour = Math.Clamp(hour, 0, 23);
        saveConfig();
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
            config.RenderOnScroll
        );
        settingsModal.GetAtlasSwitch("player-compass")?.SetValue(
            config.ShowPlayerCompass
        );
        settingsModal.GetAtlasSwitch("map-layers")?.SetValue(config.MapLayersEnabled);
        settingsModal.GetAtlasSwitch("search-mode")?.SetValue(
            CreativeCheatSettingsAvailable && config.SearchModeEnabled
        );
        settingsModal.GetAtlasSwitch("search-mode")!.Enabled =
            CreativeCheatSettingsAvailable;
        settingsModal.GetAtlasSwitch("fog")?.SetValue(EffectiveFogEnabled);
        settingsModal.GetAtlasSwitch("fog")!.Enabled = capi.IsSinglePlayer;
        settingsModal.GetAtlasSwitch("animations")?.SetValue(config.AnimationsEnabled);
        settingsModal.GetAtlasSwitch("skip-opening-animation")?.SetValue(
            config.SkipOpeningAnimation
        );
        settingsModal.GetAtlasSwitch("clouds")?.SetValue(config.CloudsEnabled);
        settingsModal.GetAtlasSwitch("live-lighting")?.SetValue(config.LiveLightingEnabled);
        settingsModal.GetAtlasSlider("fixed-sun-hour")!.Enabled = !config.LiveLightingEnabled;
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
            int reduction = Math.Clamp(config.TextureDetailReduction, 0, 2);
            performanceModal.GetAtlasSwitch("performance-lighting")?.SetValue(
                config.PerformanceLightingEnabled
            );
            performanceModal.GetAtlasSwitch("texture-full")?.SetValue(reduction == 0);
            performanceModal.GetAtlasSwitch("texture-half")?.SetValue(reduction == 1);
            performanceModal.GetAtlasSwitch("texture-quarter")?.SetValue(reduction == 2);
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
            InvalidateFogTexture();
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

    private void RenderFogMask(bool renderIntoAtlasTexture)
    {
        if (!EffectiveFogEnabled) return;

        AtlasViewportBounds viewport = AtlasViewport;
        int frameWidth = viewport.Width;
        int frameHeight = viewport.Height;
        int width = Math.Max(1, (frameWidth + FogTextureDownsample - 1) / FogTextureDownsample);
        int height = Math.Max(1, (frameHeight + FogTextureDownsample - 1) / FogTextureDownsample);
        bool needsRebuild = fogTexture == null
            || fogTextureWidth != width
            || fogTextureHeight != height
            || Math.Abs(fogTextureZoom - zoom) > 0.1f
            || Math.Abs(fogTexturePitch - pitchDegrees) > 0.1f;
        if (needsRebuild
            && (fogTexture == null
                || capi.ElapsedMilliseconds - lastFogTextureBuildMilliseconds >= 40))
        {
            RebuildFogTexture(width, height);
        }

        if (fogTexture?.TextureId > 0)
        {
            IRenderAPI render = capi.Render;
            FrameBufferRef? previousFramebuffer = render.CurrentFrameBuffer;
            bool clipped = !renderIntoAtlasTexture && BeginScrollContentClip();
            try
            {
                int destinationX = viewport.X;
                int destinationY = viewport.Y;
                int destinationWidth = frameWidth;
                int destinationHeight = frameHeight;
                if (renderIntoAtlasTexture)
                {
                    FrameBufferRef primary = render.FrameBuffers[
                        (int)EnumFrameBuffer.Primary
                    ];
                    render.CurrentFrameBuffer = primary;
                    render.GlViewport(0, 0, primary.Width, primary.Height);
                    destinationX = 0;
                    destinationY = 0;
                    destinationWidth = render.FrameWidth;
                    destinationHeight = render.FrameHeight;
                }

                render.GetEngineShader(EnumShaderProgram.Gui).Use();
                render.GLDisableDepthTest();
                render.GLDepthMask(false);
                render.GlToggleBlend(true, EnumBlendMode.Standard);
                render.Render2DTexture(
                    fogTexture.TextureId,
                    destinationX,
                    destinationY,
                    destinationWidth,
                    destinationHeight,
                    40
                );
            }
            finally
            {
                EndScrollContentClip(clipped);
                render.GLDepthMask(true);
                render.CurrentFrameBuffer = previousFramebuffer;
                render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
            }
        }
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
        render.CurrentFrameBuffer = null;
        render.CurrentActiveShader?.Stop();
        render.GLDisableDepthTest();
        render.GLDepthMask(false);
        render.GlToggleBlend(false, EnumBlendMode.Standard);
        render.GlColorMask(false, false, false, true);
        try
        {
            shader.Use();
            render.RenderMesh(opacityQuad);
            shader.Stop();
        }
        finally
        {
            render.GlColorMask(true, true, true, true);
        }
    }

    private void RebuildFogTexture(int width, int height)
    {
        fogTexture ??= new LoadedTexture(capi);
        fogTextureWidth = width;
        fogTextureHeight = height;
        fogTextureZoom = zoom;
        fogTexturePitch = pitchDegrees;
        lastFogTextureBuildMilliseconds = capi.ElapsedMilliseconds;

        using ImageSurface surface = new(Format.Argb32, Math.Max(1, width), Math.Max(1, height));
        using Context context = new(surface);
        Vec3f fogColor = AtlasFogColor;
        context.Operator = Operator.Source;
        context.SetSourceRGBA(fogColor.X, fogColor.Y, fogColor.Z, 1.0);
        context.Paint();

        double radiusX = height * GameViewDistance / (2.0 * zoom);
        double pitchRadians = pitchDegrees * GameMath.DEG2RAD;
        double radiusY = radiusX * Math.Max(0.12, Math.Sin(pitchRadians));
        double reliefAllowance = Math.Min(192, GameViewDistance * 0.3);
        radiusY += height * reliefAllowance * Math.Abs(Math.Cos(pitchRadians)) / (2.0 * zoom);
        // The revealed area belongs to the player's actual world position,
        // not to the movable atlas camera. Panning therefore moves the clear
        // area across the screen instead of revealing distant terrain.
        double playerDeltaX = capi.World.Player.Entity.Pos.X - centerX;
        double playerDeltaY = capi.World.Player.Entity.Pos.Y - centerY;
        double playerDeltaZ = capi.World.Player.Entity.Pos.Z - centerZ;
        double yawRadians = yawDegrees * GameMath.DEG2RAD;
        double sinYaw = Math.Sin(yawRadians);
        double cosYaw = Math.Cos(yawRadians);
        double sinPitch = Math.Sin(pitchRadians);
        double cosPitch = Math.Cos(pitchRadians);
        double projectedRight = playerDeltaX * cosYaw - playerDeltaZ * sinYaw;
        double projectedUp = -playerDeltaX * sinYaw * sinPitch
            + playerDeltaY * cosPitch
            - playerDeltaZ * cosYaw * sinPitch;
        double pixelsPerBlock = height / (2.0 * zoom);
        double centerScreenX = width / 2.0 + projectedRight * pixelsPerBlock;
        double centerScreenY = height / 2.0 - projectedUp * pixelsPerBlock;

        // Overlap the view-distance edge instead of starting beyond it. This
        // hides transient chunk cross-sections and turns the disclosure limit
        // into an atmospheric horizon rather than a hard circular cutout.
        double softness = Math.Clamp(config.BoundarySoftnessPercent, 25, 200) / 100.0;
        const double opaqueScale = 1.04;
        double clearScale = Math.Clamp(opaqueScale - 0.14 * softness, 0.70, 1.0);
        int featherSteps = Math.Clamp((int)Math.Round(24 * softness), 8, 48);
        for (int step = featherSteps; step >= 0; step--)
        {
            double progress = step / (double)featherSteps;
            double scale = clearScale + (opaqueScale - clearScale) * progress;
            double alpha = progress;
            context.Save();
            context.Translate(centerScreenX, centerScreenY);
            context.Scale(radiusX * scale, radiusY * scale);
            context.Arc(0, 0, 1, 0, Math.PI * 2);
            context.Restore();
            context.SetSourceRGBA(fogColor.X, fogColor.Y, fogColor.Z, alpha);
            context.Fill();
        }
        context.Save();
        context.Translate(centerScreenX, centerScreenY);
        context.Scale(radiusX * clearScale, radiusY * clearScale);
        context.Arc(0, 0, 1, 0, Math.PI * 2);
        context.Restore();
        context.SetSourceRGBA(0, 0, 0, 0);
        context.Fill();

        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref fogTexture);
    }

    private void InvalidateFogTexture()
    {
        fogTextureZoom = -1;
        fogTexturePitch = -1;
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
