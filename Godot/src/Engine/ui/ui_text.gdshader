shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;

// The material every piece of UI text draws through. Exists so text can be CLIPPED — the guest
// masks text by the same mechanism it masks quads, at a different base (the type-5 text object
// resolves its mask into +148 and caches the handle at +152).
//
// Unshaded because a glyph atlas is emissive art, and an alpha SCISSOR rather than blending
// because the atlas is padded with transparent black — without the cut, the bilinear tail of
// the neighbouring cell haloes every character.

uniform sampler2D atlas : source_color, filter_linear_mipmap, repeat_disable;

uniform vec4 colour = vec4(1.0);

// Same contract as ui_quad.gdshader: min.xy, max.xy in the laid-out UI space.
uniform vec4 clip_rect = vec4(-1e9, -1e9, 1e9, 1e9);

varying vec2 clip_world;

void vertex() {
	clip_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xy;
}

void fragment() {
	if (clip_world.x < clip_rect.x || clip_world.x > clip_rect.z ||
		clip_world.y < clip_rect.y || clip_world.y > clip_rect.w) {
		discard;
	}

	vec4 texel = texture(atlas, UV);

	// The scissor.
	if (texel.a * colour.a < 0.01) {
		discard;
	}

	ALBEDO = colour.rgb;
	ALPHA = texel.a * colour.a;
}
