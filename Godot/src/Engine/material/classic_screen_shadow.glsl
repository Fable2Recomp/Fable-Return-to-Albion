#[compute]
#version 450

// The title's screen-space sun shadow mask (PSHADER_SHADOWMAP_POISSON, `sub_821AF248` from the
// cascade loop): one 8-bit lit fraction per pixel, from the frame's own depth (the Z prepass)
// and the two sun cascades, before the opaque colour pass. Its SCR1 materials sample it at
// pixel * (1/W, 1/H) (PS c0 g_TileOffsetSize) and multiply it into the sun.
//
//   W     = the pixel's world position, guest axes; dist = |W - eye|
//   near  (dist < c12.x + c12.z = 22): 12 Poisson taps on cascade 0 around its shadow uv, the
//         disk rotated per pixel by a 32x32 random rotation, radius 2.5 / map size; a tap is lit
//         when (1 - z) > map (strict)
//   far   (20 < dist < 45 = c12.y + 10): 2x2 bilinear-weighted taps on cascade 1, lit when
//         (1 - z) >= map
//   mask  = lerp(lerp(near, far, saturate((dist - 20) / 2)), c12.w, saturate(0.1 (dist - 35)))

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D depth_tex;
layout(set = 0, binding = 1) uniform sampler2D cascade0;
layout(set = 0, binding = 2) uniform sampler2D cascade1;
layout(r8, set = 0, binding = 3) uniform restrict writeonly image2D mask_image;
layout(set = 0, binding = 4, std140) uniform Params {
	mat4 inv_projection;   // Godot camera projection, inverted (raw depth in, view out)
	mat4 view_to_guest;    // camera transform composed with Godot -> guest axes
	mat4 shadow0;          // guest world -> cascade 0 (PS c36..c39 as rows)
	mat4 shadow1;          // guest world -> cascade 1 (PS c40..c43)
	vec4 eye;              // guest eye
	vec4 ranges;           // PS c12 = (20, 35, 2, DISTANT_SHADOW_FACTOR)
	vec4 sizes;            // (W, H, 1 / map size, 0)
} params;

const vec2 POISSON[12] = vec2[](
	vec2(-0.3262, -0.4058), vec2(-0.7916, -0.5977), vec2(-0.3219, -0.9326), vec2(0.8964, 0.4125),
	vec2(-0.6959, 0.4571), vec2(0.4734, -0.4800), vec2(0.9623, -0.1950), vec2(-0.2033, 0.6207),
	vec2(-0.8401, -0.0736), vec2(0.5074, 0.0644), vec2(0.1855, -0.8931), vec2(0.5195, 0.7670));

// The title's g_RandomRotTexture is 32x32 of random angles, point sampled, wrapped; any fixed
// random table gives its pattern, so the angle here is a hash of the wrapped cell.
vec2 rotation(ivec2 pixel) {
	ivec2 cell = pixel & ivec2(31);
	uint h = uint(cell.x) * 73856093u ^ uint(cell.y) * 19349663u;
	h = (h ^ (h >> 13u)) * 1274126177u;
	float angle = float(h & 0xFFFFu) / 65535.0 * 6.2831853;
	return vec2(sin(angle), cos(angle));
}

vec4 shadow_coords(mat4 rows, vec3 world) {
	vec4 p = vec4(world, 1.0);
	return vec4(dot(rows[0], p), dot(rows[1], p), dot(rows[2], p), dot(rows[3], p));
}

void main() {
	ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
	ivec2 size = ivec2(params.sizes.xy);
	if (pixel.x >= size.x || pixel.y >= size.y) {
		return;
	}
	float depth = texelFetch(depth_tex, pixel, 0).r;
	vec2 uv = (vec2(pixel) + 0.5) / vec2(size);
	// The scene's projection already carries the renderer's y flip and reversed-Z [0, 1] depth.
	vec4 view = params.inv_projection * vec4(uv * 2.0 - 1.0, depth, 1.0);
	view /= view.w;
	vec3 world = (params.view_to_guest * vec4(view.xyz, 1.0)).xyz;
	float dist = length(world - params.eye.xyz);

	float near_lit = 0.0;
	if (dist < params.ranges.x + params.ranges.z) {
		vec4 s0 = shadow_coords(params.shadow0, world);
		s0 /= s0.w;
		float ref = 1.0 - s0.z;
		vec2 rot = rotation(pixel);
		float radius = 2.5 * params.sizes.z;
		for (int k = 0; k < 12; k++) {
			vec2 p = POISSON[k];
			vec2 tap = s0.xy + radius * vec2(p.x * rot.x + p.y * rot.y, p.y * rot.x - p.x * rot.y);
			near_lit += ref > texture(cascade0, tap).r ? 1.0 : 0.0;
		}
		near_lit *= 1.0 / 12.0;
	}
	float far_lit = 0.0;
	if (dist > params.ranges.x && dist < params.ranges.y + 10.0) {
		vec4 s1 = shadow_coords(params.shadow1, world);
		s1 /= s1.w;
		float ref = 1.0 - s1.z;
		vec2 texels = s1.xy / params.sizes.z - 0.5;
		vec2 base = floor(texels);
		vec2 f = texels - base;
		float t00 = ref >= texelFetch(cascade1, ivec2(base), 0).r ? 1.0 : 0.0;
		float t10 = ref >= texelFetch(cascade1, ivec2(base) + ivec2(1, 0), 0).r ? 1.0 : 0.0;
		float t01 = ref >= texelFetch(cascade1, ivec2(base) + ivec2(0, 1), 0).r ? 1.0 : 0.0;
		float t11 = ref >= texelFetch(cascade1, ivec2(base) + ivec2(1, 1), 0).r ? 1.0 : 0.0;
		far_lit = mix(mix(t00, t10, f.x), mix(t01, t11, f.x), f.y);
	}
	float lit = mix(near_lit, far_lit, clamp((dist - params.ranges.x) / max(params.ranges.z, 1e-4), 0.0, 1.0));
	lit = mix(lit, params.ranges.w, clamp(0.1 * (dist - params.ranges.y), 0.0, 1.0));
	imageStore(mask_image, pixel, vec4(lit, 0.0, 0.0, 0.0));
}
