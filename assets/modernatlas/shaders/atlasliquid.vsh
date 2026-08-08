#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlags;
layout(location = 4) in vec2 flowVector;
layout(location = 5) in int colormapData;
layout(location = 6) in int waterFlagsIn;

uniform vec3 origin;
uniform vec3 playerpos;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform vec2 blockTextureSize;
uniform float waterStillCounter;

out vec2 uv;
out vec2 uvSize;
out float stillFrameWeight;
out vec2 flowVectorf;
flat out vec2 uvBase;
flat out int waterFlags;

#include noise3d.ash
#include colormap.vsh

void main(void)
{
    vec4 worldPos = vec4(xyz + origin, 1.0);
    gl_Position = projectionMatrix * modelViewMatrix * worldPos;
    // Give the liquid surface deterministic depth priority without changing
    // its world-space height or making it camera-relative.
    gl_Position.z -= 0.0005 * gl_Position.w;
    uv = uvIn;
    uvSize = vec2((waterFlagsIn >> 10) & 0xff, (waterFlagsIn >> 18) & 0xff)
        / 255.0 * blockTextureSize;
    uvBase = uvIn - uvSize;
    flowVectorf = flowVector;
    waterFlags = waterFlagsIn;
    float framePhase = mod(
        waterStillCounter + length(worldPos.xz + playerpos.xz) / 3.0,
        2.0
    );
    stillFrameWeight = smoothstep(0.0, 1.0, abs(framePhase - 1.0));
    calcColorMapUvs(colormapData, worldPos + vec4(playerpos, 1.0), rgbaLightIn.a, false);
}
