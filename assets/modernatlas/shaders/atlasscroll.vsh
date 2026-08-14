#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;

uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform int materialKind;

out vec2 uv;
out vec3 viewNormal;
out vec3 objectPosition;

void main(void)
{
    vec3 objectNormal;
    if (materialKind == 0 || materialKind == 7)
    {
        // The sheet is gently bowed toward the viewer at both rollers.
        objectNormal = normalize(vec3(-vertexPositionIn.x * 0.32, 0.0, 1.0));
    }
    else if (materialKind < 4)
    {
        // Every rolled-paper, wood and metal part is a closed Y-axis cylinder.
        // Cap vertices sit just beyond the side vertices so their normal can
        // be reconstructed without an extra mesh attribute.
        objectNormal = abs(vertexPositionIn.y) > 0.495
            ? vec3(0.0, sign(vertexPositionIn.y), 0.0)
            : normalize(vec3(vertexPositionIn.x, 0.0, vertexPositionIn.z));
    }
    else if (materialKind >= 9 && materialKind <= 14)
    {
        objectNormal = vec3(0.0, 0.0, 1.0);
    }
    else
    {
        vec3 absolutePosition = abs(vertexPositionIn);
        objectNormal = absolutePosition.x > absolutePosition.y
            && absolutePosition.x > absolutePosition.z
            ? vec3(sign(vertexPositionIn.x), 0.0, 0.0)
            : absolutePosition.y > absolutePosition.z
                ? vec3(0.0, sign(vertexPositionIn.y), 0.0)
                : vec3(0.0, 0.0, sign(vertexPositionIn.z));
    }

    uv = uvIn;
    objectPosition = vertexPositionIn;
    viewNormal = normalize(mat3(modelViewMatrix) * objectNormal);
    gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPositionIn, 1.0);
}
