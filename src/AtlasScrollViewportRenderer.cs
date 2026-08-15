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

    public void Render(
        int atlasTextureId,
        int backgroundTextureId,
        AtlasViewportBounds viewport
    )
    {
        IRenderAPI render = capi.Render;
        SetCanonicalGuiState(render);
        if (!EnsureMeshes()) return;
        IShaderProgram? shader = shaderProvider();
        if (shader == null || shader.Disposed || sheetMesh == null || cylinderMesh == null)
        {
            return;
        }

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
        shader.Use();
        shader.UniformMatrix("projectionMatrix", projection);
        shader.Uniform("entityColor", new Vec4f(1f, 1f, 1f, 1f));
        if (atlasTextureId > 0)
        {
            shader.BindTexture2D("atlasTex", atlasTextureId, 0);
        }
        shader.Uniform("backgroundAvailable", backgroundTextureId > 0 ? 1 : 0);
        if (backgroundTextureId > 0)
        {
            shader.BindTexture2D("backgroundTex", backgroundTextureId, 1);
        }

        try
        {
            // Present a dimmed frozen POV behind the scroll. It remains fully
            // opaque and stationary, so the live world cannot leak through or
            // move while the atlas is open.
            render.GlToggleBlend(false, EnumBlendMode.Standard);
            RenderComponent(
                shader,
                sheetMesh,
                CreateModel(0f, 0f, -0.10f, aspect * 2f, 2f, 1f),
                8
            );
            // The physical parchment is opaque too. Keep blending disabled
            // for the sheet and rollers so driver blend state cannot reveal
            // the frozen world through their nominally alpha-one pixels.
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
                    atlasTextureId > 0 ? 7 : 16
                );
            }
            finally
            {
                render.GlToggleBlend(false, EnumBlendMode.Standard);
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
            SetCanonicalGuiState(render);
        }
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
        render.GlToggleBlend(false, EnumBlendMode.Standard);
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

    /// <summary>
    /// Draws one procedural, low-opacity weather layer over the map opening.
    /// The caller invokes this only for an exposed Survival player in physical
    /// Scroll mode, so Fullscreen never allocates or renders weather work.
    /// </summary>
    public void RenderWeatherOverlay(
        AtlasViewportBounds viewport,
        AtlasScrollWeatherState weather,
        float realTimeSeconds
    )
    {
        if (!EnsureMeshes() || sheetMesh == null) return;
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
        float[] projection = Mat4f.Create();
        Mat4f.Ortho(projection, -aspect, aspect, -1f, 1f, -4f, 4f);
        AtlasViewportBounds clip = viewport.Inset(AtlasContentClipInsetPixels);

        render.CurrentActiveShader?.Stop();
        render.CurrentFrameBuffer = null;
        render.GLDisableDepthTest();
        render.GLDepthMask(false);
        render.GlDisableCullFace();
        render.GlToggleBlend(true, EnumBlendMode.Standard);
        shader.Use();
        shader.UniformMatrix("projectionMatrix", projection);
        shader.Uniform("materialKind", 15);
        shader.Uniform("alpha", 1f);
        shader.Uniform("lightSweep", 0f);
        shader.Uniform("weatherType", weather.PrecipitationKind);
        shader.Uniform("weatherIntensity", weather.PrecipitationIntensity);
        shader.Uniform("weatherFog", weather.FogIntensity);
        shader.Uniform("weatherWindOffset", weather.WindOffset);
        shader.Uniform("weatherLightning", weather.LightningIntensity);
        shader.Uniform("weatherTime", realTimeSeconds);
        try
        {
            // First animate the exposed air in front of the frozen world POV.
            // Atlas UI and the handheld instrument render later and remain
            // perfectly readable.
            shader.Uniform("weatherSurface", 0);
            shader.Uniform("weatherLayerScale", 0.90f);
            shader.UniformMatrix(
                "modelViewMatrix",
                CreateModel(0f, 0f, -0.018f, aspect * 2f, 2f, 1f)
            );
            render.RenderMesh(sheetMesh);

            // A second, softer pass is clipped to the parchment's live map
            // opening and adds only the restrained surface contact marks.
            render.GlScissor(
                clip.X,
                Math.Max(0, render.FrameHeight - clip.Bottom),
                clip.Width,
                clip.Height
            );
            render.GlScissorFlag(true);
            shader.Uniform("weatherSurface", 1);
            shader.Uniform("weatherLayerScale", 0.62f);
            shader.UniformMatrix(
                "modelViewMatrix",
                CreateModel(centerX, centerY, -0.020f, mapWidth, mapHeight, 1f)
            );
            render.RenderMesh(sheetMesh);
        }
        finally
        {
            shader.Stop();
            render.GlScissorFlag(false);
            render.GLDepthMask(true);
            render.GLEnableDepthTest();
            render.GlToggleBlend(false, EnumBlendMode.Standard);
        }
    }

    public void RenderAtlasFullscreen(int atlasTextureId)
    {
        IRenderAPI render = capi.Render;
        SetCanonicalGuiState(render);
        if (!EnsureMeshes() || sheetMesh == null) return;
        IShaderProgram? shader = shaderProvider();
        if (shader == null || shader.Disposed) return;

        float frameHeight = Math.Max(1, render.FrameHeight);
        float aspect = render.FrameWidth / frameHeight;
        float[] projection = Mat4f.Create();
        Mat4f.Ortho(projection, -aspect, aspect, -1f, 1f, -4f, 4f);

        render.CurrentActiveShader?.Stop();
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
        shader.Use();
        shader.UniformMatrix("projectionMatrix", projection);
        shader.Uniform("entityColor", new Vec4f(1f, 1f, 1f, 1f));
        if (atlasTextureId > 0)
        {
            shader.BindTexture2D("atlasTex", atlasTextureId, 0);
        }
        try
        {
            // Framebuffer textures use the orientation expected by the atlas
            // presentation shader. The engine GUI texture helper flips this
            // GPU-owned texture, which made throttled full-screen frames
            // alternate between upright and upside-down presentations.
            RenderComponent(
                shader,
                sheetMesh,
                CreateModel(0f, 0f, 0f, aspect * 2f, 2f, 1f),
                atlasTextureId > 0 ? 7 : 16
            );
        }
        finally
        {
            shader.Stop();
            SetCanonicalGuiState(render);
        }
    }

    private static void SetCanonicalGuiState(IRenderAPI render)
    {
        // This renderer is a GUI presentation pass. Never inherit a failed
        // atlas draw's framebuffer, scissor, color mask or world depth state.
        render.CurrentFrameBuffer = null;
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
        render.GlColorMask(true, true, true, true);
        render.GlScissorFlag(false);
        render.GLDisableDepthTest();
        render.GLDepthMask(false);
        render.GlDisableCullFace();
        render.GlToggleBlend(false, EnumBlendMode.Standard);
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
