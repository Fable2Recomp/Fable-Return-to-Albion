shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, ambient_light_disabled;

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
/// — two hemisphere terms, sky and sun, wrapped rather than hard `dot(L, N)`.
///
/// THAT IS WHY A CANOPY DRAWN AS A SOLID GOES FLAT. A leaf card's normal is a poor description of a
/// canopy, so every card takes roughly the same hard `dot(L, N)` and the whole thing collapses to
/// one tone. Wrapping restores the gradient the guest gets per vertex, from the same normal we
/// already have.
uniform bool foliage = false;

/// How much of a canopy's gradient comes from the sun rather than from the sky hemisphere.
/// 0 is the old up-only behaviour; 1 is sun alone. OURS, not the guest's — its split is two
/// interpolated vertex colours we have not decoded.
uniform float foliage_sun_mix : hint_range(0.0, 1.0) = 0.5;

/// How dark a canopy's unlit side gets. Was a fixed 0.8, which is a 20% range across the whole
/// canopy and reads as flat.
uniform float foliage_floor : hint_range(0.0, 1.0) = 0.62;

void vertex() {
    // GUARDED BY AN `if`, NOT AN EARLY `return`. Godot rejects the latter outright — "Using
    // 'return' in the 'vertex' processor function is incorrect" — and the shader then fails to
    // compile as a whole, taking every uniform with it.
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
}

void fragment() {
    // PARALLAX FIRST, because every other fetch below has to use the displaced coordinate — a
    // shader that offset only the albedo would slide the colour off its own normal and specular.
    //
    // OFFSET MAPPING, not the full occlusion march. One tap and no loop, which matters because this
    // runs on every surface in the world; the march buys self-occlusion at a cost this does not
    // need to pay for what is fundamentally a derived, band-limited height.
    vec2 uv = UV;
    if (has_orm && parallax_scale > 0.0) {
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
    if (alpha_scissor_on && alb.a < alpha_scissor_threshold) {
        discard;
    }

    // Gamma 2.0 by squaring, exactly as the guest's pixel shader does it. Alpha stays raw —
    // it was tested above as the artist authored it.
    alb.rgb *= alb.rgb;
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
    NORMAL = FRONT_FACING ? NORMAL : -NORMAL;
    BINORMAL = FRONT_FACING ? BINORMAL : -BINORMAL;

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
    if (has_normal) {
        vec3 nm = texture(normal_tex, uv).rgb;
        NORMAL_MAP = nm;
        vec2 nxy = (nm.xy * 2.0) - 1.0;
        ndotn = sqrt(max(1.0 - dot(nxy, nxy), 0.0));
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

    if (has_specular) {
        // SQUARED LIKE THE ALBEDO, and by the same line — the guest reads the specular map raw
        // and then `r8.x = r8.x * r8.x` component by component, exactly as it does for the
        // diffuse. The ALPHA is left alone here too, because it is the exponent selector rather
        // than a colour.
        vec4 spec = texture(specular_tex, uv);
        spec_colour = spec.rgb * spec.rgb;
        float power = mix(spec_power_min, spec_power_max, spec.a);

        ROUGHNESS = clamp(sqrt(2.0 / (power + 2.0)), 0.0, 1.0);
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

    vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
    vec3 bake = fable_bake(world_normal, COLOR);
    vec3 darken = mix(darkening_colour.rgb, vec3(1.0), ndotn * ndotn);

    if (foliage) {
        // WRAPPED, because the guest's canopy term is `saturate(const - x)` rather than a clamp at
        // zero: light does not stop dead where the surface turns away, which is what stops a
        // canopy reading as a cardboard cut-out.
        //
        // The normal-map darkening is dropped with it. It exists to shade a solid surface's
        // relief, and a canopy has no relief to shade — keeping it would put the flat tone back by
        // another route.
        // THE FLOOR IS AN APPROXIMATION AND IT IS MINE, not the guest's. Its canopy blends two
        // interpolated colours built in the tree vertex shader from `g_GlobalLightParamsVS(7)` and
        // `(8)` — a sun term and a sky term — and until that split is decoded there is no derived
        // number to put here. A wrap against world up has the right SHAPE (top lit, underside
        // shaded, no hard terminator) but its depth is a choice.
        //
        // 0.55 was the first choice and it was too dark: foreground foliage in Bower Lake went
        // near-black while everything around it was correctly lit. 0.8 keeps the gradient that
        // stops a canopy reading as one flat tone without inventing a deep shadow the guest does
        // not have. It errs toward the shipped look, which is the right direction to err in for a
        // number nobody measured.
        // TWO TERMS, SUN AND SKY — the shape the guest's canopy actually has. Its vertex
        // shader builds `sun * r4.z + sky * r4.w` and wraps the result, so a leaf facing the
        // sun is brighter than one facing away. An up-only wrap has no sun in it at all, which
        // is what collapsed a canopy to one flat tone whatever the time of day.
        //
        // `atmos_sun` is the same global the fog samples, in dome axes — swapping y and z is
        // its own inverse and puts it back in world. The DIRECTION is measured; the two weights
        // and the floor below are still ours, which is why they are uniforms.
        vec3 sun_world = normalize(vec3(atmos_sun.x, atmos_sun.z, atmos_sun.y));
        float sun_wrap = dot(world_normal, sun_world) * 0.5 + 0.5;
        float sky_wrap = dot(world_normal, vec3(0.0, 1.0, 0.0)) * 0.5 + 0.5;
        float wrap = mix(sky_wrap, sun_wrap, foliage_sun_mix);
        darken = vec3(mix(foliage_floor, 1.0, wrap));

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
    EMISSION = alb.rgb * fable_ambient(bake * lightmap_colour.rgb * darken * ao);
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

        // This family's specular goes through Godot's own lobe rather than through `fable_specular`,
        // and SPECULAR is a scalar, so the fog reaches it only as a scalar. Weighted by the same
        // luma the channel was built with above, which is the closest a single number gets to a
        // per-channel extinction.
        SPECULAR *= dot(fog_t, LUMA);
    }
}
