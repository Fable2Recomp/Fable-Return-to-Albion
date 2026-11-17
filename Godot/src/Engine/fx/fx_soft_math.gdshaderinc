#ifndef FX_SOFT_MATH_INCLUDED
#define FX_SOFT_MATH_INCLUDED

// Computes soft-particle fade against a sampled scene depth value.
// Clamps inv_proj * ndc `.w` away from zero so sky/unbound reverse-Z depth (0.0)
// cannot inject Inf/NaN into FP16 FX targets.
float fx_soft_fade_from_depth(
		float inv, float out_alpha, vec3 view_pos, vec2 screen_uv, float depth, mat4 inv_proj, mat4 inv_view) {
	if (inv <= 0.0) {
		return 1.0;
	}
	if (out_alpha <= 0.01) {
		return 0.0;
	}
	vec4 ndc = vec4(screen_uv * 2.0 - 1.0, depth, 1.0);
	vec4 scene_view = inv_proj * ndc;
	float safe_w = abs(scene_view.w) > 1e-6 ? scene_view.w : 1e-6;
	scene_view.xyz /= safe_w;
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

#endif
