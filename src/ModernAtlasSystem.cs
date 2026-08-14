using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
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
    private IShaderProgram? atlasOpacityShader;
    private IShaderProgram? atlasScrollShader;
    private CheatModeConsentDialog? cheatModeDialog;
    private AtlasOpeningTransitionDialog? openingTransition;
    private AtlasSoundController? soundController;
    private string? activeWorldIdentifier;
    private int worldSessionGeneration;
    private bool automatedWorldExitRequested;
    private bool automatedSmokeTestOpeningStarted;

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
            "[ModernAtlas] Server policy loaded: fog {0}; Cheat Mode allowed={1}; live 3D models players={2}, animals={3}, mobs={4}, npcs={5}.",
            policy.FogEnabled,
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

        if (GetStableLiquidShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the stable liquid shader.");
        }
        if (GetAtlasCloudShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the atlas cloud shader.");
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
            GetStableLiquidShader,
            GetAtlasCloudShader,
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
        api.Event.RegisterRenderer(
            openingTransition,
            EnumRenderStage.Opaque,
            "modernatlas-opening-scroll"
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
            // A second G must never skip an opening scene or restart a closing
            // scene. The transition owns the key until it has released input.
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

        StartOpeningTransition();
        return true;
    }

    private bool RequestCloseAtlas()
    {
        return RequestCloseAtlas(null);
    }

    private bool RequestCloseAtlas(Action<bool>? onCompleted)
    {
        if (dialog?.IsOpened() != true) return false;

        if (!dialog.TryClose()) return false;
        if (ShouldSkipScrollTransitions())
        {
            onCompleted?.Invoke(true);
            return true;
        }
        if (openingTransition == null
            || !openingTransition.BeginClosing(
                false,
                passed =>
                {
                    onCompleted?.Invoke(passed);
                }
            ))
        {
            clientApi?.Logger.Warning(
                "[ModernAtlas] The scroll stowing transition was unavailable."
            );
            onCompleted?.Invoke(false);
        }
        return true;
    }

    private void StartOpeningTransition()
    {
        if (dialog == null || openingTransition == null) return;

        if (ShouldSkipScrollTransitions())
        {
            if (!dialog.TryOpen())
            {
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
                    clientApi?.Logger.Error(
                        "[ModernAtlas] The atlas transition did not release its input layer; atlas activation was cancelled."
                    );
                    return;
                }
                if (openingTransition?.IsOpened() == true)
                {
                    clientApi?.Logger.Error(
                        "[ModernAtlas] Refusing to open the atlas while the completed transition dialog is still active."
                    );
                    return;
                }
                dialog.PrepareForTransitionHandoff();
                if (dialog.TryOpen()) return;
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
            dialog.TryOpen();
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
        if (clientApi != null)
        {
            clientApi.Event.LeaveWorld -= OnLeaveWorld;
            clientApi.Event.LevelFinalize -= OnLevelFinalize;
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
        openingTransition?.CancelWithoutOpening();
        openingTransition?.Dispose();
        openingTransition = null;
        dialog?.Dispose();
        dialog = null;
        soundController?.Dispose();
        soundController = null;
        cheatModeDialog?.CancelWithoutDecision();
        cheatModeDialog?.Dispose();
        cheatModeDialog = null;
        stableLiquidShader = null;
        atlasCloudShader = null;
        atlasOpacityShader = null;
        atlasScrollShader = null;
        clientApi = null;
        serverApi = null;
        config = null;
        serverConfig = null;
        serverPolicyChannel = null;
        clientPolicyChannel = null;
        activeWorldIdentifier = null;
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
            "[ModernAtlas] Applied server policy: fog {0}; Cheat Mode allowed={1}; live 3D models players={2}, animals={3}, mobs={4}, npcs={5}.",
            policy.FogEnabled,
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

        string worldIdentifier = clientApi.World.SavegameIdentifier;
        if (string.IsNullOrWhiteSpace(worldIdentifier)) return;

        int sessionGeneration = ++worldSessionGeneration;
        activeWorldIdentifier = worldIdentifier;
        automatedSmokeTestOpeningStarted = false;
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

        if (AutomatedSmokeTestEnabled)
        {
            ScheduleAutomatedCheatCommandTest(worldIdentifier, sessionGeneration);
        }
    }

    private bool AutomatedSmokeTestEnabled => string.Equals(
        Environment.GetEnvironmentVariable(SmokeTestEnvironmentVariable),
        "1",
        StringComparison.Ordinal
    );

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

        // Recover from a direct atlas open that may have happened before the
        // scheduled smoke callback. The test must own exactly one opening
        // transition, never stack a second dialog over the first one.
        if (dialog.IsOpened()) dialog.TryClose();
        if (openingTransition.IsOpened()) openingTransition.CancelWithoutOpening();

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
            clientApi.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED: the opening transition or atlas open check failed."
            );
            FinishAutomatedSmokeTest(worldIdentifier, sessionGeneration, false);
            return;
        }

        clientApi.Logger.Notification(
            "[ModernAtlas] Automated opening transition entered the atlas normally."
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

        if (passed)
        {
            clientApi.Logger.Notification(
                "[ModernAtlas] AUTOMATED ATLAS CHECKS PASSED: exact terrain and requested atlas features rendered."
            );
        }
        else
        {
            clientApi.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED: exact terrain did not render before timeout."
            );
        }

        if (dialog.IsOpened())
        {
            if (openingTransition != null
                && dialog.TryClose()
                && openingTransition.BeginClosing(
                    true,
                    closePassed =>
                    {
                        if (!closePassed)
                        {
                            clientApi?.Logger.Error(
                                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED: the reverse scroll transition did not complete."
                            );
                        }
                        clientApi?.Event.RegisterCallback(
                            _ => ExitWorldForAutomatedSmokeTest(
                                worldIdentifier,
                                sessionGeneration
                            ),
                            1000
                        );
                    }
                ))
            {
                return;
            }
        }

        clientApi.Event.RegisterCallback(
            _ => ExitWorldForAutomatedSmokeTest(worldIdentifier, sessionGeneration),
            1000
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
