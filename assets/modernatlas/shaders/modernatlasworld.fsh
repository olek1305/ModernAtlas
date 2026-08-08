#version 330 core

layout(location = 0) out vec4 outColor;

uniform sampler2D tex;
uniform vec3 sunDirection;
uniform float dayLight;
uniform float moonLight;

in vec2 uv;
in vec4 color;
in vec3 worldPosition;

void main()
{
    outColor = texture(tex, uv) * color;
    if (outColor.a < 0.01) discard;

    vec3 normal = normalize(cross(dFdx(worldPosition), dFdy(worldPosition)));
    if (normal.y < 0.0) normal = -normal;
    vec3 liveSunDirection = normalize(sunDirection);
    float diffuse = max(dot(normal, liveSunDirection), 0.0);
    float light = clamp(
        0.20 + 0.68 * dayLight + 0.12 * diffuse * dayLight + 0.15 * moonLight,
        0.20,
        1.0
    );

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
    float gridShade = mix(1.0, 0.88, clamp(blockInterior, 0.0, 1.0));
    outColor.rgb *= light * gridShade;
}
