# Makarov Physics Sandbox

A WinForms + OpenGL physics sandbox in pure C#.

Current feature set includes:

- English menu/toolbar UI.
- Visual toolbar tools and keyboard shortcuts.
- Scene placement tools with preview for attractor, repeller, wind and explosion.
- Wavy water surface with buoyancy.
- Force-field VFX for attractor, repeller and wind.
- Object connection, disconnection and spring links.
- Save/load scene support via `.mpscene` JSON files.
- Object selection and property editing.
- Material presets: Wood, Metal, Rubber, Glass, Stone, Foam, Ice and Plastic.
- Scene presets.
- Challenges/goals.
- Breakable objects.
- Basic sound and particle feedback.
- Trigger plates / sensors.
- Editor tool modes and a simple object gizmo.

Material presets are not hard engine types yet. They are convenient UI presets that apply density, friction, bounciness, color and breakability to the selected object. Scenes still save the resulting physical values, so older files remain compatible.

Polish pass:

- Darker application chrome for menus, toolbar, status bar and property panels.
- Application title, maximized startup window and minimum usable window size.
- F4 toggles the right property panel.
- F11 toggles fullscreen mode.
- Ctrl+S and Ctrl+O are handled globally for save/load.
- Ctrl+D duplicates the selected object.
- Help text and status text were shortened and cleaned up for the current editor workflow.
