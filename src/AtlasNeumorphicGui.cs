using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ModernAtlas;

internal enum AtlasButtonStyle
{
    Surface,
    Dark,
    Icon,
    Compact
}

internal static class AtlasUiStyle
{
    public static readonly double[] Ink = { 0.97, 0.98, 0.99, 1.0 };
    public static readonly double[] MutedInk = { 0.76, 0.80, 0.84, 1.0 };
    public static readonly double[] LightInk = { 1.0, 1.0, 1.0, 1.0 };

    public static CairoFont TitleFont(float size = 21) =>
        CairoFont.WhiteSmallishText()
            .WithFontSize(size)
            .WithWeight(FontWeight.Bold)
            .WithColor(Ink);

    public static CairoFont LabelFont(float size = 14) =>
        CairoFont.WhiteDetailText()
            .WithFontSize(size)
            .WithWeight(FontWeight.Bold)
            .WithColor(Ink);

    public static CairoFont DetailFont(float size = 13) =>
        CairoFont.WhiteDetailText().WithFontSize(size).WithColor(MutedInk);

    public static CairoFont InputFont(float size = 14) =>
        CairoFont.SmallTextInput().WithFontSize(size).WithColor(Ink);

    public static void DrawCard(Context context, ImageSurface surface, ElementBounds bounds)
    {
        DrawRaisedPanel(
            context,
            bounds.drawX,
            bounds.drawY,
            bounds.InnerWidth,
            bounds.InnerHeight,
            16
        );
    }

    public static void DrawOpaquePreviewCard(
        Context context,
        ImageSurface surface,
        ElementBounds bounds
    )
    {
        if (bounds.InnerWidth <= 0 || bounds.InnerHeight <= 0) return;

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double edge = Math.Max(1, scale);
        RoundedRectangle(
            context,
            bounds.drawX + 4 * scale,
            bounds.drawY + 6 * scale,
            Math.Max(1, bounds.InnerWidth - 6 * scale),
            Math.Max(1, bounds.InnerHeight - 6 * scale),
            16 * scale
        );
        context.SetSourceRGBA(0.005, 0.010, 0.014, 0.86);
        context.Fill();
        RoundedRectangle(
            context,
            bounds.drawX + edge,
            bounds.drawY + edge,
            Math.Max(1, bounds.InnerWidth - 2 * edge),
            Math.Max(1, bounds.InnerHeight - 2 * edge),
            Math.Max(2, 16 * scale - edge)
        );
        context.SetSourceRGBA(0.010, 0.018, 0.024, 0.96);
        context.FillPreserve();
        context.SetSourceRGBA(1, 1, 1, 0.20);
        context.LineWidth = edge;
        context.Stroke();
    }

    public static void DrawToolbarPanel(Context context, ImageSurface surface, ElementBounds bounds)
    {
        if (bounds.InnerWidth <= 0 || bounds.InnerHeight <= 0) return;

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        RoundedRectangle(
            context,
            bounds.drawX,
            bounds.drawY,
            bounds.InnerWidth,
            bounds.InnerHeight,
            10 * scale
        );
        // The toolbar is deliberately lighter than the content panel: it is
        // a navigation aid, not a second window over the atlas.
        context.SetSourceRGBA(0.015, 0.025, 0.032, 0.25);
        context.FillPreserve();
        context.SetSourceRGBA(1, 1, 1, 0.16);
        context.LineWidth = Math.Max(1, scale * 0.65);
        context.Stroke();
    }

    public static void DrawTooltip(
        Context context,
        ImageSurface surface,
        ElementBounds bounds,
        string text,
        double localY
    )
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double paddingX = 9 * scale;
        double paddingY = 5 * scale;
        using CairoFont font = DetailFont(12);
        font.SetupContext(context);
        TextExtents extents = font.GetTextExtents(text);
        double width = Math.Min(
            bounds.InnerWidth,
            Math.Max(70 * scale, extents.Width + paddingX * 2)
        );
        double height = Math.Max(24 * scale, extents.Height + paddingY * 2);
        double x = bounds.drawX;
        double y = Math.Clamp(
            bounds.drawY + localY - height * 0.5,
            bounds.drawY + 3 * scale,
            bounds.drawY + Math.Max(3 * scale, bounds.InnerHeight - height - 3 * scale)
        );
        RoundedRectangle(context, x, y, width, height, 7 * scale);
        context.SetSourceRGBA(0.015, 0.025, 0.032, 0.82);
        context.FillPreserve();
        context.SetSourceRGBA(1, 1, 1, 0.22);
        context.LineWidth = Math.Max(1, scale * 0.65);
        context.Stroke();
        context.SetSourceRGBA(Ink[0], Ink[1], Ink[2], 1);
        context.MoveTo(
            x + paddingX - extents.XBearing,
            y + (height - extents.Height) * 0.5 - extents.YBearing
        );
        context.ShowText(text);
    }

    public static void DrawInsetCard(Context context, ImageSurface surface, ElementBounds bounds)
    {
        DrawInsetPanel(
            context,
            bounds.drawX,
            bounds.drawY,
            bounds.InnerWidth,
            bounds.InnerHeight,
            13
        );
    }

    public static void DrawSeparator(Context context, ImageSurface surface, ElementBounds bounds)
    {
        double y = bounds.drawY + bounds.InnerHeight * 0.5;
        context.SetSourceRGBA(1, 1, 1, 0.22);
        context.LineWidth = Math.Max(1, RuntimeEnv.GUIScale * 0.7);
        context.MoveTo(bounds.drawX, y);
        context.LineTo(bounds.drawX + bounds.InnerWidth, y);
        context.Stroke();
    }

    public static void DrawRaisedPanel(
        Context context,
        double x,
        double y,
        double width,
        double height,
        double radius
    )
    {
        if (width <= 0 || height <= 0) return;

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double edge = Math.Max(1, scale);
        RoundedRectangle(
            context,
            x + 5 * scale,
            y + 7 * scale,
            width - 7 * scale,
            height - 7 * scale,
            radius * scale
        );
        context.SetSourceRGBA(0.01, 0.02, 0.025, 0.32);
        context.Fill();
        RoundedRectangle(
            context,
            x + edge,
            y + edge,
            width - 2 * edge,
            height - 2 * edge,
            Math.Max(2, radius * scale - edge)
        );
        context.SetSourceRGBA(0.025, 0.035, 0.045, 0.34);
        context.FillPreserve();
        context.SetSourceRGBA(1, 1, 1, 0.18);
        context.LineWidth = edge;
        context.Stroke();
    }

    public static void DrawInsetPanel(
        Context context,
        double x,
        double y,
        double width,
        double height,
        double radius
    )
    {
        if (width <= 0 || height <= 0) return;

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        RoundedRectangle(context, x, y, width, height, radius * scale);
        context.SetSourceRGBA(0.01, 0.02, 0.025, 0.42);
        context.Fill();
        RoundedRectangle(
            context,
            x + 2 * scale,
            y + 2 * scale,
            width - 4 * scale,
            height - 4 * scale,
            Math.Max(2, radius * scale - 2 * scale)
        );
        context.SetSourceRGBA(0.03, 0.045, 0.055, 0.36);
        context.FillPreserve();
        context.SetSourceRGBA(1, 1, 1, 0.18);
        context.LineWidth = Math.Max(1, scale * 0.75);
        context.Stroke();
    }

    public static void DrawRaisedControl(
        Context context,
        double width,
        double height,
        bool pressed,
        bool hovered,
        bool dark,
        double radius
    )
    {
        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double margin = 4 * scale;
        double offset = pressed ? 2 * scale : 0;
        double faceX = margin + offset;
        double faceY = margin + offset;
        double faceWidth = Math.Max(1, width - margin * 2);
        double faceHeight = Math.Max(1, height - margin * 2);
        double scaledRadius = Math.Min(faceHeight * 0.5, radius * scale);

        if (!pressed)
        {
            RoundedRectangle(
                context,
                faceX + 3 * scale,
                faceY + 4 * scale,
                faceWidth,
                faceHeight,
                scaledRadius
            );
            context.SetSourceRGBA(0.01, 0.02, 0.025, 0.36);
            context.Fill();
        }

        RoundedRectangle(context, faceX, faceY, faceWidth, faceHeight, scaledRadius);
        if (dark || pressed)
        {
            context.SetSourceRGBA(0.02, 0.03, 0.04, hovered ? 0.78 : 0.68);
        }
        else
        {
            context.SetSourceRGBA(0.025, 0.04, 0.05, hovered ? 0.48 : 0.30);
        }
        context.FillPreserve();
        context.SetSourceRGBA(1, 1, 1, hovered ? 0.34 : 0.20);
        context.LineWidth = Math.Max(1, scale * 0.7);
        context.Stroke();
    }

    public static void DrawCenteredText(
        Context context,
        CairoFont font,
        string text,
        double width,
        double height,
        double offsetX = 0,
        double offsetY = 0
    )
    {
        font.SetupContext(context);
        TextExtents extents = font.GetTextExtents(text);
        context.MoveTo(
            (width - extents.Width) * 0.5 - extents.XBearing + offsetX,
            (height - extents.Height) * 0.5 - extents.YBearing + offsetY
        );
        context.ShowText(text);
    }

    public static void Clear(Context context)
    {
        context.Operator = Operator.Source;
        context.SetSourceRGBA(0, 0, 0, 0);
        context.Paint();
        context.Operator = Operator.Over;
    }

    public static void RoundedRectangle(
        Context context,
        double x,
        double y,
        double width,
        double height,
        double radius
    )
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        radius = Math.Clamp(radius, 0, Math.Min(width, height) * 0.5);
        context.NewPath();
        context.Arc(x + width - radius, y + radius, radius, -Math.PI / 2, 0);
        context.Arc(x + width - radius, y + height - radius, radius, 0, Math.PI / 2);
        context.Arc(x + radius, y + height - radius, radius, Math.PI / 2, Math.PI);
        context.Arc(x + radius, y + radius, radius, Math.PI, Math.PI * 1.5);
        context.ClosePath();
    }
}

internal sealed class GuiElementAtlasButton : GuiElementControl
{
    private readonly string label;
    private readonly ActionConsumable onClick;
    private readonly AtlasButtonStyle style;
    private readonly LoadedTexture normalTexture;
    private readonly LoadedTexture hoverTexture;
    private readonly LoadedTexture pressedTexture;
    private readonly LoadedTexture activeTexture;
    private readonly LoadedTexture disabledTexture;
    private bool hovered;
    private bool pressed;
    private bool active;

    public GuiElementAtlasButton(
        ICoreClientAPI capi,
        string label,
        ActionConsumable onClick,
        ElementBounds bounds,
        AtlasButtonStyle style
    ) : base(capi, bounds)
    {
        this.label = label;
        this.onClick = onClick;
        this.style = style;
        normalTexture = new LoadedTexture(capi);
        hoverTexture = new LoadedTexture(capi);
        pressedTexture = new LoadedTexture(capi);
        activeTexture = new LoadedTexture(capi);
        disabledTexture = new LoadedTexture(capi);
        MouseOverCursor = "hand";
    }

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        ComposeTexture(normalTexture, false, false, true);
        ComposeTexture(hoverTexture, false, true, true);
        ComposeTexture(pressedTexture, true, true, true);
        ComposeTexture(activeTexture, false, true, true, true);
        ComposeTexture(disabledTexture, false, false, false);
    }

    public bool IsActive => active;

    public override void RenderInteractiveElements(float deltaTime)
    {
        LoadedTexture texture = !Enabled
            ? disabledTexture
            : pressed
                ? pressedTexture
                : active
                    ? activeTexture
                : hovered
                    ? hoverTexture
                    : normalTexture;
        if (texture.TextureId > 0)
        {
            Render2DTexture(texture.TextureId, Bounds, 50, ColorUtil.WhiteArgbVec);
        }
    }

    public override void OnMouseMove(ICoreClientAPI capi, MouseEvent args)
    {
        hovered = Bounds.PointInside(args.X, args.Y);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI capi, MouseEvent args)
    {
        if (!Enabled || args.Button != EnumMouseButton.Left) return;
        pressed = true;
        args.Handled = true;
    }

    public override void OnMouseUpOnElement(ICoreClientAPI capi, MouseEvent args)
    {
        if (!pressed || args.Button != EnumMouseButton.Left) return;
        pressed = false;
        if (Enabled) onClick();
        args.Handled = true;
    }

    public override void OnMouseUp(ICoreClientAPI capi, MouseEvent args)
    {
        // GuiElement.OnMouseUp dispatches OnMouseUpOnElement. Skipping the
        // base call leaves a visibly pressed atlas button without ever firing
        // its action.
        base.OnMouseUp(capi, args);
        if (args.Button == EnumMouseButton.Left) pressed = false;
    }

    /// <summary>
    /// Invokes the button from a parent dialog that owns a modal input layer.
    /// The preview is rendered after the atlas viewport and must retain mouse
    /// ownership even when the engine's GUI dispatcher has already marked the
    /// physical release as handled by another HUD dialog.
    /// </summary>
    internal bool InvokeFromOwner()
    {
        if (!Enabled) return false;
        pressed = false;
        onClick();
        return true;
    }

    public void SetActive(bool enabled)
    {
        if (active == enabled) return;
        active = enabled;
        if (activeTexture.TextureId > 0)
        {
            ComposeTexture(activeTexture, false, true, true, true);
        }
    }

    public override void Dispose()
    {
        normalTexture.Dispose();
        hoverTexture.Dispose();
        pressedTexture.Dispose();
        activeTexture.Dispose();
        disabledTexture.Dispose();
        base.Dispose();
    }

    private void ComposeTexture(
        LoadedTexture texture,
        bool isPressed,
        bool isHovered,
        bool isEnabled,
        bool isActive = false
    )
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        AtlasUiStyle.Clear(context);
        bool dark = style == AtlasButtonStyle.Dark;
        AtlasUiStyle.DrawRaisedControl(
            context,
            width,
            height,
            isPressed,
            (isHovered || isActive) && isEnabled,
            dark || isActive,
            style == AtlasButtonStyle.Icon ? 40 : style == AtlasButtonStyle.Compact ? 8 : 12
        );

        double[] color = !isEnabled
            ? new[] { 0.72, 0.75, 0.78, 0.58 }
            : AtlasUiStyle.LightInk;
        using CairoFont font = CairoFont.WhiteDetailText()
            .WithFontSize(
                style == AtlasButtonStyle.Icon
                    ? 20
                    : style == AtlasButtonStyle.Compact ? 13 : 12
            )
            .WithWeight(FontWeight.Bold)
            .WithColor(color);
        AtlasUiStyle.DrawCenteredText(
            context,
            font,
            label,
            width,
            height,
            isPressed ? Math.Max(1, RuntimeEnv.GUIScale) : 0,
            isPressed ? Math.Max(1, RuntimeEnv.GUIScale) : 0
        );
        generateTexture(surface, ref texture, true);
    }
}

internal sealed class GuiElementAtlasSwitch : GuiElementControl
{
    private readonly Action<bool> onChanged;
    private readonly LoadedTexture offTexture;
    private readonly LoadedTexture onTexture;
    private readonly LoadedTexture disabledTexture;
    private bool value;
    private bool pressed;

    public GuiElementAtlasSwitch(
        ICoreClientAPI capi,
        Action<bool> onChanged,
        ElementBounds bounds
    ) : base(capi, bounds)
    {
        this.onChanged = onChanged;
        offTexture = new LoadedTexture(capi);
        onTexture = new LoadedTexture(capi);
        disabledTexture = new LoadedTexture(capi);
        MouseOverCursor = "hand";
    }

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        ComposeTexture(offTexture, false, true);
        ComposeTexture(onTexture, true, true);
        ComposeTexture(disabledTexture, false, false);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        LoadedTexture texture = !Enabled ? disabledTexture : value ? onTexture : offTexture;
        if (texture.TextureId > 0)
        {
            Render2DTexture(texture.TextureId, Bounds, 50, ColorUtil.WhiteArgbVec);
        }
    }

    public override void OnMouseDownOnElement(ICoreClientAPI capi, MouseEvent args)
    {
        if (!Enabled || args.Button != EnumMouseButton.Left) return;
        pressed = true;
        args.Handled = true;
    }

    public override void OnMouseUpOnElement(ICoreClientAPI capi, MouseEvent args)
    {
        if (args.Button != EnumMouseButton.Left) return;
        bool ownedPress = pressed;
        pressed = false;
        if (!ownedPress || !Enabled) return;
        value = !value;
        onChanged(value);
        args.Handled = true;
    }

    public override void OnMouseUp(ICoreClientAPI capi, MouseEvent args)
    {
        // A presentation change can rebuild viewport composers between the
        // two mouse events. Always clear the local press latch, but only a
        // press owned by this switch may toggle its value.
        base.OnMouseUp(capi, args);
        if (args.Button == EnumMouseButton.Left) pressed = false;
    }

    public void SetValue(bool enabled) => value = enabled;

    public override void Dispose()
    {
        pressed = false;
        offTexture.Dispose();
        onTexture.Dispose();
        disabledTexture.Dispose();
        base.Dispose();
    }

    private void ComposeTexture(LoadedTexture texture, bool enabledValue, bool available)
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        AtlasUiStyle.Clear(context);

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double margin = 4 * scale;
        double trackWidth = Math.Max(1, width - margin * 2);
        double trackHeight = Math.Max(1, height - margin * 2);
        double radius = trackHeight * 0.5;
        AtlasUiStyle.RoundedRectangle(context, margin, margin, trackWidth, trackHeight, radius);
        if (!available)
        {
            context.SetSourceRGBA(0.18, 0.20, 0.22, 0.42);
        }
        else if (enabledValue)
        {
            context.SetSourceRGBA(0.02, 0.03, 0.04, 0.84);
        }
        else
        {
            context.SetSourceRGBA(0.03, 0.045, 0.055, 0.34);
        }
        context.FillPreserve();
        context.SetSourceRGBA(1, 1, 1, enabledValue ? 0.48 : 0.22);
        context.LineWidth = Math.Max(1, scale * 0.7);
        context.Stroke();

        double knobRadius = Math.Max(2, radius - 3 * scale);
        double knobX = enabledValue
            ? margin + trackWidth - radius
            : margin + radius;
        double knobY = margin + radius;
        context.Arc(knobX + scale, knobY + 2 * scale, knobRadius, 0, Math.PI * 2);
        context.SetSourceRGBA(0.04, 0.05, 0.06, available ? 0.28 : 0.12);
        context.Fill();
        context.Arc(knobX, knobY, knobRadius, 0, Math.PI * 2);
        context.SetSourceRGBA(
            available ? 0.96 : 0.66,
            available ? 0.97 : 0.68,
            available ? 0.98 : 0.70,
            available ? 0.98 : 0.64
        );
        context.FillPreserve();
        context.SetSourceRGBA(1, 1, 1, 0.62);
        context.Stroke();
        generateTexture(surface, ref texture, true);
    }
}

internal sealed class GuiElementAtlasSlider : GuiElementControl
{
    private readonly ActionConsumable<int> onChanged;
    private LoadedTexture texture;
    private int minimum;
    private int maximum = 100;
    private int step = 1;
    private int value;
    private string unit = "";
    private bool dragging;
    private bool composed;

    public GuiElementAtlasSlider(
        ICoreClientAPI capi,
        ActionConsumable<int> onChanged,
        ElementBounds bounds
    ) : base(capi, bounds)
    {
        this.onChanged = onChanged;
        texture = new LoadedTexture(capi);
        MouseOverCursor = "hand";
    }

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        composed = true;
        ComposeTexture();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (texture.TextureId > 0)
        {
            Render2DTexture(texture.TextureId, Bounds, 50, ColorUtil.WhiteArgbVec);
        }
    }

    public override void OnMouseDownOnElement(ICoreClientAPI capi, MouseEvent args)
    {
        if (!Enabled || args.Button != EnumMouseButton.Left) return;
        dragging = true;
        UpdateFromMouse(args.X);
        args.Handled = true;
    }

    public override void OnMouseMove(ICoreClientAPI capi, MouseEvent args)
    {
        if (dragging)
        {
            UpdateFromMouse(args.X);
            args.Handled = true;
        }
    }

    public override void OnMouseUp(ICoreClientAPI capi, MouseEvent args)
    {
        if (args.Button == EnumMouseButton.Left) dragging = false;
    }

    public override void OnMouseWheel(ICoreClientAPI capi, MouseWheelEventArgs args)
    {
        if (!Enabled || !Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY)) return;
        float wheel = args.deltaPrecise != 0 ? args.deltaPrecise : args.delta;
        SetInteractiveValue(value + (wheel > 0 ? step : -step));
        args.SetHandled();
    }

    public void SetValues(int current, int minimum, int maximum, int step, string unit)
    {
        this.minimum = Math.Min(minimum, maximum);
        this.maximum = Math.Max(minimum, maximum);
        this.step = Math.Max(1, step);
        this.unit = unit ?? "";
        value = Snap(current);
        if (composed) ComposeTexture();
    }

    public void SetValue(int current)
    {
        value = Snap(current);
        if (composed) ComposeTexture();
    }

    public int GetValue() => value;

    public override void Dispose()
    {
        texture.Dispose();
        base.Dispose();
    }

    private void UpdateFromMouse(int mouseX)
    {
        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double labelWidth = Math.Min(Bounds.InnerWidth * 0.30, 56 * scale);
        double trackStart = 10 * scale;
        double trackWidth = Math.Max(1, Bounds.InnerWidth - labelWidth - 20 * scale);
        // A drag remains owned by the slider after the pointer leaves its
        // bounds. PositionInside returns null in that case, so derive the
        // horizontal coordinate directly and clamp it to the track instead.
        double localX = mouseX - Bounds.absX;
        double progress = Math.Clamp((localX - trackStart) / trackWidth, 0, 1);
        SetInteractiveValue(minimum + (int)Math.Round((maximum - minimum) * progress));
    }

    private void SetInteractiveValue(int candidate)
    {
        int snapped = Snap(candidate);
        if (snapped == value) return;
        value = snapped;
        onChanged(value);
        ComposeTexture();
    }

    private int Snap(int candidate)
    {
        int clamped = Math.Clamp(candidate, minimum, maximum);
        int snapped = minimum + (int)Math.Round((clamped - minimum) / (double)step) * step;
        return Math.Clamp(snapped, minimum, maximum);
    }

    private void ComposeTexture()
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        AtlasUiStyle.Clear(context);

        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double labelWidth = Math.Min(width * 0.30, 56 * scale);
        double trackX = 10 * scale;
        double trackWidth = Math.Max(1, width - labelWidth - 20 * scale);
        double trackHeight = Math.Max(5 * scale, height * 0.20);
        double trackY = (height - trackHeight) * 0.5;
        double progress = maximum == minimum ? 0 : (value - minimum) / (double)(maximum - minimum);

        AtlasUiStyle.RoundedRectangle(context, trackX, trackY, trackWidth, trackHeight, trackHeight * 0.5);
        context.SetSourceRGBA(1, 1, 1, Enabled ? 0.22 : 0.10);
        context.Fill();
        double filledWidth = Math.Max(trackHeight, trackWidth * progress);
        AtlasUiStyle.RoundedRectangle(context, trackX, trackY, filledWidth, trackHeight, trackHeight * 0.5);
        context.SetSourceRGBA(0.95, 0.97, 0.99, Enabled ? 0.82 : 0.30);
        context.Fill();

        double knobX = trackX + trackWidth * progress;
        double knobRadius = Math.Max(5 * scale, height * 0.24);
        context.Arc(knobX + scale, height * 0.5 + 2 * scale, knobRadius, 0, Math.PI * 2);
        context.SetSourceRGBA(0.03, 0.04, 0.05, 0.24);
        context.Fill();
        context.Arc(knobX, height * 0.5, knobRadius, 0, Math.PI * 2);
        context.SetSourceRGBA(0.97, 0.98, 0.99, Enabled ? 0.98 : 0.62);
        context.FillPreserve();
        context.SetSourceRGBA(1, 1, 1, 0.62);
        context.Stroke();

        using CairoFont font = CairoFont.WhiteDetailText()
            .WithFontSize(11)
            .WithWeight(FontWeight.Bold)
            .WithColor(Enabled ? AtlasUiStyle.Ink : AtlasUiStyle.MutedInk);
        string text = $"{value}{unit}";
        font.SetupContext(context);
        TextExtents extents = font.GetTextExtents(text);
        double textX = width - labelWidth * 0.5 - extents.Width * 0.5 - extents.XBearing;
        double textY = (height - extents.Height) * 0.5 - extents.YBearing;
        context.MoveTo(textX, textY);
        context.ShowText(text);
        generateTexture(surface, ref texture, true);
    }
}

internal sealed class GuiElementAtlasChoice : GuiElementControl
{
    private string[] values;
    private string[] names;
    private readonly SelectionChangedDelegate onChanged;
    private readonly LoadedTexture normalTexture;
    private readonly LoadedTexture pressedTexture;
    private int selectedIndex;
    private int pressedDirection;
    private bool composed;

    public GuiElementAtlasChoice(
        ICoreClientAPI capi,
        string[] values,
        string[] names,
        int selectedIndex,
        SelectionChangedDelegate onChanged,
        ElementBounds bounds
    ) : base(capi, bounds)
    {
        this.values = values;
        this.names = names;
        this.selectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, names.Length - 1));
        this.onChanged = onChanged;
        normalTexture = new LoadedTexture(capi);
        pressedTexture = new LoadedTexture(capi);
        MouseOverCursor = "hand";
    }

    public string SelectedValue => values.Length == 0 ? "" : values[selectedIndex];

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        composed = true;
        ComposeTexture(normalTexture, false);
        ComposeTexture(pressedTexture, true);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        LoadedTexture texture = pressedDirection == 0 ? normalTexture : pressedTexture;
        if (texture.TextureId > 0)
        {
            Render2DTexture(texture.TextureId, Bounds, 50, ColorUtil.WhiteArgbVec);
        }
    }

    public override void OnMouseDownOnElement(ICoreClientAPI capi, MouseEvent args)
    {
        if (!Enabled || args.Button != EnumMouseButton.Left) return;
        Vec2d inside = Bounds.PositionInside(args.X, args.Y);
        pressedDirection = inside.X < Bounds.InnerWidth * 0.5 ? -1 : 1;
        args.Handled = true;
    }

    public override void OnMouseUpOnElement(ICoreClientAPI capi, MouseEvent args)
    {
        if (pressedDirection == 0 || args.Button != EnumMouseButton.Left) return;
        int direction = pressedDirection;
        pressedDirection = 0;
        Select(selectedIndex + direction, true);
        args.Handled = true;
    }

    public override void OnMouseUp(ICoreClientAPI capi, MouseEvent args)
    {
        base.OnMouseUp(capi, args);
        if (args.Button == EnumMouseButton.Left) pressedDirection = 0;
    }

    public override void OnMouseWheel(ICoreClientAPI capi, MouseWheelEventArgs args)
    {
        if (!Enabled || !Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY)) return;
        float wheel = args.deltaPrecise != 0 ? args.deltaPrecise : args.delta;
        Select(selectedIndex + (wheel > 0 ? -1 : 1), true);
        args.SetHandled();
    }

    public void SetSelectedIndex(int index) => Select(index, false);

    public void SetList(string[] values, string[] names)
    {
        this.values = values;
        this.names = names;
        selectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, names.Length - 1));
        RecomposeTextures();
    }

    public override void Dispose()
    {
        normalTexture.Dispose();
        pressedTexture.Dispose();
        base.Dispose();
    }

    private void Select(int index, bool notify)
    {
        if (names.Length == 0) return;
        int count = names.Length;
        selectedIndex = ((index % count) + count) % count;
        RecomposeTextures();
        if (notify) onChanged(values[selectedIndex], true);
    }

    private void RecomposeTextures()
    {
        if (!composed) return;
        ComposeTexture(normalTexture, false);
        ComposeTexture(pressedTexture, true);
    }

    private void ComposeTexture(LoadedTexture texture, bool pressed)
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        AtlasUiStyle.Clear(context);
        AtlasUiStyle.DrawRaisedControl(context, width, height, pressed, false, false, 12);

        string name = names.Length == 0 ? "—" : names[selectedIndex];
        double[] color = Enabled ? AtlasUiStyle.Ink : AtlasUiStyle.MutedInk;
        using CairoFont labelFont = CairoFont.WhiteDetailText()
            .WithFontSize(12)
            .WithWeight(FontWeight.Bold)
            .WithColor(color);
        AtlasUiStyle.DrawCenteredText(context, labelFont, name, width, height);

        using CairoFont arrowFont = CairoFont.WhiteSmallishText()
            .WithFontSize(20)
            .WithWeight(FontWeight.Bold)
            .WithColor(color);
        double offset = pressed ? Math.Max(1, RuntimeEnv.GUIScale) : 0;
        AtlasUiStyle.DrawCenteredText(context, arrowFont, "‹", width * 0.18, height, offset, offset);
        AtlasUiStyle.DrawCenteredText(context, arrowFont, "›", width * 0.18, height, width * 0.82 + offset, offset);
        generateTexture(surface, ref texture, true);
    }
}

internal sealed class GuiElementAtlasTextInput : GuiElementTextInput
{
    public GuiElementAtlasTextInput(
        ICoreClientAPI capi,
        ElementBounds bounds,
        Action<string> onTextChanged,
        CairoFont font
    ) : base(capi, bounds, onTextChanged, font)
    {
    }

    public override void ComposeTextElements(Context context, ImageSurface surface)
    {
        AtlasUiStyle.DrawInsetPanel(
            context,
            Bounds.drawX,
            Bounds.drawY,
            Bounds.InnerWidth,
            Bounds.InnerHeight,
            10
        );
        base.ComposeTextElements(context, surface);
    }
}

internal static class AtlasGuiComposerExtensions
{
    public static GuiComposer AddAtlasButton(
        this GuiComposer composer,
        string label,
        ActionConsumable onClick,
        ElementBounds bounds,
        string key,
        AtlasButtonStyle style = AtlasButtonStyle.Surface
    ) => composer.AddInteractiveElement(
        new GuiElementAtlasButton(composer.Api, label, onClick, bounds, style),
        key
    );

    public static GuiComposer AddAtlasSwitch(
        this GuiComposer composer,
        Action<bool> onChanged,
        ElementBounds bounds,
        string key
    ) => composer.AddInteractiveElement(
        new GuiElementAtlasSwitch(composer.Api, onChanged, bounds),
        key
    );

    public static GuiComposer AddAtlasSlider(
        this GuiComposer composer,
        ActionConsumable<int> onChanged,
        ElementBounds bounds,
        string key
    ) => composer.AddInteractiveElement(
        new GuiElementAtlasSlider(composer.Api, onChanged, bounds),
        key
    );

    public static GuiComposer AddAtlasChoice(
        this GuiComposer composer,
        string[] values,
        string[] names,
        int selectedIndex,
        SelectionChangedDelegate onChanged,
        ElementBounds bounds,
        string key
    ) => composer.AddInteractiveElement(
        new GuiElementAtlasChoice(
            composer.Api,
            values,
            names,
            selectedIndex,
            onChanged,
            bounds
        ),
        key
    );

    public static GuiComposer AddAtlasTextInput(
        this GuiComposer composer,
        ElementBounds bounds,
        Action<string> onTextChanged,
        CairoFont font,
        string key
    ) => composer.AddInteractiveElement(
        new GuiElementAtlasTextInput(composer.Api, bounds, onTextChanged, font),
        key
    );

    public static GuiElementAtlasButton? GetAtlasButton(this GuiComposer? composer, string key) =>
        composer?.GetElement(key) as GuiElementAtlasButton;

    public static GuiElementAtlasSwitch? GetAtlasSwitch(this GuiComposer? composer, string key) =>
        composer?.GetElement(key) as GuiElementAtlasSwitch;

    public static GuiElementAtlasSlider? GetAtlasSlider(this GuiComposer? composer, string key) =>
        composer?.GetElement(key) as GuiElementAtlasSlider;

    public static GuiElementAtlasChoice? GetAtlasChoice(this GuiComposer? composer, string key) =>
        composer?.GetElement(key) as GuiElementAtlasChoice;

    public static GuiElementAtlasTextInput? GetAtlasTextInput(this GuiComposer? composer, string key) =>
        composer?.GetElement(key) as GuiElementAtlasTextInput;
}
