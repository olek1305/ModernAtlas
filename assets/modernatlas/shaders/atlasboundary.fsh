#version 330 core

in vec2 ndc;

layout(location = 0) out vec4 outColor;

uniform sampler2D sourceColorTex;
uniform sampler2D sourceDepthTex;
uniform sampler2D surfaceHeightTex;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
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

void main()
{
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    ivec2 dimensions = textureSize(sourceDepthTex, 0);
    if (any(lessThan(pixel, ivec2(0)))
        || any(greaterThanEqual(pixel, dimensions)))
    {
        outColor = vec4(backgroundColor.rgb, 1.0);
        return;
    }

    float depth = texelFetch(sourceDepthTex, pixel, 0).r;
    if (depth >= 0.999999)
    {
        outColor = vec4(backgroundColor.rgb, 1.0);
        return;
    }

    mat4 inverseMvp = inverse(projectionMatrix * modelViewMatrix);
    vec3 relativePosition = unproject(
        inverseMvp,
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
        outColor = vec4(backgroundColor.rgb, 1.0);
        return;
    }
    vec2 disclosureDelta = absolutePosition.xz - disclosureCenterXZ;
    if (dot(disclosureDelta, disclosureDelta)
            >= disclosureRadius * disclosureRadius
        || !hasSurfaceCoverage(absolutePosition.xz))
    {
        outColor = vec4(backgroundColor.rgb, 1.0);
        return;
    }

    outColor = vec4(texelFetch(sourceColorTex, pixel, 0).rgb, 1.0);
}
