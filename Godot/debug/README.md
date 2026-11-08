# Debug scenes

Diagnostic harnesses. None of these ship or are referenced by the app — `main.tscn` stays
at the project root because it is the entry point; everything here exists to answer a
question about the data or the runtime.

The game itself is NOT a scene. The launcher opens it as a real OS window
(`Refii.Game.GameWindow`), so there is nothing at the root to load it with; the scene here
runs a `GameSession` on its own, without the launcher, for testing that half in isolation.

Run one with:

```
godot --path Godot res://debug/<name>.tscn
```

Add `--headless` for anything that does not need to draw. The two viewers do.

| Scene | What it answers |
| --- | --- |
| `mdl_survey` | Do the models parse, and how much geometry comes out? Whole asset set, one number per outcome. Catches silent truncation. |
| `model_viewer` | Does the geometry LOOK right? Orbit + step through models; the only instrument that catches mirroring, bad winding, or wrong materials. |
| `mip_cost` | What does completing the short mip chains cost? Times tail generation against the decode already being paid. |
| `tex_mip_probe` | Dumps one texture's levels — shipped and synthesised — as PNGs. This is what separates a bad seed from a bad downsample from a bad encode; each of those looks identical on a model. |
| `fx_check` | Does the particle data reach the effects, and where does the chain break? Bank parse, per-route resolution counts, how many survive the one-shot and dedup gates, and whether the textures exist. Replaces the keyword table that used to make a region look full of fires whether or not a binding had ever been read. |
| `ui_export` | Converts every GUI screen and texture; prints the pass/fail counts that gate UI work. |
| `ui_viewer` | Renders one converted GUI screen. |
| `front_end_menu` | Does the Godot-owned front end LOOK and FEEL like the original? Drives the row menu and the boy/girl gender card with no guest. The slot bow, the auto-repeat curve and the card poses are all read out of shipped data and all look plausible when wrong, so a still is not enough — this is the only place they can be watched moving. |
| `locomotion_check` | Does the hero locomotion state machine read correctly out of the shipped Lua, and does it move? Asserts the values and defaults against the binary's own reader, runs a speed ramp through the machine, and measures how many of the states' clip names the animation catalogue can actually resolve. |
| `locomotion_live` | Does the LIVE guest publish the locomotion state we think it does? Boots the guest, starts a game, and checks every published state name against the 41 the shipped script defines — a wrong pointer hop matches none of them. |
| `anim_timing_lab` | Does a limb move as evenly as the body carrying it, and do they move together? The animation timing path — the draw point, the clip playhead, the pose ring and its stamps, the body timeline — run against a simulated guest whose step and read timing are the real one's (placements read at the top of the frame, rigs late in it), at 45-165 Hz with jitter and hitches. Prints, per series, how evenly it advances frame to frame (1.000 with no holds or jumps is smooth) and the body-against-clip skate in milliseconds. Headless; pass `--log-file` to a scratch path, or it rotates the game's own logs. `REFII_LAB_LOOP=<seconds>` plays a looping clip; `REFII_LAB_CSV=/abs/file.csv` (with `REFII_LAB_SCENARIO=<name>`) writes every frame for plotting. |
| `attach_probe` | Where do a character's carry dummies sit? For every `Character.Carry*` attachment in the hero's part gmds: its node, the file's transform (model space), the node's bind, and the transform relative to the node that the attachment placer composes under the bone. Sheath and hand dummies should come out at the identity. Headless; pass `--log-file` to a scratch path. |
| `host_check` | Is the native host bridge present and at the expected ABI? |
| `host_boot` | Does the guest boot in-process, running its own loop? |
| `host_driven` | Does the guest run with GODOT driving it a slice per frame? The plan's "refii as a library". |
| `game_session` | Runs a `GameSession` without the launcher — asset mount plus guest, no window management. |
| `game_window` | Asserts the game opens as a REAL OS window, sized from `refii.toml`, that the launcher minimises behind it, and that closing it restores the launcher without taking the process down. Checks `Window.IsEmbedded()` and the toml values rather than trusting the eye — an embedded subwindow looks identical in a screenshot, and a hardcoded 1280x720 assertion passes whether or not the file is read at all. |
| `shadow_bake` | Bakes octahedral static shadow depth atlases (`.shadows`) for all levels and all scenarios into `Godot/assets/ShadowMaps/`. Headless (`-- --resolution <int> --level <filter>` or `REFII_SHADOW_RES` / `REFII_SHADOW_LEVEL`). |

## Environment

`REFII_GAME_DATA` overrides where game data is found; otherwise it resolves by convention
(`assets/game/data` relative to the app, then dev build outputs).

`model_viewer` also takes:

- `REFII_MODEL` — name or path fragment; an exact file name wins over a substring
- `REFII_MODEL_SHOT` — write a PNG here and quit, so a render is checkable without a human
- `REFII_MODEL_ZOOM` — multiplier on the framed distance (`0.15` to inspect surface detail)
- `REFII_MAT_MODE` — `Full`, `NoNormal`, or `Albedo`; isolates a lighting fault from a
  texture-decode fault, which otherwise look alike

`front_end_menu` takes:

- `REFII_MENU_SCREEN` — a presentation to stand up behind the menu. Set it to
  `frontendmainmenu` to source the row frames and the boy/girl cards from the real front end;
  unset, the menu is labels only and the card draws stand-ins
- `REFII_MENU_OPEN=card` — open the gender card immediately, so a capture run does not have to
  synthesise a keypress to reach it
- `REFII_MENU_OPEN=saves` — open the Load Game card fan instead. The game opens it on the
  guest reaching substate 5, which is a state no harness can reach, so the keypress stands in
- `REFII_MENU_SAVES=<n>` — how many stand-in saves are on the fan, three by default. Worth
  sweeping 2 through 6: the fan is CENTRED by a base slot that changes at 2 and at 5, and a
  short list laid out left-aligned is the failure this exists to catch
- `REFII_MENU_SAVE_NAV=<n>` — step the fan's cursor right n times before the capture, so a
  still can show which card drops out of the row. The selected card is the one that does NOT
  lift, so a capture of the default cursor cannot tell a working panel from an inverted one
- `REFII_MENU_SHOT` — write the viewport here and quit (outside the repository; a render of the
  game's card art is derived data)

`ui_viewer` also takes:

- `REFII_UI_SCREEN` — which of the 52 screens to stand up; `REFII_UI_LIST=1` names them all
- `REFII_UI_SLIDES=1` — every owning element's slides and the attribute writes each one makes.
  This is how a panel's real layout is read: the artist's authored transform is only a starting
  frame, and the slides state where things actually go
- `REFII_UI_ATTRS=<name>` — every element carrying that attribute, and its value.
  `REFII_UI_ATTRS='*'` dumps all 41,659 of them. Set it to `MeterName` on `hud` to see how the
  in-game HUD binds its live values: `Bar` carries `MeterName "PlayerHealth"`, and the fill
  under it is authored `scale.x = 0` because the runtime is what fills it
- `REFII_UI_BINDINGS=1` — the same thing as a table, through `Engine_UiBindings`, with the
  FNV-1 hash each name is published under. `hud` has 113 bound elements; the front-end screens
  have none, which is correct for them rather than a failure

`tex_mip_probe` takes:

- `REFII_TEX` — path fragment naming the texture; first match wins
- `REFII_TEX_DUMP` — output directory (required, and must be outside the repo)
- `REFII_TEX_LIST=1` — list matches with their format and level counts instead of dumping
- `REFII_TEX_VALIDATE=1` — for a texture shipping a full chain, generate each level from
  the one above it and write it beside the artist's own, so the encoder can be checked
  against real data rather than against an opinion

Texture decoding has one switch:

- `REFII_TEX_NO_MIPS=1` — no mipmaps at all, shipped levels included

It separates "mipmapping is involved" from everything else.
That distinction is what found the actual bug behind green fringing on the Crucible: not
the tail, but raw (`comp=7`) shipped levels being copied without untiling.

`level_viewer` also takes:

- `REFII_LEVEL` — level name; `REFII_LEVEL_TOD` moves the time of day after the region stands
- `REFII_LEVEL_LIGHT` — `direct` or `ambient`, to isolate the sun from the baked term
- `REFII_LEVEL_FOG=off` — holds the atmosphere off. The THIRD term, and without it the other two
  isolations can agree and still be answering nothing: where in-scatter supplies most of a frame's
  luminance, `direct` and `ambient` report the same histogram as each other and as the full frame,
  which reads as "neither light term matters" rather than as "something else is the picture". Held
  clear every frame, because the sky node republishes the global from the theme
- `REFII_LEVEL_PITCH=<deg>` — camera pitch. The sky's share of the frame is a metering choice: a
  histogram taken over a frame that is mostly dome describes the dome
- `REFII_EXPOSURE_DUMP=<frames>` — the exposure state AND the sun values, every n frames. This is
  the pair that separates the two faults a wrong-looking region can have, and they are on one
  switch because either alone is unreadable. The dump prints the histogram, the adapted triple,
  the derived curve terms, and the **clip point** — the scene luminance at which the tone curve
  reaches 1.0. Held against the frame's own p99 that settles it: a clip point BELOW p99 means the
  curve is throwing away the top of the frame, one well ABOVE it means nothing is clipping and a
  dark image is dark on arrival, which is a question about the sun term printed beside it.
  It reads back from the GPU and stalls the device, so it is off unless asked for — the shipping
  path never crosses the bus. Give it a few hundred frames: the scalar blends at 5% per frame and
  a single early sample reads as settled without being settled.

`game_window` takes `REFII_GAME_WINDOW_GUEST=1` to run the REAL guest. That distinction
matters: with the guest off the session's shutdown is a no-op, so a guest-less run cannot
answer the question the check exists for — whether tearing the guest down takes the host
process with it. It did, twice, in two different ways.

`host_boot` and `host_driven` require `REFII_HOST_BOOT=1`.

**Screenshots must be written outside the repository and any build output.** A render of
game geometry is derived game data and is not redistributable.

Host-side character animation is opt-in with `REFII_HOST_ANIM=1`. It REPLACES the guest bone
feed for rigged characters rather than adding to it — both write every bone of the same
skeleton, so running them together tears the rig instead of blending. Off means Godot copies
the guest's bones (the long-standing behaviour); on means Godot plays the guest's clips.

`ui_viewer` can also drive a screen's live values with no guest, which is the only way to see
whether a bar is a bar:

- `REFII_UI_METERS=50` — every bound meter at 50%. `REFII_UI_METERS=PlayerHealth=35,PlayerHealthMax=100`
  names them individually, which is the form that catches a bar driven by the wrong neighbour:
  the two health bars sit on top of each other and look identical when given the same value
- `REFII_UI_TEXTS=1` — writes each bound text field's own NAME into it, so a field that resolves
  but never draws is told apart from one that was never bound

It prints each driven bar's local scale, its INHERITED scale and the basis column lengths of
every ancestor. That last column is the one that matters: a bar piece is authored `scale.x = 0`,
so its basis is singular, and `Node3D.Scale` reads back `(0,0,0)` on every axis for it — the
scale getter multiplies by `SIGN(determinant)`. A fill can therefore report the exact percentage
asked for and still draw nothing.
