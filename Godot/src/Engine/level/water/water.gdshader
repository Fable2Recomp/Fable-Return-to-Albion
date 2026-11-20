shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never;

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
varying float v_cloud_t;

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
			add_swell_wave(pos.xz, TIME * 0.85, vec2(0.940, 0.342), 13.7, 0.014, swell_slope, swell_h);
			add_swell_wave(pos.xz, TIME * 0.85, vec2(-0.515, 0.857), 8.3, 0.009, swell_slope, swell_h);
			float shore_att = clamp(dt * 0.75, 0.0, 1.0) * body_edge(pos.xz);
			pos.y += swell_h * shore_att;
		}
	}

	VERTEX = pos;
	v_local = pos;
	v_world = (MODEL_MATRIX * vec4(pos, 1.0)).xyz;
	v_cloud_t = guest_water ? 1.0 : surface_cloud_transmission(v_world);
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
// In Forward+ reversed-Z, d == 0.0 is the infinite sky plane; return 1e6 so sky pixels never match SSR rays.
float linear_depth(vec2 uv, mat4 inv_proj) {
	float d = textureLod(depth_tex, uv, 0.0).r;
	if (d <= 1e-5) {
		return 1e6;
	}
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
		EMISSION = vec3(0.0);
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
			float shore_damp = (has_terrain_height ? clamp(dt * 0.85, 0.15, 1.0) : 1.0)
				* smoothstep(140.0, 25.0, dist);
			add_swell_wave(v_local.xz, TIME * 0.85, vec2(0.940, 0.342), 13.7, 0.028 * shore_damp, swell_slope, swell_h);
			add_swell_wave(v_local.xz, TIME * 0.85, vec2(-0.515, 0.857), 8.3, 0.019 * shore_damp, swell_slope, swell_h);
			if (water_quality_tier >= 2) {
				add_swell_wave(v_local.xz, TIME * 0.85, vec2(0.292, -0.956), 4.9, 0.012 * shore_damp, swell_slope, swell_h);
				add_swell_wave(v_local.xz, TIME * 0.85, vec2(-0.875, -0.485), 2.9, 0.008 * shore_damp, swell_slope, swell_h);
			}
			w_warp = swell_slope * 1.2;
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

			ALBEDO = vec3(0.0);
			EMISSION = max(col, vec3(0.0));
			ALPHA = mask_alpha;
		} else {
			vec3 n_detail = rnm_blend(t0, t1);

			// Near-field rotated 3rd micro-ripple octave; strictly faded out by 28 m so FSR1 + aniso=0 never speckles.
			if (water_quality_tier >= 1 && dist < 28.0) {
				const mat2 ROT37 = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8));
				vec2 s2 = w_scale.zw * 2.05;
				vec2 uv2 = (ROT37 * v_local.xz) * s2 + vec2(TIME * 0.021, -TIME * 0.029);
				vec2 ddx2 = (ROT37 * local_xz_dx) * s2;
				vec2 ddy2 = (ROT37 * local_xz_dy) * s2;
				vec3 t2 = sample_water_normal(uv2, ddx2, ddy2);
				float micro_fade = smoothstep(28.0, 6.0, dist) * 0.28;
				t2 = normalize(mix(vec3(0.0, 0.0, 1.0), t2, micro_fade));
				n_detail = rnm_blend(n_detail, t2);
			}

			vec2 rain_n = vec2(0.0);
			if (water_quality_tier >= 2 && dynamic_surface_rain > 0.01 && dist < 42.0) {
				rain_n = sample_rain_ripples(v_local.xz, dynamic_surface_time, dynamic_surface_rain)
					* (1.0 - smoothstep(18.0, 42.0, dist));
			}

			// Derivative / Toksvig footprint damping prevents sub-pixel normal & specular fireflies under FSR.
			float uv_footprint = max(length(local_xz_dx * w_scale.x), length(local_xz_dy * w_scale.x));
			float aa_damp = 1.0 / (1.0 + (uv_footprint * uv_footprint * 85.0));
			float roughness = clamp(0.045 + clamp(uv_footprint * 6.5, 0.0, 0.42), 0.045, 0.48);

			// Fable's WATER_NORMAL_SCALE (w_bias.z) scales vertical Z: lower values amplify gentle normal-map slopes.
			float z_scale = clamp(w_bias.z, 0.20, 2.0);
			vec2 combined_xy = (((n_detail.xy / z_scale) + swell_slope + rain_n) * aa_damp) + w_wave_n;
			float xy_len = length(combined_xy);
			if (xy_len > 0.85) {
				combined_xy *= 0.85 / xy_len;
			}
			// Nf drives 3D ripple shading, Fresnel contrast, and wave highlights; Nr keeps reflected silhouettes coherent.
			vec3 Nf = normalize(vec3(combined_xy.x, n_detail.z, combined_xy.y));
			float refl_tilt = clamp(w_reflp.x * 0.28, 0.14, 0.55);
			vec3 Nr = normalize(mix(vec3(0.0, 1.0, 0.0), Nf, refl_tilt));

			float water_lin = -VERTEX.z;
			float scene_here = linear_depth(SCREEN_UV, INV_PROJECTION_MATRIX);
			float raw_depth_here = max(scene_here - water_lin, 0.0);

			float ndv_f = clamp(dot(V, Nf), 0.001, 1.0);
			float fres_curve = 0.08 + ((0.92 - (0.35 * roughness)) * pow(1.0 - ndv_f, 3.0));

			// Underwater transmitted scene (lit) vs in-scattered water volume reflectance (lit via ALBEDO + light()).
			vec3 refr_transmitted = vec3(0.0);
			vec3 volume_albedo = mix(w_deep.rgb, w_surface.rgb, clamp(fres_curve, 0.0, 1.0));
			if (!ocean_mode) {
				float refr_strength = clamp(w_reflp.y, 0.05, 1.5) * 0.09 *
					clamp(raw_depth_here * 0.85, 0.0, 1.0) * smoothstep(95.0, 16.0, dist);
				vec2 refr_offset = water_quality_tier >= 1 ? (Nr.xz * refr_strength) : vec2(0.0);
				vec2 refr_uv = clamp(SCREEN_UV + refr_offset, vec2(0.001), vec2(0.999));
				float refr_lin = water_quality_tier >= 1
					? linear_depth(refr_uv, INV_PROJECTION_MATRIX)
					: scene_here;
				if (refr_lin <= water_lin || refr_lin > 9e5) {
					refr_uv = SCREEN_UV;
					refr_lin = scene_here;
					refr_offset = vec2(0.0);
				}

				vec3 Rr = max(textureLod(screen_tex, refr_uv, 0.0).rgb, vec3(0.0));
				if (water_quality_tier >= 3 && dist < 30.0 && length(refr_offset) > 1e-4) {
					vec2 uv_r = clamp(refr_uv + (refr_offset * 0.12), vec2(0.001), vec2(0.999));
					vec2 uv_b = clamp(refr_uv - (refr_offset * 0.12), vec2(0.001), vec2(0.999));
					float lin_r = linear_depth(uv_r, INV_PROJECTION_MATRIX);
					float lin_b = linear_depth(uv_b, INV_PROJECTION_MATRIX);
					if (lin_r > water_lin && lin_r < 9e5) {
						Rr.r = max(textureLod(screen_tex, uv_r, 0.0).r, 0.0);
					}
					if (lin_b > water_lin && lin_b < 9e5) {
						Rr.b = max(textureLod(screen_tex, uv_b, 0.0).b, 0.0);
					}
				}

				float depth_w = clamp(refr_lin - water_lin, 0.0, 60.0);
				float absorb_scale = 1.0 / clamp(w_bias.w * 0.22, 0.75, 3.0);
				vec3 extinction = vec3(1.75, 0.88, 0.72) * absorb_scale;
				vec3 transmittance = exp(-depth_w * extinction);
				float deep_mix = 1.0 - exp(-depth_w * 0.75);
				vec3 scatter_col = mix(w_surface.rgb, w_deep.rgb, deep_mix);
				refr_transmitted = Rr * transmittance;
				volume_albedo = scatter_col * (vec3(1.0) - transmittance);
			}

			// World-space reflection direction clamped above the horizontal water plane so rays never dip into lakebed mud.
			vec3 R_local = reflect(-V, Nr);
			vec3 R_world = (MODEL_MATRIX * vec4(R_local, 0.0)).xyz;
			R_world.y = max(R_world.y, 0.012);
			R_world = normalize(R_world);
			vec3 sky_refl = has_dome ? dome_reflect(R_world) : sky_constant;
			vec3 refl_col = sky_refl;

			float refl_str = clamp(w_deep.w, 0.0, 1.0);
			float artist_bias = clamp((w_bias.x * 0.30) + (w_bias.y * 0.45), -0.05, 0.45);
			float depth_reflect = ocean_mode ? 1.0 : smoothstep(0.0, 2.2, raw_depth_here);
			float dist_reflect = ocean_mode ? 1.0 : smoothstep(6.0, 55.0, dist);
			float shore_clear = ocean_mode ? 1.0 : mix(0.25, 1.0, clamp(raw_depth_here * 3.0, 0.0, 1.0));
			float base_f = max(fres_curve + artist_bias, mix(0.14, 0.58, depth_reflect * mix(0.45, 1.0, dist_reflect)));
			float F = clamp(base_f * refl_str * shore_clear, 0.0, 0.95);

			if (planar_reflection) {
				vec2 raw_planar_uv = SCREEN_UV + (Nr.xz * 0.16 * clamp(w_reflp.x, 0.1, 1.5));
				vec2 planar_uv = clamp(vec2(1.0 - raw_planar_uv.x, raw_planar_uv.y), vec2(0.001), vec2(0.999));
				bool second_view = second_reflection &&
					distance(CAMERA_POSITION_WORLD, second_eye) < distance(CAMERA_POSITION_WORLD, first_eye);
				vec3 planar_col = max(second_view
					? textureLod(reflection_tex_second, planar_uv, 0.0).rgb
					: textureLod(reflection_tex, planar_uv, 0.0).rgb, vec3(0.0));
				vec2 pb = min(planar_uv, 1.0 - planar_uv);
				float planar_edge = smoothstep(0.0, 0.06, min(pb.x, pb.y));
				refl_col = mix(sky_refl, planar_col, planar_edge * 0.94);
			}

			// Full-range perspective SSR (~60-110 m reach) with overlap slab thickness (zero Bayer checkerboard speckles)
			// and 4-step binary search refinement. Captures characters, NPCs, entity pillars, and shoreline trees.
			if (water_quality_tier >= 1 && F > 0.02 && dist < 150.0) {
				int max_steps = water_quality_tier >= 3 ? 24 : (water_quality_tier == 2 ? 18 : 14);
				float t_start = water_quality_tier >= 3 ? 0.22 : (water_quality_tier == 2 ? 0.26 : 0.30);
				float step_growth = water_quality_tier >= 3 ? 1.31 : (water_quality_tier == 2 ? 1.40 : 1.50);

				vec3 ro = VERTEX;
				vec3 rd = normalize((VIEW_MATRIX * vec4(R_world, 0.0)).xyz);
				vec4 clip_ro = PROJECTION_MATRIX * vec4(ro, 1.0);
				vec4 clip_rd = PROJECTION_MATRIX * vec4(rd, 0.0);

				float jitter = fable_bayer_threshold(FRAGCOORD.xy);
				float t_prev = 0.06;
				float t_ray = t_start * (0.88 + (0.24 * jitter));
				bool found = false;
				float hit_lo = 0.0;
				float hit_hi = 0.0;
				float hit_thickness = 0.25;
				vec2 hit_uv = vec2(0.0);

				const int MAX_SSR = 24;
				for (int i = 0; i < MAX_SSR; i++) {
					if (i >= max_steps || t_ray > 125.0) {
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
					float step_span = t_ray - t_prev;
					float thickness = max(step_span * 1.40, 0.20 + (t_ray * 0.18));
					if (scene < 9e5 && scene > 0.20 && scene < ray && (ray - scene) < thickness) {
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
						if (scene_m < 9e5 && scene_m > 0.20 && scene_m < ray_m) {
							hit_hi = mid;
							hit_uv = uv_m;
							ray_best = ray_m;
							scene_best = scene_m;
						} else {
							hit_lo = mid;
						}
					}

					vec2 b = min(hit_uv, 1.0 - hit_uv);
					float edge = smoothstep(0.0, 0.07, b.x) * smoothstep(0.0, 0.12, hit_uv.y)
						* smoothstep(0.0, 0.05, 1.0 - hit_uv.y);
					float gap = max(ray_best - scene_best, 0.0);
					float conf = 1.0 - smoothstep(hit_thickness * 0.45, hit_thickness, gap);
					float hit_weight = edge * conf * smoothstep(145.0, 95.0, dist);
					if (hit_weight > 0.004) {
						vec3 ssr_col = max(textureLod(screen_tex, hit_uv, roughness * 2.0).rgb, vec3(0.0));
						refl_col = mix(refl_col, ssr_col, hit_weight);
					}
				}
			}

			vec3 lit_emissive = (refr_transmitted * (1.0 - F)) + (refl_col * F);
			vec3 lit_albedo = volume_albedo * (1.0 - F);

			// Shoreline foam routed through lit_albedo so shadows and night darkness attenuate foam naturally.
			if (!ocean_mode && dist < 85.0) {
				float foam_w = 0.0;
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
						foam_w = smoothstep(0.45, 0.95, band * (0.35 + (0.95 * pattern))) * 0.72;
					}
				} else if (dist < 45.0) {
					float calm_lap = 0.55 + (0.25 * sin((TIME * 1.1) + dot(v_local.xz, vec2(0.31, 0.17))));
					float meniscus = (1.0 - clamp(raw_depth_here / max(foam_width * 0.30, 0.10), 0.0, 1.0))
						* calm_lap * (1.0 - smoothstep(beach_slope * 0.7, beach_slope * 1.4, slope))
						* smoothstep(45.0, 12.0, dist);
					if (meniscus > 0.008) {
						float f0 = textureGrad(normal_map,
							(v_local.xz * 0.42) + vec2(TIME * 0.010, TIME * 0.007),
							local_xz_dx * 0.42, local_xz_dy * 0.42).x;
						foam_w = smoothstep(0.45, 0.95, meniscus * (0.40 + (0.85 * f0))) * 0.32;
					}
				}

				if (foam_w > 0.001) {
					lit_albedo = mix(lit_albedo, vec3(0.62), foam_w);
					lit_emissive *= (1.0 - (foam_w * 0.65));
				}
			}

			vec3 fog_t = vec3(1.0);
			vec3 fog_c = vec3(0.0);
			if (fable_atmosphere_terms(VERTEX, INV_VIEW_MATRIX, fog_t, fog_c)) {
				fable_atmosphere_apply(fog_t, fog_c, 1.0, lit_albedo, lit_emissive);
			}

			vec3 world_nf = normalize((MODEL_MATRIX * vec4(Nf, 0.0)).xyz);
			NORMAL = normalize((VIEW_MATRIX * vec4(world_nf, 0.0)).xyz);
			ROUGHNESS = roughness;
			METALLIC = 0.0;
			BACKLIGHT = fog_t;
			ALBEDO = max(lit_albedo, vec3(0.0));
			EMISSION = max(lit_emissive, vec3(0.0));
			ALPHA = modern_alpha;
		}
	}
}

// Forward+ water lighting: directional sun/moon shadows + GGX ripple highlights + placed/FX lamps.
// Guarded with `if` rather than `return;` per Godot's light processor rule.
void light() {
	if (!guest_water && ALPHA > 0.002) {
		float cloud_gate = LIGHT_IS_DIRECTIONAL ? v_cloud_t : 1.0;
		vec3 radiance = (LIGHT_COLOR * (1.0 / PI)) * ATTENUATION * cloud_gate;
		float ndl = dot(NORMAL, LIGHT);

		// Subsurface wrap diffuse illuminates ALBEDO water volume & wave slopes in sunlit areas
		// while dropping cleanly into shadow behind pillars, cliffs, bridges, and trees.
		float wrap_diff = clamp((ndl + 0.35) / 1.35, 0.0, 1.0);
		DIFFUSE_LIGHT += radiance * wrap_diff;

		// Dual-lobe Trowbridge-Reitz GGX: crisp sparkle + broad wave-facet sheen across all camera angles.
		if (ndl > -0.15 && SPECULAR_AMOUNT > 0.0) {
			vec3 H = normalize(LIGHT + VIEW);
			float ndh = clamp(dot(NORMAL, H), 0.0, 1.0);
			float ndv_l = clamp(dot(NORMAL, VIEW), 0.001, 1.0);
			float ndl_pos = clamp((ndl * 0.85) + 0.15, 0.001, 1.0);
			float vdh = clamp(dot(VIEW, H), 0.0, 1.0);

			float a_sharp = clamp(ROUGHNESS * 0.85, 0.045, 0.42);
			float a2_s = a_sharp * a_sharp;
			float denom_s = (ndh * ndh * (a2_s - 1.0)) + 1.0;
			float D_s = a2_s / max(PI * denom_s * denom_s, 1e-5);
			float gv_s = ndl_pos * sqrt((ndv_l * ndv_l * (1.0 - a2_s)) + a2_s);
			float gl_s = ndv_l * sqrt((ndl_pos * ndl_pos * (1.0 - a2_s)) + a2_s);
			float Vis_s = 0.5 / max(gv_s + gl_s, 1e-4);

			float a_sheen = clamp((ROUGHNESS * 2.4) + 0.16, 0.20, 0.65);
			float a2_w = a_sheen * a_sheen;
			float denom_w = (ndh * ndh * (a2_w - 1.0)) + 1.0;
			float D_w = a2_w / max(PI * denom_w * denom_w, 1e-5);

			float f_spec = 0.02 + (0.98 * pow(1.0 - vdh, 5.0));
			float glit_scale = LIGHT_IS_DIRECTIONAL ? clamp(w_reflp.w * 0.18, 0.40, 1.80) : 1.0;
			// Soft-cap single-pixel NDF spikes so sub-pixel waves never fire isolated FSR speckles.
			float spec_brdf = (min(D_s * Vis_s, 16.0) * glit_scale) + (D_w * 0.22);
			SPECULAR_LIGHT += radiance * (spec_brdf * f_spec * ndl_pos * SPECULAR_AMOUNT) * BACKLIGHT;
		}
	}
}
