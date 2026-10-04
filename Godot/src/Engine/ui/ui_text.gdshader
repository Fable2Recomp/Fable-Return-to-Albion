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

// Texture pixels per SHIPPED atlas texel — the atlas is supersampled at load.
uniform float supersample = 1.0;

uniform vec4 colour = vec4(1.0);

// Same contract as ui_quad.gdshader: min.xy, max.xy in the laid-out UI space.
uniform vec4 clip_rect = vec4(-1e9, -1e9, 1e9, 1e9);

// The element's RESOLVED opacity, written by the clone binding exactly as it writes the quads'.
//
// TEXT IGNORED THE RESOLVE UNTIL THIS EXISTED. The binding sets `opacity` on every bound
// mesh's material, but this shader had no such uniform, so the write was a silent no-op and a
// text element at accumulated opacity 0 rendered fully opaque — four button prompts drawn over
// each other with only one icon visible (the icons are quads, which do honour it), and hidden
// pages' text bleeding through wherever a book selects pages by opacity.
uniform float opacity = 1.0;

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

	// LEGIBLE AT ANY SIZE. The font is a 720p coverage mask whose edge is about one shipped
	// texel wide; drawn at 2x or 3x that edge is a 2-3 pixel blur. `magnified` is screen pixels
	// per shipped texel, and the coverage is re-contrasted about its half point by that
	// factor, so the edge is one SCREEN pixel wide whatever the display. At or below 1:1 it
	// is the shipped coverage untouched.
	vec2 shipped = UV * vec2(textureSize(atlas, 0)) / supersample;
	float magnified = 1.0 / max(max(fwidth(shipped.x), fwidth(shipped.y)), 1e-5);
	if (magnified > 1.0) {
		texel.a = clamp((texel.a - 0.5) * min(magnified, 8.0) + 0.5, 0.0, 1.0);
	}

	// PER-RUN TINT RIDES THE VERTEX COLOUR, as a multiplier over the element's own. Inline
	// `<b>`/`<s1>`..`<s4>` colour the run they enclose, and they can appear several times in
	// one label — so the tint cannot be a uniform without splitting the mesh per colour. An
	// unmarked run is white and multiplies to nothing, which is what leaves `colour` as the
	// live channel Engine_UiTheme.Retint writes.
	vec4 tint = colour * COLOR;

	// The scissor.
	if (texel.a * tint.a * opacity < 0.01) {
		discard;
	}

	ALBEDO = tint.rgb;
	ALPHA = texel.a * tint.a * opacity;
}
