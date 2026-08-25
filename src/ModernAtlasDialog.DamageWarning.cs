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
    /// A dead local player must leave the atlas even when the optional
    /// CloseAtlasOnDamage preference is disabled. Invalid health is ignored;
    /// the caller passes <see cref="float.NaN"/> when no health observation is
    /// available yet.
    /// </summary>
    private static bool DamageWarningEmergencyCloseRequired(
        bool alive,
        float currentHealth
    ) => !alive
        || float.IsFinite(currentHealth) && currentHealth <= 0f;

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
        damageWarningRepeatCarryAlpha = 0;
        damageWarningRepeatCarryWidthScale = 0;
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
        var localPlayer = capi.World.Player?.Entity;
        bool localPlayerAlive = localPlayer?.Alive ?? true;
        float current = float.NaN;
        try
        {
            ITreeAttribute? healthTree = localPlayer
                ?.WatchedAttributes.GetTreeAttribute("health");
            if (healthTree != null)
            {
                current = healthTree.GetFloat("currenthealth", float.NaN);
            }
        }
        catch
        {
            if (DamageWarningEmergencyCloseRequired(localPlayerAlive, current))
            {
                QueueDamageWarningEmergencyClose();
            }
            return;
        }

        if (DamageWarningEmergencyCloseRequired(localPlayerAlive, current))
        {
            QueueDamageWarningEmergencyClose();
            return;
        }

        if (!DamageWarningPolicyActive)
        {
            ResetDamageWarning();
            return;
        }

        if (damageWarningElapsedSeconds < DamageWarningDurationSeconds)
        {
            damageWarningElapsedSeconds += atlasRealDeltaTime;
        }

        if (!float.IsFinite(current)) return;

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
        bool ongoingPulse = damageWarningPulseStrength > 0
            && damageWarningElapsedSeconds < DamageWarningDurationSeconds;
        if (ongoingPulse)
        {
            // Restart the episode timer and strengthen its next breath, but
            // carry the visible alpha across the signal so a repeat hit cannot
            // blink the vignette or caption down to zero.
            damageWarningRepeatCarryAlpha = Math.Clamp(
                DamageWarningVignetteAlpha(),
                0f,
                DamageWarningMaximumPulseAlpha
            );
            damageWarningRepeatCarryWidthScale = DamageWarningEdgeWidthScale();
        }
        else
        {
            damageWarningRepeatCarryAlpha = 0;
            damageWarningRepeatCarryWidthScale = 0;
        }
        damageWarningElapsedSeconds = 0;
        damageWarningPulseStrength = Math.Min(
            1f,
            damageWarningPulseStrength <= 0 ? 0.65f : damageWarningPulseStrength + 0.35f
        );
        if (!AutoCloseOnDamageActive || damageWarningAutoCloseRequested) return;

        // Never close the dialog from inside the atlas render pass: OnGuiClosed
        // verifies that no world framebuffer is left bound, and mid-pass the
        // engine still has one. The close runs at the start of the next frame.
        QueueDamageWarningEmergencyClose();
    }

    private void QueueDamageWarningEmergencyClose()
    {
        if (damageWarningAutoCloseRequested) return;
        damageWarningAutoCloseRequested = true;
        damageWarningEmergencyClosePending = true;
    }

    /// <summary>
    /// Test hook. It feeds the warning state machine directly, so the automated
    /// smoke test never has to hurt the player or write to the save.
    /// </summary>
    internal void TriggerDamageWarningForAutomatedTest()
    {
        if (!DamageWarningPolicyActive) return;
        SignalDamageWarning();
    }

    /// <summary>
    /// Test-only render-time seek used to inspect a visible warning sample
    /// without waiting on a wall-clock callback. Production timing always
    /// advances from real render time in <see cref="AdvanceDamageWarning"/>.
    /// </summary>
    internal void SetDamageWarningAnimationTimeForAutomatedTest(
        float elapsedSeconds
    )
    {
        damageWarningElapsedSeconds = float.IsFinite(elapsedSeconds)
            ? Math.Clamp(elapsedSeconds, 0f, DamageWarningDurationSeconds)
            : 0f;
    }

    internal bool DamageWarningActiveForAutomatedTest =>
        DamageWarningVignetteAlpha() > 0;

    /// <summary>
    /// Requires a meaningful inhale before the real-damage smoke capture. A
    /// merely non-zero attack sample is intentionally not enough: the first
    /// rendered frames are almost transparent by design.
    /// </summary>
    internal bool DamageWarningMeaningfulInhaleForAutomatedTest =>
        DamageWarningVignetteAlpha() >= 0.14f
        && DamageWarningEdgeWidthScale() >= 0.45f;

    internal static bool DamageWarningMeaningfulInhaleAtForAutomatedTest(
        float elapsedSeconds,
        float pulseStrength
    ) => DamageWarningVignetteAlphaAt(elapsedSeconds, pulseStrength) >= 0.14f
        && DamageWarningEdgeWidthScaleAt(elapsedSeconds, pulseStrength) >= 0.45f;

    /// <summary>
    /// Returns the deterministic vignette curve at an arbitrary render-time
    /// sample. The smoke test uses this instead of assuming that a signal must
    /// already be visible on the exact frame it is raised.
    /// </summary>
    internal static float DamageWarningVignetteAlphaForAutomatedTest(
        float elapsedSeconds,
        float pulseStrength
    ) => DamageWarningVignetteAlphaAt(
        elapsedSeconds,
        pulseStrength
    );

    /// <summary>
    /// Returns the caption alpha from the same attack, breathing and release
    /// envelope as the edge vignette.
    /// </summary>
    internal static float DamageWarningCaptionAlphaForAutomatedTest(
        float elapsedSeconds,
        float pulseStrength
    ) => DamageWarningCaptionAlpha(
        DamageWarningVignetteAlphaAt(
            elapsedSeconds,
            pulseStrength
        )
    );

    /// <summary>
    /// Returns the edge-width breathing curve independently of the GUI
    /// texture. The width starts at zero, reaches only the configured small
    /// maximum, and returns to zero with the same pulse as the colour.
    /// </summary>
    internal static float DamageWarningEdgeWidthScaleForAutomatedTest(
        float elapsedSeconds,
        float pulseStrength
    ) => DamageWarningEdgeWidthScaleAt(elapsedSeconds, pulseStrength);

    internal static bool DamageWarningEmergencyCloseRequiredForAutomatedTest(
        bool alive,
        float currentHealth
    ) => DamageWarningEmergencyCloseRequired(alive, currentHealth);

    /// <summary>
    /// Samples the restarted episode while carrying the previous visible
    /// alpha. This keeps repeat-hit continuity deterministic and independent
    /// of a live dialog instance.
    /// </summary>
    internal static float DamageWarningRepeatAlphaForAutomatedTest(
        float previousAlpha,
        float elapsedSeconds,
        float pulseStrength
    ) => DamageWarningApplyRepeatCarry(
        DamageWarningVignetteAlphaAt(
            elapsedSeconds,
            pulseStrength
        ),
        previousAlpha,
        elapsedSeconds
    );

    /// <summary>
    /// Deterministic self-check for the damage-warning animation. It keeps the
    /// first frame quiet, proves the attack rises into a slow inhale, proves a
    /// later exhale is softer than that inhale, and proves both layers release
    /// to zero at the preserved 3.2-second endpoint.
    /// </summary>
    internal static bool ValidateDamageWarningAnimationForAutomatedTest(
        out string diagnostic
    )
    {
        const float strength = 0.65f;
        float start = DamageWarningVignetteAlphaAt(0f, strength);
        float rising = DamageWarningVignetteAlphaAt(0.24f, strength);
        float inhale = DamageWarningVignetteAlphaAt(0.72f, strength);
        float exhale = DamageWarningVignetteAlphaAt(
            DamageWarningBreathPeriodSeconds * 0.75f,
            strength
        );
        float breathTrough = DamageWarningVignetteAlphaAt(
            DamageWarningBreathPeriodSeconds,
            strength
        );
        float secondBreathTrough = DamageWarningVignetteAlphaAt(
            DamageWarningBreathPeriodSeconds * 2f,
            strength
        );
        float end = DamageWarningVignetteAlphaAt(
            DamageWarningDurationSeconds,
            strength
        );
        float captionStart = DamageWarningCaptionAlphaForAutomatedTest(
            0f,
            strength
        );
        float captionRising = DamageWarningCaptionAlphaForAutomatedTest(
            0.24f,
            strength
        );
        float captionInhale = DamageWarningCaptionAlphaForAutomatedTest(
            0.72f,
            strength
        );
        float captionBreathTrough = DamageWarningCaptionAlphaForAutomatedTest(
            DamageWarningBreathPeriodSeconds,
            strength
        );
        float captionSecondBreathTrough = DamageWarningCaptionAlphaForAutomatedTest(
            DamageWarningBreathPeriodSeconds * 2f,
            strength
        );
        float captionEnd = DamageWarningCaptionAlphaForAutomatedTest(
            DamageWarningDurationSeconds,
            strength
        );
        float edgeStart = DamageWarningEdgeWidthScaleAt(0f, strength);
        float edgeRising = DamageWarningEdgeWidthScaleAt(0.24f, strength);
        float edgeInhale = DamageWarningEdgeWidthScaleAt(0.72f, strength);
        float edgeTrough = DamageWarningEdgeWidthScaleAt(
            DamageWarningBreathPeriodSeconds * 0.5f,
            strength
        );
        float edgeBreathTrough = DamageWarningEdgeWidthScaleAt(
            DamageWarningBreathPeriodSeconds,
            strength
        );
        float edgeSecondBreathTrough = DamageWarningEdgeWidthScaleAt(
            DamageWarningBreathPeriodSeconds * 2f,
            strength
        );
        float edgeEnd = DamageWarningEdgeWidthScaleAt(
            DamageWarningDurationSeconds,
            strength
        );
        bool meaningfulInhale =
            DamageWarningMeaningfulInhaleAtForAutomatedTest(0.72f, strength);
        float repeatAtSignal = DamageWarningRepeatAlphaForAutomatedTest(
            inhale,
            0f,
            1f
        );
        float repeatAtNextFrame = DamageWarningRepeatAlphaForAutomatedTest(
            inhale,
            0.016f,
            1f
        );

        if (start > 0.001f
            || captionStart > 0.001f
            || edgeStart > 0.001f)
        {
            diagnostic = FormattableString.Invariant(
                $"the damage signal must start quiet, got vignette={start:0.000}, caption={captionStart:0.000}"
            );
            return false;
        }
        if (!(rising > 0.01f
            && inhale > rising
            && captionRising > 0.01f
            && captionInhale > captionRising
            && edgeRising > 0.01f
            && edgeInhale > edgeRising
            && edgeTrough > 0.01f
            && breathTrough <= 0.001f
            && secondBreathTrough <= 0.001f
            && captionBreathTrough <= 0.001f
            && captionSecondBreathTrough <= 0.001f
            && edgeBreathTrough <= 0.001f
            && edgeSecondBreathTrough <= 0.001f))
        {
            diagnostic = FormattableString.Invariant(
                $"the damage curve must rise smoothly into its inhale and return to zero between breaths, got vignette {rising:0.000} -> {inhale:0.000}, trough={breathTrough:0.000}, caption={captionRising:0.000}"
            );
            return false;
        }
        if (!(exhale > end + 0.01f && exhale < inhale))
        {
            diagnostic = FormattableString.Invariant(
                $"the breathing exhale must be softer than inhale and precede release, got inhale={inhale:0.000}, exhale={exhale:0.000}, end={end:0.000}"
            );
            return false;
        }
        if (end > 0.001f
            || captionEnd > 0.001f
            || edgeEnd > 0.001f)
        {
            diagnostic = FormattableString.Invariant(
                $"the damage curve must release at {DamageWarningDurationSeconds:0.0}s, got vignette={end:0.000}, caption={captionEnd:0.000}"
            );
            return false;
        }
        if (!meaningfulInhale)
        {
            diagnostic = FormattableString.Invariant(
                $"the inhale sample must be meaningful for smoke capture, got vignette={inhale:0.000}, edge={edgeInhale:0.000}"
            );
            return false;
        }
        if (repeatAtSignal + 0.001f < inhale
            || repeatAtNextFrame + 0.02f < inhale)
        {
            diagnostic = FormattableString.Invariant(
                $"a repeat hit must not step the visible alpha down, got before={inhale:0.000}, signal={repeatAtSignal:0.000}, nextFrame={repeatAtNextFrame:0.000}"
            );
            return false;
        }

        diagnostic = FormattableString.Invariant(
            $"start={start:0.000}, rising={rising:0.000}, inhale={inhale:0.000}, exhale={exhale:0.000}, trough={breathTrough:0.000}, secondTrough={secondBreathTrough:0.000}, end={end:0.000}, edgeStart={edgeStart:0.000}, edgeInhale={edgeInhale:0.000}, edgeTrough={edgeTrough:0.000}, edgeBreathTrough={edgeBreathTrough:0.000}, edgeSecondTrough={edgeSecondBreathTrough:0.000}, edgeEnd={edgeEnd:0.000}, captionStart={captionStart:0.000}, captionEnd={captionEnd:0.000}"
        );
        return true;
    }

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
                && EnsureDamageEdgeTextures()
                && EnsureDamageWarningCaption("TAKING DAMAGE — CLOSE THE ATLAS")
                && damageEdgeTopTexture?.TextureId > 0
                && damageEdgeBottomTexture?.TextureId > 0
                && damageEdgeLeftTexture?.TextureId > 0
                && damageEdgeRightTexture?.TextureId > 0
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
        float alpha = DamageWarningVignetteAlphaAt(
            damageWarningElapsedSeconds,
            damageWarningPulseStrength
        );
        return DamageWarningApplyRepeatCarry(
            alpha,
            damageWarningRepeatCarryAlpha,
            damageWarningElapsedSeconds
        );
    }

    private float DamageWarningEdgeWidthScale()
    {
        float widthScale = DamageWarningEdgeWidthScaleAt(
            damageWarningElapsedSeconds,
            damageWarningPulseStrength
        );
        return DamageWarningApplyRepeatCarry(
            widthScale,
            damageWarningRepeatCarryWidthScale,
            damageWarningElapsedSeconds
        );
    }

    private static float DamageWarningApplyRepeatCarry(
        float alpha,
        float carryAlpha,
        float elapsedSeconds
    )
    {
        if (carryAlpha <= 0f
            || elapsedSeconds >= DamageWarningRepeatCarryFadeSeconds)
        {
            return alpha;
        }

        float carry = carryAlpha * (
            1f - DamageWarningSmoothStep(
                elapsedSeconds / DamageWarningRepeatCarryFadeSeconds
            )
        );
        return Math.Max(alpha, carry);
    }

    /// <summary>
    /// Uses a zero-slope smooth attack and release around a slow breathing
    /// pulse. Keeping this pure makes the first-frame and endpoint guarantees
    /// testable without touching a live dialog or framebuffer.
    /// </summary>
    private static float DamageWarningVignetteAlphaAt(
        float elapsedSeconds,
        float pulseStrength
    )
    {
        float strength = float.IsFinite(pulseStrength)
            ? Math.Clamp(pulseStrength, 0f, 1f)
            : 0f;
        if (strength <= 0f) return 0f;

        float envelope = DamageWarningPulseEnvelope(elapsedSeconds);
        float breath = DamageWarningBreathAlpha(elapsedSeconds);
        return Math.Clamp(
            strength * envelope * breath * 0.52f,
            0f,
            DamageWarningMaximumPulseAlpha
        );
    }

    private static float DamageWarningCaptionAlpha(float vignetteAlpha) =>
        Math.Clamp(vignetteAlpha * 1.45f, 0f, 0.90f);

    private static float DamageWarningEdgeWidthScaleAt(
        float elapsedSeconds,
        float pulseStrength
    )
    {
        float strength = float.IsFinite(pulseStrength)
            ? Math.Clamp(pulseStrength, 0f, 1f)
            : 0f;
        if (strength <= 0f) return 0f;
        return Math.Clamp(
            strength
                * DamageWarningPulseEnvelope(elapsedSeconds)
                * DamageWarningBreathWidthScale(elapsedSeconds),
            0f,
            1f
        );
    }

    private static int DamageWarningEdgeThickness(
        int width,
        int height,
        float edgeWidthScale
    )
    {
        if (edgeWidthScale <= 0f || width <= 0 || height <= 0) return 0;
        double maximum = Math.Min(width, height)
            * DamageWarningMaximumEdgeFraction;
        return Math.Clamp(
            (int)Math.Ceiling(maximum * edgeWidthScale),
            1,
            DamageWarningMaximumEdgePixels
        );
    }

    private static float DamageWarningPulseEnvelope(float elapsedSeconds)
    {
        if (!float.IsFinite(elapsedSeconds)
            || elapsedSeconds <= 0f
            || elapsedSeconds >= DamageWarningDurationSeconds)
        {
            return 0f;
        }

        float attack = DamageWarningSmoothStep(
            elapsedSeconds / DamageWarningFadeInSeconds
        );
        float release = DamageWarningSmoothStep(
            (DamageWarningDurationSeconds - elapsedSeconds)
                / DamageWarningFadeOutSeconds
        );
        return attack * release;
    }

    private static float DamageWarningBreathAlpha(float elapsedSeconds)
    {
        if (!float.IsFinite(elapsedSeconds)) return 0f;
        float phase = elapsedSeconds * MathF.PI * 2f
            / DamageWarningBreathPeriodSeconds
            - MathF.PI * 0.5f;
        float inhale = 0.5f + 0.5f * MathF.Sin(phase);
        // The whole warning reaches zero between breaths, so the caption and
        // the edge width share the same transparent trough.
        return inhale;
    }

    private static float DamageWarningBreathWidthScale(float elapsedSeconds)
    {
        if (!float.IsFinite(elapsedSeconds)) return 0f;
        float phase = elapsedSeconds * MathF.PI * 2f
            / DamageWarningBreathPeriodSeconds
            - MathF.PI * 0.5f;
        // The edge width disappears between breaths with the same true-zero
        // trough as the colour and caption: 0 -> small maximum -> 0.
        return Math.Clamp(0.5f + 0.5f * MathF.Sin(phase), 0f, 1f);
    }

    private static float DamageWarningSmoothStep(float value)
    {
        if (!float.IsFinite(value)) return value > 0f ? 1f : 0f;
        float clamped = Math.Clamp(value, 0f, 1f);
        return clamped * clamped * (3f - 2f * clamped);
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
        float edgeWidthScale = DamageWarningEdgeWidthScale();
        int edgeWidth = DamageWarningEdgeThickness(
            viewport.Width,
            viewport.Height,
            edgeWidthScale
        );
        if (edgeWidth > 0 && EnsureDamageEdgeTextures())
        {
            RenderDamageEdges(viewport, edgeWidth, alpha);
        }

        const string caption = "TAKING DAMAGE — CLOSE THE ATLAS";
        if (!EnsureDamageWarningCaption(caption)) return;

        float captionWidth = damageWarningCaptionTexture!.Width;
        float captionHeight = damageWarningCaptionTexture.Height;
        float captionAlpha = DamageWarningCaptionAlpha(alpha);
        if (captionAlpha <= 0.002f) return;
        capi.Render.Render2DTexture(
            damageWarningCaptionTexture.TextureId,
            viewport.X + (viewport.Width - captionWidth) * 0.5f,
            viewport.Y + 18,
            captionWidth,
            captionHeight,
            61,
            new Vec4f(1f, 1f, 1f, captionAlpha)
        );
    }

    private bool EnsureDamageEdgeTextures()
    {
        if (damageEdgeTopTexture?.TextureId > 0
            && damageEdgeBottomTexture?.TextureId > 0
            && damageEdgeLeftTexture?.TextureId > 0
            && damageEdgeRightTexture?.TextureId > 0)
        {
            return true;
        }

        try
        {
            bool top = BuildDamageEdgeTexture(
                vertical: true,
                reversed: false,
                ref damageEdgeTopTexture
            );
            bool bottom = BuildDamageEdgeTexture(
                vertical: true,
                reversed: true,
                ref damageEdgeBottomTexture
            );
            bool left = BuildDamageEdgeTexture(
                vertical: false,
                reversed: false,
                ref damageEdgeLeftTexture
            );
            bool right = BuildDamageEdgeTexture(
                vertical: false,
                reversed: true,
                ref damageEdgeRightTexture
            );
            return top && bottom && left && right;
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "[ModernAtlas] Could not build the damage warning edge textures: {0}",
                exception.Message
            );
            return false;
        }
    }

    private bool BuildDamageEdgeTexture(
        bool vertical,
        bool reversed,
        ref LoadedTexture? texture
    )
    {
        if (texture?.TextureId > 0) return true;

        int width = vertical ? 1 : DamageWarningEdgeGradientPixels;
        int height = vertical ? DamageWarningEdgeGradientPixels : 1;
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        AtlasUiStyle.Clear(context);

        double startX = vertical ? 0 : (reversed ? width : 0);
        double startY = vertical ? (reversed ? height : 0) : 0;
        double endX = vertical ? 0 : (reversed ? 0 : width);
        double endY = vertical ? (reversed ? 0 : height) : 0;
        using LinearGradient gradient = new(startX, startY, endX, endY);
        gradient.AddColorStop(0, new Color(0.40, 0.02, 0.02, 0.70));
        gradient.AddColorStop(0.45, new Color(0.40, 0.02, 0.02, 0.18));
        gradient.AddColorStop(1, new Color(0.40, 0.02, 0.02, 0));
        context.SetSource(gradient);
        context.Rectangle(0, 0, width, height);
        context.Fill();

        texture ??= new LoadedTexture(capi);
        capi.Gui.LoadOrUpdateCairoTexture(
            surface,
            true,
            ref texture
        );
        return texture?.TextureId > 0;
    }

    private void RenderDamageEdges(
        AtlasViewportBounds viewport,
        int edgeWidth,
        float alpha
    )
    {
        Vec4f tint = new(1f, 1f, 1f, alpha);
        capi.Render.Render2DTexture(
            damageEdgeTopTexture!.TextureId,
            viewport.X,
            viewport.Y,
            viewport.Width,
            edgeWidth,
            60,
            tint
        );
        capi.Render.Render2DTexture(
            damageEdgeBottomTexture!.TextureId,
            viewport.X,
            viewport.Y + viewport.Height - edgeWidth,
            viewport.Width,
            edgeWidth,
            60,
            tint
        );
        capi.Render.Render2DTexture(
            damageEdgeLeftTexture!.TextureId,
            viewport.X,
            viewport.Y,
            edgeWidth,
            viewport.Height,
            60,
            tint
        );
        capi.Render.Render2DTexture(
            damageEdgeRightTexture!.TextureId,
            viewport.X + viewport.Width - edgeWidth,
            viewport.Y,
            edgeWidth,
            viewport.Height,
            60,
            tint
        );
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
            int width = (int)Math.Ceiling(caption.Length * 12 * scale + 8 * scale);
            int height = (int)Math.Ceiling(30 * scale);
            using ImageSurface surface = new(Format.Argb32, width, height);
            using Context context = new(surface);
            AtlasUiStyle.Clear(context);
            // Keep the caption texture transparent everywhere except for the
            // glyphs. A subtle offset shadow preserves readability over the
            // live atlas without creating the old opaque raised-panel strip.
            using CairoFont shadow = CairoFont.WhiteDetailText()
                .WithFontSize((float)(13 * scale))
                .WithWeight(FontWeight.Bold)
                .WithColor(new[] { 0.05, 0.0, 0.0, 0.72 });
            AtlasUiStyle.DrawCenteredText(
                context,
                shadow,
                caption,
                width,
                height,
                1.2 * scale,
                1.2 * scale
            );
            using CairoFont font = CairoFont.WhiteDetailText()
                .WithFontSize((float)(13 * scale))
                .WithWeight(FontWeight.Bold)
                .WithColor(new[] { 1.0, 0.78, 0.78, 1.0 });
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
        damageEdgeTopTexture?.Dispose();
        damageEdgeTopTexture = null;
        damageEdgeBottomTexture?.Dispose();
        damageEdgeBottomTexture = null;
        damageEdgeLeftTexture?.Dispose();
        damageEdgeLeftTexture = null;
        damageEdgeRightTexture?.Dispose();
        damageEdgeRightTexture = null;
        damageWarningCaptionTexture?.Dispose();
        damageWarningCaptionTexture = null;
        damageWarningCaptionValue = null;
    }
}
