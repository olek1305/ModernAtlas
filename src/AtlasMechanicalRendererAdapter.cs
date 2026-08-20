using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
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

    [ThreadStatic]
    private static IReadOnlyDictionary<IMechanicalPowerRenderable, float>?
        activeScreenshotAngles;

    [ThreadStatic]
    private static int activeScreenshotAngleReadCount;

    private readonly ICoreClientAPI capi;
    private readonly Harmony harmony;
    private readonly MechNetworkRenderer? renderer;
    private readonly FieldInfo? rendererGroupsField;
    private readonly FieldInfo? renderedDevicesField;
    private readonly FieldInfo? quantityBlocksField;
    private readonly MethodInfo? renderGroupMethod;
    private readonly HashSet<MethodInfo> patchedAngleGetters = new();
    private Dictionary<IMechanicalPowerRenderable, float>?
        pausedAnimationAngles;
    private Dictionary<IMechanicalPowerRenderable, float>? screenshotAngles;
    private bool disabled;
    private bool loggedAvailability;
    private bool loggedFailure;

    public int LastLoadedDeviceCount { get; private set; }
    public int LastRenderedDeviceCount { get; private set; }
    public int LastRendererGroupCount { get; private set; }
    public bool NativeCollectionsRestored { get; private set; } = true;
    public bool ScreenshotFreezeActive => screenshotAngles != null;
    public int ScreenshotFrozenDeviceCount => screenshotAngles?.Count ?? 0;
    public int LastScreenshotFrozenAngleReadCount { get; private set; }
    public int ScreenshotFrozenAngleReadCount { get; private set; }

    public AtlasMechanicalRendererAdapter(ICoreClientAPI capi, Harmony harmony)
    {
        this.capi = capi;
        this.harmony = harmony;
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

    /// <summary>
    /// Captures the native mechanism angles once for an entire tiled PNG job.
    /// The world simulation remains untouched. During the atlas-only native
    /// renderer call a narrowly scoped Harmony prefix returns these captured
    /// values, then the override is cleared before ordinary rendering resumes.
    /// </summary>
    public bool TryBeginScreenshotFreeze(out string diagnostic)
    {
        EndScreenshotFreeze();
        LastScreenshotFrozenAngleReadCount = 0;
        ScreenshotFrozenAngleReadCount = 0;
        if (!TryCaptureCurrentAngles(
                pausedAnimationAngles,
                out Dictionary<IMechanicalPowerRenderable, float> captured,
                out diagnostic
            ))
        {
            return false;
        }

        screenshotAngles = captured;
        diagnostic = captured.Count == 1
            ? "1 native mechanical pose captured"
            : $"{captured.Count} native mechanical poses captured";
        return true;
    }

    public void EndScreenshotFreeze()
    {
        screenshotAngles = null;
        activeScreenshotAngles = null;
        activeScreenshotAngleReadCount = 0;
    }

    public void ClearAnimationFreezes()
    {
        EndScreenshotFreeze();
        pausedAnimationAngles = null;
    }

    public int Render(
        float deltaTime,
        double[] atlasView,
        float[] atlasProjection,
        double disclosureCenterX,
        double disclosureCenterZ,
        int disclosureRadius,
        AtlasSurfaceHeightTexture? surfaceHeightTexture,
        bool animationsEnabled
    )
    {
        LastLoadedDeviceCount = 0;
        LastRenderedDeviceCount = 0;
        LastRendererGroupCount = 0;
        LastScreenshotFrozenAngleReadCount = 0;
        NativeCollectionsRestored = true;
        if (animationsEnabled)
        {
            pausedAnimationAngles = null;
        }
        if (disabled
            || renderer == null
            || rendererGroupsField == null
            || renderedDevicesField == null
            || quantityBlocksField == null
            || renderGroupMethod == null)
        {
            return 0;
        }

        if (!animationsEnabled && pausedAnimationAngles == null)
        {
            if (!TryCaptureCurrentAngles(
                    null,
                    out Dictionary<IMechanicalPowerRenderable, float> captured,
                    out string freezeDiagnostic
                ))
            {
                disabled = true;
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    capi.Logger.Error(
                        "[ModernAtlas] Native mechanical animations could not be frozen; their atlas pass was disabled without affecting the world: {0}",
                        freezeDiagnostic
                    );
                }
                return 0;
            }
            pausedAnimationAngles = captured;
            capi.Logger.Notification(
                "[ModernAtlas] Atlas mechanical animations paused on one captured pose: {0} devices.",
                captured.Count
            );
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

                    if (screenshotAngles == null
                        && pausedAnimationAngles != null
                        && !pausedAnimationAngles.ContainsKey(device))
                    {
                        EnsureAngleGetterPatched(device);
                        pausedAnimationAngles.Add(device, device.AngleRad);
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

                    IReadOnlyDictionary<IMechanicalPowerRenderable, float>?
                        effectiveFrozenAngles = screenshotAngles
                            ?? pausedAnimationAngles;
                    if (screenshotAngles != null
                        && !effectiveFrozenAngles!.ContainsKey(device))
                    {
                        // A mechanism loaded after tile 0 is not part of the
                        // captured scene. Excluding it avoids introducing a
                        // different object set halfway across the PNG.
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

                activeScreenshotAngles = screenshotAngles
                    ?? pausedAnimationAngles;
                activeScreenshotAngleReadCount = 0;
                try
                {
                    foreach (FilteredGroupState state in filteredGroups)
                    {
                        if (state.VisibleDeviceCount <= 0) continue;
                        renderGroupMethod.Invoke(
                            state.RendererGroup,
                            new object[] { deltaTime, shader }
                        );
                    }
                    LastScreenshotFrozenAngleReadCount =
                        activeScreenshotAngleReadCount;
                    ScreenshotFrozenAngleReadCount +=
                        LastScreenshotFrozenAngleReadCount;
                }
                finally
                {
                    activeScreenshotAngles = null;
                    activeScreenshotAngleReadCount = 0;
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

    private bool TryCaptureCurrentAngles(
        IReadOnlyDictionary<IMechanicalPowerRenderable, float>? preferredAngles,
        out Dictionary<IMechanicalPowerRenderable, float> captured,
        out string diagnostic
    )
    {
        captured = new Dictionary<IMechanicalPowerRenderable, float>(
            ReferenceEqualityComparer.Instance
        );
        if (disabled
            || renderer == null
            || rendererGroupsField == null
            || renderedDevicesField == null)
        {
            diagnostic = "native mechanical rendering is unavailable, so no animated mechanisms are present";
            return true;
        }

        try
        {
            if (rendererGroupsField.GetValue(renderer) is not IList rendererGroups)
            {
                throw new InvalidOperationException(
                    "The native mechanical renderer group collection is unavailable."
                );
            }

            foreach (object? rendererGroup in rendererGroups)
            {
                if (rendererGroup == null
                    || renderedDevicesField.GetValue(rendererGroup)
                        is not IDictionary loadedDevices)
                {
                    continue;
                }

                foreach (DictionaryEntry entry in loadedDevices)
                {
                    if (entry.Value is not IMechanicalPowerRenderable device
                        || captured.ContainsKey(device))
                    {
                        continue;
                    }

                    EnsureAngleGetterPatched(device);
                    captured.Add(
                        device,
                        preferredAngles != null
                            && preferredAngles.TryGetValue(
                                device,
                                out float preferredAngle
                            )
                                ? preferredAngle
                                : device.AngleRad
                    );
                }
            }

            diagnostic = captured.Count == 1
                ? "1 native mechanical pose captured"
                : $"{captured.Count} native mechanical poses captured";
            return true;
        }
        catch (Exception exception)
        {
            captured.Clear();
            diagnostic = exception.Message;
            return false;
        }
    }

    private void EnsureAngleGetterPatched(IMechanicalPowerRenderable device)
    {
        MethodInfo angleGetter = ResolveAngleGetter(device.GetType());
        if (patchedAngleGetters.Contains(angleGetter)) return;

        MethodInfo prefix = typeof(AtlasMechanicalRendererAdapter)
            .GetMethod(
                nameof(UseFrozenAtlasAngle),
                BindingFlags.Static | BindingFlags.NonPublic
            )
            ?? throw new MissingMethodException(nameof(UseFrozenAtlasAngle));
        harmony.Patch(
            angleGetter,
            prefix: new HarmonyMethod(prefix)
        );
        // Record the method only after Harmony accepted it. A failed patch
        // must remain retryable instead of silently producing live angles on
        // every later capture attempt.
        patchedAngleGetters.Add(angleGetter);
    }

    private static MethodInfo ResolveAngleGetter(Type deviceType)
    {
        // GetInterfaceMap can return a virtual slot projected onto the most
        // derived runtime type even though that type does not declare a body.
        // Harmony correctly rejects such a MethodInfo and asks for the
        // declared implementation (for windmills this is
        // BEBehaviorMPRotor.get_AngleRad). Walk the hierarchy explicitly and
        // choose the first concrete declared getter.
        for (Type? current = deviceType; current != null; current = current.BaseType)
        {
            foreach (MethodInfo method in current.GetMethods(
                BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly
            ))
            {
                if (method.IsAbstract
                    || method.ReturnType != typeof(float)
                    || method.GetParameters().Length != 0
                    || !(method.Name.Equals(
                            "get_AngleRad",
                            StringComparison.Ordinal
                        )
                        || method.Name.EndsWith(
                            ".get_AngleRad",
                            StringComparison.Ordinal
                        )))
                {
                    continue;
                }
                return method;
            }
        }

        MethodInfo interfaceGetter = typeof(IMechanicalPowerRenderable)
            .GetProperty(nameof(IMechanicalPowerRenderable.AngleRad))
            ?.GetMethod
            ?? throw new MissingMethodException(
                typeof(IMechanicalPowerRenderable).FullName,
                "get_AngleRad"
            );
        InterfaceMapping mapping = deviceType.GetInterfaceMap(
            typeof(IMechanicalPowerRenderable)
        );
        for (int index = 0; index < mapping.InterfaceMethods.Length; index++)
        {
            if (mapping.InterfaceMethods[index] == interfaceGetter)
            {
                MethodInfo target = mapping.TargetMethods[index];
                if (!target.IsAbstract) return target;
                break;
            }
        }
        throw new MissingMethodException(
            deviceType.FullName,
            "IMechanicalPowerRenderable.get_AngleRad"
        );
    }

    private static bool UseFrozenAtlasAngle(
        object __instance,
        ref float __result
    )
    {
        if (__instance is not IMechanicalPowerRenderable device
            || activeScreenshotAngles == null
            || !activeScreenshotAngles.TryGetValue(device, out float angle))
        {
            return true;
        }

        __result = angle;
        activeScreenshotAngleReadCount++;
        return false;
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
