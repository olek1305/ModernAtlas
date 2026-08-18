using System;
using System.Collections.Generic;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// A first-person atlas transition over the live world. The camera remains
/// fixed while first-person forearms built from the loaded Seraph model
/// retrieve, hold and unroll a physical scroll. The opening hotkey cancels the
/// local player's native hand action first, then this scene draws only its two
/// transition-owned arms.
/// </summary>
internal sealed class AtlasOpeningTransitionDialog : GuiDialog, IRenderer
{
    // All local and remote scroll choreography runs against this shared scene
    // clock. Phase values remain deliberately unchanged so their sequencing
    // stays synchronized while the real-time transition is 1 / 1.2 shorter.
    internal const float TransitionSpeed = 1.2f;
    // Values are expressed on the shared 1.2x scene clock. The player sees
    // the complete retrieve-and-unroll motion in about 1.7 seconds.
    private const float ScrollAppearsSeconds = 0.48f;
    private const float HandoffStartSeconds = 0.90f;
    private const float UnrollStartSeconds = 1.08f;
    private const float UnrollEndSeconds = 1.68f;
    private const float LightPhaseStartSeconds = 1.98f;
    private const float TotalDurationSeconds = 2.08f;
    private const float ClosingDurationSeconds =
        LightPhaseStartSeconds - ScrollAppearsSeconds;
    private const float ClosingReleaseRightSeconds =
        LightPhaseStartSeconds - UnrollStartSeconds;
    private const float ClosingStowStartSeconds =
        LightPhaseStartSeconds - HandoffStartSeconds;
    private const float PreparationTimeoutSeconds = 9f;
    private const string BowAnimationCode = "modernatlas-scroll-left-hand";
    private const string HandoffAnimationCode = "modernatlas-opening-handoff";
    private const string HoldAnimationCode = "modernatlas-opening-scroll-hold";
    private const string SmokeScreenshotEnvironmentVariable =
        "MODERNATLAS_SMOKE_SCREENSHOT";
    private const float AutomatedFinalScreenshotMarginSeconds = 0.05f;
    private const long OrdinaryWorldSnapshotIntervalMilliseconds = 500;
    private const int OrdinaryWorldSnapshotDownsampleFactor = 4;

    private readonly Func<bool> prepareAtlasResources;
    private readonly Func<IShaderProgram?> getScrollShader;
    private readonly Action<AtlasScrollPhase> publishAnimationPhase;
    private readonly AtlasSoundController soundController;
    private LoadedTexture solidTexture;
    // The transition never samples the live world while it is visible. It
    // presents this atlas-owned, throttled readback instead. Two textures make
    // a refresh transactional: a failed upload leaves the previous complete
    // snapshot available for the next opening or closing frame.
    private LoadedTexture? ordinaryWorldSnapshotTexture;
    private LoadedTexture? ordinaryWorldSnapshotStagingTexture;
    private long lastOrdinaryWorldSnapshotMilliseconds = long.MinValue;
    private bool loggedOrdinaryWorldSnapshot;
    // Closing is initiated from the atlas GUI before the next ordinary
    // AfterBlit callback. Keep the last complete ordinary-world image locked
    // across that handoff; otherwise the callback can read the one transient
    // frame in which the atlas has just closed but the normal scene has not
    // yet presented its complete lighting/composition.
    private bool ordinaryWorldSnapshotLocked;
    private MeshRef? scrollSheetMesh;
    private MeshRef? scrollCylinderMesh;
    // Retained only by the legacy conversion helper below. All active scroll
    // rendering uses the shared SeraphForearmRenderer.
    private MeshRef? leftSeraphForearmMesh;
    private MeshRef? rightSeraphForearmMesh;
    private int seraphSkinTextureId;
    private Vec4f seraphSkinColor = new(1f, 1f, 1f, 1f);
    private readonly SeraphForearmRenderer seraphForearms;
    private readonly Dictionary<string, RemoteScrollState> remoteScrolls = new();

    private Action<bool>? completion;
    private readonly Func<bool> captureNormalWorldBackground;
    private bool normalWorldBackgroundCaptured;
    private bool closing;
    private long startedTimestamp;
    private bool automatedTest;
    private bool finishing;
    private bool resourcesReady;
    private bool cameraCaptured;
    private bool cameraMoved;
    private bool bowAnimationStarted;
    private bool handoffAnimationStarted;
    private bool holdAnimationStarted;
    private bool handoffPhaseStarted;
    private bool holdPhaseStarted;
    private bool pocketSoundAttempted;
    private bool pocketSoundPlayed;
    private bool unrollSoundAttempted;
    private bool unrollSoundPlayed;
    private bool swooshSoundAttempted;
    private bool swooshSoundPlayed;
    private bool physicalScrollRendered;
    private bool rolledScrollRendered;
    private bool openScrollRendered;
    private bool seraphForearmMeshesReady;
    private bool thirdPersonHandAnchorsReady;
    private bool automatedPocketScreenshotHandled;
    private bool automatedPocketScreenshotPassed;
    private bool automatedImmediateScreenshotHandled;
    private bool automatedImmediateScreenshotPassed;
    private bool automatedHandoffScreenshotHandled;
    private bool automatedHandoffScreenshotPassed;
    private bool automatedScreenshotHandled;
    private bool automatedScreenshotPassed;
    private float savedMousePitch;
    private float savedMouseYaw;
    private float savedCameraPitch;
    private float savedCameraYaw;
    private float savedCameraRoll;
    private float savedEntityPitch;
    private float savedEntityYaw;
    private float savedEntityRoll;
    private float savedBodyYaw;
    private float savedHeadPitch;
    private float savedHeadYaw;
    private EntityAgent? heldItemAgent;
    private ItemSlot? savedLeftHandItemSlot;
    private ItemSlot? savedRightHandItemSlot;
    private DummySlot? hiddenLeftHandItemSlot;
    private DummySlot? hiddenRightHandItemSlot;
    private bool heldItemSlotsSuppressed;
    private bool heldItemsRestored;
    private bool cameraRestored;
    private bool disposed;
    private float[]? localPerspectiveProjection;

    public override string ToggleKeyCombinationCode => "";
    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override double DrawOrder => 0.99;
    public override double InputOrder => 0.01;
    public override bool PrefersUngrabbedMouse => false;
    public override bool DisableMouseGrab => false;
    // Draw immediately before the normal entity pass. The transition-owned
    // arms and scroll share one world-depth buffer and remain attached.
    public double RenderOrder => 0.39;
    public int RenderRange => int.MaxValue;
    public bool IsClosing => closing;

    public void SetRemoteAnimation(string playerUid, AtlasScrollPhase phase)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return;
        if (phase == AtlasScrollPhase.Stop)
        {
            remoteScrolls.Remove(playerUid);
            return;
        }
        remoteScrolls[playerUid] = new RemoteScrollState(
            phase,
            Stopwatch.GetTimestamp()
        );
    }

    public void ClearRemoteAnimations() => remoteScrolls.Clear();

    public AtlasOpeningTransitionDialog(
        ICoreClientAPI capi,
        Func<bool> captureNormalWorldBackground,
        Func<bool> prepareAtlasResources,
        Func<IShaderProgram?> getScrollShader,
        Action<AtlasScrollPhase> publishAnimationPhase,
        AtlasSoundController soundController
    ) : base(capi)
    {
        this.captureNormalWorldBackground = captureNormalWorldBackground;
        this.prepareAtlasResources = prepareAtlasResources;
        this.getScrollShader = getScrollShader;
        this.publishAnimationPhase = publishAnimationPhase;
        this.soundController = soundController;
        solidTexture = new LoadedTexture(capi);
        seraphForearms = new SeraphForearmRenderer(capi);
    }

    /// <summary>
    /// The ordinary-world AfterBlit renderer calls this while no atlas dialog
    /// or transition owns the screen. The snapshot is deliberately refreshed
    /// only every half second so the readback cannot become a per-frame cost.
    /// </summary>
    internal bool ShouldRefreshOrdinaryWorldSnapshot()
    {
        if (disposed || IsOpened() || ordinaryWorldSnapshotLocked) return false;
        if (capi.Render.FrameWidth <= 0 || capi.Render.FrameHeight <= 0)
        {
            return false;
        }

        try
        {
            if (capi.World?.Player?.Entity == null) return false;
        }
        catch
        {
            return false;
        }

        long now = capi.ElapsedMilliseconds;
        return lastOrdinaryWorldSnapshotMilliseconds == long.MinValue
            || now - lastOrdinaryWorldSnapshotMilliseconds
                >= OrdinaryWorldSnapshotIntervalMilliseconds;
    }

    internal bool TryRefreshOrdinaryWorldSnapshot()
    {
        if (!ShouldRefreshOrdinaryWorldSnapshot()) return true;
        return CaptureOrdinaryWorldSnapshot();
    }

    private bool CaptureOrdinaryWorldSnapshot(bool allowWhileTransition = false)
    {
        if (disposed
            || (!allowWhileTransition
                && (IsOpened() || ordinaryWorldSnapshotLocked)))
        {
            return false;
        }

        // Throttle failed readbacks as well as successful ones. A broken or
        // temporarily unavailable framebuffer must not turn the AfterBlit
        // renderer into a per-frame retry loop.
        lastOrdinaryWorldSnapshotMilliseconds = capi.ElapsedMilliseconds;

        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        try
        {
            int sourceWidth = Math.Max(1, render.FrameWidth);
            int sourceHeight = Math.Max(1, render.FrameHeight);
            using BitmapRef screenshot = render.GrabScreenshot(
                sourceWidth,
                sourceHeight,
                false,
                true,
                true
            );

            int[] sourcePixels = screenshot.Pixels;
            int requiredPixels = checked(sourceWidth * sourceHeight);
            if (sourcePixels.Length < requiredPixels)
            {
                capi.Logger.Warning(
                    "[ModernAtlas] Ordinary transition snapshot returned {0} pixels; expected at least {1}.",
                    sourcePixels.Length,
                    requiredPixels
                );
                return false;
            }

            int factor = OrdinaryWorldSnapshotDownsampleFactor;
            int targetWidth = Math.Max(1, (sourceWidth + factor - 1) / factor);
            int targetHeight = Math.Max(1, (sourceHeight + factor - 1) / factor);
            int[] blurredPixels = CreateBlurredSnapshotPixels(
                sourcePixels,
                sourceWidth,
                sourceHeight,
                targetWidth,
                targetHeight,
                factor
            );

            EnsureOrdinaryWorldSnapshotTextures(targetWidth, targetHeight);
            LoadedTexture staging = ordinaryWorldSnapshotStagingTexture
                ?? throw new InvalidOperationException(
                    "The ordinary transition snapshot staging texture was not created."
                );
            staging.Width = targetWidth;
            staging.Height = targetHeight;
            render.LoadOrUpdateTextureFromBgra(
                blurredPixels,
                true,
                1,
                ref staging
            );
            if (staging.TextureId <= 0)
            {
                return false;
            }

            ordinaryWorldSnapshotStagingTexture = staging;
            LoadedTexture? previous = ordinaryWorldSnapshotTexture;
            ordinaryWorldSnapshotTexture = staging;
            ordinaryWorldSnapshotStagingTexture = previous;
            lastOrdinaryWorldSnapshotMilliseconds = capi.ElapsedMilliseconds;
            if (!loggedOrdinaryWorldSnapshot)
            {
                loggedOrdinaryWorldSnapshot = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Atlas transition background now uses a blurred ordinary-world snapshot refreshed every {0} ms ({1}x{2}).",
                    OrdinaryWorldSnapshotIntervalMilliseconds,
                    targetWidth,
                    targetHeight
                );
            }
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not refresh the ordinary-world transition snapshot; retaining the previous image: {0}",
                exception.Message
            );
            return false;
        }
        finally
        {
            // GrabScreenshot and texture upload are engine helpers that may
            // touch their own temporary target. Restore the observed program
            // and framebuffer even when a readback or upload throws.
            renderState.RestoreCapturedState();
        }
    }

    private void CaptureOrdinaryWorldSnapshotWhileTransitionStarts()
    {
        _ = CaptureOrdinaryWorldSnapshot(allowWhileTransition: true);
    }

    private static int[] CreateBlurredSnapshotPixels(
        int[] source,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        int factor
    )
    {
        int[] result = new int[checked(targetWidth * targetHeight)];
        for (int targetY = 0; targetY < targetHeight; targetY++)
        {
            int centerY = targetY * factor + factor / 2;
            int minY = Math.Max(0, centerY - factor);
            int maxY = Math.Min(sourceHeight - 1, centerY + factor);
            for (int targetX = 0; targetX < targetWidth; targetX++)
            {
                int centerX = targetX * factor + factor / 2;
                int minX = Math.Max(0, centerX - factor);
                int maxX = Math.Min(sourceWidth - 1, centerX + factor);
                long red = 0;
                long green = 0;
                long blue = 0;
                int count = 0;
                for (int sourceY = minY; sourceY <= maxY; sourceY++)
                {
                    int row = sourceY * sourceWidth;
                    for (int sourceX = minX; sourceX <= maxX; sourceX++)
                    {
                        int pixel = source[row + sourceX];
                        red += (pixel >> 16) & 0xff;
                        green += (pixel >> 8) & 0xff;
                        blue += pixel & 0xff;
                        count++;
                    }
                }

                int averageRed = count == 0 ? 0 : (int)(red / count);
                int averageGreen = count == 0 ? 0 : (int)(green / count);
                int averageBlue = count == 0 ? 0 : (int)(blue / count);
                // The transition background is always opaque. The ordinary
                // screenshot may carry a window alpha value, but that alpha
                // must never re-enter the atlas/GUI composition.
                result[targetY * targetWidth + targetX] =
                    (255 << 24)
                    | (averageRed << 16)
                    | (averageGreen << 8)
                    | averageBlue;
            }
        }
        return result;
    }

    private void EnsureOrdinaryWorldSnapshotTextures(int width, int height)
    {
        if (ordinaryWorldSnapshotTexture != null
            && (ordinaryWorldSnapshotTexture.Width != width
                || ordinaryWorldSnapshotTexture.Height != height))
        {
            ordinaryWorldSnapshotTexture.Dispose();
            ordinaryWorldSnapshotTexture = null;
        }
        if (ordinaryWorldSnapshotStagingTexture != null
            && (ordinaryWorldSnapshotStagingTexture.Width != width
                || ordinaryWorldSnapshotStagingTexture.Height != height))
        {
            ordinaryWorldSnapshotStagingTexture.Dispose();
            ordinaryWorldSnapshotStagingTexture = null;
        }

        ordinaryWorldSnapshotTexture ??= new LoadedTexture(capi)
        {
            Width = width,
            Height = height
        };
        ordinaryWorldSnapshotStagingTexture ??= new LoadedTexture(capi)
        {
            Width = width,
            Height = height
        };
    }

    public bool Begin(bool automated, Action<bool> onCompleted)
    {
        if (disposed || IsOpened()) return false;

        closing = false;
        automatedTest = automated;
        completion = onCompleted;
        finishing = false;
        resourcesReady = false;
        cameraCaptured = false;
        cameraMoved = false;
        bowAnimationStarted = false;
        handoffAnimationStarted = false;
        holdAnimationStarted = false;
        handoffPhaseStarted = false;
        holdPhaseStarted = false;
        pocketSoundAttempted = false;
        pocketSoundPlayed = false;
        unrollSoundAttempted = false;
        unrollSoundPlayed = false;
        swooshSoundAttempted = false;
        swooshSoundPlayed = false;
        physicalScrollRendered = false;
        rolledScrollRendered = false;
        openScrollRendered = false;
        seraphForearmMeshesReady = false;
        thirdPersonHandAnchorsReady = false;
        automatedPocketScreenshotHandled = false;
        automatedPocketScreenshotPassed = false;
        automatedImmediateScreenshotHandled = false;
        automatedImmediateScreenshotPassed = false;
        automatedHandoffScreenshotHandled = false;
        automatedHandoffScreenshotPassed = false;
        automatedScreenshotHandled = false;
        automatedScreenshotPassed = false;
        heldItemSlotsSuppressed = false;
        heldItemsRestored = false;
        cameraRestored = false;
        normalWorldBackgroundCaptured = false;
        ordinaryWorldSnapshotLocked = true;
        localPerspectiveProjection = null;
        bool opened = TryOpen();
        if (!opened) ordinaryWorldSnapshotLocked = false;
        return opened;
    }

    public bool BeginClosing(bool automated, Action<bool> onCompleted)
    {
        if (disposed || IsOpened()) return false;

        closing = true;
        automatedTest = automated;
        completion = onCompleted;
        finishing = false;
        resourcesReady = true;
        cameraCaptured = false;
        cameraMoved = false;
        bowAnimationStarted = false;
        handoffAnimationStarted = false;
        holdAnimationStarted = false;
        handoffPhaseStarted = false;
        holdPhaseStarted = false;
        pocketSoundAttempted = false;
        pocketSoundPlayed = false;
        unrollSoundAttempted = false;
        unrollSoundPlayed = false;
        swooshSoundAttempted = true;
        swooshSoundPlayed = true;
        physicalScrollRendered = false;
        rolledScrollRendered = false;
        openScrollRendered = false;
        seraphForearmMeshesReady = false;
        thirdPersonHandAnchorsReady = false;
        automatedPocketScreenshotHandled = false;
        automatedPocketScreenshotPassed = false;
        automatedImmediateScreenshotHandled = false;
        automatedImmediateScreenshotPassed = false;
        automatedHandoffScreenshotHandled = false;
        automatedHandoffScreenshotPassed = false;
        automatedScreenshotHandled = false;
        automatedScreenshotPassed = false;
        heldItemSlotsSuppressed = false;
        heldItemsRestored = false;
        cameraRestored = false;
        normalWorldBackgroundCaptured = true;
        ordinaryWorldSnapshotLocked = true;
        localPerspectiveProjection = null;
        soundController.StopLightCue();
        bool opened = TryOpen();
        return opened;
    }

    public void CancelWithoutOpening()
    {
        completion = null;
        finishing = true;
        RestorePlayerPresentation();
        publishAnimationPhase(AtlasScrollPhase.Stop);
        if (IsOpened()) TryClose();
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        EnsureRenderResources();
        RefreshSeraphForearmMeshes();
        CapturePlayerPresentation();
        startedTimestamp = Stopwatch.GetTimestamp();
        if (closing)
        {
            publishAnimationPhase(AtlasScrollPhase.RollClosed);
            holdAnimationStarted = StartClientAnimationPair(
                "holdbothhandslarge",
                HoldAnimationCode,
                1.0f
            );
        }
        else
        {
            publishAnimationPhase(AtlasScrollPhase.Retrieve);
            bowAnimationStarted = StartClientAnimationPair(
                "holdinglanternlefthand",
                BowAnimationCode,
                1.0f
            );
        }
        capi.Logger.Notification(
            closing
                ? "[ModernAtlas] Started the first-person scroll stowing transition."
                : "[ModernAtlas] Started the stationary first-person scroll opening transition."
        );
    }

    public override void OnGuiClosed()
    {
        RestorePlayerPresentation();
        ordinaryWorldSnapshotLocked = false;
        base.OnGuiClosed();
    }

    /// <summary>
    /// Locks the last complete ordinary-world snapshot before the atlas GUI
    /// is closed. The next AfterBlit callback must not publish a partially
    /// restored world frame as the closing backdrop.
    /// </summary>
    internal void LockOrdinaryWorldSnapshotForClosing()
    {
        if (!disposed) ordinaryWorldSnapshotLocked = true;
    }

    internal void UnlockOrdinaryWorldSnapshot()
    {
        ordinaryWorldSnapshotLocked = false;
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (finishing) return;

        IRenderAPI renderStateApi = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(
            renderStateApi
        );
        try
        {

        // On the first Survival GUI frame the normal world has completed its
        // ordinary render, while this transition deliberately skipped its
        // Opaque-stage arms and scroll. The transition backdrop is owned by
        // this late GUI pass, so cover the completed world immediately before
        // any transition-owned meshes are drawn.
        if (!closing && !normalWorldBackgroundCaptured)
        {
            normalWorldBackgroundCaptured = captureNormalWorldBackground();
            // The periodic AfterBlit owner normally has a fresh image already.
            // If the hotkey arrived before that first half-second refresh, take
            // one readback from this still-complete ordinary frame before the
            // backdrop covers it. This is an atlas-owned texture operation;
            // ordinary chunk shaders are never changed.
            if (normalWorldBackgroundCaptured
                && (ordinaryWorldSnapshotTexture == null
                    || ordinaryWorldSnapshotTexture.TextureId <= 0))
            {
                CaptureOrdinaryWorldSnapshotWhileTransitionStarts();
            }
            RenderOpaqueTransitionBackdrop();
            return;
        }

        float elapsed = TransitionElapsedSeconds(startedTimestamp);
        if (!closing) resourcesReady = prepareAtlasResources();
        MaintainHeldItemSuppression();
        UpdatePlayerPresentation(elapsed);
        if (closing) AdvanceClosingAnimationAndSound(elapsed);
        else AdvanceAnimationAndSound(elapsed);
        RenderOpaqueTransitionBackdrop();
        if (localPerspectiveProjection != null
            && (closing || normalWorldBackgroundCaptured))
        {
            float scrollTime = closing
                ? Math.Max(0, LightPhaseStartSeconds - elapsed)
                : elapsed;
            physicalScrollRendered |= RenderPhysicalScroll(
                scrollTime,
                null,
                localPerspectiveProjection
            );
        }
        CaptureAutomatedScreenshot(elapsed);

        float duration = closing ? ClosingDurationSeconds : TotalDurationSeconds;
        if (elapsed >= duration && resourcesReady)
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
        finally
        {
            try
            {
                renderState.RestoreGuiHandoff();
            }
            finally
            {
                try
                {
                    renderStateApi.GetEngineShader(EnumShaderProgram.Gui).Use();
                }
                catch
                {
                    // GUI teardown can invalidate the engine program.
                }
            }
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
            // The global hotkey and dialog path are engine-order dependent.
            // Both must release an opening scene so G always closes every
            // atlas-owned input layer just like Escape.
            CancelWithoutOpening();
        }
        args.Handled = true;
    }

    public override void OnKeyPress(KeyEvent args) => args.Handled = true;

    public override void OnMouseDown(MouseEvent args) => args.Handled = true;
    public override void OnMouseUp(MouseEvent args) => args.Handled = true;
    public override void OnMouseMove(MouseEvent args) => args.Handled = true;

    public override void OnMouseWheel(MouseWheelEventArgs args) => args.SetHandled();

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.Opaque) return;

        localPerspectiveProjection = Mat4f.CloneIt(capi.Render.CurrentProjectionMatrix);
        if (!finishing && IsOpened())
        {
            float elapsed = TransitionElapsedSeconds(startedTimestamp);
            if (!closing && !normalWorldBackgroundCaptured) return;

            UpdatePlayerPresentation(elapsed);
            thirdPersonHandAnchorsReady |= ValidateCurrentThirdPersonHandAnchors();
        }
        RenderRemoteScrolls();
    }

    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CancelWithoutOpening();
        scrollSheetMesh?.Dispose();
        scrollSheetMesh = null;
        scrollCylinderMesh?.Dispose();
        scrollCylinderMesh = null;
        seraphForearms.Dispose();
        LoadedTexture? snapshotTexture = ordinaryWorldSnapshotTexture;
        LoadedTexture? snapshotStagingTexture =
            ordinaryWorldSnapshotStagingTexture;
        ordinaryWorldSnapshotTexture = null;
        ordinaryWorldSnapshotStagingTexture = null;
        snapshotTexture?.Dispose();
        if (snapshotStagingTexture != null
            && !ReferenceEquals(snapshotStagingTexture, snapshotTexture))
        {
            snapshotStagingTexture.Dispose();
        }
        solidTexture.Dispose();
        base.Dispose();
    }

    private void CapturePlayerPresentation()
    {
        try
        {
            IClientPlayer clientPlayer = capi.World.Player;
            EntityPlayer player = capi.World.Player.Entity;
            savedMousePitch = capi.Input.MousePitch;
            savedMouseYaw = capi.Input.MouseYaw;
            savedCameraPitch = clientPlayer.CameraPitch;
            savedCameraYaw = clientPlayer.CameraYaw;
            savedCameraRoll = clientPlayer.CameraRoll;
            savedEntityPitch = player.Pos.Pitch;
            savedEntityYaw = player.Pos.Yaw;
            savedEntityRoll = player.Pos.Roll;
            savedBodyYaw = player.BodyYaw;
            savedHeadPitch = player.Pos.HeadPitch;
            savedHeadYaw = player.Pos.HeadYaw;
            cameraCaptured = true;
            SuppressHeldItems(player);
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not capture the player presentation for the opening transition: {0}",
                exception.Message
            );
            RestorePlayerPresentation();
        }
    }

    private void SuppressHeldItems(EntityPlayer player)
    {
        heldItemAgent = player;
        savedLeftHandItemSlot = player.LeftHandItemSlot;
        savedRightHandItemSlot = player.RightHandItemSlot;
        hiddenLeftHandItemSlot = new DummySlot();
        hiddenRightHandItemSlot = new DummySlot();
        player.LeftHandItemSlot = hiddenLeftHandItemSlot;
        player.RightHandItemSlot = hiddenRightHandItemSlot;
        heldItemSlotsSuppressed = true;
    }

    private void MaintainHeldItemSuppression()
    {
        try
        {
            if (heldItemAgent != null
                && hiddenLeftHandItemSlot != null
                && hiddenRightHandItemSlot != null)
            {
                heldItemAgent.LeftHandItemSlot = hiddenLeftHandItemSlot;
                heldItemAgent.RightHandItemSlot = hiddenRightHandItemSlot;
            }
        }
        catch
        {
            RestorePlayerPresentation();
        }
    }

    private void UpdatePlayerPresentation(float elapsed)
    {
        if (!cameraCaptured) return;
        try
        {
            IClientPlayer clientPlayer = capi.World.Player;
            EntityPlayer player = capi.World.Player.Entity;
            // Input is captured for the transition, and these assignments also
            // prevent other client systems from introducing a camera bow or
            // pocket-facing turn while the scroll is in the player's hands.
            clientPlayer.CameraYaw = savedCameraYaw;
            clientPlayer.CameraPitch = savedCameraPitch;
            clientPlayer.CameraRoll = savedCameraRoll;
            capi.Input.MouseYaw = savedMouseYaw;
            capi.Input.MousePitch = savedMousePitch;
            player.Pos.Pitch = savedEntityPitch;
            player.Pos.Yaw = savedEntityYaw;
            player.Pos.Roll = savedEntityRoll;
            player.BodyYaw = savedBodyYaw;
            player.Pos.HeadPitch = savedHeadPitch;
            player.Pos.HeadYaw = savedHeadYaw;
        }
        catch
        {
            RestorePlayerPresentation();
        }
    }

    private void AdvanceAnimationAndSound(float elapsed)
    {
        if (!pocketSoundAttempted && elapsed >= ScrollAppearsSeconds)
        {
            pocketSoundAttempted = true;
            pocketSoundPlayed = soundController.PlayPageTouch();
        }

        if (!handoffPhaseStarted && elapsed >= HandoffStartSeconds)
        {
            handoffPhaseStarted = true;
            publishAnimationPhase(AtlasScrollPhase.Handoff);
            StopClientAnimation(BowAnimationCode);
            handoffAnimationStarted = StartClientAnimationPair(
                "twohandplaceblock",
                HandoffAnimationCode,
                0.80f
            );
        }

        if (!unrollSoundAttempted && elapsed >= UnrollStartSeconds)
        {
            unrollSoundAttempted = true;
            unrollSoundPlayed = soundController.PlayOpeningUnroll();
        }

        if (!holdPhaseStarted && elapsed >= UnrollEndSeconds)
        {
            holdPhaseStarted = true;
            publishAnimationPhase(AtlasScrollPhase.HoldOpen);
            StopClientAnimation(HandoffAnimationCode);
            holdAnimationStarted = StartClientAnimationPair(
                "holdbothhandslarge",
                HoldAnimationCode,
                1.0f
            );
        }

        if (!swooshSoundAttempted && elapsed >= LightPhaseStartSeconds)
        {
            swooshSoundAttempted = true;
            swooshSoundPlayed = soundController.PlayLightSweep();
        }
    }

    private void AdvanceClosingAnimationAndSound(float elapsed)
    {
        if (!unrollSoundAttempted && elapsed >= 0.18f)
        {
            unrollSoundAttempted = true;
            unrollSoundPlayed = soundController.PlayClosingRoll();
        }

        if (!handoffPhaseStarted && elapsed >= ClosingReleaseRightSeconds)
        {
            handoffPhaseStarted = true;
            publishAnimationPhase(AtlasScrollPhase.ReleaseRight);
            StopClientAnimation(HoldAnimationCode);
            handoffAnimationStarted = StartClientAnimationPair(
                "twohandplaceblock",
                HandoffAnimationCode,
                0.80f
            );
        }

        if (!holdPhaseStarted && elapsed >= ClosingStowStartSeconds)
        {
            holdPhaseStarted = true;
            publishAnimationPhase(AtlasScrollPhase.Stow);
            StopClientAnimation(HandoffAnimationCode);
            bowAnimationStarted = StartClientAnimationPair(
                "holdinglanternlefthand",
                BowAnimationCode,
                1.0f,
                1000f
            );
        }

        if (!pocketSoundAttempted && elapsed >= ClosingDurationSeconds - 0.2f)
        {
            pocketSoundAttempted = true;
            pocketSoundPlayed = soundController.PlayPageTouch();
        }
    }

    private bool RenderPhysicalScroll(
        float elapsed,
        RemoteScrollPose? remotePose = null,
        float[]? projectionOverride = null
    )
    {
        if (!EnsureRenderResources()) return false;

        IShaderProgram? shader = getScrollShader();
        if (shader == null || shader.Disposed
            || scrollSheetMesh == null
            || scrollCylinderMesh == null)
        {
            return false;
        }

        // The mesh stays absent while the right hand reaches for the pocket.
        // It enters at that hand only after the grab, never as a detached
        // object already floating in front of the player.
        float visible = elapsed >= ScrollAppearsSeconds ? 1f : 0f;
        float reach = SmoothStep(elapsed / ScrollAppearsSeconds);
        float lift = EaseOutCubic(
            (elapsed - ScrollAppearsSeconds)
                / (UnrollStartSeconds - ScrollAppearsSeconds)
        );
        float unroll = SmoothStep(
            (elapsed - UnrollStartSeconds)
                / (UnrollEndSeconds - UnrollStartSeconds)
        );
        float dive = EaseInCubic(
            (elapsed - LightPhaseStartSeconds)
                / (TotalDurationSeconds - LightPhaseStartSeconds)
        );

        IRenderAPI render = capi.Render;
        // The closed roll follows the retrieving right hand from the belt,
        // then settles below centre while the two endpoints separate.
        float pocketX = elapsed < ScrollAppearsSeconds
            ? Lerp(0.34f, 0.62f, reach)
            : 0.62f;
        float pocketY = elapsed < ScrollAppearsSeconds
            ? Lerp(-0.46f, -0.76f, reach)
            : -0.76f;
        float anchoredX = Lerp(pocketX, 0f, lift);
        float anchoredY = Lerp(pocketY, -0.10f, lift);
        float anchoredZ = Lerp(-1.34f, -1.14f, lift);
        float centerX = Lerp(anchoredX, 0.02f, dive);
        float centerY = Lerp(anchoredY, 0.02f, dive);
        float centerZ = Lerp(anchoredZ, -0.34f, dive);
        float parentScale = 1f + dive * 4.25f;
        float rotationX = Lerp(0.26f, -0.06f, lift) * (1f - dive);
        float rotationY = Lerp(0.16f, -0.02f, lift) * (1f - dive);
        float rotationZ = Lerp(0.26f, -0.018f, lift) * (1f - dive);
        // Keep the opened landscape parchment large enough to read, while
        // leaving the upper world view clear instead of filling the window.
        float scrollWidth = Lerp(0.07f, 0.72f, unroll);
        float scrollHeight = Lerp(0.30f, 0.40f, lift);
        float sweep = SmoothStep(
            (elapsed - LightPhaseStartSeconds)
                / (TotalDurationSeconds - LightPhaseStartSeconds)
        );

        float[] projection = projectionOverride ?? render.CurrentProjectionMatrix;
        bool remoteWorldScroll = remotePose != null;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);

        float[] parent;
        try
        {
        if (remotePose is RemoteScrollPose pose)
        {
            float handDistance = Distance(pose.LeftHand, pose.RightHand);
            scrollWidth = Lerp(
                0.11f,
                Math.Clamp(handDistance, 0.30f, 1.25f),
                unroll
            );
            scrollHeight = 0.42f;
            sweep = 0;
            parent = CreateRemoteScrollParent(pose, unroll);
        }
        else
        {
            parent = Mat4f.Create();
            Mat4f.Translate(parent, parent, centerX, centerY, centerZ);
            Mat4f.RotateZ(parent, parent, rotationZ);
            Mat4f.RotateY(parent, parent, rotationY);
            Mat4f.RotateX(parent, parent, rotationX);
            Mat4f.Scale(parent, parent, parentScale, parentScale, parentScale);
        }

        render.CurrentActiveShader?.Stop();
        shader.Use();
        shader.UniformMatrix("projectionMatrix", projection);
        render.GLDepthMask(remoteWorldScroll && visible >= 0.96f);
        if (remoteWorldScroll)
        {
            render.GLEnableDepthTest();
        }
        else
        {
            // The local pocket scroll is a first-person overlay. Testing it
            // against the previous world depth lets bowls, jugs, walls and
            // their shadows punch silhouettes through the parchment.
            render.GLDisableDepthTest();
        }
        render.GlDisableCullFace();
        // The local parchment is a physical, opaque first-person overlay.
        // Explicitly disabling blending prevents leaked engine blend state
        // from making the sheet or rollers transparent. Remote scrolls retain
        // their short world-space fade while entering the scene.
        render.GlToggleBlend(remoteWorldScroll, EnumBlendMode.Standard);
        try
        {
            float rollerOffset = scrollWidth * 0.5f;
            float sheetAlpha = visible * SmoothStep(unroll / 0.18f);
            if (sheetAlpha > 0.001f)
            {
                float[] sheetModel = CreateComponentModel(
                    parent,
                    0,
                    0,
                    0,
                    // Both attachment edges terminate beneath the paper
                    // sleeves. The small hidden overlap prevents a light gap
                    // without letting the sheet protrude past either roller.
                    Math.Max(0.075f, scrollWidth + 0.022f),
                    scrollHeight,
                    1f
                );
                RenderComponent(
                    shader,
                    scrollSheetMesh,
                    sheetModel,
                    0,
                    remoteWorldScroll ? sheetAlpha : 1f,
                    sweep
                );
            }

            float remainingRoll = 1f - unroll;
            float rollerRotation = unroll * MathF.PI * 3.5f;
            float componentAlpha = remoteWorldScroll ? visible : 1f;

            // Both wooden cores and their permanent paper sleeves exist from
            // the closed bundle onward. Moving their centres apart therefore
            // opens one connected object instead of spawning a second rod or
            // an independent sheet midway through the motion.
            RenderRoller(
                shader,
                parent,
                rollerOffset,
                scrollHeight,
                componentAlpha,
                sweep,
                includePaperSleeve: true,
                rotationRadians: rollerRotation
            );
            RenderRoller(
                shader,
                parent,
                -rollerOffset,
                scrollHeight,
                componentAlpha,
                sweep,
                includePaperSleeve: true,
                rotationRadians: -rollerRotation * 0.82f
            );

            if (remainingRoll > 0.015f)
            {
                // Symmetric windings visibly shrink while paper is paid out
                // between the rods. Their radius always covers the sheet's
                // hidden attachment overlap, so neither edge can detach.
                float windingRadius = 0.017f + remainingRoll * 0.030f;
                RenderPaperWinding(
                    shader,
                    parent,
                    rollerOffset,
                    scrollHeight,
                    windingRadius,
                    componentAlpha,
                    sweep,
                    -rollerRotation
                );
                RenderPaperWinding(
                    shader,
                    parent,
                    -rollerOffset,
                    scrollHeight,
                    windingRadius,
                    componentAlpha,
                    sweep,
                    rollerRotation * 0.82f
                );
            }

            // Draw the arms after the lower rod ends. Their hands then overlap
            // the wooden grips instead of stopping beside them, while the
            // parchment remains safely behind the complete held assembly.
            if (remotePose == null && (!closing || visible > 0))
            {
                RenderFirstPersonArms(
                    shader,
                    parent,
                    rollerOffset,
                    scrollHeight,
                    elapsed
                );
            }

        }
        finally
        {
            shader.Stop();
        }

        rolledScrollRendered |= elapsed < UnrollStartSeconds + 0.18f;
        openScrollRendered |= unroll >= 0.78f;
        return true;
        }
        finally
        {
            if (remoteWorldScroll)
            {
                renderState.RestoreWorldOpaqueHandoff();
            }
            else
            {
                renderState.RestoreGuiHandoff();
                if (projectionOverride != null)
                {
                    // Late GUI renderers following this dialog (notably the
                    // crosshair) expect the engine GUI shader to remain active.
                    try
                    {
                        render.GetEngineShader(EnumShaderProgram.Gui).Use();
                    }
                    catch
                    {
                        // The GUI program can be invalid during world leave.
                    }
                }
            }
        }
    }

    private void RenderFirstPersonArms(
        IShaderProgram shader,
        float[] parent,
        float rollerOffset,
        float scrollHeight,
        float elapsed
    )
    {
        if (!seraphForearms.IsReady) return;
        seraphForearms.BindSkin(shader);

        // The right hand alone reaches the belt, grasps the invisible roll,
        // then carries it upward. The left hand joins only for the second end
        // after the handoff phase begins.
        float gripY = -scrollHeight * 0.5f - 0.022f;
        float gripInset = 0.004f;
        float rightReach = SmoothStep(elapsed / ScrollAppearsSeconds);
        float rightSettle = SmoothStep(
            (elapsed - ScrollAppearsSeconds)
                / (HandoffStartSeconds - ScrollAppearsSeconds)
        );
        float rightStartX = Lerp(0.48f, 0.20f, rightReach);
        float rightStartY = Lerp(-0.48f, -0.72f, rightReach);
        rightStartX = Lerp(rightStartX, 0.62f, rightSettle);
        rightStartY = Lerp(rightStartY, -0.62f, rightSettle);
        seraphForearms.RenderRightArm(
            shader,
            parent,
            rightStartX,
            rightStartY,
            rollerOffset - gripInset,
            gripY,
            1f
        );

        float leftAlpha = SmoothStep(
            (elapsed - HandoffStartSeconds)
                / (UnrollStartSeconds - HandoffStartSeconds)
        );
        float leftReach = SmoothStep(
            (elapsed - HandoffStartSeconds)
                / (UnrollEndSeconds - HandoffStartSeconds)
        );
        float leftStartX = Lerp(-0.72f, -0.60f, leftReach);
        float leftStartY = Lerp(-0.66f, -0.62f, leftReach);
        seraphForearms.RenderLeftArm(
            shader,
            parent,
            leftStartX,
            leftStartY,
            -rollerOffset + gripInset,
            gripY,
            leftAlpha
        );
    }

    private void RenderRemoteScrolls()
    {
        if (remoteScrolls.Count == 0 || capi.World.Player?.Entity == null) return;

        List<string>? expired = null;
        foreach (KeyValuePair<string, RemoteScrollState> entry in remoteScrolls)
        {
            EntityPlayer? remotePlayer = null;
            foreach (Entity entity in capi.World.LoadedEntities.Values)
            {
                if (entity is EntityPlayer candidate
                    && candidate.PlayerUID == entry.Key)
                {
                    remotePlayer = candidate;
                    break;
                }
            }
            if (remotePlayer == null) continue;

            float phaseElapsed = TransitionElapsedSeconds(
                entry.Value.StartedTimestamp
            );
            float scrollTime = RemoteScrollTime(entry.Value.Phase, phaseElapsed);
            float stowDuration = ClosingDurationSeconds - ClosingStowStartSeconds;
            if (entry.Value.Phase == AtlasScrollPhase.Stow
                && phaseElapsed > stowDuration + 0.05f)
            {
                (expired ??= new List<string>()).Add(entry.Key);
                continue;
            }

            if (TryGetRemoteScrollPose(remotePlayer, out RemoteScrollPose pose))
            {
                RenderPhysicalScroll(scrollTime, pose);
            }
        }

        if (expired != null)
        {
            foreach (string uid in expired) remoteScrolls.Remove(uid);
        }
    }

    private static float RemoteScrollTime(AtlasScrollPhase phase, float elapsed)
    {
        return phase switch
        {
            AtlasScrollPhase.Retrieve =>
                Lerp(0, HandoffStartSeconds, SmoothStep(elapsed / HandoffStartSeconds)),
            AtlasScrollPhase.Handoff =>
                Lerp(
                    HandoffStartSeconds,
                    UnrollEndSeconds,
                    SmoothStep(elapsed / (UnrollEndSeconds - HandoffStartSeconds))
                ),
            AtlasScrollPhase.HoldOpen => UnrollEndSeconds,
            AtlasScrollPhase.RollClosed =>
                Lerp(
                    LightPhaseStartSeconds,
                    UnrollStartSeconds,
                    SmoothStep(elapsed / ClosingReleaseRightSeconds)
                ),
            AtlasScrollPhase.ReleaseRight =>
                Lerp(
                    UnrollStartSeconds,
                    HandoffStartSeconds,
                    SmoothStep(
                        elapsed
                            / (ClosingStowStartSeconds - ClosingReleaseRightSeconds)
                    )
                ),
            AtlasScrollPhase.Stow =>
                Lerp(
                    HandoffStartSeconds,
                    ScrollAppearsSeconds,
                    SmoothStep(elapsed / (ClosingDurationSeconds - ClosingStowStartSeconds))
                ),
            _ => 0
        };
    }

    private static float TransitionElapsedSeconds(long timestamp) =>
        (float)Stopwatch.GetElapsedTime(timestamp).TotalSeconds
        * TransitionSpeed;

    private bool TryGetRemoteScrollPose(
        EntityPlayer player,
        out RemoteScrollPose pose
    )
    {
        pose = default;
        IAnimator? animator = player.TpAnimManager.Animator;
        AttachmentPointAndPose? left = animator?.GetAttachmentPointPose("LeftHand");
        AttachmentPointAndPose? right = animator?.GetAttachmentPointPose("RightHand");
        if (left?.AttachPoint == null || right?.AttachPoint == null) return false;

        EntityPlayer localPlayer = capi.World.Player.Entity;
        float[] entityModel = Mat4f.Create();
        Mat4f.Translate(
            entityModel,
            entityModel,
            (float)(player.Pos.X - localPlayer.CameraPos.X),
            (float)(player.Pos.Y - localPlayer.CameraPos.Y),
            (float)(player.Pos.Z - localPlayer.CameraPos.Z)
        );

        float halfHeight = player.SelectionBox.Y2 * 0.5f;
        CompositeShape? shape = player.Properties.Client.Shape;
        float shapeYaw = (shape?.rotateY ?? 0) + 90f;
        Mat4f.Translate(entityModel, entityModel, 0, halfHeight, 0);
        Mat4f.RotateY(
            entityModel,
            entityModel,
            player.Pos.Yaw + shapeYaw * GameMath.DEG2RAD
        );
        Mat4f.Translate(entityModel, entityModel, 0, -halfHeight, 0);
        float size = player.Properties.Client.Size;
        Mat4f.Scale(entityModel, entityModel, size, size, size);
        Mat4f.Translate(entityModel, entityModel, -0.5f, 0, -0.5f);

        float[] modelView = Mat4f.Create();
        Mat4f.Mul(modelView, capi.Render.CameraMatrixOriginf, entityModel);
        Vec3f leftHand = TransformAttachmentPoint(modelView, left);
        Vec3f rightHand = TransformAttachmentPoint(modelView, right);
        Vec3f rightAxis = Normalize(TransformDirection(modelView, 1, 0, 0));
        Vec3f upAxis = Normalize(TransformDirection(modelView, 0, 1, 0));
        if (!IsFinite(leftHand)
            || !IsFinite(rightHand)
            || !IsFinite(rightAxis)
            || !IsFinite(upAxis))
        {
            return false;
        }

        pose = new RemoteScrollPose(leftHand, rightHand, rightAxis, upAxis);
        return true;
    }

    private static Vec3f TransformAttachmentPoint(
        float[] modelView,
        AttachmentPointAndPose point
    )
    {
        Vec3f local = TransformPoint(
            point.AnimModelMatrix,
            (float)(point.AttachPoint.PosX / 16.0),
            (float)(point.AttachPoint.PosY / 16.0),
            (float)(point.AttachPoint.PosZ / 16.0)
        );
        return TransformPoint(modelView, local.X, local.Y, local.Z);
    }

    private static float[] CreateRemoteScrollParent(
        RemoteScrollPose pose,
        float unroll
    )
    {
        Vec3f handAxis = Subtract(pose.RightHand, pose.LeftHand);
        float handDistance = Length(handAxis);
        Vec3f xAxis = handDistance > 0.03f
            ? Scale(handAxis, 1f / handDistance)
            : pose.RightAxis;
        Vec3f upProjection = Subtract(
            pose.UpAxis,
            Scale(xAxis, Dot(pose.UpAxis, xAxis))
        );
        Vec3f yAxis = Length(upProjection) > 0.03f
            ? Normalize(upProjection)
            : new Vec3f(0, 1, 0);
        Vec3f zAxis = Normalize(Cross(xAxis, yAxis));
        yAxis = Normalize(Cross(zAxis, xAxis));

        float gripBlend = SmoothStep(unroll);
        Vec3f midpoint = Scale(Add(pose.LeftHand, pose.RightHand), 0.5f);
        Vec3f center = Lerp(pose.LeftHand, midpoint, gripBlend);
        return new[]
        {
            xAxis.X, xAxis.Y, xAxis.Z, 0,
            yAxis.X, yAxis.Y, yAxis.Z, 0,
            zAxis.X, zAxis.Y, zAxis.Z, 0,
            center.X, center.Y, center.Z, 1
        };
    }

    private static Vec3f TransformPoint(
        float[] matrix,
        float x,
        float y,
        float z
    )
    {
        if (matrix.Length < 16) return new Vec3f();

        return new Vec3f(
            matrix[0] * x + matrix[4] * y + matrix[8] * z + matrix[12],
            matrix[1] * x + matrix[5] * y + matrix[9] * z + matrix[13],
            matrix[2] * x + matrix[6] * y + matrix[10] * z + matrix[14]
        );
    }

    private static Vec3f TransformDirection(
        float[] matrix,
        float x,
        float y,
        float z
    ) => new(
        matrix[0] * x + matrix[4] * y + matrix[8] * z,
        matrix[1] * x + matrix[5] * y + matrix[9] * z,
        matrix[2] * x + matrix[6] * y + matrix[10] * z
    );

    private static Vec3f Add(Vec3f a, Vec3f b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static Vec3f Subtract(Vec3f a, Vec3f b) =>
        new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static Vec3f Scale(Vec3f value, float scale) =>
        new(value.X * scale, value.Y * scale, value.Z * scale);

    private static Vec3f Lerp(Vec3f a, Vec3f b, float amount) =>
        new(
            Lerp(a.X, b.X, amount),
            Lerp(a.Y, b.Y, amount),
            Lerp(a.Z, b.Z, amount)
        );

    private static float Dot(Vec3f a, Vec3f b) =>
        a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static Vec3f Cross(Vec3f a, Vec3f b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X
    );

    private static float Length(Vec3f value) =>
        MathF.Sqrt(Dot(value, value));

    private static float Distance(Vec3f a, Vec3f b) =>
        Length(Subtract(a, b));

    private static Vec3f Normalize(Vec3f value)
    {
        float length = Length(value);
        return length > 0.0001f ? Scale(value, 1f / length) : new Vec3f();
    }

    private static bool IsFinite(Vec3f value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);

    private void RenderRoller(
        IShaderProgram shader,
        float[] parent,
        float x,
        float sheetHeight,
        float alpha,
        float sweep,
        bool includePaperSleeve,
        float rotationRadians
    )
    {
        if (scrollCylinderMesh == null || alpha <= 0.001f) return;

        // A scroll needs a fine, dark wooden spine rather than a pillar. The
        // paper sleeve is only slightly wider and shorter than the map, so it
        // reads as the parchment curling around the roller.
        float sleeveHeight = sheetHeight * 0.95f;
        float rodHeight = sheetHeight + 0.065f;
        float[] rodModel = CreateComponentModel(
            parent,
            x,
            0,
            0.004f,
            0.025f,
            rodHeight,
            0.025f,
            rotationY: rotationRadians
        );
        RenderComponent(shader, scrollCylinderMesh, rodModel, 1, alpha, sweep);
        if (includePaperSleeve)
        {
            RenderComponent(
                shader,
                scrollCylinderMesh,
                CreateComponentModel(
                    parent,
                    x,
                    0,
                    0.008f,
                    0.032f,
                    sleeveHeight,
                    0.032f,
                    rotationY: rotationRadians
                ),
                3,
                alpha,
                sweep
            );
        }

        float knobY = rodHeight * 0.5f + 0.026f;
        RenderComponent(
            shader,
            scrollCylinderMesh,
            CreateComponentModel(
                parent,
                x,
                knobY,
                0.004f,
                0.052f,
                0.036f,
                0.052f,
                rotationY: rotationRadians
            ),
            1,
            alpha,
            sweep
        );
        RenderComponent(
            shader,
            scrollCylinderMesh,
            CreateComponentModel(
                parent,
                x,
                -knobY,
                0.004f,
                0.052f,
                0.036f,
                0.052f,
                rotationY: rotationRadians
            ),
            1,
            alpha,
            sweep
        );
    }

    private void RenderPaperWinding(
        IShaderProgram shader,
        float[] parent,
        float x,
        float sheetHeight,
        float radius,
        float alpha,
        float sweep,
        float rotationRadians
    )
    {
        if (scrollCylinderMesh == null || alpha <= 0.001f) return;

        RenderComponent(
            shader,
            scrollCylinderMesh,
            CreateComponentModel(
                parent,
                x,
                0,
                0.008f,
                radius * 2f,
                sheetHeight * 0.95f,
                radius * 2f,
                rotationY: rotationRadians
            ),
            3,
            alpha,
            sweep
        );
    }

    private void RenderComponent(
        IShaderProgram shader,
        MeshRef mesh,
        float[] model,
        int materialKind,
        float alpha,
        float sweep
    )
    {
        shader.UniformMatrix("modelViewMatrix", model);
        shader.Uniform("materialKind", materialKind);
        shader.Uniform("alpha", Math.Clamp(alpha, 0f, 1f));
        shader.Uniform("lightSweep", Math.Clamp(sweep, 0f, 1f));
        capi.Render.RenderMesh(mesh);
    }

    private static float[] CreateComponentModel(
        float[] parent,
        float x,
        float y,
        float z,
        float scaleX,
        float scaleY,
        float scaleZ,
        float rotationY = 0f
    )
    {
        float[] model = Mat4f.CloneIt(parent);
        Mat4f.Translate(model, model, x, y, z);
        if (rotationY != 0f) Mat4f.RotateY(model, model, rotationY);
        Mat4f.Scale(model, model, scaleX, scaleY, scaleZ);
        return model;
    }

    private void RenderOpaqueTransitionBackdrop()
    {
        if (!EnsureRenderResources()) return;

        int backdropTextureId = ordinaryWorldSnapshotTexture?.TextureId > 0
            ? ordinaryWorldSnapshotTexture.TextureId
            : solidTexture.TextureId;
        if (backdropTextureId <= 0) return;

        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        try
        {
            render.CurrentActiveShader?.Stop();
            render.GetEngineShader(EnumShaderProgram.Gui).Use();
            render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
            render.GlColorMask(true, true, true, true);
            render.GlScissorFlag(false);
            render.GLDepthMask(false);
            render.GLDisableDepthTest();
            render.GlDisableCullFace();
            // This is the transition's complete opaque base. It is a frozen,
            // blurred ordinary-world photo when one is available, otherwise
            // the neutral atlas fallback. It is drawn before the physical
            // scroll and never blends with the ordinary world framebuffer, so
            // no terrain, OIT, cloud or stale depth fragment can remain visible
            // around the scroll.
            render.GlToggleBlend(false, EnumBlendMode.Standard);
            render.Render2DTexture(
                backdropTextureId,
                0,
                0,
                render.FrameWidth,
                render.FrameHeight,
                99,
                ordinaryWorldSnapshotTexture?.TextureId > 0
                    // Preserve the captured ordinary-world exposure. The
                    // blur is the transition treatment; a tint here would
                    // erase lanterns and make closing look like a void fade.
                    ? new Vec4f(1f, 1f, 1f, 1f)
                    : new Vec4f(0.035f, 0.075f, 0.11f, 1f)
            );
        }
        finally
        {
            renderState.RestoreGuiHandoff(false);
            try
            {
                render.GetEngineShader(EnumShaderProgram.Gui).Use();
            }
            catch
            {
                // GUI teardown can invalidate the engine program.
            }
        }
    }

    private void RefreshSeraphForearmMeshes()
    {
        seraphForearms.Dispose();
        seraphForearmMeshesReady = seraphForearms.Prepare();
    }

    // Kept temporarily to make old save-session hot reloads harmless. New
    // atlas scenes use SeraphForearmRenderer, so this duplicate path is never
    // entered by ModernAtlas itself.
    private void RefreshSeraphForearmMeshesLegacy()
    {
        leftSeraphForearmMesh?.Dispose();
        leftSeraphForearmMesh = null;
        rightSeraphForearmMesh?.Dispose();
        rightSeraphForearmMesh = null;
        seraphSkinTextureId = 0;
        seraphSkinColor = new Vec4f(1f, 1f, 1f, 1f);

        try
        {
            EntityPlayer player = capi.World.Player.Entity;
            Shape? loadedShape = player.Properties.Client.LoadedShapeForEntity
                ?? player.Properties.Client.LoadedShape;
            if (loadedShape == null)
            {
                throw new InvalidOperationException(
                    "The loaded Seraph shape is unavailable."
                );
            }

            Shape? bareSeraphShape = Shape.TryGet(
                capi,
                new AssetLocation(
                    "game",
                    "shapes/entity/humanoid/seraph-hairless.json"
                )
            );
            if (bareSeraphShape == null)
            {
                throw new InvalidOperationException(
                    "The base Seraph skin shape is unavailable."
                );
            }

            if (!PlayerSkinTextureAdapter.TryGet(
                    player,
                    out ITexPositionSource? textureSource,
                    out seraphSkinTextureId,
                    out seraphSkinColor
                )
                || textureSource == null)
            {
                throw new InvalidOperationException(
                    "The player's composed skin texture is unavailable."
                );
            }
            string leftBoneName = player.GetBoneName(
                "lowerArmLBoneName",
                "LowerArmL"
            );
            string rightBoneName = player.GetBoneName(
                "lowerArmRBoneName",
                "LowerArmR"
            );
            if (FindShapeElement(loadedShape.Elements, leftBoneName) == null
                || FindShapeElement(loadedShape.Elements, rightBoneName) == null)
            {
                throw new InvalidOperationException(
                    "The loaded player model has no compatible forearm bones."
                );
            }

            Shape leftForearmShape = CreateSingleElementShape(
                bareSeraphShape,
                "LowerArmL"
            );
            Shape rightForearmShape = CreateSingleElementShape(
                bareSeraphShape,
                "LowerArmR"
            );
            capi.Tesselator.TesselateShape(
                "ModernAtlas left skin forearm",
                leftForearmShape,
                out MeshData leftMesh,
                textureSource,
                new Vec3f(),
                0,
                0,
                0
            );
            capi.Tesselator.TesselateShape(
                "ModernAtlas right skin forearm",
                rightForearmShape,
                out MeshData rightMesh,
                textureSource,
                new Vec3f(),
                0,
                0,
                0
            );
            NormalizeForearmMesh(leftMesh);
            NormalizeForearmMesh(rightMesh);
            leftSeraphForearmMesh = capi.Render.UploadMesh(leftMesh);
            rightSeraphForearmMesh = capi.Render.UploadMesh(rightMesh);
            seraphForearmMeshesReady = true;
            capi.Logger.Debug(
                "[ModernAtlas] Prepared thick first-person Seraph forearms from player skin texture {0} with tint {1:F3}, {2:F3}, {3:F3}, {4:F3}.",
                seraphSkinTextureId,
                seraphSkinColor.R,
                seraphSkinColor.G,
                seraphSkinColor.B,
                seraphSkinColor.A
            );
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "[ModernAtlas] Could not build first-person arms from the loaded Seraph model: {0}",
                exception.Message
            );
        }
    }

    private static ShapeElement? FindShapeElement(
        ShapeElement[]? elements,
        string name
    )
    {
        if (elements == null) return null;
        foreach (ShapeElement element in elements)
        {
            if (string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return element;
            }
            ShapeElement? child = FindShapeElement(element.Children, name);
            if (child != null) return child;
        }
        return null;
    }

    private static Shape CreateSingleElementShape(
        Shape source,
        string elementName
    )
    {
        Shape shape = source.Clone();
        ShapeElement element = FindShapeElement(shape.Elements, elementName)
            ?? throw new InvalidOperationException(
                $"The base Seraph shape has no {elementName} element."
            );
        element.ParentElement = null;
        shape.Elements = new[] { element };
        shape.Animations = Array.Empty<Animation>();
        return shape;
    }

    private static void NormalizeForearmMesh(MeshData mesh)
    {
        if (mesh.VerticesCount <= 0)
        {
            throw new InvalidOperationException(
                "The selected Seraph forearm contains no vertices."
            );
        }

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float minZ = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        float maxZ = float.MinValue;
        for (int vertex = 0; vertex < mesh.VerticesCount; vertex++)
        {
            int index = vertex * 3;
            float x = mesh.xyz[index];
            float y = mesh.xyz[index + 1];
            float z = mesh.xyz[index + 2];
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            maxZ = Math.Max(maxZ, z);
        }

        float sizeY = Math.Max(0.001f, maxY - minY);
        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        float centerZ = (minZ + maxZ) * 0.5f;
        for (int vertex = 0; vertex < mesh.VerticesCount; vertex++)
        {
            int index = vertex * 3;
            mesh.xyz[index] = (mesh.xyz[index] - centerX) / sizeY;
            mesh.xyz[index + 1] = (mesh.xyz[index + 1] - centerY) / sizeY;
            mesh.xyz[index + 2] = (mesh.xyz[index + 2] - centerZ) / sizeY;
        }
    }

    private bool EnsureRenderResources()
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

        if (scrollSheetMesh != null && scrollCylinderMesh != null)
        {
            return true;
        }

        try
        {
            scrollSheetMesh ??= capi.Render.UploadMesh(CreateScrollSheetMesh());
            scrollCylinderMesh ??= capi.Render.UploadMesh(CreateCylinderMesh(18));
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "[ModernAtlas] Could not upload the physical scroll mesh: {0}",
                exception.Message
            );
            scrollSheetMesh?.Dispose();
            scrollSheetMesh = null;
            scrollCylinderMesh?.Dispose();
            scrollCylinderMesh = null;
            return false;
        }
    }

    private static MeshData CreateScrollSheetMesh()
    {
        const int horizontalSegments = 28;
        const int verticalSegments = 12;
        MeshData mesh = new(horizontalSegments * verticalSegments * 4);
        int color = unchecked((int)0xffffffff);

        for (int yIndex = 0; yIndex < verticalSegments; yIndex++)
        {
            float v0 = yIndex / (float)verticalSegments;
            float v1 = (yIndex + 1) / (float)verticalSegments;
            for (int xIndex = 0; xIndex < horizontalSegments; xIndex++)
            {
                float u0 = xIndex / (float)horizontalSegments;
                float u1 = (xIndex + 1) / (float)horizontalSegments;
                Vec3f p00 = SheetPoint(u0, v0);
                Vec3f p10 = SheetPoint(u1, v0);
                Vec3f p11 = SheetPoint(u1, v1);
                Vec3f p01 = SheetPoint(u0, v1);
                int first = mesh.VerticesCount;
                mesh.AddVertex(p00.X, p00.Y, p00.Z, u0, v0, color);
                mesh.AddVertex(p10.X, p10.Y, p10.Z, u1, v0, color);
                mesh.AddVertex(p11.X, p11.Y, p11.Z, u1, v1, color);
                mesh.AddVertex(p01.X, p01.Y, p01.Z, u0, v1, color);
                mesh.AddQuadIndices(first);
            }
        }

        return mesh;
    }

    private static Vec3f SheetPoint(float u, float v)
    {
        float x = u - 0.5f;
        float y = v - 0.5f;
        // Side edges are the physical attachment seams. Keep them straight
        // and inside the roller sleeves; ragged outward vertices made the
        // parchment visibly protrude beyond its rods during unrolling.
        if (v <= 0.0001f || v >= 0.9999f)
        {
            float side = v < 0.5f ? -1f : 1f;
            float tear = 0.008f
                + MathF.Sin(u * 37.0f + 0.3f) * 0.007f
                + MathF.Sin(u * 13.0f + 2.1f) * 0.005f;
            y += side * Math.Max(0.002f, tear);
        }
        return new Vec3f(x, y, SheetDepth(x, y));
    }

    private static float SheetDepth(float x, float y)
    {
        // At each attachment seam the paper reaches the front tangent of the
        // sleeve (z ~= 0.024). A shallow centre sag keeps the sheet physical
        // without lifting its ends in front of, or away from, the rollers.
        float edgeCurl = MathF.Pow(MathF.Abs(x) * 2f, 3f) * 0.024f;
        float centerSag = (1f - MathF.Abs(x) * 2f) * (0.5f - MathF.Abs(y)) * -0.014f;
        return edgeCurl + centerSag;
    }

    private static MeshData CreateCylinderMesh(int segments)
    {
        MeshData mesh = new(segments * 10);
        int color = unchecked((int)0xffffffff);
        for (int segment = 0; segment < segments; segment++)
        {
            float u0 = segment / (float)segments;
            float u1 = (segment + 1) / (float)segments;
            float angle0 = u0 * MathF.PI * 2f;
            float angle1 = u1 * MathF.PI * 2f;
            float x0 = MathF.Cos(angle0) * 0.5f;
            float z0 = MathF.Sin(angle0) * 0.5f;
            float x1 = MathF.Cos(angle1) * 0.5f;
            float z1 = MathF.Sin(angle1) * 0.5f;

            int side = mesh.VerticesCount;
            mesh.AddVertex(x0, -0.49f, z0, u0, 0, color);
            mesh.AddVertex(x1, -0.49f, z1, u1, 0, color);
            mesh.AddVertex(x1, 0.49f, z1, u1, 1, color);
            mesh.AddVertex(x0, 0.49f, z0, u0, 1, color);
            mesh.AddQuadIndices(side);

            int top = mesh.VerticesCount;
            mesh.AddVertex(0, 0.501f, 0, 0.5f, 0.5f, color);
            mesh.AddVertex(x0, 0.501f, z0, x0 + 0.5f, z0 + 0.5f, color);
            mesh.AddVertex(x1, 0.501f, z1, x1 + 0.5f, z1 + 0.5f, color);
            mesh.AddIndex(top);
            mesh.AddIndex(top + 1);
            mesh.AddIndex(top + 2);

            int bottom = mesh.VerticesCount;
            mesh.AddVertex(0, -0.501f, 0, 0.5f, 0.5f, color);
            mesh.AddVertex(x1, -0.501f, z1, x1 + 0.5f, z1 + 0.5f, color);
            mesh.AddVertex(x0, -0.501f, z0, x0 + 0.5f, z0 + 0.5f, color);
            mesh.AddIndex(bottom);
            mesh.AddIndex(bottom + 1);
            mesh.AddIndex(bottom + 2);
        }
        return mesh;
    }

    private bool StartClientAnimationPair(
        string sourceCode,
        string transitionCode,
        float speed,
        float easeOutSpeed = 8f
    )
    {
        // The transition already renders both skinned Seraph forearms. A
        // simultaneous native SelfFp/Tp clip creates a third visible hand on
        // first-person and full-body renderers, so local gesture animation is
        // deliberately suppressed for every scroll phase.
        _ = sourceCode;
        _ = transitionCode;
        _ = speed;
        _ = easeOutSpeed;
        return true;
    }

    private void StopClientAnimation(string code)
    {
        try
        {
            EntityPlayer player = capi.World.Player.Entity;
            player.TpAnimManager.StopAnimation(code);
            player.SelfFpAnimManager.StopAnimation(code);
        }
        catch
        {
            // World teardown can invalidate the player while the scene closes.
        }
    }

    private void RestorePlayerPresentation()
    {
        StopClientAnimation(BowAnimationCode);
        StopClientAnimation(HandoffAnimationCode);
        StopClientAnimation(HoldAnimationCode);

        bool cameraAnglesRestored = !cameraCaptured;
        if (cameraCaptured)
        {
            try
            {
                IClientPlayer clientPlayer = capi.World.Player;
                EntityPlayer player = capi.World.Player.Entity;
                clientPlayer.CameraPitch = savedCameraPitch;
                clientPlayer.CameraYaw = savedCameraYaw;
                clientPlayer.CameraRoll = savedCameraRoll;
                capi.Input.MousePitch = savedMousePitch;
                capi.Input.MouseYaw = savedMouseYaw;
                player.Pos.Pitch = savedEntityPitch;
                player.Pos.Yaw = savedEntityYaw;
                player.Pos.Roll = savedEntityRoll;
                player.BodyYaw = savedBodyYaw;
                player.Pos.HeadPitch = savedHeadPitch;
                player.Pos.HeadYaw = savedHeadYaw;
                cameraAnglesRestored = true;
            }
            catch
            {
                // The player may already be gone during world shutdown.
            }
            cameraCaptured = false;
        }
        cameraRestored = cameraAnglesRestored;

        bool slotsRestored = heldItemsRestored || !heldItemSlotsSuppressed;
        if (heldItemAgent != null)
        {
            try
            {
                heldItemAgent.LeftHandItemSlot = savedLeftHandItemSlot;
                heldItemAgent.RightHandItemSlot = savedRightHandItemSlot;
                slotsRestored = true;
            }
            catch
            {
                // The entity may already have been disposed during teardown.
            }
        }
        if (heldItemSlotsSuppressed)
        {
            heldItemsRestored = slotsRestored;
        }
        if (heldItemsRestored)
        {
            heldItemAgent = null;
            savedLeftHandItemSlot = null;
            savedRightHandItemSlot = null;
            hiddenLeftHandItemSlot = null;
            hiddenRightHandItemSlot = null;
        }
    }

    private void QueueFinish(bool passed)
    {
        if (finishing) return;
        finishing = true;
        RestorePlayerPresentation();
        publishAnimationPhase(AtlasScrollPhase.Stop);
        if (automatedTest && (!heldItemsRestored || !cameraRestored)) passed = false;

        if (automatedTest)
        {
            if (passed)
            {
                capi.Logger.Notification(
                    "[ModernAtlas] AUTOMATED {0} TRANSITION CHECK PASSED: stationary camera, two transition-owned first-person arms, no native local-player gesture, attached physical scroll and restored player state.",
                    closing ? "CLOSING" : "OPENING"
                );
            }
            else
            {
                capi.Logger.Error(
                    "[ModernAtlas] AUTOMATED {0} TRANSITION CHECK FAILED: cameraStationary={1}/{2}, left={3}, handoff={4}, hold={5}, hidden={6}/{7}, scroll={8}/{9}/{10}, sounds={11}/{12}/{13}, restored={14}, screenshot={15}, resources={16}, seraphForearms={17}, handAnchors={18}, phaseTimings={19}.",
                    closing ? "CLOSING" : "OPENING",
                    !cameraMoved,
                    cameraRestored,
                    bowAnimationStarted,
                    handoffAnimationStarted,
                    holdAnimationStarted,
                    heldItemSlotsSuppressed,
                    true,
                    physicalScrollRendered,
                    rolledScrollRendered,
                    openScrollRendered,
                    pocketSoundPlayed,
                    unrollSoundPlayed,
                    swooshSoundPlayed,
                    heldItemsRestored,
                    automatedScreenshotPassed,
                    resourcesReady,
                    seraphForearmMeshesReady,
                    thirdPersonHandAnchorsReady,
                    ValidateSynchronizedPhaseTimings()
                );
            }
        }

        Action<bool>? callback = completion;
        completion = null;
        capi.Event.EnqueueMainThreadTask(
            () =>
            {
                bool closed = !IsOpened() || TryClose();
                if (!closed || IsOpened())
                {
                    capi.Logger.Error(
                        "[ModernAtlas] Transition input capture remained active after its finish request; the atlas will not open over the transition."
                    );
                    passed = false;
                }
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
        bool snapshotReady = ordinaryWorldSnapshotTexture?.TextureId > 0;
        if (screenshotRequired && !snapshotReady)
        {
            capi.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED: the transition used the neutral fallback because no blurred ordinary-world snapshot was available."
            );
        }
        return resourcesReady
            && seraphForearmMeshesReady
            && thirdPersonHandAnchorsReady
            && ValidateSynchronizedPhaseTimings()
            && !cameraMoved
            && bowAnimationStarted
            && handoffAnimationStarted
            && holdAnimationStarted
            && heldItemSlotsSuppressed
            && physicalScrollRendered
            && rolledScrollRendered
            && openScrollRendered
            && pocketSoundPlayed
            && unrollSoundPlayed
            && swooshSoundPlayed
            && (!screenshotRequired
                || (snapshotReady
                    && automatedImmediateScreenshotPassed
                    && automatedPocketScreenshotPassed
                    && automatedHandoffScreenshotPassed
                    && automatedScreenshotPassed));
    }

    private bool ValidateCurrentThirdPersonHandAnchors()
    {
        EntityPlayer? player = capi.World.Player?.Entity;
        if (player == null
            || !TryGetRemoteScrollPose(player, out RemoteScrollPose pose))
        {
            return false;
        }

        float handDistance = Distance(pose.LeftHand, pose.RightHand);
        return float.IsFinite(handDistance)
            && handDistance > 0.05f
            && handDistance < 3f;
    }

    private static bool ValidateSynchronizedPhaseTimings()
    {
        const float tolerance = 0.001f;
        static bool Equal(float left, float right) =>
            Math.Abs(left - right) <= tolerance;

        float openingHandoffDuration =
            UnrollEndSeconds - HandoffStartSeconds;
        float closingReleaseDuration =
            ClosingStowStartSeconds - ClosingReleaseRightSeconds;
        float closingStowDuration =
            ClosingDurationSeconds - ClosingStowStartSeconds;

        return Equal(RemoteScrollTime(AtlasScrollPhase.Retrieve, 0), 0)
            && Equal(
                RemoteScrollTime(
                    AtlasScrollPhase.Retrieve,
                    HandoffStartSeconds
                ),
                HandoffStartSeconds
            )
            && Equal(
                RemoteScrollTime(AtlasScrollPhase.Handoff, 0),
                HandoffStartSeconds
            )
            && Equal(
                RemoteScrollTime(
                    AtlasScrollPhase.Handoff,
                    openingHandoffDuration
                ),
                UnrollEndSeconds
            )
            && Equal(
                RemoteScrollTime(AtlasScrollPhase.HoldOpen, 0),
                UnrollEndSeconds
            )
            && Equal(
                RemoteScrollTime(AtlasScrollPhase.RollClosed, 0),
                LightPhaseStartSeconds
            )
            && Equal(
                RemoteScrollTime(
                    AtlasScrollPhase.RollClosed,
                    ClosingReleaseRightSeconds
                ),
                UnrollStartSeconds
            )
            && Equal(
                RemoteScrollTime(AtlasScrollPhase.ReleaseRight, 0),
                UnrollStartSeconds
            )
            && Equal(
                RemoteScrollTime(
                    AtlasScrollPhase.ReleaseRight,
                    closingReleaseDuration
                ),
                HandoffStartSeconds
            )
            && Equal(
                RemoteScrollTime(AtlasScrollPhase.Stow, 0),
                HandoffStartSeconds
            )
            && Equal(
                RemoteScrollTime(
                    AtlasScrollPhase.Stow,
                    closingStowDuration
                ),
                ScrollAppearsSeconds
            );
    }

    private void CaptureAutomatedScreenshot(float elapsed)
    {
        if (!automatedTest) return;

        string? configuredPath = Environment.GetEnvironmentVariable(
            SmokeScreenshotEnvironmentVariable
        );
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            automatedImmediateScreenshotHandled = true;
            automatedImmediateScreenshotPassed = true;
            automatedPocketScreenshotHandled = true;
            automatedPocketScreenshotPassed = true;
            automatedHandoffScreenshotHandled = true;
            automatedHandoffScreenshotPassed = true;
            automatedScreenshotHandled = true;
            automatedScreenshotPassed = true;
            return;
        }

        string prefix = configuredPath.EndsWith(
            ".png",
            StringComparison.OrdinalIgnoreCase
        ) ? configuredPath[..^4] : configuredPath;
        string phaseName = closing ? "closing" : "opening";
        // Capture the first transition-owned GUI frame separately from the
        // later pocket/handoff probes so the first visible transition frame
        // can be compared with the complete ordinary-world frame.
        if (!automatedImmediateScreenshotHandled && elapsed >= 0.12f)
        {
            automatedImmediateScreenshotHandled = true;
            automatedImmediateScreenshotPassed = TryCaptureAutomatedScreenshot(
                $"{prefix}-{phaseName}-immediate.png"
            );
        }
        if (!automatedPocketScreenshotHandled && elapsed >= 0.66f)
        {
            automatedPocketScreenshotHandled = true;
            automatedPocketScreenshotPassed = TryCaptureAutomatedScreenshot(
                $"{prefix}-{phaseName}-pocket.png"
            );
        }
        if (!automatedHandoffScreenshotHandled && elapsed >= 1.20f)
        {
            automatedHandoffScreenshotHandled = true;
            automatedHandoffScreenshotPassed = TryCaptureAutomatedScreenshot(
                $"{prefix}-{phaseName}-handoff.png"
            );
        }
        // ClosingDurationSeconds is currently 1.50 seconds, so the old fixed
        // 1.60-second probe could never run during the reverse transition.
        // Keep the opening probe at its established point, but capture the
        // closing frame near the end of the actual phase.
        float finalScreenshotElapsed = closing
            ? Math.Max(
                1.21f,
                ClosingDurationSeconds - AutomatedFinalScreenshotMarginSeconds
            )
            : 1.60f;
        if (!automatedScreenshotHandled && elapsed >= finalScreenshotElapsed)
        {
            automatedScreenshotHandled = true;
            automatedScreenshotPassed = TryCaptureAutomatedScreenshot(
                $"{prefix}-{phaseName}.png"
            );
        }
    }

    private bool TryCaptureAutomatedScreenshot(string path)
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
                "[ModernAtlas] Saved automated physical-scroll screenshot: {0}",
                path
            );
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "[ModernAtlas] Automated opening-transition screenshot failed for {0}: {1}",
                path,
                exception.Message
            );
            return false;
        }
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

    private readonly record struct RemoteScrollState(
        AtlasScrollPhase Phase,
        long StartedTimestamp
    );

    private readonly record struct RemoteScrollPose(
        Vec3f LeftHand,
        Vec3f RightHand,
        Vec3f RightAxis,
        Vec3f UpAxis
    );
}
