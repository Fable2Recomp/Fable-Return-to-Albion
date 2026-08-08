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
| `ui_export` | Converts every GUI screen and texture; prints the pass/fail counts that gate UI work. |
| `ui_viewer` | Renders one converted GUI screen. |
| `host_check` | Is the native host bridge present and at the expected ABI? |
| `host_boot` | Does the guest boot in-process, running its own loop? |
| `host_driven` | Does the guest run with GODOT driving it a slice per frame? The plan's "refii as a library". |
| `game_session` | Runs a `GameSession` without the launcher — asset mount plus guest, no window management. |
| `game_window` | Asserts the game opens as a REAL OS window, sized from `refii.toml`, that the launcher minimises behind it, and that closing it restores the launcher without taking the process down. Checks `Window.IsEmbedded()` and the toml values rather than trusting the eye — an embedded subwindow looks identical in a screenshot, and a hardcoded 1280x720 assertion passes whether or not the file is read at all. |

## Environment

`REFII_GAME_DATA` overrides where game data is found; otherwise it resolves by convention
(`assets/game/data` relative to the app, then dev build outputs).

`model_viewer` also takes:

- `REFII_MODEL` — name or path fragment; an exact file name wins over a substring
- `REFII_MODEL_SHOT` — write a PNG here and quit, so a render is checkable without a human
- `REFII_MODEL_ZOOM` — multiplier on the framed distance (`0.15` to inspect surface detail)
- `REFII_MAT_MODE` — `Full`, `NoNormal`, or `Albedo`; isolates a lighting fault from a
  texture-decode fault, which otherwise look alike

`tex_mip_probe` takes:

- `REFII_TEX` — path fragment naming the texture; first match wins
- `REFII_TEX_DUMP` — output directory (required, and must be outside the repo)
- `REFII_TEX_LIST=1` — list matches with their format and level counts instead of dumping
- `REFII_TEX_VALIDATE=1` — for a texture shipping a full chain, generate each level from
  the one above it and write it beside the artist's own, so the encoder can be checked
  against real data rather than against an opinion

Texture decoding has two switches, and they answer different questions:

- `REFII_TEX_NO_MIP_TAIL=1` — keep the levels the file ships, drop the synthesised tail
- `REFII_TEX_NO_MIPS=1` — no mipmaps at all, shipped levels included

Together they separate "mipmapping is involved" from "the levels we generated are wrong".
That distinction is what found the actual bug behind green fringing on the Crucible: not
the tail, but raw (`comp=7`) shipped levels being copied without untiling.

`game_window` takes `REFII_GAME_WINDOW_GUEST=1` to run the REAL guest. That distinction
matters: with the guest off the session's shutdown is a no-op, so a guest-less run cannot
answer the question the check exists for — whether tearing the guest down takes the host
process with it. It did, twice, in two different ways.

`host_boot` and `host_driven` require `REFII_HOST_BOOT=1`.

**Screenshots must be written outside the repository and any build output.** A render of
game geometry is derived game data and is not redistributable.
