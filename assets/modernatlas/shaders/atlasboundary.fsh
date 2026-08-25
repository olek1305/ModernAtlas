#version 330 core

in vec2 ndc;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outValidity;

uniform sampler2D sourceColorTex;
uniform sampler2D sourceDepthTex;
uniform sampler2D surfaceHeightTex;
uniform mat4 inverseProjectionView;
uniform vec3 worldOffset;
uniform vec2 disclosureCenterXZ;
uniform float disclosureRadius;
uniform int completeBoundaryEnabled;
uniform vec2 completeBoundaryMinXZ;
uniform vec2 completeBoundaryMaxXZ;
uniform int surfaceCoverageEnabled;
uniform vec2 surfaceOriginXZ;
uniform float surfaceSampleSize;
uniform vec4 backgroundColor;
uniform vec3 atlasSkyLightDirection;
uniform vec3 atlasSkyLightColor;
uniform float atlasSkyDaylight;
uniform float atlasSkyExposure;

vec3 unproject(mat4 inverseMvp, vec4 position)
{
    vec4 result = inverseMvp * position;
    return result.xyz / max(abs(result.w), 0.0001);
}

bool hasSurfaceCoverage(vec2 absoluteXZ)
{
    if (surfaceCoverageEnabled <= 0) return true;

    ivec2 samplePosition = ivec2(floor(
        (absoluteXZ - surfaceOriginXZ) / surfaceSampleSize
    ));
    ivec2 dimensions = textureSize(surfaceHeightTex, 0);
    if (any(lessThan(samplePosition, ivec2(0)))
        || any(greaterThanEqual(samplePosition, dimensions)))
    {
        return false;
    }

    // AtlasSurfaceHeightTexture stores its validity marker in blue.
    return texelFetch(surfaceHeightTex, samplePosition, 0).b >= 0.5;
}

// This is deliberately a world-anchored atmospheric dome, not terrain. The
// exact atlas is orthographic, so a literal camera ray would be constant for
// every pixel. The continuous world position keeps the gradient stable when a
// high-resolution screenshot is rendered as multiple tiles.
vec3 atlasSkyBackground(
    vec3 relativeWorldPosition,
    vec3 cameraDirection,
    vec3 cameraUp,
    float skyScale
)
{
    float verticalCoordinate = dot(relativeWorldPosition, cameraUp)
        / max(skyScale, 1.0);
    vec3 skyDirection = normalize(
        cameraDirection + cameraUp * clamp(verticalCoordinate, -1.0, 1.0) * 0.46
    );
    float elevation = clamp(dot(skyDirection, vec3(0.0, 1.0, 0.0)), -1.0, 1.0);
    float horizonGradient = smoothstep(-0.22, 0.42, elevation);

    float daylight = smoothstep(0.10, 0.78, clamp(atlasSkyDaylight, 0.0, 1.5));
    vec3 nightZenith = vec3(0.012, 0.024, 0.070);
    vec3 nightHorizon = vec3(0.085, 0.135, 0.235);
    vec3 dayZenith = vec3(0.075, 0.205, 0.475);
    vec3 dayHorizon = vec3(0.52, 0.705, 0.90);
    vec3 zenith = mix(nightZenith, dayZenith, daylight);
    vec3 horizon = mix(nightHorizon, dayHorizon, daylight);
    vec3 color = mix(horizon, zenith, horizonGradient);

    // Keep dawn, sunset and moonlight recognizable without letting a native
    // world tint or player-local effect leak into the atlas framebuffer.
    vec3 celestialTint = clamp(atlasSkyLightColor, vec3(0.55), vec3(1.30));
    color *= mix(vec3(0.82, 0.88, 1.0), celestialTint, 0.28);
    color *= mix(0.72, 1.0, clamp(atlasSkyExposure / 1.15, 0.0, 1.0))
        * mix(
            1.0,
            1.12,
            clamp((atlasSkyExposure - 1.5) / 0.5, 0.0, 1.0)
        );

    vec3 lightDirection = normalize(atlasSkyLightDirection);
    float lightAlignment = max(dot(skyDirection, lightDirection), 0.0);
    float glow = pow(lightAlignment, 18.0) * 0.085
        + pow(lightAlignment, 72.0) * 0.115;
    color += celestialTint * glow * mix(0.45, 1.0, daylight);

    // The horizon band is intentionally subdued so distant buildings remain
    // legible while the atlas edge blends into the sky instead of exposing a
    // dark void.
    float horizonBand = 1.0 - smoothstep(0.03, 0.43, abs(elevation));
    vec3 hazeColor = mix(vec3(0.25, 0.34, 0.43), horizon, 0.55);
    color = mix(color, hazeColor, horizonBand * 0.16);
    return clamp(color, vec3(0.0), vec3(1.0));
}

// Empty atlas space darkens toward neutral charcoal when the atlas camera
// tilts down. The lower-screen gradient starts near 35 degrees and reaches
// charcoal everywhere at a straight-down view; terrain edge haze keeps using
// skyColor below so valid material pixels are unchanged.
vec3 atlasEmptyBackgroundColor(
    vec3 skyColor,
    vec3 relativeWorldPosition,
    vec3 cameraDirection,
    vec3 cameraUp,
    float skyScale
)
{
    vec3 worldUp = vec3(0.0, 1.0, 0.0);
    float downwardAngle = asin(clamp(
        -dot(normalize(cameraDirection), worldUp),
        0.0,
        1.0
    ));
    float pitchOnset = smoothstep(
        radians(30.0),
        radians(35.0),
        downwardAngle
    );
    float topDownProgress = smoothstep(
        radians(35.0),
        radians(90.0),
        downwardAngle
    );
    float verticalCoordinate = dot(relativeWorldPosition, cameraUp)
        / max(skyScale, 1.0);
    float lowerScreen = 1.0 - smoothstep(-0.30, 0.52, verticalCoordinate);
    float charcoalWeight = pitchOnset
        * mix(lowerScreen * 0.78, 1.0, topDownProgress);
    vec3 neutralCharcoal = vec3(0.0627451, 0.0745098, 0.0941176);
    return mix(skyColor, neutralCharcoal, clamp(charcoalWeight, 0.0, 1.0));
}

void main()
{
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    ivec2 dimensions = textureSize(sourceDepthTex, 0);
    if (any(lessThan(pixel, ivec2(0)))
        || any(greaterThanEqual(pixel, dimensions)))
    {
        outColor = vec4(backgroundColor.rgb, 1.0);
        outValidity = vec4(0.0);
        return;
    }

    float depth = texelFetch(sourceDepthTex, pixel, 0).r;
    vec3 nearPoint = unproject(inverseProjectionView, vec4(ndc, -1.0, 1.0));
    vec3 farPoint = unproject(inverseProjectionView, vec4(ndc, 1.0, 1.0));
    vec3 upPoint = unproject(
        inverseProjectionView,
        vec4(ndc + vec2(0.0, 1.0), -1.0, 1.0)
    );
    vec3 cameraDirection = normalize(farPoint - nearPoint);
    vec3 upDelta = upPoint - nearPoint;
    vec3 cameraUp = length(upDelta) > 0.0001
        ? normalize(upDelta)
        : vec3(0.0, 1.0, 0.0);
    vec3 worldPosition = nearPoint + worldOffset;
    vec3 atlasAnchor = vec3(
        disclosureCenterXZ.x,
        worldOffset.y,
        disclosureCenterXZ.y
    );
    vec3 relativeWorldPosition = worldPosition - atlasAnchor;
    vec3 skyColor = atlasSkyBackground(
        relativeWorldPosition,
        cameraDirection,
        cameraUp,
        disclosureRadius
    );
    vec3 emptyBackgroundColor = atlasEmptyBackgroundColor(
        skyColor,
        relativeWorldPosition,
        cameraDirection,
        cameraUp,
        disclosureRadius
    );
    if (depth >= 0.999999)
    {
        outColor = vec4(emptyBackgroundColor, 1.0);
        outValidity = vec4(0.0);
        return;
    }

    vec3 relativePosition = unproject(
        inverseProjectionView,
        vec4(ndc, depth * 2.0 - 1.0, 1.0)
    );
    vec3 absolutePosition = relativePosition + worldOffset;
    if (completeBoundaryEnabled > 0
        && (any(lessThan(absolutePosition.xz, completeBoundaryMinXZ))
            || any(greaterThanEqual(
                absolutePosition.xz,
                completeBoundaryMaxXZ
            ))))
    {
        outColor = vec4(emptyBackgroundColor, 1.0);
        outValidity = vec4(0.0);
        return;
    }
    vec2 disclosureDelta = absolutePosition.xz - disclosureCenterXZ;
    if (dot(disclosureDelta, disclosureDelta)
            >= disclosureRadius * disclosureRadius
        || !hasSurfaceCoverage(absolutePosition.xz))
    {
        outColor = vec4(emptyBackgroundColor, 1.0);
        outValidity = vec4(0.0);
        return;
    }

    // Fade only the last part of the already-authorized disclosure radius.
    // This soft atmospheric edge never changes validity, adds geometry or
    // makes any unloaded terrain visible.
    float edgeDistance = length(disclosureDelta);
    float edgeHaze = smoothstep(
        disclosureRadius * 0.62,
        disclosureRadius * 0.98,
        edgeDistance
    ) * 0.12;
    vec3 terrainColor = texelFetch(sourceColorTex, pixel, 0).rgb;
    outColor = vec4(mix(terrainColor, skyColor, edgeHaze), 1.0);
    outValidity = vec4(1.0, 0.0, 0.0, 1.0);
}
