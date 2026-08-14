using System;
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
    private readonly ICoreClientAPI capi;
    private readonly Func<IShaderProgram?> shaderProvider;
    private readonly SeraphForearmRenderer forearms;
    private MeshRef? housingMesh;
    private MeshRef? faceMesh;
    private MeshRef? needleMesh;
    private float visibility;
    private bool targetVisible;
    private bool stowingForClose;
    private bool forearmsPrepared;
    private Action? stowed;

    public AtlasCompassRenderer(ICoreClientAPI capi, Func<IShaderProgram?> shaderProvider)
    {
        this.capi = capi;
        this.shaderProvider = shaderProvider;
        forearms = new SeraphForearmRenderer(capi);
    }

    public void SetEnabled(bool enabled)
    {
        if (!stowingForClose) targetVisible = enabled;
    }

    public void ResetForAtlasOpen(bool enabled)
    {
        stowingForClose = false;
        stowed = null;
        targetVisible = enabled;
    }

    public bool BeginStowingForClose(Action onStowed)
    {
        if (stowingForClose) return true;
        if (visibility <= 0.002f && !targetVisible) return false;
        stowingForClose = true;
        targetVisible = false;
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
        if (shader == null || shader.Disposed || housingMesh == null || faceMesh == null || needleMesh == null)
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
        float elbowX = Lerp(hiddenGripX + 0.20f, shownGripX + 0.38f, visible);
        float elbowY = Lerp(hiddenGripY - 0.20f, shownGripY - 0.42f, visible);
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
            Mat4f.Translate(body, body, gripX, gripY, -0.18f);
            Mat4f.RotateX(body, body, MathF.PI * 0.5f);
            RenderComponent(shader, housingMesh, body, 9, 0.19f, alpha);
            float[] dial = Mat4f.CloneIt(body);
            Mat4f.Translate(dial, dial, 0, 0, 0.035f);
            RenderComponent(shader, faceMesh, dial, 10, 0.145f, alpha);
            float[] needle = Mat4f.CloneIt(dial);
            Mat4f.Translate(needle, needle, 0, 0, 0.011f);
            Mat4f.RotateZ(needle, needle, -atlasYawDegrees * GameMath.DEG2RAD);
            RenderComponent(shader, needleMesh, needle, 11, 0.115f, alpha);
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
        if (housingMesh != null && faceMesh != null && needleMesh != null) return true;
        try
        {
            housingMesh ??= capi.Render.UploadMesh(CreateCylinderMesh(18));
            faceMesh ??= capi.Render.UploadMesh(CreateQuadMesh());
            needleMesh ??= capi.Render.UploadMesh(CreateNeedleMesh());
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error("[ModernAtlas] Could not create the atlas compass meshes: {0}", exception.Message);
            DisposeMeshes();
            return false;
        }
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
        faceMesh?.Dispose(); faceMesh = null;
        needleMesh?.Dispose(); needleMesh = null;
    }

    public void Dispose()
    {
        stowingForClose = false;
        stowed = null;
        forearmsPrepared = false;
        targetVisible = false;
        visibility = 0;
        DisposeMeshes();
        forearms.Dispose();
    }

    private static MeshData CreateQuadMesh()
    {
        MeshData mesh = new(4);
        int color = unchecked((int)0xffffffff);
        mesh.AddVertex(-0.5f, -0.5f, 0, 0, 0, color);
        mesh.AddVertex(0.5f, -0.5f, 0, 1, 0, color);
        mesh.AddVertex(0.5f, 0.5f, 0, 1, 1, color);
        mesh.AddVertex(-0.5f, 0.5f, 0, 0, 1, color);
        mesh.AddQuadIndices(0);
        return mesh;
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
    private static float Lerp(float from, float to, float amount) => from + (to - from) * Math.Clamp(amount, 0f, 1f);
    private static float MoveTowards(float from, float to, float amount) => from < to ? Math.Min(to, from + amount) : Math.Max(to, from - amount);
    private static float SmoothStep(float value) { value = Math.Clamp(value, 0f, 1f); return value * value * (3f - 2f * value); }
}
