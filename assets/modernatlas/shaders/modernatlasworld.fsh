#version 330 core

layout(location = 0) out vec4 outColor;

uniform sampler2D tex;

in vec2 uv;
in vec4 color;
in vec3 worldPosition;

void main()
{
    outColor = texture(tex, uv) * color;
    if (outColor.a < 0.01) discard;

    vec3 normal = normalize(cross(dFdx(worldPosition), dFdy(worldPosition)));
    if (normal.y < 0.0) normal = -normal;
    vec3 sunDirection = normalize(vec3(-0.45, 1.0, 0.3));
    float light = 0.64 + 0.36 * max(dot(normal, sunDirection), 0.0);

    vec2 planePosition;
    if (abs(normal.y) > abs(normal.x) && abs(normal.y) > abs(normal.z)) {
        planePosition = worldPosition.xz;
    } else if (abs(normal.x) > abs(normal.z)) {
        planePosition = worldPosition.zy;
    } else {
        planePosition = worldPosition.xy;
    }

    vec2 cell = abs(fract(planePosition) - 0.5);
    // One screen pixel centered exactly on each integer block boundary.
    // Geometry remains one world unit per block at every zoom level.
    vec2 edgeWidth = max(fwidth(planePosition) * 0.5, vec2(0.0001));
    float blockInterior = smoothstep(0.5 - edgeWidth.x, 0.5, cell.x)
        + smoothstep(0.5 - edgeWidth.y, 0.5, cell.y);
    float gridShade = mix(1.0, 0.78, clamp(blockInterior, 0.0, 1.0));
    outColor.rgb *= light * gridShade;
}
