#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict image2D color_image;

layout(set = 0, binding = 1, std430) restrict readonly buffer StateBuffer {
	float v[];
} state;

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

layout(set = 0, binding = 3) uniform sampler2D bloom_tex;
layout(set = 0, binding = 4) uniform sampler2D dof_tex;
layout(set = 0, binding = 5) uniform sampler2D depth_tex;

layout(push_constant, std430) uniform Params {
	ivec2 size;
	float pad0;
	float pad1;
	vec4 depth;
} push;

const int S_APPLIED_FRAME = 16;
const int S_LUT = 544;
const int CURVE_SIZE = 512;
const vec3 LUMINANCE_WEIGHTS = vec3(0.2125, 0.7154, 0.0721);

float linear_depth(float z) {
	float ndc_z = z;
	float view_z = push.depth.x * ndc_z + push.depth.y;
	float view_w = push.depth.z * ndc_z + push.depth.w;
	float safe_w = abs(view_w) > 1e-6 ? view_w : 1e-6;
	return -view_z / safe_w;
}

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= push.size.x || coord.y >= push.size.y) {
		return;
	}

	vec4 texel = imageLoad(color_image, coord);
	vec3 c = texel.rgb * state.v[S_APPLIED_FRAME];
	vec2 uv = (vec2(coord) + 0.5) / vec2(push.size);

	if (params.bits.y > 0.5) {
		float z = texelFetch(depth_tex, coord, 0).r;
		float d = linear_depth(z);
		float near_w = min(max((params.dof.y - d) * params.dof.x, 0.0), params.dof2.y);
		float far_w = min(max((d - params.dof.z) * params.dof.w, 0.0), params.dof2.x);
		float w = (d < params.dof.y) ? near_w : ((d > params.dof.z) ? far_w : 0.0);
		w *= 1.0 - clamp(d - 2550.0, 0.0, 1.0);
		vec3 blurred = texture(dof_tex, uv).rgb;
		c = mix(c, blurred, w);
	}

	vec3 col = c;
	if (params.bits.x > 0.5) {
		float l = dot(c, LUMINANCE_WEIGHTS);
		int i0 = clamp(int(l * params.misc.w * float(CURVE_SIZE)), 0, CURVE_SIZE - 1);
		float gain = state.v[S_LUT + i0];
		col = vec3(l) + clamp(l * params.misc.x, 0.0, 1.0) * (c - vec3(l));
		col = clamp(gain * params.sat.y * col, 0.0, 1.0);
		vec3 bloom = clamp(texture(bloom_tex, uv).rgb * params.composite.x, 0.0, 1.0);
		col = col + bloom * (1.0 - col);
	} else {
		col = clamp(c, 0.0, 1.0);
	}

	if (params.bits.z > 0.5) {
		float lc = dot(col, LUMINANCE_WEIGHTS);
		col = col + params.sat.x * (col - vec3(lc));
	}

	col *= params.misc.z;
	col = clamp(col, 0.0, 1.0);
	if (params.misc.y > 0.0) {
		col = pow(max(col, vec3(1e-6)), vec3(params.misc.y));
	}

	imageStore(color_image, coord, vec4(col, texel.a));
}
