shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, depth_test_disabled, unshaded, shadows_disabled, fog_disabled;

#include "res://src/Engine/fx/fx_billboard.gdshaderinc"

// ONE/ONE particle drawn into the additive effects layer (Engine_FxAdditiveLayer's half-res
// viewport), not the scene. The guest's bucket-5 pass keeps ZEnable with ZWrite off; this viewport
// has no depth of its own, so the captured scene depth is tested by hand, as the displacement layer
// does. Composition is 0210's: `oC0.rgb = vc.a * B.w * vc.rgb * tex.rgb * tex.rgb`, alpha ONE.
// No clamp here: the layer is clamped whole when it joins the scene, as the guest's A8R8G8B8
// target does.
uniform sampler2D fx_tex : filter_linear, repeat_enable;
uniform float fx_alpha_ref = 1.0;
uniform float fx_soft_inv = 0.0;
global uniform sampler2D fx_scene_depth;
global uniform float fx_scene_depth_valid;
#include "res://src/Engine/core/views.gdshaderinc"

float soft_fade(float out_alpha, vec3 view_pos, vec2 screen_uv, float scene_z, mat4 inv_proj, mat4 inv_view) {
	if (fx_soft_inv <= 0.0) {
		return 1.0;
	}
	if (out_alpha <= 0.01) {
		return 0.0;
	}
	vec4 ndc = vec4(screen_uv * 2.0 - 1.0, scene_z, 1.0);
	vec4 scene_view = inv_proj * ndc;
	scene_view.xyz /= scene_view.w;
	vec3 scene_world = (inv_view * vec4(scene_view.xyz, 1.0)).xyz;
	vec3 frag_world = (inv_view * vec4(view_pos, 1.0)).xyz;
	vec3 d = frag_world - scene_world;
	float x = dot(d, d) * fx_soft_inv;
	if (x <= 0.5) {
		float t = clamp(2.0 * x, 0.0, 1.0);
		return 0.5 * t * t * t * t;
	}
	float t = clamp(2.0 * (1.0 - x), 0.0, 1.0);
	return 1.0 - 0.5 * t * t * t * t;
}

void fragment() {
	float scene_z = 0.0;
	if (fx_scene_depth_valid > 0.5) {
		scene_z = REFII_SCENE_DEPTH(SCREEN_UV).r;
		// Reversed Z: a smaller value is farther. Behind the scene surface is discarded.
		if (FRAGCOORD.z < scene_z - 0.00001) {
			discard;
		}
	}
	vec4 texel = texture(fx_tex, UV);
	if (texel.a * COLOR.a * 255.0 <= fx_alpha_ref) {
		discard;
	}
	float fade = fx_scene_depth_valid > 0.5
		? soft_fade(texel.a * COLOR.a, VERTEX, SCREEN_UV, scene_z, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX)
		: 1.0;
	ALBEDO = COLOR.a * COLOR.rgb * texel.rgb * texel.rgb * fade;
	ALPHA = 1.0;
}
