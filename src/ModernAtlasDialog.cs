using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
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

    private GuiComposer? overlay;
    private GuiComposer? settingsModal;
    private bool settingsModalOpen;
    private LoadedTexture? fogTexture;
    private MeshRef? opacityQuad;
    private bool leftDragging;
    private bool rightDragging;
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
    private float MinimumPitchDegrees => HasUnlockedCameraPitch
        ? UnlockedMinimumPitchDegrees
        : StandardMinimumPitchDegrees;

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
        RefreshVisibleEntityPolicy();
        ComposeOverlay();
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        settingsModalOpen = false;
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
        if (!loggedFirstRender)
        {
            loggedFirstRender = true;
            capi.Logger.Notification("[ModernAtlas] First 3D atlas GUI frame rendered.");
        }
        bool rendered = RenderLiveWorld(deltaTime);
        if (rendered && automatedSmokeTestActive)
        {
            AutomatedSmokeTestRenderedExactWorld = true;
            if (automatedSmokeTestRequiresUnlockedPitch
                && pitchDegrees <= UnlockedMinimumPitchDegrees + 0.01f)
            {
                automatedSmokeTestRenderedAtPitchFloor = true;
            }
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
        overlay?.Render(deltaTime);
        if (settingsModalOpen)
        {
            settingsModal?.Render(deltaTime);
        }
        AdvanceAutomatedSmokeTest();
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (settingsModalOpen)
        {
            settingsModal?.OnMouseDown(args);
            args.Handled = true;
            return;
        }
        overlay?.OnMouseDown(args);
        if (args.Handled) return;

        if (args.Button == EnumMouseButton.Left)
        {
            leftDragging = true;
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
        if (settingsModalOpen)
        {
            settingsModal?.OnMouseUp(args);
            args.Handled = true;
            return;
        }
        // Once a map drag begins, keep ownership of the gesture even if the
        // pointer crosses the settings panel. Letting the overlay consume the
        // release leaves the drag latched and the next move jumps the camera.
        if (args.Button == EnumMouseButton.Left && leftDragging)
        {
            leftDragging = false;
            InvalidateFogTexture();
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

        overlay?.OnMouseMove(args);
        // The atlas covers the entire screen. Do not leak hover interaction to
        // hotbar slots, creative inventory elements or dialogs underneath it.
        args.Handled = true;
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
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
        if (settingsModalOpen)
        {
            settingsModal?.OnKeyDown(args, false);
        }
        else
        {
            overlay?.OnKeyDown(args, false);
        }
        if (args.Handled) return;

        if (args.KeyCode == (int)GlKeys.Escape || args.KeyCode == (int)GlKeys.G)
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
        AutomatedSmokeTestRenderedExactWorld = false;
    }

    private void AdvanceAutomatedSmokeTest()
    {
        if (!automatedSmokeTestActive) return;

        automatedSmokeTestElapsedSeconds += atlasRealDeltaTime;
        if (automatedSmokeTestElapsedSeconds < 8f) return;

        automatedSmokeTestActive = false;
        Action<bool>? completion = automatedSmokeTestCompletion;
        automatedSmokeTestCompletion = null;
        bool passed = AutomatedSmokeTestRenderedExactWorld
            && (!automatedSmokeTestRequiresUnlockedPitch
                || automatedSmokeTestRenderedAtPitchFloor);
        completion?.Invoke(passed);
    }

    internal void OnWorldLeave()
    {
        automatedSmokeTestActive = false;
        automatedSmokeTestElapsedSeconds = 0;
        automatedSmokeTestCompletion = null;
        automatedSmokeTestRequiresUnlockedPitch = false;
        automatedSmokeTestRenderedAtPitchFloor = false;
        AutomatedSmokeTestRenderedExactWorld = false;
        cheatModeEnabled = false;
        settingsModalOpen = false;
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
        fogTexture?.Dispose();
        fogTexture = null;
        opacityQuad?.Dispose();
        opacityQuad = null;
        overlay?.Dispose();
        overlay = null;
        settingsModal?.Dispose();
        settingsModal = null;
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
            .AddButton(
                "Settings",
                OpenSettingsModal,
                ElementBounds.Fixed(Math.Max(24, guiWidth - 150), 24, 120, 34),
                EnumButtonStyle.Normal,
                "settings-button"
            )
            .Compose();

        ElementBounds modalRoot = ElementBounds.Fixed(0, 0, 340, 490)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds modalBackground = ElementBounds.Fixed(0, 0, 340, 490);
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
            .Compose();
        settingsModal.GetSlider("fixed-sun-hour")?.SetValues(
            Math.Clamp(config.FixedSunHour, 0, 23),
            0,
            23,
            1,
            "h"
        );
        SyncSettingsControls();
    }

    private bool OpenSettingsModal()
    {
        ResetPointerDrag();
        settingsModalOpen = true;
        SyncSettingsControls();
        return true;
    }

    private bool CloseSettingsModal()
    {
        settingsModalOpen = false;
        return true;
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
        exactChunkRenderer?.RenderSurfacePreparationFrame(EffectiveFogEnabled);
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
        context.Operator = Operator.Source;
        context.SetSourceRGBA(0.32, 0.38, 0.40, 1.0);
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
        const double clearScale = 0.90;
        const double opaqueScale = 1.04;
        const int featherSteps = 24;
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
            context.SetSourceRGBA(0.32, 0.38, 0.40, alpha);
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
