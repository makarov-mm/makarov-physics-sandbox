# Preset and Mechanism Guide

This document describes Wrecksmith's mechanisms, triggers and presets. It separates playable/showcase content from internal test labs.

## Current rule

Do not add more mechanism/object categories until the existing interaction matrix is understandable. The project already has enough raw parts: android dummy, fire, electricity, water, explosions, triggers, timers, gates, sliding doors, conveyors, pistons and motor hinges.

The next useful work is clarity: what each preset is supposed to demonstrate, what the player should try, and whether it belongs to a future game slice or only to an internal test lab.

## Mechanisms

### Trigger / pressure plate

A trigger is a sensor volume. When a dynamic object enters or presses it, it fires one or more outputs.

Use it for: starting timers, opening gates, starting conveyors, firing pistons, starting motors, or triggering explosions.

Important controls:

- Select the trigger to edit its outputs in the Trigger panel.
- F7 snaps the selected trigger output target to the nearest compatible mechanism.
- W shows or hides trigger wiring.

### Timer

A timer is a delayed relay. A trigger can start the timer; the timer then fires its own configured action after a delay.

Use it when a chain reaction needs staging, for example:

`ball hits pressure plate -> timer waits -> door opens -> conveyor starts -> piston fires`

### Gate

A gate is a blocking barrier that opens when triggered. It is useful as a simple obstacle in a chain reaction lane.

Typical use:

`Trigger output: OpenGate -> target gate`

### Sliding Door

A sliding door is similar to a gate, but it is intended to read visually as a door or moving wall panel.

Typical use:

`Trigger output: ToggleDoor -> target sliding door`

### Conveyor

A conveyor pushes dynamic objects along its belt direction. It is a gameplay mechanism, not a precise contact-surface simulation.

Typical use:

`Trigger output: StartConveyor -> target conveyor`

### Piston

A piston is a gameplay actuator. It moves a pusher body and applies impulse to nearby dynamic bodies.

Typical use:

`Trigger output: StartPiston -> target piston`

### Motor Hinge

Motor hinge is currently a powered rotating arm. It is not a full constraint-editing UI yet. It is intended for simple rotating hazards or pushers.

Typical use:

`Trigger output: StartMotor -> target motor hinge`

If it is unclear what it does in a preset, the preset is not good enough yet and should be treated as an internal lab, not as a player-facing scene.

## Preset categories

### Showcase candidates

These are the only presets that should be considered for a player-facing vertical slice right now:

- Android Crash Test Chamber
- Piston Crusher Lab
- Conveyor Chain Lab

They still need design work: tighter camera, mounted android target, clear start state, clear win/fail state, and a visible payoff.

### Prototype labs

These are internal stress tests. They are allowed to be messy, but they should not be presented as the game loop:

- Android Fire Lab
- Electrical Chain Lab
- Android Stress Chamber
- Mechanism Chain Reaction
- Trigger Playground
- Vehicle Crash Test
- Ragdoll Bowling
- Burning Barricade
- Catapult

### Legacy/simple demos

These are mostly physics-engine demos:

- Domino Run
- Tower Collapse
- Bridge Test
- Newton Cradle
- Zero-G Chaos
- Water Playground
- Wrecking Ball
- Explosive Domino
- Barrel Pyramid
- Electric Floor Trap

## What a good preset must have

A preset should not just contain objects. A useful preset needs:

1. A clear one-line purpose.
2. A visible start point.
3. A predictable chain of events.
4. A target or payoff.
5. A readable success/fail condition if it is gameplay-facing.
6. A reason to retry.

If a preset does not satisfy these points, it is a lab, not a game slice.

## M4.2 bridge / catapult / drone toys

New player-facing toys:

- **Bridge span**: a compact jointed wooden bridge module. It is intended for quick vehicle/weight/collapse experiments without manually placing every plank and joint.
- **Catapult launcher**: a stable one-shot launcher. It is not a full physically simulated medieval catapult yet; it is a readable launcher toy for producing a reliable payoff shot.
- **Drone target**: a small synthetic target object. It gives the sandbox a third non-human target class beside android dummy and vehicle.

New presets:

- **Bridge Jump**: vehicle + bridge + drone target. Intended as a bridge/vehicle showcase candidate.
- **Catapult Bridge Siege**: catapult shot into a bridge-side target cluster. Intended as a catapult/bridge showcase candidate.
- **Drone Target Range**: catapult launcher and multiple drone targets. Intended as a simple aiming/destruction toy.

Design note: these are deliberately framed as toys/showcase objects, not as another deep system branch. The next step should be tuning and presentation, not adding more object categories.


### 2026-06-14 — M4.6 visual realism / stability fix pass

- Reworked texture assets again with explicit raster PNGs for sky, brick wall, crate wood, cart wood, rusty metal and explosive barrel.
- The arena wall now uses a clearer brick texture and higher wall UV repetition so long wall segments no longer read as stretched rectangles.
- Replaced the flat/gray skybox asset with a brighter cloud-sky texture.
- Reworked the explosive barrel material toward a real painted metal drum: ribs, warning label, rust spots and a separate bump map.
- Bowling pin red bands are now rendered as flattened round rings instead of red box geometry, so the pins no longer have square red artifacts.
- Bowling pin visual scale now derives from the underlying capsule radius, so editor scaling affects both physics and the rendered visual.
- The catapult launcher is now a single editable compound object instead of several separate static bodies; it can be selected, moved, rotated and scaled as one launcher.
- Wooden cart wheel overlay was made more visible: more spokes, faster visual spin and a hub marker while keeping the stable compound-body physics.
- Ragdoll joint-stress iteration now guards against mid-loop muscle removal after limb severing, fixing a crash observed when an android broke apart in/near the cart.

Current visual rule: physics shapes may stay simplified, but important props can use richer visual overlays/textures when this improves readability.

## M4.7 object variety / prop polish notes

New gameplay props added in this pass:

- **Cylinder** — generic metal cylinder using a capsule physics proxy.
- **Explosive barrel** — still capsule-proxy physics, but visually treated as a proper warning-labelled drum.
- **Gas cylinder** — smaller explosive pressure-vessel style prop.
- **Beach ball** — light, very bouncy and buoyant toy object.
- **Metal cube** — heavy conductive object for crush/impact/electricity tests.
- **Sentinel bot** — additional synthetic target distinct from android, drone and vehicles.
- **Police car / ambulance** — vehicle variants for bridge/crash scenes.

Catapult launchers should be treated as triggerable toys now: placing one should not immediately fire. Use trigger outputs with `LaunchCatapult` near the launcher to fire it as part of a chain reaction.

