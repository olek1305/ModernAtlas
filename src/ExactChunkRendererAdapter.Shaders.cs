using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
namespace ModernAtlas;

/// <summary>
/// The atlas chunk-shader variant: uniform configuration per atlas pass, the
/// switch-off path that returns the ordinary world to its native code, and the
/// GLSL injection that adds the atlas boundary, cave, layer and vegetation
/// filters to the engine's compiled chunk programs.
/// </summary>
internal sealed partial class ExactChunkRendererAdapter
{
    private bool ConfigureAtlasFilters(
        AtlasSurfaceHeightTexture? surfaceHeightTexture,
        AtlasMapLayerTexture? mapLayerTexture,
        float mapLayerOpacity,
        Vec3d cameraPosition,
        bool enabled,
        bool hideUndergroundCaves,
        bool concealSurvivalOres,
        bool hideVegetation,
        int vegetationPass,
        int disclosureRadius,
        bool requireOpaqueDepth
    )
    {
        if (!enabled)
        {
            DisableAtlasFilterUniforms();
            return true;
        }

        if ((hideUndergroundCaves
                && (surfaceHeightTexture?.Ready != true
                    || surfaceHeightTexture.TextureId <= 0))
            || (concealSurvivalOres && !oreTextureReplacement.Ready)
            || (hideVegetation && !vegetationTextureMask.Ready)
            || !EnsureAtlasFilterShaders())
        {
            atlasUniformsActive = false;
            return false;
        }

        bool applyMapLayer = mapLayerTexture?.Ready == true
            && mapLayerTexture.TextureId > 0
            && mapLayerTexture.Layer != AtlasMapLayer.TexturedTerrain;

        try
        {
            atlasUniformsActive = true;
            foreach (EnumShaderProgram program in AtlasFilterPrograms)
            {
                IShaderProgram shader = atlasFilterShaders[program].Shader;
                capi.Render.CurrentActiveShader?.Stop();
                shader.Use();
                bool applyCaveFilter = hideUndergroundCaves
                    && !DeveloperDisableCaveFilter;
                shader.Uniform("atlasHideCaves", applyCaveFilter ? 1 : 0);
                if (shader.HasUniform("atlasRenderingEnabled"))
                {
                    // Keep this separate from atlasFilteringEnabled. The
                    // developer may disable visual filtering while the atlas
                    // is still rendering, but ordinary world rendering must
                    // bypass every atlas-only branch in the injected program.
                    shader.Uniform("atlasRenderingEnabled", 1);
                }
                if (shader.HasUniform("atlasBoundaryEnabled"))
                {
                    shader.Uniform(
                        "atlasBoundaryEnabled",
                        disclosureRadius > 0 ? 1 : 0
                    );
                    shader.Uniform(
                        "atlasDisclosureCenterXZ",
                        (float)capi.World.Player.Entity.Pos.X,
                        (float)capi.World.Player.Entity.Pos.Z
                    );
                    shader.Uniform(
                        "atlasDisclosureRadius",
                        (float)Math.Max(GlobalConstants.ChunkSize, disclosureRadius)
                    );
                }
                if (shader.HasUniform("atlasCompleteBoundaryEnabled"))
                {
                    shader.Uniform(
                        "atlasCompleteBoundaryEnabled",
                        atlasCompleteBoundaryEnabled ? 1 : 0
                    );
                    shader.Uniform(
                        "atlasCompleteBoundaryMinXZ",
                        atlasCompleteMinimumChunkX * (float)GlobalConstants.ChunkSize,
                        atlasCompleteMinimumChunkZ * (float)GlobalConstants.ChunkSize
                    );
                    shader.Uniform(
                        "atlasCompleteBoundaryMaxXZ",
                        (atlasCompleteMaximumChunkX + 1) * (float)GlobalConstants.ChunkSize,
                        (atlasCompleteMaximumChunkZ + 1) * (float)GlobalConstants.ChunkSize
                    );
                }
                if (shader.HasUniform("atlasRequireOpaqueDepth"))
                {
                    bool bindOpaqueDepth = requireOpaqueDepth;
                    FrameBufferRef primaryFramebuffer = capi.Render.FrameBuffers[
                        (int)EnumFrameBuffer.Primary
                    ];
                    bool hasOpaqueDepth = bindOpaqueDepth
                        && primaryFramebuffer.DepthTextureId > 0;
                    shader.Uniform(
                        "atlasRequireOpaqueDepth",
                        hasOpaqueDepth ? 1 : 0
                    );
                    if (hasOpaqueDepth)
                    {
                        shader.BindTexture2D(
                            "atlasOpaqueDepthTex",
                            primaryFramebuffer.DepthTextureId,
                            OpaqueDepthTextureUnit
                        );
                    }
                }
                if (shader.HasUniform("atlasSeaLevel"))
                {
                    shader.Uniform("atlasSeaLevel", (float)capi.World.SeaLevel);
                }
                shader.Uniform(
                    "atlasConcealOres",
                    concealSurvivalOres ? 1 : 0
                );
                if (shader.HasUniform("atlasHideVegetation"))
                {
                    shader.Uniform("atlasHideVegetation", hideVegetation ? 1 : 0);
                }
                if (shader.HasUniform("atlasVegetationPass"))
                {
                    shader.Uniform("atlasVegetationPass", vegetationPass);
                }
                if (shader.HasUniform("atlasVegetationMaskEnabled"))
                {
                    shader.Uniform(
                        "atlasVegetationMaskEnabled",
                        vegetationTextureMask.Ready ? 1 : 0
                    );
                }
                if (shader.HasUniform("atlasTextureMipBias"))
                {
                    shader.Uniform("atlasTextureMipBias", atlasTextureMipBias);
                }
                if (shader.HasUniform("atlasMinimumTerrainBrightness"))
                {
                    shader.Uniform("atlasMinimumTerrainBrightness", 0.20f);
                }
                if (shader.HasUniform("atlasFilteringEnabled"))
                {
                    shader.Uniform(
                        "atlasFilteringEnabled",
                        DeveloperDisableFiltering ? 0 : 1
                    );
                }
                if (shader.HasUniform("atlasVegetationMipBias"))
                {
                    shader.Uniform("atlasVegetationMipBias", atlasVegetationMipBias);
                }
                if (shader.HasUniform("atlasVegetationAlphaCoverage"))
                {
                    shader.Uniform(
                        "atlasVegetationAlphaCoverage",
                        atlasVegetationAlphaCoverage
                    );
                }
                if (shader.HasUniform("atlasVegetationDebugMode"))
                {
                    shader.Uniform(
                        "atlasVegetationDebugMode",
                        DeveloperVegetationDebugMode
                    );
                }
                if (shader.HasUniform("atlasCaveConcealmentColor"))
                {
                    shader.Uniform(
                        "atlasCaveConcealmentColor",
                        ScaleColor(CaveConcealmentColor, atlasCaveMaskBrightness)
                    );
                }
                if (surfaceHeightTexture?.Ready == true
                    && surfaceHeightTexture.TextureId > 0)
                {
                    shader.BindTexture2D(
                        "atlasSurfaceHeightTex",
                        surfaceHeightTexture.TextureId,
                        CaveFilterTextureUnit
                    );
                    shader.Uniform(
                        "atlasSurfaceOriginXZ",
                        (float)surfaceHeightTexture.OriginX,
                        (float)surfaceHeightTexture.OriginZ
                    );
                    shader.Uniform(
                        "atlasSurfaceSampleSize",
                        (float)AtlasSurfaceHeightTexture.HorizontalSampleSize
                    );
                    shader.Uniform("atlasVisibleSubsurfaceDepth", VisibleSubsurfaceDepth);
                    if (shader.HasUniform("atlasCaveConcealmentDepth"))
                    {
                        shader.Uniform(
                            "atlasCaveConcealmentDepth",
                            CaveEntranceConcealmentDepth
                        );
                    }
                }
                if (shader.HasUniform("atlasLayerEnabled"))
                {
                    shader.Uniform("atlasLayerEnabled", applyMapLayer ? 1 : 0);
                    if (applyMapLayer && mapLayerTexture != null)
                    {
                        shader.BindTexture2D(
                            "atlasLayerTex",
                            mapLayerTexture.TextureId,
                            MapLayerTextureUnit
                        );
                        shader.Uniform(
                            "atlasLayerOriginXZ",
                            (float)mapLayerTexture.OriginX,
                            (float)mapLayerTexture.OriginZ
                        );
                        shader.Uniform(
                            "atlasLayerSampleSize",
                            (float)AtlasMapLayerTexture.HorizontalSampleSize
                        );
                            shader.Uniform(
                                "atlasLayerOpacity",
                                Math.Clamp(mapLayerOpacity, 0f, 1f)
                            );
                            if (shader.HasUniform("atlasLayerContours"))
                            {
                                shader.Uniform(
                                    "atlasLayerContours",
                                    mapLayerTexture.ContoursEnabled ? 1 : 0
                                );
                            }
                    }
                }
                shader.Uniform(
                    "atlasWorldOffset",
                    (float)cameraPosition.X,
                    (float)cameraPosition.Y,
                    (float)cameraPosition.Z
                );
                if (shader.HasUniform("atlasDisableHorizonFade"))
                {
                    shader.Uniform("atlasDisableHorizonFade", 1);
                }
                if (shader.HasUniform("atlasDisableLod0Fade"))
                {
                    // Leaves and other exact LOD0 meshes are already bounded
                    // by the game's loaded chunk set. The normal perspective
                    // shader fade must not remove them from a distant atlas
                    // view while leaving their non-LOD trunks behind.
                    shader.Uniform("atlasDisableLod0Fade", 1);
                }
                shader.Stop();
            }
            return true;
        }
        catch
        {
            DisableAtlasFilterUniforms();
            throw;
        }
    }

    private bool EnsureAtlasFilterShaders()
    {
        foreach (EnumShaderProgram program in AtlasFilterPrograms)
        {
            if (!EnsureAtlasFilterShader(
                program,
                program != EnumShaderProgram.Chunktransparent
            ))
            {
                return false;
            }
        }

        if (!loggedCaveFilterReady)
        {
            loggedCaveFilterReady = true;
            capi.Logger.Notification(
                "[ModernAtlas] Opaque terrain above sea level keeps complete exact walls; below sea level it keeps its {0}-block exterior layer with a {1:0.0}-block neutral band, while transparent and liquid geometry is filtered at every height.",
                VisibleSubsurfaceDepth,
                CaveEntranceConcealmentDepth
            );
        }
        return true;
    }

    private bool EnsureAtlasFilterShader(
        EnumShaderProgram program,
        bool supportsBoundaryColor
    )
    {
        if (atlasFilterInjectionFailures.Contains(program)) return false;

        IShaderProgram shader = capi.Render.GetEngineShader(program);
        if (shader.Disposed || shader.FragmentShader == null) return false;
        string source = shader.FragmentShader.Code ?? "";
        if (atlasFilterShaders.TryGetValue(program, out AtlasFilterShaderState? state)
            && ReferenceEquals(shader, state.Shader)
            && source.Contains(AtlasFilterMarker, StringComparison.Ordinal))
        {
            return shader.HasUniform("atlasHideCaves")
                && shader.HasUniform("atlasBoundaryEnabled")
                && shader.HasUniform("atlasDisclosureCenterXZ")
                && shader.HasUniform("atlasDisclosureRadius")
                && shader.HasUniform("atlasConcealOres")
                && shader.HasUniform("atlasRenderingEnabled")
                && shader.HasUniform("atlasHideVegetation")
                && shader.HasUniform("atlasVegetationMaskTex")
                && shader.HasUniform("atlasFilteringEnabled")
                && shader.HasUniform("atlasVegetationMipBias")
                && shader.HasUniform("atlasVegetationAlphaCoverage")
                && shader.HasUniform("atlasVegetationDebugMode")
                && shader.HasUniform("atlasVegetationPass")
                && shader.HasUniform("atlasVegetationMaskEnabled")
                && shader.HasUniform("atlasRequireOpaqueDepth");
        }

        if (state != null && !ReferenceEquals(shader, state.Shader))
        {
            RestoreAtlasFilterSource(program);
        }

        string? injected = InjectAtlasFilter(
            source,
            supportsBoundaryColor,
            program == EnumShaderProgram.Chunktopsoil
        );
        if (injected == null)
        {
            atlasFilterInjectionFailures.Add(program);
            capi.Logger.Error(
                "[ModernAtlas] Could not locate the main function in engine shader {0}.",
                program
            );
            return false;
        }

        capi.Render.CurrentActiveShader?.Stop();
        shader.FragmentShader.Code = injected;
        if (!shader.Compile())
        {
            shader.FragmentShader.Code = source;
            shader.Compile();
            atlasFilterInjectionFailures.Add(program);
            capi.Logger.Error(
                "[ModernAtlas] Failed to compile the atlas safety filter for engine shader {0}; the original shader was restored.",
                program
            );
            return false;
        }

        atlasFilterShaders[program] = new AtlasFilterShaderState(shader, source);
        return shader.HasUniform("atlasHideCaves")
            && shader.HasUniform("atlasBoundaryEnabled")
            && shader.HasUniform("atlasDisclosureCenterXZ")
            && shader.HasUniform("atlasDisclosureRadius")
            && shader.HasUniform("atlasConcealOres")
            && shader.HasUniform("atlasRenderingEnabled")
            && shader.HasUniform("atlasHideVegetation")
            && shader.HasUniform("atlasVegetationMaskTex")
            && shader.HasUniform("atlasFilteringEnabled")
            && shader.HasUniform("atlasVegetationMipBias")
            && shader.HasUniform("atlasVegetationAlphaCoverage")
            && shader.HasUniform("atlasVegetationDebugMode")
            && shader.HasUniform("atlasVegetationPass")
            && shader.HasUniform("atlasVegetationMaskEnabled")
            && shader.HasUniform("atlasRequireOpaqueDepth");
    }

    private void DisableAtlasFilterUniforms()
    {
        atlasUniformsActive = false;
        DefaultShaderUniforms uniforms = capi.Render.ShaderUniforms;
        if (uniforms.ColorMapRects4 == null
            || uniforms.ColorMapRects4.Length < 40 * 4)
        {
            return;
        }

        foreach (AtlasFilterShaderState state in atlasFilterShaders.Values)
        {
            IShaderProgram shader = state.Shader;
            if (shader.Disposed) continue;
            try
            {
                capi.Render.CurrentActiveShader?.Stop();
                shader.Use();
                if (shader.HasUniform("atlasRenderingEnabled"))
                {
                    // Disable the master gate first. Even if a later cleanup
                    // upload is rejected during a transient client handoff,
                    // the normal world can never execute atlas-only code.
                    shader.Uniform("atlasRenderingEnabled", 0);
                }
                if (shader.HasUniform("atlasHideCaves"))
                {
                    shader.Uniform("atlasHideCaves", 0);
                }
                if (shader.HasUniform("atlasBoundaryEnabled"))
                {
                    shader.Uniform("atlasBoundaryEnabled", 0);
                }
                if (shader.HasUniform("atlasCompleteBoundaryEnabled"))
                {
                    shader.Uniform("atlasCompleteBoundaryEnabled", 0);
                }
                if (shader.HasUniform("atlasRequireOpaqueDepth"))
                {
                    shader.Uniform("atlasRequireOpaqueDepth", 0);
                }
                if (shader.HasUniform("atlasConcealOres"))
                {
                    shader.Uniform("atlasConcealOres", 0);
                }
                if (shader.HasUniform("atlasHideVegetation"))
                {
                    shader.Uniform("atlasHideVegetation", 0);
                }
                if (shader.HasUniform("atlasFilteringEnabled"))
                {
                    shader.Uniform("atlasFilteringEnabled", 0);
                }
                if (shader.HasUniform("atlasMinimumTerrainBrightness"))
                {
                    shader.Uniform("atlasMinimumTerrainBrightness", 0f);
                }
                if (shader.HasUniform("atlasVegetationMipBias"))
                {
                    shader.Uniform("atlasVegetationMipBias", 0f);
                }
                if (shader.HasUniform("atlasVegetationAlphaCoverage"))
                {
                    shader.Uniform("atlasVegetationAlphaCoverage", 0f);
                }
                if (shader.HasUniform("atlasVegetationDebugMode"))
                {
                    shader.Uniform("atlasVegetationDebugMode", 0);
                }
                if (shader.HasUniform("atlasVegetationPass"))
                {
                    shader.Uniform("atlasVegetationPass", 0);
                }
                if (shader.HasUniform("atlasVegetationMaskEnabled"))
                {
                    shader.Uniform("atlasVegetationMaskEnabled", 0);
                }
                if (shader.HasUniform("atlasLayerEnabled"))
                {
                    shader.Uniform("atlasLayerEnabled", 0);
                }
                if (shader.HasUniform("atlasLayerContours"))
                {
                    shader.Uniform("atlasLayerContours", 0);
                }
                if (shader.HasUniform("atlasDisableHorizonFade"))
                {
                    // Keep this override at its safe value. Vintage Story's
                    // compiled atlas variant can produce a bright red horizon
                    // silhouette when haxyFade is re-enabled after closing;
                    // the master gate already bypasses every other atlas-only
                    // branch, so do not re-enable this one in the world.
                    shader.Uniform("atlasDisableHorizonFade", 1);
                }
                if (shader.HasUniform("atlasDisableLod0Fade"))
                {
                    shader.Uniform("atlasDisableLod0Fade", 0);
                }
                shader.Stop();
            }
            catch
            {
                // The client may already be tearing down its GL context.
            }
        }
    }

    private void RestoreAtlasFilterSources()
    {
        foreach (EnumShaderProgram program in AtlasFilterPrograms)
        {
            RestoreAtlasFilterSource(program);
        }
    }

    private void RestoreAtlasFilterSource(EnumShaderProgram program)
    {
        if (!atlasFilterShaders.Remove(program, out AtlasFilterShaderState? state)) return;
        if (state.Shader.FragmentShader != null
            && state.Shader.FragmentShader.Code?.Contains(
                AtlasFilterMarker,
                StringComparison.Ordinal
            ) == true)
        {
            // The compiled program is safe between atlas passes because its
            // master atlas gate is reset to zero. Restoring the source still
            // ensures a later engine-owned shader rebuild compiles the
            // unmodified game shader.
            state.Shader.FragmentShader.Code = state.OriginalFragmentCode;
        }
    }

    private static string? InjectAtlasFilter(
        string source,
        bool supportsBoundaryColor,
        bool usesTopsoilUv
    )
    {
        int mainIndex = source.LastIndexOf("void main", StringComparison.Ordinal);
        if (mainIndex < 0) return null;

        int nameIndex = mainIndex + "void ".Length;
        if (nameIndex + "main".Length > source.Length
            || !source.AsSpan(nameIndex, "main".Length).SequenceEqual("main".AsSpan()))
        {
            return null;
        }

        string renamed = source.Remove(nameIndex, "main".Length)
            .Insert(nameIndex, "modernAtlasOriginalMain");
        const string terrainSample = "texture(terrainTex, uv)";
        if (!renamed.Contains(terrainSample, StringComparison.Ordinal))
        {
            return null;
        }
        renamed = renamed.Replace(
            terrainSample,
            "modernAtlasSampleTerrain(terrainTex, uv)",
            StringComparison.Ordinal
        );
        const string topsoilGrassSample =
            "texture(terrainTex, uv2 + vec2(blockTextureSize.x * normal.y, 0))";
        if (renamed.Contains(topsoilGrassSample, StringComparison.Ordinal))
        {
            renamed = renamed.Replace(
                topsoilGrassSample,
                "modernAtlasSampleTerrain(terrainTex, uv2 + vec2(blockTextureSize.x * normal.y, 0))",
                StringComparison.Ordinal
            );
        }

        // chunkopaque/chunktopsoil calculate their vertex alpha from the
        // normal camera's view-distance fade. At the atlas disclosure edge
        // that fade drives otherwise valid foliage to zero, so tiny camera
        // changes make the alpha test pop. Preserve the texture's authored
        // alpha while removing only that camera-distance multiplier inside
        // the atlas shader variant. The ordinary world shader source is not
        // modified by this replacement.
        const string opaqueColorExpression =
            "getColorMapped(terrainTexLinear, modernAtlasSampleTerrain(terrainTex, uv)) * rgba";
        renamed = renamed.Replace(
            opaqueColorExpression,
            "getColorMapped(terrainTexLinear, modernAtlasSampleTerrain(terrainTex, uv))"
                + " * vec4(rgba.rgb, atlasFilteringEnabled > 0 ? 1.0 : rgba.a)",
            StringComparison.Ordinal
        );
        const string topsoilBrownExpression =
            "modernAtlasSampleTerrain(terrainTex, uv) * rgba";
        renamed = renamed.Replace(
            topsoilBrownExpression,
            "modernAtlasSampleTerrain(terrainTex, uv)"
                + " * vec4(rgba.rgb, atlasFilteringEnabled > 0 ? 1.0 : rgba.a)",
            StringComparison.Ordinal
        );
        const string topsoilGrassExpression =
            "getColorMapped(terrainTexLinear, modernAtlasSampleTerrain(terrainTex, uv2 + vec2(blockTextureSize.x * normal.y, 0))) * rgba";
        renamed = renamed.Replace(
            topsoilGrassExpression,
            "getColorMapped(terrainTexLinear, modernAtlasSampleTerrain(terrainTex, uv2 + vec2(blockTextureSize.x * normal.y, 0)))"
                + " * vec4(rgba.rgb, atlasFilteringEnabled > 0 ? 1.0 : rgba.a)",
            StringComparison.Ordinal
        );
        // chunktopsoil carries its perspective-camera distance fade in the
        // separate rgbaFog.a varying. Replace that alpha only in the atlas
        // branch; the ordinary world keeps the native horizon/LOD fade.
        renamed = renamed.Replace(
            "outColor.a = rgbaFog.a;",
            "outColor.a = atlasFilteringEnabled > 0 ? 1.0 : rgbaFog.a;",
            StringComparison.Ordinal
        );
        const string transparentColorExpression =
            "rgba * getColorMapped(terrainTex, modernAtlasSampleTerrain(terrainTex, uv))";
        renamed = renamed.Replace(
            transparentColorExpression,
            "vec4(rgba.rgb, atlasFilteringEnabled > 0 ? 1.0 : rgba.a)"
                + " * getColorMapped(terrainTex, modernAtlasSampleTerrain(terrainTex, uv))",
            StringComparison.Ordinal
        );
        renamed = renamed.Insert(
            mainIndex,
            "uniform int atlasRenderingEnabled;\n"
                + "uniform int atlasFilteringEnabled;\n"
                + "uniform float atlasMinimumTerrainBrightness;\n"
                + "uniform float atlasVegetationMipBias;\n"
                + "uniform float atlasVegetationAlphaCoverage;\n"
                + "uniform int atlasVegetationDebugMode;\n"
                + "uniform int atlasVegetationPass;\n"
                + "uniform int atlasVegetationMaskEnabled;\n"
                + "vec4 modernAtlasSampleTerrain(sampler2D sourceTexture, vec2 sourceUv);\n"
                + "bool modernAtlasIsWindVegetation();\n"
                + "float modernAtlasAlphaTestThreshold(float alphaValue, float baseThreshold);\n"
                + "vec4 modernAtlasApplyFogAndDirectionalWithNormal(vec4 targetColor, float fogAmount, vec3 surfaceNormal, float normalShadeIntensity, float minimumNormalShade, vec3 fragmentWorldPosition);\n"
                + "void modernAtlasClampMinimumBrightness(inout vec4 targetColor);\n"
                + "vec4 modernAtlasVegetationDebugColor(vec4 color, float alphaValue, float threshold, float lodFadeValue);\n\n"
        );
        const string lod0FadeTerm = "- lod0Fade";
        if (renamed.Contains(lod0FadeTerm, StringComparison.Ordinal))
        {
            renamed = renamed.Replace(
                lod0FadeTerm,
                DeveloperPreserveVegetationLod0
                    ? "- (atlasDisableLod0Fade > 0 && !modernAtlasIsWindVegetation() ? 0.0 : lod0Fade)"
                    : "- (atlasDisableLod0Fade > 0 ? 0.0 : lod0Fade)",
                StringComparison.Ordinal
            );
            renamed = renamed.Insert(
                mainIndex,
                "uniform int atlasDisableLod0Fade;\n\n"
            );
        }
        const string horizonFadeCondition = "if (haxyFade > 0)";
        if (renamed.Contains(horizonFadeCondition, StringComparison.Ordinal))
        {
            renamed = renamed.Replace(
                horizonFadeCondition,
                "if (haxyFade > 0 && atlasDisableHorizonFade == 0)",
                StringComparison.Ordinal
            );
            renamed = renamed.Insert(
                mainIndex,
                "uniform int atlasDisableHorizonFade;\n\n"
            );
        }
        // The native fragment shaders still call the perspective camera's
        // shadow-map helpers even when DropShadowIntensity is temporarily
        // zero. Avoid the texture reads altogether in the atlas variant: a
        // stale normal-camera shadow projection produces broad, camera-bound
        // dark bands across otherwise valid chunk meshes. Directional face
        // shading remains active through the engine's normal term.
        const string opaqueShadowSample =
            "float b = getBrightnessFromShadowMap();";
        int opaqueShadowSampleIndex = renamed.IndexOf(
            opaqueShadowSample,
            mainIndex,
            StringComparison.Ordinal
        );
        if (opaqueShadowSampleIndex >= 0)
        {
            renamed = renamed.Remove(
                opaqueShadowSampleIndex,
                opaqueShadowSample.Length
            ).Insert(
                opaqueShadowSampleIndex,
                "float b = atlasFilteringEnabled > 0"
                    + " ? 1.0 : getBrightnessFromShadowMap();"
            );
        }
        renamed = renamed.Replace(
            "min(b, nb), worldPos.xyz",
            "max(min(b, nb), atlasFilteringEnabled > 0"
                + " ? atlasMinimumTerrainBrightness : 0.0), worldPos.xyz",
            StringComparison.Ordinal
        );
        renamed = renamed.Replace(
            "outColor = applyFogAndShadowWithNormal(outColor,",
            "outColor = modernAtlasApplyFogAndDirectionalWithNormal(outColor,",
            StringComparison.Ordinal
        );
        renamed = renamed.Replace(
            "texColor = applyFogAndShadowWithNormal(texColor,",
            "texColor = modernAtlasApplyFogAndDirectionalWithNormal(texColor,",
            StringComparison.Ordinal
        );
        renamed = renamed.Replace(
            "aTest < alphaTest",
            "aTest < modernAtlasAlphaTestThreshold(aTest, alphaTest)",
            StringComparison.Ordinal
        );
        // chunkopaque has a second hard discard on the normal camera's
        // distance-faded vertex alpha. Keep it for the ordinary world, but
        // let the authored texture alpha decide in the atlas variant.
        renamed = renamed.Replace(
            "|| rgba.a < 0.005",
            "|| (atlasFilteringEnabled == 0 && rgba.a < 0.005)",
            StringComparison.Ordinal
        );
        renamed = renamed.Replace(
            "if (rgba.a < 0.005) discard;",
            "if (atlasFilteringEnabled == 0 && rgba.a < 0.005) discard;",
            StringComparison.Ordinal
        );
        const string weakAlphaLine =
            "if ((renderFlags & WindModeBitMask) == WindModeWeakLowAlphaTest) aTest *= 4;";
        if (renamed.Contains(weakAlphaLine, StringComparison.Ordinal))
        {
            renamed = renamed.Replace(
                weakAlphaLine,
                weakAlphaLine
                    + "\n\n"
                    + "\tif (atlasFilteringEnabled > 0\n"
                    + "\t    && atlasVegetationDebugMode > 0\n"
                    + "\t    && modernAtlasIsWindVegetation())\n"
                    + "\t{\n"
                    + "\t\toutColor = modernAtlasVegetationDebugColor(\n"
                    + "\t\t\toutColor, aTest, alphaTest, lod0Fade\n"
                    + "\t\t);\n"
                    + "\t\toutGlow = vec4(0.0);\n"
                    + "\t\treturn;\n"
                    + "\t}",
                StringComparison.Ordinal
            );
        }
        else
        {
            int topsoilAlphaIndex = renamed.IndexOf(
                "float aTest = outColor.a;",
                mainIndex,
                StringComparison.Ordinal
            );
            if (topsoilAlphaIndex >= 0)
            {
                int topsoilDebugIndex = renamed.IndexOf(
                    "#if NORMALVIEW == 0",
                    topsoilAlphaIndex,
                    StringComparison.Ordinal
                );
                if (topsoilDebugIndex >= 0)
                {
                    renamed = renamed.Insert(
                        topsoilDebugIndex,
                        "if (atlasFilteringEnabled > 0\n"
                            + "    && atlasVegetationDebugMode > 0\n"
                            + "    && modernAtlasIsWindVegetation())\n"
                            + "{\n"
                            + "    outColor = modernAtlasVegetationDebugColor(\n"
                            + "        outColor, aTest, alphaTest, 0.0\n"
                            + "    );\n"
                            + "    outGlow = vec4(0.0);\n"
                            + "    return;\n"
                            + "}\n\n"
                    );
                }
            }
        }
        string atlasBrightnessCode = supportsBoundaryColor
            ? "    modernAtlasClampMinimumBrightness(outColor);\n"
            : "";
        string mapLayerCode = supportsBoundaryColor
            ? """
    modernAtlasApplyMapLayer(outColor, modernAtlasAbsoluteWorldPosition);
"""
            : "";
        if (!supportsBoundaryColor)
        {
            const string oitOutput = "OIT(texColor, glowLevel);";
            if (!renamed.Contains(oitOutput, StringComparison.Ordinal))
            {
                return null;
            }
            renamed = renamed.Replace(
                oitOutput,
                "if (atlasFilteringEnabled > 0\n"
                    + "        && atlasVegetationDebugMode > 0\n"
                    + "        && modernAtlasIsWindVegetation())\n"
                    + "    {\n"
                    + "        OIT(\n"
                    + "            modernAtlasVegetationDebugColor(\n"
                    + "                texColor, texColor.a, 0.0, 0.0\n"
                    + "            ),\n"
                    + "            0.0\n"
                    + "        );\n"
                    + "        return;\n"
                    + "    }\n"
                    + "modernAtlasApplyRelativeMapLayer(texColor, worldPos.xyz);\n"
                    + "    OIT(texColor, glowLevel);",
                StringComparison.Ordinal
            );
            renamed = renamed.Insert(
                mainIndex,
                "void modernAtlasApplyRelativeMapLayer(inout vec4 targetColor, vec3 relativeWorldPosition);\n\n"
            );
        }
        string caveFilterCode = supportsBoundaryColor
            ? """
    // Apply the local exterior test at every altitude. A cave entrance in a
    // mountain is still an interior cutout; restricting this to sea level
    // leaves exactly the dark underside bands that flicker at an atlas edge.
    if (atlasHideCaves > 0)
    {
        float modernAtlasExteriorFloor;
        bool modernAtlasHasExteriorFloor = modernAtlasReadExteriorFloor(
            modernAtlasAbsoluteWorldPosition,
            normal,
            modernAtlasExteriorFloor
        );
        if (!modernAtlasHasExteriorFloor
            || modernAtlasAbsoluteWorldPosition.y < modernAtlasExteriorFloor)
        {
            if (modernAtlasHasExteriorFloor
                && modernAtlasAbsoluteWorldPosition.y
                >= modernAtlasExteriorFloor - atlasCaveConcealmentDepth)
            {
                // Keep a thin band of real opaque faces near the exterior to
                // quiet clipped cave mouths. The branch below uses the same
                // neutral treatment for deeper existing faces so a cave cutout
                // never exposes the dark Primary background as a moving band.
                modernAtlasOriginalMain();
                outColor = vec4(atlasCaveConcealmentColor, 1.0);
                modernAtlasApplyMapLayer(
                    outColor,
                    modernAtlasAbsoluteWorldPosition
                );
                return;
            }

            // Deeper cave faces are not part of the exterior atlas. Discard
            // the existing fragment so no black underside or underground
            // tunnel can become visible; no replacement geometry is created.
            discard;
        }
    }
"""
            : """
    if (atlasHideCaves > 0)
    {
        float modernAtlasExteriorFloor;
        bool modernAtlasHasExteriorFloor = modernAtlasReadExteriorFloor(
            modernAtlasAbsoluteWorldPosition,
            normal,
            modernAtlasExteriorFloor
        );
        if (!modernAtlasHasExteriorFloor
            || modernAtlasAbsoluteWorldPosition.y < modernAtlasExteriorFloor)
        {
            // Transparent chunk geometry has no neutral concealment pass. It
            // must not remain below the same real exterior safety layer.
            discard;
        }
    }
""";

        string atlasBoundaryCode = """
    if (atlasCompleteBoundaryEnabled > 0
        && (any(lessThan(
                modernAtlasAbsoluteWorldPosition.xz,
                atlasCompleteBoundaryMinXZ
            ))
            || any(greaterThanEqual(
                modernAtlasAbsoluteWorldPosition.xz,
                atlasCompleteBoundaryMaxXZ
            ))))
    {
        discard;
    }

    // Apply the hard world-space cutoff to the fragment's real block position
    // in every terrain material, including OIT. Never move this test according
    // to height or camera tilt: doing so can translate tall cliff/tree faces
    // from outside the allowed cylinder into it and expose vertical pillars.
    vec2 modernAtlasDisclosureDelta =
        modernAtlasAbsoluteWorldPosition.xz - atlasDisclosureCenterXZ;
    float modernAtlasDisclosureRadiusSquared =
        atlasDisclosureRadius * atlasDisclosureRadius;
    if (atlasBoundaryEnabled > 0
        && dot(
            modernAtlasDisclosureDelta,
            modernAtlasDisclosureDelta
        ) >= modernAtlasDisclosureRadiusSquared)
    {
        discard;
    }

    // The last world chunk is a disclosure seam, not a cutaway wall. Keep
    // only the real exterior surface in that one-chunk ring. This removes
    // deep chunk sides and partial tall objects whose remaining halves would
    // otherwise look like floating terrain, trunks or leaf columns after the
    // hard circular cutoff. Interior geometry keeps its full block models.
    if (atlasHideCaves > 0
        && length(modernAtlasDisclosureDelta)
            >= max(0.0, atlasDisclosureRadius - 32.0))
    {
        ivec2 modernAtlasEdgeSample = ivec2(floor(
            (modernAtlasAbsoluteWorldPosition.xz - atlasSurfaceOriginXZ)
                / atlasSurfaceSampleSize
        ));
        float modernAtlasEdgeSurfaceHeight;
        if (!modernAtlasReadSurfaceHeight(
                modernAtlasEdgeSample,
                modernAtlasEdgeSurfaceHeight
            )
            || modernAtlasAbsoluteWorldPosition.y
                < modernAtlasEdgeSurfaceHeight + 0.85
            || modernAtlasAbsoluteWorldPosition.y
                > modernAtlasEdgeSurfaceHeight + 1.15)
        {
            discard;
        }
    }
""";
        string opaqueDepthCode = """
    // Transparent pools can complete independently of the opaque pool. A
    // column-level cull is therefore insufficient: if no opaque fragment
    // actually wrote Primary depth at this screen pixel, a leaf, flower or
    // liquid-side fragment would float over the atlas background.
    if (atlasRequireOpaqueDepth > 0)
    {
        ivec2 modernAtlasDepthPosition = ivec2(gl_FragCoord.xy);
        ivec2 modernAtlasDepthDimensions = textureSize(
            atlasOpaqueDepthTex,
            0
        );
        if (any(lessThan(modernAtlasDepthPosition, ivec2(0)))
            || any(greaterThanEqual(
                modernAtlasDepthPosition,
                modernAtlasDepthDimensions
            )))
        {
            discard;
        }
        float modernAtlasOpaqueDepth = texelFetch(
            atlasOpaqueDepthTex,
            modernAtlasDepthPosition,
            0
        ).r;
        if (modernAtlasOpaqueDepth >= 0.999999
            || gl_FragCoord.z > modernAtlasOpaqueDepth + 0.0005)
        {
            discard;
        }
        // The dedicated vegetation pass must be backed by a small continuous
        // patch of completed opaque terrain, not merely by one trunk, log or
        // isolated face at the same screen pixel. This removes the sparse
        // leaf/grass fringe beyond ragged loaded-ground edges without
        // changing solid terrain or ordinary world rendering.
        if (atlasVegetationPass == 2)
        {
            for (int modernAtlasOffsetY = -2; modernAtlasOffsetY <= 2; modernAtlasOffsetY++)
            {
                for (int modernAtlasOffsetX = -2; modernAtlasOffsetX <= 2; modernAtlasOffsetX++)
                {
                    ivec2 modernAtlasNeighbor = modernAtlasDepthPosition
                        + ivec2(modernAtlasOffsetX, modernAtlasOffsetY);
                    if (any(lessThan(modernAtlasNeighbor, ivec2(0)))
                        || any(greaterThanEqual(
                            modernAtlasNeighbor,
                            modernAtlasDepthDimensions
                        ))
                        || texelFetch(
                            atlasOpaqueDepthTex,
                            modernAtlasNeighbor,
                            0
                        ).r >= 0.999999)
                    {
                        discard;
                    }
                }
            }
        }
    }
""";

        string vegetationUvMaskExpression = usesTopsoilUv
            ? "\n        || (atlasVegetationMaskEnabled > 0 && modernAtlasIsVegetation(uv2))"
            : "";

        return renamed + """

// MODERNATLAS_SURFACE_AND_BOUNDARY_FILTER
uniform int atlasHideCaves;
uniform int atlasBoundaryEnabled;
uniform vec2 atlasDisclosureCenterXZ;
uniform float atlasDisclosureRadius;
uniform int atlasCompleteBoundaryEnabled;
uniform vec2 atlasCompleteBoundaryMinXZ;
uniform vec2 atlasCompleteBoundaryMaxXZ;
uniform int atlasRequireOpaqueDepth;
uniform sampler2D atlasOpaqueDepthTex;
uniform sampler2D atlasSurfaceHeightTex;
uniform vec2 atlasSurfaceOriginXZ;
uniform float atlasSurfaceSampleSize;
uniform float atlasVisibleSubsurfaceDepth;
uniform float atlasCaveConcealmentDepth;
uniform vec3 atlasWorldOffset;
uniform vec3 atlasCaveConcealmentColor;
uniform int atlasLayerEnabled;
uniform sampler2D atlasLayerTex;
uniform vec2 atlasLayerOriginXZ;
uniform float atlasLayerSampleSize;
uniform float atlasLayerOpacity;
uniform int atlasLayerContours;
uniform int atlasConcealOres;
uniform float atlasTextureMipBias;
uniform sampler2D atlasOreMapTex;
uniform sampler2D atlasStoneTex;
uniform int atlasHideVegetation;
uniform sampler2D atlasVegetationMaskTex;

void modernAtlasClampMinimumBrightness(inout vec4 targetColor)
{
    if (atlasFilteringEnabled <= 0) return;

    float minimumBrightness = clamp(
        atlasMinimumTerrainBrightness,
        0.0,
        1.0
    );
    float luminance = dot(
        max(targetColor.rgb, vec3(0.0)),
        vec3(0.2126, 0.7152, 0.0722)
    );
    if (luminance >= minimumBrightness) return;
    if (luminance > 0.001)
    {
        targetColor.rgb *= minimumBrightness / luminance;
    }
    else
    {
        // A block with no native vertex light is still real loaded geometry.
        // Use the same subdued atlas floor as the cave occlusion material so
        // it cannot become a camera-dependent black strip.
        targetColor.rgb = vec3(minimumBrightness);
    }
}

void modernAtlasApplyMapLayer(
    inout vec4 targetColor,
    vec3 absoluteWorldPosition
)
{
    if (atlasLayerEnabled <= 0) return;

    vec2 layerPosition =
        (absoluteWorldPosition.xz - atlasLayerOriginXZ)
        / atlasLayerSampleSize;
    ivec2 layerDimensions = textureSize(atlasLayerTex, 0);
    if (any(lessThan(layerPosition, vec2(0.0)))
        || any(greaterThanEqual(layerPosition, vec2(layerDimensions))))
    {
        return;
    }

    vec2 layerUv = layerPosition / vec2(layerDimensions);
    vec4 layerColor = texture(atlasLayerTex, layerUv);
    float layerValidity = smoothstep(0.04, 0.22, layerColor.a);
    float layerScalar = clamp((layerColor.a - 0.25) / 0.75, 0.0, 1.0);
    float baseLuminance = dot(
        clamp(targetColor.rgb, vec3(0.0), vec3(1.0)),
        vec3(0.2126, 0.7152, 0.0722)
    );
    vec3 reliefColor = layerColor.rgb * mix(0.68, 1.18, baseLuminance);
    if (atlasLayerContours > 0)
    {
        float bands = layerScalar * 6.0;
        float distanceToLine = abs(fract(bands + 0.5) - 0.5);
        float lineWidth = max(fwidth(bands) * 0.55, 0.025);
        float contour = 1.0 - smoothstep(lineWidth, lineWidth * 2.2, distanceToLine);
        reliefColor *= mix(1.0, 0.86, contour * layerValidity);
    }
    targetColor.rgb = mix(
        targetColor.rgb,
        reliefColor,
        clamp(layerValidity * atlasLayerOpacity, 0.0, 1.0)
    );
}

void modernAtlasApplyRelativeMapLayer(
    inout vec4 targetColor,
    vec3 relativeWorldPosition
)
{
    modernAtlasApplyMapLayer(
        targetColor,
        relativeWorldPosition + atlasWorldOffset
    );
}

bool modernAtlasIsVegetation(vec2 sourceUv)
{
    if (atlasVegetationMaskEnabled <= 0) return false;

    ivec2 dimensions = textureSize(atlasVegetationMaskTex, 0);
    ivec2 position = clamp(
        ivec2(floor(sourceUv * vec2(dimensions))),
        ivec2(0),
        dimensions - ivec2(1)
    );
    return texelFetch(atlasVegetationMaskTex, position, 0).r > 0.5;
}

bool modernAtlasIsWindVegetation()
{
    int windMode = renderFlags & WindModeBitMask;
    // Water surfaces use a separate stable atlas shader. All other engine
    // wind modes represent plant/leaf geometry, including modded blocks that
    // use the public render-flag contract instead of a vanilla block ID.
    return windMode != 0 && windMode != WindModeLiquidWarp;
}

vec4 modernAtlasApplyFogAndDirectionalWithNormal(
    vec4 targetColor,
    float fogAmount,
    vec3 surfaceNormal,
    float normalShadeIntensity,
    float minimumNormalShade,
    vec3 fragmentWorldPosition
)
{
    if (atlasFilteringEnabled <= 0)
    {
        return applyFogAndShadowWithNormal(
            targetColor,
            fogAmount,
            surfaceNormal,
            normalShadeIntensity,
            minimumNormalShade,
            fragmentWorldPosition
        );
    }

    float directionalBrightness = getBrightnessFromNormal(
        surfaceNormal,
        normalShadeIntensity,
        minimumNormalShade
    );
    targetColor *= vec4(
        directionalBrightness,
        directionalBrightness,
        directionalBrightness,
        1.0
    );
    vec4 foggedColor = applyFog(targetColor, fogAmount);
    return applySpheresFog(foggedColor, fogAmount, fragmentWorldPosition);
}

float modernAtlasTerrainMipBias()
{
    if (atlasFilteringEnabled <= 0) return 0.0;
    return modernAtlasIsWindVegetation()
        ? atlasVegetationMipBias
        : atlasTextureMipBias;
}

float modernAtlasAlphaTestThreshold(
    float alphaValue,
    float baseThreshold
)
{
    if (atlasFilteringEnabled <= 0
        || atlasVegetationAlphaCoverage <= 0.0
        || !modernAtlasIsWindVegetation())
    {
        return baseThreshold;
    }

    // Keep a small derivative-sized coverage band instead of making a
    // binary alpha test switch an entire leaf quad as the camera crosses a
    // subpixel boundary. This is projection/footprint based, never temporal.
    float coverageBand = clamp(
        fwidth(alphaValue) * atlasVegetationAlphaCoverage,
        0.0,
        0.25
    );
    return max(0.0001, baseThreshold - coverageBand);
}

vec4 modernAtlasVegetationDebugColor(
    vec4 color,
    float alphaValue,
    float threshold,
    float lodFadeValue
)
{
    float windMode = float((renderFlags & WindModeBitMask) >> 25) / 15.0;
    if (atlasVegetationDebugMode == 1)
    {
        // Wind-flag coverage: red is the encoded engine wind mode.
        return vec4(windMode, 1.0 - windMode, 0.05, 1.0);
    }
    if (atlasVegetationDebugMode == 2)
    {
        // Mip bias and derivative coverage are shown together.
        return vec4(
            clamp(atlasVegetationMipBias / 2.0, 0.0, 1.0),
            clamp(atlasVegetationAlphaCoverage * 2.0, 0.0, 1.0),
            1.0 - clamp(atlasVegetationMipBias / 2.0, 0.0, 1.0),
            1.0
        );
    }
    if (atlasVegetationDebugMode == 3)
    {
        // Alpha before the engine discard, with low alpha kept visible.
        return vec4(clamp(alphaValue * 8.0, 0.0, 1.0), clamp(alphaValue, 0.0, 1.0), 0.0, 1.0);
    }
    if (atlasVegetationDebugMode == 4)
    {
        // Red is the pre-test alpha; green is the effective threshold.
        return vec4(
            clamp(alphaValue * 8.0, 0.0, 1.0),
            clamp(threshold * 100.0, 0.0, 1.0),
            clamp(fwidth(alphaValue) * 8.0, 0.0, 1.0),
            1.0
        );
    }
    if (atlasVegetationDebugMode == 5)
    {
        return vec4(
            clamp(lodFadeValue, 0.0, 1.0),
            0.0,
            1.0 - clamp(lodFadeValue, 0.0, 1.0),
            1.0
        );
    }
    return vec4(color.rgb, 1.0);
}

vec4 modernAtlasSampleTerrain(sampler2D sourceTexture, vec2 sourceUv)
{
    vec4 originalColor = texture(
        sourceTexture,
        sourceUv,
        modernAtlasTerrainMipBias()
    );
    if (atlasConcealOres <= 0) return originalColor;

    ivec2 mappingDimensions = textureSize(atlasOreMapTex, 0);
    vec2 mappingPosition = sourceUv * vec2(mappingDimensions);
    ivec2 lookupPosition = clamp(
        ivec2(floor(mappingPosition)),
        ivec2(0),
        mappingDimensions - ivec2(1)
    );
    vec4 encodedTarget = texelFetch(atlasOreMapTex, lookupPosition, 0);
    int targetX = int(floor(encodedTarget.r * 255.0 + 0.5)) * 256
        + int(floor(encodedTarget.g * 255.0 + 0.5));
    int targetY = int(floor(encodedTarget.b * 255.0 + 0.5)) * 256
        + int(floor(encodedTarget.a * 255.0 + 0.5));
    if (targetX <= 0 || targetY <= 0) return originalColor;

    ivec2 stoneDimensions = textureSize(atlasStoneTex, 0);
    vec2 subpixelOffset = (fract(mappingPosition) - vec2(0.5)) * 4.0;
    vec2 stonePixel = vec2(targetX - 1, targetY - 1) + subpixelOffset;
    vec2 stoneUv = (stonePixel + vec2(0.5)) / vec2(stoneDimensions);
    vec4 stoneColor = texture(atlasStoneTex, stoneUv);
    return vec4(stoneColor.rgb, originalColor.a);
}

bool modernAtlasReadSurfaceHeight(ivec2 samplePosition, out float surfaceHeight)
{
    ivec2 dimensions = textureSize(atlasSurfaceHeightTex, 0);
    if (any(lessThan(samplePosition, ivec2(0)))
        || any(greaterThanEqual(samplePosition, dimensions)))
    {
        return false;
    }

    vec4 encodedHeight = texelFetch(atlasSurfaceHeightTex, samplePosition, 0);
    if (encodedHeight.b < 0.5) return false;

    surfaceHeight = floor(encodedHeight.r * 255.0 + 0.5) * 256.0
        + floor(encodedHeight.g * 255.0 + 0.5);
    return true;
}

void modernAtlasIncludeLowerSurfaceHeight(
    ivec2 samplePosition,
    inout float minimumSurfaceHeight
)
{
    float candidateHeight;
    if (modernAtlasReadSurfaceHeight(samplePosition, candidateHeight))
    {
        minimumSurfaceHeight = min(minimumSurfaceHeight, candidateHeight);
    }
}

bool modernAtlasReadExteriorFloor(
    vec3 absoluteWorldPosition,
    vec3 surfaceNormal,
    out float exteriorFloor
)
{
    ivec2 samplePosition = ivec2(floor(
        (absoluteWorldPosition.xz - atlasSurfaceOriginXZ) / atlasSurfaceSampleSize
    ));
    float exteriorSurfaceHeight;
    if (!modernAtlasReadSurfaceHeight(samplePosition, exteriorSurfaceHeight))
    {
        return false;
    }

    // A height map alone classifies the underside of a natural overhang as a
    // deep cave. For vertical faces, inspect both immediately adjacent columns
    // along the face axis. A second two-block sample is used only when neither
    // immediate column lowers the surface; this handles a face whose fragment
    // lands on the neighboring heightmap column without allowing a distant low
    // column to rescue an interior mine wall. For downward faces, inspect only
    // a local ring. This keeps real completed mesh faces on an exterior
    // silhouette without using air connectivity, a terrain shell or persistent
    // geometry.
    if (abs(surfaceNormal.y) < 0.75)
    {
        ivec2 exteriorStep = abs(surfaceNormal.x) >= abs(surfaceNormal.z)
            ? ivec2(surfaceNormal.x >= 0.0 ? 1 : -1, 0)
            : ivec2(0, surfaceNormal.z >= 0.0 ? 1 : -1);
        float originalSurfaceHeight = exteriorSurfaceHeight;
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + exteriorStep,
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition - exteriorStep,
            exteriorSurfaceHeight
        );
        if (exteriorSurfaceHeight >= originalSurfaceHeight)
        {
            modernAtlasIncludeLowerSurfaceHeight(
                samplePosition + exteriorStep * 2,
                exteriorSurfaceHeight
            );
            modernAtlasIncludeLowerSurfaceHeight(
                samplePosition - exteriorStep * 2,
                exteriorSurfaceHeight
            );
        }
    }

    if (surfaceNormal.y < -0.25)
    {
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2( 1,  0),
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2(-1,  0),
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2( 0,  1),
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2( 0, -1),
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2( 2,  0),
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2(-2,  0),
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2( 0,  2),
            exteriorSurfaceHeight
        );
        modernAtlasIncludeLowerSurfaceHeight(
            samplePosition + ivec2( 0, -2),
            exteriorSurfaceHeight
        );
    }

    exteriorFloor = exteriorSurfaceHeight - atlasVisibleSubsurfaceDepth;
    return true;
}

void main()
{
    if (atlasRenderingEnabled <= 0)
    {
        // The atlas variant remains compiled between openings to avoid a
        // global shader reload. Once the atlas is closed, this is the native
        // world path: no atlas boundary, cave, layer, depth or vegetation
        // filter may execute.
        modernAtlasOriginalMain();
        return;
    }

    vec3 modernAtlasAbsoluteWorldPosition = worldPos.xyz + atlasWorldOffset;
""" + atlasBoundaryCode + opaqueDepthCode + """
    bool modernAtlasVegetation = modernAtlasIsWindVegetation()
        || (atlasVegetationMaskEnabled > 0 && modernAtlasIsVegetation(uv))
""" + vegetationUvMaskExpression + """
;
    if ((atlasHideVegetation > 0 || atlasVegetationPass == 1)
        && modernAtlasVegetation)
    {
        discard;
    }
    if (atlasVegetationPass == 2 && !modernAtlasVegetation)
    {
        discard;
    }
""" + caveFilterCode + """
    modernAtlasOriginalMain();
""" + atlasBrightnessCode + mapLayerCode + """
}
""";
    }

}
