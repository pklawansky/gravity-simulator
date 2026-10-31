#version 430 core
// Gas particles straight from the solver's SSBOs (bound as vertex attributes, no read-back).
layout(location = 0) in vec4 aPosH;     // xyz, h
layout(location = 1) in vec4 aRhoP;     // rho, P, c_s, Balsara
layout(location = 2) in vec4 aMassId;   // mass, id bits
uniform mat4 uView;
uniform mat4 uProj;
uniform float uPointScale;    // viewport height * proj[1][1]
uniform int uColorMode;       // 0 temperature (P/rho), 1 density
uniform float uLogTMin, uLogTMax, uLogRhoMin, uLogRhoMax;
uniform float uRhoRef;
out vec3 vColor;
out float vWeight;

vec3 heat(float t)             // black-body-like: deep red, orange, yellow, white, blue-white
{
    vec3 c0 = vec3(0.35, 0.02, 0.05), c1 = vec3(0.95, 0.25, 0.05), c2 = vec3(1.0, 0.75, 0.25);
    vec3 c3 = vec3(1.0, 1.0, 0.9), c4 = vec3(0.6, 0.75, 1.0);
    t = clamp(t, 0.0, 1.0) * 4.0;
    if (t < 1.0) return mix(c0, c1, t);
    if (t < 2.0) return mix(c1, c2, t - 1.0);
    if (t < 3.0) return mix(c2, c3, t - 2.0);
    return mix(c3, c4, t - 3.0);
}

vec3 cool(float t)             // density: indigo, teal, pale cyan, white
{
    vec3 c0 = vec3(0.10, 0.08, 0.35), c1 = vec3(0.10, 0.45, 0.65), c2 = vec3(0.55, 0.90, 0.95), c3 = vec3(1.0);
    t = clamp(t, 0.0, 1.0) * 3.0;
    if (t < 1.0) return mix(c0, c1, t);
    if (t < 2.0) return mix(c1, c2, t - 1.0);
    return mix(c2, c3, t - 2.0);
}

void main()
{
    float m = aMassId.x;
    vec4 vp = uView * vec4(aPosH.xyz, 1.0);
    gl_Position = uProj * vp;
    float depth = max(-vp.z, 1e-6);
    float px = 2.0 * aPosH.w * uPointScale / depth;
    float size = clamp(px, 1.5, 64.0);
    gl_PointSize = m > 0.0 ? size : 0.0;
    float rho = max(aRhoP.x, 1e-30);
    if (uColorMode == 0)
    {
        float logT = log(max(aRhoP.y / rho, 1e-30)) / log(10.0);
        vColor = heat((logT - uLogTMin) / max(uLogTMax - uLogTMin, 1e-6));
    }
    else
    {
        float logRho = log(rho) / log(10.0);
        vColor = cool((logRho - uLogRhoMin) / max(uLogRhoMax - uLogRhoMin, 1e-6));
    }
    // Mass spread over the splat's area (a column density), brighter where the gas is dense.
    vWeight = m > 0.0 ? m / (size * size) * pow(rho / uRhoRef, 0.35) : 0.0;
}
