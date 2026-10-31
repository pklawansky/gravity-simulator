// Shared by every SPH compute shader (prepended after the #version line).
// Mirrors SphKernel.cs / SphReference.cs; keep the two in step.

const float SIGMA = 0.41778174;          // 21 / (16 pi), Wendland C2 in 3D

uniform uint uCount;                      // particles in the arrays (live + dead in place)
uniform uint uTableMask;                  // hash table size - 1 (power of two)
uniform uint uTableSize;

float kernelW(float r, float h)
{
    float q = r / h;
    if (q >= 2.0) return 0.0;
    float t = 1.0 - 0.5 * q;
    float t2 = t * t;
    return SIGMA / (h * h * h) * t2 * t2 * (2.0 * q + 1.0);
}

// grad_i W(|x_ij|, h) for x_ij = x_i - x_j (|x_ij| = r).
vec3 gradW(vec3 xij, float r, float h)
{
    float q = r / h;
    if (q >= 2.0) return vec3(0.0);
    float t = 1.0 - 0.5 * q;
    float h2 = h * h;
    return xij * (-5.0 * SIGMA * t * t * t / (h2 * h2 * h));
}

// Radiative cooling over dt (mirrors SphReference.Cool): u relaxes to uFloor with t_cool = beta t_ff,
// t_ff = sqrt(3 pi / (32 rho)), integrated exactly (stable for any dt, never below the floor).
float coolU(float u, float rho, float dt, float beta, float uFloor)
{
    if (!(beta > 0.0) || !(u > uFloor) || !(dt > 0.0)) return u;
    float tCool = beta * sqrt(3.0 * 3.14159265 / (32.0 * max(rho, 1e-30)));
    return uFloor + (u - uFloor) * exp(-dt / tCool);
}

ivec3 cellOf(vec3 x, float cell)
{
    return ivec3(clamp(floor(x / cell), vec3(-1073741824.0), vec3(1073741823.0)));
}

uint hashCell(ivec3 c)
{
    return ((uint(c.x) * 73856093u) ^ (uint(c.y) * 19349663u) ^ (uint(c.z) * 83492791u)) & uTableMask;
}
