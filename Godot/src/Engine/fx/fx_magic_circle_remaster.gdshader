shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

uniform sampler2D fx_tex : hint_default_white, filter_linear, repeat_disable;
uniform sampler2D fx_filigree_tex : hint_default_white, filter_linear, repeat_disable;
#include "res://src/Engine/fx/fx_soft.gdshaderinc"

uniform vec3 will_core_radiance = vec3(0.42, 1.68, 3.00);
uniform vec3 will_accent_radiance = vec3(1.15, 2.35, 3.80);
uniform float circle_alpha = 1.0;
uniform float rune_time = 0.0;
uniform bool has_fx_tex = false;
uniform bool has_filigree_tex = false;
uniform bool is_pbr = false;

global uniform float fable_emission_boost = 1.0;

void fragment() {
	// Precompute continuous UV derivatives before wrapping UV.x so mip selection never spikes on
	// the angular wrap seam and hardware Early-Z remains active without alpha kill.
	vec2 uv_dx = dFdx(UV);
	vec2 uv_dy = dFdy(UV);
	vec2 wrapped_uv = vec2(fract(UV.x), clamp(UV.y, 0.02, 0.98));
	vec2 filigree_uv = vec2(fract(UV.x * 1.5 - rune_time * 0.12), clamp(UV.y, 0.02, 0.98));

	vec4 tex_main = textureGrad(fx_tex, wrapped_uv, uv_dx, uv_dy);
	vec4 tex_fil = textureGrad(fx_filigree_tex, filigree_uv, uv_dx * 1.5, uv_dy);

	// Across-band soft bell profile: 0 at inner/outer band edges, 1 at band centre.
	float across = sin(clamp(UV.y, 0.0, 1.0) * 3.14159265);
	float crisp_band = across * across;

	// Procedural Albion Will harmonic filigree & sanctum sigil nodes along the ring circumference.
	// COLOR packs per-band attributes: r = filigree weight, g = sanctum mask, b = brightness, a = alpha.
	float ang = UV.x * 6.28318530;
	float wave8 = 0.5 + 0.5 * cos(ang * 8.0 - rune_time * 1.6);
	float wave16 = 0.5 + 0.5 * sin(ang * 16.0 + rune_time * 2.4 + (UV.y - 0.5) * 6.0);
	float filigree_crest = pow(wave8 * 0.55 + wave16 * 0.45, 1.35);
	float sanctum6 = pow(0.5 + 0.5 * cos(ang * 6.0 + rune_time * 1.1), 2.5);
	float glyph_lattice = mix(0.58 + 0.42 * wave8, 0.34 + 0.96 * filigree_crest, COLOR.r);
	glyph_lattice = mix(glyph_lattice, 0.36 + 1.04 * sanctum6, step(0.85, COLOR.g));

	float main_cov = has_fx_tex
		? max(tex_main.a, max(tex_main.r, max(tex_main.g, tex_main.b)))
		: 1.0;
	float fil_cov = has_filigree_tex
		? max(tex_fil.a, max(tex_fil.r, max(tex_fil.g, tex_fil.b)))
		: 1.0;
	float tex_mod = mix(1.0, mix(0.58, 1.25, main_cov * 0.65 + fil_cov * 0.35), COLOR.r);

	float raw_alpha = crisp_band * glyph_lattice * tex_mod * COLOR.a * circle_alpha;
	bool should_skip = (raw_alpha <= 0.002);

	ALBEDO = vec3(0.0);
	ALPHA = 0.0;

	if (!should_skip) {
		float fade = fx_soft_fade(raw_alpha, VERTEX, SCREEN_UV, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);

		// Keep far-aimed spell circles vivid across the entire aiming range (28m..80m).
		float dist = length(VERTEX);
		float dist_fade = 1.0;
		if (dist > 28.0) {
			float t = clamp((dist - 28.0) / 52.0, 0.0, 1.0);
			dist_fade = mix(1.0, 0.65, t * t * (3.0 - 2.0 * t));
		}

		float boost = is_pbr ? 1.0 : mix(1.0, max(fable_emission_boost, 1.0), dist_fade);
		vec3 tex_tint = vec3(1.0);
		if (has_fx_tex) {
			vec3 decoded = is_pbr ? tex_main.rgb : (tex_main.rgb * tex_main.rgb);
			tex_tint = mix(vec3(1.0), max(decoded, vec3(0.40)), 0.35 * COLOR.r);
		}

		// Hue-preserving per-spell HDR radiance (no flat white channel wash so Red stays vivid red).
		vec3 base_radiance = mix(will_core_radiance, will_accent_radiance, crisp_band * COLOR.r);
		vec3 rgb = max(base_radiance * tex_tint * (COLOR.b * boost * dist_fade), vec3(0.0));

		ALBEDO = rgb;
		ALPHA = clamp(raw_alpha * fade, 0.0, 1.0);
	}
}
