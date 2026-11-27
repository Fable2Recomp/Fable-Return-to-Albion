shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

#include "res://src/Engine/fx/fx_billboard.gdshaderinc"

uniform sampler2D fx_tex : hint_default_white, filter_linear, repeat_disable;
global uniform sampler2D fx_scene_depth;
global uniform float fx_scene_depth_valid;
#include "res://src/Engine/core/views.gdshaderinc"

void fragment() {
	// Zero-discard `depth_draw_never` blend path: output `ALPHA = 0.0` when occluded so
	// hardware Early-Z remains active without `discard;`.
	vec2 uv_dx = dFdx(UV);
	vec2 uv_dy = dFdy(UV);
	vec4 texel = textureGrad(fx_tex, UV, uv_dx, uv_dy);
	bool should_discard = false;
	if (fx_scene_depth_valid > 0.5) {
		float scene_z = REFII_SCENE_DEPTH(SCREEN_UV).r;
		if (FRAGCOORD.z < scene_z - 0.00001) {
			should_discard = true;
		}
	}
	ALBEDO = vec3(0.0);
	ALPHA = 0.0;
	if (!should_discard) {
		vec3 rgb = texel.rgb * texel.rgb * COLOR.rgb;
		float py = texel.a * COLOR.a;
		float a = max(sqrt(max(py, 0.0)), 0.001);
		ALBEDO = rgb / a;
		ALPHA = a;
	}
}
