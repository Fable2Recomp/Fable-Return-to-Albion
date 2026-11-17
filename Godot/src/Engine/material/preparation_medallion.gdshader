shader_type canvas_item;

uniform float pulse_speed : hint_range(0.5, 5.0) = 1.8;
uniform float min_pulse : hint_range(0.5, 1.0) = 0.5;
uniform float max_pulse : hint_range(1.0, 2.5) = 1.0;
uniform float glow_spread : hint_range(1.0, 40.0) = 20.0;
uniform float aura_intensity : hint_range(0.0, 2.0) = 0.40;
uniform float phase_offset : hint_range(0.0, 3.14159) = 0.0;

uniform float blue_sensitivity : hint_range(0.5, 2.5) = 1.0;
uniform float red_sensitivity  : hint_range(0.5, 2.5) = 1.35;

uniform vec3 blue_glow_tint : source_color = vec3(0.30, 0.75, 1.0);
uniform vec3 red_glow_tint  : source_color = vec3(1.00, 0.25, 0.35);

global uniform float fable_emission_boost = 1.0;

float detect_blue(vec4 c) {
	if (c.a < 0.05) {
		return 0.0;
	}
	float cyan_bias = (c.b + c.g) - (c.r * 1.75);
	float blue_dom = c.b - c.r;
	float val = smoothstep(0.06, 0.35, cyan_bias) *
	            smoothstep(0.0, 0.20, blue_dom) *
	            smoothstep(0.24, 0.60, c.b);
	return clamp(val * blue_sensitivity, 0.0, 1.0);
}

float detect_red(vec4 c) {
	if (c.a < 0.05) {
		return 0.0;
	}
	// Red must clearly exceed green (all red, coral, and pink runes have low green relative to red)
	float red_over_green = c.r - c.g;

	// Reject gold frame: gold is yellow-amber, where green strongly exceeds blue (g - b > 0.18).
	// In red/pink runes, blue is either close to or higher than green, or green is very low.
	float not_gold = 1.0 - smoothstep(0.15, 0.32, c.g - c.b);

	// Reject pure purple center: in the purple mosaic, blue dominates red (b - r > 0.05).
	float not_purple = 1.0 - smoothstep(0.05, 0.25, c.b - c.r);

	float val = smoothstep(0.10, 0.30, red_over_green) *
	            smoothstep(0.26, 0.58, c.r) *
	            not_gold *
	            not_purple;

	return clamp(val * red_sensitivity, 0.0, 1.0);
}

void fragment() {
	// Zero-discard `canvas_item` blend path: compute derivatives unconditionally before branching
	// so `textureGrad` inside `if (!should_discard)` skips the 16-tap bloom loop without helper-lane
	// hangs, and emit `COLOR = vec4(0.0)` when transparent.
	vec2 uv_dx = dFdx(UV);
	vec2 uv_dy = dFdy(UV);
	vec4 base = textureGrad(TEXTURE, UV, uv_dx, uv_dy);
	COLOR = vec4(0.0);

	bool should_discard = (base.a <= 0.001);

	if (!should_discard) {
		float blue_val = detect_blue(base);
		float red_val  = detect_red(base);
		float rune_mask = clamp(blue_val + red_val, 0.0, 1.0);

		// Multi-tap bloom kernel for tight, soft neon radiance around the runes
		vec2 px = TEXTURE_PIXEL_SIZE * glow_spread;
		float b_bloom = 0.0;
		float r_bloom = 0.0;

		b_bloom += detect_blue(textureGrad(TEXTURE, UV + vec2( px.x,  0.0), uv_dx, uv_dy));
		b_bloom += detect_blue(textureGrad(TEXTURE, UV + vec2(-px.x,  0.0), uv_dx, uv_dy));
		b_bloom += detect_blue(textureGrad(TEXTURE, UV + vec2( 0.0,   px.y), uv_dx, uv_dy));
		b_bloom += detect_blue(textureGrad(TEXTURE, UV + vec2( 0.0,  -px.y), uv_dx, uv_dy));
		b_bloom += detect_blue(textureGrad(TEXTURE, UV + vec2( px.x,  px.y) * 0.7071, uv_dx, uv_dy));
		b_bloom += detect_blue(textureGrad(TEXTURE, UV + vec2(-px.x,  px.y) * 0.7071, uv_dx, uv_dy));
		b_bloom += detect_blue(textureGrad(TEXTURE, UV + vec2( px.x, -px.y) * 0.7071, uv_dx, uv_dy));
		b_bloom += detect_blue(textureGrad(TEXTURE, UV + vec2(-px.x, -px.y) * 0.7071, uv_dx, uv_dy));

		r_bloom += detect_red(textureGrad(TEXTURE, UV + vec2( px.x,  0.0), uv_dx, uv_dy));
		r_bloom += detect_red(textureGrad(TEXTURE, UV + vec2(-px.x,  0.0), uv_dx, uv_dy));
		r_bloom += detect_red(textureGrad(TEXTURE, UV + vec2( 0.0,   px.y), uv_dx, uv_dy));
		r_bloom += detect_red(textureGrad(TEXTURE, UV + vec2( 0.0,  -px.y), uv_dx, uv_dy));
		r_bloom += detect_red(textureGrad(TEXTURE, UV + vec2( px.x,  px.y) * 0.7071, uv_dx, uv_dy));
		r_bloom += detect_red(textureGrad(TEXTURE, UV + vec2(-px.x,  px.y) * 0.7071, uv_dx, uv_dy));
		r_bloom += detect_red(textureGrad(TEXTURE, UV + vec2( px.x, -px.y) * 0.7071, uv_dx, uv_dy));
		r_bloom += detect_red(textureGrad(TEXTURE, UV + vec2(-px.x, -px.y) * 0.7071, uv_dx, uv_dy));

		b_bloom *= 0.125;
		r_bloom *= 0.125;

		// Sinusoidal pulse waveforms
		float p_blue = mix(min_pulse, max_pulse, 0.5 + 0.5 * sin(TIME * pulse_speed));
		float p_red  = mix(min_pulse, max_pulse, 0.5 + 0.5 * sin(TIME * pulse_speed + phase_offset));

		float boost = max(fable_emission_boost, 1.0);

		// Subtle, focused bloom aura that stays close to the runes
		vec3 aura = (b_bloom * blue_glow_tint * p_blue + r_bloom * red_glow_tint * p_red) * (aura_intensity * boost);

		// Gentle, natural modulative pulse on the runes
		float active_p = blue_val > red_val ? p_blue : p_red;
		vec3 lit_core = (blue_val * blue_glow_tint + red_val * red_glow_tint) * (0.45 * boost);
		vec3 rune_lit = mix(base.rgb * min_pulse,
		                    base.rgb * 1.25 + lit_core,
		                    active_p / max_pulse);

		vec3 final_rgb = mix(base.rgb, rune_lit, rune_mask) + aura * (1.0 - rune_mask * 0.4);

		COLOR = vec4(final_rgb, base.a);
	}
}
