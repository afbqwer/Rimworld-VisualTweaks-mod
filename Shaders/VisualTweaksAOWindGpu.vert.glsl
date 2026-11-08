#version 450

// GPU ribbon expansion for the particle style (experimental).
//
// The CPU no longer writes vertices. It packs three vec4 per particle (position +
// direction, trail length + size + segment count, baked bucket colour) and the
// vertex stage below rebuilds the whole ribbon from that plus the per-cell wind
// field. One mesh vertex = (particle, joint, side); the static mesh only provides
// gl_VertexIndex, so its vertex stream is never uploaded after startup.

layout(location = 0) in vec3 inPosition; // @vertex Position required

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Params;  // (altitude, halfWidth, tailWidthFactor, headWidthFactor)
    vec4 _Params2; // (mapWidth, mapHeight, activeCount, unused)
    vec4 _Color;   // (1, 1, 1, opacityScale)
} ub;

// Waylith builds the material descriptor set from the FRAGMENT source, so every
// buffer below is declared in the fragment shader as well even though it only
// reads _MainTex there.
layout(std430, set = 2, binding = 3) readonly buffer _AOParticles
{
    vec4 particleData[]; // 3 vec4 per particle
};

layout(std430, set = 2, binding = 4) readonly buffer _AOWindVX
{
    float windVX[];
};

layout(std430, set = 2, binding = 5) readonly buffer _AOWindVZ
{
    float windVZ[];
};

layout(std430, set = 2, binding = 6) readonly buffer _AOWindMask
{
    float windMask[];
};

// ArcDirectionBlend, ArcTrailSegments + 1 joints, ArcTailWidthFactor etc. mirror
// the constants in WindFlowRenderer so the two paths can be compared frame by frame.
const float DirectionBlend = 0.65;
const int MaxJoints = 9;
const float MinSampleSpeed = 0.05;

// Mask-weighted bilinear tap, identical to WindFlowRenderer.SampleField: blocked
// cells are down-weighted so a sample next to a wall does not average in the
// zeroed airflow of the wall itself.
vec2 SampleWind(vec2 p)
{
    float mapW = ub._Params2.x;
    float mapH = ub._Params2.y;

    if (p.x < 0.0 || p.y < 0.0 || p.x > mapW - 1.0 || p.y > mapH - 1.0)
    {
        return vec2(0.0);
    }

    int w = int(mapW);
    int x0 = int(clamp(floor(p.x), 0.0, mapW - 2.0));
    int z0 = int(clamp(floor(p.y), 0.0, mapH - 2.0));
    int x1 = x0 + 1;
    int z1 = z0 + 1;

    float fx = clamp(p.x - float(x0), 0.0, 1.0);
    float fz = clamp(p.y - float(z0), 0.0, 1.0);
    float fx0 = 1.0 - fx;
    float fz0 = 1.0 - fz;

    int i00 = z0 * w + x0;
    int i10 = z0 * w + x1;
    int i01 = z1 * w + x0;
    int i11 = z1 * w + x1;

    float w00 = fx0 * fz0 * windMask[i00];
    float w10 = fx * fz0 * windMask[i10];
    float w01 = fx0 * fz * windMask[i01];
    float w11 = fx * fz * windMask[i11];
    float denom = w00 + w10 + w01 + w11;

    if (denom < 1e-4)
    {
        return vec2(0.0);
    }

    float inv = 1.0 / denom;
    return vec2(
        (windVX[i00] * w00 + windVX[i10] * w10 + windVX[i01] * w01 + windVX[i11] * w11) * inv,
        (windVZ[i00] * w00 + windVZ[i10] * w10 + windVZ[i01] * w01 + windVZ[i11] * w11) * inv);
}

void main()
{
    int vertexIndex = gl_VertexIndex;
    int record = vertexIndex / (MaxJoints * 2);

    if (float(record) >= ub._Params2.z)
    {
        // Particle beyond the active count: collapse outside clip space. Its
        // triangles are clipped away before rasterisation.
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        vUv = vec2(0.0);
        vColor = vec4(0.0);
        return;
    }

    int jointVertex = vertexIndex % (MaxJoints * 2);
    int joint = jointVertex / 2;
    int side = jointVertex % 2;

    vec4 head = particleData[record * 3];
    vec4 trail = particleData[record * 3 + 1];
    vec4 tint = particleData[record * 3 + 2];

    float segments = max(1.0, trail.z);
    int segmentCount = int(segments);
    float stepLength = trail.x / segments;

    // Walk upstream from the head, re-reading the field at every joint and blending
    // towards it instead of snapping, exactly like EmitArcTrailSlice on the CPU.
    vec2 centers[MaxJoints];
    vec2 directions[MaxJoints];

    vec2 p = head.xy;
    vec2 dir = head.zw;
    centers[0] = p;

    for (int k = 1; k < MaxJoints; k++)
    {
        if (k > segmentCount)
        {
            break;
        }

        vec2 sampled = SampleWind(p);
        float speed = length(sampled);
        if (speed > MinSampleSpeed)
        {
            dir = normalize(dir * (1.0 - DirectionBlend) + (sampled / speed) * DirectionBlend);
        }

        p -= dir * stepLength;
        centers[k] = p;
    }

    for (int k = 0; k < MaxJoints - 1; k++)
    {
        if (k >= segmentCount)
        {
            break;
        }

        vec2 delta = centers[k + 1] - centers[k];
        float len = length(delta);
        directions[k] = (len > 1e-5) ? delta / len : vec2(0.0);
    }

    int j = min(joint, segmentCount);

    vec2 previousDirection = (j > 0) ? directions[j - 1] : vec2(0.0);
    vec2 nextDirection = (j < segmentCount) ? directions[j] : vec2(0.0);
    vec2 previousNormal = vec2(-previousDirection.y, previousDirection.x);
    vec2 nextNormal = vec2(-nextDirection.y, nextDirection.x);

    vec2 normal;
    if (j == 0)
    {
        normal = nextNormal;
    }
    else if (j == segmentCount)
    {
        normal = previousNormal;
    }
    else
    {
        // Mitre, with the same clamp as the CPU so a hairpin cannot grow a fin.
        normal = previousNormal + nextNormal;
        float miter = length(normal);
        if (miter < 1e-4)
        {
            normal = previousNormal;
        }
        else
        {
            normal /= miter;
            normal /= max(0.5, dot(normal, previousNormal));
        }
    }

    if (dot(normal, normal) < 1e-8)
    {
        normal = vec2(-head.w, head.z);
    }

    float u = 1.0 - float(j) / segments;
    float widthScale = mix(ub._Params.z, ub._Params.w, u) * trail.y;
    vec2 position = centers[j];

    if (joint <= segmentCount)
    {
        position += normal * (ub._Params.y * widthScale * ((side == 0) ? -1.0 : 1.0));
        vUv = vec2(u, float(side));
        vColor = tint * ub._Color;
    }
    else
    {
        // Spare joints collapse onto the tail so they rasterise nothing.
        vUv = vec2(0.0);
        vColor = vec4(0.0);
    }

    gl_Position = uVertex.ViewProj * vec4(position.x, ub._Params.x, position.y, 1.0);
}
