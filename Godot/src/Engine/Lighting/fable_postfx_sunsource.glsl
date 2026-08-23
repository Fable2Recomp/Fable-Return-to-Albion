#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D element_tex;
layout(set = 0, binding = 1) uniform sampler2D depth_tex;
layout(rgba16f, set = 0, binding = 2) uniform restrict writeonly image2D dst_image;

layout(push_constant, std430) uniform Params {
	ivec2 full_size;
	ivec2 size;
	vec2 centre;
	vec2 half_extent;
	vec4 colour;
	vec4 misc;
} push;

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= push.size.x || coord.y >= push.size.y) {
		return;
	}

	vec2 full = vec2(push.full_size);
	vec2 uv = (vec2(coord) + 0.5) / vec2(push.size);
	vec2 texel = 1.0 / full;
	vec2 tex_size = vec2(textureSize(element_tex, 0));
	vec2 ratio = tex_size / max(push.half_extent * full, vec2(1e-4));
	float lod = max(log2(max(ratio.x, ratio.y)), 0.0);
	bool disc = push.misc.x > 0.5;
	float exposure = push.misc.y;
	float bias = push.misc.z;

	vec3 sum = vec3(0.0);
	for (int y = 0; y < 4; y++) {
		for (int x = 0; x < 4; x++) {
			vec2 p = uv + (vec2(x, y) - 1.5) * texel;
			ivec2 pix = clamp(ivec2(p * full), ivec2(0), push.full_size - 1);
			if (texelFetch(depth_tex, pix, 0).r > bias) {
				continue;
			}
			vec2 ndc = vec2(p.x * 2.0 - 1.0, 1.0 - p.y * 2.0);
			vec2 local = (ndc - push.centre) / push.half_extent;
			if (abs(local.x) > 1.0 || abs(local.y) > 1.0) {
				continue;
			}
			vec4 t = textureLod(element_tex, vec2(local.x * 0.5 + 0.5, 0.5 - local.y * 0.5), lod);
			if (disc) {
				vec3 rgb = t.rgb * push.colour.rgb * t.a * exposure;
				rgb = rgb / (1.0 + rgb);
				sum += rgb * t.a;
			} else {
				sum += t.rgb * push.colour.rgb;
			}
		}
	}

	imageStore(dst_image, coord, vec4(sum * (1.0 / 16.0), 1.0));
}
