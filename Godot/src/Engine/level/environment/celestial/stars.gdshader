shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

uniform float star_brightness = 0.0;
uniform float star_radius = 2500.0;
uniform float star_point_size = 1.5;
uniform float star_phase_rate = 2.5;
uniform float star_depth_bias = 0.000001;

varying float v_value;

void vertex() {
	vec4 hash = fract(VERTEX.x * vec4(732.051, 236.068, 645.751, 141.421));

	vec2 dir_xy = (hash.xy * 2.0) - 1.0;
	float len_sq = dot(dir_xy, dir_xy) + (hash.z * hash.z);
	float inv_len = inversesqrt(abs(len_sq));
	vec3 direction = vec3(dir_xy * inv_len, hash.z * inv_len);

	vec4 phase = fract((star_phase_rate * TIME) * (hash.xywz + 1.0));
	float twinkle = max(max(phase.x, phase.y), max(phase.z, phase.w));

	vec3 view_pos = (VIEW_MATRIX * vec4(direction * star_radius, 0.0)).xyz;

	if (view_pos.z >= -0.01) {
		POSITION = vec4(0.0, 0.0, -10.0, 1.0);
		v_value = 0.0;
	} else {
		vec4 clip = PROJECTION_MATRIX * vec4(view_pos, 1.0);
		vec2 ndc = (clip.xy / clip.w) + (UV * (star_point_size / VIEWPORT_SIZE));

		float far_depth = step(0.5, -PROJECTION_MATRIX[2][2]);
		POSITION = vec4(ndc, mix(far_depth, 1.0 - far_depth, star_depth_bias), 1.0);

		v_value = (hash.w * hash.w) * twinkle * star_brightness;
	}
}

void fragment() {
	ALBEDO = vec3(v_value);
	ALPHA = 1.0;
}
