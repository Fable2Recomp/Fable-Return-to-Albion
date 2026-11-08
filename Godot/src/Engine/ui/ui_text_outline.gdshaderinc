// THE OUTLINE, Fable 3's PSHADER_GUI_OUTLINED_TEXT_3D (Engine_UiTextContract.Outlines), shared by
// ui_text.gdshader and ui_world_marker.gdshader: alpha is the largest coverage of the 3x3 shipped
// atlas texels around the pixel (c48 = 1 / glyph texture size, param 0x67 0x82C99764), colour the
// run's times the centre coverage, so the one-texel ring outside the ink is the run's alpha in black.

// One outline tap: the coverage at `uv`, through the same edge the centre took (the field's
// one-pixel edge, the re-contrast, or the shipped coverage), at the centre's own gradients.
float ui_text_outline_tap(sampler2D glyph_atlas, sampler2D glyph_field, vec2 uv, vec2 uv_dx, vec2 uv_dy,
		bool from_field, float field_width, float contrast) {
	if (from_field) {
		return clamp((textureGrad(glyph_field, uv, uv_dx, uv_dy).r - 0.5) / field_width + 0.5, 0.0, 1.0);
	}
	float a = textureGrad(glyph_atlas, uv, uv_dx, uv_dy).a;
	return contrast > 0.0 ? clamp((a - 0.5) * contrast + 0.5, 0.0, 1.0) : a;
}

// The outlined coverage: `centre` (the pixel's own) maxed with its eight neighbours one shipped
// texel apart (`texels_per_shipped` texture pixels per shipped texel).
float ui_text_outline_cover(sampler2D glyph_atlas, sampler2D glyph_field, vec2 uv, float centre,
		vec2 uv_dx, vec2 uv_dy, bool from_field, float field_width, float contrast, float texels_per_shipped) {
	float cover = centre;
	vec2 texel_step = texels_per_shipped / vec2(textureSize(glyph_atlas, 0));
	for (int y = -1; y <= 1; y++) {
		for (int x = -1; x <= 1; x++) {
			if (x != 0 || y != 0) {
				cover = max(cover, ui_text_outline_tap(glyph_atlas, glyph_field, uv + vec2(float(x), float(y)) * texel_step,
					uv_dx, uv_dy, from_field, field_width, contrast));
			}
		}
	}
	return cover;
}
