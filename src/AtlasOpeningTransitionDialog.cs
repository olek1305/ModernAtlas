using System;
using System.Diagnostics;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// A short atlas-opening scene rendered over the live world. The player bows
/// their view, raises and unfolds a procedural scroll, then follows its light
/// into the independent atlas framebuffer.
/// </summary>
internal sealed class AtlasOpeningTransitionDialog : GuiDialog
{
    private const float BowPhaseEndSeconds = 0.48f;
    private const float UnfoldPhaseEndSeconds = 1.48f;
    private const float LightPhaseStartSeconds = 1.82f;
    private const float TotalDurationSeconds = 2.42f;
    private const float PreparationTimeoutSeconds = 8f;
    private const string BowAnimationCode = "modernatlas-opening-bow";
    private const string UnfoldAnimationCode = "modernatlas-opening-unfold";
    private const string SmokeScreenshotEnvironmentVariable =
        "MODERNATLAS_SMOKE_SCREENSHOT";

    private readonly Func<bool> prepareAtlasResources;
    private LoadedTexture scrollTexture;
    private LoadedTexture glowTexture;
    private LoadedTexture solidTexture;

    private Action<bool>? completion;
    private long startedTimestamp;
    private bool automatedTest;
    private bool finishing;
    private bool resourcesReady;
    private bool cameraCaptured;
    private bool cameraMoved;
    private bool bowAnimationStarted;
    private bool unfoldAnimationStarted;
    private bool unfoldPhaseStarted;
    private bool clothSoundPlayed;
    private bool swooshSoundAttempted;
    private bool swooshSoundPlayed;
    private bool automatedScreenshotHandled;
    private bool automatedScreenshotPassed;
    private float savedMousePitch;
    private float savedEntityPitch;
    private float savedHeadPitch;
    private bool disposed;

    public override string ToggleKeyCombinationCode => "";
    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override double DrawOrder => 0.99;
    public override double InputOrder => 0.01;
    public override bool PrefersUngrabbedMouse => false;
    public override bool DisableMouseGrab => false;

    public AtlasOpeningTransitionDialog(
        ICoreClientAPI capi,
        Func<bool> prepareAtlasResources
    ) : base(capi)
    {
        this.prepareAtlasResources = prepareAtlasResources;
        scrollTexture = new LoadedTexture(capi);
        glowTexture = new LoadedTexture(capi);
        solidTexture = new LoadedTexture(capi);
    }

    public bool Begin(bool automated, Action<bool> onCompleted)
    {
        if (disposed || IsOpened()) return false;

        automatedTest = automated;
        completion = onCompleted;
        finishing = false;
        resourcesReady = false;
        cameraCaptured = false;
        cameraMoved = false;
        bowAnimationStarted = false;
        unfoldAnimationStarted = false;
        unfoldPhaseStarted = false;
        clothSoundPlayed = false;
        swooshSoundAttempted = false;
        swooshSoundPlayed = false;
        automatedScreenshotHandled = false;
        automatedScreenshotPassed = false;
        return TryOpen();
    }

    public void SkipToAtlas()
    {
        if (!IsOpened() || finishing) return;
        resourcesReady = prepareAtlasResources();
        QueueFinish(!automatedTest || ValidateAutomatedScene());
    }

    public void CancelWithoutOpening()
    {
        completion = null;
        finishing = true;
        RestorePlayerPresentation();
        if (IsOpened()) TryClose();
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        EnsureTextures();
        CapturePlayerPresentation();
        startedTimestamp = Stopwatch.GetTimestamp();
        bowAnimationStarted = StartClientAnimation(
            "bow-fp",
            BowAnimationCode,
            1.18f
        );
        clothSoundPlayed = PlayLocalSound("sounds/block/cloth", 0.55f);
        capi.Logger.Notification(
            "[ModernAtlas] Started the scroll-unfold opening transition."
        );
    }

    public override void OnGuiClosed()
    {
        RestorePlayerPresentation();
        base.OnGuiClosed();
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (finishing) return;

        float elapsed = (float)Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds;
        resourcesReady = prepareAtlasResources();
        UpdatePlayerPresentation(elapsed);
        AdvanceAnimationAndSound(elapsed);
        RenderTransition(elapsed);
        CaptureAutomatedScreenshot(elapsed);

        if (elapsed >= TotalDurationSeconds && resourcesReady)
        {
            QueueFinish(!automatedTest || ValidateAutomatedScene());
        }
        else if (elapsed >= PreparationTimeoutSeconds)
        {
            capi.Logger.Error(
                "[ModernAtlas] The opening transition timed out while preparing atlas resources."
            );
            QueueFinish(false);
        }
    }

    public override bool CaptureAllInputs() => true;
    public override bool CaptureRawMouse() => true;

    public override bool OnEscapePressed()
    {
        CancelWithoutOpening();
        return true;
    }

    public override void OnKeyDown(KeyEvent args)
    {
        if (args.KeyCode == (int)GlKeys.Escape)
        {
            CancelWithoutOpening();
        }
        else if (args.KeyCode == (int)GlKeys.G)
        {
            SkipToAtlas();
        }
        args.Handled = true;
    }

    public override void OnKeyPress(KeyEvent args) => args.Handled = true;

    public override void OnMouseDown(MouseEvent args) => args.Handled = true;
    public override void OnMouseUp(MouseEvent args) => args.Handled = true;
    public override void OnMouseMove(MouseEvent args) => args.Handled = true;

    public override void OnMouseWheel(MouseWheelEventArgs args) => args.SetHandled();

    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CancelWithoutOpening();
        scrollTexture.Dispose();
        glowTexture.Dispose();
        solidTexture.Dispose();
        base.Dispose();
    }

    private void CapturePlayerPresentation()
    {
        try
        {
            EntityPlayer player = capi.World.Player.Entity;
            savedMousePitch = capi.Input.MousePitch;
            savedEntityPitch = player.Pos.Pitch;
            savedHeadPitch = player.Pos.HeadPitch;
            cameraCaptured = true;
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not capture the player camera for the opening transition: {0}",
                exception.Message
            );
        }
    }

    private void UpdatePlayerPresentation(float elapsed)
    {
        if (!cameraCaptured) return;

        float bowAmount;
        if (elapsed <= BowPhaseEndSeconds)
        {
            bowAmount = SmoothStep(elapsed / BowPhaseEndSeconds);
        }
        else
        {
            bowAmount = 1f - SmoothStep(
                (elapsed - BowPhaseEndSeconds) / 0.72f
            );
        }
        bowAmount = Math.Clamp(bowAmount, 0f, 1f);
        float pitchOffset = bowAmount * 0.44f;

        try
        {
            EntityPlayer player = capi.World.Player.Entity;
            capi.Input.MousePitch = savedMousePitch + pitchOffset;
            player.Pos.Pitch = savedEntityPitch + pitchOffset;
            player.Pos.HeadPitch = savedHeadPitch + pitchOffset;
            cameraMoved |= bowAmount >= 0.85f;
        }
        catch
        {
            RestorePlayerPresentation();
        }
    }

    private void AdvanceAnimationAndSound(float elapsed)
    {
        if (!unfoldPhaseStarted && elapsed >= BowPhaseEndSeconds)
        {
            unfoldPhaseStarted = true;
            StopClientAnimation(BowAnimationCode);
            unfoldAnimationStarted = StartClientAnimation(
                "interactstaticlong-fp",
                UnfoldAnimationCode,
                1.05f
            );
        }

        if (!swooshSoundAttempted && elapsed >= LightPhaseStartSeconds)
        {
            swooshSoundAttempted = true;
            swooshSoundPlayed = PlayLocalSound("sounds/effect/swoosh", 0.92f);
        }
    }

    private void RenderTransition(float elapsed)
    {
        IRenderAPI render = capi.Render;
        float frameWidth = render.FrameWidth;
        float frameHeight = render.FrameHeight;
        if (frameWidth <= 0 || frameHeight <= 0) return;

        render.CurrentActiveShader?.Stop();
        render.GetEngineShader(EnumShaderProgram.Gui).Use();
        render.GLDepthMask(false);
        render.GLDisableDepthTest();
        render.GlToggleBlend(true, EnumBlendMode.Standard);

        float bowProgress = SmoothStep(elapsed / BowPhaseEndSeconds);
        float unfoldProgress = SmoothStep(
            (elapsed - BowPhaseEndSeconds)
                / (UnfoldPhaseEndSeconds - BowPhaseEndSeconds)
        );
        float lightProgress = SmoothStep(
            (elapsed - LightPhaseStartSeconds)
                / (TotalDurationSeconds - LightPhaseStartSeconds)
        );

        float dimAlpha = 0.10f + bowProgress * 0.28f + lightProgress * 0.28f;
        RenderSolid(0, 0, frameWidth, frameHeight, new Vec4f(0.01f, 0.02f, 0.03f, dimAlpha));

        float targetWidth = Math.Min(frameWidth * 0.58f, 720f * (float)RuntimeEnv.GUIScale);
        float targetHeight = Math.Min(frameHeight * 0.64f, targetWidth * 0.76f);
        float pocketWidth = Math.Max(62f, targetWidth * 0.13f);
        float pocketHeight = Math.Max(20f, targetHeight * 0.08f);
        float centerX = frameWidth * 0.5f;
        float centerY = frameHeight * 0.50f;
        float pocketX = frameWidth * 0.78f;
        float pocketY = frameHeight * 0.86f;

        float raisedProgress = EaseOutCubic(
            Math.Clamp(elapsed / BowPhaseEndSeconds, 0f, 1f)
        );
        float width = Lerp(pocketWidth, targetWidth, unfoldProgress);
        float height = Lerp(pocketHeight, targetHeight, unfoldProgress);
        float scrollCenterX = Lerp(pocketX, centerX, raisedProgress);
        float scrollCenterY = Lerp(pocketY, centerY, raisedProgress);

        float zoomScale = 1f + EaseInCubic(lightProgress) * 3.8f;
        width *= zoomScale;
        height *= zoomScale;
        float scrollX = scrollCenterX - width * 0.5f;
        float scrollY = scrollCenterY - height * 0.5f;

        float glowSize = Math.Max(width, height) * (1.20f + lightProgress * 0.65f);
        float glowAlpha = Math.Clamp(0.16f + unfoldProgress * 0.34f + lightProgress * 0.50f, 0f, 1f);
        render.Render2DTexturePremultipliedAlpha(
            glowTexture.TextureId,
            scrollCenterX - glowSize * 0.5f,
            scrollCenterY - glowSize * 0.5f,
            glowSize,
            glowSize,
            92,
            new Vec4f(0.70f, 0.90f, 1f, glowAlpha)
        );
        render.Render2DTexturePremultipliedAlpha(
            scrollTexture.TextureId,
            scrollX,
            scrollY,
            width,
            height,
            94,
            new Vec4f(1f, 1f, 1f, 1f)
        );

        if (lightProgress > 0)
        {
            float flashAlpha = Math.Clamp(
                MathF.Pow(lightProgress, 2.3f) * 1.12f,
                0f,
                resourcesReady ? 1f : 0.82f
            );
            RenderSolid(
                0,
                0,
                frameWidth,
                frameHeight,
                new Vec4f(0.86f, 0.95f, 1f, flashAlpha)
            );
        }
    }

    private void RenderSolid(
        float x,
        float y,
        float width,
        float height,
        Vec4f color
    )
    {
        capi.Render.Render2DTexture(
            solidTexture.TextureId,
            x,
            y,
            width,
            height,
            90,
            color
        );
    }

    private bool StartClientAnimation(
        string sourceCode,
        string transitionCode,
        float speed
    )
    {
        try
        {
            EntityPlayer player = capi.World.Player.Entity;
            if (!player.Properties.Client.AnimationsByMetaCode.TryGetValue(
                sourceCode,
                out AnimationMetaData? source
            ))
            {
                return false;
            }

            AnimationMetaData animation = source.Clone();
            animation.Code = transitionCode;
            animation.AnimationSpeed = speed;
            animation.ClientSide = true;
            animation.EaseInSpeed = Math.Max(animation.EaseInSpeed, 8f);
            animation.EaseOutSpeed = Math.Max(animation.EaseOutSpeed, 8f);
            animation.Init();
            return player.AnimManager.StartAnimation(animation);
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not start opening animation {0}: {1}",
                sourceCode,
                exception.Message
            );
            return false;
        }
    }

    private void StopClientAnimation(string code)
    {
        try
        {
            capi.World.Player.Entity.AnimManager.StopAnimation(code);
        }
        catch
        {
            // World teardown can invalidate the player while the scene closes.
        }
    }

    private void RestorePlayerPresentation()
    {
        StopClientAnimation(BowAnimationCode);
        StopClientAnimation(UnfoldAnimationCode);
        if (!cameraCaptured) return;

        try
        {
            EntityPlayer player = capi.World.Player.Entity;
            capi.Input.MousePitch = savedMousePitch;
            player.Pos.Pitch = savedEntityPitch;
            player.Pos.HeadPitch = savedHeadPitch;
        }
        catch
        {
            // The player may already be gone during world shutdown.
        }
        cameraCaptured = false;
    }

    private bool PlayLocalSound(string path, float volume)
    {
        try
        {
            var soundLocation = new AssetLocation("game", path);
            if (capi.Assets.TryGet(new AssetLocation("game", $"{path}.ogg")) == null)
            {
                capi.Logger.Warning(
                    "[ModernAtlas] Opening-transition sound asset is unavailable: {0}",
                    soundLocation
                );
                return false;
            }
            capi.World.PlaySoundFor(
                soundLocation,
                capi.World.Player,
                false,
                16,
                volume
            );
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Debug(
                "[ModernAtlas] Opening-transition sound {0} was unavailable: {1}",
                path,
                exception.Message
            );
            return false;
        }
    }

    private void QueueFinish(bool passed)
    {
        if (finishing) return;
        finishing = true;
        RestorePlayerPresentation();

        if (automatedTest)
        {
            if (passed)
            {
                capi.Logger.Notification(
                    "[ModernAtlas] AUTOMATED OPENING TRANSITION CHECK PASSED: camera bow, client-side hand animation, scroll unfold, sound, light zoom and atlas preparation completed."
                );
            }
            else
            {
                capi.Logger.Error(
                    "[ModernAtlas] AUTOMATED OPENING TRANSITION CHECK FAILED: bow={0}, hands={1}/{2}, sounds={3}/{4}, screenshot={5}, resources={6}.",
                    cameraMoved,
                    bowAnimationStarted,
                    unfoldAnimationStarted,
                    clothSoundPlayed,
                    swooshSoundPlayed,
                    automatedScreenshotPassed,
                    resourcesReady
                );
            }
        }

        Action<bool>? callback = completion;
        completion = null;
        capi.Event.EnqueueMainThreadTask(
            () =>
            {
                if (IsOpened()) TryClose();
                callback?.Invoke(passed);
            },
            "modernatlas-opening-transition-finish"
        );
    }

    private bool ValidateAutomatedScene()
    {
        bool screenshotRequired = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(SmokeScreenshotEnvironmentVariable)
        );
        return resourcesReady
            && cameraMoved
            && bowAnimationStarted
            && unfoldAnimationStarted
            && clothSoundPlayed
            && swooshSoundPlayed
            && (!screenshotRequired || automatedScreenshotPassed);
    }

    private void CaptureAutomatedScreenshot(float elapsed)
    {
        if (!automatedTest
            || automatedScreenshotHandled
            || elapsed < 1.18f)
        {
            return;
        }

        automatedScreenshotHandled = true;
        string? configuredPath = Environment.GetEnvironmentVariable(
            SmokeScreenshotEnvironmentVariable
        );
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            automatedScreenshotPassed = true;
            return;
        }

        string prefix = configuredPath.EndsWith(
            ".png",
            StringComparison.OrdinalIgnoreCase
        ) ? configuredPath[..^4] : configuredPath;
        string path = $"{prefix}-opening.png";
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
            automatedScreenshotPassed = true;
            capi.Logger.Notification(
                "[ModernAtlas] Saved automated opening-transition screenshot: {0}",
                path
            );
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated opening-transition screenshot failed for {0}: {1}",
                path,
                exception.Message
            );
        }
    }

    private void EnsureTextures()
    {
        if (solidTexture.TextureId <= 0)
        {
            solidTexture.Width = 1;
            solidTexture.Height = 1;
            int[] pixel = { unchecked((int)0xffffffff) };
            capi.Render.LoadOrUpdateTextureFromBgra(
                pixel,
                false,
                1,
                ref solidTexture
            );
        }
        if (scrollTexture.TextureId <= 0) ComposeScrollTexture();
        if (glowTexture.TextureId <= 0) ComposeGlowTexture();
    }

    private void ComposeScrollTexture()
    {
        const int width = 768;
        const int height = 600;
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        AtlasUiStyle.Clear(context);

        AtlasUiStyle.RoundedRectangle(context, 30, 34, 708, 532, 38);
        context.SetSourceRGBA(0.04, 0.025, 0.018, 0.62);
        context.Fill();
        AtlasUiStyle.RoundedRectangle(context, 42, 24, 684, 532, 34);
        context.SetSourceRGBA(0.72, 0.57, 0.33, 1);
        context.FillPreserve();
        context.SetSourceRGBA(1, 0.88, 0.58, 0.72);
        context.LineWidth = 5;
        context.Stroke();

        AtlasUiStyle.RoundedRectangle(context, 58, 50, 652, 480, 24);
        context.SetSourceRGBA(0.13, 0.115, 0.085, 0.96);
        context.FillPreserve();
        context.SetSourceRGBA(0.44, 0.78, 0.80, 0.68);
        context.LineWidth = 3;
        context.Stroke();

        context.SetSourceRGBA(0.30, 0.62, 0.65, 0.18);
        context.LineWidth = 2;
        for (int index = 0; index < 10; index++)
        {
            double inset = 24 + index * 16;
            context.Arc(384, 306, 236 - inset * 0.45, 0.15 + index * 0.21, 5.1 + index * 0.08);
            context.Stroke();
        }

        context.SetSourceRGBA(0.76, 0.92, 0.91, 0.30);
        context.LineWidth = 2;
        for (int x = 122; x <= 646; x += 58)
        {
            context.MoveTo(x, 204);
            context.LineTo(x, 476);
        }
        for (int y = 220; y <= 474; y += 46)
        {
            context.MoveTo(104, y);
            context.LineTo(664, y);
        }
        context.Stroke();

        DrawCompassRose(context, 384, 332, 88);
        CairoFont titleFont = CairoFont.WhiteSmallishText()
            .WithFontSize(34)
            .WithWeight(FontWeight.Bold)
            .WithColor(new[] { 0.88, 0.96, 0.95, 1.0 });
        AtlasUiStyle.DrawCenteredText(
            context,
            titleFont,
            "MODERN ATLAS",
            width,
            height,
            0,
            -208
        );
        CairoFont detailFont = CairoFont.WhiteDetailText()
            .WithFontSize(13)
            .WithColor(new[] { 0.52, 0.80, 0.80, 1.0 });
        AtlasUiStyle.DrawCenteredText(
            context,
            detailFont,
            "LIVE TERRAIN  •  LOADED WORLD DATA",
            width,
            height,
            0,
            -164
        );

        DrawScrollRoller(context, 20, 50, 44, 480);
        DrawScrollRoller(context, 704, 50, 44, 480);
        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref scrollTexture);
    }

    private void ComposeGlowTexture()
    {
        const int size = 512;
        using ImageSurface surface = new(Format.Argb32, size, size);
        using Context context = new(surface);
        AtlasUiStyle.Clear(context);

        for (int ring = 13; ring >= 1; ring--)
        {
            double radius = ring * 18.5;
            double alpha = 0.012 + (14 - ring) * 0.003;
            context.SetSourceRGBA(0.34, 0.78, 0.88, alpha);
            context.Arc(size * 0.5, size * 0.5, radius, 0, Math.PI * 2);
            context.Fill();
        }
        context.SetSourceRGBA(0.72, 0.96, 1, 0.30);
        context.LineWidth = 7;
        context.MoveTo(38, 426);
        context.LineTo(472, 80);
        context.MoveTo(72, 486);
        context.LineTo(490, 154);
        context.Stroke();
        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref glowTexture);
    }

    private static void DrawScrollRoller(
        Context context,
        double x,
        double y,
        double width,
        double height
    )
    {
        AtlasUiStyle.RoundedRectangle(context, x, y, width, height, width * 0.48);
        context.SetSourceRGBA(0.31, 0.20, 0.10, 1);
        context.FillPreserve();
        context.SetSourceRGBA(0.95, 0.76, 0.39, 0.72);
        context.LineWidth = 3;
        context.Stroke();
        context.Arc(x + width * 0.5, y + 18, width * 0.32, 0, Math.PI * 2);
        context.Arc(x + width * 0.5, y + height - 18, width * 0.32, 0, Math.PI * 2);
        context.SetSourceRGBA(0.08, 0.045, 0.025, 1);
        context.Fill();
    }

    private static void DrawCompassRose(
        Context context,
        double centerX,
        double centerY,
        double radius
    )
    {
        context.SetSourceRGBA(0.56, 0.90, 0.88, 0.88);
        context.LineWidth = 4;
        context.Arc(centerX, centerY, radius, 0, Math.PI * 2);
        context.Stroke();
        context.SetSourceRGBA(0.82, 0.97, 0.95, 0.94);
        context.MoveTo(centerX, centerY - radius * 0.88);
        context.LineTo(centerX + radius * 0.20, centerY - radius * 0.12);
        context.LineTo(centerX + radius * 0.78, centerY);
        context.LineTo(centerX + radius * 0.18, centerY + radius * 0.14);
        context.LineTo(centerX, centerY + radius * 0.84);
        context.LineTo(centerX - radius * 0.18, centerY + radius * 0.14);
        context.LineTo(centerX - radius * 0.78, centerY);
        context.LineTo(centerX - radius * 0.20, centerY - radius * 0.12);
        context.ClosePath();
        context.Fill();
        context.SetSourceRGBA(0.10, 0.16, 0.15, 1);
        context.Arc(centerX, centerY, radius * 0.14, 0, Math.PI * 2);
        context.Fill();
    }

    private static float SmoothStep(float value)
    {
        float clamped = Math.Clamp(value, 0f, 1f);
        return clamped * clamped * (3f - 2f * clamped);
    }

    private static float EaseOutCubic(float value)
    {
        float inverse = 1f - Math.Clamp(value, 0f, 1f);
        return 1f - inverse * inverse * inverse;
    }

    private static float EaseInCubic(float value)
    {
        float clamped = Math.Clamp(value, 0f, 1f);
        return clamped * clamped * clamped;
    }

    private static float Lerp(float from, float to, float amount) =>
        from + (to - from) * Math.Clamp(amount, 0f, 1f);
}
