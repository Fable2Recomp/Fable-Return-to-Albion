#[compute]
#version 450

// The guest's tone curve, applied the way the guest applies it.
//
// THIS IS A GAIN ON LUMINANCE, NOT A PER-CHANNEL TRANSFER. The shipped LUT is 512x1 R32F — one
// channel — its entries are stored already divided by their own input, and the recompiled
// tone-map blob 0500 samples it once with the frame's luminance and multiplies the whole rgb by
// that single result. A per-channel curve would pull each channel toward white independently and
// bleach the hue out of every highlight; this preserves the hue and rescales the brightness.
//
// The curve is evaluated directly rather than through a 512-entry table: with the shipped default
// ContrastEnhancement of 0.0 the stored entry reduces to plain extended Reinhard, which is three
// instructions and needs no texture.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict image2D color_image;

// 32 BYTES, AND THE SIZE IS PART OF THE CONTRACT. `Engine_ExposureEffect.Push` builds one 32-byte
// block for every pass. A shader that declares fewer does not ignore the tail — Vulkan rejects the
// dispatch outright and the pass never runs, which looks identical to a tone curve working on a
// dark scene.
layout(push_constant, std430) uniform Params {
	ivec2 size;
	float pad0;
	float pad1;
	float pad2;
	float pad3;
	float pad4;
	float pad5;
} params;

// THE CURVE TERMS COME FROM THE GPU, NOT FROM A PUSH CONSTANT. Feeding them as push constants
// meant the CPU had to know them, which meant reading the histogram back every frame, which
// flushed the device and cost the frame rate. `fable_exposure_reduce` writes this buffer and
// nothing ever leaves the GPU.
layout(set = 0, binding = 1, std430) restrict readonly buffer StateBuffer {
	float v[];
} state;

const vec3 LUMINANCE_WEIGHTS = vec3(0.2125, 0.7154, 0.0721);

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= params.size.x || coord.y >= params.size.y) {
		return;
	}

	vec4 texel = imageLoad(color_image, coord);
	float lum = dot(max(texel.rgb, vec3(0.0)), LUMINANCE_WEIGHTS);

	// x = luminance * key / previousExposure, then `((x/W^2 + 1) * x) / (x + 1)` — the entry
	// expression at 0x8217E59C with the reciprocal-square white point from 0x8217E530.
	float x = lum * state.v[5];
	float mapped = ((x * state.v[6]) + 1.0) * x / (x + 1.0);
	float gain = lum > 1e-6 ? mapped / lum : 1.0;

	// BRIGHTNESS IS NOT OPTIONAL. The shipped line is
	//     colour = saturate(brightness * LUT(luminance * 0.25) * colour)
	// and the curve above already divided by the exposure scalar, so leaving this out darkens the
	// whole frame by that scalar — sixteen-fold wherever the loop has driven it to its ceiling,
	// which is exactly what a dark interior does.
	//
	// The guest SATURATES here — the tone-map blob ends on FClamp(x, 0, 1) — and then applies its
	// own display gamma. We stop at the clamp: Godot owns the output transform, and its tonemapper
	// is set to Linear so nothing compresses this a second time.
	vec3 mapped_rgb = texel.rgb * gain * state.v[7];
	imageStore(color_image, coord, vec4(clamp(mapped_rgb, 0.0, 1.0), texel.a));
}
