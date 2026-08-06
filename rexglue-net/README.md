# rexglue-net — netplay SDK integration workspace

**Status 2026-07-19: the shadow rebase described below is DONE.** `shadow/`
now targets the netplay SDK; select this tree with
`-DREFII_REXGLUE_NETPLAY=ON` (see "Building against this tree" below).
Highlights of the rebase:

- The 6 colliding shadow files were 3-way merged (RexNet options/overlay/
  session bridges all preserved).
- Our Xenia-derived SDL windowing shadow files (`window_sdl.*`,
  `windowed_app_context_sdl.*`) were dropped — the netplay SDK ships its own
  rewritten SDL3 windowing. The external-shutdown machinery from
  `windowed_app_main_posix/win.cpp` was ported into a shadowed
  `src/ui/windowed_app_main_sdl.cpp` (those two per-platform mains no longer
  exist upstream).
- SDK achievements are disabled in this tree's shadow: `rex_app.cpp`'s
  `CreateAchievementsOverlay()`/`CreateAchievementNotificationDialog()`
  return nullptr, and the shadowed `xgi_app.cpp` routes
  `XGIUserWriteAchievements` into refii's own achievement runtime instead of
  the SDK `AchievementManager` (refii ships its own system in
  `src/achievement/`).

Same layout as `rexglue/`, but carrying the **netplay** SDK so the Fable 2
shadow changes can be adapted against it without touching the working
v0.8.0 setup:

- `rexglue-sdk/` — **git submodule**: `https://github.com/Loreaxe/rexglue-sdk.git`,
  branch `netplay`, pinned at `c339752` (2026-08-06). Nothing here is
  hand-edited; the working tree matches the branch byte for byte, so an SDK
  fix belongs upstream and arrives via `git submodule update --remote`.
  Includes the complete RexNet module: `src/rexnet/` (C++ shim + `core/`
  Rust crate), `include/rex/net/`, kernel/XSocket/XGI/XLiveBase integration,
  the F6 overlay, and the vendored `corrosion` submodule. Module status:
  `rexglue-sdk/src/rexnet/README.md`; design RFC:
  `rexglue-sdk/docs/rexnet-design-spec.md`.
- `shadow/` — refii's overlay on that SDK, tracked in refii. 63 files.

  **A shadow file identical to its SDK counterpart is not necessarily
  redundant.** `refii_rexglue_replace_target_source()` compiles a shadowed
  `.cpp` *from its shadow path*, so its `#include "../..."` lines resolve
  inside `shadow/`, not inside the SDK. The 24 prebuilt shader headers under
  `src/ui/shaders/` exist solely to satisfy the relative includes in the
  shadowed `src/ui/vulkan/vulkan_presenter.cpp`; deleting them as "no-op
  overlays" on 2026-08-06 broke the build with `fatal error:
  '../shaders/vulkan_spirv/guest_output_bilinear_dither_ps.h' file not
  found`. Judge a shadow file by whether the shadow tree still compiles
  without it, never by diffing it against the SDK.

## Shadow files that overlay netplay-modified files

A shadow file is a **whole-file substitution**, so one authored against an
older point on the branch silently reverts every upstream change to the file
it covers. Twelve shadow files were in that state when the SDK was pinned at
`c339752` on 2026-08-06; all twelve were rebased that day and now carry
upstream's work plus our delta:

| Shadow file | Our delta vs `c339752` | Upstream work now preserved |
|---|---|---|
| `src/audio/xma_context.cpp` | +8/-0 | `a7638a5` flush ffmpeg buffers on context release; `e5c3554` XMA loop-wrap garbage + dropped loop-end frame |
| `CMakeLists.txt` | +24/-0 | `db97de5` GNS drop; `a3b9a6c` RexNet behind a Rust toolchain gate |
| `include/rex/rex_app.h` | +18/-0 | `a3b9a6c` toolchain gate; `4944683` file headers |
| `src/ui/CMakeLists.txt` | +19/-1 | `a3b9a6c` toolchain gate |
| `src/ui/rex_app.cpp` | +257/-27 | `a3b9a6c` toolchain gate; `4944683` file headers |
| `include/rex/ui/window.h` | +21/-5 | `6c1c66a` per-window cursor auto-hide delay |
| `src/kernel/xam/apps/xgi_app.cpp` | +66/-22 | `aa6f90a` clang-format; `4944683` file headers |
| `src/kernel/xam/xam_notify.cpp` | +51/-0 | `aa6f90a` clang-format; `4944683` file headers |
| `src/kernel/xam/xam_user.cpp` | +13/-0 | `aa6f90a` clang-format; `4944683` file headers |
| `src/ui/imgui_drawer.cpp` | +56/-14 | `4944683` file headers |
| `src/system/xsocket.cpp`, `src/kernel/xam/xam_net.cpp` | none | upstream absorbed our version verbatim |

The rebase was a `git merge-file` 3-way against each file's original base
(`784d804`, `8fb48f2`, `b1959f5`, `29eaa8a`). Nine merged clean. `xgi_app.cpp`
conflicted twice, in `XSessionWriteStats` and `XSessionFlushStats`: upstream
only reformatted the length check, while our side replaced it with a
`buffer_length >= 20` guard (an exact-length assert had killed the process
during a co-op session). Ours was kept — it prevents reads past a short
buffer; upstream's reformatting of the rest of the file was preserved.

**When the pin moves, redo this.** `git diff <old-pin>..<new-pin> -- <path>`
over the shadowed paths shows exactly what a stale overlay would throw away.

## Building against this tree

One switch selects the tree (it repoints the SDK/shadow cache vars, defaults
`REXGLUE_ENABLE_REXNET=ON`, and uses the SDL entry point):

```
cmake ... -DREFII_REXGLUE_NETPLAY=ON
```

Flipping the switch on an existing build dir is supported — the shadow
overlay is wiped and rebuilt from the newly selected tree. The manual cache
vars still work for pointing at a non-bundled tree:

```
cmake ... \
  -DREFII_REXGLUE_SDK_SOURCE_DIR="$PWD/rexglue-net/rexglue-sdk" \
  -DREFII_REXGLUE_SHADOW_DIR="$PWD/rexglue-net/shadow/rexglue-sdk" \
  -DREXGLUE_ENABLE_REXNET=ON
```

Requirements/caveats:

- RexNet needs a Rust toolchain (cargo; built via corrosion) and the SDK
  needs clang 20 on this machine (`clang++` resolves to 18, which cannot
  compile the SDK's `std::expected` usage — pass
  `-DCMAKE_C_COMPILER=clang-20 -DCMAKE_CXX_COMPILER=clang++-20`).
- The SDK's own `thirdparty/*` submodules are populated on disk but not
  initialised as repos (their dangling `.git` pointer files were deleted on
  2026-08-06 — they broke `git status` in refii outright). The SDK's
  REQUIRED_SUBMODULES presence check still passes because
  `refii_rexglue_prepare_shadow_tree()` synthesises a `.git` marker in the
  build copy for each expected submodule dir
  (`cmake/refii_rexglue.cmake`). Consequence: those thirdparty trees are
  whatever is on disk, unverified against the pins — a fresh clone must run
  `git submodule update --init --recursive` inside the SDK to match.
- Per-game config goes in the title metadata dir as `rexnet.toml` (see
  `rexglue-sdk/src/rexnet/README.md` and the spec's §11.1).

Canonical source of truth is the `netplay` branch of
`https://github.com/Loreaxe/rexglue-sdk`. SDK-side fixes belong there and
arrive here by moving the submodule pin — never by editing `rexglue-sdk/`
in place.
