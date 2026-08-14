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
    private readonly ICoreClientAPI capi;
    private readonly Func<IShaderProgram?> shaderProvider;
    private readonly SeraphForearmRenderer forearms;
    private MeshRef? housingMesh;
    private MeshRef? outlineMesh;
    private MeshRef? faceMesh;
    private MeshRef? needleMesh;
    private LoadedTexture? dialTexture;
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
        if (shader == null || shader.Disposed || housingMesh == null || faceMesh == null || needleMesh == null || dialTexture?.TextureId <= 0)
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
            shader.BindTexture2D("compassTex", dialTexture!.TextureId, 1);
            if (!forearmsPrepared)
            {
                forearmsPrepared = forearms.Prepare();
            }
            if (forearms.IsReady)
            {
                forearms.BindSkin(shader);
                forearms.RenderRightArm(shader, Mat4f.Create(), elbowX, elbowY, gripX, gripY, alpha);
            }

            // The housing, dial and glass are a small authored model rather
            // than a copied image. The dial rotates only its needle, with
            // world north (positive Z) at its top when atlas yaw is zero.
            float[] body = Mat4f.Create();
            Mat4f.Translate(body, body, compassX, compassY, -0.18f);
            Mat4f.RotateX(body, body, MathF.PI * 0.5f);
            Mat4f.RotateZ(body, body, tossFlip * 0.18f);
            RenderComponent(shader, housingMesh, body, 9, 0.19f, alpha);
            // The cylindrical casing turns around the screen Z axis, but the
            // dial must remain in the screen XY plane. Reusing the rotated
            // casing matrix made the texture edge-on, leaving only its brown
            // rim visible instead of the compass face.
            float[] dial = Mat4f.Create();
            Mat4f.Translate(dial, dial, compassX, compassY, -0.060f);
            Mat4f.RotateY(dial, dial, tossFlip);
            RenderComponent(shader, outlineMesh!, dial, 12, 0.171f, alpha);
            float dialAlpha = alpha * dialVisibility;
            RenderComponent(shader, faceMesh, dial, 10, 0.145f, dialAlpha);
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
            float needleAlpha = dialAlpha * (1f - airborne * 0.35f);
            RenderComponent(shader, needleMesh, needle, 11, 0.115f, needleAlpha);
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
        if (housingMesh != null && outlineMesh != null && faceMesh != null && needleMesh != null && dialTexture?.TextureId > 0) return true;
        try
        {
            housingMesh ??= capi.Render.UploadMesh(CreateCylinderMesh(18));
            outlineMesh ??= capi.Render.UploadMesh(CreateDiscMesh(32));
            faceMesh ??= capi.Render.UploadMesh(CreateQuadMesh());
            needleMesh ??= capi.Render.UploadMesh(CreateNeedleMesh());
            EnsureDialTexture();
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
        dialTexture?.Dispose(); dialTexture = null;
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

    private void EnsureDialTexture()
    {
        if (dialTexture?.TextureId > 0) return;
        const int size = 256;
        dialTexture ??= new LoadedTexture(capi);
        using ImageSurface surface = new(Format.Argb32, size, size);
        using Context context = new(surface);
        context.Operator = Operator.Source;
        context.SetSourceRGBA(0.12, 0.075, 0.028, 1);
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
        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref dialTexture);
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
        mesh.AddVertex(-0.13f, -0.42f, 0, 0, 0, color);
        mesh.AddVertex(0.13f, -0.42f, 0, 1, 0, color);
        mesh.AddVertex(0.055f, 0.46f, 0, 1, 1, color);
        mesh.AddVertex(-0.055f, 0.46f, 0, 0, 1, color);
        mesh.AddQuadIndices(0);
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
