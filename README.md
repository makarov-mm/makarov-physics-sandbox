# Makarov Physics Sandbox → 3D People Playground (working title)

A 3D rigid-body physics engine and sandbox written in **pure C#** — no physics, math or
windowing libraries. WinForms host, hand-written OpenGL renderer (P/Invoke to `opengl32`),
hand-written constraint solver.

The project is pivoting from "a physics sandbox" (a toy with no goal, a crowded and
commercially dead genre) into a **3D People Playground-style game**: a physics
experimentation sandbox whose product is the *matrix of interactions* between several
simulation systems, with a hurtable, dismemberable humanoid ragdoll as the canvas.

---

## Two build modes (design intent)

The WinForms application — with all its menus, toolbar, property panels, gizmos, presets,
challenges and the editor tools — is the **editor / authoring build**. It exists for *our*
convenience: building scenes, tuning, debugging.

The **release build** ships only the **fullscreen game scene** (no menus, no toolbar, no
property panels) — you spawn into a scene and play. The plan is a single startup switch
(build flag or launch argument) that boots straight into a borderless fullscreen `GlPanel`
with the editor chrome suppressed.

*Status:* not implemented yet. Today there is an `F11` fullscreen toggle and `F4` panel
toggle; the release-mode boot path is a later task (see Roadmap → M2/M3).

---

## The engine today (what actually exists)

Everything below is implemented and working in the current code.

### Rigid-body dynamics (`Physics.cs`)
- **Bodies**: spheres, boxes, capsules, and **compound bodies** (multiple child shapes in
  one rigid body). Origin is moved to the centre of mass; full **inertia tensors** built
  via the parallel-axis theorem (`Mat3`, a custom 3x3 matrix type — `System.Numerics` has
  none). Custom `Quat.Mul` with a documented, consistent Hamilton-product convention.
- **Solver**: sequential-impulse, fixed step `1/120 s`, 10 iterations. **Warm starting**
  (cached impulses re-anchored in A's local frame) so stacks settle instead of buzzing.
  **Split-impulse** (pseudo-velocity / Baumgarte) position correction so penetration fixes
  don't inject energy. Friction via two tangent directions per contact.
- **Broad phase**: uniform **spatial hash** for normal bodies; a separate "huge body" path
  for the arena walls so they don't blow up the cell size. (Replaced the old O(n^2).)
- **Narrow phase**: shape-vs-plane (sphere/box/capsule) and pairwise contact generation.
- **Sleeping**: bodies below linear+angular thresholds for a delay nod off; impacts and
  force fields wake them.
- **Rolling resistance** for balls in contact (so they coast to rest, don't roll forever).
- **Joints** (`Joint`): `Point` (ball-socket — used for chains *and ragdolls*), `Distance`
  (rigid rod), `Rope` (resists stretch only), `Spring` (soft, oscillating). Solved in the
  same loop as contacts. World-anchored if `B == null`.
- **Force fields** (`ForceField`): attractor, repeller, wind — applied as mass-independent
  accelerations with linear falloff.
- **Water** (`WaterVolume`): Archimedes buoyancy from per-body density, depth-scaled drag,
  wavy animated surface, and object-driven expanding **ripples**.
- **Destruction**: `Breakable` bodies fracture into smaller pieces above an impact
  threshold (one-level fracture to keep debris bounded).
- **Queries / impulses**: `RayCast` (sphere/box/capsule), `ApplyExplosion` (radial impulse
  with a lift bias), grab spring (`Grabbed` + `DragTarget`), and an **`Impacts`** channel
  the renderer drains for sparks (also now drives ragdoll damage).

### Rendering (`Core.cs`, `GL.cs`, `Shaders.cs`, `Textures.cs`, `Mesh.cs`)
- Hand-written OpenGL via P/Invoke. Shadow-map pass + main pass, per-body colour and
  emissive, procedural textures, particles, animated water surface, force-field VFX,
  joint rods / spring lines, aim markers, editor gizmo.

### Editor / app (`Core.cs`, `MakarovPhysicsSandbox*.cs`, `*PropertiesPanel.cs`)
- Toolbar + menus + dark chrome, status bar, keyboard shortcuts.
- Editor tools: Select / Move / Rotate / Scale, object gizmo, selection + live property
  editing (material, density, friction, bounciness, position, velocity, colour, static,
  breakable, break force, scale).
- Spawn tools (sphere/box/capsule/plank/pillar/dumbbell/hammer/table, bowling pins, chain),
  connect / spring / disconnect, attractor / repeller / wind / explosion placement,
  water and gravity toggles, slow-mo, pause, single-step.
- **Triggers / pressure plates** (`SceneTrigger`, `TriggerPropertiesPanel`) with actions.
- **Presets**, **Challenges**, and a **Campaign** (levels + stars + progress).
- **Save/Load** scenes as `.mpscene` JSON (`SceneSerialization.cs`).

### Material presets
Wood, Metal, Rubber, Glass, Stone, Foam, Ice, Plastic. These are **UI presets** (they set
density/friction/bounciness/colour/breakability), not hard engine material types yet.

---

## Where we're going: 3D People Playground

**The product is not the ragdoll and not "lots of items". It is the interaction matrix
between several simulation systems.** Selling examples (People Playground, ~3M copies as a
solo/micro-studio title) all share three things, and a bare sandbox has none of them:

1. **A sharp identity / hook** (not "physics in general").
2. **Controllable, reactive "toys"** — above all a humanoid that holds together, reacts,
   bleeds, dies, can be revived — not just falling boxes.
3. **A creation + sharing loop** (Workshop / share contraptions).

Our **differentiator is 3D**, where the genre leaders are 2D — and our **moat is the
from-scratch engine** (no Unity PhysX black box; deterministic-friendly, fully ours).

The emergent "magic" comes from filling in the **verb x material-property matrix**:
fire x flammable, current x conductor, current x water-on-floor, blade x joint,
blast x durability, ... Each filled cell is an emergent moment. **Design that table first.**

A 3D-specific marketing pillar: **shareable cinematic clips**. People Playground spreads
through clips — so camera, slow-mo and "looks good in 3D" are first-class concerns, not
afterthoughts.

---

## Roadmap

- **M0 — Ragdoll feel gate** *(in progress)*: a humanoid that holds a standing pose,
  reacts to hits, takes localized damage, dismembers, and goes limp on death. **This is
  the go/no-go gate**: if poking/shooting/blasting one ragdoll isn't satisfying for five
  minutes, no amount of content saves the game. Tune *this* before building anything else.
- **M1 — Interacting systems**: per-bone/per-object material properties (flammability,
  conductivity, sharpness, durability); **fire/heat** (per-object temperature scalar +
  contact/proximity spread + ignition — *not* a 3D heat field); **electricity** (propagation
  over the contact/connection graph); explosives polish. Then fill the interaction matrix.
- **M2 — Depth & contraptions**: more verbs/items; **joint motors/actuators** (so the
  existing joints become powered — wheels, pistons), keybind-to-action; release fullscreen
  boot path; in-scene spawn UI for the game build.
- **M3 — Retention**: scene/creation **sharing** (Steam Workshop), object catalogue,
  cinematic camera + clip capture.
- **M4 — Optional heavy pillars** *(only if the core lands)*: SPH **fluids/blood**,
  **soft body**. These are the biggest time sinks in 3D; deferred on purpose.

---

## Status

**Done**
- Full rigid-body engine, renderer, editor, presets/challenges/campaign, save/load (above).

**In progress (M0)**
- `Ragdoll.cs`: humanoid built from boxes + sphere + `Point` joints; **pose muscles**
  (per-joint angular drives holding the spawn pose); **damage** fed from the world
  `Impacts` channel (balls, blasts, falls all hurt for free); **dismemberment** (a bone at
  0 HP severs its joints); **death** (killing head/torso drops the body limp). Render tint:
  skin reddens with damage, brief emissive flash on hit, dims when dead.
- Spawn with the **`0`** key (drops a ragdoll at the floor point under the cursor).

**Next**
- Tune ragdoll feel: muscle stiffness/strength per joint, upright strength, damage curve,
  mass. Decide whether v1 should self-balance at all or just hold pose + flop convincingly.
- Add a dedicated **hitscan weapon** (raycast -> `RagdollSystem.DamageBone`) and a blade.
- Begin M1: material properties + fire.

---

## Implementation notes & gotchas (living)

- **This repo was last edited in an environment with no Windows/.NET-desktop toolchain, so
  the M0 changes were verified by inspection against the real engine APIs, not compiled.**
  Build on your machine and expect to tune; logic was checked against the actual signatures
  in `Physics.cs` / `Core.cs`.
- **Ragdoll is decoupled**: `Ragdoll.cs` only touches *public* members of `RigidBody`,
  `Joint`, `PhysicsWorld`, plus the new one-line `RigidBody.Tag`. It never modifies the
  solver, so it cannot destabilise the existing sandbox.
- **Integration points** (search for `_ragdolls` / `RagdollSystem` in `Core.cs`):
  field next to `_particles`; `_ragdolls.Update(simDt, _world)` in `RenderFrame` after
  `UpdateParticles`; `SpawnRagdollAtAim()` + key `0x30`; `RagdollSystem.TryTint` in the
  body render loop; `_ragdolls.Clear()` in `ClearDynamic`. Reset/clear of the *whole* world
  is handled by self-healing: `Update` prunes any ragdoll whose pelvis is no longer in
  `world.Bodies`, so the two `ResetScene` paths and `EvictIfFull` need no edits.
- **Tunables** live as `const`s at the top of `RagdollSystem` (`HurtSpeed`,
  `DamagePerSpeed`, muscle shares, `MaxDeltaW`, `BoneDensity`) and `Ragdoll.UprightStrength`.
- **Known limitations (v1)**:
  - *No self-collision filtering* between jointed bones — neighbours can jitter when they
    overlap. Spawn layout avoids initial penetration; if jitter shows up, either reduce the
    upright/muscle gains or add a collision-skip flag for jointed pairs.
  - *Full balance is deferred*: M0 only **holds pose + weak self-righting**. Real
    centre-of-mass balance (standing on feet, stepping to recover) is hard and not v1.
  - *Spawning near the body cap* (`MaxBodies`) can evict a bone mid-build; the pelvis-prune
    then drops the orphaned ragdoll. Fine for normal use; revisit if it bites.
- **Interaction matrix (fill this in as M1 lands):**

  | verb \ property | flammable | conductive | wet | sharp-target | fragile |
  |---|---|---|---|---|---|
  | fire    | ignite -> spread | — | resists/quench | — | — |
  | current | — | propagate | jump through water | — | — |
  | blade   | — | — | — | sever joint | — |
  | blast   | ignite? | — | — | — | fracture |
  | impact  | — | — | splash | wound | fracture |

---

## Changelog

- **(this pass)** Pivot direction documented (3D People Playground). Added `Ragdoll.cs`
  (humanoid, pose muscles, impact-driven damage, dismemberment, death, render tint) and the
  one-line `RigidBody.Tag`. Wired into `GlPanel`: per-frame update, `0`-key spawn, render
  tint, clear-on-`ClearDynamic`. README rewritten with full engine inventory + roadmap.
