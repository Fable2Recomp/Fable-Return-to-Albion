shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

#include "res://src/Engine/fx/fx_billboard.gdshaderinc"

uniform sampler2D fx_tex : hint_default_white, filter_linear, repeat_disable;
uniform float fx_alpha_ref = 1.0;
#include "res://src/Engine/fx/fx_soft.gdshaderinc"

void fragment() {
	// Zero-discard `depth_draw_never` blend path: guard `fx_soft_fade` inside `if (!should_discard)`.
	vec2 uv_dx = dFdx(UV);
	vec2 uv_dy = dFdy(UV);
	vec4 texel = textureGrad(fx_tex, UV, uv_dx, uv_dy);
	bool should_discard = (texel.a * COLOR.a * 255.0 <= fx_alpha_ref);

	ALBEDO = vec3(0.0);
	ALPHA = 0.0;

	if (!should_discard) {
		float fade = fx_soft_fade(texel.a * COLOR.a, VERTEX, SCREEN_UV, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);
		ALPHA = texel.a * COLOR.a * fade;
	}
}
