#version 330 core

uniform sampler2D terrainTex;
uniform vec2 blockTextureSize;
uniform vec2 textureAtlasSize;
uniform float liquidAnimationTime;

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
    float speed = isLava ? liquidAnimationTime * 0.1 : liquidAnimationTime;
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
    // Atlas fluids are stable world-aligned block surfaces. They deliberately
    // exclude Fresnel, shadows, scene lighting and camera-dependent depth
    // effects, while retaining the registered block texture and biome tint.
    color.a = 1.0;
    outColor = color;
}
