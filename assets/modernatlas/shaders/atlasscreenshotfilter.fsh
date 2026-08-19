#version 330 core

in vec2 ndc;

layout(location = 0) out vec4 outColor;

uniform sampler2D sourceColorTex;
uniform sampler2D sourceDepthTex;
uniform sampler2D validityTex;
uniform sampler2D bloomTex;
uniform int passMode;
uniform int bloomTextureEnabled;
uniform int depthTextureEnabled;
uniform int validityTextureEnabled;
uniform mat4 inverseProjectionView;
uniform vec2 sourceSize;
uniform vec2 targetSize;
uniform float filterIntensity;
uniform float exposure;
uniform float contrast;
uniform float saturation;
uniform float temperature;
uniform vec3 shadowTint;
uniform vec3 highlightTint;
uniform float shadowTintStrength;
uniform float depthReliefStrength;
uniform float indirectLightStrength;
uniform float bloomStrength;
uniform int blurRadius;

const vec3 backgroundColor = vec3(0.035, 0.075, 0.11);
const ivec2 sampleOffsets[8] = ivec2[8](
    ivec2(1, 0),
    ivec2(-1, 0),
    ivec2(0, 1),
    ivec2(0, -1),
    ivec2(1, 1),
    ivec2(-1, 1),
    ivec2(1, -1),
    ivec2(-1, -1)
);

bool inside(ivec2 position, ivec2 dimensions)
{
    return !any(lessThan(position, ivec2(0)))
        && !any(greaterThanEqual(position, dimensions));
}

ivec2 sourcePosition(ivec2 targetPosition)
{
    vec2 uv = (vec2(targetPosition) + vec2(0.5))
        / max(targetSize, vec2(1.0));
    return ivec2(clamp(
        floor(uv * sourceSize),
        vec2(0.0),
        max(sourceSize - vec2(1.0), vec2(0.0))
    ));
}

bool validSourcePixel(ivec2 position)
{
    if (validityTextureEnabled <= 0) return true;
    ivec2 dimensions = textureSize(validityTex, 0);
    return inside(position, dimensions)
        && texelFetch(validityTex, position, 0).r >= 0.5;
}

bool validPixel(ivec2 targetPosition)
{
    if (validityTextureEnabled <= 0) return true;
    ivec2 dimensions = textureSize(validityTex, 0);
    vec2 uv = (vec2(targetPosition) + vec2(0.5))
        / max(targetSize, vec2(1.0));
    ivec2 validityPosition = ivec2(clamp(
        floor(uv * vec2(dimensions)),
        vec2(0.0),
        max(vec2(dimensions) - vec2(1.0), vec2(0.0))
    ));
    return inside(validityPosition, dimensions)
        && texelFetch(validityTex, validityPosition, 0).r >= 0.5;
}

float readSourceDepth(ivec2 position);

vec3 readColor(ivec2 targetPosition)
{
    ivec2 position = sourcePosition(targetPosition);
    ivec2 dimensions = textureSize(sourceColorTex, 0);
    if (!inside(position, dimensions)) return backgroundColor;
    return texelFetch(sourceColorTex, position, 0).rgb;
}

float readDepth(ivec2 targetPosition)
{
    if (depthTextureEnabled <= 0) return 1.0;
    return readSourceDepth(sourcePosition(targetPosition));
}

float readSourceDepth(ivec2 position)
{
    if (depthTextureEnabled <= 0) return 1.0;
    ivec2 dimensions = textureSize(sourceDepthTex, 0);
    if (!inside(position, dimensions)) return 1.0;
    return texelFetch(sourceDepthTex, position, 0).r;
}

vec3 readSourceColor(ivec2 position)
{
    ivec2 dimensions = textureSize(sourceColorTex, 0);
    if (!inside(position, dimensions)) return backgroundColor;
    return texelFetch(sourceColorTex, position, 0).rgb;
}

bool reconstructPosition(ivec2 position, out vec3 result)
{
    if (depthTextureEnabled <= 0 || !validSourcePixel(position)) return false;
    float depth = readSourceDepth(position);
    if (depth >= 0.999999) return false;
    vec2 uv = (vec2(position) + vec2(0.5)) / max(sourceSize, vec2(1.0));
    vec4 clipPosition = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 worldPosition = inverseProjectionView * clipPosition;
    if (abs(worldPosition.w) < 0.0001) return false;
    result = worldPosition.xyz / worldPosition.w;
    return all(greaterThanEqual(result, vec3(-1000000.0)))
        && all(lessThanEqual(result, vec3(1000000.0)));
}

bool reconstructNormal(ivec2 position, out vec3 normal, out vec3 center)
{
    if (!reconstructPosition(position, center)) return false;

    vec3 left;
    vec3 right;
    vec3 down;
    vec3 up;
    bool hasLeft = reconstructPosition(position + ivec2(-1, 0), left);
    bool hasRight = reconstructPosition(position + ivec2(1, 0), right);
    bool hasDown = reconstructPosition(position + ivec2(0, -1), down);
    bool hasUp = reconstructPosition(position + ivec2(0, 1), up);

    vec3 horizontal = hasLeft && hasRight
        ? right - left
        : hasRight ? right - center : hasLeft ? center - left : vec3(0.0);
    vec3 vertical = hasDown && hasUp
        ? up - down
        : hasUp ? up - center : hasDown ? center - down : vec3(0.0);
    if (length(horizontal) < 0.0001 || length(vertical) < 0.0001) {
        return false;
    }
    normal = normalize(cross(horizontal, vertical));
    return length(normal) > 0.001;
}

float luminance(vec3 color)
{
    return dot(max(color, vec3(0.0)), vec3(0.2126, 0.7152, 0.0722));
}

// This is deliberately a small depth-relief cue, not a shadow map or a
// camera-world occlusion implementation. It only uses reconstructed visible
// positions, rejects disocclusions, and never crosses a zero-validity pixel.
float screenDepthRelief(ivec2 position)
{
    if (depthReliefStrength <= 0.001 || !validPixel(position)) return 0.0;
    vec3 center;
    vec3 normal;
    ivec2 centerSource = sourcePosition(position);
    if (!reconstructNormal(centerSource, normal, center)) return 0.0;

    float relief = 0.0;
    float weightSum = 0.0;
    for (int index = 0; index < 8; index++)
    {
        ivec2 samplePosition = centerSource + sampleOffsets[index];
        vec3 sampleWorld;
        if (!reconstructPosition(samplePosition, sampleWorld)) {
            continue;
        }
        vec3 delta = sampleWorld - center;
        float distanceToSample = length(delta);
        if (distanceToSample < 0.0001 || distanceToSample > 12.0) continue;
        vec3 direction = delta / distanceToSample;
        float normalAgreement = 1.0 - smoothstep(
            0.45,
            1.0,
            abs(dot(normal, direction))
        );
        float depthAgreement = 1.0 - smoothstep(0.35, 4.0, distanceToSample);
        float sampleWeight = normalAgreement * depthAgreement
            / (1.0 + distanceToSample * 0.5);
        relief += (1.0 - normalAgreement) * sampleWeight;
        weightSum += sampleWeight;
    }
    return weightSum > 0.0001
        ? clamp(relief / weightSum, 0.0, 1.0) * depthReliefStrength
        : 0.0;
}

vec3 screenIndirectLight(ivec2 position)
{
    if (indirectLightStrength <= 0.001 || !validPixel(position)) {
        return vec3(0.0);
    }
    vec3 center;
    vec3 normal;
    ivec2 centerSource = sourcePosition(position);
    if (!reconstructNormal(centerSource, normal, center)) return vec3(0.0);

    vec3 accumulated = vec3(0.0);
    float weightSum = 0.0;
    for (int index = 0; index < 8; index++)
    {
        ivec2 samplePosition = centerSource + sampleOffsets[index] * 2;
        if (!validSourcePixel(samplePosition)) continue;
        vec3 sampleWorld;
        if (!reconstructPosition(samplePosition, sampleWorld)) {
            continue;
        }
        vec3 delta = sampleWorld - center;
        float distanceToSample = length(delta);
        if (distanceToSample < 0.05 || distanceToSample > 10.0) continue;
        vec3 direction = delta / distanceToSample;
        float normalAgreement = 0.25 + 0.75 * (1.0 - smoothstep(
            0.45,
            1.0,
            abs(dot(normal, direction))
        ));
        float distanceWeight = 1.0 - smoothstep(0.5, 8.0, distanceToSample);
        float depthWeight = 1.0 - smoothstep(
            0.25,
            3.5,
            abs(readSourceDepth(samplePosition)
                - readSourceDepth(centerSource)) * 18.0
        );
        float weight = normalAgreement * distanceWeight * depthWeight;
        accumulated += readSourceColor(samplePosition) * weight;
        weightSum += weight;
    }
    if (weightSum <= 0.0001) return vec3(0.0);
    // The final multiplier is intentionally small: this is a visible-pixel
    // color-bounce approximation, never true global illumination.
    return accumulated / weightSum * indirectLightStrength * 0.045;
}

vec3 applyFullFilter(ivec2 position, vec3 original)
{
    vec3 color = max(original, vec3(0.0));
    color *= exp2(exposure);

    float grey = luminance(color);
    color = mix(vec3(grey), color, max(0.0, saturation));
    color = (color - vec3(0.5)) * max(0.0, contrast) + vec3(0.5);

    vec3 warmTint = temperature >= 0.0
        ? vec3(1.0 + temperature * 0.16, 1.0, 1.0 - temperature * 0.12)
        : vec3(1.0 + temperature * 0.10, 1.0, 1.0 - temperature * 0.16);
    color *= mix(vec3(1.0), warmTint, 0.45);

    grey = luminance(color);
    float shadowWeight = 1.0 - smoothstep(0.16, 0.58, grey);
    float highlightWeight = smoothstep(0.55, 0.96, grey);
    color = mix(color, color * shadowTint,
        shadowWeight * shadowTintStrength);
    color = mix(color, color * highlightTint,
        highlightWeight * 0.12);

    float relief = screenDepthRelief(position);
    color *= 1.0 - relief * 0.24;
    color += screenIndirectLight(position);

    // Bounded filmic compression keeps bright snow, clouds and lava within
    // the opaque screenshot range without affecting the live atlas.
    color = max(color, vec3(0.0));
    color = color / (vec3(1.0) + color * 0.075) * 1.075;
    return clamp(color, vec3(0.0), vec3(1.0));
}

vec3 readBloom(ivec2 position)
{
    if (!validPixel(position)) return vec3(0.0);
    return max(readColor(position) - vec3(0.72), vec3(0.0));
}

vec3 blurBloom(ivec2 position, bool vertical, bool extractHighlights)
{
    vec3 accumulated = vec3(0.0);
    float accumulatedWeight = 0.0;
    int radius = clamp(blurRadius, 1, 8);
    for (int offset = -8; offset <= 8; offset++)
    {
        if (abs(offset) > radius) continue;
        ivec2 samplePosition = position
            + (vertical ? ivec2(0, offset) : ivec2(offset, 0));
        if (!validPixel(samplePosition)) continue;
        float distanceWeight = 1.0 - abs(float(offset))
            / float(radius + 1);
        vec3 sampleColor = extractHighlights
            ? readBloom(samplePosition)
            : max(readColor(samplePosition), vec3(0.0));
        accumulated += sampleColor * distanceWeight;
        accumulatedWeight += distanceWeight;
    }
    return accumulatedWeight > 0.0
        ? accumulated / accumulatedWeight
        : vec3(0.0);
}

void main()
{
    ivec2 position = ivec2(gl_FragCoord.xy);
    bool valid = validPixel(position);
    if (passMode == 1 || passMode == 2)
    {
        outColor = vec4(
            valid
                ? blurBloom(position, passMode == 2, passMode == 1)
                : vec3(0.0),
            1.0
        );
        return;
    }

    vec3 original = readColor(position);
    if (!valid)
    {
        // Spatial filters never borrow from invalid pixels. The color-only
        // path still preserves the opaque boundary background exactly.
        outColor = vec4(original, 1.0);
        return;
    }

    float effect = clamp(filterIntensity, 0.0, 2.0);
    vec3 color = effect <= 0.000001
        ? original
        : mix(original, applyFullFilter(position, original), effect);
    if (passMode == 3 && bloomTextureEnabled > 0 && effect > 0.000001)
    {
        vec2 bloomUv = (vec2(position) + vec2(0.5))
            / max(targetSize, vec2(1.0));
        vec3 bloom = texture(bloomTex, bloomUv).rgb;
        color += bloom * bloomStrength * 0.75 * effect;
    }
    outColor = vec4(clamp(color, vec3(0.0), vec3(1.0)), 1.0);
}
