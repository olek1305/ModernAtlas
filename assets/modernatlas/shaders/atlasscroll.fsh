#version 330 core

uniform int materialKind;
uniform float alpha;
uniform float lightSweep;
uniform sampler2D entityTex;
uniform vec4 entityColor;
uniform sampler2D atlasTex;
uniform sampler2D backgroundTex;
uniform sampler2D compassTex;
uniform int backgroundAvailable;

in vec2 uv;
in vec3 viewNormal;
in vec3 objectPosition;

layout(location = 0) out vec4 outColor;

float hash21(vec2 value)
{
    vec3 p = fract(vec3(value.xyx) * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}

float paperFiber(vec2 position)
{
    vec2 cell = floor(position * vec2(92.0, 138.0));
    float coarse = hash21(cell);
    float thread = sin(position.y * 980.0 + coarse * 5.0) * 0.5 + 0.5;
    return coarse * 0.62 + thread * 0.38;
}

vec3 frozenBackground(vec2 position)
{
    vec2 backgroundPosition = vec2(position.x, 1.0 - position.y);
    return texture(backgroundTex, backgroundPosition).rgb;
}

void main(void)
{
    vec3 baseColor;
    float materialAlpha = alpha;
    if (materialKind == 0 || materialKind == 3)
    {
        float fiber = paperFiber(uv * 1.7);
        float age = hash21(floor(uv * vec2(58.0, 76.0)));
        baseColor = vec3(0.73, 0.57, 0.34)
            * mix(0.95, 1.05, fiber)
            * mix(0.98, 1.02, age);

        if (materialKind == 0)
        {
            float edgeDistance = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
            baseColor *= mix(0.70, 1.0, smoothstep(0.0, 0.075, edgeDistance));

            // Restrained contour ink makes this read as a map surface without
            // putting branding, words or a flat screen-space logo on it.
            vec2 mapPosition = uv - 0.5;
            mapPosition += vec2(
                sin(mapPosition.y * 7.0) * 0.055,
                sin(mapPosition.x * 6.0) * 0.045
            );
            float relief = length(mapPosition * vec2(1.08, 0.82));
            float contourDistance = abs(fract(relief * 10.5) - 0.5);
            float ink = 1.0 - smoothstep(0.0, 0.040, contourDistance);
            baseColor = mix(baseColor, vec3(0.20, 0.24, 0.20), ink * 0.24);
        }
    }
    else if (materialKind == 1)
    {
        float grain = sin((uv.y * 22.0 + uv.x * 2.5) * 6.28318) * 0.5 + 0.5;
        baseColor = mix(vec3(0.12, 0.055, 0.018), vec3(0.34, 0.18, 0.055), grain);
    }
    else if (materialKind == 2)
    {
        float patina = hash21(floor(uv * 40.0));
        baseColor = mix(vec3(0.46, 0.27, 0.075), vec3(0.88, 0.68, 0.26), patina);
    }
    else if (materialKind == 4)
    {
        baseColor = vec3(0.64, 0.42, 0.28);
    }
    else if (materialKind == 5)
    {
        float weave = sin((uv.x + uv.y) * 110.0) * 0.025;
        baseColor = vec3(0.16, 0.20, 0.18) + weave;
    }
    else if (materialKind == 6)
    {
        baseColor = texture(entityTex, uv).rgb * entityColor.rgb;
    }
    else if (materialKind == 8)
    {
        baseColor = backgroundAvailable > 0
            ? frozenBackground(uv) * vec3(0.62, 0.66, 0.64)
            : vec3(0.012, 0.020, 0.027);
    }
    else if (materialKind == 9)
    {
        // Atlas compass casing: a deliberately simple, authored brass body.
        float brushed = sin(uv.y * 94.0 + uv.x * 7.0) * 0.035;
        baseColor = vec3(0.40, 0.25, 0.075) + brushed;
    }
    else if (materialKind == 10)
    {
        baseColor = texture(compassTex, uv).rgb;
    }
    else if (materialKind == 11)
    {
        baseColor = objectPosition.y >= 0.0
            ? vec3(0.78, 0.12, 0.075)
            : vec3(0.10, 0.16, 0.20);
    }
    else
    {
        vec4 atlasColor = texture(atlasTex, uv);
        baseColor = atlasColor.rgb;
        // Atlas coverage is binary at presentation time. The completed atlas
        // texture has already composed authored water alpha, OIT materials and
        // fog internally. Do not blend that completed image a second time
        // with the player's normal POV, because it makes world blocks appear
        // translucent through the map.
        // Native opaque chunk shaders do not provide presentation coverage in
        // Primary's alpha attachment. The scroll viewport itself is the
        // coverage mask, so make every pixel inside it fully opaque and let
        // its scissor rectangle clip terrain and liquids at the paper edge.
        materialAlpha = 1.0;
    }

    vec3 normal = normalize(viewNormal);
    vec3 lightDirection = normalize(vec3(-0.42, 0.74, 0.52));
    float diffuse = 0.48 + max(dot(normal, lightDirection), 0.0) * 0.52;
    float rim = pow(1.0 - abs(normal.z), 2.2) * 0.14;

    float sweepCenter = mix(-0.25, 1.25, clamp(lightSweep, 0.0, 1.0));
    float sweep = materialKind == 0
        ? exp(-pow((uv.x - sweepCenter) * 6.0, 2.0)) * smoothstep(0.0, 0.28, lightSweep)
        : 0.0;
    vec3 finalColor = materialKind == 8
        ? baseColor
        : materialKind == 7
        ? baseColor * (0.90 + diffuse * 0.10)
        : baseColor * (diffuse + rim);
    finalColor += vec3(0.30, 0.42, 0.38) * sweep * 0.50;
    outColor = vec4(finalColor, materialAlpha);
}
