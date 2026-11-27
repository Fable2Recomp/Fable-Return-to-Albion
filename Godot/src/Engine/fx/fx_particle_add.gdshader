shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

#include "res://src/Engine/fx/fx_billboard.gdshaderinc"

uniform sampler2D fx_tex : hint_default_white, filter_linear, repeat_disable;
uniform float fx_alpha_ref = 1.0;
#include "res://src/Engine/fx/fx_soft.gdshaderinc"
uniform float fx_exposure = 3.3333333;
global uniform sampler2D fable_exposure_tex;
global uniform float fable_exposure_valid;

global uniform float fable_emission_boost = 1.0;

uniform float fx_src_alpha = 1.0;
uniform bool is_pbr = false;

// THE SAME COMPOSITION AS THE MIX VARIANT — only the blend differs.
//
// 0210_pshader_particlematerial serves both, and it is a pure multiply chain:
//
//     r2.x   = vertexColour.a * g_GlobalAmbientAndBrightness.w
//     r2.xyz = r2.xxx * vertexColour.rgb
//     r2.xyz = r2.xyz * texel.rgb          <- the texel enters
//     oC0.xyz = r2.xyz * texel.rgb         <- and again: gamma 2.0 by squaring
//     oC0.w   = vertexColour.a * texel.a
//
// So neither a clamp nor `ALPHA = 1.0` is the guest's. It writes a real alpha and that alpha
// is live in every blend case the bank actually asks for —
// SRC_ALPHA:INV_SRC_ALPHA, ZERO:INV_SRC_ALPHA and the ONE:INV_SRC_ALPHA default all consume
// it, and every particle draw enables the alpha test against it too (D3DRS_ALPHATESTENABLE = 1
// at 0x82a5a2dc). Forcing it to 1 makes soft-edged particles hard-edged and stops them fading.
void fragment() {
	// Zero-discard `depth_draw_never` blend path: guard `fx_soft_fade`, distance fade, and boost
	// math inside `if (!should_discard)` and emit the blend identity (`ALPHA = 0`) otherwise.
	vec2 uv_dx = dFdx(UV);
	vec2 uv_dy = dFdy(UV);
	vec4 texel = textureGrad(fx_tex, UV, uv_dx, uv_dy);
	bool should_discard = (texel.a * COLOR.a * 255.0 <= fx_alpha_ref);

	ALBEDO = vec3(0.0);
	ALPHA = 0.0;

	if (!should_discard) {
		float fade = fx_soft_fade(texel.a * COLOR.a, VERTEX, SCREEN_UV, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);

		// Attenuate extreme unshaded HDR radiance on distant additive particles
		// so distant torches/lantern flames don't stay at peak emissive brightness
		float dist = length(VERTEX);
		float dist_fade = 1.0;
		if (dist > 12.0) {
			float t = clamp((dist - 12.0) / 33.0, 0.0, 1.0);
			dist_fade = mix(1.0, 0.35, t * t * (3.0 - 2.0 * t));
		}

		vec3 tex_rgb = is_pbr ? texel.rgb : (texel.rgb * texel.rgb * mix(1.0, max(fable_emission_boost, 1.0), dist_fade));
		vec3 rgb = max(COLOR.a * COLOR.rgb * tex_rgb * dist_fade, vec3(0.0));
		ALBEDO = rgb * fade;
		ALPHA = mix(1.0, texel.a * COLOR.a * fade, fx_src_alpha);
	}
}
