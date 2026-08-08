#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

in vec2 ndc;

layout(location = 0) out vec4 outColor;

uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform sampler2D depthTex;
uniform sampler2D cloudMap;
uniform sampler2D cloudCol;
uniform vec3 cloudOffset;
uniform float cloudBaseY;
uniform float cloudThickness;
uniform float cloudMapWidth;
uniform vec2 depthScale;

vec3 unproject(mat4 inverseMvp, vec4 position)
{
    vec4 result = inverseMvp * position;
    return result.xyz / max(abs(result.w), 0.0001);
}

vec4 sampleSmooth(sampler2D textureSampler, vec2 position)
{
    vec2 shifted = position - 0.5;
    ivec2 base = ivec2(floor(shifted));
    vec2 blend = smoothstep(vec2(0.0), vec2(1.0), fract(shifted));
    vec4 northWest = texelFetch(textureSampler, base, 0);
    vec4 northEast = texelFetch(textureSampler, base + ivec2(1, 0), 0);
    vec4 southWest = texelFetch(textureSampler, base + ivec2(0, 1), 0);
    vec4 southEast = texelFetch(textureSampler, base + ivec2(1, 1), 0);
    return mix(mix(northWest, northEast, blend.x), mix(southWest, southEast, blend.x), blend.y);
}

float hash21(vec2 position)
{
    vec3 p = fract(vec3(position.xyx) * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}

float hash31(vec3 position)
{
    position = fract(position * 0.1031);
    position += dot(position, position.yzx + 33.33);
    return fract((position.x + position.y) * position.z);
}

float valueNoise3d(vec3 position)
{
    vec3 cell = floor(position);
    vec3 blend = smoothstep(vec3(0.0), vec3(1.0), fract(position));
    float lower = mix(
        mix(hash31(cell), hash31(cell + vec3(1.0, 0.0, 0.0)), blend.x),
        mix(hash31(cell + vec3(0.0, 1.0, 0.0)), hash31(cell + vec3(1.0, 1.0, 0.0)), blend.x),
        blend.y
    );
    float upper = mix(
        mix(hash31(cell + vec3(0.0, 0.0, 1.0)), hash31(cell + vec3(1.0, 0.0, 1.0)), blend.x),
        mix(hash31(cell + vec3(0.0, 1.0, 1.0)), hash31(cell + vec3(1.0)), blend.x),
        blend.y
    );
    return mix(lower, upper, blend.z);
}

void main()
{
    const float cloudTileSize = 50.0;
    mat4 inverseMvp = inverse(projectionMatrix * modelViewMatrix);
    vec3 origin = unproject(inverseMvp, vec4(ndc, -1.0, 1.0));
    vec3 farPoint = unproject(inverseMvp, vec4(ndc, 1.0, 1.0));
    vec3 direction = normalize(farPoint - origin);
    if (abs(direction.y) < 0.0001) discard;

    float distanceToBase = (cloudBaseY - origin.y) / direction.y;
    float distanceToTop = (cloudBaseY + cloudThickness - origin.y) / direction.y;
    float cloudNear = max(0.0, min(distanceToBase, distanceToTop));
    float cloudFar = max(distanceToBase, distanceToTop);
    if (cloudFar <= cloudNear) discard;

    ivec2 depthPixel = ivec2(gl_FragCoord.xy * depthScale);
    float depth = texelFetch(depthTex, depthPixel, 0).r;
    // Untouched far depth is outside loaded terrain. Do not let the cloud
    // volume cover the GUI background or reveal missing chunks.
    if (depth >= 0.999999) discard;
    vec3 terrain = unproject(inverseMvp, vec4(ndc, depth * 2.0 - 1.0, 1.0));
    cloudFar = min(cloudFar, distance(origin, terrain));
    if (cloudFar <= cloudNear) discard;

    vec3 cloudPoint = origin + direction * ((cloudNear + cloudFar) * 0.5);
    vec2 mapPosition = (cloudPoint.xz - cloudOffset.xz) / cloudTileSize
        + cloudMapWidth * 0.5;
    if (any(lessThan(mapPosition, vec2(1.0)))
        || any(greaterThanEqual(mapPosition, vec2(cloudMapWidth - 2.0)))) discard;

    vec4 mapValue = sampleSmooth(cloudMap, mapPosition);
    vec4 cloudColor = sampleSmooth(cloudCol, mapPosition);
    float weatherDensity = clamp(max(mapValue.r, 0.0) * 0.48, 0.0, 1.0);
    if (weatherDensity < 0.01) discard;

    // A short ray march gives the clouds visible sides and rounded tops. The
    // noise has no time input: the live cloud offset moves it with the game's
    // wind, but its height cannot pulse or grow.
    const int sampleCount = 12;
    float segmentLength = (cloudFar - cloudNear) / float(sampleCount);
    float jitter = hash21(gl_FragCoord.xy) * segmentLength;
    vec4 accumulated = vec4(0.0);
    for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
    {
        float rayDistance = cloudNear
            + (float(sampleIndex) + 0.2) * segmentLength
            + jitter * 0.6;
        vec3 samplePoint = origin + direction * rayDistance;
        vec2 localPosition = samplePoint.xz - cloudOffset.xz;
        float height = clamp((samplePoint.y - cloudBaseY) / cloudThickness, 0.0, 1.0);

        vec3 noisePosition = vec3(localPosition.x, samplePoint.y, localPosition.y) / 34.0;
        float coarse = valueNoise3d(noisePosition);
        float fine = valueNoise3d(noisePosition * 2.07 + vec3(11.7, 3.1, -8.4));
        float shapeNoise = coarse * 0.68 + fine * 0.32;

        float puffTop = clamp(
            0.42 + weatherDensity * 0.38 + (coarse - 0.5) * 0.34,
            0.32,
            0.96
        );
        float verticalShape = smoothstep(0.02, 0.16, height)
            * (1.0 - smoothstep(max(0.18, puffTop - 0.20), puffTop, height));
        float density = smoothstep(
            0.40,
            0.72,
            shapeNoise + weatherDensity * 0.24
        ) * verticalShape * weatherDensity;

        float sampleAlpha = 1.0 - exp(-density * segmentLength * 0.040);
        float topLight = mix(
            0.72,
            1.13,
            smoothstep(0.08, max(0.16, puffTop), height)
        );
        float softEdge = 1.0 - smoothstep(0.52, 0.92, density);
        vec3 sampleColor = cloudColor.rgb * topLight;
        sampleColor = mix(sampleColor, vec3(1.0), softEdge * 0.08);

        accumulated.rgb += (1.0 - accumulated.a) * sampleColor * sampleAlpha;
        accumulated.a += (1.0 - accumulated.a) * sampleAlpha;
        if (accumulated.a >= 0.72) break;
    }

    float edgeDistance = min(
        min(mapPosition.x, mapPosition.y),
        min(cloudMapWidth - mapPosition.x, cloudMapWidth - mapPosition.y)
    );
    float cloudAlpha = min(accumulated.a, 0.76)
        * smoothstep(1.0, cloudMapWidth * 0.12, edgeDistance);

    // A restrained, displaced cloud-only shadow makes the layer read as
    // floating above terrain. It is independent of the engine shadow map, so
    // it cannot recreate the dark square-per-chunk artifact.
    vec2 shadowLocalPosition = terrain.xz - cloudOffset.xz + vec2(-24.0, -28.0);
    vec2 shadowMapPosition = shadowLocalPosition / cloudTileSize
        + cloudMapWidth * 0.5;
    float shadowAlpha = 0.0;
    if (all(greaterThanEqual(shadowMapPosition, vec2(1.0)))
        && all(lessThan(shadowMapPosition, vec2(cloudMapWidth - 2.0))))
    {
        float shadowWeather = clamp(
            max(sampleSmooth(cloudMap, shadowMapPosition).r, 0.0) * 0.48,
            0.0,
            1.0
        );
        vec3 shadowNoisePosition = vec3(
            shadowLocalPosition.x,
            cloudBaseY + cloudThickness * 0.48,
            shadowLocalPosition.y
        ) / 34.0;
        float shadowCoarse = valueNoise3d(shadowNoisePosition);
        float shadowFine = valueNoise3d(
            shadowNoisePosition * 2.07 + vec3(11.7, 3.1, -8.4)
        );
        float shadowShape = smoothstep(
            0.38,
            0.72,
            shadowCoarse * 0.68 + shadowFine * 0.32 + shadowWeather * 0.24
        );
        shadowAlpha = shadowShape * shadowWeather * 0.10;
    }

    float alpha = cloudAlpha + (1.0 - cloudAlpha) * shadowAlpha;
    if (alpha < 0.005) discard;
    vec3 finalColor = accumulated.rgb / max(accumulated.a, 0.0001);
    vec3 shadowColor = vec3(0.055, 0.075, 0.095);
    vec3 combinedColor = (
        finalColor * cloudAlpha
        + shadowColor * shadowAlpha * (1.0 - cloudAlpha)
    ) / max(alpha, 0.0001);
    outColor = vec4(combinedColor, alpha);
}
