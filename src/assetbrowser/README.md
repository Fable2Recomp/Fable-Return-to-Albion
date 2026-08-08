# Refii Asset Browser SDK Layout

Same convention as `rexglue/`: keep the upstream project close to pristine while
allowing Refii-only additions.

- `assetbrowser-sdk/` is a SUBMODULE of the upstream Fable2AssetBrowser tree
  (https://github.com/adarec1994/Fable2AssetBrowser — same author; the two projects
  are merging into one app). Its sources live under `source/src`, which is why the
  build points there rather than at the SDK root: the merge originally vendored a
  flattened, pruned copy with that level removed, and a submodule tracks upstream's
  real layout instead of a snapshot of it.

  Clone with `--recurse-submodules`, or run `git submodule update --init` afterwards.
- `shadow/assetbrowser-sdk/` contains Refii-only files. **Edit here, never in the
  SDK tree**, so upstream can be updated without losing local work.

`CMakeLists.txt` builds `refii_assetbrowser`, a shared library exposing the browser's
format code to the Godot C# side by P/Invoke — the same shape as
`refii_bnk_extractor`. The upstream `CMakeLists.txt` builds the ImGui application and
is not used; that UI is being migrated to Godot exactly as the launcher was.

## What is built

The 20 translation units the `.mdl` -> glTF converter actually needs: the MDL
converter and parser, TexParser, LhTexCodec, the DDS/TIFF/PNG/JPG writers, the
animation system, ISO mounting and BNKCore. Deliberately NOT every non-UI source —
compiling that pulls in ImGui, `Quest::Bp::Register*Nodes` and `Level::Creation::*`,
none of which the converter touches.

## The four app seams

`shadow/assetbrowser-sdk/abi/coreshim.cpp` defines exactly four symbols the core
reaches for but the app owns. Each becomes an injected input once the UI is Godot's:

| symbol | why |
|---|---|
| `S` | the browser's global `State`. Accessed as `S.field`, so grepping `State::` finds nothing |
| `g_level_vfs_texture_body_bnks`, `decode_tex_to_rgba` | texture embedding — refii decodes `.tex` in C#, and the GLB carries texture NAMES |
| `OutputLog::log` | UI log panel (54 ImGui references) |
| `read_lua_file_content` | app Lua panel |

Before adding a stub here, check the symbol is not simply a source that belongs in
`CMakeLists.txt` — several "missing" symbols turned out to be real files.

## Building

Standalone (fast — no PPC recompile, renderer or shaders):

    cmake -S src/assetbrowser -B out/build/assetbrowser -DCMAKE_BUILD_TYPE=Release
    cmake --build out/build/assetbrowser -j

Or as part of the top-level build, where it is an `add_subdirectory` after
`src/extractor`.

## Game data

The banks live under the staged build output:

    out/build/linux-amd64-debug/bin/assets/game/data
