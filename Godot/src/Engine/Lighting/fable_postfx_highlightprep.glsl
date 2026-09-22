#[compute]
#version 450

// Targeting highlight silhouette, resolved to what the guest's buffer holds.
//
// The guest draws each highlighted object into a cleared half-res A8R8G8B8 target with
// `PSHADER_OUTLINEMATERIAL_GLT1` (entry 1076, blob 0520): a flat `g_OutliningColour`, discarded
// where the object is more than 0.15 units behind the scene depth. The layer this reads holds
// that draw with the per-object constant replaced by a SLOT — R = slot / 8 where the silhouette
// survived the depth clip, G = 1 wherever the silhouette is at all — so the colour a slot stands
// for is looked up here from the objects table rather than rasterised through Godot's colour
// pipeline.
//
// Colour and alpha arrive already quantised to 1/255, as the guest's 8-bit target stores them.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D layer_tex;
layout(rgba16f, set = 0, binding = 1) uniform restrict writeonly image2D dst_image;

/// Per slot, three vec4s: colour (rgb, alpha), screen rect (u0, v0, u1, v1), (nearest corner depth, centre depth, small, 0).
layout(set = 0, binding = 2, std430) restrict readonly buffer Objects {
	vec4 data[];
} objects;

layout(push_constant, std430) uniform Params {
	ivec2 size;
	int slots;
	float pad0;
} push;

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= push.size.x || coord.y >= push.size.y) {
		return;
	}

	ivec2 hi = textureSize(layer_tex, 0) - ivec2(1);
	float r = texelFetch(layer_tex, clamp(coord, ivec2(0), hi), 0).r;
	int slot = int(round(r * 8.0));
	vec4 v = vec4(0.0);
	if (slot >= 1 && slot <= push.slots) {
		v = objects.data[(slot - 1) * 3];
	}

	imageStore(dst_image, coord, v);
}
