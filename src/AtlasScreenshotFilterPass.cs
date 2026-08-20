using System;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

internal enum AtlasScreenshotFilterMode
{
    Unfiltered,
    Filtered
}

internal readonly record struct AtlasScreenshotFilterReadbackDiagnostics(
    int Width,
    int Height,
    long ChangedPixelCount,
    long TotalPixelCount,
    int MaximumChannelDelta
)
{
    public double ChangedRatio => TotalPixelCount <= 0
        ? 0d
        : (double)ChangedPixelCount / TotalPixelCount;

    public bool IsExactRgb => ChangedPixelCount == 0;

    public override string ToString() =>
        $"dimensions={Width}x{Height}, changedPixels={ChangedPixelCount}/{TotalPixelCount}, changedRatio={ChangedRatio:0.####}, maxChannelDelta={MaximumChannelDelta}";
}

/// <summary>
/// The immutable visual contract captured when one tiled screenshot starts.
/// It intentionally contains no engine objects or mutable configuration
/// references, so every tile in a job receives the same grading values.
/// </summary>
internal readonly record struct AtlasScreenshotFilterSettings
{
    public bool Enabled { get; }
    public string Preset { get; }
    public int FilterIntensityPercent { get; }
    public int SaturationPercent { get; }
    public int ContrastPercent { get; }
    public int TemperaturePercent { get; }
    public int ShadowTintStrengthPercent { get; }
    public int AmbientOcclusionPercent { get; }
    public int IndirectLightPercent { get; }
    public int BloomPercent { get; }
    public float Exposure { get; }
    public float ShadowTintR { get; }
    public float ShadowTintG { get; }
    public float ShadowTintB { get; }
    public float HighlightTintR { get; }
    public float HighlightTintG { get; }
    public float HighlightTintB { get; }
    public float RedBalance { get; }
    public float GreenBalance { get; }
    public float BlueBalance { get; }

    internal static readonly string[] PresetValues =
    {
        "off",
        "natural",
        "atlas-relief",
        "cinematic",
        "old-photo",
        "western",
        "custom"
    };

    internal static readonly string[] PresetNames =
    {
        "Off",
        "Natural",
        "Atlas Relief",
        "Cinematic",
        "Old Photo",
        "Western",
        "Custom"
    };

    public AtlasScreenshotFilterSettings(
        bool enabled,
        string preset,
        int filterIntensityPercent,
        int saturationPercent,
        int contrastPercent,
        int temperaturePercent,
        int shadowTintStrengthPercent,
        int ambientOcclusionPercent,
        int indirectLightPercent,
        int bloomPercent,
        float exposure,
        float shadowTintR,
        float shadowTintG,
        float shadowTintB,
        float highlightTintR,
        float highlightTintG,
        float highlightTintB,
        float redBalance = 1f,
        float greenBalance = 1f,
        float blueBalance = 1f
    )
    {
        Enabled = enabled;
        Preset = NormalizePreset(preset);
        FilterIntensityPercent = ClampIntensity(filterIntensityPercent);
        SaturationPercent = ClampPercent(saturationPercent);
        ContrastPercent = ClampPercent(contrastPercent);
        TemperaturePercent = Math.Clamp(temperaturePercent, -100, 100);
        ShadowTintStrengthPercent = ClampStrength(shadowTintStrengthPercent);
        AmbientOcclusionPercent = ClampStrength(ambientOcclusionPercent);
        IndirectLightPercent = ClampStrength(indirectLightPercent);
        BloomPercent = ClampStrength(bloomPercent);
        Exposure = Math.Clamp(exposure, -0.35f, 0.35f);
        ShadowTintR = Math.Clamp(shadowTintR, 0.5f, 1.5f);
        ShadowTintG = Math.Clamp(shadowTintG, 0.5f, 1.5f);
        ShadowTintB = Math.Clamp(shadowTintB, 0.5f, 1.5f);
        HighlightTintR = Math.Clamp(highlightTintR, 0.5f, 1.5f);
        HighlightTintG = Math.Clamp(highlightTintG, 0.5f, 1.5f);
        HighlightTintB = Math.Clamp(highlightTintB, 0.5f, 1.5f);
        RedBalance = Math.Clamp(redBalance, 0.5f, 1.5f);
        GreenBalance = Math.Clamp(greenBalance, 0.5f, 1.5f);
        BlueBalance = Math.Clamp(blueBalance, 0.5f, 1.5f);
    }

    internal static AtlasScreenshotFilterSettings FromConfig(
        ModernAtlasConfig config
    )
    {
        string preset = NormalizePreset(config.ScreenshotFilterPreset);
        if (!config.ScreenshotFiltersEnabled || preset == "off")
        {
            return CreatePreset("off");
        }

        if (preset != "custom") return CreatePreset(preset);

        int temperature = Math.Clamp(
            config.ScreenshotTemperaturePercent,
            -100,
            100
        );
        GetTemperatureTints(
            temperature,
            out float shadowR,
            out float shadowG,
            out float shadowB,
            out float highlightR,
            out float highlightG,
            out float highlightB
        );
        shadowR += (config.ScreenshotShadowRedPercent - 100) / 100f;
        shadowG += (config.ScreenshotShadowGreenPercent - 100) / 100f;
        shadowB += (config.ScreenshotShadowBluePercent - 100) / 100f;
        highlightR += (config.ScreenshotHighlightRedPercent - 100) / 100f;
        highlightG += (config.ScreenshotHighlightGreenPercent - 100) / 100f;
        highlightB += (config.ScreenshotHighlightBluePercent - 100) / 100f;
        return new AtlasScreenshotFilterSettings(
            true,
            "custom",
            config.ScreenshotFilterIntensityPercent,
            config.ScreenshotSaturationPercent,
            config.ScreenshotContrastPercent,
            temperature,
            config.ScreenshotShadowTintStrengthPercent,
            config.ScreenshotAmbientOcclusionPercent,
            config.ScreenshotIndirectLightPercent,
            config.ScreenshotBloomPercent,
            0f,
            shadowR,
            shadowG,
            shadowB,
            highlightR,
            highlightG,
            highlightB,
            config.ScreenshotRedBalancePercent / 100f,
            config.ScreenshotGreenBalancePercent / 100f,
            config.ScreenshotBlueBalancePercent / 100f
        );
    }

    internal AtlasScreenshotFilterSettings WithoutSpatialEffects() =>
        new(
            Enabled,
            Preset,
            FilterIntensityPercent,
            SaturationPercent,
            ContrastPercent,
            TemperaturePercent,
            ShadowTintStrengthPercent,
            0,
            0,
            0,
            Exposure,
            ShadowTintR,
            ShadowTintG,
            ShadowTintB,
            HighlightTintR,
            HighlightTintG,
            HighlightTintB,
            RedBalance,
            GreenBalance,
            BlueBalance
        );

    internal AtlasScreenshotFilterSettings WithoutDepthEffects() =>
        new(
            Enabled,
            Preset,
            FilterIntensityPercent,
            SaturationPercent,
            ContrastPercent,
            TemperaturePercent,
            ShadowTintStrengthPercent,
            0,
            0,
            BloomPercent,
            Exposure,
            ShadowTintR,
            ShadowTintG,
            ShadowTintB,
            HighlightTintR,
            HighlightTintG,
            HighlightTintB,
            RedBalance,
            GreenBalance,
            BlueBalance
        );

    internal static void ApplyPresetToConfig(
        ModernAtlasConfig config,
        string requestedPreset
    )
    {
        string preset = NormalizePreset(requestedPreset);
        AtlasScreenshotFilterSettings settings = CreatePreset(preset);
        config.ScreenshotFilterPreset = preset;
        config.ScreenshotFiltersEnabled = settings.Enabled;
        if (preset == "off" || preset == "custom")
        {
            // Off changes only the capture decision, and Custom changes only
            // the editing mode. Keep the last numeric values so toggling Off
            // and returning to Custom is reversible.
            return;
        }
        config.ScreenshotFilterIntensityPercent = settings.FilterIntensityPercent;
        config.ScreenshotSaturationPercent = settings.SaturationPercent;
        config.ScreenshotContrastPercent = settings.ContrastPercent;
        config.ScreenshotTemperaturePercent = settings.TemperaturePercent;
        config.ScreenshotShadowTintStrengthPercent =
            settings.ShadowTintStrengthPercent;
        config.ScreenshotAmbientOcclusionPercent =
            settings.AmbientOcclusionPercent;
        config.ScreenshotIndirectLightPercent = settings.IndirectLightPercent;
        config.ScreenshotBloomPercent = settings.BloomPercent;
        config.ScreenshotRedBalancePercent = 100;
        config.ScreenshotGreenBalancePercent = 100;
        config.ScreenshotBlueBalancePercent = 100;
        GetTemperatureTints(
            settings.TemperaturePercent,
            out float temperatureShadowR,
            out float temperatureShadowG,
            out float temperatureShadowB,
            out float temperatureHighlightR,
            out float temperatureHighlightG,
            out float temperatureHighlightB
        );
        config.ScreenshotShadowRedPercent = TintOffsetPercent(
            settings.ShadowTintR,
            temperatureShadowR
        );
        config.ScreenshotShadowGreenPercent = TintOffsetPercent(
            settings.ShadowTintG,
            temperatureShadowG
        );
        config.ScreenshotShadowBluePercent = TintOffsetPercent(
            settings.ShadowTintB,
            temperatureShadowB
        );
        config.ScreenshotHighlightRedPercent = TintOffsetPercent(
            settings.HighlightTintR,
            temperatureHighlightR
        );
        config.ScreenshotHighlightGreenPercent = TintOffsetPercent(
            settings.HighlightTintG,
            temperatureHighlightG
        );
        config.ScreenshotHighlightBluePercent = TintOffsetPercent(
            settings.HighlightTintB,
            temperatureHighlightB
        );
    }

    private static int TintOffsetPercent(float tint, float temperatureTint) =>
        Math.Clamp((int)Math.Round(100f + (tint - temperatureTint) * 100f), 50, 150);

    internal static AtlasScreenshotFilterSettings CreatePreset(string value)
    {
        return NormalizePreset(value) switch
        {
            "natural" => new AtlasScreenshotFilterSettings(
                true,
                "natural",
                100,
                104,
                106,
                0,
                5,
                8,
                4,
                0,
                0.018f,
                0.97f,
                0.99f,
                1.02f,
                1.01f,
                1.01f,
                0.99f
            ),
            "atlas-relief" => new AtlasScreenshotFilterSettings(
                true,
                "atlas-relief",
                100,
                108,
                116,
                -5,
                16,
                18,
                12,
                5,
                0.038f,
                0.90f,
                0.96f,
                1.08f,
                1.05f,
                1.03f,
                0.96f
            ),
            "cinematic" => new AtlasScreenshotFilterSettings(
                true,
                "cinematic",
                100,
                96,
                124,
                8,
                28,
                24,
                16,
                12,
                0.045f,
                0.78f,
                0.88f,
                1.10f,
                1.10f,
                0.97f,
                0.84f
            ),
            "old-photo" => new AtlasScreenshotFilterSettings(
                true,
                "old-photo",
                100,
                52,
                108,
                32,
                22,
                8,
                3,
                2,
                -0.015f,
                1.12f,
                0.96f,
                0.76f,
                1.10f,
                1.02f,
                0.86f
            ),
            "western" => new AtlasScreenshotFilterSettings(
                true,
                "western",
                100,
                78,
                122,
                24,
                30,
                16,
                7,
                4,
                0.025f,
                0.96f,
                0.82f,
                0.62f,
                1.12f,
                0.98f,
                0.78f
            ),
            "custom" => new AtlasScreenshotFilterSettings(
                true,
                "custom",
                100,
                100,
                100,
                0,
                0,
                0,
                0,
                0,
                0f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f
            ),
            _ => new AtlasScreenshotFilterSettings(
                false,
                "off",
                100,
                100,
                100,
                0,
                0,
                0,
                0,
                0,
                0f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f
            )
        };
    }

    internal static string NormalizePreset(string? value)
    {
        string normalized = (value ?? "natural").Trim().ToLowerInvariant();
        return normalized switch
        {
            "atlas-relief"
                or "googleearth"
                or "google_earth"
                or "google earth"
                or "google-earth"
                or "atlasrelief"
                or "atlas_relief"
                or "atlas relief" => "atlas-relief",
            "off" or "none" or "disabled" => "off",
            "old-photo" or "oldphoto" or "old_photo" or "old photo" =>
                "old-photo",
            "western" or "western-sepia" or "western sepia" => "western",
            // Parchment was an early screenshot preset. Treat existing saved
            // selections as Natural now that it is no longer exposed.
            "parchment" => "natural",
            "natural" or "cinematic" or "custom" => normalized,
            _ => "natural"
        };
    }

    internal static int PresetIndex(ModernAtlasConfig config)
    {
        string preset = !config.ScreenshotFiltersEnabled
            ? "off"
            : NormalizePreset(config.ScreenshotFilterPreset);
        for (int index = 0; index < PresetValues.Length; index++)
        {
            if (PresetValues[index] == preset) return index;
        }
        return 0;
    }

    private static int ClampPercent(int value) => Math.Clamp(value, 0, 200);

    private static int ClampIntensity(int value) => Math.Clamp(value, 0, 200);

    private static int ClampStrength(int value) => Math.Clamp(value, 0, 100);

    private static void GetTemperatureTints(
        int temperature,
        out float shadowR,
        out float shadowG,
        out float shadowB,
        out float highlightR,
        out float highlightG,
        out float highlightB
    )
    {
        float amount = Math.Clamp(temperature / 100f, -1f, 1f);
        shadowR = 1f + Math.Max(0f, amount) * 0.10f;
        shadowG = 1f - Math.Abs(amount) * 0.025f;
        shadowB = 1f + Math.Max(0f, -amount) * 0.10f;
        highlightR = 1f + Math.Max(0f, amount) * 0.08f;
        highlightG = 1f;
        highlightB = 1f + Math.Max(0f, -amount) * 0.08f;
    }
}

/// <summary>
/// Screenshot-only post-process. It owns one full-resolution grading target
/// and two half-resolution bloom targets; no target is ever sized from the
/// stitched PNG.
/// </summary>
internal sealed class AtlasScreenshotFilterPass : IDisposable
{
    private readonly ICoreClientAPI capi;
    private readonly Func<IShaderProgram?> shaderProvider;
    private MeshRef? quad;
    private FrameBufferRef? framebufferA;
    private FrameBufferRef? framebufferB;
    private FrameBufferRef? framebufferC;
    private bool disposed;
    private bool loggedSuccess;
    private bool loggedFailure;

    public string? LastError { get; private set; }

    /// <summary>
    /// The most recently published filtered framebuffer.  It is exposed only
    /// for the automated same-source RGB diagnostic; the capture pipeline
    /// still treats the returned framebuffer as an immutable per-frame output.
    /// </summary>
    public FrameBufferRef? LastOutputFramebuffer => framebufferA;

    public bool UsesSpatialEffects(AtlasScreenshotFilterSettings settings) =>
        settings.Enabled
        && settings.FilterIntensityPercent > 0
        && (settings.AmbientOcclusionPercent > 0
            || settings.IndirectLightPercent > 0
            || settings.BloomPercent > 0);

    public bool RequiresValidityMask(AtlasScreenshotFilterSettings settings) =>
        settings.Enabled
        && settings.FilterIntensityPercent > 0
        && UsesSpatialEffects(settings);

    public bool RequiresDepthTexture(AtlasScreenshotFilterSettings settings) =>
        settings.Enabled
        && settings.FilterIntensityPercent > 0
        && (settings.AmbientOcclusionPercent > 0
            || settings.IndirectLightPercent > 0);

    public AtlasScreenshotFilterPass(
        ICoreClientAPI capi,
        Func<IShaderProgram?> shaderProvider
    )
    {
        this.capi = capi;
        this.shaderProvider = shaderProvider;
    }

    /// <summary>
    /// Performs all checks that can fail before tile zero is written. The
    /// preflight never silently changes a filtered job into an unfiltered one.
    /// Resource creation is nevertheless wrapped in a render-state scope:
    /// public framebuffer/mesh APIs are allowed to bind or otherwise alter GL
    /// state internally, especially when a window resize recreates targets.
    /// </summary>
    public bool Preflight(
        FrameBufferRef? source,
        int sourceDepthTextureId,
        int validityMaskTextureId,
        AtlasScreenshotFilterSettings settings,
        out string diagnostic
    )
    {
        if (!settings.Enabled)
        {
            diagnostic = "the frozen screenshot filter mode is Off";
            return true;
        }
        if (disposed)
        {
            diagnostic = "the screenshot filter pass is disposed";
            return false;
        }
        if (source == null
            || source.Disposed
            || source.ColorTextureIds is not { Length: > 0 }
            || source.ColorTextureIds[0] <= 0)
        {
            diagnostic = "the resolved atlas color framebuffer is unavailable";
            return false;
        }
        if (RequiresDepthTexture(settings) && sourceDepthTextureId <= 0)
        {
            diagnostic = "the frozen preset requires a depth texture";
            return false;
        }
        if (RequiresValidityMask(settings) && validityMaskTextureId <= 0)
        {
            diagnostic = "the frozen preset requires the disclosure validity mask for spatial effects";
            return false;
        }
        IShaderProgram? shader = shaderProvider();
        if (shader == null || shader.Disposed)
        {
            diagnostic = "the screenshot filter shader is unavailable";
            return false;
        }
        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        try
        {
            if (!EnsureResources(
                    source.Width,
                    source.Height,
                    bloom: settings.BloomPercent > 0
                        && settings.FilterIntensityPercent > 0
                ))
            {
                diagnostic = "the screenshot filter framebuffer could not be created";
                return false;
            }
        }
        catch (Exception exception)
        {
            diagnostic = $"the screenshot filter framebuffer preflight failed: {exception.Message}";
            return false;
        }
        finally
        {
            // Resource creation is a GUI-to-world boundary operation. Restore
            // the captured program/target and make the subsequent owner see a
            // deterministic GUI handoff without activating a new shader.
            renderState.RestoreGuiHandoff();
            RestoreReadBufferForCurrentFramebuffer(render);
        }
        LastError = null;
        diagnostic = UsesSpatialEffects(settings)
            ? "filtered capture is ready with disclosure-masked spatial effects"
            : "filtered capture is ready with full-resolution color grading";
        return true;
    }

    public FrameBufferRef? Apply(
        FrameBufferRef source,
        int sourceDepthTextureId,
        int validityMaskTextureId,
        AtlasScreenshotFilterSettings settings,
        float[] projection,
        double[] view,
        int overlapMargin
    )
    {
        LastError = null;
        if (!settings.Enabled)
        {
            return null;
        }

        if (!Preflight(
                source,
                sourceDepthTextureId,
                validityMaskTextureId,
                settings,
                out string preflightDiagnostic
            )
            || framebufferA == null
            || quad == null)
        {
            LogFailure(preflightDiagnostic);
            return null;
        }

        IShaderProgram? shader = shaderProvider();
        if (shader == null || shader.Disposed)
        {
            LogFailure("the screenshot filter shader is unavailable");
            return null;
        }

        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        try
        {
            int sourceColorTextureId = source.ColorTextureIds[0];
            int radius = Math.Clamp(Math.Min(overlapMargin, 8), 1, 8);
            int bloomRadius = Math.Clamp(Math.Min(overlapMargin / 2, 4), 1, 4);
            float[] modelView = Array.ConvertAll(
                view,
                value => (float)value
            );
            float[] projectionView = Mat4f.Create();
            Mat4f.Multiply(projectionView, projection, modelView);
            float[] inverseProjectionView = Mat4f.Create();
            Mat4f.Invert(inverseProjectionView, projectionView);
            RenderPass(
                render,
                shader,
                framebufferA,
                sourceColorTextureId,
                source.Width,
                source.Height,
                sourceDepthTextureId,
                validityMaskTextureId,
                0,
                0,
                settings,
                inverseProjectionView,
                radius,
                validityMaskTextureId > 0,
                sourceDepthTextureId > 0
            );

            if (settings.BloomPercent > 0
                && settings.FilterIntensityPercent > 0)
            {
                // The two blur passes stay within the screenshot tile overlap
                // so bloom cannot bleed through a stitched edge.
                RenderPass(
                    render,
                    shader,
                    framebufferB!,
                    framebufferA.ColorTextureIds[0],
                    source.Width,
                    source.Height,
                    sourceDepthTextureId,
                    validityMaskTextureId,
                    1,
                    0,
                    settings,
                    inverseProjectionView,
                    bloomRadius,
                    validityMaskTextureId > 0,
                    sourceDepthTextureId > 0
                );
                RenderPass(
                    render,
                    shader,
                    framebufferC!,
                    framebufferB!.ColorTextureIds[0],
                    framebufferB.Width,
                    framebufferB.Height,
                    sourceDepthTextureId,
                    validityMaskTextureId,
                    2,
                    0,
                    settings,
                    inverseProjectionView,
                    bloomRadius,
                    validityMaskTextureId > 0,
                    sourceDepthTextureId > 0
                );
                RenderPass(
                    render,
                    shader,
                    framebufferA,
                    sourceColorTextureId,
                    source.Width,
                    source.Height,
                    sourceDepthTextureId,
                    validityMaskTextureId,
                    3,
                    framebufferC!.ColorTextureIds[0],
                    settings,
                    inverseProjectionView,
                    radius,
                    validityMaskTextureId > 0,
                    sourceDepthTextureId > 0
                );
                if (!loggedSuccess)
                {
                    loggedSuccess = true;
                    capi.Logger.Notification(
                        "[ModernAtlas] Screenshot-only GPU filter pass is active with boundary validity masking and bounded ping-pong bloom."
                    );
                }
                return framebufferA;
            }

            if (!loggedSuccess)
            {
                loggedSuccess = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Screenshot-only GPU color-grade pass is active with boundary validity masking."
                );
            }
            return framebufferA;
        }
        catch (Exception exception)
        {
            LogFailure($"the screenshot filter pass failed: {exception.Message}");
            return null;
        }
        finally
        {
            // The next owner is GUI/readback. Establish its explicit safe
            // handoff without activating any shader during world teardown.
            renderState.RestoreGuiHandoff();
        }
    }

    private void RenderPass(
        IRenderAPI render,
        IShaderProgram shader,
        FrameBufferRef target,
        int sourceColorTextureId,
        int sourceWidth,
        int sourceHeight,
        int sourceDepthTextureId,
        int validityMaskTextureId,
        int passMode,
        int bloomTextureId,
        AtlasScreenshotFilterSettings settings,
        float[] inverseProjectionView,
        int radius,
        bool validityTextureEnabled,
        bool depthTextureEnabled
    )
    {
        render.CurrentActiveShader?.Stop();
        render.CurrentFrameBuffer = target;
        render.GlViewport(0, 0, target.Width, target.Height);
        render.GlColorMask(true, true, true, true);
        render.GlScissorFlag(false);
        render.GLDisableDepthTest();
        render.GLDepthMask(false);
        render.GlDisableCullFace();
        render.GlToggleBlend(false, EnumBlendMode.Standard);

        shader.Use();
        shader.BindTexture2D("sourceColorTex", sourceColorTextureId, 0);
        if (sourceDepthTextureId > 0)
        {
            shader.BindTexture2D("sourceDepthTex", sourceDepthTextureId, 1);
        }
        if (validityMaskTextureId > 0)
        {
            shader.BindTexture2D("validityTex", validityMaskTextureId, 2);
        }
        if (bloomTextureId > 0)
        {
            shader.BindTexture2D("bloomTex", bloomTextureId, 3);
        }
        shader.Uniform("passMode", passMode);
        shader.Uniform("bloomTextureEnabled", bloomTextureId > 0 ? 1 : 0);
        shader.Uniform("validityTextureEnabled", validityTextureEnabled ? 1 : 0);
        shader.Uniform("depthTextureEnabled", depthTextureEnabled ? 1 : 0);
        shader.UniformMatrix("inverseProjectionView", inverseProjectionView);
        shader.Uniform("sourceSize", sourceWidth, sourceHeight);
        shader.Uniform("targetSize", target.Width, target.Height);
        shader.Uniform(
            "filterIntensity",
            settings.FilterIntensityPercent / 100f
        );
        shader.Uniform("exposure", settings.Exposure);
        shader.Uniform("contrast", settings.ContrastPercent / 100f);
        shader.Uniform("saturation", settings.SaturationPercent / 100f);
        shader.Uniform("temperature", settings.TemperaturePercent / 100f);
        shader.Uniform(
            "shadowTint",
            settings.ShadowTintR,
            settings.ShadowTintG,
            settings.ShadowTintB
        );
        shader.Uniform(
            "highlightTint",
            settings.HighlightTintR,
            settings.HighlightTintG,
            settings.HighlightTintB
        );
        shader.Uniform(
            "rgbBalance",
            settings.RedBalance,
            settings.GreenBalance,
            settings.BlueBalance
        );
        shader.Uniform(
            "shadowTintStrength",
            settings.ShadowTintStrengthPercent / 100f
        );
        shader.Uniform(
            "depthReliefStrength",
            settings.AmbientOcclusionPercent / 100f
        );
        shader.Uniform(
            "indirectLightStrength",
            settings.IndirectLightPercent / 100f
        );
        shader.Uniform("bloomStrength", settings.BloomPercent / 100f);
        shader.Uniform("blurRadius", radius);
        render.RenderMesh(quad!);
        shader.Stop();
    }

    /// <summary>
    /// Compares the filter output against the exact resolved source used for
    /// the same tile. This is an opt-in smoke diagnostic; normal screenshots
    /// never perform the additional CPU readbacks.
    /// </summary>
    public bool TryCompareReadback(
        FrameBufferRef source,
        FrameBufferRef filtered,
        out AtlasScreenshotFilterReadbackDiagnostics diagnostics,
        out string error
    )
    {
        diagnostics = default;
        error = "";
        if (source.Disposed || filtered.Disposed)
        {
            error = "the source or filtered framebuffer is disposed";
            return false;
        }
        if (source.Width <= 0
            || source.Height <= 0
            || source.Width != filtered.Width
            || source.Height != filtered.Height
            || source.ColorTextureIds is not { Length: > 0 }
            || filtered.ColorTextureIds is not { Length: > 0 }
            || source.ColorTextureIds[0] <= 0
            || filtered.ColorTextureIds[0] <= 0)
        {
            error = "source and filtered framebuffer dimensions or color attachments disagree";
            return false;
        }

        IRenderAPI render = capi.Render;
        AtlasRenderStateScope renderState = AtlasRenderStateScope.Capture(render);
        try
        {
            int byteCount = checked(source.Width * source.Height * 4);
            byte[] sourcePixels = new byte[byteCount];
            byte[] filteredPixels = new byte[byteCount];
            PrepareReadbackTarget(render, source);
            GL.ReadPixels(
                0,
                0,
                source.Width,
                source.Height,
                PixelFormat.Bgra,
                PixelType.UnsignedByte,
                sourcePixels
            );
            PrepareReadbackTarget(render, filtered);
            GL.ReadPixels(
                0,
                0,
                filtered.Width,
                filtered.Height,
                PixelFormat.Bgra,
                PixelType.UnsignedByte,
                filteredPixels
            );

            long changedPixelCount = 0;
            int maximumChannelDelta = 0;
            for (int offset = 0; offset < sourcePixels.Length; offset += 4)
            {
                int blueDelta = Math.Abs(
                    sourcePixels[offset] - filteredPixels[offset]
                );
                int greenDelta = Math.Abs(
                    sourcePixels[offset + 1] - filteredPixels[offset + 1]
                );
                int redDelta = Math.Abs(
                    sourcePixels[offset + 2] - filteredPixels[offset + 2]
                );
                int pixelDelta = Math.Max(redDelta, Math.Max(greenDelta, blueDelta));
                maximumChannelDelta = Math.Max(maximumChannelDelta, pixelDelta);
                if (pixelDelta > 0) changedPixelCount++;
            }
            diagnostics = new AtlasScreenshotFilterReadbackDiagnostics(
                source.Width,
                source.Height,
                changedPixelCount,
                (long)source.Width * source.Height,
                maximumChannelDelta
            );
            return true;
        }
        catch (Exception exception)
        {
            error = $"same-source filter readback failed: {exception.Message}";
            return false;
        }
        finally
        {
            renderState.RestoreCapturedState();
            RestoreReadBufferForCurrentFramebuffer(render);
        }
    }

    private static void PrepareReadbackTarget(
        IRenderAPI render,
        FrameBufferRef target
    )
    {
        render.CurrentActiveShader?.Stop();
        render.CurrentFrameBuffer = target;
        render.GlViewport(0, 0, target.Width, target.Height);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        GL.PixelStore(PixelStoreParameter.PackRowLength, 0);
        GL.PixelStore(PixelStoreParameter.PackSkipRows, 0);
        GL.PixelStore(PixelStoreParameter.PackSkipPixels, 0);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
    }

    private static void RestoreReadBufferForCurrentFramebuffer(IRenderAPI render)
    {
        try
        {
            GL.ReadBuffer(
                render.CurrentFrameBuffer == null
                    ? ReadBufferMode.Back
                    : ReadBufferMode.ColorAttachment0
            );
            GL.PixelStore(PixelStoreParameter.PackRowLength, 0);
            GL.PixelStore(PixelStoreParameter.PackSkipRows, 0);
            GL.PixelStore(PixelStoreParameter.PackSkipPixels, 0);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
        }
        catch
        {
            // The client may already be leaving the world.
        }
    }

    private bool EnsureResources(int width, int height, bool bloom)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        quad ??= capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        if (framebufferA is { Disposed: false }
            && framebufferA.Width == width
            && framebufferA.Height == height
            && framebufferA.ColorTextureIds is { Length: > 0 }
            && (!bloom
                || (framebufferB is { Disposed: false }
                    && framebufferC is { Disposed: false }
                    && framebufferB.Width == Math.Max(1, (width + 1) / 2)
                    && framebufferB.Height == Math.Max(1, (height + 1) / 2)
                    && framebufferC.Width == framebufferB.Width
                    && framebufferC.Height == framebufferB.Height
                    && framebufferB.ColorTextureIds is { Length: > 0 }
                    && framebufferC.ColorTextureIds is { Length: > 0 })))
        {
            return true;
        }

        DestroyFramebuffers();
        framebufferA = CreateFramebuffer("modernatlas-screenshot-filter-a", width, height);
        if (bloom)
        {
            int halfWidth = Math.Max(1, (width + 1) / 2);
            int halfHeight = Math.Max(1, (height + 1) / 2);
            framebufferB = CreateFramebuffer(
                "modernatlas-screenshot-filter-b-half",
                halfWidth,
                halfHeight
            );
            framebufferC = CreateFramebuffer(
                "modernatlas-screenshot-filter-c-half",
                halfWidth,
                halfHeight
            );
        }
        return framebufferA is { Disposed: false }
            && framebufferA.ColorTextureIds is { Length: > 0 }
            && framebufferA.ColorTextureIds[0] > 0
            && (!bloom
                || (framebufferB is { Disposed: false }
                    && framebufferC is { Disposed: false }
                    && framebufferB.ColorTextureIds is { Length: > 0 }
                    && framebufferC.ColorTextureIds is { Length: > 0 }
                    && framebufferB.ColorTextureIds[0] > 0
                    && framebufferC.ColorTextureIds[0] > 0));
    }

    private FrameBufferRef? CreateFramebuffer(string name, int width, int height)
    {
        RawTexture color = new()
        {
            Width = width,
            Height = height,
            MinFilter = EnumTextureFilter.Linear,
            MagFilter = EnumTextureFilter.Linear,
            WrapS = EnumTextureWrap.ClampToEdge,
            WrapT = EnumTextureWrap.ClampToEdge,
            PixelInternalFormat = EnumTextureInternalFormat.Rgba8,
            PixelFormat = EnumTexturePixelFormat.Rgba
        };
        FramebufferAttrs attributes = new(name, width, height)
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
        return capi.Render.CreateFrameBuffer(attributes);
    }

    private void DestroyFramebuffers()
    {
        if (framebufferA != null && !framebufferA.Disposed)
        {
            capi.Render.DestroyFrameBuffer(framebufferA);
        }
        if (framebufferB != null && !framebufferB.Disposed)
        {
            capi.Render.DestroyFrameBuffer(framebufferB);
        }
        if (framebufferC != null && !framebufferC.Disposed)
        {
            capi.Render.DestroyFrameBuffer(framebufferC);
        }
        framebufferA = null;
        framebufferB = null;
        framebufferC = null;
    }

    private void LogFailure(string message)
    {
        LastError = message;
        if (!loggedFailure)
        {
            loggedFailure = true;
            capi.Logger.Warning(
                "[ModernAtlas] Screenshot filter failure: {0}.",
                message
            );
        }
    }

    /// <summary>
    /// Releases world-session GPU targets without permanently disabling the
    /// reusable client dialog. This performs no shader activation and is safe
    /// to call before the next world is joined.
    /// </summary>
    public void ResetWorldResources()
    {
        if (disposed) return;
        DestroyFramebuffers();
        quad?.Dispose();
        quad = null;
        loggedSuccess = false;
        loggedFailure = false;
        LastError = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        DestroyFramebuffers();
        quad?.Dispose();
        quad = null;
    }
}
