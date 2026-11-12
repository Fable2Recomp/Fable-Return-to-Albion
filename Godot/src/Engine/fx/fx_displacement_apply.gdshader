shader_type canvas_item;
render_mode blend_mix;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform sampler2D disp_tex : filter_linear;
uniform vec2 disp_pixel_scale = vec2(0.0);

void fragment() {
	vec4 d = texture(disp_tex, SCREEN_UV);
	vec2 offset_px = vec2(d.g - d.r, d.a - d.b) * disp_pixel_scale;
	if (abs(offset_px.x) + abs(offset_px.y) < 0.05) {
		discard;
	}
	vec2 e = min(SCREEN_UV, 1.0 - SCREEN_UV) / SCREEN_PIXEL_SIZE;
	vec2 f = clamp(e / 127.5, 0.0, 1.0);
	float fade = min(f.x, f.y);
	vec2 uv = SCREEN_UV + offset_px * fade * SCREEN_PIXEL_SIZE;
	uv = clamp(uv, SCREEN_PIXEL_SIZE * 0.5, 1.0 - SCREEN_PIXEL_SIZE * 0.5);
	COLOR = vec4(texture(screen_tex, uv).rgb, 1.0);
}
