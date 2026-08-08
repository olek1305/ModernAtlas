using System;
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
    private static readonly float[] AtlasClearColor = { 0.035f, 0.075f, 0.11f, 1f };

    private ExactChunkRendererAdapter? exactChunkRenderer;
    private readonly float[] projection = Mat4f.Create();

    private GuiComposer? overlay;
    private bool leftDragging;
    private bool rightDragging;
    private double centerX;
    private double centerY;
    private double centerZ;
    private float yawDegrees = 42;
    private float pitchDegrees = 72;
    private float zoom = 38;
    private bool loggedFirstRender;

    public override string ToggleKeyCombinationCode => "modernatlas-open";
    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override double DrawOrder => 0.82;
    public override double InputOrder => 0.05;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;

    public ModernAtlasDialog(ICoreClientAPI capi) : base(capi)
    {
        ComposeOverlay();
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        exactChunkRenderer ??= ExactChunkRendererAdapter.TryCreate(capi);
        centerX = capi.World.Player.Entity.Pos.X;
        centerY = capi.World.Player.Entity.Pos.Y;
        centerZ = capi.World.Player.Entity.Pos.Z;
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
        string status = rendered
            ? "Live world chunks: exact game geometry, materials and lighting"
            : "Exact world renderer unavailable - no substitute materials are shown";
        overlay?.GetDynamicText("status").SetNewText(status);
        overlay?.Render(deltaTime);
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (args.Button == EnumMouseButton.Left) leftDragging = true;
        if (args.Button == EnumMouseButton.Right) rightDragging = true;
        if (args.Button == EnumMouseButton.Middle) ResetView();
        args.Handled = true;
    }

    public override void OnMouseUp(MouseEvent args)
    {
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
        float wheel = args.deltaPrecise != 0 ? args.deltaPrecise : args.delta;
        zoom = Math.Clamp(zoom * MathF.Pow(0.84f, wheel), 8, 180);
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

    public override void Dispose()
    {
        overlay?.Dispose();
        overlay = null;
        base.Dispose();
    }

    private void ComposeOverlay()
    {
        ElementBounds root = ElementBounds.Fill;
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
            .Compose();
    }

    private bool RenderLiveWorld(float deltaTime)
    {
        IRenderAPI render = capi.Render;
        FrameBufferRef target = render.CurrentFrameBuffer;
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
        render.ClearFrameBuffer(target, AtlasClearColor, true, true);

        float aspect = render.FrameWidth / (float)Math.Max(1, render.FrameHeight);
        Mat4f.Ortho(projection, -zoom * aspect, zoom * aspect, -zoom, zoom, 0.1f, 800f);
        float yaw = yawDegrees * GameMath.DEG2RAD;
        float pitch = pitchDegrees * GameMath.DEG2RAD;

        bool rendered = exactChunkRenderer?.Render(
            deltaTime,
            projection,
            centerX,
            centerY,
            centerZ,
            yaw,
            pitch
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
        zoom = 38;
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
