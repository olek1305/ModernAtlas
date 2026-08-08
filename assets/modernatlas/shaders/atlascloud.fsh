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
uniform float cloudPlaneY;
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

float valueNoise(vec2 position)
{
    vec2 cell = floor(position);
    vec2 blend = smoothstep(vec2(0.0), vec2(1.0), fract(position));
    return mix(
        mix(hash21(cell), hash21(cell + vec2(1.0, 0.0)), blend.x),
        mix(hash21(cell + vec2(0.0, 1.0)), hash21(cell + vec2(1.0)), blend.x),
        blend.y
    );
}

void main()
{
    const float cloudTileSize = 50.0;
    mat4 inverseMvp = inverse(projectionMatrix * modelViewMatrix);
    vec3 origin = unproject(inverseMvp, vec4(ndc, -1.0, 1.0));
    vec3 farPoint = unproject(inverseMvp, vec4(ndc, 1.0, 1.0));
    vec3 direction = normalize(farPoint - origin);
    if (abs(direction.y) < 0.0001) discard;

    float distanceToCloud = (cloudPlaneY - origin.y) / direction.y;
    if (distanceToCloud <= 0.0) discard;

    ivec2 depthPixel = ivec2(gl_FragCoord.xy * depthScale);
    float depth = texelFetch(depthTex, depthPixel, 0).r;
    // Primary was cleared before the atlas terrain draw. Its untouched far
    // depth marks pixels outside loaded chunk meshes; never paint clouds onto
    // the dialog background or across missing chunks.
    if (depth >= 0.999999) discard;
    vec3 terrain = unproject(inverseMvp, vec4(ndc, depth * 2.0 - 1.0, 1.0));
    if (distance(origin, terrain) < distanceToCloud) discard;

    vec3 cloudPoint = origin + direction * distanceToCloud;
    vec2 mapPosition = (cloudPoint.xz - cloudOffset.xz) / cloudTileSize
        + cloudMapWidth * 0.5;
    if (any(lessThan(mapPosition, vec2(1.0)))
        || any(greaterThanEqual(mapPosition, vec2(cloudMapWidth - 2.0)))) discard;

    vec4 mapValue = sampleSmooth(cloudMap, mapPosition);
    vec4 cloudColor = sampleSmooth(cloudCol, mapPosition);
    // The engine weather map is intentionally one value per 50-block tile.
    // Add stable detail in the same wind-relative coordinates so a tilted
    // atlas does not expose those tiles as large translucent squares. The
    // weather map still controls where clouds exist and their live colour.
    vec2 localPosition = cloudPoint.xz - cloudOffset.xz;
    float detail = valueNoise(localPosition / 18.0) * 0.52
        + valueNoise(localPosition / 39.0) * 0.31
        + valueNoise(localPosition / 82.0) * 0.17;
    float coverage = smoothstep(
        0.38,
        0.72,
        detail + min(max(mapValue.r, 0.0), 2.0) * 0.16
    );
    float alpha = clamp(1.0 - exp(-max(0.0, mapValue.r) * 0.38), 0.0, 0.58)
        * coverage;
    float edgeDistance = min(
        min(mapPosition.x, mapPosition.y),
        min(cloudMapWidth - mapPosition.x, cloudMapWidth - mapPosition.y)
    );
    alpha *= smoothstep(1.0, cloudMapWidth * 0.12, edgeDistance);
    if (alpha < 0.005) discard;

    outColor = vec4(cloudColor.rgb, alpha);
}
