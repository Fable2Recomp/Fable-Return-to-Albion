shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded;

#include "res://src/Engine/level/environment/sky_dome.gdshaderinc"
#include "res://src/Engine/level/environment/fable_atmosphere.gdshaderinc"

uniform sampler2D normal_map : filter_linear_mipmap, repeat_enable;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

uniform bool guest_water = false;

// The mirrored render of the world — Engine_WaterReflection's SubViewport, one per body, mounted
// only in guest mode. Until it is mounted `planar_reflection` stays false and the sky dome stands
// in, which is what every path that builds water without a live camera (the viewers, headless)
// gets. The tile is sampled at 1-u: our mirror camera is a proper rotation, so its image is
// horizontally flipped against the guest's improper-basis render that screen-space lookup assumes.
uniform sampler2D reflection_tex : filter_linear, repeat_disable;
uniform bool planar_reflection = false;

// The authored coverage mask. BILINEAR AND CLAMPED, both proven rather than chosen:
// `Render_BindMaterialTextures_INFERRED` sets MagFilter/MinFilter from *(0x83316FC4) = 1 and
// AddressU/V from *(0x83316F40) = 2, which decode through the D3D layer as k_Linear and
// k_ClampToEdge. Point sampling here would make the whole edge ramp a no-op.
uniform sampler2D water_mask : filter_linear, repeat_disable;
uniform bool has_water_mask = false;

// (minX, minZ, 1/sizeX, 1/sizeZ) — turns a local XZ position into the mask's UV, so the mesh does
// not have to carry one. The guest's patches use a plain 0..1 parametric UV over their own extent;
// ours spans the body because our mesh merges its tiles.
uniform vec4 w_mask_bounds = vec4(0.0, 0.0, 1.0, 1.0);

// (EdgeBlendMin, EdgeBlendMax, EdgeBlendBias, unused) — theme-only, no per-body slot exists.
uniform vec4 w_edge = vec4(0.0, 1.0, 0.0, 0.0);

uniform vec4 w_bias;
uniform vec4 w_speed;
uniform vec4 w_scale;
uniform vec4 w_surface;
uniform vec4 w_deep;
uniform vec4 w_reflp;
uniform vec4 w_glit;
uniform vec3 sky_constant = vec3(0.58, 0.62, 0.57);

uniform float wave_amp = 0.12;
uniform float wave_slope = 1.5;
uniform float wave_len = 1.0;
uniform float wave_speed = 0.35;
uniform float wave_fade_depth = 0.8;
uniform float foam_width = 1.1;
uniform float beach_slope = 0.5;

uniform sampler2D terrain_height_tex : filter_linear, repeat_disable;
uniform vec4 terrain_bounds = vec4(0.0, 0.0, 1.0, 1.0);
uniform bool has_terrain_height = false;

uniform vec4 w_body_bounds = vec4(0.0);
uniform bool has_body_bounds = false;

// THE SUN GLINT IS LIT BY THE LEVEL'S MAIN DIRECTIONAL LIGHT, and it used to be lit by a
// hardcoded direction that never moved. The water draw's own upload settles it: the offscreen
// water renderer `sub_82B3CDE8` takes the LAST entry of the frame's directional-light vector
// (`dword_834A4D68`/`_4D6C`, built by `sub_821BD0C8` filtering PRIMITIVE_INSTANCE_DIRECTIONAL_LIGHT,
// type 10) and uploads `light+0xD0` to the binding the shader reads as `g_LightColor`:
//
//     0x82b3cf0c  lwz    r30, -4(r11)      ; last light in the list
//     0x82b3cf34  lvx128 v1, r30, 0xD0     ; its colour
//     0x82b3cfc4  li     r3, 0x7A          ; binding 122
//
// and that +0xD0 is written from the day-set theme's MAIN_LIGHT_COLOUR (`sub_821DF670` loads
// theme+16 and sends it as command id 29; `sub_822BD190` stores it to +0xD0). It is the same
// light our DirectionalLight3D is built from, so it arrives here the same way the props' bake
// does — per instance, refreshed whenever time of day moves.
//
// Nothing is water-specific about it: the identical `li 0xD0 -> binding 122` snippet appears in
// the mesh, quad, rain and snow paths too.
//
// WORLD SPACE, pointing TOWARD the sun. The guest hands its light the NEGATED sun direction and
// the shader negates it back at the reflect; carrying the toward-sun vector skips both.
instance uniform vec4 sun_direction = vec4(0.0, 1.0, 0.0, 0.0);

/// rgb: the main light's colour already multiplied by its energy.
instance uniform vec4 sun_colour = vec4(1.0);

uniform sampler2D in_scatter_lut : filter_linear, repeat_disable;
uniform vec3 beta_rayleigh = vec3(0.001, 0.0015, 0.004);
uniform vec3 beta_mie = vec3(0.0056, 0.0064, 0.0105);
uniform vec3 sun_engine = vec3(1.0, 0.0, 0.0);
uniform float horizon_distance = 1900.0;

uniform bool has_dome = false;

varying vec3 v_local;
varying vec3 v_eye_local;
varying vec3 v_sun_local;

float body_edge(vec2 p) {
	if (!has_body_bounds) {
		return 1.0;
	}
	float d = min(min(p.x - w_body_bounds.x, p.y - w_body_bounds.y),
		min(w_body_bounds.z - p.x, w_body_bounds.w - p.y));
	return clamp(d / 1.5, 0.0, 1.0);
}

float terrain_probe(vec2 p, float y, out vec2 shore_dir, out float slope, out float beach) {
	shore_dir = vec2(0.0);
	slope = 0.0;
	beach = 0.0;
	if (!has_terrain_height) {
		return 4.0;
	}

	vec2 tuv = clamp((p - terrain_bounds.xy) * terrain_bounds.zw, vec2(0.0), vec2(1.0));
	vec2 duv = terrain_bounds.zw * 1.5;
	float h = textureLod(terrain_height_tex, tuv, 0.0).r;
	float hpx = textureLod(terrain_height_tex,
		clamp(tuv + vec2(duv.x, 0.0), vec2(0.0), vec2(1.0)), 0.0).r;
	float hnx = textureLod(terrain_height_tex,
		clamp(tuv - vec2(duv.x, 0.0), vec2(0.0), vec2(1.0)), 0.0).r;
	float hpz = textureLod(terrain_height_tex,
		clamp(tuv + vec2(0.0, duv.y), vec2(0.0), vec2(1.0)), 0.0).r;
	float hnz = textureLod(terrain_height_tex,
		clamp(tuv - vec2(0.0, duv.y), vec2(0.0), vec2(1.0)), 0.0).r;

	vec2 grad = vec2(hpx - hnx, hpz - hnz) / 3.0;
	slope = length(grad);
	if (slope > 1e-4) {
		shore_dir = grad / slope;
	}
	beach = 1.0 - smoothstep(beach_slope * 0.8, beach_slope * 1.6, slope);
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

const vec2 WAVE_DIR[5] = {
	vec2(0.995, 0.100), vec2(-0.665, 0.747), vec2(0.138, -0.990),
	vec2(0.780, 0.625), vec2(-0.502, 0.865) };
const float WAVE_LEN[5] = { 13.0, 7.9, 4.8, 2.9, 1.8 };
const float WAVE_LEN_SUM = 30.4;

vec3 wave_field(vec2 p, float t) {
	vec2 slope = vec2(0.0);
	float height = 0.0;
	for (int i = 0; i < 5; i++) {
		float k = TAU / WAVE_LEN[i];
		float amp = wave_amp * (WAVE_LEN[i] / WAVE_LEN_SUM);
		float phase = (dot(p, WAVE_DIR[i]) * k) - (sqrt(9.8 * k) * t);
		slope += sin(phase) * (k * amp) * WAVE_DIR[i];
		height += cos(phase) * amp;
	}
	return vec3(slope, height);
}

void vertex() {
	v_local = VERTEX;
	v_eye_local = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;

	// The glint is built against `v_local` and `v_eye_local`, so the sun has to be in the same
	// space as they are. w = 0 — this is a direction, and the body's translation must not reach it.
	v_sun_local = (inverse(MODEL_MATRIX) * vec4(sun_direction.xyz, 0.0)).xyz;

	if (!guest_water) {
		vec2 sdir;
		float slope;
		float beach;
		float dt = terrain_probe(VERTEX.xz, VERTEX.y, sdir, slope, beach);
		float env = wave_zone(dt, slope);
		if (env > 0.002) {
			float phase = (dt + (TIME * wave_speed)) / max(wave_len, 0.05);
			float shoal = 1.0 - clamp(dt / max(wave_fade_depth, 0.05), 0.0, 1.0);
			float lift = roller_shape(fract(phase), shoal) * wave_amp * (0.7 + (0.9 * shoal))
				* env * roller_vary(VERTEX.xz, phase);
			VERTEX.y += lift * body_edge(VERTEX.xz);
		}
	}
}

// The reflected sky is THE SAME DOME the sky shader draws, so it comes from the same function.
// A second copy of the arithmetic here would have to tonemap too, and then the water reflects
// a compressed sky while the sky above it is not compressed at all.
vec3 dome_reflect(vec3 r) {
	return fable_sky_inscatter(
		vec3(r.x, r.z, r.y),
		sun_engine,
		in_scatter_lut,
		beta_rayleigh,
		beta_mie,
		horizon_distance);
}

float linear_depth(vec2 uv, mat4 inv_proj) {
	float d = texture(depth_tex, uv).r;
	vec4 v = inv_proj * vec4((uv * 2.0) - 1.0, d, 1.0);
	return -v.z / v.w;
}

void fragment() {
	float w_surge = 0.0;
	float w_low = 0.0;
	float w_roller = 0.0;
	vec2 w_wave_n = vec2(0.0);
	vec2 w_warp = vec2(0.0);
	if (!guest_water) {
		vec2 sdir;
		float slope;
		float beach;
		float dt = terrain_probe(v_local.xz, v_local.y, sdir, slope, beach);
		float env = wave_zone(dt, slope);
		vec3 wf = wave_field(v_local.xz, TIME);
		w_warp = wf.xy * 4.0;
		w_surge = clamp(wf.z / max(wave_amp, 0.001), -1.0, 1.0);
		if (has_terrain_height) {
			w_low = 1.0 - smoothstep(beach_slope * 0.6, beach_slope * 1.2, slope);
		}
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

	vec2 phase01 = fract(TIME * w_speed.xy);
	vec2 phase23 = fract(TIME * w_speed.zw);

	vec2 uv0 = ((v_local.xz + w_warp) * w_scale.xy) + phase01;
	vec2 uv1 = ((v_local.xz + w_warp) * w_scale.zw) + phase23;

	vec2 n0 = (texture(normal_map, uv0).xy * 2.0) - 1.0;
	vec2 n1 = (texture(normal_map, uv1).xy * 2.0) - 1.0;
	float z0 = sqrt(clamp(1.0 - dot(n0, n0), 0.0, 1.0));
	float z1 = sqrt(clamp(1.0 - dot(n1, n1), 0.0, 1.0));
	vec2 nxy = n0 + n1 + w_wave_n;
	float nz = z0 + z1;

	vec3 view = v_eye_local - v_local;
	float dist = length(view);
	vec3 V = view / max(dist, 1e-5);

	vec3 Nf = normalize(vec3(nxy.x, w_bias.z * nz, nxy.y));
	float fres = clamp((1.0 - dot(V, Nf)) + w_bias.x, 0.0, 1.0);

	vec3 watercol = mix(w_deep.rgb, w_surface.rgb, fres);

	float refl_str = clamp(w_deep.w, 0.0, 1.0);

	vec3 col;
	vec3 L = normalize(v_sun_local);

	if (guest_water) {
		// THE GUEST'S COMPOSITION, IN ITS OWN ORDER — 0057 pshader_ocean_water:
		//
		//     near   = lerp(W, lerp(Rr, Rf, f), s)
		//     far    = lerp(W, Rf, s)
		//     colour = lerp(near, far, t)
		//
		// with W the fresnel-blended body colour, f = fresnel + ReflectionBias,
		// s = ReflectionStrength and t = distance/MaxRefractionDistance. The reflection and
		// refraction tiles are 8-bit in the guest, so both fetches clip at 1.0. The glint is the
		// guest's own Phong against the bent normal, `saturate(dot(V, 2(g.S)g - S))^power`.
		float frefl = clamp(fres + w_bias.y, 0.0, 1.0);
		float distf = clamp(dist / max(w_bias.w, 0.001), 0.0, 1.0);

		vec2 raw_uv = SCREEN_UV + (nxy * 0.5 * w_reflp.x);
		vec2 refl_uv = vec2(1.0 - raw_uv.x, raw_uv.y);
		vec3 Ng = normalize(vec3(nxy.x * w_reflp.x * 0.5, 1.0, nxy.y * w_reflp.x * 0.5));
		vec3 Rg = reflect(-V, Ng);
		Rg.y = abs(Rg.y);
		vec3 dome = has_dome ? dome_reflect(Rg) : sky_constant;

		vec3 Rf = min(planar_reflection
			? texture(reflection_tex, clamp(refl_uv, vec2(0.0), vec2(1.0))).rgb
			: dome, vec3(1.0));

		vec2 refr_uv = SCREEN_UV + (nxy * 0.5 * w_reflp.y);
		vec3 Rr = min(texture(screen_tex, refr_uv).rgb, vec3(1.0));

		vec3 near_col = mix(watercol, mix(Rr, Rf, frefl), refl_str);
		vec3 far_col = mix(watercol, Rf, refl_str);
		col = mix(near_col, far_col, distf);

		vec3 g = normalize(vec3(Nf.x, w_reflp.z * Nf.y, Nf.z));
		vec3 Rl = (2.0 * dot(L, g) * g) - L;
		float spec = clamp(dot(V, Rl), 0.0, 1.0);
		float glint = pow(max(spec, 1e-6), max(w_glit.x, 1.0));
		col += sun_colour.rgb * (glint * max(w_reflp.w, 0.0));
	} else {
		float distort = clamp(w_reflp.x * 0.25, 0.05, 1.0);
		vec3 Nr = normalize(vec3(nxy.x * distort, nz, nxy.y * distort));
		float ndv = clamp(dot(V, Nr), 0.0, 1.0);
		float fres_s = 0.02 + (0.98 * pow(1.0 - ndv, 5.0));

		float water_lin = -VERTEX.z;
		float depth_here = max(linear_depth(SCREEN_UV, INV_PROJECTION_MATRIX) - water_lin, 0.0);

		vec2 refr_uv = SCREEN_UV + (nxy * 0.5 * w_reflp.y * clamp(depth_here, 0.0, 1.0));
		float refr_lin = linear_depth(refr_uv, INV_PROJECTION_MATRIX);
		if (refr_lin < water_lin) {
			refr_uv = SCREEN_UV;
			refr_lin = linear_depth(SCREEN_UV, INV_PROJECTION_MATRIX);
		}
		vec3 Rr = texture(screen_tex, refr_uv).rgb;

		float depth_w = max(refr_lin - water_lin, 0.0);
		float absorb = 1.0 - exp(-depth_w * (6.0 / max(w_bias.w, 1.0)));
		vec3 under = mix(Rr, watercol, absorb);

		vec3 R = reflect(-V, Nr);
		R.y = abs(R.y);
		vec3 refl_col = has_dome ? dome_reflect(R) : sky_constant;

		float F = clamp(fres_s * refl_str, 0.0, 1.0);
		if (F > 0.004) {
			vec3 world_n = normalize((MODEL_MATRIX * vec4(Nr, 0.0)).xyz);
			vec3 view_n = normalize((VIEW_MATRIX * vec4(world_n, 0.0)).xyz);
			vec3 ro = VERTEX;
			vec3 rd = normalize(reflect(normalize(ro), view_n));

			float hit = 0.0;
			vec2 hit_uv = vec2(0.0);
			float t_ray = 0.35;
			for (int i = 0; i < 24; i++) {
				vec3 pos = ro + (rd * t_ray);
				vec4 clip = PROJECTION_MATRIX * vec4(pos, 1.0);
				if (clip.w <= 0.01) {
					break;
				}

				vec2 uv = ((clip.xy / clip.w) * 0.5) + 0.5;
				if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) {
					break;
				}

				float scene = linear_depth(uv, INV_PROJECTION_MATRIX);
				float ray = -pos.z;
				if (scene < ray && (ray - scene) < (0.12 + (t_ray * 0.35))) {
					vec2 b = min(uv, 1.0 - uv);
					float edge = smoothstep(0.0, 0.08, min(b.x, b.y));
					hit = edge;
					hit_uv = uv;
					break;
				}

				t_ray *= 1.28;
			}

			if (hit > 0.0) {
				refl_col = mix(refl_col, texture(screen_tex, hit_uv).rgb, hit);
			}
		}

		col = mix(under, refl_col, F);

		vec3 H = normalize(L + V);
		vec3 g = normalize(vec3(Nf.x, Nf.y * max(w_reflp.z, 0.05), Nf.z));
		float ndh = clamp(dot(g, H), 0.0, 1.0);
		float power = max(w_glit.x, 8.0);
		float glare = pow(ndh, power) + (0.04 * pow(ndh, power * 0.1));
		float corridor = pow(clamp(dot(reflect(-V, vec3(0.0, 1.0, 0.0)), L), 0.0, 1.0), 16.0);
		col += sun_colour.rgb * max(w_reflp.w, 0.0) * glare * corridor *
			clamp(L.y * 6.0, 0.0, 1.0);

		float lap = 0.6 + (0.4 * w_surge);
		float shore_foam = (1.0 - clamp(depth_here / max(foam_width, 0.05), 0.0, 1.0)) * lap
			* (1.0 - w_low);
		float band = max(shore_foam, w_roller);
		if (band > 0.003) {
			float f0 = texture(normal_map,
				(v_local.xz * 0.33) + vec2(TIME * 0.020, TIME * 0.012)).x;
			float f1 = texture(normal_map,
				(v_local.xz * 0.19) - vec2(TIME * 0.016, TIME * 0.010)).y;
			float pattern = mix(f1, f0, clamp((band - 0.4) / 0.4, 0.0, 1.0));
			float foam = smoothstep(0.45, 0.95, band * (0.35 + (0.95 * pattern)));

			vec3 zen = has_dome ? dome_reflect(vec3(0.0, 1.0, 0.0)) : sky_constant;
			vec3 foam_col = (zen * 0.75) + (sun_colour.rgb * 0.30 * clamp(L.y * 2.0, 0.0, 1.0));
			col = mix(col, foam_col, foam * 0.75);
		}
	}

	// THE ATMOSPHERE TAIL — 0057 runs the full extinction/in-scatter pair over the composed
	// colour, after the glint and before its exposure multiply:
	//
	//     r2 = (fogC*(1-T)*AtmFactors.y - colour*(1-T))*AtmFactors.x + colour
	//
	// Applied to both compositions (this surface is unshaded, one value carries everything)
	// with the in-scatter added back at full weight, matching the guest's own placement.
	vec3 fog_t;
	vec3 fog_c;
	if (fable_atmosphere_terms(VERTEX, INV_VIEW_MATRIX, fog_t, fog_c)) {
		col = col * fog_t + (vec3(1.0) - fog_t) * fog_c;
	}

	ALBEDO = col;

	// THE SHORELINE, from the authored coverage mask.
	//
	// The guest's alpha, isolated cleanly in the z-pass blob 0061 and identical in 0057:
	//
	//     mask  = tfetch2D(g_WaterMaskSampler, TEXCOORD3.xy).x
	//     d     = 1.0 - mask
	//     a     = saturate(-d * WC25.y + WC25.x)
	//     ALPHA = 1.0 - a
	//
	// with the constants built on the CPU at the tail of `sub_821E4C88`:
	//
	//     821e50c0  WC25.y = 1 / (Max - Min)
	//     821e50d8  WC25.x = (Bias + Max) / (Max - Min)
	//
	// Substituting collapses the double negation into a plain linear ramp on coverage — zero at
	// `Min + Bias`, one at `Max + Bias`:
	//
	//     ALPHA = saturate((d - (Min + Bias)) / (Max - Min))
	//
	// The mask reads 1 for LAND (Engine_WaterMask inverts on the way in, as `sub_82B3B4F8` does),
	// so `d` is water coverage and the ramp opens as coverage rises.
	//
	// WITHOUT A MASK THIS STAYS OPAQUE rather than guessing a falloff from geometry — a body whose
	// tiles carry no cells is water everywhere, which is exactly the guest's empty-mask branch at
	// 0x82B3AB24: a 4x4 texture of constant 0.0, and no blur.
	if (has_water_mask) {
		vec2 mask_uv = (v_local.xz - w_mask_bounds.xy) * w_mask_bounds.zw;
		float mask = texture(water_mask, clamp(mask_uv, vec2(0.0), vec2(1.0))).r;
		float d = 1.0 - mask;
		ALPHA = clamp(
			(d - (w_edge.x + w_edge.z)) / max(w_edge.y - w_edge.x, 1e-4),
			0.0, 1.0);
	} else {
		ALPHA = 1.0;
	}
	ALPHA = max(ALPHA, min(w_roller, 1.0));
}
