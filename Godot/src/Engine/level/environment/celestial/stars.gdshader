shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

uniform float star_brightness = 0.0;
uniform float star_radius = 2500.0;
uniform float star_point_size = 1.5;
uniform float star_phase_rate = 2.5;
uniform float star_depth_bias = 0.000001;
uniform vec4 moon_occluder = vec4(0.0, 1.0, 0.0, 2.0);

varying float v_value;

void vertex() {
	vec4 hash = fract(VERTEX.x * vec4(732.051, 236.068, 645.751, 141.421));

	vec2 dir_xz = (hash.xy * 2.0) - 1.0;
	float up_y = (hash.z * 0.98) + 0.02;
	float len_sq = dot(dir_xz, dir_xz) + (up_y * up_y);
	float inv_len = inversesqrt(max(len_sq, 1e-6));
	// Godot world space is +Y up; keep all stars on the upper sky hemisphere above the horizon.
	vec3 direction = vec3(dir_xz.x * inv_len, up_y * inv_len, dir_xz.y * inv_len);

	vec4 phase = fract((star_phase_rate * TIME) * (hash.xywz + 1.0));
	float twinkle = max(max(phase.x, phase.y), max(phase.z, phase.w));

	vec3 view_pos = (VIEW_MATRIX * vec4(direction * star_radius, 0.0)).xyz;

	if (view_pos.z >= -0.01) {
		POSITION = vec4(0.0, 0.0, -10.0, 1.0);
		v_value = 0.0;
	} else {
		vec4 clip = PROJECTION_MATRIX * vec4(view_pos, 1.0);
		float safe_w = abs(clip.w) > 1e-5 ? clip.w : 1e-5;
		vec2 ndc = (clip.xy / safe_w) + (UV * (star_point_size / VIEWPORT_SIZE));

		float far_depth = step(0.5, -PROJECTION_MATRIX[2][2]);
		POSITION = vec4(ndc, mix(far_depth, 1.0 - far_depth, star_depth_bias), 1.0);

		float moon_clear = 1.0 - smoothstep(
			moon_occluder.w - 0.002,
			moon_occluder.w + 0.001,
			dot(direction, moon_occluder.xyz));
		v_value = (hash.w * hash.w) * twinkle * star_brightness * moon_clear;
	}
}

// 0127 pshader_sky_stars is three lines and has no texture, no constants and no transfer:
//
//     oC0.xyz = r0.xxx;      // the twinkle brightness the vertex shader computed
//     oC0.w   = 0.0;
//
// The brightness goes out raw. Note the guest writes ALPHA 0, which only works because the
// draw is additive — a src-alpha blend would erase it.
void fragment() {
	ALBEDO = vec3(v_value);
	ALPHA = 1.0;
}
