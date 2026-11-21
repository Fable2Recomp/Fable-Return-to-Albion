#[compute]
#version 450

// Plane-aware cross-bilateral filter & full-resolution depth-guided upsampler for fable_ssao_tex.
// Smooths 4x4 spatial Bayer noise along angled walls/floors without blotching while preserving
// razor-sharp 1-pixel edges on iron fences, timber beams, stairs, characters, and grass blades.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D raw_ao_tex;
layout(set = 0, binding = 1) uniform sampler2D depth_tex;
layout(r8, set = 0, binding = 2) uniform restrict writeonly image2D out_ao_image;

layout(set = 0, binding = 3, std140) uniform BlurParams {
	mat4 inv_projection;
	vec4 sizes;       // (trace_w, trace_h, out_w, out_h)
	vec4 inv_sizes;   // (1/trace_w, 1/trace_h, 1/out_w, 1/out_h)
} params;

float linearize_depth(float raw_d) {
	// View-space Z/W from column 2 and column 3 of inv_projection (clip x=0, y=0).
	float vz = params.inv_projection[2].z * raw_d + params.inv_projection[3].z;
	float vw = params.inv_projection[2].w * raw_d + params.inv_projection[3].w;
	float safe_w = abs(vw) > 1e-6 ? vw : (vw >= 0.0 ? 1e-6 : -1e-6);
	return -(vz / safe_w);
}

float spatial_weight_1d(int d) {
	int ad = abs(d);
	return ad == 0 ? 1.0 : (ad == 1 ? 0.78 : 0.42);
}

void main() {
	ivec2 px = ivec2(gl_GlobalInvocationID.xy);
	ivec2 out_size = ivec2(params.sizes.zw);
	if (px.x >= out_size.x || px.y >= out_size.y) {
		return;
	}

	ivec2 out_max = out_size - ivec2(1);
	float d_c = texelFetch(depth_tex, px, 0).r;
	if (d_c <= 1e-6) {
		imageStore(out_ao_image, px, vec4(1.0, 0.0, 0.0, 0.0));
		return;
	}

	float z_c = linearize_depth(d_c);
	if (z_c <= 0.02) {
		imageStore(out_ao_image, px, vec4(1.0, 0.0, 0.0, 0.0));
		return;
	}

	// 2nd-order affine-planar slope (dz/dx, dz/dy in linear metres per output pixel) so grazing eaves and chamfers filter smoothly.
	float d_l = texelFetch(depth_tex, max(px - ivec2(1, 0), ivec2(0)), 0).r;
	float d_r = texelFetch(depth_tex, min(px + ivec2(1, 0), out_max), 0).r;
	float d_u = texelFetch(depth_tex, max(px - ivec2(0, 1), ivec2(0)), 0).r;
	float d_d = texelFetch(depth_tex, min(px + ivec2(0, 1), out_max), 0).r;
	float d_l2 = texelFetch(depth_tex, max(px - ivec2(2, 0), ivec2(0)), 0).r;
	float d_r2 = texelFetch(depth_tex, min(px + ivec2(2, 0), out_max), 0).r;
	float d_u2 = texelFetch(depth_tex, max(px - ivec2(0, 2), ivec2(0)), 0).r;
	float d_d2 = texelFetch(depth_tex, min(px + ivec2(0, 2), out_max), 0).r;

	float err_l = d_l > 1e-6 ? min(abs(d_c - d_l), d_l2 > 1e-6 ? abs((2.0 * d_l - d_l2) - d_c) : 1e9) : 1e9;
	float err_r = d_r > 1e-6 ? min(abs(d_r - d_c), d_r2 > 1e-6 ? abs((2.0 * d_r - d_r2) - d_c) : 1e9) : 1e9;
	float err_u = d_u > 1e-6 ? min(abs(d_c - d_u), d_u2 > 1e-6 ? abs((2.0 * d_u - d_u2) - d_c) : 1e9) : 1e9;
	float err_d = d_d > 1e-6 ? min(abs(d_d - d_c), d_d2 > 1e-6 ? abs((2.0 * d_d - d_d2) - d_c) : 1e9) : 1e9;

	float z_l = d_l > 1e-6 ? linearize_depth(d_l) : z_c;
	float z_r = d_r > 1e-6 ? linearize_depth(d_r) : z_c;
	float z_u = d_u > 1e-6 ? linearize_depth(d_u) : z_c;
	float z_d = d_d > 1e-6 ? linearize_depth(d_d) : z_c;

	float dz_r = z_r - z_c;
	float dz_l = z_c - z_l;
	float dz_d = z_d - z_c;
	float dz_u = z_c - z_u;
	float dz_dx = (d_r > 1e-6 && (d_l <= 1e-6 || err_r < err_l)) ? dz_r : (d_l > 1e-6 ? dz_l : 0.0);
	float dz_dy = (d_d > 1e-6 && (d_u <= 1e-6 || err_d < err_u)) ? dz_d : (d_u > 1e-6 ? dz_u : 0.0);

	ivec2 trace_size = ivec2(params.sizes.xy);
	ivec2 trace_max = trace_size - ivec2(1);
	vec2 uv = (vec2(px) + 0.5) * params.inv_sizes.zw;
	ivec2 tp_center = clamp(ivec2(uv * params.sizes.xy), ivec2(0), trace_max);

	float inv_plane_tol = 1.0 / max(0.025, z_c * 0.015);
	float sum_ao = 0.0;
	float sum_w = 0.0;
	float best_dz = 1e9;
	float nearest_ao = 1.0;

	for (int dy = -2; dy <= 2; ++dy) {
		int ty = clamp(tp_center.y + dy, 0, trace_max.y);
		float wy = spatial_weight_1d(dy);
		for (int dx = -2; dx <= 2; ++dx) {
			int tx = clamp(tp_center.x + dx, 0, trace_max.x);
			ivec2 tp_tap = ivec2(tx, ty);
			vec2 tap_uv = (vec2(tp_tap) + 0.5) * params.inv_sizes.xy;
			ivec2 fp_tap = clamp(ivec2(tap_uv * params.sizes.zw), ivec2(0), out_max);

			float d_tap = texelFetch(depth_tex, fp_tap, 0).r;
			if (d_tap <= 1e-6) {
				continue;
			}

			float z_tap = linearize_depth(d_tap);
			vec2 delta_px = vec2(fp_tap - px);
			float z_expected = z_c + dz_dx * delta_px.x + dz_dy * delta_px.y;
			float plane_err = abs(z_tap - z_expected) * inv_plane_tol;
			float w_depth = 1.0 / (1.0 + plane_err * plane_err * 6.0);
			float w = wy * spatial_weight_1d(dx) * w_depth;

			float ao_tap = texelFetch(raw_ao_tex, tp_tap, 0).r;
			sum_ao += ao_tap * w;
			sum_w += w;

			float abs_dz = abs(z_tap - z_c);
			if (abs_dz < best_dz) {
				best_dz = abs_dz;
				nearest_ao = ao_tap;
			}
		}
	}

	float filtered = sum_w > 1e-4 ? (sum_ao / sum_w) : nearest_ao;
	// Narrow 1..2px chamfers between half-res trace centers fall back to nearest depth-matched tap rather than flashing 1.0 white.
	float confidence = clamp(sum_w * 6.0, 0.0, 1.0);
	float final_ao = mix(nearest_ao, filtered, confidence);
	imageStore(out_ao_image, px, vec4(final_ao, 0.0, 0.0, 0.0));
}
