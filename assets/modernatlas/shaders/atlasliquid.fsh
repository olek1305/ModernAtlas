#version 330 core

uniform sampler2D terrainTex;

in vec2 uv;

layout(location = 0) out vec4 outColor;

#include colormap.fsh

void main(void)
{
    vec4 color = getColorMapped(terrainTex, texture(terrainTex, uv));
    // Atlas fluids are stable world-aligned block surfaces. They deliberately
    // exclude Fresnel, shadows, scene lighting and camera-dependent depth
    // effects, while retaining the registered block texture and biome tint.
    color.a = 1.0;
    outColor = color;
}
