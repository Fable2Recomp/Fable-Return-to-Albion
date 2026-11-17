---
name: refii-shaders
description: >-
  Use this skill when authoring, modifying, auditing, or debugging shaders (.gdshader,
  .gdshaderinc, .glsl) in Refii. Covers the scene-referred HDR pipeline, PBR mod asset
  isolation, legacy 2008 SDR texture handling, emission boost rules, and avoiding intermediate
  clamping.
---

# Refii Shader Development & HDR Scene-Referred Pipeline Guide

This skill governs the architecture, rules, and best practices for writing, modifying, and debugging shaders (`.gdshader`, `.gdshaderinc`, and `.glsl` compute passes) in Refii.

---

## 1. The Core Scene-Referred Contract

Refii uses a **pure scene-referred rendering pipeline**. All intermediate rendering stages operate in unbounded linear physical radiance $[0, \infty)$ stored in FP16 (`RGBA16F`) render targets.

> [!IMPORTANT]
> **The Golden Rule**: Shaders authoring or processing intermediate scene content (geometry, dynamic lights, particles, sun rays, halos, additive effects, bloom cut) must be **strictly display-agnostic**:
> 1. **NEVER clamp intermediate values to 1.0**: Do not use `clamp(col, 0.0, 1.0)` or `saturate()`. Only clamp against negative radiance/NaN (`max(col, vec3(0.0))`).
> 2. **NEVER branch on SDR vs. HDR**: Shaders must not check display headroom or HDR state during scene evaluation.
> 3. **NEVER apply early tonemapping or display gamma**: Tonemapping and display mapping are strictly centralized in the presentation pass (`fable_postfx_composite.glsl`).

---

## 2. Asset Radiance: PBR Mod Assets vs. Legacy 2008 Assets

Refii runs two categories of assets side by side:
1. **Shipped 2008 Vanilla Assets**: Authored for the Xbox 360 SDR framebuffer ($[0, 1]$ range). Emissive textures were gamma 2.0 encoded.
2. **Modern PBR Mod Assets**: Authored with linear textures and physical radiance values.

### 2.1. The `is_pbr` Uniform Rule
Every surface and particle shader that samples textures or evaluates emission must declare:
```glsl
uniform bool is_pbr = false;
```

### 2.2. The `fable_emission_boost` Isolation Rule
`fable_emission_boost` is a global uniform (`global uniform float fable_emission_boost`) representing the display headroom multiplier (`peak / paper_white`) needed to lift 2008 SDR textures into HDR headroom.

> [!CAUTION]
> - **Legacy Assets (`is_pbr == false`)**: MUST square the sampled texture (guest gamma 2.0 decode) and MUST be scaled by `fable_emission_boost`.
> - **Modern PBR Assets (`is_pbr == true`)**: MUST use linear texture samples directly and **MUST NEVER BE MULTIPLIED BY `fable_emission_boost`**.
> - **Dynamic Lights**: MUST NEVER be touched by `fable_emission_boost`.

---

## 3. Shader Templates & Patterns

### 3.1. Surface Material Emission Pattern
Used in [`standard_material_body.gdshaderinc`](file:///f:/Documents/programming/refii/Godot/src/Engine/material/standard_material_body.gdshaderinc):

```glsl
uniform bool is_pbr = false;
uniform float self_illum_scale = 1.0;
uniform sampler2D emission_tex : source_color, filter_linear;
global uniform float fable_emission_boost = 1.0;

void fragment() {
    ...
    if (has_emission) {
        vec3 selfIllum = texture(emission_tex, uv).rgb;
        if (is_pbr) {
            // Modern PBR mod assets: selfIllum and self_illum_scale carry authored physical radiance directly
            EMISSION += selfIllum * self_illum_scale;
        } else {
            // Shipped 2008 SDR assets: squared per guest pixel shader (r3.xyz = r3.xyz * r3.xyz)
            // and scaled by fable_emission_boost so legacy SDR textures can emit into HDR headroom
            EMISSION += (selfIllum * selfIllum) * (self_illum_scale * fable_emission_boost);
        }
    }
}
```

### 3.2. Particle / Billboard Pattern
Used in [`fx_particle_add.gdshader`](file:///f:/Documents/programming/refii/Godot/src/Engine/fx/fx_particle_add.gdshader) and [`fx_particle.gdshader`](file:///f:/Documents/programming/refii/Godot/src/Engine/fx/fx_particle.gdshader):

```glsl
shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

#include "res://src/Engine/fx/fx_billboard.gdshaderinc"

uniform sampler2D fx_tex : filter_linear, repeat_enable;
uniform float fx_alpha_ref = 1.0;
#include "res://src/Engine/fx/fx_soft.gdshaderinc"
uniform float fx_src_alpha = 1.0;
uniform bool is_pbr = false;
global uniform float fable_emission_boost = 1.0;

void fragment() {
    // Zero-discard `depth_draw_never` blend path: compute derivatives before branching, guard
    // shading inside `if (!should_discard)`, and output the blend identity (`ALPHA = 0.0`)
    // otherwise so hardware Early-Z remains active without `discard;` hazards.
    vec2 uv_dx = dFdx(UV);
    vec2 uv_dy = dFdy(UV);
    vec4 texel = textureGrad(fx_tex, UV, uv_dx, uv_dy);
    bool should_discard = (texel.a * COLOR.a * 255.0 <= fx_alpha_ref);

    ALBEDO = vec3(0.0);
    ALPHA = 0.0;

    if (!should_discard) {
        float fade = fx_soft_fade(texel.a * COLOR.a, VERTEX, SCREEN_UV, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);
        
        // PBR particles use linear texel radiance directly;
        // Legacy particles use gamma 2.0 decode (squaring) and legacy emission boost.
        vec3 tex_rgb = is_pbr ? texel.rgb : (texel.rgb * texel.rgb * max(fable_emission_boost, 1.0));
        vec3 rgb = max(COLOR.a * COLOR.rgb * tex_rgb, vec3(0.0));
        
        ALBEDO = rgb * fade;
        ALPHA = mix(1.0, texel.a * COLOR.a * fade, fx_src_alpha);
    }
}
```

### 3.3. Dynamic Lighting Evaluation
Used in [`fable_lighting.gdshaderinc`](file:///f:/Documents/programming/refii/Godot/src/Engine/material/fable_lighting.gdshaderinc):

Godot's clustered/forward+ lighting passes `LIGHT_COLOR = color * energy * PI`.
To recover physical radiance:
```glsl
vec3 fable_direct(vec3 normal, vec3 light, float attenuation, vec3 light_colour) {
    // Normal dot light is clamped to [0, 1], but radiance (light_colour / PI) is UNCLAMPED
    return clamp(dot(normal, light), 0.0, 1.0) * attenuation * (light_colour / PI);
}
```
Dynamic light energy in C# (`node.LightEnergy`) is passed as raw unclipped physical intensity.

### 3.4. Intermediate Compute Post-Processing Passes
For post-processing passes like additive blending, bloom extraction, and halos:
- Read input textures linearly.
- Never clamp upper values:
  ```glsl
  // Extract bloom with soft threshold:
  float luma = dot(color.rgb, vec3(0.2125, 0.7154, 0.0721));
  float rq = clamp(luma - bloom_threshold, 0.0, 2.0 * bloom_softness);
  rq = (rq * rq) / (4.0 * bloom_softness + 1e-4);
  float weight = max(rq, luma - bloom_threshold) / max(luma, 1e-4);
  vec3 bloom = color.rgb * max(weight, 0.0);
  // Do NOT clamp bloom output! Write directly to FP16 brightcut buffer.
  ```

---

## 4. Developer Anti-Patterns & Checklist

When reviewing or writing shaders, verify against this checklist:

- [ ] **No `clamp(x, 0.0, 1.0)` on final color**: Radiance must remain unbounded until the final tonemapper.
- [ ] **No `fable_emission_boost` applied to PBR**: Ensure `is_pbr == true` bypasses `fable_emission_boost`.
- [ ] **No `fable_emission_boost` on dynamic lights**: Ensure dynamic lights (`SpotLight3D`, `OmniLight3D`, `DirectionalLight3D`) only use physical `LightEnergy`.
- [ ] **No display curve in intermediate passes**: Reinhard, ACES, Filmic, and sRGB transforms belong **only** in `fable_postfx_composite.glsl`.
- [ ] **Always support `is_pbr`**: When adding a new surface or particle shader, expose `uniform bool is_pbr = false;` to allow modern modded PBR assets to pass linear physical data.
- [ ] **Zero `discard;` in Forward+ `PASS_MODE_COLOR` (`!MODE_RENDER_DEPTH`) & `canvas_item` Shaders; Never Guard `dFdx`/`dFdy` on `IN_SHADOW_PASS`**: Under `#if CURRENT_RENDERER == RENDERER_FORWARD_PLUS`, never emit `discard;` in any color-pass spatial or `canvas_item` shader (`godot#118719`, `#120857`, `PR #122994`). Use blend identities (`ALPHA = 0.0`) on `depth_draw_never`/`canvas_item`, and `render_mode depth_prepass_alpha;` + `ALPHA = should_discard ? 0.0 : ...;` + `RenderPriority = -16` on cutout/fur shaders (`DEPTH = should_discard ? 0.0 : FRAGCOORD.z;` on `fur_shell_depth`), leaving `discard;` strictly in `MODE_RENDER_DEPTH` (`ShadowsOnly` casters and Godot's `USE_OPAQUE_PREPASS`). Never wrap `dFdx`/`dFdy` in `if (!IN_SHADOW_PASS)` — `IN_SHADOW_PASS` is a runtime UBO bit in Forward+. Always guard `void light()` on `ALPHA > 0.0` without `return;`.
- [ ] **Zero `OmniLight3D.ShadowMode.DualParaboloid`**: Always use `OmniLight3D.ShadowMode.Cube` (scaled via `placed_shadow_limit`: `1/2/4/8`, ON across all 4 presets including `Low`). Godot's `SHADOW_DUAL_PARABOLOID` injects `if (dp_clip > 0.0) discard;` before user `fragment()`, breaking alpha-cutout shadow derivatives on D3D12. Use `shadow_caster_solid.gdshader` / `shadow_caster_cutout.gdshader` for non-fading `ShadowProxy` casters.
- [ ] **No implicit derivatives in branches or loops**: Never call `texture()`, `dFdx()`, `dFdy()`, `fwidth()`, or `fable_normal_side()` inside `if (!should_discard)` or per-pixel branches/loops. Precompute derivatives (`uv_dx`, `uv_dy`, `geom_cross`) unconditionally at the very top of `fragment()` and use `textureGrad(...)`, `textureLod(..., 0.0)`, `texelFetch(...)`, or `fable_normal_side_precomputed(...)`.
- [ ] **No dynamic vector indexing (`OpVectorExtractDynamic`) or unguarded `/ w` divides**: Never index `vec4[i]`, construct indexed `vec2[4]` arrays in loops, or index constant LUT arrays (`bayer[...]`) at runtime inside branches; use explicit channel selectors, unrolled edges, or branchless ALU (`fable_bayer_threshold`). Guard every projective `/ w` divide against `abs(w) <= 1e-6`.
- [ ] **No unhinted 2D / 2DArray samplers & no empty `Texture2Drd` globals**: Declare `hint_default_*` / `hint_normal` on local `sampler2D` / `sampler2DArray` uniforms (`samplerCube` and `global uniform` samplers excluded), and never publish `new Texture2Drd()` (`TextureRdRid == default`) into `RenderingServer.GlobalShaderParameterSet(...)`.
- [ ] **Automated Tests**: Any new tonemapping, radiance, or D3D12 shader-safety logic must have unit tests added under `Godot/tests/Refii.Tests/` (`HdrTonemapTests.cs` / `Dx12AndMemoryOptimizationTests.cs`).

---

## 5. Strict Separation Between Modern and Classic Pipelines

Refii provides two distinct rendering pipelines toggled at runtime by `Engine_ClassicMaterial.Active` (`r_classic_material`):

1. **Modern Pipeline (`Engine_ClassicMaterial.Active == false`)**:
   - **Color Space**: Unclamped scene-referred linear HDR (`RGBA16F`). All Modern surface, water, sky, and particle shaders (`fx_particle*.gdshader`, `water.gdshader`, `fable_*.gdshader`) must output unclamped linear radiance (`max(col, vec3(0.0))`) with zero `[0, 1]` upper clamping or intermediate sRGB re-encoding.
   - **Subsystems**: Uses Modern subsystems exclusively (`FableFx` / `Engine_FxSystem`, `Engine_DisplacementMeshes` + `Engine_FxDisplacement`, `Engine_ModernWater`, `Engine_ModernRain`, `Engine_ModernDrunk`, `FablePostFxEffect`).
2. **Classic Pipeline (`Engine_ClassicMaterial.Active == true`)**:
   - **Color Space**: Faithful Xbox 360 2008 fixed-point / gamma-space emulation (`Engine_Classic*` subsystems and `classic_*.gdshader`).
   - **Isolation Rule**: Never route Modern mode rendering or adoption through `Engine_Classic*` classes (`Engine_ClassicParticles`, `Engine_ClassicDisplacement`, `Engine_ClassicCasters`, `Engine_ClassicWater`, etc.). Every `Engine_Classic*` adoption/registration/pump entry point must guard on `Engine_ClassicMaterial.Active`. Shared math, parsing, or interpolation helpers must live in mode-agnostic utilities (`Engine_GuestFrame`, `Engine_FxBank`, `Engine_FxMeshes`).

---

## 6. D3D12 / Intel Arc DXIL Shader & Descriptor Rules

On Direct3D 12 (especially Intel Arc Xe2 / Alchemist DXIL drivers), Vulkan-tolerated undefined behavior causes permanent GPU hangs / full-system freezes or `DXGI_ERROR_DEVICE_REMOVED`:

1. **Zero `discard;` in Forward+ `PASS_MODE_COLOR` (`!MODE_RENDER_DEPTH`) & Runtime-UBO `IN_SHADOW_PASS` Warning**:
   - **Why `discard;` in `PASS_MODE_COLOR` hangs Intel Arc D3D12 (`godot#118719`, `#120857`, `PR #122994`)**: Mesa's `nir_to_dxil` in Godot 4.7 lacks `nir_opt_peephole_select`, so `if (should_discard) { discard; }` compiles to a divergent basic-block branch around `dx.op.discard(i1 true)`. On Intel Arc D3D12, any divergent `dx.op.discard(i1 true)` followed later in `PSMain` by Forward+'s clustered `while (merged_mask != 0)` loops (`decals` even on `unshaded`, `reflection_probes`, `omni_lights` with shadow maps, `spot_lights`) hangs the EU / triggers a WDDM TDR. In `MODE_RENDER_DEPTH` (Depth Prepass + Directional/Spot/Cube Shadow Passes), `#ifndef MODE_RENDER_DEPTH` strips all clustered `while` loops out of `scene_forward_clustered.glsl`, so `discard;` inside `MODE_RENDER_DEPTH` is safe.
   - **Never gate `dFdx`/`dFdy` on `IN_SHADOW_PASS`**: In Forward+, `IN_SHADOW_PASS` is a runtime UBO bit (`bool(scene_data_block.data.flags & SCENE_DATA_FLAGS_IN_SHADOW_PASS)`), NOT a compile-time `#define`. Wrapping derivatives in `if (!IN_SHADOW_PASS)` moves them out of uniform entry control flow and causes GPU hangs while stripping zero instructions.
   - **Required Cutout Pattern (`CURRENT_RENDERER == RENDERER_FORWARD_PLUS`)**:
     1. Under `#if CURRENT_RENDERER == RENDERER_FORWARD_PLUS`, add `render_mode depth_prepass_alpha;` so Godot's internal `USE_OPAQUE_PREPASS` discards `ALPHA < opaque_prepass_threshold` strictly inside `MODE_RENDER_DEPTH` (depth prepass + shadow maps, zero `while` loops).
     2. Compute `dFdx`/`dFdy` unconditionally at the very top of `fragment()`, capture `float fable_instance_fade = ALPHA;`, evaluate cheap coverage into `should_discard`, wrap Stage-2 math in `if (!should_discard)` using explicit `textureGrad` / `textureLod`, and emit `ALPHA = should_discard ? 0.0 : fable_instance_fade;` (no `discard;` in `PASS_MODE_COLOR`; `fur_shell_depth` writes `DEPTH = should_discard ? 0.0 : FRAGCOORD.z;`, and `light()` guards on `ALPHA > 0.0`).
     3. Set `RenderPriority = -16` (`Engine_MdlMaterial.CutoutRenderPriority`) on cutout and fur-shell materials so `depth_prepass_alpha` surfaces draw in `RENDER_LIST_ALPHA` ahead of all true semi-transparents (`water`, `standard_material_translucent`, particles, clouds, weather) at `RenderPriority = 0`.
   - **Template (cutout spatial shader)**:
     ```glsl
     #if CURRENT_RENDERER == RENDERER_FORWARD_PLUS
     render_mode depth_prepass_alpha;
     #endif

     void fragment() {
         vec2 uv_dx = dFdx(UV);
         vec2 uv_dy = dFdy(UV);
     #if CURRENT_RENDERER == RENDERER_FORWARD_PLUS
         float fable_instance_fade = ALPHA;
         ALPHA = 0.0;
     #endif
         vec4 alb = textureGrad(albedo_tex, UV, uv_dx, uv_dy);
         bool should_discard = (alpha_scissor_on && alb.a < alpha_scissor_threshold);
         ALBEDO = vec3(0.0);
         if (!should_discard) {
             // Expensive normal/ORM/emission/atmosphere work here using textureGrad / textureLod
             ALBEDO = alb.rgb;
         }
     #if CURRENT_RENDERER == RENDERER_FORWARD_PLUS
         ALPHA = should_discard ? 0.0 : fable_instance_fade;
     #else
         if (should_discard) {
             discard;
         }
     #endif
     }
     ```
2. **Zero `discard;` in `depth_draw_never` / `canvas_item` Blended Shaders & `DualParaboloid` Retirement**:
   - Any shader with `depth_draw_never` or `canvas_item` blending (including `FABLE_TRANSLUCENT`, `fx_guest_particle_pixel.gdshaderinc`, `fx_particle*.gdshader`, `clouds.gdshader`, `weather.gdshader`, `ui_*.gdshader`, and `dynamic_*.gdshader`) MUST **omit `discard;` completely** and output the hardware blend identity (`ALBEDO = vec3(0.0); ALPHA = 0.0;` or `COLOR = vec4(0.0);`), preserving hardware Early-Z on occluded transparents and eliminating DXIL `discard` hazards.
   - Opaque solids (`FABLE_SOLID`, `shadow_caster_solid.gdshader`) omit `discard;` completely. Only `ShadowsOnly` casters (`shadow_caster.gdshader`, `shadow_caster_cutout.gdshader`) and `depth_prepass_marker.gdshader` keep `discard;`.
   - **`OmniLight3D.ShadowMode.DualParaboloid` is permanently retired on all renderers**: Godot 4.7 Forward+ `SHADOW_DUAL_PARABOLOID` injects `if (dp_clip > 0.0) discard;` at the top of `PSMain` *before* user `fragment()`. Every `OmniLight3D` uses `OmniLight3D.ShadowMode.Cube`, with active light counts controlled by `placed_shadow_limit` (`1/2/4/8`, ON across all presets).
   - **Upstream Godot 4.7 Issue `godotengine/godot#120857` (Intel Arc D3D12 Alpha-Scissor TDR `0x887a0005`)**: Refii also strips runtime surface `lods` centrally in [`Engine_ArrayMeshes.cs`](file:///c:/Users/Miki/Documents/programming/refii/Godot/src/Engine/core/Engine_ArrayMeshes.cs) (`ShouldStripSurfaceLodsForD3d12`) while keeping full LOD 0 geometry intact.
3. **No Dynamic Vector / Small-Array Indexing (`OpVectorExtractDynamic`)**:
   - Avoid dynamic indexing into vectors (`vec4[i]`), small local arrays inside loops (`vec2 c[4]` indexed by loop variable `k`), or runtime-indexed constant lookup tables (`const float bayer[16]`) inside divergent branches — SPIR-V emits `OpVectorExtractDynamic` (reported by Intel Arc drivers as `OpVectorExtractDynamic is not supported yet.`), which can break or miscompile under DXIL.
   - Replace dynamic vector channel lookups with explicit channel selectors (`c == 0 ? v.x : (c == 1 ? v.y : (c == 2 ? v.z : v.w))`), unroll 4-edge checks, and use pure ALU 4x4 Bayer (`fable_bayer_threshold` in `fable_dither.gdshaderinc`).
4. **Guard Projective `/ w` Division Against Zero**:
   - Never divide reconstructed clip/view positions (`inv_proj * vec4(ndc_xy, scene_z, 1.0)`) by raw `.w`: on reverse-Z sky texels (`scene_z == 0.0`) or unbound depth defaults, `w` can approach `0.0`, producing `Inf`/`NaN` that poisons FP16 render targets (`RGBA16F`). Always clamp the divisor (`float safe_w = abs(scene_view.w) > 1e-6 ? scene_view.w : 1e-6;`).
5. **Explicit Sampler Defaults & Valid Global `Texture2Drd` Fallbacks**:
   - Every non-global `sampler2D` and `sampler2DArray` uniform (except `hint_screen_texture` / `hint_depth_texture` and `samplerCube`) must carry an explicit `hint_default_white`, `hint_default_black`, `hint_default_transparent`, or `hint_normal`.
   - Global `sampler2D` shader parameters (`fx_scene_depth`, `fable_exposure_tex`, and `_second` views) must be initialized with a valid 1x1 `ImageTexture` fallback in `RegisterGlobals()` and restored to that fallback (while alive) on effect teardown — **never** `new Texture2Drd()` with `TextureRdRid == default`, because shaders sample globals even while compositor effects are gated off.
6. **Invariant Enforcement**:
   - Automated checks in [`Dx12AndMemoryOptimizationTests.cs`](file:///C:/Users/Miki/Documents/programming/refii/Godot/tests/Refii.Tests/Dx12AndMemoryOptimizationTests.cs) enforce these shader and C# patterns across every `.gdshader` and `.gdshaderinc` under `Godot/src/`. Also enforce standing rule [shader-dxil-safety.md](file:///c:/Users/Miki/Documents/programming/refii/.agent/rules/shader-dxil-safety.md).
