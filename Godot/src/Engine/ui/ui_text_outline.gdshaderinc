// THE OUTLINE, Fable 3's PSHADER_GUI_OUTLINED_TEXT_3D (Engine_UiTextContract.Outlines), shared by
// ui_text.gdshader and ui_world_marker.gdshader: alpha is the largest coverage of the 3x3 shipped
// atlas texels around the pixel (c48 = 1 / glyph texture size, param 0x67 0x82C99764), colour the
// run's times the centre coverage, so the one-texel ring outside the ink is the run's alpha in black.

// The outlined coverage: `centre` (the pixel's own) maxed with its eight neighbours one shipped
// texel apart (`texel_step` in UV units, precomputed in uniform control flow).
// Both coverage (`.a`) and signed distance (`.g`) live in the single `glyph_atlas` texture so UI text
// binds one sampler per material on D3D12 while keeping `from_field` hoisted outside the 8-tap loop.
float ui_text_outline_cover(sampler2D glyph_atlas, vec2 uv, float centre,
		vec2 uv_dx, vec2 uv_dy, bool from_field, float field_width, float contrast, vec2 texel_step) {
	float cover = centre;
	if (from_field) {
		for (int y = -1; y <= 1; y++) {
			for (int x = -1; x <= 1; x++) {
				if (x != 0 || y != 0) {
					float d = textureGrad(glyph_atlas, uv + vec2(float(x), float(y)) * texel_step, uv_dx, uv_dy).g;
					cover = max(cover, clamp((d - 0.5) / field_width + 0.5, 0.0, 1.0));
				}
			}
		}
	} else {
		for (int y = -1; y <= 1; y++) {
			for (int x = -1; x <= 1; x++) {
				if (x != 0 || y != 0) {
					float a = textureGrad(glyph_atlas, uv + vec2(float(x), float(y)) * texel_step, uv_dx, uv_dy).a;
					cover = max(cover, contrast > 0.0 ? clamp((a - 0.5) * contrast + 0.5, 0.0, 1.0) : a);
				}
			}
		}
	}
	return cover;
}
