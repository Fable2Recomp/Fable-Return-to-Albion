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
- **Fable 2 Histogram CDF (`fable_cdf_pw`, `sub_8217E428`)**:
  - In [`fable_postfx_reduce.glsl`](file:///f:/Documents/programming/refii/Godot/src/Engine/Lighting/fable_postfx_reduce.glsl), the histogram percentiles (`S_SM_BLACK`, `S_SM_MID`, `S_SM_WHITE`, `S_SM_P99`) and the 256-bin CDF (`S_CDF`) adapt in lockstep with `S_EXPOSURE` (`max(params.bounds.x, EXPOSURE_BLEND)` = `0.05` in modern mode) so the histogram never drifts after exposure has settled.
  - In [`fable_postfx_composite.glsl`](file:///f:/Documents/programming/refii/Godot/src/Engine/Lighting/fable_postfx_composite.glsl), `fable_cdf_pw(l_exp)` evaluates Fable 2's parabolic band-clamped histogram CDF (`eq = clamp(cdf, t - dev, t + dev)`, `pw = eq^2`) directly from `state.v[S_CDF]` at pre-exposed luminance $l_{\text{exp}} = l \cdot E$.
  - `base_r = fable_reinhard(x, inv_white_sq)` (where $x = l_{\text{exp}} \cdot \text{S\_CURVE\_SCALE}$) is blended with `pw` via `cdf_w = params.contrast.x * (1.0 - smoothstep(0.85, 1.25, base_r))` **before** any HDR shoulder compression (`curved = mix(base_r, pw, cdf_w)`). This preserves Fable 2's dark-interior midtone lift and S-curve contrast in caves and the Guild Hall while tapering smoothly into the analytical Reinhard highlight shoulder.
- **Output-Space HDR Shoulder (`fable_hdr_shoulder`) & SDR Highlight Ceiling Fit (`fable_fit_ceiling`)**:
  - In HDR (`headroom > 1.0`), `fable_hdr_shoulder(curved, headroom)` applies a $C^1$ exponential roll-off on output values above `HDR_KNEE_Y = 0.80` toward `headroom`, leaving shadows and midtones ($\le 0.80$) bit-identical while compressing specular glints, sun/sky, torches, and spells smoothly into $[0.80, \text{headroom}]$.
  - Before clamping to `headroom`, `fable_fit_ceiling(max(gain * brightness * col, vec3(0.0)), headroom)` desaturates any pixel whose peak channel exceeds `headroom` along a constant-luminance line toward `vec3(l)`. In SDR (`headroom = 1.0`), bright sky, water reflections, and sun-ward fog (`curved >= 1.0`) map cleanly to pure `(1, 1, 1)` white, while sub-white warm highlights (`curved < 1.0`, `R > 1.0`) desaturate gracefully without per-channel hue shifts.
- **Shadow Black-Point Toe (`fable_gamma_toe_hdr`)**:
  - Applies a piecewise-sRGB-to-$\gamma=2.2$ toe on $[0, 1]$ ($O(c^{2.2})$ near black to give Fable 2's rich, grounded shadow blacks without shifting mid-grey or Paper White), while passing HDR highlights $(1.0, \text{headroom}]$ linearly 1:1 up to `headroom`.
- **Bloom Rescale (`bloom_scale`), Saturation Ordering, & Highlight Desaturation**:
  - Bright-cut extraction ([`fable_postfx_brightcut.glsl`](file:///f:/Documents/programming/refii/Godot/src/Engine/Lighting/fable_postfx_brightcut.glsl)) computes `cut = max(l * inv_exposure - threshold, 0.0)` in unexposed space, soft-compresses extreme spikes above $4.0\times$ with a $C^1$ exponential shoulder, and multiplies the unit chromaticity vector `c / max(l, 1e-5)` by `cut` (`bright = c * (cut / max(l, 1e-5))`, matching Xbox 360 blob `0163`) before writing `o * 0.5` (`1 / g_BloomRescaleFactor`).
  - The composite restores the guest's `2.0x` rescale multiplied by `bloom_scale` (`Engine_PostFxParams.BloomGain`, default `1.0`, hot-reloadable via `Engine_RefiiConfig.PollReload()`), blends bloom via `col + bloom * max(headroom - col, 0.0) / headroom`, applies theme saturation **after** bloom so bloom halos pick up Fable's warm chroma boost, and runs `fable_highlight_desaturate` (in HDR) followed by `fable_fit_ceiling`.

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

### Rule 5: Volumetric Mist & Fog Shaders (`modern_mist.gdshader`)
1. **Linear Albedo Uniforms**: Guest mist colors (`Engine_ClassicMist.Volume.Colour`) are already in linear space. Declare `uniform vec3 albedo = vec3(1.0);` **without** `: source_color` so Godot does not apply an extra sRGB-to-linear ($\gamma = 2.2$) crush.
2. **Self-Emission Floor**: Set `EMISSION = albedo * emission_energy;` (default `0.25`) alongside `ALBEDO = albedo;` so mist volumes in shadowed valleys or night scenes retain Fable 2's signature self-luminous atmospheric glow while still taking volumetric in-scatter from nearby directional and point lights.

---

## 4. Verification & Testing

Every shader modification must be accompanied by automated verification:
1. **Unit Tests**: Ensure all mathematical formulas and bypass branches are tested in [`Godot/tests/Refii.Tests/Lighting/HdrTonemapTests.cs`](file:///f:/Documents/programming/refii/Godot/tests/Refii.Tests/Lighting/HdrTonemapTests.cs).
2. **CLI Test Execution**:
   ```pwsh
   dotnet test Godot/
   ```
3. **Parity Check**: Ensure SDR mode (`headroom = 1.0`, `fable_emission_boost = 1.0`) produces identical visuals to the original 2008 game presentation.
