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
    private IShaderProgram? stableLiquidShader;
    private IShaderProgram? atlasCloudShader;
    private IShaderProgram? atlasOpacityShader;
    private CheatModeConsentDialog? cheatModeDialog;
    private string? activeWorldIdentifier;
    private int worldSessionGeneration;

    public override bool ShouldLoad(EnumAppSide side) => true;

    public override void StartServerSide(ICoreServerAPI api)
    {
        serverApi = api;
        serverConfig = api.LoadModConfig<ModernAtlasServerConfig>(ServerConfigFileName)
            ?? new ModernAtlasServerConfig();
        api.StoreModConfig(serverConfig, ServerConfigFileName);

        serverPolicyChannel = api.Network
            .RegisterChannel(PolicyChannelName)
            .RegisterMessageType<ModernAtlasServerPolicy>();
        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;

        ModernAtlasServerPolicy policy = serverConfig.ToPolicy();
        api.Logger.Notification(
            "[ModernAtlas] Server policy loaded: fog {0}; live 3D models players={1}, animals={2}, mobs={3}, npcs={4}.",
            policy.FogEnabled,
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

        api.Network
            .RegisterChannel(PolicyChannelName)
            .RegisterMessageType<ModernAtlasServerPolicy>()
            .SetMessageHandler<ModernAtlasServerPolicy>(OnServerPolicyReceived);
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

        dialog = new ModernAtlasDialog(
            api,
            config,
            serverPolicy,
            SaveConfig,
            GetStableLiquidShader,
            GetAtlasCloudShader,
            GetAtlasOpacityShader
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
        if (cheatModeDialog?.IsOpened() == true)
        {
            cheatModeDialog.Focus();
            return true;
        }

        dialog?.Toggle();
        return true;
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
        dialog?.Dispose();
        dialog = null;
        cheatModeDialog?.CancelWithoutDecision();
        cheatModeDialog?.Dispose();
        cheatModeDialog = null;
        stableLiquidShader = null;
        atlasCloudShader = null;
        atlasOpacityShader = null;
        clientApi = null;
        serverApi = null;
        config = null;
        serverConfig = null;
        serverPolicyChannel = null;
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
            "[ModernAtlas] Applied server policy: fog {0}; live 3D models players={1}, animals={2}, mobs={3}, npcs={4}.",
            policy.FogEnabled,
            policy.ShowPlayers,
            policy.ShowAnimals,
            policy.ShowMobs,
            policy.ShowNpcs
        );
    }

    private void OnLeaveWorld()
    {
        worldSessionGeneration++;
        cheatModeDialog?.CancelWithoutDecision();
        cheatModeDialog?.Dispose();
        cheatModeDialog = null;
        activeWorldIdentifier = null;
        dialog?.OnWorldLeave();
        serverPolicy.ResetToSafeDefaults();
    }

    private void OnLevelFinalize()
    {
        if (clientApi == null || config == null) return;

        string worldIdentifier = clientApi.World.SavegameIdentifier;
        if (string.IsNullOrWhiteSpace(worldIdentifier)) return;

        int sessionGeneration = ++worldSessionGeneration;
        activeWorldIdentifier = worldIdentifier;
        cheatModeDialog?.CancelWithoutDecision();
        cheatModeDialog?.Dispose();
        cheatModeDialog = null;

        // A client preference can never authorize multiplayer disclosure. A
        // future server policy may add this explicitly; until then the safe
        // surface-only view is mandatory outside singleplayer.
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
            ScheduleAutomatedSmokeTest(worldIdentifier, sessionGeneration);
        }
    }

    private bool AutomatedSmokeTestEnabled => string.Equals(
        Environment.GetEnvironmentVariable(SmokeTestEnvironmentVariable),
        "1",
        StringComparison.Ordinal
    );

    private void ScheduleAutomatedSmokeTest(string worldIdentifier, int sessionGeneration)
    {
        clientApi?.Logger.Notification(
            "[ModernAtlas] Automated smoke test scheduled for the loaded singleplayer world."
        );
        clientApi?.Event.RegisterCallback(
            _ => OpenAtlasForAutomatedSmokeTest(worldIdentifier, sessionGeneration),
            2500
        );
    }

    private void OpenAtlasForAutomatedSmokeTest(
        string worldIdentifier,
        int sessionGeneration
    )
    {
        if (clientApi == null
            || dialog == null
            || !clientApi.IsSinglePlayer
            || activeWorldIdentifier != worldIdentifier
            || worldSessionGeneration != sessionGeneration)
        {
            return;
        }

        dialog.BeginAutomatedSmokeTest(
            passed => FinishAutomatedSmokeTest(
                worldIdentifier,
                sessionGeneration,
                passed
            )
        );
        if (!dialog.TryOpen())
        {
            clientApi.Logger.Error(
                "[ModernAtlas] AUTOMATED SMOKE TEST FAILED: the atlas dialog could not be opened."
            );
            return;
        }

        clientApi.Logger.Notification(
            "[ModernAtlas] Automated smoke test opened the atlas without keyboard input."
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
                "[ModernAtlas] AUTOMATED SMOKE TEST PASSED: exact terrain rendered and the atlas is closing cleanly."
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
            dialog.TryClose();
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
            object game = clientApi.GetType().GetField("game", instanceFlags)?.GetValue(clientApi)
                ?? throw new InvalidOperationException("Client game instance is unavailable.");
            object runningGame = game.GetType().GetField(
                "ScreenRunningGame",
                instanceFlags
            )?.GetValue(game)
                ?? throw new InvalidOperationException("Running-game screen is unavailable.");
            MethodInfo exitOrRedirect = runningGame.GetType().GetMethod(
                "ExitOrRedirect",
                instanceFlags
            ) ?? throw new MissingMethodException(
                runningGame.GetType().FullName,
                "ExitOrRedirect"
            );
            Type exitModeType = exitOrRedirect.GetParameters()[2].ParameterType;
            object softExit = Enum.Parse(exitModeType, "SoftExit");

            clientApi.Logger.Notification(
                "[ModernAtlas] Automated smoke test is leaving the world through the game's normal soft-exit path."
            );
            exitOrRedirect.Invoke(runningGame, new[] { true, "", softExit });
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

    private void SaveConfig()
    {
        if (clientApi != null && config != null)
        {
            clientApi.StoreModConfig(config, ConfigFileName);
        }
    }
}
