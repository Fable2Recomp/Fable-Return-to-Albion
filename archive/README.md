# archive/

Snapshots of work that has been **disabled in the build but not deleted**, taken at
`98ba332d` on 2026-08-18.

Godot is the shell now: it drives display, hosts the guest in-process and owns the UI. The
three things here belong to the layout before that, and each was switched off rather than
carried as a build cost nobody pays for. Nothing depends on them.

| zip | what it is | why it was retired |
|---|---|---|
| `imgui-launcher-*.zip` | `src/launcher` — the GLFW + ImGui launcher executable | replaced by the Godot launcher in `Godot/src/launcher` |
| `renderer-*.zip` | `src/renderer` — the D3D9/GTL renderer and its Plume backends | Godot renders; the guest never draws. Already `OFF` by default, and 86% of the build |
| `appimage-flatpak-*.zip` | the AppImage/Flatpak CMake logic and `refii.desktop` | packaged the standalone `refii` exe and the ImGui launcher, so it could not outlive them |
| `shader-tools-*.zip` | `src/shader` — the `.sbk` shader extractor and Xenos translator, with a standalone CMake root and README (taken at `d5b604b7f`, 2026-10-03; includes the vendored zlib, but not the DXC binaries, which are fetched at configure time) | handed to someone outside the project who is pursuing the translated-shader route; refii does not use it |

## What is NOT in the zips, and why

- **Vendored submodules.** `src/launcher/vendors` (glfw, imgui, stb) and
  `src/renderer/thirdparty` (Plume, Vulkan-Headers, D3D12MemoryAllocator, …) are git
  submodules pinned by commit — 266 MB and 21 MB respectively. `git submodule update`
  restores them exactly; copying them here would bloat the repo for nothing.
- **`appimagetool-x86_64/aarch64.AppImage`.** Prebuilt third-party tools, ~28 MB, still
  tracked at `packaging/appimage/`.

The zips are built from `git ls-files`, so they contain the committed state and nothing
untracked or generated.

## These are a convenience, not the record

The source is still in the tree and still in git history. If a directory is deleted later,
the zip is the standing snapshot; until then `git show 98ba332d:<path>` is equally good.

## Turning any of it back on

The build flags are the whole switch — the sources were left in place:

```
-DREFII_BUILD_LAUNCHER=ON      # the ImGui launcher
-DREFII_BUILD_STANDALONE=ON    # the standalone `refii` executable
-DREFII_BUILD_RENDERER_HOOKS=ON
-DREFII_BUILD_APPIMAGE=ON      # needs BOTH of the first two
```

`REFII_BUILD_APPIMAGE` fails configure with a `FATAL_ERROR` unless `refii` exists and
`REFII_BUILD_LAUNCHER` is ON — turn those on with it, not instead of it.
