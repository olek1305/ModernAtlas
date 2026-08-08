#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlags;
layout(location = 4) in vec2 flowVector;
layout(location = 5) in int colormapData;

uniform vec3 origin;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;

out vec2 uv;

#include noise3d.ash
#include colormap.vsh

void main(void)
{
    vec4 worldPos = vec4(xyz + origin, 1.0);
    gl_Position = projectionMatrix * modelViewMatrix * worldPos;
    uv = uvIn;
    calcColorMapUvs(colormapData, worldPos + vec4(playerpos, 1.0), rgbaLightIn.a, false);
}
