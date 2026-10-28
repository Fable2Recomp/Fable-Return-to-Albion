# Android native build

The ARM64 runtime libraries (the guest runtime, host bridge, SDL3, Lua, the Bink and XMA
decoders, RexNet) are built from this repository by `build-native.py`. The game module is
made from the player's own game image, normally on the phone itself; it is never committed
or packaged. `build-apk.py` runs this build as its native step (see
[the Android guide](../../docs/android.md)).

## Building the runtime

Needs NDK r28c, CMake 3.25+, Ninja, Python 3, the pinned ReXGlue SDK and zlib submodules
(or `--sdk-source` / `--zlib-source` pointing at initialized trees), rustup's
`aarch64-linux-android` target for `--rexnet`, and the desktop `rexglued` generator built
from the same SDK revision: the build generates on the PC and never runs an ARM64
generator there.

```sh
python tools/android/build-native.py --ndk <ndk-r28c> --host-codegen <desktop rexglued> \
  --output <outside the repo>/android-native --api 31 --jobs 8 --rexnet
```

`native/` holds the production closure, with the NDK's `libc++_shared.so`.
`native-manifest.json` records the target, each library's hash, SONAME and NEEDED edges,
the 16 KiB ELF load alignment, the compiler and the header identities the game module must
match; `build/compile_commands.json` lists the payload hooks the phone compiler bundle
carries. Build commands and logs are written beside them.

## The phone compiler bundle

`pack-phone-bundle.py` refreshes the bundle `export-apk.py --phone-compiler-bundle` packages.
Its `data.zip` holds the NDK sysroot and resources, the SDK and bridge headers, the public
version configs and the payload hook sources; `bundle.json` hashes every file and records
the hook recipe, and `bundle-proof.json` binds the ZIP to its `native-manifest.json`. The
export checks the signed APK's copy against those hashes. No game file, generated source or
module is in it.

Its executables are prebuilt and reused from an earlier bundle: `librefii_clang.so`,
`librefii_lld.so` and `librefii_codegen.so` (Clang, ELF lld and the ReXGlue generator as
ARM64/Bionic/API 31 PIEs from NDK r28c's LLVM, source pin
`97a699bf4812a18fb657c2779f5296a4ab2694d2`), and `librefii_phone_bridge.so`. Their build is
not in this repository. They must keep these SONAMEs, 16 KiB load alignment and no RPATH;
the generator calls `InitializeAndroidPlatform` / `ShutdownAndroidPlatform` around its run
(an app-UID child does not inherit the parent's runtime initialization), and every payload
translation unit is compiled with the mutex ABI guard and the guest's floating-point model
(precise, no contraction). The app runs them from its extracted native library directory.

## A game module compiled on a PC

`--game-directory <folder with default.xex and default.xexp>` also generates and compiles
the module (`build/private-module/librefii_generated.so`, with
`private-module-manifest.json`); `--generated-dir` compiles earlier generated source
instead. Keep both outside the repository and import them with `prepare-import.py`.

- **Mutex ABI.** The module reaches the runtime's one recursive mutex through the VFS
  bridge header, which must come before the raw SDK header. Every generated and hook
  translation unit, PCH included, forces an assertion of that, and the build audits each
  ELF object for raw recursive-mutex calls: a mismatch links and then crashes at the first
  guest step.
- **ARM64 barriers.** The PC generator treated PPC `sync`, `lwsync` and `eieio` as no-ops,
  which loses ordering on ARM64. The SDK shadow now emits a sequentially consistent fence
  for them, and `prepare-generated.py` restores the fences in older generated source (a
  checked, content-addressed copy under `build/android-generated/`).

## Self-tests

`--selftests` adds libraries kept out of the production closure:
`librefii_android_fiber_selftest.so` (fibers, callee-saved registers, FPCR/FPSR, stacks)
and `librefii_android_platform_selftest.so` (threads, events, semaphores, timers, clocks),
each exporting a function that returns a PASS/FAIL report, and
`build/tests/refii_android_sdl_probe`, which checks SDL's embedded event loop without
`SDLActivity`. Run them in an app-UID process before the guest starts. The tooling's own
tests: `python -m unittest discover -s tools/android/tests`.

## Runtime constraints

- The guest initializes and steps on one dedicated OS thread: a guest fiber cannot move
  between threads, and the guest cannot be torn down live, so restart the process instead.
- Godot owns the Activity and rendering; SDL runs its dummy video and audio drivers without
  `SDLActivity`. The SDL overlay's lifecycle guard for the dummy driver is required: without
  it `SDL_PollEvent` waits forever.
- Some older kernels ignore `MAP_FIXED_NOREPLACE`; the runtime checks the address it got,
  unmaps a moved mapping and never falls back to `MAP_FIXED`.
- No `content://` paths: an importer copies files to ordinary app storage first.
- API 31 and 32 keep crash diagnostics without Bionic's API 33 `execinfo`.

The SDK shadow overlays keep their upstream licence notices; the two ARM64 context
assembly files keep Boost.Context's Boost Software License 1.0 and provenance in
`rexglue-net/shadow/rexglue-sdk/thirdparty/boost_context_android/`.
