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
    private static readonly int[] RadiusSteps = { 250, 500, 750, 1000, 1500, 2000, 3000, 5000 };

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
        config.AtlasSessionInProgress = true;
        saveConfig();
        FitLoadedTerrain();
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
        string fogStatus = config.FogEnabled ? "fog on" : "fog off";
        string performanceStatus = config.PerformanceMode ? "performance on" : "quality mode";
        string animationStatus = !config.AnimationsEnabled
            ? "animations off"
            : config.PerformanceMode ? "animations reduced" : "animations on";
        string status = $"Radius {config.RadiusBlocks} blocks ({config.RadiusBlocks * 2}×{config.RadiusBlocks * 2}) • {fogStatus} • {performanceStatus} • {animationStatus} • {rendererStatus} • no distant chunk requests";
        overlay?.GetDynamicText("status").SetNewText(status);
        overlay?.Render(deltaTime);
    }

    public override void OnMouseDown(MouseEvent args)
    {
        base.OnMouseDown(args);
        if (args.Handled) return;

        if (args.Button == EnumMouseButton.Left) leftDragging = true;
        if (args.Button == EnumMouseButton.Right) rightDragging = true;
        if (args.Button == EnumMouseButton.Middle) ResetView();
        args.Handled = true;
    }

    public override void OnMouseUp(MouseEvent args)
    {
        base.OnMouseUp(args);
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
        base.OnMouseMove(args);
        if (args.Handled) return;

        if (rightDragging)
        {
            yawDegrees = NormalizeDegrees(yawDegrees + args.DeltaX * 0.42f);
            pitchDegrees = Math.Clamp(pitchDegrees - args.DeltaY * 0.32f, 18, 86);
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
            centerX -= (args.DeltaX * rightX - args.DeltaY * forwardX) * worldPerPixel;
            centerZ -= (args.DeltaX * rightZ - args.DeltaY * forwardZ) * worldPerPixel;
            args.Handled = true;
        }

        // The atlas covers the entire screen. Do not leak hover interaction to
        // hotbar slots, creative inventory elements or dialogs underneath it.
        args.Handled = true;
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        base.OnMouseWheel(args);
        if (args.IsHandled) return;

        float wheel = args.deltaPrecise != 0 ? args.deltaPrecise : args.delta;
        zoom = Math.Clamp(zoom * MathF.Pow(0.84f, wheel), 8, 6000);
        InvalidateFogTexture();
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
            args.Handled = true;
        }
        else if (args.KeyCode == (int)GlKeys.F)
        {
            pitchDegrees = Math.Clamp(pitchDegrees - 4, 18, 86);
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
        string[] radiusValues = Array.ConvertAll(RadiusSteps, value => value.ToString());
        string[] radiusNames = Array.ConvertAll(
            RadiusSteps,
            value => $"{value} block radius ({value * 2}×{value * 2})"
        );
        int selectedRadius = Math.Max(
            0,
            Array.FindIndex(RadiusSteps, value => value == config.RadiusBlocks)
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
                ElementBounds.FixedOffseted(EnumDialogArea.RightTop, -40, 30, 260, 30)
            )
            .AddStaticText(
                "Visible radius",
                CairoFont.WhiteDetailText(),
                ElementBounds.FixedOffseted(EnumDialogArea.RightTop, -40, 62, 260, 24)
            )
            .AddDropDown(
                radiusValues,
                radiusNames,
                selectedRadius,
                OnRadiusSelected,
                ElementBounds.FixedOffseted(EnumDialogArea.RightTop, -40, 88, 260, 34),
                "radius"
            )
            .AddStaticText(
                "Unexplored fog",
                CairoFont.WhiteDetailText(),
                ElementBounds.FixedOffseted(EnumDialogArea.RightTop, -120, 136, 180, 28)
            )
            .AddSwitch(
                OnFogToggled,
                ElementBounds.FixedOffseted(EnumDialogArea.RightTop, -40, 132, 46, 30),
                "fog",
                24,
                4
            )
            .AddStaticText(
                "Performance mode",
                CairoFont.WhiteDetailText(),
                ElementBounds.FixedOffseted(EnumDialogArea.RightTop, -120, 172, 180, 28)
            )
            .AddSwitch(
                OnPerformanceToggled,
                ElementBounds.FixedOffseted(EnumDialogArea.RightTop, -40, 168, 46, 30),
                "performance",
                24,
                4
            )
            .AddStaticText(
                "Animations",
                CairoFont.WhiteDetailText(),
                ElementBounds.FixedOffseted(EnumDialogArea.RightTop, -120, 208, 180, 28)
            )
            .AddSwitch(
                OnAnimationsToggled,
                ElementBounds.FixedOffseted(EnumDialogArea.RightTop, -40, 204, 46, 30),
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
        float farPlane = Math.Max(2000, config.RadiusBlocks * 6);
        Mat4f.Ortho(projection, -zoom * aspect, zoom * aspect, -zoom, zoom, 0.1f, farPlane);
        float yaw = yawDegrees * GameMath.DEG2RAD;
        float pitch = pitchDegrees * GameMath.DEG2RAD;

        float renderDeltaTime = config.AnimationsEnabled && !config.PerformanceMode
            ? deltaTime
            : 0;
        bool rendered = exactChunkRenderer?.Render(
            renderDeltaTime,
            projection,
            centerX,
            centerY,
            centerZ,
            yaw,
            pitch,
            config.RadiusBlocks
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
        MaybeRecenterScene();
        args.Handled = true;
    }

    private void MaybeRecenterScene()
    {
        // Exact chunk meshes are live and do not require an atlas rebuild.
    }

    private void FitLoadedTerrain()
    {
        float radius = config.RadiusBlocks;
        float aspect = capi.Render.FrameWidth / (float)Math.Max(1, capi.Render.FrameHeight);
        float pitch = pitchDegrees * GameMath.DEG2RAD;

        // A circular radius projects to an ellipse when the camera tilts. Add
        // vertical headroom for trees, buildings and hills so neither the top
        // nor bottom edge is clipped at low camera angles.
        float horizontalFit = radius / Math.Max(0.5f, aspect);
        float verticalFit = radius * Math.Abs(MathF.Sin(pitch))
            + Math.Min(192, radius * 0.3f) * Math.Abs(MathF.Cos(pitch));
        zoom = Math.Clamp(Math.Max(horizontalFit, verticalFit) * 1.08f, 80, 6000);
        InvalidateFogTexture();
    }

    private void ChangeRadius(int direction)
    {
        int currentIndex = Array.FindIndex(RadiusSteps, value => value >= config.RadiusBlocks);
        if (currentIndex < 0) currentIndex = RadiusSteps.Length - 1;
        int nextIndex = Math.Clamp(currentIndex + direction, 0, RadiusSteps.Length - 1);
        config.RadiusBlocks = RadiusSteps[nextIndex];
        saveConfig();
        FitLoadedTerrain();
        SyncSettingsControls();
    }

    private void OnRadiusSelected(string value, bool selected)
    {
        if (!selected || !int.TryParse(value, out int radius)) return;
        config.RadiusBlocks = Math.Clamp(
            radius,
            ModernAtlasConfig.MinimumRadius,
            ModernAtlasConfig.MaximumRadius
        );
        saveConfig();
        FitLoadedTerrain();
    }

    private void OnFogToggled(bool enabled)
    {
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
            Array.FindIndex(RadiusSteps, value => value == config.RadiusBlocks)
        );
        overlay.GetDropDown("radius")?.SetSelectedIndex(radiusIndex);
        overlay.GetSwitch("fog")?.SetValue(config.FogEnabled);
        overlay.GetSwitch("performance")?.SetValue(config.PerformanceMode);
        overlay.GetSwitch("animations")?.SetValue(config.AnimationsEnabled);
    }

    private void RenderFogMask()
    {
        if (!config.FogEnabled) return;

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

        double radiusX = height * config.RadiusBlocks / (2.0 * zoom);
        double pitchRadians = pitchDegrees * GameMath.DEG2RAD;
        double radiusY = radiusX * Math.Max(0.12, Math.Sin(pitchRadians));
        double reliefAllowance = Math.Min(192, config.RadiusBlocks * 0.3);
        radiusY += height * reliefAllowance * Math.Abs(Math.Cos(pitchRadians)) / (2.0 * zoom);
        double centerScreenX = width / 2.0;
        double centerScreenY = height / 2.0;

        // Concentric translucent ellipses form a soft fog bank without any
        // world sampling. The fully clear center is the configured radius.
        const int featherSteps = 18;
        for (int step = featherSteps; step >= 0; step--)
        {
            double scale = 0.82 + 0.18 * step / featherSteps;
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
        context.Scale(radiusX * 0.82, radiusY * 0.82);
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
        args.Handled = true;
    }

    private static float NormalizeDegrees(float value)
    {
        value %= 360;
        return value < 0 ? value + 360 : value;
    }
}
