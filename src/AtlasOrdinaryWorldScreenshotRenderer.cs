using System;
using Vintagestory.API.Client;

namespace ModernAtlas;

/// <summary>
/// Captures explicit automated-test screenshots at the engine's AfterBlit
/// stage. Production gameplay never registers this renderer, and an idle
/// callback never performs a readback or any other background work.
/// </summary>
internal sealed class AtlasOrdinaryWorldScreenshotRenderer : IRenderer
{
    private readonly Func<string, bool> capture;
    private readonly Action<string>? telemetryLogger;
    private string? pendingSuffix;
    private Action<bool>? pendingCompletion;
    private long queuedCaptureCount;
    private long completedCaptureCount;

    internal AtlasOrdinaryWorldScreenshotRenderer(
        Func<string, bool> capture,
        Action<string>? telemetryLogger = null
    )
    {
        this.capture = capture;
        this.telemetryLogger = telemetryLogger;
    }

    public double RenderOrder => 0.99;
    public int RenderRange => int.MaxValue;
    internal long QueuedCaptureCount => queuedCaptureCount;
    internal long CompletedCaptureCount => completedCaptureCount;

    internal bool Queue(string suffix, Action<bool> completion)
    {
        if (pendingSuffix != null) return false;
        pendingSuffix = suffix;
        pendingCompletion = completion;
        if (queuedCaptureCount < long.MaxValue) queuedCaptureCount++;
        return true;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        _ = deltaTime;
        if (stage != EnumRenderStage.AfterBlit) return;

        if (pendingSuffix == null) return;

        string suffix = pendingSuffix;
        Action<bool>? completion = pendingCompletion;
        pendingSuffix = null;
        pendingCompletion = null;

        bool passed;
        try
        {
            passed = capture(suffix);
            if (completedCaptureCount < long.MaxValue) completedCaptureCount++;
        }
        catch
        {
            passed = false;
        }
        completion?.Invoke(passed);
    }

    public void Dispose()
    {
        LogTelemetry("dispose");
        pendingSuffix = null;
        pendingCompletion = null;
    }

    internal void LogTelemetry(string reason)
    {
        telemetryLogger?.Invoke(
            FormattableString.Invariant(
                $"[ModernAtlas] Automated ordinary-world screenshot summary: reason={reason}; queued={queuedCaptureCount}; completed={completedCaptureCount}; idleReadbacks=0."
            )
        );
    }

    /// <summary>
    /// Pure scheduler self-check used by the automated smoke test. It drives
    /// the renderer with delegates only, so it never touches a framebuffer.
    /// </summary>
    internal static string? ValidatePerformancePolicy()
    {
        int captureCalls = 0;
        AtlasOrdinaryWorldScreenshotRenderer renderer =
            new(
                _ =>
                {
                    captureCalls++;
                    return true;
                }
            );

        // Both profiles use this same dormant production policy: an
        // unrequested frame must never touch the screenshot delegate.
        renderer.OnRenderFrame(0, EnumRenderStage.AfterBlit);
        renderer.OnRenderFrame(0, EnumRenderStage.AfterBlit);
        if (captureCalls != 0)
        {
            renderer.Dispose();
            return "idle AfterBlit frames must never perform a readback";
        }

        if (!renderer.Queue("policy-check", _ => { }))
        {
            renderer.Dispose();
            return "an explicit automated screenshot could not be queued";
        }
        renderer.OnRenderFrame(0, EnumRenderStage.AfterBlit);
        renderer.Dispose();
        if (captureCalls != 1)
        {
            return "an explicit automated screenshot must perform exactly one readback";
        }
        return null;
    }
}
