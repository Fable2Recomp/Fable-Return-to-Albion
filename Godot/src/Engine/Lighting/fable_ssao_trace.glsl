#[compute]
#version 450

// Dual-scale screen-space horizon GTAO pass (PreOpaque, after Forward+ depth prepass).
// Evaluates both tight contact creases (~0.22m: timber beams, fence bars, stair risers, grass roots,
// shoe/plinth ground contact) and medium architectural creases (~0.95m: alcoves, eaves, room corners)
// directly from the hardware depth buffer, including alpha-scissored depth_prepass_alpha surfaces.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D depth_tex;
layout(r8, set = 0, binding = 1) uniform restrict writeonly image2D raw_ao_image;

layout(set = 0, binding = 2, std140) uniform Params {
	mat4 inv_projection;
	mat4 projection;
	vec4 sizes;       // (trace_w, trace_h, depth_w, depth_h)
	vec4 inv_sizes;   // (1/trace_w, 1/trace_h, 1/depth_w, 1/depth_h)
	vec4 radii;       // (r_tight, r_macro, fade_start, fade_end)
	vec4 tuning;      // (num_dirs, num_steps, slope_bias, intensity)
} params;

const float PI = 3.14159265359;

vec3 reconstruct_view_pos(ivec2 depth_px, float raw_depth) {
	vec2 uv = (vec2(depth_px) + 0.5) * params.inv_sizes.zw;
	vec4 clip = vec4(uv * 2.0 - 1.0, raw_depth, 1.0);
	vec4 view = params.inv_projection * clip;
	float safe_w = abs(view.w) > 1e-6 ? view.w : (view.w >= 0.0 ? 1e-6 : -1e-6);
	return view.xyz / safe_w;
}

vec2 project_view_to_uv(vec3 view_pos) {
	vec4 clip = params.projection * vec4(view_pos, 1.0);
	float safe_w = abs(clip.w) > 1e-6 ? clip.w : (clip.w >= 0.0 ? 1e-6 : -1e-6);
	return (clip.xy / safe_w) * 0.5 + 0.5;
}

// Deterministic 4x4 Bayer + interleaved gradient hash so the 5x5 bilateral filter resolves cleanly with zero temporal ghosting.
vec2 spatial_noise(ivec2 px) {
	int bx = px.x & 3;
	int by = px.y & 3;
	int b0 = ((bx & 1) << 1) | (by & 1);
	int b1 = (((bx >> 1) & 1) << 1) | ((by >> 1) & 1);
	int bayer16 = (b0 << 2) | b1;
	float angle = (float(bayer16) + 0.5) * (1.0 / 16.0);
	float jitter = fract(52.9829189 * fract(0.06711056 * float(px.x) + 0.00583715 * float(px.y)));
	return vec2(angle, jitter);
}

void accumulate_direction_step(
	vec2 uv_c,
	vec2 step_uv,
	ivec2 depth_max,
	vec3 p_c,
	vec3 n_v,
	float inv_tight,
	float inv_macro,
	float slope_bias,
	inout float max_tight,
	inout float max_macro
) {
	vec2 tap_uv = uv_c + step_uv;
	if (tap_uv.x <= 0.0 || tap_uv.x >= 1.0 || tap_uv.y <= 0.0 || tap_uv.y >= 1.0) {
		return;
	}
	ivec2 tap_px = clamp(ivec2(tap_uv * params.sizes.zw), ivec2(0), depth_max);
	float tap_d = texelFetch(depth_tex, tap_px, 0).r;
	if (tap_d <= 1e-6) {
		return;
	}

	vec3 p_s = reconstruct_view_pos(tap_px, tap_d);
	vec3 delta = p_s - p_c;
	float dist_sq = dot(delta, delta);
	if (dist_sq <= 1e-7) {
		return;
	}

	float inv_dist = inversesqrt(dist_sq);
	float dist = dist_sq * inv_dist;
	float sin_h = dot(delta, n_v) * inv_dist - slope_bias;
	if (sin_h <= 0.0) {
		return;
	}

	// Thin-occluder thickness rejection: prevents foreground fence bars, characters, or foliage cards
	// from casting wide dark halos onto distant background walls while preserving full 90-deg crease AO.
	float front_jump = max(0.0, p_s.z - p_c.z);
	float allowed_thick = mix(0.28, 1.15, clamp(dist * inv_macro, 0.0, 1.0));
	float excess_thick = max(0.0, front_jump - allowed_thick) * 2.25;
	float thick_w = 1.0 / (1.0 + excess_thick * excess_thick);

	float falloff_tight = max(0.0, 1.0 - dist * inv_tight);
	float falloff_macro = max(0.0, 1.0 - dist * inv_macro);

	float h_tight = sin_h * falloff_tight * thick_w;
	float h_macro = sin_h * falloff_macro * sqrt(falloff_macro) * thick_w;

	max_tight = max(max_tight, h_tight);
	max_macro = max(max_macro, h_macro);
}

void main() {
	ivec2 tp = ivec2(gl_GlobalInvocationID.xy);
	ivec2 trace_size = ivec2(params.sizes.xy);
	if (tp.x >= trace_size.x || tp.y >= trace_size.y) {
		return;
	}

	ivec2 depth_size = ivec2(params.sizes.zw);
	ivec2 depth_max = depth_size - ivec2(1);
	vec2 uv_c = (vec2(tp) + 0.5) * params.inv_sizes.xy;
	ivec2 fp = clamp(ivec2(uv_c * params.sizes.zw), ivec2(0), depth_max);

	float d_c = texelFetch(depth_tex, fp, 0).r;
	if (d_c <= 1e-6) {
		imageStore(raw_ao_image, tp, vec4(1.0, 0.0, 0.0, 0.0));
		return;
	}

	vec3 p_c = reconstruct_view_pos(fp, d_c);
	float lin_z = -p_c.z;
	if (lin_z <= 0.02 || lin_z >= params.radii.w) {
		imageStore(raw_ao_image, tp, vec4(1.0, 0.0, 0.0, 0.0));
		return;
	}

	// Reconstruct crisp face normal using minimum-delta depth pairs so silhouettes and cutouts never smear normals.
	ivec2 fp_l = max(fp - ivec2(1, 0), ivec2(0));
	ivec2 fp_r = min(fp + ivec2(1, 0), depth_max);
	ivec2 fp_u = max(fp - ivec2(0, 1), ivec2(0));
	ivec2 fp_d = min(fp + ivec2(0, 1), depth_max);

	float d_l = texelFetch(depth_tex, fp_l, 0).r;
	float d_r = texelFetch(depth_tex, fp_r, 0).r;
	float d_u = texelFetch(depth_tex, fp_u, 0).r;
	float d_d = texelFetch(depth_tex, fp_d, 0).r;

	vec3 p_l = d_l > 1e-6 ? reconstruct_view_pos(fp_l, d_l) : p_c;
	vec3 p_r = d_r > 1e-6 ? reconstruct_view_pos(fp_r, d_r) : p_c;
	vec3 p_u = d_u > 1e-6 ? reconstruct_view_pos(fp_u, d_u) : p_c;
	vec3 p_d = d_d > 1e-6 ? reconstruct_view_pos(fp_d, d_d) : p_c;

	bool use_r = (d_r > 1e-6) && ((d_l <= 1e-6) || abs(p_r.z - p_c.z) < abs(p_c.z - p_l.z));
	bool use_d = (d_d > 1e-6) && ((d_u <= 1e-6) || abs(p_d.z - p_c.z) < abs(p_c.z - p_u.z));
	vec3 dp_dx = use_r ? (p_r - p_c) : (p_c - p_l);
	vec3 dp_dy = use_d ? (p_d - p_c) : (p_c - p_u);

	vec3 n_raw = cross(dp_dy, dp_dx);
	float n_len_sq = dot(n_raw, n_raw);
	vec3 view_dir = normalize(-p_c);
	vec3 n_v = n_len_sq > 1e-12 ? (n_raw * inversesqrt(n_len_sq)) : view_dir;
	if (dot(n_v, view_dir) < 0.0) {
		n_v = -n_v;
	}

	float r_tight = params.radii.x;
	float r_macro = params.radii.y;
	float inv_tight = 1.0 / max(r_tight, 0.05);
	float inv_macro = 1.0 / max(r_macro, 0.10);

	// Screen-space UV radius corresponding to r_macro at p_c, capped to keep cache locality high when right up against a wall.
	vec2 uv_edge_x = project_view_to_uv(p_c + vec3(r_macro, 0.0, 0.0)) - uv_c;
	vec2 uv_edge_y = project_view_to_uv(p_c + vec3(0.0, r_macro, 0.0)) - uv_c;
	vec2 max_radius_uv = min(vec2(abs(uv_edge_x.x), abs(uv_edge_y.y)), vec2(0.14, 0.14 * (params.sizes.z * params.inv_sizes.w)));
	vec2 min_step_uv = params.inv_sizes.zw * 1.5;
	if (max(max_radius_uv.x, max_radius_uv.y) <= max(min_step_uv.x, min_step_uv.y)) {
		imageStore(raw_ao_image, tp, vec4(1.0, 0.0, 0.0, 0.0));
		return;
	}

	int num_dirs = clamp(int(params.tuning.x + 0.5), 2, 4);
	int num_steps = clamp(int(params.tuning.y + 0.5), 3, 6);
	float slope_bias = params.tuning.z + min(0.04, lin_z * 0.0008);
	vec2 noise = spatial_noise(tp);

	float sum_tight = 0.0;
	float sum_macro = 0.0;
	float inv_steps = 1.0 / float(num_steps);

	for (int d = 0; d < num_dirs; ++d) {
		float phi = (PI / float(num_dirs)) * (float(d) + noise.x);
		vec2 dir = vec2(cos(phi), sin(phi));

		float pos_tight = 0.0;
		float pos_macro = 0.0;
		float neg_tight = 0.0;
		float neg_macro = 0.0;

		for (int s = 0; s < num_steps; ++s) {
			float frac_s = (float(s) + 0.25 + 0.70 * noise.y) * inv_steps;
			// Quadratic step distribution concentrates samples tightly into 90-degree beam/stair/fence joints while reaching r_macro.
			float t = mix(0.06, 1.0, frac_s * frac_s);
			vec2 step_uv = dir * max(max_radius_uv * t, min_step_uv * float(s + 1));

			accumulate_direction_step(uv_c, step_uv, depth_max, p_c, n_v, inv_tight, inv_macro, slope_bias, pos_tight, pos_macro);
			accumulate_direction_step(uv_c, -step_uv, depth_max, p_c, n_v, inv_tight, inv_macro, slope_bias, neg_tight, neg_macro);
		}

		sum_tight += 0.5 * (pos_tight + neg_tight);
		sum_macro += 0.5 * (pos_macro + neg_macro);
	}

	float inv_dirs = 1.0 / float(num_dirs);
	float occ_tight = clamp(sum_tight * inv_dirs * 1.15, 0.0, 0.85);
	float occ_macro = clamp(sum_macro * inv_dirs * 1.05, 0.0, 0.82);

	float vis_tight = 1.0 - occ_tight;
	float vis_macro = 1.0 - occ_macro;
	// Non-crushing horizon union between tight contact and architectural room creases.
	float ao = min(vis_tight, vis_macro) * mix(1.0, max(vis_tight, vis_macro), 0.28);
	ao = clamp(mix(1.0, ao, params.tuning.w), 0.08, 1.0);

	float dist_fade = 1.0 - smoothstep(params.radii.z, params.radii.w, lin_z);
	ao = mix(1.0, ao, dist_fade);

	imageStore(raw_ao_image, tp, vec4(ao, 0.0, 0.0, 0.0));
}
