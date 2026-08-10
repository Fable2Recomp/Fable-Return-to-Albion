shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled,
    ambient_light_disabled, specular_disabled;

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
uniform bool has_normal = false;
uniform bool has_emission = false;
uniform bool alpha_scissor_on = false;
uniform float alpha_scissor_threshold = 0.25;

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
    // viewer — so `clamp(dot(normal, light), 0, 1)` in `fable_direct` collapses to zero and the
    // surface takes no direct light at all, only ambient. On a closed prop that never shows,
    // because you never see its back faces. On FOLIAGE it is most of what you see: a grass tuft
    // or a flower is intersecting single-thickness cards, so about half of every plant was lit
    // and the other half was not, which reads as flat, patchy and wrongly coloured rather than
    // as a lighting bug.
    //
    // Flipped before the bake below as well as before `light()`, because the probe SH is
    // evaluated for a direction too and a back face was sampling the wrong lobe of it.
    NORMAL = FRONT_FACING ? NORMAL : -NORMAL;
    BINORMAL = FRONT_FACING ? BINORMAL : -BINORMAL;

    if (has_normal) {
        NORMAL_MAP = texture(normal_tex, UV).rgb;
    }

    vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
    vec3 bake = fable_bake(world_normal, COLOR);

    // `bake * LightmapFactor + GlobalAmbient`, the guest's line for this shader family.
    EMISSION = alb.rgb * fable_ambient(bake * lightmap_colour.rgb);
    if (has_emission) {
        // Squared for the same reason, and by the same line: the guest's self-illumination
        // fetch is followed by `r3.xyz = r3.xyz * r3.xyz` before it is added in.
        vec3 selfIllum = texture(emission_tex, UV).rgb;
        EMISSION += selfIllum * selfIllum;
    }
}

void light() {
    DIFFUSE_LIGHT += fable_direct(NORMAL, LIGHT, ATTENUATION, LIGHT_COLOR, ALBEDO);
}
