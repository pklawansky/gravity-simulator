# Collision outcomes (Planet formation) — design

Approved 2026-10-31. Replaces merge-or-bounce as the default collision rule for rocky bodies in
Planet formation (`2026-10-30-planet-formation-design.md`). Merge-or-bounce stays selectable.

## Decisions (human, verbatim)

- "Collision outcomes first"
- Fragments: "Real particles, capped (Recommended)"
- Old model: "Keep as a selectable option (Recommended)"
- Design: "Approve, build it (Recommended)"

## Scope

Applies only to pairs where **both** bodies are `BodyClass.Planet`, i.e. rocky bodies (all bodies in the
planet-scale presets). Any pair involving gas, a star, a remnant or a black hole keeps today's rules
unchanged, including "black hole always absorbs". Planet-scale presets have G = 1 and
no physical units. A grain is roughly Mars-to-Earth mass at 50k–100k particles (disc mass 10% of
the star), so only the **gravity regime** is modelled. There is no material-strength term.

## Model (Leinhardt & Stewart 2012, "LS12"; Stewart & Leinhardt 2012)

Target t = heavier body, projectile p = lighter. Per contact:

- M_tot = M_t + M_p, γ = M_p/M_t, reduced mass μ = M_t M_p / M_tot.
- Impact speed V_i = |v_p − v_t| at contact.
- Mutual escape speed V_esc = sqrt(2 M_tot / (R_t + R_p)).
- Impact parameter b = sin θ = |d × v̂_rel| / (R_t + R_p), where d is the separation at contact.
- Grazing when b > b_crit = R_t / (R_t + R_p).
- Specific impact energy Q_R = μ V_i² / (2 M_tot).
- Catastrophic disruption threshold, gravity regime, G = 1:
  Q*_RD = c* · (4/5) π ρ̄ R_C1², with c* = 1.9.
  - R_C1 is the radius of a sphere of mass M_tot at the bodies' mean density ρ̄.
  - Corrected for mass ratio: Q'*_RD = Q*_RD · [(γ+1)²/(4γ)]^(2/(3μ̄) − 1), with μ̄ = 0.36.
  - Angle correction: the interacting-mass correction from LS12 §3.
- Largest remnant: M_lr / M_tot = 1 − Q_R / (2 Q'*_RD), the universal law, for
  Q_R < 1.8 Q'*_RD. Above that is the super-catastrophic regime:
  M_lr / M_tot = (0.1 / 1.8^η) (Q_R / Q'*_RD)^η, with η = −1.5.

Outcome selection, in order:

| Outcome | Condition | Result |
|---|---|---|
| Perfect merge | V_i < V_esc | one body: mass, momentum and centre of mass conserved; radius from summed volume (as today) |
| Hit-and-run | grazing, V_esc ≤ V_i, and Q_R below the erosion threshold of the target | the target keeps its mass. The projectile keeps M_lr,p from the universal law applied to the projectile, with the remainder as fragments. Both keep their tangential velocity; the normal component is exchanged as in a perfectly inelastic hit. They are separated so they no longer overlap. |
| Partial accretion | not grazing, V_i ≥ V_esc, M_lr ≥ M_t | the largest remnant (M_lr, at the centre of mass) plus fragments |
| Erosion | M_lr < M_t, M_lr ≥ 0.1 M_tot | the largest remnant plus fragments |
| Catastrophic disruption | M_lr < 0.1 M_tot | the super-catastrophic law; fragments carry the rest |

## Fragments

- **Count:** at most 8 per event. Each fragment has mass ≥ the starting grain mass m₀, which is
  recorded at Load. If the debris mass is below m₀, it merges into the largest remnant
  instead. Fragment masses follow a simple power-law split (largest fragment first).
- **Placement:** fragments sit just outside the remnant's surface, so they don't immediately
  re-collide this step.
- **Velocity:** the centre-of-mass velocity plus 1.05–1.3 × V_esc of the remnant, inside a 45° cone around
  the impact direction, which is the projectile's incoming velocity in the centre-of-mass frame. The fragments' total momentum relative to the centre of mass is
  zeroed, so total mass, momentum and centre of mass are conserved exactly.
- **Global ceiling:** the live particle count never exceeds the count at Load. When an event
  would exceed it, the fragment count is reduced and the excess mass goes into the largest fragment, or into
  the remnant if no fragment remains.
- **Bookkeeping:** fragments are appended after the current step's contact sweep, so a fragment never collides in the
  step it is born.

## Integration

- **`CollisionModel` enum:** `Outcomes` (default) and `MergeOrBounce`, in `SimulationParameters`.
  `CollisionResolver` dispatches on it only for Planet–Planet pairs. In `MergeOrBounce` mode the code path is exactly today's, and the existing tests
  keep passing untouched.
- **Code layout:** the outcome maths lives in a pure static class `CollisionOutcomes` (inputs: masses, radii,
  relative position/velocity → outcome + remnant/fragment specs), so it can be tested without a
  ParticleSystem. `CollisionResolver` applies the result.
- **`ParticleSystem.Add`:** used for fragments. Capacity grows only up to the ceiling.
- **Stats:** counts per outcome per step and cumulative, with names matching the table above.

## UI

- **Collision model dropdown:** "Outcomes (Leinhardt & Stewart)" or "Merge or bounce".
- **Controls that show only for merge-or-bounce:** the merge threshold k and Restitution e.
- **Readout:** cumulative merges, partial accretions, hit-and-runs, erosions, disruptions, fragments created.

## Testing

- Each regime is triggered by a constructed two-body contact and asserted.
- Every regime conserves mass, momentum and centre of mass to float tolerance.
- The universal law reproduces M_lr/M_tot at sample Q_R/Q'*_RD values.
- Hit-and-run occurs only when b > b_crit; the same speed head-on gives accretion or erosion.
- No fragment is lighter than m₀; at most 8 per event; the global ceiling holds under a
  forced shower of disruptions.
- Fragments don't collide in the step they are born.
- MergeOrBounce mode gives results identical to before on a fixed seed.
- Stars, gas and black holes are unaffected (the stellar-model tests still pass).
- A disc run under the Outcomes model still forms clumps (the 10% disc gives at least 50 clumps of 10+ grains in
  1,500 steps). Report the count compared with merge-or-bounce.

## Out of scope

Material strength regime, composition and atmospheres (next design), fragment size
distributions below m₀, and changes to the SPH mode or to the cosmic presets' stellar bodies.

## Amendment 2026-10-31: more fragments, overflow kept separate

Decision (human, verbatim): "I am happy for the overflow fragments to remain separate - looking for
something closer to reality while also looking good. too few fragments makes it look less dramatic.
let's set an upper limit to where we think the simulation will start to become unaffordable"

This amendment replaces the Fragments rules above where they differ.

### Measured cost (Release, 32 cores, RTX 4070 Laptop)

`AffordabilityBenchmarks` (Category=Perf). Debris cloud: a 50k 10% disc after 150 steps (clumps, fragments,
about 15k live) topped up to N with debris of m0/4 to m0 on near-circular orbits, σ_z = 0.05. Fresh disc: the
UI-default 10% disc loaded at N, timed on steps 4–13. That disc merges fast, so its live count is far below N.
Render: upload plus draw into a 1030×800 offscreen framebuffer (the app's GL view), with glFinish.

| N (debris cloud) | live during timing | step | steps/s | render | fresh disc at N: live, steps/s |
|---|---|---|---|---|---|
| 50,000 | 52.6k → 57.5k | 85 ms | 11.8 | 0.6 ms | 42.9k, 21.5 |
| 100,000 | 103k → 110k | 212 ms | 4.7 | 1.8 ms | 73.3k, 8.9 |
| 150,000 | 151k → 159k | 370 ms | 2.7 | 2.5 ms | 94.4k, 6.5 |
| 200,000 | 198k → 205k | 603 ms | 1.7 | 3.0 ms | 108k, 4.8 |
| 300,000 | 285k → 291k | 1,081 ms | 0.9 | 4.2 ms | 119k, 3.2 |
| 400,000 | 367k → 368k | 1,743 ms | 0.6 | 6.0 ms | 119k, 2.7 |

At about 110k live, a debris-cloud step is roughly 90 ms of gravity and 135 ms of collision sweep. Rendering is
never the limit (≥ 166 fps even at 400k), so the step rate decides.

### Rules

- **Global ceiling:** `SimulationParameters.MaxLiveParticles` = **100,000**, the largest clean count that still
  sustains about 5 steps/s. The effective ceiling is max(MaxLiveParticles, count at Load), so a larger load still
  runs. Below the ceiling, every fragment stays a separate particle. Only at the ceiling is the excess folded into
  the largest fragment, or into the remnant if no fragment fits. Folded fragments are counted
  (`FragmentsFoldedAtCeiling`, shown as "Folded" in the readout). Particle capacity grows geometrically but never
  past the ceiling.
- **Count per event:** n = min(**64**, floor(debris / m_min)). The split stays LS12's power law, largest first:
  m_k ∝ k^(−3/β), β = 2.85. That is the rank-ordered form of the eq. 31 cumulative distribution N(>D) ∝ D^−β:
  the k-th largest body has N(>m_k) = k. It is trimmed so no fragment is below m_min. The count grows with debris
  mass: 4 fragments for 10 m_min of debris, 23 for 100 m_min, and 64 from about 345 m_min. Cost of the
  cap: a forced shower of 1,000 head-on disruptions, each emitting the full 64 fragments, resolves in 37 ms
  (about 37 µs per event). The cap is therefore not the expensive part. The live count it creates is, and the
  ceiling bounds that.
- **Minimum fragment mass:** m_min = **m0/4**. A fragment of m0/4 has 0.63 of a grain's radius, so it still
  draws as a visible point. A hit with only two grains' worth of debris now throws 3 pieces instead of 1. Going
  lower (m0/8) would add bodies faster than it adds anything visible, and it would sit further below the disc's
  own resolution (one grain).
- **Velocity:** relative to the remnant, u_k = V_esc,rem × min(2, 1.05 (m_k/m_1)^(−1/6) + 0.2 ξ), with ξ uniform
  in [0, 1). The largest fragment leaves just above escape, and smaller fragments leave faster, up to 2 V_esc.
  The weak v ∝ m^(−1/6) mass dependence follows laboratory disruption experiments (Nakamura & Fujiwara 1991,
  Icarus 92, 132). Directions and the momentum balancing are as before: a 45° double cone, then the set is
  shifted so total mass, momentum and centre of mass are conserved exactly.
- **Placement:** each fragment starts just outside the remnant and is pushed outward along its own direction
  until it clears every fragment already placed in this event, and the hit-and-run target. This replaces the
  per-side radial stacking, which grew too long at 64 fragments. Fragments still never collide in the step they
  are born.

### Clumping check (10% disc, 50k, seed 1, 1,500 steps)

| | before (8 fragments, m0, ceiling = load) | after |
|---|---|---|
| clumps of 10+ grains | 112 | 131 |
| bodies left | 636 | 2,076 |
| fragments created | 1,747 | 8,988 |
| peak live count | 50,000 | 50,000 (the load; never exceeded) |
| folded at ceiling | n/a | 0 |

## Amendment 2026-10-31: faster collision sweep, ceiling raised to 150,000

Decision (human, verbatim): "Yes, speed it up"

This amendment replaces the ceiling in the amendment above.

### What changed

The collision sweep used one uniform grid. Bodies larger than 4× the mean radius were checked against every body,
but only the first 64 of them. Past 64 large clumps, the grid cell became twice the largest remaining radius, so
cells were huge. At 110k live it made 16.6 million distance tests to find about 400 contacts, on one thread.

It is now a hierarchical spatial hash. Bodies are binned by size into levels whose cell size doubles. The smallest
cell is just over twice the smallest radius, and each body sits in the lowest level whose cell is at least its
diameter. Each body checks its own level (its cell and the 13 forward neighbours), and the cells within reach in
each coarser level. A level with 16 bodies or fewer is checked as a plain list. The work per body no longer depends
on how large the largest bodies are, and the 64-body cliff is gone.

Pairs are found in parallel at the start of the sweep. They are sorted by (lower index, higher index), duplicates
are removed, and then they are resolved one at a time in that order. Each pair is re-tested against the current
state first. Resolution stays sequential because it changes bodies and draws random numbers, so both collision
models stay deterministic per seed.

Other options considered:

- **Sort-and-sweep:** rejected, measured. On the 100k debris-cloud state, the best axis (x or y) yields 25.7M
  candidate pairs, as many as the old grid's 25.5M tests (z yields 675M). The hierarchical hash makes 1.0M tests on
  the same state.
- **Querying the Barnes-Hut octree:** rejected on analysis, not measured. Its nodes carry no radius bounds, and
  walking the tree means pointer-chasing per body, which should be slower than a hash lookup.

`CollisionBroadPhaseTests` checks the new broad phase against an O(n²) search. On random mixed-size states (more
than 64 large bodies, a star-sized body, dead bodies) it finds exactly the same contact set.

**Behaviour change.** The old sweep found and resolved pairs in one interleaved pass. So a contact created
mid-sweep, such as a merged body grown into a neighbour, could be resolved in the same step. Now that contact waits
one step. The merge and bounce rules are unchanged. The MergeOrBounce golden hashes were re-pinned in their own
commit:

- disc: 1,450 → 1,451 bodies left, 2,550 → 2,549 merges;
- spinning cloud: 153 → 182 bodies left, 2,847 → 2,818 merges, 3,809 → 3,894 bounces.

### Step split (debris cloud, Release, 32 cores)

Gravity is the tree build plus forces. Collisions are one sweep on a copy of the timed state.

| N | step before → after | gravity before → after | collisions before → after | snapshot |
|---|---|---|---|---|
| 100,000 | 220 → 132 ms | 92 → 116 ms | 127 → 19 ms | 0.3 ms |
| 200,000 | 572 → 310 ms | 239 → 273 ms | 413 → 37 ms | 0.5–1 ms |

Before, at 100k, the collision time broke down as 2 ms grid build, 75 ms scanning, 20 ms on the 64 oversize bodies,
and run-to-run variance; compaction was under 0.1 ms. The old sweep tested and resolved in the same loop, so its
broad and narrow phases cannot be separated.

After, at 100k, the 19 ms is 18.4 ms finding pairs (1.2M tests, in parallel), 0.5 ms resolving and 0.1 ms
compacting. At 200k it is 35.5 + 1.5 + 0.3 ms.

Gravity varies about ±20% between runs. **Gravity is now the bottleneck**: 88% of the step at 100k and 200k.

### Measured cost after

Same method and states as the table above.

| N (debris cloud) | live during timing | step | steps/s | render | fresh disc at N: live, steps/s |
|---|---|---|---|---|---|
| 50,000 | 53.6k → 61.6k | 53 ms | 18.9 | 0.8 ms | 41.7k, 29.9 |
| 100,000 | 106k → 118k | 132 ms | 7.6 | 1.7 ms | 70.4k, 12.0 |
| 150,000 | 156k → 169k | 227 ms | 4.4 | 2.2 ms | 89.8k, 10.5 |
| 200,000 | 205k → 222k | 310 ms | 3.2 | 2.9 ms | 102k, 8.3 |
| 300,000 | 299k → 316k | 496 ms | 2.0 | 4.1 ms | 113k, 7.6 |
| 400,000 | 386k → 398k | 719 ms | 1.4 | 5.3 ms | 115k, 7.3 |

### New ceiling

`SimulationParameters.MaxLiveParticles` = **150,000**. The Particles box maximum now follows the constant.

Held at that ceiling, the debris cloud ran at **4.9 steps/s**: 205 ms per step over 30 steps, 144k → 150k live,
with gravity 147 ms and collisions 19 ms. That is the same "about 5 steps/s" bar that set 100,000 before, which ran
at 4.7 steps/s. The table row for 150,000 shows 4.4 steps/s because there the cloud grew past the load to 169k with
no ceiling.

### Clumping check after (10% disc, 50k, 1,500 steps, Outcomes)

The numbers do not match exactly, because the resolution order changed. Seed 1 now gives 121 clumps of 10+ grains
(was 131), 1,788 bodies (was 2,076) and 9,484 fragments (was 8,988). Peak live stays at the 50,000 load, and none
were folded.

Across seeds 1–4, the differences are mostly within the seed-to-seed spread:

| | old code | new code |
|---|---|---|
| clumps | 122–143 (mean 131) | 118–132 (mean 123) |
| bodies | 1,960–2,373 | 1,788–3,170 |
| fragments | 8,988–10,580 | 9,387–15,029 |

Clump counts run about 6% lower, consistent with chained merges now taking an extra step each. The spec's bar of at
least 50 clumps still holds.

## Amendment 2026-10-31: GPU gravity, ceiling raised to 350,000

Request (task brief, paraphrasing the human): move Planet formation's gravity onto the GPU, then re-measure and
put the ceiling where the simulation starts to become unaffordable (about 5 steps/s held), with fragments still
numerous.

This amendment replaces the ceiling in the amendment above.

### What changed

- **GPU gravity** (`Planets/GpuGravity.cs`, `Sph/Shaders/planet_gravity.comp`). The CPU still builds the octree.
  It is flattened into the stackless layout the SPH mode already uses (`BarnesHutTree.Flatten`, now also returning
  the bodies in walk order) and uploaded with `GpuFlatTree`, which is now shared with the SPH solver. One GPU thread
  per body walks it with the CPU tree's rules: the same θ = 0.6 test, the same "point inside the cube" rule, one
  Plummer ε² for every pair and node, and itself skipped. Force contributors (radiation pressure) still run on
  the CPU, before the kick.
- **Threading.** The GL work runs on a private thread that owns a hidden GL 4.3 context, so the simulation thread,
  the UI thread (on Load) and tests can all call it. Calls are serialised.
- **Fallback.** `PlanetSimulation` stays on the CPU tree by default, so the golden-hash tests are unchanged. The
  app calls `TryEnableGpuGravity` on load. If the context or the shader fails, or a GL call fails later, it drops
  to the CPU tree with no dialog. The readout says "Gravity GPU" or "Gravity CPU (reason)".
- **Tree order.** On the GPU path only, the bodies are re-sorted into the tree's walk order after every kick
  (`ParticleSystem.Reorder`). The next tree build and flatten then walk memory in order. At 400k this cut them
  from 150 ms to 60 ms.

### Accuracy (`PlanetGpuGravityTests`, θ = 0.6)

Relative error of |a| per body against the direct O(n²) sum:

| state | CPU tree: mean / p99 / max | GPU: mean / p99 / max | GPU vs CPU tree: max |
|---|---|---|---|
| uniform cloud, 3,000, ε = 0.01 | 0.96% / 3.3% / 10.4% | identical | 3.1e-6 |
| 10% disc, 4,000, ε = 0.005 | 0.18% / 0.91% / 3.8% | identical | 1.7e-6 |

The GPU walk is the CPU walk to float rounding. The two only add the terms in a different order. The tests
require a mean below 1.5% and a p99 below 5% (the CPU tree's own bound), and at most 1e-4 between GPU and CPU.

### Step split (debris cloud, Release, 32 cores, RTX 4070 Laptop)

Method as above, now with the simulation's own per-phase timers (`PlanetSimulation.LastPhaseTimes`, averaged over
10 steps). "Gravity" is flatten, upload, walk, read-back, kick and reorder. The tree build is listed on its own.

| N | live | CPU step | GPU step | GPU: tree build / gravity / collisions |
|---|---|---|---|---|
| 50,000 | 54k → 62k | 61 ms (16.4/s) | 24 ms (42.5/s) | 6.6 / 11.0 / 5.4 ms |
| 100,000 | 106k → 118k | 149 ms (6.7/s) | 43 ms (23.5/s) | 13.0 / 19.1 / 9.5 ms |
| 150,000 | 156k → 170k | 238 ms (4.2/s) | 62 ms (16.1/s) | 19.3 / 27.1 / 14.4 ms |
| 200,000 | 205k → 222k | 353 ms (2.8/s) | 85 ms (11.7/s) | 25.7 / 38.5 / 19.2 ms |
| 300,000 | 298k → 315k | 544 ms (1.8/s) | 125 ms (8.0/s) | 36.9 / 54.3 / 30.9 ms |
| 400,000 | 385k → 398k | 805 ms (1.2/s) | 160 ms (6.2/s) | 47.4 / 66.5 / 43.3 ms |
| 500,000 | 463k → 467k | — | 197 ms (5.1/s) | 57.0 / 83.3 / 53.1 ms |
| 600,000 | 533k → 528k | — | 222 ms (4.5/s) | 65.3 / 94.6 / 58.0 ms |
| 800,000 | 663k → 629k | — | 277 ms (3.6/s) | 79.5 / 116.1 / 76.8 ms |
| 1,000,000 | 770k → 701k | — | 327 ms (3.1/s) | 90.0 / 128.3 / 102.9 ms |

On the CPU, gravity was 78% of the step at 400k (631 of 805 ms). On the GPU, no single phase dominates any more. At
400k, the 66.5 ms of "gravity" is 23 ms of flatten and pack on the CPU and 42 ms of upload, walk and read-back. The
**CPU tree build (single-threaded) and the collision sweep are now the bottleneck**: together about 57% of the step.
The obvious next steps are a parallel or GPU tree build, and moving the collision broad phase onto the GPU. Neither
is part of this change.

### New ceiling

`SimulationParameters.DefaultMaxLiveParticles` = **350,000** with GPU gravity. The new
`SimulationParameters.CpuMaxLiveParticles` = **150,000** (the old value) is used when the app falls back to the
CPU. The Particles box maximum follows whichever ceiling is active.

Two loads were held at the ceiling. In each, fragments keep the live count pinned at the ceiling.

- **Debris cloud** (`DebrisCloud_HeldAtCeiling`): the cloud above, loaded at the ceiling, 30 steps timed.
- **Light disc** (`LightDisc_HeldAtCeiling`): the 0.1% disc loaded at the ceiling, steps 101–200 timed. Its grains
  barely merge, so nearly every contact sprays fragments. It folds about 100k fragments per step, and collisions
  are about half its GPU step.

| ceiling | backend | debris cloud steps/s | light disc steps/s (tree / gravity / collisions ms) |
|---|---|---|---|
| 150,000 | CPU | 4.4 | 4.1 (68 / 132 / 39) |
| 150,000 | GPU | 15.8 | 14.5 (20 / 24 / 24) |
| 300,000 | GPU | — | 5.7 (45 / 50 / 79) |
| 350,000 | GPU | 5.7 | **5.0** (50 / 54 / 94) |
| 400,000 | GPU | 5.2 (first run: 5.9) | 3.9 (62 / 66 / 128) |
| 450,000 | GPU | 4.6 (first run: 5.3) | 3.3 (70 / 75 / 155) |
| 500,000 | GPU | 4.0 (first run: 4.6) | — |

Repeat runs vary by 10–15% on this laptop. Rule applied: the largest round count at which **both** loads hold about
5 steps/s. That is 350,000. The debris cloud alone would have allowed 400,000–450,000, but the light disc drops to
3.3–3.9 steps/s there. In the app, the light disc at 450,000 ran at 3.4 steps/s, against 3.3 headless, so rendering
on the same GPU costs little. By the same rule, the CPU's 150,000 sits a little below the bar on the light disc
(4.1). It is unchanged here.

**Collision bug found at the higher counts.** In a 450k 10% disc on the GPU path, a super-catastrophic hit
rounded its largest remnant to 0 against the total, with all the debris in one fragment. The remnant's escape
speed became 0/0, and the NaN velocities then broke the tree. Every particle vanished from view, and the star
drifted. The bug is not GPU-specific; it is the collision code's, and more particles make it likely sooner. Fixed
in `CollisionResolver.EmitFragments`: the remnant slot is left dead and compacted, and the fragments use the
system's escape speed. Regression test `SuperCatastrophic_RemnantRoundsToZero_StaysFiniteAndConserved`. The
MergeOrBounce goldens are unchanged.

### Clumping check, GPU vs CPU (10% disc, 50k, 1,500 steps, Outcomes; `GpuVsCpuClumpingTests`)

| seed | CPU: clumps (≥100 grains), bodies, fragments | GPU: clumps (≥100 grains), bodies, fragments |
|---|---|---|
| 1 | 121 (61), 1,788, 9,484 | 112 (56), 1,803, 9,422 |
| 2 | 132 (59), 3,170, 15,029 | 127 (61), 2,605, 11,034 |
| 3 | 121 (60), 2,073, 10,707 | 110 (63), 2,338, 10,171 |
| 4 | 118 (63), 2,031, 9,387 | 125 (69), 2,466, 10,891 |

GPU runs do not reproduce CPU runs bit for bit. Forces differ in the last bits, and the tree-order reorder changes
the order in which contacts are resolved. Clumps of 10+ grains average 118.5 on the GPU against 123 on the CPU,
about 4% fewer. That is inside the seed-to-seed spread (110–127 against 118–132), and four seeds cannot tell it
apart from noise. Clumps of 100+ grains average 61 on the CPU and 62 on the GPU. The spec's bar of at least 50 clumps holds on both
backends.

## Amendment 2026-10-31: GPU tree build and GPU broad phase, ceiling raised to 700,000

Request (task brief): optimise Planet formation with standard methods. Profile first, fix the biggest phase
first, measure after each change, and keep only what measurably helps. Then reset the GPU ceiling by the same rule
as above. This amendment replaces the 350,000 ceiling. The CPU path and its golden hashes are unchanged.

### Baseline profile (debris cloud, GPU path, Release, 32 cores, RTX 4070 Laptop)

`AffordabilityBenchmarks.DebrisCloud_GpuProfile`: the debris cloud above, 10 steps after 2 warm-up steps, with every
sub-phase of `PlanetSimulation.LastPhaseTimes`. Upload and walk are timed to `glFinish`. All figures are ms per step.

| phase | 150k | 350k | 500k |
|---|---|---|---|
| CPU tree build (single-threaded) | 17.2 | 40.1 | 53.0 |
| CPU flatten | 8.4 | 19.7 | 25.6 |
| pack | 0.8 | 2.1 | 2.1 |
| upload (positions + tree) | 6.8 | 15.2 | 23.9 |
| GPU walk | 8.4 | 21.7 | 30.4 |
| read-back | 1.1 | 2.1 | 2.2 |
| scatter + reorder | 1.5 | 1.5 | 1.5 |
| collision broad phase (CPU) | 11.3 | 29.5 | 43.8 |
| collision resolve (CPU, sequential) | 1.7 | 4.2 | 5.7 |
| compact, kick/drift, rest | 1.3 | 3.6 | 3.6 |
| **step** | **58.9** | **139.9** | **192.0** |

The tree build, flatten, pack and tree upload came to 55% of the step, and the broad phase to 21%.

### What changed (in the order the profile said)

1. **Octree built on the GPU** (`GpuGravity`, `planet_keys.comp`, `planet_tree_*.comp`, `radix_*.comp`). Method:
   Morton (Z-order) keys and a radix sort, then the octree built from the sorted keys (Karras 2012, "Maximizing
   parallelism in the construction of BVHs, octrees and k-d trees"; Bédorf et al. 2012, "Bonsai"). Per step, the CPU
   uploads only positions and masses. The GPU then does five things:
   - It gives each body a 60-bit key by descending 20 levels with the CPU tree's own octant test and float centre
     update. So a key names exactly the cube the CPU tree would use.
   - It LSD radix-sorts the keys: 4-bit digits, 16 passes, a per-tile histogram plus the shared SPH scan, and a
     stable scatter.
   - It splits nodes top-down, one level per indirect dispatch. A node with more than 8 bodies splits into its
     non-empty octants, found by binary search on the sorted keys.
   - It sums centres of mass bottom-up, deepest level first.
   - It lays the tree out in stackless pre-order. Pre-order is the nodes sorted by (first body, level), so a count,
     a scan and a scatter suffice, with skip = base[end].

   The tree never comes back to the CPU. A node-buffer overflow is flagged; the buffers grow and the tree is
   rebuilt. A walk leaf with more than one body is summed body by body when it is not accepted whole, with self
   skipped by index. The softening and the θ test are unchanged.
2. **Collision broad phase on the GPU** (`coll_*.comp`). Method: the standard GPU uniform grid (Green 2010,
   "Particle Simulation using CUDA"): a counting sort into hashed cells by atomics, prefix-scanned cell starts, and a
   scan of the 13 forward neighbours. It keeps the size levels of `CollisionResolver`: one grid per level, and coarser
   levels searched by reach, or by list when a level holds at most 16 bodies. It runs on the same key-ordered bodies
   as the walk, in the same GL job.

   The GPU emits candidates with a 1e-4 slack on the squared distance. The CPU re-tests each candidate with the
   exact contact test, then sorts and de-duplicates. So the resolved pairs, and their (lower, higher) order, are
   exactly what the CPU broad phase finds on that state. The CPU resolve is unchanged and still sequential. Two
   changes measured as helping: about 4 buckets per body instead of 1, and the level packed into each bucket entry.
   Together they took the search kernel from 13 to 4.6 ms at 500k.
3. **Parallel CPU around the resolve**, bit-identical:
   - The GPU candidates are re-tested in parallel into buckets by lower index, and each bucket is sorted on its own.
   - Outcomes contacts are classified in parallel before the sweep. A classification is used only while neither
     body has been changed by an earlier pair; otherwise the pair is classified again, as before.

   `ResolveEquivalenceTests` proves both bit for bit. Removing the "changed" check fails them. Light disc held at
   750k: re-test and sort 28.0 → 4.8 ms, resolve 151.7 → 137.5 ms, with identical fragment and fold counts.

### Accuracy (`PlanetGpuGravityTests`, θ = 0.6, against the direct O(n²) sum)

| state | CPU tree: mean / p99 / max | GPU tree, leaf 8 (default): mean / p99 / max |
|---|---|---|
| uniform cloud, 3,000 | 0.96% / 3.28% / 10.4% | 0.89% / 3.03% / 9.1% |
| 10% disc, 4,000 | 0.18% / 0.91% / 3.77% | 0.16% / 0.84% / 3.77% |

At leaf capacity 1, the GPU-built tree has the same node count as the CPU tree. It agrees with the CPU walk to
3e-6 (max relative), and in `PlanetSimulation` at Load (20k disc) to 1.1e-6 at p99 and 1.8e-4 max. One rounding of
a centre of mass can flip one opening decision.

The test requires the GPU's mean, p99 and max to be no worse than the CPU tree's (2% tolerance for summation order)
at leaf capacities 1, 4, 8 and 16. Larger leaves are slightly more accurate. Every node the GPU accepts whole is a
CPU-tree node with the same cube, and where the CPU tree would open further, the GPU sums exactly.

The GPU broad phase passes the brute-force contact-set test of the CPU broad phase (four mixed-size states,
including dead bodies). It also equals the CPU broad phase pair for pair, in order. Overflow tests cover the node
buffer and the pair buffer.

### After (same profile)

| phase | 150k | 350k | 500k |
|---|---|---|---|
| CPU prep (bounds, pack) | 0.3 | 0.7 | 0.7 |
| GPU tree build | 2.9 | 4.6 | 5.6 |
| upload (positions, masses, radii) | 2.6 | 4.6 | 4.2 |
| GPU walk (leaf 8) | 4.7 | 11.5 | 16.5 |
| GPU broad phase | 1.5 | 4.4 | 6.9 |
| read-back (accelerations, order, pairs) | 1.4 | 2.7 | 2.9 |
| scatter + reorder | 0.4 | 1.3 | 1.4 |
| CPU re-test + sort of candidates | 0.1 | 0.2 | 0.4 |
| collision resolve (CPU, sequential) | 1.5 | 3.7 | 4.7 |
| compact, kick/drift, rest | 1.3 | 3.3 | 3.7 |
| **step** | **16.7** (3.5x) | **37.0** (3.8x) | **47.1** (4.1x) |

Debris cloud, whole step (`DebrisCloud_StepCostAndPhaseSplit`, GPU):

| N | live | before | after |
|---|---|---|---|
| 150,000 | 156k → 169k | 62 ms (16.1/s) | 18 ms (55.2/s) |
| 200,000 | 205k → 222k | 85 ms (11.7/s) | 21 ms (47.0/s) |
| 300,000 | 298k → 315k | 125 ms (8.0/s) | 36 ms (27.5/s) |
| 400,000 | 385k → 398k | 160 ms (6.2/s) | 37 ms (27.4/s) |
| 500,000 | 463k → 466k | 197 ms (5.1/s) | 49 ms (20.3/s) |
| 600,000 | 534k → 529k | 222 ms (4.5/s) | 60 ms (16.6/s) |
| 800,000 | 663k → 629k | 277 ms (3.6/s) | 67 ms (15.0/s) |
| 1,000,000 | 771k → 702k | 327 ms (3.1/s) | 73 ms (13.7/s) |

### Tried and dropped

- **Group walk** (Barnes 1990: one walk per 64 consecutive bodies, opening test against the group's box). It was 6
  to 9 times slower here: 239 against 36 ms at 350k, and 662 against 73 ms at 1M. Its max error on the disc (7.7%)
  was also worse than the CPU tree's (3.8%). Removed.
- **Leaf capacity.** Walk times at 350k (repeat runs vary about 1.5 ms): 15.2–15.6 ms at leaf 1, 13.4 at 4,
  10.0–11.7 at 8, 9.8–11.9 at 16, and 12.2–16.1 at 32. 8 is kept: as fast as 16 within the noise.
- **Hoisting the constant `Math.Pow(1.8, -1.5)` out of `LargestRemnantFraction`.** Bit-identical, but only 5% of
  classify time, inside the noise. Reverted.
- **Persistent-mapped upload/read-back:** not done. Positions must go up every step, because the integrator and the
  resolve stay on the CPU. Upload plus read-back are about 7 ms at 350k, against the 137 ms sequential resolve that
  now sets the ceiling.

### New ceiling

`SimulationParameters.DefaultMaxLiveParticles` = **700,000** (GPU). The rule is the same as above: the largest round
count at which both held-at-ceiling loads keep about 5 steps/s.

| ceiling | debris cloud steps/s | light disc steps/s (gravity / collisions ms; contacts per step) |
|---|---|---|
| 350,000 | 29.8 | 13.1 (26 / 47; 86k) |
| 500,000 | 18.8 | 9.0 (40 / 68; 188k) |
| 600,000 | 14.8 | 7.0 (42 / 96; 284k) |
| 650,000 | 13.8 | 6.1 (47 / 112; 339k) |
| **700,000** | **12.2** | **5.4** (51 / 130; 403k) |
| 750,000 | 10.6 | 4.8 (57 / 146; 472k) |
| 1,000,000 | 7.4 | 2.9 (87 / 256; 914k) |
| 1,250,000 | 5.0 | 1.8 |
| 1,500,000 | 4.2 | 1.4 |

The debris cloud alone would allow 1,250,000. The light disc sets the ceiling. Its contacts grow about as N²
(fixed disc, more grains), and **the sequential CPU resolve is now the limit**: 123 of 185 ms at 700k, about
300 ns per contact, two thirds of it in `CollisionOutcomes.Classify`. Only about 18% of its classifications can be
reused from the parallel pass, because most bodies are in more than one contact. 750,000 (4.8 steps/s) is
borderline. 700,000 holds with margin. In the app, the 0.1% disc at 700k reads 5.0 steps/s (Gravity 58 ms, Collide
136 ms).

The CPU's 150,000 was re-checked and is unchanged: light disc 5.5 steps/s (before 4.1, helped by the parallel
candidate and classify work), debris cloud 4.7 (before 4.4–4.9).

### Clumping check, GPU (new) vs CPU (10% disc, 50k, 1,500 steps, Outcomes; `GpuVsCpuClumpingTests`)

| seed | CPU: clumps ≥10 (≥100), bodies, fragments | GPU: clumps ≥10 (≥100), bodies, fragments |
|---|---|---|
| 1 | 121 (61), 1,788, 9,484 | 121 (65), 2,362, 10,803 |
| 2 | 132 (59), 3,170, 15,029 | 98 (50), 2,120, 10,520 |
| 3 | 121 (60), 2,073, 10,707 | 123 (55), 2,213, 10,806 |
| 4 | 118 (63), 2,031, 9,387 | 128 (59), 2,714, 12,314 |

Clumps of 10+ grains average 117.5 on the GPU against 123 on the CPU, about 4.5% fewer. That is the same gap as the
previous GPU path (118.5). Seed 2 is low (98 against 132), while seeds 1, 3 and 4 match or exceed the CPU. Clumps of
100+ grains average 57 on the GPU against 61 on the CPU. Four seeds cannot separate this from seed-to-seed spread,
but the gap points the same way as before. The spec's bar of at least 50 clumps of 10+ grains holds on every seed.

Visual check, Release app at 700,000 (PrintWindow): the 10% disc (rings of clumps, then a cleared disc with planets)
and the 30% disc (fast collapse to a few large bodies). Both stay finite and look as before, and the 0.1% disc is
pinned at 700k. Screenshots: `.gauntlet/planet-formation-shots/treebuild-*.png`.
