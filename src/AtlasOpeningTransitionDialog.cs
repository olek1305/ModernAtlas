using System;
using System.Diagnostics;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// A short cinematic atlas-opening scene over the live world. The player's
/// full third-person model reaches into a pocket, lifts a physical scroll and
/// unrolls it before the view enters the independent atlas framebuffer.
/// </summary>
internal sealed class AtlasOpeningTransitionDialog : GuiDialog, IRenderer
{
    private const float ScrollAppearsSeconds = 0.34f;
    private const float PocketReachEndSeconds = 1.02f;
    private const float HandoffStartSeconds = 0.96f;
    private const float HoldStartSeconds = 1.64f;
    private const float UnrollStartSeconds = 1.22f;
    private const float UnrollEndSeconds = 2.30f;
    private const float LightPhaseStartSeconds = 2.58f;
    private const float TotalDurationSeconds = 3.34f;
    private const float PreparationTimeoutSeconds = 9f;
    private const string BowAnimationCode = "modernatlas-opening-pocket-reach";
    private const string HandoffAnimationCode = "modernatlas-opening-handoff";
    private const string HoldAnimationCode = "modernatlas-opening-scroll-hold";
    private const string SmokeScreenshotEnvironmentVariable =
        "MODERNATLAS_SMOKE_SCREENSHOT";

    private readonly Func<bool> prepareAtlasResources;
    private readonly Func<IShaderProgram?> getScrollShader;
    private LoadedTexture solidTexture;
    private MeshRef? scrollSheetMesh;
    private MeshRef? scrollCylinderMesh;

    private Action<bool>? completion;
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
    private bool cinematicCameraApplied;
    private bool automatedPocketScreenshotHandled;
    private bool automatedPocketScreenshotPassed;
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
    private EntityRenderer? heldItemRenderer;
    private FieldInfo? renderHeldItemField;
    private bool savedRenderHeldItem;
    private bool heldItemSlotsSuppressed;
    private bool rendererHeldItemSuppressed;
    private bool heldItemsRestored;
    private bool cameraRestored;
    private FieldInfo? overrideCameraModeField;
    private object? savedOverrideCameraMode;
    private bool overrideCameraModeCaptured;
    private object? engineCamera;
    private FieldInfo? engineCameraModeField;
    private object? savedEngineCameraMode;
    private FieldInfo? thirdPersonDistanceField;
    private object? savedThirdPersonDistance;
    private FieldInfo? targetCameraDistanceField;
    private object? savedTargetCameraDistance;
    private bool engineCameraCaptured;
    private bool disposed;

    public override string ToggleKeyCombinationCode => "";
    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override double DrawOrder => 0.99;
    public override double InputOrder => 0.01;
    public override bool PrefersUngrabbedMouse => false;
    public override bool DisableMouseGrab => false;
    // Draw immediately before the normal entity pass. The scroll writes the
    // same world-depth buffer, then the player's nearer hands can naturally
    // appear in front of it instead of the scroll covering the model.
    public double RenderOrder => 0.39;
    public int RenderRange => int.MaxValue;

    public AtlasOpeningTransitionDialog(
        ICoreClientAPI capi,
        Func<bool> prepareAtlasResources,
        Func<IShaderProgram?> getScrollShader
    ) : base(capi)
    {
        this.prepareAtlasResources = prepareAtlasResources;
        this.getScrollShader = getScrollShader;
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
        cinematicCameraApplied = false;
        automatedPocketScreenshotHandled = false;
        automatedPocketScreenshotPassed = false;
        automatedHandoffScreenshotHandled = false;
        automatedHandoffScreenshotPassed = false;
        automatedScreenshotHandled = false;
        automatedScreenshotPassed = false;
        heldItemSlotsSuppressed = false;
        rendererHeldItemSuppressed = false;
        heldItemsRestored = false;
        cameraRestored = false;
        overrideCameraModeField = null;
        savedOverrideCameraMode = null;
        overrideCameraModeCaptured = false;
        engineCamera = null;
        engineCameraModeField = null;
        savedEngineCameraMode = null;
        thirdPersonDistanceField = null;
        savedThirdPersonDistance = null;
        targetCameraDistanceField = null;
        savedTargetCameraDistance = null;
        engineCameraCaptured = false;
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
        EnsureRenderResources();
        CapturePlayerPresentation();
        startedTimestamp = Stopwatch.GetTimestamp();
        bowAnimationStarted = StartClientAnimation(
            "bow",
            BowAnimationCode,
            1.0f
        );
        capi.Logger.Notification(
            "[ModernAtlas] Started the physical scroll opening transition."
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
        MaintainHeldItemSuppression();
        UpdatePlayerPresentation(elapsed);
        AdvanceAnimationAndSound(elapsed);
        RenderFinalAtlasEntry(elapsed);
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

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.Opaque || finishing || !IsOpened()) return;

        float elapsed = (float)Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds;
        // Entity control updates can overwrite BodyYaw after the GUI update.
        // Reapply the cinematic facing immediately before the entity render
        // order so the real player model holds the scroll toward the camera.
        UpdatePlayerPresentation(elapsed);
        physicalScrollRendered |= RenderPhysicalScroll(elapsed);
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
            ApplyCinematicCamera(clientPlayer);
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

    private void ApplyCinematicCamera(IClientPlayer clientPlayer)
    {
        overrideCameraModeField = FindField(
            clientPlayer.GetType(),
            "OverrideCameraMode"
        );
        Type? overrideType = overrideCameraModeField?.FieldType;
        Type? cameraModeType = overrideType == null
            ? null
            : Nullable.GetUnderlyingType(overrideType) ?? overrideType;
        FieldInfo? gameField = FindField(capi.GetType(), "game");
        object? game = gameField?.GetValue(capi);
        FieldInfo? mainCameraField = game == null
            ? null
            : FindField(game.GetType(), "MainCamera");
        engineCamera = mainCameraField?.GetValue(game);
        engineCameraModeField = engineCamera == null
            ? null
            : FindField(engineCamera.GetType(), "CameraMode");
        thirdPersonDistanceField = engineCamera == null
            ? null
            : FindField(engineCamera.GetType(), "Tppcameradistance");
        targetCameraDistanceField = engineCamera == null
            ? null
            : FindField(engineCamera.GetType(), "targetCameraDistance");

        if (cameraModeType != typeof(EnumCameraMode)
            || engineCameraModeField?.FieldType != typeof(EnumCameraMode)
            || thirdPersonDistanceField?.FieldType != typeof(float)
            || targetCameraDistanceField?.FieldType != typeof(float))
        {
            capi.Logger.Warning(
                "[ModernAtlas] The cinematic third-person camera is unavailable; preserving the current camera mode."
            );
            cinematicCameraApplied = false;
            return;
        }

        savedOverrideCameraMode = overrideCameraModeField!.GetValue(clientPlayer);
        savedEngineCameraMode = engineCameraModeField.GetValue(engineCamera);
        savedThirdPersonDistance = thirdPersonDistanceField.GetValue(engineCamera);
        savedTargetCameraDistance = targetCameraDistanceField.GetValue(engineCamera);
        overrideCameraModeCaptured = true;
        engineCameraCaptured = true;
        overrideCameraModeField.SetValue(clientPlayer, EnumCameraMode.ThirdPerson);
        engineCameraModeField.SetValue(engineCamera, EnumCameraMode.ThirdPerson);
        thirdPersonDistanceField.SetValue(engineCamera, 3.25f);
        targetCameraDistanceField.SetValue(engineCamera, 3.25f);
        cinematicCameraApplied = true;
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

        EntityRenderer? renderer = player.Properties.Client.Renderer;
        if (renderer == null) return;

        heldItemRenderer = renderer;
        renderHeldItemField = FindField(renderer.GetType(), "DoRenderHeldItem");
        if (renderHeldItemField?.FieldType != typeof(bool)) return;

        savedRenderHeldItem = (bool)(renderHeldItemField.GetValue(renderer) ?? true);
        renderHeldItemField.SetValue(renderer, false);
        rendererHeldItemSuppressed = true;
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
            if (renderHeldItemField != null && heldItemRenderer != null)
            {
                renderHeldItemField.SetValue(heldItemRenderer, false);
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

        float bowAmount;
        if (elapsed <= PocketReachEndSeconds)
        {
            bowAmount = SmoothStep(elapsed / 0.58f);
        }
        else
        {
            bowAmount = 1f - SmoothStep(
                (elapsed - PocketReachEndSeconds) / 0.70f
            );
        }
        bowAmount = Math.Clamp(bowAmount, 0f, 1f);
        try
        {
            IClientPlayer clientPlayer = capi.World.Player;
            EntityPlayer player = capi.World.Player.Entity;
            float cameraBlend = SmoothStep(elapsed / 0.46f);
            float targetYaw = savedEntityYaw + 0.30f;
            float targetPitch = 0.10f + bowAmount * 0.055f;
            float targetRoll = -bowAmount * 0.025f;
            float cameraYaw = LerpAngle(savedCameraYaw, targetYaw, cameraBlend);
            float cameraPitch = Lerp(savedCameraPitch, targetPitch, cameraBlend);
            float cameraRoll = Lerp(savedCameraRoll, targetRoll, cameraBlend);
            float bodyBlend = SmoothStep(elapsed / 0.34f);
            float bodyYaw = LerpAngle(
                savedBodyYaw,
                targetYaw + MathF.PI,
                bodyBlend
            );

            clientPlayer.CameraYaw = cameraYaw;
            clientPlayer.CameraPitch = cameraPitch;
            clientPlayer.CameraRoll = cameraRoll;
            capi.Input.MouseYaw = cameraYaw;
            capi.Input.MousePitch = cameraPitch;

            // Face the model toward the cinematic camera while the full-body
            // clips bend its torso, neck and head. Rotating BodyYaw keeps the
            // skeleton intact; the former first-person arm clips did not.
            player.Pos.Pitch = savedEntityPitch;
            player.Pos.Yaw = bodyYaw;
            player.Pos.Roll = savedEntityRoll;
            player.BodyYaw = bodyYaw;
            player.Pos.HeadPitch = savedHeadPitch;
            player.Pos.HeadYaw = bodyYaw;
            cameraMoved |= cameraBlend >= 0.95f && bowAmount >= 0.75f;
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
            pocketSoundPlayed = PlayLocalSound("sounds/block/cloth", 0.48f);
        }

        if (!handoffPhaseStarted && elapsed >= HandoffStartSeconds)
        {
            handoffPhaseStarted = true;
            StopClientAnimation(BowAnimationCode);
            handoffAnimationStarted = StartClientAnimation(
                "twohandplaceblock",
                HandoffAnimationCode,
                0.80f
            );
        }

        if (!unrollSoundAttempted && elapsed >= UnrollStartSeconds)
        {
            unrollSoundAttempted = true;
            unrollSoundPlayed = PlayLocalSound("sounds/block/cloth", 0.62f);
        }

        if (!holdPhaseStarted && elapsed >= HoldStartSeconds)
        {
            holdPhaseStarted = true;
            StopClientAnimation(HandoffAnimationCode);
            holdAnimationStarted = StartClientAnimation(
                "holdbothhandslarge",
                HoldAnimationCode,
                1.0f
            );
        }

        if (!swooshSoundAttempted && elapsed >= LightPhaseStartSeconds)
        {
            swooshSoundAttempted = true;
            swooshSoundPlayed = PlayLocalSound("sounds/effect/swoosh", 0.78f);
        }
    }

    private bool RenderPhysicalScroll(float elapsed)
    {
        if (elapsed < ScrollAppearsSeconds || !EnsureRenderResources()) return false;

        IShaderProgram? shader = getScrollShader();
        if (shader == null || shader.Disposed
            || scrollSheetMesh == null
            || scrollCylinderMesh == null)
        {
            return false;
        }

        float visible = SmoothStep((elapsed - ScrollAppearsSeconds) / 0.20f);
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
        Vec3f playerAnchor = TransformPlayerLocalPoint(
            render.CameraMatrixOriginf,
            0,
            1.24f,
            0
        );
        float anchoredX = playerAnchor.X + Lerp(0.29f, 0f, lift);
        float anchoredY = playerAnchor.Y + Lerp(-0.43f, 0.01f, lift);
        float anchoredZ = playerAnchor.Z + Lerp(0.16f, 0.24f, lift);
        float centerX = Lerp(anchoredX, 0.02f, dive);
        float centerY = Lerp(anchoredY, 0.02f, dive);
        float centerZ = Lerp(anchoredZ, -0.34f, dive);
        float parentScale = 1f + dive * 4.25f;
        float rotationX = Lerp(0.24f, -0.08f, lift) * (1f - dive);
        float rotationY = Lerp(-0.20f, 0.03f, lift) * (1f - dive);
        float rotationZ = Lerp(-0.34f, -0.025f, lift) * (1f - dive);
        float scrollWidth = Lerp(0.11f, 0.82f, unroll);
        float scrollHeight = Lerp(0.42f, 0.52f, lift);
        float sweep = SmoothStep(
            (elapsed - LightPhaseStartSeconds)
                / (TotalDurationSeconds - LightPhaseStartSeconds)
        );

        float[] projection = render.CurrentProjectionMatrix;

        float[] parent = Mat4f.Create();
        Mat4f.Translate(parent, parent, centerX, centerY, centerZ);
        Mat4f.RotateZ(parent, parent, rotationZ);
        Mat4f.RotateY(parent, parent, rotationY);
        Mat4f.RotateX(parent, parent, rotationX);
        Mat4f.Scale(parent, parent, parentScale, parentScale, parentScale);

        render.CurrentActiveShader?.Stop();
        shader.Use();
        shader.UniformMatrix("projectionMatrix", projection);
        render.GLDepthMask(visible >= 0.96f);
        render.GLEnableDepthTest();
        render.GlDisableCullFace();
        render.GlToggleBlend(true, EnumBlendMode.Standard);
        try
        {
            float sheetAlpha = visible * SmoothStep(unroll / 0.18f);
            if (sheetAlpha > 0.001f)
            {
                float[] sheetModel = CreateComponentModel(
                    parent,
                    0,
                    0,
                    0,
                    Math.Max(0.035f, scrollWidth - 0.06f),
                    scrollHeight,
                    1f
                );
                RenderComponent(
                    shader,
                    scrollSheetMesh,
                    sheetModel,
                    0,
                    sheetAlpha,
                    sweep
                );
            }

            float rollerOffset = scrollWidth * 0.5f;
            float remainingRoll = 1f - unroll;
            if (remainingRoll > 0.015f)
            {
                float rollRadius = 0.055f + remainingRoll * 0.060f;
                float[] paperRollModel = CreateComponentModel(
                    parent,
                    rollerOffset,
                    0,
                    0,
                    rollRadius * 2f,
                    scrollHeight,
                    rollRadius * 2f
                );
                RenderComponent(
                    shader,
                    scrollCylinderMesh,
                    paperRollModel,
                    3,
                    visible,
                    sweep
                );
            }

            RenderRoller(
                shader,
                parent,
                rollerOffset,
                scrollHeight,
                visible,
                sweep
            );
            if (unroll > 0.025f)
            {
                RenderRoller(
                    shader,
                    parent,
                    -rollerOffset,
                    scrollHeight,
                    visible * SmoothStep(unroll / 0.20f),
                    sweep
                );
            }
        }
        finally
        {
            shader.Stop();
            render.GlEnableCullFace();
            render.GLEnableDepthTest();
            render.GLDepthMask(true);
            render.GlToggleBlend(false, EnumBlendMode.Standard);
        }

        rolledScrollRendered |= elapsed < UnrollStartSeconds + 0.18f;
        openScrollRendered |= unroll >= 0.78f;
        return true;
    }

    private static Vec3f TransformPlayerLocalPoint(
        float[] matrix,
        float x,
        float y,
        float z
    )
    {
        if (matrix.Length < 16) return new Vec3f(0, -0.18f, -2.8f);

        return new Vec3f(
            matrix[0] * x + matrix[4] * y + matrix[8] * z + matrix[12],
            matrix[1] * x + matrix[5] * y + matrix[9] * z + matrix[13],
            matrix[2] * x + matrix[6] * y + matrix[10] * z + matrix[14]
        );
    }

    private void RenderRoller(
        IShaderProgram shader,
        float[] parent,
        float x,
        float sheetHeight,
        float alpha,
        float sweep
    )
    {
        if (scrollCylinderMesh == null || alpha <= 0.001f) return;

        float rodHeight = sheetHeight + 0.18f;
        float[] rodModel = CreateComponentModel(
            parent,
            x,
            0,
            0.004f,
            0.046f,
            rodHeight,
            0.046f
        );
        RenderComponent(shader, scrollCylinderMesh, rodModel, 1, alpha, sweep);

        float knobY = rodHeight * 0.5f + 0.035f;
        RenderComponent(
            shader,
            scrollCylinderMesh,
            CreateComponentModel(parent, x, knobY, 0.004f, 0.095f, 0.060f, 0.095f),
            2,
            alpha,
            sweep
        );
        RenderComponent(
            shader,
            scrollCylinderMesh,
            CreateComponentModel(parent, x, -knobY, 0.004f, 0.095f, 0.060f, 0.095f),
            2,
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
        float scaleZ
    )
    {
        float[] model = Mat4f.CloneIt(parent);
        Mat4f.Translate(model, model, x, y, z);
        Mat4f.Scale(model, model, scaleX, scaleY, scaleZ);
        return model;
    }

    private void RenderFinalAtlasEntry(float elapsed)
    {
        float entry = SmoothStep(
            (elapsed - (TotalDurationSeconds - 0.16f)) / 0.16f
        );
        if (entry <= 0 || solidTexture.TextureId <= 0) return;

        capi.Render.CurrentActiveShader?.Stop();
        capi.Render.GetEngineShader(EnumShaderProgram.Gui).Use();
        capi.Render.GLDepthMask(false);
        capi.Render.GLDisableDepthTest();
        capi.Render.GlToggleBlend(true, EnumBlendMode.Standard);
        capi.Render.Render2DTexture(
            solidTexture.TextureId,
            0,
            0,
            capi.Render.FrameWidth,
            capi.Render.FrameHeight,
            98,
            new Vec4f(0.72f, 0.78f, 0.68f, entry)
        );
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

        if (scrollSheetMesh != null && scrollCylinderMesh != null) return true;

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
        const int horizontalSegments = 18;
        const int verticalSegments = 5;
        MeshData mesh = new(horizontalSegments * verticalSegments * 4);
        int color = unchecked((int)0xffffffff);

        for (int yIndex = 0; yIndex < verticalSegments; yIndex++)
        {
            float v0 = yIndex / (float)verticalSegments;
            float v1 = (yIndex + 1) / (float)verticalSegments;
            float y0 = v0 - 0.5f;
            float y1 = v1 - 0.5f;
            for (int xIndex = 0; xIndex < horizontalSegments; xIndex++)
            {
                float u0 = xIndex / (float)horizontalSegments;
                float u1 = (xIndex + 1) / (float)horizontalSegments;
                float x0 = u0 - 0.5f;
                float x1 = u1 - 0.5f;
                int first = mesh.VerticesCount;
                mesh.AddVertex(x0, y0, SheetDepth(x0, y0), u0, v0, color);
                mesh.AddVertex(x1, y0, SheetDepth(x1, y0), u1, v0, color);
                mesh.AddVertex(x1, y1, SheetDepth(x1, y1), u1, v1, color);
                mesh.AddVertex(x0, y1, SheetDepth(x0, y1), u0, v1, color);
                mesh.AddQuadIndices(first);
            }
        }

        return mesh;
    }

    private static float SheetDepth(float x, float y)
    {
        float edgeCurl = MathF.Pow(MathF.Abs(x) * 2f, 3f) * 0.075f;
        float centerSag = (1f - MathF.Abs(x) * 2f) * (0.5f - MathF.Abs(y)) * -0.018f;
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
            return player.TpAnimManager.StartAnimation(animation);
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
        bool overrideCameraModeRestored = !overrideCameraModeCaptured;
        bool engineCameraRestored = !engineCameraCaptured;
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
        if (overrideCameraModeCaptured && overrideCameraModeField != null)
        {
            try
            {
                overrideCameraModeField.SetValue(
                    capi.World.Player,
                    savedOverrideCameraMode
                );
                overrideCameraModeRestored = true;
            }
            catch
            {
                // The client player may already be gone during world shutdown.
            }
        }
        if (engineCameraCaptured
            && engineCamera != null
            && engineCameraModeField != null
            && thirdPersonDistanceField != null
            && targetCameraDistanceField != null)
        {
            try
            {
                engineCameraModeField.SetValue(engineCamera, savedEngineCameraMode);
                thirdPersonDistanceField.SetValue(
                    engineCamera,
                    savedThirdPersonDistance
                );
                targetCameraDistanceField.SetValue(
                    engineCamera,
                    savedTargetCameraDistance
                );
                engineCameraRestored = true;
            }
            catch
            {
                // The engine camera may already be gone during world shutdown.
            }
        }
        cameraRestored = cameraAnglesRestored
            && overrideCameraModeRestored
            && engineCameraRestored;
        overrideCameraModeCaptured = false;
        overrideCameraModeField = null;
        savedOverrideCameraMode = null;
        engineCameraCaptured = false;
        engineCamera = null;
        engineCameraModeField = null;
        savedEngineCameraMode = null;
        thirdPersonDistanceField = null;
        savedThirdPersonDistance = null;
        targetCameraDistanceField = null;
        savedTargetCameraDistance = null;

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
        bool rendererRestored = heldItemsRestored || !rendererHeldItemSuppressed;
        if (renderHeldItemField != null && heldItemRenderer != null)
        {
            try
            {
                renderHeldItemField.SetValue(heldItemRenderer, savedRenderHeldItem);
                rendererRestored = true;
            }
            catch
            {
                // The renderer may already have been released.
            }
        }

        if (heldItemSlotsSuppressed || rendererHeldItemSuppressed)
        {
            heldItemsRestored = slotsRestored && rendererRestored;
        }
        if (heldItemsRestored)
        {
            heldItemAgent = null;
            savedLeftHandItemSlot = null;
            savedRightHandItemSlot = null;
            hiddenLeftHandItemSlot = null;
            hiddenRightHandItemSlot = null;
            heldItemRenderer = null;
            renderHeldItemField = null;
        }
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
        if (automatedTest && (!heldItemsRestored || !cameraRestored)) passed = false;

        if (automatedTest)
        {
            if (passed)
            {
                capi.Logger.Notification(
                    "[ModernAtlas] AUTOMATED OPENING TRANSITION CHECK PASSED: full-player pocket reach, hidden held items, physical rolled scroll, two-handed unroll, restored camera/player state and atlas preparation completed."
                );
            }
            else
            {
                capi.Logger.Error(
                    "[ModernAtlas] AUTOMATED OPENING TRANSITION CHECK FAILED: camera={0}/{1}, bow={2}, handoff={3}, hold={4}, hidden={5}/{6}, scroll={7}/{8}/{9}, sounds={10}/{11}/{12}, restored={13}, screenshot={14}, resources={15}.",
                    cinematicCameraApplied,
                    cameraRestored,
                    bowAnimationStarted,
                    handoffAnimationStarted,
                    holdAnimationStarted,
                    heldItemSlotsSuppressed,
                    rendererHeldItemSuppressed,
                    physicalScrollRendered,
                    rolledScrollRendered,
                    openScrollRendered,
                    pocketSoundPlayed,
                    unrollSoundPlayed,
                    swooshSoundPlayed,
                    heldItemsRestored,
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
            && cinematicCameraApplied
            && bowAnimationStarted
            && handoffAnimationStarted
            && holdAnimationStarted
            && heldItemSlotsSuppressed
            && rendererHeldItemSuppressed
            && physicalScrollRendered
            && rolledScrollRendered
            && openScrollRendered
            && pocketSoundPlayed
            && unrollSoundPlayed
            && swooshSoundPlayed
            && (!screenshotRequired
                || (automatedPocketScreenshotPassed
                    && automatedHandoffScreenshotPassed
                    && automatedScreenshotPassed));
    }

    private void CaptureAutomatedScreenshot(float elapsed)
    {
        if (!automatedTest) return;

        string? configuredPath = Environment.GetEnvironmentVariable(
            SmokeScreenshotEnvironmentVariable
        );
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
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
        if (!automatedPocketScreenshotHandled && elapsed >= 0.72f)
        {
            automatedPocketScreenshotHandled = true;
            automatedPocketScreenshotPassed = TryCaptureAutomatedScreenshot(
                $"{prefix}-opening-pocket.png"
            );
        }
        if (!automatedHandoffScreenshotHandled && elapsed >= 1.48f)
        {
            automatedHandoffScreenshotHandled = true;
            automatedHandoffScreenshotPassed = TryCaptureAutomatedScreenshot(
                $"{prefix}-opening-handoff.png"
            );
        }
        if (!automatedScreenshotHandled && elapsed >= 2.12f)
        {
            automatedScreenshotHandled = true;
            automatedScreenshotPassed = TryCaptureAutomatedScreenshot(
                $"{prefix}-opening.png"
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

    private static FieldInfo? FindField(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(
                name,
                BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly
            );
            if (field != null) return field;
        }
        return null;
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

    private static float LerpAngle(float from, float to, float amount)
    {
        float difference = GameMath.Mod(to - from + MathF.PI, MathF.PI * 2f)
            - MathF.PI;
        return from + difference * Math.Clamp(amount, 0f, 1f);
    }
}
