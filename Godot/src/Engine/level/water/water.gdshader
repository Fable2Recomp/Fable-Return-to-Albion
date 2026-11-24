shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, fog_disabled;

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

// Planar reflections rendered by Engine_WaterReflection (eye-matched per split-screen view) + camera-pan reprojection.
uniform sampler2D reflection_tex : hint_default_black, filter_linear, repeat_disable;
uniform bool planar_reflection = false;
uniform sampler2D reflection_tex_second : hint_default_black, filter_linear, repeat_disable;
uniform bool second_reflection = false;
uniform vec3 first_eye = vec3(0.0);
uniform vec3 second_eye = vec3(0.0);
uniform mat4 reflection_reproj = mat4(1.0);
uniform mat4 reflection_reproj_second = mat4(1.0);
uniform bool has_reflection_reproj = false;

// Interactive hero/dog/NPC wader wake impulse ring buffer (Balanced = wader0 + 2 slots, High = 4 slots, Ultra = 8 slots + wader0/1).
uniform sampler2D foam_tex : hint_default_black, filter_linear_mipmap, repeat_enable;
uniform mat4 w_ripples_a = mat4(0.0);
uniform mat4 w_ripples_b = mat4(0.0);
uniform vec4 w_wader0 = vec4(0.0);
uniform vec4 w_wader1 = vec4(0.0);
uniform vec4 w_wader0_vel = vec4(0.0);
uniform vec4 w_wader1_vel = vec4(0.0);
uniform float w_ripple_clock = 0.0;
uniform bool w_has_wakes = false;
uniform vec4 w_celestial = vec4(1.0, 0.034, 0.0, 0.034);
uniform vec3 w_celestial_dir = vec3(0.0, 1.0, 0.0);

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
global uniform float fable_emission_boost = 1.0;
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
#ifdef FABLE_WATER_ZENITH_VERTEX
varying flat vec3 v_zenith;
#endif

float body_edge(vec2 p) {
	if (!has_body_bounds) {
		return 1.0;
	}
	float d = min(min(p.x - w_body_bounds.x, p.y - w_body_bounds.y),
		min(w_body_bounds.z - p.x, w_body_bounds.w - p.y));
	return clamp(d / 1.5, 0.0, 1.0);
}

float body_edge_sea(vec2 p) {
	if (!has_body_bounds) {
		return 1.0;
	}
	float d = min(min(p.x - w_body_bounds.x, p.y - w_body_bounds.y),
		min(w_body_bounds.z - p.x, w_body_bounds.w - p.y));
	return clamp(d / 3.5, 0.0, 1.0);
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
#ifdef FABLE_WATER_ZENITH_VERTEX
	v_zenith = has_dome ? fable_sky_overlaid(vec3(0.0, 1.0, 0.0), fable_sky_inscatter(
		vec3(0.0, 0.0, 1.0), atmos_sun, atmos_inscatter, beta_rayleigh, beta_mie, horizon_distance)) : sky_constant;
#endif

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
		}
		if (!ocean_mode && water_quality_tier >= 2) {
			vec2 swell_slope = vec2(0.0);
			float swell_h = 0.0;
			float tier_amp = water_quality_tier >= 3 ? 1.0 : 0.65;
			add_swell_wave(pos.xz, TIME * 0.90, vec2(0.940, 0.342), 15.4, 0.036 * tier_amp, swell_slope, swell_h);
			add_swell_wave(pos.xz, TIME * 0.90, vec2(-0.515, 0.857), 9.8, 0.024 * tier_amp, swell_slope, swell_h);
			float shore_att = clamp(dt * 0.55, 0.0, 1.0) * body_edge(pos.xz);
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

vec2 hash22_water(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.xx + p3.yz) * p3.zy);
}

// Dual-scrolled channel-packed RGBA foam & spherical-bubble evaluator with selective bubble-pop erosion:
//   foam_tex.r = billowy macro froth, .g = clustered round bubbles, .b = fine fizzy micro-bubbles, .a = turbulent erosion.
float eval_packed_foam(vec2 world_xz, vec2 warp, vec2 dx, vec2 dy, float t, float energy, bool single_tap) {
	float e = clamp(energy, 0.0, 1.0);
	if (e <= 0.02) {
		return 0.0;
	}
	vec2 uv0 = (world_xz * 0.44) + (warp * 0.24) + vec2(t * 0.018, -t * 0.014);
	vec4 f0 = textureGrad(foam_tex, uv0, dx * 0.44, dy * 0.44);
	float froth;
	float bubbles;
	float fizz;
	float erosion;
	if (single_tap) {
		froth = f0.r;
		bubbles = f0.g * 0.90;
		fizz = f0.b * 0.85;
		erosion = f0.a;
	} else {
		const mat2 FOAM_ROT = mat2(vec2(0.80, 0.60), vec2(-0.60, 0.80));
		vec2 uv1 = (FOAM_ROT * world_xz * 0.93) - (warp * 0.19) + vec2(-t * 0.023, t * 0.019);
		vec4 f1 = textureGrad(foam_tex, uv1, (FOAM_ROT * dx) * 0.93, (FOAM_ROT * dy) * 0.93);
		froth = (f0.r * 0.62) + (f1.r * 0.38);
		bubbles = max(f0.g * 0.92, f1.g * 0.86);
		fizz = max(f0.b * 0.88, f1.b * 0.88);
		erosion = (f0.a * 0.58) + (f1.a * 0.42);
	}

	float eroded = clamp((e * 1.28) - ((1.0 - erosion) * 0.58), 0.0, 1.0);
	float fizz_layer = fizz * smoothstep(0.02, 0.19, eroded);
	float bubble_layer = bubbles * smoothstep(0.13, 0.44, eroded);
	float froth_layer = froth * smoothstep(0.42, 0.82, eroded);
	return clamp(max(fizz_layer * 0.74, max(bubble_layer * 0.95, froth_layer)), 0.0, 1.0)
		* smoothstep(0.02, 0.10, e);
}

// Centered 2x2 animated Voronoi F1 cusp distance for seam-free sunlit underwater caustics.
float voronoi_f1_anim(vec2 p, float t) {
	vec2 n = floor(p - 0.5);
	vec2 f = p - n;
	float f1 = 8.0;

	vec2 h00 = hash22_water(n + vec2(0.0, 0.0));
	vec2 r00 = vec2(0.5, 0.5) + (0.34 * sin((t * 1.15) + (TAU * h00))) - f;
	f1 = min(f1, dot(r00, r00));

	vec2 h10 = hash22_water(n + vec2(1.0, 0.0));
	vec2 r10 = vec2(1.5, 0.5) + (0.34 * sin((t * 1.15) + (TAU * h10))) - f;
	f1 = min(f1, dot(r10, r10));

	vec2 h01 = hash22_water(n + vec2(0.0, 1.0));
	vec2 r01 = vec2(0.5, 1.5) + (0.34 * sin((t * 1.15) + (TAU * h01))) - f;
	f1 = min(f1, dot(r01, r01));

	vec2 h11 = hash22_water(n + vec2(1.0, 1.0));
	vec2 r11 = vec2(1.5, 1.5) + (0.34 * sin((t * 1.15) + (TAU * h11))) - f;
	f1 = min(f1, dot(r11, r11));

	return sqrt(f1);
}

float eval_caustic_voronoi(vec2 p, float t) {
	const mat2 CAUST_ROT = mat2(vec2(0.80, 0.60), vec2(-0.60, 0.80));
	float c0 = voronoi_f1_anim((p * 1.15) + vec2(t * 0.08, -t * 0.06), t);
	float c1 = voronoi_f1_anim((CAUST_ROT * p * 1.87) - vec2(t * 0.07, t * 0.09), (t * 1.13) + 2.1);
	float focus = clamp((c0 * 0.68) + (c1 * 0.58), 0.0, 1.15);
	return pow(clamp(focus * 1.08, 0.0, 1.0), 4.0) * 1.75;
}

vec2 eval_wake_impulse(vec2 world_xz, vec4 imp, vec2 n_warp, float now_s) {
	if (imp.w <= 0.001) {
		return vec2(0.0);
	}
	float age = now_s - imp.z;
	if (age <= 0.0 || age > 3.8) {
		return vec2(0.0);
	}
	vec2 d = (world_xz - imp.xy) + (n_warp * 0.22);
	float r = length(d);
	float front = 0.28 + (age * 1.05);
	float dr = r - front;
	float env = exp(-age * 1.15) * exp(-(dr * dr) * 1.85) * smoothstep(0.12, 0.42, r);
	float osc = sin((dr * 5.8) - (age * 3.2));
	return (d / max(r, 1e-3)) * (osc * env * imp.w * 0.16);
}

vec2 eval_wader_ring(vec2 world_xz, vec4 wader, vec4 wader_vel, vec2 n_warp, float t) {
	if (wader.w <= 0.001) {
		return vec2(0.0);
	}
	vec2 d = (world_xz - wader.xy) + (n_warp * 0.28);
	float r = length(d);
	float env = exp(-max(r - 0.36, 0.0) * 1.45) * smoothstep(0.16, 0.48, r) * wader.z;
	float amp = mix(0.036, 0.082, clamp(wader_vel.w, 0.0, 1.0));
	return (d / max(r, 1e-3)) * (sin((r * 4.6) - (t * 2.8)) * env * amp);
}

// Breadcrumb splash remnants emitted only during active swimming/wading (imp.w > 0.30).
float eval_wake_churn(vec2 world_xz, vec4 imp, vec2 n_warp, float now_s) {
	if (imp.w <= 0.30) {
		return 0.0;
	}
	float age = now_s - imp.z;
	if (age <= 0.0 || age > 1.85) {
		return 0.0;
	}
	vec2 warped_d = (world_xz - imp.xy) + (n_warp * 0.30);
	float r = length(warped_d);
	float patch_mask = (1.0 - smoothstep(0.16, 0.54 + (age * 0.20), r))
		* (1.0 - smoothstep(0.22, 1.85, age));
	return patch_mask * clamp((imp.w - 0.30) * 1.55, 0.0, 0.88);
}

// Heading-aligned hydrodynamic V-wake + shoulder stroke froth trailing strictly behind moving swimmers/waders.
float eval_swimmer_vwake(vec2 world_xz, vec4 wader, vec4 wader_vel, vec2 warp) {
	if (wader.w <= 0.001 || wader_vel.w <= 0.04) {
		return 0.0;
	}
	float dir_len = length(wader_vel.xy);
	vec2 fwd = dir_len > 1e-3 ? (wader_vel.xy / dir_len) : vec2(0.0, 1.0);
	vec2 right = vec2(-fwd.y, fwd.x);
	vec2 rel = (world_xz - wader.xy) + (warp * 0.26);
	float along_behind = -dot(rel, fwd);
	float across = abs(dot(rel, right));

	float splash_along = smoothstep(-0.22, -0.01, along_behind) * (1.0 - smoothstep(0.18, 0.56, along_behind));
	float splash_across = 1.0 - smoothstep(0.12, 0.44, across);
	float stroke_splash = splash_along * splash_across * 0.96;

	float wake_len = 0.85 + (clamp(wader_vel.z, 0.0, 4.0) * 0.48);
	float wake_along = smoothstep(-0.06, 0.16, along_behind)
		* (1.0 - smoothstep(wake_len * 0.30, wake_len, along_behind));
	float half_w = 0.22 + (0.32 * max(along_behind, 0.0));
	float v_arm = 1.0 - smoothstep(half_w * 0.46, half_w, across);
	float center_dip = mix(1.0, 0.68 + (0.32 * smoothstep(0.0, half_w * 0.65, across)), smoothstep(0.25, 1.10, along_behind));
	float v_trail = wake_along * v_arm * center_dip;

	return max(stroke_splash, v_trail) * clamp(wader_vel.w * 1.22, 0.0, 1.0);
}

vec2 sample_wader_wakes(vec2 world_xz, vec2 n_warp, int tier, float now_s, float t) {
	vec2 g = eval_wake_impulse(world_xz, w_ripples_a[0], n_warp, now_s)
		+ eval_wake_impulse(world_xz, w_ripples_a[1], n_warp, now_s)
		+ eval_wader_ring(world_xz, w_wader0, w_wader0_vel, n_warp, t);
	if (tier >= 2) {
		g += eval_wake_impulse(world_xz, w_ripples_a[2], n_warp, now_s)
			+ eval_wake_impulse(world_xz, w_ripples_a[3], n_warp, now_s);
	}
	if (tier >= 3) {
		g += eval_wake_impulse(world_xz, w_ripples_b[0], n_warp, now_s)
			+ eval_wake_impulse(world_xz, w_ripples_b[1], n_warp, now_s)
			+ eval_wake_impulse(world_xz, w_ripples_b[2], n_warp, now_s)
			+ eval_wake_impulse(world_xz, w_ripples_b[3], n_warp, now_s)
			+ eval_wader_ring(world_xz, w_wader1, w_wader1_vel, n_warp, t);
	}
	return g;
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
float linear_depth_from_raw(float d, mat4 inv_proj) {
	if (d <= 1e-5) {
		return 1e6;
	}
	float raw_w = (inv_proj[2].w * d) + inv_proj[3].w;
	float safe_w = abs(raw_w) > 1e-6 ? raw_w : 1e-6;
	return -((inv_proj[2].z * d) + inv_proj[3].z) / safe_w;
}

float linear_depth(vec2 uv, mat4 inv_proj) {
	return linear_depth_from_raw(textureLod(depth_tex, uv, 0.0).r, inv_proj);
}

void fragment() {
	// Evaluate local & world XZ derivatives unconditionally before any control flow (DXIL rule).
	vec2 local_xz_dx = dFdx(v_local.xz);
	vec2 local_xz_dy = dFdy(v_local.xz);
	vec2 world_xz_dx = dFdx(v_world.xz);
	vec2 world_xz_dy = dFdy(v_world.xz);

	float dt = v_shore.z;
	float slope = v_shore.w;
	vec2 sdir = v_shore.xy;

	float w_roller = 0.0;
	vec2 w_wave_n = vec2(0.0);
	if (!guest_water && has_terrain_height && coastal_surf) {
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
	if (!guest_water && !ocean_mode && coastal_surf && dt > 1.5) {
		// Feather the outer 3.5 m of deep coastal WaterBody bounds so it dissolves into VistaOcean seamlessly.
		modern_alpha *= mix(1.0, body_edge_sea(v_local.xz), smoothstep(1.5, 3.0, dt));
	}

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

		if (!guest_water && water_quality_tier >= 1) {
			float swell_gain = water_quality_tier >= 3 ? 2.4 : (water_quality_tier == 2 ? 1.75 : 1.05);
			float shore_gate = (!ocean_mode && has_terrain_height) ? clamp(dt * 0.80, 0.20, 1.0) : 1.0;
			float mid_damp = shore_gate * smoothstep(520.0, 40.0, dist) * swell_gain;
			float far_damp = shore_gate * smoothstep(1800.0, 140.0, dist) * swell_gain;
			add_swell_wave(v_local.xz, TIME * 0.85, vec2(0.940, 0.342), 14.2, 0.052 * mid_damp, swell_slope, swell_h);
			add_swell_wave(v_local.xz, TIME * 0.85, vec2(-0.515, 0.857), 8.6, 0.035 * mid_damp, swell_slope, swell_h);
			add_swell_wave(v_local.xz, TIME * 0.72, vec2(0.819, -0.574), 37.0, 0.088 * far_damp, swell_slope, swell_h);
			if (water_quality_tier >= 2) {
				add_swell_wave(v_local.xz, TIME * 0.85, vec2(0.292, -0.956), 5.1, 0.020 * mid_damp, swell_slope, swell_h);
				add_swell_wave(v_local.xz, TIME * 0.85, vec2(-0.875, -0.485), 3.1, 0.011 * mid_damp, swell_slope, swell_h);
				add_swell_wave(v_local.xz, TIME * 0.72, vec2(-0.358, 0.934), 23.0, 0.058 * far_damp, swell_slope, swell_h);
			}
			w_warp = swell_slope * 1.15;
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
			if (fable_atmosphere_terms_uncompensated(VERTEX, INV_VIEW_MATRIX, fog_t, fog_c)) {
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

			vec2 wake_local = vec2(0.0);
			float wake_max_dist = water_quality_tier >= 2 ? 32.0 : 24.0;
			if (w_has_wakes && water_quality_tier >= 1 && !ocean_mode && dist < wake_max_dist) {
				vec2 wake_world = sample_wader_wakes(v_world.xz, n_detail.xy, water_quality_tier, w_ripple_clock, TIME);
				wake_local = (inverse(MODEL_MATRIX) * vec4(wake_world.x, 0.0, wake_world.y, 0.0)).xz
					* (1.0 - smoothstep(wake_max_dist * 0.5, wake_max_dist, dist));
			}

			// Derivative / Toksvig footprint damping prevents sub-pixel normal & specular fireflies under FSR,
			// while keeping macro 14-37 m analytical ocean swells undamped out into the far field.
			float uv_footprint = max(length(local_xz_dx * w_scale.x), length(local_xz_dy * w_scale.x));
			float world_footprint = max(length(local_xz_dx), length(local_xz_dy));
			float aa_damp = 1.0 / (1.0 + (uv_footprint * uv_footprint * 42.0));
			float swell_aa = 1.0 / (1.0 + (world_footprint * world_footprint * 0.06));
			float roughness = clamp(0.042 + clamp(uv_footprint * 2.8, 0.0, 0.105), 0.042, 0.147);

			// Fable's WATER_NORMAL_SCALE (w_bias.z) scales vertical Z: lower values amplify gentle normal-map slopes.
			// Decouple macro wave slopes from local wakes so reflections undulate smoothly with swells without tipping to zenith around swimmers.
			float z_scale = clamp(w_bias.z, 0.20, 2.0);
			vec2 micro_xy = (n_detail.xy / z_scale) * aa_damp;
			vec2 swell_xy = (swell_slope * swell_aa) + w_wave_n;
			vec2 ripple_xy = (rain_n + wake_local) * aa_damp;
			vec2 combined_xy = micro_xy + swell_xy + ripple_xy;
			float xy_len = length(combined_xy);
			if (xy_len > 0.85) {
				combined_xy *= 0.85 / xy_len;
			}
			vec3 Nf = normalize(vec3(combined_xy.x, n_detail.z, combined_xy.y));
			float refl_tilt = clamp(w_reflp.x * 0.34, 0.20, 0.56);
			vec2 nr_xy = (micro_xy * refl_tilt) + (swell_xy * 0.80) + (ripple_xy * 0.42);
			float nr_len = length(nr_xy);
			if (nr_len > 0.58) {
				nr_xy *= 0.58 / nr_len;
			}
			vec3 Nr = normalize(vec3(nr_xy.x, 1.0, nr_xy.y));

			float water_lin = -VERTEX.z;
			float scene_raw_d = textureLod(depth_tex, SCREEN_UV, 0.0).r;
			float scene_here = linear_depth_from_raw(scene_raw_d, INV_PROJECTION_MATRIX);
			float raw_depth_here = max(scene_here - water_lin, 0.0);
			if (!ocean_mode && scene_here >= water_lin) {
				modern_alpha *= clamp((scene_here - water_lin) / 0.022, 0.18, 1.0);
			}

			// Continuous angle-driven Fresnel: drops cleanly to ~4% at steep downward views while rising at grazing angles.
			float ndv_f = clamp(dot(V, Nf), 0.001, 1.0);
			float one_minus_ndv = 1.0 - ndv_f;
			float fres_curve = 0.04 + ((0.96 - (0.14 * roughness)) * pow(one_minus_ndv, 3.4));

			float atmos_len = length(atmos_sun);
			vec3 sun_disc_world = atmos_len > 1e-4
				? (vec3(atmos_sun.x, atmos_sun.z, atmos_sun.y) / atmos_len)
				: normalize(fable_sun_toward);

			// Underwater transmitted scene + two-path Beer-Lambert absorption + sun-gated Voronoi caustics.
			vec3 refr_transmitted = vec3(0.0);
			vec3 volume_albedo = w_deep.rgb;
			float bed_raw_d = 0.0;
			float vert_depth = 4.0;
			float flat_favor = 0.0;
			if (!ocean_mode) {
				vec2 refr_uv = SCREEN_UV;
				float refr_lin = scene_here;
				bed_raw_d = scene_raw_d;
				if (water_quality_tier >= 1 && dist < 95.0) {
					float refr_strength = clamp(w_reflp.y, 0.20, 1.5) * 0.038 *
						clamp(raw_depth_here * 1.15, 0.0, 1.25) * smoothstep(95.0, 16.0, dist);
					refr_uv = clamp(SCREEN_UV + (Nf.xz * refr_strength), vec2(0.001), vec2(0.999));
					bed_raw_d = textureLod(depth_tex, refr_uv, 0.0).r;
					refr_lin = linear_depth_from_raw(bed_raw_d, INV_PROJECTION_MATRIX);
					if (refr_lin <= water_lin || refr_lin > 9e5) {
						refr_uv = SCREEN_UV;
						refr_lin = scene_here;
						bed_raw_d = scene_raw_d;
					}
				}

				vec3 Rr = max(textureLod(screen_tex, refr_uv, 0.0).rgb, vec3(0.0));
				float depth_w = clamp(refr_lin - water_lin, 0.0, 60.0);
				vec3 sun_dir_w = normalize(vec3(sun_disc_world.x, max(sun_disc_world.y, 0.18), sun_disc_world.z));

				vec3 bed_world = v_world - vec3(0.0, depth_w, 0.0);
				vert_depth = depth_w * 0.65;
				if (bed_raw_d > 1e-5 && dist < 60.0 && depth_w < 16.0) {
					vec4 bed_h = INV_PROJECTION_MATRIX * vec4((refr_uv * 2.0) - 1.0, bed_raw_d, 1.0);
					vec3 bed_view = bed_h.xyz / (abs(bed_h.w) > 1e-6 ? bed_h.w : 1e-6);
					bed_world = (INV_VIEW_MATRIX * vec4(bed_view, 1.0)).xyz;
					vert_depth = clamp(v_world.y - bed_world.y, 0.0, 60.0);
				}
				flat_favor = clamp((vert_depth * 1.65) / max(depth_w * 0.48, 0.06), 0.0, 1.0);

				float s_down = vert_depth / max(sun_dir_w.y, 0.38);
				float optical_path = depth_w + (0.85 * s_down);
				float clarity = clamp(w_bias.w / 80.0, 0.0, 1.0);
				float turbidity = mix(1.45, 0.78, clarity);
				vec3 theme_tint = clamp(mix(w_surface.rgb, w_deep.rgb, 0.45), vec3(0.02), vec3(1.0));
				float tint_peak = max(theme_tint.r, max(theme_tint.g, theme_tint.b));
				vec3 theme_chroma = theme_tint / max(tint_peak, 0.04);
				vec3 extinction = (vec3(1.18, 0.36, 0.24) + ((vec3(1.0) - theme_chroma) * 0.72)) * turbidity;
				vec3 transmittance = exp(-optical_path * extinction);
				vec3 view_inscatter = vec3(1.0) - exp(-depth_w * extinction * 1.10);
				float deep_mix = 1.0 - exp(-vert_depth * 0.85 * turbidity);
				vec3 scatter_col = mix(w_surface.rgb, w_deep.rgb, deep_mix);
				refr_transmitted = Rr * transmittance;
				volume_albedo = scatter_col * view_inscatter;

				// Direct-sun-gated dual-octave Voronoi caustics routed through lit ALBEDO so bridges/cliffs shadow them.
				float sun_luma = dot(fable_sun_colour, vec3(0.2126, 0.7152, 0.0722));
				float caust_sun_gate = smoothstep(0.10, 0.28, sun_disc_world.y)
					* smoothstep(0.18, 0.45, sun_luma)
					* v_cloud_t;
				if (water_quality_tier >= 2 && caust_sun_gate > 0.005 && bed_raw_d > 1e-5 &&
					vert_depth > 0.05 && vert_depth < 4.2 && dist < 44.0) {
					vec3 refr_sun = refract(-sun_dir_w, vec3(0.0, 1.0, 0.0), 0.75);
					vec2 caust_base = bed_world.xz - (refr_sun.xz * (vert_depth / max(-refr_sun.y, 0.32)));
					vec3 caust_rgb;
					if (water_quality_tier >= 3) {
						vec2 disp = refr_sun.xz * (min(vert_depth, 1.2) * 0.010);
						caust_rgb = vec3(
							eval_caustic_voronoi(caust_base + disp, TIME),
							eval_caustic_voronoi(caust_base, TIME),
							eval_caustic_voronoi(caust_base - disp, TIME));
					} else {
						caust_rgb = vec3(eval_caustic_voronoi(caust_base, TIME));
					}
					float caust_env = smoothstep(0.05, 0.30, vert_depth)
						* (1.0 - smoothstep(2.0, 4.2, vert_depth))
						* smoothstep(44.0, 14.0, dist)
						* flat_favor
						* caust_sun_gate;
					vec3 sun_down_filter = exp(-s_down * extinction * 0.65);
					volume_albedo += caust_rgb * Rr * sun_down_filter * transmittance * (caust_env * 1.45);
				}
			}

			// World-space reflection direction clamped above the horizontal water plane so rays never dip into lakebed mud.
			vec3 eye_dir_w = normalize(v_world - CAMERA_POSITION_WORLD);
			vec3 V_world = -eye_dir_w;
			vec3 world_nf = normalize((MODEL_MATRIX * vec4(Nf, 0.0)).xyz);
			vec3 R_local = reflect(-V, Nr);
			vec3 R_world = (MODEL_MATRIX * vec4(R_local, 0.0)).xyz;
			R_world.y = max(R_world.y, 0.012);
			R_world = normalize(R_world);
			vec3 cel_dir_world = length(w_celestial_dir) > 1e-4
				? normalize(w_celestial_dir)
				: sun_disc_world;
			float low_horizon_fill = (w_celestial.x > 0.001)
				? (1.0 - smoothstep(0.002, 0.082, cel_dir_world.y))
				: 0.0;

			vec3 horizon_sky = sky_constant;
			if (has_dome && (dist > 55.0 || ocean_mode)) {
				vec3 horiz_dir_w = normalize(vec3(eye_dir_w.x, max(eye_dir_w.y, 0.004), eye_dir_w.z));
				horizon_sky = dome_reflect(horiz_dir_w);
			}

			float refl_str = clamp(w_deep.w, 0.0, 1.0);
			float artist_bias = clamp((w_bias.x * 0.22) + (w_bias.y * 0.30), -0.04, 0.28) * pow(one_minus_ndv, 1.4);
			float shore_clear = ocean_mode ? 1.0 : mix(0.20, 1.0, clamp(raw_depth_here * 3.0, 0.0, 1.0));
			float F = clamp((fres_curve + artist_bias) * refl_str * shore_clear, 0.0, 0.95);
			float far_grazing = smoothstep(120.0, 850.0, dist) * pow(one_minus_ndv, 2.4);
			F = mix(F, 0.98, far_grazing);

			float reproj_valid = 0.0;
			vec3 planar_col = vec3(0.0);
			if (planar_reflection) {
				bool second_view = second_reflection &&
					distance(CAMERA_POSITION_WORLD, second_eye) < distance(CAMERA_POSITION_WORLD, first_eye);
				vec3 rendered_eye = second_view ? second_eye : first_eye;
				float eye_drift = distance(CAMERA_POSITION_WORLD, rendered_eye);
				float drift_fade = 1.0 - smoothstep(0.75, 2.50, eye_drift);
				float ocean_mirror_fade = ocean_mode ? smoothstep(850.0, 220.0, dist) : 1.0;
				vec2 base_uv = SCREEN_UV;
				if (has_reflection_reproj) {
					mat4 reproj_mat = second_view ? reflection_reproj_second : reflection_reproj;
					vec4 rq = reproj_mat * vec4(v_world + (eye_dir_w * 60.0), 1.0);
					if (rq.w > 0.05) {
						base_uv = rq.xy / rq.w;
					}
				}
				float uv_dist_damp = mix(1.0, 0.28, smoothstep(16.0, 180.0, dist));
				vec2 raw_planar_uv = base_uv + (Nr.xz * (mix(0.08, 0.18, clamp(w_reflp.x, 0.0, 1.2)) * uv_dist_damp));
				vec2 ob = min(raw_planar_uv, vec2(1.0) - raw_planar_uv);
				reproj_valid = (has_reflection_reproj
					? smoothstep(0.004, 0.042, min(ob.x, ob.y))
					: 0.0) * drift_fade * ocean_mirror_fade;
				vec2 planar_uv = clamp(vec2(1.0 - raw_planar_uv.x, raw_planar_uv.y), vec2(0.001), vec2(0.999));
				planar_col = max(second_view
					? textureLod(reflection_tex_second, planar_uv, 0.0).rgb
					: textureLod(reflection_tex, planar_uv, 0.0).rgb, vec3(0.0));
			}

			vec3 sky_refl = sky_constant;
			if (has_dome) {
				if (ocean_mode && dist >= 6800.0) {
					sky_refl = horizon_sky;
				} else if (reproj_valid >= 0.995 && low_horizon_fill <= 0.002) {
					sky_refl = planar_col;
				} else {
					sky_refl = dome_reflect(R_world);
					if (ocean_mode && dist > 2200.0) {
						sky_refl = mix(sky_refl, horizon_sky, smoothstep(2200.0, 6800.0, dist));
					}
				}
			}
			vec3 refl_col = planar_reflection ? mix(sky_refl, planar_col, reproj_valid) : sky_refl;

			// Perspective SSR with front-to-back surface crossing, 4-step binary refinement,
			// and 3D world-Y waterline validation so underwater mud/limbs never reflect as dark islands.
			// On Balanced, skip per-pixel SSR wherever the planar mirror already covers the water.
			bool planar_covered = planar_reflection && (reproj_valid > 0.65);
			float ssr_planar_dist = water_quality_tier >= 3 ? 52.0 : (water_quality_tier == 2 ? 36.0 : 0.0);
			float ssr_open_dist = water_quality_tier >= 2 ? 150.0 : 110.0;
			float ssr_max_dist = planar_covered ? ssr_planar_dist : ssr_open_dist;
			if (water_quality_tier >= 1 && F > 0.038 && dist < ssr_max_dist) {
				vec3 ro = VERTEX;
				vec3 rd = normalize((VIEW_MATRIX * vec4(R_world, 0.0)).xyz);
				float forward_gate = smoothstep(0.12, -0.15, rd.z);
				if (forward_gate > 0.01) {
					int max_steps = water_quality_tier >= 3 ? 24 : (water_quality_tier == 2 ? 18 : 12);
					float t_start = water_quality_tier >= 3 ? 0.22 : (water_quality_tier == 2 ? 0.26 : 0.30);
					float step_growth = water_quality_tier >= 3 ? 1.31 : (water_quality_tier == 2 ? 1.40 : 1.60);
					float ssr_max_ray = planar_covered ? 42.0 : 125.0;

					vec4 clip_ro = PROJECTION_MATRIX * vec4(ro, 1.0);
					vec4 clip_rd = PROJECTION_MATRIX * vec4(rd, 0.0);

					float jitter = fable_bayer_threshold(FRAGCOORD.xy);
					float t_prev = 0.06;
					float t_ray = t_start * (0.88 + (0.24 * jitter));
					bool prev_in_front = true;
					bool found = false;
					float hit_lo = 0.0;
					float hit_hi = 0.0;
					vec2 hit_uv = vec2(0.0);

					const int MAX_SSR = 24;
					for (int i = 0; i < MAX_SSR; i++) {
						if (i >= max_steps || t_ray > ssr_max_ray) {
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
						float diff = scene - ray;
						float step_span = t_ray - t_prev;
						float coarse_thick = max(step_span * 1.25, 0.35 + (t_ray * 0.05));
						bool foreground_occluder = (rd.z <= 0.0) && (scene < (water_lin - 0.15));

						if (foreground_occluder) {
							prev_in_front = false;
						} else if (prev_in_front && scene < 9e5 && scene > 0.20 && diff <= 0.0 && (-diff) <= coarse_thick) {
							found = true;
							hit_lo = t_prev;
							hit_hi = t_ray;
							hit_uv = uv;
							break;
						} else {
							prev_in_front = (diff > -0.02);
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
							if (scene_m < 9e5 && scene_m > 0.20 && scene_m < ray_m && !(rd.z <= 0.0 && scene_m < (water_lin - 0.15))) {
								hit_hi = mid;
								hit_uv = uv_m;
								ray_best = ray_m;
								scene_best = scene_m;
							} else {
								hit_lo = mid;
							}
						}

						float hit_raw_d = textureLod(depth_tex, hit_uv, 0.0).r;
						float above_water_gate = 0.0;
						if (hit_raw_d > 1e-5) {
							vec4 hit_h = INV_PROJECTION_MATRIX * vec4((hit_uv * 2.0) - 1.0, hit_raw_d, 1.0);
							vec3 hit_view = hit_h.xyz / (abs(hit_h.w) > 1e-6 ? hit_h.w : 1e-6);
							vec3 hit_world = (INV_VIEW_MATRIX * vec4(hit_view, 1.0)).xyz;
							above_water_gate = smoothstep(0.02, 0.10, hit_world.y - v_world.y);
						}

						vec2 b = min(hit_uv, 1.0 - hit_uv);
						float edge_w = planar_covered ? 0.095 : 0.030;
						float edge = smoothstep(0.0, edge_w, b.x) * smoothstep(0.0, max(edge_w, 0.04), hit_uv.y)
							* smoothstep(0.0, edge_w, 1.0 - hit_uv.y);
						float phys_thickness = (planar_covered ? 0.24 : 0.40) + (hit_hi * (planar_covered ? 0.038 : 0.06));
						float gap = max(ray_best - scene_best, 0.0);
						float conf = 1.0 - smoothstep(phys_thickness * 0.40, phys_thickness, gap);
						float hit_weight = edge * conf * forward_gate * above_water_gate
							* smoothstep(ssr_max_dist, ssr_max_dist * 0.62, dist);
						if (hit_weight > 0.004) {
							vec3 ssr_col = max(textureLod(screen_tex, hit_uv, roughness * 2.0).rgb, vec3(0.0));
							refl_col = mix(refl_col, ssr_col, hit_weight);
						}
					}
				}
			}

			vec3 lit_emissive = (refr_transmitted * (1.0 - F)) + (refl_col * F);
			vec3 lit_albedo = volume_albedo * (1.0 - F);

			// Physically scoped channel-packed seafoam & spherical bubbles (zero open-water swell bands,
			// wave-modulated pier/hull bubble clusters, beach surf froth, and swimmer V-wake + idle collar).
			float foam_cov = 0.0;
			if (!ocean_mode && dist < 88.0) {
				vec2 foam_warp = (t0.xy * 0.45) + (t1.xy * 0.30) + (swell_slope * 0.55) + (w_wave_n * 0.50);
				float foam_energy = 0.0;

				// 1. Beach & shallow obstacle lapping: rich froth on gentle beaches, wave-modulated bubble pockets on pilings/hulls.
				if (bed_raw_d > 1e-5 && dist < 58.0) {
					float wave_lap = clamp(0.62 + (t0.x * 0.34) + (t1.y * 0.28) + (swell_h * 4.5), 0.28, 1.08);
					float slosh = clamp((swell_h * 6.0) + (t0.x * 0.28) + (t1.y * 0.22), -0.04, 0.08) * mix(0.35, 1.0, flat_favor);
					float band_max = clamp(mix(0.135, 0.38, flat_favor) + slosh, 0.075, 0.44);
					float contact_env = smoothstep(0.001, 0.016, vert_depth)
						* (1.0 - smoothstep(band_max * 0.38, band_max, vert_depth));
					float max_contact = mix(0.50 * wave_lap, 0.90, flat_favor);
					foam_energy = max(foam_energy, contact_env * max_contact * smoothstep(58.0, 22.0, dist));
				}

				// 2. Coastal breaking surf rollers strictly on gently sloped sandy beaches.
				if (coastal_surf && w_roller > 0.04) {
					foam_energy = max(foam_energy, clamp(w_roller * 0.98, 0.0, 0.95) * smoothstep(88.0, 36.0, dist));
				}

				// 3. Hydrodynamic heading-aligned swimmer V-wake & stroke breadcrumbs strictly when moving.
				if (w_has_wakes && water_quality_tier >= 1 && dist < wake_max_dist) {
					vec2 wake_warp = (t0.xy * 0.52) + (t1.xy * 0.38) + (swell_slope * 0.65);
					float swim_e = eval_swimmer_vwake(v_world.xz, w_wader0, w_wader0_vel, wake_warp)
						+ eval_wake_churn(v_world.xz, w_ripples_a[0], wake_warp, w_ripple_clock)
						+ eval_wake_churn(v_world.xz, w_ripples_a[1], wake_warp, w_ripple_clock);
					if (water_quality_tier >= 2) {
						swim_e += eval_wake_churn(v_world.xz, w_ripples_a[2], wake_warp, w_ripple_clock)
							+ eval_wake_churn(v_world.xz, w_ripples_a[3], wake_warp, w_ripple_clock);
					}
					if (water_quality_tier >= 3) {
						swim_e = max(swim_e, eval_swimmer_vwake(v_world.xz, w_wader1, w_wader1_vel, wake_warp));
						swim_e += eval_wake_churn(v_world.xz, w_ripples_b[0], wake_warp, w_ripple_clock)
							+ eval_wake_churn(v_world.xz, w_ripples_b[1], wake_warp, w_ripple_clock)
							+ eval_wake_churn(v_world.xz, w_ripples_b[2], wake_warp, w_ripple_clock)
							+ eval_wake_churn(v_world.xz, w_ripples_b[3], wake_warp, w_ripple_clock);
					}
					foam_energy = max(foam_energy, clamp(swim_e, 0.0, 0.94) * smoothstep(wake_max_dist, wake_max_dist * 0.45, dist));
				}

				if (foam_energy > 0.02) {
					bool single_foam_tap = (water_quality_tier <= 1) && (dist > 18.0);
					foam_cov = eval_packed_foam(v_world.xz, foam_warp, world_xz_dx, world_xz_dy, TIME, foam_energy, single_foam_tap) * 0.94;
					if (foam_cov > 0.002) {
#ifdef FABLE_WATER_ZENITH_VERTEX
						vec3 zen = v_zenith;
#else
						vec3 zen = has_dome ? dome_reflect(vec3(0.0, 1.0, 0.0)) : sky_constant;
#endif
						// Route foam primarily through shadow-mapped PBR ALBEDO while retaining realistic bubble
						// internal sky-zenith multiple scattering so dusk/night foam stays legible without glowing.
						vec3 foam_albedo = mix(w_surface.rgb, vec3(0.82, 0.85, 0.87), 0.88);
						lit_albedo = mix(lit_albedo, foam_albedo, foam_cov * 0.92);
						lit_emissive = (lit_emissive * (1.0 - (foam_cov * 0.72)))
							+ (foam_albedo * zen * (0.15 * foam_cov));
						roughness = mix(roughness, 0.76, foam_cov);
					}
				}
			}

			// Horizon-limb handover wave sparkles (pre_horizon_glint_hdr): active strictly while the Sun or Moon
			// is cresting/setting across HorizonFade (cel_dir_world.y < 0.082) before DirectionalLight3D reaches full
			// strength. Uses tight, non-expanding wave-facet thresholds + physical wave-crest incidence (eff_ndl > 0)
			// so rising/setting half-discs produce crisp scattered sparkles with zero reverse cone or solid beam.
			float hdr_headroom = 1.0 + (max(fable_emission_boost - 1.0, 0.0) * 2.6666667);
			float cel_strength = max(w_celestial.x, 0.0);
			vec3 pre_horizon_glint_hdr = vec3(0.0);
			if (low_horizon_fill > 0.002 && cel_strength > 0.001) {
				vec3 eff_cel_dir = normalize(vec3(cel_dir_world.x, max(cel_dir_world.y, 0.016), cel_dir_world.z));
				float eff_ndl = clamp(dot(world_nf, eff_cel_dir), 0.0, 1.0);
				if (eff_ndl > 0.002) {
					vec3 H_cel = normalize(eff_cel_dir + V_world);
					vec2 cel_horiz = eff_cel_dir.xz;
					float cel_horiz_len = length(cel_horiz);
					vec2 view_horiz = eye_dir_w.xz;
					float view_horiz_len = length(view_horiz);
					vec2 t_along = cel_horiz_len > 1e-4
						? (cel_horiz / cel_horiz_len)
						: (view_horiz_len > 1e-4 ? (view_horiz / view_horiz_len) : vec2(0.0, 1.0));
					vec2 t_side = vec2(-t_along.y, t_along.x);
					vec2 s_req = H_cel.xz / max(H_cel.y, 0.024);
					vec2 s_wave = world_nf.xz / max(world_nf.y, 0.25);
					vec2 ds_cel = s_req - s_wave;
					float ds_side = dot(ds_cel, t_side);
					float ds_along = dot(ds_cel, t_along);

					float sig_side = 0.019;
					float sig_along = 0.032;
					float q = 0.5 * (((ds_side * ds_side) / (sig_side * sig_side))
						+ ((ds_along * ds_along) / (sig_along * sig_along)));
					if (q < 8.5) {
						float sky_luma = dot(sky_refl, vec3(0.2126, 0.7152, 0.0722));
						float planar_luma = dot(planar_col, vec3(0.2126, 0.7152, 0.0722));
						float planar_sky_open = planar_reflection
							? mix(1.0, smoothstep(0.32, 0.72, (planar_luma + 0.004) / (sky_luma + 0.004)), reproj_valid)
							: 1.0;
						if (planar_sky_open > 0.002) {
							bool is_moon = w_celestial.z > 0.5;
							float q_env = exp(-q) * smoothstep(8.5, 4.5, q);
							float crest_gain = smoothstep(0.005, 0.14, eff_ndl);
							float f_glitter = mix(0.55, 0.95, pow(one_minus_ndv, 2.2)) * (1.0 - (foam_cov * 0.78));
							vec3 refl_chroma = sky_luma > 0.04 ? (sky_refl / sky_luma) : vec3(1.0, 0.88, 0.68);
							vec3 glint_tint = is_moon
								? (sky_luma > 0.025
									? mix(clamp(refl_chroma, vec3(0.55), vec3(1.45)), vec3(0.90, 0.94, 1.0), 0.72)
									: vec3(0.90, 0.94, 1.0))
								: mix(clamp(refl_chroma, vec3(0.25), vec3(2.4)), vec3(1.0, 0.92, 0.78), 0.45);
							float hdr_glint_gain = mix(1.0, hdr_headroom, is_moon ? 0.65 : 0.82);
							float base_peak = is_moon ? 5.2 : 9.0;
							pre_horizon_glint_hdr = glint_tint
								* (q_env * crest_gain * base_peak * hdr_glint_gain * f_glitter
								* cel_strength * low_horizon_fill * planar_sky_open * mix(0.25, 1.0, v_cloud_t));
						}
					}
				}
			}

			vec3 fog_t = vec3(1.0);
			vec3 fog_c = vec3(0.0);
			if (fable_atmosphere_terms_uncompensated(VERTEX, INV_VIEW_MATRIX, fog_t, fog_c)) {
				if (has_dome && (dist > 55.0 || ocean_mode)) {
					float sky_fog_match = ocean_mode ? 1.0 : smoothstep(55.0, 260.0, dist);
					fog_c = mix(fog_c, horizon_sky, sky_fog_match);
				}
				fable_atmosphere_apply(fog_t, fog_c, 1.0, lit_albedo, lit_emissive);
			}
			lit_emissive += pre_horizon_glint_hdr * exp(-dist * 0.0014);
			if (ocean_mode && dist > 2200.0) {
				lit_albedo *= 1.0 - smoothstep(2200.0, 6800.0, dist);
			}

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

// Forward+ water lighting: directional sun/moon shadows + HDR solar/lunar shimmer + wave SSS + placed/FX lamps.
// Guarded with `if` rather than `return;` per Godot's light processor rule.
void light() {
	if (!guest_water && ALPHA > 0.002) {
		float cloud_gate = LIGHT_IS_DIRECTIONAL ? v_cloud_t : 1.0;
		vec3 radiance = (LIGHT_COLOR * (1.0 / PI)) * ATTENUATION * cloud_gate;
		float ndl = dot(NORMAL, LIGHT);

		// Subsurface wrap diffuse illuminates ALBEDO water volume, aerated foam, & shallow sun caustics
		// while dropping cleanly into shadow behind pillars, cliffs, bridges, and trees.
		float wrap_diff = clamp((ndl + 0.35) / 1.35, 0.0, 1.0);
		DIFFUSE_LIGHT += radiance * wrap_diff;

		// Subsurface wave-crest scattering (SSS) gives backlit swells a translucent liquid interior.
		if (LIGHT_IS_DIRECTIONAL && water_quality_tier >= 2) {
			vec3 up_view = normalize(VIEW_MATRIX[1].xyz);
			float cos_up = clamp(dot(NORMAL, up_view), -1.0, 1.0);
			float wave_sin = sqrt(max(1.0 - (cos_up * cos_up), 0.0));
			vec3 back_dir = normalize(LIGHT + (NORMAL * 0.55));
			float sss_phase = 0.20 + (0.80 * pow(clamp(dot(VIEW, -back_dir), 0.0, 1.0), 2.6));
			float sss_crest = smoothstep(0.06, 0.36, wave_sin) * clamp(ndl + 0.55, 0.0, 1.0);
			vec3 sss_col = mix(w_deep.rgb, w_surface.rgb, 0.82) * 2.2;
			SPECULAR_LIGHT += radiance * sss_col * (sss_phase * sss_crest * 0.65) * BACKLIGHT;
		}

		// Unified single-cone HDR solar & lunar wave-facet glitter + finite-bulb harbor/ship lamps.
		float min_spec_ndl = LIGHT_IS_DIRECTIONAL ? 0.001 : -0.15;
		if (ndl > min_spec_ndl && SPECULAR_AMOUNT > 0.0) {
			vec3 R_v = reflect(-VIEW, NORMAL);
			float is_moon_l = LIGHT_IS_DIRECTIONAL ? clamp(w_celestial.z, 0.0, 1.0) : 0.0;
			float disc_sin = LIGHT_IS_DIRECTIONAL ? clamp(w_celestial.w, 0.008, 0.040) : 0.065;
			vec3 center_to_ray = R_v - (dot(R_v, LIGHT) * LIGHT);
			float ctr_len = length(center_to_ray);
			vec3 L_rep = normalize(LIGHT + (center_to_ray * clamp(disc_sin / max(ctr_len, 1e-4), 0.0, 1.0)));

			vec3 H = normalize(L_rep + VIEW);
			float ndh = clamp(dot(NORMAL, H), 0.0, 1.0);
			float ndv_l = clamp(dot(NORMAL, VIEW), 0.001, 1.0);
			float ndl_spec = LIGHT_IS_DIRECTIONAL
				? clamp(ndl, 0.0, 1.0)
				: clamp((ndl * 0.85) + 0.15, 0.001, 1.0);
			float vdh = clamp(dot(VIEW, H), 0.0, 1.0);

			float a_sharp = clamp(ROUGHNESS * 0.80, 0.042, 0.36);
			float a2_s = a_sharp * a_sharp;
			float denom_s = (ndh * ndh * (a2_s - 1.0)) + 1.0;
			float D_s = a2_s / max(PI * denom_s * denom_s, 1e-5);
			float gv_s = ndl_spec * sqrt((ndv_l * ndv_l * (1.0 - a2_s)) + a2_s);
			float gl_s = ndv_l * sqrt((ndl_spec * ndl_spec * (1.0 - a2_s)) + a2_s);
			float Vis_s = min(0.5 / max(gv_s + gl_s, 0.18), 1.45);

			float spec_brdf;
			vec3 spec_fog = BACKLIGHT;
			if (LIGHT_IS_DIRECTIONAL) {
				float hdr_headroom_l = 1.0 + (max(fable_emission_boost - 1.0, 0.0) * 2.6666667);
				float max_lc = max(LIGHT_COLOR.r, max(LIGHT_COLOR.g, LIGHT_COLOR.b));
				float cel_boost = max(smoothstep(0.14, 0.95, max_lc), clamp(w_celestial.x, 0.0, 1.0) * 0.88);
				float hdr_cel_gain = mix(1.0, hdr_headroom_l, mix(0.62, 0.72, is_moon_l) * cel_boost);
				float f_solar = mix(
					0.04 + (0.96 * pow(1.0 - vdh, 5.0)),
					0.55 + (0.45 * pow(1.0 - vdh, 3.0)),
					cel_boost);
				float cap_sharp = mix(18.0, 72.0 * hdr_cel_gain, cel_boost);
				float glit_scale = clamp(w_reflp.w * mix(0.20, 0.44, cel_boost), 0.40, 2.80) * mix(1.0, hdr_cel_gain, 0.48);

				vec3 H_raw = normalize(LIGHT + VIEW);
				float ndh_g = clamp(dot(NORMAL, H_raw), 0.0, 1.0);
				float a_glint = clamp((ROUGHNESS * 1.15) + 0.065, 0.08, 0.24);
				float a2_g = a_glint * a_glint;
				float denom_g = (ndh_g * ndh_g * (a2_g - 1.0)) + 1.0;
				float D_g = a2_g / max(PI * denom_g * denom_g, 1e-5);

				spec_brdf = ((min(D_s * Vis_s, cap_sharp) * glit_scale)
					+ (D_g * cel_boost * hdr_cel_gain * clamp(w_reflp.w * 0.12, 0.22, 0.90))) * f_solar;
				float dist_view = distance(v_local, v_eye_local);
				vec3 cel_fog_floor = vec3(exp(-dist_view * 0.00065));
				spec_fog = mix(BACKLIGHT, max(sqrt(max(BACKLIGHT, vec3(0.0))), cel_fog_floor), 0.78 * cel_boost);
			} else {
				float f_lamp = 0.26 + (0.74 * pow(1.0 - vdh, 3.5));
				float a_lamp = clamp((ROUGHNESS * 1.8) + 0.12, 0.14, 0.45);
				float a2_l = a_lamp * a_lamp;
				float denom_l = (ndh * ndh * (a2_l - 1.0)) + 1.0;
				float D_l = a2_l / max(PI * denom_l * denom_l, 1e-5);
				spec_brdf = ((min(D_s * Vis_s, 44.0) * 1.45) + (D_l * 0.95)) * f_lamp;
			}

			SPECULAR_LIGHT += radiance * (spec_brdf * ndl_spec * SPECULAR_AMOUNT) * spec_fog;
		}
	}
}
