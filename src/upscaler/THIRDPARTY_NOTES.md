# src/upscaler — Real-ESRGAN (§8)

The neural super-resolution backend for the texture modernization pipeline. The managed side is
finished and waiting behind `ITexUpscaler`; what lives here is the native half.

## Layout

| | |
|---|---|
| `src/thirdparty/ncnn` | the inference runtime, added as a git submodule |
| `src/thirdparty/ncnn/glslang` | ncnn's own submodule, required by `NCNN_VULKAN=ON` |
| `src/upscaler/include/refii/upscaler.h` | the C ABI the Godot side calls |

`pybind11` is deliberately **not** initialised — it is only for ncnn's Python bindings, and this
build has no use for them.

## What is already done

- `Engine_TexEnhance` / `ITexUpscaler` — the seam. `Image` in, `Image` out, no file paths.
- `Engine_TexResample` — the classical baseline this has to beat, and the fallback when no
  neural backend loads.
- `Engine_TexRealEsrgan` — the managed backend. It P/Invokes the ABI below and **declines
  cleanly when the library is absent**, which is why the pipeline works today with nothing built
  here.
- The queue, priorities, cache, budget, runtime switch and in-place swap.

- `src/upscaler/CMakeLists.txt` + `src/upscaler/src/upscaler.cpp` — the native half: ncnn with
  Vulkan, tiled inference, and the four entry points.

## Building it

**OFF by default**, for the same reason `REFII_BUILD_RENDERER_HOOKS` is: ncnn plus glslang is a
large build and the pipeline works without it.

```sh
cmake -S . -B out/build/linux-amd64-debug-netplay -DREFII_BUILD_UPSCALER=ON
```

Standalone, which is what to use while iterating on this component — it avoids running the main
project's rexglue codegen, which writes into the source tree and must not run concurrently with
anyone else's build:

```sh
cmake -S src/upscaler -B out/build/upscaler -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DREFII_UPSCALER_STAGE_DIR="$PWD/Godot/build/bin"
cmake --build out/build/upscaler --target refii_upscaler
```

ncnn's tools, examples, benchmarks, tests and Python bindings are all forced OFF — they are most
of its build time and none of them ship. ncnn links STATICALLY into `refii_upscaler`, so Godot
loads one file.

## Implementation notes

**Tiled, 200px with 8px of context.** Tiling is not an optimisation — it is what stops a 2048
albedo needing the whole image resident on the GPU at four times the size. The context is fed to
the network and then discarded, because without it every tile boundary is an edge the network has
never seen past and the seams show as a grid.

**The network runs at ITS scale, then the result is resized.** A 4x model asked for 2x produces 4x
first; the scale is read from the model's file name because ncnn cannot be asked, and believing a
4x network is 2x produces an image of the wrong size.

**Alpha never goes through the network.** Real-ESRGAN is three-channel, and on a Fable 2 albedo the
alpha is the scissor threshold the material tests against — inventing detail in it would move the
cutout edge. It is resampled bilinearly, which moves nothing.

**Only `PERCEPTUAL` is accepted.** Normals and control signals are declined and go back to the
classical path, which handles them correctly. A network that treats a tangent-space normal as
colour produces a result that looks fine in isolation and lights wrongly on every surface.

**The GPU is preferred, not required.** With no Vulkan device it runs on CPU — slowly — rather than
refusing to start, and the caller decides from throughput whether to keep using it.

## The weights

**Not in the tree.** The Real-ESRGAN `.param`/`.bin` files are third-party redistributable data and
belong beside the build — the same rule that keeps converted game assets out of the repository.
`refii_upscaler_create` takes the directory to find them in, and returning null when they are
missing is a supported outcome that falls back to the classical resampler.

`texture_upscaling_weights` names them; the default is `realesr-general-x4v3`. The **scale is read
from that name** (`x4`), because ncnn cannot be asked and believing a 4x network is 2x produces an
image of the wrong size.

Verified with `4xLSDIRCompactC3` from `upscayl/custom-models`, which is 1.25 MB and quick to test
with. Any ncnn ESRGAN-family `.param`/`.bin` pair works.

## The ABI

`include/refii/upscaler.h`. Four entry points, all allowed to fail:

| | |
|---|---|
| `refii_upscaler_create` | null is a supported answer on a machine with no suitable Vulkan device |
| `refii_upscaler_destroy` | |
| `refii_upscaler_id` | must change when the WEIGHTS change — it keys the texture cache |
| `refii_upscaler_run` | RGBA8 in, RGBA8 out, non-zero return means "declined" |

The boundary is a raw buffer rather than a file on purpose. §8 lists process creation, Python
startup, PNG encode, PNG decode and re-initializing Vulkan per texture as the costs to avoid, and
every one of them is a consequence of the boundary being a file.

`refii_upscaler_filter` mirrors `TexFilterClass`. A backend that cannot honour a class must
**decline** rather than substitute one it can do — an upscaler that quietly treats a tangent-space
normal as colour produces a result that looks fine in isolation and lights wrongly on every
surface.

## Measured

It builds, loads, and runs. `Godot/build/bin/librefii_upscaler.so`, 19.7 MB with ncnn linked in
statically, all four symbols exported.

`src/tools/TexEnhanceSoak.cs`, 16 cores, cold cache, 4 workers:

| | classical `resample-1` | neural `4xLSDIRCompactC3` |
|---|---|---|
| throughput | 13.9/s | 5.4/s |
| projected region first load | ~90–100 s | ~257 s |
| managed churn peak | +1,283 MB | +207 MB (20 textures) |
| managed settled | ±0.0 MB | ±0.0 MB |

Neural is **~2.6x slower** and still a once-per-asset background cost with the originals on screen
throughout. The weights used for that run are a 1.25 MB compact model; `realesr-general-x4v3` is
larger and will be slower again.

The id carries a hash of the weight files (`4xLSDIRCompactC3-x4-f1d8453dcc22e6b6`), so swapping
weights invalidates every cached texture that came from the old ones — which is the property the
cache needs and a name alone cannot give.

The interesting question is not whether the neural result looks sharper — it will — but whether it
is sharper in a way that respects §3.2. A model trained on photographs invents detail that a 2008
console texture never had, and the plan is explicit that original semantics beat a generic guess.
Compare against `resample-1` on the same textures before turning it on by default.
