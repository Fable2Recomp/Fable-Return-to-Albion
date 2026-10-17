uniform float fx_soft_inv = 0.0;
uniform sampler2D fx_depth_tex : hint_depth_texture, filter_nearest;

/// The ease over squared distance to the scene behind, scaled by `inv` (1 / the soft distance); 0 = off.
float fx_soft_fade_by(float inv, float out_alpha, vec3 view_pos, vec2 screen_uv, mat4 inv_proj, mat4 inv_view) {
	if (inv <= 0.0) {
		return 1.0;
	}
	if (out_alpha <= 0.01) {
		return 0.0;
	}
	float depth = texture(fx_depth_tex, screen_uv).x;
	vec4 ndc = vec4(screen_uv * 2.0 - 1.0, depth, 1.0);
	vec4 scene_view = inv_proj * ndc;
	scene_view.xyz /= scene_view.w;
	vec3 scene_world = (inv_view * vec4(scene_view.xyz, 1.0)).xyz;
	vec3 frag_world = (inv_view * vec4(view_pos, 1.0)).xyz;
	vec3 d = frag_world - scene_world;
	float x = dot(d, d) * inv;
	if (x <= 0.5) {
		float t = clamp(2.0 * x, 0.0, 1.0);
		return 0.5 * t * t * t * t;
	}
	float t = clamp(2.0 * (1.0 - x), 0.0, 1.0);
	return 1.0 - 0.5 * t * t * t * t;
}

float fx_soft_fade(float out_alpha, vec3 view_pos, vec2 screen_uv, mat4 inv_proj, mat4 inv_view) {
	return fx_soft_fade_by(fx_soft_inv, out_alpha, view_pos, screen_uv, inv_proj, inv_view);
}
