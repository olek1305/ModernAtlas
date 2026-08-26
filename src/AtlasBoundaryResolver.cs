using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

internal readonly record struct AtlasValidityMaskDiagnostics(
    int Width,
    int Height,
    long ValidPixelCount,
    long TotalPixelCount
)
{
    public double ValidRatio => TotalPixelCount <= 0
        ? 0d
        : (double)ValidPixelCount / TotalPixelCount;

    public bool HasSensibleCoverage =>
        ValidPixelCount >= 64
        && (ValidRatio >= 0.00025d || ValidPixelCount >= 512);

    public override string ToString() =>
        $"dimensions={Width}x{Height}, validPixels={ValidPixelCount}/{TotalPixelCount}, validRatio={ValidRatio:0.####}";
}

/// <summary>
/// Aggregate validity coverage for one complete filtered screenshot job. The
/// dimensions describe one tile; the pixel counts and tile counts cover all
/// recorded tiles. Empty tiles are legal at a disclosure edge or at a
/// top-down pitch, so callers validate aggregate coverage and non-empty tile
/// count rather than requiring background pixels or terrain in every tile.
/// </summary>
internal readonly record struct AtlasValidityMaskAggregateDiagnostics(
    int Width,
    int Height,
    int TileCount,
    int NonEmptyTileCount,
    int EmptyTileCount,
    long ValidPixelCount,
    long TotalPixelCount
)
{
    public double ValidRatio => TotalPixelCount <= 0
        ? 0d
        : (double)ValidPixelCount / TotalPixelCount;

    public bool HasSensibleCoverage =>
        TileCount > 0
        && NonEmptyTileCount > 0
        && ValidPixelCount >= 64
        && (ValidRatio >= 0.00025d || ValidPixelCount >= 512);

    public override string ToString() =>
        $"dimensions={Width}x{Height}, tiles={TileCount}, nonEmptyTiles={NonEmptyTileCount}, emptyTiles={EmptyTileCount}, validPixels={ValidPixelCount}/{TotalPixelCount}, validRatio={ValidRatio:0.####}";
}

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
    private bool loggedMrtFallback;
    private bool loggedDrawBufferMismatch;

    private static readonly bool DrawBufferDiagnosticsEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("MODERNATLAS_SMOKE_TEST"),
            "1",
            StringComparison.Ordinal
        );

    public bool LastResolveSucceeded { get; private set; }

    /// <summary>
    /// Smoke-only state probe. It is deliberately observational: the resolver
    /// never writes draw-buffer state after the previous framebuffer has been
    /// restored. A false value means the probe found a change (or could not
    /// complete) and automated validation must fail.
    /// </summary>
    public bool LastDrawBufferStateCheckPassed { get; private set; } = true;

    public bool LastDrawBufferStateCheckPerformed { get; private set; }

    public string LastDrawBufferStateDiagnostic { get; private set; } =
        "draw-buffer probe is disabled";

    public FrameBufferRef? Framebuffer =>
        framebuffer is { Disposed: false } ? framebuffer : null;

    public int ColorTextureId => Framebuffer?.ColorTextureIds is { Length: > 0 }
        ? Framebuffer.ColorTextureIds[0]
        : 0;

    /// <summary>
    /// R channel validity mask from the final disclosure resolve. A value of
    /// one means the pixel belongs to geometry inside the hard boundary; zero
    /// is opaque background and must not participate in spatial filtering.
    /// </summary>
    public int ValidityTextureId => Framebuffer?.ColorTextureIds is { Length: > 1 }
        ? Framebuffer.ColorTextureIds[1]
        : 0;

    public bool HasValidityMask => ValidityTextureId > 0;

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
        float completeMaximumZ,
        Vec3f atlasSkyLightDirection,
        Vec3f atlasSkyLightColor,
        float atlasSkyDaylight,
        float atlasSkyExposure
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
        int[]? capturedDrawBuffers = DrawBufferDiagnosticsEnabled
            ? TryCaptureDrawBuffers()
            : null;
        LastDrawBufferStateCheckPerformed =
            DrawBufferDiagnosticsEnabled && capturedDrawBuffers != null;
        LastDrawBufferStateCheckPassed = !DrawBufferDiagnosticsEnabled
            || capturedDrawBuffers != null;
        LastDrawBufferStateDiagnostic = DrawBufferDiagnosticsEnabled
            ? capturedDrawBuffers == null
                ? "draw-buffer probe could not capture the current framebuffer"
                : FormatDrawBuffers(capturedDrawBuffers)
            : "draw-buffer probe is disabled";
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

            // Draw-buffer state belongs to the framebuffer currently bound.
            // Configure the resolver target here, before rendering, and never
            // write it after RestoreCapturedState() has rebound Primary or
            // the default framebuffer.
            if (HasValidityMask)
            {
                GL.DrawBuffers(
                    2,
                    new[]
                    {
                        DrawBuffersEnum.ColorAttachment0,
                        DrawBuffersEnum.ColorAttachment1
                    }
                );
            }
            else
            {
                GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            }

            shader.Use();
            shader.BindTexture2D("sourceColorTex", source.ColorTextureIds[0], 0);
            shader.BindTexture2D("sourceDepthTex", source.DepthTextureId, 1);
            float[] modelView = Array.ConvertAll(
                view,
                value => (float)value
            );
            // Resolve camera matrices once on the CPU. The boundary shader
            // runs for every atlas pixel, so doing a mat4 inverse in the
            // fragment path would turn the opaque background into a sizeable
            // avoidable per-pixel cost.
            float[] projectionView = Mat4f.Create();
            Mat4f.Multiply(projectionView, projection, modelView);
            float[] inverseProjectionView = Mat4f.Create();
            Mat4f.Invert(inverseProjectionView, projectionView);
            shader.UniformMatrix(
                "inverseProjectionView",
                inverseProjectionView
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
            // The final resolve owns the atlas-only background. These values
            // come from the already selected live/fixed-hour atlas lighting
            // state and are never installed in the ordinary world shader
            // path. The shader uses them only for pixels rejected by the
            // disclosure/depth predicates (and a restrained edge haze for
            // pixels that passed those predicates).
            shader.Uniform("atlasSkyLightDirection", atlasSkyLightDirection);
            shader.Uniform("atlasSkyLightColor", atlasSkyLightColor);
            shader.Uniform(
                "atlasSkyDaylight",
                Math.Clamp(atlasSkyDaylight, 0f, 1.5f)
            );
            shader.Uniform(
                "atlasSkyExposure",
                Math.Clamp(
                    atlasSkyExposure,
                    0.04f,
                    AtlasExposureCalibration.MaximumMultiplier
                )
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

            if (DrawBufferDiagnosticsEnabled && capturedDrawBuffers != null)
            {
                int[]? restoredDrawBuffers = TryCaptureDrawBuffers();
                bool unchanged = restoredDrawBuffers != null
                    && DrawBuffersEqual(
                        capturedDrawBuffers,
                        restoredDrawBuffers
                    );
                LastDrawBufferStateCheckPerformed = restoredDrawBuffers != null;
                LastDrawBufferStateCheckPassed = unchanged;
                LastDrawBufferStateDiagnostic = restoredDrawBuffers == null
                    ? "draw-buffer probe could not inspect the restored framebuffer"
                    : $"before={FormatDrawBuffers(capturedDrawBuffers)}, after={FormatDrawBuffers(restoredDrawBuffers)}";
                if (!unchanged && !loggedDrawBufferMismatch)
                {
                    loggedDrawBufferMismatch = true;
                    capi.Logger.Error(
                        "[ModernAtlas] Atlas boundary resolve changed the previously bound framebuffer draw buffers: {0}.",
                        LastDrawBufferStateDiagnostic
                    );
                }
            }
        }
    }

    private static int[]? TryCaptureDrawBuffers()
    {
        try
        {
            GL.GetInteger(GetPName.MaxDrawBuffers, out int maximum);
            maximum = Math.Clamp(maximum, 1, 16);
            int[] values = new int[maximum];
            for (int index = 0; index < values.Length; index++)
            {
                GL.GetInteger(
                    (GetPName)((int)GetPName.DrawBuffer0 + index),
                    out values[index]
                );
            }
            return values;
        }
        catch
        {
            return null;
        }
    }

    private static bool DrawBuffersEqual(int[] expected, int[] actual)
    {
        if (expected.Length != actual.Length) return false;
        for (int index = 0; index < expected.Length; index++)
        {
            if (expected[index] != actual[index]) return false;
        }
        return true;
    }

    private static string FormatDrawBuffers(int[] values) =>
        string.Join(",", values);


    private bool EnsureResources(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        quad ??= capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        if (framebuffer is { Disposed: false }
            && framebuffer.Width == width
            && framebuffer.Height == height)
        {
            return framebuffer.ColorTextureIds is { Length: > 0 }
                && framebuffer.ColorTextureIds[0] > 0;
        }

        DestroyFramebuffer();

        try
        {
            framebuffer = capi.Render.CreateFrameBuffer(
                CreateAttributes(
                    "modernatlas-boundary-resolve",
                    width,
                    height,
                    includeValidity: true
                )
            );
            if (framebuffer is { Disposed: false }
                && framebuffer.ColorTextureIds is { Length: > 1 }
                && framebuffer.ColorTextureIds[0] > 0
                && framebuffer.ColorTextureIds[1] > 0)
            {
                return true;
            }
        }
        catch (Exception exception)
        {
            LogMrtFallback(exception.Message);
        }

        DestroyFramebuffer();
        try
        {
            framebuffer = capi.Render.CreateFrameBuffer(
                CreateAttributes(
                    "modernatlas-boundary-resolve-color",
                    width,
                    height,
                    includeValidity: false
                )
            );
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not create the color-only atlas boundary framebuffer: {0}",
                exception.Message
            );
            framebuffer = null;
        }
        LogMrtFallback("the optional validity attachment is unavailable");
        return framebuffer is { Disposed: false }
            && framebuffer.ColorTextureIds is { Length: > 0 }
            && framebuffer.ColorTextureIds[0] > 0;
    }

    private FramebufferAttrs CreateAttributes(
        string name,
        int width,
        int height,
        bool includeValidity
    )
    {
        List<FramebufferAttrsAttachment> attachments = new()
        {
            new FramebufferAttrsAttachment
            {
                AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                Texture = CreateColorTexture(width, height)
            }
        };
        if (includeValidity)
        {
            attachments.Add(
                new FramebufferAttrsAttachment
                {
                    AttachmentType = EnumFramebufferAttachment.ColorAttachment1,
                    Texture = CreateColorTexture(width, height)
                }
            );
        }
        return new FramebufferAttrs(name, width, height)
        {
            Attachments = attachments.ToArray()
        };
    }

    private static RawTexture CreateColorTexture(int width, int height) => new()
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

    private void LogMrtFallback(string reason)
    {
        if (loggedMrtFallback) return;
        loggedMrtFallback = true;
        capi.Logger.Warning(
            "[ModernAtlas] Screenshot validity MRT is unavailable ({0}); the color boundary remains enabled and spatial screenshot filters will be disabled.",
            reason
        );
    }

    /// <summary>
    /// Opt-in CPU diagnostic for the optional second boundary attachment. It
    /// never participates in normal atlas rendering or capture readback.
    /// </summary>
    public bool TryReadValidityMask(
        out byte[] mask,
        out AtlasValidityMaskDiagnostics diagnostics
    )
    {
        mask = Array.Empty<byte>();
        diagnostics = default;
        FrameBufferRef? target = Framebuffer;
        if (target == null || !HasValidityMask)
        {
            return false;
        }

        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        try
        {
            render.CurrentActiveShader?.Stop();
            render.CurrentFrameBuffer = target;
            render.GlViewport(0, 0, target.Width, target.Height);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment1);
            GL.PixelStore(PixelStoreParameter.PackRowLength, 0);
            GL.PixelStore(PixelStoreParameter.PackSkipRows, 0);
            GL.PixelStore(PixelStoreParameter.PackSkipPixels, 0);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            mask = new byte[checked(target.Width * target.Height)];
            GL.ReadPixels(
                0,
                0,
                target.Width,
                target.Height,
                PixelFormat.Red,
                PixelType.UnsignedByte,
                mask
            );
            long validPixels = 0;
            foreach (byte value in mask)
            {
                if (value >= 128) validPixels++;
            }
            diagnostics = new AtlasValidityMaskDiagnostics(
                target.Width,
                target.Height,
                validPixels,
                mask.LongLength
            );
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not read the atlas validity mask: {0}",
                exception.Message
            );
            mask = Array.Empty<byte>();
            diagnostics = default;
            return false;
        }
        finally
        {
            renderState.RestoreCapturedState();
            try
            {
                GL.PixelStore(PixelStoreParameter.PackRowLength, 0);
                GL.PixelStore(PixelStoreParameter.PackSkipRows, 0);
                GL.PixelStore(PixelStoreParameter.PackSkipPixels, 0);
                GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
                GL.ReadBuffer(
                    render.CurrentFrameBuffer == null
                        ? ReadBufferMode.Back
                        : ReadBufferMode.ColorAttachment0
                );
            }
            catch
            {
                // The client may already be leaving the world.
            }
        }
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
        LastDrawBufferStateCheckPassed = true;
        LastDrawBufferStateCheckPerformed = false;
        LastDrawBufferStateDiagnostic = "draw-buffer probe is disabled";
        loggedDrawBufferMismatch = false;
        loggedMrtFallback = false;
        DestroyFramebuffer();
        quad?.Dispose();
        quad = null;
    }
}
