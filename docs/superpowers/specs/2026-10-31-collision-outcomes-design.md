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
