using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ModernAtlas;

/// <summary>
/// One-time, per-save spoiler consent. Multiplayer never opens this dialog;
/// underground disclosure remains locked there unless a future server policy
/// explicitly authorizes it.
/// </summary>
internal sealed class CheatModeConsentDialog : GuiDialog
{
    private readonly Action<bool> onDecision;
    private bool resolved;

    public override string ToggleKeyCombinationCode => "";
    public override EnumDialogType DialogType => EnumDialogType.Dialog;
    public override double DrawOrder => 0.91;
    public override double InputOrder => 0;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;

    public CheatModeConsentDialog(ICoreClientAPI capi, Action<bool> onDecision)
        : base(capi)
    {
        this.onDecision = onDecision;

        ElementBounds root = ElementBounds.Fixed(0, 0, 610, 300)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds background = ElementBounds.Fixed(0, 0, 610, 300);
        SingleComposer = capi.Gui.CreateCompo("modernatlas-cheat-consent", root)
            .AddShadedDialogBG(background, true)
            .AddStaticText(
                "Enable Cheat Mode for this world?",
                CairoFont.WhiteSmallishText().WithFontSize(22),
                ElementBounds.Fixed(24, 22, 560, 38)
            )
            .AddRichtext(
                "Cheat Mode allows ModernAtlas to reveal underground caves and ore heatmaps. "
                    + "This can spoil exploration.\n\n"
                    + "ModernAtlas still uses only data already available to the client and never "
                    + "requests unexplored chunks. Multiplayer keeps these spoiler layers locked.",
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(26, 76, 558, 128),
                "consent-text"
            )
            .AddButton(
                "Keep caves hidden",
                ChooseSafeMode,
                ElementBounds.Fixed(32, 232, 220, 42),
                EnumButtonStyle.Normal,
                "safe-mode"
            )
            .AddButton(
                "Enable Cheat Mode",
                ChooseCheatMode,
                ElementBounds.Fixed(358, 232, 220, 42),
                EnumButtonStyle.Normal,
                "cheat-mode"
            )
            .Compose();
    }

    public override bool CaptureAllInputs() => true;
    public override bool CaptureRawMouse() => true;
    public override bool OnEscapePressed() => Resolve(false);

    public void CancelWithoutDecision()
    {
        resolved = true;
        TryClose();
    }

    private bool ChooseSafeMode() => Resolve(false);

    private bool ChooseCheatMode() => Resolve(true);

    private bool Resolve(bool enabled)
    {
        if (resolved) return true;

        resolved = true;
        onDecision(enabled);
        TryClose();
        return true;
    }
}
