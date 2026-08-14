using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// A local, view-space compass scene for the atlas. Its only live input is
/// the interpolated atlas yaw; it never reads chunks or entities other than
/// the local player's already-loaded skin used for the forearm.
/// </summary>
internal sealed class AtlasCompassRenderer : IDisposable
{
    private const float TravelSeconds = 0.28f;
    private const float TossDurationSeconds = 0.62f;
    private const float HousingScale = 0.3825f;
    private const float OutlineScale = 0.3435f;
    private const float DialScale = 0.297f;
    private const float CompassNeedleScale = 0.235f;
    private const float SundialIndicatorScale = 0.300f;
    private readonly ICoreClientAPI capi;
    private readonly Func<IShaderProgram?> shaderProvider;
    private readonly SeraphForearmRenderer forearms;
    private MeshRef? housingMesh;
    private MeshRef? outlineMesh;
    private MeshRef? faceMesh;
    private MeshRef? needleMesh;
    private MeshRef? sundialShadowMesh;
    private MeshRef? sundialGnomonMesh;
    private LoadedTexture? compassDialTexture;
    private LoadedTexture? sundialDialTexture;
    private float visibility;
    private bool targetVisible;
    private bool stowingForClose;
    private bool forearmsPrepared;
    private float openingSeconds = -1f;
    private float presentationSeconds;
    private float cameraShakeEnergy;
    private float lastYawDegrees;
    private float lastYawDirection;
    private bool yawSampled;
    private Action? stowed;

    public AtlasCompassRenderer(ICoreClientAPI capi, Func<IShaderProgram?> shaderProvider)
    {
        this.capi = capi;
        this.shaderProvider = shaderProvider;
        forearms = new SeraphForearmRenderer(capi);
    }

    public void SetEnabled(bool enabled)
    {
        if (stowingForClose) return;
        if (enabled && !targetVisible)
        {
            // A short authored entrance: hand enters, flicks the compass up,
            // then catches it at the fixed lower-right presentation pose.
            openingSeconds = 0;
        }
        if (!enabled) openingSeconds = -1f;
        targetVisible = enabled;
    }

    public void ResetForAtlasOpen(bool enabled)
    {
        stowingForClose = false;
        stowed = null;
        targetVisible = enabled;
        openingSeconds = enabled ? 0 : -1f;
        presentationSeconds = 0;
        cameraShakeEnergy = 0;
        yawSampled = false;
    }

    public bool BeginStowingForClose(Action onStowed)
    {
        if (stowingForClose) return true;
        if (visibility <= 0.002f && !targetVisible) return false;
        stowingForClose = true;
        targetVisible = false;
        openingSeconds = -1f;
        stowed = onStowed;
        return true;
    }

    public void Render(
        bool enabled,
        bool timeMode,
        float atlasHour,
        bool sundialShowsTime,
        float atlasYawDegrees,
        AtlasViewportBounds viewport,
        float realDeltaTime
    )
    {
        SetEnabled(enabled);
        presentationSeconds += realDeltaTime;
        UpdateCameraMotion(atlasYawDegrees, realDeltaTime);
        if (openingSeconds >= 0)
        {
            openingSeconds += realDeltaTime;
            if (openingSeconds > TossDurationSeconds) openingSeconds = -1f;
        }
        float step = Math.Clamp(realDeltaTime / TravelSeconds, 0f, 1f);
        visibility = MoveTowards(visibility, targetVisible ? 1f : 0f, step);
        if (stowingForClose && visibility <= 0.002f)
        {
            FinishStowing();
            return;
        }
        if (visibility <= 0.002f) return;
        if (!EnsureMeshes()) return;

        IShaderProgram? shader = shaderProvider();
        LoadedTexture? selectedDialTexture = timeMode
            ? sundialDialTexture
            : compassDialTexture;
        if (shader == null
            || shader.Disposed
            || housingMesh == null
            || faceMesh == null
            || needleMesh == null
            || sundialShadowMesh == null
            || sundialGnomonMesh == null
            || selectedDialTexture == null
            || selectedDialTexture.TextureId <= 0)
        {
            return;
        }

        IRenderAPI render = capi.Render;
        float frameHeight = Math.Max(1, render.FrameHeight);
        float aspect = render.FrameWidth / frameHeight;
        float[] projection = Mat4f.Create();
        Mat4f.Ortho(projection, -aspect, aspect, -1f, 1f, -4f, 4f);

        // The wrist begins beyond the lower-right corner and settles into a
        // fixed screen-space pose. Ease only changes that view-space pose;
        // atlas panning and pitch have no influence on the hand or housing.
        float visible = SmoothStep(visibility);
        float shownGripX = ScreenToViewX(viewport.Right - viewport.Width * 0.135f, aspect);
        float shownGripY = ScreenToViewY(viewport.Bottom - viewport.Height * 0.15f);
        float hiddenGripX = ScreenToViewX(viewport.Right + viewport.Width * 0.23f, aspect);
        float hiddenGripY = ScreenToViewY(viewport.Bottom + viewport.Height * 0.18f);
        float gripX = Lerp(hiddenGripX, shownGripX, visible);
        float gripY = Lerp(hiddenGripY, shownGripY, visible);
        float compassX = gripX;
        float compassY = gripY;
        float tossFlip = 0;
        float airborne = 0;
        float dialVisibility = openingSeconds >= 0 ? 0f : 1f;
        if (openingSeconds >= 0)
        {
            float flickX = shownGripX - 0.15f;
            float flickY = shownGripY + 0.14f;
            float handReach = SmoothStep(openingSeconds / 0.21f);
            float handCatch = SmoothStep((openingSeconds - 0.22f) / 0.30f);
            gripX = openingSeconds < 0.22f
                ? Lerp(hiddenGripX, flickX, handReach)
                : Lerp(flickX, shownGripX, handCatch);
            gripY = openingSeconds < 0.22f
                ? Lerp(hiddenGripY, flickY, handReach)
                : Lerp(flickY, shownGripY, handCatch);

            float flight = SmoothStep((openingSeconds - 0.19f) / 0.39f);
            if (openingSeconds >= 0.19f)
            {
                float arc = MathF.Sin(flight * MathF.PI);
                airborne = arc;
                compassX = Lerp(flickX, shownGripX, flight) - arc * 0.10f;
                compassY = Lerp(flickY, shownGripY, flight) + arc * 0.24f;
                // First revolution exposes only the back of the casing.
                // The textured face is introduced as the second turn starts.
                tossFlip = flight * MathF.PI * 4f;
                float secondTurn = SmoothStep((flight - 0.50f) / 0.16f);
                float frontFacing = Math.Max(0f, MathF.Cos(tossFlip));
                dialVisibility = secondTurn * frontFacing;
            }
            else
            {
                compassX = gripX;
                compassY = gripY;
            }
        }

        float shakePhase = presentationSeconds * 29f;
        float shakeAmount = cameraShakeEnergy * (openingSeconds >= 0 ? 0.35f : 1f);
        float shakeX = MathF.Sin(shakePhase) * shakeAmount * 0.032f * lastYawDirection;
        float shakeY = MathF.Cos(shakePhase * 0.78f) * shakeAmount * 0.020f;
        gripX += shakeX;
        gripY += shakeY;
        compassX += shakeX;
        compassY += shakeY;
        float elbowX = Lerp(hiddenGripX + 0.20f, shownGripX + 0.38f, visible) + shakeX * 0.45f;
        float elbowY = Lerp(hiddenGripY - 0.20f, shownGripY - 0.42f, visible) + shakeY * 0.45f;
        float alpha = Math.Clamp(visibility * 1.5f, 0f, 1f);

        render.CurrentActiveShader?.Stop();
        // The atlas has already selected either the window or its completed
        // presentation target. Do not change that framebuffer here: doing so
        // can discard the just-composed parchment on some drivers.
        render.GLDisableDepthTest();
        render.GLDepthMask(false);
        render.GlDisableCullFace();
        render.GlToggleBlend(true, EnumBlendMode.Standard);
        bool clipped = false;
        try
        {
            if (viewport.X > 0 || viewport.Y > 0 || viewport.Right < render.FrameWidth || viewport.Bottom < render.FrameHeight)
            {
                render.GlScissor(viewport.X, Math.Max(0, render.FrameHeight - viewport.Bottom), viewport.Width, viewport.Height);
                render.GlScissorFlag(true);
                clipped = true;
            }
            shader.Use();
            shader.UniformMatrix("projectionMatrix", projection);
            shader.BindTexture2D("compassTex", selectedDialTexture.TextureId, 1);
            if (!forearmsPrepared)
            {
                forearmsPrepared = forearms.Prepare();
            }
            if (forearms.IsReady)
            {
                forearms.BindSkin(shader);
                forearms.RenderRightArm(shader, Mat4f.Create(), elbowX, elbowY, gripX, gripY, alpha);
            }

            // Both instrument modes use the same hand-made wooden housing.
            // Only the simple face and its functional indicator change, so
            // the compass and sundial read as parts of one traveller's kit.
            // Rotate every physical layer from one shared instrument pose.
            // The old casing used a slow Z spin while the dial flipped around
            // Y, which separated the circular layers and looked like several
            // ghost housings during the toss.
            float[] instrument = Mat4f.Create();
            Mat4f.Translate(instrument, instrument, compassX, compassY, -0.18f);
            Mat4f.RotateY(instrument, instrument, tossFlip);

            // A single flat wooden shell is intentional here. A closed
            // cylinder has several overlapping caps and side faces, while
            // this late GUI pass cannot reuse the world's depth buffer. Seen
            // edge-on during the toss, those faces looked like ghost copies.
            float[] body = Mat4f.CloneIt(instrument);
            RenderComponent(shader, housingMesh, body, 9, HousingScale, alpha);

            float[] dial = Mat4f.CloneIt(instrument);
            // Keep the face slightly above the wooden cylinder in the
            // instrument's own space, so that offset follows the same flip.
            Mat4f.Translate(dial, dial, 0, 0, 0.120f);
            float dialAlpha = alpha * dialVisibility;
            // The charred ring belongs to the front face. Hiding it with the
            // face on the back turn leaves exactly one visible wooden casing.
            RenderComponent(shader, outlineMesh!, dial, 12, OutlineScale, dialAlpha);
            RenderComponent(shader, faceMesh, dial, 10, DialScale, dialAlpha);
            float indicatorAlpha = dialAlpha * (1f - airborne * 0.35f);
            if (timeMode)
            {
                // A sundial cannot disclose a time while the sun is down.
                // Keep the physical gnomon, but draw its hour shadow only in
                // the explicitly supported 06:00-18:00 daylight interval.
                if (sundialShowsTime)
                {
                    float[] shadow = Mat4f.CloneIt(dial);
                    // The dial's authored hour-line origin is slightly below
                    // its geometric center. Rotate a local ray first, then
                    // place that pivot exactly on the gnomon foot.
                    Mat4f.Translate(
                        shadow,
                        shadow,
                        0,
                        -0.047f * SundialIndicatorScale,
                        0.006f
                    );
                    Mat4f.RotateZ(
                        shadow,
                        shadow,
                        SundialShadowRotationRadians(atlasHour) + tossFlip
                    );
                    RenderComponent(
                        shader,
                        sundialShadowMesh,
                        shadow,
                        13,
                        SundialIndicatorScale,
                        indicatorAlpha * 0.88f
                    );
                }

                float[] gnomon = Mat4f.CloneIt(dial);
                Mat4f.Translate(gnomon, gnomon, 0, 0, 0.012f);
                RenderComponent(
                    shader,
                    sundialGnomonMesh,
                    gnomon,
                    14,
                    SundialIndicatorScale,
                    indicatorAlpha
                );
            }
            else
            {
                float[] needle = Mat4f.CloneIt(dial);
                Mat4f.Translate(needle, needle, 0, 0, 0.008f);
                // During the toss the needle is physically carried by the case.
                // Fade it while the face turns edge-on so it cannot look painted
                // above the rear of the spinning compass.
                Mat4f.RotateZ(
                    needle,
                    needle,
                    NeedleRotationRadians(atlasYawDegrees) + tossFlip
                );
                RenderComponent(
                    shader,
                    needleMesh,
                    needle,
                    11,
                    CompassNeedleScale,
                    indicatorAlpha
                );
            }
        }
        finally
        {
            if (clipped) render.GlScissorFlag(false);
            shader.Stop();
            render.GlEnableCullFace();
            render.GLEnableDepthTest();
            render.GLDepthMask(false);
            render.GlToggleBlend(true, EnumBlendMode.Standard);
            render.GetEngineShader(EnumShaderProgram.Gui).Use();
        }
    }

    private bool EnsureMeshes()
    {
        if (housingMesh != null
            && outlineMesh != null
            && faceMesh != null
            && needleMesh != null
            && sundialShadowMesh != null
            && sundialGnomonMesh != null
            && compassDialTexture?.TextureId > 0
            && sundialDialTexture?.TextureId > 0)
        {
            return true;
        }
        try
        {
            housingMesh ??= capi.Render.UploadMesh(CreateDiscMesh(48));
            outlineMesh ??= capi.Render.UploadMesh(CreateDiscMesh(32));
            faceMesh ??= capi.Render.UploadMesh(CreateQuadMesh());
            needleMesh ??= capi.Render.UploadMesh(CreateNeedleMesh());
            sundialShadowMesh ??= capi.Render.UploadMesh(CreateSundialShadowMesh());
            sundialGnomonMesh ??= capi.Render.UploadMesh(CreateSundialGnomonMesh());
            EnsureDialTextures();
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error("[ModernAtlas] Could not create the atlas compass meshes: {0}", exception.Message);
            DisposeMeshes();
            return false;
        }
    }

    private void UpdateCameraMotion(float yawDegrees, float realDeltaTime)
    {
        if (yawSampled)
        {
            float delta = NormalizeSignedDegrees(yawDegrees - lastYawDegrees);
            float speed = Math.Abs(delta) / Math.Max(0.001f, realDeltaTime);
            if (speed > 0.01f)
            {
                lastYawDirection = MathF.Sign(delta);
                cameraShakeEnergy = Math.Max(cameraShakeEnergy, Math.Clamp(speed / 260f, 0f, 1f));
            }
        }
        yawSampled = true;
        lastYawDegrees = yawDegrees;
        cameraShakeEnergy *= MathF.Exp(-7.5f * Math.Max(0, realDeltaTime));
    }

    private void RenderComponent(IShaderProgram shader, MeshRef mesh, float[] model, int materialKind, float scale, float alpha)
    {
        float[] component = Mat4f.CloneIt(model);
        Mat4f.Scale(component, component, scale, scale, scale);
        shader.UniformMatrix("modelViewMatrix", component);
        shader.Uniform("materialKind", materialKind);
        shader.Uniform("alpha", alpha);
        shader.Uniform("lightSweep", 0f);
        capi.Render.RenderMesh(mesh);
    }

    private void FinishStowing()
    {
        stowingForClose = false;
        Action? callback = stowed;
        stowed = null;
        callback?.Invoke();
    }

    private void DisposeMeshes()
    {
        housingMesh?.Dispose(); housingMesh = null;
        outlineMesh?.Dispose(); outlineMesh = null;
        faceMesh?.Dispose(); faceMesh = null;
        needleMesh?.Dispose(); needleMesh = null;
        sundialShadowMesh?.Dispose(); sundialShadowMesh = null;
        sundialGnomonMesh?.Dispose(); sundialGnomonMesh = null;
        compassDialTexture?.Dispose(); compassDialTexture = null;
        sundialDialTexture?.Dispose(); sundialDialTexture = null;
    }

    public void Dispose()
    {
        stowingForClose = false;
        stowed = null;
        forearmsPrepared = false;
        targetVisible = false;
        visibility = 0;
        openingSeconds = -1f;
        presentationSeconds = 0;
        cameraShakeEnergy = 0;
        yawSampled = false;
        DisposeMeshes();
        forearms.Dispose();
    }

    private static MeshData CreateQuadMesh()
    {
        MeshData mesh = new(4);
        int color = unchecked((int)0xffffffff);
        // Cairo's first row is the visual top, while this OpenGL texture
        // coordinate convention starts V at the visual bottom. Flip V here
        // so N remains at the top and glyphs are not vertically mirrored.
        mesh.AddVertex(-0.5f, -0.5f, 0, 0, 1, color);
        mesh.AddVertex(0.5f, -0.5f, 0, 1, 1, color);
        mesh.AddVertex(0.5f, 0.5f, 0, 1, 0, color);
        mesh.AddVertex(-0.5f, 0.5f, 0, 0, 0, color);
        mesh.AddQuadIndices(0);
        return mesh;
    }

    private static MeshData CreateDiscMesh(int segments)
    {
        MeshData mesh = new(segments + 1);
        int color = unchecked((int)0xffffffff);
        mesh.AddVertex(0, 0, 0, 0.5f, 0.5f, color);
        for (int segment = 0; segment < segments; segment++)
        {
            float angle = segment / (float)segments * MathF.PI * 2f;
            mesh.AddVertex(
                MathF.Cos(angle) * 0.5f,
                MathF.Sin(angle) * 0.5f,
                0,
                MathF.Cos(angle) * 0.5f + 0.5f,
                MathF.Sin(angle) * 0.5f + 0.5f,
                color
            );
        }
        for (int segment = 0; segment < segments; segment++)
        {
            mesh.AddIndex(0);
            mesh.AddIndex(segment + 1);
            mesh.AddIndex(segment == segments - 1 ? 1 : segment + 2);
        }
        return mesh;
    }

    private void EnsureDialTextures()
    {
        EnsureCompassDialTexture();
        EnsureSundialDialTexture();
    }

    private void EnsureCompassDialTexture()
    {
        if (compassDialTexture?.TextureId > 0) return;
        const int size = 256;
        compassDialTexture ??= new LoadedTexture(capi);
        using ImageSurface surface = new(Format.Argb32, size, size);
        using Context context = new(surface);
        context.Operator = Operator.Source;
        context.SetSourceRGBA(0, 0, 0, 0);
        context.Paint();
        context.Operator = Operator.Over;

        // A small, authored parchment dial. It is generated at runtime so no
        // game or third-party art is redistributed with the mod.
        context.SetSourceRGBA(0.82, 0.72, 0.50, 1);
        context.Arc(128, 128, 118, 0, Math.PI * 2);
        context.Fill();
        context.SetSourceRGBA(0.22, 0.13, 0.045, 1);
        context.LineWidth = 8;
        context.Arc(128, 128, 114, 0, Math.PI * 2);
        context.Stroke();
        context.SetSourceRGBA(0.34, 0.21, 0.075, 0.92);
        context.LineWidth = 3;
        context.Arc(128, 128, 91, 0, Math.PI * 2);
        context.Stroke();

        for (int tick = 0; tick < 32; tick++)
        {
            double angle = tick * Math.PI * 2 / 32 - Math.PI * 0.5;
            double outer = 101;
            double inner = tick % 8 == 0 ? 79 : tick % 4 == 0 ? 86 : 92;
            context.LineWidth = tick % 4 == 0 ? 3.5 : 1.5;
            context.MoveTo(128 + Math.Cos(angle) * inner, 128 + Math.Sin(angle) * inner);
            context.LineTo(128 + Math.Cos(angle) * outer, 128 + Math.Sin(angle) * outer);
            context.Stroke();
        }

        context.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
        context.SetFontSize(25);
        context.SetSourceRGBA(0.20, 0.11, 0.035, 1);
        DrawCenteredText(context, "N", 128, 62);
        DrawCenteredText(context, "E", 194, 136);
        DrawCenteredText(context, "S", 128, 205);
        DrawCenteredText(context, "W", 62, 136);
        context.SetSourceRGBA(0.65, 0.10, 0.055, 1);
        context.Arc(128, 128, 9, 0, Math.PI * 2);
        context.Fill();
        capi.Gui.LoadOrUpdateCairoTexture(
            surface,
            true,
            ref compassDialTexture
        );
    }

    private void EnsureSundialDialTexture()
    {
        if (sundialDialTexture?.TextureId > 0) return;
        const int size = 256;
        const double centerX = 128;
        const double centerY = 140;
        sundialDialTexture ??= new LoadedTexture(capi);
        using ImageSurface surface = new(Format.Argb32, size, size);
        using Context context = new(surface);
        context.Operator = Operator.Source;
        context.SetSourceRGBA(0, 0, 0, 0);
        context.Paint();
        context.Operator = Operator.Over;

        // Warm, uneven timber replaces the sterile paper-and-ink diagram
        // look. Sparse curved grain and burned marks keep the face legible at
        // its small in-game size without turning it into a technical plate.
        context.SetSourceRGBA(0.54, 0.32, 0.12, 1);
        context.Arc(128, 128, 118, 0, Math.PI * 2);
        context.Fill();
        context.SetSourceRGBA(0.16, 0.075, 0.020, 1);
        context.LineWidth = 9;
        context.Arc(128, 128, 113, 0, Math.PI * 2);
        context.Stroke();
        context.SetSourceRGBA(0.30, 0.16, 0.050, 1);
        context.LineWidth = 3;
        context.Arc(128, 128, 96, 0, Math.PI * 2);
        context.Stroke();

        context.SetSourceRGBA(0.18, 0.080, 0.018, 0.95);
        for (int hour = 6; hour <= 18; hour++)
        {
            double angle = (hour - 12) * Math.PI / 12 - Math.PI * 0.5;
            bool labelledHour = hour % 3 == 0;
            double inner = labelledHour ? 13 : 20;
            double outer = labelledHour ? 70 : 82;
            context.LineWidth = labelledHour ? 3.4 : 1.9;
            context.MoveTo(
                centerX + Math.Cos(angle) * inner,
                centerY + Math.Sin(angle) * inner
            );
            context.LineTo(
                centerX + Math.Cos(angle) * outer,
                centerY + Math.Sin(angle) * outer
            );
            context.Stroke();
        }

        context.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
        context.SetFontSize(24);
        context.SetSourceRGBA(0.14, 0.060, 0.014, 1);
        DrawSundialHour(context, "6", 6, centerX, centerY);
        DrawSundialHour(context, "9", 9, centerX, centerY);
        DrawSundialHour(context, "12", 12, centerX, centerY);
        DrawSundialHour(context, "15", 15, centerX, centerY);
        DrawSundialHour(context, "18", 18, centerX, centerY);
        context.Arc(centerX, centerY, 7, 0, Math.PI * 2);
        context.Fill();
        capi.Gui.LoadOrUpdateCairoTexture(
            surface,
            true,
            ref sundialDialTexture
        );
    }

    private static void DrawSundialHour(
        Context context,
        string label,
        int hour,
        double centerX,
        double centerY
    )
    {
        double angle = (hour - 12) * Math.PI / 12 - Math.PI * 0.5;
        DrawCenteredText(
            context,
            label,
            centerX + Math.Cos(angle) * 88,
            centerY + Math.Sin(angle) * 88
        );
    }

    private static void DrawCenteredText(Context context, string text, double x, double y)
    {
        TextExtents extents = context.TextExtents(text);
        context.MoveTo(x - extents.Width * 0.5 - extents.XBearing, y - extents.Height * 0.5 - extents.YBearing);
        context.ShowText(text);
    }

    private static MeshData CreateNeedleMesh()
    {
        MeshData mesh = new(4);
        int color = unchecked((int)0xffffffff);
        mesh.AddVertex(-0.085f, -0.35f, 0, 0, 0, color);
        mesh.AddVertex(0.085f, -0.35f, 0, 1, 0, color);
        mesh.AddVertex(0.038f, 0.39f, 0, 1, 1, color);
        mesh.AddVertex(-0.038f, 0.39f, 0, 0, 1, color);
        mesh.AddQuadIndices(0);
        return mesh;
    }

    private static MeshData CreateSundialShadowMesh()
    {
        MeshData mesh = new(4);
        int color = unchecked((int)0xffffffff);
        mesh.AddVertex(-0.014f, 0, 0, 0, 0, color);
        mesh.AddVertex(0.014f, 0, 0, 1, 0, color);
        mesh.AddVertex(0.018f, 0.30f, 0, 1, 1, color);
        mesh.AddVertex(-0.018f, 0.30f, 0, 0, 1, color);
        mesh.AddQuadIndices(0);
        return mesh;
    }

    private static MeshData CreateSundialGnomonMesh()
    {
        MeshData mesh = new(3);
        int color = unchecked((int)0xffffffff);
        mesh.AddVertex(-0.060f, -0.060f, 0, 0, 0, color);
        mesh.AddVertex(0.060f, -0.060f, 0, 1, 0, color);
        mesh.AddVertex(0, 0.145f, 0, 0.5f, 1, color);
        mesh.AddIndex(0);
        mesh.AddIndex(1);
        mesh.AddIndex(2);
        return mesh;
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
            float x0 = MathF.Cos(angle0) * 0.5f; float z0 = MathF.Sin(angle0) * 0.5f;
            float x1 = MathF.Cos(angle1) * 0.5f; float z1 = MathF.Sin(angle1) * 0.5f;
            int side = mesh.VerticesCount;
            mesh.AddVertex(x0, -0.49f, z0, u0, 0, color);
            mesh.AddVertex(x1, -0.49f, z1, u1, 0, color);
            mesh.AddVertex(x1, 0.49f, z1, u1, 1, color);
            mesh.AddVertex(x0, 0.49f, z0, u0, 1, color);
            mesh.AddQuadIndices(side);
            int cap = mesh.VerticesCount;
            mesh.AddVertex(0, 0.501f, 0, 0.5f, 0.5f, color);
            mesh.AddVertex(x0, 0.501f, z0, x0 + 0.5f, z0 + 0.5f, color);
            mesh.AddVertex(x1, 0.501f, z1, x1 + 0.5f, z1 + 0.5f, color);
            mesh.AddIndex(cap); mesh.AddIndex(cap + 1); mesh.AddIndex(cap + 2);
            int bottom = mesh.VerticesCount;
            mesh.AddVertex(0, -0.501f, 0, 0.5f, 0.5f, color);
            mesh.AddVertex(x1, -0.501f, z1, x1 + 0.5f, z1 + 0.5f, color);
            mesh.AddVertex(x0, -0.501f, z0, x0 + 0.5f, z0 + 0.5f, color);
            mesh.AddIndex(bottom); mesh.AddIndex(bottom + 1); mesh.AddIndex(bottom + 2);
        }
        return mesh;
    }

    private float ScreenToViewX(float screenX, float aspect) => (screenX / capi.Render.FrameWidth * 2f - 1f) * aspect;
    private float ScreenToViewY(float screenY) => 1f - screenY / capi.Render.FrameHeight * 2f;
    internal static float NeedleRotationRadians(float interpolatedAtlasYawDegrees)
    {
        float normalizedYaw = interpolatedAtlasYawDegrees % 360f;
        return -normalizedYaw * GameMath.DEG2RAD;
    }
    internal static float SundialShadowRotationRadians(float atlasHour)
    {
        float normalizedHour = atlasHour % 24f;
        if (normalizedHour < 0) normalizedHour += 24f;
        // Cairo's dial texture has 06:00 on the left and 18:00 on the right.
        // Negate the OpenGL Z rotation so the live shadow lands on the same
        // authored hour lines instead of mirroring morning and afternoon.
        return -(normalizedHour - 12f) / 12f * MathF.PI;
    }
    internal static bool IsSundialTimeVisible(float atlasHour)
    {
        float normalizedHour = atlasHour % 24f;
        if (normalizedHour < 0) normalizedHour += 24f;
        return normalizedHour >= 6f && normalizedHour <= 18f;
    }
    private static float NormalizeSignedDegrees(float degrees)
    {
        float normalized = degrees % 360f;
        if (normalized > 180f) normalized -= 360f;
        if (normalized <= -180f) normalized += 360f;
        return normalized;
    }
    private static float Lerp(float from, float to, float amount) => from + (to - from) * Math.Clamp(amount, 0f, 1f);
    private static float MoveTowards(float from, float to, float amount) => from < to ? Math.Min(to, from + amount) : Math.Max(to, from - amount);
    private static float SmoothStep(float value) { value = Math.Clamp(value, 0f, 1f); return value * value * (3f - 2f * value); }
}
