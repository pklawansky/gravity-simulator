# Planet formation mode — design

Approved 2026-10-30. A second simulator mode: tens of thousands of tiny particles under mutual
gravity that collide and accrete into planets, rendered with OpenGL.

## Decisions (human, verbatim choices)

- Scale: "~50k–100k" particles, smooth.
- Merging: "Merge if slow, bounce if fast".
- Start state: "Selectable presets".
- UI: "Choose at startup".
- Branch: based off main after the .NET 10 port was merged.

## Architecture

New code lives in `GravitySimulator/Planets/`. The classic mode (`Form1`, `Engines/`, `Models/`)
is not modified.

| Unit | Responsibility |
|---|---|
| `ParticleSystem` | Struct-of-arrays state: `Vector3` position/velocity/acceleration, `float` mass/radius, `Count`; add, remove (swap-with-last), compaction. |
| `BarnesHutTree` | Octree rebuilt each step; node mass + centre of mass; acceleration query with opening angle θ (default 0.6) and Plummer softening ε. |
| `CollisionResolver` | Uniform spatial hash (cell ≈ 2 × max radius of non-star bodies; oversize bodies checked separately). For each touching pair: relative speed `v` vs mutual escape speed `v_esc = sqrt(2G(m1+m2)/(r1+r2))`. `v < k·v_esc` → merge (mass, momentum conserved; radius from summed volume; position at centre of mass). Otherwise → bounce along the contact normal with restitution `e`, and positions separated so they no longer overlap. |
| `PlanetSimulation` | Owns the state and parameters; leapfrog kick-drift-kick step with `Parallel.For` over particles for forces; runs on a background thread; after each step publishes an immutable snapshot (positions, radii, masses, count, stats) for the renderer; drops particles beyond the escape radius. Start / pause / reset. |
| `Presets` | `DiscAroundStar(count, seed)`: central star (≈ 1000× total dust mass), dust in a thin disc with near-circular Keplerian velocities plus small dispersion. `SpinningCloud(count, seed)`: uniform sphere of dust with solid-body rotation below virial. Deterministic for a seed. |
| `PlanetRenderer` | OpenGL 3.3 core: one VBO streamed from the latest snapshot; point sprites sized by radius with perspective; colour by mass (dust dim → rock warm → planet bright); star emissive. |
| `OrbitCamera` | Yaw/pitch/distance; mouse drag rotates, wheel zooms; produces view/projection matrices. |
| `PlanetForm` | WinForms form hosting `GLControl` plus a side panel: preset, particle count (10k–100k), time speed, merge threshold k, restitution e, Start/Pause/Reset; readout of count, largest bodies, steps/s, fps. |
| `LauncherForm` | Startup chooser: "Classic simulator" (opens `Form1` unchanged) or "Planet formation" (opens `PlanetForm`). `Program.cs` runs the launcher. |

Units are normalised (G = 1) and independent of the classic engine's SI-ish units.

## Dependencies

- `OpenTK` 4.x and `OpenTK.GLControl` 4.x (MIT). New NuGet dependencies.

## Testing

New xUnit project `GravitySimulator.Tests` covering the physics core (no GL):

- Barnes-Hut acceleration within a stated relative error of direct all-pairs for a random cloud.
- θ = 0 matches direct summation to floating-point tolerance.
- Merge conserves total mass and momentum; merged radius follows summed volume.
- Below the threshold → merge; above → bounce; a bounce conserves momentum and scales normal relative speed by e.
- A two-body circular orbit conserves energy within tolerance over 10k leapfrog steps.
- Presets produce the requested count, are deterministic per seed, and the disc has net angular momentum along +Z.
- Particles beyond the escape radius are removed.

Rendering and UI are verified by launching the app.

## Out of scope

GPU compute, fragmentation, gas/drag, saving and loading runs, changes to the classic mode.
