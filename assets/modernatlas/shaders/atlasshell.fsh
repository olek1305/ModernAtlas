#version 330 core

uniform sampler2D terrainTex;

in vec2 uv;
in vec4 color;

layout(location = 0) out vec4 outColor;

void main(void)
{
    vec4 stone = texture(terrainTex, uv) * color;
    outColor = vec4(stone.rgb * vec3(0.76, 0.79, 0.82), 1.0);
}
