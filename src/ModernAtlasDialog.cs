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
    private GuiComposer? settingsModal;
    private GuiComposer? visualLabModal;
    private GuiComposer? unitPanel;
    private bool settingsModalOpen;
    private bool visualLabModalOpen;
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
    private bool automatedSmokeTestUnitInspectionAttempted;
    private bool automatedSmokeTestUnitInspectionPassed;
    private int automatedSmokeTestSearchPhase;
    private bool automatedSmokeTestSearchPassed;
    private int automatedSmokeTestMapLayerPhase;
    private bool automatedSmokeTestMapLayerPassed;
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
    private bool SurfaceSafetyEnabled => !capi.IsSinglePlayer || !cheatModeEnabled;
    private bool HasUnlockedCameraPitch => cheatModeEnabled
        || capi.World.Player.WorldData.CurrentGameMode == EnumGameMode.Creative;
    private bool UnitInspectionEnabled => capi.IsSinglePlayer && HasUnlockedCameraPitch;
    private float MinimumPitchDegrees => HasUnlockedCameraPitch
        ? UnlockedMinimumPitchDegrees
        : StandardMinimumPitchDegrees;
    private Vec3f AtlasFogColor => AtlasVisualPalettes.FogColor(config.FogPalette);

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
        settingsModalOpen = false;
        visualLabModalOpen = false;
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
            targetPitchDegrees = UnlockedMinimumPitchDegrees;
            pitchDegrees = UnlockedMinimumPitchDegrees;
            capi.Logger.Notification(
                "[ModernAtlas] Automated smoke test is exercising the unlocked 0-degree camera pitch."
            );
        }
        FocusOnExteriorSurface();
        FitLoadedTerrain();
        zoom = targetZoom;
        PrepareSurfaceSafetyFilter();
        PrepareMapLayer();
        SyncSettingsControls();
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
        AdvanceSearch();
        if (rendered && automatedSmokeTestActive)
        {
            AutomatedSmokeTestRenderedExactWorld = true;
            if (automatedSmokeTestRequiresUnlockedPitch
                && pitchDegrees <= UnlockedMinimumPitchDegrees + 0.01f)
            {
                automatedSmokeTestRenderedAtPitchFloor = true;
            }
            ExerciseAutomatedUnitInspection();
            ExerciseAutomatedSearch();
            ExerciseAutomatedMapLayers();
        }
        if (rendered && !loggedEntityModels && visibleEntityPolicy.AnyEntityModels)
        {
            loggedEntityModels = true;
            capi.Logger.Notification(
                "[ModernAtlas] Rendered {0} live 3D living entity models from client-loaded entities.",
                exactChunkRenderer?.LastRenderedEntityCount ?? 0
            );
        }
        RenderFogMask();
        ForceOpaqueWindowAlpha();
        capi.Render.GetEngineShader(EnumShaderProgram.Gui).Use();
        capi.Render.GLDepthMask(false);
        capi.Render.GLDisableDepthTest();
        capi.Render.GlToggleBlend(true, EnumBlendMode.Standard);
        RenderSearchMarkers();
        string rendererStatus = rendered
            ? "exact loaded chunk geometry"
            : "exact renderer unavailable";
        string fogStatus = EffectiveFogEnabled ? "fog on" : "fog off";
        string animationStatus = config.AnimationsEnabled ? "animations on" : "animations paused";
        string cloudStatus = config.CloudsEnabled ? "clouds on" : "clouds off";
        string lightingStatus = config.LiveLightingEnabled
            ? "live sun/weather"
            : $"fixed sun {config.FixedSunHour:00}:00";
        string entityStatus = visibleEntityPolicy.AnyEntityModels
            ? "living models on"
            : capi.IsSinglePlayer || serverPolicy.AnyEntityModels
                ? "living models off"
                : "living models blocked by server";
        string caveStatus = SurfaceSafetyEnabled
            ? "underground caves hidden"
            : "cave view (Cheat Mode)";
        string multiplayerStatus = capi.IsSinglePlayer ? "singleplayer controls" : "multiplayer safe limits locked";
        string pauseStatus = capi.IsSinglePlayer ? "game paused" : "live server";
        if (preparingSurfaceFilter)
        {
            rendererStatus = $"preparing surface safety {surfaceHeightTexture.ProgressPercent}%";
        }
        string status = $"Game view distance {GameViewDistance} blocks • {fogStatus} • {lightingStatus} • {animationStatus} • {cloudStatus} • {entityStatus} • {caveStatus} • exterior surface • {pauseStatus} • {multiplayerStatus} • {rendererStatus} • no distant chunk requests";
        overlay?.GetDynamicText("status").SetNewText(status);
        overlay?.GetDynamicText("search-status").SetNewText(searchController.StatusText);
        overlay?.GetDynamicText("layer-status").SetNewText(mapLayerTexture.StatusText);
        overlay?.Render(deltaTime);
        RenderUnitInspection(deltaTime);
        if (settingsModalOpen)
        {
            settingsModal?.Render(deltaTime);
        }
        if (visualLabModalOpen)
        {
            visualLabModal?.Render(deltaTime);
        }
        AdvanceAutomatedSmokeTest();
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (visualLabModalOpen)
        {
            visualLabModal?.OnMouseDown(args);
            args.Handled = true;
            return;
        }
        if (settingsModalOpen)
        {
            settingsModal?.OnMouseDown(args);
            args.Handled = true;
            return;
        }
        if (selectedEntityId != null)
        {
            unitPanel?.OnMouseDown(args);
            if (args.Handled) return;
        }
        overlay?.OnMouseDown(args);
        if (args.Handled) return;

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
        if (visualLabModalOpen)
        {
            visualLabModal?.OnMouseUp(args);
            args.Handled = true;
            return;
        }
        if (settingsModalOpen)
        {
            settingsModal?.OnMouseUp(args);
            args.Handled = true;
            return;
        }
        if (selectedEntityId != null)
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

        overlay?.OnMouseUp(args);
        if (args.Handled) return;
        args.Handled = true;
    }

    public override void OnMouseMove(MouseEvent args)
    {
        if (visualLabModalOpen)
        {
            visualLabModal?.OnMouseMove(args);
            args.Handled = true;
            return;
        }
        if (settingsModalOpen)
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
                targetYawDegrees = NormalizeDegrees(targetYawDegrees + (float)deltaX * 0.42f);
                targetPitchDegrees = Math.Clamp(
                    targetPitchDegrees - (float)deltaY * 0.32f,
                    MinimumPitchDegrees,
                    86
                );
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

        if (selectedEntityId != null)
        {
            unitPanel?.OnMouseMove(args);
            if (args.Handled) return;
        }
        overlay?.OnMouseMove(args);
        // The atlas covers the entire screen. Do not leak hover interaction to
        // hotbar slots, creative inventory elements or dialogs underneath it.
        args.Handled = true;
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (visualLabModalOpen)
        {
            visualLabModal?.OnMouseWheel(args);
            args.SetHandled();
            return;
        }
        if (settingsModalOpen)
        {
            settingsModal?.OnMouseWheel(args);
            args.SetHandled();
            return;
        }
        overlay?.OnMouseWheel(args);
        if (args.IsHandled) return;

        float wheel = args.deltaPrecise != 0 ? args.deltaPrecise : args.delta;
        targetZoom = Math.Clamp(targetZoom * MathF.Pow(0.84f, wheel), 8, 30000);
        args.SetHandled();
    }

    public override void OnKeyDown(KeyEvent args)
    {
        if (args.KeyCode == (int)GlKeys.Escape || args.KeyCode == (int)GlKeys.G)
        {
            TryClose();
            args.Handled = true;
            return;
        }

        if (visualLabModalOpen)
        {
            visualLabModal?.OnKeyDown(args, false);
        }
        else if (settingsModalOpen)
        {
            settingsModal?.OnKeyDown(args, false);
        }
        else
        {
            overlay?.OnKeyDown(args, false);
        }
        if (args.Handled) return;

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
            targetPitchDegrees = Math.Clamp(targetPitchDegrees + 4, MinimumPitchDegrees, 86);
            args.Handled = true;
        }
        else if (args.KeyCode == (int)GlKeys.F)
        {
            targetPitchDegrees = Math.Clamp(targetPitchDegrees - 4, MinimumPitchDegrees, 86);
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
            base.OnKeyDown(args);
        }
    }

    public override bool CaptureAllInputs() => true;
    public override bool CaptureRawMouse() => true;
    public override bool ShouldReceiveKeyboardEvents() => IsOpened();
    public override bool ShouldReceiveMouseEvents() => IsOpened();
    public override bool OnEscapePressed() => TryClose();

    public override void OnGuiClosed()
    {
        settingsModalOpen = false;
        visualLabModalOpen = false;
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
        if (activeMapLayer.RequiresSpoilerAccess() && !HasUnlockedCameraPitch)
        {
            SetMapLayer(AtlasMapLayer.TexturedTerrain);
        }
        ClampPitchToAccessLevel(!IsOpened());
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
        automatedSmokeTestActive = true;
        automatedSmokeTestElapsedSeconds = 0;
        automatedSmokeTestCompletion = completion;
        automatedSmokeTestRequiresUnlockedPitch = false;
        automatedSmokeTestRenderedAtPitchFloor = false;
        automatedSmokeTestUnitInspectionAttempted = false;
        automatedSmokeTestUnitInspectionPassed = false;
        automatedSmokeTestSearchPhase = 0;
        automatedSmokeTestSearchPassed = false;
        automatedSmokeTestMapLayerPhase = 0;
        automatedSmokeTestMapLayerPassed = false;
        AutomatedSmokeTestRenderedExactWorld = false;
    }

    private void AdvanceAutomatedSmokeTest()
    {
        if (!automatedSmokeTestActive) return;

        automatedSmokeTestElapsedSeconds += atlasRealDeltaTime;
        bool passed = AutomatedSmokeTestRenderedExactWorld
            && (!automatedSmokeTestRequiresUnlockedPitch
                || automatedSmokeTestRenderedAtPitchFloor)
            && (!automatedSmokeTestUnitInspectionAttempted
                || automatedSmokeTestUnitInspectionPassed)
            && automatedSmokeTestSearchPassed
            && automatedSmokeTestMapLayerPassed;
        if (passed && automatedSmokeTestElapsedSeconds < 3f) return;
        if (!passed && automatedSmokeTestElapsedSeconds < 24f) return;

        automatedSmokeTestActive = false;
        Action<bool>? completion = automatedSmokeTestCompletion;
        automatedSmokeTestCompletion = null;
        completion?.Invoke(passed);
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
        automatedSmokeTestActive = false;
        automatedSmokeTestElapsedSeconds = 0;
        automatedSmokeTestCompletion = null;
        automatedSmokeTestRequiresUnlockedPitch = false;
        automatedSmokeTestRenderedAtPitchFloor = false;
        automatedSmokeTestUnitInspectionAttempted = false;
        automatedSmokeTestUnitInspectionPassed = false;
        automatedSmokeTestSearchPhase = 0;
        automatedSmokeTestSearchPassed = false;
        automatedSmokeTestMapLayerPhase = 0;
        automatedSmokeTestMapLayerPassed = false;
        AutomatedSmokeTestRenderedExactWorld = false;
        cheatModeEnabled = false;
        settingsModalOpen = false;
        visualLabModalOpen = false;
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
        settingsModal?.Dispose();
        settingsModal = null;
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

        overlay = capi.Gui.CreateCompo("modernatlas-3d", root)
            .AddStaticText(
                "ModernAtlas 3D",
                CairoFont.WhiteSmallishText().WithFontSize(26),
                ElementBounds.Fixed(22, 18, 480, 40)
            )
            .AddStaticText(
                Lang.Get("modernatlas:controls-help"),
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(24, 58, 900, 34)
            )
            .AddDynamicText(
                "",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(24, 88, 650, 34),
                "status"
            )
            .AddStaticText(
                "Search loaded map",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(24, 128, 140, 30)
            )
            .AddTextInput(
                ElementBounds.Fixed(166, 122, 300, 34),
                OnSearchTextChanged,
                CairoFont.SmallTextInput(),
                "search-input"
            )
            .AddButton(
                "Clear",
                ClearSearch,
                ElementBounds.Fixed(478, 122, 76, 34),
                EnumButtonStyle.Normal,
                "search-clear"
            )
            .AddDynamicText(
                "",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(24, 162, 760, 30),
                "search-status"
            )
            .AddStaticText(
                "Map layer",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(24, 202, 140, 30)
            )
            .AddDropDown(
                AtlasMapLayerInfo.Values,
                AtlasMapLayerInfo.Names,
                (int)activeMapLayer,
                OnMapLayerChanged,
                ElementBounds.Fixed(166, 196, 300, 34),
                "map-layer"
            )
            .AddDynamicText(
                "",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(24, 236, 760, 30),
                "layer-status"
            )
            .AddButton(
                "Settings",
                OpenSettingsModal,
                ElementBounds.Fixed(Math.Max(24, guiWidth - 150), 24, 120, 34),
                EnumButtonStyle.Normal,
                "settings-button"
            )
            .Compose();
        overlay.GetTextInput("search-input")?.SetMaxLength(80);
        overlay.GetTextInput("search-input")?.SetPlaceHolderText(
            "Block, creature, player or dropped item"
        );

        ElementBounds unitRoot = ElementBounds.Fixed(0, 0, 340, 250)
            .WithAlignment(EnumDialogArea.RightMiddle)
            .WithFixedOffset(-24, 0);
        unitPanel = capi.Gui.CreateCompo("modernatlas-unit-inspection", unitRoot)
            .AddShadedDialogBG(ElementBounds.Fixed(0, 0, 340, 250), true)
            .AddDynamicText(
                "",
                CairoFont.WhiteSmallishText().WithFontSize(20),
                ElementBounds.Fixed(20, 18, 220, 34),
                "unit-name"
            )
            .AddButton(
                "Close",
                CloseUnitInspection,
                ElementBounds.Fixed(245, 14, 75, 30),
                EnumButtonStyle.Normal,
                "unit-close"
            )
            .AddDynamicText(
                "",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 64, 300, 168),
                "unit-details"
            )
            .Compose();

        ElementBounds modalRoot = ElementBounds.Fixed(0, 0, 340, 535)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds modalBackground = ElementBounds.Fixed(0, 0, 340, 535);
        settingsModal = capi.Gui.CreateCompo("modernatlas-settings", modalRoot)
            .AddShadedDialogBG(modalBackground, true)
            .AddStaticText(
                "Settings",
                CairoFont.WhiteSmallishText().WithFontSize(20),
                ElementBounds.Fixed(20, 18, 200, 30)
            )
            .AddButton(
                "Close",
                CloseSettingsModal,
                ElementBounds.Fixed(240, 14, 80, 30),
                EnumButtonStyle.Normal,
                "settings-close"
            )
            .AddStaticText(
                capi.IsSinglePlayer ? "Unexplored fog" : "Server fog (locked)",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 65, 180, 28)
            )
            .AddSwitch(
                OnFogToggled,
                ElementBounds.Fixed(264, 61, 46, 30),
                "fog",
                24,
                4
            )
            .AddStaticText(
                "Atlas animations",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 105, 180, 28)
            )
            .AddSwitch(
                OnAnimationsToggled,
                ElementBounds.Fixed(264, 101, 46, 30),
                "animations",
                24,
                4
            )
            .AddStaticText(
                "Live clouds",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 145, 180, 28)
            )
            .AddSwitch(
                OnCloudsToggled,
                ElementBounds.Fixed(264, 141, 46, 30),
                "clouds",
                24,
                4
            )
            .AddStaticText(
                "Living entities",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 185, 180, 28)
            )
            .AddSwitch(
                OnLivingEntitiesToggled,
                ElementBounds.Fixed(264, 181, 46, 30),
                "entities",
                24,
                4
            )
            .AddStaticText(
                "Players",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(40, 225, 160, 28)
            )
            .AddSwitch(
                OnPlayersToggled,
                ElementBounds.Fixed(264, 221, 46, 30),
                "players",
                24,
                4
            )
            .AddStaticText(
                "Animals",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(40, 265, 160, 28)
            )
            .AddSwitch(
                OnAnimalsToggled,
                ElementBounds.Fixed(264, 261, 46, 30),
                "animals",
                24,
                4
            )
            .AddStaticText(
                "Hostile mobs",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(40, 305, 160, 28)
            )
            .AddSwitch(
                OnMobsToggled,
                ElementBounds.Fixed(264, 301, 46, 30),
                "mobs",
                24,
                4
            )
            .AddStaticText(
                "NPCs",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(40, 345, 160, 28)
            )
            .AddSwitch(
                OnNpcsToggled,
                ElementBounds.Fixed(264, 341, 46, 30),
                "npcs",
                24,
                4
            )
            .AddStaticText(
                "Live sun and weather",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 385, 200, 28)
            )
            .AddSwitch(
                OnLiveLightingToggled,
                ElementBounds.Fixed(264, 381, 46, 30),
                "live-lighting",
                24,
                4
            )
            .AddStaticText(
                "Fixed sun hour",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 425, 140, 28)
            )
            .AddSlider(
                OnFixedSunHourChanged,
                ElementBounds.Fixed(170, 419, 140, 34),
                "fixed-sun-hour"
            )
            .AddButton(
                "Atlas visual lab",
                OpenVisualLab,
                ElementBounds.Fixed(20, 472, 300, 38),
                EnumButtonStyle.Normal,
                "visual-lab-open"
            )
            .Compose();
        settingsModal.GetSlider("fixed-sun-hour")?.SetValues(
            Math.Clamp(config.FixedSunHour, 0, 23),
            0,
            23,
            1,
            "h"
        );

        ElementBounds labRoot = ElementBounds.Fixed(0, 0, 430, 440)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        visualLabModal = capi.Gui.CreateCompo("modernatlas-visual-lab", labRoot)
            .AddShadedDialogBG(ElementBounds.Fixed(0, 0, 430, 440), true)
            .AddStaticText(
                "Atlas visual lab",
                CairoFont.WhiteSmallishText().WithFontSize(20),
                ElementBounds.Fixed(20, 18, 240, 30)
            )
            .AddButton(
                "Back",
                CloseVisualLab,
                ElementBounds.Fixed(330, 14, 80, 30),
                EnumButtonStyle.Normal,
                "visual-lab-back"
            )
            .AddStaticText(
                "Atlas-only controls; the normal world is never changed.",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 54, 390, 28)
            )
            .AddStaticText(
                "Exposure",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 96, 160, 28)
            )
            .AddSlider(
                OnAtlasExposureChanged,
                ElementBounds.Fixed(205, 90, 205, 34),
                "atlas-exposure"
            )
            .AddStaticText(
                "Data-layer opacity",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 146, 175, 28)
            )
            .AddSlider(
                OnMapLayerOpacityChanged,
                ElementBounds.Fixed(205, 140, 205, 34),
                "layer-opacity"
            )
            .AddStaticText(
                "Boundary softness",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 196, 175, 28)
            )
            .AddSlider(
                OnBoundarySoftnessChanged,
                ElementBounds.Fixed(205, 190, 205, 34),
                "boundary-softness"
            )
            .AddStaticText(
                "Cave mask brightness",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 246, 180, 28)
            )
            .AddSlider(
                OnCaveMaskBrightnessChanged,
                ElementBounds.Fixed(205, 240, 205, 34),
                "cave-mask-brightness"
            )
            .AddStaticText(
                "Fog palette",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 296, 160, 28)
            )
            .AddDropDown(
                AtlasVisualPalettes.Values,
                AtlasVisualPalettes.Names,
                AtlasVisualPalettes.IndexOf(config.FogPalette),
                OnFogPaletteChanged,
                ElementBounds.Fixed(205, 290, 205, 34),
                "fog-palette"
            )
            .AddButton(
                "Reset visual tuning",
                ResetVisualTuning,
                ElementBounds.Fixed(20, 370, 390, 38),
                EnumButtonStyle.Normal,
                "visual-lab-reset"
            )
            .Compose();
        ConfigureVisualLabSliders();
        SyncSettingsControls();
    }

    private bool OpenSettingsModal()
    {
        ResetPointerDrag();
        visualLabModalOpen = false;
        settingsModalOpen = true;
        SyncSettingsControls();
        return true;
    }

    private bool CloseSettingsModal()
    {
        settingsModalOpen = false;
        visualLabModalOpen = false;
        return true;
    }

    private bool OpenVisualLab()
    {
        ResetPointerDrag();
        settingsModalOpen = false;
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
        visualLabModal?.GetSlider("atlas-exposure")?.SetValues(
            Math.Clamp(config.AtlasExposurePercent, 50, 150),
            50,
            150,
            5,
            "%"
        );
        visualLabModal?.GetSlider("layer-opacity")?.SetValues(
            Math.Clamp(config.MapLayerOpacityPercent, 0, 100),
            0,
            100,
            5,
            "%"
        );
        visualLabModal?.GetSlider("boundary-softness")?.SetValues(
            Math.Clamp(config.BoundarySoftnessPercent, 25, 200),
            25,
            200,
            5,
            "%"
        );
        visualLabModal?.GetSlider("cave-mask-brightness")?.SetValues(
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
        visualLabModal?.GetDropDown("fog-palette")?.SetSelectedIndex(
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
        config.MapLayerOpacityPercent = 62;
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
        GuiElementTextInput? input = overlay?.GetTextInput("search-input");
        if (input != null && input.GetText().Length > 0)
        {
            input.SetValue("", true);
        }
        return true;
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
        GuiElementDropDown? dropdown = overlay?.GetDropDown("map-layer");
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
        if (!searchController.HasActiveQuery) return;
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
            Math.Clamp(config.MapLayerOpacityPercent, 0, 100) / 100f,
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
        targetPitchDegrees = 72;
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
        settingsModal.GetSwitch("fog")?.SetValue(EffectiveFogEnabled);
        settingsModal.GetSwitch("fog").Enabled = capi.IsSinglePlayer;
        settingsModal.GetSwitch("animations")?.SetValue(config.AnimationsEnabled);
        settingsModal.GetSwitch("clouds")?.SetValue(config.CloudsEnabled);
        settingsModal.GetSwitch("live-lighting")?.SetValue(config.LiveLightingEnabled);
        settingsModal.GetSlider("fixed-sun-hour").Enabled = !config.LiveLightingEnabled;
        bool serverAllowsAny = capi.IsSinglePlayer || serverPolicy.AnyEntityModels;
        settingsModal.GetSwitch("entities")?.SetValue(config.LivingEntitiesEnabled && serverAllowsAny);
        settingsModal.GetSwitch("entities").Enabled = serverAllowsAny;
        SyncEntityCategorySwitch("players", config.ShowPlayers, serverPolicy.ShowPlayers);
        SyncEntityCategorySwitch("animals", config.ShowAnimals, serverPolicy.ShowAnimals);
        SyncEntityCategorySwitch("mobs", config.ShowMobs, serverPolicy.ShowMobs);
        SyncEntityCategorySwitch("npcs", config.ShowNpcs, serverPolicy.ShowNpcs);
    }

    private void SyncEntityCategorySwitch(string key, bool clientEnabled, bool serverEnabled)
    {
        bool categoryAllowed = capi.IsSinglePlayer || serverEnabled;
        settingsModal?.GetSwitch(key)?.SetValue(clientEnabled && categoryAllowed);
        settingsModal.GetSwitch(key).Enabled = config.LivingEntitiesEnabled && categoryAllowed;
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
