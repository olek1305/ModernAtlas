using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.GameContent.Mechanics;

namespace ModernAtlas;

/// <summary>
/// Draws the already loaded native mechanical-power instances into the atlas
/// framebuffer. Vintage Story keeps windmill sails, axles, gears and other
/// moving mechanisms out of chunk meshes, so exact terrain rendering alone
/// cannot show them. This adapter invokes only the dedicated mechanical mesh
/// renderer, never the global world render stage or the network simulation.
/// </summary>
internal sealed class AtlasMechanicalRendererAdapter
{
    private const int ExteriorSafetyAllowance = 3;

    private readonly ICoreClientAPI capi;
    private readonly MechNetworkRenderer? renderer;
    private readonly FieldInfo? rendererGroupsField;
    private readonly FieldInfo? renderedDevicesField;
    private readonly FieldInfo? quantityBlocksField;
    private readonly MethodInfo? renderGroupMethod;
    private bool disabled;
    private bool loggedAvailability;
    private bool loggedFailure;

    public int LastLoadedDeviceCount { get; private set; }
    public int LastRenderedDeviceCount { get; private set; }
    public int LastRendererGroupCount { get; private set; }
    public bool NativeCollectionsRestored { get; private set; } = true;

    public AtlasMechanicalRendererAdapter(ICoreClientAPI capi)
    {
        this.capi = capi;
        try
        {
            MechanicalPowerMod mechanicalPower = capi.ModLoader
                .GetModSystem<MechanicalPowerMod>();
            renderer = mechanicalPower.Renderer;
            if (renderer == null) return;

            rendererGroupsField = FindField(renderer.GetType(), "MechBlockRenderer");
            Type? groupType = rendererGroupsField?.FieldType.IsGenericType == true
                ? rendererGroupsField.FieldType.GetGenericArguments()[0]
                : null;
            renderedDevicesField = groupType == null
                ? null
                : FindField(groupType, "renderedDevices");
            quantityBlocksField = groupType == null
                ? null
                : FindField(groupType, "quantityBlocks");
            renderGroupMethod = groupType?.GetMethod(
                "OnRenderFrame",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(float), typeof(IShaderProgram) },
                null
            );
        }
        catch (Exception exception)
        {
            disabled = true;
            capi.Logger.Warning(
                "[ModernAtlas] Native mechanical models are unavailable for the atlas: {0}",
                exception.Message
            );
        }
    }

    public int Render(
        float deltaTime,
        double[] atlasView,
        float[] atlasProjection,
        double disclosureCenterX,
        double disclosureCenterZ,
        int disclosureRadius,
        AtlasSurfaceHeightTexture? surfaceHeightTexture
    )
    {
        LastLoadedDeviceCount = 0;
        LastRenderedDeviceCount = 0;
        LastRendererGroupCount = 0;
        NativeCollectionsRestored = true;
        if (disabled
            || renderer == null
            || rendererGroupsField == null
            || renderedDevicesField == null
            || quantityBlocksField == null
            || renderGroupMethod == null)
        {
            return 0;
        }

        if (rendererGroupsField.GetValue(renderer) is not IList rendererGroups)
        {
            return 0;
        }

        List<FilteredGroupState> filteredGroups = new(rendererGroups.Count);
        IRenderAPI render = capi.Render;
        FrameBufferRef? atlasFramebuffer = render.CurrentFrameBuffer;
        try
        {
            double radius = Math.Max(GlobalConstants.ChunkSize, disclosureRadius);
            double radiusSquared = radius * radius;
            foreach (object? rendererGroup in rendererGroups)
            {
                if (rendererGroup == null
                    || renderedDevicesField.GetValue(rendererGroup)
                        is not IDictionary loadedDevices)
                {
                    continue;
                }

                IDictionary visibleDevices = (IDictionary)(
                    Activator.CreateInstance(loadedDevices.GetType())
                    ?? throw new InvalidOperationException(
                        "The native mechanical device collection could not be filtered."
                    )
                );
                foreach (DictionaryEntry entry in loadedDevices)
                {
                    LastLoadedDeviceCount++;
                    if (entry.Value is not IMechanicalPowerRenderable device)
                    {
                        continue;
                    }

                    double dx = device.Position.X + 0.5 - disclosureCenterX;
                    double dz = device.Position.Z + 0.5 - disclosureCenterZ;
                    if (dx * dx + dz * dz >= radiusSquared) continue;
                    if (surfaceHeightTexture != null
                        && (!surfaceHeightTexture.TryGetSurfaceHeight(
                                device.Position.X + 0.5,
                                device.Position.Z + 0.5,
                                out int surfaceHeight
                            )
                            || device.Position.InternalY
                                < surfaceHeight - ExteriorSafetyAllowance))
                    {
                        continue;
                    }

                    visibleDevices.Add(entry.Key, entry.Value);
                }

                int originalQuantity = quantityBlocksField.GetValue(rendererGroup)
                    is int quantity
                        ? quantity
                        : loadedDevices.Count;
                filteredGroups.Add(
                    new FilteredGroupState(
                        rendererGroup,
                        loadedDevices,
                        originalQuantity,
                        visibleDevices.Count
                    )
                );
                NativeCollectionsRestored = false;
                renderedDevicesField.SetValue(rendererGroup, visibleDevices);
                quantityBlocksField.SetValue(rendererGroup, visibleDevices.Count);
                LastRenderedDeviceCount += visibleDevices.Count;
                LastRendererGroupCount++;
            }

            if (LastRenderedDeviceCount > 0)
            {
                render.CurrentActiveShader?.Stop();
                render.GLEnableDepthTest();
                render.GLDepthMask(true);
                render.GlColorMask(true, true, true, true);
                render.GlScissorFlag(false);
                render.GlDisableCullFace();
                render.GlToggleBlend(false, EnumBlendMode.Standard);

                // MechNetworkRenderer normally uploads CameraMatrixOriginf.
                // That float matrix belongs to the ordinary world camera and
                // is not the double-precision origin matrix temporarily used
                // by exact atlas chunks. Reusing the network renderer here
                // therefore makes windmills rotate toward the screen and
                // drift as the atlas camera moves. Keep the native instance
                // meshes and poses, but draw their renderer groups with the
                // explicit atlas matrices. Their transforms are already
                // relative to Player.CameraPos, which is also the origin used
                // when atlasView was built.
                IShaderProgram shader = capi.Shader.GetProgramByName("instanced");
                if (shader.Disposed)
                {
                    shader = capi.Shader.GetProgramByName("instanced");
                }
                shader.Use();
                shader.BindTexture2D(
                    "tex",
                    capi.BlockTextureAtlas.Positions[0].atlasTextureId,
                    0
                );
                shader.Uniform("rgbaFogIn", render.FogColor);
                shader.Uniform("rgbaAmbientIn", render.AmbientColor);
                shader.Uniform("fogMinIn", render.FogMin);
                shader.Uniform("fogDensityIn", render.FogDensity);
                shader.UniformMatrix("projectionMatrix", atlasProjection);
                shader.UniformMatrix(
                    "modelViewMatrix",
                    Array.ConvertAll(atlasView, value => (float)value)
                );

                foreach (FilteredGroupState state in filteredGroups)
                {
                    if (state.VisibleDeviceCount <= 0) continue;
                    renderGroupMethod.Invoke(
                        state.RendererGroup,
                        new object[] { deltaTime, shader }
                    );
                }
                shader.Stop();
            }

            if (!loggedAvailability)
            {
                loggedAvailability = true;
                capi.Logger.Notification(
                    "[ModernAtlas] Atlas mechanical-model pass is active: {0} of {1} loaded devices in {2} renderer groups are inside the current safe atlas area.",
                    LastRenderedDeviceCount,
                    LastLoadedDeviceCount,
                    LastRendererGroupCount
                );
            }
            return LastRenderedDeviceCount;
        }
        catch (Exception exception)
        {
            disabled = true;
            if (!loggedFailure)
            {
                loggedFailure = true;
                capi.Logger.Error(
                    "[ModernAtlas] Native mechanical models were disabled for this session without disabling exact terrain: {0}",
                    exception.Message
                );
            }
            return 0;
        }
        finally
        {
            NativeCollectionsRestored = true;
            for (int index = filteredGroups.Count - 1; index >= 0; index--)
            {
                FilteredGroupState state = filteredGroups[index];
                try
                {
                    renderedDevicesField.SetValue(
                        state.RendererGroup,
                        state.Devices
                    );
                    quantityBlocksField.SetValue(
                        state.RendererGroup,
                        state.QuantityBlocks
                    );
                }
                catch (Exception exception)
                {
                    disabled = true;
                    if (!loggedFailure)
                    {
                        loggedFailure = true;
                        capi.Logger.Error(
                            "[ModernAtlas] Native mechanical renderer state could not be restored; its atlas pass was disabled without disabling exact terrain: {0}",
                            exception.Message
                        );
                    }
                    NativeCollectionsRestored = false;
                }
            }

            try
            {
                render.CurrentActiveShader?.Stop();
            }
            catch
            {
                // A partially torn-down optional native shader must not block
                // restoration of the atlas framebuffer and explicit state.
            }
            try
            {
                render.CurrentFrameBuffer = atlasFramebuffer;
                render.GLEnableDepthTest();
                render.GLDepthMask(true);
                render.GlColorMask(true, true, true, true);
                render.GlScissorFlag(false);
                render.GlEnableCullFace();
                render.GlToggleBlend(false, EnumBlendMode.Standard);
            }
            catch (Exception exception)
            {
                disabled = true;
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    capi.Logger.Error(
                        "[ModernAtlas] Native mechanical render-state handoff failed; its atlas pass was disabled without disabling exact terrain: {0}",
                        exception.Message
                    );
                }
            }
        }
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(
                name,
                BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly
            );
            if (field != null) return field;
        }
        return null;
    }

    private readonly record struct FilteredGroupState(
        object RendererGroup,
        IDictionary Devices,
        int QuantityBlocks,
        int VisibleDeviceCount
    );
}
