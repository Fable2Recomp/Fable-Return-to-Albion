# Fable 2 Remake: Return To Albion

This project is still in active development. Join the Discord for updates, or to help out!

**Return To Albion** is a native PC remake of [Fable 2](https://en.wikipedia.org/wiki/Fable_II)
for Windows and Linux. It began as a static recompilation, and the recompiled game is still at
its core: the original game code is recompiled with [ReXGlue](https://github.com/rexglue/rexglue-sdk)
and runs the simulation, so quests, AI, combat and the world behave exactly as they did on the
Xbox 360. Everything around it is new. The game runs inside [Godot](https://godotengine.org),
which rebuilds the world natively from your own game files and renders it, and which owns the
interface, the menus, audio and input.

Return To Albion ships no game data. You need your own copy of Fable 2. The launcher recompiles
the game code from it on your machine, and reads the game's asset banks in place.

This remake is currently based on [Fable 2](https://en.wikipedia.org/wiki/Fable_II) GOTY, title
ID `4D5307F1`, in these pressings:

| Pressing | Media ID | Title update |
|---|---|---|
| USA / Europe | `716F0A0D` | `TU_16L61VH_0000038000000.000000000008Q` or none |
| Russia | `05FBECEA` | `TU_16L61VH_0000044000000.0000000000091` or none |
| France | `1358B1A4` | none yet |

Other Fable 2 pressings have different media IDs, and each has its own title update. The launcher
checks both files before it copies anything and refuses any disc or title update other than the
pairs above. This restriction will be eased as we eventually support more versions closer to
release.

## Features

- **The original game, running natively.** The recompiled game code drives the simulation. It is
  not emulated.
- **A modern renderer.** Godot draws the world at your display's native resolution, with no
  upscaling, and the frame rate is independent of the game's simulation rate. The target is
  1440p at 120 fps.
- **Modernized lighting and effects.** Placed lights, fog, water, particles and day-set themes
  are rebuilt on Godot's lighting model, and are on by default.
- **Co-op, local and online.** Local co-op works with two controllers, and online co-op runs over
  the RexNet netplay module.
- **Launcher and installer.** Point the launcher at your Fable 2 ISO and title update, and it
  handles the rest.
- **Modding.** The editor and asset browser open the game's own banks, and the release package
  includes a modding SDK.
- **Windows and Linux**, on x86_64. Windows ARM64 is also built.

## Building Return To Albion

The ReXGlue SDK is bundled as a submodule (`rexglue-net/rexglue-sdk`, the `netplay` branch of
[Loreaxe/rexglue-sdk](https://github.com/Loreaxe/rexglue-sdk)). You don't install it separately
or set a `REXSDK` variable.

### Prerequisites

- Clang 20: `clang-20`/`clang++-20` on Linux (the presets name them), `clang`/`clang++` on Windows
- CMake 3.25+
- Ninja
- Rust (`cargo` and `rustc` 1.88+) for the RexNet netplay module, e.g. via [rustup](https://rustup.rs)
- .NET 8 SDK, because the Godot app is written in C#
- Git

The build fetches the rest itself and checks each download's hash. Godot 4.7.1 (.NET) with its
export template and Zig go to `tools/` the first time you package. On Windows,
[vcpkg](https://github.com/microsoft/vcpkg) is cloned and bootstrapped into `tools/vcpkg` unless
`VCPKG_ROOT` already points at one.

### Clone

```bash
git clone --recursive https://github.com/Fable2Recomp/Fable2Recomp.git
cd Fable2Recomp
```

If you cloned without `--recursive`, run `git submodule update --init --recursive`.

### Configure and build

```bash
cmake --preset linux-amd64-debug-netplay
cmake --build --preset linux-amd64-debug-netplay
```

Available presets:

| Platform | Presets |
|---|---|
| Linux x86_64 | `linux-amd64-{debug,release,relwithdebinfo}-netplay` |
| Windows x86_64 | `win-amd64-{debug,release,relwithdebinfo}-netplay` |
| Windows ARM64 | `win-arm64-{debug,release,relwithdebinfo}-netplay` |

Outputs:

- `out/build/<preset>/bin`: the native build
- `Godot/build/<arch>/`: the runtime and native libraries, staged for Godot
- `out/package/refii-<platform>-<arch>.zip`: the release package. A normal build exports and
  zips it. Configure with `-DREFII_PACKAGE=OFF` to skip that step while iterating on code.

### Game code (no codegen step)

You don't run `rexglue codegen` and you don't put game files in the repo. The launcher recompiles
the game code on first install, from the player's own copy of the game. The player selects the
Fable 2 ISO and its title update (`default.xexp` or the TU package). The launcher then runs codegen
and compiles the result with the bundled Zig toolchain. Nothing derived from the game is built
into or shipped with this repository.

## Discord presence
With Discord running, the game shows as "Playing Fable 2: Return to Albion" with the level you
are in (Bowerstone Market, Rookridge). Nothing to set up; `discord_presence = false` in
`refii.toml` turns it off.

## Contributing
We welcome contributions to Return To Albion! We're still learning as we go, and we're happy to
learn from anyone willing to teach. If you have experience, or are willing to learn and help out,
your support would be greatly appreciated. Feel free to open a PR.

Join the Discord server at https://discord.gg/zVVtGbEsCR
