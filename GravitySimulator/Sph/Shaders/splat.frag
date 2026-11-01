#version 430 core
// Additive Gaussian splat into the floating-point accumulation buffer.
in vec3 vColor;
in float vWeight;
uniform float uExposure;
out vec4 fragColor;
void main()
{
    vec2 d = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(d, d);
    if (r2 > 1.0) discard;
    float g = exp(-4.0 * r2) * 1.62;   // ~unit integral over the disc
    fragColor = vec4(vColor * (vWeight * uExposure * g), 1.0);
}
