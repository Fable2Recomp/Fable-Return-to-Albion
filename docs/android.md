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
  templates carry nine engine patches (`tools/android/godot-patches/README.md`); the four
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
   SDK with NDK r29; `build-apk.py` does not set it. `build-apk.py` refuses templates built
   with other patches than `godot-patches/` (they record theirs in `patches.txt`).
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
| Phone compiler tools | `build-phone-tools.py`, or an earlier phone compiler bundle (see below) |

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

**The phone compiler tools.** The bundle's executables (ARM64/Bionic builds of Clang, lld and the
ReXGlue generator, and the phone bridge) are built by `build-phone-tools.py`: `pack-phone-bundle.py`
takes them from the folder `phone_tools_from` names (that output, or an earlier `build-apk.py`
output's `compiler-bundle/`), with its NDK sysroot, Clang resources and recipe pins, and overlays
this checkout's headers, payload hooks, version configs and runtime libraries.
`tools/android/NATIVE.md` records their LLVM pin and contract.

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
launcher then removes the extracted game copies and builds it no longer uses. A rebuild reuses
every unit whose inputs did not change (`phone-object-cache`); a change to the compiler, its flags
or `AndroidPhoneCompiler.RecipeRevision` compiles every unit once.

## On the phone

- **Settings** are the app's `files/runtime/refii.toml` (and Fable 3's `refiii.toml`), the same keys
  as the desktop's; the launcher's Launch options edit them in the desktop's tabs, and a setting both
  games read is written to both files. On a phone each tab lists what a player sets (the graphics
  preset, frame cap, FPS counter, render scale, baked lamp shadows, camera, vibration, RexNet and split
  screen); everything else is under each tab's **Advanced** (`AndroidSettingsLayout`), and the
  desktop's window, VSync, upscaler, anti-aliasing and Performance Preset settings, which a phone
  ignores, are not listed. A row's reset goes back to the High preset's value. `debug_channel = true` opens the debug channel on
  `127.0.0.1` (port in `files/runtime/debug/refii-debug.json`; forward it with adb).
  `android_render_thread = true` runs Godot's renderer on its own thread from the next
  launch; it needs the patched templates. Every graphics preset sets it. `android_tex_upload_cache`
  (on by default) keeps each texture's GPU-ready form on disk for a GPU without BC, for faster
  repeat loads; `android_eac_normals` (off) stores normal maps there as EAC RG11, at half the
  video memory and a slower load. `android_compress_mesh_attributes` (off) stores models that are
  not skinned with Godot's attribute compression, 20 bytes a vertex instead of 36; a model over 32 m
  or with UVs past ±8 keeps the float layout.
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
  A hard mod (`native/` sources) asks for consent as on a PC and is compiled into the game
  code with the desktop's checks; changing the enabled hard mods makes the launcher offer
  Build Game Code. Mod actions reach the touchscreen through their `touch` pills and captions
  ([Mods on a touchscreen](#mods-on-a-touchscreen)) and a controller through the mod wheel on Back.
- **Co-op.** RexNet needs the `--rexnet` native build and `--netplay` export (both in
  `build-apk.py`). With RexNet up the game signs in with the RexNet profile. When the game
  starts, a profile that was saved to less recently than another one takes a copy of the
  newest saves, so turning RexNet on or off keeps your heroes; the profile's own older saves
  go to a hidden `.<XUID>.replaced-<time>` folder (`xam_save_profile_android.h`). Add a friend
  by copying their friend code (Multiplayer, My friend code) and choosing Add a friend; join
  from Multiplayer, Friends, or with Interact on another player's orb.

## Android graphics preset

Launch options has a **Graphics preset** card at the top of its Display and Graphics tabs on a
phone, in place of the desktop's Performance Preset: **Low**, **Medium**, **High**, **Ultra** or
**Custom** (a preset setting changed by hand), with a line saying what the preset in use gives. A
phone starts on High, or on Low if its GPU is an Adreno older than the 700s,
with a 30 FPS cap either way. A 20 FPS cap is the player's choice in Options.

| Preset | Render scale | Sun shadow | Also |
|---|---|---|---|
| Low | 40% | none | the nearest four placed lights, trees to 300 m, half the undergrowth, no normal maps, the modern lighting without ground-contact shadows |
| Medium | 45% | none | more lights and distance than Low |
| High | 50% | where the theme casts one | 16 placed lights, three quarters of the undergrowth, every same-picture shortcut |
| Ultra | 67% | longer | the 360's model detail, the desktop's prop distances, 32 placed lights, the ground's bump |

Each preset sets its lighting model (`android_modern_lighting`): the modern one for Low, the fable
one for Medium, High and Ultra. It changes at the next start. A phone that stored the retired Low
(lean) preset moves to Low.

Low and Medium suit a 5-6 GB phone such as the OnePlus 7 Pro, Medium and High an 8 GB Adreno 640
phone such as the Galaxy S10e, High and Ultra a Snapdragon 8 Elite such as the Galaxy S25+. An
Adreno 640 is limited by its GPU outdoors, so no preset holds 30 FPS there. High leaves out the
ground's normal maps, as Low and Medium do.

High softens the sun shadow's edges at Godot's Soft Low and Ultra at Soft Medium (`sun_shadow_filter`);
Godot's own default on a phone is Hard, which stair-steps. Both draw the sun shadow on a 2048 map
instead of 1024 (`sun_shadow_map`). Both are rows in Options' Graphics settings: **Shadow softness**
and **Shadow quality**.

The low-memory tier (`android_low_ram_mode`) trades a little picture and waiting for memory: normal
maps and large colour maps one size smaller where the GPU has no BC formats, the pause and Items menus built when first opened, no
shader folds, and a bound on the hero's decoded texture sources. `auto` turns it on under 7 GB of
memory, and the Low preset sets `on`. A new install under 7 GB starts on Low.

Finished character composites are kept in `cache/<Title>/composites/<build>` in the form they upload,
up to 256 MB, longest unused first out. `android_composite_cache = false` turns that off.

The values and the measurements behind them are in `Godot/src/android/GraphicsPresets.cs`;
raising its `Revision` makes a phone still on a preset take the new values once. The stored names
predate the labels (`android_low`, `android_mid`, `android`, `android_high`), so a phone on one
stays on it. Picking Ultra, in Launch options or in-game, first warns that it isn't intended for
casual use and has performance issues, and applies only if confirmed.

### In-game Options

- **Graphics settings** opens the Graphics column beside the buttons: the preset, picked from the
  list it opens on, the **Frame limit** (a button each for no cap, 20, 30, 40 and 60 FPS), and the
  settings that change the picture, each applied at once (`Godot/src/android/AndroidGraphicsOptions.cs`).
  A coloured line under each says what it costs and **?** says what it does; changing one makes the
  preset Custom. Options itself opens on the buttons alone, and back closes the column first.
- **Graphics rows:** Resolution, Distant detail, Object distance, Tree distance, Plant density,
  Shadow distance, Shadow quality, Shadow softness, Lamps, Lamp shadows and Bloom. A row the presets
  move with other settings steps them together. Plant density applies at the next loading screen.
  The launcher's list uses the same names (`Godot/src/android/AndroidSettingNames.cs`).
- **Skip intro videos** (the launcher's Display settings, `android_skip_intro_videos`, off by
  default) skips the logo videos at startup. Later cutscenes are not affected.
- **Baked lamp shadows** (the launcher's Graphics settings, `android_baked_shadows`, Off by default)
  picks when lamp and torch shadows are baked on the phone. Fable 2 only.
  - **Before playing** (`true`): Play first runs the desktop's static shadow sweep (lamp shadows and
    ground AO for every area, about 2.5 GB, hours on a phone) as a foreground job. A cancelled sweep
    resumes at the next Play.
  - **While playing** (`while_playing`): an area's lamp shadows bake in the background the first time
    it is entered. Ground AO is left to the sweep.
  - **Off** (`false`): no bake; baked files on the phone are ignored.
- **FPS counter** shows the frame rate in the top left corner, and
  **Vibration** sets the rumble's strength or turns it off (`vibration_strength`, 50% by default).
- **Diagnostics** unfolds **Performance stats** and **Developer tools**, the desktop's F5 and F6.
- **Controller buttons:** tap a game button, then press the controller's button for it; the two
  swap. Saved as `android-controller-map.json` beside `refii.toml`.
- **Edit touch controls:** the layout of the kind of screen Options was opened on (the world,
  menus, a pub game...): drag to move, Smaller and Bigger, Reset screen, and named presets of every
  screen. Saved as `android-touch-layouts.json` beside `refii.toml`.

## Mods on a touchscreen

What a mod states for the touch controls. The rest of the mod API is in the modding guide
([Godot/docs/modding](../Godot/docs/modding/README.md)); these parts only matter on a phone, so they are documented here.

An action in a mod's `input.json` ([Input actions](../Godot/docs/modding/settings-and-input.md)) may carry one more field:

| Field | Meaning |
|---|---|
| `touch` | optional: how a phone's touchscreen presses it — a pill of its own, or a caption on the on-screen button it is bound to |

An action whose only binding is a touch pill is kept: only a touchscreen presses it, and the
Controls page has no row for it.

A phone has no keyboard, and the touch controls draw the pad's buttons, not the keys: an action
bound to keys alone would never fire there. `"touch"` says how the screen presses it.

```json
{
	"toggle":  { "label": "Skateboard on or off", "keys": ["K"], "touch": "Skate" },
	"blue":    { "label": "Fire blue (touch)", "touch": { "pill": "Blue", "when": "portal_gun.mode" } },
	"ollie":   { "label": "Hop", "keys": ["Space"], "buttons": ["A"], "touch": { "caption": "Hop", "when": "skate.on" } }
}
```

| `touch` | Meaning |
|---|---|
| `"Skate"` | a PILL of its own, named so, held while a finger is on it |
| `true` | a pill named by the action's `label` |
| `{ "pill": "Blue" }` | the same as `"Blue"`; `label` is read when `pill` is absent |
| `{ "caption": "Hop" }` | no pill: the on-screen button the action's `buttons` name (A, B, X or Y) is captioned "Hop", and stands even where the hero could not use it otherwise |
| `"when": "<key>"` | with either: it stands only while that published value (`Refii.publish`, `host.publish`) is at least 0.5 — a mode's controls appear with the mode |

- Pills go in the touch controls' top row, filling leftward from Emotes in the load order and the
  file's order, and on into a second row beneath when the first is full. They look like Menu's and
  Emotes'. A pill with `"when"` hangs under its own mod's first pill instead (the skateboard's
  Wheelie under Skate), so the row never moves as a mode comes and goes. The player moves and resizes them with **Edit touch controls...** like any other
  control, and the layout keeps them by action, so a pill comes back where it was put.
- A pill or an area presses the action for player one with the other devices, through the same
  `input_action` event, claims and rebinds: a script cannot tell a tap from a key.
- THE ON-SCREEN PAD IS PLAYER ONE'S PAD. An action with no pill of its own is pressed by the touch
  controls' buttons and triggers its `buttons` name, under its chord, as a controller's would: the
  skateboard's `"A"` hop fires on the screen's A. An action with a pill is pressed by the pill alone,
  so a pill on an action bound to `Back` keeps the screen's Emotes from pressing it too.
- Pills and captions stand only in play, not in menus, wheels or photo mode.
- A pill steers as the face buttons do: a thumb that slides off it turns the camera while the pill
  stays held, so a held action can be aimed with the same thumb.

A mod's own drawing can be tapped too. A Godot script states a rectangle of the screen with
`host.touch_area("my_mod.size", rect)` — the root viewport's coordinates, where its `CanvasLayer`
draws; an empty `Rect2()` removes it — and a finger landing there presses the action, held while
it stays. The area takes the finger before the move stick and the camera. A HUD piece can be the
area itself: its `"touch"` names the action, and the piece is tapped where it is drawn (see
[HUD pieces](../Godot/docs/modding/ui-and-hud.md)). `host.touch_mode()` says whether the touchscreen is the device in
use now, for a mod that draws differently there, and `host.touch_stick()` reads the on-screen move
stick (x right, y up, -1..1) for a mod that steers by the stick itself.

```gdscript
func _process(_delta: float) -> void:
	var wanted := Rect2(centre - Vector2(radius, radius), Vector2(radius, radius) * 2.0) if host.touch_mode() else Rect2()
	if wanted != area:
		area = wanted
		host.touch_area("minimap.size", wanted)
```

The calls, on a Godot script's `host` (Lua asks `touch_mode` and `touch_stick` through `Refii.ask_host`; an area is a rectangle of a script's own drawing, so `touch_area` is Godot's alone). The modding guide carries the same tables ([On a touchscreen](../Godot/docs/modding/settings-and-input.md#on-a-touchscreen)):

| Call | Meaning |
|---|---|
| `touch_area(action, rect) -> bool` | on a touchscreen a finger on `rect` (the root viewport's coordinates) presses the action for player one; an empty `Rect2()` removes it |
| `touch_mode() -> bool` | whether the touchscreen is the device in use now: the touch controls are drawn |
| `touch_stick() -> Vector2` | the touch controls' move stick, x right and y up, -1..1; zero with no thumb on it |

A HUD piece in `ui/hud.json` ([HUD pieces](../Godot/docs/modding/ui-and-hud.md)) has two touchscreen fields:

| Field | Meaning |
|---|---|
| `touch` | a picture's input action (`"slot4"`, scoped to the mod, or `"my_mod.slot4"`): on a touchscreen a finger on player one's copy, where it is drawn, presses it |
| `touchScale` | on a group: a number the scale is multiplied by on a touchscreen device (a phone), where the screen is small and the pieces are tapped with a thumb: a bar the right size on a monitor is too small to see or press there. Not stated, or not above 0, it is 1 |

A mod's own screen ([A screen of your own](../Godot/docs/modding/ui-and-hud.md#a-screen-of-your-own)) takes the
taps on a touchscreen: a finger lands on it as the mouse's left button, a swipe scrolls as the mouse
wheel does, and the touch controls draw only Options and a Close pill (Start) over it — their pad
does not reach the game here. Give every key the screen offers a button to tap when
`host.touch_mode()` is true; a typed field needs a keyboard a phone does not have.

## A game module compiled on a PC

For development the game module can instead be generated and compiled on a PC:
`build-native.py --game-directory <extracted game>` builds `librefii_generated.so`,
`prepare-import.py` packs it with a save and `android-runtime.json`, and the app loads it
from its own files. `tools/android/NATIVE.md` covers the native build, the SDK overlays and
the module's ABI checks; [android-runtime.md](android-runtime.md) covers the runtime's private
configuration and diagnostics.

## Known gaps

- Memory on the Galaxy S10e (8 GB): Android has ended the game for low memory after a short
  time in the background.
