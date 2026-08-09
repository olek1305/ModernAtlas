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
    private const float MinimumPitchDegrees = 20;
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
    private readonly AtlasSurfaceCache surfaceCache;
    private readonly System.Func<int, bool> clearCache;

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
    private float yawDegrees = 42;
    private float pitchDegrees = 72;
    private float zoom = 180;
    private bool loggedFirstRender;
    private int fogTextureWidth;
    private int fogTextureHeight;
    private float fogTextureZoom = -1;
    private float fogTexturePitch = -1;
    private bool pausedGameForAtlas;
    private float atlasAnimationSeconds;
    private float frozenWindWaveCounter;
    private float frozenWindWaveCounterHighFrequency;
    private float frozenWaterStillCounter;
    private float frozenWaterFlowCounter;
    private long lastAtlasFrameMilliseconds;
    private float atlasRealDeltaTime;
    private bool loggedEntityModels;

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

    public override string ToggleKeyCombinationCode => "modernatlas-open";
    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override double DrawOrder => 0.82;
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
        Func<IShaderProgram?> atlasOpacityShaderProvider,
        AtlasSurfaceCache surfaceCache,
        System.Func<int, bool> clearCache
    ) : base(capi)
    {
        this.config = config;
        this.serverPolicy = serverPolicy;
        this.saveConfig = saveConfig;
        this.stableLiquidShaderProvider = stableLiquidShaderProvider;
        this.atlasCloudShaderProvider = atlasCloudShaderProvider;
        this.atlasOpacityShaderProvider = atlasOpacityShaderProvider;
        this.surfaceCache = surfaceCache;
        this.clearCache = clearCache;
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
            atlasCloudShaderProvider,
            surfaceCache
        );
        centerX = capi.World.Player.Entity.Pos.X;
        centerZ = capi.World.Player.Entity.Pos.Z;
        centerY = capi.World.Player.Entity.Pos.Y;
        FocusOnExteriorSurface();
        FitLoadedTerrain();
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
        if (!loggedFirstRender)
        {
            loggedFirstRender = true;
            capi.Logger.Notification("[ModernAtlas] First 3D atlas GUI frame rendered.");
        }
        bool rendered = RenderLiveWorld(deltaTime);
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
        string entityStatus = visibleEntityPolicy.AnyEntityModels
            ? "living models on"
            : capi.IsSinglePlayer || serverPolicy.AnyEntityModels
                ? "living models off"
                : "living models blocked by server";
        string multiplayerStatus = capi.IsSinglePlayer ? "singleplayer controls" : "multiplayer safe limits locked";
        string pauseStatus = capi.IsSinglePlayer ? "game paused" : "live server";
        string status = $"Game view distance {GameViewDistance} blocks • {surfaceCache.Status} • {fogStatus} • {animationStatus} • {cloudStatus} • {entityStatus} • exterior surface • {pauseStatus} • {multiplayerStatus} • {rendererStatus} • no distant chunk requests";
        overlay?.GetDynamicText("status").SetNewText(status);
        overlay?.Render(deltaTime);
        if (settingsModalOpen)
        {
            settingsModal?.GetDynamicText("cache-status")?.SetNewText(surfaceCache.Status);
            settingsModal?.Render(deltaTime);
        }
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
                yawDegrees = NormalizeDegrees(yawDegrees + (float)deltaX * 0.42f);
                pitchDegrees = Math.Clamp(
                    pitchDegrees - (float)deltaY * 0.32f,
                    MinimumPitchDegrees,
                    86
                );
                InvalidateFogTexture();
            }

            if (leftDragging)
            {
                double worldPerPixel = zoom * 2.0 / Math.Max(1, capi.Render.FrameHeight);
                double yaw = yawDegrees * GameMath.DEG2RAD;
                double rightX = Math.Cos(yaw);
                double rightZ = -Math.Sin(yaw);
                double forwardX = Math.Sin(yaw);
                double forwardZ = Math.Cos(yaw);
                // Drag the map in the same screen-space direction as the mouse.
                // The vertical sign must not flip when the camera yaw changes.
                centerX -= (deltaX * rightX + deltaY * forwardX) * worldPerPixel;
                centerZ -= (deltaX * rightZ + deltaY * forwardZ) * worldPerPixel;
                InvalidateFogTexture();
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
        zoom = Math.Clamp(zoom * MathF.Pow(0.84f, wheel), 8, 30000);
        InvalidateFogTexture();
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

        float pan = Math.Max(1, zoom * 0.08f);
        double yaw = yawDegrees * GameMath.DEG2RAD;
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
            pitchDegrees = Math.Clamp(pitchDegrees + 4, MinimumPitchDegrees, 86);
            InvalidateFogTexture();
            args.Handled = true;
        }
        else if (args.KeyCode == (int)GlKeys.F)
        {
            pitchDegrees = Math.Clamp(pitchDegrees - 4, MinimumPitchDegrees, 86);
            InvalidateFogTexture();
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

    public override void Dispose()
    {
        ResumeSingleplayerAfterAtlas();
        exactChunkRenderer?.Dispose();
        exactChunkRenderer = null;
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

        ElementBounds modalRoot = ElementBounds.Fixed(0, 0, 340, 500)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds modalBackground = ElementBounds.Fixed(0, 0, 340, 500);
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
                "Map cache",
                CairoFont.WhiteSmallishText(),
                ElementBounds.Fixed(20, 392, 180, 28)
            )
            .AddDynamicText(
                "",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 421, 300, 28),
                "cache-status"
            )
            .AddButton(
                "Clear cache",
                () => clearCache(GameViewDistance),
                ElementBounds.Fixed(20, 450, 140, 34),
                EnumButtonStyle.Normal,
                "cache-clear"
            )
            .Compose();
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
            windWaveCounter,
            windWaveCounterHighFrequency,
            waterStillCounter,
            waterFlowCounter,
            config.CloudsEnabled,
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
        yawDegrees = 42;
        pitchDegrees = 72;
        FitLoadedTerrain();
    }

    private void CenterOnPlayer()
    {
        centerX = capi.World.Player.Entity.Pos.X;
        centerZ = capi.World.Player.Entity.Pos.Z;
        centerY = capi.World.Player.Entity.Pos.Y;
        FocusOnExteriorSurface();
        InvalidateFogTexture();
    }

    private void Pan(double x, double z, float amount, KeyEvent args)
    {
        centerX += x * amount;
        centerZ += z * amount;
        InvalidateFogTexture();
        args.Handled = true;
    }

    private void ResetPointerDrag()
    {
        leftDragging = false;
        rightDragging = false;
    }

    private void FitLoadedTerrain()
    {
        float radius = GameViewDistance;
        float aspect = capi.Render.FrameWidth / (float)Math.Max(1, capi.Render.FrameHeight);
        float pitch = pitchDegrees * GameMath.DEG2RAD;

        // A circular radius projects to an ellipse when the camera tilts. Add
        // vertical headroom for trees, buildings and hills so neither the top
        // nor bottom edge is clipped at low camera angles.
        float horizontalFit = radius / Math.Max(0.5f, aspect);
        float verticalFit = radius * Math.Abs(MathF.Sin(pitch))
            + Math.Min(192, radius * 0.3f) * Math.Abs(MathF.Cos(pitch));
        zoom = Math.Clamp(Math.Max(horizontalFit, verticalFit) * 1.08f, 80, 30000);
        InvalidateFogTexture();
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
        settingsModal.GetDynamicText("cache-status")?.SetNewText(surfaceCache.Status);
        settingsModal.GetSwitch("fog")?.SetValue(EffectiveFogEnabled);
        settingsModal.GetSwitch("fog").Enabled = capi.IsSinglePlayer;
        settingsModal.GetSwitch("animations")?.SetValue(config.AnimationsEnabled);
        settingsModal.GetSwitch("clouds")?.SetValue(config.CloudsEnabled);
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
        int x = (int)Math.Floor(centerX);
        int z = (int)Math.Floor(centerZ);
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
        if (fogTexture == null
            || fogTextureWidth != width
            || fogTextureHeight != height
            || Math.Abs(fogTextureZoom - zoom) > 0.1f
            || Math.Abs(fogTexturePitch - pitchDegrees) > 0.1f)
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

        using ImageSurface surface = new(Format.Argb32, Math.Max(1, width), Math.Max(1, height));
        using Context context = new(surface);
        context.Operator = Operator.Source;
        context.SetSourceRGBA(0.32, 0.38, 0.40, 0.90);
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

        // Keep the complete game view distance clear. The feather starts only
        // outside that radius so tilted hills and buildings are not hidden by
        // the GUI fog mask.
        const int featherSteps = 18;
        for (int step = featherSteps; step >= 0; step--)
        {
            double scale = 1.0 + 0.18 * step / featherSteps;
            double alpha = 0.90 * step / featherSteps;
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
        context.Scale(radiusX, radiusY);
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
        yawDegrees = NormalizeDegrees(yawDegrees + degrees);
        InvalidateFogTexture();
        args.Handled = true;
    }

    private static float NormalizeDegrees(float value)
    {
        value %= 360;
        return value < 0 ? value + 360 : value;
    }
}
