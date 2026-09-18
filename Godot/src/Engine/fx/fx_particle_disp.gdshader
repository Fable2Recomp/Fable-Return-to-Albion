shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

#include "res://src/Engine/fx/fx_billboard.gdshaderinc"

uniform sampler2D fx_tex : filter_linear, repeat_enable;
global uniform sampler2D fx_scene_depth;
global uniform float fx_scene_depth_valid;

void fragment() {
	if (fx_scene_depth_valid > 0.5) {
		float scene_z = texture(fx_scene_depth, SCREEN_UV).r;
		if (FRAGCOORD.z < scene_z - 0.00001) {
			discard;
		}
	}
	vec4 texel = texture(fx_tex, UV);
	vec3 rgb = texel.rgb * texel.rgb * COLOR.rgb;
	float py = texel.a * COLOR.a;
	float a = max(sqrt(max(py, 0.0)), 0.001);
	ALBEDO = rgb / a;
	ALPHA = a;
}
