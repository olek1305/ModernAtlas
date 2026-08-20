using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace ModernAtlas;

/// <summary>
/// Owns the independent ModernAtlas 3D dialog. The vanilla map remains
/// untouched and can still be opened through its own configured controls.
/// </summary>
public sealed class ModernAtlasSystem : ModSystem
{
    private const string ConfigFileName = "ModernAtlas.json";
    private const string ServerConfigFileName = "ModernAtlasServer.json";
    private const string PolicyChannelName = "modernatlas-policy";
    private const string SmokeTestEnvironmentVariable = "MODERNATLAS_SMOKE_TEST";
    private const string SmokeGodModePatchId = "modernatlas.smoke.godmode";

    // The smoke test must not change the saved world or the Vintage Story
    // game mode.  This narrowly scoped Harmony guard only rejects damage for
    // the local smoke-test player while the opt-in test is running.  It is
    // removed before the automated soft exit and again on every teardown path.
    private static ModernAtlasSystem? smokeGodModeOwner;

    private ModernAtlasDialog? dialog;
    private ICoreClientAPI? clientApi;
    private ICoreServerAPI? serverApi;
    private ModernAtlasConfig? config;
    private ModernAtlasServerConfig? serverConfig;
    private readonly ModernAtlasServerPolicy serverPolicy = new();
    private IServerNetworkChannel? serverPolicyChannel;
    private IClientNetworkChannel? clientPolicyChannel;
    private IShaderProgram? stableLiquidShader;
    private IShaderProgram? atlasCloudShader;
    private IShaderProgram? atlasBoundaryShader;
    private IShaderProgram? atlasScreenshotFilterShader;
    private IShaderProgram? atlasOpacityShader;
    private IShaderProgram? atlasScrollShader;
    private CheatModeConsentDialog? cheatModeDialog;
    private AtlasOpeningTransitionDialog? openingTransition;
    private AtlasOrdinaryWorldScreenshotRenderer? ordinaryWorldScreenshotRenderer;
    private AtlasSoundController? soundController;
    private string? activeWorldIdentifier;
    private int worldSessionGeneration;
    private bool automatedWorldExitRequested;
    private bool automatedSmokeTestOpeningStarted;
    private int automatedSmokeAtlasCycle;
    private bool automatedSmokeAllCyclesPassed = true;
    private bool automatedSmokeCycleFinishing;
    private bool automatedSmokeOriginalCheatMode;
    private Harmony? smokeGodModeHarmony;
    private bool smokeGodModeEnabled;
    private long smokeGodModeEntityId;
    private string? smokeGodModePlayerUid;
    private bool suppressLocalHandActions;
    private long handActionSuppressionListenerId = -1;

    private static readonly string[] LocalHandActionAnimationCodes =
    {
        "breakhand",
        "breakhand-fp",
        "breaktool",
        "breaktool-fp",
        "helditemattack",
        "helditeminteract",
        "eat",
        "eat-fp",
        "drink",
        "drink-fp"
    };

    public override bool ShouldLoad(EnumAppSide side) => true;

    public override void StartServerSide(ICoreServerAPI api)
    {
        serverApi = api;
        serverConfig = api.LoadModConfig<ModernAtlasServerConfig>(ServerConfigFileName)
            ?? new ModernAtlasServerConfig();
        api.StoreModConfig(serverConfig, ServerConfigFileName);

        serverPolicyChannel = api.Network
            .RegisterChannel(PolicyChannelName)
            .RegisterMessageType<ModernAtlasServerPolicy>()
            .RegisterMessageType<AtlasScrollAnimationMessage>()
            .RegisterMessageType<AtlasCheatModeMessage>()
            .SetMessageHandler<AtlasScrollAnimationMessage>(
                OnServerScrollAnimation
            );
        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;

        api.ChatCommands
            .Create("ma")
            .WithDescription("ModernAtlas server controls")
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("cheat")
                .WithDescription("ModernAtlas Cheat Mode controls")
                .RequiresPrivilege(Privilege.chat)
                .BeginSubCommand("mode")
                    .WithDescription("Enable or disable ModernAtlas Cheat Mode")
                    .RequiresPlayer()
                    .RequiresPrivilege(Privilege.chat)
                    .WithArgs(api.ChatCommands.Parsers.Word("state"))
                    .HandleWith(OnServerCheatModeCommand)
                .EndSubCommand()
            .EndSubCommand();

        ModernAtlasServerPolicy policy = serverConfig.ToPolicy();
        api.Logger.Notification(
            "[ModernAtlas] Server policy loaded: Cheat Mode allowed={0}; live 3D models players={1}, animals={2}, mobs={3}, npcs={4}.",
            policy.CheatModeAllowed,
            policy.ShowPlayers,
            policy.ShowAnimals,
            policy.ShowMobs,
            policy.ShowNpcs
        );
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        clientApi = api;
        config = api.LoadModConfig<ModernAtlasConfig>(ConfigFileName) ?? new ModernAtlasConfig();
        SaveConfig();
        serverPolicy.ResetToSafeDefaults();

        clientPolicyChannel = api.Network
            .RegisterChannel(PolicyChannelName)
            .RegisterMessageType<ModernAtlasServerPolicy>()
            .RegisterMessageType<AtlasScrollAnimationMessage>()
            .RegisterMessageType<AtlasCheatModeMessage>()
            .SetMessageHandler<ModernAtlasServerPolicy>(OnServerPolicyReceived)
            .SetMessageHandler<AtlasScrollAnimationMessage>(
                OnRemoteScrollAnimation
            )
            .SetMessageHandler<AtlasCheatModeMessage>(OnCheatModeMessage);
        api.Event.LeaveWorld += OnLeaveWorld;
        api.Event.LevelFinalize += OnLevelFinalize;
        handActionSuppressionListenerId = api.Event.RegisterGameTickListener(
            MaintainLocalHandActionSuppression,
            10
        );

        if (GetStableLiquidShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the stable liquid shader.");
        }
        if (GetAtlasCloudShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the atlas cloud shader.");
        }
        if (GetAtlasBoundaryShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the final boundary shader.");
        }
        if (GetAtlasScreenshotFilterShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the screenshot filter shader.");
        }
        if (GetAtlasOpacityShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the window opacity shader.");
        }
        if (GetAtlasScrollShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the physical scroll shader.");
        }

        soundController = new AtlasSoundController(api);
        dialog = new ModernAtlasDialog(
            api,
            config,
            serverPolicy,
            SaveConfig,
            RequestCloseAtlas,
            RequestEmergencyCloseAtlas,
            GetStableLiquidShader,
            GetAtlasCloudShader,
            GetAtlasBoundaryShader,
            GetAtlasScreenshotFilterShader,
            GetAtlasOpacityShader,
            GetAtlasScrollShader,
            soundController
        );
        openingTransition = new AtlasOpeningTransitionDialog(
            api,
            dialog.CaptureNormalWorldSnapshotBeforeTransition,
            dialog.PrepareOpeningTransitionFrame,
            GetAtlasScrollShader,
            PublishScrollAnimationPhase,
            soundController
        );
        ordinaryWorldScreenshotRenderer = new AtlasOrdinaryWorldScreenshotRenderer(
            dialog.CaptureAutomatedOrdinaryWorldScreenshot,
            openingTransition.ShouldRefreshOrdinaryWorldSnapshot,
            openingTransition.TryRefreshOrdinaryWorldSnapshot
        );
        api.Event.RegisterRenderer(
            openingTransition,
            EnumRenderStage.Opaque,
            "modernatlas-opening-scroll"
        );
        api.Event.RegisterRenderer(
            ordinaryWorldScreenshotRenderer,
            EnumRenderStage.AfterBlit,
            "modernatlas-ordinary-world-smoke-screenshot"
        );

        api.Input.RegisterHotKey(
            "modernatlas-open",
            Lang.Get("modernatlas:hotkey-open-map"),
            GlKeys.G,
            HotkeyType.HelpAndOverlays
        );
        api.Input.SetHotKeyHandler("modernatlas-open", OnOpenMap);

        api.Logger.Notification(
            "[ModernAtlas] Registered independent 3D atlas GUI. Vanilla map data remains unchanged."
        );
    }

    private bool OnOpenMap(KeyCombination keyCombination)
    {
        // The automated path owns the complete open/close sequence. A real G
        // event can still arrive from the desktop or a stale hotkey while the
        // world is settling; allowing it here races the scheduled transition
        // and opens the atlas once without animation and once with it.
        if (AutomatedSmokeTestEnabled)
        {
            return true;
        }

        if (cheatModeDialog?.IsOpened() == true)
        {
            cheatModeDialog.Focus();
            return true;
        }

        if (openingTransition?.IsOpened() == true)
        {
            // G closes every atlas-owned presentation. During the opening
            // scene it acts like Escape and releases the transition input
            // layer instead of leaving the player trapped behind it.
            openingTransition.CancelWithoutOpening();
            return true;
        }

        // HelpAndOverlays hotkeys can be evaluated independently from dialog
        // key events. A focused atlas search box owns G as text, not as the
        // open/close command.
        if (dialog?.SearchInputHasFocus == true)
        {
            return true;
        }

        if (dialog?.IsOpened() == true)
        {
            RequestCloseAtlas();
            return true;
        }

        suppressLocalHandActions = true;
        CancelLocalPlayerHandActions(true);
        StartOpeningTransition();
        return true;
    }

    private bool CancelLocalPlayerHandActions(bool resetAllAnimations)
    {
        if (clientApi?.World.Player?.Entity is not EntityPlayer player)
        {
            return false;
        }

        try
        {
            ReleaseInWorldMouseButtons();

            EntityControls controls = player.Controls;
            bool actionWasActive = controls.LeftMouseDown
                || controls.RightMouseDown
                || controls.HandUse != EnumHandInteract.None;

            // Release the world controls before asking the held item or block
            // to cancel. Otherwise a physically held or rapidly clicked mouse
            // button can restart the same action on the following client tick.
            controls.LeftMouseDown = false;
            controls.RightMouseDown = false;
            player.TryStopHandAction(
                true,
                EnumItemUseCancelReason.ReleasedMouse
            );
            controls.HandUse = EnumHandInteract.None;
            controls.HandUsingBlockSel = null;
            controls.Dirty = true;

            // Cancellation stops gameplay use, while a strike or eating clip
            // may still have time left. Remove those captured arm poses before
            // the atlas renders the local player's third-person model.
            if (resetAllAnimations)
            {
                player.SelfFpAnimManager.StopAllAnimations();
                player.TpAnimManager.StopAllAnimations();
            }
            else
            {
                StopLocalHandActionAnimations(player);
            }

            if (actionWasActive)
            {
                clientApi.Logger.Debug(
                    "[ModernAtlas] Cancelled the local player's active hand action before opening the atlas."
                );
            }
            return !controls.LeftMouseDown
                && !controls.RightMouseDown
                && controls.HandUse == EnumHandInteract.None;
        }
        catch (Exception exception)
        {
            clientApi.Logger.Warning(
                "[ModernAtlas] Could not cancel the local player's hand action before opening the atlas: {0}",
                exception.Message
            );
            return false;
        }
    }

    private void MaintainLocalHandActionSuppression(float deltaTime)
    {
        _ = deltaTime;
        if (!suppressLocalHandActions || clientApi == null) return;

        bool atlasOwnsInput = dialog?.IsOpened() == true
            || openingTransition?.IsOpened() == true;
        bool physicalMouseHeld = IsPhysicalMouseButtonDown();
        if (!atlasOwnsInput && !physicalMouseHeld)
        {
            suppressLocalHandActions = false;
            return;
        }

        CancelLocalPlayerHandActions(false);
    }

    private void ReleaseInWorldMouseButtons()
    {
        MouseButtonState? states = clientApi?.Input.InWorldMouseButton;
        if (states == null) return;
        states.Left = false;
        states.Right = false;
    }

    private bool IsPhysicalMouseButtonDown()
    {
        MouseButtonState? states = clientApi?.Input.MouseButton;
        return states?.Left == true || states?.Right == true;
    }

    internal static void StopLocalHandActionAnimations(EntityPlayer player)
    {
        foreach (string code in LocalHandActionAnimationCodes)
        {
            player.SelfFpAnimManager.StopAnimation(code);
            player.TpAnimManager.StopAnimation(code);
        }
    }

    private bool BeginAutomatedHandActionFixture()
    {
        if (clientApi?.World.Player?.Entity is not EntityPlayer player)
        {
            return false;
        }

        MouseButtonState? inWorldMouse = clientApi.Input.InWorldMouseButton;
        if (inWorldMouse == null) return false;

        inWorldMouse.Left = true;
        player.Controls.LeftMouseDown = true;
        player.Controls.HandUse = EnumHandInteract.HeldItemAttack;
        bool thirdPersonStarted = player.TpAnimManager.StartAnimation(
            "breakhand"
        );
        bool firstPersonStarted = player.SelfFpAnimManager.StartAnimation(
            "breakhand-fp"
        );
        return thirdPersonStarted && firstPersonStarted;
    }

    private static bool LocalHandActionAnimationsStopped(EntityPlayer player)
    {
        foreach (string code in LocalHandActionAnimationCodes)
        {
            if (player.SelfFpAnimManager.ActiveAnimationsByAnimCode.ContainsKey(
                    code
                )
                || player.TpAnimManager.ActiveAnimationsByAnimCode.ContainsKey(
                    code
                ))
            {
                return false;
            }
        }
        return true;
    }

    private bool RequestCloseAtlas()
    {
        return RequestCloseAtlas(null);
    }

    private bool RequestCloseAtlas(Action<bool>? onCompleted)
    {
        if (dialog?.IsOpened() != true) return false;

        // Freeze the last complete ordinary-world snapshot before closing the
        // atlas GUI.  The first AfterBlit callback after TryClose can observe
        // a transient handoff frame; that frame must never replace the
        // closing backdrop with a dark/partially restored scene.
        openingTransition?.LockOrdinaryWorldSnapshotForClosing();
        if (!dialog.TryClose())
        {
            openingTransition?.UnlockOrdinaryWorldSnapshot();
            return false;
        }
        if (ShouldSkipScrollTransitions())
        {
            openingTransition?.UnlockOrdinaryWorldSnapshot();
            onCompleted?.Invoke(true);
            return true;
        }
        if (openingTransition == null
            || !openingTransition.BeginClosing(
                false,
                passed =>
                {
                    openingTransition?.UnlockOrdinaryWorldSnapshot();
                    onCompleted?.Invoke(passed);
                }
            ))
        {
            openingTransition?.UnlockOrdinaryWorldSnapshot();
            clientApi?.Logger.Warning(
                "[ModernAtlas] The scroll stowing transition was unavailable."
            );
            onCompleted?.Invoke(false);
        }
        return true;
    }

    /// <summary>
    /// Safety close used by the atlas damage warning. It closes the atlas
    /// without the scroll stowing transition so the player regains control in
    /// the same frame, and releases the transition state that a normal close
    /// would have handed over.
    /// </summary>
    private bool RequestEmergencyCloseAtlas()
    {
        if (dialog?.IsOpened() != true) return false;

        bool closed = dialog.TryClose();
        // CancelWithoutOpening also unlocks the ordinary-world snapshot, so the
        // backdrop resumes refreshing after the emergency close.
        openingTransition?.CancelWithoutOpening();
        if (!closed)
        {
            clientApi?.Logger.Error(
                "[ModernAtlas] The atlas could not be closed after damage was detected."
            );
        }
        return closed;
    }

    private void StartOpeningTransition()
    {
        if (dialog == null || openingTransition == null) return;

        if (ShouldSkipScrollTransitions())
        {
            openingTransition.LockOrdinaryWorldSnapshot();
            if (!dialog.TryOpen())
            {
                openingTransition?.UnlockOrdinaryWorldSnapshot();
                clientApi?.Logger.Error(
                    "[ModernAtlas] The atlas could not be opened while its opening animation was skipped."
                );
            }
            return;
        }

        bool started = openingTransition.Begin(
            false,
            passed =>
            {
                if (!passed)
                {
                    openingTransition?.UnlockOrdinaryWorldSnapshot();
                    clientApi?.Logger.Error(
                        "[ModernAtlas] The atlas transition did not release its input layer; atlas activation was cancelled."
                    );
                    return;
                }
                if (openingTransition?.IsOpened() == true)
                {
                    openingTransition.CancelWithoutOpening();
                    clientApi?.Logger.Error(
                        "[ModernAtlas] Refusing to open the atlas while the completed transition dialog is still active."
                    );
                    return;
                }
                dialog.PrepareForTransitionHandoff();
                if (dialog.TryOpen()) return;
                openingTransition?.UnlockOrdinaryWorldSnapshot();
                clientApi?.Logger.Error(
                    "[ModernAtlas] The atlas could not be opened after its transition."
                );
            }
        );
        if (!started)
        {
            clientApi?.Logger.Warning(
                "[ModernAtlas] The opening transition was unavailable; opening the atlas directly."
            );
            openingTransition.LockOrdinaryWorldSnapshot();
            if (!dialog.TryOpen())
            {
                openingTransition.UnlockOrdinaryWorldSnapshot();
            }
            return;
        }

    }

    private bool ShouldSkipScrollTransitions()
    {
        if (config?.SkipOpeningAnimation == true) return true;
        try
        {
            return clientApi?.IsSinglePlayer == true
                && clientApi.World.Player?.WorldData.CurrentGameMode
                    == EnumGameMode.Creative;
        }
        catch
        {
            return false;
        }
    }

    public override void Dispose()
    {
        DisableAutomatedSmokeGodMode();
        if (clientApi != null)
        {
            clientApi.Event.LeaveWorld -= OnLeaveWorld;
            clientApi.Event.LevelFinalize -= OnLevelFinalize;
            if (handActionSuppressionListenerId >= 0)
            {
                clientApi.Event.UnregisterGameTickListener(
                    handActionSuppressionListenerId
                );
            }
        }
        if (serverApi != null)
        {
            serverApi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
        }
        if (clientApi != null && openingTransition != null)
        {
            clientApi.Event.UnregisterRenderer(
                openingTransition,
                EnumRenderStage.Opaque
            );
        }
        if (clientApi != null && ordinaryWorldScreenshotRenderer != null)
        {
            clientApi.Event.UnregisterRenderer(
                ordinaryWorldScreenshotRenderer,
                EnumRenderStage.AfterBlit
            );
        }
        openingTransition?.CancelWithoutOpening();
        openingTransition?.Dispose();
        openingTransition = null;
        ordinaryWorldScreenshotRenderer = null;
        dialog?.Dispose();
        dialog = null;
        soundController?.Dispose();
        soundController = null;
        cheatModeDialog?.CancelWithoutDecision();
        cheatModeDialog?.Dispose();
        cheatModeDialog = null;
        stableLiquidShader = null;
        atlasCloudShader = null;
        atlasBoundaryShader = null;
        atlasScreenshotFilterShader = null;
        atlasOpacityShader = null;
        atlasScrollShader = null;
        clientApi = null;
        serverApi = null;
        config = null;
        serverConfig = null;
        serverPolicyChannel = null;
        clientPolicyChannel = null;
        activeWorldIdentifier = null;
        suppressLocalHandActions = false;
        handActionSuppressionListenerId = -1;
        base.Dispose();
    }

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        if (serverConfig != null)
        {
            serverPolicyChannel?.SendPacket(serverConfig.ToPolicy(), player);
        }
    }

    private void OnServerPolicyReceived(ModernAtlasServerPolicy policy)
    {
        serverPolicy.CopyFrom(policy);
        dialog?.OnServerPolicyChanged();
        clientApi?.Logger.Notification(
            "[ModernAtlas] Applied server policy: Cheat Mode allowed={0}; live 3D models players={1}, animals={2}, mobs={3}, npcs={4}.",
            policy.CheatModeAllowed,
            policy.ShowPlayers,
            policy.ShowAnimals,
            policy.ShowMobs,
            policy.ShowNpcs
        );
    }

    private TextCommandResult OnServerCheatModeCommand(TextCommandCallingArgs args)
    {
        if (serverApi == null || serverConfig == null
            || args.Caller.Player is not IServerPlayer player)
        {
            return TextCommandResult.Error("ModernAtlas requires a player caller.");
        }

        string state = (args[0] as string ?? "").Trim().ToLowerInvariant();
        if (state is not ("on" or "off"))
        {
            return TextCommandResult.Error("Use /ma cheat mode on or /ma cheat mode off.");
        }

        bool enabled = state == "on";
        if (enabled && serverApi.Server.IsDedicated && !serverConfig.CheatModeAllowed)
        {
            return TextCommandResult.Error(
                "The server owner has disabled ModernAtlas Cheat Mode."
            );
        }

        serverPolicyChannel?.SendPacket(
            new AtlasCheatModeMessage { Enabled = enabled },
            player
        );
        bool creativeStillGrantsAccess = !enabled
            && !serverApi.Server.IsDedicated
            && player.WorldData.CurrentGameMode == EnumGameMode.Creative;
        return TextCommandResult.Success(
            enabled
                ? "ModernAtlas Cheat Mode is on."
                : creativeStillGrantsAccess
                    ? "ModernAtlas Cheat Mode is off. Creative mode still provides Creative/Cheat controls."
                    : "ModernAtlas Cheat Mode is off."
        );
    }

    private void OnCheatModeMessage(AtlasCheatModeMessage message)
    {
        if (clientApi == null || dialog == null || activeWorldIdentifier == null)
        {
            return;
        }

        if (!clientApi.IsSinglePlayer
            && message.Enabled
            && !serverPolicy.CheatModeAllowed)
        {
            clientApi.Logger.Warning(
                "[ModernAtlas] Ignored a Cheat Mode command that conflicts with the server policy."
            );
            return;
        }

        if (clientApi.IsSinglePlayer && config != null && !AutomatedSmokeTestEnabled)
        {
            config.CheatModeByWorld[activeWorldIdentifier] = message.Enabled;
            SaveConfig();
        }
        dialog.SetCheatMode(message.Enabled);
        clientApi.Logger.Notification(
            "[ModernAtlas] Cheat Mode command applied: {0}.",
            message.Enabled ? "on" : "off"
        );
    }

    private void PublishScrollAnimationPhase(AtlasScrollPhase phase)
    {
        if (clientPolicyChannel?.Connected != true || clientApi?.IsSinglePlayer == true)
        {
            return;
        }

        clientPolicyChannel.SendPacket(new AtlasScrollAnimationMessage
        {
            Phase = phase
        });
    }

    private void OnServerScrollAnimation(
        IServerPlayer player,
        AtlasScrollAnimationMessage message
    )
    {
        if (!Enum.IsDefined(message.Phase)) return;

        message.PlayerUid = player.PlayerUID;
        serverPolicyChannel?.BroadcastPacket(message, player);
    }

    private void OnRemoteScrollAnimation(AtlasScrollAnimationMessage message)
    {
        if (clientApi == null || string.IsNullOrWhiteSpace(message.PlayerUid)) return;

        openingTransition?.SetRemoteAnimation(message.PlayerUid, message.Phase);

        foreach (var entry in clientApi.World.LoadedEntities)
        {
            if (entry.Value is not EntityPlayer player
                || player.PlayerUID != message.PlayerUid)
            {
                continue;
            }

            StopRemoteScrollAnimations(player);
            string? sourceCode = message.Phase switch
            {
                AtlasScrollPhase.Retrieve or AtlasScrollPhase.Stow =>
                    "holdinglanternlefthand",
                AtlasScrollPhase.Handoff or AtlasScrollPhase.ReleaseRight =>
                    "twohandplaceblock",
                AtlasScrollPhase.HoldOpen or AtlasScrollPhase.RollClosed =>
                    "holdbothhandslarge",
                _ => null
            };
            if (sourceCode != null
                && player.Properties.Client.AnimationsByMetaCode.TryGetValue(
                    sourceCode,
                    out AnimationMetaData? source
                ))
            {
                AnimationMetaData animation = source.Clone();
                animation.Code = $"modernatlas-remote-{message.Phase}";
                animation.ClientSide = true;
                animation.AnimationSpeed *= AtlasOpeningTransitionDialog.TransitionSpeed;
                animation.EaseInSpeed = Math.Max(animation.EaseInSpeed, 8f);
                float easeOutSpeed = message.Phase == AtlasScrollPhase.Stow
                    ? 1000f
                    : 8f;
                animation.EaseOutSpeed = Math.Max(
                    animation.EaseOutSpeed,
                    easeOutSpeed
                );
                animation.Init();
                player.TpAnimManager.StartAnimation(animation);
            }
            break;
        }
    }

    private static void StopRemoteScrollAnimations(
        EntityPlayer player
    )
    {
        foreach (AtlasScrollPhase phase in Enum.GetValues<AtlasScrollPhase>())
        {
            player.TpAnimManager.StopAnimation($"modernatlas-remote-{phase}");
        }
    }

    private void OnLeaveWorld()
    {
        bool completeAutomatedWorldExit = automatedWorldExitRequested;
        automatedWorldExitRequested = false;
        DisableAutomatedSmokeGodMode();
        clientApi?.Logger.Notification(
            "[ModernAtlas] World leave received; releasing atlas state without activating engine shaders."
        );
        worldSessionGeneration++;
        cheatModeDialog?.CancelWithoutDecision();
        cheatModeDialog?.Dispose();
        cheatModeDialog = null;
        openingTransition?.CancelWithoutOpening();
        openingTransition?.ClearRemoteAnimations();
        soundController?.StopAll();
        activeWorldIdentifier = null;
        automatedSmokeTestOpeningStarted = false;
        automatedSmokeAtlasCycle = 0;
        automatedSmokeAllCyclesPassed = true;
        automatedSmokeCycleFinishing = false;
        suppressLocalHandActions = false;
        dialog?.OnWorldLeave();
        serverPolicy.ResetToSafeDefaults();
        if (completeAutomatedWorldExit)
        {
            clientApi?.Logger.Notification(
                "[ModernAtlas] AUTOMATED WORLD-EXIT CHECK PASSED: the atlas released its world resources without a crash."
            );
        }
    }

    private void OnLevelFinalize()
    {
        if (clientApi == null || config == null) return;

        // A previous session may have ended before its normal completion
        // callback. Remove its test-only damage guard before binding the new
        // world/player identity.
        DisableAutomatedSmokeGodMode();

        string worldIdentifier = clientApi.World.SavegameIdentifier;
        if (string.IsNullOrWhiteSpace(worldIdentifier)) return;

        int sessionGeneration = ++worldSessionGeneration;
        activeWorldIdentifier = worldIdentifier;
        automatedSmokeTestOpeningStarted = false;
        automatedSmokeAtlasCycle = 0;
        automatedSmokeAllCyclesPassed = true;
        automatedSmokeCycleFinishing = false;
        cheatModeDialog?.CancelWithoutDecision();
        cheatModeDialog?.Dispose();
        cheatModeDialog = null;

        // Multiplayer always begins in the safe mode. Only the server command
        // and its authoritative policy packet may unlock Cheat Mode later.
        if (!clientApi.IsSinglePlayer)
        {
            dialog?.SetCheatMode(false);
            if (AutomatedSmokeTestEnabled)
            {
                clientApi.Logger.Warning(
                    "[ModernAtlas] Automated smoke test skipped because it is restricted to singleplayer."
                );
            }
            return;
        }

        if (config.CheatModeByWorld.TryGetValue(worldIdentifier, out bool enabled))
        {
            dialog?.SetCheatMode(enabled);
            clientApi.Logger.Notification(
                "[ModernAtlas] Restored the saved spoiler mode for this world: {0}.",
                enabled ? "Cheat Mode enabled" : "caves hidden"
            );
        }

        else
        {
            dialog?.SetCheatMode(false);
            if (!AutomatedSmokeTestEnabled)
            {
                clientApi.Event.RegisterCallback(
                    _ => OpenCheatModeConsent(worldIdentifier),
                    350
                );
            }
        }

        automatedSmokeOriginalCheatMode = dialog?.CheatModeEnabledForAutomation == true;

        if (AutomatedSmokeTestEnabled)
        {
            EnableAutomatedSmokeGodMode();
            ScheduleAutomatedCheatCommandTest(worldIdentifier, sessionGeneration);
        }
    }

    private bool AutomatedSmokeTestEnabled => string.Equals(
        Environment.GetEnvironmentVariable(SmokeTestEnvironmentVariable),
        "1",
        StringComparison.Ordinal
    );

    private bool EnableAutomatedSmokeGodMode()
    {
        if (!AutomatedSmokeTestEnabled || smokeGodModeEnabled)
        {
            return smokeGodModeEnabled;
        }

        EntityPlayer? player = clientApi?.World.Player?.Entity;
        if (player == null)
        {
            clientApi?.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED: the local player was unavailable while enabling smoke-test damage protection."
            );
            return false;
        }

        try
        {
            List<MethodInfo> targets = new();
            // ReceiveDamage is overridden through several engine entity
            // layers.  The public contract guarantees that it calls the
            // concrete player's ShouldReceiveDamage virtual, so patch that
            // player implementation only.  This avoids touching the base
            // world entity method and keeps the guard local to the smoke
            // player's server/client entity.
            AddSmokeDamageTarget(
                targets,
                player.GetType(),
                nameof(Entity.ShouldReceiveDamage)
            );
            AddSmokeDamageTarget(
                targets,
                typeof(EntityPlayer),
                nameof(Entity.ShouldReceiveDamage)
            );

            if (targets.Count == 0)
            {
                throw new MissingMethodException(
                    typeof(Entity).FullName,
                    nameof(Entity.ShouldReceiveDamage)
                );
            }

            MethodInfo prefix = typeof(ModernAtlasSystem).GetMethod(
                nameof(SmokeGodModeDamagePrefix),
                BindingFlags.Static | BindingFlags.NonPublic
            ) ?? throw new MissingMethodException(
                typeof(ModernAtlasSystem).FullName,
                nameof(SmokeGodModeDamagePrefix)
            );

            Harmony harmony = new(SmokeGodModePatchId);
            foreach (MethodInfo target in targets)
            {
                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(prefix)
                );
            }

            smokeGodModeEntityId = player.EntityId;
            smokeGodModePlayerUid = player.PlayerUID;
            smokeGodModeHarmony = harmony;
            smokeGodModeOwner = this;
            smokeGodModeEnabled = true;
            clientApi?.Logger.Notification(
                "[ModernAtlas] AUTOMATED SMOKE GOD MODE ENABLED: damage is blocked only for the local smoke-test player; Vintage Story game mode and world data are unchanged."
            );
            return true;
        }
        catch (Exception exception)
        {
            try
            {
                new Harmony(SmokeGodModePatchId).UnpatchAll(SmokeGodModePatchId);
            }
            catch
            {
                // Keep the original setup error as the useful diagnostic.
            }
            smokeGodModeHarmony = null;
            smokeGodModeOwner = null;
            smokeGodModeEnabled = false;
            smokeGodModeEntityId = 0;
            smokeGodModePlayerUid = null;
            clientApi?.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED: could not install the local-player damage guard: {0}",
                exception.Message
            );
            return false;
        }
    }

    private static void AddSmokeDamageTarget(
        List<MethodInfo> targets,
        Type ownerType,
        string methodName
    )
    {
        MethodInfo? method = ownerType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(DamageSource), typeof(float) },
            null
        );
        // Harmony must receive a concrete method body.  Some engine types
        // expose only a virtual declaration, so ignore those declarations.
        bool alreadyAdded = method != null && targets.Exists(
            existing => existing.Module == method.Module
                && existing.MetadataToken == method.MetadataToken
        );
        if (method != null && !method.IsAbstract && !alreadyAdded)
        {
            targets.Add(method);
        }
    }

    private static bool SmokeGodModeDamagePrefix(
        Entity __instance,
        ref bool __result
    )
    {
        ModernAtlasSystem? owner = smokeGodModeOwner;
        if (owner == null
            || !owner.smokeGodModeEnabled
            || !owner.IsSmokeGodModePlayer(__instance))
        {
            return true;
        }

        __result = false;
        return false;
    }

    private bool IsSmokeGodModePlayer(Entity entity)
    {
        if (entity is not EntityPlayer player) return false;
        if (!string.IsNullOrWhiteSpace(smokeGodModePlayerUid)
            && string.Equals(
                player.PlayerUID,
                smokeGodModePlayerUid,
                StringComparison.Ordinal
            ))
        {
            return true;
        }
        return smokeGodModeEntityId != 0
            && player.EntityId == smokeGodModeEntityId;
    }

    private void DisableAutomatedSmokeGodMode()
    {
        bool owned = ReferenceEquals(smokeGodModeOwner, this);
        if (!owned && smokeGodModeHarmony == null && !smokeGodModeEnabled)
        {
            return;
        }

        smokeGodModeEnabled = false;
        if (owned) smokeGodModeOwner = null;
        try
        {
            smokeGodModeHarmony?.UnpatchAll(SmokeGodModePatchId);
        }
        catch (Exception exception)
        {
            clientApi?.Logger.Warning(
                "[ModernAtlas] Could not remove the smoke-test damage guard during teardown: {0}",
                exception.Message
            );
        }
        finally
        {
            smokeGodModeHarmony = null;
            smokeGodModeEntityId = 0;
            smokeGodModePlayerUid = null;
        }
        clientApi?.Logger.Notification(
            "[ModernAtlas] AUTOMATED SMOKE GOD MODE RESTORED: the temporary local-player damage guard is disabled."
        );
    }

    private void ScheduleAutomatedCheatCommandTest(
        string worldIdentifier,
        int sessionGeneration
    )
    {
        clientApi?.Event.RegisterCallback(
            _ =>
            {
                if (!IsCurrentAutomatedWorld(worldIdentifier, sessionGeneration)) return;

                clientApi!.SendChatMessage("/ma cheat mode on", "");
                clientApi.Event.RegisterCallback(
                    _ => CompleteAutomatedCheatModeOn(
                        worldIdentifier,
                        sessionGeneration
                    ),
                    500
                );
            },
            500
        );
    }

    private void CompleteAutomatedCheatModeOn(
        string worldIdentifier,
        int sessionGeneration
    )
    {
        if (!IsCurrentAutomatedWorld(worldIdentifier, sessionGeneration)) return;

        bool enabled = dialog!.CheatModeEnabledForAutomation;
        clientApi!.SendChatMessage("/ma cheat mode off", "");
        clientApi.Event.RegisterCallback(
            _ =>
            {
                if (!IsCurrentAutomatedWorld(worldIdentifier, sessionGeneration)) return;

                bool disabled = !dialog!.CheatModeEnabledForAutomation;
                if (enabled && disabled)
                {
                    clientApi!.Logger.Notification(
                        "[ModernAtlas] AUTOMATED CHEAT COMMAND CHECK PASSED: /ma cheat mode on and off were executed through the server command path."
                    );
                }
                else
                {
                    clientApi!.Logger.Error(
                        "[ModernAtlas] AUTOMATED SMOKE TEST FAILED: /ma cheat mode command check returned on={0}, off={1}.",
                        enabled,
                        disabled
                    );
                }
                // Exercise the command path above, then enable only the
                // atlas-owned disclosure switch for the remaining checks.
                // Never change Vintage Story's player game mode: an aborted
                // smoke run must not leave persistent Creative state in the
                // named standard test world.
                dialog!.SetCheatMode(true);
                ScheduleAutomatedSmokeTest(worldIdentifier, sessionGeneration);
            },
            500
        );
    }

    private bool IsCurrentAutomatedWorld(
        string worldIdentifier,
        int sessionGeneration
    ) => clientApi != null
        && dialog != null
        && clientApi.IsSinglePlayer
        && activeWorldIdentifier == worldIdentifier
        && worldSessionGeneration == sessionGeneration;

    private void ScheduleAutomatedSmokeTest(string worldIdentifier, int sessionGeneration)
    {
        clientApi?.Logger.Notification(
            "[ModernAtlas] Automated smoke test scheduled 7.5 seconds after world load so initial chunk meshes can finish streaming."
        );
        clientApi?.Event.RegisterCallback(
            _ => OpenAtlasForAutomatedSmokeTest(worldIdentifier, sessionGeneration),
            7500
        );
    }

    private void OpenAtlasForAutomatedSmokeTest(
        string worldIdentifier,
        int sessionGeneration
    )
    {
        if (clientApi == null
            || dialog == null
            || openingTransition == null
            || !clientApi.IsSinglePlayer
            || activeWorldIdentifier != worldIdentifier
            || worldSessionGeneration != sessionGeneration)
        {
            return;
        }

        if (automatedSmokeTestOpeningStarted) return;
        automatedSmokeTestOpeningStarted = true;
        if (automatedSmokeAtlasCycle <= 0) automatedSmokeAtlasCycle = 1;
        if (automatedSmokeAtlasCycle > 2)
        {
            automatedSmokeTestOpeningStarted = false;
            return;
        }

        if (automatedSmokeAtlasCycle == 1)
        {
            if (ordinaryWorldScreenshotRenderer == null
                || !ordinaryWorldScreenshotRenderer.Queue(
                    "ordinary-before-atlas",
                    passed => clientApi?.Event.RegisterCallback(
                        _ => BeginAutomatedAtlasOpen(
                            worldIdentifier,
                            sessionGeneration,
                            passed
                        ),
                        0
                    )
                ))
            {
                clientApi.Logger.Error(
                    "[ModernAtlas] AUTOMATED SMOKE TEST FAILED: the ordinary-world baseline screenshot could not be queued before the atlas opened."
                );
                FinishAutomatedSmokeTest(worldIdentifier, sessionGeneration, false);
            }
            return;
        }
        BeginAutomatedAtlasOpen(worldIdentifier, sessionGeneration, true);
    }

    private void BeginAutomatedAtlasOpen(
        string worldIdentifier,
        int sessionGeneration,
        bool ordinaryBaselinePassed
    )
    {
        if (clientApi == null
            || dialog == null
            || openingTransition == null
            || !clientApi.IsSinglePlayer
            || activeWorldIdentifier != worldIdentifier
            || worldSessionGeneration != sessionGeneration)
        {
            return;
        }
        if (!ordinaryBaselinePassed)
        {
            clientApi.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED: the ordinary-world baseline screenshot could not be captured after a completed world frame."
            );
            FinishAutomatedSmokeTest(worldIdentifier, sessionGeneration, false);
            return;
        }
        clientApi.Logger.Notification(
            "[ModernAtlas] Automated atlas open/close cycle {0} of 2 is starting after the previous world-shader restore.",
            automatedSmokeAtlasCycle
        );

        // Recover from a direct atlas open that may have happened before the
        // scheduled smoke callback. The test must own exactly one opening
        // transition, never stack a second dialog over the first one.
        if (dialog.IsOpened()) dialog.TryClose();
        if (openingTransition.IsOpened()) openingTransition.CancelWithoutOpening();

        bool handActionFixtureStarted = BeginAutomatedHandActionFixture();
        suppressLocalHandActions = true;
        bool handActionsCancelled = CancelLocalPlayerHandActions(true);
        EntityPlayer? localPlayer = clientApi.World.Player?.Entity;
        if (!handActionFixtureStarted
            || !handActionsCancelled
            || localPlayer == null
            || !LocalHandActionAnimationsStopped(localPlayer))
        {
            clientApi.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED: a synthetic breakhand action was not fully cancelled before the atlas transition."
            );
            FinishAutomatedSmokeTest(worldIdentifier, sessionGeneration, false);
            return;
        }
        clientApi.Logger.Notification(
            "[ModernAtlas] AUTOMATED HAND-ACTION CANCELLATION CHECK PASSED: an active breakhand clip, attack use and block-interaction controls were neutralized before atlas capture."
        );

        dialog.BeginAutomatedSmokeTest(
            passed => FinishAutomatedSmokeTest(
                worldIdentifier,
                sessionGeneration,
                passed
            )
        );
        if (!openingTransition.Begin(
            true,
            passed => CompleteAutomatedOpeningTransition(
                worldIdentifier,
                sessionGeneration,
                passed
            )
        ))
        {
            clientApi.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED: the atlas opening transition could not be started."
            );
            FinishAutomatedSmokeTest(worldIdentifier, sessionGeneration, false);
            return;
        }

        clientApi.Logger.Notification(
            "[ModernAtlas] Automated smoke test started the atlas transition without keyboard input."
        );
    }

    private void CompleteAutomatedOpeningTransition(
        string worldIdentifier,
        int sessionGeneration,
        bool passed
    )
    {
        if (clientApi == null
            || dialog == null
            || activeWorldIdentifier != worldIdentifier
            || worldSessionGeneration != sessionGeneration)
        {
            return;
        }
        if (!passed || (!dialog.IsOpened() && !dialog.TryOpen()))
        {
            openingTransition?.UnlockOrdinaryWorldSnapshot();
            clientApi.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED: the opening transition or atlas open check failed."
            );
            FinishAutomatedSmokeTest(worldIdentifier, sessionGeneration, false);
            return;
        }

        clientApi.Logger.Notification(
            "[ModernAtlas] Automated opening transition entered atlas cycle {0} normally.",
            automatedSmokeAtlasCycle
        );
    }

    private void FinishAutomatedSmokeTest(
        string worldIdentifier,
        int sessionGeneration,
        bool passed
    )
    {
        if (clientApi == null
            || dialog == null
            || activeWorldIdentifier != worldIdentifier
            || worldSessionGeneration != sessionGeneration)
        {
            return;
        }

        if (automatedSmokeCycleFinishing) return;
        automatedSmokeCycleFinishing = true;
        int cycle = automatedSmokeAtlasCycle <= 0 ? 1 : automatedSmokeAtlasCycle;
        automatedSmokeAllCyclesPassed &= passed;

        if (passed)
        {
            clientApi.Logger.Notification(
                "[ModernAtlas] AUTOMATED ATLAS CHECKS PASSED (cycle {0}): exact terrain and requested atlas features rendered.",
                cycle
            );
        }
        else
        {
            clientApi.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED (cycle {0}): one or more atlas, presentation or screenshot checks did not finish before timeout.",
                cycle
            );
        }

        if (dialog.IsOpened())
        {
            openingTransition?.LockOrdinaryWorldSnapshotForClosing();
            if (openingTransition != null
                && dialog.TryClose()
                && openingTransition.BeginClosing(
                    true,
                    closePassed =>
                    {
                        openingTransition?.UnlockOrdinaryWorldSnapshot();
                        CompleteAutomatedAtlasClose(
                            worldIdentifier,
                            sessionGeneration,
                            closePassed
                        );
                    }
                ))
            {
                return;
            }
            openingTransition?.UnlockOrdinaryWorldSnapshot();
        }

        CompleteAutomatedAtlasClose(
            worldIdentifier,
            sessionGeneration,
            false
        );
    }

    private void CompleteAutomatedAtlasClose(
        string worldIdentifier,
        int sessionGeneration,
        bool closePassed
    )
    {
        if (clientApi == null
            || activeWorldIdentifier != worldIdentifier
            || worldSessionGeneration != sessionGeneration)
        {
            return;
        }

        automatedSmokeCycleFinishing = false;
        bool closeStatePassed = dialog?.AutomatedSmokeCloseStatePassed == true;
        automatedSmokeAllCyclesPassed &= closePassed && closeStatePassed;
        int cycle = automatedSmokeAtlasCycle <= 0 ? 1 : automatedSmokeAtlasCycle;
        if (!closePassed || !closeStatePassed)
        {
            clientApi.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED (cycle {0}): close transition passed={1}, atlas state restore passed={2}.",
                cycle,
                closePassed,
                closeStatePassed
            );
        }
        else
        {
            clientApi.Logger.Notification(
                "[ModernAtlas] AUTOMATED ATLAS OPEN/CLOSE CYCLE {0} PASSED: the atlas closed and released its per-draw world state.",
                cycle
            );
        }

        // Give the client two ordinary render-frame boundaries after the
        // closing transition. The screenshot must observe the world renderer,
        // not the transition or an atlas-owned framebuffer handoff.
        clientApi.Event.RegisterCallback(
            _ => clientApi?.Event.RegisterCallback(
                __ => CompleteAutomatedAtlasCloseAfterWorldFrames(
                    worldIdentifier,
                    sessionGeneration,
                    cycle
                ),
                100
            ),
            100
        );
    }

    private void CompleteAutomatedAtlasCloseAfterWorldFrames(
        string worldIdentifier,
        int sessionGeneration,
        int cycle
    )
    {
        if (clientApi == null
            || activeWorldIdentifier != worldIdentifier
            || worldSessionGeneration != sessionGeneration)
        {
            return;
        }

        if (ordinaryWorldScreenshotRenderer == null
            || !ordinaryWorldScreenshotRenderer.Queue(
                $"ordinary-after-cycle-{cycle}",
                passed => clientApi?.Event.RegisterCallback(
                    _ => CompleteAutomatedAtlasCloseAfterOrdinaryScreenshot(
                        worldIdentifier,
                        sessionGeneration,
                        cycle,
                        passed
                    ),
                    0
                )
            ))
        {
            automatedSmokeAllCyclesPassed = false;
            clientApi.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED (cycle {0}): ordinary-world screenshot could not be queued after two post-close frames.",
                cycle
            );
            CompleteAutomatedAtlasCloseAfterOrdinaryScreenshot(
                worldIdentifier,
                sessionGeneration,
                cycle,
                false
            );
        }
    }

    private void CompleteAutomatedAtlasCloseAfterOrdinaryScreenshot(
        string worldIdentifier,
        int sessionGeneration,
        int cycle,
        bool ordinaryFramePassed
    )
    {
        if (clientApi == null
            || activeWorldIdentifier != worldIdentifier
            || worldSessionGeneration != sessionGeneration)
        {
            return;
        }

        automatedSmokeAllCyclesPassed &= ordinaryFramePassed;
        if (!ordinaryFramePassed)
        {
            clientApi.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED (cycle {0}): ordinary-world screenshot after two post-close frames could not be captured.",
                cycle
            );
        }

        if (cycle < 2)
        {
            automatedSmokeAtlasCycle = cycle + 1;
            automatedSmokeTestOpeningStarted = false;
            clientApi.Logger.Notification(
                "[ModernAtlas] First atlas cycle completed; scheduling the second open/render/close cycle after a world frame boundary."
            );
            clientApi.Event.RegisterCallback(
                _ => OpenAtlasForAutomatedSmokeTest(
                    worldIdentifier,
                    sessionGeneration
                ),
                1500
            );
            return;
        }

        if (automatedSmokeAllCyclesPassed)
        {
            clientApi.Logger.Notification(
                "[ModernAtlas] AUTOMATED ATLAS TWO-CYCLE CHECK PASSED: the atlas opened, rendered, closed, reopened, rendered, and closed again without losing exact terrain."
            );
        }
        else
        {
            clientApi.Logger.Error(
                "[ModernAtlas] AUTOMATED ATLAS TWO-CYCLE CHECK FAILED: at least one open/render/close cycle did not complete cleanly."
            );
        }

        clientApi.Event.RegisterCallback(
            _ => RestoreAutomatedSmokeAccessThenExit(
                worldIdentifier,
                sessionGeneration
            ),
            1000
        );
    }

    private void RestoreAutomatedSmokeAccessThenExit(
        string worldIdentifier,
        int sessionGeneration
    )
    {
        if (!IsCurrentAutomatedWorld(worldIdentifier, sessionGeneration)) return;
        DisableAutomatedSmokeGodMode();
        dialog!.SetCheatMode(automatedSmokeOriginalCheatMode);
        clientApi!.Logger.Notification(
            "[ModernAtlas] Automated smoke test restored the atlas Cheat Mode state without changing the Vintage Story player game mode."
        );
        clientApi.Event.RegisterCallback(
            _ => ExitWorldForAutomatedSmokeTest(worldIdentifier, sessionGeneration),
            500
        );
    }

    private void ExitWorldForAutomatedSmokeTest(
        string worldIdentifier,
        int sessionGeneration
    )
    {
        if (clientApi == null
            || activeWorldIdentifier != worldIdentifier
            || worldSessionGeneration != sessionGeneration)
        {
            return;
        }

        try
        {
            const BindingFlags instanceFlags = BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic;
            const BindingFlags staticFlags = BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic;
            object game = clientApi.GetType().GetField("game", instanceFlags)?.GetValue(clientApi)
                ?? throw new InvalidOperationException("Client game instance is unavailable.");
            object platform = game.GetType().GetField(
                "Platform",
                instanceFlags
            )?.GetValue(game)
                ?? throw new InvalidOperationException("Client platform is unavailable.");
            MethodInfo windowExit = platform.GetType().GetMethod(
                "WindowExit",
                instanceFlags
            ) ?? throw new MissingMethodException(platform.GetType().FullName, "WindowExit");
            Type exitModeType = windowExit.GetParameters()[1].ParameterType;
            object softExit = Enum.Parse(exitModeType, "SoftExit");
            Type screenManagerType = game.GetType().Assembly.GetType(
                "Vintagestory.Client.ScreenManager"
            ) ?? throw new TypeLoadException("Vintage Story screen manager is unavailable.");
            MethodInfo enqueueCallback = screenManagerType.GetMethod(
                "EnqueueCallBack",
                staticFlags
            ) ?? throw new MissingMethodException(
                screenManagerType.FullName,
                "EnqueueCallBack"
            );

            clientApi.Logger.Notification(
                "[ModernAtlas] Automated smoke test scheduled a clean window-close soft exit between frames."
            );
            automatedWorldExitRequested = true;
            Action closeClient = () => windowExit.Invoke(
                platform,
                new[] { "ModernAtlas automated smoke test completed", softExit }
            );
            enqueueCallback.Invoke(
                null,
                new object[] { closeClient, 100, "modernatlas-smoke-window-close" }
            );
        }
        catch (Exception exception)
        {
            Exception cause = exception is TargetInvocationException { InnerException: not null }
                ? exception.InnerException
                : exception;
            clientApi.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED: the test could not leave the world: {0}",
                cause.Message
            );
        }
    }

    private void OpenCheatModeConsent(string worldIdentifier)
    {
        if (clientApi == null || config == null
            || !clientApi.IsSinglePlayer
            || activeWorldIdentifier != worldIdentifier
            || config.CheatModeByWorld.ContainsKey(worldIdentifier))
        {
            return;
        }

        cheatModeDialog = new CheatModeConsentDialog(
            clientApi,
            enabled => SaveCheatModeDecision(worldIdentifier, enabled)
        );
        cheatModeDialog.TryOpen();
    }

    private void SaveCheatModeDecision(string worldIdentifier, bool enabled)
    {
        if (clientApi == null || config == null
            || activeWorldIdentifier != worldIdentifier)
        {
            return;
        }

        config.CheatModeByWorld[worldIdentifier] = enabled;
        SaveConfig();
        dialog?.SetCheatMode(enabled);
        clientApi.Logger.Notification(
            "[ModernAtlas] Saved the spoiler decision for this world: {0}.",
            enabled ? "Cheat Mode enabled" : "underground caves hidden"
        );
    }

    private IShaderProgram? GetStableLiquidShader()
    {
        if (stableLiquidShader != null && !stableLiquidShader.Disposed)
        {
            return stableLiquidShader;
        }
        if (clientApi == null) return null;

        IShaderProgram program = clientApi.Shader.NewShaderProgram();
        program.AssetDomain = "modernatlas";
        clientApi.Shader.RegisterFileShaderProgram("atlasliquid", program);
        if (!program.Compile()) return null;

        stableLiquidShader = program;
        return program;
    }

    private IShaderProgram? GetAtlasCloudShader()
    {
        if (atlasCloudShader != null && !atlasCloudShader.Disposed)
        {
            return atlasCloudShader;
        }
        if (clientApi == null) return null;

        IShaderProgram program = clientApi.Shader.NewShaderProgram();
        program.AssetDomain = "modernatlas";
        clientApi.Shader.RegisterFileShaderProgram("atlascloud", program);
        if (!program.Compile()) return null;

        atlasCloudShader = program;
        return program;
    }

    private IShaderProgram? GetAtlasOpacityShader()
    {
        if (atlasOpacityShader != null && !atlasOpacityShader.Disposed)
        {
            return atlasOpacityShader;
        }
        if (clientApi == null) return null;

        IShaderProgram program = clientApi.Shader.NewShaderProgram();
        program.AssetDomain = "modernatlas";
        clientApi.Shader.RegisterFileShaderProgram("atlasopacity", program);
        if (!program.Compile()) return null;

        atlasOpacityShader = program;
        return program;
    }

    private IShaderProgram? GetAtlasBoundaryShader()
    {
        if (atlasBoundaryShader != null && !atlasBoundaryShader.Disposed)
        {
            return atlasBoundaryShader;
        }
        if (clientApi == null) return null;

        IShaderProgram program = clientApi.Shader.NewShaderProgram();
        program.AssetDomain = "modernatlas";
        clientApi.Shader.RegisterFileShaderProgram("atlasboundary", program);
        if (!program.Compile()) return null;

        atlasBoundaryShader = program;
        return program;
    }

    private IShaderProgram? GetAtlasScreenshotFilterShader()
    {
        if (atlasScreenshotFilterShader != null
            && !atlasScreenshotFilterShader.Disposed)
        {
            return atlasScreenshotFilterShader;
        }
        if (clientApi == null) return null;

        IShaderProgram program = clientApi.Shader.NewShaderProgram();
        program.AssetDomain = "modernatlas";
        clientApi.Shader.RegisterFileShaderProgram(
            "atlasscreenshotfilter",
            program
        );
        if (!program.Compile()) return null;

        atlasScreenshotFilterShader = program;
        return program;
    }

    private IShaderProgram? GetAtlasScrollShader()
    {
        if (atlasScrollShader != null && !atlasScrollShader.Disposed)
        {
            return atlasScrollShader;
        }
        if (clientApi == null) return null;

        IShaderProgram program = clientApi.Shader.NewShaderProgram();
        program.AssetDomain = "modernatlas";
        clientApi.Shader.RegisterFileShaderProgram("atlasscroll", program);
        if (!program.Compile()) return null;

        atlasScrollShader = program;
        return program;
    }

    private void SaveConfig()
    {
        if (clientApi != null && config != null)
        {
            clientApi.StoreModConfig(config, ConfigFileName);
        }
    }
}
