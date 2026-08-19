#version 330 core

uniform int materialKind;
uniform float alpha;
uniform float lightSweep;
uniform sampler2D entityTex;
uniform vec4 entityColor;
uniform sampler2D atlasTex;
uniform vec2 atlasUvOffset;
uniform vec2 atlasUvScale;
uniform sampler2D backgroundTex;
uniform sampler2D compassTex;
uniform int backgroundAvailable;
uniform int weatherType;
uniform float weatherIntensity;
uniform float weatherFog;
uniform float weatherWindOffset;
uniform float weatherLightning;
uniform float weatherTime;
uniform int weatherSurface;
uniform float weatherLayerScale;

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

float weatherParticle(vec2 position, vec2 cells, float timeOffset, float radius)
{
    vec2 scaled = position * cells;
    vec2 cell = floor(scaled);
    vec2 local = fract(scaled) - 0.5;
    float seed = hash21(cell + timeOffset);
    local.x += (seed - 0.5) * 0.58;
    local.y += (hash21(cell + 17.31) - 0.5) * 0.45;
    return (1.0 - smoothstep(radius, radius * 1.8, length(local)))
        * step(0.76, seed);
}

vec4 scrollWeather(vec2 position)
{
    float intensity = clamp(weatherIntensity, 0.0, 1.0);
    float fogAmount = clamp(weatherFog, 0.0, 1.0);
    vec3 color = vec3(0.78, 0.84, 0.86);
    float coverage = 0.0;

    if (weatherType == 1)
    {
        // Sparse diagonal streaks and tiny wet marks remain below the visual
        // weight of map symbols and controls.
        vec2 moving = position;
        moving.x += weatherWindOffset * 0.72;
        moving.y += weatherTime * 1.35;
        vec2 grid = moving * vec2(24.0, 10.0);
        vec2 cell = floor(grid);
        vec2 local = fract(grid) - 0.5;
        float seed = hash21(cell);
        float streak = (1.0 - smoothstep(0.035, 0.095, abs(local.x + local.y * 0.18)))
            * (1.0 - smoothstep(0.12, 0.48, abs(local.y)))
            * step(0.72, seed);
        float wetMark = weatherSurface > 0
            ? weatherParticle(position, vec2(17.0, 13.0), 4.7, 0.065) * 0.18
            : 0.0;
        coverage = (streak * 0.20 + wetMark * 0.08) * intensity;
        color = vec3(0.60, 0.72, 0.77);
    }
    else if (weatherType == 2)
    {
        vec2 moving = position + vec2(
            sin(weatherTime * 0.7 + position.y * 8.0) * 0.035
                + weatherWindOffset * 0.24,
            weatherTime * 0.16
        );
        float flakes = weatherParticle(moving, vec2(13.0, 10.0), 2.1, 0.095);
        float fineFlakes = weatherParticle(
            moving + vec2(0.31, weatherTime * 0.09),
            vec2(21.0, 16.0),
            9.4,
            0.070
        );
        coverage = (flakes * 0.18 + fineFlakes * 0.080) * intensity;
        color = vec3(0.93, 0.96, 0.98);
    }
    else if (weatherType == 3)
    {
        vec2 moving = position + vec2(
            weatherWindOffset * 0.34,
            weatherTime * 0.72
        );
        float hail = weatherParticle(moving, vec2(20.0, 15.0), 6.3, 0.075);
        coverage = hail * 0.16 * intensity;
        color = vec3(0.88, 0.94, 0.97);
    }

    float hazeNoise = hash21(floor(
        (position + vec2(weatherTime * 0.006, 0.0)) * vec2(7.0, 5.0)
    ));
    float haze = fogAmount * mix(0.025, 0.075, hazeNoise);
    float lightning = clamp(weatherLightning, 0.0, 1.0)
        * (weatherSurface > 0 ? 0.10 : 0.055);
    float alphaOut = clamp(
        (coverage + haze + lightning) * clamp(weatherLayerScale, 0.0, 1.0),
        0.0,
        0.22
    );
    color = mix(color, vec3(0.72, 0.76, 0.76), haze * 4.0);
    color = mix(color, vec3(0.94, 0.97, 1.0), lightning * 7.0);
    return vec4(color, alphaOut);
}

float inkLine(float value, float width)
{
    return 1.0 - smoothstep(width, width * 1.8, abs(value));
}

float segmentInk(vec2 position, vec2 start, vec2 end, float width)
{
    vec2 segment = end - start;
    float along = clamp(
        dot(position - start, segment) / max(dot(segment, segment), 0.00001),
        0.0,
        1.0
    );
    float distanceToSegment = length(position - (start + segment * along));
    return 1.0 - smoothstep(width, width * 1.8, distanceToSegment);
}

float compassRose(vec2 position, vec2 center, float size)
{
    vec2 ray = position - center;
    float radius = length(ray);
    float angle = atan(ray.y, ray.x);
    float cardinal = pow(abs(cos(angle * 2.0)), 28.0)
        * smoothstep(size * 0.10, size * 0.22, radius)
        * (1.0 - smoothstep(size * 0.58, size, radius));
    float diagonal = pow(abs(cos(angle * 4.0 + 0.7854)), 38.0)
        * smoothstep(size * 0.14, size * 0.25, radius)
        * (1.0 - smoothstep(size * 0.40, size * 0.68, radius));
    float ring = inkLine(radius - size * 0.72, size * 0.035);
    float hub = 1.0 - smoothstep(size * 0.055, size * 0.105, radius);
    return max(max(cardinal, diagonal * 0.58), max(ring * 0.64, hub));
}

float mapMarker(vec2 position, vec2 center, float size)
{
    float radius = length(position - center);
    float ring = inkLine(radius - size, size * 0.18);
    float centerDot = 1.0 - smoothstep(size * 0.18, size * 0.40, radius);
    return max(ring * 0.82, centerDot);
}

float mountainMark(vec2 position, vec2 center, float size)
{
    vec2 left = center + vec2(-size, -size * 0.55);
    vec2 peak = center + vec2(0.0, size);
    vec2 right = center + vec2(size, -size * 0.55);
    float ridge = max(
        segmentInk(position, left, peak, size * 0.075),
        segmentInk(position, peak, right, size * 0.075)
    );
    float snow = max(
        segmentInk(
            position,
            center + vec2(-size * 0.32, size * 0.48),
            center + vec2(-size * 0.08, size * 0.20),
            size * 0.055
        ),
        segmentInk(
            position,
            center + vec2(-size * 0.08, size * 0.20),
            center + vec2(size * 0.18, size * 0.44),
            size * 0.055
        )
    );
    return max(ridge, snow * 0.72);
}

float cartographicInk(vec2 position)
{
    vec2 p = position - 0.5;

    // One continuous river follows the western side of the sheet. Its two
    // frequencies keep it hand drawn without turning it into random noise.
    float river = inkLine(
        p.x + 0.27 - p.y * 0.13
            + sin(p.y * 13.0 + 0.8) * 0.030
            + sin(p.y * 29.0) * 0.009,
        0.0034
    ) * smoothstep(-0.45, -0.37, p.y)
        * (1.0 - smoothstep(0.30, 0.43, p.y));

    // The main traveller's road crosses the centre, with one deliberate fork
    // heading north-east from its marked junction.
    float mainRoadY = 0.015 + p.x * 0.13
        + sin((p.x + 0.36) * 9.0) * 0.017;
    float mainRoadMask = smoothstep(-0.45, -0.37, p.x)
        * (1.0 - smoothstep(0.35, 0.44, p.x));
    float mainRoad = inkLine(p.y - mainRoadY, 0.0032) * mainRoadMask;

    float branchY = 0.020 + (p.x + 0.035) * 0.67
        + sin((p.x + 0.04) * 15.0) * 0.011;
    float branchMask = smoothstep(-0.06, -0.015, p.x)
        * (1.0 - smoothstep(0.27, 0.34, p.x));
    float branchRoad = inkLine(p.y - branchY, 0.0028) * branchMask;

    // Faint dashed bearings connect known places to a single compass rose.
    float dash = smoothstep(0.05, 0.42, sin((p.x + p.y) * 145.0));
    float bearings = max(
        segmentInk(position, vec2(0.76, 0.24), vec2(0.49, 0.51), 0.0012),
        segmentInk(position, vec2(0.76, 0.24), vec2(0.68, 0.66), 0.0012)
    ) * dash * 0.30;

    float places = max(
        mapMarker(position, vec2(0.49, 0.51), 0.014),
        mapMarker(position, vec2(0.68, 0.66), 0.011)
    );
    places = max(places, mapMarker(position, vec2(0.29, 0.46), 0.009));

    float terrain = max(
        mountainMark(position, vec2(0.33, 0.72), 0.045),
        mountainMark(position, vec2(0.39, 0.69), 0.034)
    ) * 0.72;
    float rose = compassRose(position, vec2(0.79, 0.23), 0.080);

    float strongInk = max(max(river, mainRoad), max(branchRoad, places));
    return max(strongInk, max(rose * 0.88, max(terrain, bearings)));
}

vec3 frozenBackground(vec2 position)
{
    vec2 backgroundPosition = vec2(position.x, 1.0 - position.y);
    return texture(backgroundTex, backgroundPosition).rgb;
}

void main(void)
{
    if (materialKind == 15)
    {
        outColor = scrollWeather(uv);
        return;
    }

    vec3 baseColor;
    float materialAlpha = alpha;
    if (materialKind == 0 || materialKind == 3)
    {
        float fiber = paperFiber(uv * 1.7);
        float age = hash21(floor(uv * vec2(58.0, 76.0)));
        float broadMottle = hash21(floor(uv * vec2(8.0, 11.0)));
        baseColor = vec3(0.84, 0.68, 0.40)
            * mix(0.91, 1.07, fiber)
            * mix(0.93, 1.04, age)
            * mix(0.90, 1.05, broadMottle);

        if (materialKind == 0)
        {
            float edgeDistance = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
            float edgeNoise = hash21(floor(uv * vec2(47.0, 61.0))) - 0.5;
            float wornEdge = 1.0 - smoothstep(
                0.018,
                0.135,
                edgeDistance + edgeNoise * 0.028
            );
            baseColor = mix(baseColor, vec3(0.30, 0.17, 0.070), wornEdge * 0.64);

            // Hand-drawn coastlines, routes and compass roses make the item
            // read as a travel map, while remaining subordinate to the live
            // atlas that replaces it at the end of the transition.
            float ink = cartographicInk(uv);
            baseColor = mix(baseColor, vec3(0.19, 0.105, 0.048), ink * 0.78);
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
    else if (materialKind == 18)
    {
        // The capture-area guide must remain legible over bright terrain and
        // filtered imagery. It is deliberately neutral and bypasses the
        // parchment/mesh lighting used by the other material kinds.
        baseColor = vec3(0.86, 0.94, 0.68);
        materialAlpha = alpha;
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
    else if (materialKind == 16)
    {
        // A failed or not-yet-published atlas frame still gets a fully opaque
        // neutral map surface. It is deliberately distinct from the paper so
        // the missing frame cannot expose the normal world framebuffer.
        float fiber = paperFiber(uv * 1.25);
        float mottle = hash21(floor(uv * vec2(13.0, 17.0)));
        baseColor = mix(
            vec3(0.20, 0.24, 0.25),
            vec3(0.29, 0.33, 0.34),
            fiber * 0.72 + mottle * 0.18
        );
        materialAlpha = 1.0;
    }
    else if (materialKind == 17)
    {
        // Screenshot Preview displays the frozen atlas framebuffer without
        // the parchment's diffuse tint. BEFORE and AFTER therefore compare
        // the same source pixels on screen, while the existing material 7
        // remains unchanged for the live scroll presentation.
        baseColor = texture(atlasTex, atlasUvOffset + uv * atlasUvScale).rgb;
        materialAlpha = 1.0;
    }
    else if (materialKind == 9)
    {
        // One rough, dark wooden casing shared by compass and sundial.
        float growthRing = sin(uv.y * 66.0 + uv.x * 10.0) * 0.5 + 0.5;
        float knot = hash21(floor(uv * vec2(22.0, 9.0)));
        baseColor = mix(
            vec3(0.20, 0.095, 0.025),
            vec3(0.43, 0.245, 0.070),
            growthRing * 0.72 + knot * 0.12
        );
    }
    else if (materialKind == 10)
    {
        vec4 instrumentFace = texture(compassTex, uv);
        baseColor = instrumentFace.rgb;
        materialAlpha *= instrumentFace.a;
    }
    else if (materialKind == 11)
    {
        baseColor = objectPosition.y >= 0.0
            ? vec3(0.78, 0.12, 0.075)
            : vec3(0.10, 0.16, 0.20);
    }
    else if (materialKind == 12)
    {
        // Charred-looking boundary between the wooden case and either dial.
        baseColor = vec3(0.055, 0.038, 0.020);
    }
    else if (materialKind == 13)
    {
        // High-contrast, narrow sundial shadow: this is the time indicator.
        baseColor = vec3(0.035, 0.014, 0.004);
    }
    else if (materialKind == 14)
    {
        // A small hand-cut wooden gnomon, lighter than its moving shadow.
        float wornEdge = smoothstep(0.030, 0.060, abs(objectPosition.x));
        baseColor = mix(
            vec3(0.31, 0.145, 0.035),
            vec3(0.58, 0.34, 0.105),
            wornEdge * 0.48
        );
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
        : materialKind == 17 || materialKind == 18
        ? baseColor
        : materialKind == 7
        ? baseColor * (0.90 + diffuse * 0.10)
        : baseColor * (diffuse + rim);
    finalColor += vec3(0.30, 0.42, 0.38) * sweep * 0.50;
    outColor = vec4(finalColor, materialAlpha);
}
