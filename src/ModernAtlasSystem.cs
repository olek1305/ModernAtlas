using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
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
    private const int RelightIntervalMilliseconds = 500;
    private const int MaximumRelightRadiusBlocks = 1024;

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
    private IShaderProgram? atlasCacheShader;
    private IShaderProgram? atlasOpacityShader;
    private AtlasSurfaceCache? surfaceCache;
    private long cacheTickListenerId;
    private long relightTickListenerId;
    private readonly Queue<Vec2i> relightQueue = new();
    private IServerPlayer? relightPlayer;
    private int relightCompleted;
    private int relightTotal;

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
            .RegisterMessageType<ModernAtlasRelightRequest>()
            .RegisterMessageType<ModernAtlasRelightProgress>()
            .SetMessageHandler<ModernAtlasRelightRequest>(OnRelightRequested);
        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        relightTickListenerId = api.Event.RegisterGameTickListener(
            ProcessRelightQueue,
            RelightIntervalMilliseconds
        );

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

        clientPolicyChannel = api.Network
            .RegisterChannel(PolicyChannelName)
            .RegisterMessageType<ModernAtlasServerPolicy>()
            .RegisterMessageType<ModernAtlasRelightRequest>()
            .RegisterMessageType<ModernAtlasRelightProgress>()
            .SetMessageHandler<ModernAtlasServerPolicy>(OnServerPolicyReceived)
            .SetMessageHandler<ModernAtlasRelightProgress>(OnRelightProgress);
        api.Event.LeaveWorld += OnLeaveWorld;
        api.Event.LevelFinalize += OnLevelFinalize;
        api.Event.BlockChanged += OnBlockChanged;

        if (GetStableLiquidShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the stable liquid shader.");
        }
        if (GetAtlasCloudShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the atlas cloud shader.");
        }
        if (GetAtlasCacheShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the atlas cache shader.");
        }
        if (GetAtlasOpacityShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the window opacity shader.");
        }

        surfaceCache = new AtlasSurfaceCache(api, GetAtlasCacheShader);
        cacheTickListenerId = api.Event.RegisterGameTickListener(OnCacheTick, 250);

        dialog = new ModernAtlasDialog(
            api,
            config,
            serverPolicy,
            SaveConfig,
            GetStableLiquidShader,
            GetAtlasCloudShader,
            GetAtlasOpacityShader,
            surfaceCache,
            ClearCacheAndRepairLighting
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
            clientApi.Event.LevelFinalize -= OnLevelFinalize;
            clientApi.Event.BlockChanged -= OnBlockChanged;
            if (cacheTickListenerId != 0)
            {
                clientApi.Event.UnregisterGameTickListener(cacheTickListenerId);
            }
        }
        if (serverApi != null)
        {
            serverApi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
            if (relightTickListenerId != 0)
            {
                serverApi.Event.UnregisterGameTickListener(relightTickListenerId);
            }
        }
        dialog?.Dispose();
        dialog = null;
        surfaceCache?.Dispose();
        surfaceCache = null;
        stableLiquidShader = null;
        atlasCloudShader = null;
        atlasCacheShader = null;
        atlasOpacityShader = null;
        clientApi = null;
        serverApi = null;
        config = null;
        serverConfig = null;
        serverPolicyChannel = null;
        clientPolicyChannel = null;
        relightQueue.Clear();
        relightPlayer = null;
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

    private bool ClearCacheAndRepairLighting(int viewDistanceBlocks)
    {
        bool cleared = surfaceCache?.Clear() ?? true;
        if (clientApi?.IsSinglePlayer == true)
        {
            surfaceCache?.SetRelightProgress(0, 0, false);
            clientPolicyChannel?.SendPacket(new ModernAtlasRelightRequest
            {
                ViewDistanceBlocks = viewDistanceBlocks
            });
        }
        return cleared;
    }

    private void OnRelightRequested(IServerPlayer player, ModernAtlasRelightRequest request)
    {
        if (serverApi == null || !player.HasPrivilege(Privilege.controlserver))
        {
            return;
        }

        int chunkSize = serverApi.WorldManager.ChunkSize;
        int radiusBlocks = Math.Clamp(
            request.ViewDistanceBlocks,
            chunkSize,
            MaximumRelightRadiusBlocks
        );
        int radiusChunks = (radiusBlocks + chunkSize - 1) / chunkSize;
        int playerChunkX = (int)Math.Floor(player.Entity.Pos.X / chunkSize);
        int playerChunkZ = (int)Math.Floor(player.Entity.Pos.Z / chunkSize);
        int chunkMapSizeX = serverApi.WorldManager.MapSizeX / chunkSize;

        List<Vec2i> loadedColumns = new();
        foreach (long index in serverApi.World.LoadedMapChunkIndices)
        {
            int chunkX = (int)(index % chunkMapSizeX);
            int chunkZ = (int)(index / chunkMapSizeX);
            if (Math.Abs(chunkX - playerChunkX) > radiusChunks
                || Math.Abs(chunkZ - playerChunkZ) > radiusChunks)
            {
                continue;
            }
            loadedColumns.Add(new Vec2i(chunkX, chunkZ));
        }
        loadedColumns.Sort((left, right) =>
        {
            int leftDistance = Math.Max(
                Math.Abs(left.X - playerChunkX),
                Math.Abs(left.Y - playerChunkZ)
            );
            int rightDistance = Math.Max(
                Math.Abs(right.X - playerChunkX),
                Math.Abs(right.Y - playerChunkZ)
            );
            return leftDistance.CompareTo(rightDistance);
        });

        relightQueue.Clear();
        foreach (Vec2i column in loadedColumns)
        {
            relightQueue.Enqueue(column);
        }
        relightPlayer = player;
        relightCompleted = 0;
        relightTotal = relightQueue.Count;
        SendRelightProgress(finished: relightTotal == 0);
        serverApi.Logger.Notification(
            "[ModernAtlas] Queued vanilla relighting for {0} loaded chunk columns after cache clear.",
            relightTotal
        );
    }

    private void ProcessRelightQueue(float deltaTime)
    {
        if (serverApi == null || relightPlayer == null || relightQueue.Count == 0) return;

        Vec2i column = relightQueue.Dequeue();
        int chunkSize = serverApi.WorldManager.ChunkSize;
        if (serverApi.WorldManager.GetMapChunk(column.X, column.Y) != null)
        {
            BlockPos min = new(column.X * chunkSize, 0, column.Y * chunkSize);
            BlockPos max = new(
                (column.X + 1) * chunkSize - 1,
                serverApi.WorldManager.MapSizeY - 1,
                (column.Y + 1) * chunkSize - 1
            );
            serverApi.WorldManager.FullRelight(min, max, true);
        }
        relightCompleted++;

        bool finished = relightQueue.Count == 0;
        if (finished || relightCompleted % 4 == 0)
        {
            SendRelightProgress(finished);
        }
        if (finished)
        {
            serverApi.Logger.Notification(
                "[ModernAtlas] Completed vanilla relighting for {0} loaded chunk columns.",
                relightCompleted
            );
            relightPlayer = null;
        }
    }

    private void SendRelightProgress(bool finished)
    {
        if (relightPlayer == null) return;
        serverPolicyChannel?.SendPacket(new ModernAtlasRelightProgress
        {
            Completed = relightCompleted,
            Total = relightTotal,
            Finished = finished
        }, relightPlayer);
    }

    private void OnRelightProgress(ModernAtlasRelightProgress progress)
    {
        surfaceCache?.SetRelightProgress(progress.Completed, progress.Total, progress.Finished);
        if (progress.Finished)
        {
            clientApi?.Logger.Notification(
                "[ModernAtlas] Vanilla lighting repair completed for {0} loaded chunk columns.",
                progress.Completed
            );
        }
    }

    private void OnLeaveWorld()
    {
        serverPolicy.ResetToSafeDefaults();
        surfaceCache?.LeaveWorld();
        dialog?.OnServerPolicyChanged();
    }

    private void OnLevelFinalize()
    {
        surfaceCache?.InitializeWorld();
    }

    private void OnBlockChanged(Vintagestory.API.MathTools.BlockPos position, Block? oldBlock)
    {
        surfaceCache?.MarkDirty(position);
    }

    private void OnCacheTick(float deltaTime)
    {
        surfaceCache?.Tick(deltaTime);
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

    private IShaderProgram? GetAtlasCacheShader()
    {
        if (atlasCacheShader != null && !atlasCacheShader.Disposed)
        {
            return atlasCacheShader;
        }
        if (clientApi == null) return null;

        IShaderProgram program = clientApi.Shader.NewShaderProgram();
        program.AssetDomain = "modernatlas";
        clientApi.Shader.RegisterFileShaderProgram("atlascache", program);
        if (!program.Compile()) return null;

        atlasCacheShader = program;
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
