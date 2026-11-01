# Stellar model and cosmic-scale presets — design

Approved 2026-10-31. Extends planet formation mode (`2026-10-30-planet-formation-design.md`).

## Decisions (human, verbatim)

- Physics: "Start with sub grid stellar model, with an option to use full sph hydrodynamics in the future"
- Presets: "New cosmic-scale presets (Recommended)" — keep disc and cloud at planet scale; add
  "Star-forming cloud" and "Dense star cluster", each with its own units.
- Earlier: "get it clumping nicely, set presets where you need them" (done for the disc: disc-mass
  slider, default 10% of the star).

## Why the existing merge model cannot make a black hole

Merges keep density constant (radius from summed volume), so no body ever gets denser. Real
objects are set by hydrostatic equilibrium — gravity against gas, radiation, or degeneracy
pressure — and that is what the sub-grid model approximates.

## Model (sub-grid)

Each body carries a **class** and, for stars, an **age**. Class decides radius, luminosity,
collision behaviour, and colour. Thresholds in solar masses (M☉):

| Class | Rule | Radius |
|---|---|---|
| Gas | cosmic presets' initial particles; clouds of gas | fixed per preset (large, low density) |
| Planet / brown dwarf | non-degenerate, M < 0.08 | constant density from volume (as today) |
| Star | non-degenerate, M ≥ 0.08 (fusion ignites) | R = R☉ · M^0.8 |
| White dwarf | dead star remnant, M ≤ 1.4 (Chandrasekhar) | R ∝ M^(-1/3), ~0.01 R☉ at 0.6 M☉ |
| Neutron star | remnant 1.4 < M ≤ 2.5 (TOV), or WD pushed over 1.4 | ~12 km |
| Black hole | remnant over 2.5, star ≥ 20 M☉ at end of life, or NS pushed over 2.5 | event horizon r_s = 2GM/c² |

- **Lifetime:** main-sequence lifetime t = 10 Gyr · M^-2.5, divided by a user "stellar time
  acceleration" so stars evolve within minutes of viewing.
- **End of life:** M < 8 → white dwarf (keeps min(M, 0.6 + 0.1(M-1)) capped at 1.4; the rest leaves
  as a shell of gas particles); 8 ≤ M < 20 → supernova → neutron star 1.4 M☉; M ≥ 20 → black
  hole keeping ~40% of the mass. Supernovae emit ejecta as gas particles moving outward and give
  nearby bodies an outward impulse.
- **Radiation pressure (fusion pushing outward):** luminosity L = L☉ · M^3.5 for stars. Each
  star pushes gas/dust/planets radially with acceleration L·πR²/(4πr²c·m). Applied from the
  brightest ≤ 64 stars, O(N·S).
- **Sink particles (accretion):** at cosmic scale, event horizons and stellar radii are far below
  a particle's size, so stars and compact objects accrete as sink particles (Bate, Bonnell &
  Price 1995): gas within an accretion radius that is gravitationally bound to the sink is absorbed
  (mass and momentum conserved). Black holes always absorb on contact, never bounce. The rendered
  black hole shows the event horizon at a minimum on-screen size plus a glow ring.
- **Paczyński–Wiita potential:** not used at these scales (r_s is many orders of magnitude below
  the accretion radius); noted as the upgrade path if a close-in black-hole preset is added later.

## Units

`SimulationParameters` gains a unit system: mass unit (M☉), length unit (pc or AU), time unit
derived so G = 1. The speed of light, R☉, L☉, and lifetimes are converted into sim units.
The planet-scale presets run with the stellar model **off** and behave exactly as before.

## Presets

- **Star-forming cloud:** turbulent sphere of gas particles (total ~1000–5000 M☉, ~1 pc).
  Collapses into clumps; clumps over 0.08 M☉ ignite; massive stars light up, push gas
  away, explode, and leave neutron stars and black holes.
- **Dense star cluster:** Plummer sphere of stars with masses drawn from the Kroupa initial mass
  function. Massive stars die first into black holes, which sink to the centre and merge.

## Extension seam for future SPH

Two interfaces keep physics pluggable:

- `IForceContributor` — adds accelerations after gravity each step (radiation pressure is the
  first implementation; an SPH pressure force would be another).
- `IBodyEvolution` — per-step class/age/radius updates and events (stellar model is the first
  implementation).

No SPH code is written now.

## UI

- Preset list gains the two cosmic presets.
- Stellar time acceleration slider, shown for cosmic presets.
- Readout: counts per class; supernovae and black holes formed so far.
- Colours: stars by mass (red → yellow → blue-white), white dwarfs small white, neutron stars
  small cyan, black holes black disc with an orange ring, supernova flash.

## Testing

- Classification at each threshold (0.08, 1.4, 2.5, 8, 20 M☉).
- Lifetime scaling and end states for 1, 10, and 30 M☉ stars.
- White dwarf over 1.4 → neutron star; neutron star over 2.5 → black hole.
- Radiation pressure points away from the star and scales with 1/r² and L.
- Sink accretion and supernova ejecta conserve mass and momentum (ejecta + remnant = progenitor).
- Black hole never bounces.
- Unit conversions round-trip; cosmic presets deterministic per seed; planet presets
  unchanged with the stellar model off (existing tests still pass).
