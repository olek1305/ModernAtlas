using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ModernAtlas;

/// <summary>
/// Owns the independent ModernAtlas 3D dialog. The vanilla map remains
/// untouched and can still be opened through its own configured controls.
/// </summary>
public sealed class ModernAtlasSystem : ModSystem
{
    private const string ConfigFileName = "ModernAtlas.json";

    private ModernAtlasDialog? dialog;
    private ICoreClientAPI? clientApi;
    private ModernAtlasConfig? config;
    private IShaderProgram? stableLiquidShader;
    private IShaderProgram? surfaceShellShader;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        clientApi = api;
        config = api.LoadModConfig<ModernAtlasConfig>(ConfigFileName) ?? new ModernAtlasConfig();
        SaveConfig();

        if (GetStableLiquidShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the stable liquid shader.");
        }
        if (GetSurfaceShellShader() == null)
        {
            api.Logger.Error("[ModernAtlas] Failed to compile the surface shell shader.");
        }

        dialog = new ModernAtlasDialog(
            api,
            config,
            SaveConfig,
            GetStableLiquidShader,
            GetSurfaceShellShader
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
        dialog?.Dispose();
        dialog = null;
        stableLiquidShader = null;
        surfaceShellShader = null;
        clientApi = null;
        config = null;
        base.Dispose();
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

    private IShaderProgram? GetSurfaceShellShader()
    {
        if (surfaceShellShader != null && !surfaceShellShader.Disposed)
        {
            return surfaceShellShader;
        }
        if (clientApi == null) return null;

        IShaderProgram program = clientApi.Shader.NewShaderProgram();
        program.AssetDomain = "modernatlas";
        clientApi.Shader.RegisterFileShaderProgram("atlasshell", program);
        if (!program.Compile()) return null;

        surfaceShellShader = program;
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
