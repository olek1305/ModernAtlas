#version 330 core

layout(location = 0) out vec4 outColor;

void main()
{
    // RGB writes are disabled while this pass runs. Only make the native
    // window framebuffer opaque so desktop windows cannot show through it.
    outColor = vec4(0.0, 0.0, 0.0, 1.0);
}
