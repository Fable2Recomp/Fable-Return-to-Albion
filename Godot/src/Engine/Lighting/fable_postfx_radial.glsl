#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D src_tex;
layout(rgba16f, set = 0, binding = 1) uniform restrict writeonly image2D dst_image;

layout(set = 0, binding = 2, std140) uniform PostFxParams {
	vec4 frame;
	vec4 bounds;
	vec4 pct;
	vec4 contrast;
	vec4 bloom;
	vec4 radial;
	vec4 dof;
	vec4 dof2;
	vec4 sat;
	vec4 misc;
	vec4 bits;
	vec4 depth;
	vec4 composite;
	vec4 reserved0;
	vec4 reserved1;
	vec4 reserved2;
} params;

layout(push_constant, std430) uniform Params {
	ivec2 size;
	float pad0;
	float pad1;
	vec2 center;
	vec2 pad2;
} push;

const float K = 0.571429;

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= push.size.x || coord.y >= push.size.y) {
		return;
	}

	vec2 uv = (vec2(coord) + 0.5) / vec2(push.size);
	vec2 center = push.center;
	float decay = (params.radial.z - 1.0) * K + 1.0;
	float zstep = (params.radial.w - 1.0) * K + 1.0;
	vec2 dir = uv - center;
	// NORMALISED BY THE ACCUMULATED WEIGHT, not by the constant K. The taps are weighted
	// 0.5 * decay^t, so their sum depends entirely on decay: at decay 1.0 it is 7.0, and
	// scaling by K = 4/7 left the pass with a 4x gain that washed the frame out. Dividing by
	// the sum makes it unity gain for any falloff, and the ray strength is then what
	// RadialFactor * RayGain states at composite time. K still remaps the two parameters.
	vec3 accum = vec3(0.0);
	float total = 0.0;
	float weight = 0.5;
	float scale = 1.0;
	for (int t = 0; t < 14; t++) {
		vec2 tap = center + dir * scale;
		accum += texture(src_tex, tap).rgb * weight;
		total += weight;
		scale *= zstep;
		weight *= decay;
	}

	imageStore(dst_image, coord, vec4(accum / max(total, 1e-4), 1.0));
}
