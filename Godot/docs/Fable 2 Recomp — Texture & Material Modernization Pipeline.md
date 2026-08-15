# Fable 2 Recomp — Texture & Material Modernization Pipeline

## 1. Purpose

This document defines the proposed rendering modernization pipeline for the Fable 2 recompilation project.

The goal is to preserve the original game's material and shader intent while taking advantage of the modern Godot Forward+ renderer and Vulkan.

The system should allow original Xbox 360 assets to be:

- Extracted from the game data.
- Associated with their original materials and shaders.
- Upscaled at runtime when first required.
- Converted into modern Godot-compatible PBR materials.
- Rendered with per-pixel lighting where appropriate.
- Cached persistently so expensive processing only occurs once.
- Retain original shader behaviour where a standard PBR material is insufficient.

The original extracted material definitions and translated HLSL shaders are considered authoritative sources for determining texture semantics and material behaviour.

---

# 2. High-Level Architecture

```text
                         Fable 2 Game Data
                                │
              ┌─────────────────┴─────────────────┐
              │                                   │
              ▼                                   ▼
        Material Data                         SWF / Banks
              │                                   │
              │                              Texture Extraction
              │                                   │
              ▼                                   ▼
       Shader Association                  Texture Classification
              │                                   │
              └─────────────────┬─────────────────┘
                                │
                                ▼
                    Material / Shader Analysis
                                │
                    ┌───────────┴───────────┐
                    │                       │
                    ▼                       ▼
              Existing Maps             Missing Maps
                    │                       │
                    │                  AI / Derived
                    │                  Map Generation
                    │                       │
                    └───────────┬───────────┘
                                ▼
                       Texture Upscaling
                                │
                                ▼
                     Xbox → PBR Conversion
                                │
                                ▼
                  Godot Material / Shader
                                │
                                ▼
                     Godot Forward+
                                │
                                ▼
                            Vulkan
```

---

# 3. Design Principles

## 3.1 Original Data Is the Authority

The system should not guess the meaning of a texture when the original material/shader data can determine it.

For example, a texture named `specular` should not automatically be assumed to be a Godot specular map.

The original shader should be inspected to determine whether it represents:

- Specular intensity.
- Specular colour.
- Gloss.
- Shininess.
- Metallic information.
- A packed mask.
- Emission.
- Another material-specific parameter.

This is especially important because Xbox 360-era shaders frequently use packed or non-PBR texture representations.

---

## 3.2 Preserve Artistic Intent

Modernization should improve resolution and rendering quality without unnecessarily changing the appearance of the original game.

The system should prefer:

```text
Original material semantics
        ↓
Modern equivalent
```

over:

```text
Original texture
        ↓
Generic AI guess
```

AI-generated data should primarily fill gaps where the original game does not provide sufficient information.

---

# 4. Texture Pipeline

## 4.1 Runtime Processing

Texture enhancement should initially be performed on demand.

When a level loads:

```text
Level Load
    │
    ▼
Discover referenced assets
    │
    ▼
Calculate texture/material keys
    │
    ├── Cache Hit ─────────► Load cached asset
    │
    └── Cache Miss
            │
            ▼
       Queue processing
            │
            ▼
       Texture extraction
            │
            ▼
          Upscale
            │
            ▼
       Generate maps
            │
            ▼
      Build material
            │
            ▼
          Cache
```

This avoids requiring a complete offline conversion of the game before it can be played.

---

# 5. Persistent Texture Cache

The result of processing should be stored persistently.

A possible structure:

```text
cache/
    textures/
        <hash>.ktx2

    materials/
        <material-hash>.tres

    shaders/
        <shader-hash>.gdshader

    metadata/
        <material-hash>.json
```

The cache key should include enough information to invalidate stale results.

Potential inputs:

```text
source asset hash
material identifier
shader identifier
upscaling model
upscaling scale
conversion version
```

For example:

```text
SHA256(
    source_texture +
    material +
    shader +
    model +
    scale +
    converter_version
)
```

Changing the conversion pipeline should therefore automatically produce a new cache entry.

---

# 6. Asynchronous Processing

Texture processing should not block gameplay unnecessarily.

A worker system should be used:

```text
                    Texture Request
                          │
                          ▼
                     Cache Lookup
                          │
                  ┌───────┴───────┐
                  │               │
                Found           Missing
                  │               │
                  ▼               ▼
              Use Cache       Work Queue
                                  │
                                  ▼
                            GPU Upscaler
                                  │
                                  ▼
                             Cache Result
                                  │
                                  ▼
                           Replace Texture
```

If the enhanced texture is not ready when it is first required, the original texture may be used temporarily.

Once processing completes, subsequent material instances should use the enhanced version.

---

# 7. Texture Priorities

Processing should be prioritized according to visibility and importance.

Suggested priority levels:

```text
Priority 0:
    HUD
    Menus
    Currently visible SWF assets

Priority 1:
    Current scene materials

Priority 2:
    Other assets referenced by the current level

Priority 3:
    Background / predicted assets
```

This allows low-end hardware to begin rendering immediately while enhancement continues in the background.

---

# 8. Real-ESRGAN Integration

## 8.1 Role

Real-ESRGAN should be used primarily for raster texture super-resolution.

It should not be treated as the material conversion system itself.

Conceptually:

```text
Original Bitmap
      │
      ▼
Real-ESRGAN
      │
      ▼
High Resolution Bitmap
```

The existing C++/Vulkan implementation should be preferred over invoking a Python process.

The desired architecture is an embedded inference implementation that keeps the model and Vulkan context alive.

---

## 8.2 Avoid External Process Overhead

The preferred pipeline is:

```text
BAD:

Texture
 ↓
PNG encode
 ↓
Real-ESRGAN process
 ↓
PNG decode
 ↓
GPU upload
```

Preferred:

```text
Texture
 ↓
RGBA / GPU texture
 ↓
Embedded inference
 ↓
Upscaled GPU texture
 ↓
Material processing
```

This avoids:

- Process creation.
- Python startup.
- PNG encoding.
- PNG decoding.
- Unnecessary CPU/GPU transfers.
- Reinitializing Vulkan for every texture.

---

# 9. Upscaling Strategy

Not all textures should be treated identically.

Suggested classification:

| Asset Type | Processing |
|---|---|
| Albedo | AI upscale |
| Diffuse | AI upscale |
| Normal | Specialized normal-aware upscale |
| Roughness | Edge-preserving / scalar upscale |
| Gloss | Scalar upscale |
| Metallic | Preserve/scalar upscale |
| AO | Scalar/edge-preserving upscale |
| Masks | Preserve channel semantics |
| Emission | AI upscale where appropriate |
| UI | AI upscale or native SWF rendering |
| Vector SWF | Render at target resolution |
| Tiny utility textures | Do not upscale |

---

# 10. Adaptive Upscaling

The system should not automatically perform 4× upscaling on every texture.

The target resolution should be based on how the texture will actually be displayed.

Example:

```text
128 × 128 → 512 × 512     4×
256 × 256 → 512 × 512     2×
512 × 512 → 1024 × 1024   2×
1024 × 1024 → 2048 × 2048 2×
```

A texture should not be enlarged beyond a useful resolution merely because the selected AI model supports 4×.

---

# 11. PBR Material Reconstruction

The final target is a modern Godot material representation.

Possible output:

```text
Albedo
Normal
Roughness
Metallic
AO
Emission
Height
```

Godot's `StandardMaterial3D` should be used whenever the original material can be represented accurately using standard PBR properties.

Custom Godot shaders should be generated for materials requiring special behaviour.

---

# 12. Xbox Material → Godot Mapping

The mapping must be derived from the original shader rather than texture names.

Typical mappings include:

```text
Xbox Diffuse
	→ Godot Albedo

Xbox Normal
	→ Godot Normal

Xbox Emissive / Self Illumination
	→ Godot Emission

Xbox Gloss
	→ Godot Roughness (inverted/converted)

Xbox Metallic
	→ Godot Metallic

Xbox AO
	→ Godot AO
```

Specular is **not** equivalent to emission.

Emission should only be used for material components intended to emit light or appear self-illuminated.

---

# 13. Gloss → Roughness

Xbox-era shaders may use glossiness or specular power rather than modern roughness.

The conversion must therefore be derived from the original shader equation.

Conceptually:

```text
Gloss
  │
  ▼
Normalized Gloss
  │
  ▼
Invert / Remap
  │
  ▼
Godot Roughness
```

The exact conversion should be validated against the original shader.

If the original shader uses a nonlinear specular-power equation, the conversion should reproduce the perceptual response rather than simply using:

```text
roughness = 1 - gloss
```

---

# 14. Shader Analysis

The translated HLSL shaders provide a major advantage.

Each shader can be analysed to determine:

- Texture samplers.
- Texture semantics.
- Constant buffers.
- Material parameters.
- Vertex-stage calculations.
- Pixel-stage calculations.
- Lighting equations.
- Normal-map handling.
- Specular equations.
- Emission.
- Alpha behaviour.
- Blending.
- Special effects.

Example:

```hlsl
float3 diffuse =
	tex2D(DiffuseSampler, uv).rgb;

float3 normal =
	DecodeNormal(
		tex2D(NormalSampler, uv)
	);

float specular =
	tex2D(SpecularSampler, uv).r;
```

This gives the converter reliable semantic information:

```text
DiffuseSampler
	→ Albedo

NormalSampler
	→ Normal

SpecularSampler
	→ Specular
```

---

# 15. Material Classification

Materials should be classified before conversion.

Suggested categories:

```text
PBR
Unlit
Emissive
Transparent
Alpha-Tested
Foliage
Water
Skin
Hair
Glass
Particle
Decal
UI
Special Effect
Custom
```

Not every material should be forced into PBR.

For example, a UI texture or baked 2D image should generally remain unlit.

---

# 16. Vertex Lighting → Per-Pixel Lighting

One of the major modernization opportunities is moving appropriate lighting calculations from the original vertex shader into the fragment shader.

Original:

```text
Vertex
   │
   ├── Lighting
   │
   ▼
Interpolated Lighting
   │
   ▼
Pixel
```

Modern:

```text
Vertex
   │
   ├── Position
   ├── UV
   ├── Normal
   ├── Tangent
   └── World Position
		  │
		  ▼
	  Interpolation
		  │
		  ▼
		Pixel
		  │
		  ├── Normal Map
		  ├── Roughness
		  ├── Metallic
		  ├── Specular
		  └── Lighting
```

This allows lighting to respond to the actual surface normal at each pixel.

---

# 17. Normal Mapping

Per-pixel lighting makes recovered/generated normal maps significantly more useful.

The geometry can remain unchanged while surface detail is represented by the normal map.

This provides:

- More detailed specular highlights.
- Better small-scale lighting.
- Better response to dynamic lights.
- Improved perceived geometry.
- No increase in polygon count.

The original normal map, if available, should always be preferred over an AI-generated normal.

---

# 18. Original Shader Preservation

Some Xbox shaders will not map cleanly to `StandardMaterial3D`.

For these cases the system should generate a custom Godot shader.

```text
Original HLSL
	  │
	  ▼
Shader Analysis
	  │
	  ├── Standard PBR compatible
	  │       │
	  │       ▼
	  │  StandardMaterial3D
	  │
	  └── Custom behaviour
			  │
			  ▼
		Generated gdshader
```

The custom shader should preserve the original material semantics while taking advantage of modern per-pixel rendering.

---

# 19. Material Conversion Example

Original:

```text
Material:
	Diffuse
	Normal
	Specular
	Gloss
	Emission
```

Converted:

```text
Godot Material:

Albedo
	← Diffuse

Normal
	← Normal

Specular
	← Specular

Roughness
	← converted Gloss

Emission
	← Emission
```

If the original shader performs custom calculations, the converter should generate a custom shader instead of forcing the material into `StandardMaterial3D`.

---

# 20. SWF Rendering

SWF assets should receive special treatment.

SWF content may contain:

- Vector shapes.
- Text.
- Bitmaps.
- Masks.
- Gradients.
- Movie clips.

Vector and text content should preferably be rendered at the target display resolution rather than AI-upscaled from a low-resolution rasterization.

Bitmap content can use the texture enhancement pipeline.

```text
SWF
 │
 ├── Vector ────────► Native-resolution rendering
 │
 ├── Text ──────────► Native-resolution rendering
 │
 └── Bitmap ────────► AI upscale
```

This should produce substantially cleaner UI and 2D elements at high resolutions.

---

# 21. Cache Format

The preferred final texture format should be GPU-friendly.

KTX2/Basis Universal or an appropriate native GPU-compressed format should be considered.

For example:

```text
cache/
	textures/
		albedo_<hash>.ktx2
		normal_<hash>.ktx2
		orm_<hash>.ktx2

	materials/
		material_<hash>.tres
```

An ORM texture can potentially pack:

```text
R = Ambient Occlusion
G = Roughness
B = Metallic
```

where appropriate.

---

# 22. First-Load Experience

The system should degrade gracefully on slower hardware.

Example:

```text
First Level Load

Original assets
	  │
	  ▼
Game starts
	  │
	  ├───────────────┐
	  │               │
	  ▼               ▼
Original texture   Enhancement worker
					  │
					  ▼
				 Upscale texture
					  │
					  ▼
				  Build material
					  │
					  ▼
					Cache
```

Subsequent loads:

```text
Level Load
	│
	▼
Cache lookup
	│
	▼
Load enhanced texture/material
```

This makes the enhancement cost effectively a one-time cost per asset.

---

# 23. Low-End GPU Considerations

The system should remain usable on older GPUs.

For a GPU such as a GTX 650, processing should be:

- Asynchronous.
- Prioritized.
- Cached.
- Tile-based where required by VRAM limits.
- Configurable by quality level.
- Able to fall back to original assets.

Potential settings:

```toml
[texture_upscaling]

enabled = true
scale = 2
model = "realesr-general"
async = true
cache = true
max_texture_size = 2048
```

A higher-end GPU can use a larger model or higher scale.

---

# 24. Proposed ReXGlue Components

Potential components:

```text
rex_texture
rex_texture_cache
rex_texture_upscaler
rex_material
rex_material_cache
rex_material_converter
rex_shader_analyzer
rex_shader_translator
rex_swf_renderer
```

The exact module boundaries should be determined during implementation.

---

# 25. Recommended Development Order

## Phase 1 — Material Inspection

Build tooling to:

1. Extract original materials.
2. Associate materials with shaders.
3. Enumerate texture samplers.
4. Identify texture semantics.
5. Dump shader/material metadata.

Output should be machine-readable.

Example:

```json
{
	"material": "Stone_07",
	"shader": "Material_Stone",
	"textures": {
		"diffuse": "texture_123",
		"normal": "texture_124",
		"specular": "texture_125",
		"gloss": "texture_126"
	}
}
```

---

## Phase 2 — Basic Godot Conversion

Convert known materials into `StandardMaterial3D`.

Initially do not use AI-generated textures.

Validate visual correctness against the original game.

---

## Phase 3 — Per-Pixel Shader Conversion

Identify shaders where vertex lighting can be moved into the fragment stage.

Validate:

- Diffuse.
- Specular.
- Normal mapping.
- Attenuation.
- Fog.
- Emission.
- Alpha.

---

## Phase 4 — Texture Upscaling

Integrate the Vulkan/C++ neural upscaling backend.

Start with:

```text
Original texture
	↓
2× upscale
	↓
Cache
	↓
Godot texture
```

---

## Phase 5 — Material Map Reconstruction

Add generation/conversion for:

- Roughness.
- Metallic.
- AO.
- Normal.
- Height.

Original maps always take precedence over generated maps.

---

## Phase 6 — Runtime Cache

Implement:

- Hashing.
- Persistent cache.
- Cache invalidation.
- Async processing.
- Priority queue.
- Background processing.

---

## Phase 7 — SWF Integration

Render vector/text SWF content at the target resolution and only upscale embedded raster artwork.

---

## Phase 8 — Advanced Material Conversion

Handle:

- Water.
- Glass.
- Skin.
- Foliage.
- Hair.
- Particles.
- Custom shaders.
- Special effects.

---

# 26. Desired End State

The final system should allow the recomp to take original Fable 2 assets and automatically produce:

```text
Original Xbox 360 Asset
		  │
		  ▼
Material + Shader Analysis
		  │
		  ▼
Semantic Texture Extraction
		  │
		  ▼
Resolution Enhancement
		  │
		  ▼
PBR Map Conversion/Generation
		  │
		  ▼
Shader Modernization
		  │
		  ▼
Godot Forward+
		  │
		  ▼
Vulkan
```

The original game data remains the source of truth, while the recomp provides a modern rendering representation.

The intended result is not merely a texture-resolution upgrade.

It is a **material and rendering modernization layer** that can provide:

- Higher-resolution textures.
- Proper PBR materials.
- Per-pixel lighting.
- Modern normal mapping.
- Improved specular response.
- Modern roughness/metallic workflows.
- Modern Godot Forward+ lighting.
- High-resolution SWF rendering.
- Persistent enhancement caching.
- Hardware-scaled quality settings.

The key principle is that the system should **derive as much as possible from the original materials and shaders rather than hallucinating replacement assets**.

This gives us a path toward significantly improving Fable 2's rendering while retaining the original game's material definitions and artistic intent.

---

# 27. Implementation Status

Audited against the tree on 2026-08-15. This section is the map from the plan above to the
code, and it exists because the plan was written without knowing how much of it the engine
already did.

## 27.1 Already built, and built from the binary

Phases 1 to 3 are substantially done, and the parts that were done are the parts the plan
warned were easy to get wrong.

| Section | Where | Note |
|---|---|---|
| §3.1 original data is the authority | `Engine_MdlData.cs` | The sampler identities are decompile-derived. Two of them were wrong under name-based guessing: record+148 was called `Metallic` and is the **environment map** (s14, pixel bool c6); record+16 was called `Extra` and is **self illumination** (s13, bool c5). |
| §12 Xbox → Godot mapping | `standard_material.gdshader` | Diffuse → `ALBEDO`, normal → `NORMAL_MAP`, self illumination → `EMISSION`, specular → `SPECULAR`. Specular is not routed to emission. |
| §13 gloss → roughness | `standard_material.gdshader` | **Not** `1 - gloss`. The guest builds `power = lerp(c24.x, c24.y, spec.a)`, so the shader takes that exponent and converts it as `sqrt(2 / (power + 2))`. §13's "validate against the original shader" was done first. |
| §14 shader analysis | — | Done statically against `Shaders.sbk`, per blob, rather than by a translator pass. |
| §16 vertex → per-pixel lighting | `standard_material.gdshader` | Lighting is already entirely in `fragment()`. |
| §17 normal mapping | `standard_material.gdshader` | Including the guest's ambient-normal-map darkening term, which scales the **bake only** — the flat global ambient is added afterwards and never darkened. |

The one correction the plan needs: **texture decode is gamma 2.0, not sRGB.** The guest squares
its colour fetches (`r4.yzw = r4.yzw * r4.yzw`). Any new stage that resamples in linear space
has to square on the way in and square-root on the way out, or it shifts every midtone.

## 27.2 Landed by this change

| Section | Where |
|---|---|
| §5 persistent cache | `tex/Engine_TexCache.cs` — `user://cache/textures/<xx>/<hash>.rftx`, sharded |
| §5 cache key | `Engine_TexCache.BaseKey` / `.Key` — see 27.2.1 |
| §6 async workers | `tex/Engine_TexLibrary.cs` — background pool, main-thread apply through `Pump()` |
| §7 priorities | `TexPriority` — `Ui`, `Scene`, `Level`, `Background` |
| §9 per-semantic policy | `tex/Engine_TexSemantic.cs` — `TexFilterClass` per slot |
| §10 adaptive scale | `Engine_TexSemantic.TargetScale` — the §10 table verbatim, clamped per semantic |
| §8 backend seam | `tex/Engine_TexEnhance.cs` — `ITexUpscaler`, `Image` in and `Image` out, no file paths |
| §8 baseline backend | `tex/Engine_TexResample.cs` — classical, no dependency, **off by default** |
| §23 settings + switch | `tex/Engine_TexSettings.cs`, `Engine_TexEnhance.SetEnabled` — see 27.4 |
| §25 phase 6 invalidation | `Engine_TexCache.Maintain` — epoch directories, size budget, background sweep |
| §12 environment mapping | `Engine_TexCube`, `GetCubemap`, `envmap_tex` — 526 records, was rendering nothing |
| §7 priority-0 UI | `Engine_TexLibrary.TrackUi` — the `game://` path, tracked weakly |
| §22/§23 cost | `src/tools/TexEnhanceSoak.cs` — measured, not assumed |
| §21 ORM packing | `tex/Engine_TexOrm.cs`, `orm_tex` — **off by default**, see 27.2.7 |
| §8 neural seam | `src/upscaler/` ABI + `Engine_TexRealEsrgan` — dependency not vendored |

`Engine_MdlMaterial` and the `game://` loader both go through the library now. They used to be
two routes to the same files, and only one of them could ever have been given a persistent cache.

### 27.2.1 Two keys, not one

The decode and the enhancement are keyed separately, and the split matters:

```text
BaseKey = SHA256(source, converter_version)
Key     = SHA256(source, semantic, policy_version, scale, converter_version, backend_id)
```

Untiling and re-encoding a `.tex` produces the same bytes whatever slot bound it. Folding the
semantic into the decode key stored one identical decode once per slot; folding the policy
version in discarded every decoded entry on disk whenever a per-semantic tuning value changed.
Both were happening until this was split out.

### 27.2.1b Invalidation, and why the version is in the path

```text
user://cache/textures/v<converter>/<first two hex>/<hash>.rftx
```

The converter version is in the **path**, not only in the key, and the two do different jobs. In
the key it stops a stale entry being *read*. In the path it makes stale entries *identifiable* —
without it, a version bump leaves every file on disk unreadable, unreachable and
indistinguishable from a live one, because the only way to prove an entry is stale would be to
recompute its key from a source you no longer have. A few hundred megabytes per bump that nothing
could ever clean up. With it, a bump is one directory to delete.

`Maintain()` runs once at startup on a background thread and does two things: delete every epoch
directory that is not the current one, then trim the current one to `REFII_TEX_CACHE_MB`
(default 4096).

Eviction is **oldest-write-first, which is insertion order and not true LRU.** An entry is
written once and never rewritten, so nothing on disk records that a texture was read a hundred
times. Real LRU would mean touching a file on every cache hit — turning the read path into a
write path for the sake of a sweep that runs once per startup. The consequence worth knowing:
revisiting a region evicted earlier in the same session re-decodes it, so a low hit rate after a
budget trim is expected behaviour rather than a defect.

The sweep races the enhancement workers on purpose. A deleted entry is simply re-created on the
next request, and `.tmp` files are skipped so a store in flight is never what gets removed.

### 27.2.1c The swap is in place, and that is what makes it a switch

When an enhancement finishes, the **same `ImageTexture` the caller already holds** has its image
replaced, on the main thread. Nothing registers a callback, nothing holds a weak reference to a
material, and nothing has to be walked to be updated — every material and every node pointing at
that texture shows the new one because it is the same object.

The first version did register callbacks: four weak references to the material per surface, which
a region's worth of materials turned into thousands of live callbacks that a level change then had
to outlive. In-place replacement deleted all of it.

It is also what makes the **runtime switch** possible. `Engine_TexEnhance.SetEnabled(false)`
reverts every enhanced texture to its original by reloading the base from the cache and swapping
it back. Clearing the backend alone would only stop *future* work — everything already on screen
would keep its enhanced image until the next level change, so "off" would not visibly mean off.

Turning it back on is cheap: the results are on disk, so the workers re-apply from the cache
rather than re-running the backend.

The revert reloads rather than retaining. Holding the base `Image` of every texture in memory
against the chance that someone toggles a setting would roughly double a region's resident
texture memory for the whole session.

### 27.2.1d The editor never enhances

The asset browser, the model viewer and the screen viewer exist to show **what is actually in the
player's game data**. An upscaled albedo in the model viewer is a picture of our pipeline, not of
the asset, and anyone checking whether a texture decoded correctly would be looking at a resample
of it.

Two independent guarantees, deliberately:

- `Engine_TexEnhance.Enabled` is false whenever `Engine.IsEditorHint()` is true. It is the first
  condition, not an afterthought. False in an exported build and in a headless tool run, so only
  the editor is excluded.
- The `game://` loader calls `Engine_TexLibrary.Decode`, which only ever reads the **base** key.
  That path is structurally incapable of returning an enhanced texture regardless of any flag.

### 27.2.2 The baseline backend

`Engine_TexResample` is a classical resample — Lanczos for colour, cubic for normals and control
signals — and it is **not a stand-in for Real-ESRGAN**. It is the baseline the neural backend has
to beat, and it exists first for a practical reason: with no backend registered, everything
downstream of `ITexUpscaler` had never executed once. It is what makes the queue, the coalescing,
the re-encode and the main-thread swap testable in a real session.

Colour is resampled in **linear light** — squared on the way in, square-rooted on the way out,
because gamma 2.0 is the guest's own encoding. Averaging gamma-encoded values darkens every
high-contrast edge. This deliberately differs from `Engine_TexMipGen`, which box filters in gamma
space for the mip chain; changing that would alter how existing textures look at distance, which
is a separate question from this one.

Normals are resampled with **cubic, not Lanczos**, and clamped back inside the unit disk. The
guest rebuilds Z as `sqrt(1 - nx^2 - ny^2)`, so an overshooting filter puts a NaN in the shader.

### 27.2.3 Policy corrections, two of them found by running it

- **Specular is scalar, not perceptual.** Its alpha is the exponent selector, not an image, and
  a model trained to invent plausible image detail has no business in a control signal.
- **Environment maps are not resampled at all.** They are cube maps; resampling the faces
  independently opens a seam at every edge. They go back in when the filter is cube aware.
- **The utility-size floor is 128, not 32.** With a 32-texel floor, the first real texture the
  end-to-end probe picked was `Globals/Engine/Textures/alphastipple.tex` — and it turned a 32px
  screen-space dither pattern into a 128px one. A stipple works *because* adjacent texels differ;
  resampling averages away the only thing it has. Below 128 a Fable 2 texture is usually a
  function — a stipple, a ramp, a lookup — rather than a picture. §10's own table starts at 128
  and now so does the policy.

### 27.2.4 Verification

`src/tools/TexCacheProbe.cs`, headless, 50 checks:

```text
Godot_v4.7.1-stable_mono_linux.x86_64 --headless --path Godot \
	--script res://src/tools/TexCacheProbe.cs
```

Byte-exact container round-trip for RGBA8/DXT1/DXT5 with and without mip chains; every key input
shown to invalidate, and the base key shown not to collide with an enhanced one; the §10 table
including the utility-size floor; the resampler's gamma round trip and its normal-disk invariant;
the sweep's stale-epoch deletion, its eviction order and its refusal to touch an in-flight
`.tmp`, against a scratch root rather than the live cache; the settings round trip, including the
`refii.toml` preservation guarantee proved against a COPY OF THE REAL 158-key config with zero
keys lost; and one real texture driven
through the whole §6 path — decoded, enhanced in place, reverted by the switch, and re-applied
from cache when the switch goes back on.

The editor was verified separately with `--editor --quit`: the project opens clean, and the only
`game://` failures are `.ghf` and `.ehf.mat` paths that predate this work and come from a bare
editor open mounting no banks. No `.tex` failures.

`Engine_MdlMaterial.ReportIfChanged` prints what the region's materials actually bound:

```text
[material] 1,320 built: 1,190 normal, 830 specular, 41 self-illum, 6 envmap, 0 orm
```

That line exists because a run could not answer **"are the reflections working?"**. Only 526 of
8,931 records name an environment map — about six per cent — so walking a region and seeing none
is exactly what you would expect whether the feature works or is completely broken, and nothing
was logged when one bound. A count tells those two apart: zero means there were none to draw.

Cache counters are reported every 600 guest steps when they have moved, and at session exit. The
first version of this cache shipped printing nothing on a good run — it moved 359 MB through
1,385 entries on its first real session and left no line in the log about any of it.

## 27.2.5 §15 material classification: the field it would need does not exist

`MdlMaterial.ShaderType` is parsed and never used, and the obvious plan was to classify from it.
**It is 0 on every shipped material record.** Measured by `src/tools/MaterialCensus.cs`:

```text
5,446 .mdl entries: 4,335 parsed, 1,111 header-only stubs, 0 unreadable
parse paths: 4,134 engine-format, 201 fallback (362 materials EXCLUDED)
8,931 material records from the engine path, 1 distinct ShaderType value

value   materials   normal  specul   envmap  selfIll   2ndDif   noDiff
	0       8,931    8,016   5,206      526      102        0      178
```

The exclusion is the part that makes the number mean anything. Only `Engine_MdlEngine` assigns
`ShaderType`; the fallback header parser never touches it, so its materials report the field's
*default* of 0. Pooling the two showed a wall of zeroes that could not distinguish "always 0 in the
data" from "never read at all". Splitting them leaves 8,931 records genuinely read — still one
value.

Three counts in that table match previously verified ground truth exactly (526 envmap, 102 self
illumination, 0 secondary diffuse), which is what makes the rest of it trustworthy.

**So classification cannot come from a type field, because there isn't one.** What the guest
actually uses is the presence mask — which slots are filled selects the shader permutation
(`SpecNormalSecDifParams_SDif` when the secondary diffuse is present, `_Trunk` when record+24 is
absent). Our `standard_material.gdshader` already branches on exactly that through `has_normal`,
`has_specular` and `has_emission`, so §15 is, for this record, **already implemented** — as uniform
branches rather than as distinct material classes.

The doc's other categories are not in this record at all. Foliage is classified by PATH
(`Engine_MdlFoliage.IsFoliagePath`); water and terrain have their own level subsystems. Water,
Skin, Hair, Glass, Particle and Decal have no representation in the .mdl material record, and any
§15 work on them has to start by finding where the guest states them — not by pattern-matching
names.

### The real gap this turned up: environment mapping

**526 material records name an environment map and none of them were being rendered.** Now they
are — see "Environment mapping, end to end" below. The equation is fully decoded from the
recompiled shaders (`0367_pshader_standardmaterial_blt1_glt1_mist0_amb1_nrm1_scr1`,
confirmed identical in shape in `0444_..._sp2`):

```text
r6.x   = dot(D, N);                       // D = -r8.wyz, the camera-to-surface direction
r6.xyw = -N * (2 * dot(D, N)) + D;        // R = reflect(D, N), exactly
   ...  rotated into cube space by r1/r3/r4
r6.xyz = tfetchCube(g_EnvmapSampler, R).xyz;
r6.xyz = r6.xyz * r6.xyz;                 // SQUARED — gamma 2.0, same decode as the albedo
r5.xyz = r2.xyz * r6.xyz + r5.xyz;        // ADDED, weighted by r2
```

`r2.xyz` is the **squared specular RGB** (`r2.x = r10.x * r10.x` and siblings, where `r10` is the
`g_SpecularTextureSampler` fetch). So:

```text
colour += (specular.rgb ^ 2) * (cubeSample.rgb ^ 2)
```

The specular map doubles as the reflectivity mask. Nothing else weights it.

**`g_NeedsEnvmapping` is published but never read.** It is defined as `(1 << 22)` in all 192
`pshader_standardmaterial` permutations and used by **none** of them, while all 192 *do* sample the
cube. So the bool is not the gate — the binding is. A material naming no envmap gets the guest's
fallback bound at s14 and the term multiplies out to nothing, which is the same pattern our
`specular_tex` already uses with `hint_default_black`. This refines the note in `Engine_MdlData`,
which called it a runtime branch.

### compFlag 24 was never a compression flag

Every one of the 36 cube textures failed `Engine_TexData.TryDecode` with
`plane 0: unsupported compFlag 24`, which read as a missing compression scheme and sent the search
after a codec that does not exist.

**A cube level record is not a plane record.** It is:

```text
u32 faceOffset[6]        relative to the level record start
per face:  a standard 48-byte plane record, then that face's data
```

The old parser read the first three words of the face table as a `(compFlag, dataOffset, size)`
triple — so the "flag" was always 24, which is just the byte size of the six-entry table, and the
"size" was always the second face's offset. No new codec is needed; the faces use the existing
compFlags.

Confirmed on three shipped files before a line was written:

| File | Evidence |
|---|---|
| `Katana_Iron_EnvMap` 32×32 | offsets `24 584 1144 1704 2264 2824`, uniform step 560 = 48-byte plane record + 512-byte raw BC1 face; level spans 84→3468 = 24 + 6×560 exactly |
| `ob_augment_black_env` 128×128 | offsets non-uniform because the faces are Huffman (`compFlag 1`); face 0 runs 108→2384 = 48 + 2228 exactly |
| its last mip | offsets `24 80 136 192 248 304`, step 56 = 48 + 8 (minimum BC1 block); 24308 + 24 + 6×56 = 24668 = end of file |

`Engine_TexCube.TryDecode` implements it. All **36/36** decode with full mip chains, every face
exactly the byte length its size, format and mip count demand, and all 36 build a Godot `Cubemap` —
whose `CreateFromImages` independently validates that the faces are square, equal-sized and
same-format, which is the check that would catch a face table read in the wrong order.

**Every shipped texture now decodes: 9,419 of 9,419, 100.00%, zero failures.**
`src/tools/TextureCensus.cs` is the standing check; it had never been run before, which is why a
decoder gap this size was found by accident while looking for something else.

Two measurements I made along the way were wrong and are recorded so nobody repeats them: comparing
*payload size* to raw face bytes measures the compressor, not the face count (two 256×256 nine-level
cubes ship at 67,448 and 91,872 bytes), and `TryParseChain` is the headerless entry point which
rejects anything with a header, so it answers "not a bare-chain .tex" for every cube regardless.

### Environment mapping, end to end

Landed. The chain is:

| Piece | Where |
|---|---|
| six-face decode | `Engine_TexCube.TryDecode` |
| Godot cube | `Engine_TexImage.TryCreateCubemap` / `CubemapFromImages` |
| cache | `Engine_TexCache` container **v2** — a face count, six payloads, one entry |
| library | `Engine_TexLibrary.GetCubemap` |
| shader | `standard_material.gdshader`, `envmap_tex` + `has_envmap` |
| binding | `Engine_MdlMaterial` binds `material.EnvironmentMap` |

**One cache entry per cube, not six.** The faces share a key because they are one asset; six
entries could be half-evicted by the budget sweep and leave a cube rendering with four faces.
Bumping the container version strands nothing — `TryLoadFaces` rejects an unknown version, that is
a miss, and the miss rewrites under the *same* key, so old entries are overwritten in place rather
than orphaned.

**Cubes live in a separate map from 2D textures**, because a `Cubemap` is a `TextureLayered` and
the live map's whole mechanism is the in-place `ImageTexture.SetImage` swap, which a layered
texture has no equivalent of. That costs nothing: the policy for `EnvironmentMap` is
`TexFilterClass.None`, so cubes are never enhanced and never need swapping.

The shader reflects the **incident** direction (`-VIEW`), not the eye vector. The guest builds
`D - 2·dot(D,N)·N` where D is camera-to-surface; reflecting the eye vector instead mirrors the
result through the surface and looks plausible until the camera moves.

`src/tools/TexCubeProbe.cs` checks all of it: 36/36 decode, all 36 build a `Cubemap`, a cold
library read stores and a warm one is served from disk, and the shader compiles and accepts the
cube sampler. That last check exists because a `.gdshader` with a syntax error loads as null and
every surface silently falls back — "the build succeeded" says nothing about shader validity.

The warm-read check earned its place immediately: it failed on the first run with `hit=0` but a
valid cubemap, because `Engine_TexLibrary.Clear` had not been taught about the new cube map. The
"warm" lookup was being answered from memory, so the disk path it existed to exercise never ran —
and every environment cube was pinned for the life of the process across level changes.

## 27.2.5b §7's priority-0 path is live

The plan puts the HUD, the menus and the front end at the **top** of the queue, and they were the
one route that could never be enhanced at all: the `game://` loader hands its texture to Godot and
forgets it, so there was nothing left to swap.

`Engine_TexLibrary.TrackUi` registers it — **weakly**. Godot's ResourceLoader caches those textures
*and frees them*, so a strong reference would pin every menu frame and font page for the life of
the process behind Godot's back, unreachable from anything that reports on memory. A weak one lets
the enhancement land if the texture is still alive and quietly do nothing if it is not.

It is called on **every** load, not just the first, because Godot frees and reloads these: a second
load produces a new `ImageTexture` holding the base image, so the entry is re-pointed at it and
re-requested. That re-request is a disk read, not another resample.

Verified: `Art/GUI/WorldMap/WorldMap_Autumn_A.tex` goes 512px → 1024px in place, loaded through
`ResourceLoader` exactly as the front end loads it.

## 27.2.6 What it costs, measured (§22, §23)

Both sections were assertions with nothing behind them, and the enhancement path had only ever run
over **one** texture. `src/tools/TexEnhanceSoak.cs` runs it at scale, through the real queue,
workers, re-encode, cache and pump — a benchmark that called the resampler in a loop would skip
every part that behaves differently under load.

150 textures, 16-core machine, cold cache:

| | 4 workers | 8 workers |
|---|---|---|
| throughput | 13.9/s | 14.0/s |
| Godot resident peak | +447 MB | +801 MB |

**It does not scale past about four workers.** Doubling them bought 0.1/s and cost 354 MB, so the
default is `cores/2` **capped at 4**. The two commonest core counts in the survey land on 4 and 3
either way, so the cap only bites where it is free.

**The memory alarm was mine and it was wrong.** The first run reported a +2.6 GB managed peak,
which reads as "this needs 2.6 GB". It does not: `GetTotalMemory(false)` counts garbage that has
not been collected, so it measures churn. Forcing a collection puts the settled figure at **±0.0 MB**
— the working set returns to where it started. What was real was the churn itself, and reworking
the resampler's buffers to `MemoryMarshal.Cast` instead of copying arrays cut it from 2,622 MB to
1,283 MB with bit-identical output.

### §23's hardware baseline is out of date

The plan sizes everything against "a GPU such as a GTX 650" — a 2 GB card from 2012. The **July
2026 Steam survey** says the target is nothing like that:

| | Most common | Next |
|---|---|---|
| GPU | RTX 3060 (3.71%) | RTX 4060 Laptop (3.44%), RTX 4060 (3.43%), RTX 5070 (3.40%) |
| VRAM | 16 GB (25.9%) | 8 GB (25.3%), 12 GB (12.9%) |
| System RAM | 16 GB (41.0%) | 32 GB (36.9%), 8 GB (8.0%) |
| CPU cores | 8 (27.9%) | 6 (27.5%), 4 (13.1%) |

So the floor worth designing to is roughly an 8 GB card with 16 GB of system RAM and 6–8 cores.
The worker default was `cores/4` because of the GTX 650 line, which gave the two commonest
machines one or two workers for no reason.

### The disk cost is the real constraint

`TextureCensus` projects the enhanced cache **per texture, from its own dimensions and format plus
its mip chain** — not from a sample average:

```text
8,437 of 9,419 textures (89.6%) would be enhanced, 2,486 M pixels
projected enhanced cache: 9.2 GB, on top of 2.0 GB of decodes
```

An earlier version of this projection said 21.4 GB by averaging 2.6 MB per texture over the first
150 in bank order. That sample was not representative and the number was more than double the
truth; per-texture arithmetic replaced it.

A 4 GB budget with upscaling **on** would therefore evict most of the work continuously and read as
a broken cache rather than a full one. `texture_cache_max_mb` now defaults to `0` = decide: **4 GB
with upscaling off, 12 GB with it on.**

First load for a region is roughly **90–100 s of background work** at this throughput, once, with
the original textures on screen throughout.

## 27.2.7 ORM (§21), and what it can and cannot carry

**Fable 2 ships no AO map, no metallic map, no roughness map and no height map.** A material record
names exactly five textures — diffuse, specular, normal, environment map, self illumination — and
the specular one is already a packed material texture in its own right: RGB is specular colour,
which also weights the environment reflection, and ALPHA is the exponent selector.

So Godot's ORM layout, filled from shipped data, is one real channel and two constants:

```text
R = 1          no ambient occlusion exists to pack
G = roughness  converted from the specular alpha by the guest's own equation
B = 0          the guest sets metallic to zero unconditionally
```

`Engine_TexOrm` builds exactly that, cached and mipped like any other derived texture, and the
shader takes it through `orm_tex` / `has_orm` (AO multiplies the bake, alongside the normal-map
darkening; roughness and metallic replace the derived values).

### §25 phase 5 makes R real

`Engine_TexOrm.CavityFromNormal` derives occlusion from the **divergence of the normal field** —
where neighbouring normals turn toward each other the surface is concave and light reaches it
less. That is arithmetic on data the artist authored, repeatable to the bit, and it adds no detail
that is not already in the normal map. 8,016 of the 8,931 material records name one, so two of
ORM's three channels now carry information and the packing starts to earn its texture.

**Metallic is still not generated, and should not be.** There is nothing in the data to derive it
from, and inventing which surfaces are metal is exactly the generic guess §3.2 puts last.

The whole path stays **off by default** regardless: the guest applies no occlusion at all, so
switching this on makes the game look different from the original. That is a player's choice, not
a default. `texture_orm_ao_strength_percent` controls it; 0 gives the packing with a constant R,
which is what shipped data alone supports.

It also **cannot replace the specular map**, and building it made that concrete: ORM has no slot
for specular colour, and dropping that would take the environment reflection's weight with it
(`colour += specular.rgb^2 * env^2`).

The probe pins the packer to the shader rather than to a typed-in number — two definitions of the
same roughness in two places is exactly how they drift. They agree to within 1/255 (worst 0.00115
over a sampled specular map, range 0.140–0.816).

The AO is checked against **synthetic** normal maps with a known shape, because the property that
matters is that occlusion tracks the normal field rather than merely being non-constant: a flat
normal map must produce no occlusion at all (or every flat wall quietly darkens), and a groove —
normals leaning toward each other across the middle — must come out darker at its centre than at
its flanks. Both hold.

## 27.2.8 §8: the seam is finished, the dependency is not vendored

The managed half is complete and the native half is specified:

| | |
|---|---|
| `src/upscaler/include/refii/upscaler.h` | the C ABI — RGBA8 in, RGBA8 out, five entry points, all allowed to fail |
| `src/upscaler/THIRDPARTY_NOTES.md` | vendoring, weights, and what to measure |
| `Engine_TexRealEsrgan` | the managed backend, P/Invoking that ABI |

`texture_upscaling_model = "realesrgan"` selects it. With the library absent it logs
`refii_upscaler is not present` and **falls back to the classical resampler** — asking for the
neural model and silently getting nothing would be worse than getting the classical one, so the
log says which arrived.

The boundary is a buffer rather than a file on purpose: §8 lists process creation, Python startup,
PNG encode, PNG decode and re-initializing Vulkan per texture as the costs to avoid, and every one
is a consequence of the boundary being a file.

### It is vendored, built and measured

`src/thirdparty/ncnn` (plus its `glslang`) is a submodule; `src/upscaler` builds
`librefii_upscaler.so` with ncnn linked in statically, behind `REFII_BUILD_UPSCALER` which is
**OFF** by default for the same reason `REFII_BUILD_RENDERER_HOOKS` is.

| | classical `resample-1` | neural `4xLSDIRCompactC3` |
|---|---|---|
| throughput | 13.9/s | 5.4/s |
| projected region first load | ~90–100 s | ~257 s |
| managed settled | ±0.0 MB | ±0.0 MB |

Weights are named by `texture_upscaling_weights` and live beside the build, never in the tree. The
**scale is read from the file name** because ncnn cannot be asked, and the backend id carries an
FNV hash of the weight files — so swapping weights invalidates every texture cached from the old
ones, which a name alone could not do.

Two defects only a real load could have found: the resolver that finds native libraries is
installed by static constructors on the Bink, XMA and host-bridge bindings, and **nothing on the
texture path touched any of them** — so a perfectly good library reported as "not present",
indistinguishable from never having been built. And the weights directory pointed at `res://`, the
project, rather than the build output where they actually live.

The question still worth asking is not whether the neural result looks sharper — it will — but
whether it is sharper in a way §3.2 allows. A model trained on photographs invents detail a 2008
console texture never had.

## 27.2.9 It runs in the actual game

Two sessions with `texture_upscaling_enabled = true`, one region, upscaling switched on from the
launcher's settings panel.

| | cold run | next run |
|---|---|---|
| textures enhanced | 1,334 of 1,380 | 1,270 of 1,399 |
| cache hits | 1,578 / 3,236 (48.8%) | **3,120 / 3,126 (99.8%)** |
| entries stored | 1,658 | **6** |
| cache held at startup | 465.9 MB | 1,772.3 MB |
| evictions | 0 | 0 |

**§22's claim is confirmed in the game, not just in the soak.** The second session did essentially
no work — 99.8% hits, six new entries — and the queue drained to zero in the first. The cost is
one-time per asset, as the plan asserted and nothing had yet shown.

The region also reports **88 materials with an environment map and 928 with an ORM texture**, so
both of the features that needed eyes rather than probes are live and on screen in that region.
The `[material]` line exists precisely so this is knowable without guessing.

### Three things the run exposed

**The neural backend never loaded.** `texture_upscaling_model = "realesrgan"` but the log says
`declined to start` and falls back to `resample-1`, because `texture_upscaling_weights` names
`realesr-general-x4v3` and those weights are not beside the build. Everything measured above is the
CLASSICAL resampler. The fallback behaved exactly as designed, and said so.

**Improved defaults do not reach an existing install.** The file carries
`texture_upscaling_workers = 1` and `texture_cache_max_mb = 4096` — explicit values the launcher
wrote when those were the definitions' defaults. Changing a default to `0` = "decide for me"
afterwards changes nothing for anyone whose `refii.toml` already exists, because `EnsureDefaults`
only fills keys that are MISSING. That is the correct rule for a settings file — a build must not
silently rewrite a player's choices — but it means the tuning measured in 27.2.6 is dead on any
install that predates it, and `workers = 1` runs the enhancement single-threaded.

**One region is 1.8 GB of cache, not 9.2.** The 9.2 GB projection is the whole shipped set; a
single region's worth sits comfortably inside the 4 GB budget and evicted nothing. The budget still
wants raising for a full playthrough, but it is not the urgent problem the projection made it
sound.

## 27.2.10 §20 applies after all — and it is now implemented

**This section previously said "there is no SWF". That was wrong**, and it is worth recording how,
because the evidence that produced it was correct and the conclusion drawn from it was not.

`src/tools/BankCensus.cs` counted the bank indices and found no `.swf` and no `.gfx` entries, which
is true. What it could not see is that **the pub games do not live in the banks**: Keystone, Mage
Dice and Wizard's Tower ship as loose `game.swf` plus `.dds` under `<gamedata>/data/pubgames/`,
outside the bank filesystem entirely. A census of the archives is the wrong instrument for an asset
that was never archived, and "the index is the only thing that answers it" was exactly the sentence
that made the gap invisible.

The guest itself settles it: Scaleform GFx is statically linked into `default.xex` (the loader
strings, `GFxLoader read failed - file does not start with a SWF header`, and the pub-game source
paths `C:\dev\mgs\Fable2PubGames\...` are all compiled in), and `sub_82EECD70` builds
`"%s\\game.swf"` and hands it to `GFxLoader::CreateMovie`.

**§20's requirement — render vector and text content at the target resolution rather than upscaling
a rasterisation — is now met literally rather than by analogy.** The pub games' artwork is
triangulated once and drawn by Godot as geometry, so it resolves at whatever the window is:

| | software rasterizer | vector geometry |
|---|---|---|
| build (keystone, 234 placed shapes) | 4.6 s | **1.65 s** |
| memory | 109 MB RGBA8 | **28.8 MB** vertices |
| at 2.5x oversample | 11.9 s / 348 MB | **unchanged — no resolution at all** |

`SwfShapeTriangulator` reproduces the rasterizer's even-odd fill rule exactly — verified at
**100.000% agreement over 3.3 million sample points** across all three games by
`src/tools/SwfTriangulationProbe.cs` — and `SwfVectorShape` mirrors its style resolution, including
the host-bitmap substitution the replacement `.dds` arrives through. Only the embedded raster
artwork is upscaled, which is §20's other half: `PubGameTextures` routes every loose `.dds` through
`Engine_TexLibrary.EnhanceDecoded`, the same policy, cache and backend as everything else.

The Gameface triple (52 screens as `.bgf` + `.fac` + `.bsg`) is still what the *main* game uses,
and that path meets §20's intent as described before — every element is a quad sampling an atlas
through UVs, and the atlases go through enhancement at priority 0.

## 27.2.11 §25 phase 5: metallic, and what the specular maps actually contain

`src/tools/SpecularCensus.cs` opened 600 of the **1,193 distinct specular textures the material
records name** (8,931 records). Two results, one of which corrects an earlier claim in this
document.

**There is real signal for a generated metallic.** Fable 2's specular RGB is a Fresnel term in all
but name — the shipped pixel shader weights the environment reflection with it,
`colour += specular.rgb^2 * env^2` — and a dielectric's F0 is grey and dim while a metal's is
bright and coloured.

```text
chroma      max(RGB)-min(RGB)   p50 0.031   p90 0.165   p99 0.518
luminance                       p50 0.144   p90 0.520   p99 0.932
127 of 600 textures are >=25% BRIGHT AND coloured
```

The median texel is grey and dark; about a fifth of the game's specular maps describe metal. That
is a distribution with a real tail, not uniform noise — had it come back grey everywhere, phase 5
would have got AO and nothing else. `Engine_TexOrm.Metallic` requires **both** conditions, with
smoothsteps rather than thresholds because so many texels sit near any cut that would be chosen.
Neutral metals (polished steel: bright, grey) are the known gap, and under-reporting is the right
direction to fail in — a wrongly metallic surface loses its diffuse colour and reads as a mirror,
while a wrongly dielectric one is merely the shipped look.

**The roughness channel was overstated, and this document said so first.** §27.2.7 described ORM as
carrying "one real channel and two constants". The real count is closer to zero: **584 of the 600
specular maps have no alpha channel at all**. The shader's roughness is
`lerp(c24.x, c24.y, spec.a)`, so for 97% of materials the selector reads 1.0 and the roughness is a
*constant* too. Only 16 textures carry a genuine exponent map, and there it is strongly bimodal
(p50 0.086, p90 1.000).

A measurement trap worth recording: the first run of the census reported the alpha at
p50=p90=p99=1.000, which reads as "the artists set the exponent to maximum everywhere". It was
Godot returning 1.0 for the 584 textures that have no alpha channel to read. Pooling those with the
16 real ones measured the decoder's default, not the data.

## 27.2.12 §25 phase 5: height, and the algorithm the data chose

`src/tools/NormalCensus.cs` over the 1,701 distinct normal maps the material records name:

```text
8,016 of 8,931 records name a normal map (89.8%)
relief fraction   p10 0.193   p50 0.786   p90 0.994    346 of 400 are >=25% non-flat
curl / gradient   p10 0.125   p50 0.255   p90 0.437    max 0.694
```

**The first line says there is relief to recover** — unlike the specular maps, whose median texel
was grey, the normal maps are richly detailed and nearly every material has one.

**The second line chose the algorithm, and ruled out the obvious one.** A tangent-space normal is a
surface gradient, `(dh/dx, dh/dy) = (-nx/nz, -ny/nz)`, so height is its integral — but integration
has a unique answer only if the field is conservative, and a quarter to a half of the shipped
gradient magnitude is *curl*. Artists paint normal maps; nothing makes them integrable. A
cumulative sum along rows would accumulate that inconsistency into visible drift, so
`Engine_TexHeight` solves the least-squares fit instead: the Poisson equation `∇²h = div(g)`, which
discards the curl rather than smearing it.

**Band limited on purpose.** The solve stops at 24 Jacobi passes rather than converging. That
settles local relief — the scale parallax displaces at — and leaves the low frequencies near zero,
which is where a *tiling* texture wants them: a global tilt or bowl across the image makes adjacent
tiles disagree at every seam. The differences are wrapped for the same reason.

**Height rides in the ORM's alpha**, which Godot's convention leaves free, so one texture now
carries every derived scalar a material has. The honest cost: a constant alpha compresses as DXT1
and a varying one forces DXT5, which is why `TrySolve` returns null for a normal map with no relief
and why `texture_orm_height_enabled` exists.

One threshold was wrong on the first attempt and the probe caught it. The relief gate was set at
25%, borrowed from the census's "346 of 400 maps are at least a quarter non-flat" — but that is a
statistic about the *population*, not a threshold for usefulness, and it discarded every texture
whose relief is local. A plate with rivets over a fifth of its area has real height on that fifth.
It is now 2%, just above the 8-bit quantisation floor, with the post-solve range check rejecting
the rest.

## 27.2.13 §22: the prebuild, and the other offer

§22 describes the lazy path, and the lazy path is right for a player who just wants to start:
original assets appear immediately, enhancement happens on a worker, and the cost is one-time per
asset. What it cannot avoid is that the *first* visit to every area is the expensive one — felt as
the game getting sharper a second after you look at something.

`Engine_TexPrebuild` is the other offer, and the one a remaster usually makes: spend the whole cost
once, deliberately, in front of a progress line rather than in front of the game. It is exposed as
**"Build Modernized Assets"** in the launcher's **reinstall** tab, beside unpacking the banks and
compiling the shaders, and as `src/tools/TexPrebuildRun.cs` for a headless run.

**It belongs with the install steps, not with the settings.** It was first put in the settings
panel, which was the wrong place: settings hold *preferences* — `materials_modernized` decides
which look you get — while this decides only WHEN that look is paid for. It is the same kind of
thing as unpacking the banks: a one-off preparation of the player's own game data that makes every
later session cheaper. `src/tools/LauncherPanelGate.cs` checks the placement, that the three
existing install actions survived the move, and that a `Refresh` in both states does not throw.

Measured over the shipped set, upscaling off:

```text
6,563 items (textures by slot, plus ORM pairs)
6,558 built, 5 failed, 73.2 s
```

**Both paths write the same entries under the same keys**, so this is purely a question of *when* —
nothing in it is a second implementation of the pipeline. That claim is the one way a prebuild
fails silently, and it is not visible from either side alone: the prebuild would report thousands
of entries built, the session would report thousands stored, and both would be telling the truth
while the game rebuilt everything. `src/tools/PrebuildGate.cs` checks it the only way that means
anything — prebuild an entry, clear the live map, ask the live path for the same material, and
require **a hit and zero stores**. ORM is the case it tests, because its key is assembled from five
values (exponent range, AO strength, metallic strength, height flag, normal path) and a
reconstruction that differed anywhere would miss every time.

Two design points worth recording:

**It builds no textures at all.** `PrebuildTexture` and `PrebuildOrm` stop at the cache; the
`ImageTexture` is the cheap half and the half that must happen on the main thread. `BuildOrmImage`
was split out of `ResolveOrm` for this, so the live path and the prebuild share one implementation
with only the texture creation lifted off.

**It does not touch the live map.** A prebuilt entry that pinned itself would leave the whole
game's textures resident before the player had loaded a level.

The launcher runs it on a background thread and publishes progress through a field that `_Process`
reads on the main thread — a worker never touches a Control. It also **saves the settings first**,
because the player has very likely just switched the look on in that panel, and building the cache
for the settings they abandoned would be a long wait for the wrong artefacts.

## 27.2.14 Cost-aware eviction

§27.3 recorded that "enhanced entries are not separately budgeted — the sweep treats a decoded entry
and an enhanced one identically, so a trim can evict a ~570 ms resample as readily as a ~70 ms
decode." That is now fixed, and the prebuild is what made it urgent: filling the cache deliberately
means the budget is far more likely to bind, and a trim that spent the expensive entries first
would undo exactly the work the button exists to do.

The container carries a **cost class** — `Decoded`, `Derived`, `Enhanced` — written at store time
rather than inferred from the key, which is a hash and says nothing about how an entry was made.
The sweep sorts on it first and on write time only within a class, so a trim spends the decodes,
which are the entries that cost least to get back.

A class rather than a timing, because recording milliseconds would make the value machine specific
— an entry built on a fast machine would look cheap on a slow one — and what the sweep needs is an
ordering, which is the same everywhere.

`ContainerVersion` went 2 → 3 for the extra byte. **That strands every existing entry**: the version
is checked on load and a mismatch reads as a miss, so the old files sit unreadable until the budget
sweep reclaims them. Entries the current build cannot read report `Decoded`, which puts them at the
front of the eviction order — exactly where they belong.

`src/tools/EvictionGate.cs` writes the expensive entries **first**, so age alone would evict exactly
the ones that must survive, and asserts they all do. It assembles the headers by hand rather than
through `Store`, and that mattered: the sweep reads the cost at a fixed offset and the first attempt
computed it as "current position plus n", landing on the alpha flag — which would have sorted the
whole cache by transparency. A test that wrote through the same code that reads would have agreed
with itself and proved nothing.

**One measurement worth recording, because it nearly became a wrong fix.** After the first full
prebuild the cache held 4.3 GB across 9,867 entries, against a 4 GB default budget with upscaling
off — which reads as a badly sized default. It is not: that prebuild stored 3,278 entries, so most
of the 9,867 were accumulated across earlier runs at different settings (base decodes, resample
output, neural output, several ORM key variants). The per-texture projection in 27.2.6 — 2.0 GB of
decodes, 9.2 GB enhanced — still holds, and the budget is sized for it. The real observation is that
**one cache legitimately holds several configurations at once**, and nothing evicts a variant just
because the player has stopped using it. Cost-aware eviction makes that safe rather than solving it.

## 27.2.15 §21: the cache format, and what KTX2 would actually have bought

§21 asks for "a GPU-friendly ... KTX2/Basis Universal **or an appropriate native GPU-compressed
format**". The `.rftx` payload has always been exactly that — the bytes `Image.GetData()` returns
for a block-compressed image — so the requirement was met from the start and KTX2 was named as
something to *consider*, not a deliverable. What it would still have bought is **size**, through
Basis supercompression.

That is now bought without taking on a container to parse. `ContainerVersion` 4 deflates the
payload. Measured over 200 random cache entries:

```text
99.3 MB  ->  45.6 MB   (45.9%, deflate level 6)
```

which takes the projected 11.2 GB whole-game total to roughly 5.1 GB. Level 6 rather than
`SmallestSize` because the higher setting costs several times the CPU for a couple of points, and
the prebuild runs this 6,563 times in a row.

It compresses far better than block-compressed data should, and the reason is worth knowing: a good
share of the cache is **not** block compressed — the uncompressed RGBA8 entries the CPU-sampled
paths ask for (the pub games' `-raw` variants), and every mip tail with large flat regions.

Verified by a full prebuild and a second run over it: **6,558 of 6,558 hits, 0 stored**, so the
deflated container round-trips exactly. Cold 94.0 s, warm 23.8 s.

A cube's faces are deflated **together**, not one stream each: six faces of one asset are near
enough identical in structure that compressing them jointly is meaningfully better, and the reader
wants a single buffer anyway. `Inflate` is given the expected length and rejects anything that does
not inflate to exactly it — `Image.CreateFromData` asserts on a buffer of the wrong size, so a
corrupt entry is turned into a cache miss here rather than a crash two frames later.

**A regression the gate caught in itself.** `EvictionGate` hand-writes container headers, and it had
copied the version as a literal `3`. The bump to 4 made every entry it wrote fail the version
check, so the sweep classified them all as `Decoded`, fell back to pure age order, and the gate
failed reporting precisely the inversion it exists to detect. The constant is now `internal` and
shared. A test that has to be edited in step with the thing it tests eventually will not be.

## 27.2.16 §11/§15: the classification is the shader family, and it is structural

This was recorded as blocked, on the grounds that the `.mdl` material record has no family field:
`ShaderType` is always 0 for engine-format records, and its value 2 changes the record LAYOUT to a
68-byte stub, which makes it a record-kind discriminator rather than a shader family. All true —
and **`ShaderType` being 0 is consistent, because nothing in the binary reads a family from a
material record at all.**

**The family is chosen by the draw path**, through a virtual call on the render-material class.
Verified directly: `sub_82B24310`, the builder call site for `standardmaterial`, has exactly **one
xref and it is `data`** — a vtable slot at `0x821351E8`, with no code callers. The same holds for
the fur and tree builders. There is no family parameter anywhere; each family has its own
compile-time technique registry, its own lookup function and its own cache.

Six families have registries, identified by the literal binding-index bases their generated
registration functions load. Verified for `standardmaterial` by disassembly:

```text
82AC24F8  li r11, 0x17F      ; 383 = first standardmaterial PIXEL binding
82AC2500  li r30, 0x11F      ; 287 = first standardmaterial VERTEX binding
```

| family | registry | binding block |
|---|---|---|
| `landscapematerial` | `0x834A5834` | VS 207–210, PS 211–242 |
| `furmaterial` | `0x834A585C` | VS 260–262, PS 263–286 |
| `standardmaterial` | `0x834A5870` | VS 287–382, PS 383–894 |
| `repeatedmaterial` | `0x834A5884` | VS 895–910, PS 911–1038 |
| `outlinematerial` | `0x834A58AC` | VS 1072–1075, PS 1076 |
| `treematerial` | `0x834A58C0` | VS 1077–1156, PS 1157–1316 |

(`particlematerial` and `postprocessingmaterial` occupy binding blocks too but are not surface
material families.) The blob counts quoted earlier — 192/44/32/32/8 — are *distinct payloads*; the
binding counts are larger because many bindings alias one payload.

Three corrections to what this section said before it was decompiled:

- **Six families, not five.** `outlinematerial` exists, and so do particle and post-processing
  blocks.
- **The structural reading was a hypothesis and is now confirmed**, by the vtable-only xrefs rather
  than by the names looking geometric.
- **Skinning is a PERMUTATION, not a family.** `sub_82B31D80` enumerates a 4x2x4x2x3 = 192-slot
  table over skin, screen, ambient, normal and specular dimensions — which is why animated meshes
  still use `standardmaterial`, and why the 192 figure appears on both sides.

**What `repeatedmaterial` is:** the instanced repeated-mesh (scatter/foliage) path,
`PRIMITIVE_INSTANCE_REPEATED_MESH`. Its extra features are geometric and come from the renderer
context rather than the material — grass, wind and collision flattening, written by the submit at
`sub_8222B600` from a KIND enum on the mesh definition. It differs from `standardmaterial` by an
instanced vertex stream and vertex animation, and by *lacking* the skinning, ambient and specular
dimensions.

**What this means for our engine.** Select the family from the geometry kind being built —
static/animated mesh to standard, SpeedTree to tree, scattered instance batch to repeated,
heightfield patch to landscape, fur shell pass to fur — and take only the PERMUTATION from the
material. `standard_material.gdshader` covers `standardmaterial`, which is the largest block by far.
The two permutation bits the material record genuinely contributes are on the render-material
object's 180-byte texture/param records, not on the `.mdl` record: bit `0x04` = normal map present,
bit `0x10` = secondary diffuse.

So §11/§15 is no longer blocked; it is scoped. What remains is implementing the other families,
which is §18's work and wants a reason per family rather than a sweep.

## 27.2.17 §18: satisfied by construction, and more strongly than it asked

§18 asks for a decision: a material that maps cleanly onto `StandardMaterial3D` uses it, and one
with custom behaviour gets a generated `gdshader`.

**That decision has been resolved to the custom branch everywhere, from the start.** No surface in
this engine has ever used `StandardMaterial3D`. Every one goes through a hand-written shader derived
by decompiling the recompiled originals — `standard_material.gdshader`, `terrain_splat.gdshader`,
`water.gdshader`, `sky.gdshader`, `clouds.gdshader`, the celestial set, `fx_particle*`. The terrain
shader alone carries 26 citations of specific guest behaviour, down to which fetch is squared and
why the cliff is triplanar where the ground is not.

The one word not met is *generate*. We derived these rather than generating them from HLSL analysis,
and for a fixed asset set that is the better trade: generation would be machinery whose output is
six files that already exist, and it could not produce the comments explaining which instruction in
which shader each line came from. §18's intent — preserve the original semantics while taking
advantage of per-pixel rendering — is what those files are.

### What genuinely remains, and it is small

With 27.2.16 settling that the family is structural, the six families divide cleanly:

| family | status |
|---|---|
| `standardmaterial` | `standard_material.gdshader`, derived |
| `landscapematerial` | `terrain_splat.gdshader`, derived |
| `treematerial` | **no consumer** — nothing in the engine builds SpeedTree geometry |
| `furmaterial` | **no consumer** — no fur shell pass |
| `outlinematerial` | **no consumer** — no outline pass |
| `repeatedmaterial` | consumer exists; one feature missing (below) |

Implementing a family nothing draws is dead code, so the first three wait on the geometry, not on
the shader.

**`repeatedmaterial` is the real gap, and it is wind.** We batch props into a `MultiMesh`, which is
our own optimisation rather than the guest's repeated-mesh primitive, and we draw vegetation static
where the guest animates it. The guest's flags are read in `sub_8222B600`, verified by disassembly
(base `r29` = the mesh definition, `r8` = `*(r29 + 0xD4)`, a sub-record):

```text
8222b690  lwz  r8, 0xD4(r29)     ; +0xD4 -> sub-record
8222b694  lbz  r7, 0x200(r29)    ; +0x200 = WIND
8222b69c  lbz  r6, 0x6F(r8)      ; sub +0x6F = COLL
8222b6a0  lwz  r11, 0x64(r8)     ; sub +0x64 = KIND enum
8222b6a4  addi r5, r11, -4       ; kind == 4  ->  GRASS
```

The wind DRIVER is already on our side and already parsed: `Engine_EnvironmentTheme` carries
`HasWind`, `WindStrengthMin/Max`, `WindXyRotationMin/Max`, and `Engine_LevelScene` already resolves
an azimuth and a strength from them.

**Wind is now implemented end to end, and the signal was not a GDB property at all.** That was the
wrong place to look: the flags live in the mesh's own file header, in the 88 bytes
`Engine_MdlData` was skipping wholesale.

`sub_82B8F1B8` parses the header block and hands its tail to `sub_82B8F568`, whose stream order and
widths are u8, u8, u8, u8, f32, f32, u8, u8, f32, f32, u8, u8, f32 — **28 bytes, exactly the end of
the 104-byte header**, so the block starts at file offset 76. `sub_8222B600` reads the first as a
KIND (`== 4` is grass) and `sub_82B201D0` the second as a movement mode, with two floats for
amplitude and speed.

**An offset derived by arithmetic is a hypothesis until something independent agrees**, so it was
checked against the shipped set by `src/tools/WindGate.cs`:

```text
5,446 MeshFile headers, 173 sway (3.2%), 38 grass-lit
of those that sway: 173 name a plant, 0 do not
```

**173 of 173, with zero false positives across the whole game.** The mode separates as cleanly as
the kind: 1 is low plants (turnip, bracken, bramble), 2 is trees (beech, oak, silver birch,
saplings) — which is why the guest only tests `== 1` on the repeated-mesh path, since trees are
drawn by `treematerial` instead. Model names were used to CHECK the decode and never to make it;
§3.1 forbids classifying by file name, and a gate that classified by name would be testing itself.

The consumer is `standard_material.gdshader`'s `vertex()` stage: the bend is anchored at the base
and expressed as a FRACTION OF THE SURFACE'S OWN HEIGHT, phased by world position so a `MultiMesh`
field does not bend on one beat, and summed over two non-harmonic octaves normalised to +/-1 so the
amplitude means what it says. `Engine_MdlMesh` sets it per surface from the header — a material
cannot know this, the mesh can.

**The first version was a hurricane, and the mistake is worth recording.** It multiplied the mesh's
authored amplitude by an arbitrary 20 and then by the theme's `WindStrength`, and bent every tree in
Bowerstone flat. `WindStrength`'s UNITS WERE NEVER ESTABLISHED — a census of the shipped themes
could not recover a plausible range for it — so that was a gain applied against an unknown scale,
the same error as gaining a term ahead of the auto-exposure. It is not used at all now.

**Every number is the file's.** `WindSkewMaxAmplitude` is 0.05 on every shipped mesh and is a
fraction of height; `WindSkewMaxSpeed` is 1.0 and is the rate. The only constant chosen here is that
a tree leans 1.0x its authored amplitude and a low plant 0.6x, because we have one surface shader
where the guest has two families and never has to weigh them against each other. The result:

```text
plant, 0.3 m tall   ->   0.9 cm of sway
plant, 1.0 m        ->   3.0 cm
tree,  4.0 m        ->  20.0 cm
```

The theme still supplies the DIRECTION, as a unit vector, and it is the same one the rain uses, so
vegetation and weather cannot disagree about which way it blows.

Two limitations, stated rather than hidden: wind is applied when a material is BUILT, so a weather
change mid-level does not retro-fit surfaces already standing (fixing it wants a global shader
parameter, with its own registration-order concerns); and `wind_amplitude` defaults to 0, so
anything that does not go through `Engine_MdlMesh` stays still.

One implementation note worth keeping, because it cost a compile: Godot rejects an early `return`
in a processor function — *"Using 'return' in the 'vertex' processor function is incorrect"* — and
the shader then fails as a whole, taking every uniform with it. `ShaderGate` caught it immediately
by reporting 0 uniforms; a gate that only checked "does the file load" would not have, because a
broken shader still loads.

## 27.2.18 Tree canopies are not lit per pixel, and that is why ours look flat

The translator already writes readable HLSL beside its SPIR-V — `output_path + ".hlsl"` — so no
extra tooling was needed after all. `treematerial` splits canopy from trunk as a PERMUTATION:
`0544_pshader_treematerial_blt1_glt1_mist1` against `0545_..._trunk1`. Diffing them isolates the
canopy exactly.

**The trunk is lit normally.** It references `g_GlobalLightParamsPS`, takes a `dot(L, N)`, and
samples the screen shadow map — ordinary directional lighting.

**The canopy does none of that.** `g_GlobalLightParamsPS` is not referenced anywhere in it, and
there is no `dot` against a normal. Its whole shading is:

```text
r0.w   = tfetch2D(g_ScreenShadowMap, screenUV).x
r6     = tfetch2D(g_DiffuseTextureSampler, uv)
r1     = r6 * r6                                   // gamma 2.0, as everywhere else
r0.w   = r0.w * c255.w + c255.z                    // c255.zw = (0.4, 0.6) -> shadow*0.6 + 0.4
r2.xyz = r0.www * r2.zyx + r7.xyz                  // r2, r3 = iTexCoord2, iTexCoord3
r7.xyz = r0.www * r3.zyx
r1.xyz = r3.xyz * r2.zyx + r1.xyz                  // two interpolated light terms, combined
oC0.xyz = g_GlobalAmbientAndBrightness.www * g_StandardMaterialPSConstants(2).xyz * r2.xyz
```

So a canopy is lit by **two per-vertex interpolated colour terms** — computed in the tree vertex
shader and arriving as `iTexCoord2`/`iTexCoord3` — modulated by a shadow that is floored at 0.4 and
never reaches black, then by ambient and the material tint. It is the classic two-tone canopy: sky
term and sun term blended per vertex, which gives a canopy volume without any per-pixel normal.

**That is exactly why ours reads as a flat bright blob.** We draw canopies through
`standard_material.gdshader`, which applies per-pixel directional lighting using the leaf CARD's
normal. A leaf card's normal is a poor description of a canopy, so every card takes roughly the same
`dot(L, N)` and the whole canopy flattens into one tone.

**The vertex shader says where the two colours come from.** `0521_vshader_treematerial_...`:

```text
r6.xyz = r4.www * g_GlobalLightParamsVS(8).xyz            // sky colour x term
r7.xyz = r4.zzz * g_GlobalLightParamsVS(7).xyz + r6.xyz   // sun colour x term + sky
r1.xyz = saturate(-r7.xzy + c253.www)                     // WRAPPED, not clamped at zero
oTexCoord3.xyz = r6.xyz;  oTexCoord2.xyzw = r7.xyzw;
```

Two hemisphere terms — sky and sun — and the crucial detail is `saturate(const - x)` rather than a
clamp at zero: **the term is wrapped**. Light does not stop dead where the surface turns away.

**Implemented** as a `foliage` uniform on `standard_material.gdshader`, set for every mesh whose
header says `MovementMode == 2`. It replaces the normal-map darkening with a wrapped hemisphere
term, which is what restores the gradient the guest gets per vertex from the same normal we already
have. The darkening is dropped with it: it exists to shade a solid surface's relief, and a canopy
has none — keeping it would put the flat tone back by another route.

**One part of the guest's line is deliberately NOT reproduced.** Its canopy applies
`shadow * 0.6 + 0.4` to a 0..1 scalar from the screen shadow map. We have no such scalar — `bake` is
a probe COLOUR — and the arithmetic does not transfer: written against a colour the same expression
is `bake * 0.6 + 0.4 * bake`, which is `bake`, a no-op wearing a comment explaining what it does.
That line was written, spotted and removed. The floor belongs wherever a real shadow term lands.

The canopy/trunk split is a permutation rather than a mesh flag, so `MovementMode == 2` marks the
whole tree — trunks get canopy lighting too. That is the known imprecision, and it is a far smaller
error than lighting the canopy as a solid.

## 27.2.19 Aerial perspective: the curve was firing, the in-scatter was not reaching foliage

A Bower Lake screenshot settled a question two sections had been guessing at: **fog IS enabled and
the depth curve IS firing.** The distant hills haze toward the sunset exactly as they should.

But the trees standing on those hills were near-black silhouettes against them. That is not a
missing curve — terrain and canopy are at the same depth and receive the same transmittance — so
the difference had to be in what each surface does with the in-scatter.

`fable_atmosphere_apply` weights the in-scatter by coverage:

```glsl
emission = (emission * transmittance) + ((1.0 - transmittance) * fog_colour * alpha);
```

That weight is right for an alpha-BLENDED surface, which must not add a full fog term over whatever
shows through it. It is wrong for an alpha-TESTED one. **A scissored pixel has already been
decided** — it either survived the clip or it is gone — and the survivor covers its pixel
completely. Passing the leaf texture's alpha there treats an opacity MASK as a coverage weight.

The symptom follows exactly: terrain has alpha 1 and took its full in-scatter, while every leaf card
took a fraction of one, so foliage got *darker* with distance where everything around it got
hazier. `standard_material.gdshader` now passes 1.0 when `alpha_scissor_on`, and the texture's alpha
only when the surface is genuinely blended.

## 27.3 Not started

**Nothing in the plan is unstarted.** §8's native library is built (27.2.8), §20 is implemented
(27.2.10), §21 is met and the size benefit taken (27.2.15), §11/§15's classification is decompiled
(27.2.16) and §18 is satisfied by construction (27.2.17). Adopting KTX2 itself would now buy interop
and nothing else; the container stays deliberately dumb.

One thing is *scoped but not built*: the tree, fur and outline families, which wait on geometry the
engine does not yet build rather than on this pipeline. Wind on vegetation is done (27.2.17).

§25 **phase 5 is complete**: every map the plan lists is now either shipped or derived. **AO** from
the divergence of the normal field (`CavityFromNormal`), **metallic** from the specular map's colour
(`Engine_TexOrm.Metallic`, 27.2.11), **height** from a Poisson solve over the normal gradient
(`Engine_TexHeight`, 27.2.12), **roughness** from the guest's own exponent equation, and **normal**
shipped. All four derived channels ride in one ORM texture, R/G/B/A.

The **consumer** is wired too. `standard_material.gdshader` samples the ORM's alpha as a height and
displaces the texture coordinate by it — offset mapping rather than a full occlusion march, one tap
and no loop, because this runs on every surface in the world. The view direction is taken into
tangent space through the three dots against `TANGENT`/`BINORMAL`/`NORMAL`, the relief is centred on
0.5 so the solved mean does not move, and `tangent_view.z` is floored at 0.4 — it goes to zero at
grazing incidence and an unfloored divide is the smear that makes bad parallax obvious.

**Every fetch uses the displaced coordinate**, not just the albedo: a shader that offset only the
colour would slide it off its own normal and specular. `parallax_scale` defaults to 0.02 and is a
uniform, because the height is normalised to its own range — Poisson fixes a gradient and leaves the
additive constant free, so there is no absolute depth to inherit and the displacement has to be
chosen rather than derived.

So phase 5 is end to end: derived, packed, cached, bound and sampled. ORM stays **off by default**
regardless, because every one of these channels is a deliberate departure from how the original
looked.

**A policy-version bump still strands enhanced entries in place.** The epoch directory is keyed
on the converter version only, so those orphans are reclaimed by the size budget rather than
identified and deleted. Correct, but slower than it needs to be.

## 27.4 Settings

**`refii.toml` is the one settings file.** §23's keys live there:

```toml
# Textures
materials_modernized = true         # THE switch: the remake, or the game as it shipped
texture_upscaling_enabled = true    # follows materials_modernized unless set
texture_orm_enabled = true          # follows materials_modernized unless set
texture_upscaling_model = "resample"
texture_upscaling_workers = 0       # 0 = half the cores, clamped 1..4
texture_cache_enabled = true
texture_cache_max_mb = 0            # 0 = 4 GB, or 12 GB with upscaling on
```

**`materials_modernized` is the switch, and it defaults ON.** Refii is an
Oblivion-Remastered-style remake: the modernized look is the product, and the switch exists so a
player can ask for the original instead — exactly the choice that remaster offers. Off restores the
shipped assets precisely: no upscaling, no ORM, so no derived occlusion, no derived metallic, no
derived height and no parallax, with the material path falling back to the specular map and the
shader's own roughness.

This corrected a defaults design that had grown backwards. Each derived channel had been switched
off individually, each with a comment arguing that it "departs from how the original looked" —
which is an argument against the point of the project rather than for caution, and left the remake
assembled out of five keys nobody would find. The per-channel keys remain, and an explicit one
still wins over the master, so "modernized materials without the upscaler" is one line rather than
a contradiction.

Two of the three do not take effect at the same moment, which is worth stating plainly:
**upscaling is live** — `Engine_TexEnhance.SetEnabled` swaps every texture in place and reverts
them on the way back — while **ORM is bound into materials when they are built**, so it applies at
the next level load. `src/tools/SettingsGate.cs` checks the cascade three ways: master on, master
off, and an explicit key beating the master.

Two things had to be fixed before that was safe, and both are worth recording because neither is
visible from reading the file.

**`WriteValues` was destroying keys it did not recognise.** It rebuilt `refii.toml` from the
definition list and wrote nothing else, so any key without a definition was deleted the next time a
player saved anything in the settings panel. The failure was quiet and specific: set an option,
open settings, change the volume, and it is gone with nothing said. It now writes unrecognised keys
back under an `# Other` heading, which also protects the reverse case — a config written by a newer
build, opened by an older launcher, is no longer downgraded on first save.

**`RuntimeCVars` is generated** from the native runtime's own cvar registry, so it cannot describe
anything Godot owns, and hand-editing it would last until the next regeneration.
`RuntimeCVarsGodot` is the hand-maintained other half, merged through `RuntimeCVarSet.All`, which
every reader, writer and settings-panel consumer now goes through. The generated list wins on a
name collision, so a stale hand-written duplicate can never shadow a real native cvar. The texture
keys are therefore first-class settings — type, default, description, and a place in the launcher
panel — rather than debris that survives only by the preservation path.

**One known cost.** These keys are not in the native cvar registry, so a *standalone native* run
logs `Config: unknown cvar` once per key. It does not happen in the Godot-hosted game: embedded,
the guest looks for `refii.toml` beside the host binary, finds none, and never loads it. Silencing
it would mean registering the cvars natively, which needs a native rebuild.

Environment variables override the file for one session and are never persisted back — that is how
a developer A/Bs a run without editing the player's settings. They override **one key at a time**,
so overriding the worker count does not silently reset the enable flag to its default:

```text
REFII_TEX_UPSCALE=1     REFII_TEX_MODEL=resample|realesrgan       REFII_TEX_ORM=1
REFII_TEX_CACHE=0       REFII_TEX_WORKERS=N       REFII_TEX_CACHE_MB=N
```

`texture_upscaling_workers` and `texture_cache_max_mb` both take `0` meaning "decide for me", and
those defaults are resolved **after** the environment overrides — deriving the budget from the
file's upscale flag and then letting `REFII_TEX_UPSCALE` flip that flag would size the cache for
the mode the session is not in.

### 27.4.1 The switch

`Engine_TexEnhance.SetEnabled(bool, persist)` is the runtime switch, safe to call mid-session:

- it flips a runtime override that outranks the file — without one, the toggle would flip the
  backend and then every request would still be refused by a settings read reflecting what the
  file said at *startup*, so it would appear to do nothing until the next launch;
- it reverts or re-applies every texture already on screen (27.2.1c);
- `persist: true` reads the whole of `refii.toml`, changes its one key, and writes it back.
  `WriteValues` emits the entire file from the values it is handed, so passing it anything less
  than everything currently in there would be a truncation wearing a save's clothes — the very
  failure that made this setting unsafe to keep in `refii.toml` in the first place.

Enhancement is **off unless asked for**. It changes how the game looks and spends CPU and memory
doing it, and neither is something to start doing to a session because a build happened to land.
The persistent cache is on either way — that one has no visible consequence.

An unrecognised model name registers nothing and says so in the log. Treating a typo as "on" would
mean a misremembered model name silently produced the built-in resampler's output and got
attributed to the model.