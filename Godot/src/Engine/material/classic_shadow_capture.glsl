#[compute]
#version 450

// Copies a cascade viewport's depth into the map the classic shaders sample. Godot's world is
// the guest's rotated ((x, y, z) -> (-x, z, y)), so a camera placed on the guest's light basis
// renders the guest's map as it is; flip_x stays for a viewport whose u runs the other way.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D depth_tex;
layout(r32f, set = 0, binding = 1) uniform restrict writeonly image2D out_image;

// The title's cascade pass draws its casters with D3DRS_DEPTHBIAS and SLOPESCALEDEPTHBIAS set
// negative on its inverted depth (`sub_8228DCC8`: -0.001/-2.0 and -0.005/-5.0), pushing every
// stored caster away from the light. Godot's viewport has no such state, so the push is
// applied here: the constant, and the slope scaled by the map's own depth gradient.
layout(push_constant, std430) uniform Params {
	ivec2 size;
	int flip_x;
	float bias_constant;
	float bias_slope;
	float pad1;
	float pad2;
	float pad3;
} params;

float depth_at(ivec2 at) {
	at = clamp(at, ivec2(0), params.size - 1);
	return texelFetch(depth_tex, at, 0).r;
}

void main() {
	ivec2 p = ivec2(gl_GlobalInvocationID.xy);
	if (p.x >= params.size.x || p.y >= params.size.y) {
		return;
	}
	ivec2 source = p;
	if (params.flip_x != 0) {
		source.x = params.size.x - 1 - p.x;
	}
	float d = depth_at(source);
	float slope = max(
		max(abs(d - depth_at(source + ivec2(1, 0))), abs(d - depth_at(source - ivec2(1, 0)))),
		max(abs(d - depth_at(source + ivec2(0, 1))), abs(d - depth_at(source - ivec2(0, 1)))));
	float pushed = max(d - params.bias_constant - params.bias_slope * slope, 0.0);
	imageStore(out_image, p, vec4(pushed, 0.0, 0.0, 0.0));
}
