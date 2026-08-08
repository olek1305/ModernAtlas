#version 330 core

uniform sampler2D terrainTex;
uniform vec2 blockTextureSize;
uniform vec2 textureAtlasSize;
uniform float waterFlowCounter;

in vec2 uv;
in vec2 uvSize;
in float stillFrameWeight;
in vec2 flowVectorf;
flat in vec2 uvBase;
flat in int waterFlags;

layout(location = 0) out vec4 outColor;

#include colormap.fsh

void main(void)
{
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
    // Preserve the source texture's authored water alpha. Lava is natively
    // opaque. Geometry remains stable because no camera-dependent vertex warp
    // or depth reconstruction is used by this atlas shader.
    if (isLava) color.a = 1.0;
    outColor = color;
}
