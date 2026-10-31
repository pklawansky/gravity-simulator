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

- **Sort-and-sweep:** rejected. In a thin disc, one axis's intervals overlap about as many bodies as the old grid's
  cells did.
- **Querying the Barnes-Hut octree:** rejected. It needs per-node radius bounds and pointer-chasing traversal, which
  is slower than hashing.

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
