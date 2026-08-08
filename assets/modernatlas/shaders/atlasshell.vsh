#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;

uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform vec3 referencePosition;

out vec2 uv;
out vec4 color;

void main(void)
{
    vec3 relativePosition = xyz - referencePosition;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(relativePosition, 1.0);
    uv = uvIn;
    color = colorIn;
}
