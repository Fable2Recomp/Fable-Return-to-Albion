#[compute]
#version 450

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict readonly image2D quarter_image;

layout(set = 0, binding = 1, std430) restrict buffer HistogramBuffer {
	uint bins[];
} histogram;

layout(push_constant, std430) uniform Params {
	ivec2 size;
	float pad0;
	float pad1;
} params;

const float RANGE = 4.0;
const float BIN_SCALE = 128.0;
const vec3 LUMINANCE_WEIGHTS = vec3(0.2125, 0.7154, 0.0721);

shared uint local_bins[513];

void main() {
	uint lid = gl_LocalInvocationIndex;
	local_bins[lid] = 0u;
	local_bins[lid + 256u] = 0u;
	if (lid == 0u) {
		local_bins[512] = 0u;
	}

	memoryBarrierShared();
	barrier();

	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x < params.size.x && coord.y < params.size.y) {
		vec3 c = imageLoad(quarter_image, coord).rgb;
		float l = dot(max(c, vec3(0.0)), LUMINANCE_WEIGHTS);
		if (!(l > 0.0)) {
			l = 0.0;
		}
		if (l > RANGE) {
			l = RANGE;
		}
		uint b = min(uint(l * BIN_SCALE), 512u);
		atomicAdd(local_bins[b], 1u);
	}

	memoryBarrierShared();
	barrier();

	atomicAdd(histogram.bins[lid], local_bins[lid]);
	atomicAdd(histogram.bins[lid + 256u], local_bins[lid + 256u]);
	if (lid == 0u) {
		atomicAdd(histogram.bins[512], local_bins[512]);
	}
}
