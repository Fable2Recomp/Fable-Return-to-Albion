shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never,
	unshaded, shadows_disabled, fog_disabled;

uniform sampler2D element_tex : hint_default_black, filter_linear, repeat_disable;
uniform vec3 element_dir = vec3(0.0, 1.0, 0.0);
uniform float element_distance = 1900.0;
uniform vec2 element_half = vec2(1.0, 1.0);
uniform vec4 element_colour = vec4(1.0, 1.0, 1.0, 1.0);
uniform float element_facing_power = 0.0;
uniform float element_depth_bias = 0.000001;
uniform float element_vis = 1.0;
uniform bool is_pbr = false;
uniform vec4 element_phase = vec4(0.0, 0.0, 1.0, 0.0);

// PREMULTIPLY BY THE TEXEL'S ALPHA — 1 for the moon's bloom, 0 for the sun's glare.
uniform float element_premultiply = 0.0;

varying vec3 v_colour;

void vertex() {
	vec3 dir_view = (VIEW_MATRIX * vec4(normalize(element_dir), 0.0)).xyz;
	float facing = -dir_view.z;

	if (facing <= 0.0) {
		POSITION = vec4(0.0, 0.0, -10.0, 1.0);
		v_colour = vec3(0.0);
	} else {
		vec3 view_pos = (dir_view * element_distance) +
		                vec3(VERTEX.x * element_half.x, VERTEX.y * element_half.y, 0.0);
		vec4 clip = PROJECTION_MATRIX *
		            vec4(view_pos.xy, min(view_pos.z, -0.01), 1.0);
		float safe_w = abs(clip.w) > 1e-6 ? clip.w : 1e-6;

		float far_depth = step(0.5, -PROJECTION_MATRIX[2][2]);
		POSITION = vec4(clip.xy / safe_w,
		                mix(far_depth, 1.0 - far_depth, element_depth_bias), 1.0);

		v_colour = element_colour.rgb * pow(facing, element_facing_power);
	}
}

// Additive in linear. Hardware depth testing ensures foreground static objects
// (trees, statues, buildings, terrain) occlude the glare with subpixel accuracy,
// eliminating all 25-tap discrete sample flicker and preventing the halo from
// bleeding onto foreground geometry.
float moon_arc_lobe(float u, float v, float vk, float xk, float w_lat, float spine, float inv_sig_u, float inv_sig_v) {
	float du = (u - (spine * xk)) * inv_sig_u;
	float dv = (v - vk) * inv_sig_v;
	return w_lat * exp(-((du * du) + (dv * dv)));
}

void fragment() {
	vec2 uv_dx = dFdx(UV);
	vec2 uv_dy = dFdy(UV);
	vec4 texel = textureGrad(element_tex, UV, uv_dx, uv_dy);

	// Match VERTEX.xy screen orientation (+X right, +Y up) so MoonGlare and Moon share exact tilt axes.
	vec2 screen_pos = vec2((UV.x - 0.5) * 2.0, (0.5 - UV.y) * 2.0);
	float r = length(screen_pos);
	// Smooth cubic Hermite radial falloff ensuring the halo reaches absolute zero
	// before the billboard edge, completely eliminating visible circular ring borders.
	float radial_falloff = 1.0 - smoothstep(0.35, 0.95, r);

	float phase_halo = 1.0;
	if (element_phase.w > 0.0) {
		float illum = clamp(0.5 * (1.0 + element_phase.z), 0.0, 1.0);
		vec2 q_moon = screen_pos / max(element_phase.w, 1e-4);
		float dir_xy_len = length(element_phase.xy);
		if (dir_xy_len <= 1e-4) {
			phase_halo = element_phase.z > 0.0 ? 1.0 : 0.0;
		} else {
			vec2 axis_u = element_phase.xy / dir_xy_len;
			vec2 axis_v = vec2(-axis_u.y, axis_u.x);
			float u = dot(q_moon, axis_u);
			float v = dot(q_moon, axis_v);
			float spine = 0.5 * (1.0 - element_phase.z);
			float half_w = max(0.5 * (1.0 + element_phase.z), 0.14);
			float inv_sig_u = 1.0 / (0.36 + (0.54 * half_w));
			const float inv_sig_v = 1.0 / 0.54;
			float arc_sum =
				moon_arc_lobe(u, v, -0.74, 0.6726, 0.452, spine, inv_sig_u, inv_sig_v) +
				moon_arc_lobe(u, v, -0.38, 0.9250, 0.856, spine, inv_sig_u, inv_sig_v) +
				moon_arc_lobe(u, v,  0.00, 1.0000, 1.000, spine, inv_sig_u, inv_sig_v) +
				moon_arc_lobe(u, v,  0.38, 0.9250, 0.856, spine, inv_sig_u, inv_sig_v) +
				moon_arc_lobe(u, v,  0.74, 0.6726, 0.452, spine, inv_sig_u, inv_sig_v);
			phase_halo = arc_sum * 0.42 * pow(illum, 0.55);
		}
	}

	float vis = clamp(element_vis, 0.0, 1.0);
	float alpha_mult = mix(1.0, texel.a, element_premultiply);
	vec3 tex_rgb = is_pbr ? texel.rgb : texel.rgb;
	ALBEDO = max(tex_rgb * alpha_mult * v_colour * (radial_falloff * vis * phase_halo), vec3(0.0));
	ALPHA = 1.0;
}
