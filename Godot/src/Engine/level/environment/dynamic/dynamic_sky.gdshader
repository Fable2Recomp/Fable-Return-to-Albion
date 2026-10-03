shader_type sky;
render_mode use_quarter_res_pass, disable_fog;

#include "res://src/Engine/level/environment/sky_dome.gdshaderinc"

uniform sampler2D in_scatter_lut : filter_linear, repeat_disable;
uniform vec3 beta_rayleigh = vec3(0.001, 0.0015, 0.004);
uniform vec3 beta_mie = vec3(0.0056, 0.0064, 0.0105);
uniform vec3 sun_engine = vec3(1.0, 0.0, 0.0);
uniform float horizon_distance = 1900.0;
uniform bool cubemap_fable_dome = true;

global uniform float dynamic_sky_time;
global uniform vec4 dynamic_sky_lightning;
uniform vec3 moon_dir_world = vec3(-0.6, 0.3, -0.5);
uniform float moon_phase_offset = 0.0;

uniform vec3 colour_zenith : source_color = vec3(0.42, 0.56, 0.76);
uniform vec3 colour_horizon : source_color = vec3(0.78, 0.72, 0.64);
uniform vec3 colour_sunset : source_color = vec3(1.0, 0.52, 0.30);
uniform vec3 colour_night : source_color = vec3(0.045, 0.09, 0.22);
uniform vec3 colour_ground : source_color = vec3(0.30, 0.29, 0.28);
uniform float exposure = 1.0;
uniform float haze = 0.40;
uniform float dream = 0.60;

uniform vec3 sun_colour : source_color = vec3(1.0, 0.94, 0.82);
uniform float sun_intensity = 1.0;
uniform float sun_size = 0.025;
uniform float sun_glow = 1.0;

uniform vec3 moon_colour : source_color = vec3(0.86, 0.90, 1.0);
uniform float moon_brightness = 1.0;
uniform float moon_size = 0.035;
uniform bool moon_use_texture = false;
uniform sampler2D moon_texture : filter_linear, repeat_enable;

uniform float star_brightness = 1.0;
uniform float star_density = 0.35;
uniform bool star_use_texture = false;
uniform sampler2D star_texture : filter_linear_mipmap, repeat_enable;
uniform float star_rotation = 0.0;

uniform sampler2D cloud_shape : filter_linear_mipmap, repeat_enable;
uniform sampler2D cloud_detail : filter_linear_mipmap, repeat_enable;
uniform float cloud_coverage = 0.45;
uniform float cloud_density = 0.70;
uniform float cloud_softness = 0.35;
uniform float cloud_scale = 1.0;
uniform float cloud_height = 0.12;
uniform float cloud_speed = 1.0;
uniform vec2 wind_dir = vec2(1.0, 0.25);
uniform float cirrus_amount = 0.35;
uniform vec3 cloud_colour : source_color = vec3(1.0, 0.98, 0.96);
uniform vec3 cloud_shadow_colour : source_color = vec3(0.62, 0.64, 0.72);

uniform float overcast = 0.0;
uniform float storm = 0.0;
uniform float snow = 0.0;
uniform float fog_haze = 0.0;

const vec3 LUMA = vec3(0.299, 0.587, 0.114);

vec3 sun_world() {
	return normalize(vec3(sun_engine.x, sun_engine.z, sun_engine.y));
}

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

vec2 hash22(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.xx + p3.yz) * p3.zy);
}

float daylight(vec3 sun) {
	return smoothstep(-0.14, 0.10, sun.y);
}

float sun_low(vec3 sun) {
	return (1.0 - smoothstep(0.02, 0.40, sun.y)) * smoothstep(-0.30, -0.06, sun.y);
}

float horizon_band(float up, float width) {
	return exp(-max(up, 0.0) / max(width, 0.02));
}

vec3 sky_gradient(vec3 dir, vec3 sun, float day) {
	float up = dir.y;
	float band = horizon_band(up, haze * 0.5 + fog_haze * 0.6);
	vec3 col = mix(colour_zenith, colour_horizon, band);

	vec2 flat_dir = normalize(dir.xz + vec2(1e-4, 0.0));
	vec2 flat_sun = normalize(sun.xz + vec2(1e-4, 0.0));
	float toward = dot(flat_dir, flat_sun) * 0.5 + 0.5;
	float warm = sun_low(sun) * band * (toward * toward);
	col = mix(col, colour_sunset, warm);

	// THE NIGHT IS THE FABLE DOME'S — the same in-scatter the cubemap pass hands the GI and the
	// ambient, so the sky on screen is the sky that lights the world. `colour_night` stands only
	// where there is no dome (the preview scene).
	vec3 night = cubemap_fable_dome
		? fable_sky_inscatter(vec3(dir.x, dir.z, dir.y), sun_engine, in_scatter_lut,
			beta_rayleigh, beta_mie, horizon_distance)
		: colour_night * (1.0 + band * 0.9);
	col = mix(night, col, day);

	float grey = mix(1.0, 0.72, overcast) * mix(1.0, 0.80, storm);
	col = mix(col * grey, vec3(dot(col, LUMA)) * grey, overcast * 0.55 + snow * 0.25);
	col = mix(col, col * vec3(0.94, 0.97, 1.04), snow);

	col = mix(col, colour_ground * mix(night, vec3(1.0), day) * 0.9,
		1.0 - smoothstep(-0.12, 0.0, up));

	col += dream * band * colour_horizon * 0.45 * day * (1.0 - overcast * 0.7);
	col = mix(col, vec3(dot(col, LUMA)), dream * 0.12);
	return col;
}

vec3 sun_terms(vec3 dir, vec3 sun, float day, float cloud_cover) {
	float cos_s = dot(dir, sun);
	float low = sun_low(sun);
	vec3 tint = mix(sun_colour, colour_sunset, low * 0.8);
	float clear = 1.0 - cloud_cover;

	float g = max(cos_s, 0.0);
	float g2 = g * g;
	float g4 = g2 * g2;
	float g8 = g4 * g4;
	float g32 = g8 * g8 * g8 * g8;
	float halo = (g8 * 0.22 + g32 * 0.45) * sun_glow * (1.0 + dream * 0.8);
	halo *= mix(1.0, 0.35, overcast);

	float disc = smoothstep(cos(sun_size * 1.35), cos(sun_size), cos_s);
	float above = smoothstep(-sun_size, 0.0, sun.y);
	disc *= above;

	return tint * (halo * day * sun_intensity + disc * clear * sun_intensity * 3.0 * above);
}

vec3 moon_terms(vec3 dir, vec3 moon, vec3 sun, float day) {
	float cos_m = dot(dir, moon);
	float night = 1.0 - day;
	if (moon.y < -moon_size || night <= 0.001) {
		return vec3(0.0);
	}

	float g = max(cos_m, 0.0);
	float g4 = g * g * g * g;
	float g16 = g4 * g4 * g4 * g4;
	float glow = g16 * g16 * 0.10 * moon_brightness;

	vec3 col = moon_colour * glow;

	float edge = cos(moon_size);
	if (cos_m > edge * 0.999) {
		vec3 right = normalize(cross(vec3(0.0, 1.0, 0.0), moon));
		vec3 up = cross(moon, right);
		vec3 p = dir - moon * cos_m;
		vec2 local = vec2(dot(p, right), dot(p, up)) / sin(moon_size);
		float r2 = dot(local, local);
		float inside = 1.0 - smoothstep(0.92, 1.0, r2);
		if (inside > 0.0) {
			float z = sqrt(max(1.0 - r2, 0.0));
			vec3 n = right * local.x + up * local.y + moon * z;
			vec3 lit_dir = normalize(sun - moon * moon_phase_offset);
			float lambert = max(dot(n, lit_dir), 0.0);
			float earthshine = 0.04;
			vec3 albedo = vec3(1.0);
			if (moon_use_texture) {
				vec2 uv = vec2(atan(local.x, z) / TAU + 0.5, acos(clamp(local.y, -1.0, 1.0)) / PI);
				albedo = texture(moon_texture, uv).rgb * 1.6;
			}
			float face = (lambert + earthshine) * moon_brightness * 0.9;
			col = mix(col, moon_colour * albedo * face, inside);
		}
	}

	return col * night;
}

vec3 star_terms(vec3 dir, float day, float cloud_cover) {
	float night = 1.0 - day;
	if (night <= 0.001 || dir.y < -0.05) {
		return vec3(0.0);
	}

	float fade = night * (1.0 - cloud_cover) * star_brightness * smoothstep(-0.05, 0.15, dir.y);

	if (star_use_texture) {
		float c = cos(star_rotation);
		float s = sin(star_rotation);
		vec3 d = vec3(dir.x * c - dir.z * s, dir.y, dir.x * s + dir.z * c);
		vec2 uv = vec2(atan(d.x, -d.z) / TAU + 0.5, acos(clamp(d.y, -1.0, 1.0)) / PI);
		return texture(star_texture, uv).rgb * fade * 0.8;
	}

	vec2 sph = vec2(atan(dir.z, dir.x), asin(clamp(dir.y, -1.0, 1.0)));
	vec2 grid = sph * vec2(70.0, 70.0);
	vec2 cell = floor(grid);
	vec2 f = fract(grid);
	vec2 h = hash22(cell);
	float present = step(1.0 - star_density, hash12(cell + 7.1));
	vec2 centre = 0.2 + h * 0.6;
	float d = length(f - centre);
	float twinkle = 0.65 + 0.35 * sin(dynamic_sky_time * (2.0 + h.x * 4.0) + h.y * 6.283);
	float star = present * (1.0 - smoothstep(0.0, 0.07 + h.y * 0.05, d)) * twinkle;
	vec3 tint = mix(vec3(0.85, 0.9, 1.0), vec3(1.0, 0.92, 0.80), h.x);
	return tint * star * (0.35 + h.y * 0.65) * fade;
}

vec4 cloud_layers(vec3 dir, vec3 sun, vec3 moon, float day) {
	if (dir.y <= 0.0) {
		return vec4(0.0);
	}

	float cover = clamp(cloud_coverage + overcast * 0.6 + storm * 0.3, 0.0, 1.0);
	float dens = clamp(cloud_density + overcast * 0.35 + storm * 0.3, 0.0, 1.0);
	float soft = mix(cloud_softness, 0.12, overcast);

	float horizon_fade = smoothstep(0.0, 0.16, dir.y);
	vec2 drift = wind_dir * (dynamic_sky_time * cloud_speed * 0.004);

	vec2 p = dir.xz / (dir.y + 0.10) * cloud_height * cloud_scale;
	vec2 uv = p + drift;
	float shape = texture(cloud_shape, uv).r;
	float detail = texture(cloud_detail, uv * 2.7 + drift * 1.6).r;
	float n = shape - (1.0 - detail) * 0.22 * (1.0 - overcast * 0.5);
	float t0 = 1.0 - cover;
	float a = smoothstep(t0, t0 + soft, n) * dens;

	vec2 sun_flat = sun.xz;
	float sun_len = length(sun_flat);
	vec2 to_sun = sun_len > 1e-3 ? sun_flat / sun_len : vec2(0.0);
	float n_sun = texture(cloud_shape, uv + to_sun * 0.012 * cloud_scale * (1.0 - abs(sun.y))).r;
	float lit = clamp((n - n_sun) * 3.0 + 0.55, 0.0, 1.0);
	lit = mix(lit, 0.5, overcast * 0.7);

	float low = sun_low(sun);
	vec3 light_tint = mix(sun_colour, colour_sunset, low * 0.9);
	vec3 moon_tint = moon_colour * 0.08 * moon_brightness * smoothstep(-0.1, 0.2, moon.y);
	vec3 day_light = mix(cloud_shadow_colour, cloud_colour, lit) * light_tint;
	vec3 night_light = mix(cloud_shadow_colour * 0.08, cloud_shadow_colour * 0.25, lit) *
		vec3(0.55, 0.72, 1.0) + moon_tint;
	vec3 col = mix(night_light, day_light, day);

	float edge = a * (1.0 - a) * 4.0;
	float toward = max(dot(dir, sun), 0.0);
	float t4 = toward * toward * toward * toward;
	col += light_tint * edge * t4 * t4 * 0.6 * day * (1.0 - overcast);

	col *= mix(1.0, 0.55, overcast) * mix(1.0, 0.45, storm);
	col = mix(col, vec3(dot(col, LUMA)) * vec3(0.96, 0.98, 1.02), snow * 0.6 + overcast * 0.3);

	vec2 lf = dynamic_sky_lightning.xz;
	float ll = length(lf);
	vec2 flash_dir = ll > 1e-3 ? lf / ll : vec2(0.0, 1.0);
	float toward_flash = dot(normalize(dir.xz + vec2(1e-4, 0.0)), flash_dir) * 0.5 + 0.5;
	float flash = dynamic_sky_lightning.w * (toward_flash * toward_flash * toward_flash) * (0.35 + a);
	col += vec3(0.85, 0.88, 1.0) * flash;

	a *= horizon_fade;

	vec2 p2 = dir.xz / (dir.y + 0.05) * cloud_height * 0.55 * cloud_scale;
	vec2 uv2 = p2 * vec2(0.55, 1.7) + drift * 1.8 + vec2(0.31, 0.77);
	float wisp = texture(cloud_shape, uv2).r;
	float cirrus = smoothstep(0.58, 0.86, wisp) * cirrus_amount * 0.45 * horizon_fade;
	cirrus *= 1.0 - overcast;
	vec3 cirrus_col = mix(cloud_colour, light_tint, 0.6) * mix(0.12, 1.0, day);
	cirrus_col += vec3(0.85, 0.88, 1.0) * flash * 0.5;

	float out_a = a + cirrus * (1.0 - a);
	vec3 out_rgb = (col * a + cirrus_col * cirrus * (1.0 - a));
	return vec4(out_rgb, out_a);
}

void sky() {
	vec3 dir = normalize(EYEDIR);
	vec3 sun = sun_world();

	if (AT_CUBEMAP_PASS && cubemap_fable_dome) {
		if (AT_QUARTER_RES_PASS || AT_HALF_RES_PASS) {
			COLOR = vec3(0.0);
			ALPHA = 0.0;
		} else {
			COLOR = fable_sky_inscatter(
				vec3(dir.x, dir.z, dir.y),
				sun_engine,
				in_scatter_lut,
				beta_rayleigh,
				beta_mie,
				horizon_distance);
		}
	} else {
		vec3 moon = normalize(moon_dir_world);
		float day = daylight(sun);

		if (AT_QUARTER_RES_PASS) {
			vec4 c = cloud_layers(dir, sun, moon, day);
			COLOR = c.rgb;
			ALPHA = c.a;
		} else {
			vec4 clouds = QUARTER_RES_COLOR;
			float cover = clamp(clouds.a, 0.0, 1.0);

			vec3 col = sky_gradient(dir, sun, day);
			col += star_terms(dir, day, cover);
			col += moon_terms(dir, moon, sun, day) * (1.0 - cover);
			col += sun_terms(dir, sun, day, cover);

			col = col * (1.0 - cover) + clouds.rgb;

			float band = horizon_band(dir.y, 0.10 + fog_haze * 0.5);
			vec3 fog_col = mix(colour_horizon, vec3(dot(colour_horizon, LUMA)), 0.5) *
				mix(colour_night * 4.0, vec3(1.0), day);
			col = mix(col, fog_col * mix(1.0, 0.7, overcast), band * fog_haze * 0.85);

			col += vec3(0.85, 0.88, 1.0) * dynamic_sky_lightning.w * 0.08;

			COLOR = col * exposure;
		}
	}
}
