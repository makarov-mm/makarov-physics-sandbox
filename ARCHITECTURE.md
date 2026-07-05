# Architecture notes

This project is intentionally a custom C#/.NET + Win32/OpenGL codebase rather
than a Unity/Unreal/Bullet/PhysX project. Everything — solver, renderer,
windowing, UI, audio, image decoding — is written in this repository against
raw OS APIs.

## Layers

1. **Platform** — `Win32.cs` (window, message loop, DPI), `Input.cs`,
   `GdiPlus.cs` (PNG decoding via GDI+/WIC), `GL.cs` (OpenGL P/Invoke and
   extension loading).
2. **Physics** — everything under `Physics/`. Self-contained; exposes
   `PhysicsWorld`, `RigidBody`, `Joint`, `ForceField`, `WaterVolume`,
   `BreakEvent` and an `Impacts` channel. No references to rendering or UI.
3. **Simulation systems** — `Ragdoll.cs`, `Heat.cs`, `Electricity.cs`,
   `Mechanisms.cs`, `Material/`. Built only on the public physics API; they
   never modify the solver.
4. **Game/editor shell** — `GlPanel` (the big partial class in `Core.cs` +
   `Core.Menu.cs`) plus its data types under `Core/`, the campaign under
   `Campaign/`, scene serialization (`SceneSerialization.cs`, `SceneJson.cs`,
   `Dto/`), presets (`PresetsExtra.cs`), and the app host
   (`MakarovPhysicsSandbox.cs`, `Program.cs`, `LaunchOptions.cs`).

## Source layout rules

- One data type per file where practical; small `GlPanel`-owned types live
  under `Core/` (`Particle`, `Beam`, `VehicleRig`, `SceneTrigger`, selection
  snapshots, enums).
- `GlPanel` stays a `partial` class; `Core.cs` holds simulation/render
  orchestration, `Core.Menu.cs` holds the in-engine menu/catalog UI. New
  gameplay systems should be added as separate files, not appended to
  `Core.cs`.
- New player-facing objects must be registered through the existing
  spawn/catalog/UI paths (`PhysicObjectMenuGenerator.cs`), not hidden as
  one-off preset code.
- Physical shape and visual shape may differ, but the difference must be
  explicit and documented at the object's creation/drawing code.
- New user-facing features must be exposed consistently in the editor UI, the
  fullscreen player UI, help text and `README.md`/`PRESET_GUIDE.md` where
  applicable.

## Naming note

The repository, project files and root namespace use the project's original
name, `MakarovPhysicsSandbox`. The shipped assembly and product name are
`Wrecksmith` — the title the game is sold under on Steam.

## Known cleanup targets

- `Core.cs` is still large (~7000 lines); splitting it into per-subsystem
  partial files (input, spawning, triggers, rendering) is the next structural
  pass.
- `MakarovPhysicsSandbox.cs` still mixes menu creation, toolbar creation,
  player fullscreen UI, title overlay and form-level orchestration.
