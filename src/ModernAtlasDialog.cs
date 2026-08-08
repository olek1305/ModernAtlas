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
    private static readonly float[] TransparentBlack = { 0f, 0f, 0f, 0f };

    private readonly ModernAtlasScene scene;
    private ExactChunkRendererAdapter? exactChunkRenderer;
    private readonly float[] projection = Mat4f.Create();
    private readonly float[] view = Mat4f.Create();
    private readonly float[] model = Mat4f.Create();

    private GuiComposer? overlay;
    private IShaderProgram? atlasShader;
    private FrameBufferRef? framebuffer;
    private int framebufferWidth;
    private int framebufferHeight;
    private bool leftDragging;
    private bool rightDragging;
    private double centerX;
    private double centerZ;
    private float yawDegrees = 42;
    private float pitchDegrees = 72;
    private float zoom = 38;
    private long lastSceneBeginMilliseconds;
    private bool loggedFirstRender;

    public override string ToggleKeyCombinationCode => "modernatlas-open";
    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override double DrawOrder => 0.82;
    public override double InputOrder => 0.05;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;

    public ModernAtlasDialog(ICoreClientAPI capi) : base(capi)
    {
        scene = new ModernAtlasScene(capi);
        ComposeOverlay();
    }

    public void SetShader(IShaderProgram? shader)
    {
        atlasShader = shader;
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        exactChunkRenderer ??= ExactChunkRendererAdapter.TryCreate(capi);
        centerX = capi.World.Player.Entity.Pos.X;
        centerZ = capi.World.Player.Entity.Pos.Z;
        BeginScene();
        capi.Logger.Notification("[ModernAtlas] Opened independent 3D atlas GUI.");
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (!loggedFirstRender)
        {
            loggedFirstRender = true;
            capi.Logger.Notification("[ModernAtlas] First 3D atlas GUI frame rendered.");
        }
        scene.BuildStep(20);
        if (!scene.IsBuilding
            && !scene.IsReady
            && capi.ElapsedMilliseconds - lastSceneBeginMilliseconds >= 2000)
        {
            BeginScene();
        }
        EnsureFramebuffer();
        if (framebuffer != null)
        {
            RenderScene(framebuffer);
            capi.Render.GetEngineShader(EnumShaderProgram.Gui).Use();
            capi.Render.GlToggleBlend(false);
            capi.Render.Render2DTexture(
                framebuffer.ColorTextureIds[0],
                0,
                capi.Render.FrameHeight,
                capi.Render.FrameWidth,
                -capi.Render.FrameHeight,
                20
            );
            capi.Render.GlToggleBlend(true, EnumBlendMode.Standard);
        }

        string status = scene.IsBuilding
            ? Lang.Get("modernatlas:status-building", (int)(scene.Progress * 100), scene.BlocksAdded)
            : Lang.Get("modernatlas:status-ready", scene.BlocksAdded);
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
        scene.Dispose();
        DestroyFramebuffer();
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

    private void EnsureFramebuffer()
    {
        int width = Math.Max(1, capi.Render.FrameWidth);
        int height = Math.Max(1, capi.Render.FrameHeight);
        if (framebuffer != null && framebufferWidth == width && framebufferHeight == height) return;

        DestroyFramebuffer();
        framebufferWidth = width;
        framebufferHeight = height;
        FramebufferAttrs attributes = new("modernatlas-3d", width, height)
        {
            Attachments = new[]
            {
                new FramebufferAttrsAttachment
                {
                    AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                    Texture = new RawTexture
                    {
                        Width = width,
                        Height = height,
                        PixelInternalFormat = EnumTextureInternalFormat.Rgba8,
                        PixelFormat = EnumTexturePixelFormat.Rgba
                    }
                },
                new FramebufferAttrsAttachment
                {
                    AttachmentType = EnumFramebufferAttachment.DepthAttachment,
                    Texture = new RawTexture
                    {
                        Width = width,
                        Height = height,
                        PixelInternalFormat = EnumTextureInternalFormat.DepthComponent32,
                        PixelFormat = EnumTexturePixelFormat.DepthComponent,
                        MinFilter = EnumTextureFilter.Nearest,
                        MagFilter = EnumTextureFilter.Nearest
                    }
                }
            }
        };
        framebuffer = capi.Render.CreateFrameBuffer(attributes);
    }

    private void RenderScene(FrameBufferRef target)
    {
        IRenderAPI render = capi.Render;
        FrameBufferRef previous = render.CurrentFrameBuffer;
        render.CurrentFrameBuffer = target;
        render.GlViewport(0, 0, target.Width, target.Height);
        render.ClearFrameBuffer(target, new[] { 0.035f, 0.075f, 0.11f, 1f }, true, true);

        MultiTextureMeshRef? mesh = scene.MeshRef;
        if (mesh != null)
        {
            float aspect = target.Width / (float)Math.Max(1, target.Height);
            Mat4f.Ortho(projection, -zoom * aspect, zoom * aspect, -zoom, zoom, 0.1f, 800f);

            float yaw = yawDegrees * GameMath.DEG2RAD;
            float pitch = pitchDegrees * GameMath.DEG2RAD;
            float distance = 180;
            float horizontal = MathF.Cos(pitch) * distance;
            float[] eye =
            {
                MathF.Sin(yaw) * horizontal,
                MathF.Sin(pitch) * distance + scene.VerticalCenter,
                MathF.Cos(yaw) * horizontal
            };
            float[] targetPoint = { 0, scene.VerticalCenter, 0 };
            Mat4f.LookAt(view, eye, targetPoint, new[] { 0f, 1f, 0f });
            Mat4f.Identity(model);
            Mat4f.Translate(
                model,
                model,
                (float)(scene.CenterX - centerX),
                0,
                (float)(scene.CenterZ - centerZ)
            );

            if (atlasShader != null)
            {
                render.GLEnableDepthTest();
                render.GLDepthMask(true);
                // Default block meshes can contain double-sided or non-cube faces
                // and the off-screen projection may invert winding. Match the
                // official loose-block renderer and do not cull atlas faces.
                render.GlDisableCullFace();
                render.GlToggleBlend(false);
                render.CurrentActiveShader?.Stop();

                IShaderProgram shader = atlasShader;
                shader.Use();
                shader.UniformMatrix("projectionMatrix", projection);
                shader.UniformMatrix("viewMatrix", view);
                shader.UniformMatrix("modelMatrix", model);
                render.RenderMultiTextureMesh(mesh, "tex");
                shader.Stop();

                render.GLDepthMask(false);
                render.GLDisableDepthTest();
            }

            // Preserve the portable block image underneath the 1.22.6 engine
            // pass. Some graphics configurations accept the chunk draw call
            // but do not expose its deferred color attachment to this FBO.
            // Clearing depth only lets exact chunk pixels replace the fallback
            // without ever turning the atlas into an empty blue frame.
            render.ClearFrameBuffer(target, TransparentBlack, true, false);
            exactChunkRenderer?.Render(
                0,
                projection,
                centerX,
                scene.WorldVerticalCenter,
                centerZ,
                yaw,
                pitch
            );
        }

        render.CurrentFrameBuffer = previous;
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
    }

    private void DestroyFramebuffer()
    {
        if (framebuffer == null) return;
        capi.Render.DestroyFrameBuffer(framebuffer);
        framebuffer = null;
    }

    private void ResetView()
    {
        centerX = capi.World.Player.Entity.Pos.X;
        centerZ = capi.World.Player.Entity.Pos.Z;
        yawDegrees = 42;
        pitchDegrees = 72;
        zoom = 38;
        BeginScene();
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
        double dx = centerX - scene.CenterX;
        double dz = centerZ - scene.CenterZ;
        if (dx * dx + dz * dz < 12 * 12) return;
        BeginScene();
    }

    private void BeginScene()
    {
        scene.Begin((int)Math.Floor(centerX), (int)Math.Floor(centerZ));
        lastSceneBeginMilliseconds = capi.ElapsedMilliseconds;
        capi.Logger.Debug(
            "[ModernAtlas] Scene capture found {0} loaded block columns.",
            scene.LoadedColumns
        );
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
