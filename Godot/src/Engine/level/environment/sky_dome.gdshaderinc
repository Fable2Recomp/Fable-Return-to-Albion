const float FABLE_SKY_TZENITH_RAYLEIGH = 150.0;
const float FABLE_SKY_TZENITH_MIE = 100.0;

vec3 fable_sky_inscatter(
		vec3 ray_engine,
		vec3 sun_engine,
		sampler2D in_scatter_lut,
		vec3 beta_rayleigh,
		vec3 beta_mie,
		float horizon_distance) {
	vec3 ray_n = normalize(ray_engine);

	float sun_dot = clamp(dot(normalize(sun_engine), ray_n), 0.0, 1.0);
	vec3 scatter = texture(in_scatter_lut, vec2(sun_dot, 0.5)).rgb;

	float up = max(abs(ray_n.z), 0.0001);
	float h = exp2(log2(up) * 0.2);
	float k = horizon_distance * (1.05 - h);
	float depth_rayleigh = mix(k, FABLE_SKY_TZENITH_RAYLEIGH, h);
	float depth_mie = mix(k, FABLE_SKY_TZENITH_MIE, h);

	vec3 tau = (beta_rayleigh * depth_rayleigh) + (beta_mie * depth_mie);
	vec3 extinction = exp2(-tau * 1.442695);
	return (vec3(1.0) - extinction) * scatter;
}
