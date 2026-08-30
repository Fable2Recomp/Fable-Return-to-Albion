#[compute]
#version 450

// THE SUN LAYER — the rays' source, rebuilt natively.
//
// The guest keeps TWO half-res FX layers (sub_821CB750): the glow-sprite layer that feeds
// bloom, and the SUN layer at off_8331A9A8 — the depth-tested, occlusion-query-scaled sun and
// moon disc draws — which is the ONLY input to the rays chain (DOWNSCALE2X, then RADIALBLUR
// twice). Radial-blurring the scene instead washed the whole frame; synthesizing beams with no
// depth test shone the sun through cave walls. This pass is the stated middle: the disc's own
// texture and colour, drawn at its projected position, per-pixel depth-tested against the
// scene — exactly what the guest's layer holds, without a second scene render.
//
// The [0,1] per-channel clamp is the layer's FORMAT in the guest (2_10_10_10 UNORM with
// ONE/ONE blending — the ROP saturates each channel), which is where the overlap invariance
// comes from. min() reproduces it.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D depth_tex;
layout(set = 0, binding = 1) uniform sampler2D disc_tex;
layout(rgba16f, set = 0, binding = 2) uniform restrict writeonly image2D dst_image;

layout(push_constant, std430) uniform Params {
	ivec2 size;
	float far_value;
	float pad0;
	vec2 sun_uv;
	vec2 half_uv;
	vec4 colour;
} push;

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= push.size.x || coord.y >= push.size.y) {
		return;
	}

	vec2 uv = (vec2(coord) + 0.5) / vec2(push.size);
	vec2 local = (uv - push.sun_uv) / max(push.half_uv, vec2(1e-6));
	if (abs(local.x) > 1.0 || abs(local.y) > 1.0) {
		imageStore(dst_image, coord, vec4(0.0));
		return;
	}

	// THE DEPTH TEST IS THE POINT. The disc sits at the far plane, so it survives only where
	// the scene wrote nothing — sky. A hill in front of the sun removes its pixels from this
	// layer and the rays fade exactly as far as the hill covers, which is the guest's own
	// per-pixel Z test on the layer draw.
	float scene_depth = texture(depth_tex, uv).r;
	if (abs(scene_depth - push.far_value) > 1e-6) {
		imageStore(dst_image, coord, vec4(0.0));
		return;
	}

	vec4 disc = texture(disc_tex, (local * 0.5) + 0.5);
	vec3 c = min(push.colour.rgb * disc.rgb * disc.a, vec3(1.0));
	imageStore(dst_image, coord, vec4(c, 0.0));
}
