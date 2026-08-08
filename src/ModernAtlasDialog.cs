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
    private readonly Action saveConfig;
    private readonly Func<IShaderProgram?> stableLiquidShaderProvider;

    private GuiComposer? overlay;
    private LoadedTexture? fogTexture;
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
    private bool EffectiveFogEnabled => capi.IsSinglePlayer && config.FogEnabled
        || !capi.IsSinglePlayer;

    public override string ToggleKeyCombinationCode => "modernatlas-open";
    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override double DrawOrder => 0.82;
    public override double InputOrder => 0.05;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;

    public ModernAtlasDialog(
        ICoreClientAPI capi,
        ModernAtlasConfig config,
        Action saveConfig,
        Func<IShaderProgram?> stableLiquidShaderProvider
    ) : base(capi)
    {
        this.config = config;
        this.saveConfig = saveConfig;
        this.stableLiquidShaderProvider = stableLiquidShaderProvider;
        ComposeOverlay();
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        PauseSingleplayerForAtlas();
        atlasAnimationSeconds = 0;
        CaptureAnimationFrame();
        lastAtlasFrameMilliseconds = capi.ElapsedMilliseconds;
        exactChunkRenderer ??= ExactChunkRendererAdapter.TryCreate(
            capi,
            stableLiquidShaderProvider
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
        RenderFogMask();
        capi.Render.GetEngineShader(EnumShaderProgram.Gui).Use();
        capi.Render.GLDepthMask(false);
        capi.Render.GLDisableDepthTest();
        capi.Render.GlToggleBlend(true, EnumBlendMode.Standard);
        string rendererStatus = rendered
            ? "exact loaded chunk geometry"
            : "exact renderer unavailable";
        string fogStatus = EffectiveFogEnabled ? "fog on" : "fog off";
        string animationStatus = config.AnimationsEnabled ? "animations on" : "animations paused";
        string multiplayerStatus = capi.IsSinglePlayer ? "singleplayer controls" : "multiplayer safe limits locked";
        string pauseStatus = capi.IsSinglePlayer ? "game paused" : "live server";
        string status = $"Game view distance {GameViewDistance} blocks • {fogStatus} • {animationStatus} • exterior surface • {pauseStatus} • {multiplayerStatus} • {rendererStatus} • no distant chunk requests";
        overlay?.GetDynamicText("status").SetNewText(status);
        overlay?.Render(deltaTime);
    }

    public override void OnMouseDown(MouseEvent args)
    {
        overlay?.OnMouseDown(args);
        if (args.Handled) return;

        if (args.Button == EnumMouseButton.Left) leftDragging = true;
        if (args.Button == EnumMouseButton.Right) rightDragging = true;
        if (args.Button == EnumMouseButton.Middle) ResetView();
        args.Handled = true;
    }

    public override void OnMouseUp(MouseEvent args)
    {
        overlay?.OnMouseUp(args);
        if (args.Handled) return;

        if (args.Button == EnumMouseButton.Left)
        {
            leftDragging = false;
            MaybeRecenterScene();
            InvalidateFogTexture();
        }
        if (args.Button == EnumMouseButton.Right)
        {
            rightDragging = false;
            InvalidateFogTexture();
        }
        args.Handled = true;
    }

    public override void OnMouseMove(MouseEvent args)
    {
        overlay?.OnMouseMove(args);
        if (args.Handled) return;

        if (rightDragging)
        {
            yawDegrees = NormalizeDegrees(yawDegrees + args.DeltaX * 0.42f);
            pitchDegrees = Math.Clamp(
                pitchDegrees - args.DeltaY * 0.32f,
                MinimumPitchDegrees,
                86
            );
            InvalidateFogTexture();
            args.Handled = true;
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
            centerX -= (args.DeltaX * rightX + args.DeltaY * forwardX) * worldPerPixel;
            centerZ -= (args.DeltaX * rightZ + args.DeltaY * forwardZ) * worldPerPixel;
            InvalidateFogTexture();
            args.Handled = true;
        }

        // The atlas covers the entire screen. Do not leak hover interaction to
        // hotbar slots, creative inventory elements or dialogs underneath it.
        args.Handled = true;
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        overlay?.OnMouseWheel(args);
        if (args.IsHandled) return;

        float wheel = args.deltaPrecise != 0 ? args.deltaPrecise : args.delta;
        zoom = Math.Clamp(zoom * MathF.Pow(0.84f, wheel), 8, 30000);
        InvalidateFogTexture();
        args.SetHandled();
    }

    public override void OnKeyDown(KeyEvent args)
    {
        overlay?.OnKeyDown(args, false);
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
        ResumeSingleplayerAfterAtlas();
        base.OnGuiClosed();
    }

    public override void Dispose()
    {
        ResumeSingleplayerAfterAtlas();
        exactChunkRenderer?.Dispose();
        exactChunkRenderer = null;
        fogTexture?.Dispose();
        fogTexture = null;
        overlay?.Dispose();
        overlay = null;
        base.Dispose();
    }

    private void ComposeOverlay()
    {
        ElementBounds root = ElementBounds.Fill;
        double guiWidth = capi.Gui.WindowBounds.InnerWidth / Math.Max(0.5, RuntimeEnv.GUIScale);
        double settingsX = Math.Max(20, guiWidth - 320);

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
                "Atlas settings",
                CairoFont.WhiteSmallishText().WithFontSize(20),
                ElementBounds.Fixed(settingsX + 20, 30, 260, 30)
            )
            .AddStaticText(
                capi.IsSinglePlayer ? "Unexplored fog" : "Server fog (locked)",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(settingsX + 20, 70, 180, 28)
            )
            .AddSwitch(
                OnFogToggled,
                ElementBounds.Fixed(settingsX + 234, 66, 46, 30),
                "fog",
                24,
                4
            )
            .AddStaticText(
                "Atlas animations",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(settingsX + 20, 110, 180, 28)
            )
            .AddSwitch(
                OnAnimationsToggled,
                ElementBounds.Fixed(settingsX + 234, 106, 46, 30),
                "animations",
                24,
                4
            )
            .Compose();
        SyncSettingsControls();
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
            waterFlowCounter
        ) == true;
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
        return rendered;
    }

    private void ResetView()
    {
        centerX = capi.World.Player.Entity.Pos.X;
        centerZ = capi.World.Player.Entity.Pos.Z;
        centerY = capi.World.Player.Entity.Pos.Y;
        FocusOnExteriorSurface();
        yawDegrees = 42;
        pitchDegrees = 72;
        FitLoadedTerrain();
    }

    private void Pan(double x, double z, float amount, KeyEvent args)
    {
        centerX += x * amount;
        centerZ += z * amount;
        InvalidateFogTexture();
        MaybeRecenterScene();
        args.Handled = true;
    }

    private void MaybeRecenterScene()
    {
        FocusOnExteriorSurface();
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

    private void SyncSettingsControls()
    {
        if (overlay == null) return;
        overlay.GetSwitch("fog")?.SetValue(EffectiveFogEnabled);
        overlay.GetSwitch("fog").Enabled = capi.IsSinglePlayer;
        overlay.GetSwitch("animations")?.SetValue(config.AnimationsEnabled);
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
