shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

#include "res://src/Engine/fx/fx_billboard.gdshaderinc"

uniform sampler2D fx_tex : hint_default_white, filter_linear, repeat_enable;
uniform float fx_alpha_ref = 1.0;
#include "res://src/Engine/fx/fx_soft.gdshaderinc"
uniform float fx_exposure = 3.3333333;
global uniform sampler2D fable_exposure_tex;
global uniform float fable_exposure_valid;
uniform bool is_pbr = false;

void fragment() {
	// Zero-discard `depth_draw_never` blend path: guard `fx_soft_fade` and shading math inside
	// `if (!should_discard)` and output the blend identity (`ALBEDO = 0, ALPHA = 0`) otherwise.
	vec2 uv_dx = dFdx(UV);
	vec2 uv_dy = dFdy(UV);
	vec4 texel = textureGrad(fx_tex, UV, uv_dx, uv_dy);
	bool should_discard = (texel.a * COLOR.a * 255.0 <= fx_alpha_ref);

	ALBEDO = vec3(0.0);
	ALPHA = 0.0;

	if (!should_discard) {
		float fade = fx_soft_fade(texel.a * COLOR.a, VERTEX, SCREEN_UV, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);
		vec3 tex_rgb = is_pbr ? texel.rgb : (texel.rgb * texel.rgb);
		vec3 rgb = COLOR.a * COLOR.rgb * tex_rgb;
		// NO CLAMP — MIX-BLENDED PARTICLES ARE SCENE CONTENT. The guest draws them into the HDR
		// scene with no bound of their own and the tone curve compresses them like any surface;
		// only the ADDITIVE layer is bounded. Clamping here pinned smoke to a fixed fraction of
		// display white whatever the scene around it was doing.
		if (fable_exposure_valid <= 0.5) {
			rgb *= fx_exposure;
		}
		rgb = max(rgb, vec3(0.0));
		ALBEDO = rgb * fade;
		ALPHA = texel.a * COLOR.a * fade;
	}
}
