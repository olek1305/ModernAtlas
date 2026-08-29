using System;
using System.IO;
using ProtoBuf;

namespace ModernAtlas;

/// <summary>
/// Pure access rules for the server-owned Settings lock. Keeping this
/// separate from the GUI makes the protobuf fallback and both multiplayer
/// outcomes deterministic and easy to exercise without a live client.
/// </summary>
internal static class ModernAtlasServerSettingsPolicy
{
    public const string DisabledReason = "Disabled by server policy";

    /// <summary>
    /// Singleplayer never consults a server lock. A null policy represents an
    /// absent channel and is allowed for compatibility with client-only use;
    /// the sensitive Cheat/entity switches retain their separate fail-closed
    /// defaults in <see cref="ModernAtlasServerPolicy"/>.
    /// </summary>
    public static bool AllowsClientSettings(
        bool isSinglePlayer,
        ModernAtlasServerPolicy? policy
    ) => isSinglePlayer || policy?.ClientSettingsLocked != true;

    public static bool AllowsClientPreset(
        bool isSinglePlayer,
        ModernAtlasServerPolicy? policy
    ) => AllowsClientSettings(isSinglePlayer, policy);

    public static bool AllowsHideVegetation(
        bool isSinglePlayer,
        ModernAtlasServerPolicy? policy
    ) => isSinglePlayer || policy?.HideVegetationLocked != true;

    /// <summary>
    /// Applies the server master switch and the per-category server switch to
    /// one client preference. Keeping this rule pure prevents a client-side
    /// preference from ever widening the disclosure granted by the server.
    /// </summary>
    public static bool AllowsEntityCategory(
        bool isSinglePlayer,
        bool clientEnabled,
        bool serverEnabled,
        bool masterEnabled
    ) => masterEnabled
        && clientEnabled
        && (isSinglePlayer || serverEnabled);

    /// <summary>
    /// The integrated singleplayer/listen-server path keeps its local preset
    /// behavior. A dedicated server with Settings locked must reject the
    /// request before it sends anything, while the client repeats the same
    /// check before applying every received preset.
    /// </summary>
    public static bool AllowsServerPresetRequest(
        bool isDedicatedServer,
        ModernAtlasServerConfig? config
    ) => !isDedicatedServer || config?.AllowClientSettings == true;

    /// <summary>
    /// Listen servers run both sides in one process. The local host keeps its
    /// singleplayer client behavior, while a remote caller is still subject
    /// to AllowClientSettings just like a dedicated-server client. When the
    /// host identity is unavailable, fail closed for a listen-server caller.
    /// </summary>
    public static bool AllowsServerPresetRequest(
        bool isDedicatedServer,
        bool isIntegratedHostCaller,
        ModernAtlasServerConfig? config
    ) => isIntegratedHostCaller && !isDedicatedServer
        || config?.AllowClientSettings == true;

    /// <summary>
    /// A server Cheat Mode command may be disabled for ordinary callers even
    /// on an integrated server. The local listen-server host remains the
    /// singleplayer authority; disabling a command is still always allowed so
    /// an already enabled mode can be turned off safely.
    /// </summary>
    public static bool AllowsServerCheatCommand(
        bool isDedicatedServer,
        bool isIntegratedHostCaller,
        ModernAtlasServerConfig? config,
        bool requestedEnabled
    ) => !requestedEnabled
        || (isIntegratedHostCaller && !isDedicatedServer)
        || config?.CheatModeAllowed == true;

    /// <summary>
    /// Enabling Cheat Mode in multiplayer requires both the authoritative
    /// policy packet and its explicit grant. A value read from the client's
    /// ModernAtlas.json is deliberately not an input to this decision.
    /// Disabling Cheat Mode is always accepted.
    /// </summary>
    public static bool AllowsCheatModeRequest(
        bool isSinglePlayer,
        bool policyPacketReceived,
        ModernAtlasServerPolicy? policy,
        bool requestedEnabled
    ) => !requestedEnabled
        || isSinglePlayer
        || (policyPacketReceived && policy?.CheatModeAllowed == true);

    public static bool AllowsCheatMode(
        bool isSinglePlayer,
        ModernAtlasServerPolicy? policy
    ) => isSinglePlayer || policy?.CheatModeAllowed == true;

    public static string ToolbarText(
        bool isSinglePlayer,
        ModernAtlasServerPolicy? policy
    ) => AllowsClientSettings(isSinglePlayer, policy)
        ? "Settings"
        : $"Settings · {DisabledReason}";

    /// <summary>
    /// A connected ModernAtlas channel is evidence that an authoritative
    /// packet is expected, but the packet can arrive after level finalization.
    /// Hold Settings closed during that narrow window. A channel that is not
    /// connected means an unmodded/older server path, which must retain the
    /// client-only compatibility fallback.
    /// </summary>
    public static bool ShouldTemporarilyLockUntilPolicyPacket(
        bool isSinglePlayer,
        bool channelConnected,
        bool policyPacketReceived
    ) => !isSinglePlayer && channelConnected && !policyPacketReceived;

    /// <summary>
    /// Covers the default config, explicit allow/deny packets, the absent
    /// channel fallback, and the copy/reset paths used around world joins.
    /// </summary>
    public static string? Validate()
    {
        ModernAtlasServerConfig defaultConfig = new();
        ModernAtlasServerPolicy defaultPolicy = defaultConfig.ToPolicy();
        if (!defaultConfig.AllowClientSettings
            || !defaultConfig.AllowHideVegetation
            || defaultPolicy.ClientSettingsLocked
            || defaultPolicy.HideVegetationLocked
            || !AllowsClientSettings(false, defaultPolicy)
            || !AllowsClientSettings(false, null)
            || !AllowsClientSettings(true, new ModernAtlasServerPolicy
            {
                ClientSettingsLocked = true
            }))
        {
            return "default or missing-channel Settings access is not allowed";
        }

        // Exercise every master/per-category combination. A false master
        // switch must dominate every category, while an enabled master must
        // preserve each independent administrator flag.
        foreach (bool masterEnabled in new[] { false, true })
        foreach (bool showPlayers in new[] { false, true })
        foreach (bool showAnimals in new[] { false, true })
        foreach (bool showMobs in new[] { false, true })
        foreach (bool showNpcs in new[] { false, true })
        {
            ModernAtlasServerConfig matrixConfig = new()
            {
                LivingEntitiesEnabled = masterEnabled,
                ShowPlayers = showPlayers,
                ShowAnimals = showAnimals,
                ShowMobs = showMobs,
                ShowNpcs = showNpcs
            };
            ModernAtlasServerPolicy matrixPolicy = matrixConfig.ToPolicy();
            if (matrixPolicy.ShowPlayers != (masterEnabled && showPlayers)
                || matrixPolicy.ShowAnimals != (masterEnabled && showAnimals)
                || matrixPolicy.ShowMobs != (masterEnabled && showMobs)
                || matrixPolicy.ShowNpcs != (masterEnabled && showNpcs))
            {
                return "LivingEntitiesEnabled did not dominate the category policy matrix";
            }
        }

        const string overridePlayerUid = "modernatlas-policy-override-player";
        ModernAtlasServerConfig perPlayerConfig = new()
        {
            AllowClientSettings = false,
            AllowHideVegetation = false,
            CheatModeAllowed = false,
            LivingEntitiesEnabled = false
        };
        perPlayerConfig.PlayerOverrides[overridePlayerUid] =
            new ModernAtlasPlayerPolicyOverride
            {
                AllowClientSettings = true,
                AllowHideVegetation = true,
                CheatModeAllowed = true,
                ShowPlayers = true,
                ShowAnimals = true,
                ShowMobs = true,
                ShowNpcs = true
            };
        ModernAtlasServerPolicy overriddenPlayerPolicy =
            perPlayerConfig.ToPolicy(overridePlayerUid);
        ModernAtlasServerPolicy ordinaryPlayerPolicy =
            perPlayerConfig.ToPolicy("ordinary-player");
        if (overriddenPlayerPolicy.ClientSettingsLocked
            || overriddenPlayerPolicy.HideVegetationLocked
            || !overriddenPlayerPolicy.CheatModeAllowed
            || !overriddenPlayerPolicy.ShowPlayers
            || !overriddenPlayerPolicy.ShowAnimals
            || !overriddenPlayerPolicy.ShowMobs
            || !overriddenPlayerPolicy.ShowNpcs
            || !ordinaryPlayerPolicy.ClientSettingsLocked
            || !ordinaryPlayerPolicy.HideVegetationLocked
            || ordinaryPlayerPolicy.CheatModeAllowed
            || ordinaryPlayerPolicy.AnyEntityModels)
        {
            return "a per-player exception did not override only its target policy";
        }

        perPlayerConfig.PlayerOverrides[overridePlayerUid] =
            new ModernAtlasPlayerPolicyOverride
            {
                ShowPlayers = false
            };
        perPlayerConfig.LivingEntitiesEnabled = true;
        perPlayerConfig.ShowPlayers = true;
        perPlayerConfig.ShowAnimals = true;
        ModernAtlasServerPolicy inheritedPlayerPolicy =
            perPlayerConfig.ToPolicy(overridePlayerUid);
        if (inheritedPlayerPolicy.ShowPlayers
            || !inheritedPlayerPolicy.ShowAnimals
            || perPlayerConfig.PlayerOverrides[overridePlayerUid].HasAnyOverride
                == false)
        {
            return "a per-player deny did not preserve inherited category defaults";
        }

        // The client-side disclosure rule gets the same exhaustive treatment:
        // singleplayer is local-only, while multiplayer cannot turn a denied
        // server category back on with its saved client preference.
        foreach (bool isSinglePlayer in new[] { false, true })
        foreach (bool masterEnabled in new[] { false, true })
        foreach (bool clientEnabled in new[] { false, true })
        foreach (bool serverEnabled in new[] { false, true })
        {
            bool expected = masterEnabled
                && clientEnabled
                && (isSinglePlayer || serverEnabled);
            if (AllowsEntityCategory(
                    isSinglePlayer,
                    clientEnabled,
                    serverEnabled,
                    masterEnabled
                ) != expected)
            {
                return "client/server entity disclosure matrix is invalid";
            }
        }

        ModernAtlasServerConfig lockedConfig = new()
        {
            AllowClientSettings = false,
            AllowHideVegetation = false
        };
        ModernAtlasServerPolicy lockedPolicy = lockedConfig.ToPolicy();
        if (!lockedPolicy.ClientSettingsLocked
            || !lockedPolicy.HideVegetationLocked
            || AllowsClientSettings(false, lockedPolicy)
            || AllowsHideVegetation(false, lockedPolicy)
            || !AllowsClientSettings(true, lockedPolicy)
            || !AllowsHideVegetation(true, lockedPolicy)
            || ToolbarText(false, lockedPolicy) != $"Settings · {DisabledReason}")
        {
            return "an explicit server Settings lock did not deny multiplayer access";
        }

        if (AllowsClientPreset(false, lockedPolicy)
            || AllowsServerPresetRequest(true, false, lockedConfig)
            || AllowsServerPresetRequest(false, false, lockedConfig)
            || !AllowsServerPresetRequest(false, true, lockedConfig)
            || AllowsServerPresetRequest(true, null)
            || !AllowsServerPresetRequest(false, lockedConfig)
            || AllowsServerPresetRequest(false, false, null)
            || !AllowsServerPresetRequest(false, true, null))
        {
            return "a client, remote listen-server, or dedicated-server preset bypassed the Settings lock";
        }

        // Exercise every dedicated/listen caller/config combination. A local
        // integrated host is the only caller that can retain its local
        // singleplayer behavior when the server setting is locked; a remote
        // listen caller follows the same setting as a dedicated client.
        foreach (bool isDedicatedServer in new[] { false, true })
        foreach (bool isIntegratedHostCaller in new[] { false, true })
        foreach (bool hasConfig in new[] { false, true })
        foreach (bool allowClientSettings in new[] { false, true })
        {
            ModernAtlasServerConfig? commandConfig = hasConfig
                ? new ModernAtlasServerConfig
                {
                    AllowClientSettings = allowClientSettings
                }
                : null;
            bool expected = isIntegratedHostCaller && !isDedicatedServer
                || commandConfig?.AllowClientSettings == true;
            if (AllowsServerPresetRequest(
                    isDedicatedServer,
                    isIntegratedHostCaller,
                    commandConfig
                ) != expected)
            {
                return "dedicated/listen preset authority matrix is invalid";
            }
        }

        foreach (bool isDedicatedServer in new[] { false, true })
        foreach (bool isIntegratedHostCaller in new[] { false, true })
        foreach (bool hasConfig in new[] { false, true })
        foreach (bool cheatModeAllowed in new[] { false, true })
        foreach (bool requestedEnabled in new[] { false, true })
        {
            ModernAtlasServerConfig? commandConfig = hasConfig
                ? new ModernAtlasServerConfig
                {
                    CheatModeAllowed = cheatModeAllowed
                }
                : null;
            bool expected = !requestedEnabled
                || isIntegratedHostCaller && !isDedicatedServer
                || commandConfig?.CheatModeAllowed == true;
            if (AllowsServerCheatCommand(
                    isDedicatedServer,
                    isIntegratedHostCaller,
                    commandConfig,
                    requestedEnabled
                ) != expected)
            {
                return "dedicated/listen Cheat Mode authority matrix is invalid";
            }
        }

        ModernAtlasServerConfig cheatDeniedConfig = new();
        ModernAtlasServerConfig cheatAllowedConfig = new()
        {
            CheatModeAllowed = true
        };
        if (AllowsServerCheatCommand(true, false, cheatDeniedConfig, true)
            || AllowsServerCheatCommand(false, false, cheatDeniedConfig, true)
            || !AllowsServerCheatCommand(false, false, cheatAllowedConfig, true)
            || !AllowsServerCheatCommand(false, true, cheatDeniedConfig, true)
            || !AllowsServerCheatCommand(true, false, cheatDeniedConfig, false))
        {
            return "dedicated/listen Cheat Mode command authority is invalid";
        }

        ModernAtlasServerPolicy cheatDeniedPolicy = new()
        {
            CheatModeAllowed = false
        };
        ModernAtlasServerPolicy cheatAllowedPolicy = new()
        {
            CheatModeAllowed = true
        };
        if (AllowsCheatModeRequest(
                false,
                policyPacketReceived: true,
                cheatDeniedPolicy,
                requestedEnabled: true
            )
            || AllowsCheatModeRequest(
                false,
                policyPacketReceived: false,
                cheatAllowedPolicy,
                requestedEnabled: true
            )
            || !AllowsCheatModeRequest(
                false,
                policyPacketReceived: true,
                cheatAllowedPolicy,
                requestedEnabled: true
            )
            || !AllowsCheatModeRequest(
                false,
                policyPacketReceived: false,
                cheatDeniedPolicy,
                requestedEnabled: false
            ))
        {
            return "local or pre-policy Cheat Mode state bypassed server authority";
        }

        if (!ShouldTemporarilyLockUntilPolicyPacket(false, true, false)
            || ShouldTemporarilyLockUntilPolicyPacket(false, false, false)
            || ShouldTemporarilyLockUntilPolicyPacket(false, true, true)
            || ShouldTemporarilyLockUntilPolicyPacket(true, true, false))
        {
            return "connected/absent/pending policy-channel fallback states are invalid";
        }

        ModernAtlasServerPolicy copiedPolicy = new();
        copiedPolicy.CopyFrom(lockedPolicy);
        if (!copiedPolicy.ClientSettingsLocked
            || !copiedPolicy.HideVegetationLocked)
        {
            return "CopyFrom lost a server-owned client control lock";
        }

        copiedPolicy.ResetToSafeDefaults();
        if (copiedPolicy.ClientSettingsLocked
            || copiedPolicy.HideVegetationLocked
            || !AllowsClientSettings(false, copiedPolicy))
        {
            return "ResetToSafeDefaults did not restore the compatibility fallback";
        }

        try
        {
            using MemoryStream currentStream = new();
            Serializer.Serialize(currentStream, lockedPolicy);
            currentStream.Position = 0;
            ModernAtlasServerPolicy currentPolicy =
                Serializer.Deserialize<ModernAtlasServerPolicy>(currentStream);
            if (!currentPolicy.ClientSettingsLocked)
            {
                return "protobuf did not preserve an explicit Settings lock";
            }
            if (!currentPolicy.HideVegetationLocked)
            {
                return "protobuf did not preserve an explicit Hide vegetation lock";
            }

            using MemoryStream legacyStream = new();
            Serializer.Serialize(legacyStream, new LegacyPolicyPacket
            {
                ShowPlayers = true,
                CheatModeAllowed = true
            });
            legacyStream.Position = 0;
            ModernAtlasServerPolicy legacyPolicy =
                Serializer.Deserialize<ModernAtlasServerPolicy>(legacyStream);
            if (legacyPolicy.ClientSettingsLocked
                || legacyPolicy.HideVegetationLocked
                || !legacyPolicy.ShowPlayers
                || !legacyPolicy.CheatModeAllowed)
            {
                return "a policy packet without fields 7 and 8 lost protobuf compatibility";
            }
        }
        catch (Exception exception)
        {
            return $"protobuf authority validation threw {exception.GetType().Name}: {exception.Message}";
        }

        return null;
    }

    [ProtoContract]
    private sealed class LegacyPolicyPacket
    {
        [ProtoMember(2)]
        public bool ShowPlayers { get; set; }

        [ProtoMember(6)]
        public bool CheatModeAllowed { get; set; }
    }
}
