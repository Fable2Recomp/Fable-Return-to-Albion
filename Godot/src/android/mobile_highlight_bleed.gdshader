// Targeting highlight bleed, one axis, drawn by the canvas (Engine_HighlightCanvas) where the post
// chain cannot run. The same fifteen taps, kernel, gain and 8-bit quantisation as
// fable_postfx_highlightblur.glsl; the horizontal pass also does fable_postfx_highlightprep.glsl's
// work, looking each tap's slot up in the colour table.
shader_type canvas_item;
render_mode blend_disabled, unshaded;

uniform sampler2D src_tex : filter_nearest;

/// (1, 0) or (0, 1).
uniform vec2 dir = vec2(1.0, 0.0);

/// `g_OutlineScaleFactor` (Engine_EntityHighlightLayer.WidthScale).
uniform float scale = 1.0;

/// True on the first pass: src_tex is the silhouette layer, R = slot / 8.
uniform bool prep = false;

/// Per slot: colour (rgb, alpha) of `g_OutliningColour`.
uniform vec4 colours[8];

const int Taps = 7;

vec4 at(ivec2 p, ivec2 hi) {
	vec4 v = texelFetch(src_tex, clamp(p, ivec2(0), hi), 0);
	if (!prep) {
		return v;
	}

	int slot = int(round(v.r * 8.0));
	return slot >= 1 && slot <= 8 ? colours[slot - 1] : vec4(0.0);
}

void fragment() {
	ivec2 hi = textureSize(src_tex, 0) - ivec2(1);
	ivec2 coord = ivec2(FRAGCOORD.xy);
	ivec2 axis = ivec2(dir);
	vec4 sum = vec4(0.0);
	float norm = 0.0;
	for (int d = -Taps; d <= Taps; ++d) {
		float w = 1.0 / exp(float(d * d) / 49.0);
		sum += at(coord + axis * d, hi) * w;
		norm += w;
	}

	vec4 v = clamp(sum / norm * scale, vec4(0.0), vec4(1.0));
	COLOR = round(v * 255.0) / 255.0;
}
