#[compute]
#version 450

// Targeting highlight composite — `sub_821B6B70`, pass id 6.
//
// SHADER: `PSHADER_OUTLINEQUAD` (entry 171, blob 0143, bound by 0x82A6AF88) is one fetch of the
// blurred buffer, `rgb * g_GlobalAmbientAndBrightness.w` and `a` straight through. c20 has no
// writer in the binary and is 1.0 everywhere else in this chain.
//
// BLEND: SrcBlend `g_Default_SrcBlend` = 1 (ONE), DestBlend `g_Blend2D_Dst` = 7 (INVSRCALPHA) in
// the Xenos enum: scene = glow.rgb + scene * (1 - glow.a). Not an add.
//
// STENCIL: EQUAL (`dword_83316F94` = 2, Xenos enum), mask 0x30. Bit 0x20 is written over every
// highlighted object's silhouette just before, by the same function, with the z-test OFF
// (`sub_82AA3510`: ColorWrite 0, ZEnable 0, func ALWAYS, op REPLACE, ref/mask/write 32). Both of
// the composite's references (16, then 0) need 0x20 CLEAR, so the glow never touches the
// highlighted object itself — it is a halo around the silhouette, occluded or not. Here the
// silhouette is the layer's G channel, which the proxies write whether or not they survive the
// depth clip.
//
// DEPTH: ZEnable 1, ZWrite 0, and every quad drawn at ONE depth (`sub_821F2918` takes it in f1).
// `sub_82A6BBA0` draws each object's padded rect twice — at its AABB centre's depth for pixels
// whose stencil holds 0x10 (a renderable with bound radius <= 3.0, `sub_82AA2A68`), and at its
// nearest AABB corner's depth for the rest. Only the second is reproduced: this chain has no
// per-pixel record of which renderable drew a pixel, so every pixel is tested against the
// nearest corner. The halo is therefore hidden by anything standing in front of the object.
//
// Per object, in order. The guest blends the same blurred buffer once per object whose rect
// covers the pixel, so overlapping rects blend twice; so does this.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict image2D color_image;
layout(set = 0, binding = 1) uniform sampler2D depth_tex;
layout(set = 0, binding = 2) uniform sampler2D glow_tex;
layout(set = 0, binding = 3) uniform sampler2D layer_tex;

/// Per slot, three vec4s: colour (rgb, alpha), screen rect (u0, v0, u1, v1), (nearest depth, 0, 0, 0).
layout(set = 0, binding = 4, std430) restrict readonly buffer Objects {
	vec4 data[];
} objects;

layout(push_constant, std430) uniform Params {
	ivec2 size;
	int slots;
	float pad0;
	vec4 depth;
} push;

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
	ivec2 lhi = textureSize(layer_tex, 0) - ivec2(1);
	ivec2 lat = clamp(ivec2(uv * vec2(textureSize(layer_tex, 0))), ivec2(0), lhi);
	if (texelFetch(layer_tex, lat, 0).g > 0.5) {
		return;
	}

	float scene = linear_depth(texelFetch(depth_tex, coord, 0).r);
	vec4 glow = clamp(texture(glow_tex, uv), vec4(0.0), vec4(1.0));
	vec4 c = imageLoad(color_image, coord);
	vec3 rgb = c.rgb;
	bool touched = false;

	for (int k = 0; k < push.slots; ++k) {
		vec4 rect = objects.data[k * 3 + 1];
		if (rect.x >= rect.z || uv.x < rect.x || uv.x > rect.z || uv.y < rect.y || uv.y > rect.w) {
			continue;
		}

		if (objects.data[k * 3 + 2].x <= scene) {
			rgb = glow.rgb + rgb * (1.0 - glow.a);
			touched = true;
		}
	}

	if (touched) {
		imageStore(color_image, coord, vec4(rgb, c.a));
	}
}
