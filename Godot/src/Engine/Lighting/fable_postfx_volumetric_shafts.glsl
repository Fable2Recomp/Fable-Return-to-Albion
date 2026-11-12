#[compute]
#version 450

// Scene-referred RGBA16F volumetric light-shaft raymarch pass (Engine_PostFxEffect).
//
// Evaluates dynamic omni/spot/synthetic-flame volumetric scattering at half resolution with:
//   1. Conservative 2x2 hardware depth stopping (`t_scene`) so solid walls never bleed indoor light.
//   2. 8x8 workgroup tile light culling (instant early-out when 0 lights intersect the tile).
//   3. Unified 16-step IGN-jittered raymarch across the union light chord [t_enter, t_exit].
//   4. Hybrid occlusion: 512x512 octahedral static shadow atlas (`fable_static_shadow_atlas`
//      Texture2DArray) * analytic moving character capsules (`V = V_static * V_dynamic`).
//   5. Exact `Engine_ModernMist` 2-octave procedural gradient noise inside ground mist volumes.
//   6. Half-res RGBA16F temporal reprojection with depth-disocclusion & variance clamping.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D depth_tex;
layout(set = 0, binding = 1) uniform sampler2DArray shadow_atlas_tex;
layout(set = 0, binding = 2) uniform sampler2D prev_shaft_tex;
layout(set = 0, binding = 3) uniform sampler2D prev_half_depth_tex;
layout(rgba16f, set = 0, binding = 4) uniform restrict writeonly image2D curr_shaft_img;
layout(r16f, set = 0, binding = 5) uniform restrict writeonly image2D curr_half_depth_img;

layout(set = 0, binding = 6, std430) restrict readonly buffer ShaftBuffer {
	vec4 entries[];
} buf;

layout(push_constant, std430) uniform Push {
	ivec2 half_size;
	ivec2 full_size;
	int light_count;
	int mist_count;
	int capsule_count;
	int frame_index;
	float shaft_scale;
	float pad0;
} push;

const int BASE_LIGHTS = 10;
const int LIGHT_STRIDE = 5;
const int BASE_MISTS = 330;
const int MIST_STRIDE = 4;
const int BASE_CAPSULES = 394;
const int CAPSULE_STRIDE = 2;

const int RAY_STEPS = 8;
const float MAX_RAY_DIST = 80.0;
const float INV_FOUR_PI = 0.07957747154;

shared uint s_tile_max_dist_mm;
shared uint s_tile_light_mask_lo;
shared uint s_tile_light_mask_hi;
shared uint s_tile_mist_mask;

float linear_depth(float raw_z, vec4 terms) {
	float vz = terms.x * raw_z + terms.y;
	float vw = terms.z * raw_z + terms.w;
	float sw = abs(vw) > 1e-6 ? vw : 1e-6;
	float d = -vz / sw;
	return (d > 0.0 && d < 100000.0) ? d : MAX_RAY_DIST;
}

vec2 octahedral_uv(vec3 dir) {
	vec3 n = dir / max(abs(dir.x) + abs(dir.y) + abs(dir.z), 1e-6);
	vec2 oct = n.z >= 0.0
		? n.xy
		: (1.0 - abs(n.yx)) * vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
	return oct * 0.5 + 0.5;
}

float henyey_greenstein(float cos_theta, float g) {
	float g2 = g * g;
	float denom = max(1.0 + g2 - 2.0 * g * cos_theta, 1e-4);
	float hg = (1.0 - g2) / (denom * sqrt(denom));
	// Dual-lobe blend with isotropic scattering (normalized over 4*PI) so both side-viewed
	// window/pillar shafts and forward-viewed god-rays read clearly in scene-referred HDR.
	return INV_FOUR_PI * mix(1.0, hg, 0.64);
}

vec2 mist_gradient(vec2 cell) {
	float a = fract(sin(dot(cell, vec2(127.1, 311.7))) * 43758.5453) * 6.2831853;
	return vec2(cos(a), sin(a));
}

float mist_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	float a = dot(mist_gradient(i), f);
	float b = dot(mist_gradient(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0));
	float c = dot(mist_gradient(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0));
	float d = dot(mist_gradient(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0));
	return 1.41421356 * mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

void eval_local_medium(
		vec3 world_pos,
		float dist_from_cam,
		float fog_start,
		vec3 base_scattering,
		vec3 tint_dir,
		float base_extinction,
		uint mist_mask,
		out vec3 sigma_s,
		out float sigma_t) {
	float atmos_ramp = clamp((dist_from_cam - fog_start) * 0.25, 0.0, 1.0);
	// Always retain a local air scattering floor inside fog_start and in low-fog outdoor/indoor
	// regions so close torches, lanterns, and braziers form crisp volumetric shafts without requiring
	// thick background world fog.
	float weight = mix(0.65, 1.0, atmos_ramp);
	float local_dist_fade = 1.0 - smoothstep(20.0, 35.0, dist_from_cam);
	vec3 local_floor = tint_dir * (0.0042 * mix(0.80, 1.0, atmos_ramp) * local_dist_fade);
	sigma_s = max(base_scattering * weight * 1.65, local_floor);
	sigma_t = max(base_extinction * weight, 0.0022 * mix(0.75, 1.0, atmos_ramp) * local_dist_fade);

	while (mist_mask != 0u) {
		int m = findLSB(mist_mask);
		mist_mask &= (mist_mask - 1u);
		int idx = BASE_MISTS + m * MIST_STRIDE;
		vec4 bmin = buf.entries[idx];
		vec4 bmax = buf.entries[idx + 1];
		if (any(lessThan(world_pos, bmin.xyz)) || any(greaterThan(world_pos, bmax.xyz))) {
			continue;
		}

		vec3 albedo = buf.entries[idx + 2].xyz;
		vec4 scale_off = buf.entries[idx + 3];
		vec2 guest_xz = vec2(-world_pos.x, world_pos.z);
		vec2 p = guest_xz * scale_off.xy + scale_off.zw;
		float n = 0.5 + mist_noise(p) / 3.0 + mist_noise(p * 2.0 + 17.0) / 6.0;
		float flatten = bmax.w;
		float texel = flatten + (1.0 - flatten) * clamp(n, 0.0, 1.0);
		// Smooth vertical & edge feather inside the mist box.
		float h_frac = clamp((bmax.y - world_pos.y) / max((bmax.y - bmin.y) * 0.25, 0.25), 0.0, 1.0);
		float ext = bmin.w * texel * h_frac;
		sigma_s += albedo * (ext * 0.45);
		sigma_t += ext * 0.35;
	}
}

float segment_segment_dist(vec3 p1, vec3 d1, vec3 p2, vec3 q2) {
	vec3 d2 = q2 - p2;
	vec3 r = p1 - p2;
	float a = dot(d1, d1);
	float e = dot(d2, d2);
	float f = dot(d2, r);
	float s = 0.0;
	float t = 0.0;

	if (a <= 1e-6 && e <= 1e-6) {
		return length(r);
	}
	if (a <= 1e-6) {
		t = clamp(f / e, 0.0, 1.0);
	} else {
		float c = dot(d1, r);
		if (e <= 1e-6) {
			s = clamp(-c / a, 0.0, 1.0);
		} else {
			float b = dot(d1, d2);
			float denom = a * e - b * b;
			if (denom != 0.0) {
				s = clamp((b * f - c * e) / denom, 0.0, 1.0);
			}
			t = (b * s + f) / e;
			if (t < 0.0) {
				t = 0.0;
				s = clamp(-c / a, 0.0, 1.0);
			} else if (t > 1.0) {
				t = 1.0;
				s = clamp((b - c) / a, 0.0, 1.0);
			}
		}
	}
	// Avoid self-occluding right at the light origin (s < 0.05).
	if (s < 0.05) {
		return 1e5;
	}
	vec3 closest_diff = r + d1 * s - d2 * t;
	return length(closest_diff);
}

float eval_dynamic_capsules(vec3 light_pos, vec3 light_to_sample, uint mask) {
	float vis = 1.0;
	while (mask != 0u) {
		int c = findLSB(mask);
		mask &= (mask - 1u);
		int idx = BASE_CAPSULES + c * CAPSULE_STRIDE;
		vec4 cap_a = buf.entries[idx];
		vec4 cap_b = buf.entries[idx + 1];
		if (dot(light_to_sample, cap_a.xyz - light_pos) < 0.0 &&
		    dot(light_to_sample, cap_b.xyz - light_pos) < 0.0) {
			continue;
		}
		float dist = segment_segment_dist(light_pos, light_to_sample, cap_a.xyz, cap_b.xyz);
		vis *= smoothstep(cap_a.w * 0.65, cap_a.w + cap_b.w, dist);
		if (vis <= 1e-3) {
			return 0.0;
		}
	}
	return vis;
}

float sample_static_shadow(
		vec3 light_to_world,
		float d,
		vec4 atlas_rect,
		float node_range,
		float indoor_from_outside,
		float inv_atlas_size) {
	if (atlas_rect.z <= 0.0 || d <= 1e-4) {
		return indoor_from_outside > 0.5 ? 0.0 : 1.0;
	}

	float d_receiver = indoor_from_outside > 0.5
		? (d + 0.12)
		: (d - max(0.04, d * 0.02));

	vec2 tile_uv = octahedral_uv(light_to_world);
	// Clamp slightly inside the octahedral tile border to prevent adjacent-tile bleed.
	float border = 1.5 * inv_atlas_size;
	vec2 uv = atlas_rect.xy + clamp(tile_uv * atlas_rect.z, vec2(border), vec2(max(atlas_rect.z - border, border)));

	float d0 = textureLod(shadow_atlas_tex, vec3(uv, atlas_rect.w), 0.0).r;
	float transition = max(0.08, d * 0.025);
	float vis = smoothstep(d_receiver - transition, d_receiver, d0);
	if (indoor_from_outside > 0.5) {
		float wall_cutoff = max(node_range * 0.60, 3.2);
		vis *= smoothstep(wall_cutoff, wall_cutoff + 1.2, d0);
	}
	return vis;
}

void main() {
	uint local_idx = gl_LocalInvocationIndex;
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	bool in_bounds = coord.x < push.half_size.x && coord.y < push.half_size.y;

	if (local_idx == 0u) {
		s_tile_max_dist_mm = 0u;
		s_tile_light_mask_lo = 0u;
		s_tile_light_mask_hi = 0u;
		s_tile_mist_mask = 0u;
	}
	barrier();

	vec4 cam_origin_fog = buf.entries[0];
	vec4 cam_fwd_px = buf.entries[1];
	vec4 cam_right_py = buf.entries[2];
	vec4 cam_up_ext = buf.entries[3];
	vec4 med_scat_aniso = buf.entries[4];
	vec4 depth_terms = buf.entries[9];

	vec3 cam_origin = cam_origin_fog.xyz;
	vec3 cam_fwd = cam_fwd_px.xyz;
	vec3 cam_right = cam_right_py.xyz;
	vec3 cam_up = cam_up_ext.xyz;
	float px = max(cam_fwd_px.w, 1e-5);
	float py = max(cam_right_py.w, 1e-5);

	vec2 uv = (vec2(min(coord, push.half_size - ivec2(1))) + 0.5) / vec2(push.half_size);
	float ndc_x = (2.0 * uv.x - 1.0) / px;
	float ndc_y = (1.0 - 2.0 * uv.y) / py;
	vec3 ray_unnorm = cam_fwd + ndc_x * cam_right + ndc_y * cam_up;
	float ray_len = length(ray_unnorm);
	vec3 ray_dir = ray_unnorm / max(ray_len, 1e-6);

	// Sample 2x2 full-res depth footprint and take maximum distance so foreground silhouette
	// edges never truncate background shafts at reduced resolution.
	float t_scene = MAX_RAY_DIST;
	if (in_bounds) {
		ivec2 scale_xy = max(push.full_size / max(push.half_size, ivec2(1)), ivec2(1));
		ivec2 step_xy = max(scale_xy >> 1, ivec2(1));
		ivec2 full_max = push.full_size - ivec2(1);
		ivec2 full_base = min(coord * scale_xy, full_max);
		float z00 = texelFetch(depth_tex, full_base, 0).r;
		float z10 = texelFetch(depth_tex, min(full_base + ivec2(step_xy.x, 0), full_max), 0).r;
		float z01 = texelFetch(depth_tex, min(full_base + ivec2(0, step_xy.y), full_max), 0).r;
		float z11 = texelFetch(depth_tex, min(full_base + step_xy, full_max), 0).r;

		float d_max = max(
			max(linear_depth(z00, depth_terms), linear_depth(z10, depth_terms)),
			max(linear_depth(z01, depth_terms), linear_depth(z11, depth_terms)));
		t_scene = min(d_max * ray_len, MAX_RAY_DIST);
		imageStore(curr_half_depth_img, coord, vec4(t_scene, 0.0, 0.0, 0.0));
		atomicMax(s_tile_max_dist_mm, uint(t_scene * 1000.0));
	}
	barrier();

	float tile_max_t = float(s_tile_max_dist_mm) * 0.001;

	// Compute tile center ray and angular cone radius for fast cooperative light culling.
	vec2 tile_center_uv = (vec2(gl_WorkGroupID.xy * 8u + 4u)) / vec2(push.half_size);
	vec2 tile_half_uv = vec2(4.5) / vec2(push.half_size);
	vec3 tile_unnorm = cam_fwd
		+ ((2.0 * tile_center_uv.x - 1.0) / px) * cam_right
		+ ((1.0 - 2.0 * tile_center_uv.y) / py) * cam_up;
	vec3 tile_dir = normalize(tile_unnorm);
	float tile_tan = length(vec2(2.0 * tile_half_uv.x / px, 2.0 * tile_half_uv.y / py));

	int total_mists = min(push.mist_count, 16);
	if (int(local_idx) < total_mists && tile_max_t > 0.05) {
		int m_idx = BASE_MISTS + int(local_idx) * MIST_STRIDE;
		vec3 bmin = buf.entries[m_idx].xyz;
		vec3 bmax = buf.entries[m_idx + 1].xyz;
		vec3 clamped_cam = clamp(cam_origin, bmin, bmax);
		if (dot(cam_origin - clamped_cam, cam_origin - clamped_cam) <= tile_max_t * tile_max_t) {
			atomicOr(s_tile_mist_mask, 1u << local_idx);
		}
	}

	int total_lights = min(push.light_count, 64);
	if (int(local_idx) < total_lights && tile_max_t > 0.05) {
		int l_idx = int(local_idx);
		int base = BASE_LIGHTS + l_idx * LIGHT_STRIDE;
		vec4 pos_range = buf.entries[base];
		vec3 to_light = pos_range.xyz - cam_origin;
		float r = pos_range.w;
		float proj_t = clamp(dot(to_light, tile_dir), 0.0, tile_max_t);
		vec3 closest_pt = tile_dir * proj_t;
		float cone_rad = (proj_t + r) * tile_tan + r;
		if (dot(to_light - closest_pt, to_light - closest_pt) <= cone_rad * cone_rad) {
			if (l_idx < 32) {
				atomicOr(s_tile_light_mask_lo, 1u << uint(l_idx));
			} else {
				atomicOr(s_tile_light_mask_hi, 1u << uint(l_idx - 32));
			}
		}
	}
	barrier();

	if (!in_bounds) {
		return;
	}

	uvec2 tile_light_mask = uvec2(s_tile_light_mask_lo, s_tile_light_mask_hi);
	if ((tile_light_mask.x == 0u && tile_light_mask.y == 0u) || t_scene <= 0.05) {
		imageStore(curr_shaft_img, coord, vec4(0.0));
		return;
	}

	// Intersect pixel ray with tile's culled light spheres in deterministic distance-sorted order.
	float t_enter = t_scene;
	float t_exit = 0.0;
	uvec2 ray_light_mask = uvec2(0u);
	int tile_words = tile_light_mask.y != 0u ? 2 : 1;

	for (int word = 0; word < tile_words; word++) {
		uint m = word == 0 ? tile_light_mask.x : tile_light_mask.y;
		while (m != 0u) {
			int bit = findLSB(m);
			m &= (m - 1u);
			int li = word * 32 + bit;
			int base = BASE_LIGHTS + li * LIGHT_STRIDE;

			vec4 pos_range = buf.entries[base];
			vec3 oc = pos_range.xyz - cam_origin;
			float r = pos_range.w;
			float b = dot(oc, ray_dir);
			float c = dot(oc, oc) - r * r;
			float disc = b * b - c;
			if (disc <= 0.0) {
				continue;
			}
			float sq = sqrt(disc);
			float t0 = max(b - sq, 0.0);
			float t1 = min(b + sq, t_scene);
			if (t1 <= t0) {
				continue;
			}

			float spot_cos = buf.entries[base + 1].w;
			if (spot_cos >= 0.0) {
				vec3 spot_dir = buf.entries[base + 3].xyz;
				if (dot(ray_dir * t0 - oc, spot_dir) <= 0.0 && dot(ray_dir * t1 - oc, spot_dir) <= 0.0) {
					continue;
				}
			}

			t_enter = min(t_enter, t0);
			t_exit = max(t_exit, t1);
			if (word == 0) {
				ray_light_mask.x |= (1u << uint(bit));
			} else {
				ray_light_mask.y |= (1u << uint(bit));
			}
		}
	}

	if (t_exit <= t_enter) {
		imageStore(curr_shaft_img, coord, vec4(0.0));
		return;
	}

	float span = t_exit - t_enter;
	float dt = span / float(RAY_STEPS);

	// Temporally animated interleaved gradient noise.
	float ign = fract(52.9829189 * fract(dot(vec2(coord), vec2(0.06711056, 0.00583715))));
	float jitter = fract(ign + float(push.frame_index & 63) * 0.6180339887);

	float fog_start = cam_origin_fog.w;
	vec3 base_scattering = med_scat_aniso.xyz;
	vec3 tint_dir = base_scattering / max(max(base_scattering.r, max(base_scattering.g, base_scattering.b)), 1e-5);
	float anisotropy = med_scat_aniso.w;
	float base_extinction = cam_up_ext.w;
	float inv_atlas_size = buf.entries[8].w;
	vec3 view_dir = -ray_dir;
	uint tile_mist_mask = s_tile_mist_mask;
	int word_count = ray_light_mask.y != 0u ? 2 : 1;

	// Beer-Lambert transmittance from camera to t_enter.
	float enter_fog_dist = max(t_enter - fog_start, 0.0);
	float transmittance = exp(-base_extinction * enter_fog_dist);
	vec3 accum_scatter = vec3(0.0);
	float scatter_weight_sum = 0.0;
	float scatter_t_sum = 0.0;
	bool any_dynamic_caster = false;

	for (int s = 0; s < RAY_STEPS; s++) {
		float t = t_enter + (float(s) + jitter) * dt;
		vec3 sample_pos = cam_origin + ray_dir * t;

		vec3 sigma_s;
		float sigma_t;
		eval_local_medium(sample_pos, t, fog_start, base_scattering, tint_dir, base_extinction, tile_mist_mask, sigma_s, sigma_t);

		if (max(sigma_s.r, max(sigma_s.g, sigma_s.b)) > 1e-6) {
			vec3 step_in_scatter = vec3(0.0);

			for (int word = 0; word < word_count; word++) {
				uint mask = word == 0 ? ray_light_mask.x : ray_light_mask.y;
				while (mask != 0u) {
					int bit = findLSB(mask);
					mask &= (mask - 1u);
					int li = word * 32 + bit;
					int base = BASE_LIGHTS + li * LIGHT_STRIDE;

					vec4 pos_range = buf.entries[base];
					vec3 light_to_world = sample_pos - pos_range.xyz;
					float d2 = dot(light_to_world, light_to_world);
					float node_r = pos_range.w;
					if (d2 >= node_r * node_r) {
						continue;
					}

					vec4 flags = buf.entries[base + 4];
					float u2 = d2 * flags.w;
					if (u2 >= 1.0) {
						continue;
					}

					float inv_d = inversesqrt(max(d2, 1e-6));
					float d = d2 * inv_d;
					vec3 l_dir = light_to_world * inv_d;

					vec4 flux_spot = buf.entries[base + 1];
					float spot_cos = flux_spot.w;
					float cone_atten = 1.0;
					if (spot_cos > -1.0) {
						vec3 spot_dir = buf.entries[base + 3].xyz;
						float cos_angle = dot(l_dir, spot_dir);
						if (cos_angle <= spot_cos) {
							continue;
						}
						cone_atten = clamp((cos_angle - spot_cos) / max(1.0 - spot_cos, 1e-4), 0.0, 1.0);
					}

					// Pure-ALU windowed distance falloff & phase before any shadow texture / capsule fetches.
					float nd = max(1.0 - u2 * u2, 0.0);
					float dist_atten = (nd * nd) * inversesqrt(d2 + 0.35);
					float phase = henyey_greenstein(dot(l_dir, view_dir), anisotropy);
					vec3 unshadowed = flux_spot.rgb * (dist_atten * cone_atten * phase);
					if (max(unshadowed.r, max(unshadowed.g, unshadowed.b)) <= 5e-4) {
						continue;
					}

					float stat_vis = sample_static_shadow(
						light_to_world,
						d,
						buf.entries[base + 2],
						node_r,
						flags.x,
						inv_atlas_size);
					if (stat_vis <= 1e-3) {
						continue;
					}

					uint caster_mask = uint(flags.y + 0.5) | (uint(flags.z + 0.5) << 16);
					float dyn_vis = 1.0;
					if (caster_mask != 0u) {
						dyn_vis = eval_dynamic_capsules(pos_range.xyz, light_to_world, caster_mask);
						if (dyn_vis < 0.99) {
							any_dynamic_caster = true;
						}
						if (dyn_vis <= 1e-3) {
							continue;
						}
					}

					step_in_scatter += unshadowed * (stat_vis * dyn_vis);
				}
			}

			float step_trans = exp(-sigma_t * dt);
			vec3 step_contrib = transmittance * sigma_s * (step_in_scatter * push.shaft_scale) * dt;
			accum_scatter += step_contrib;
			float w_step = max(step_contrib.r, max(step_contrib.g, step_contrib.b));
			scatter_weight_sum += w_step;
			scatter_t_sum += w_step * t;
			transmittance *= step_trans;
		}
	}

	vec3 result = max(accum_scatter, vec3(0.0));

	// Temporal reprojection anchored at the energy-weighted ray distance of the illuminated shaft.
	vec4 prev_origin_valid = buf.entries[5];
	if (prev_origin_valid.w > 0.5) {
		float t_mid = scatter_weight_sum > 1e-5
			? (scatter_t_sum / scatter_weight_sum)
			: (0.5 * (t_enter + t_exit));
		vec3 world_mid = cam_origin + ray_dir * t_mid;
		vec3 prev_offset = world_mid - prev_origin_valid.xyz;

		vec4 prev_fwd_px = buf.entries[6];
		vec4 prev_right_py = buf.entries[7];
		vec4 prev_up = buf.entries[8];
		float prev_facing = dot(prev_offset, prev_fwd_px.xyz);

		if (prev_facing > 0.1) {
			float prev_u = (dot(prev_offset, prev_right_py.xyz) * prev_fwd_px.w / prev_facing + 1.0) * 0.5;
			float prev_v = (1.0 - dot(prev_offset, prev_up.xyz) * prev_right_py.w / prev_facing) * 0.5;

			if (prev_u >= 0.0 && prev_u <= 1.0 && prev_v >= 0.0 && prev_v <= 1.0) {
				float prev_t_scene = textureLod(prev_half_depth_tex, vec2(prev_u, prev_v), 0.0).r;
				float depth_diff = abs(prev_t_scene - t_scene);
				if (depth_diff <= max(0.35, t_scene * 0.12) && prev_t_scene >= t_enter) {
					vec3 history = max(textureLod(prev_shaft_tex, vec2(prev_u, prev_v), 0.0).rgb, vec3(0.0));
					// Soft variance/envelope clamp to suppress temporal lag on flickering or character-occluded shafts
					// while smoothing out the IGN raymarch dither in unbounded HDR space.
					float clamp_mult = any_dynamic_caster ? 1.65 : 2.25;
					vec3 box_min = result * 0.25;
					vec3 box_max = result * clamp_mult + vec3(0.015);
					history = clamp(history, box_min, box_max);
					float alpha = any_dynamic_caster ? 0.62 : 0.82;
					result = mix(result, history, alpha);
				}
			}
		}
	}

	imageStore(curr_shaft_img, coord, vec4(max(result, vec3(0.0)), 0.0));
}
