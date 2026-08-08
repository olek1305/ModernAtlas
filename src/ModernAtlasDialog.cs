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
    private static readonly int[] RadiusSteps =
        { 250, 500, 750, 1000, 1500 };

    private ExactChunkRendererAdapter? exactChunkRenderer;
    private readonly float[] projection = Mat4f.Create();
    private readonly ModernAtlasConfig config;
    private readonly Action saveConfig;

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
    private int radiusSafetyGeneration;

    private int EffectiveRadius => capi.IsSinglePlayer
        ? config.RadiusBlocks
        : ModernAtlasConfig.DefaultRadius;
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
        Action saveConfig
    ) : base(capi)
    {
        this.config = config;
        this.saveConfig = saveConfig;
        ComposeOverlay();
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        exactChunkRenderer ??= ExactChunkRendererAdapter.TryCreate(capi);
        centerX = capi.World.Player.Entity.Pos.X;
        centerY = capi.World.Player.Entity.Pos.Y;
        centerZ = capi.World.Player.Entity.Pos.Z;
        FitLoadedTerrain();
        SyncSettingsControls();
        capi.Logger.Notification("[ModernAtlas] Opened independent 3D atlas GUI.");
    }

    public override void OnRenderGUI(float deltaTime)
    {
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
        string performanceStatus = config.PerformanceMode ? "performance on" : "quality mode";
        string animationStatus = !config.AnimationsEnabled
            ? "animations off"
            : config.PerformanceMode ? "animations reduced" : "animations on";
        string multiplayerStatus = capi.IsSinglePlayer ? "singleplayer controls" : "multiplayer safe limits locked";
        string status = $"Radius {EffectiveRadius} blocks ({EffectiveRadius * 2}×{EffectiveRadius * 2}) • {fogStatus} • {performanceStatus} • {animationStatus} • {multiplayerStatus} • {rendererStatus} • no distant chunk requests";
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
        }
        if (args.Button == EnumMouseButton.Right) rightDragging = false;
        args.Handled = true;
    }

    public override void OnMouseMove(MouseEvent args)
    {
        overlay?.OnMouseMove(args);
        if (args.Handled) return;

        if (rightDragging)
        {
            yawDegrees = NormalizeDegrees(yawDegrees + args.DeltaX * 0.42f);
            pitchDegrees = Math.Clamp(pitchDegrees - args.DeltaY * 0.32f, 18, 86);
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
            pitchDegrees = Math.Clamp(pitchDegrees + 4, 18, 86);
            InvalidateFogTexture();
            args.Handled = true;
        }
        else if (args.KeyCode == (int)GlKeys.F)
        {
            pitchDegrees = Math.Clamp(pitchDegrees - 4, 18, 86);
            InvalidateFogTexture();
            args.Handled = true;
        }
        else if (args.KeyCode == (int)GlKeys.PageUp)
        {
            ChangeRadius(1);
            args.Handled = true;
        }
        else if (args.KeyCode == (int)GlKeys.PageDown)
        {
            ChangeRadius(-1);
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
            config.RadiusBlocks = ModernAtlasConfig.DefaultRadius;
            config.FogEnabled = true;
            config.PerformanceMode = true;
            config.AnimationsEnabled = true;
            saveConfig();
            FitLoadedTerrain();
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
        EndAtlasSession();
        base.OnGuiClosed();
    }

    public override void Dispose()
    {
        EndAtlasSession();
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
        string[] radiusValues = Array.ConvertAll(RadiusSteps, value => value.ToString());
        string[] radiusNames = Array.ConvertAll(
            RadiusSteps,
            value => $"{value} block radius ({value * 2}×{value * 2})"
        );
        int selectedRadius = Math.Max(
            0,
            Array.FindIndex(RadiusSteps, value => value == EffectiveRadius)
        );

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
                capi.IsSinglePlayer ? "Visible radius" : "Server-safe radius (locked)",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(settingsX + 20, 62, 260, 24)
            )
            .AddDropDown(
                radiusValues,
                radiusNames,
                selectedRadius,
                OnRadiusSelected,
                ElementBounds.Fixed(settingsX + 20, 88, 260, 34),
                "radius"
            )
            .AddStaticText(
                capi.IsSinglePlayer ? "Unexplored fog" : "Server fog (locked)",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(settingsX + 20, 136, 180, 28)
            )
            .AddSwitch(
                OnFogToggled,
                ElementBounds.Fixed(settingsX + 234, 132, 46, 30),
                "fog",
                24,
                4
            )
            .AddStaticText(
                "Performance mode",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(settingsX + 20, 172, 180, 28)
            )
            .AddSwitch(
                OnPerformanceToggled,
                ElementBounds.Fixed(settingsX + 234, 168, 46, 30),
                "performance",
                24,
                4
            )
            .AddStaticText(
                "Animations",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(settingsX + 20, 208, 180, 28)
            )
            .AddSwitch(
                OnAnimationsToggled,
                ElementBounds.Fixed(settingsX + 234, 204, 46, 30),
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
        float farPlane = Math.Max(2000, EffectiveRadius * 6);
        Mat4f.Ortho(projection, -zoom * aspect, zoom * aspect, -zoom, zoom, 0.1f, farPlane);
        float yaw = yawDegrees * GameMath.DEG2RAD;
        float pitch = pitchDegrees * GameMath.DEG2RAD;

        float renderDeltaTime = config.AnimationsEnabled ? deltaTime : 0;
        bool rendered = exactChunkRenderer?.Render(
            renderDeltaTime,
            projection,
            centerX,
            centerY,
            centerZ,
            yaw,
            pitch,
            EffectiveRadius,
            EffectiveFogEnabled
        ) == true;
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
        return rendered;
    }

    private void ResetView()
    {
        centerX = capi.World.Player.Entity.Pos.X;
        centerY = capi.World.Player.Entity.Pos.Y;
        centerZ = capi.World.Player.Entity.Pos.Z;
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
        // Exact chunk meshes are live and do not require an atlas rebuild.
    }

    private void FitLoadedTerrain()
    {
        float radius = EffectiveRadius;
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

    private void ChangeRadius(int direction)
    {
        if (!capi.IsSinglePlayer) return;
        int currentIndex = Array.FindIndex(RadiusSteps, value => value >= config.RadiusBlocks);
        if (currentIndex < 0) currentIndex = RadiusSteps.Length - 1;
        int nextIndex = Math.Clamp(currentIndex + direction, 0, RadiusSteps.Length - 1);
        config.RadiusBlocks = RadiusSteps[nextIndex];
        BeginRadiusSafetyWindow();
        FitLoadedTerrain();
        SyncSettingsControls();
    }

    private void OnRadiusSelected(string value, bool selected)
    {
        if (!capi.IsSinglePlayer) return;
        if (!selected || !int.TryParse(value, out int radius)) return;
        config.RadiusBlocks = Math.Clamp(
            radius,
            ModernAtlasConfig.MinimumRadius,
            ModernAtlasConfig.MaximumRadius
        );
        BeginRadiusSafetyWindow();
        FitLoadedTerrain();
    }

    private void BeginRadiusSafetyWindow()
    {
        config.AtlasSessionInProgress = true;
        saveConfig();
        int generation = ++radiusSafetyGeneration;
        capi.Event.RegisterCallback(_ =>
        {
            if (generation != radiusSafetyGeneration) return;
            config.AtlasSessionInProgress = false;
            saveConfig();
        }, 15000);
    }

    private void OnFogToggled(bool enabled)
    {
        if (!capi.IsSinglePlayer) return;
        config.FogEnabled = enabled;
        InvalidateFogTexture();
        saveConfig();
    }

    private void OnPerformanceToggled(bool enabled)
    {
        config.PerformanceMode = enabled;
        saveConfig();
    }

    private void OnAnimationsToggled(bool enabled)
    {
        config.AnimationsEnabled = enabled;
        saveConfig();
    }

    private void SyncSettingsControls()
    {
        if (overlay == null) return;
        int radiusIndex = Math.Max(
            0,
            Array.FindIndex(RadiusSteps, value => value == EffectiveRadius)
        );
        overlay.GetDropDown("radius")?.SetSelectedIndex(radiusIndex);
        overlay.GetSwitch("fog")?.SetValue(EffectiveFogEnabled);
        overlay.GetDropDown("radius").Enabled = capi.IsSinglePlayer;
        overlay.GetSwitch("fog").Enabled = capi.IsSinglePlayer;
        overlay.GetSwitch("performance")?.SetValue(config.PerformanceMode);
        overlay.GetSwitch("animations")?.SetValue(config.AnimationsEnabled);
    }

    private void RenderFogMask()
    {
        if (!EffectiveFogEnabled) return;

        int width = capi.Render.FrameWidth;
        int height = capi.Render.FrameHeight;
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
            capi.Render.Render2DTexture(fogTexture.TextureId, 0, 0, width, height, 40);
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

        double radiusX = height * EffectiveRadius / (2.0 * zoom);
        double pitchRadians = pitchDegrees * GameMath.DEG2RAD;
        double radiusY = radiusX * Math.Max(0.12, Math.Sin(pitchRadians));
        double reliefAllowance = Math.Min(192, EffectiveRadius * 0.3);
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

        // Keep the complete configured radius clear. The feather starts only
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

    private void EndAtlasSession()
    {
        if (!config.AtlasSessionInProgress) return;
        config.AtlasSessionInProgress = false;
        saveConfig();
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
