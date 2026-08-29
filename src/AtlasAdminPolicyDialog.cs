using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ModernAtlas;

/// <summary>
/// Operator-only editor for the server-owned ModernAtlas policy. Opening and
/// every mutation are authorized again on the server; this client GUI is not
/// a trust boundary.
/// </summary>
internal sealed class AtlasAdminPolicyDialog : GuiDialog
{
    private static readonly string[] DefaultValues = { "0", "1" };
    private static readonly string[] DefaultNames = { "Deny", "Allow" };
    private static readonly string[] OverrideValues = { "-1", "0", "1" };
    private static readonly string[] OverrideNames = { "Inherit", "Deny", "Allow" };
    private static readonly string[] PolicyKeys =
    {
        "admin-settings",
        "admin-hide-vegetation",
        "admin-creative-cheat",
        "admin-show-players",
        "admin-show-animals",
        "admin-show-mobs",
        "admin-show-npcs"
    };

    private readonly Action<AtlasAdminPolicyUpdate> onApply;
    private AtlasAdminPolicyState state;
    private string selectedPlayerUid = "";

    public override string ToggleKeyCombinationCode => "";
    public override EnumDialogType DialogType => EnumDialogType.Dialog;
    public override double DrawOrder => 0.96;
    public override double InputOrder => 0;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;

    public AtlasAdminPolicyDialog(
        ICoreClientAPI capi,
        AtlasAdminPolicyState state,
        Action<AtlasAdminPolicyUpdate> onApply
    ) : base(capi)
    {
        this.state = state;
        this.onApply = onApply;
        Compose();
    }

    public override bool CaptureAllInputs() => true;
    public override bool CaptureRawMouse() => true;
    public override bool OnEscapePressed()
    {
        TryClose();
        return true;
    }

    public void UpdateState(AtlasAdminPolicyState updated)
    {
        state = updated;
        if (!state.Targets.Exists(target => target.PlayerUid == selectedPlayerUid))
        {
            selectedPlayerUid = "";
        }
        Compose();
    }

    internal string? ValidateAutomatedControlSurface()
    {
        if (SingleComposer.GetAtlasChoice("admin-target") == null)
        {
            return "the Target selector is missing";
        }
        foreach (string key in PolicyKeys)
        {
            if (SingleComposer.GetAtlasChoice(key) == null)
            {
                return $"the {key} policy selector is missing";
            }
        }
        foreach (string key in new[] { "admin-close", "admin-reset", "admin-apply" })
        {
            if (SingleComposer.GetAtlasButton(key) == null)
            {
                return $"the {key} button is missing";
            }
        }
        return null;
    }

    internal bool SelectFirstPlayerForAutomation()
    {
        if (state.Targets.Count < 2 || !string.IsNullOrEmpty(selectedPlayerUid))
        {
            return false;
        }
        return SingleComposer.GetAtlasChoice("admin-target")
            ?.InvokeDirectionFromOwner(1) == true
            && !string.IsNullOrEmpty(selectedPlayerUid);
    }

    internal bool SelectDefaultsForAutomation()
    {
        int guard = Math.Max(1, state.Targets.Count + 1);
        GuiElementAtlasChoice? target = SingleComposer.GetAtlasChoice("admin-target");
        while (!string.IsNullOrEmpty(selectedPlayerUid) && guard-- > 0)
        {
            if (target?.InvokeDirectionFromOwner(-1) != true) return false;
            target = SingleComposer.GetAtlasChoice("admin-target");
        }
        return string.IsNullOrEmpty(selectedPlayerUid);
    }

    internal bool CycleEveryPolicyChoiceForAutomation(int steps)
    {
        foreach (string key in PolicyKeys)
        {
            for (int step = 0; step < steps; step++)
            {
                if (SingleComposer.GetAtlasChoice(key)
                        ?.InvokeDirectionFromOwner(1) != true)
                {
                    return false;
                }
            }
        }
        return true;
    }

    internal bool InvokeApplyForAutomation() =>
        SingleComposer.GetAtlasButton("admin-apply")?.InvokeFromOwner() == true;

    internal bool InvokeResetForAutomation() =>
        SingleComposer.GetAtlasButton("admin-reset")?.InvokeFromOwner() == true;

    internal bool InvokeCloseForAutomation() =>
        SingleComposer.GetAtlasButton("admin-close")?.InvokeFromOwner() == true;

    private void Compose()
    {
        SingleComposer?.Dispose();
        AtlasAdminPolicyTarget selected = SelectedTarget();
        bool defaults = string.IsNullOrEmpty(selected.PlayerUid);
        string[] targetValues = state.Targets.Select(target => target.PlayerUid).ToArray();
        string[] targetNames = state.Targets.Select(target =>
            string.IsNullOrEmpty(target.PlayerUid)
                ? "Server defaults"
                : target.PlayerName
        ).ToArray();
        int targetIndex = Math.Max(0, state.Targets.FindIndex(
            target => target.PlayerUid == selected.PlayerUid
        ));

        const double width = 720;
        const double height = 596;
        ElementBounds root = ElementBounds.Fixed(0, 0, width, height)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        GuiComposer composer = capi.Gui.CreateCompo(
                "modernatlas-admin-policy",
                root
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(0, 0, width, height),
                AtlasUiStyle.DrawCard
            )
            .AddStaticText(
                "MODERNATLAS SERVER POLICY",
                AtlasUiStyle.TitleFont(20),
                ElementBounds.Fixed(26, 20, 560, 34)
            )
            .AddAtlasButton(
                "×",
                Close,
                ElementBounds.Fixed(width - 60, 12, 44, 38),
                "admin-close",
                AtlasButtonStyle.Icon
            )
            .AddStaticText(
                "Target",
                AtlasUiStyle.LabelFont(12),
                ElementBounds.Fixed(28, 70, 100, 28)
            )
            .AddAtlasChoice(
                targetValues,
                targetNames,
                targetIndex,
                OnTargetChanged,
                ElementBounds.Fixed(144, 64, 544, 40),
                "admin-target"
            )
            .AddStaticText(
                defaults
                    ? "These defaults apply to every player without an exception."
                    : "Inherit follows Server defaults. Exceptions are stored by stable player UID.",
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(28, 112, 650, 32)
            )
            .AddStaticCustomDraw(
                ElementBounds.Fixed(26, 146, 662, 2),
                AtlasUiStyle.DrawSeparator
            );

        AddPolicyRow(composer, 164, "Settings access", PolicyKeys[0], selected.ClientSettings, defaults);
        AddPolicyRow(composer, 208, "Hide vegetation", PolicyKeys[1], selected.HideVegetation, defaults);
        AddPolicyRow(composer, 252, "Creative / Cheat atlas tools", PolicyKeys[2], selected.CreativeCheatTools, defaults);
        AddPolicyRow(composer, 310, "Show players", PolicyKeys[3], selected.ShowPlayers, defaults);
        AddPolicyRow(composer, 354, "Show animals", PolicyKeys[4], selected.ShowAnimals, defaults);
        AddPolicyRow(composer, 398, "Show hostile mobs", PolicyKeys[5], selected.ShowMobs, defaults);
        AddPolicyRow(composer, 442, "Show NPCs", PolicyKeys[6], selected.ShowNpcs, defaults);

        composer
            .AddStaticText(
                "Creative / Cheat grants ModernAtlas inspection, search, cave and ore tools only; it never changes the player's Vintage Story game mode.",
                AtlasUiStyle.DetailFont(10),
                ElementBounds.Fixed(28, 286, 650, 30)
            )
            .AddDynamicText(
                state.Status,
                AtlasUiStyle.DetailFont(11),
                ElementBounds.Fixed(28, 494, 650, 28),
                "admin-status"
            )
            .AddAtlasButton(
                "INHERIT ALL",
                ResetSelectedOverride,
                ElementBounds.Fixed(28, 536, 190, 40),
                "admin-reset",
                AtlasButtonStyle.Dark
            )
            .AddAtlasButton(
                "APPLY POLICY",
                Apply,
                ElementBounds.Fixed(482, 536, 206, 40),
                "admin-apply"
            );

        SingleComposer = composer.Compose();
        SingleComposer.GetAtlasButton("admin-reset")!.Enabled = !defaults;
    }

    private static void AddPolicyRow(
        GuiComposer composer,
        double y,
        string label,
        string key,
        int mode,
        bool defaults
    )
    {
        string[] values = defaults ? DefaultValues : OverrideValues;
        string[] names = defaults ? DefaultNames : OverrideNames;
        int selectedIndex = Array.IndexOf(values, mode.ToString(CultureInfo.InvariantCulture));
        if (selectedIndex < 0) selectedIndex = 0;
        composer
            .AddStaticText(
                label,
                AtlasUiStyle.LabelFont(12),
                ElementBounds.Fixed(30, y + 7, 330, 28)
            )
            .AddAtlasChoice(
                values,
                names,
                selectedIndex,
                (_, _) => { },
                ElementBounds.Fixed(430, y, 258, 36),
                key
            );
    }

    private AtlasAdminPolicyTarget SelectedTarget() =>
        state.Targets.Find(target => target.PlayerUid == selectedPlayerUid)
        ?? state.Targets.FirstOrDefault()
        ?? new AtlasAdminPolicyTarget { PlayerName = "Server defaults" };

    private void OnTargetChanged(string playerUid, bool _)
    {
        selectedPlayerUid = playerUid;
        Compose();
    }

    private bool Apply()
    {
        onApply(BuildUpdate());
        SingleComposer.GetDynamicText("admin-status")?.SetNewText(
            "Saving on the server…"
        );
        return true;
    }

    private bool ResetSelectedOverride()
    {
        if (string.IsNullOrEmpty(selectedPlayerUid)) return true;
        foreach (string key in PolicyKeys)
        {
            SingleComposer.GetAtlasChoice(key)?.SetSelectedIndex(0);
        }
        return Apply();
    }

    private AtlasAdminPolicyUpdate BuildUpdate() => new()
    {
        PlayerUid = selectedPlayerUid,
        ClientSettings = ReadMode(PolicyKeys[0]),
        HideVegetation = ReadMode(PolicyKeys[1]),
        CreativeCheatTools = ReadMode(PolicyKeys[2]),
        ShowPlayers = ReadMode(PolicyKeys[3]),
        ShowAnimals = ReadMode(PolicyKeys[4]),
        ShowMobs = ReadMode(PolicyKeys[5]),
        ShowNpcs = ReadMode(PolicyKeys[6])
    };

    private int ReadMode(string key) => int.TryParse(
        SingleComposer.GetAtlasChoice(key)?.SelectedValue,
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out int mode
    ) ? mode : 0;

    private bool Close()
    {
        TryClose();
        return true;
    }
}
