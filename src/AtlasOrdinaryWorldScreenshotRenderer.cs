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
    private string? pendingSuffix;
    private Action<bool>? pendingCompletion;

    internal AtlasOrdinaryWorldScreenshotRenderer(Func<string, bool> capture)
    {
        this.capture = capture;
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
        if (stage != EnumRenderStage.AfterBlit || pendingSuffix == null) return;

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
