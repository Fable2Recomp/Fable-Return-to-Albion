shader_type canvas_item;
render_mode blend_mix;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform sampler2D disp_tex : hint_default_black, filter_linear;
uniform vec2 disp_pixel_scale = vec2(0.0);
// The back-buffer region copied this frame, in screen uv (min, max); the whole screen unless
// android_displacement_rect copies only part (AndroidDisplacementRect).
uniform vec4 copy_rect = vec4(0.0, 0.0, 1.0, 1.0);

void fragment() {
	// Zero-discard `canvas_item` blend path: output `COLOR = vec4(0.0)` when displacement is
	// negligible so `blend_mix` leaves the destination buffer unchanged without `discard;`.
	vec4 d = textureLod(disp_tex, SCREEN_UV, 0.0);
	vec2 offset_px = vec2(d.g - d.r, d.a - d.b) * disp_pixel_scale;
	bool should_discard = (abs(offset_px.x) + abs(offset_px.y) < 0.05);
	COLOR = vec4(0.0);
	if (!should_discard) {
		vec2 e = min(SCREEN_UV, 1.0 - SCREEN_UV) / SCREEN_PIXEL_SIZE;
		vec2 f = clamp(e / 127.5, 0.0, 1.0);
		float fade = min(f.x, f.y);
		vec2 uv = SCREEN_UV + offset_px * fade * SCREEN_PIXEL_SIZE;
		uv = clamp(uv, max(copy_rect.xy, vec2(0.0)) + SCREEN_PIXEL_SIZE * 0.5, min(copy_rect.zw, vec2(1.0)) - SCREEN_PIXEL_SIZE * 0.5);
		COLOR = vec4(textureLod(screen_tex, uv, 0.0).rgb, 1.0);
	}
}
