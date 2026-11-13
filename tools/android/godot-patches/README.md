# Godot 4.7.1 patches for the Android templates

The Android export uses Godot templates built with these patches. The desktop runs the official
editor and binaries and is not affected.

Most of them exist for Godot's separate render thread (`rendering/driver/threads/thread_model = 2`),
which lets the main thread run the next frame while the last one draws: about 20% more frames on
a GPU-bound Galaxy S10e. Stock 4.7.1 is not safe with it on Android. Four fix races in the C#
bindings that the render thread and a compacting GC expose; they are not Android-specific, only
much rarer on the desktop.

| Patch | Bug and fix | Upstream |
|---|---|---|
| `0001-android-resize-syncs-render-thread.diff` | `GodotLib_resize` calls `reset_window()` on the main thread while the render thread may still be drawing into the swapchain it frees, which crashes (SIGTRAP on `VkThread`) on resume. The resize now syncs the render thread first. | Not fixed or reported. |
| `0002-shader-parameter-list-sync.diff` | `instance_geometry_get_shader_parameter_list` and `canvas_item_get_instance_shader_parameter_list` are queued commands that write through a list on the caller's stack after it is gone; `Node.Duplicate()` of a 3D node reaches them. Both are made synchronous. | Fixed in 4.7.2 (godotengine/godot#120858): drop on upgrade. |
| `0003-swappy-interval-keeps-max-fps.diff` | Every swapchain rebuild (startup, a vsync change, resume, rotation) resets Swappy's swap interval, so `max_fps` stops pacing the display after the first rebuild and frame times alternate. The rebuild now keeps the cap's interval. | Not fixed or reported. |
| `0004-csharp-destroy-off-managed-heap.diff` | GodotSharp destroys `StringName`, `NodePath`, `Array`, `Dictionary` and `Variant` values through a pointer into the unpinned managed wrapper, and the destructors read it again after the refcount drop; a compacting GC in between makes them read another object. The value is now copied to native storage first. | Not reported. |
| `0005-csharp-binding-replacement-locked.diff` | Replacing a collected object's C# wrapper skips the binding lock (upstream's own "Possible data race?" TODO), so two threads loading the same cached resource make two wrappers and trip `CRASH_COND(!is_released())`. The replacement and the finalizer's release now take the lock. | Not reported. |
| `0006-canvas-item-property-list-without-material.diff` | `CanvasItem::_get_property_list` asks the rendering server for instance shader parameters (a synchronous render-thread round trip) even for an item with no material. Skipped when there is none; it made duplicating Flash clips very slow. | Not reported. |
| `0007-csharp-refcounted-dispose-unreference-last.diff` | `godotsharp_internal_refcounted_disposed` drops its reference before reading the instance binding, so another holder (such as a queued texture update) can delete the object in between. The binding is now read and released first, and the reference dropped last. | Not reported. |
| `0008-csharp-binding-swap-locked.diff` | The reference callback swaps a RefCounted's wrapper handle between strong and weak without the release lock, so two threads can free the same GCHandle and a later lookup returns the wrong object (`InvalidCastException` on `GetChild`). The swap now takes `script_gchandle_release_mutex`. | Not reported. |
| `0009-shader-code-on-demand.diff` | Every shader with specialization constants keeps a parsed re-spirv copy of each stage for the whole run, and the driver keeps a copy in its unused base module: about 330 MB of native memory on a phone. Graphics stages now keep only their compressed code, and parse it and make a module as each pipeline is created. | Not sent |
| `0010-shader-cache-saved-when-compiled.diff` | `ShaderRD` compiles a shader version's groups on worker threads but writes them to the shader cache only when one of the group's variants is first used or the version is freed. A group compiled during a load and not used before the process ends (the player swipes the game away, or it is killed in the background) was compiled again on every launch: about 8 s of a Galaxy S10e's region load. The last variant to compile now saves the group. | Not reported. |
| `0011-mobile-light-loop-ends-at-empty-slot.diff` | Mobile's omni, spot and area loops test for the `0xFF` end marker only from slot 1, so a draw none of whose lights are in this frame's light buffer still shades entry 255 once per light type on every pixel. Slot 0 now ends the loop too, as the decal and reflection-probe loops already do. Same picture. | Not reported. |

## Building

```
tools/android/godot-patches/build-templates.py \
  --godot-source <godot 4.7.1-stable checkout> \
  --official-templates <official 4.7.1 .NET templates> \
  --out <patched templates>
```

Then export with `tools/android/export-apk.py --templates <patched templates> ...`
(`build-apk.py` does both). Only the debug template's arm64 library is rebuilt, because the export
runs `--export-debug`.

The game reads `android_render_thread = true` from refii.toml and writes the thread model into
`user://renderer.cfg` for the next launch (`AndroidRenderThread`). Leave it off with the official
templates: the resume crash is in the engine.

## On a Godot upgrade

1. Check whether upstream fixed each bug, and delete the patches it did.
2. `git apply --check` the rest on the new tag and rebuild.
3. On a phone, relaunch with `android_render_thread = true`, background and resume the game a
   dozen times, and confirm the process survives (logcat's crash buffer stays empty).
