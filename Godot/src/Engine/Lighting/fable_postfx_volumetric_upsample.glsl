#[compute]
#version 450

// Full-resolution depth-aware cross-bilateral upsample for volumetric light shafts (Engine_PostFxEffect).
//
// Filters the half-resolution RGBA16F shaft buffer against full-resolution hardware depth so
// wall edges, window mullions, cave pillars, and character silhouettes remain razor-sharp,
// then injects unbounded linear physical radiance directly into `colour` before bloom and tonemapping.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict image2D colour_img;
layout(set = 0, binding = 1) uniform sampler2D depth_tex;
layout(set = 0, binding = 2) uniform sampler2D shaft_half_tex;
layout(set = 0, binding = 3) uniform sampler2D depth_half_tex;

layout(push_constant, std430) uniform Push {
	ivec2 full_size;
	ivec2 half_size;
	vec4 depth_terms;
	vec4 cam_proj_scales; // x: inv_px, y: inv_py, z: max_ray_dist, w: unused
} push;

float linear_depth(float raw_z) {
	float vz = push.depth_terms.x * raw_z + push.depth_terms.y;
	float vw = push.depth_terms.z * raw_z + push.depth_terms.w;
	float sw = abs(vw) > 1e-6 ? vw : 1e-6;
	float d = -vz / sw;
	return (d > 0.0 && d < 100000.0) ? d : push.cam_proj_scales.z;
}

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= push.full_size.x || coord.y >= push.full_size.y) {
		return;
	}

	vec2 uv = (vec2(coord) + 0.5) / vec2(push.full_size);
	float raw_z = texelFetch(depth_tex, coord, 0).r;
	float ndc_x = (2.0 * uv.x - 1.0) * push.cam_proj_scales.x;
	float ndc_y = (1.0 - 2.0 * uv.y) * push.cam_proj_scales.y;
	float ray_len = sqrt(1.0 + ndc_x * ndc_x + ndc_y * ndc_y);
	float t_full = min(linear_depth(raw_z) * ray_len, push.cam_proj_scales.z);

	vec2 half_coord_f = uv * vec2(push.half_size) - 0.5;
	ivec2 base_c = ivec2(floor(half_coord_f));
	vec2 f = fract(half_coord_f);
	ivec2 max_c = push.half_size - ivec2(1);

	float inv_depth_sigma = 1.0 / max(0.20, t_full * 0.06);

	vec3 sum_scatter = vec3(0.0);
	float sum_weight = 0.0;
	vec3 fallback_scatter = vec3(0.0);
	float fallback_weight = 0.0;

	for (int dy = 0; dy <= 1; dy++) {
		float wy = dy == 0 ? (1.0 - f.y) : f.y;
		for (int dx = 0; dx <= 1; dx++) {
			float wx = dx == 0 ? (1.0 - f.x) : f.x;
			float w_spatial = max(wx * wy, 1e-4);

			ivec2 hc = clamp(base_c + ivec2(dx, dy), ivec2(0), max_c);
			vec3 s = texelFetch(shaft_half_tex, hc, 0).rgb;
			float t_half = texelFetch(depth_half_tex, hc, 0).r;

			float w_depth = exp(-abs(t_half - t_full) * inv_depth_sigma);
			float w = w_spatial * w_depth;

			sum_scatter += s * w;
			sum_weight += w;
			fallback_scatter += s * w_spatial;
			fallback_weight += w_spatial;
		}
	}

	vec3 shaft = sum_weight > 1e-4
		? (sum_scatter / sum_weight)
		: (fallback_scatter / max(fallback_weight, 1e-4));
	shaft = max(shaft, vec3(0.0));

	if (max(shaft.r, max(shaft.g, shaft.b)) > 1e-6) {
		vec4 scene = imageLoad(colour_img, coord);
		scene.rgb += shaft;
		imageStore(colour_img, coord, scene);
	}
}
