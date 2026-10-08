# Refii HDR Scene-Referred Pipeline & Shader Authoring Guide

This document describes Refii's **Scene-Referred HDR Rendering Pipeline** and establishes the required engineering standards for authoring, editing, and maintaining shaders across Godot.

---

## 1. Core Architecture: Scene-Referred vs. Display-Referred

Refii follows a modern **physically-based, scene-referred rendering model**:

```
[ Geometry & Textures ] ──┐
[ Dynamic Lights (AAA) ] ─┼─► [ FP16 Linear HDR Scene Buffer ] ─► [ Intermediate PostFX ] ─► [ Final Display Tonemapper ] ─► [ SDR / HDR Display ]
[ Particle Billboards ]  ─┘      (Unbounded [0, ∞) radiance)        (Unbounded, agnostic)       (Sole tonemap authority)
```

### 1.1. Unbounded Intermediate Radiance
- All 3D rendering passes—including geometry, direct dynamic lighting, particle effects, sun rays, halos, additive composite layers, and bright-cut bloom extraction—operate strictly in **linear physical scene radiance** $[0, \infty)$ using 16-bit floating point (`RGBA16F`) render targets.
- Intermediate shaders are strictly **display-agnostic**:
  - **NO** branching on whether the screen is running in SDR or HDR.
  - **NO** clamping intermediate color or luminance to $1.0$ (or any arbitrary ceiling).
  - **NO** early tonemapping or display transfer curves inside geometry or effect passes.

### 1.2. Centralized Presentation Tonemapper
The presentation composite shader ([`fable_postfx_composite.glsl`](file:///f:/Documents/programming/refii/Godot/src/Engine/Lighting/fable_postfx_composite.glsl)) is the **sole authority** that compresses unbounded scene radiance to fit the physical capabilities of the display:
- **SDR Mode** (`headroom = 1.0`): Compresses $[0, \infty)$ into standard dynamic range $[0, 1.0]$.
- **HDR Mode** (`headroom > 1.0`): Preserves linear fidelity in shadows and midtones while rolling off specular highlights, sun glints, and magic emissions into available display headroom via a $C^1$-continuous shoulder curve, path-to-white highlight desaturation, and ceiling fitting.

---

## 2. Asset Radiance & The Emission Boost Bridge

Fable 2 (2008) was developed for the Xbox 360, where all framebuffers and textures were bounded by SDR ($1.0 = \text{maximum television output}$, roughly 100 nits CRT).

In modern HDR rendering, **paper white** is $1.0$ (e.g. 300 nits), while specular highlights can reach $3.33+$ (1000 nits). If legacy textures output in $[0, 1]$, glowing magic runes and torch flames cannot exceed paper white and appear flat.

To solve this, Refii provides a bridge:

### 2.1. `fable_emission_boost`
- A global uniform (`global uniform float fable_emission_boost`) representing the physical headroom multiplier (`peak_luminance / paper_white`, or user configured via CVars).
- **Purpose**: Scales legacy 2008 SDR textures so they can emit physical light into the HDR headroom.
- **Rule**: It is **ONLY** for legacy 2008 SDR assets.

### 2.2. Modern PBR Mod Assets (`is_pbr = true`)
- Modern PBR mod assets already author true physical radiance in linear space (via linear textures and `self_illum_scale`).
- Modern PBR assets **MUST NEVER** be multiplied by `fable_emission_boost` and **MUST NEVER** have their textures squared.

### 2.3. Dynamic Lights (Directional, Omni, Spot)
- Godot's modern dynamic lighting system (`DirectionalLight3D`, `OmniLight3D`, `SpotLight3D`) is real AAA dynamic lighting evaluated directly from light energy and distance attenuation.
- Dynamic lights are **NEVER clamped** and **NEVER boosted by `fable_emission_boost`**.

---

## 3. Shader Authoring Rules

### Rule 1: Surface & Material Shaders (`standard_material_body.gdshaderinc`)

When reading material textures (diffuse/albedo, emission):

1. **Check `is_pbr`**:
   Materials state `uniform bool is_pbr = false;`.
2. **Handle Texture Decode**:
   - Legacy assets were authored for Xbox 360 gamma 2.0 and require squaring sampled values (`r * r`).
   - PBR assets are already linear.
3. **Handle Emission**:
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
               // Modern PBR mod assets: selfIllum and self_illum_scale carry authored physical radiance
               EMISSION += selfIllum * self_illum_scale;
           } else {
               // Shipped 2008 SDR assets: squared per guest pixel shader (r3.xyz = r3.xyz * r3.xyz)
               // and scaled by fable_emission_boost so legacy SDR textures can emit into HDR headroom
               EMISSION += (selfIllum * selfIllum) * (self_illum_scale * fable_emission_boost);
           }
       }
   }
   ```

### Rule 2: Particle & Billboard Shaders (`fx_particle_*.gdshader`)

Particle billboards combine vertex color and texture maps. To support both legacy particle banks and modern PBR mod particles:

1. **Provide `uniform bool is_pbr = false;`**:
   ```glsl
   shader_type spatial;
   render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

   uniform sampler2D fx_tex : filter_linear, repeat_enable;
   uniform bool is_pbr = false;
   global uniform float fable_emission_boost = 1.0;

   void fragment() {
       vec4 texel = texture(fx_tex, UV);
       ...
       // PBR particles use linear texel radiance directly;
       // Legacy particles use gamma 2.0 decode (squaring) and legacy emission boost.
       vec3 tex_rgb = is_pbr ? texel.rgb : (texel.rgb * texel.rgb * max(fable_emission_boost, 1.0));
       vec3 rgb = max(COLOR.a * COLOR.rgb * tex_rgb, vec3(0.0));
       
       ALBEDO = rgb * fade;
       ALPHA = mix(1.0, texel.a * COLOR.a * fade, fx_src_alpha);
   }
   ```
2. **Never clamp particle colors**: Do not apply `clamp(rgb, 0.0, 1.0)` or saturate particle outputs. Let them write pure linear radiance into the scene.

### Rule 3: Dynamic Lighting Shaders (`fable_lighting.gdshaderinc`)

In Godot custom `light()` shaders:
- `LIGHT_COLOR` contains `node.LightColor * node.LightEnergy * PI`.
- Dividing by $\pi$ recovers the exact physical energy `node.LightColor * node.LightEnergy`.
- Evaluate Lambertian diffuse and specular highlights without ceiling clamps:
  ```glsl
  vec3 fable_direct(vec3 normal, vec3 light, float attenuation, vec3 light_colour) {
      return clamp(dot(normal, light), 0.0, 1.0) * attenuation * (light_colour / PI);
  }
  ```
- **Do not** apply `fable_emission_boost` to dynamic lights. Dynamic lights are physical light fixtures, not texture emissions.

### Rule 4: Intermediate Post-Processing Shaders (`.glsl` compute passes)

When authoring or editing intermediate compute passes (e.g. `fable_postfx_addlayer.glsl`, `fable_postfx_sunlayer.glsl`, `fable_postfx_brightcut.glsl`):
1. **No Clamping**: Do not cap outputs to $[0, 1]$.
   ```glsl
   // WRONG:
   color = clamp(color, 0.0, 1.0);

   // RIGHT:
   color = max(color, vec3(0.0)); // Guard against negative NaN/underflow only
   ```
2. **No Early Tonemapping**: Never apply Reinhard, Filmic, or display curves inside intermediate passes. Allow bloom extractors, halos, and flares to accumulate true high dynamic range values.
3. **No Early Gamma/sRGB Encoding**: Keep intermediate buffers strictly linear. Display transfer curves belong only in the final composite presentation pass.

---

## 4. Verification & Testing

Every shader modification must be accompanied by automated verification:
1. **Unit Tests**: Ensure all mathematical formulas and bypass branches are tested in [`Godot/tests/Refii.Tests/Lighting/HdrTonemapTests.cs`](file:///f:/Documents/programming/refii/Godot/tests/Refii.Tests/Lighting/HdrTonemapTests.cs).
2. **CLI Test Execution**:
   ```pwsh
   dotnet test Godot/
   ```
3. **Parity Check**: Ensure SDR mode (`headroom = 1.0`, `fable_emission_boost = 1.0`) produces identical visuals to the original 2008 game presentation.
