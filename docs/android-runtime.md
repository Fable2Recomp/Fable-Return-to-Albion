# Android host runtime

How the Android app is put together. Building and installing it are in
[the Android guide](android.md).

## Structure

- **Launcher.** The APK starts in the desktop's own launcher scene (`main.tscn`), with
  `AndroidPhoneLauncher` in its Install tab: the player picks an ISO and title update
  through Android's document picker, and `AndroidGameInstaller` / `AndroidInstallJob`
  extract the game and generate, compile and link the module on the phone inside a
  foreground service, so a paused frame loop or a backgrounded app does not stop it.
  Settings and Mods are the desktop's panels over the app's `refii.toml`.
- **Game host.** `res://src/android/AndroidGame.tscn` (`AndroidGameHost`) runs the
  production `GameUi`, `GameInput`, `GameWorld` and `GameAudio` without `GameSession`:
  `AndroidNativeSession` is the one owner that initializes and steps the guest, on its own
  thread. One `Engine_GameAssets` mount serves the guest, the UI and the world, and the
  host does the per-frame mod work `GameSession` does on the desktop.
- **Idle.** With no input for `android_idle_screen_minutes` (10) play pauses and the screen goes
  by the phone's own timeout; an untouched pause menu draws at 10 fps. The launcher runs in Godot's low-processor mode (`AndroidIdlePower`).
- **C#.** Release builds compile it with NativeAOT (`export-apk.py --nativeaot`); without
  that flag it runs on .NET 9 Mono. See the guide for what shared code must avoid.

## Files

Everything writable is under the app's user directory (`OS.GetUserDataDir()`, the app's
`files/`):

| Path | What |
| --- | --- |
| `runtime/refii.toml` | Settings, as on the desktop |
| `runtime/assets/save`, `runtime/assets/mods` | Saves; installed mods and `loadorder.toml` |
| `logs/godot.log`, `runtime/logs/refii.log` | Godot's log (with the `[hitch]` lines) and the runtime's |
| `runtime/debug/refii-debug.json` | The debug channel's port, when `debug_channel = true` |
| `android-status.json` | A status snapshot the main thread rewrites about once a second: phase, frame and guest counters, input, audio, render and memory figures |
| `android-runtime.json` | The game and module the host runs: written when the launcher activates a phone build, or by hand for a PC-compiled module (below) |
| `phone-installs/`, `phone-builds/` | Extracted games and compiled game modules. While the active game and module are the phone's own, the launcher removes the ones not in use at start and after it activates a build; interrupted attempts keep only small logs |
| `phone-jobs/` | Each compile job's progress and outcome; kept |
| `phone-installer-state.json` | The last extracted game and compiled module, with the compiler bundle each module was built from |

Exports (`AndroidLogBundle`) go to the phone's shared Download folder as
`Fable2-logs-and-saves-<time>.zip` or `Fable2-saves-<time>.zip`. Inside, `device.txt` describes the phone,
and every other file keeps its path under the app's directory (`files/logs/…`,
`files/runtime/refii.toml`, `files/runtime/assets/save/…`). An export with saves also carries
`export.json` and leaves out the save folder's `rexnet/` (the RexNet identity key and friend
lists). One with logs also carries `install.txt`: when the APK was installed, the module the
game runs, whether it was compiled from this APK's bundle, and every compile job with whether
it finished. `AndroidSaveTransfer` imports one: it checks every save entry (paths, sizes, at least
one hero header), extracts into a staging folder beside `runtime/assets/save`, writes the
current save to Download as a backup export, and swaps the new save in, carrying the phone's
own `rexnet/` across. It refuses once the native session has started in this process.

A first boot writes `refii.toml` only if it is absent, and a file that has never had a
`graphics_preset` takes the High preset and a 30 FPS cap once. Defaults that differ from
the desktop's: `render_scale_percent` 50 when absent, a 1024 shadow map with two splits, no
eager shader warm-up (older Adreno drivers hung compiling unused shader families), no
MSAA/TAA or texture upscaling, and smaller caches: decoded level files 256 MiB (64 MiB a
file), nested banks 256 MiB, textures on disk 512 MiB. `max_fps` and
`render_scale_percent` are reread once a second, so the in-game Options panel changes
them live.

## A PC-compiled module

For development, `android-runtime.json` (schema 1) points the host at a module compiled on
a PC instead of the phone's own installation:

```json
{
  "schema": 1,
  "game_root": "/absolute/app-readable/game-directory",
  "module_path": "/absolute/app-private/module/librefii_generated.so",
  "save_import_root": "/absolute/imported-user-data-root",
  "skip_movies": true,
  "movie_dwell_seconds": 2
}
```

`game_root` holds `default.xex`, an optional `default.xexp` and `data`; `module_path`
defaults to `module/librefii_generated.so`; a save under `save_import_root` (a whole native
`user_data_root` tree) is copied in only while the private save folder is empty. Paths must
be absolute filesystem paths; unknown or mistyped fields stop the boot with an error.
`tools/android/prepare-import.py` builds this set.

## Platform specifics

- **Input.** Physical controllers go through `GameInput` as on the desktop; the touch
  controls are one more device on pad 0. A pressed button reaches the guest exactly once:
  the host tags each physical press and release, and only a successful XInput state copy
  into the guest acknowledges it (`host_input.h`'s read receipts). Android installs only the
  host-fed input driver, without the SDK's always-connected NOP pad.
- **Audio.** The host bridge is also loaded as a GDExtension, and its
  `RefiiGuestAudioEffect` on a `GuestAudio` bus takes the guest's frames on Godot's audio
  thread. Godot's Android driver is stereo at 44.1 kHz, so the effect folds 5.1 to stereo
  and resamples 48 to 44.1 kHz.
- **Textures.** `AndroidTextureUpload` reads the GPU's formats once. Where BC is not
  sampled but ASTC is (Adreno 6xx, the S10e), `Engine_AstcTranscode` rewrites BC1/BC3 as
  ASTC 4x4 on the CPU instead of expanding them to RGBA8. An optional PC-prepared ASTC
  albedo cache (`runtime/gpu-textures/astc4x4-v1`) is used when present and falls back to
  the normal path per texture.
- **SDL** runs embedded without SDLActivity; the dummy video driver's lifecycle guard is
  needed even for event polling (without it the first guest step stalled).
- **Lifecycle.** One native session per process. Leaving the app holds the game;
  Continue after returning to the title works in the same process.

## Diagnostics

With `debug_channel = true` the debug channel listens on `127.0.0.1` (the first free port
from 8787; `refii-debug.json` names it); forward it with `adb forward`. It speaks the
desktop's JSON-lines protocol, including `render` levers, `framephases`, `metrics` and the
Android-only status fields. Options in game has Diagnostics, which unfolds Performance stats
and Developer tools: the desktop's F5 and F6 overlays. The NativeAOT library's split debug symbols stay out of the APK, in the export's
`native-symbols/`, for reading crash addresses from logcat.
