using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

/// <summary>
/// Atlas damage warning. Losing health while the atlas is open draws a dark red
/// edge vignette plus a short caption over the atlas framebuffer. It is a GUI
/// overlay only: no shader, no world rendering and no engine state is touched,
/// it never captures mouse or keyboard input, and it never hints at the source
/// of the damage, so it cannot disclose an entity the atlas was not allowed to
/// draw. Every source of health loss counts, including fire, falling and
/// hunger.
/// </summary>
public sealed partial class ModernAtlasDialog
{
    /// <summary>
    /// Mode policy. This deliberately reads only the local player's actual game
    /// mode: Cheat Mode stays a protected feature switch and must not disable a
    /// safety warning, so Survival with accepted Cheat Mode behaves exactly like
    /// Survival. Only real Creative suppresses the warning and the auto-close.
    /// An unreadable game mode keeps the warning available; it still needs a
    /// real health drop to appear.
    /// </summary>
    private static bool DamageWarningPolicyForMode(EnumGameMode mode) =>
        mode != EnumGameMode.Creative;

    private bool DamageWarningPolicyActive =>
        DamageWarningPolicyForMode(EffectiveDamageWarningGameMode);

    /// <summary>
    /// The local player's actual game mode, or the automated test's in-memory
    /// override. The override exists because the owner's standard smoke world
    /// runs in Creative, where the warning is correctly suppressed and its
    /// behaviour could otherwise never be exercised. It changes no player state,
    /// no game mode and nothing on disk, and production code always resolves the
    /// real mode.
    /// </summary>
    private EnumGameMode EffectiveDamageWarningGameMode
    {
        get
        {
            if (automatedDamageWarningModeOverride != null)
            {
                return automatedDamageWarningModeOverride.Value;
            }
            try
            {
                return capi.World.Player?.WorldData.CurrentGameMode
                    ?? EnumGameMode.Survival;
            }
            catch
            {
                return EnumGameMode.Survival;
            }
        }
    }

    internal void SetDamageWarningModeOverrideForAutomatedTest(EnumGameMode? mode)
    {
        automatedDamageWarningModeOverride = mode;
    }

    private bool AutoCloseOnDamageActive =>
        DamageWarningPolicyActive && config.CloseAtlasOnDamage;

    /// <summary>
    /// Runs the deferred safety close at the start of a frame, before the atlas
    /// binds any framebuffer. Returns true when the atlas was closed and the
    /// caller must skip the rest of the frame.
    /// </summary>
    private bool FlushPendingEmergencyClose()
    {
        if (!damageWarningEmergencyClosePending) return false;

        damageWarningEmergencyClosePending = false;
        RequestEmergencyAtlasClose();
        return !IsOpened();
    }

    private void ResetDamageWarning()
    {
        damageWarningBaselineHealth = float.NaN;
        damageWarningElapsedSeconds = 0;
        damageWarningPulseStrength = 0;
        damageWarningLowHealth = false;
        damageWarningAutoCloseRequested = false;
        damageWarningEmergencyClosePending = false;
    }

    /// <summary>
    /// Reads the local player's health once per frame and drives the warning
    /// state with real render time, so it keeps animating while singleplayer is
    /// paused. The first frame after opening only records the baseline.
    /// </summary>
    private void AdvanceDamageWarning()
    {
        if (!DamageWarningPolicyActive)
        {
            ResetDamageWarning();
            return;
        }

        if (damageWarningElapsedSeconds < DamageWarningDurationSeconds)
        {
            damageWarningElapsedSeconds += atlasRealDeltaTime;
        }

        float current = float.NaN;
        float maximum = float.NaN;
        try
        {
            ITreeAttribute? healthTree = capi.World.Player?.Entity
                ?.WatchedAttributes.GetTreeAttribute("health");
            if (healthTree != null)
            {
                current = healthTree.GetFloat("currenthealth", float.NaN);
                maximum = healthTree.GetFloat("maxhealth", float.NaN);
            }
        }
        catch
        {
            return;
        }

        if (float.IsNaN(current) || float.IsNaN(maximum) || maximum <= 0) return;

        damageWarningLowHealth = current > 0
            && current / maximum < LowHealthWarningFraction;

        if (!float.IsNaN(damageWarningBaselineHealth)
            && current < damageWarningBaselineHealth - 0.01f)
        {
            SignalDamageWarning();
        }
        damageWarningBaselineHealth = current;
    }

    /// <summary>
    /// Starts or restarts the pulse. A second hit restarts the envelope and
    /// slightly strengthens it, and requests the optional emergency close once
    /// per damage episode.
    /// </summary>
    private void SignalDamageWarning()
    {
        damageWarningElapsedSeconds = 0;
        damageWarningPulseStrength = Math.Min(
            1f,
            damageWarningPulseStrength <= 0 ? 0.65f : damageWarningPulseStrength + 0.35f
        );
        if (!AutoCloseOnDamageActive || damageWarningAutoCloseRequested) return;

        // Never close the dialog from inside the atlas render pass: OnGuiClosed
        // verifies that no world framebuffer is left bound, and mid-pass the
        // engine still has one. The close runs at the start of the next frame.
        damageWarningAutoCloseRequested = true;
        damageWarningEmergencyClosePending = true;
    }

    /// <summary>
    /// Test hook. It feeds the warning state machine directly, so the automated
    /// smoke test never has to hurt the player or write to the save.
    /// </summary>
    internal void TriggerDamageWarningForAutomatedTest(bool lowHealth)
    {
        if (!DamageWarningPolicyActive) return;
        damageWarningLowHealth = lowHealth;
        SignalDamageWarning();
    }

    internal bool DamageWarningActiveForAutomatedTest =>
        DamageWarningVignetteAlpha() > 0;

    /// <summary>
    /// Reports whether the warning would actually be drawn, including the
    /// screenshot suppression, so the smoke test can prove a capture is never
    /// contaminated by it.
    /// </summary>
    internal bool DamageWarningWouldRenderForAutomatedTest =>
        !pendingScreenshotRequest
        && !tileScreenshot.Busy
        && !screenshotPreviewOpen
        && !screenshotPreviewOpening
        && DamageWarningVignetteAlpha() > 0.002f;

    internal bool DamageWarningLowHealthForAutomatedTest => damageWarningLowHealth;
    internal EnumGameMode LocalGameModeForAutomatedTest
    {
        get
        {
            try
            {
                return capi.World.Player?.WorldData.CurrentGameMode
                    ?? EnumGameMode.Survival;
            }
            catch
            {
                return EnumGameMode.Survival;
            }
        }
    }

    internal bool AutoCloseOnDamageActiveForAutomatedTest => AutoCloseOnDamageActive;

    /// <summary>
    /// Proves the overlay can really be drawn, not just that its state is
    /// active: both GPU textures must exist for the current viewport. A state
    /// check alone once hid a texture build failure.
    /// </summary>
    internal bool DamageWarningTexturesReadyForAutomatedTest
    {
        get
        {
            AtlasViewportBounds viewport = AtlasViewport;
            return viewport.Width > 0
                && viewport.Height > 0
                && EnsureDamageVignetteTexture(viewport.Width, viewport.Height)
                && EnsureDamageWarningCaption("LOW HEALTH")
                && damageVignetteTexture?.TextureId > 0
                && damageWarningCaptionTexture?.TextureId > 0;
        }
    }

    internal static bool DamageWarningPolicyForModeForAutomatedTest(EnumGameMode mode) =>
        DamageWarningPolicyForMode(mode);

    /// <summary>
    /// Safety close. A running tiled capture or an open screenshot preview must
    /// never block it: the capture is cancelled, the camera restored and the
    /// modal closed before the atlas itself is closed without the scroll
    /// stowing animation, so the player regains control immediately.
    /// </summary>
    private void RequestEmergencyAtlasClose()
    {
        if (screenshotPreviewOpen || screenshotPreviewOpening)
        {
            CloseScreenshotPreview(false);
        }
        if (tileScreenshot.Busy || pendingScreenshotRequest)
        {
            CancelScreenshotCapture();
        }
        capi.Logger.Notification(
            "[ModernAtlas] Damage detected while the atlas was open; closing it immediately without the stowing animation."
        );
        requestEmergencyClose();
    }

    private float DamageWarningVignetteAlpha()
    {
        float steady = damageWarningLowHealth ? 0.30f : 0f;
        if (damageWarningPulseStrength <= 0
            || damageWarningElapsedSeconds >= DamageWarningDurationSeconds)
        {
            return steady;
        }

        float progress = damageWarningElapsedSeconds / DamageWarningDurationSeconds;
        // Two soft pulses that fade out over the window.
        float envelope = (1f - progress) * (1f - progress);
        float pulse = 0.55f + 0.45f * MathF.Sin(progress * MathF.PI * 4f);
        return Math.Clamp(
            steady + damageWarningPulseStrength * envelope * pulse * 0.55f,
            0f,
            0.62f
        );
    }

    /// <summary>
    /// Draws the warning above the atlas frame and below the atlas panels, so
    /// controls stay readable. It is skipped while a tiled capture or the
    /// screenshot preview owns the frame: the warning must never be baked into
    /// a saved PNG or into the BEFORE/AFTER comparison.
    /// </summary>
    private void RenderDamageWarning()
    {
        if (pendingScreenshotRequest
            || tileScreenshot.Busy
            || screenshotPreviewOpen
            || screenshotPreviewOpening)
        {
            return;
        }

        float alpha = DamageWarningVignetteAlpha();
        if (alpha <= 0.002f) return;

        AtlasViewportBounds viewport = AtlasViewport;
        if (viewport.Width <= 0 || viewport.Height <= 0) return;
        if (!EnsureDamageVignetteTexture(viewport.Width, viewport.Height)) return;

        capi.Render.Render2DTexture(
            damageVignetteTexture!.TextureId,
            viewport.X,
            viewport.Y,
            viewport.Width,
            viewport.Height,
            60,
            new Vec4f(1f, 1f, 1f, alpha)
        );

        string caption = damageWarningPulseStrength > 0
            && damageWarningElapsedSeconds < DamageWarningDurationSeconds
            ? "TAKING DAMAGE — CLOSE THE ATLAS"
            : "LOW HEALTH";
        if (!EnsureDamageWarningCaption(caption)) return;

        float captionWidth = damageWarningCaptionTexture!.Width;
        float captionHeight = damageWarningCaptionTexture.Height;
        capi.Render.Render2DTexture(
            damageWarningCaptionTexture.TextureId,
            viewport.X + (viewport.Width - captionWidth) * 0.5f,
            viewport.Y + 18,
            captionWidth,
            captionHeight,
            61,
            new Vec4f(1f, 1f, 1f, Math.Clamp(0.55f + alpha, 0f, 1f))
        );
    }

    private bool EnsureDamageVignetteTexture(int width, int height)
    {
        if (damageVignetteTexture?.TextureId > 0
            && damageVignetteTexture.Width == width
            && damageVignetteTexture.Height == height)
        {
            return true;
        }

        try
        {
            using ImageSurface surface = new(Format.Argb32, width, height);
            using Context context = new(surface);
            AtlasUiStyle.Clear(context);

            // Dark red edges with a fully transparent centre so the map stays
            // readable. The old red-border defect was a shader artifact; this is
            // a GUI texture that never touches world rendering.
            // Keep the band narrow and bounded: the map must stay readable and
            // the effect must not read as a red frame around the atlas.
            double thickness = Math.Clamp(Math.Min(width, height) * 0.085, 24, 96);
            DrawDamageEdge(context, 0, 0, width, thickness, 0, 1);
            DrawDamageEdge(context, 0, height - thickness, width, thickness, 0, -1);
            DrawDamageEdge(context, 0, 0, thickness, height, 1, 0);
            DrawDamageEdge(context, width - thickness, 0, thickness, height, -1, 0);
            // LoadOrUpdateCairoTexture needs an existing instance, exactly like
            // the ore hover and search marker textures.
            damageVignetteTexture ??= new LoadedTexture(capi);
            capi.Gui.LoadOrUpdateCairoTexture(
                surface,
                true,
                ref damageVignetteTexture
            );
            return damageVignetteTexture?.TextureId > 0;
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not build the damage warning vignette: {0}",
                exception.Message
            );
            return false;
        }
    }

    private static void DrawDamageEdge(
        Context context,
        double x,
        double y,
        double width,
        double height,
        int horizontalDirection,
        int verticalDirection
    )
    {
        double startX = horizontalDirection >= 0 ? x : x + width;
        double startY = verticalDirection >= 0 ? y : y + height;
        double endX = horizontalDirection == 0
            ? startX
            : startX + horizontalDirection * width;
        double endY = verticalDirection == 0
            ? startY
            : startY + verticalDirection * height;
        using LinearGradient gradient = new(startX, startY, endX, endY);
        gradient.AddColorStop(0, new Color(0.40, 0.02, 0.02, 0.70));
        // Fall off quickly so only the outer edge carries colour.
        gradient.AddColorStop(0.45, new Color(0.40, 0.02, 0.02, 0.18));
        gradient.AddColorStop(1, new Color(0.40, 0.02, 0.02, 0));
        context.SetSource(gradient);
        context.Rectangle(x, y, width, height);
        context.Fill();
    }

    private bool EnsureDamageWarningCaption(string caption)
    {
        if (damageWarningCaptionTexture?.TextureId > 0
            && string.Equals(
                damageWarningCaptionValue,
                caption,
                StringComparison.Ordinal
            ))
        {
            return true;
        }

        try
        {
            double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
            int width = (int)Math.Ceiling(caption.Length * 12 * scale);
            int height = (int)Math.Ceiling(34 * scale);
            using ImageSurface surface = new(Format.Argb32, width, height);
            using Context context = new(surface);
            AtlasUiStyle.Clear(context);
            AtlasUiStyle.DrawRaisedPanel(context, 0, 0, width, height, 10);
            using CairoFont font = CairoFont.WhiteDetailText()
                .WithFontSize((float)(13 * scale))
                .WithWeight(FontWeight.Bold)
                .WithColor(new[] { 1.0, 0.86, 0.86, 1.0 });
            AtlasUiStyle.DrawCenteredText(context, font, caption, width, height);
            damageWarningCaptionTexture ??= new LoadedTexture(capi);
            capi.Gui.LoadOrUpdateCairoTexture(
                surface,
                true,
                ref damageWarningCaptionTexture
            );
            damageWarningCaptionValue = caption;
            return damageWarningCaptionTexture?.TextureId > 0;
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not build the damage warning caption: {0}",
                exception.Message
            );
            return false;
        }
    }

    private void DisposeDamageWarningTextures()
    {
        damageVignetteTexture?.Dispose();
        damageVignetteTexture = null;
        damageWarningCaptionTexture?.Dispose();
        damageWarningCaptionTexture = null;
        damageWarningCaptionValue = null;
    }
}
