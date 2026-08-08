#version 330 core

layout(location = 0) out vec4 outColor;

uniform sampler2D tex;

in vec2 uv;
in vec4 color;

void main()
{
    outColor = texture(tex, uv) * color;
    if (outColor.a < 0.01) discard;
}
