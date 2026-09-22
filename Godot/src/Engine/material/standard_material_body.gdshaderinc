// The whole of the standard material. Two shader files include it and differ in one word of their
// render mode: `standard_material.gdshader` draws both sides, `standard_material_culled.gdshader`
// culls back faces as the guest does. A render mode is fixed per shader file, so the body lives
// here once.

#include "res://src/Engine/material/fable_lighting.gdshaderinc"
#include "res://src/Engine/level/environment/fable_atmosphere.gdshaderinc"

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

/// The wind's direction in world XZ, as a UNIT vector.
///
/// DIRECTION ONLY — NO STRENGTH. The theme states `WindStrengthMin/Max` alongside the rotation, and
/// the first version of this multiplied by it. Its UNITS ARE NOT ESTABLISHED: a census of the
/// shipped themes could not recover a plausible range for it, so multiplying by it was applying a
/// gain against an unknown scale, and the result bent every tree in Bowerstone flat. Only terms
/// whose scale is known are used here, and an angle is one of them.
uniform vec2 wind_direction = vec2(0.0, 0.0);

/// How far the surface leans, as a FRACTION OF ITS OWN HEIGHT.
///
/// THE MESH STATES THIS ITSELF. `WindSkewMaxAmplitude` sits in the mesh file header beside the
/// movement mode, and it is 0.05 on every shipped mesh that has one — so a four-metre tree's canopy
/// travels about twenty centimetres. That is a breeze, which is what Albion has.
///
/// Zero means the surface does not move, and it is the default: nothing sways until a mesh header
/// says it does.
uniform float wind_amplitude : hint_range(0.0, 0.5) = 0.0;

/// How fast it sways. Also from the mesh header — `WindSkewMaxSpeed`, 1.0 on everything shipped.
uniform float wind_speed : hint_range(0.1, 8.0) = 1.0;

/// Light this surface as a tree canopy rather than as a solid.
///
/// DERIVED FROM `treematerial`, whose canopy permutation
/// (`0544_pshader_treematerial_blt1_glt1_mist1`) is unambiguous: it references
/// `g_GlobalLightParamsPS` NOWHERE and takes no `dot` against a normal. A canopy is lit entirely by
/// two colours interpolated from its vertices, and the vertex shader builds them as
///
///     r6.xyz = r4.www * g_GlobalLightParamsVS(8).xyz          // sky colour  x term
///     r7.xyz = r4.zzz * g_GlobalLightParamsVS(7).xyz + r6.xyz // sun colour  x term + sky
///     r1.xyz = saturate(-r7.xzy + c253.www)                   // WRAPPED, not clamped at zero
///
/// — a per-light WRAP diffuse, `saturate((N.L + w)/(1 + w))`, decoded from tree VS blob 0524.
global uniform int fable_light_debug;

uniform bool foliage = false;

/// The canopy wrap amount `g_StandardMaterialVSConstants.w` (VS c50.w). Runtime material data
/// with no CTAB default anywhere in the corpus — 0.5 is a placeholder until a live capture
/// reads it. The FORMULA is the guest's; this number is not yet.
uniform float foliage_wrap : hint_range(0.0, 1.0) = 0.5;

/// THE SPECULAR TERMS, HANDED TO `light()`. Godot's `light()` cannot reach a fragment local, and
/// the guest's specular is Phong over the material's own maps, so the colour and the exponent
/// travel as varyings — the same arrangement `standard_material_phong` uses.
varying vec3 v_specular;
varying float v_specular_power;

/// The PRT ambient, computed per vertex as the guest's AMB2 vertex shader does; a = present.
varying vec4 v_transfer;

void vertex() {
    v_transfer = fable_transfer(CUSTOM0, CUSTOM1, CUSTOM2, mat3(MODEL_MATRIX));
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
        // ANCHORED AT THE BASE, and the displacement is a FRACTION OF HEIGHT. That is what keeps
        // the motion proportionate without needing to know how tall anything is: a blade of grass
        // moves millimetres and a tree's canopy moves centimetres, from one number.
        float height = max(VERTEX.y, 0.0);

        // PHASE FROM WORLD POSITION, so neighbouring instances are not synchronised. A MultiMesh
        // draws thousands of copies of one mesh through one shader, and without this every blade
        // in a field bends on the same beat — which reads as the ground breathing, not as wind.
        float offset = (MODEL_MATRIX[3].x + MODEL_MATRIX[3].z) * 0.35;
        float phase = (TIME * wind_speed) + offset;

        // Two octaves, NORMALISED TO +/-1 so the amplitude above means exactly what it says. The
        // second is deliberately not a harmonic of the first, or they would beat into a slow pulse.
        float bend = (sin(phase) * 0.67) + (sin(phase * 2.3) * 0.33);

        VERTEX.xz += wind_direction * bend * wind_amplitude * height;
    }
#endif

    // After the wind: the light table measures to where the vertex is drawn.
    fable_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    // PARALLAX FIRST, because every other fetch below has to use the displaced coordinate — a
    // shader that offset only the albedo would slide the colour off its own normal and specular.
    //
    // OFFSET MAPPING, not the full occlusion march. One tap and no loop, which matters because this
    // runs on every surface in the world; the march buys self-occlusion at a cost this does not
    // need to pay for what is fundamentally a derived, band-limited height.
    vec2 uv = UV;
    if (has_orm && has_height && parallax_scale > 0.0) {
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

    vec4 alb = texture(albedo_tex, uv);
    // FABLE_SOLID: no discard either — the second thing that refuses the shared shadow material.
    // A culled material has an albedo without alpha, so its scissor is never on (see vertex()).
#ifndef FABLE_SOLID
    if (alpha_scissor_on && alb.a < alpha_scissor_threshold) {
        discard;
    }
#endif

    // Gamma 2.0 by squaring, exactly as the guest's pixel shader does it. Alpha stays raw —
    // it was tested above as the artist authored it.
    alb.rgb *= alb.rgb;

    if (premultiplied_alpha) {
        alb.rgb /= max(alb.a * alb.a, 1e-6);
    }

    ALBEDO = alb.rgb;

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
    if (has_normal) {
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

    // THE PACKED PATH OVERRIDES the values derived above, and only those two — the specular
    // colour computed from spec_colour still stands, because ORM has nowhere to put it.
    float ao = 1.0;
    if (has_orm) {
        vec3 orm = texture(orm_tex, uv).rgb;
        ao = orm.r;
        ROUGHNESS = orm.g;
        METALLIC = orm.b;
    }

    // ORM HAS NOWHERE TO PUT A SPECULAR COLOUR, so the pair is reconstructed from what it does
    // carry: the exponent inverts the `sqrt(2 / (power + 2))` mapping above, and the colour is
    // the dielectric/metal split. Ours, not the guest's — it has no packed path.
    v_specular = spec_colour;
    v_specular_power = spec_power;
    // GATED ON THE SPECULAR TOO: the reconstruction only replaces values a specular fetch
    // produced. A normal-only ORM has no exponent to invert, and the 0.04 dielectric floor
    // would hand a specular lobe to a material the guest renders with NONE — its fallback
    // sampler is transparent black.
    if (has_orm && has_specular) {
        v_specular_power = clamp(2.0 / max(ROUGHNESS * ROUGHNESS, 1e-4) - 2.0, 1.0, 4096.0);
        v_specular = mix(vec3(0.04), alb.rgb, METALLIC);
    }

    // GODOT'S INDIRECT SPECULAR IS OFF. The direct lobe is ours — `light()` takes it from the two
    // varyings above — so SPECULAR, METALLIC and ROUGHNESS reach only Godot's ambient and GI
    // REFLECTIONS. Those were inert while the shader disabled Godot's ambient; with the fill now
    // Godot's they are live, and a Phong exponent turned into roughness 0.14 made every leaf card
    // a mirror of the sky: lavender canopies over a red dusk (Bower Lake, view 1 lit them alone).
    // The guest has no indirect specular; its one reflection is the authored cube below.
    SPECULAR = 0.0;
    METALLIC = 0.0;
    ROUGHNESS = 1.0;

    vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
    vec3 bake = fable_bake(world_normal, COLOR, UV2);
    if (v_transfer.a > 0.5 && lightmap_colour.w > 0.5 && lightmap_colour.w < 1.5) {
        // Mode 1 on a mesh that carries its transfer: the guest's PRT, decoded as AMB2 is.
        bake = pow(max(v_transfer.rgb, vec3(0.0)), vec3(FABLE_BAKE_GAMMA));
    }

    // THE GUEST'S BENT NORMAL, exactly (blob 0245 lines 1248-1294, and 0335's use of it):
    // t = 0.5*(dot(N, AmbRef)+1); bent = lerp(N, AmbRef, t); factor = saturate(dot(bent,
    // mappedN))^2. AmbRef is VS c19 = the main light's direction (`sub_821B16F8` writes it
    // beside the 1.3 gamma) — the same toward-the-sun vector the foliage wrap uses, NOT world
    // up. Dotting the map against the geometric normal instead under-darkened every wall.
    // nrm0 surfaces take no darkening, as shipped — the term is the nrm1 permutation's.
    vec3 darken = vec3(1.0);
    if (has_normal) {
        vec3 amb_ref = normalize(vec3(atmos_sun.x, atmos_sun.z, atmos_sun.y));
        float bend = 0.5 * (dot(world_normal, amb_ref) + 1.0);
        vec3 bent = normalize(mix(world_normal, amb_ref, bend));
        vec3 mapped_world = normalize((INV_VIEW_MATRIX * vec4(mapped_view, 0.0)).xyz);
        float nb = clamp(dot(bent, mapped_world), 0.0, 1.0);
        darken = mix(darkening_colour.rgb, vec3(1.0), nb * nb);
    }

    if (foliage) {
        // NO SUN TERM HERE ANY MORE — foliage is lit in `light()` by the guest's per-light wrap,
        // which covers the sun AND the lamps (blob 0524 runs the same formula over every slot).
        // Modulating the bake by a sun wrap as well counted the sun twice and the lamps never:
        // a hedge beside a lantern stayed pitch dark at night. The bake keeps only the relief
        // exemption: a canopy has none to shade.
        darken = vec3(1.0);

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
    float bake_ao = lightmap_colour.w > 2.5 ? 1.0 : ao;
    vec3 ambient_half = bake * lightmap_colour.rgb * darken * bake_ao;

    // The sky tether runs POST-LightmapColour (see fable_sky_tether) on every exterior mode;
    // mode 4's amb3 lightmaps are interiors-only and stay authored.
    if (lightmap_colour.w < 3.5) {
        ambient_half = fable_sky_tether(ambient_half);
    }

    EMISSION = alb.rgb * fable_ambient(ambient_half);
    // Godot's ambient and GI take the AO the guest's fills took above; 1 in the fable model.
    AO = mix(1.0, ao, fable_unified);
    if (has_emission) {
        // Squared for the same reason, and by the same line: the guest's self-illumination
        // fetch is followed by `r3.xyz = r3.xyz * r3.xyz` before it is added in.
        //
        // AND SCALED. The full line is
        // `colour = selfIllum^2 * g_StandardMaterialPSConstants(0).zzz + colour`, and c24.z is the
        // material's own self-illumination scale — block offset 29, stored to material+72 by
        // `82b24954 stfs f0, 0x48(r30)`. Adding the texture unscaled made every self-illuminated
        // surface glow at whatever full strength its map happened to be authored at.
        vec3 selfIllum = texture(emission_tex, uv).rgb;
        EMISSION += selfIllum * selfIllum * self_illum_scale;
    }

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
        // World space, because the cube is a world-space environment. NORMAL here is already the
        // two-sided, normal-mapped one from above.
        vec3 incident = normalize((INV_VIEW_MATRIX * vec4(-VIEW, 0.0)).xyz);
        vec3 reflected = reflect(incident, world_normal);

        // SQUARED like the albedo and the specular — `r6.xyz = r6.xyz * r6.xyz` — because gamma
        // 2.0 is the guest's decode for every colour fetch, this one included.
        vec3 env = texture(envmap_tex, reflected).rgb;
        EMISSION += spec_colour * env * env;
    }

    // LAST, because it attenuates everything above it. The guest's fog is the final thing its
    // pixel shader does to the colour as well — the only line after it is the global brightness
    // multiply, which Godot owns.
    vec3 fog_t;
    vec3 fog_c;
    if (fable_atmosphere_terms(VERTEX, INV_VIEW_MATRIX, fog_t, fog_c)) {
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
}

/// THE GUEST'S MODEL, so every family shades by the same rules.
///
/// There is no separate main-light constant in the shipped microcode: every light — sun, moon, torch, lantern — is a
/// slot in `g_GlobalLightParamsPS`, and the distance term is a LINEAR ramp rather than an inverse
/// square, which is what `fable_attenuation` restores. The sun reaches it as a slot with
/// `Type = 0` and `InvRadii = 0`, so its attenuation collapses to the shadow term alone.
///
/// FOLIAGE TAKES THE WRAP, NOT LAMBERT. The canopy vertex shader (blob 0524) runs
/// `saturate((N.L + w)/(1 + w))` over EVERY light slot — the sun and the lamps alike — with the
/// same linear attenuation, and no specular.
///
/// GUARDED, NOT RETURNED FROM. Godot rejects `return` in the `light` processor outright —
/// "Using 'return' in the 'light' processor function is incorrect" — and a shader that fails to
/// compile falls back to a default material, which strips every texture in the scene.
void light() {
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
    } else {
        float atten = fable_attenuation(ATTENUATION, LIGHT_IS_DIRECTIONAL, SPECULAR_AMOUNT, fable_world);
        if (foliage) {
            float wrap = clamp((dot(NORMAL, LIGHT) + foliage_wrap) / (1.0 + foliage_wrap), 0.0, 1.0);
            DIFFUSE_LIGHT += wrap * atten * (LIGHT_COLOR / PI);
        } else {
            DIFFUSE_LIGHT += fable_direct(NORMAL, LIGHT, atten, LIGHT_COLOR);
            SPECULAR_LIGHT += fable_specular(
                NORMAL, LIGHT, VIEW, atten, LIGHT_COLOR, v_specular, v_specular_power);
        }
    }
}
