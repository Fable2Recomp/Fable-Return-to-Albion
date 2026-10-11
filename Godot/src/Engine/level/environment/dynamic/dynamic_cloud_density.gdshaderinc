float cloud_hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

const float CLOUD_BASE = 1400.0;
const float CLOUD_DEPTH = 1300.0;
const float CLOUD_DISTANCE = 60000.0;
const float CLOUD_EXTINCTION = 0.009;
const int CLOUD_STEPS = 64;

float weather_noise(vec2 position) {
	vec2 cell = floor(position);
	vec2 f = fract(position);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(cloud_hash12(cell), cloud_hash12(cell + vec2(1.0, 0.0)), f.x),
		mix(cloud_hash12(cell + vec2(0.0, 1.0)), cloud_hash12(cell + vec2(1.0, 1.0)), f.x), f.y);
}

vec2 cloud_weather(vec2 position) {
	vec2 p = position / 6500.0 + vec2(1.0, 1.0);
	vec2 warp = vec2(weather_noise(p * 0.61 + vec2(19.1, 7.3)),
		weather_noise(p * 0.73 + vec2(-4.7, 31.5))) - 0.5;
	p += warp * 1.8;
	vec2 rotated = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8)) * p;
	float distribution = weather_noise(p) * 0.62 +
		weather_noise(rotated * 2.17 + vec2(17.4, -8.2)) * 0.26 +
		weather_noise(rotated * 4.39 + vec2(-11.2, 23.7)) * 0.12;
	float type = weather_noise(rotated * 0.87 + vec2(43.3, 12.8));
	return vec2(distribution, type);
}

float volume_density(vec3 position) {
	float height = (position.y - CLOUD_BASE) / CLOUD_DEPTH;
	if (height <= 0.0 || height >= 1.0) {
		return 0.0;
	}
	vec3 drift = vec3(wind_dir.x, 0.0, wind_dir.y) * cloud_time * cloud_speed * cloud_motion_multiplier * 20.0;
	vec3 advected = (position + drift) * max(cloud_scale, 0.05);
	vec2 weather = cloud_weather(advected.xz);
	float cover = clamp(cloud_coverage + overcast * 0.38 + storm * 0.2, 0.0, 1.0);
	float local_cover = smoothstep(1.0 - cover - 0.14, 1.0 - cover + 0.17, weather.x);
	if (local_cover < 0.001) {
		return 0.0;
	}

	float top = mix(0.38, 1.0, weather.y) * mix(0.65, 1.0, local_cover);
	float profile = smoothstep(0.0, 0.07, height) *
		(1.0 - smoothstep(top * 0.42, top, height));
	if (profile < 0.001) {
		return 0.0;
	}
	vec3 uv = advected / 3400.0;
	vec3 warp = vec3(weather.y - 0.5, weather.x - 0.5, weather.x - weather.y) * 0.27;
	float billow = texture(cloud_volume_shape, uv + warp).r;
	vec3 rotated = vec3(uv.z * 0.8 + uv.x * 0.6, uv.y, uv.z * 0.6 - uv.x * 0.8);
	billow = billow * 0.72 + texture(cloud_volume_shape, rotated * 1.73 + vec3(0.31, 0.73, 0.17)).r * 0.28;
	float body = clamp((billow - (1.0 - local_cover) * 0.35 - 0.36) / 0.36, 0.0, 1.0);
	body *= profile;
	float erosion = 1.0 - texture(cloud_volume_detail, uv * 5.31 + warp * 2.0).r;
	body = max(body - erosion * 0.36 * pow(1.0 - body, 2.0), 0.0);
	float density = clamp(cloud_density + overcast * 0.3 + storm * 0.3, 0.0, 1.0);
	return body * local_cover * density;
}

