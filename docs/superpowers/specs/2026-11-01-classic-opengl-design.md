# Classic simulator on OpenGL — design

**Verdict:** the classic simulator now draws through OpenGL 3.3 (the same OpenTK GLControl as Planet
formation) and keeps the old GDI renderer as an automatic fallback. Same scene, same projection and
focus control, same labels and keys; smooth per-pixel lighting instead of facets, and a depth buffer
instead of painter's sorting. Physics is untouched.

## What is drawn

| Element | GDI (before) | OpenGL (now) |
|---|---|---|
| Walls (floor, ceiling, left, right, back) | `Plane` facets 0.1 m, colour per facet from the 5 point lights | The same `Plane` facets uploaded once; the same lighting formula per pixel |
| Spheres | `SphereModel` facet meshes, sorted back to front by y | Ray-cast impostors: a disc of the projected radius, normal and depth per pixel, lit by the same formula |
| Lights | Translucent white octahedra (alpha 150), drawn before the spheres | Translucent white discs (alpha 0.83), depth tested, not depth written |
| Collision flash | `DrawCircle` with the radius in metres used as pixels, so a red dot of a pixel or two | A red dot of 2 px radius at the sphere centre, shown once then cleared, as before |

Lighting, per light: `base * ((pi/2 * ambience) - angle) / (pi/2 * ambience) * intensity / d^2`,
clamped per light, summed and clamped (`Facet.UpdateBrush`), with the wall normals facing into the
box and the sphere normals outward.

## Projection

`GraphicsEngine.Convert3DTo2DFixedPerspective` shrinks a point towards the image centre by
`s = 1 - (1 - focus) * y / bounds.y`. That factor is linear in depth, and a 4x4 projection matrix can
only divide by something linear in depth, so no matrix reproduces it exactly (the nearest true
perspective is off by about 25 px at mid-depth near the edges). `Classic/ClassicProjection.cs` splits
it instead into a scene matrix, world to `(2x/bx - 1, 2z/bz - 1, depth, y/by)`, and the scale `s`. The
vertex shader emits `(q.x, q.y, q.z / s, 1 / s)`, so after the divide by w the point is at
`(q.x * s, q.y * s)` exactly as GDI, with an NDC depth linear in y (mapped to [-0.999, 0.999] so the
back wall is neither clipped nor lost to the cleared depth). Straight edges between projected
vertices are drawn straight, as GDI did. The focus keys therefore behave exactly as before: Left lowers
focus and shrinks the back of the box.

Window resize: the scene keeps its aspect (bounds.x : bounds.z), scaled to fit the GL control and
centred with black bars; at the size the scene was built for it fills the control, as the bitmap did.

## Driving and fallback

- The 17 ms WinForms timer still runs one physics step per tick on the UI thread (unchanged physics
  and step size). The tick snapshots the spheres and invalidates the GL control; drawing happens on
  paint. Drawing costs well under a millisecond of CPU, so it no longer holds the tick back.
- Start-up probes a hidden OpenGL 3.3 core context first (a GLControl's own context failure is raised
  inside its window procedure, where WinForms shows the unhandled-exception dialog rather than
  throwing). If that fails, the GDI renderer runs and the label says "GDI (OpenGL unavailable)", with
  the reason in its tooltip. A shader compile or link error is an internal error: a dialog says so,
  then GDI runs.
- `GRAVITYSIM_CLASSIC_RENDERER=gdi` forces GDI; `=simulate-gl-failure` requests OpenGL 9.9 to exercise
  the fallback.
- A small grey label under Focus shows the renderer, frames per second, and the draw and tick cost.

## Behaviour change to know about

The GDI renderer took about 190 ms per frame, so the timer ticked about 5 times a second; with OpenGL
it ticks at the timer's real rate (about 32 a second). Physics advances a fixed time per tick, so the
simulation now runs about 6x faster in wall-clock time, and merges happen sooner. This is the speed
the timer always asked for; slowing it back down would be a separate, deliberate change.

## Tests

- `ShaderCompileTests.ClassicGlRenderer_AllProgramsBuild`: both programs compile and link.
- `ClassicProjectionTests`: the C# projection matches the GDI function at 729 points x 6 focus values
  x 3 sizes (worst 0.04 px, float rounding), the Left key direction, and the resize fit.
- `ClassicGlRendererTests.SphereCentres_LandWhereGdiPutsThem`: a sphere rendered on the GPU, its pixel
  centroid against the GDI position, 27 points x 5 focus values x 2 sizes (worst 0.13 px).
- `ClassicGlRendererTests.AllWalls_AreDrawn`: every wall, the back one included, is drawn.

GPU tests skip without an OpenGL 4.3 context, like the others.

## Out of scope

More particles, an orbit camera, physics changes, the unused orthographic mode (GDI only).
