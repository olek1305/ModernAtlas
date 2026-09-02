using System;
using System.Reflection;
using Vintagestory.API.Client;

namespace ModernAtlas;

/// <summary>
/// Separate opt-in multiplayer smoke for the server policy dialog. It is not
/// part of MODERNATLAS_SMOKE_TEST and performs no atlas rendering, screenshots
/// or ordinary post-change checks.
/// </summary>
public sealed partial class ModernAtlasSystem
{
    private int adminPolicySmokePhase;
    private string? adminPolicySmokeWorld;
    private int adminPolicySmokeGeneration;
    private string? adminPolicySmokePlayerUid;
    private AtlasAdminPolicyTarget? adminPolicySmokeOriginalDefaults;
    private bool adminPolicySmokeFinished;

    private void ScheduleAdminPolicySmokeStart(
        string worldIdentifier,
        int sessionGeneration
    )
    {
        if (!AdminPolicySmokeEnabled || clientApi == null) return;

        adminPolicySmokePhase = 0;
        adminPolicySmokeWorld = worldIdentifier;
        adminPolicySmokeGeneration = sessionGeneration;
        adminPolicySmokePlayerUid = clientApi.World.Player?.PlayerUID;
        adminPolicySmokeOriginalDefaults = null;
        adminPolicySmokeFinished = false;
        clientApi.Logger.Notification(
            "[ModernAtlas] ADMIN POLICY SMOKE START: separate dedicated-server/client GUI test; main atlas smoke is disabled."
        );
        clientApi.Event.RegisterCallback(
            _ =>
            {
                if (!IsCurrentAdminPolicySmoke()) return;
                clientApi.SendChatMessage("/ma admin", "");
            },
            5000
        );
        clientApi.Event.RegisterCallback(
            _ =>
            {
                if (IsCurrentAdminPolicySmoke() && !adminPolicySmokeFinished)
                {
                    FailAdminPolicySmoke(
                        "the server/client control sequence did not finish before its timeout"
                    );
                }
            },
            30000
        );
    }

    private void HandleAdminPolicySmokeState(AtlasAdminPolicyState state)
    {
        if (!IsCurrentAdminPolicySmoke() || adminPolicyDialog == null) return;

        string? surfaceFailure = adminPolicyDialog.ValidateAutomatedControlSurface();
        if (surfaceFailure != null)
        {
            FailAdminPolicySmoke(surfaceFailure);
            return;
        }

        switch (adminPolicySmokePhase)
        {
            case 0:
                BeginAdminPlayerControlCoverage(state);
                return;
            case 1:
                ValidatePlayerAllowThenReset(state);
                return;
            case 2:
                ValidatePlayerInheritanceThenEditDefaults(state);
                return;
            case 3:
                ValidateInvertedDefaultsThenRestore(state);
                return;
            case 4:
                ValidateRestoredDefaultsThenReopen();
                return;
            case 5:
                ValidateReopenedPersistentState(state);
                return;
        }
    }

    private void BeginAdminPlayerControlCoverage(AtlasAdminPolicyState state)
    {
        AtlasAdminPolicyTarget? defaults = FindAdminTarget(state, "");
        AtlasAdminPolicyTarget? player = FindAdminTarget(
            state,
            adminPolicySmokePlayerUid
        );
        if (defaults == null || player == null)
        {
            FailAdminPolicySmoke(
                "the policy snapshot did not contain Server defaults and the local player"
            );
            return;
        }
        if (!EveryAdminMode(player, -1))
        {
            FailAdminPolicySmoke(
                "the disposable fixture must begin with no per-player exception"
            );
            return;
        }

        adminPolicySmokeOriginalDefaults = CopyAdminTarget(defaults);
        if (!adminPolicyDialog!.SelectFirstPlayerForAutomation())
        {
            FailAdminPolicySmoke("the Target selector could not select its player entry");
            return;
        }
        clientApi!.Event.RegisterCallback(
            _ =>
            {
                if (!IsCurrentAdminPolicySmoke() || adminPolicyDialog == null) return;
                if (!adminPolicyDialog.CycleEveryPolicyChoiceForAutomation(2))
                {
                    FailAdminPolicySmoke(
                        "one of the seven player policy selectors could not traverse Inherit, Deny and Allow"
                    );
                    return;
                }
                adminPolicySmokePhase = 1;
                if (!adminPolicyDialog.InvokeApplyForAutomation())
                {
                    FailAdminPolicySmoke("Apply policy could not be invoked");
                }
            },
            150
        );
    }

    private void ValidatePlayerAllowThenReset(AtlasAdminPolicyState state)
    {
        AtlasAdminPolicyTarget? player = FindAdminTarget(
            state,
            adminPolicySmokePlayerUid
        );
        if (player == null || !EveryAdminMode(player, 1)
            || !EffectivePolicyMatches(player))
        {
            FailAdminPolicySmoke(
                "Allow was not persisted and applied to every player policy field"
            );
            return;
        }

        adminPolicySmokePhase = 2;
        if (!adminPolicyDialog!.InvokeResetForAutomation())
        {
            FailAdminPolicySmoke("Inherit all could not be invoked");
        }
    }

    private void ValidatePlayerInheritanceThenEditDefaults(
        AtlasAdminPolicyState state
    )
    {
        AtlasAdminPolicyTarget? player = FindAdminTarget(
            state,
            adminPolicySmokePlayerUid
        );
        if (player == null || !EveryAdminMode(player, -1)
            || adminPolicySmokeOriginalDefaults == null
            || !EffectivePolicyMatches(adminPolicySmokeOriginalDefaults))
        {
            FailAdminPolicySmoke(
                "Inherit all did not remove the exception and restore effective defaults"
            );
            return;
        }
        if (!adminPolicyDialog!.SelectDefaultsForAutomation())
        {
            FailAdminPolicySmoke("the Target selector could not return to Server defaults");
            return;
        }
        clientApi!.Event.RegisterCallback(
            _ =>
            {
                if (!IsCurrentAdminPolicySmoke() || adminPolicyDialog == null) return;
                if (!adminPolicyDialog.CycleEveryPolicyChoiceForAutomation(1))
                {
                    FailAdminPolicySmoke(
                        "one of the seven Server default selectors could not toggle"
                    );
                    return;
                }
                adminPolicySmokePhase = 3;
                if (!adminPolicyDialog.InvokeApplyForAutomation())
                {
                    FailAdminPolicySmoke("Apply policy failed for Server defaults");
                }
            },
            150
        );
    }

    private void ValidateInvertedDefaultsThenRestore(
        AtlasAdminPolicyState state
    )
    {
        AtlasAdminPolicyTarget? defaults = FindAdminTarget(state, "");
        if (defaults == null || adminPolicySmokeOriginalDefaults == null
            || !EveryAdminModeInverted(defaults, adminPolicySmokeOriginalDefaults)
            || !EffectivePolicyMatches(defaults))
        {
            FailAdminPolicySmoke(
                "the toggled Server defaults were not persisted and applied"
            );
            return;
        }
        if (!adminPolicyDialog!.CycleEveryPolicyChoiceForAutomation(1))
        {
            FailAdminPolicySmoke("the Server default selectors could not restore their values");
            return;
        }
        adminPolicySmokePhase = 4;
        if (!adminPolicyDialog.InvokeApplyForAutomation())
        {
            FailAdminPolicySmoke("Apply policy failed while restoring Server defaults");
        }
    }

    private void ValidateRestoredDefaultsThenReopen()
    {
        if (adminPolicySmokeOriginalDefaults == null
            || !EffectivePolicyMatches(adminPolicySmokeOriginalDefaults))
        {
            FailAdminPolicySmoke("the original effective policy was not restored");
            return;
        }
        if (!adminPolicyDialog!.InvokeCloseForAutomation()
            || adminPolicyDialog.IsOpened())
        {
            FailAdminPolicySmoke("Close did not close the administrator dialog");
            return;
        }
        adminPolicySmokePhase = 5;
        clientApi!.Event.RegisterCallback(
            _ =>
            {
                if (!IsCurrentAdminPolicySmoke()) return;
                clientApi.SendChatMessage("/ma admin", "");
            },
            250
        );
    }

    private void ValidateReopenedPersistentState(AtlasAdminPolicyState state)
    {
        AtlasAdminPolicyTarget? defaults = FindAdminTarget(state, "");
        AtlasAdminPolicyTarget? player = FindAdminTarget(
            state,
            adminPolicySmokePlayerUid
        );
        if (defaults == null || player == null
            || adminPolicySmokeOriginalDefaults == null
            || !SameAdminModes(defaults, adminPolicySmokeOriginalDefaults)
            || !EveryAdminMode(player, -1))
        {
            FailAdminPolicySmoke(
                "reopening the panel did not return the restored persistent server state"
            );
            return;
        }
        if (!adminPolicyDialog!.InvokeCloseForAutomation())
        {
            FailAdminPolicySmoke("Close failed after reopening the panel");
            return;
        }

        adminPolicySmokeFinished = true;
        clientApi!.Logger.Notification(
            "[ModernAtlas] ADMIN POLICY SMOKE PASSED: Target, all seven policy selectors, Apply policy, Inherit all, Close, server persistence, per-player isolation and immediate policy refresh were verified."
        );
        clientApi.Event.RegisterCallback(
            _ => ExitClientAfterAdminPolicySmoke(),
            500
        );
    }

    private static AtlasAdminPolicyTarget? FindAdminTarget(
        AtlasAdminPolicyState state,
        string? playerUid
    ) => state.Targets.Find(
        target => target.PlayerUid == (playerUid ?? "")
    );

    private bool EffectivePolicyMatches(AtlasAdminPolicyTarget target) =>
        target.ClientSettings == (serverPolicy.ClientSettingsLocked ? 0 : 1)
        && target.HideVegetation == (serverPolicy.HideVegetationLocked ? 0 : 1)
        && target.CreativeCheatTools == (serverPolicy.CheatModeAllowed ? 1 : 0)
        && target.ShowPlayers == (serverPolicy.ShowPlayers ? 1 : 0)
        && target.ShowAnimals == (serverPolicy.ShowAnimals ? 1 : 0)
        && target.ShowMobs == (serverPolicy.ShowMobs ? 1 : 0)
        && target.ShowNpcs == (serverPolicy.ShowNpcs ? 1 : 0);

    private static bool EveryAdminMode(AtlasAdminPolicyTarget target, int mode) =>
        target.ClientSettings == mode
        && target.HideVegetation == mode
        && target.CreativeCheatTools == mode
        && target.ShowPlayers == mode
        && target.ShowAnimals == mode
        && target.ShowMobs == mode
        && target.ShowNpcs == mode;

    private static bool EveryAdminModeInverted(
        AtlasAdminPolicyTarget current,
        AtlasAdminPolicyTarget original
    ) => current.ClientSettings == 1 - original.ClientSettings
        && current.HideVegetation == 1 - original.HideVegetation
        && current.CreativeCheatTools == 1 - original.CreativeCheatTools
        && current.ShowPlayers == 1 - original.ShowPlayers
        && current.ShowAnimals == 1 - original.ShowAnimals
        && current.ShowMobs == 1 - original.ShowMobs
        && current.ShowNpcs == 1 - original.ShowNpcs;

    private static bool SameAdminModes(
        AtlasAdminPolicyTarget left,
        AtlasAdminPolicyTarget right
    ) => left.ClientSettings == right.ClientSettings
        && left.HideVegetation == right.HideVegetation
        && left.CreativeCheatTools == right.CreativeCheatTools
        && left.ShowPlayers == right.ShowPlayers
        && left.ShowAnimals == right.ShowAnimals
        && left.ShowMobs == right.ShowMobs
        && left.ShowNpcs == right.ShowNpcs;

    private static AtlasAdminPolicyTarget CopyAdminTarget(
        AtlasAdminPolicyTarget source
    ) => new()
    {
        PlayerUid = source.PlayerUid,
        PlayerName = source.PlayerName,
        ClientSettings = source.ClientSettings,
        HideVegetation = source.HideVegetation,
        CreativeCheatTools = source.CreativeCheatTools,
        ShowPlayers = source.ShowPlayers,
        ShowAnimals = source.ShowAnimals,
        ShowMobs = source.ShowMobs,
        ShowNpcs = source.ShowNpcs
    };

    private bool IsCurrentAdminPolicySmoke() =>
        AdminPolicySmokeEnabled
        && clientApi != null
        && !clientApi.IsSinglePlayer
        && clientWorldSessionActive
        && activeWorldIdentifier == adminPolicySmokeWorld
        && worldSessionGeneration == adminPolicySmokeGeneration
        && !adminPolicySmokeFinished;

    private void FailAdminPolicySmoke(string reason)
    {
        if (adminPolicySmokeFinished) return;
        adminPolicySmokeFinished = true;
        clientApi?.Logger.Error(
            "[ModernAtlas] ADMIN POLICY SMOKE FAILED: {0}.",
            reason
        );
        clientApi?.Event.RegisterCallback(
            _ => ExitClientAfterAdminPolicySmoke(),
            500
        );
    }

    private void ExitClientAfterAdminPolicySmoke()
    {
        if (clientApi == null) return;
        try
        {
            const BindingFlags instanceFlags = BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic;
            const BindingFlags staticFlags = BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic;
            object game = clientApi.GetType().GetField(
                "game",
                instanceFlags
            )?.GetValue(clientApi)
                ?? throw new InvalidOperationException(
                    "Client game instance is unavailable."
                );
            object platform = game.GetType().GetField(
                "Platform",
                instanceFlags
            )?.GetValue(game)
                ?? throw new InvalidOperationException(
                    "Client platform is unavailable."
                );
            MethodInfo windowExit = platform.GetType().GetMethod(
                "WindowExit",
                instanceFlags
            ) ?? throw new MissingMethodException(
                platform.GetType().FullName,
                "WindowExit"
            );
            Type exitModeType = windowExit.GetParameters()[1].ParameterType;
            object softExit = Enum.Parse(exitModeType, "SoftExit");
            Type screenManagerType = game.GetType().Assembly.GetType(
                "Vintagestory.Client.ScreenManager"
            ) ?? throw new TypeLoadException(
                "Vintage Story screen manager is unavailable."
            );
            MethodInfo enqueueCallback = screenManagerType.GetMethod(
                "EnqueueCallBack",
                staticFlags
            ) ?? throw new MissingMethodException(
                screenManagerType.FullName,
                "EnqueueCallBack"
            );
            Action closeClient = () => windowExit.Invoke(
                platform,
                new[]
                {
                    "ModernAtlas admin policy smoke completed",
                    softExit
                }
            );
            enqueueCallback.Invoke(
                null,
                new object[]
                {
                    closeClient,
                    100,
                    "modernatlas-admin-policy-smoke-window-close"
                }
            );
        }
        catch (Exception exception)
        {
            Exception cause = exception is TargetInvocationException
                { InnerException: not null }
                ? exception.InnerException
                : exception;
            clientApi.Logger.Error(
                "[ModernAtlas] ADMIN POLICY SMOKE FAILED: clean client exit threw {0}: {1}.",
                cause.GetType().Name,
                cause.Message
            );
        }
    }

    private void ResetAdminPolicySmokeState()
    {
        adminPolicySmokePhase = 0;
        adminPolicySmokeWorld = null;
        adminPolicySmokeGeneration = 0;
        adminPolicySmokePlayerUid = null;
        adminPolicySmokeOriginalDefaults = null;
        adminPolicySmokeFinished = false;
    }
}
