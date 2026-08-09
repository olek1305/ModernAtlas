#version 330 core

in vec4 color;

layout(location = 0) out vec4 outColor;

void main()
{
    // Block.GetColor returns RGB without an authored alpha byte. The mesh
    // supplies opaque colors, and this small display-gamma lift keeps the
    // low-detail fallback readable without inheriting
    // world lighting or turning legitimate dark terrain into black chunks.
    vec3 readableColor = clamp(pow(max(color.rgb, vec3(0.015)), vec3(0.82)) * 1.06, 0.0, 1.0);
    outColor = vec4(readableColor, 1.0);
}
