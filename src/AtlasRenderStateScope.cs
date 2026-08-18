using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ModernAtlas;

/// <summary>
/// Keeps the small amount of render state that ModernAtlas can safely observe
/// through the Vintage Story API while an atlas-owned pass temporarily changes
/// the target or shader.  OpenGL boolean state is deliberately not queried
/// here: callers restore the explicit handoff state required by their next
/// renderer after this scope has restored the captured target and program.
/// </summary>
internal sealed class AtlasRenderStateScope : IDisposable
{
    private readonly IRenderAPI render;
    private readonly FrameBufferRef? previousFrameBuffer;
    private readonly IShaderProgram? previousShader;
    private bool restored;

    private AtlasRenderStateScope(IRenderAPI render)
    {
        this.render = render;
        previousFrameBuffer = render.CurrentFrameBuffer;
        previousShader = render.CurrentActiveShader;
    }

    public static AtlasRenderStateScope Capture(IRenderAPI render) =>
        new(render);

    /// <summary>
    /// Restores only values exposed as readable API properties.  The caller
    /// then selects the explicit GUI/world handoff state it owns.
    /// </summary>
    public void RestoreCapturedState()
    {
        if (restored) return;
        restored = true;

        try
        {
            render.CurrentActiveShader?.Stop();
        }
        catch
        {
            // A shader can become invalid during a client teardown. The
            // framebuffer and the remaining explicit handoff still need to be
            // restored in that case.
        }

        render.CurrentFrameBuffer = previousFrameBuffer;
        if (previousShader != null && !previousShader.Disposed)
        {
            try
            {
                previousShader.Use();
            }
            catch
            {
                // Do not activate a disposed/partially torn-down shader.
            }
        }
    }

    /// <summary>
    /// Restores the captured target/program and establishes the explicit GUI
    /// handoff used by ModernAtlas' late GUI passes. This is not presented as
    /// a generic OpenGL snapshot; it is the state the caller owns next.
    /// </summary>
    public void RestoreGuiHandoff(bool blendEnabled = true)
    {
        RestoreCapturedState();
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
        render.GlColorMask(true, true, true, true);
        render.GlScissorFlag(false);
        render.GLDisableDepthTest();
        render.GLDepthMask(false);
        render.GlDisableCullFace();
        render.GlToggleBlend(blendEnabled, EnumBlendMode.Standard);
    }

    /// <summary>
    /// Restores the captured target/program and the explicit world-space
    /// opaque handoff used by the remote transition scroll.
    /// </summary>
    public void RestoreWorldOpaqueHandoff()
    {
        RestoreCapturedState();
        render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
        render.GlColorMask(true, true, true, true);
        render.GlScissorFlag(false);
        render.GLEnableDepthTest();
        render.GLDepthMask(true);
        render.GlEnableCullFace();
        render.GlToggleBlend(false, EnumBlendMode.Standard);
    }

    public void Dispose() => RestoreCapturedState();
}
