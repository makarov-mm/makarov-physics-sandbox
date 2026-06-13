# Makarov Physics Sandbox

A small WinForms/OpenGL physics sandbox written in C# without third-party physics or rendering libraries.

## Controls

Mouse:

- Left mouse: grab and drag an object.
- Right mouse + move: rotate the camera.
- Mouse wheel: zoom in/out.
- Middle mouse: shoot a ball.

Toolbar / menu:

- Object buttons, bowling pins, chain, explosion, attractor and repeller are placement tools.
- Click the toolbar/menu item first, then click inside the scene to place the object/effect/field.
- Press `Esc` to cancel a pending placement.
- Gravity, water, wind, pause, slow motion, step, clear and reset act immediately.

Keyboard:

- `1`-`8`: spawn objects.
- `9`: bowling pins.
- `L`: chain.
- `Space` or `F`: shoot a ball.
- `E`: explosion.
- `Z`: attractor.
- `X`: repeller.
- `U`: wind.
- `V`: water.
- `G`: gravity on/off.
- `P`: pause.
- `T`: slow motion.
- `B`: single physics step.
- `C`: clear dynamic objects.
- `R`: reset scene.
- `H`: keyboard help.
- `Esc`: cancel pending toolbar/menu placement.

Keyboard actions use the current aim point under the cursor marker. Toolbar/menu placement tools no longer use the cursor position while it is over the toolbar; they wait for the next scene click.
