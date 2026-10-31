# Android (ARM64)

Refii runs on Android phones (ARM64, Android 12 / API 31 or newer) as a Godot app. The APK
holds the Godot host, the native runtime and a self-contained compiler: the player picks
their own Fable 2 ISO and title update on the phone, and the app extracts the game and
generates, compiles and links the game module there: about 20 minutes on a Galaxy S25+ and
70-80 on a Galaxy S10e, and again after any update whose phone compiler bundle differs
(runtime libraries, headers, hooks or version configs). The APK contains no game files,
generated source or game module.

Tested on a Galaxy S25+ (Snapdragon 8 Elite, 12 GB, Android 17) and a Galaxy S10e
(Snapdragon 855, 8 GB, Android 12): the game plays with an Xbox controller or touch
controls, saves and loads, and mods, photo mode, Murgo's mirror, the pub games and RexNet
co-op work, with the gaps listed at the end.

## What it changes outside Android

- **Native.** The runtime and host build with the NDK behind `if(ANDROID)` in CMake
  (`cmake/refii_android_toolchain.cmake`, `cmake/refii_android.cmake`, and the shadow SDK's
  `thirdparty/sdl_android.cmake`). Phone-only native code sits in `*_android` files beside
  the shared ones (`lifecycle_android.cpp`, `babel_text_hooks_android.cpp`,
  `refii_app_android.h`, the shadow SDK's `threading_owner_lifetime_android.h`), and the
  shadow SDK gains POSIX/Android paths for threads, fibers, input and the window. Desktop
  builds take the same sources.
- **C#.** Android code lives in `Godot/src/android` (Fable 2's own in
  `Godot/src/Titles/Fable2/android`), mostly as `partial` files of the shared classes it
  extends; the shared file keeps a one-line call where the phone code was. Textures upload
  through `Engine_TextureUpload`, which hands a phone's images to `AndroidTextureUpload`.
  The NativeAOT export's MSBuild settings are in `Godot/src/android/Android.targets`. Shared
  code asks
  `Engine_Platform.IsAndroid`, not `OperatingSystem.IsAndroid()`, which is false under
  NativeAOT's linux-bionic.
- **Shaders.** The lite variants include the same shader bodies as the desktop; what they
  leave out is behind `#ifdef FABLE_LITE` in those bodies, not in second copies.
- **Settings.** Every setting the port added defaults to the desktop's existing behaviour.
  The Android [graphics presets](#android-graphics-preset) turn them on, and a desktop
  neither shows nor writes the presets.
- **Godot.** The desktop keeps the official 4.7.1 editor and binaries. The Android
  templates carry eight engine patches (`tools/android/godot-patches/README.md`); the four
  C# binding races they fix (0004, 0005, 0007, 0008) are not Android-specific, only rarer on
  desktop.

Shared C# that runs on Android must also work under NativeAOT: no reflection-based
System.Text.Json (use `Engine_Json` and the source-generated contexts), no OpenSSL (hash
through `Engine_Hash`, no HTTPS), and no thread started from a module initializer.

## Where Android code goes

- Phone-only C# goes in `Godot/src/android` (one title's own in `Godot/src/Titles/<title>/android`), as a
  `partial` file of the shared class or a helper of its own. The shared file keeps at most a one-line
  call, gated by `Engine_Platform.IsAndroid`.
- Phone-only native code goes in a `*_android.cpp/.h` beside the shared file, added under `if(ANDROID)`;
  Android build settings go in `cmake/refii_android*.cmake` and `Godot/src/android/Android.targets`.
- Nothing is copied to separate it. The lite shaders are the full shaders plus defines, built by
  `Engine_ShaderVariant`; `FABLE_LITE` blocks inside the shared shader bodies are the exception that
  avoids second copies.
- Android docs go in `docs/`: `Godot/docs` ships in every install's SDK.

`AndroidSeparationTests` holds this: it counts the lines in each shared file that name the phone
against `Godot/tests/Refii.Tests/AndroidSeparationBaseline.txt` and fails when one goes up. A hook that
really has to stay in a shared file raises that file's count in the same commit (or run the tests with
`REFII_WRITE_ANDROID_BASELINE=1`), so it shows up in review.

## Build the APK

`tools/android/build-apk.py` builds a signed APK (an export of Godot's debug template) in
one step from a config file naming the tools. It runs, each as a script of its own:

1. **templates**, only when missing: the patched Godot 4.7.1 Android templates
   (`godot-patches/build-templates.py`). This step needs SCons, the Godot source with
   Swappy installed (`misc/scripts/install_swappy_android.py`) and `ANDROID_HOME` set to an
   SDK with NDK r29; `build-apk.py` does not set it.
2. **native**: `build-native.py --rexnet`, the ARM64 runtime libraries.
3. **bundle**: `pack-phone-bundle.py`, the phone compiler bundle for this runtime.
4. **export**: `export-apk.py --phone-compiler-bundle --nativeaot --netplay`, which
   publishes the C# ahead of time, packages and signs the APK and verifies it.

| Needs | Version |
|---|---|
| Godot .NET editor and Android templates | 4.7.1 stable mono (or the 4.7.1 source, SCons, Swappy and NDK r29 to patch the templates) |
| .NET SDK | 9.0 with the `android` workload (the desktop stays on .NET 8) |
| Java, Android SDK, NDK | JDK 17; platform 36 and build-tools 36.1.0; NDK r28c (28.2.13676358) |
| Native tools | CMake 3.25+, Ninja, Python 3.11+, rustup with `aarch64-linux-android` |
| ReXGlue generator | the desktop `rexglued` built from this checkout's SDK |
| Phone compiler tools | an earlier phone compiler bundle (see below) |

Initialize the submodules first (`git submodule update --init --recursive`), copy
`tools/android/build-apk.example.toml` outside the checkout, fill in the paths, and run:

```sh
python tools/android/build-apk.py --config ~/.config/refii-android/build-apk.toml \
  --output ../android-local/release-01
```

`--output` must be a new directory outside the checkout; `--dry-run` prints the commands,
and `--native <earlier output>/native` reuses a native build. It writes
`refii-android.apk`, `SHA256SUMS.txt`, `build-apk.json` (commit, step times, the bundle's
hashes and host bridge ABI), the exporter's reports under `apk/` and each step's log under
`logs/`.

**Signing.** Without `keystore` the exporter makes a new debug key. An APK only updates an
installed one signed with the same key and package, so keep one key. The passwords come
from the file `passwords_json` names (`REFII_ANDROID_STORE_PASSWORD`,
`REFII_ANDROID_KEY_PASSWORD`) or from those environment variables, and are never printed.

**The phone compiler tools are prebuilt.** The bundle's executables (ARM64/Bionic builds of
Clang, lld and the ReXGlue generator, and the phone bridge) are not built here:
`pack-phone-bundle.py` reuses them from the bundle `phone_tools_from` names (an earlier
`build-apk.py` output's `compiler-bundle/`), with that bundle's NDK sysroot, Clang resources
and recipe pins, and overlays this checkout's headers, payload hooks, version configs and
runtime libraries. `tools/android/NATIVE.md` records their LLVM pin and contract.

Tests: `dotnet test Godot/refii-launcher.sln` and
`python -B -m unittest discover -s tools/android/tests -p 'test_*.py'`.

### Automating the build

`build-apk.py` needs no input and exits non-zero at the first failed step, so a scheduled
job can call it as it is. The machine needs the toolchains, the patched templates and a
phone compiler bundle on disk, so use a build machine or a self-hosted CI runner:

```sh
#!/bin/sh
set -e
cd /src/refii && git pull --ff-only && git submodule update --init --recursive
python3 tools/android/build-apk.py --config /etc/refii/build-apk.toml \
  --output "/builds/refii-android-$(date +%Y%m%d-%H%M)"
```

In CI, keep the keystore and its two passwords as secrets: write the keystore to the path
the config's `keystore` names, set the two password variables for the step, and archive
`refii-android.apk`, `SHA256SUMS.txt`, `build-apk.json` and `apk/native-symbols/`
(for crash backtraces). A GitHub Actions job on a self-hosted runner (an example, not yet
run on GitHub):

```yaml
jobs:
  apk:
    runs-on: [self-hosted, linux, refii-android]
    steps:
      - uses: actions/checkout@v4
        with: { submodules: recursive }
      - run: echo "$KEYSTORE_B64" | base64 -d > /etc/refii/release.keystore
        env: { KEYSTORE_B64: "${{ secrets.ANDROID_KEYSTORE_B64 }}" }
      - run: python3 tools/android/build-apk.py --config /etc/refii/build-apk.toml --output "$RUNNER_TEMP/apk"
        env:
          REFII_ANDROID_STORE_PASSWORD: ${{ secrets.ANDROID_STORE_PASSWORD }}
          REFII_ANDROID_KEY_PASSWORD: ${{ secrets.ANDROID_KEY_PASSWORD }}
      - uses: actions/upload-artifact@v4
        with: { name: refii-android, path: "${{ runner.temp }}/apk/refii-android.apk" }
```

## Install or update on a phone

`adb install -r refii-android.apk`, or open the APK on the phone. Install over an earlier
build signed with the same key to keep its saves and extracted game; never uninstall first.
In the launcher, **Launch options → Install → Extract and compile on this phone** prepares
the game. An update whose phone compiler bundle differs compiles it again by itself when the
launcher opens; let it finish (Cancel keeps playing the earlier build meanwhile). The
launcher then removes the extracted game copies and builds it no longer uses.

## On the phone

- **Settings** are the app's `files/runtime/refii.toml`, the same keys as the desktop's;
  the launcher's Launch options edit it. `debug_channel = true` opens the debug channel on
  `127.0.0.1` (port in `files/runtime/debug/refii-debug.json`; forward it with adb).
  `android_render_thread = true` runs Godot's renderer on its own thread from the next
  launch; it needs the patched templates.
- **Logs and saves.** Export (the launcher's Install tab and File menu, and Options in game)
  writes a zip to Download: the logs, settings, device and save for a bug report
  (`Fable2-logs-and-saves-…`), or the save folder alone as a backup (`Fable2-saves-…`).
  **Import save**, in the launcher only and refused once the game has started, restores the
  save from an export that has one, after exporting the current save to Download
  (`Fable2-saves-before-import-…`). The phone's RexNet identity and friends are never
  exported or replaced.
- **Controls.** An Xbox controller, or the touch controls (`AndroidTouchControls`): a move
  stick that appears under the left thumb, a camera drag anywhere else, A/B/X/Y, Lock and
  Spells at the side edges, Menu, Emotes and View pills, each labelled with the controller
  button it presses, an Options pill, and a D-pad in menus. They press the same pad as a
  controller (`GameInput.Touch`). The back gesture pauses or backs out; leaving the app
  holds the game.
- **Mods.** The launcher's Modding tab is the desktop's Modding panel; the APK bundles
  `mods-src/` switched off. **Install from file…** opens Android's document picker, and the
  chosen `.f2mod` is checked and installed as on a PC, named after the file (or after the
  package's title in lower case when the picker gives no name: `Toggle Sprint` →
  `toggle_sprint`). There is no folder to open, no URL install and no editor. A soft mod runs
  as on a PC: its files, rows, images, Lua, `settings.json`, `input.json` and `godot/*.gd`.
  Hard mods (with `native/` sources) cannot be built on the phone, so the tab marks them
  *not on phone* and refuses them. Mod actions bound to keys need a keyboard; the touch
  controls have no buttons for them yet.
- **Co-op.** RexNet needs the `--rexnet` native build and `--netplay` export (both in
  `build-apk.py`). With RexNet up the game signs in with the RexNet profile, so earlier
  saves need copying into it. Join from the main menu's Multiplayer, or with Interact on
  another player's orb.

## Android graphics preset

Launch options has a **Graphics Preset** row on a phone: **Performance**, **Balanced**,
**Quality** or **Custom** (any preset setting changed by hand). A phone starts on Balanced
with a 30 FPS cap; the in-game Options panel switches the preset and the cap, and its
see-through Graphics column steps the settings that change the picture and apply at once
(render scale, model detail, prop distance, sun shadows and their reach, lamps lit, lamp range
and shadows, bloom; `Godot/src/android/AndroidGraphicsOptions.cs`). Stepping the preset moves
every row it sets; changing a row makes it Custom. A coloured line under each row's name says how much it costs. Balanced
(tuned on the S25+) renders at half resolution with the sun's shadows where the theme casts
them and every same-picture shortcut on; Performance (measured on a throttled S25+, with
values checked on the S10e) drops to 30% resolution, no dynamic sun shadow and the nearest
four placed lights; Quality, for a phone with frames to spare, renders at 60% of the
resolution with the 360's model detail, the desktop's prop distances, a longer sun shadow
and more placed lights. Balanced was labelled Quality before Quality was added; it keeps
its stored name (`android`), so a phone on it stays on it. The values and the measurement
behind each are in `Godot/src/launcher/settings/GraphicsPresets.cs`; raising its
`Revision` makes phones still on a preset take the new values once.

## A game module compiled on a PC

For development the game module can instead be generated and compiled on a PC:
`build-native.py --game-directory <extracted game>` builds `librefii_generated.so`,
`prepare-import.py` packs it with a save and `android-runtime.json`, and the app loads it
from its own files. `tools/android/NATIVE.md` covers the native build, the SDK overlays and
the module's ABI checks; [android-runtime.md](android-runtime.md) covers the runtime's private
configuration and diagnostics.

## Known gaps

- The phone compiler's executables are prebuilt (above); their build is not in this
  repository.
- Hard mods, and touch buttons for mod actions.
- Memory on the Galaxy S10e (8 GB): Android has ended the game for low memory right after
  a co-op join, and after a short time in the background.
