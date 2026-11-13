// The phone's cheaper reads of the modern lighting's ground-contact map (AndroidGroundShading.cs beside
// this file), and the props' vertex and near-the-floor readers of it. Included by fable_lite_lighting
// (the end of fable_lighting in a lite build) where the props read the map in vertex() (the near gate's
// height, `lite_ground_ao` near; the vertex AO, fast and fastest): FABLE_VERTEX_GROUND_AO or
// FABLE_NEAR_GROUND_AO (fable_lite_switches), after the map's globals and fable_evaluate_prop_baked_ao.
//
//   FABLE_LITE_GROUND_AO_GATHER  (`lite_ground_ao_gather`) the props' four taps, per vertex and the near
//                                path's per pixel, as textureGather of the map's G, B and A: three texture
//                                instructions for four texel fetches (two in the near gate's vertex, which
//                                reads no G), one coordinate for four clamped integer ones. The same
//                                texels, weights and arithmetic: the same picture.

#if defined(FABLE_LITE_GROUND_AO_GATHER) && (defined(FABLE_NEAR_GROUND_AO) || defined(FABLE_VERTEX_GROUND_AO))
#define FABLE_GATHER_GROUND_AO
#endif

#ifdef FABLE_GATHER_GROUND_AO
/// The map's 2x2 block from texel base_f (its lower corner, in texels): G and the decoded floor height
/// of each tap, in textureGather's order x = (0, 1), y = (1, 1), z = (1, 0), w = (0, 0). The gather
/// point is the block's centre, half a texel from any edge of the footprint's choice, so the hardware
/// picks the block floor() picked; at the map's last row or column the clamp-to-edge sampler repeats
/// the edge texel, as the texelFetch readers' min() does.
void fable_gather_ground_taps(vec2 base_f, out vec4 g, out vec4 h) {
	vec2 uv = (base_f + 1.0) / fable_terrain_ao_height.zw;
	g = textureGather(fable_terrain_contact_ao, uv, 1);
	vec4 lo = textureGather(fable_terrain_contact_ao, uv, 2);
	vec4 hi = textureGather(fable_terrain_contact_ao, uv, 3);
	h = fable_terrain_ao_height.x + (lo * 255.0 + hi * 65280.0) * (fable_terrain_ao_height.y * (1.0 / 65535.0));
}

/// fable_evaluate_prop_baked_ao with its four taps gathered: the same tests, weights and fade.
float fable_gather_prop_baked_ao(float color_alpha, vec3 world_pos) {
	if (FABLE_UNIFIED <= 0.5 || FABLE_AO_STRENGTH <= 0.001) {
		return 1.0;
	}
	float vertex_ao = color_alpha > 0.001 ? color_alpha : 1.0;
	float inst_ao = instance_baked_ao > 0.001 ? instance_baked_ao : 1.0;
	if (fable_terrain_ao_height.z <= 1.5 ||
	    fable_terrain_ao_rect.z <= 0.0 || fable_terrain_ao_rect.w <= 0.0) {
		return vertex_ao * inst_ao;
	}
	float min_h = fable_terrain_ao_height.x;
	float span_h = fable_terrain_ao_height.y;
	if (world_pos.y < min_h - 0.45 || world_pos.y > min_h + span_h + 0.45) {
		return vertex_ao * inst_ao;
	}
	vec2 grid_uv = vec2(
		(-world_pos.x - fable_terrain_ao_rect.x) * fable_terrain_ao_rect.z,
		(world_pos.z - fable_terrain_ao_rect.y) * fable_terrain_ao_rect.w);
	if (grid_uv.x < 0.0 || grid_uv.x > 1.0 || grid_uv.y < 0.0 || grid_uv.y > 1.0) {
		return vertex_ao * inst_ao;
	}
	vec2 st = grid_uv * (fable_terrain_ao_height.zw - vec2(1.0));
	vec2 base_f = floor(st);
	vec2 f = st - base_f;
	vec4 g;
	vec4 h;
	fable_gather_ground_taps(base_f, g, h);
	vec4 hw = vec4(1.0) - smoothstep(vec4(0.06), vec4(0.42), abs(vec4(world_pos.y) - h));
	vec4 w = vec4((1.0 - f.x) * f.y, f.x * f.y, f.x * (1.0 - f.y), (1.0 - f.x) * (1.0 - f.y)) * hw;
	float sum_w = w.w + w.z + w.x + w.y;
	float matched_ao = (w.w * g.w + w.z * g.z + w.x * g.x + w.y * g.y) / max(sum_w, 1e-4);
	float max_hw = max(max(hw.w, hw.z), max(hw.x, hw.y));
	float fade = max_hw * smoothstep(0.0, 0.12, sum_w);
	return vertex_ao * min(inst_ao, mix(1.0, matched_ao, fade));
}
#endif

/// The props' ground AO in vertex(), for fable_prop_baked_ao_from_vertex and fable_near_prop_baked_ao:
/// (walkable-floor AO, height above that floor, vertex AO, instance AO). The same four texel taps as
/// fable_evaluate_prop_baked_ao, weighted by the same bilinear and height-match weights, each floored
/// at a thousandth of its bilinear share so a vertex no tap matches (the top of a wall) still carries
/// the floor beneath it. The height is signed: the per-pixel match is on |y - floor|, and a signed
/// difference interpolates exactly across a triangle where the floor under it is planar.
/// Off the map, or with no bake, the AO is 1 and the height 0, so the near path fetches and returns
/// the per-pixel function's own fallback there.
vec4 fable_vertex_prop_ground_ao(float color_alpha, vec3 world_pos) {
	vec4 ao = vec4(1.0, 0.0,
		color_alpha > 0.001 ? color_alpha : 1.0,
		instance_baked_ao > 0.001 ? instance_baked_ao : 1.0);
	if (fable_terrain_ao_height.z > 1.5 && fable_terrain_ao_rect.z > 0.0 && fable_terrain_ao_rect.w > 0.0) {
		vec2 grid_uv = vec2(
			(-world_pos.x - fable_terrain_ao_rect.x) * fable_terrain_ao_rect.z,
			(world_pos.z - fable_terrain_ao_rect.y) * fable_terrain_ao_rect.w);
		if (grid_uv.x >= 0.0 && grid_uv.x <= 1.0 && grid_uv.y >= 0.0 && grid_uv.y <= 1.0) {
			vec2 max_coord_f = fable_terrain_ao_height.zw - vec2(1.0);
			vec2 st = grid_uv * max_coord_f;
			vec2 base_f = floor(st);
			vec2 f = st - base_f;
#ifdef FABLE_GATHER_GROUND_AO
			vec4 tg;
			vec4 th;
			fable_gather_ground_taps(base_f, tg, th);
			vec4 t00 = vec4(0.0, tg.w, 0.0, 0.0);
			vec4 t10 = vec4(0.0, tg.z, 0.0, 0.0);
			vec4 t01 = vec4(0.0, tg.x, 0.0, 0.0);
			vec4 t11 = vec4(0.0, tg.y, 0.0, 0.0);
			float h00 = th.w;
			float h10 = th.z;
			float h01 = th.x;
			float h11 = th.y;
#else
			ivec2 max_c = ivec2(max_coord_f);
			ivec2 c00 = max(ivec2(0), min(ivec2(base_f), max_c));
			ivec2 c10 = min(c00 + ivec2(1, 0), max_c);
			ivec2 c01 = min(c00 + ivec2(0, 1), max_c);
			ivec2 c11 = min(c00 + ivec2(1, 1), max_c);
			vec4 t00 = texelFetch(fable_terrain_contact_ao, c00, 0);
			vec4 t10 = texelFetch(fable_terrain_contact_ao, c10, 0);
			vec4 t01 = texelFetch(fable_terrain_contact_ao, c01, 0);
			vec4 t11 = texelFetch(fable_terrain_contact_ao, c11, 0);
			float min_h = fable_terrain_ao_height.x;
			float h_scale = fable_terrain_ao_height.y * (1.0 / 65535.0);
			float h00 = min_h + (t00.b * 255.0 + t00.a * 65280.0) * h_scale;
			float h10 = min_h + (t10.b * 255.0 + t10.a * 65280.0) * h_scale;
			float h01 = min_h + (t01.b * 255.0 + t01.a * 65280.0) * h_scale;
			float h11 = min_h + (t11.b * 255.0 + t11.a * 65280.0) * h_scale;
#endif
			float w00 = (1.0 - f.x) * (1.0 - f.y) * max(1.0 - smoothstep(0.06, 0.42, abs(world_pos.y - h00)), 1e-3);
			float w10 = f.x * (1.0 - f.y) * max(1.0 - smoothstep(0.06, 0.42, abs(world_pos.y - h10)), 1e-3);
			float w01 = (1.0 - f.x) * f.y * max(1.0 - smoothstep(0.06, 0.42, abs(world_pos.y - h01)), 1e-3);
			float w11 = f.x * f.y * max(1.0 - smoothstep(0.06, 0.42, abs(world_pos.y - h11)), 1e-3);
			float inv_w = 1.0 / max(w00 + w10 + w01 + w11, 1e-6);
			ao.x = (w00 * t00.g + w10 * t10.g + w01 * t01.g + w11 * t11.g) * inv_w;
			ao.y = world_pos.y - ((w00 * h00 + w10 * h10 + w01 * h01 + w11 * h11) * inv_w);
		}
	}
	return ao;
}

#ifdef FABLE_VERTEX_GROUND_AO
/// fable_evaluate_prop_baked_ao from the vertex's fable_vertex_prop_ground_ao: no fetch. The floor's AO
/// between the vertices is what blurs; the height fade (the per-pixel match's, on the interpolated
/// height) keeps its 0.42 m on a tall wall.
float fable_prop_baked_ao_from_vertex(vec4 ao) {
	if (FABLE_UNIFIED <= 0.5 || FABLE_AO_STRENGTH <= 0.001) {
		return 1.0;
	}
	float fade = 1.0 - smoothstep(0.06, 0.42, abs(ao.y));
	return ao.z * min(ao.w, mix(1.0, ao.x, fade));
}
#endif

#ifdef FABLE_NEAR_GROUND_AO
/// fable_evaluate_prop_baked_ao, its taps taken only within FABLE_NEAR_GROUND_REACH of the walkable
/// floor by the height the vertices give (fable_vertex_prop_ground_ao's y, off the map 0). Past it
/// every tap is more than 0.42 m from the pixel, its match weight is exactly zero, and this is the
/// same value (the instance AO is at most 1) without the taps. Exact wherever the floor under the
/// pixel's taps lies within the reach's 0.58 m over the match of the floor its triangle's vertices
/// interpolate: a walkable floor that steps further than that under one triangle (a stair or a
/// ledge under a large face) can lose its contact shadow there.
float fable_near_prop_baked_ao(float color_alpha, vec3 world_pos, float vertex_height) {
	if (fable_terrain_ao_height.z <= 1.5 || abs(vertex_height) < FABLE_NEAR_GROUND_REACH) {
#ifdef FABLE_GATHER_GROUND_AO
		return fable_gather_prop_baked_ao(color_alpha, world_pos);
#else
		return fable_evaluate_prop_baked_ao(color_alpha, world_pos);
#endif
	}
	if (FABLE_UNIFIED <= 0.5 || FABLE_AO_STRENGTH <= 0.001) {
		return 1.0;
	}
	return (color_alpha > 0.001 ? color_alpha : 1.0) * (instance_baked_ao > 0.001 ? instance_baked_ao : 1.0);
}
#endif
