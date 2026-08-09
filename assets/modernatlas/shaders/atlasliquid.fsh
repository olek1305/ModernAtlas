#version 330 core

uniform sampler2D terrainTex;
uniform vec2 blockTextureSize;
uniform vec2 textureAtlasSize;
uniform float waterFlowCounter;
uniform sampler2D loadedChunkMask;
uniform vec2 maskChunkOrigin;
uniform float maskSize;
uniform float chunkSize;
uniform vec2 disclosureCenterXZ;
uniform float disclosureRadius;
uniform vec3 atlasSunDirection;
uniform float atlasExposure;

in vec2 uv;
in vec2 uvSize;
in float stillFrameWeight;
in vec2 flowVectorf;
in vec2 absoluteWorldXZ;
flat in vec2 uvBase;
flat in int waterFlags;

layout(location = 0) out vec4 outColor;

#include colormap.fsh

void main(void)
{
    vec2 disclosureDelta = absoluteWorldXZ - disclosureCenterXZ;
    if (dot(disclosureDelta, disclosureDelta) > disclosureRadius * disclosureRadius) discard;

    vec2 maskCell = floor(absoluteWorldXZ / chunkSize) - maskChunkOrigin;
    if (any(lessThan(maskCell, vec2(0.0)))
        || any(greaterThanEqual(maskCell, vec2(maskSize)))) discard;
    vec2 maskUv = (maskCell + vec2(0.5)) / maskSize;
    if (texture(loadedChunkMask, maskUv).r < 0.5) discard;

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
        float exposure = mix(0.14, 1.0, clamp(atlasExposure, 0.0, 1.0));
        color.rgb *= topLight * exposure;
    }
    // Preserve the source texture's authored water alpha. Lava is natively
    // opaque. Geometry remains stable because no camera-dependent vertex warp
    // or depth reconstruction is used by this atlas shader.
    if (isLava) color.a = 1.0;
    outColor = color;
}
