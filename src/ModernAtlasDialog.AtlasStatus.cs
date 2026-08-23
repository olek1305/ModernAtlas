using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// First-frame preparation status for the live atlas. This is a GUI-only
/// overlay: it never inspects framebuffer pixels and never participates in
/// terrain rendering or disclosure decisions.
/// </summary>
public sealed partial class ModernAtlasDialog
{
    private const long AtlasPreparationDelayedMilliseconds = 3000;
    private const string AtlasPreparingText = "Preparing atlas...";
    private const string AtlasDelayedPreparationText =
        "Atlas preparation is taking longer than usual.";
    private const string AtlasRenderingFailedText =
        "Atlas rendering failed. See client-main.log for details.";

    private long atlasPreparationStartedMilliseconds;
    private bool atlasPreparationSessionActive;
    private bool atlasPreparationDelayed;
    private bool atlasRenderingFailure;
    private string? atlasRenderingFailureReason;
    private string? atlasLastLoggedPreparationReason;
    private bool atlasFirstCompleteFrameLogged;
    private LoadedTexture? atlasPreparationStatusTexture;
    private string? atlasPreparationStatusTextureText;
    private int atlasPreparationStatusTextureWidth;
    private int atlasPreparationStatusTextureHeight;

    private bool HasCompleteAtlasFrame =>
        atlasFrameCacheTexture is { TextureId: > 0 };

    private void BeginAtlasPreparationSession()
    {
        atlasPreparationSessionActive = true;
        atlasPreparationStartedMilliseconds = capi.ElapsedMilliseconds;
        atlasPreparationDelayed = false;
        atlasRenderingFailure = false;
        atlasRenderingFailureReason = null;
        atlasLastLoggedPreparationReason = null;
        atlasFirstCompleteFrameLogged = false;
        SyncToolbarControls();
        capi.Logger.Notification(
            "[ModernAtlas] Atlas preparation started for the {0} presentation.",
            config.RenderOnScroll ? "Scroll" : "Fullscreen"
        );
    }

    private void ResetAtlasPreparationState()
    {
        atlasPreparationStartedMilliseconds = 0;
        atlasPreparationSessionActive = false;
        atlasPreparationDelayed = false;
        atlasRenderingFailure = false;
        atlasRenderingFailureReason = null;
        atlasLastLoggedPreparationReason = null;
        atlasFirstCompleteFrameLogged = false;
        SyncToolbarControls();
    }

    private void LogAtlasPreparationReason(string reason)
    {
        if (string.Equals(
            atlasLastLoggedPreparationReason,
            reason,
            StringComparison.Ordinal
        ))
        {
            return;
        }

        atlasLastLoggedPreparationReason = reason;
        capi.Logger.Notification(
            "[ModernAtlas] Atlas preparation reason changed: {0}.",
            reason
        );
    }

    private void MarkAtlasRenderingFailure(string reason)
    {
        if (atlasRenderingFailure) return;

        atlasRenderingFailure = true;
        atlasRenderingFailureReason = string.IsNullOrWhiteSpace(reason)
            ? "The atlas renderer reported an unavailable or failed rendering path."
            : reason;
        capi.Logger.Error(
            "[ModernAtlas] Atlas rendering failed: {0}",
            atlasRenderingFailureReason
        );
    }

    private void MarkAtlasFramePublished()
    {
        bool firstPublication = !atlasFirstCompleteFrameLogged;
        long elapsedMilliseconds = Math.Max(
            0,
            capi.ElapsedMilliseconds - atlasPreparationStartedMilliseconds
        );
        if (firstPublication)
        {
            atlasFirstCompleteFrameLogged = true;
            capi.Logger.Notification(
                "[ModernAtlas] First complete atlas frame published after {0} ms.",
                elapsedMilliseconds
            );
        }

        atlasPreparationSessionActive = false;
        atlasPreparationDelayed = false;
        // A later successful publication is allowed to recover a transient
        // renderer or copy error. The status was already hidden whenever a
        // previous complete cache frame existed.
        atlasRenderingFailure = false;
        atlasRenderingFailureReason = null;
        if (firstPublication) SyncToolbarControls();
    }

    private string GetAtlasPreparationStatusText()
    {
        if (!atlasPreparationSessionActive || HasCompleteAtlasFrame)
        {
            return string.Empty;
        }

        if (atlasRenderingFailure)
        {
            return AtlasRenderingFailedText;
        }

        long elapsedMilliseconds = Math.Max(
            0,
            capi.ElapsedMilliseconds - atlasPreparationStartedMilliseconds
        );
        if (elapsedMilliseconds >= AtlasPreparationDelayedMilliseconds
            && !atlasPreparationDelayed)
        {
            atlasPreparationDelayed = true;
            capi.Logger.Notification(
                "[ModernAtlas] Atlas preparation status changed to delayed after {0} ms; elapsed time is not treated as a rendering failure.",
                elapsedMilliseconds
            );
        }

        return atlasPreparationDelayed
            ? AtlasDelayedPreparationText
            : AtlasPreparingText;
    }

    /// <summary>
    /// Draws status after the atlas frame and before toolbar controls. The
    /// viewport-relative placement is shared by Scroll and Fullscreen, while
    /// the toolbar is drawn later and therefore remains interactive and
    /// readable above this non-interactive card.
    /// </summary>
    private void RenderAtlasPreparationStatus()
    {
        if (interfaceHidden || screenshotPreviewOpen || screenshotPreviewOpening)
        {
            return;
        }

        string text = GetAtlasPreparationStatusText();
        if (text.Length == 0) return;
        if (!EnsureAtlasPreparationStatusTexture(text)) return;

        AtlasViewportBounds viewport = AtlasViewport;
        if (viewport.Width <= 0 || viewport.Height <= 0) return;

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double toolbarWidth = ToolbarWidthGui(viewport.Width / scale) * scale;
        double left = viewport.X + toolbarWidth + 28 * scale;
        double right = viewport.Right - 16 * scale;
        double availableCenter = left < right
            ? (left + right) * 0.5
            : viewport.X + viewport.Width * 0.5;
        double x = availableCenter - atlasPreparationStatusTextureWidth * 0.5;
        double y = viewport.Y
            + (viewport.Height - atlasPreparationStatusTextureHeight) * 0.5;
        double minimumX = viewport.X + 8 * scale;
        double maximumX = Math.Max(
            minimumX,
            viewport.Right - atlasPreparationStatusTextureWidth - 8 * scale
        );
        x = Math.Clamp(x, minimumX, maximumX);
        y = Math.Clamp(
            y,
            viewport.Y + 8 * scale,
            Math.Max(
                viewport.Y + 8 * scale,
                viewport.Bottom - atlasPreparationStatusTextureHeight - 8 * scale
            )
        );

        capi.Render.Render2DTexture(
            atlasPreparationStatusTexture!.TextureId,
            (float)x,
            (float)y,
            atlasPreparationStatusTextureWidth,
            atlasPreparationStatusTextureHeight,
            42,
            ColorUtil.WhiteArgbVec
        );
    }

    private bool EnsureAtlasPreparationStatusTexture(string text)
    {
        if (atlasPreparationStatusTexture is { TextureId: > 0 }
            && string.Equals(
                atlasPreparationStatusTextureText,
                text,
                StringComparison.Ordinal
            ))
        {
            return true;
        }

        try
        {
            double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
            int width;
            int height = Math.Max(42, (int)Math.Ceiling(48 * scale));
            using (CairoFont font = AtlasUiStyle.LabelFont((float)(14 * scale)))
            {
                using ImageSurface measurementSurface = new(Format.Argb32, 4, 4);
                using Context measurementContext = new(measurementSurface);
                font.SetupContext(measurementContext);
                TextExtents extents = font.GetTextExtents(text);
                width = Math.Max(
                    (int)Math.Ceiling(220 * scale),
                    (int)Math.Ceiling(extents.Width + 44 * scale)
                );
            }

            using ImageSurface surface = new(Format.Argb32, width, height);
            using Context context = new(surface);
            AtlasUiStyle.Clear(context);
            AtlasUiStyle.DrawRaisedPanel(
                context,
                0,
                0,
                width,
                height,
                12 * scale
            );
            using (CairoFont font = AtlasUiStyle.LabelFont((float)(14 * scale)))
            {
                AtlasUiStyle.DrawCenteredText(context, font, text, width, height);
            }

            atlasPreparationStatusTexture ??= new LoadedTexture(capi);
            capi.Gui.LoadOrUpdateCairoTexture(
                surface,
                true,
                ref atlasPreparationStatusTexture
            );
            if (atlasPreparationStatusTexture?.TextureId <= 0) return false;

            atlasPreparationStatusTextureText = text;
            atlasPreparationStatusTextureWidth = width;
            atlasPreparationStatusTextureHeight = height;
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not build the atlas preparation status texture: {0}",
                exception.Message
            );
            return false;
        }
    }

    private void DisposeAtlasPreparationStatusTexture()
    {
        atlasPreparationStatusTexture?.Dispose();
        atlasPreparationStatusTexture = null;
        atlasPreparationStatusTextureText = null;
        atlasPreparationStatusTextureWidth = 0;
        atlasPreparationStatusTextureHeight = 0;
    }
}
