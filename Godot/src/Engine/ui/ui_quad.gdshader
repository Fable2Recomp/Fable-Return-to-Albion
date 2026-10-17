shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix;

// The one shader every Gameface quad draws through — the native form of the title's own
// `0098_pshader_guiquad`:
//
//     rgb = primary.rgb ; alpha = primary.a
//     if (detail) { rgb = detail.rgb ; alpha *= detail.a }     // blend factor is 1.0
//     rgb -= (1 - tint) * tintAmount                           // SUBTRACTIVE, weighted
//     alpha *= accumulatedOpacity
//
// The blend factor is stored unconditionally as 1.0 (`stfs f0, 0x1A4(r30)` at 0x8286E614), so
// a detail-mapped quad takes its RGB entirely from the detail map and the primary contributes
// only ALPHA — which is why the primary may be the exporter's magenta placeholder. `g_Tinted`
// is likewise set unconditionally (`|= 0x40` at 0x8286E51C): the tint is gated by its WEIGHT,
// not by a flag.
//
// The gamma ramp is deliberately not reproduced — our texture decode already lands in the
// calibrated space, and a second transfer would double-count it.

uniform sampler2D diffuse_tex : source_color, filter_linear_mipmap, repeat_disable;

// Repeat-enabled: the detail maps tile, and the derived uv set runs outside 0..1.
uniform sampler2D detail_tex : source_color, filter_linear_mipmap, repeat_enable;

// The atlas rectangle, already inset to texel centres on the CPU side.
uniform vec2 uv1_offset = vec2(0.0);
uniform vec2 uv1_scale = vec2(1.0);

uniform bool detail_mapped = false;

// The guest seeds this from two randoms per quad so neighbouring frame pieces do not tile in
// lockstep; ours is a hash of the element ordinal, which keeps a screenshot reproducible.
uniform vec2 detail_origin = vec2(0.0);

// `uv1 = |corner_i - corner_j| * 0.0025`, from sub_8286F830 (flt_833210C8 = 0x3B23D70A).
// UNPROVEN which space those corners are in; this assumes the 960x720 virtual space, so a
// 400-unit quad tiles once. Calibrate here if the grain reads wrong.
uniform float detail_scale = 0.0025;

// Lets the detail tiling come off the model matrix rather than an accumulated scale.
uniform float quad_base_size = 100.0;

// .fac field 14 (colour) and field 13 (weight). The weight is 0 on ~85% of records — the
// runtime raises it through the `TintingAmount` attribute.
uniform vec3 tint_rgb = vec3(1.0);
uniform float tint_amount = 0.0;

// A hue rotation in turns, 0 = none. Lets a mod recolour shipped art — the health bar's red as
// a mana bar's blue — without a second texture.
uniform float hue_shift = 0.0;

// The ACCUMULATED opacity down the element chain, not this element's own.
uniform float opacity = 1.0;

// Clip rect in the laid-out UI space: min.xy, max.xy. An element carrying `MaskName` — and,
// via the parent walk in `sub_82427188`, its subtree — is clipped by the named element.
//
// The guest does this as a render-to-texture, not a rect. A discard is exact for all 14
// shipped masks (axis-aligned, zero pivot, single-texel textures) and nothing more.
uniform vec4 clip_rect = vec4(-1e9, -1e9, 1e9, 1e9);

// A controller button cut from a shared atlas (`abyx`) carries the keycap of the key bound to
// it; while the keyboard and mouse are the active device the quad draws that instead of its
// atlas cell. One global flips every glyph at once — no material is walked on a switch.
uniform sampler2D keycap_tex : source_color, filter_linear_mipmap, repeat_disable;
uniform bool has_keycap = false;
global uniform bool ui_keyboard_mode;

varying vec2 clip_world;

void vertex() {
	clip_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xy;
}

void fragment() {
	if (clip_world.x < clip_rect.x || clip_world.x > clip_rect.z ||
		clip_world.y < clip_rect.y || clip_world.y > clip_rect.w) {
		discard;
	}

	vec2 uv = UV * uv1_scale + uv1_offset;
	vec4 primary = has_keycap && ui_keyboard_mode
		? texture(keycap_tex, UV)
		: texture(diffuse_tex, uv);

	vec3 rgb = primary.rgb;
	float alpha = primary.a;

	if (detail_mapped) {
		// Laid-out size in UI units, off the model matrix so ancestor scale is included.
		vec2 size = vec2(length(MODEL_MATRIX[0].xyz), length(MODEL_MATRIX[1].xyz))
			* quad_base_size;

		// V is negated exactly as the guest writes it (`uv1.y = -|dy| * 0.0025 + rand`).
		vec2 detail_uv = vec2(UV.x, -UV.y) * size * detail_scale + detail_origin;
		vec4 detail = texture(detail_tex, detail_uv);

		// REPLACE, not blend — the stored factor is 1.0.
		rgb = detail.rgb;
		alpha *= detail.a;
	}

	if (hue_shift != 0.0) {
		// RGB -> HSV, rotate, back (the branchless form).
		vec4 k = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
		vec4 p = mix(vec4(rgb.bg, k.wz), vec4(rgb.gb, k.xy), step(rgb.b, rgb.g));
		vec4 q = mix(vec4(p.xyw, rgb.r), vec4(rgb.r, p.yzx), step(p.x, rgb.r));
		float d = q.x - min(q.w, q.y);
		vec3 hsv = vec3(abs(q.z + (q.w - q.y) / (6.0 * d + 1e-10)), d / (q.x + 1e-10), q.x);
		hsv.x = fract(hsv.x + hue_shift);
		vec3 c = clamp(abs(fract(hsv.xxx + vec3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0, 0.0, 1.0);
		rgb = hsv.z * mix(vec3(1.0), c, hsv.y);
	}

	// SUBTRACTIVE and weighted; weight 0 leaves the colour alone.
	rgb -= (vec3(1.0) - tint_rgb) * tint_amount;

	ALBEDO = rgb;
	ALPHA = alpha * opacity;
}
