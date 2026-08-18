using System;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Resolves the fully composed atlas through one material-independent hard
/// boundary. Terrain shaders keep their early guards, but this final pass is
/// authoritative for terrain, entities, OIT, liquids and clouds alike.
/// </summary>
internal sealed class AtlasBoundaryResolver : IDisposable
{
    private static readonly float[] BackgroundColor =
    {
        0.035f, 0.075f, 0.11f, 1f
    };

    private readonly ICoreClientAPI capi;
    private readonly Func<IShaderProgram?> shaderProvider;
    private MeshRef? quad;
    private FrameBufferRef? framebuffer;
    private bool disposed;
    private bool loggedSuccess;

    public bool LastResolveSucceeded { get; private set; }

    public FrameBufferRef? Framebuffer =>
        framebuffer is { Disposed: false } ? framebuffer : null;

    public int ColorTextureId => Framebuffer?.ColorTextureIds is { Length: > 0 }
        ? Framebuffer.ColorTextureIds[0]
        : 0;

    public AtlasBoundaryResolver(
        ICoreClientAPI capi,
        Func<IShaderProgram?> shaderProvider
    )
    {
        this.capi = capi;
        this.shaderProvider = shaderProvider;
    }

    public bool Resolve(
        FrameBufferRef source,
        float[] projection,
        double[] view,
        Vec3d worldOffset,
        double disclosureCenterX,
        double disclosureCenterZ,
        int disclosureRadius,
        AtlasSurfaceHeightTexture? surfaceHeightTexture,
        bool completeBoundaryEnabled,
        float completeMinimumX,
        float completeMinimumZ,
        float completeMaximumX,
        float completeMaximumZ
    )
    {
        LastResolveSucceeded = false;
        if (disposed
            || source.ColorTextureIds is not { Length: > 0 }
            || source.ColorTextureIds[0] <= 0
            || source.DepthTextureId <= 0
            || !EnsureResources(source.Width, source.Height)
            || framebuffer == null
            || quad == null)
        {
            return false;
        }

        IShaderProgram? shader = shaderProvider();
        if (shader == null || shader.Disposed) return false;

        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        try
        {
            render.CurrentActiveShader?.Stop();
            render.CurrentFrameBuffer = framebuffer;
            render.GlViewport(0, 0, framebuffer.Width, framebuffer.Height);
            render.GLDisableDepthTest();
            render.GLDepthMask(false);
            render.GlToggleBlend(false, EnumBlendMode.Standard);
            render.GlDisableCullFace();

            shader.Use();
            shader.BindTexture2D("sourceColorTex", source.ColorTextureIds[0], 0);
            shader.BindTexture2D("sourceDepthTex", source.DepthTextureId, 1);
            shader.UniformMatrix("projectionMatrix", projection);
            shader.UniformMatrix(
                "modelViewMatrix",
                Array.ConvertAll(view, value => (float)value)
            );
            shader.Uniform(
                "worldOffset",
                (float)worldOffset.X,
                (float)worldOffset.Y,
                (float)worldOffset.Z
            );
            shader.Uniform(
                "disclosureCenterXZ",
                (float)disclosureCenterX,
                (float)disclosureCenterZ
            );
            shader.Uniform(
                "disclosureRadius",
                (float)Math.Max(GlobalConstants.ChunkSize, disclosureRadius)
            );
            shader.Uniform(
                "completeBoundaryEnabled",
                completeBoundaryEnabled ? 1 : 0
            );
            shader.Uniform(
                "completeBoundaryMinXZ",
                completeMinimumX,
                completeMinimumZ
            );
            shader.Uniform(
                "completeBoundaryMaxXZ",
                completeMaximumX,
                completeMaximumZ
            );
            shader.Uniform(
                "backgroundColor",
                BackgroundColor[0],
                BackgroundColor[1],
                BackgroundColor[2],
                BackgroundColor[3]
            );

            bool hasSurfaceCoverage = surfaceHeightTexture?.Ready == true
                && surfaceHeightTexture.TextureId > 0;
            shader.Uniform(
                "surfaceCoverageEnabled",
                hasSurfaceCoverage ? 1 : 0
            );
            if (hasSurfaceCoverage && surfaceHeightTexture != null)
            {
                shader.BindTexture2D(
                    "surfaceHeightTex",
                    surfaceHeightTexture.TextureId,
                    2
                );
                shader.Uniform(
                    "surfaceOriginXZ",
                    (float)surfaceHeightTexture.OriginX,
                    (float)surfaceHeightTexture.OriginZ
                );
                shader.Uniform(
                    "surfaceSampleSize",
                    (float)AtlasSurfaceHeightTexture.HorizontalSampleSize
                );
            }

            render.RenderMesh(quad);
            shader.Stop();
            if (!loggedSuccess)
            {
                loggedSuccess = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Final atlas boundary resolve is active for terrain, vegetation, entities, OIT, liquids and clouds."
                );
            }
            LastResolveSucceeded = true;
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                "[ModernAtlas] Final atlas boundary resolve failed: {0}",
                exception.Message
            );
            return false;
        }
        finally
        {
            renderState.RestoreCapturedState();
        }
    }


    private bool EnsureResources(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        quad ??= capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        if (framebuffer is { Disposed: false }
            && framebuffer.Width == width
            && framebuffer.Height == height)
        {
            return true;
        }

        DestroyFramebuffer();
        RawTexture color = new()
        {
            Width = width,
            Height = height,
            MinFilter = EnumTextureFilter.Nearest,
            MagFilter = EnumTextureFilter.Nearest,
            WrapS = EnumTextureWrap.ClampToEdge,
            WrapT = EnumTextureWrap.ClampToEdge,
            PixelInternalFormat = EnumTextureInternalFormat.Rgba8,
            PixelFormat = EnumTexturePixelFormat.Rgba
        };
        FramebufferAttrs attributes = new(
            "modernatlas-boundary-resolve",
            width,
            height
        )
        {
            Attachments = new[]
            {
                new FramebufferAttrsAttachment
                {
                    AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                    Texture = color
                }
            }
        };
        framebuffer = capi.Render.CreateFrameBuffer(attributes);
        return framebuffer is { Disposed: false }
            && framebuffer.ColorTextureIds is { Length: > 0 }
            && framebuffer.ColorTextureIds[0] > 0;
    }

    private void DestroyFramebuffer()
    {
        if (framebuffer != null && !framebuffer.Disposed)
        {
            capi.Render.DestroyFrameBuffer(framebuffer);
        }
        framebuffer = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        LastResolveSucceeded = false;
        DestroyFramebuffer();
        quad?.Dispose();
        quad = null;
    }
}
