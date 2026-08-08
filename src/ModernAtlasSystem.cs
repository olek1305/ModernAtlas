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
    private ModernAtlasDialog? dialog;
    private IShaderProgram? atlasShader;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        dialog = new ModernAtlasDialog(api);
        api.Event.BlockTexturesLoaded += () => LoadShader(api);
        api.Event.ReloadShader += () => LoadShader(api);

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

    private bool LoadShader(ICoreClientAPI api)
    {
        atlasShader?.Dispose();
        atlasShader = api.Shader.NewShaderProgram();
        atlasShader.AssetDomain = "modernatlas";
        atlasShader.VertexShader = api.Shader.NewShader(EnumShaderType.VertexShader);
        atlasShader.FragmentShader = api.Shader.NewShader(EnumShaderType.FragmentShader);
        api.Shader.RegisterFileShaderProgram("modernatlasworld", atlasShader);

        bool compiled = atlasShader.Compile();
        dialog?.SetShader(compiled ? atlasShader : null);
        api.Logger.Notification(
            compiled
                ? "[ModernAtlas] Atlas world shader compiled."
                : "[ModernAtlas] Atlas world shader failed to compile."
        );
        return compiled;
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
        atlasShader?.Dispose();
        atlasShader = null;
        base.Dispose();
    }
}
