shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, ambient_light_disabled;

#include "res://src/Engine/material/fable_lighting.gdshaderinc"

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

uniform bool has_normal = false;
uniform bool has_emission = false;
uniform bool has_specular = false;
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

void fragment() {
    vec4 alb = texture(albedo_tex, UV);
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
        vec3 nm = texture(normal_tex, UV).rgb;
        NORMAL_MAP = nm;
        vec2 nxy = (nm.xy * 2.0) - 1.0;
        ndotn = sqrt(max(1.0 - dot(nxy, nxy), 0.0));
    }

    METALLIC = 0.0;

    if (has_specular) {
        // SQUARED LIKE THE ALBEDO, and by the same line — the guest reads the specular map raw
        // and then `r8.x = r8.x * r8.x` component by component, exactly as it does for the
        // diffuse. The ALPHA is left alone here too, because it is the exponent selector rather
        // than a colour.
        vec4 spec = texture(specular_tex, UV);
        vec3 spec_colour = spec.rgb * spec.rgb;
        float power = mix(spec_power_min, spec_power_max, spec.a);

        ROUGHNESS = clamp(sqrt(2.0 / (power + 2.0)), 0.0, 1.0);
        SPECULAR = clamp(dot(spec_colour, LUMA), 0.0, 1.0);
    } else {
        ROUGHNESS = 1.0;
        SPECULAR = 0.0;
    }

    vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
    vec3 bake = fable_bake(world_normal, COLOR);
    vec3 darken = mix(darkening_colour.rgb, vec3(1.0), ndotn * ndotn);

    // `bake * LightmapFactor * darkening + GlobalAmbient`, the guest's line for this family.
    EMISSION = alb.rgb * fable_ambient(bake * lightmap_colour.rgb * darken);
    if (has_emission) {
        // Squared for the same reason, and by the same line: the guest's self-illumination
        // fetch is followed by `r3.xyz = r3.xyz * r3.xyz` before it is added in.
        //
        // AND SCALED. The full line is
        // `colour = selfIllum^2 * g_StandardMaterialPSConstants(0).zzz + colour`, and c24.z is the
        // material's own self-illumination scale — block offset 29, stored to material+72 by
        // `82b24954 stfs f0, 0x48(r30)`. Adding the texture unscaled made every self-illuminated
        // surface glow at whatever full strength its map happened to be authored at.
        vec3 selfIllum = texture(emission_tex, UV).rgb;
        EMISSION += selfIllum * selfIllum * self_illum_scale;
    }
}
