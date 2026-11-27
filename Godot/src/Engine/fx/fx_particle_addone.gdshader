shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, depth_test_disabled, unshaded, shadows_disabled, fog_disabled;

#include "res://src/Engine/fx/fx_billboard.gdshaderinc"

// ONE/ONE particle drawn into the additive effects layer (Engine_FxAdditiveLayer's half-res
// viewport), not the scene. The guest's bucket-5 pass keeps ZEnable with ZWrite off; this viewport
// has no depth of its own, so the captured scene depth is tested by hand, as the displacement layer
// does. Composition is 0210's: `oC0.rgb = vc.a * B.w * vc.rgb * tex.rgb * tex.rgb`, alpha ONE.
// No clamp here: the layer is clamped whole when it joins the scene, as the guest's A8R8G8B8
// target does.
uniform sampler2D fx_tex : hint_default_white, filter_linear, repeat_disable;
uniform float fx_alpha_ref = 1.0;
uniform float fx_soft_inv = 0.0;
global uniform sampler2D fx_scene_depth;
global uniform float fx_scene_depth_valid;
global uniform float fable_emission_boost = 1.0;
uniform bool is_pbr = false;
#include "res://src/Engine/core/views.gdshaderinc"
#include "res://src/Engine/fx/fx_soft_math.gdshaderinc"

void fragment() {
	// Zero-discard `depth_draw_never` blend path: evaluate alpha-ref and depth-cull first, keep
	// `soft_fade`, distance fade, and boost math inside `if (!should_discard)`, and emit `ALPHA = 0.0`
	// when culled so `blend_add` leaves the additive target untouched.
	vec2 uv_dx = dFdx(UV);
	vec2 uv_dy = dFdy(UV);
	vec4 texel = textureGrad(fx_tex, UV, uv_dx, uv_dy);
	bool should_discard = (texel.a * COLOR.a * 255.0 <= fx_alpha_ref);
	float scene_z = 0.0;
	if (!should_discard && fx_scene_depth_valid > 0.5) {
		scene_z = REFII_SCENE_DEPTH(SCREEN_UV).r;
		// Reversed Z: a smaller value is farther. Behind the scene surface is culled.
		if (FRAGCOORD.z < scene_z - 0.00001) {
			should_discard = true;
		}
	}

	ALBEDO = vec3(0.0);
	ALPHA = 0.0;

	if (!should_discard) {
		float fade = fx_scene_depth_valid > 0.5
			? fx_soft_fade_from_depth(fx_soft_inv, texel.a * COLOR.a, VERTEX, SCREEN_UV, scene_z, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX)
			: 1.0;

		// Attenuate extreme unshaded HDR radiance on distant additive layer particles
		float dist = length(VERTEX);
		float dist_fade = 1.0;
		if (dist > 12.0) {
			float t = clamp((dist - 12.0) / 33.0, 0.0, 1.0);
			dist_fade = mix(1.0, 0.35, t * t * (3.0 - 2.0 * t));
		}

		vec3 tex_rgb = is_pbr ? texel.rgb : (texel.rgb * texel.rgb * mix(1.0, max(fable_emission_boost, 1.0), dist_fade));
		ALBEDO = max(COLOR.a * COLOR.rgb * tex_rgb * (fade * dist_fade), vec3(0.0));
		ALPHA = 1.0;
	}
}
