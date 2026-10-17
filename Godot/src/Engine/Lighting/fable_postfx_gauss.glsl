#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict readonly image2D src_image;
layout(rgba16f, set = 0, binding = 1) uniform restrict writeonly image2D dst_image;

layout(push_constant, std430) uniform Params {
	ivec2 size;
	ivec2 dir;
	float w0;
	float w1;
	float w2;
	float w3;
	float w4;
	float w5;
	float w6;
	float pad0;
} params;

vec4 tap(ivec2 at) {
	return imageLoad(src_image, clamp(at, ivec2(0), params.size - 1));
}

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= params.size.x || coord.y >= params.size.y) {
		return;
	}

	float w[7] = float[7](params.w0, params.w1, params.w2, params.w3, params.w4, params.w5, params.w6);
	vec4 c = tap(coord) * w[0];
	for (int k = 1; k < 7; k++) {
		ivec2 o = params.dir * k;
		c += tap(coord + o) * w[k];
		c += tap(coord - o) * w[k];
	}

	imageStore(dst_image, coord, c);
}
