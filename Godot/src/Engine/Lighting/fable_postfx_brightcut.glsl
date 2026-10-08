#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict readonly image2D quarter_image;
layout(rgba16f, set = 0, binding = 1) uniform restrict readonly image2D rays_image;
layout(rgba16f, set = 0, binding = 2) uniform restrict readonly image2D occl_image;

layout(set = 0, binding = 3, std430) restrict readonly buffer StateBuffer {
	float v[];
} state;

layout(set = 0, binding = 4, std140) uniform PostFxParams {
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

layout(rgba16f, set = 0, binding = 5) uniform restrict writeonly image2D dst_image;

layout(push_constant, std430) uniform Params {
	ivec2 size;
	float pad0;
	float pad1;
} push;

const int S_THRESHOLD = 14;
const int S_EXPOSURE_FRAME = 15;
const vec3 LUMINANCE_WEIGHTS = vec3(0.2125, 0.7154, 0.0721);

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= push.size.x || coord.y >= push.size.y) {
		return;
	}

	vec3 c = imageLoad(quarter_image, coord).rgb;
	float l = dot(max(c, vec3(0.0)), LUMINANCE_WEIGHTS);
	// BRIGHTNESSCUTOFF (blob 0163): cut = saturate(L * scale - threshold), where the guest
	// uploads the pair as one float2 in `sub_821B6730` — scale is literally 1/post+0x41A8, the
	// exposure scalar, and threshold is min(white*f, mid*f) mirrored to post+208.
	float inv_exposure = 1.0 / max(state.v[S_EXPOSURE_FRAME], 1e-4);
	float cut = max(l * inv_exposure - state.v[S_THRESHOLD], 0.0);
	vec3 bright = c * (cut / max(l, 1e-5));
	vec3 rays = imageLoad(rays_image, coord).rgb;
	vec3 occl = imageLoad(occl_image, coord).rgb;
	// The rays ride at 6.0 * RadialBlurFactor (0163: c255.w = 6.0 multiplies the radial tap
	// before the factor) — the 6 lives in bloom.z. The guest's radial source is the RAW scene
	// where our quarter is exposure-scaled, so the term unexposes to match its space.
	vec3 o = bright * params.bloom.y + rays * inv_exposure * params.bloom.z + occl;
	// 0.5 = 1/g_BloomRescaleFactor; the guest uploads 2.0 (flt_8209B480) to const 21.
	imageStore(dst_image, coord, vec4(o * 0.5, 1.0));
}
