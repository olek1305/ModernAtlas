#version 330 core

uniform sampler2D terrainTex;
uniform sampler2D opaqueDepthTex;
uniform vec2 blockTextureSize;
uniform vec2 textureAtlasSize;
uniform float waterFlowCounter;
uniform vec2 disclosureCenterXZ;
uniform float disclosureRadius;
uniform int completeBoundaryEnabled;
uniform vec2 completeBoundaryMinXZ;
uniform vec2 completeBoundaryMaxXZ;
uniform int atlasHideCaves;
uniform int atlasSurfaceCoverageEnabled;
uniform int atlasLiquidDepthCoveragePass;
uniform int atlasLiquidDepthAbsorptionPass;
uniform sampler2D atlasSurfaceHeightTex;
uniform vec2 atlasSurfaceOriginXZ;
uniform float atlasSurfaceSampleSize;
uniform float atlasVisibleSubsurfaceDepth;
uniform int atlasLayerEnabled;
uniform sampler2D atlasLayerTex;
uniform vec2 atlasLayerOriginXZ;
uniform float atlasLayerSampleSize;
uniform float atlasLayerOpacity;
uniform int atlasLayerContours;
uniform vec3 atlasSunDirection;
uniform vec3 atlasSunColor;
uniform float atlasExposure;
uniform float atlasTextureMipBias;

in vec2 uv;
in vec2 uvSize;
in float stillFrameWeight;
in vec2 flowVectorf;
in vec3 absoluteWorldPosition;
in vec3 normalIn;
flat in vec2 uvBase;
flat in int waterFlags;

layout(location = 0) out vec4 outColor;

#include colormap.fsh

bool readSurfaceHeight(ivec2 samplePosition, out float surfaceHeight)
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

bool readLiquidSurfaceHeight(out float surfaceHeight)
{
    if (atlasSurfaceCoverageEnabled <= 0) return false;

    ivec2 samplePosition = ivec2(floor(
        (absoluteWorldPosition.xz - atlasSurfaceOriginXZ)
            / atlasSurfaceSampleSize
    ));
    return readSurfaceHeight(samplePosition, surfaceHeight);
}

void main(void)
{
    // Stable atlas liquids are drawn after opaque exact chunk geometry into
    // the same Primary framebuffer. A cleared depth texel means that no
    // opaque terrain or structure exists behind this liquid fragment. Do not
    // let an independently completed water mesh bridge unloaded terrain or
    // extend beyond the live exact atlas edge.
    bool isLava = (waterFlags & (1 << 27)) != 0;
    bool hasOpaqueDepth = false;
    if (atlasLiquidDepthCoveragePass <= 0)
    {
        ivec2 depthPosition = ivec2(gl_FragCoord.xy);
        ivec2 depthDimensions = textureSize(opaqueDepthTex, 0);
        if (any(lessThan(depthPosition, ivec2(0)))
            || any(greaterThanEqual(depthPosition, depthDimensions)))
        {
            discard;
        }
        float opaqueDepth = texelFetch(opaqueDepthTex, depthPosition, 0).r;
        if (opaqueDepth >= 0.999999)
        {
            // Survival cave filtering can intentionally remove an ocean floor
            // several blocks below a completed liquid mesh. Keep any real
            // liquid fragment inside the loaded surface envelope; the pool
            // visibility, loaded chunk and atlas boundary guards still apply
            // before this shader runs, so this cannot bridge an unloaded
            // frontier.
            float fallbackSurfaceHeight;
            if (isLava
                || (atlasSurfaceCoverageEnabled > 0
                    && (!readLiquidSurfaceHeight(fallbackSurfaceHeight)
                        || absoluteWorldPosition.y
                            < fallbackSurfaceHeight
                                - atlasVisibleSubsurfaceDepth)))
            {
                discard;
            }
        }
        else
        {
            hasOpaqueDepth = true;
            if (gl_FragCoord.z > opaqueDepth + 0.0005)
            {
                discard;
            }
        }
    }

    vec2 disclosureDelta = absoluteWorldPosition.xz - disclosureCenterXZ;
    if (completeBoundaryEnabled > 0
        && (any(lessThan(absoluteWorldPosition.xz, completeBoundaryMinXZ))
            || any(greaterThanEqual(
                absoluteWorldPosition.xz,
                completeBoundaryMaxXZ
            ))))
    {
        discard;
    }
    float disclosureDistance = length(disclosureDelta);
    if (disclosureDistance >= disclosureRadius) discard;
    if (atlasHideCaves > 0)
    {
        ivec2 samplePosition = ivec2(floor(
            (absoluteWorldPosition.xz - atlasSurfaceOriginXZ) / atlasSurfaceSampleSize
        ));
        float exteriorSurfaceHeight;
        if (!readSurfaceHeight(samplePosition, exteriorSurfaceHeight))
        {
            discard;
        }

        if (absoluteWorldPosition.y
            < exteriorSurfaceHeight - atlasVisibleSubsurfaceDepth)
        {
            discard;
        }

        // Match the terrain disclosure seam: the outer chunk may show only
        // its registered liquid surface, never deep liquid sides or partial
        // columns extending beyond the accepted atlas footprint.
        if (disclosureDistance >= max(0.0, disclosureRadius - 32.0)
            && (absoluteWorldPosition.y < exteriorSurfaceHeight - 0.25
                || absoluteWorldPosition.y > exteriorSurfaceHeight + 1.5))
        {
            discard;
        }
    }

    if (atlasLiquidDepthCoveragePass > 0)
    {
        // The depth-only pass writes real non-lava liquid faces, including
        // vertical and underside fragments, so the final resolver can retain
        // those authored mesh pixels at low camera angles. Lava remains
        // excluded, and loaded surface-height coverage keeps this pass inside
        // the same disclosure contract without sampling the attached depth
        // texture.
        float coverageSurfaceHeight;
        if (isLava
            || (atlasSurfaceCoverageEnabled > 0
                && (!readLiquidSurfaceHeight(coverageSurfaceHeight)
                    || absoluteWorldPosition.y
                        < coverageSurfaceHeight
                            - atlasVisibleSubsurfaceDepth)))
        {
            discard;
        }
        outColor = vec4(0.0);
        return;
    }

    // Match the engine liquid contract for side faces without importing its
    // camera-dependent warp or shadow/Fresnel path. Vertical faces use the
    // same native counter, with the normal-dependent cadence used by the
    // registered liquid material; downward-facing UV flow is reversed.
    float normalVariation = max(0.0, 0.9 - abs(normalIn.y));
    float speed = isLava
        ? waterFlowCounter * 0.1 * (1.0 + 5.0 * normalVariation)
        : waterFlowCounter * (1.0 + 5.0 * normalVariation);
    // Preserve the native atlas mip selection for lava. Water gets a small,
    // bounded detail lift so registered liquid frames and native UV motion
    // remain legible at atlas distance without inventing a new surface.
    float liquidMipBias = isLava
        ? atlasTextureMipBias
        : max(0.0, atlasTextureMipBias - 0.35);
    float flowSpeed = length(flowVectorf);
    vec4 color;
    if (flowSpeed > 0.001)
    {
        vec2 flow = normalize(flowVectorf) * flowSpeed;
        if (normalIn.y < 0.0) flow *= -1.0;
        vec2 offset = clamp(
            mod((uv - uvBase) + flow * speed * blockTextureSize, blockTextureSize),
            vec2(1.0) / textureAtlasSize,
            blockTextureSize - vec2(1.0) / textureAtlasSize
        );
        color = texture(
            terrainTex,
            uvBase + offset,
            liquidMipBias
        );
    }
    else
    {
        vec2 alternateOffset = clamp(
            blockTextureSize - uvSize,
            vec2(1.0) / textureAtlasSize,
            blockTextureSize - vec2(1.0) / textureAtlasSize
        );
        vec4 firstFrame = texture(
            terrainTex,
            uvBase + alternateOffset,
            liquidMipBias
        );
        vec4 secondFrame = texture(terrainTex, uv, liquidMipBias);
        float authoredAlpha = mix(
            firstFrame.a,
            secondFrame.a,
            stillFrameWeight
        );
        // Widen only the RGB excursion between the two registered frames.
        // At either endpoint this is the exact source frame; alpha remains
        // the original interpolation so authored transparency is unchanged.
        float visibleFrameWeight = isLava
            ? stillFrameWeight
            : clamp(0.5 + (stillFrameWeight - 0.5) * 1.18, 0.0, 1.0);
        color = mix(firstFrame, secondFrame, visibleFrameWeight);
        color.a = authoredAlpha;
    }
    color = getColorMapped(terrainTex, color);
    bool isContained = (waterFlags & 2) == 0 && flowSpeed <= 0.001;

    if (atlasLiquidDepthAbsorptionPass > 0)
    {
        // This pass is a color-only volume veil over the already-composed
        // terrain/OIT result. It uses the real liquid surface mesh as its
        // footprint and a loaded surface column when available. A missing
        // opaque depth therefore receives a bounded, stable water backing
        // instead of blending the authored surface directly with clear.
        if (isLava || isContained || normalIn.y <= 0.5)
        {
            discard;
        }

        float bottomSurfaceHeight;
        bool hasBottomHeight = readLiquidSurfaceHeight(bottomSurfaceHeight);
        float waterDepth = hasBottomHeight
            ? clamp(
                absoluteWorldPosition.y - bottomSurfaceHeight - 0.75,
                0.0,
                3.0
            )
            : 3.0;
        float depthProgress = hasBottomHeight
            ? smoothstep(0.0, 3.0, waterDepth)
            : 1.0;
        // Keep the same bounded celestial term as the authored pass. The
        // backing must not become a second, camera-dependent lighting path at
        // dawn/night, even though it is intentionally darker with depth.
        float daylight = clamp(atlasSunDirection.y, 0.0, 1.0);
        float upwardness = clamp(normalIn.y, 0.0, 1.0);
        float minimumTopLight = mix(0.55, 0.70, upwardness);
        float topLight = mix(minimumTopLight, 1.0, daylight);
        float lowExposure = clamp(atlasExposure, 0.0, 1.0);
        float highExposure = clamp(atlasExposure - 1.0, 0.0, 1.0);
        float exposure = mix(0.14, 1.0, lowExposure)
            * mix(1.0, 2.0, highExposure);
        vec3 celestialTint = mix(vec3(1.0), atlasSunColor, 0.28);
        vec3 litColor = color.rgb * celestialTint * topLight * exposure;
        float depthLight = mix(1.20, 1.00, depthProgress);
        litColor *= mix(1.0, depthLight, upwardness);
        float backingAlpha = hasOpaqueDepth
            // A known seabed/opaque column gets a visible 0..3 block
            // absorption ramp. At three blocks the veil is strong enough to
            // separate submerged plants and the floor, but its tinted RGB
            // contribution remains above black.
            ? mix(0.20, 0.62, depthProgress)
            // A clear-depth ocean has no opaque seabed to stabilize the
            // composition. Keep a strong but still translucent veil so real
            // loaded aquatic plants remain legible underneath it.
            : mix(0.48, 0.68, depthProgress);
        vec3 backingColor = litColor
            * mix(0.68, 0.30, depthProgress);
        outColor = vec4(backingColor, backingAlpha);
        return;
    }

    if (isContained && !isLava)
    {
        // A small exposed surface in a dark container otherwise becomes a
        // bright cyan beacon at atlas distance. Keep it readable from above
        // while matching the neutral, shaded appearance of its container.
        color.rgb *= 0.58;
        color.a *= 0.78;
    }
    if (!isLava && normalIn.y < 0.5)
    {
        // The engine's liquid shader intentionally subdues vertical and
        // underside faces. Preserve the authored top-surface alpha while
        // preventing the two triangles of every exposed side quad from
        // becoming opaque cyan wedges at atlas distance.
        color.a *= 0.3333333;
    }
    if (!isLava)
    {
        float daylight = clamp(atlasSunDirection.y, 0.0, 1.0);
        // Atlas water has no camera shadow or Fresnel contribution. Keep a
        // modest top-face lift so the registered water tint remains readable
        // beside the exact terrain and transparent plants below it.
        float upwardness = clamp(normalIn.y, 0.0, 1.0);
        float minimumTopLight = mix(0.55, 0.70, upwardness);
        float topLight = mix(minimumTopLight, 1.0, daylight);
        // The user-facing 100% setting maps to the old 150% neutral output.
        // Preserve the former curve through the new neutral point: the new
        // 100% value supplies the old 1.5 multiplier exactly. Only extend the
        // high shoulder to 2.0 so the new 150% value is genuinely brighter.
        float lowExposure = clamp(atlasExposure, 0.0, 1.0);
        float highExposure = clamp(atlasExposure - 1.0, 0.0, 1.0);
        float exposure = mix(0.14, 1.0, lowExposure)
            * mix(1.0, 2.0, highExposure);
        vec3 celestialTint = mix(vec3(1.0), atlasSunColor, 0.28);
        color.rgb *= celestialTint * topLight * exposure;

        // Use the loaded world-generation surface height as a stable bottom
        // proxy. The one-block exterior envelope can lower a shoreline sample
        // slightly, so ignore a bounded 0.75-block allowance before applying
        // the three-block visual water-depth ramp. This is RGB-only: the
        // registered texture alpha remains authored and unchanged.
        float bottomSurfaceHeight;
        if (readLiquidSurfaceHeight(bottomSurfaceHeight))
        {
            float waterDepth = clamp(
                absoluteWorldPosition.y - bottomSurfaceHeight - 0.75,
                0.0,
                3.0
            );
            float depthProgress = smoothstep(0.0, 3.0, waterDepth);
            float depthLight = mix(1.20, 1.00, depthProgress);
            color.rgb *= mix(1.0, depthLight, upwardness);
        }
    }
    if (atlasLayerEnabled > 0)
    {
        vec2 layerPosition =
            (absoluteWorldPosition.xz - atlasLayerOriginXZ)
            / atlasLayerSampleSize;
        ivec2 layerDimensions = textureSize(atlasLayerTex, 0);
        if (all(greaterThanEqual(layerPosition, vec2(0.0)))
            && all(lessThan(layerPosition, vec2(layerDimensions))))
        {
            vec2 layerUv = layerPosition / vec2(layerDimensions);
            vec4 layerColor = texture(atlasLayerTex, layerUv);
            float layerValidity = smoothstep(0.04, 0.22, layerColor.a);
            float layerScalar = clamp((layerColor.a - 0.25) / 0.75, 0.0, 1.0);
            float baseLuminance = dot(
                clamp(color.rgb, vec3(0.0), vec3(1.0)),
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
            color.rgb = mix(
                color.rgb,
                reliefColor,
                clamp(layerValidity * atlasLayerOpacity, 0.0, 1.0)
            );
        }
    }
    // Preserve the source texture's authored water alpha. Lava is natively
    // opaque. Geometry remains stable because no camera-dependent vertex warp
    // or depth reconstruction is used by this atlas shader.
    if (isLava) color.a = 1.0;
    // Keep the authored liquid alpha throughout the disclosed terrain range.
    // The hard disclosure cutoff prevents completed liquid meshes from
    // extending beyond data the atlas may show.
    outColor = color;
}
