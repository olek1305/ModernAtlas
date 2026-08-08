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

    private ModernAtlasDialog? dialog;
    private ICoreClientAPI? clientApi;
    private ICoreServerAPI? serverApi;
    private ModernAtlasConfig? config;
    private ModernAtlasServerConfig? serverConfig;
    private readonly ModernAtlasServerPolicy serverPolicy = new();
    private IServerNetworkChannel? serverPolicyChannel;
    private IShaderProgram? stableLiquidShader;
    private IShaderProgram? atlasCloudShader;

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

        if (GetStableLiquidShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the stable liquid shader.");
        }
        if (GetAtlasCloudShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the atlas cloud shader.");
        }

        dialog = new ModernAtlasDialog(
            api,
            config,
            serverPolicy,
            SaveConfig,
            GetStableLiquidShader,
            GetAtlasCloudShader
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
        dialog?.Toggle();
        return true;
    }

    public override void Dispose()
    {
        if (clientApi != null)
        {
            clientApi.Event.LeaveWorld -= OnLeaveWorld;
        }
        if (serverApi != null)
        {
            serverApi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
        }
        dialog?.Dispose();
        dialog = null;
        stableLiquidShader = null;
        atlasCloudShader = null;
        clientApi = null;
        serverApi = null;
        config = null;
        serverConfig = null;
        serverPolicyChannel = null;
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
        serverPolicy.ResetToSafeDefaults();
        dialog?.OnServerPolicyChanged();
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

    private void SaveConfig()
    {
        if (clientApi != null && config != null)
        {
            clientApi.StoreModConfig(config, ConfigFileName);
        }
    }
}
