shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded;

#include "res://src/Engine/level/environment/sky_dome.gdshaderinc"
#include "res://src/Engine/level/environment/fable_atmosphere.gdshaderinc"
#include "res://src/Engine/material/fable_dither.gdshaderinc"
#include "res://src/Engine/level/environment/dynamic/dynamic_cloud_receiver.gdshaderinc"

// Water surface normal maps. normal_map_b cross-fades live theme slot transitions cleanly.
uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D normal_map_b : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_normal_map_b = false;
uniform float normal_map_blend = 0.0;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

uniform bool is_pbr = false;
uniform bool guest_water = false;
uniform bool ocean_mode = false;
uniform bool coastal_surf = false;
// 0 = Low, 1 = Balanced, 2 = High, 3 = Ultra.
uniform int water_quality_tier = 1;

// Planar reflections rendered by Engine_WaterReflection (eye-matched per split-screen view).
uniform sampler2D reflection_tex : hint_default_black, filter_linear, repeat_disable;
uniform bool planar_reflection = false;
uniform sampler2D reflection_tex_second : hint_default_black, filter_linear, repeat_disable;
uniform bool second_reflection = false;
uniform vec3 first_eye = vec3(0.0);
uniform vec3 second_eye = vec3(0.0);

// Shoreline coverage mask (1.0 = land, 0.0 = water).
uniform sampler2D water_mask : hint_default_black, filter_linear, repeat_disable;
uniform bool has_water_mask = false;
uniform vec4 w_mask_bounds = vec4(0.0, 0.0, 1.0, 1.0);
uniform vec4 w_edge = vec4(0.0, 1.0, 0.0, 0.0);

// Theme parameters packed by Engine_WaterMaterial.Apply.
uniform vec4 w_bias;
uniform vec4 w_speed;
uniform vec4 w_phase;
uniform vec4 w_scale;
uniform vec4 w_surface;
uniform vec4 w_deep;
uniform vec4 w_reflp;
uniform vec4 w_glit;
uniform vec3 sky_constant = vec3(0.58, 0.62, 0.57);

// Shoreline & wave tuning.
uniform float wave_amp = 0.12;
uniform float wave_slope = 1.5;
uniform float wave_len = 1.0;
uniform float wave_speed = 0.35;
uniform float wave_fade_depth = 0.8;
uniform float foam_width = 1.1;
uniform float beach_slope = 0.5;

uniform sampler2D terrain_height_tex : hint_default_black, filter_linear, repeat_disable;
uniform vec4 terrain_bounds = vec4(0.0, 0.0, 1.0, 1.0);
uniform bool has_terrain_height = false;

uniform vec4 w_body_bounds = vec4(0.0);
uniform bool has_body_bounds = false;

global uniform vec3 fable_sun_toward;
global uniform vec3 fable_sun_colour;
global uniform float dynamic_surface_time;
global uniform float dynamic_surface_rain;

uniform vec3 beta_rayleigh = vec3(0.001, 0.0015, 0.004);
uniform vec3 beta_mie = vec3(0.0056, 0.0064, 0.0105);
uniform float horizon_distance = 1900.0;
uniform bool has_dome = false;

varying vec3 v_local;
varying vec3 v_world;
varying vec3 v_eye_local;
varying vec3 v_sun_local;
varying vec4 v_shore; // xy = shore_dir, z = water depth above terrain, w = terrain slope

float body_edge(vec2 p) {
	if (!has_body_bounds) {
		return 1.0;
	}
	float d = min(min(p.x - w_body_bounds.x, p.y - w_body_bounds.y),
		min(w_body_bounds.z - p.x, w_body_bounds.w - p.y));
	return clamp(d / 1.5, 0.0, 1.0);
}

// Evaluates shoreline depth/gradient in vertex() so fragment() avoids 5 terrain height taps per pixel.
float terrain_probe_vertex(vec2 p, float y, bool full_grad, out vec2 shore_dir, out float slope) {
	shore_dir = vec2(0.0);
	slope = 0.0;
	if (!has_terrain_height) {
		return 4.0;
	}

	vec2 tuv = clamp((p - terrain_bounds.xy) * terrain_bounds.zw, vec2(0.0), vec2(1.0));
	float h = textureLod(terrain_height_tex, tuv, 0.0).r;
	float quick_depth = y - h;
	if (!full_grad && quick_depth > max(wave_fade_depth * 3.0, 2.5)) {
		return quick_depth;
	}

	vec2 duv = terrain_bounds.zw * 1.5;
	float hpx = textureLod(terrain_height_tex, clamp(tuv + vec2(duv.x, 0.0), vec2(0.0), vec2(1.0)), 0.0).r;
	float hnx = textureLod(terrain_height_tex, clamp(tuv - vec2(duv.x, 0.0), vec2(0.0), vec2(1.0)), 0.0).r;
	float hpz = textureLod(terrain_height_tex, clamp(tuv + vec2(0.0, duv.y), vec2(0.0), vec2(1.0)), 0.0).r;
	float hnz = textureLod(terrain_height_tex, clamp(tuv - vec2(0.0, duv.y), vec2(0.0), vec2(1.0)), 0.0).r;

	vec2 grad = vec2(hpx - hnx, hpz - hnz) / 3.0;
	slope = length(grad);
	if (slope > 1e-4) {
		shore_dir = grad / slope;
	}
	float hs = ((h * 4.0) + hpx + hnx + hpz + hnz) * 0.125;
	return y - hs;
}

float wave_zone(float sd, float slope) {
	float reach = wave_amp * 2.0;
	float band = smoothstep(-reach, -reach * 0.35, sd) *
		(1.0 - smoothstep(wave_fade_depth, wave_fade_depth * 2.5, sd));
	float low = 1.0 - smoothstep(beach_slope * 0.6, beach_slope * 1.2, slope);
	return band * low;
}

float roller_shape(float ph, float steep) {
	float s = ph < 0.5 ? ph : ph - 1.0;
	float k = s > 0.0 ? 2.0 : -2.0 * (1.0 + (2.5 * steep));
	float dc = s * k;
	return exp(-dc * dc * 4.5);
}

float roller_vary(vec2 p, float phase) {
	float n = textureLod(normal_map, (p * 0.021) + vec2(fract(floor(phase) * 0.173)), 0.0).x;
	return 0.4 + (1.1 * n);
}

void add_swell_wave(vec2 p, float t, vec2 dir, float len_m, float amp_m, inout vec2 slope, inout float height) {
	float k = TAU / max(len_m, 0.1);
	float phase = (dot(p, dir) * k) - (sqrt(9.8 * k) * t);
	slope += sin(phase) * (k * amp_m) * dir;
	height += cos(phase) * amp_m;
}

void vertex() {
	vec3 pos = VERTEX;
	v_eye_local = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
	// Light direction is free of body translation (w = 0).
	v_sun_local = (inverse(MODEL_MATRIX) * vec4(fable_sun_toward, 0.0)).xyz;
	v_shore = vec4(0.0, 0.0, 4.0, 0.0);

	if (!guest_water) {
		vec2 sdir;
		float slope;
		float dt = terrain_probe_vertex(pos.xz, pos.y, coastal_surf, sdir, slope);
		v_shore = vec4(sdir, dt, slope);

		if (coastal_surf) {
			float env = wave_zone(dt, slope);
			if (env > 0.002) {
				float phase = (dt + (TIME * wave_speed)) / max(wave_len, 0.05);
				float shoal = 1.0 - clamp(dt / max(wave_fade_depth, 0.05), 0.0, 1.0);
				float lift = roller_shape(fract(phase), shoal) * wave_amp * (0.7 + (0.9 * shoal))
					* env * roller_vary(pos.xz, phase);
				pos.y += lift * body_edge(pos.xz);
			}
		} else if (water_quality_tier >= 3 && !ocean_mode) {
			vec2 swell_slope = vec2(0.0);
			float swell_h = 0.0;
			add_swell_wave(pos.xz, TIME * 0.85, vec2(0.940, 0.342), 13.7, 0.016, swell_slope, swell_h);
			add_swell_wave(pos.xz, TIME * 0.85, vec2(-0.515, 0.857), 8.3, 0.010, swell_slope, swell_h);
			float shore_att = clamp(dt * 0.75, 0.0, 1.0) * body_edge(pos.xz);
			pos.y += swell_h * shore_att;
		}
	}

	VERTEX = pos;
	v_local = pos;
	v_world = (MODEL_MATRIX * vec4(pos, 1.0)).xyz;
}

// Reoriented normal mapping in Z-up tangent space.
vec3 rnm_blend(vec3 a, vec3 b) {
	vec3 t = a + vec3(0.0, 0.0, 1.0);
	vec3 u = vec3(-b.xy, b.z);
	return normalize((t * dot(t, u)) - (u * t.z));
}

vec3 sample_water_normal(vec2 uv, vec2 ddx, vec2 ddy) {
	vec2 n = (textureGrad(normal_map, uv, ddx, ddy).xy * 2.0) - 1.0;
	if (has_normal_map_b && normal_map_blend > 0.005) {
		vec2 nb = (textureGrad(normal_map_b, uv, ddx, ddy).xy * 2.0) - 1.0;
		n = mix(n, nb, clamp(normal_map_blend, 0.0, 1.0));
	}
	return vec3(n, sqrt(clamp(1.0 - dot(n, n), 0.0, 1.0)));
}

// Analytical expanding ring perturbations for active rainfall without branching array indexing.
vec2 eval_rain_cell(vec2 xz, vec2 cell_scale, float time_s, float speed) {
	vec2 p = xz * cell_scale;
	vec2 id = floor(p);
	vec2 f = fract(p) - 0.5;
	vec2 h = fract(sin(vec2(dot(id, vec2(127.1, 311.7)), dot(id, vec2(269.5, 183.3)))) * 43758.5453);
	vec2 offset = (h - 0.5) * 0.42;
	float t = fract((time_s * speed) + h.x + h.y);
	vec2 d = f - offset;
	float r = length(d);
	float ring_r = t * 0.48;
	float mask = smoothstep(0.11, 0.0, abs(r - ring_r)) * (1.0 - t) * smoothstep(0.0, 0.15, t);
	return (d / max(r, 1e-3)) * sin((r - ring_r) * 52.0) * mask;
}

vec2 sample_rain_ripples(vec2 xz, float time_s, float rain) {
	if (rain <= 0.01) {
		return vec2(0.0);
	}
	vec2 r0 = eval_rain_cell(xz, vec2(0.62), time_s, 0.72);
	vec2 r1 = eval_rain_cell(xz + vec2(3.7, 1.9), vec2(1.05), time_s + 0.37, 0.94);
	return (r0 + (r1 * 0.75)) * clamp(rain, 0.0, 1.5) * 0.14;
}

vec3 dome_reflect(vec3 r_world) {
	return fable_sky_overlaid(r_world, fable_sky_inscatter(
		vec3(r_world.x, r_world.z, r_world.y),
		atmos_sun,
		atmos_inscatter,
		beta_rayleigh,
		beta_mie,
		horizon_distance));
}

// Reconstructs positive view-space linear depth with explicit textureLod (DXIL-safe in loops).
float linear_depth(vec2 uv, mat4 inv_proj) {
	float d = textureLod(depth_tex, uv, 0.0).r;
	float raw_w = (inv_proj[2].w * d) + inv_proj[3].w;
	float safe_w = abs(raw_w) > 1e-6 ? raw_w : 1e-6;
	return -((inv_proj[2].z * d) + inv_proj[3].z) / safe_w;
}

void fragment() {
	// Evaluate local XZ derivatives unconditionally before any control flow (DXIL rule).
	vec2 local_xz_dx = dFdx(v_local.xz);
	vec2 local_xz_dy = dFdy(v_local.xz);

	float dt = v_shore.z;
	float slope = v_shore.w;
	vec2 sdir = v_shore.xy;

	float w_low = 0.0;
	float w_roller = 0.0;
	vec2 w_wave_n = vec2(0.0);
	if (!guest_water && has_terrain_height) {
		w_low = 1.0 - smoothstep(beach_slope * 0.6, beach_slope * 1.2, slope);
		if (coastal_surf) {
			float env = wave_zone(dt, slope);
			if (env > 0.002) {
				float phase = (dt + (TIME * wave_speed)) / max(wave_len, 0.05);
				float ph = fract(phase);
				float shoal = 1.0 - clamp(dt / max(wave_fade_depth, 0.05), 0.0, 1.0);
				float vary = roller_vary(v_local.xz, phase);
				float sh = roller_shape(ph, shoal);
				float amp_l = wave_amp * (0.7 + (0.9 * shoal));
				float shd = (roller_shape(fract(ph + 0.02), shoal) - sh) / 0.02;
				w_wave_n = sdir * clamp(
					slope * shd * amp_l * env * vary * wave_slope / max(wave_len, 0.05),
					-0.6, 0.6);
				float burst = (1.0 - smoothstep(0.0, 0.35, abs(dt))) * smoothstep(0.3, 0.8, sh);
				w_roller = env * burst * vary;
			}
		}
	}

	float mask_alpha = 1.0;
	if (has_water_mask) {
		vec2 mask_uv = (v_local.xz - w_mask_bounds.xy) * w_mask_bounds.zw;
		float mask = textureLod(water_mask, clamp(mask_uv, vec2(0.0), vec2(1.0)), 0.0).r;
		float d = 1.0 - mask;
		mask_alpha = clamp(
			(d - (w_edge.x + w_edge.z)) / max(w_edge.y - w_edge.x, 1e-4),
			0.0, 1.0);
	}

	float modern_alpha = coastal_surf ? max(mask_alpha, min(w_roller, 1.0)) : mask_alpha;
	if (!guest_water && modern_alpha <= 0.002) {
		// Depth-draw-never transparent early-out with zero `discard;`.
		ALBEDO = vec3(0.0);
		ALPHA = 0.0;
	} else {
		vec3 view = v_eye_local - v_local;
		float dist = length(view);
		vec3 V = view / max(dist, 1e-5);
		vec3 L = normalize(v_sun_local);

		vec2 phase01 = w_phase.xy;
		vec2 phase23 = w_phase.zw;
		vec2 w_warp = vec2(0.0);
		vec2 swell_slope = vec2(0.0);
		float swell_h = 0.0;

		if (!guest_water && water_quality_tier >= 1 && !ocean_mode) {
			float shore_damp = has_terrain_height ? clamp(dt * 0.85, 0.15, 1.0) : 1.0;
			add_swell_wave(v_local.xz, TIME * 0.85, vec2(0.940, 0.342), 13.7, 0.032 * shore_damp, swell_slope, swell_h);
			add_swell_wave(v_local.xz, TIME * 0.85, vec2(-0.515, 0.857), 8.3, 0.022 * shore_damp, swell_slope, swell_h);
			if (water_quality_tier >= 2) {
				add_swell_wave(v_local.xz, TIME * 0.85, vec2(0.292, -0.956), 4.9, 0.014 * shore_damp, swell_slope, swell_h);
				add_swell_wave(v_local.xz, TIME * 0.85, vec2(-0.875, -0.485), 2.9, 0.009 * shore_damp, swell_slope, swell_h);
			}
			w_warp = swell_slope * 1.4;
		}

		vec2 uv0 = ((v_local.xz + w_warp) * w_scale.xy) + phase01;
		vec2 uv1 = ((v_local.xz + w_warp) * w_scale.zw) + phase23;
		vec3 t0 = sample_water_normal(uv0, local_xz_dx * w_scale.xy, local_xz_dy * w_scale.xy);
		vec3 t1 = sample_water_normal(uv1, local_xz_dx * w_scale.zw, local_xz_dy * w_scale.zw);

		if (guest_water) {
			vec2 nxy = t0.xy + t1.xy;
			float nz = t0.z + t1.z;
			vec3 Nf = normalize(vec3(nxy.x, w_bias.z * nz, nxy.y));
			float fres = clamp((1.0 - dot(V, Nf)) + w_bias.x, 0.0, 1.0);
			vec3 watercol = mix(w_deep.rgb, w_surface.rgb, fres);
			float refl_str = clamp(w_deep.w, 0.0, 1.0);

			float frefl = clamp(fres + w_bias.y, 0.0, 1.0);
			float distf = ocean_mode ? 1.0 : clamp(dist / max(w_bias.w, 0.001), 0.0, 1.0);

			vec2 raw_uv = SCREEN_UV + (nxy * 0.5 * w_reflp.x);
			vec2 refl_uv = vec2(1.0 - raw_uv.x, raw_uv.y);
			vec3 Ng = normalize(vec3(nxy.x * w_reflp.x * 0.5, 1.0, nxy.y * w_reflp.x * 0.5));
			vec3 Rg = reflect(-V, Ng);
			Rg.y = abs(Rg.y);
			vec3 Rg_world = normalize((MODEL_MATRIX * vec4(Rg, 0.0)).xyz);
			vec3 dome = has_dome ? dome_reflect(Rg_world) : sky_constant;

			vec2 mirror_uv = clamp(refl_uv, vec2(0.0), vec2(1.0));
			bool second_view = second_reflection &&
				distance(CAMERA_POSITION_WORLD, second_eye) < distance(CAMERA_POSITION_WORLD, first_eye);
			vec3 Rf = max(planar_reflection
				? (second_view
					? textureLod(reflection_tex_second, mirror_uv, 0.0).rgb
					: textureLod(reflection_tex, mirror_uv, 0.0).rgb)
				: dome, vec3(0.0));

			vec2 refr_uv = SCREEN_UV + (nxy * 0.5 * w_reflp.y);
			vec3 Rr = max(textureLod(screen_tex, refr_uv, 0.0).rgb, vec3(0.0));

			vec3 near_col = mix(watercol, mix(Rr, Rf, frefl), refl_str);
			vec3 far_col = mix(watercol, Rf, refl_str);
			vec3 col = mix(near_col, far_col, distf);

			vec3 g = normalize(vec3(Nf.x, w_reflp.z * Nf.y, Nf.z));
			vec3 Rl = (2.0 * dot(L, g) * g) - L;
			float spec = clamp(dot(V, Rl), 0.0, 1.0);
			float glint = pow(max(spec, 1e-6), max(w_glit.x, 1.0));
			col += fable_sun_colour * (glint * max(w_reflp.w, 0.0));

			vec3 fog_t;
			vec3 fog_c;
			if (fable_atmosphere_terms(VERTEX, INV_VIEW_MATRIX, fog_t, fog_c)) {
				col = col * fog_t + (vec3(1.0) - fog_t) * fog_c;
			}

			ALBEDO = max(col, vec3(0.0));
			ALPHA = mask_alpha;
		} else {
			vec3 n_detail = rnm_blend(t0, t1);

			// Non-axis-aligned rotated 3rd micro-ripple octave breaks sliding grid repetition.
			if (water_quality_tier >= 1 && dist < 95.0) {
				const mat2 ROT37 = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8));
				vec2 s2 = w_scale.zw * 2.13;
				vec2 uv2 = (ROT37 * v_local.xz) * s2 + vec2(TIME * 0.023, -TIME * 0.031);
				vec2 ddx2 = (ROT37 * local_xz_dx) * s2;
				vec2 ddy2 = (ROT37 * local_xz_dy) * s2;
				vec3 t2 = sample_water_normal(uv2, ddx2, ddy2);
				float micro_fade = smoothstep(95.0, 18.0, dist) * 0.36;
				t2 = normalize(mix(vec3(0.0, 0.0, 1.0), t2, micro_fade));
				n_detail = rnm_blend(n_detail, t2);
			}

			vec2 rain_n = vec2(0.0);
			if (water_quality_tier >= 2 && dynamic_surface_rain > 0.01 && dist < 42.0) {
				rain_n = sample_rain_ripples(v_local.xz, dynamic_surface_time, dynamic_surface_rain)
					* (1.0 - smoothstep(18.0, 42.0, dist));
			}

			// Correct NormalScale application: scale horizontal XY slopes instead of squashing Y.
			float normal_strength = clamp(w_bias.z, 0.10, 1.40);
			float dist_fade = mix(1.0, 0.45, clamp(dist / 120.0, 0.0, 1.0));
			vec2 surf_nxy = ((n_detail.xy * normal_strength) + (swell_slope * normal_strength) + rain_n + w_wave_n)
				* dist_fade;
			vec3 Nf = normalize(vec3(surf_nxy.x, n_detail.z, surf_nxy.y));

			// Derivative / Toksvig specular roughness anti-aliasing prevents distant shimmer sparkles.
			float footprint = length(vec2(length(local_xz_dx * w_scale.x), length(local_xz_dy * w_scale.x)));
			float roughness = clamp(0.035 + clamp(footprint * 11.0, 0.0, 0.55), 0.035, 0.65);

			float water_lin = -VERTEX.z;
			float scene_here = linear_depth(SCREEN_UV, INV_PROJECTION_MATRIX);
			float raw_depth_here = max(scene_here - water_lin, 0.0);

			float ndv = clamp(dot(V, Nf), 0.001, 1.0);
			float fres_phys = 0.02 + ((0.98 - (0.55 * roughness)) * pow(1.0 - ndv, 5.0));
			vec3 shallow_deep = mix(w_deep.rgb, w_surface.rgb, clamp(fres_phys * 1.4, 0.0, 1.0));

			// Depth-accurate underwater refraction + chromatic Beer-Lambert absorption.
			vec3 under = shallow_deep;
			if (!ocean_mode) {
				float refr_strength = clamp(w_reflp.y, 0.04, 1.2) * 0.14 *
					clamp(raw_depth_here * 0.70, 0.0, 1.0) * smoothstep(95.0, 18.0, dist);
				vec2 refr_offset = water_quality_tier >= 1 ? (surf_nxy * refr_strength) : vec2(0.0);
				vec2 refr_uv = clamp(SCREEN_UV + refr_offset, vec2(0.001), vec2(0.999));
				float refr_lin = water_quality_tier >= 1
					? linear_depth(refr_uv, INV_PROJECTION_MATRIX)
					: scene_here;
				if (refr_lin <= water_lin) {
					refr_uv = SCREEN_UV;
					refr_lin = scene_here;
					refr_offset = vec2(0.0);
				}

				vec3 Rr = max(textureLod(screen_tex, refr_uv, 0.0).rgb, vec3(0.0));
				if (water_quality_tier >= 3 && dist < 35.0 && length(refr_offset) > 1e-4) {
					vec2 uv_r = clamp(refr_uv + (refr_offset * 0.14), vec2(0.001), vec2(0.999));
					vec2 uv_b = clamp(refr_uv - (refr_offset * 0.14), vec2(0.001), vec2(0.999));
					if (linear_depth(uv_r, INV_PROJECTION_MATRIX) > water_lin) {
						Rr.r = max(textureLod(screen_tex, uv_r, 0.0).r, 0.0);
					}
					if (linear_depth(uv_b, INV_PROJECTION_MATRIX) > water_lin) {
						Rr.b = max(textureLod(screen_tex, uv_b, 0.0).b, 0.0);
					}
				}

				float depth_w = max(refr_lin - water_lin, 0.0);
				float clarity_depth = clamp(w_bias.w, 2.0, 14.0);
				vec3 extinction = vec3(1.55, 0.62, 0.48) / max(clarity_depth * 0.45, 0.5);
				vec3 transmittance = exp(-depth_w * extinction);
				float deep_mix = 1.0 - exp(-depth_w / max(clarity_depth * 0.65, 1.0));
				vec3 scatter_col = mix(w_surface.rgb, w_deep.rgb, deep_mix);
				vec3 sss = w_surface.rgb * fable_sun_colour *
					(0.22 * exp(-depth_w * 0.85) * clamp((dot(-V, L) * 0.5) + 0.5, 0.2, 1.0) * clamp(L.y * 3.0, 0.0, 1.0));
				under = (Rr * transmittance) + (scatter_col * (vec3(1.0) - transmittance)) + sss;
			}

			// Sky dome base reflection in world space (keeps region X-mirror exact).
			vec3 R_local = reflect(-V, Nf);
			R_local.y = abs(R_local.y);
			vec3 R_world = normalize((MODEL_MATRIX * vec4(R_local, 0.0)).xyz);
			vec3 sky_refl = has_dome ? dome_reflect(R_world) : sky_constant;
			vec3 refl_col = sky_refl;

			float refl_str = clamp(w_deep.w, 0.0, 1.0);
			float contact_alpha = ocean_mode ? 1.0 : clamp(raw_depth_here * 4.0, 0.0, 1.0);
			float frefl = clamp(fres_phys + (w_bias.y * 0.35), 0.0, 1.0);
			float grazing_lift = smoothstep(35.0, 140.0, dist) * 0.22;
			float F = clamp((frefl + grazing_lift) * refl_str * mix(0.35, 1.0, contact_alpha), 0.0, 0.98);

			if (planar_reflection) {
				vec2 raw_planar_uv = SCREEN_UV + (surf_nxy * 0.22 * clamp(w_reflp.x, 0.1, 1.5));
				vec2 planar_uv = clamp(vec2(1.0 - raw_planar_uv.x, raw_planar_uv.y), vec2(0.001), vec2(0.999));
				bool second_view = second_reflection &&
					distance(CAMERA_POSITION_WORLD, second_eye) < distance(CAMERA_POSITION_WORLD, first_eye);
				vec3 planar_col = max(second_view
					? textureLod(reflection_tex_second, planar_uv, 0.0).rgb
					: textureLod(reflection_tex, planar_uv, 0.0).rgb, vec3(0.0));
				vec2 pb = min(planar_uv, 1.0 - planar_uv);
				float planar_edge = smoothstep(0.0, 0.05, min(pb.x, pb.y));
				refl_col = mix(sky_refl, planar_col, planar_edge * 0.92);
			}

			// Perspective-correct contact SSR with Bayer jitter and 4-step binary refinement.
			if (water_quality_tier >= 1 && F > 0.02) {
				int max_steps = planar_reflection
					? (water_quality_tier >= 3 ? 16 : (water_quality_tier == 2 ? 12 : 8))
					: (water_quality_tier >= 3 ? 22 : (water_quality_tier == 2 ? 16 : 12));
				float step_growth = water_quality_tier >= 3 ? 1.15 : (water_quality_tier == 2 ? 1.18 : 1.24);
				float max_ssr_dist = planar_reflection ? 28.0 : 120.0;

				if (dist < (max_ssr_dist * 2.5)) {
					vec3 world_n = normalize((MODEL_MATRIX * vec4(Nf, 0.0)).xyz);
					vec3 view_n = normalize((VIEW_MATRIX * vec4(world_n, 0.0)).xyz);
					vec3 ro = VERTEX;
					vec3 rd = normalize(reflect(normalize(ro), view_n));
					if (dot(rd, view_n) > 0.02) {
						vec4 clip_ro = PROJECTION_MATRIX * vec4(ro, 1.0);
						vec4 clip_rd = PROJECTION_MATRIX * vec4(rd, 0.0);

						float jitter = fable_bayer_threshold(FRAGCOORD.xy);
						float t_prev = 0.0;
						float t_ray = 0.08 + (jitter * 0.14);
						bool found = false;
						float hit_lo = 0.0;
						float hit_hi = 0.0;
						float hit_thickness = 0.1;
						vec2 hit_uv = vec2(0.0);

						const int MAX_SSR = 22;
						for (int i = 0; i < MAX_SSR; i++) {
							if (i >= max_steps || t_ray > max_ssr_dist) {
								break;
							}
							vec4 clip = clip_ro + (clip_rd * t_ray);
							if (clip.w <= 0.01) {
								break;
							}
							float safe_w = max(clip.w, 1e-4);
							vec2 uv = ((clip.xy / safe_w) * 0.5) + 0.5;
							if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) {
								break;
							}

							float scene = linear_depth(uv, INV_PROJECTION_MATRIX);
							float ray = -(ro.z + (rd.z * t_ray));
							float thickness = 0.06 + (t_ray * 0.10);
							if (scene > 0.25 && scene < ray && (ray - scene) < thickness) {
								found = true;
								hit_lo = t_prev;
								hit_hi = t_ray;
								hit_thickness = thickness;
								hit_uv = uv;
								break;
							}

							t_prev = t_ray;
							t_ray *= step_growth;
						}

						if (found) {
							float ray_best = -(ro.z + (rd.z * hit_hi));
							float scene_best = linear_depth(hit_uv, INV_PROJECTION_MATRIX);
							for (int b = 0; b < 4; b++) {
								float mid = 0.5 * (hit_lo + hit_hi);
								vec4 clip_m = clip_ro + (clip_rd * mid);
								float safe_wm = max(clip_m.w, 1e-4);
								vec2 uv_m = clamp(((clip_m.xy / safe_wm) * 0.5) + 0.5, vec2(0.001), vec2(0.999));
								float scene_m = linear_depth(uv_m, INV_PROJECTION_MATRIX);
								float ray_m = -(ro.z + (rd.z * mid));
								if (scene_m > 0.25 && scene_m < ray_m) {
									hit_hi = mid;
									hit_uv = uv_m;
									ray_best = ray_m;
									scene_best = scene_m;
								} else {
									hit_lo = mid;
								}
							}

							vec2 b = min(hit_uv, 1.0 - hit_uv);
							float edge = smoothstep(0.0, 0.08, b.x) * smoothstep(0.0, 0.14, hit_uv.y)
								* smoothstep(0.0, 0.06, 1.0 - hit_uv.y);
							float conf = 1.0 - clamp((ray_best - scene_best) / max(hit_thickness, 0.05), 0.0, 1.0);
							float hit_weight = edge * conf;
							if (hit_weight > 0.004) {
								vec3 ssr_col = max(textureLod(screen_tex, hit_uv, roughness * 2.2).rgb, vec3(0.0));
								float ssr_mix = planar_reflection
									? (hit_weight * smoothstep(max_ssr_dist, 5.0, hit_hi) * 0.85)
									: hit_weight;
								refl_col = mix(refl_col, ssr_col, ssr_mix);
							}
						}
					}
				}
			}

			vec3 col = mix(under, refl_col, F);

			// Sun sparkle corridor attenuated by overhead dynamic clouds (scene-referred HDR).
			float cloud_t = surface_cloud_transmission(v_world);
			vec3 H = normalize(L + V);
			float gb = mix(0.25, 1.25, clamp(w_reflp.z, 0.0, 2.0));
			vec3 g = normalize(vec3(Nf.x * gb, Nf.y, Nf.z * gb));
			float ndh = clamp(dot(g, H), 0.0, 1.0);
			float power = max(w_glit.x * (1.0 - (roughness * 0.65)), 8.0);
			float glare = pow(ndh, power) + (0.05 * pow(ndh, max(power * 0.12, 4.0)));
			float corridor = pow(clamp(dot(reflect(-V, vec3(0.0, 1.0, 0.0)), L), 0.0, 1.0), 14.0);
			col += fable_sun_colour * max(w_reflp.w, 0.0) * glare * corridor *
				clamp(L.y * 6.0, 0.0, 1.0) * cloud_t;

			// Shoreline foam: surf breakers on coastlines vs gentle meniscus on calm inland lakes.
			if (!ocean_mode && dist < 85.0) {
				if (coastal_surf) {
					float w_surge = clamp(swell_h / max(wave_amp, 0.04), -1.0, 1.0);
					float lap = 0.6 + (0.4 * w_surge);
					float shore_foam = (1.0 - clamp(raw_depth_here / max(foam_width, 0.05), 0.0, 1.0))
						* lap * (1.0 - w_low);
					float band = max(shore_foam, w_roller);
					if (band > 0.003) {
						float f0 = textureGrad(normal_map,
							(v_local.xz * 0.33) + vec2(TIME * 0.020, TIME * 0.012),
							local_xz_dx * 0.33, local_xz_dy * 0.33).x;
						float f1 = textureGrad(normal_map,
							(v_local.xz * 0.19) - vec2(TIME * 0.016, TIME * 0.010),
							local_xz_dx * 0.19, local_xz_dy * 0.19).y;
						float pattern = mix(f1, f0, clamp((band - 0.4) / 0.4, 0.0, 1.0));
						float foam = smoothstep(0.45, 0.95, band * (0.35 + (0.95 * pattern)));

						vec3 zen = has_dome ? dome_reflect(vec3(0.0, 1.0, 0.0)) : sky_constant;
						vec3 foam_col = (zen * 0.75) + (fable_sun_colour * 0.30 * clamp(L.y * 2.0, 0.0, 1.0));
						col = mix(col, foam_col, foam * 0.75);
					}
				} else if (dist < 45.0) {
					float calm_lap = 0.55 + (0.25 * sin((TIME * 1.1) + dot(v_local.xz, vec2(0.31, 0.17))));
					float meniscus = (1.0 - clamp(raw_depth_here / max(foam_width * 0.35, 0.12), 0.0, 1.0))
						* calm_lap * (1.0 - smoothstep(beach_slope * 0.7, beach_slope * 1.4, slope))
						* smoothstep(45.0, 12.0, dist);
					if (meniscus > 0.008) {
						float f0 = textureGrad(normal_map,
							(v_local.xz * 0.42) + vec2(TIME * 0.010, TIME * 0.007),
							local_xz_dx * 0.42, local_xz_dy * 0.42).x;
						float foam = smoothstep(0.42, 0.95, meniscus * (0.40 + (0.85 * f0)));
						vec3 zen = has_dome ? dome_reflect(vec3(0.0, 1.0, 0.0)) : sky_constant;
						vec3 foam_col = (zen * 0.70) + (fable_sun_colour * 0.22 * clamp(L.y * 2.0, 0.0, 1.0));
						col = mix(col, foam_col, foam * 0.35);
					}
				}
			}

			vec3 fog_t;
			vec3 fog_c;
			if (fable_atmosphere_terms(VERTEX, INV_VIEW_MATRIX, fog_t, fog_c)) {
				col = col * fog_t + (vec3(1.0) - fog_t) * fog_c;
			}

			ALBEDO = max(col, vec3(0.0));
			ALPHA = modern_alpha;
		}
	}
}
