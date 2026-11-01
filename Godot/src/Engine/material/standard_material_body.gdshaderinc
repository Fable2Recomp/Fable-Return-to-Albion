// The whole of the standard material. Two shader files include it and differ in one word of their
// render mode: `standard_material.gdshader` draws both sides, `standard_material_culled.gdshader`
// culls back faces as the guest does. A render mode is fixed per shader file, so the body lives
// here once.
//
// THE MOBILE LITE VARIANT is this same body compiled with defines (material/lite/*.gdshader,
// chosen at material build by Engine_ShaderVariant under `shader_variant = "lite"`). Each define
// REMOVES code at compile time — a uniform branch removes nothing from a GPU's register budget:
//   FABLE_LITE             what is dead or exactly zero in EITHER lighting model: parallax; the
//                          surface weather and cloud shadow (the variant is refused while they
//                          are on); the debug views; the shader-cost diagnostic and the zero-work
//                          A/B switch (its default side kept).
//   FABLE_LITE_MODERN      (lite/modern/, the modern model only) the modern model's multiply-by-
//                          zero at fable_unified = 1: the PRT vertex transfer, the probe/vertex
//                          bakes, their darkening and sky tether — amb3 keeps its 0.35 share.
//                          Under the fable model that path IS the ambient, so it stays.
//   FABLE_LITE_NO_ENVMAP   the environment-cube block (a material that binds none).
//   FABLE_LITE_NO_SPEC     the whole specular path — fetch, ORM reconstruction, reflection and the
//                          per-light Phong lobe — for a material whose specular colour is zero.
#ifdef FABLE_LITE_MODERN
#ifndef FABLE_LITE
#define FABLE_LITE
#endif
#endif
#ifdef FABLE_LITE_NO_SPEC
#ifndef FABLE_LITE
#define FABLE_LITE
#endif
#endif

#include "res://src/Engine/material/fable_lighting.gdshaderinc"
#include "res://src/Engine/level/environment/fable_atmosphere.gdshaderinc"
#ifndef FABLE_LITE
#include "res://src/Engine/level/environment/dynamic/dynamic_surface_weather.gdshaderinc"
#endif
#ifdef FABLE_TREE_WIND
#include "res://src/Engine/material/fable_tree_sway.gdshaderinc"
#endif

// NOT source_color. Fable decodes its colour textures by SQUARING them, not with the sRGB
// transfer function — PSHADER_STANDARDMATERIAL_..._AMB1, recompiled out of Shaders.sbk, reads
// the diffuse raw and does `r4.yzw = r4.yzw * r4.yzw`, and the specular the same. That is a
// gamma of exactly 2.0; sRGB is ~2.4 with a linear toe near black, so `source_color` was
// darkening every midtone relative to the original and lifting the darks.
//
// Note the swizzle in the guest's line: rgb only. Alpha is left alone, which matters because
// it is the scissor threshold.
uniform sampler2D albedo_tex : hint_default_white, filter_linear_mipmap_anisotropic;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap_anisotropic;
uniform sampler2D emission_tex : filter_linear_mipmap_anisotropic;

// BLACK, not white. An unbound specular map has to contribute nothing — the guest's own fallback
// for this slot is a 16x16 transparent black texture, which is also how its identity as the
// specular slot was settled.
uniform sampler2D specular_tex : hint_default_black, filter_linear_mipmap_anisotropic;

/// The environment cube, sampled by the reflection vector. 36 of these ship; 526 material records
/// name one.
///
/// Bound to s14 in the guest and sampled with `tfetchCube` by ALL 192
/// PSHADER_STANDARDMATERIAL permutations — none of them branch on `g_NeedsEnvmapping`, which is
/// defined as bit 22 of the bool word and read by not one shader. So the binding is the gate, not
/// the flag: a material naming no cube gets the guest's fallback and the term multiplies out to
/// nothing. `has_envmap` reproduces that, and the specular weight below makes it doubly true.
uniform samplerCube envmap_tex : filter_linear_mipmap;

uniform bool has_normal = false;
uniform bool has_emission = false;
uniform bool has_specular = false;
uniform bool has_envmap = false;

/// A packed ORM texture: R = ambient occlusion, G = roughness, B = metallic.
///
/// OFF unless the player asks for it, because for shipped data it changes nothing. Fable 2 has no
/// AO map and no metallic map, so R and B are constants and G is the same roughness this shader
/// already derives from the specular alpha. It is here for §25 phase 5, when those maps start
/// being generated instead of being absent.
///
/// It does NOT replace the specular map. There is no ORM slot for specular colour, and that
/// colour is also what weights the environment reflection.
uniform sampler2D orm_tex : filter_linear_mipmap_anisotropic;
uniform bool has_orm = false;

/// Flag indicating that the material uses modern authored PBR assets rather than legacy 2008 Blinn-Phong.
/// When true: albedo is linearized from sRGB, authored Roughness/Metallic/AO are preserved, and reflection probes contribute.
uniform bool is_pbr = false;

// Whether the ORM's alpha carries a SOLVED height. The heightless sentinel is opaque white —
// read as relief it is +0.5 everywhere, a permanent view-dependent UV shift on surfaces with no
// relief at all — so parallax runs only when the packer says the channel is real.
uniform bool has_height = false;

/// How far the ORM's height channel displaces the texture coordinates.
///
/// PARALLAX IS THE CONSUMER THE HEIGHT WAS BUILT FOR. Engine_TexHeight solves a height field out of
/// the normal map's gradient and packs it in the ORM's alpha, and until something samples it the
/// solve is a cost with no picture attached.
///
/// 0.02 because the height is normalised to its own range, not to a physical depth. Poisson fixes a
/// gradient and leaves the additive constant free, so there is no absolute scale to inherit and the
/// displacement has to be chosen. A twentieth of a UV unit reads as surface relief at the distances
/// Fable 2's surfaces are seen from; more starts to swim at grazing angles, which is offset
/// mapping's characteristic failure.
uniform float parallax_scale : hint_range(0.0, 0.1) = 0.02;
uniform bool alpha_scissor_on = false;
uniform float alpha_scissor_threshold = 0.25;

uniform bool premultiplied_alpha = false;

uniform bool foliage_normals = false;

/// The exponent where the specular map's alpha is 0 and 1: the shader's c24.x and c24.y.
///
/// THE SHIPPED DATA STATES ONE PAIR AND ONLY ONE: (1, 100) on every single one of the 9,293
/// material records, from a stored minimum of 1.0 and a stored RANGE of 99.0. So the exponent is
/// driven entirely by the specular map's alpha, and the material contributes no variation to it.
///
/// A field that never varies is a field worth doubting, so this was tested rather than accepted:
/// the whole 45-byte block was captured and its other floats counted, and they are authored values
/// too — exactly 0.0 in most slots, with 0.4 and 4.703 appearing in two of them. Misaligned reads
/// do not produce clean round numbers like 1.0 and 99.0 next to exact zeroes; they produce
/// denormals and 10^30. The block is being split correctly and Fable 2's exporter simply writes a
/// near-constant scalar block.
///
/// They stay uniforms rather than becoming constants because the material states them, and a mod
/// or a later asset revision that varies them should work without a shader change.
uniform float spec_power_min = 1.0;
uniform float spec_power_max = 100.0;

/// How much of the self-illumination map is added: the shader's c24.z.
///
/// Also invariant in the shipped data — exactly 1.0 on all 9,293 records — so the map's own
/// intensity is what carries the effect.
uniform float self_illum_scale = 1.0;

const vec3 LUMA = vec3(0.2126, 0.7152, 0.0722);

/// Whether this surface sways (the mesh header's movement byte 1 with an amplitude): the WIND1
/// pair. The amplitude's VALUE is the scene's, folded into fable_wind_factors (Engine_SceneWind);
/// here it is the gate.
uniform float wind_amplitude : hint_range(0.0, 0.5) = 0.0;

/// g_SwaySpeed: the mesh header's own speed (.mdl +84), unclamped as the title takes it.
uniform float wind_speed = 1.0;

/// A tree (movement mode 2): the TRUNK1 pair every shipped tree draws, leaves included —
/// Lambert without specular, and the ambient darkening only with its own normal map.
global uniform int fable_light_debug;

// False evaluates the original zero-color work for same-process profiling only.
global uniform bool fable_zero_specular_work;

uniform bool foliage = false;

/// Repeated scatter (grass, MeshKind 4): the GRASS1 pair, which has no specular and no
/// ambient normal-map darkening.
uniform bool grass = false;

/// g_NormalMapGradient_Enable_Factor_Offset as (enable, factor, offset) in model height
/// (`sub_82233570`; built from the header's gradient in Engine_MdlMaterial.ApplyVegetation).
uniform vec3 grass_normal_gradient = vec3(0.0);

global uniform vec3 fable_wind_direction;
global uniform vec2 fable_wind_factors;


// A MATERIAL'S OWN FLAGS AS CONSTANTS, for a lite build specialised to them
// (Engine_ShaderSpecialisation, `lite_material_folds`): a flag is a uniform the material sets once
// at build and never again, so a build drawn only by materials with the flag off can fold it.
// Unspecialised, each reads its uniform as before.
#ifdef FABLE_SPEC_NOT_PBR
#define FABLE_IS_PBR false
#else
#define FABLE_IS_PBR is_pbr
#endif
#ifdef FABLE_SPEC_NO_ORM
#define FABLE_HAS_ORM false
#else
#define FABLE_HAS_ORM has_orm
#endif
#ifdef FABLE_SPEC_NO_EMISSION
#define FABLE_HAS_EMISSION false
#else
#define FABLE_HAS_EMISSION has_emission
#endif
#ifdef FABLE_SPEC_NOT_PREMULTIPLIED
#define FABLE_PREMULTIPLIED false
#else
#define FABLE_PREMULTIPLIED premultiplied_alpha
#endif
// No normal map: the fetch, the tangent frame and NORMAL_MAP itself go, so Godot drops the
// tangent and binormal from the vertex stage too.
#ifdef FABLE_SPEC_NO_NORMAL_MAP
#define FABLE_HAS_NORMAL false
#else
#define FABLE_HAS_NORMAL has_normal
#endif
#ifdef FABLE_SPEC_NO_GRASS_BEND
#define FABLE_GRASS_BEND false
#else
#define FABLE_GRASS_BEND (grass_normal_gradient.x > 0.5)
#endif

/// THE SPECULAR TERMS, HANDED TO `light()`. Godot's `light()` cannot reach a fragment local, and
/// the guest's specular is Phong over the material's own maps, so the colour and the exponent
/// travel as varyings — the same arrangement `standard_material_phong` uses.
/// rgb: the entity's stated TINT — CECGraphicAppearance+80 (SetTintColour, and the corpse tint
/// every creature death sets through `sub_82573570`), which the guest binds to
/// `g_StandardMaterialTintColour` (PS c49, `sub_82220CA8`). Its shader multiplies the summed
/// diffuse, self-illumination and reflection by `.xyz` before the atmosphere; `.w` is never read.
instance uniform vec4 entity_tint = vec4(1.0);

#ifndef FABLE_LITE_NO_SPEC
varying vec3 v_specular;
varying float v_specular_power;
#endif

/// The PRT ambient, computed per vertex as the guest's AMB2 vertex shader does; a = present.
/// Not in FABLE_LITE_MODERN: the modern model multiplies every probe bake by zero.
#ifndef FABLE_LITE_MODERN
varying vec4 v_transfer;
#endif

/// A TREE'S PER-PLACEMENT FADE (`sub_8220AF90`; Engine_PropDrawRange.TreeBand stamps the band):
/// (start, 1/length, power) in and (end, 1/length, power) out, measured from the main camera to
/// this copy's own box centre — per copy, so a batch fades tree by tree as the title's does.
/// End 0 is no tree band. A TIER-CULLED PROP carries the same out band at its cull radius
/// (Engine_PropFade.SetRadius, centre = its origin in a batch), so a cell's undergrowth dithers in
/// copy by copy as each crosses into range instead of appearing with the cell.
/// PINNED SLOTS, the same four in shadow_caster.gdshader. A prop's cast-only twin is one instance
/// holding the caster material (its merged surface) and the body's own material (each alpha-cut
/// surface it keeps, Engine_ShadowProxy.Build). Godot numbers unhinted instance uniforms in
/// declaration order, so unpinned the caster had these at 0..3 and the body at 7..10: one slot
/// set per name, the kept surfaces read slots nobody wrote, and every rebuild warned. 10..13 sit
/// above this body's own unhinted seven (fable_lighting's six, the tint) and below the classic
/// shaders' pins (14, 15).
instance uniform vec4 xr_tree_fade_in : instance_index(10) = vec4(0.0);
instance uniform vec4 xr_tree_fade_out : instance_index(11) = vec4(0.0);
instance uniform vec3 xr_tree_centre : instance_index(12) = vec3(0.0);
/// A PROP'S ARRIVAL: 0 the frame its mesh stood, 1 after Engine_PropFade.ArrivalSeconds, so a copy
/// the streamer delivers inside its radius dithers in instead of popping. 1 is standing.
instance uniform float xr_fade_alpha : instance_index(13) = 1.0;
varying float v_tree_fade;

void vertex() {
#ifndef FABLE_LITE_MODERN
#if defined(FABLE_TREE_WIND) || defined(FABLE_SPEC_NO_PRT)
    // A tree carries its wind lanes in CUSTOM1..3 (Engine_MdlTreeStreams), not transfer; a
    // FABLE_SPEC_NO_PRT build draws instances that state another bake mode, or a mesh without CUSTOM0.
    v_transfer = vec4(0.0);
#else
    v_transfer = fable_transfer(CUSTOM0, CUSTOM1, CUSTOM2, mat3(MODEL_MATRIX));
#endif
#endif
    // GRASS1's normal gradient (VS 0453 1314-1371, PS 0487 1279-1287): w = saturate(height x
    // factor + offset) bends the normal toward the instance's up, and the pixel stage bends
    // the vertex normal toward that again by the same w.
    if (FABLE_GRASS_BEND) {
        float w = clamp((VERTEX.y * grass_normal_gradient.y) + grass_normal_gradient.z, 0.0, 1.0);
        vec3 bent = normalize(mix(NORMAL, vec3(0.0, 1.0, 0.0), w));
        NORMAL = normalize(mix(NORMAL, bent, w));
    }
    v_tree_fade = 1.0;
    // FABLE_SOLID never carries a band: a tree is .mdl byte 77 == 2, which hands every one of
    // its surfaces, LOD1 included, to the two-sided or wind shader (Engine_MdlMaterial), and a
    // solid prop's dither would cost Godot's shared depth-only shadow material (it discards).
#ifndef FABLE_SOLID
    v_tree_fade = xr_fade_alpha;
    if (xr_tree_fade_out.x > 0.0) {
        float d = distance(MAIN_CAM_INV_VIEW_MATRIX[3].xyz, (MODEL_MATRIX * vec4(xr_tree_centre, 1.0)).xyz);
        float fade_in = xr_tree_fade_in.y > 0.0
            ? pow(clamp((d - xr_tree_fade_in.x) * xr_tree_fade_in.y, 0.0, 1.0), xr_tree_fade_in.z)
            : 1.0;
        float fade_out = pow(clamp((xr_tree_fade_out.x - d) * xr_tree_fade_out.y, 0.0, 1.0), xr_tree_fade_out.z);
        v_tree_fade *= fade_in * fade_out;
    }
#endif
    // GUARDED BY AN `if`, NOT AN EARLY `return`. Godot rejects the latter outright — "Using
    // 'return' in the 'vertex' processor function is incorrect" — and the shader then fails to
    // compile as a whole, taking every uniform with it.
    // FABLE_WIND ONLY (standard_material_wind.gdshader). The write below sets `uses_vertex` on
    // the compiled shader, which refuses Godot's shared depth-only shadow material
    // (scene_shader_forward_clustered.h:344), and its TIME read makes the material "animated",
    // which keeps every light whose shadow pass holds the surface dirty on every frame
    // (renderer_scene_cull.cpp, `_light_instance_update_shadow`). Only a surface that sways
    // carries it: `Engine_MdlMaterial.ApplyWind` hands that surface the wind shader.
#ifdef FABLE_WIND
    if (wind_amplitude > 0.0) {
        // THE WIND1 VERTEX SHADER (blob 0455 1328-1424), in the guest's world axes:
        //   f = frac(origin.x + origin.y)                  — the copy's world position (batch +
        //                                                    instance translation)
        //   c = cos(1 + f - T x SwaySpeed x (1 + 0.1 f))
        //   offset = c x (WD0 x WF.x + (-0.35, 1.53, 0) x WF.y) x saturate(5 x model height)
        // WD0 and WF are the scene's (Engine_SceneWind); the guest's x is our -x, its y our z.
        vec3 origin = MODEL_MATRIX[3].xyz;
        float f = fract(origin.z - origin.x);
        float c = cos(1.0 + f - (TIME * wind_speed * (1.0 + (0.1 * f))));
        vec3 g = c * ((fable_wind_direction * fable_wind_factors.x) + (vec3(-0.35, 1.53, 0.0) * fable_wind_factors.y));
        vec3 world_offset = vec3(-g.x, g.z, g.y) * clamp(5.0 * VERTEX.y, 0.0, 1.0);
        VERTEX += inverse(mat3(MODEL_MATRIX)) * world_offset;
    }
#endif

#ifdef FABLE_TREE_WIND
    // The title's tree wind: the branch offset its trunk stage gives this vertex (COLOR1 weights).
    VERTEX = fable_tree_sway(VERTEX, CUSTOM1, MODEL_MATRIX);
#endif

    // After the wind: the light table measures to where the vertex is drawn.
    fable_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    // The tree fade, as an ordered 4x4 dither (Godot's own pattern style; the title's is a 2x2
    // alpha-to-mask). A copy past its band is wholly discarded. Not in FABLE_SOLID: a discard
    // there refuses the shared depth-only shadow material, and no culled surface has a band.
#ifndef FABLE_SOLID
    if (v_tree_fade < 0.999) {
        const float bayer[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0,
            3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
        ivec2 cell = ivec2(FRAGCOORD.xy) & ivec2(3);
        if (v_tree_fade <= (bayer[(cell.y * 4) + cell.x] + 0.5) / 16.0) {
            discard;
        }
    }
#endif

    // PARALLAX FIRST, because every other fetch below has to use the displaced coordinate — a
    // shader that offset only the albedo would slide the colour off its own normal and specular.
    //
    // OFFSET MAPPING, not the full occlusion march. One tap and no loop, which matters because this
    // runs on every surface in the world; the march buys self-occlusion at a cost this does not
    // need to pay for what is fundamentally a derived, band-limited height.
    vec2 uv = UV;
#ifndef FABLE_LITE
    if (FABLE_HAS_ORM && has_height && parallax_scale > 0.0) {
        // The view direction in TANGENT space. Godot gives VIEW in view space, and the tangent
        // frame beside it, so the three dots are the change of basis.
        vec3 tangent_view = normalize(vec3(
            dot(VIEW, TANGENT), dot(VIEW, BINORMAL), dot(VIEW, NORMAL)));

        // Centred on 0.5, so a texel at the solved mean does not move at all and the relief
        // displaces both ways around it.
        float relief = texture(orm_tex, UV).a - 0.5;

        // tangent_view.z FLOORED. It goes to zero at grazing incidence and the offset would go to
        // infinity — the smear that makes bad parallax obvious.
        uv = UV + (tangent_view.xy * relief * parallax_scale / max(tangent_view.z, 0.4));
    }
#endif

    vec4 alb = texture(albedo_tex, uv);
    // FABLE_SOLID: no discard either — the second thing that refuses the shared shadow material.
    // A culled material has an albedo without alpha, so its scissor is never on (see vertex()).
#ifndef FABLE_SOLID
    if (alpha_scissor_on && alb.a < alpha_scissor_threshold) {
        discard;
    }
#endif

    // Keep the same vertex shader, parallax coordinate and alpha coverage during the diagnostic.
    // Only the work after coverage is bypassed; this deliberately changes the visible material.
#ifndef FABLE_LITE
    if ((fable_shader_cost_diagnostic & 2) == 0) {
#endif
    // Gamma 2.0 by squaring for legacy assets; standard sRGB linear decode for authored PBR.
    // Alpha stays raw — it was tested above as the artist authored it.
    if (FABLE_IS_PBR) {
        alb.rgb = mix(
            alb.rgb / 12.92,
            pow((alb.rgb + vec3(0.055)) / 1.055, vec3(2.4)),
            step(vec3(0.04045), alb.rgb)
        );
    } else {
        alb.rgb *= alb.rgb;
    }

    if (FABLE_PREMULTIPLIED) {
        alb.rgb /= max(alb.a * alb.a, 1e-6);
    }

    ALBEDO = alb.rgb;
#ifdef FABLE_TRANSLUCENT
    if (alb.a <= 0.001) {
        discard;
    }
    ALPHA = alb.a;
#endif

    // TWO-SIDED GEOMETRY NEEDS A TWO-SIDED NORMAL, and this shader is `cull_disabled`.
    //
    // A back-facing triangle keeps the normal it was authored with, which points away from the
    // viewer — so the diffuse term collapses to zero and the surface takes no direct light at
    // all, only ambient. On a closed prop that never shows, because you never see its back
    // faces. On FOLIAGE it is most of what you see: a grass tuft or a flower is intersecting
    // single-thickness cards, so about half of every plant was lit and the other half was not,
    // which reads as flat, patchy and wrongly coloured rather than as a lighting bug.
    //
    // Flipped before the bake below as well, because the probe SH is evaluated for a direction
    // too and a back face was sampling the wrong lobe of it.
    if (!foliage_normals) {
        float side = fable_normal_side(NORMAL, VERTEX, VIEW);
        NORMAL *= side;
        BINORMAL *= side;
    }

    // THE BAKE IS DARKENED WHERE A MAPPED NORMAL TURNS AWAY, and this surface was the only lit
    // one not doing it. Terrain has applied the term since it landed; a normal-mapped prop
    // standing on that terrain took its bake undarkened, so the ground creased and the wall
    // beside it stayed flat under the same sky.
    //
    // PSHADER_STANDARDMATERIAL_..._AMB1_NRM1 (blob 0335, recompiled out of Shaders.sbk) is three
    // lines, and the order of the multiplies is the whole point:
    //
    //     r6.x   = saturate(dot(vertexNormal, mappedNormal));
    //     r6.x   = r6.x * r6.x;
    //     r6.xyz = r6.xxx * (1 - g_AmbientNormalMapDarkeningColour) + g_AmbientNormalMapDarkeningColour;
    //     r6.xyz = bake * g_LightmapFactorAndSRGBGamma.xyz * r6.xyz + g_GlobalAmbientAndBrightness.xyz;
    //
    // so it scales the BAKE ONLY. The flat global ambient is added afterwards and is never
    // darkened — folding it inside instead gives a normal-mapped surface a floor of zero and
    // reads as grime in every crevice.
    //
    // ndotn IS THE RECONSTRUCTED Z, not the texture's blue channel. The guest samples the normal
    // map `.xy` only (`r26.yz = tfetch2D(g_NormalTextureSampler, ...).xy`) and rebuilds the third
    // component as `sqrt(1 - nx^2 - ny^2)`; in tangent space the vertex normal IS (0, 0, 1), so
    // that reconstructed z is exactly the dot product the guest then takes. Godot's NORMAL_MAP
    // reconstructs z the same way, which is why the map can still be handed over whole.
    float ndotn = 1.0;
    vec3 mapped_view = NORMAL;
    if (FABLE_HAS_NORMAL) {
        vec3 nm = texture(normal_tex, uv).rgb;
        NORMAL_MAP = nm;
        vec2 nxy = (nm.xy * 2.0) - 1.0;
        ndotn = sqrt(max(1.0 - dot(nxy, nxy), 0.0));
        mapped_view = normalize(TANGENT * nxy.x + BINORMAL * nxy.y + NORMAL * ndotn);
    }

    METALLIC = 0.0;

    // Hoisted because the ENVIRONMENT MAP is weighted by it. The guest's line is
    // `r5.xyz = r2.xyz * envSample^2 + r5.xyz`, and r2 is the squared specular fetch
    // (`r2.x = r10.x * r10.x`, where r10 is the g_SpecularTextureSampler read) — so the specular
    // map doubles as the reflectivity mask and nothing else weights the reflection.
    //
    // Zero when there is no specular map, which is the guest's behaviour too: its fallback for
    // that slot is transparent black, so r2 is zero and the cube contributes nothing however
    // bright it is.
    vec3 spec_colour = vec3(0.0);
    float spec_power = spec_power_min;

#ifdef FABLE_LITE_NO_SPEC
    ROUGHNESS = 1.0;
    SPECULAR = 0.0;
#else
    if (has_specular) {
        // SQUARED LIKE THE ALBEDO, and by the same line — the guest reads the specular map raw
        // and then `r8.x = r8.x * r8.x` component by component, exactly as it does for the
        // diffuse. The ALPHA is left alone here too, because it is the exponent selector rather
        // than a colour.
        vec4 spec = texture(specular_tex, uv);
        spec_colour = spec.rgb * spec.rgb;
        spec_power = mix(spec_power_min, spec_power_max, spec.a);

        ROUGHNESS = clamp(sqrt(2.0 / (spec_power + 2.0)), 0.0, 1.0);
        SPECULAR = clamp(dot(spec_colour, LUMA), 0.0, 1.0);
    } else {
        ROUGHNESS = 1.0;
        SPECULAR = 0.0;
    }
#endif

    // THE PACKED PATH OVERRIDES the values derived above, and only those two — the specular
    // colour computed from spec_colour still stands, because ORM has nowhere to put it.
    float ao = 1.0;
    if (FABLE_HAS_ORM) {
        vec3 orm = texture(orm_tex, uv).rgb;
        ao = orm.r;
        ROUGHNESS = orm.g;
        METALLIC = orm.b;
    }

    // ORM HAS NOWHERE TO PUT A SPECULAR COLOUR, so the pair is reconstructed from what it does
    // carry: the exponent inverts the `sqrt(2 / (power + 2))` mapping above, and the colour is
    // the dielectric/metal split. Ours, not the guest's — it has no packed path.
#ifndef FABLE_LITE_NO_SPEC
    v_specular = spec_colour;
    v_specular_power = spec_power;
    // GATED ON THE SPECULAR TOO: the reconstruction only replaces values a specular fetch
    // produced. A normal-only ORM has no exponent to invert, and the 0.04 dielectric floor
    // would hand a specular lobe to a material the guest renders with NONE — its fallback
    // sampler is transparent black.
    if (FABLE_HAS_ORM && (has_specular || FABLE_IS_PBR)) {
        v_specular_power = clamp(2.0 / max(ROUGHNESS * ROUGHNESS, 1e-4) - 2.0, 1.0, 4096.0);
        v_specular = mix(vec3(0.04), alb.rgb, METALLIC);
    }
#endif

    // GODOT'S INDIRECT SPECULAR IS OFF for legacy materials. For authored PBR, keep real roughness/metallic/AO.
    if (!FABLE_IS_PBR) {
        SPECULAR = 0.0;
        METALLIC = 0.0;
        ROUGHNESS = 1.0;
    } else {
        AO = ao;
        AO_LIGHT_AFFECT = 1.0;
        SPECULAR = 0.5;
    }

    vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
#ifndef FABLE_LITE
    vec3 weather_weights = surface_weights(fable_world, world_normal);
    surface_material(weather_weights, fable_world, alb.rgb, ROUGHNESS, METALLIC);
    ALBEDO = alb.rgb;
    float weather_wet = weather_weights.x * (1.0 - weather_weights.y);
    if (max(weather_wet, weather_weights.y) > 0.001) {
        SPECULAR = mix(SPECULAR, 0.5, weather_wet);
        v_specular = mix(v_specular, vec3(0.04), weather_wet);
        v_specular_power = mix(v_specular_power, mix(70.0, 350.0, weather_weights.z), weather_wet);
        v_specular *= 1.0 - weather_weights.y;
        vec2 ripple = surface_ripple(fable_world) * weather_weights.z;
        vec3 base_map = FABLE_HAS_NORMAL ? texture(normal_tex, uv).rgb : vec3(0.5, 0.5, 1.0);
        NORMAL_MAP = mix(base_map, vec3(0.5 + ripple.x, 0.5 + ripple.y, 1.0),
            max(weather_weights.z, weather_weights.y * 0.8));
    }
#endif
#ifdef FABLE_LITE_MODERN
    // THE MODERN MODEL'S FILL, AND NOTHING IT MULTIPLIES AWAY. With `fable_unified` = 1,
    // `fable_ambient` scales every bake but amb3's by max(1 - 1, 0) = 0 — the probe SH, the PRT
    // transfer, the vertex bakes, their darkening, the tether and the bake AO — so none of it is
    // compiled. amb3 (mode 4) keeps its 0.35 share, untethered and unoccluded exactly as below.
    vec3 mapped_world = normalize((INV_VIEW_MATRIX * vec4(mapped_view, 0.0)).xyz);
    vec3 ambient_half = vec3(0.0);
    if (FABLE_MODE > 3.5) {
        vec3 darken = vec3(1.0);
        if (!grass && (!foliage || FABLE_HAS_NORMAL)) {
            vec3 amb_ref = normalize(vec3(fable_ambient_ref.x, fable_ambient_ref.z, fable_ambient_ref.y));
            float bend = 0.5 * (dot(world_normal, amb_ref) + 1.0);
            vec3 bent = normalize(mix(world_normal, amb_ref, bend));
            float nb = clamp(dot(bent, mapped_world), 0.0, 1.0);
            darken = mix(fable_darkening, vec3(1.0), nb * nb);
        }
        ambient_half = fable_bake_amb3(UV2) * fable_lightmap_rgb() * darken;
    }
    EMISSION = alb.rgb * fable_ambient(ambient_half);
    AO = ao;
#else
    vec3 bake;
    if (v_transfer.a > 0.5 && FABLE_MODE > 0.5 && FABLE_MODE < 1.5) {
        // Mode 1 on a mesh that carries its transfer: the guest's PRT, decoded as AMB2 is.
        bake = pow(max(v_transfer.rgb, vec3(0.0)), vec3(FABLE_BAKE_GAMMA));
    } else {
        bake = fable_bake(world_normal, COLOR, UV2);
    }

    // THE GUEST'S BENT NORMAL, exactly (blob 0245 lines 1248-1294, and 0335's use of it):
    // t = 0.5*(dot(N, AmbRef)+1); bent = lerp(N, AmbRef, t); factor = saturate(dot(bent,
    // mappedN))^2. AmbRef is VS c19 = the main light's direction (`sub_821B16F8` writes it
    // beside the 1.3 gamma) — the toward-the-sun vector, NOT world
    // up. Dotting the map against the geometric normal instead under-darkened every wall.
    // WHO TAKES IT: every standard surface draws NRM1 — a record naming no normal map gets the
    // loader's flat default (`sub_82A9EEA0`), and the flat map still bends against the ambient
    // reference, so it darkens too (blob 0351 1582-1586). A tree takes it only with its own map
    // (0563 AMB2_NRM1_TRUNK1 1318-1323; 0559 has none); grass never (GRASS1 0487/0495 have no
    // g_AmbientNormalMapDarkeningColour).
    vec3 mapped_world = normalize((INV_VIEW_MATRIX * vec4(mapped_view, 0.0)).xyz);
    vec3 darken = vec3(1.0);
    if (!grass && (!foliage || FABLE_HAS_NORMAL)) {
        vec3 amb_ref = normalize(vec3(fable_ambient_ref.x, fable_ambient_ref.z, fable_ambient_ref.y));
        float bend = 0.5 * (dot(world_normal, amb_ref) + 1.0);
        vec3 bent = normalize(mix(world_normal, amb_ref, bend));
        float nb = clamp(dot(bent, mapped_world), 0.0, 1.0);
        darken = mix(fable_darkening, vec3(1.0), nb * nb);
    }

    if (foliage) {
        // NO SUN TERM HERE — foliage is lit in `light()`, the sun AND the lamps. Modulating the
        // bake by a sun term as well counted the sun twice and the lamps never.

        // THE GUEST'S SHADOW FLOOR IS DELIBERATELY NOT REPRODUCED HERE. Its canopy applies
        // `shadow * 0.6 + 0.4` to a 0..1 scalar from the screen shadow map, so a shadowed canopy
        // keeps 40% of its light. We have no such scalar — `bake` is a probe COLOUR, not a shadow
        // term — and the arithmetic does not transfer: written against a colour the same expression
        // is `bake * 0.6 + 0.4 * bake`, which is `bake`, a no-op wearing a comment. The floor
        // belongs wherever a real shadow term lands, and until there is one it is left out rather
        // than faked.
    }

    // `bake * LightmapFactor * darkening + GlobalAmbient`, the guest's line for this family.
    // AO multiplies the BAKE, the same place the normal-map darkening goes — the guest's flat
    // global ambient is added after and is never occluded, and folding occlusion into it would
    // give every crevice a floor of zero.
    // AO IS OURS (the generated ORM; the guest has none) and it must never occlude an
    // AUTHORED bake — a baked term already contains its occlusion, and occluding it twice is
    // what split the bridge from the terrain beside it. The probe and flat modes are synthetic
    // ambient with no baked occlusion, so the modernized AO stays legitimate there.
    float bake_ao = FABLE_MODE > 2.5 ? 1.0 : ao;
    vec3 ambient_half = bake * fable_lightmap_rgb() * darken * bake_ao;

    // The sky tether runs POST-LightmapColour (see fable_sky_tether) on every exterior mode;
    // mode 4's amb3 lightmaps are interiors-only and stay authored.
    if (FABLE_MODE < 3.5) {
        ambient_half = fable_sky_tether(ambient_half);
    }

    EMISSION = alb.rgb * fable_ambient(ambient_half);
    // Godot's ambient and GI take the AO the guest's fills took above; 1 in the fable model.
    AO = mix(1.0, ao, fable_unified);
#endif
    if (FABLE_HAS_EMISSION) {
        // Linear for authored PBR mod assets (is_pbr = true) with authored physical radiance.
        // Squared for legacy assets per guest pixel shader (r3.xyz = r3.xyz * r3.xyz),
        // scaled by material self_illum_scale and global fable_emission_boost for HDR headroom.
        vec3 selfIllum = texture(emission_tex, uv).rgb;
        if (FABLE_IS_PBR) {
            EMISSION += selfIllum * self_illum_scale;
        } else {
            EMISSION += (selfIllum * selfIllum) * (self_illum_scale * fable_emission_boost);
        }
    }

#ifndef FABLE_LITE_NO_SPEC
#ifndef FABLE_LITE_NO_ENVMAP
    if (has_envmap) {
        // THE REFLECTION, exactly as the guest builds it:
        //
        //     r6.x   = dot(D, N);                    // D = -r8.wyz, camera TO surface
        //     r6.x   = r6.x + r6.x;
        //     r6.xyw = -N * r6.xxx + D;              // D - 2*dot(D,N)*N
        //
        // which is `reflect(D, N)` term for term. D is the INCIDENT direction, so it is -VIEW:
        // Godot's VIEW points from the fragment to the camera, and the guest's r11 is
        // `g_EyePosition - position`, negated into r8. Reflecting the eye vector instead of the
        // incident one mirrors the reflection through the surface and looks plausible until you
        // move the camera.
        //
        // World space, because the cube is a world-space environment, and off the MAPPED normal
        // (0351 1663-1666 reflect about r10, the normalised mapped normal). NORMAL inside
        // fragment() is still the interpolated one — NORMAL_MAP is applied after it returns.
        vec3 incident = normalize((INV_VIEW_MATRIX * vec4(-VIEW, 0.0)).xyz);
        vec3 reflected = reflect(incident, mapped_world);

        // SQUARED like the albedo and the specular — `r6.xyz = r6.xyz * r6.xyz` — because gamma
        // 2.0 is the guest's decode for every colour fetch, this one included.
        vec3 env = texture(envmap_tex, reflected).rgb;
        EMISSION += spec_colour * env * env;
    }
#endif
#endif

    // THE ENTITY TINT, over everything lit so far and under the fog, where the guest's
    // `r1.xyz = r5.xyz * g_StandardMaterialTintColour.xyz` sits.
    ALBEDO *= entity_tint.rgb;
    EMISSION *= entity_tint.rgb;
#ifndef FABLE_LITE_NO_SPEC
    v_specular *= entity_tint.rgb;
#endif

    // LAST, because it attenuates everything above it. The guest's fog is the final thing its
    // pixel shader does to the colour as well — the only line after it is the global brightness
    // multiply, which Godot owns.
    vec3 fog_t;
    vec3 fog_c;
    if (fable_atmosphere_terms_for(VERTEX, INV_VIEW_MATRIX, !foliage, fog_t, fog_c)) {
        // A SCISSORED PIXEL IS OPAQUE, whatever its texture says. The in-scatter is weighted by
        // coverage so an alpha-BLENDED surface does not add a full fog term over whatever is
        // behind it — but an alpha-TESTED one has already been decided: it either survived the
        // clip or it is gone, and the survivor covers its pixel completely.
        //
        // Passing the texture's alpha there treats an opacity MASK as a coverage weight, and the
        // symptom is specific and was visible in Bower Lake: distant terrain hazed toward the
        // sunset correctly while the trees standing on it stayed near-black silhouettes. Terrain
        // has alpha 1 and received its full in-scatter; every leaf card received a fraction of
        // one, so the further away foliage was, the darker it got instead of the hazier.
        float coverage = alpha_scissor_on ? 1.0 : alb.a;
        fable_atmosphere_apply(fog_t, fog_c, coverage, ALBEDO, EMISSION);
    }

	// THE LIGHTING DEBUG VIEWS — `lighting_debug_view` in the launcher, global
	// `fable_light_debug`. ALBEDO = 0 kills the diffuse light term (Godot multiplies
	// DIFFUSE_LIGHT by ALBEDO after light()), so each mode isolates one half of
	// `albedo * (direct + bake)`: 1 = bake only, 2 = direct only, 3 = raw albedo,
	// 4 = the normal-map darken term. Specular still rides modes 1/3/4 — minor.
	// Not in FABLE_LITE: the views need the full shader (`shader_variant = "full"`).
#ifndef FABLE_LITE
	if (fable_light_debug == 1) {
		ALBEDO = vec3(0.0);
	} else if (fable_light_debug == 2) {
		EMISSION = vec3(0.0);
	} else if (fable_light_debug == 3) {
		EMISSION = alb.rgb;
		ALBEDO = vec3(0.0);
	} else if (fable_light_debug == 4) {
		EMISSION = darken;
		ALBEDO = vec3(0.0);
	} else if (fable_light_debug == 5) {
		// WHICH BAKE MODE THIS SURFACE TOOK: red = flat ambient (0), yellow = probe (1),
		// magenta = mode 2, green = vertex bake (3), blue = texture lightmap (4).
		float m = lightmap_colour.w;
		vec3 tag = m > 3.5 ? vec3(0.1, 0.3, 1.0)
			: m > 2.5 ? vec3(0.1, 1.0, 0.1)
			: m > 1.5 ? vec3(1.0, 0.1, 1.0)
			: m > 0.5 ? vec3(1.0, 1.0, 0.1)
			: vec3(1.0, 0.1, 0.1);
		EMISSION = tag;
		ALBEDO = vec3(0.0);
	} else if (fable_light_debug == 6 || fable_light_debug == 7) {
		// 6: the directional SHADOW TERM alone; 7: signed NdotL — light() writes them into
		// DIFFUSE_LIGHT, and a white albedo with no emission lets them through unscaled.
		EMISSION = vec3(0.0);
		ALBEDO = vec3(1.0);
	} else if (fable_light_debug == 9) {
		// 9: every light's direct term on a WHITE albedo — the summed light itself, unscaled.
		EMISSION = vec3(0.0);
		ALBEDO = vec3(1.0);
	} else if (fable_light_debug == 10) {
		// WHICH WAY THE RASTERISER SAYS THIS FACE POINTS: green front, red back. Culling is off, so
		// a surface wound inside out still draws; the only sign is that its normal is negated above.
		EMISSION = FRONT_FACING ? vec3(0.1, 1.0, 0.1) : vec3(1.0, 0.1, 0.1);
		ALBEDO = vec3(0.0);
	} else if (fable_light_debug == 8) {
		// The VIEW-SPACE NORMAL as colour — the terrain-parity diagnostic.
		EMISSION = (NORMAL * 0.5) + 0.5;
		ALBEDO = vec3(0.0);
	}
    } else {
        ALBEDO = vec3(0.5);
        EMISSION = vec3(0.0);
        NORMAL_MAP = vec3(0.5, 0.5, 1.0);
        METALLIC = 0.0;
        ROUGHNESS = 1.0;
        SPECULAR = 0.0;
        v_specular = vec3(0.0);
        v_specular_power = 1.0;
    }
#endif
}

/// THE GUEST'S MODEL, so every family shades by the same rules.
///
/// There is no separate main-light constant in the shipped microcode: every light — sun, moon, torch, lantern — is a
/// slot in `g_GlobalLightParamsPS`, and the distance term is a LINEAR ramp rather than an inverse
/// square, which is what `fable_attenuation` restores. The sun reaches it as a slot with
/// `Type = 0` and `InvRadii = 0`, so its attenuation collapses to the shadow term alone.
///
/// FOLIAGE TAKES LAMBERT AND NO SPECULAR: the TRUNK1 pair (blob 0559) every shipped tree draws.
///
/// GUARDED, NOT RETURNED FROM. Godot rejects `return` in the `light` processor outright —
/// "Using 'return' in the 'light' processor function is incorrect" — and a shader that fails to
/// compile falls back to a default material, which strips every texture in the scene.
void light() {
#ifndef FABLE_LITE
    // VIEW 6: the directional light's RAW shadow term, greyscale, on every surface — white
    // is unshadowed, black fully shadowed. The one view that separates "the shadow map does
    // not reach this surface" from "the direct term is too weak for the shadow to show".
    if (fable_light_debug == 6) {
        if (LIGHT_IS_DIRECTIONAL) {
            DIFFUSE_LIGHT = vec3(clamp(ATTENUATION, 0.0, 1.0));
        }
    } else if (fable_light_debug == 7) {
        // SIGNED NdotL — the terrain-parity diagnostic; see terrain_splat's view 7.
        if (LIGHT_IS_DIRECTIONAL) {
            DIFFUSE_LIGHT = vec3((dot(NORMAL, LIGHT) * 0.5) + 0.5);
        }
    } else
#endif
    if (dot(NORMAL, LIGHT) > 0.0) {
        // FACING AWAY, BOTH TERMS ARE EXACTLY ZERO — the diffuse's saturate(NdotL) and the
        // specular's saturate(4 NdotL) gate scale finite factors — so the light is skipped there,
        // and wherever its attenuation is zero, without changing a bit of the sum.
        float atten = fable_attenuation(ATTENUATION, LIGHT_IS_DIRECTIONAL, SPECULAR_AMOUNT, fable_world);
        if (atten > 0.0) {
            // Plain Lambert, no specular, on foliage: every shipped tree draws the TRUNK1 pair,
            // leaves included (blob 0559 `saturate(dot(L, N))` x the shadow; the canopy wrap of
            // 0524 is never reached), and a one-light tree has no specular (559 also serves SP1).
            DIFFUSE_LIGHT += fable_direct(NORMAL, LIGHT, atten, LIGHT_COLOR);
            // A black specular colour (no map, grass, a black texel) takes no highlight.
#ifndef FABLE_LITE_NO_SPEC
#ifdef FABLE_LITE
            if (!foliage && any(notEqual(v_specular, vec3(0.0)))) {
#else
            if (!foliage && (!fable_zero_specular_work || any(notEqual(v_specular, vec3(0.0))))) {
#endif
                SPECULAR_LIGHT += fable_specular(
                    NORMAL, LIGHT, VIEW, atten, LIGHT_COLOR, v_specular, v_specular_power);
            }
#endif
        }
    }
}
