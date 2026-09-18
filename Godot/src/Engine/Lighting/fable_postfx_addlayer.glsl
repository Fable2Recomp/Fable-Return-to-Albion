#[compute]
#version 450

// The additive effects layer joins the scene — the guest's `off_8331AA14`.
//
// ONE/ONE particles never touch the scene target in the guest: `sub_822749E0` routes a
// SrcBlend==ONE && DestBlend==ONE material to registry bucket 5 (0x82274b88), and
// `Render_ResolveGlobalTargetPass` (0x822C4290) drains that bucket into a HALF-RES
// A8R8G8B8 surface (`sub_8227A440`, format code 2 = D3DFMT_A8R8G8B8, UNORM) cleared to zero
// each frame (0x822c4404..0x822c4414 → D3DDevice_ClearF). The ROP saturates every add, so
// however many quads stack, a pixel of the layer is at most 1.0 per channel — that is the
// overlap invariance: a ten-quad spell core is as bright as a one-quad one. The layer then
// reaches the frame as `scene + layer × g_CompositeBrightness` in DOWNSCALECOMPOSITE (blob
// 0162, the bloom and exposure input) and as `scene + layer × g_ActualFramebufferBrightness`
// in the additive-effects tonemap (blob 0509) — one sum, consumed twice. Adding it into the
// colour buffer once, before the quarter downscale, feeds both.
//
// CLAMPED PER TEXEL, BEFORE THE FILTER — because the guest's ROP clamps before ITS filter.
// Our layer accumulates in FP16 and can hold any value; for non-negative adds, per-draw
// saturation and min(sum, 1) agree for a single texel, but not once the layer is filtered
// between accumulation and clamp — and this half-res layer read at full res always is.
// Filtering unclamped taps spreads an over-range core outward at full intensity.
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict image2D color_image;
layout(set = 0, binding = 1) uniform sampler2D layer_tex;

layout(push_constant, std430) uniform Params {
	ivec2 size;
	float gain;
	float pad0;
} push;

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= push.size.x || coord.y >= push.size.y) {
		return;
	}

	vec2 uv = (vec2(coord) + 0.5) / vec2(push.size);

	// The bilinear the hardware would have done, with the guest's ceiling applied to each tap
	// first. A NaN texel would composite to black and the histogram would bin it at zero: one
	// bad particle vertex must not be able to flash a frame, and clamp() does not define NaN.
	ivec2 lsize = textureSize(layer_tex, 0);
	vec2 t = uv * vec2(lsize) - 0.5;
	vec2 base = floor(t);
	vec2 f = t - base;
	ivec2 b = ivec2(base);
	ivec2 hi = lsize - ivec2(1);
	vec3 taps[4];
	for (int i = 0; i < 4; ++i) {
		ivec2 at = clamp(b + ivec2(i & 1, i >> 1), ivec2(0), hi);
		vec3 tap = texelFetch(layer_tex, at, 0).rgb;
		taps[i] = (any(isnan(tap)) || any(isinf(tap)))
			? vec3(0.0)
			: clamp(tap, vec3(0.0), vec3(1.0));
	}

	vec3 layer = mix(mix(taps[0], taps[1], f.x), mix(taps[2], taps[3], f.x), f.y);
	vec4 c = imageLoad(color_image, coord);
	imageStore(color_image, coord, vec4(c.rgb + layer * push.gain, c.a));
}
