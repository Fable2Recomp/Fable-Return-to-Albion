# refii-launcher
A cross-platform launcher for Fable 2 Recompiled.

Windows and Linux now share the same GLFW/ImGui launcher frontend, so layout and behavior changes live in one place.

This launcher now defaults to the `Fable2Recomp/refii` project layout. The packaged output is organized under a single top-level `assets` folder:

- `assets/game`
- `assets/save`
- `assets/update`
- `assets/launcher`

The `refii-launcher` binary now lives at the build root beside `refii`. Its `config.cfg`, `imgui.cfg`, and its own art/fonts live in `assets/launcher`, while the extracted game data stays in `assets/game` and `assets/update`.

## Source layout

- `include/core` contains the shared launcher headers.
- `include/core/utils` contains the shared utility headers.
- `include/installer` contains the shared installer headers and XISO extractor interface.
- `src/core` contains the shared launcher implementation and UI.
- `src/installer` contains the installer flow and bundled ISO extraction implementation.
- `src/windows` contains the Win32-only platform implementation.
- `src/linux` contains the Linux-only platform implementation.

## Features

- ISO extraction
- Lua workspace export for `gamescripts(.bnk)`, `gamescripts_r(.bnk)`, `guiscripts(.bnk)`, `scripts/`, and `scripts_r/`
- Local patch toggles and launch arguments
- Shared GLFW/ImGui launcher frontend on Windows and Linux
- Separate `refii-launcher` and `refii` binaries

## Supported CVAR Types

Currently supported:
- `REXCVAR_DEFINE_BOOL`
- `REXCVAR_DEFINE_INT32`

Support for additional CVAR types can be added as `refii` grows.

## Building from source

### CMake

Windows:

```bash
git submodule update --init --recursive
cmake -S src/launcher -B out/launcher-win -G Ninja
cmake --build out/launcher-win
```

Linux:

```bash
git submodule update --init --recursive
cmake -S src/launcher -B out/launcher-linux -G Ninja
cmake --build out/launcher-linux
```

Linux dependencies:

- `libcurl`
- OpenGL development headers
- X11 development headers/libraries
- `zlib` development headers/libraries

## Lua workspace

After the installer extracts game data, the Modding tab can build a mod-ready
Lua tree at `assets/unpacked`.

That workspace preserves the original data-relative paths, but rooted at
`unpacked/` instead of `data/`. For example:

- `data/gamescripts/scripts/Quests/QC030_BeetleHunter.lua`
- `unpacked/gamescripts/scripts/Quests/QC030_BeetleHunter.lua`

The launcher keeps a hidden extraction cache at `assets/unpacked/luasource`
so it can preserve the original bytecode while writing mod-ready files into the
public `unpacked/` tree.

The debug-rich `gamescripts/` bank is the best source for readable Lua workspace
output. `gamescripts_r/` contains the retail chunks the game actually runs, but
those scripts have much of their debug naming stripped.

The launcher decompiles into that `unpacked/` tree from the Modding tab by calling
the native Lua decompiler library after installation. That pass is
best-effort: known-good debug chunks are decompiled natively, retail aliases
are reused where possible, and bytecode that still defeats the source
decompiler is written as readable fallback disassembly under `unpacked/_disasm/`
instead of blocking installation.

The installer also creates `refii.toml` beside the executable if it is missing.
That runtime config enables Lua mod overrides by default while keeping the
toggle user-editable for parity checks.

If you already have extracted assets under `assets/game`, the launcher now also
offers `Unpack and Decompile Lua` and `Unpack Game Banks` in the Modding tab,
plus `Verify Lua Workspace`, `Reset All Lua Files`, and `Reset Lua File...`
actions so you can refresh, audit, or restore `unpacked/`
without running a full reinstall.
`Unpack Game Banks` mirrors non-BNK installed data files into `unpacked/` and
extracts all non-Lua BNKs that use the standard V2/V3 archive layout. Archive
files themselves are not kept in `unpacked/`; the originals remain under
`game/data`, and stale copied BNKs are removed on the next unpack. The Lua banks
stay owned by the Lua decompile action so the readable script workspace is not
overwritten.

To build or refresh that readable mirror manually, use:

```bash
cmake --build --preset linux-amd64-debug --target refii-lua-decompiler
./out/build/linux-amd64-debug/tools/refii-lua-decompiler build assets/game --workers 0
```

`--workers 0` means auto and uses the library-owned bounded worker pool.

To verify or reset the public plaintext workspace without regenerating it, use:

```bash
./out/build/linux-amd64-debug/tools/refii-lua-decompiler verify assets/unpacked
./out/build/linux-amd64-debug/tools/refii-lua-decompiler reset-file assets/unpacked gamescripts/scripts/quests/feats.lua
./out/build/linux-amd64-debug/tools/refii-lua-decompiler reset-all assets/unpacked
```

The emitted `_manifest.tsv` records which files were decompiled, aliased from
their debug counterpart, copied as plaintext, or written as `_disasm/`
fallbacks. The launcher also maintains `unpacked/.baseline/_hashes.tsv` plus
`unpacked/.baseline/files/` so public plaintext Lua can be verified and reset after
modding. Full reset restores baseline-tracked files and removes extra public
`.lua` files that were added later.

Linux and Windows now use the same bundled XISO extractor derived from `extract-xiso`, so no separate ISO extraction tool is required at runtime.

The launcher dependencies in `vendors/` now live as git submodules for `glfw`, `imgui`, and `stb`, while the generated OpenGL loader files stay in `generated/glad/`.

## Platform-specific config keys

The launcher supports optional per-platform overrides in `config.cfg`:

- `ExeNameWindows` / `ExeNameLinux`
- `ExeRelativePathWindows` / `ExeRelativePathLinux`
- `DownloadFileWindows` / `DownloadFileLinux`
- `GamePath`, `GameDataDirectory`, `UpdateDirectory`, and `GameMarkerFile`

By default `GamePath=assets/`, because the launcher binary now runs from the root output folder beside `refii` and points directly at the sibling `assets` directory for game content.
