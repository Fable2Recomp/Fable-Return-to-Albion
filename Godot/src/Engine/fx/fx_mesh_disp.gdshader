shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

uniform sampler2D albedo_tex : hint_default_white, filter_linear_mipmap;
global uniform sampler2D fx_scene_depth;
global uniform float fx_scene_depth_valid;
#include "res://src/Engine/core/views.gdshaderinc"

void fragment() {
	if (fx_scene_depth_valid > 0.5) {
		float scene_z = REFII_SCENE_DEPTH(SCREEN_UV).r;
		if (FRAGCOORD.z < scene_z - 0.00001) {
			discard;
		}
	}
	vec4 texel = texture(albedo_tex, UV);
	vec3 rgb = texel.rgb * texel.rgb * COLOR.rgb * COLOR.a;
	float py = texel.a * COLOR.a * COLOR.a;
	float a = max(sqrt(max(py, 0.0)), 0.001);
	ALBEDO = rgb / a;
	ALPHA = a;
}
