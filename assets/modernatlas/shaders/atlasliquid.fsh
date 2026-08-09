#version 330 core

uniform sampler2D terrainTex;
uniform vec2 blockTextureSize;
uniform vec2 textureAtlasSize;
uniform float waterFlowCounter;
uniform vec2 disclosureCenterXZ;
uniform float disclosureRadius;
uniform float disclosureFeather;
uniform vec3 boundaryFogColor;
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
uniform vec3 atlasSunDirection;
uniform float atlasExposure;

in vec2 uv;
in vec2 uvSize;
in float stillFrameWeight;
in vec2 flowVectorf;
in vec3 absoluteWorldPosition;
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
    vec2 disclosureDelta = absoluteWorldPosition.xz - disclosureCenterXZ;
    float disclosureDistance = length(disclosureDelta);
    if (disclosureDistance >= disclosureRadius) discard;
    float boundaryFade = smoothstep(
        max(0.0, disclosureRadius - disclosureFeather),
        disclosureRadius,
        disclosureDistance
    );
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
    }

    bool isLava = (waterFlags & (1 << 27)) != 0;
    float speed = isLava ? waterFlowCounter * 0.1 : waterFlowCounter;
    float flowSpeed = length(flowVectorf);
    vec4 color;
    if (flowSpeed > 0.001)
    {
        vec2 flow = normalize(flowVectorf) * flowSpeed;
        vec2 offset = clamp(
            mod((uv - uvBase) + flow * speed * blockTextureSize, blockTextureSize),
            vec2(1.0) / textureAtlasSize,
            blockTextureSize - vec2(1.0) / textureAtlasSize
        );
        color = texture(terrainTex, uvBase + offset);
    }
    else
    {
        vec2 alternateOffset = clamp(
            blockTextureSize - uvSize,
            vec2(1.0) / textureAtlasSize,
            blockTextureSize - vec2(1.0) / textureAtlasSize
        );
        color = mix(
            texture(terrainTex, uvBase + alternateOffset),
            texture(terrainTex, uv),
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
    if (!isLava)
    {
        float topLight = mix(0.55, 1.0, clamp(atlasSunDirection.y, 0.0, 1.0));
        float exposure = mix(0.14, 1.0, clamp(atlasExposure, 0.0, 1.0))
            * mix(1.0, 1.5, clamp((atlasExposure - 1.0) * 2.0, 0.0, 1.0));
        color.rgb *= topLight * exposure;
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
            float baseLuminance = dot(
                clamp(color.rgb, vec3(0.0), vec3(1.0)),
                vec3(0.2126, 0.7152, 0.0722)
            );
            vec3 reliefColor = layerColor.rgb * mix(0.68, 1.18, baseLuminance);
            color.rgb = mix(
                color.rgb,
                reliefColor,
                clamp(layerColor.a * atlasLayerOpacity, 0.0, 1.0)
            );
        }
    }
    // Preserve the source texture's authored water alpha. Lava is natively
    // opaque. Geometry remains stable because no camera-dependent vertex warp
    // or depth reconstruction is used by this atlas shader.
    if (isLava) color.a = 1.0;
    color.rgb = mix(color.rgb, boundaryFogColor, boundaryFade);
    // Keep the authored liquid alpha throughout the visible terrain range.
    // The GUI fog performs the single final boundary fade; attenuating alpha
    // here as well made water disappear well before opaque terrain.
    outColor = color;
}
