#[compute]
#version 450

// THE GLOW LAYER — the halo sprites, rebuilt natively.
//
// The guest's second half-res FX layer (off_8331A9CC in sub_821CB750) holds the halo quads its
// FX light entities draw around themselves, and reaches the frame ONLY through bloom: brightcut
// sums it after the cut, so a lamp's glow blooms without ever brightening the scene draw. Each
// halo is depth-tested where it is drawn, which is what stops a glow shining through a wall.
//
// This pass rasterises every published halo in one dispatch. The occlusion test compares VIEW
// DISTANCES — the scene depth linearised with the same inverse-projection terms the composite
// uses — so it holds under either depth convention. The [0,1] per-channel clamp is the layer's
// FORMAT in the guest (2_10_10_10 UNORM, ONE/ONE blending — the ROP saturates each channel);
// min() over the sum reproduces it.
//
// A halo with a resolved AUTHORED sprite (layer >= 0) samples the sprite array; one whose
// HaloTextureHash no file answers draws the procedural soft disc.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D depth_tex;
layout(rgba16f, set = 0, binding = 1) uniform restrict writeonly image2D dst_image;

// Interleaved triples: [3i] = (uv.xy, half_uv.xy), [3i+1] = (colour.rgb, view_dist),
// [3i+2] = (sprite layer or -1, 0, 0, 0).
layout(set = 0, binding = 2, std430) restrict readonly buffer Halos {
	vec4 entries[];
} halos;

layout(set = 0, binding = 3) uniform sampler2DArray sprite_tex;

layout(push_constant, std430) uniform Params {
	ivec2 size;
	int count;
	float pad0;
	vec4 depth;
} push;

// A halo sits AT its light, which is usually inside the fixture's own flame. The bias keeps the
// flame geometry itself from eating the glow while a wall a metre in front still kills it.
const float DEPTH_BIAS = 0.1;

float linear_depth(float z) {
	float view_z = push.depth.x * z + push.depth.y;
	float view_w = push.depth.z * z + push.depth.w;
	float safe_w = abs(view_w) > 1e-6 ? view_w : 1e-6;
	return -view_z / safe_w;
}

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= push.size.x || coord.y >= push.size.y) {
		return;
	}

	vec2 uv = (vec2(coord) + 0.5) / vec2(push.size);
	float scene_dist = linear_depth(texture(depth_tex, uv).r);

	vec3 sum = vec3(0.0);
	for (int i = 0; i < push.count; i++) {
		vec4 rect = halos.entries[3 * i];
		vec4 cd = halos.entries[(3 * i) + 1];
		float layer = halos.entries[(3 * i) + 2].x;
		if (scene_dist < cd.w - DEPTH_BIAS) {
			continue;
		}

		vec2 local = (uv - rect.xy) / max(rect.zw, vec2(1e-6));
		if (abs(local.x) >= 1.0 || abs(local.y) >= 1.0) {
			continue;
		}

		if (layer >= 0.0) {
			vec4 sprite = texture(sprite_tex, vec3((local * 0.5) + 0.5, layer));
			sum += cd.rgb * sprite.rgb * sprite.a;
		} else {
			float r = length(local);
			float w = clamp(1.0 - r, 0.0, 1.0);
			sum += cd.rgb * (w * w);
		}
	}

	imageStore(dst_image, coord, vec4(min(sum, vec3(1.0)), 0.0));
}
