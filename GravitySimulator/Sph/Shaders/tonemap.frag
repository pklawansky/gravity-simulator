#version 430 core
// Exponential tone map of the accumulated splats over a near-black background.
in vec2 vUv;
uniform sampler2D uHdr;
out vec4 fragColor;
void main()
{
    vec3 hdr = texture(uHdr, vUv).rgb;
    vec3 c = vec3(1.0) - exp(-hdr);
    c = pow(c, vec3(1.0 / 1.25));
    fragColor = vec4(vec3(0.01, 0.01, 0.03) + c, 1.0);
}
