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

// THE THEME'S SKY OVERLAYS (Engine_SkyOverlay): two painted skies cross-faded by the theme blend,
// laid over the scattering by their own alpha, on the dome and on everything that reflects it.
// A direction d in the guest's axes reads (0.5 + atan(d.y, d.x) / 2pi, asin(d.z) 2 / pi); the
// colour is the texel squared (the game's decode). fable_sky_overlay = (blend, on): until the
// overlays are bound (and wherever no game runs) the scattering stands alone.
global uniform sampler2D fable_sky_overlay0 : filter_linear, repeat_disable;
global uniform sampler2D fable_sky_overlay1 : filter_linear, repeat_disable;
global uniform vec2 fable_sky_overlay;

const float FABLE_SKY_TAU = 6.2831853;

vec3 fable_sky_overlaid(vec3 godot_dir, vec3 scattering) {
	if (fable_sky_overlay.y < 0.5) {
		return scattering;
	}
	// Godot (x, y, z) is the guest's (-x, z, y). Level 0: the azimuth's seam has no derivative.
	vec3 d = normalize(vec3(-godot_dir.x, godot_dir.z, godot_dir.y));
	vec2 uv = vec2(0.5 + atan(d.y, d.x) / FABLE_SKY_TAU, asin(clamp(d.z, -1.0, 1.0)) * (4.0 / FABLE_SKY_TAU));
	vec4 first = textureLod(fable_sky_overlay0, uv, 0.0);
	vec4 second = textureLod(fable_sky_overlay1, uv, 0.0);
	vec3 painted = mix(first.rgb * first.rgb, second.rgb * second.rgb, fable_sky_overlay.x);
	return mix(scattering, painted, mix(first.a, second.a, fable_sky_overlay.x));
}
