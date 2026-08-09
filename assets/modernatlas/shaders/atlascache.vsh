#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 xyz;
layout(location = 2) in vec4 rgbaIn;

uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;

out vec4 color;

void main()
{
    color = rgbaIn;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(xyz, 1.0);
}
