#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict readonly image2D color_image;
layout(rgba16f, set = 0, binding = 1) uniform restrict writeonly image2D quarter_image;

layout(set = 0, binding = 2, std430) restrict readonly buffer StateBuffer {
	float v[];
} state;

layout(push_constant, std430) uniform Params {
	ivec2 full_size;
	ivec2 quarter_size;
} params;

const int S_APPLIED_FRAME = 16;

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= params.quarter_size.x || coord.y >= params.quarter_size.y) {
		return;
	}

	vec3 total = vec3(0.0);
	for (int y = 0; y < 4; y++) {
		for (int x = 0; x < 4; x++) {
			ivec2 at = min(coord * 4 + ivec2(x, y), params.full_size - 1);
			total += imageLoad(color_image, at).rgb;
		}
	}

	float applied = state.v[S_APPLIED_FRAME];
	imageStore(quarter_image, coord, vec4(total * (1.0 / 16.0) * applied, 1.0));
}
