shader_type spatial;
render_mode blend_sub, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

#include "res://src/Engine/fx/fx_billboard.gdshaderinc"

uniform sampler2D fx_tex : filter_linear, repeat_enable;
uniform float fx_alpha_ref = 1.0;
#include "res://src/Engine/fx/fx_soft.gdshaderinc"
uniform float fx_exposure = 3.3333333;
global uniform sampler2D fable_exposure_tex;
global uniform float fable_exposure_valid;
uniform bool is_pbr = false;

void fragment() {
	vec4 texel = texture(fx_tex, UV);
	if (texel.a * COLOR.a * 255.0 <= fx_alpha_ref) {
		discard;
	}
	float fade = fx_soft_fade(texel.a * COLOR.a, VERTEX, SCREEN_UV, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);
	vec3 tex_rgb = is_pbr ? texel.rgb : (texel.rgb * texel.rgb);
	vec3 rgb = COLOR.a * COLOR.rgb * tex_rgb;
	// NO UPPER CLAMP IN LINEAR HDR — scene-referred HDR values are compressed only by the
	// final post-processing tone curve; only negative values are guarded against.
	if (fable_exposure_valid <= 0.5) {
		rgb *= fx_exposure;
	}
	rgb = max(rgb, vec3(0.0));
	ALBEDO = rgb * fade;
	ALPHA = 1.0;
}
