# Makarov Physics Sandbox (working title)

A 3D rigid-body physics engine and sandbox written in **pure C#** — no physics, math or
windowing libraries. WinForms host, hand-written OpenGL renderer (P/Invoke to `opengl32`),
hand-written constraint solver.

**Product direction:** a **3D physics sandbox about building chaotic physical experiments
and chain reactions** — a toy/game, not an educational lab, CAD tool, or engineer's
simulator. Working logline:

> *3D physics sandbox about building chaotic contraptions and watching them fall apart.*

People Playground is **market proof** (a solo/micro-studio physics sandbox can sell ~3M
copies), **not a template**. The lesson taken from it is the *loop*, not "2D gore":

> place an object → trigger a reaction → get a funny / destructive / unexpected result →
> want to do it again differently.

We deliberately stand **beside** People Playground, not head-on against it: a **3D
contraption / destruction sandbox**, same audience, different promise. Gore is *optional
and not the hook* — the cast can be **crash-test dummies, robots, blocks, vehicles,
machines**, which both widens the audience (lower age rating, more streamable) and avoids
being "just another PP clone".

> **Note on the ragdoll work:** the articulated ragdoll (joints + pose muscles + damage +
> dismemberment) is **the same tech** a crash-test dummy or a robot-that-falls-apart needs.
> Moving to the dummy/destruction framing is a **reskin** (mesh + damage feedback + Steam
> positioning), **not a rewrite**. The systems below carry over unchanged.

**Product axis for filtering every future task:** *not "an engine with lots of features",
but "a game where the player builds physical catastrophes".* Material system? Yes — players
read "glass breaks, rubber bounces, metal sinks". Triggers? Yes — they make chain reactions.
Fullscreen release mode? Yes — a Steam player must never see the WinForms editor. Force
graphs / educational explainers? Not now — that is a separate branch.

---

## Two build modes (design intent)

The WinForms application — menus, toolbar, property panels, gizmos, presets, challenges,
the editor tools — is the **dev / editor shell**. It exists for *our* convenience and is
**not** what a player should ever see.

- **Dev / editor build:** WinForms chrome, property panels, debug tools, scene editing,
  presets, internal utilities, this README.
- **Release build:** **fullscreen scene**, minimal HUD, a player-friendly radial / tool
  menu, clean controls — no visible "WinForms feeling", no engineering-panel hell.

The plan is a single startup switch (build flag or launch argument) that boots straight
into a borderless fullscreen `GlPanel` with the editor chrome suppressed.

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

## What makes it a game (not a toy): the loop + the matrix

A bare sandbox is a toy with no reason to buy it. Two things turn it into a product:

**1. The core loop.** Place → trigger → surprising result → repeat differently. Every
feature is judged by whether it tightens this loop. The "surprising result" comes from
**emergent interactions**, which is the second thing:

**2. The interaction matrix.** The product is not any single object — it is the *matrix of
interactions* between simulation systems. Fire × flammable, current × conductor, current ×
water, blade × joint, blast × durability, impact × fragile. Each filled cell is an emergent
moment the player didn't expect. **Design that table first; it is the game design.**

Our **differentiator is 3D** (genre leaders are 2D) and our **moat is the from-scratch
engine** (no Unity PhysX black box; deterministic-friendly, fully ours — spatial chain
reactions and contraptions that read better in 3D).

**Vertical-slice discipline:** the cast list (dummies / robots / blocks / vehicles /
machines) is a *menu, not a focus*. The first trailer-worthy slice needs **one sharp hook
and one verb** — pick a single fantasy and make *that* feel great before fanning out.

A 3D-specific marketing pillar: **shareable cinematic clips**. Sandboxes of this kind spread
through clips — so camera, slow-mo and "looks good in 3D" are first-class, not afterthoughts.

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

**In progress**
- **M0 — Ragdoll** (`Ragdoll.cs`): humanoid from boxes + sphere + `Point` joints; **pose
  muscles** (per-joint angular drives holding the spawn pose, now stiffer so it stays
  together when dragged); **death** + **dismemberment**. Damage from the world `Impacts`
  channel is now **blunt only** — capped, cooldowned, and floored, so falls and dragging
  bruise (redden) but never tear the body apart. Severing/killing is reserved for
  deliberate weapons via `RagdollSystem.DamageBone(..., sever-capable)`. Spawn via the
  toolbar **Ragdoll** button (icon, click-to-place) or the **`0`** key (spawn at cursor).
- **M1 — Fire/heat** (`Heat.cs`): per-body `Temperature` / `Burning` / `Flammability`
  (Physics.cs). Burning bodies consume fuel, radiate heat to nearby flammable bodies,
  ignite them past an ignition point, then char and burn out. Dense bodies (metal/stone)
  resist via a density gate. **Fire damages burning ragdoll bones over time** (sever-capable)
  — so a fire spreads bone-to-bone, cooks a vital part, and collapses the body, all
  emergent. Igniter tool: toolbar **Ignite** button (torch icon) / **`I`** key → click a
  body. Burning bodies glow and flicker; charred ones darken.

**Next**
- Tune both feels (muscle gains, damage curve, fire spread/ignition rates).
- Add the rest of M1: **electricity** (conductivity over the contact/connection graph) and
  a **blade** verb (sever-capable on contact). Wire per-material `Flammability`/conductivity
  into the material-preset table + properties panel + `.mpscene` serialization (currently
  flammability is a body default gated by density; not yet saved/loaded or editable in the
  panel). Then fill more of the interaction matrix.

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
  | fire    | **ignite -> spread (done)** | — | resists/quench | — | — |
  | current | — | propagate | jump through water | — | — |
  | blade   | — | — | — | sever joint | — |
  | blast   | ignite? | — | — | — | fracture |
  | impact  | — | — | splash | **blunt bruise (done)** | fracture |

  Also wired: **fire x ragdoll** (burning bones take damage, spread, can kill — emergent).

---

## Changelog

- **(pass 3)** *Direction refined* (no code change): positioned as a **3D chaos /
  contraption / destruction sandbox** standing beside People Playground, not a gore clone;
  documented the core loop, the editor-vs-release split detail, and the vertical-slice
  "one hook / one verb" discipline. Noted that the ragdoll work is reused as crash-test
  dummy / robot tech (reskin, not rewrite). Project now delivered as a single zip archive.
- **(pass 2)** *Sturdier ragdoll*: impact damage is now blunt-only (capped, cooldowned,
  health-floored, non-severing); muscles stiffened so it holds together when dragged.
  *Toolbar*: added **Ragdoll** (`ragdoll.png`) and **Ignite** (`torch.png`) buttons in the
  spawn / actions groups, both click-to-place; keys `0` (ragdoll) and `I` (ignite).
  *M1 opened*: `Heat.cs` fire/heat system + per-body thermal fields on `RigidBody`; fire
  ignites/spreads/chars and damages ragdoll bones.
- **(pass 1)** Pivot direction documented (3D People Playground). Added `Ragdoll.cs`
  (humanoid, pose muscles, impact-driven damage, dismemberment, death, render tint) and the
  one-line `RigidBody.Tag`. Wired into `GlPanel`: per-frame update, `0`-key spawn, render
  tint, clear-on-`ClearDynamic`. README rewritten with full engine inventory + roadmap.
