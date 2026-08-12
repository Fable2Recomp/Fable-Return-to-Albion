#[compute]
#version 450

// The guest's luminance measurement, as a compute pass.
//
// `sub_822AA9A0` bins a QUARTER-WIDTH by QUARTER-HEIGHT R32F luminance surface — produced by a
// CONVERTTOLUMINANCE resolve of the previous frame — into 512 linear buckets over [0, 4]. This
// dispatches over that same quarter-resolution grid and box-averages the 4x4 block behind each
// texel, because the guest's surface is a RESOLVE and not a point sample: metering a single texel
// out of every sixteen would let one specular pinprick move the whole frame's exposure.

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict readonly image2D color_image;

layout(set = 0, binding = 1, std430) restrict buffer HistogramBuffer {
	uint bins[];
} histogram;

layout(push_constant, std430) uniform Params {
	ivec2 full_size;
	float scale;      // buckets / range = 512 / 4 = 128
	float range;

	// Padded to match the apply pass's block. Both dispatches share one push-constant size, and
	// Vulkan rejects a push larger than the range the shader declares.
	float pad0;
	float pad1;
	float pad2;
	float pad3;
} params;

// 256 invocations, 512 bins — two apiece, and the block is accumulated in shared memory first so
// the global buffer takes 512 atomics per group instead of one per pixel.
shared uint local_bins[512];

// Rec.709, the weights the shipped tone-map blob dots with:
//     OpDot(rgb, (0.2125, 0.7154, 0.0721))
const vec3 LUMINANCE_WEIGHTS = vec3(0.2125, 0.7154, 0.0721);

void main() {
	uint lid = gl_LocalInvocationIndex;
	local_bins[lid] = 0u;
	local_bins[lid + 256u] = 0u;
	barrier();

	ivec2 quarter = ivec2(gl_GlobalInvocationID.xy);
	ivec2 quarter_size = max(params.full_size / 4, ivec2(1));

	if (quarter.x < quarter_size.x && quarter.y < quarter_size.y) {
		vec3 total = vec3(0.0);
		for (int y = 0; y < 4; y++) {
			for (int x = 0; x < 4; x++) {
				ivec2 at = min(quarter * 4 + ivec2(x, y), params.full_size - 1);
				total += imageLoad(color_image, at).rgb;
			}
		}

		float lum = dot(total * (1.0 / 16.0), LUMINANCE_WEIGHTS);

		// `822aab0c`-`822aab50`: max against 0, min against the range, multiply by N/range, then
		// TRUNCATE. Clamped to the last real bucket so a saturated pixel cannot run off the end.
		uint bucket = uint(clamp(lum, 0.0, params.range) * params.scale);
		atomicAdd(local_bins[min(bucket, 511u)], 1u);
	}

	barrier();
	atomicAdd(histogram.bins[lid], local_bins[lid]);
	atomicAdd(histogram.bins[lid + 256u], local_bins[lid + 256u]);
}
