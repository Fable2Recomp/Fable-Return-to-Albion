shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded;

uniform sampler2D normal_map : filter_linear_mipmap, repeat_enable;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear;

uniform vec4 w_bias;
uniform vec4 w_speed;
uniform vec4 w_scale;
uniform vec4 w_surface;
uniform vec4 w_deep;
uniform vec4 w_reflp;
uniform vec4 w_glit;
uniform vec4 w_sun;
uniform vec4 w_light;
uniform vec3 sky_constant = vec3(0.58, 0.62, 0.57);

varying vec3 v_local;
varying vec3 v_eye_local;

void vertex() {
	v_local = VERTEX;
	v_eye_local = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

void fragment() {
	vec2 phase01 = fract(TIME * w_speed.xy);
	vec2 phase23 = fract(TIME * w_speed.zw);

	vec2 uv0 = (v_local.xz * w_scale.xy) + phase01;
	vec2 uv1 = (v_local.xz * w_scale.zw) + phase23;

	vec2 n0 = (texture(normal_map, uv0).xy * 2.0) - 1.0;
	vec2 n1 = (texture(normal_map, uv1).xy * 2.0) - 1.0;
	float z0 = sqrt(clamp(1.0 - dot(n0, n0), 0.0, 1.0));
	float z1 = sqrt(clamp(1.0 - dot(n1, n1), 0.0, 1.0));
	vec2 nxy = n0 + n1;
	float nz = z0 + z1;

	vec3 view = v_eye_local - v_local;
	float dist = length(view);
	vec3 V = view / max(dist, 1e-5);

	vec3 Nf = normalize(vec3(nxy.x, w_bias.z * nz, nxy.y));
	float fres = clamp((1.0 - dot(V, Nf)) + w_bias.x, 0.0, 1.0);

	vec3 watercol = mix(w_deep.rgb, w_surface.rgb, fres);

	float shadow_f = 1.0;
	float frefl = clamp(fres + w_bias.y, 0.0, 1.0);
	float refl_str = clamp(w_deep.w, 0.0, 1.0);

	vec3 Nr = normalize(vec3(nxy.x * w_reflp.x * 0.5, 1.0, nxy.y * w_reflp.x * 0.5));
	vec3 R = reflect(-V, Nr);
	R.y = abs(R.y);
	vec3 refl = sky_constant * shadow_f;

	float distf = clamp(dist * (1.0 / 75.0), 0.0, 1.0);
	float refr_k = (1.0 - distf) * refl_str * (1.0 - frefl);
	vec3 col = (watercol * (1.0 - refl_str)) +
	           (refl * (refl_str * mix(frefl, 1.0, distf)));

	vec3 g = normalize(vec3(Nf.x, -max(w_reflp.z, 0.0) * Nf.y, Nf.z));
	vec3 L = normalize(w_light.xyz);
	vec3 Rl = L - (2.0 * dot(L, g) * g);
	float spec = clamp(dot(V, Rl), 0.0, 1.0);
	float glint = pow(max(spec, 1e-6), max(w_glit.x, 1.0));
	col += w_sun.rgb * (glint * max(w_reflp.w, 0.0));

	vec3 behind = texture(screen_tex, SCREEN_UV).rgb;

	ALBEDO = col + (behind * refr_k);
	ALPHA = 1.0;
}
