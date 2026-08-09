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
    private const float StandardMinimumPitchDegrees = 20;
    private const float UnlockedMinimumPitchDegrees = 0;
    private const int DefaultViewDistance = 500;
    private const int MaximumGameViewDistance = 1536;
    private const int FogTextureDownsample = 4;
    private const string SmokeScreenshotEnvironmentVariable =
        "MODERNATLAS_SMOKE_SCREENSHOT";

    private ExactChunkRendererAdapter? exactChunkRenderer;
    private readonly float[] projection = Mat4f.Create();
    private readonly ModernAtlasConfig config;
    private readonly ModernAtlasServerPolicy serverPolicy;
    private readonly ModernAtlasServerPolicy visibleEntityPolicy = new();
    private readonly Action saveConfig;
    private readonly Func<IShaderProgram?> stableLiquidShaderProvider;
    private readonly Func<IShaderProgram?> atlasCloudShaderProvider;
    private readonly Func<IShaderProgram?> atlasOpacityShaderProvider;
    private readonly AtlasSurfaceHeightTexture surfaceHeightTexture;
    private readonly AtlasMapLayerTexture mapLayerTexture;
    private readonly AtlasSearchController searchController;

    private GuiComposer? overlay;
    private GuiComposer? searchPanel;
    private GuiComposer? mapLayerPanel;
    private GuiComposer? creativeSettingsShortcut;
    private GuiComposer? settingsModal;
    private GuiComposer? creativeSettingsModal;
    private GuiComposer? visualLabModal;
    private GuiComposer? unitPanel;
    private bool settingsModalOpen;
    private bool creativeSettingsModalOpen;
    private bool visualLabModalOpen;
    private bool interfaceHidden;
    private long lastInterfaceRestoreMilliseconds = -10000;
    private LoadedTexture? fogTexture;
    private LoadedTexture? searchMarkerTexture;
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
    private bool pausedGameForAtlas;
    private float atlasAnimationSeconds;
    private float frozenWindWaveCounter;
    private float frozenWindWaveCounterHighFrequency;
    private float frozenWaterStillCounter;
    private float frozenWaterFlowCounter;
    private long lastAtlasFrameMilliseconds;
    private float atlasRealDeltaTime;
    private bool loggedEntityModels;
    private bool cheatModeEnabled;
    private bool preparingSurfaceFilter;
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
    private bool automatedSmokeTestSafeSurfaceFrameRendered;
    private bool automatedSmokeTestSafeSurfaceScreenshotHandled;
    private int automatedSmokeTestSearchPhase;
    private bool automatedSmokeTestSearchPassed;
    private int automatedSmokeTestMapLayerPhase;
    private bool automatedSmokeTestMapLayerPassed;
    private bool automatedSmokeTestPreferencesCaptured;
    private int automatedSmokeScreenshotPhase;
    private bool automatedOriginalMapLayersEnabled;
    private bool automatedOriginalCaveModeEnabled;
    private bool automatedOriginalSearchModeEnabled;
    private bool automatedOriginalCameraAngleLocked;
    private AtlasMapLayer activeMapLayer = AtlasMapLayer.TexturedTerrain;
    private bool synchronizingMapLayerDropdown;

    internal bool AutomatedSmokeTestRenderedExactWorld { get; private set; }

    private int GameViewDistance
    {
        get
        {
            int viewDistance = capi.Settings.Int["viewDistance"];
            return viewDistance > 0
                ? Math.Clamp(viewDistance, GlobalConstants.ChunkSize, MaximumGameViewDistance)
                : DefaultViewDistance;
        }
    }
    private bool EffectiveFogEnabled => capi.IsSinglePlayer
        ? config.FogEnabled
        : serverPolicy.FogEnabled;
    private bool CreativeCheatSettingsAvailable
    {
        get
        {
            if (!capi.IsSinglePlayer) return false;
            if (cheatModeEnabled) return true;
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
        && !creativeSettingsModalOpen
        && !visualLabModalOpen
        && searchPanel?.GetTextInput("search-input")?.HasFocus == true;
    private float EffectiveMapLayerOpacity
    {
        get
        {
            float configured = Math.Clamp(config.MapLayerOpacityPercent, 0, 100) / 100f;
            // A perceptual response makes middle slider values visibly useful
            // while preserving exact zero and full-strength endpoints.
            return 1f - MathF.Pow(1f - configured, 1.35f);
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
        Func<IShaderProgram?> stableLiquidShaderProvider,
        Func<IShaderProgram?> atlasCloudShaderProvider,
        Func<IShaderProgram?> atlasOpacityShaderProvider
    ) : base(capi)
    {
        this.config = config;
        this.serverPolicy = serverPolicy;
        this.saveConfig = saveConfig;
        this.stableLiquidShaderProvider = stableLiquidShaderProvider;
        this.atlasCloudShaderProvider = atlasCloudShaderProvider;
        this.atlasOpacityShaderProvider = atlasOpacityShaderProvider;
        surfaceHeightTexture = new AtlasSurfaceHeightTexture(capi);
        mapLayerTexture = new AtlasMapLayerTexture(capi);
        searchController = new AtlasSearchController(capi);
        RefreshVisibleEntityPolicy();
        ComposeOverlay();
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        // Opening with G is a map action, not an implicit request to type.
        // Search receives focus only after the player clicks its text box.
        overlay?.UnfocusOwnElements();
        searchPanel?.UnfocusOwnElements();
        settingsModalOpen = false;
        creativeSettingsModalOpen = false;
        visualLabModalOpen = false;
        interfaceHidden = false;
        lastInterfaceRestoreMilliseconds = -10000;
        selectedEntityId = null;
        ClearSearch();
        ResetPointerDrag();
        PauseSingleplayerForAtlas();
        atlasAnimationSeconds = 0;
        loggedEntityModels = false;
        CaptureAnimationFrame();
        lastAtlasFrameMilliseconds = capi.ElapsedMilliseconds;
        exactChunkRenderer ??= ExactChunkRendererAdapter.TryCreate(
            capi,
            stableLiquidShaderProvider,
            atlasCloudShaderProvider
        );
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
        AdvancePausedAnimation();
        AdvanceCamera(atlasRealDeltaTime);
        mapLayerTexture.Advance();
        if (!loggedFirstRender)
        {
            loggedFirstRender = true;
            capi.Logger.Notification("[ModernAtlas] First 3D atlas GUI frame rendered.");
        }
        bool rendered = RenderLiveWorld(deltaTime);
        if (SearchModeActive)
        {
            AdvanceSearch();
        }
        if (rendered && automatedSmokeTestActive)
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
            if (safeSurfaceFrameWasAlreadyRendered)
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
        RenderFogMask();
        ForceOpaqueWindowAlpha();
        capi.Render.GetEngineShader(EnumShaderProgram.Gui).Use();
        capi.Render.GLDepthMask(false);
        capi.Render.GLDisableDepthTest();
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
            if (creativeSettingsModalOpen)
            {
                creativeSettingsModal?.Render(deltaTime);
            }
            if (visualLabModalOpen)
            {
                visualLabModal?.Render(deltaTime);
            }
        }
        CaptureAutomatedSmokeScreenshot();
        AdvanceAutomatedSmokeTest();
    }

    public override void OnMouseDown(MouseEvent args)
    {
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
                double worldPerPixel = targetZoom * 2.0 / Math.Max(1, capi.Render.FrameHeight);
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

        float wheel = args.deltaPrecise != 0 ? args.deltaPrecise : args.delta;
        targetZoom = Math.Clamp(targetZoom * MathF.Pow(0.84f, wheel), 8, 30000);
        args.SetHandled();
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
        if (visualLabModalOpen || creativeSettingsModalOpen || settingsModalOpen)
        {
            args.Handled = true;
            return;
        }

        if (args.KeyCode == (int)GlKeys.G)
        {
            TryClose();
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
        creativeSettingsModalOpen = false;
        visualLabModalOpen = false;
        interfaceHidden = false;
        selectedEntityId = null;
        searchController.Clear();
        mapLayerTexture.Reset();
        preparingSurfaceFilter = false;
        surfaceHeightTexture.Reset();
        ResetPointerDrag();
        ResumeSingleplayerAfterAtlas();
        base.OnGuiClosed();
    }

    public void OnServerPolicyChanged()
    {
        RefreshVisibleEntityPolicy();
        InvalidateFogTexture();
        SyncSettingsControls();
    }

    public void SetCheatMode(bool enabled)
    {
        cheatModeEnabled = capi.IsSinglePlayer && enabled;
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
        SyncCreativeSettingsControls();
        if (!IsOpened()) return;

        PrepareSurfaceSafetyFilter();
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
        automatedSmokeTestPreferencesCaptured = true;
        config.MapLayersEnabled = true;
        config.CaveModeEnabled = false;
        config.SearchModeEnabled = true;
        config.CameraAngleLocked = false;
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
        automatedSmokeTestSearchPhase = 0;
        automatedSmokeTestSearchPassed = false;
        automatedSmokeTestMapLayerPhase = 0;
        automatedSmokeTestMapLayerPassed = false;
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
            && automatedSmokeTestSearchPassed
            && automatedSmokeTestMapLayerPassed;
        if (passed && automatedSmokeTestElapsedSeconds < 3f) return;
        if (!passed && automatedSmokeTestElapsedSeconds < 24f) return;

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
        int fixedSunHourBeforeDrag = config.FixedSunHour;
        bool dragSliderToMaximum = fixedSunHourBeforeDrag != 23;
        bool sliderBoundaryDragHandled = settingsOpenedByClick
            && DragAtlasSliderBeyondBoundsForAutomatedTest(
                settingsModal,
                "fixed-sun-hour",
                dragSliderToMaximum
            );
        bool sliderBoundaryDragClamped = config.FixedSunHour
            == (dragSliderToMaximum ? 23 : 0);
        config.FixedSunHour = fixedSunHourBeforeDrag;
        settingsModal?.GetAtlasSlider("fixed-sun-hour")?.SetValue(fixedSunHourBeforeDrag);
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
            && sliderBoundaryDragHandled
            && sliderBoundaryDragClamped
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
                "[ModernAtlas] Automated smoke test exercised the compact neumorphic controls, map-layer switch, Creative/Cheat cave/search/camera controls, Escape-restored hidden UI and held-item suppression for {0} living models.",
                renderedEntityCount
            );
        }
        else
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated interface-controls test failed: access={0}, settingsOpen={1}, settingsSwitch={2}/{3}, sliderBoundary={4}/{5}, settingsClose={6}, creativeOpen={7}, creativeClose={8}, layers={9}, search={10}, safeSurface={11}, cave={12}, angleLock={13}, yaw={14}, hidden={15}, restored={16}, neumorphic={17}, heldItems={18}/{19}.",
                accessAvailable,
                settingsOpenedByClick,
                settingsSwitchClicked,
                settingsSwitchRestored,
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
            || automatedSmokeScreenshotPhase >= 4)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            automatedSmokeScreenshotPhase = 4;
            return;
        }

        string prefix = configuredPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? configuredPath[..^4]
            : configuredPath;
        string suffix = automatedSmokeScreenshotPhase switch
        {
            0 => "atlas",
            1 => "settings",
            2 => "creative",
            _ => "visual-lab"
        };
        string path = $"{prefix}-{suffix}.png";
        if (!TrySaveAutomatedSmokeScreenshot(path))
        {
            automatedSmokeScreenshotPhase = 4;
            return;
        }

        automatedSmokeScreenshotPhase++;
        switch (automatedSmokeScreenshotPhase)
        {
            case 1:
                OpenSettingsModal();
                break;
            case 2:
                OpenCreativeSettingsModal();
                break;
            case 3:
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
            screenshot.Save(path);
            capi.Logger.Notification(
                "[ModernAtlas] Saved automated atlas UI screenshot: {0}",
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

        automatedSmokeTestPreferencesCaptured = false;
        config.MapLayersEnabled = automatedOriginalMapLayersEnabled;
        config.CaveModeEnabled = automatedOriginalCaveModeEnabled;
        config.SearchModeEnabled = automatedOriginalSearchModeEnabled;
        config.CameraAngleLocked = automatedOriginalCameraAngleLocked;
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

        if (automatedSmokeTestSearchPhase != 2) return;
        if (searchController.BlockResults.Count > 0)
        {
            automatedSmokeTestSearchPassed = true;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test found loaded entity and block search markers."
            );
        }
        else if (searchController.BlockScanComplete)
        {
            automatedSmokeTestSearchPhase = -1;
            capi.Logger.Error(
                "[ModernAtlas] Automated atlas block search completed without finding its known loaded block: {0}.",
                searchController.DiagnosticSummary
            );
        }
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
        automatedSmokeTestSafeSurfaceFrameRendered = false;
        automatedSmokeTestSafeSurfaceScreenshotHandled = false;
        automatedSmokeTestSearchPhase = 0;
        automatedSmokeTestSearchPassed = false;
        automatedSmokeTestMapLayerPhase = 0;
        automatedSmokeTestMapLayerPassed = false;
        AutomatedSmokeTestRenderedExactWorld = false;
        cheatModeEnabled = false;
        settingsModalOpen = false;
        creativeSettingsModalOpen = false;
        visualLabModalOpen = false;
        interfaceHidden = false;
        selectedEntityId = null;
        searchController.Clear();
        activeMapLayer = AtlasMapLayer.TexturedTerrain;
        mapLayerTexture.Reset();
        preparingSurfaceFilter = false;
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
                ResumeSingleplayerAfterAtlas();
            }
        }
        else
        {
            ResumeSingleplayerAfterAtlas();
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
        surfaceHeightTexture.Reset();
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
        ResumeSingleplayerAfterAtlas();
        exactChunkRenderer?.Dispose();
        exactChunkRenderer = null;
        surfaceHeightTexture.Dispose();
        mapLayerTexture.Dispose();
        fogTexture?.Dispose();
        fogTexture = null;
        searchMarkerTexture?.Dispose();
        searchMarkerTexture = null;
        opacityQuad?.Dispose();
        opacityQuad = null;
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
        creativeSettingsModal?.Dispose();
        creativeSettingsModal = null;
        visualLabModal?.Dispose();
        visualLabModal = null;
        unitPanel?.Dispose();
        unitPanel = null;
        base.Dispose();
    }

    private void ComposeOverlay()
    {
        ElementBounds root = ElementBounds.Fill;
        double guiWidth = capi.Gui.WindowBounds.InnerWidth / Math.Max(0.5, RuntimeEnv.GUIScale);
        double actionX = Math.Max(18, guiWidth - 232);
        double headerWidth = Math.Min(820, Math.Max(430, actionX - 34));

        overlay = capi.Gui.CreateCompo("modernatlas-3d", root)
            .AddStaticCustomDraw(
                ElementBounds.Fixed(12, 10, headerWidth, 88),
                AtlasUiStyle.DrawCard
            )
            .AddStaticText(
                "MODERNATLAS",
                AtlasUiStyle.TitleFont(20),
                ElementBounds.Fixed(30, 25, 190, 30)
            )
            .AddStaticText(
                Lang.Get("modernatlas:controls-help"),
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(208, 28, Math.Max(205, headerWidth - 226), 24)
            )
            .AddDynamicText(
                "",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(30, 62, Math.Max(390, headerWidth - 48), 24),
                "status"
            )
            .AddAtlasButton(
                "SETTINGS",
                OpenSettingsModal,
                ElementBounds.Fixed(actionX, 10, 128, 44),
                "settings-button"
            )
            .AddAtlasButton(
                "EXIT",
                CloseAtlas,
                ElementBounds.Fixed(actionX + 136, 10, 78, 44),
                "exit-button"
            )
            .AddAtlasButton(
                "HIDE UI",
                HideInterface,
                ElementBounds.Fixed(actionX, 102, 214, 44),
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
                ElementBounds.Fixed(actionX, 56, 214, 44),
                "creative-settings-button"
            )
            .Compose(false);

        ElementBounds searchRoot = ElementBounds.Fixed(18, 112, 560, 82);
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

        ElementBounds mapLayerRoot = ElementBounds.Fixed(18, 202, 560, 82);
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

        ElementBounds unitRoot = ElementBounds.Fixed(0, 0, 370, 258)
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

        ElementBounds modalRoot = ElementBounds.Fixed(0, 0, 430, 590)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        settingsModal = capi.Gui.CreateCompo("modernatlas-settings", modalRoot)
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 430, 590), AtlasUiStyle.DrawCard)
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
                capi.IsSinglePlayer ? "Unexplored fog" : "Server fog (locked)",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 130, 260, 26)
            )
            .AddAtlasSwitch(
                OnFogToggled,
                ElementBounds.Fixed(350, 124, 62, 38),
                "fog"
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
                "Live clouds",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 202, 240, 26)
            )
            .AddAtlasSwitch(
                OnCloudsToggled,
                ElementBounds.Fixed(350, 196, 62, 38),
                "clouds"
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 239, 378, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "LIVING MODELS",
                AtlasUiStyle.LabelFont(11),
                ElementBounds.Fixed(26, 252, 200, 22)
            )
            .AddStaticText(
                "Living entities",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 280, 240, 26)
            )
            .AddAtlasSwitch(
                OnLivingEntitiesToggled,
                ElementBounds.Fixed(350, 274, 62, 38),
                "entities"
            )
            .AddStaticText(
                "Players",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 316, 200, 24)
            )
            .AddAtlasSwitch(
                OnPlayersToggled,
                ElementBounds.Fixed(350, 310, 62, 38),
                "players"
            )
            .AddStaticText(
                "Animals",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 350, 200, 24)
            )
            .AddAtlasSwitch(
                OnAnimalsToggled,
                ElementBounds.Fixed(350, 344, 62, 38),
                "animals"
            )
            .AddStaticText(
                "Hostile mobs",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 384, 200, 24)
            )
            .AddAtlasSwitch(
                OnMobsToggled,
                ElementBounds.Fixed(350, 378, 62, 38),
                "mobs"
            )
            .AddStaticText(
                "NPCs",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(48, 418, 200, 24)
            )
            .AddAtlasSwitch(
                OnNpcsToggled,
                ElementBounds.Fixed(350, 412, 62, 38),
                "npcs"
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 454, 378, 2),
                AtlasUiStyle.DrawSeparator
            )
            .AddStaticText(
                "Live sun and weather",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 468, 200, 24)
            )
            .AddAtlasSwitch(
                OnLiveLightingToggled,
                ElementBounds.Fixed(350, 460, 62, 38),
                "live-lighting"
            )
            .AddStaticText(
                "Fixed sun hour",
                AtlasUiStyle.DetailFont(12),
                ElementBounds.Fixed(28, 505, 130, 24)
            )
            .AddAtlasSlider(
                OnFixedSunHourChanged,
                ElementBounds.Fixed(166, 496, 246, 42),
                "fixed-sun-hour"
            )
            .AddAtlasButton(
                "ATLAS VISUAL LAB",
                OpenVisualLab,
                ElementBounds.Fixed(24, 540, 382, 44),
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
                "Singleplayer Creative or accepted Cheat Mode only.",
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
                "Search loaded map",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 156, 300, 26)
            )
            .AddAtlasSwitch(
                OnSearchModeToggled,
                ElementBounds.Fixed(358, 149, 64, 40),
                "search-mode"
            )
            .AddStaticText(
                "Lock camera angle",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 204, 300, 26)
            )
            .AddAtlasSwitch(
                OnCameraAngleLockToggled,
                ElementBounds.Fixed(358, 197, 64, 40),
                "camera-angle-lock"
            )
            .AddStaticText(
                "Rotation remains free while the current tilt is held.",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(28, 250, 382, 22)
            )
            .Compose(false);

        ElementBounds labRoot = ElementBounds.Fixed(0, 0, 470, 474)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        visualLabModal = capi.Gui.CreateCompo("modernatlas-visual-lab", labRoot)
            .AddStaticCustomDraw(ElementBounds.Fixed(0, 0, 470, 474), AtlasUiStyle.DrawCard)
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
                "Data-layer color strength",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 160, 174, 26)
            )
            .AddAtlasSlider(
                OnMapLayerOpacityChanged,
                ElementBounds.Fixed(202, 151, 240, 44),
                "layer-opacity"
            )
            .AddStaticText(
                "Boundary softness",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 212, 174, 26)
            )
            .AddAtlasSlider(
                OnBoundarySoftnessChanged,
                ElementBounds.Fixed(202, 203, 240, 44),
                "boundary-softness"
            )
            .AddStaticText(
                "Cave mask brightness",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 264, 174, 26)
            )
            .AddAtlasSlider(
                OnCaveMaskBrightnessChanged,
                ElementBounds.Fixed(202, 255, 240, 44),
                "cave-mask-brightness"
            )
            .AddStaticText(
                "Fog palette",
                AtlasUiStyle.DetailFont(13),
                ElementBounds.Fixed(28, 318, 170, 26)
            )
            .AddAtlasChoice(
                AtlasVisualPalettes.Values,
                AtlasVisualPalettes.Names,
                AtlasVisualPalettes.IndexOf(config.FogPalette),
                OnFogPaletteChanged,
                ElementBounds.Fixed(202, 307, 240, 46),
                "fog-palette"
            )
            .AddAtlasButton(
                "RESET VISUAL TUNING",
                ResetVisualTuning,
                ElementBounds.Fixed(26, 394, 418, 48),
                "visual-lab-reset",
                AtlasButtonStyle.Dark
            )
            .Compose();
        ConfigureVisualLabSliders();
        SyncSettingsControls();
        SyncCreativeSettingsControls();
    }

    private bool OpenSettingsModal()
    {
        ResetPointerDrag();
        selectedEntityId = null;
        visualLabModalOpen = false;
        creativeSettingsModalOpen = false;
        settingsModalOpen = true;
        SyncSettingsControls();
        return true;
    }

    private bool CloseSettingsModal()
    {
        settingsModalOpen = false;
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

    private bool OpenVisualLab()
    {
        ResetPointerDrag();
        selectedEntityId = null;
        settingsModalOpen = false;
        creativeSettingsModalOpen = false;
        visualLabModalOpen = true;
        SyncVisualLabControls();
        return true;
    }

    private bool CloseVisualLab()
    {
        visualLabModalOpen = false;
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
        visualLabModal.GetAtlasSlider("layer-opacity")?.SetValues(
            Math.Clamp(config.MapLayerOpacityPercent, 0, 100),
            0,
            100,
            5,
            "%"
        );
        visualLabModal.GetAtlasSlider("boundary-softness")?.SetValues(
            Math.Clamp(config.BoundarySoftnessPercent, 25, 200),
            25,
            200,
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

    private bool OnMapLayerOpacityChanged(int value)
    {
        config.MapLayerOpacityPercent = Math.Clamp(value, 0, 100);
        saveConfig();
        return true;
    }

    private bool OnBoundarySoftnessChanged(int value)
    {
        config.BoundarySoftnessPercent = Math.Clamp(value, 25, 200);
        InvalidateFogTexture();
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
            double pixelsPerBlock = capi.Render.FrameHeight / (2.0 * zoom);
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
        double pixelsPerBlock = capi.Render.FrameHeight / (2.0 * zoom);
        screenX = capi.Render.FrameWidth / 2.0 + projectedRight * pixelsPerBlock;
        screenY = capi.Render.FrameHeight / 2.0 - projectedUp * pixelsPerBlock;
        return screenX >= 0
            && screenX <= capi.Render.FrameWidth
            && screenY >= 0
            && screenY <= capi.Render.FrameHeight;
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

    private bool CloseAtlas() => TryClose();

    private bool HideInterface()
    {
        ResetPointerDrag();
        searchPanel?.UnfocusOwnElements();
        selectedEntityId = null;
        settingsModalOpen = false;
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
        return TryClose();
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
                "Ore density is available only in singleplayer Cheat Mode or Creative mode."
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
        if (preparingSurfaceFilter)
        {
            ClearSurfacePreparationFrame();
            return false;
        }

        float aspect = render.FrameWidth / (float)Math.Max(1, render.FrameHeight);
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
            SurfaceSafetyEnabled ? surfaceHeightTexture : null,
            mapLayerTexture,
            EffectiveMapLayerOpacity,
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
            visibleEntityPolicy
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
            AtlasFogColor
        );
    }

    private void FitLoadedTerrain()
    {
        float radius = GameViewDistance;
        float aspect = capi.Render.FrameWidth / (float)Math.Max(1, capi.Render.FrameHeight);
        float pitch = targetPitchDegrees * GameMath.DEG2RAD;

        // A circular radius projects to an ellipse when the camera tilts. Add
        // vertical headroom for trees, buildings and hills so neither the top
        // nor bottom edge is clipped at low camera angles.
        float horizontalFit = radius / Math.Max(0.5f, aspect);
        float verticalFit = radius * Math.Abs(MathF.Sin(pitch))
            + Math.Min(192, radius * 0.3f) * Math.Abs(MathF.Cos(pitch));
        targetZoom = Math.Clamp(Math.Max(horizontalFit, verticalFit) * 1.08f, 80, 30000);
    }

    private void OnFogToggled(bool enabled)
    {
        if (!capi.IsSinglePlayer) return;
        config.FogEnabled = enabled;
        InvalidateFogTexture();
        saveConfig();
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
        if (!CreativeCheatSettingsAvailable) return;

        config.SearchModeEnabled = enabled;
        if (!enabled)
        {
            searchPanel?.UnfocusOwnElements();
            ClearSearch();
        }
        saveConfig();
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
        settingsModal.GetAtlasSwitch("map-layers")?.SetValue(config.MapLayersEnabled);
        settingsModal.GetAtlasSwitch("fog")?.SetValue(EffectiveFogEnabled);
        settingsModal.GetAtlasSwitch("fog")!.Enabled = capi.IsSinglePlayer;
        settingsModal.GetAtlasSwitch("animations")?.SetValue(config.AnimationsEnabled);
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

    private void SyncCreativeSettingsControls()
    {
        if (creativeSettingsModal == null) return;

        bool available = CreativeCheatSettingsAvailable;
        creativeSettingsModal.GetAtlasSwitch("cave-mode")?.SetValue(
            available && config.CaveModeEnabled
        );
        creativeSettingsModal.GetAtlasSwitch("search-mode")?.SetValue(
            available && config.SearchModeEnabled
        );
        creativeSettingsModal.GetAtlasSwitch("camera-angle-lock")?.SetValue(
            available && config.CameraAngleLocked
        );
        creativeSettingsModal.GetAtlasSwitch("cave-mode")!.Enabled = available;
        creativeSettingsModal.GetAtlasSwitch("search-mode")!.Enabled = available;
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

    private void PauseSingleplayerForAtlas()
    {
        pausedGameForAtlas = capi.IsSinglePlayer && !capi.IsGamePaused;
        if (pausedGameForAtlas)
        {
            capi.PauseGame(true);
        }
    }

    private void ResumeSingleplayerAfterAtlas()
    {
        if (!pausedGameForAtlas) return;

        pausedGameForAtlas = false;
        if (capi.IsSinglePlayer)
        {
            capi.PauseGame(false);
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

    private void RenderFogMask()
    {
        if (!EffectiveFogEnabled) return;

        int frameWidth = capi.Render.FrameWidth;
        int frameHeight = capi.Render.FrameHeight;
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
            capi.Render.GetEngineShader(EnumShaderProgram.Gui).Use();
            capi.Render.GLDisableDepthTest();
            capi.Render.GlToggleBlend(true, EnumBlendMode.Standard);
            capi.Render.Render2DTexture(fogTexture.TextureId, 0, 0, frameWidth, frameHeight, 40);
        }
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
