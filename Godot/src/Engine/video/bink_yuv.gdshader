shader_type canvas_item;

// Bink YUV -> RGB, done on the GPU.
//
// The planes arrive exactly as the guest decoded them: three 8-bit images, luma at full
// resolution and chroma at its own independent size. Converting here rather than on the
// CPU saves ~921,600 pixels of arithmetic per frame and lets the sampler do the chroma
// upscale with real filtering instead of nearest-neighbour integer steps.
//
// Chroma is NOT simply half luma — every dimension is independently (x+7) & ~7 — so the
// planes are sampled by normalised UV rather than by any assumed ratio.

uniform sampler2D luma : hint_default_black, filter_linear, source_color;
uniform sampler2D chroma_r : hint_default_black, filter_linear;
uniform sampler2D chroma_b : hint_default_black, filter_linear;

void fragment() {
	// Single-channel textures carry the value in .r.
	float y = texture(luma, UV).r;
	float cr = texture(chroma_r, UV).r;
	float cb = texture(chroma_b, UV).r;

	// BT.601 studio swing, which is what Bink encodes: luma 16..235, chroma centred on
	// 128. Treating it as full range washes the picture out.
	y = (y - 0.0627451) * 1.164384;
	cr = cr - 0.5;
	cb = cb - 0.5;

	COLOR = vec4(
		clamp(y + 1.596027 * cr, 0.0, 1.0),
		clamp(y - 0.391762 * cb - 0.812968 * cr, 0.0, 1.0),
		clamp(y + 2.017232 * cb, 0.0, 1.0),
		1.0);
}
