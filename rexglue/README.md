# Refii ReXGlue SDK Layout

This folder keeps Refii's ReXGlue integration close to upstream while still
allowing small local fixes when the game needs them.

- `rexglue-sdk/` is the upstream ReXGlue SDK submodule.
- `shadow/rexglue-sdk/` contains Refii-only files that overlay the SDK at
  configure time.

By default, the root `CMakeLists.txt` copies `rexglue-sdk/` to a build-local
shadow source tree, overlays `shadow/rexglue-sdk/`, and configures ReXGlue from
that copy. The SDK's compiled binaries are emitted under
`${CMAKE_BINARY_DIR}/rexglue` instead of the submodule's source tree, and the
upstream SDK checkout stays clean.

Provide an explicit `REXSDK_DIR` if you need to build against another SDK tree.
