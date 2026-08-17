#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D depth_tex;
layout(r32f, set = 0, binding = 1) uniform restrict writeonly image2D out_image;

layout(push_constant, std430) uniform Params {
	ivec2 size;
	float pad0;
	float pad1;
} params;

void main() {
	ivec2 p = ivec2(gl_GlobalInvocationID.xy);
	if (p.x >= params.size.x || p.y >= params.size.y) {
		return;
	}
	float d = texelFetch(depth_tex, p, 0).r;
	imageStore(out_image, p, vec4(d, 0.0, 0.0, 0.0));
}
