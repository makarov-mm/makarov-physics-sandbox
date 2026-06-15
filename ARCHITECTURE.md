# Architecture notes

This project is intentionally kept as a custom C#/.NET + WinForms/OpenGL sandbox rather than a Unity/Unreal/Bullet/PhysX project. The codebase is now large enough that feature work must follow a stricter layout rule.

## Source layout rule

- One data type per file where practical.
- `GlPanel` may stay a `partial` class for now, but each subsystem must live in a named partial file.
- New gameplay systems should not be added directly to `Core.cs`.
- New player-facing objects should be registered through the existing spawn/catalog/UI paths rather than as one-off code hidden in presets.
- Physical shape and visual shape may differ, but that difference should be explicit and documented in the object creation/drawing code.
- New user-facing features must be exposed consistently in Dev UI, Player fullscreen UI, help text and README/PRESET_GUIDE when applicable.

## Current `GlPanel` split

`Core.cs` now contains the shared fields, constructor, lifecycle, OpenGL context setup, render-frame orchestration and graphics initialization.

The behavioral parts are split into partial files:

- `Core/GlPanel.PlayerActions.cs` — public methods called by menus, toolbars, fullscreen player UI and property panels.
- `Core/GlPanel.Input.cs` — mouse and keyboard dispatch.
- `Core/GlPanel.PresetsChallenges.cs` — scene reset, presets, challenge and campaign scene builders.
- `Core/GlPanel.SpawnActions.cs` — object placement, spawn helpers and pending scene actions.
- `Core/GlPanel.Triggers.cs` — trigger evaluation, graph outputs and triggered actions.
- `Core/GlPanel.EffectsAudio.cs` — material reactions, particles, beams and audio feedback.
- `Core/GlPanel.ToolsSelectionCamera.cs` — sandbox tools, water/fields, selection, editor transforms and camera picking.
- `Core/GlPanel.Rendering.cs` — shadow pass, main pass, skybox, overlays, textures and draw helpers.

Small internal data types that belong to `GlPanel` are also split into separate files:

- `Core/Particle.cs`
- `Core/Beam.cs`
- `Core/VehicleRig.cs`
- `Core/SceneMechanism.cs`

## Campaign split

Campaign/challenge data was split into `Campaign/`:

- `ChallengeKind.cs`
- `ChallengeResult.cs`
- `LevelDef.cs`
- `LevelCatalog.cs`
- `CampaignProgress.cs`

This keeps campaign state independent from the main OpenGL control.

## Material split

Material data was split so each type has a single home:

- `Materials/MaterialId.cs`
- `Materials/MaterialDefinition.cs`
- `Materials.cs` for the static material registry.

## Known remaining cleanup targets

The project is not finished architecturally. The next cleanup candidates are:

1. `MakarovPhysicsSandbox.cs` — still mixes menu creation, toolbar creation, player fullscreen UI, title overlay and form-level orchestration.
2. `Mechanisms.cs` — still contains mechanism runtime, drawing, spawning and preset helper logic in one partial file.
3. `Textures.cs` — should be split into texture loading, procedural fallback generation and texture registry/lookup.
4. `Audio.cs` — should be split into public sound API and WinMM interop/private voice types.
5. `Ragdoll.cs` — should eventually split bone data, pose muscles, ragdoll instance and ragdoll system.
6. Object identity should gradually move away from string `Tag` checks toward an explicit `ObjectKind` field/registry.

The current cleanup is meant to reduce risk before more gameplay/UI work, not to create a perfect architecture in one pass.
