#[compute]
#version 450

// Targeting highlight bleed, one axis — `OUTLINE_BLEED_H_PIXEL_SHADER` and
// `OUTLINE_BLEED_V_PIXEL_SHADER`, Shaders.sbk entries 168 and 169 (the pixel shaders
// `Render_PostProcessFilterPassA/B` bind at 0x82a6a598 / 0x82a6aa90; blob files 0141 and 0142).
// The two are identical but for the axis.
//
// Fifteen taps at whole-texel distances -7..+7. The fetch offsets read -6.5..+7.5 with a 0.5 on
// the other axis, which on a D3D9-convention quad lands every tap on a texel CENTRE: no tap mixes
// two texels. Tap d takes `g_OutlineCoefficients[d + 7]` (c116..c119, binding 161), which is
// `g_FilterKernelConsts` as `sub_82A699B8` builds it once: `kernel[i] = 1 / exp((i - 7)^2 / 49)`
// for i = 0..14 (0x82a69a1c..0x82a69a38), then every entry divided by their sum
// (0x82a69a50..0x82a69aa0).
//
// The width is a gain, not a spread. The sum is multiplied by `g_OutlineScaleFactor.x` (c31,
// binding 165), which `sub_82197AE8` derives from the stated width `dword_83318558` and hands to
// BOTH passes (0x82197c98, 0x82197cac). It is >= 1, so a wider stated width lifts the faint tail
// of the kernel over the 8-bit floor and the halo reaches further — with the same fifteen taps.
//
// The target is 8-bit UNORM. Each pass resolves into the A8R8G8B8 half-res surface, so the
// result saturates at 1 and quantises to 1/255 after EACH axis. Both are part of the look: the
// scale drives the core into saturation, and the quantisation is what ends the tail.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D src_tex;
layout(rgba16f, set = 0, binding = 1) uniform restrict writeonly image2D dst_image;

layout(push_constant, std430) uniform Params {
	ivec2 size;
	ivec2 dir;
	float scale;
	float pad0;
	float pad1;
	float pad2;
} push;

const int Taps = 7;

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= push.size.x || coord.y >= push.size.y) {
		return;
	}

	ivec2 hi = textureSize(src_tex, 0) - ivec2(1);
	vec4 sum = vec4(0.0);
	float norm = 0.0;
	for (int d = -Taps; d <= Taps; ++d) {
		float w = 1.0 / exp(float(d * d) / 49.0);
		ivec2 at = clamp(coord + push.dir * d, ivec2(0), hi);
		sum += texelFetch(src_tex, at, 0) * w;
		norm += w;
	}

	vec4 v = clamp(sum / norm * push.scale, vec4(0.0), vec4(1.0));
	imageStore(dst_image, coord, round(v * 255.0) / 255.0);
}
