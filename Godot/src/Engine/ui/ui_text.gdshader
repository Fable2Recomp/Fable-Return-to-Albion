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

// THE GLYPH EDGE FROM A DISTANCE FIELD (Engine_SdfField): the atlas's signed distance, built on
// the player's machine from the shipped coverage, 0.5 on the coverage's own half contour. The
// edge is resolved per screen pixel at any magnification and any minification, so text stays
// crisp at 1440p/2160p without the coverage's bilinear blur. Absent, the coverage path below.
uniform sampler2D distance_field : filter_linear_mipmap, repeat_disable;
uniform bool use_distance_field = false;

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

// A menu seated in one player's half on split screen (Engine_UiPresentationHost.MenuLayer):
// scale in xy, offset in zw. The clip compares in the menu's own laid-out space, so the seat
// comes back out of the world position. Identity everywhere else. A MATERIAL uniform, not an
// `instance uniform`: with the separate render thread, an instance uniform's property list is
// fetched through the render command queue into the caller's stack, and Node.duplicate (the
// UI's template clones) crashed in InstanceUniforms::get_property_list (2026-10-20).
uniform vec4 clip_seat = vec4(1.0, 1.0, 0.0, 0.0);

varying vec2 clip_world;

void vertex() {
	clip_world = ((MODEL_MATRIX * vec4(VERTEX, 1.0)).xy - clip_seat.zw) / clip_seat.xy;

	// GLYPHS SIT ON WHOLE PIXELS, as the 360 drew them: a 2-texel stem there is two solid pixels.
	// The canvas camera frames 712.1 units in a 720-pixel viewport and elements sit at fractional
	// positions, so a glyph quad lands between pixels and every stem smears over three or four
	// partly covered ones, which read heavier than solid ones once blended in linear light. Each
	// corner goes to the nearest pixel edge, so a quad DrawWidth texels wide covers a whole number
	// of pixels. Orthographic cameras only: a mirrored screen's perspective text is left alone.
	if (PROJECTION_MATRIX[3][3] == 1.0) {
		vec4 clip = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
		vec2 pixel = round((clip.xy / clip.w * 0.5 + 0.5) * VIEWPORT_SIZE);
		clip.xy = (pixel / VIEWPORT_SIZE * 2.0 - 1.0) * clip.w;
		POSITION = clip;
	}
}

void fragment() {
	if (clip_world.x < clip_rect.x || clip_world.x > clip_rect.z ||
		clip_world.y < clip_rect.y || clip_world.y > clip_rect.w) {
		discard;
	}

	vec4 texel = texture(atlas, UV);

	// Screen pixels per SHIPPED atlas texel.
	vec2 shipped = UV * vec2(textureSize(atlas, 0)) / supersample;
	float magnified = 1.0 / max(max(fwidth(shipped.x), fwidth(shipped.y)), 1e-5);

	// THE FIELD ONLY WHERE THE TEXT IS MAGNIFIED. At or near the shipped size a screen pixel spans
	// a whole shipped texel, the field is read from its mips, and the mip average erodes every
	// stroke thinner than a pixel below the 0.5 contour — Fable 3's list rows lost the bar of 'H'
	// and the bowls of 'B' at 1280x720. The shipped coverage is exact there.
	bool from_field = use_distance_field && magnified > 1.25;
	if (from_field) {
		// One screen pixel of anti-aliasing across the 0.5 contour: the field's own change per
		// pixel, so the edge is as sharp at 3x as at 1.25x and never wider than a pixel.
		float distance = texture(distance_field, UV).r;
		float width = max(fwidth(distance), 1e-4);
		texel.a = clamp((distance - 0.5) / width + 0.5, 0.0, 1.0);
	} else if (magnified > 0.75) {
		// LEGIBLE AT ANY SIZE. The font is a 720p coverage mask whose edge is about one shipped
		// texel wide; magnified, the coverage is re-contrasted about its half point by that factor
		// so the edge is one SCREEN pixel wide.
		//
		// NEAR 1:1 THE FACTOR STAYS AT 1.3 (a 720p window, most phones). The mip read there is not
		// the shipped coverage: the cubic upsample leaves each edge a fringe of about 0.11 one texel
		// out and 0.89 inside, and on the menus' dark leather that fringe shows at about a third of
		// the text's brightness, so a 2-texel stem reads as nearly three. 1.3 takes it off and
		// barely moves the font's own antialiasing. Well below 1:1 the coverage is left alone.
		float contrast = max(min(magnified, 8.0), 1.3);
		texel.a = clamp((texel.a - 0.5) * contrast + 0.5, 0.0, 1.0);
	}

	// PER-RUN TINT RIDES THE VERTEX COLOUR, as a multiplier over the element's own. Inline
	// `<b>`/`<s1>`..`<s4>` colour the run they enclose, and they can appear several times in
	// one label — so the tint cannot be a uniform without splitting the mesh per colour. An
	// unmarked run is white and multiplies to nothing, which is what leaves `colour` as the
	// live channel Engine_UiTheme.Retint writes.
	vec4 tint = colour * COLOR;

	// COVERAGE BLENDS AS THE 360 BLENDED IT, in gamma space. Godot mixes in linear light, where a
	// half-covered pixel of light text on a dark page shows at about three quarters of the text's
	// brightness, and dark text on parchment fades the other way. Bright text takes its coverage to
	// the 2.2 power and dark text the mirror of that, so a partly covered edge looks as it did on the
	// console. Solid and empty pixels are unchanged.
	// The distance field's one-pixel edge is left as it is drawn.
	if (!from_field) {
		float bright = clamp(dot(tint.rgb, vec3(0.2126, 0.7152, 0.0722)) * 2.0, 0.0, 1.0);
		texel.a = mix(1.0 - pow(1.0 - texel.a, 2.2), pow(texel.a, 2.2), bright);
	}

	// The scissor.
	if (texel.a * tint.a * opacity < 0.01) {
		discard;
	}

	ALBEDO = tint.rgb;
	ALPHA = texel.a * tint.a * opacity;
}
