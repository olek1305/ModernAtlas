using System;
using Vintagestory.API.Client;

namespace ModernAtlas;

/// <summary>
/// Captures an ordinary world frame at the engine's AfterBlit stage. A tick
/// callback can run before the scene has been drawn, which would produce a
/// valid but useless black screenshot; this renderer observes the completed
/// world framebuffer without changing any rendering state.
/// </summary>
internal sealed class AtlasOrdinaryWorldScreenshotRenderer : IRenderer
{
    private readonly Func<string, bool> capture;
    private readonly Func<bool>? shouldRefreshBackground;
    private readonly Func<bool>? refreshBackground;
    private string? pendingSuffix;
    private Action<bool>? pendingCompletion;

    internal AtlasOrdinaryWorldScreenshotRenderer(
        Func<string, bool> capture,
        Func<bool>? shouldRefreshBackground = null,
        Func<bool>? refreshBackground = null
    )
    {
        this.capture = capture;
        this.shouldRefreshBackground = shouldRefreshBackground;
        this.refreshBackground = refreshBackground;
    }

    public double RenderOrder => 0.99;
    public int RenderRange => int.MaxValue;

    internal bool Queue(string suffix, Action<bool> completion)
    {
        if (pendingSuffix != null) return false;
        pendingSuffix = suffix;
        pendingCompletion = completion;
        return true;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        _ = deltaTime;
        if (stage != EnumRenderStage.AfterBlit) return;

        // A queued smoke screenshot has priority. Do not perform a second
        // full-frame readback in the same AfterBlit callback for the blurred
        // transition background.
        if (pendingSuffix == null)
        {
            if (shouldRefreshBackground?.Invoke() != true) return;
            try
            {
                refreshBackground?.Invoke();
            }
            catch
            {
                // A transient readback failure must never affect the ordinary
                // world renderer or prevent the transition's fallback from
                // being shown.
            }
            return;
        }

        string suffix = pendingSuffix;
        Action<bool>? completion = pendingCompletion;
        pendingSuffix = null;
        pendingCompletion = null;

        bool passed;
        try
        {
            passed = capture(suffix);
        }
        catch
        {
            passed = false;
        }
        completion?.Invoke(passed);
    }

    public void Dispose()
    {
        pendingSuffix = null;
        pendingCompletion = null;
    }
}
