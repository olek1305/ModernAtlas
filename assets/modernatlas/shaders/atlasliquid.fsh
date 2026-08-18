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

void main(void)
{
    // Stable atlas liquids are drawn after opaque exact chunk geometry into
    // the same Primary framebuffer. A cleared depth texel means that no
    // opaque terrain or structure exists behind this liquid fragment. Do not
    // let an independently completed water mesh bridge unloaded terrain or
    // extend beyond the live exact atlas edge.
    ivec2 depthPosition = ivec2(gl_FragCoord.xy);
    ivec2 depthDimensions = textureSize(opaqueDepthTex, 0);
    if (any(lessThan(depthPosition, ivec2(0)))
        || any(greaterThanEqual(depthPosition, depthDimensions)))
    {
        discard;
    }
    float opaqueDepth = texelFetch(opaqueDepthTex, depthPosition, 0).r;
    if (opaqueDepth >= 0.999999) discard;
    if (gl_FragCoord.z > opaqueDepth + 0.0005) discard;

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

    bool isLava = (waterFlags & (1 << 27)) != 0;
    // Match the engine liquid contract for side faces without importing its
    // camera-dependent warp or shadow/Fresnel path. Vertical faces use the
    // same native counter, with the normal-dependent cadence used by the
    // registered liquid material; downward-facing UV flow is reversed.
    float normalVariation = max(0.0, 0.9 - abs(normalIn.y));
    float speed = isLava
        ? waterFlowCounter * 0.1 * (1.0 + 5.0 * normalVariation)
        : waterFlowCounter * (1.0 + 5.0 * normalVariation);
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
            atlasTextureMipBias
        );
    }
    else
    {
        vec2 alternateOffset = clamp(
            blockTextureSize - uvSize,
            vec2(1.0) / textureAtlasSize,
            blockTextureSize - vec2(1.0) / textureAtlasSize
        );
        color = mix(
            texture(
                terrainTex,
                uvBase + alternateOffset,
                atlasTextureMipBias
            ),
            texture(terrainTex, uv, atlasTextureMipBias),
            stillFrameWeight
        );
    }
    color = getColorMapped(terrainTex, color);
    bool isContained = (waterFlags & 2) == 0 && flowSpeed <= 0.001;
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
        float topLight = mix(0.55, 1.0, clamp(atlasSunDirection.y, 0.0, 1.0));
        float exposure = mix(0.14, 1.0, clamp(atlasExposure, 0.0, 1.0))
            * mix(1.0, 1.5, clamp((atlasExposure - 1.0) * 2.0, 0.0, 1.0));
        vec3 celestialTint = mix(vec3(1.0), atlasSunColor, 0.28);
        color.rgb *= celestialTint * topLight * exposure;
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
