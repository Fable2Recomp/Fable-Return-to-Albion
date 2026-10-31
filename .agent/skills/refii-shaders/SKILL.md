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
    vec4 texel = texture(fx_tex, UV);
    if (texel.a * COLOR.a * 255.0 <= fx_alpha_ref) {
        discard;
    }
    float fade = fx_soft_fade(texel.a * COLOR.a, VERTEX, SCREEN_UV, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);
    
    // PBR particles use linear texel radiance directly;
    // Legacy particles use gamma 2.0 decode (squaring) and legacy emission boost.
    vec3 tex_rgb = is_pbr ? texel.rgb : (texel.rgb * texel.rgb * max(fable_emission_boost, 1.0));
    vec3 rgb = max(COLOR.a * COLOR.rgb * tex_rgb, vec3(0.0));
    
    ALBEDO = rgb * fade;
    ALPHA = mix(1.0, texel.a * COLOR.a * fade, fx_src_alpha);
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
- [ ] **Automated Tests**: Any new tonemapping or radiance logic must have unit tests added in `Godot/tests/Refii.Tests/Lighting/HdrTonemapTests.cs`.

---

## 5. Strict Separation Between Modern and Classic Pipelines

Refii provides two distinct rendering pipelines toggled at runtime by `Engine_ClassicMaterial.Active` (`r_classic_material`):

1. **Modern Pipeline (`Engine_ClassicMaterial.Active == false`)**:
   - **Color Space**: Unclamped scene-referred linear HDR (`RGBA16F`). All Modern surface, water, sky, and particle shaders (`fx_particle*.gdshader`, `water.gdshader`, `fable_*.gdshader`) must output unclamped linear radiance (`max(col, vec3(0.0))`) with zero `[0, 1]` upper clamping or intermediate sRGB re-encoding.
   - **Subsystems**: Uses Modern subsystems exclusively (`FableFx` / `Engine_FxSystem`, `Engine_DisplacementMeshes` + `Engine_FxDisplacement`, `Engine_ModernWater`, `Engine_ModernRain`, `Engine_ModernDrunk`, `FablePostFxEffect`).
2. **Classic Pipeline (`Engine_ClassicMaterial.Active == true`)**:
   - **Color Space**: Faithful Xbox 360 2008 fixed-point / gamma-space emulation (`Engine_Classic*` subsystems and `classic_*.gdshader`).
   - **Isolation Rule**: Never route Modern mode rendering or adoption through `Engine_Classic*` classes (`Engine_ClassicParticles`, `Engine_ClassicDisplacement`, `Engine_ClassicCasters`, `Engine_ClassicWater`, etc.). Every `Engine_Classic*` adoption/registration/pump entry point must guard on `Engine_ClassicMaterial.Active`. Shared math, parsing, or interpolation helpers must live in mode-agnostic utilities (`Engine_GuestFrame`, `Engine_FxBank`, `Engine_FxMeshes`).
