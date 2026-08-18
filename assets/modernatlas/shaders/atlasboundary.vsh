#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec2 vertexPosition;

out vec2 ndc;

void main()
{
    ndc = vertexPosition;
    gl_Position = vec4(vertexPosition, 0.0, 1.0);
}
