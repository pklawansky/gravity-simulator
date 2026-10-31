# Gas dynamics (SPH) mode — design

Approved 2026-10-31. Third launcher mode, next to Classic and Planet formation. Builds on the
stellar model (`2026-10-31-stellar-model-design.md`).

## Decisions (human, verbatim)

- "Build the full sph hydrodynamics option and a different mode"
- Mode: "Third launcher mode (Recommended)"
- Scale: "GPU compute, ~200k+"

## Shape

`LauncherForm` gains a third button, **Gas dynamics (SPH)**, which opens `SphForm`. Planet formation
and Classic are unchanged. SPH code lives in `GravitySimulator/Sph/`; shaders are embedded
resources under `GravitySimulator/Sph/Shaders/`.

Requires OpenGL 4.3 core (compute shaders + SSBOs). If the context cannot be created, show a
clear message and close the window. The other modes keep working on GL 3.3.

## Per-step pipeline (GPU unless noted)

All particle state lives in SSBOs: position + smoothing length, velocity, acceleration, density,
pressure, internal energy u, du/dt, mass, flags.

0. **Smoothing length:** adaptive per particle, h_i = η (m_i/ρ_i)^(1/3) with η = 1.2, using the
   previous step's density, clamped to [h_min, h_max] per preset. Collapse needs this: a fixed h
   would either blur the dense cores or flood them with neighbours. Sink creation caps the peak
   density, which keeps the h range bounded.
1. **Neighbour grid** (cell size = 2·h_max of the current step): compute cell id per particle → counting sort (histogram,
   prefix sum, scatter) → cell start/end tables. This is the standard GPU particle-grid method
   (Green, "Particle Simulation using CUDA", 2010).
2. **Density:** ρ_i = Σ m_j W(r_ij, h), Wendland C2 kernel (stable against pairing).
3. **Equation of state:** each preset picks one of
   - ideal gas P = (γ-1)ρu, γ = 5/3, or
   - barotropic for star formation P = c_s²ρ [1 + (ρ/ρ_crit)^(4/3)]: isothermal at low density,
     stiffening at high density (Bate, Bonnell & Bromm 2003).
4. **Forces:** symmetric SPH pressure gradient + Monaghan (1992) artificial viscosity (α = 1,
   β = 2, with a Balsara switch to limit shear viscosity), plus du/dt from pdV work and viscous
   heating.
5. **Self-gravity:** Barnes-Hut. Each step positions are read back (≈3 MB at 200k). The CPU builds
   the octree with the existing builder extended to emit a flattened, stackless "next/skip" layout
   and uploads it, and a compute shader walks the tree per particle with softening ε = h. The tree
   build runs on the CPU while the GPU computes density for the next step.
6. **Time step:** global, from a GPU min-reduction of the CFL condition
   Δt = C · h / (c_s + |v| + 1.2(αc_s + β·max μ)), C = 0.3, plus an acceleration limit.
   Leapfrog kick-drift-kick, as in Planet formation.
7. **Sinks (CPU, every step on a small readback):** a particle whose density exceeds ρ_sink,
   whose flow is converging (∇·v < 0), and which is gravitationally bound becomes a sink. Sinks
   absorb bound gas within their accretion radius (Bate, Bonnell & Price 1995). Sinks are
   handed to the stellar model as stars, so they ignite, shine, and go supernova as in
   Planet formation. Supernova energy goes into the u of nearby gas, so gas reacts to it.
8. **Rendering:** reads the SSBOs directly with no readback: additive Gaussian splats, colour
   by temperature (u) or density (toggle), brightness by density; sinks/stars/black holes
   drawn on top using the Planet formation colour rules.

## Presets

- **Collapsing cloud:** turbulent sphere (random divergence-free velocity field), barotropic EOS;
  fragments into filaments and forms sinks/stars. Default 200k particles.
- **Colliding clouds:** two clouds meeting head-on, ideal gas; a shock layer forms and then
  fragments.
- **Star near a black hole (tidal disruption):** a polytropic gas star (ideal gas, relaxed) on a
  parabolic orbit past a black hole sink; it gets stretched into a stream and part of it is
  swallowed.

## UI

Preset, particle count (50k–500k), time speed, colour mode (temperature/density), EOS
parameters for the active preset (c_s, ρ_crit or γ), Start/Pause/Reset, Save screenshot; readout of
particle count, sinks/stars, steps/s, fps, Δt, total energy, GPU name.

## Testing

A CPU reference implementation of the same kernels in C# (small N) is the oracle for the GPU path.

- Kernel normalisation (∫W = 1) and gradient symmetry.
- Uniform lattice → density within 1% of ρ0 (CPU and GPU).
- Grid neighbour search finds exactly the brute-force neighbour set (GPU vs CPU).
- GPU density/acceleration match the CPU reference to float tolerance for a random 4k cloud.
- Sod shock tube (quasi-1D slab): density/pressure/velocity profiles within tolerance of the
  analytic solution at t = 0.2.
- Sound-wave propagation speed within 5% of c_s.
- Evrard collapse: total energy conserved within 2% through the bounce.
- Sink formation and accretion conserve mass and momentum.

GPU tests are tagged `[Trait("Category","GPU")]`. They create a hidden GL 4.3 context and skip
with a message if none is available. The CPU-reference tests always run.

## Out of scope

Iterative (Newton–Raphson) h–ρ solve (one lagged update per step instead), multi-level grids,
individual time steps, magnetic fields,
radiative transfer beyond the supernova energy injection, multi-GPU.
