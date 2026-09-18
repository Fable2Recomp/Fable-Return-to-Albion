shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

#include "res://src/Engine/fx/fx_billboard.gdshaderinc"

uniform sampler2D fx_tex : filter_linear, repeat_enable;
uniform float fx_alpha_ref = 1.0;
#include "res://src/Engine/fx/fx_soft.gdshaderinc"

void fragment() {
	vec4 texel = texture(fx_tex, UV);
	if (texel.a * COLOR.a * 255.0 <= fx_alpha_ref) {
		discard;
	}
	float fade = fx_soft_fade(texel.a * COLOR.a, VERTEX, SCREEN_UV, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);
	ALBEDO = vec3(0.0);
	ALPHA = texel.a * COLOR.a * fade;
}
