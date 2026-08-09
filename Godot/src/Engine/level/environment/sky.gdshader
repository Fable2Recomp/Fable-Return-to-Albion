shader_type sky;

uniform vec4 sky_top;
uniform vec4 sky_bottom;
uniform vec4 sky_sunset;
uniform vec4 sky_params;
uniform vec4 sky_sun;

void sky() {
	vec3 ray = EYEDIR;
	vec3 sun_dir = normalize(sky_sun.xyz);

	float rayleigh = max(sky_params.z, 0.05);
	float mie = max(sky_params.w, 0.05);
	vec3 betaR = vec3(0.007337, 0.009459, 0.0257276) * rayleigh;
	vec3 betaM = vec3(0.0056149, 0.0063754, 0.0105143) * mie;

	float cosT = dot(ray, sun_dir);
	float phaseR = 0.059683103 * (1.0 + (cosT * cosT));

	float gm = 0.80;
	float hg = 1.0 + (gm * gm) - (2.0 * gm * cosT);
	float phaseM = 0.079577468 * (1.0 - (gm * gm)) /
	               max(pow(abs(hg), 1.5), 0.0001);

	float elev = max(ray.y, 0.004);
	float path = 1.0 / (elev + 0.09);
	vec3 od = (betaR + betaM) * path * 26.0;
	vec3 extinct = exp(-od);
	vec3 beta_sum = max(betaR + betaM, vec3(0.00001));
	vec3 inscatter = ((betaR * phaseR) + (betaM * phaseM)) / beta_sum *
	                 (1.0 - extinct);

	float sun_h = clamp((sun_dir.y * 2.2) + 0.12, 0.0, 1.0);
	vec3 col = inscatter * (7.2 * sun_h) * sky_top.rgb;

	float horizon = 1.0 - clamp(elev * 3.2, 0.0, 1.0);
	float bias = clamp(sky_params.y, 0.0, 1.0);
	col = mix(col, sky_bottom.rgb * (0.35 + (0.65 * sun_h)),
	          horizon * (0.55 + (0.30 * bias)));

	float sunset_w = clamp(1.0 - (abs(sun_dir.y) * 5.0), 0.0, 1.0) *
	                 clamp((cosT * 0.5) + 0.5, 0.0, 1.0);
	col = mix(col, sky_sunset.rgb * (0.4 + (0.8 * phaseM)), sunset_w * 0.45);

	float night = clamp((-sun_dir.y + 0.05) / 0.45, 0.0, 1.0);
	col = mix(col, (col * 0.22) + vec3(0.010, 0.018, 0.050), night);

	COLOR = col;
}
