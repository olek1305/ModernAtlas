using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Presents the completed atlas framebuffer on a gently curved 3D parchment
/// sheet. The exact terrain renderer remains unchanged; this class only owns
/// the physical scroll presentation around its final color texture.
/// </summary>
internal sealed class AtlasScrollViewportRenderer : IDisposable
{
    private const int AtlasContentClipInsetPixels = 2;
    private readonly ICoreClientAPI capi;
    private readonly Func<IShaderProgram?> shaderProvider;
    private MeshRef? sheetMesh;
    private MeshRef? cylinderMesh;

    public AtlasScrollViewportRenderer(
        ICoreClientAPI capi,
        Func<IShaderProgram?> shaderProvider
    )
    {
        this.capi = capi;
        this.shaderProvider = shaderProvider;
    }

    public void Render(int atlasTextureId, AtlasViewportBounds viewport)
    {
        if (atlasTextureId <= 0 || !EnsureMeshes()) return;
        IShaderProgram? shader = shaderProvider();
        if (shader == null || shader.Disposed || sheetMesh == null || cylinderMesh == null)
        {
            return;
        }

        IRenderAPI render = capi.Render;
        float frameHeight = Math.Max(1, render.FrameHeight);
        float aspect = render.FrameWidth / frameHeight;
        float centerX = ((viewport.X + viewport.Width * 0.5f) / render.FrameWidth * 2f - 1f)
            * aspect;
        float centerY = 1f
            - (viewport.Y + viewport.Height * 0.5f) / frameHeight * 2f;
        float mapWidth = viewport.Width / frameHeight * 2f;
        float mapHeight = viewport.Height / frameHeight * 2f;
        float paperWidth = mapWidth + 0.17f;
        float paperHeight = mapHeight + 0.20f;

        float[] projection = Mat4f.Create();
        Mat4f.Ortho(projection, -aspect, aspect, -1f, 1f, -4f, 4f);

        render.CurrentActiveShader?.Stop();
        render.CurrentFrameBuffer = null;
        render.GLDisableDepthTest();
        render.GLDepthMask(false);
        render.GlDisableCullFace();
        render.GlToggleBlend(true, EnumBlendMode.Standard);
        shader.Use();
        shader.UniformMatrix("projectionMatrix", projection);
        shader.Uniform("entityColor", new Vec4f(1f, 1f, 1f, 1f));
        shader.BindTexture2D("atlasTex", atlasTextureId, 0);

        try
        {
            RenderComponent(
                shader,
                sheetMesh,
                CreateModel(centerX, centerY, 0f, paperWidth, paperHeight, 1f),
                0
            );

            // Treat the inner parchment as a real display viewport. The map
            // texture must never paint over the paper border or the dimmed
            // world behind the scroll, regardless of atlas zoom or GUI scale.
            AtlasViewportBounds contentClip = viewport.Inset(
                AtlasContentClipInsetPixels
            );
            render.GlScissor(
                contentClip.X,
                Math.Max(0, render.FrameHeight - contentClip.Bottom),
                contentClip.Width,
                contentClip.Height
            );
            render.GlScissorFlag(true);
            try
            {
                // The atlas framebuffer has already composed terrain, fluids,
                // OIT and fog. Copy covered pixels at full opacity instead of
                // blending the completed map with the normal player view.
                render.GlToggleBlend(false, EnumBlendMode.Standard);
                RenderComponent(
                    shader,
                    sheetMesh,
                    CreateModel(centerX, centerY, -0.012f, mapWidth, mapHeight, 0.82f),
                    7
                );
            }
            finally
            {
                render.GlToggleBlend(true, EnumBlendMode.Standard);
                render.GlScissorFlag(false);
            }

            float rollerX = paperWidth * 0.5f + 0.018f;
            float rollerHeight = paperHeight + 0.18f;
            RenderRoller(shader, centerX - rollerX, centerY, rollerHeight);
            RenderRoller(shader, centerX + rollerX, centerY, rollerHeight);
        }
        finally
        {
            shader.Stop();
            // GUI composers render immediately after the scroll and use
            // screen-facing quads. Leave face culling disabled so their
            // cards, labels and controls are not discarded.
            render.GLDepthMask(true);
            render.GLEnableDepthTest();
            render.GlToggleBlend(false, EnumBlendMode.Standard);
        }
    }

    public AtlasViewportBounds GetPaperBounds(AtlasViewportBounds viewport)
    {
        int frameHeight = Math.Max(1, capi.Render.FrameHeight);
        int horizontalMargin = (int)MathF.Ceiling(frameHeight * 0.0425f);
        int verticalMargin = (int)MathF.Ceiling(frameHeight * 0.05f);
        return viewport.Expand(horizontalMargin, verticalMargin);
    }

    public void RenderRollersOverlay(AtlasViewportBounds viewport)
    {
        if (!EnsureMeshes() || cylinderMesh == null) return;
        IShaderProgram? shader = shaderProvider();
        if (shader == null || shader.Disposed) return;

        IRenderAPI render = capi.Render;
        float frameHeight = Math.Max(1, render.FrameHeight);
        float aspect = render.FrameWidth / frameHeight;
        float centerX = ((viewport.X + viewport.Width * 0.5f) / render.FrameWidth * 2f - 1f)
            * aspect;
        float centerY = 1f
            - (viewport.Y + viewport.Height * 0.5f) / frameHeight * 2f;
        float mapWidth = viewport.Width / frameHeight * 2f;
        float mapHeight = viewport.Height / frameHeight * 2f;
        float paperWidth = mapWidth + 0.17f;
        float paperHeight = mapHeight + 0.20f;
        float rollerX = paperWidth * 0.5f + 0.018f;
        float rollerHeight = paperHeight + 0.18f;

        float[] projection = Mat4f.Create();
        Mat4f.Ortho(projection, -aspect, aspect, -1f, 1f, -4f, 4f);
        render.CurrentActiveShader?.Stop();
        render.CurrentFrameBuffer = null;
        render.GLDisableDepthTest();
        render.GLDepthMask(false);
        render.GlDisableCullFace();
        render.GlToggleBlend(true, EnumBlendMode.Standard);
        shader.Use();
        shader.UniformMatrix("projectionMatrix", projection);
        shader.Uniform("entityColor", new Vec4f(1f, 1f, 1f, 1f));
        try
        {
            RenderRoller(shader, centerX - rollerX, centerY, rollerHeight);
            RenderRoller(shader, centerX + rollerX, centerY, rollerHeight);
        }
        finally
        {
            shader.Stop();
            render.GLDepthMask(true);
            render.GlToggleBlend(false, EnumBlendMode.Standard);
        }
    }

    private void RenderRoller(
        IShaderProgram shader,
        float x,
        float y,
        float height
    )
    {
        if (cylinderMesh == null) return;
        RenderComponent(
            shader,
            cylinderMesh,
            CreateModel(x, y, 0.018f, 0.070f, height, 0.070f),
            1
        );
        float knobY = height * 0.5f + 0.045f;
        RenderComponent(
            shader,
            cylinderMesh,
            CreateModel(x, y + knobY, 0.018f, 0.14f, 0.08f, 0.14f),
            2
        );
        RenderComponent(
            shader,
            cylinderMesh,
            CreateModel(x, y - knobY, 0.018f, 0.14f, 0.08f, 0.14f),
            2
        );
    }

    private void RenderComponent(
        IShaderProgram shader,
        MeshRef mesh,
        float[] model,
        int materialKind
    )
    {
        shader.UniformMatrix("modelViewMatrix", model);
        shader.Uniform("materialKind", materialKind);
        shader.Uniform("alpha", 1f);
        shader.Uniform("lightSweep", 0f);
        capi.Render.RenderMesh(mesh);
    }

    private static float[] CreateModel(
        float x,
        float y,
        float z,
        float scaleX,
        float scaleY,
        float scaleZ
    )
    {
        float[] model = Mat4f.Create();
        Mat4f.Translate(model, model, x, y, z);
        Mat4f.Scale(model, model, scaleX, scaleY, scaleZ);
        return model;
    }

    private bool EnsureMeshes()
    {
        if (sheetMesh != null && cylinderMesh != null) return true;
        try
        {
            sheetMesh ??= capi.Render.UploadMesh(CreateSheetMesh());
            cylinderMesh ??= capi.Render.UploadMesh(CreateCylinderMesh(20));
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "[ModernAtlas] Could not create the persistent 3D scroll viewport: {0}",
                exception.Message
            );
            Dispose();
            return false;
        }
    }

    private static MeshData CreateSheetMesh()
    {
        const int horizontalSegments = 24;
        const int verticalSegments = 8;
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
        float edgeCurl = MathF.Pow(MathF.Abs(x) * 2f, 3f) * 0.070f;
        float centerSag = (1f - MathF.Abs(x) * 2f)
            * (0.5f - MathF.Abs(y)) * -0.014f;
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

    public void Dispose()
    {
        sheetMesh?.Dispose();
        sheetMesh = null;
        cylinderMesh?.Dispose();
        cylinderMesh = null;
    }
}
