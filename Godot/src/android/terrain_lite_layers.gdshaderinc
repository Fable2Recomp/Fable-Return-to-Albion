// The phone's lite ground splat loop (`lite_terrain_bit_loop`, `lite_terrain_max_layers`,
// `lite_terrain_min_weight`; Engine_ShaderSpecialisation.TerrainLeverDefines beside this file).
// Included INSIDE terrain_splat_body.gdshaderinc's fragment(), in place of its own lite loop, under
// FABLE_LITE with any of the three defines below; it reads that function's locals (layer_mask,
// weight_uv, slope_w, the derivatives) and writes final, mapped_normal, ao and weight_sum as the
// body's `lite_terrain_single_fetch` loop does. One `sample_layer` copy, as that loop has.
//
//   FABLE_LITE_TERRAIN_BIT_LOOP     the loop visits only the layers that can weigh this pixel: the
//                                   material mask, the chunk's active layers and its layer count are
//                                   folded into one word first, and each pass takes its lowest set
//                                   bit. The same layers in the same order through the same
//                                   arithmetic: the same picture bit for bit, without a pass (and
//                                   its shifts, tests and branches) per absent layer below the last.
//   FABLE_LITE_TERRAIN_MIN_WEIGHT   a layer weighing under FABLE_LITE_TERRAIN_MIN_WEIGHT_VALUE
//                                   (default 0.08) is not sampled; the kept layers are rescaled to
//                                   every layer's total, so brightness holds. With nothing kept the
//                                   strongest is sampled alone.
//   FABLE_LITE_TERRAIN_MAX_LAYERS   only the strongest FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE (1-4,
//                                   default 3) layers are sampled, rescaled the same way. With it
//                                   the weights are read in one pass (each texel once) into a short
//                                   strongest-first list, and the sampling pass walks that list;
//                                   MIN_WEIGHT then drops the list's weaker entries (never the first).
//
// Both cuts sample fewer layers where layers meet (each sampled layer is one colour fetch, two more
// on a cliff, and about 30-70 ALU on the flat ground), and change the picture only there; the bit
// loop is implied by either.
//
// THE OTHER GROUND LEVERS hook the body at the lines they change:
//   FABLE_LITE_TERRAIN_CLIFF_SHARP  (`lite_terrain_cliff_sharpness`) a cliff's two vertical
//                                   projections blend only where each weighs more than
//                                   FABLE_LITE_TERRAIN_CLIFF_SHARP_VALUE (default 0.35), rescaled to
//                                   meet at the middle; outside that band one plane is fetched, not two
//                                   (terrain_lite_cliff_sharp).
//   FABLE_LITE_TERRAIN_TRILINEAR    (`lite_terrain_trilinear`) the layer and cliff colour arrays
//                                   sampled trilinear instead of anisotropic (fable_lite_switches'
//                                   FABLE_TERRAIN_LOD_FILTER).
//   FABLE_LITE_TERRAIN_VERTEX_FOG   (`lite_terrain_vertex_fog`) the fog's two terms in vertex(), as
//                                   standard_material's FABLE_LITE_VERTEX_FOG does for the props (which
//                                   that define, through lite_extra_defines, leaves the ground out of):
//                                   terrain_lite_varyings, terrain_lite_vertex, one line in the body.
//   FABLE_LITE_TERRAIN_NM_ANALYTIC  the layer normal map over the mesh's analytic frame, written to
//                                   NORMAL (terrain_lite_nm_analytic).
#ifndef FABLE_LITE_TERRAIN_MIN_WEIGHT_VALUE
#define FABLE_LITE_TERRAIN_MIN_WEIGHT_VALUE 0.08
#endif
#ifndef FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE
#define FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE 3
#endif

	// The splat loop's three ends folded into the mask: the layers painted near this texel, those
	// the chunk draws at all, and the chunk's layer count (32 at most).
	int tl_count = clamp(int(chunk_grid_size.z), 0, 32);
	uint tl_bits = layer_mask & uint(terrain_active_material_mask) &
		(tl_count >= 32 ? 0xFFFFFFFFu : ((1u << uint(tl_count)) - 1u));
	// Every weighed layer's weight (over 0.001), sampled or not, and whether any went unsampled.
	float tl_total = 0.0;
	bool tl_dropped = false;

#ifdef FABLE_LITE_TERRAIN_MAX_LAYERS
	// THE STRONGEST N, STRONGEST FIRST. Each weighed layer is carried down the list: at each entry
	// the heavier of the two stays and the lighter moves on, so the list stays sorted and an equal
	// weight goes after the layer already there (the lower index). Constant indices once unrolled.
	float tl_w[FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE];
	float tl_g[FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE];
	int tl_i[FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE];
	for (int k = 0; k < FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE; k++) {
		tl_w[k] = 0.0;
		tl_g[k] = 0.0;
		tl_i[k] = -1;
	}
	int tl_weighed = 0;
	for (int pass = 0; pass < 32; pass++) {
		if (tl_bits == 0u) {
			break;
		}
		int mat = int(findLSB(tl_bits));
		tl_bits &= tl_bits - 1u;
		vec2 ws = textureLod(material_weight, vec3(weight_uv, float(mat)), 0.0).rg;
		if (ws.r <= 0.001) {
			continue;
		}
		tl_total += ws.r;
		tl_weighed++;
		float cw = ws.r;
		float cg = ws.g;
		int ci = mat;
		for (int k = 0; k < FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE; k++) {
			if (cw > tl_w[k]) {
				float sw = tl_w[k];
				float sg = tl_g[k];
				int si = tl_i[k];
				tl_w[k] = cw;
				tl_g[k] = cg;
				tl_i[k] = ci;
				cw = sw;
				cg = sg;
				ci = si;
			}
		}
	}
	if (tl_i[0] < 0) {
		// Nothing weighs this pixel: layer 0 at full weight and the full slope, the loop's fallback.
		tl_w[0] = 1.0;
		tl_g[0] = 1.0;
		tl_i[0] = 0;
	}
	int tl_sampled = 0;
	for (int k = 0; k < FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE; k++) {
		if (tl_i[k] < 0) {
			break;
		}
#ifdef FABLE_LITE_TERRAIN_MIN_WEIGHT
		// Strongest first, so the rest are lighter still.
		if (k > 0 && tl_w[k] < FABLE_LITE_TERRAIN_MIN_WEIGHT_VALUE) {
			break;
		}
#endif
		vec3 layer_colour;
		vec3 layer_normal;
		float layer_cavity;
		sample_layer(tl_i[k], world_xy, v_local, geom_normal, slope_w * tl_g[k], par_dir, quad_cliff, local_dx, local_dy,
			layer_colour, layer_normal, layer_cavity);
		final += layer_colour * tl_w[k];
#ifndef FABLE_LITE_FLAT_TERRAIN
		if (has_normals) {
			mapped_normal += layer_normal * tl_w[k];
		}
		if (has_ao) {
			ao += layer_cavity * tl_w[k];
		}
#endif
		weight_sum += tl_w[k];
		tl_sampled++;
	}
	tl_dropped = tl_sampled < tl_weighed;
#else
	// ONE PASS PER SET BIT, then the fallback pass: the body's single-fetch loop with the absent
	// layers' passes gone. Under MIN_WEIGHT the strongest light layer is remembered for that pass.
#ifdef FABLE_LITE_TERRAIN_MIN_WEIGHT
	float tl_low_w = 0.0;
	float tl_low_g = 1.0;
	int tl_low_i = 0;
#endif
	for (int pass = 0; pass <= 32; pass++) {
		bool last = tl_bits == 0u;
		int slice = 0;
		float w = 1.0;
		float layer_slope = slope_w;
		if (last) {
			if (weight_sum > 0.001) {
				break;
			}
#ifdef FABLE_LITE_TERRAIN_MIN_WEIGHT
			if (tl_low_w > 0.0) {
				// Every weighed layer was light: the strongest alone, rescaled below.
				slice = tl_low_i;
				w = tl_low_w;
				layer_slope = slope_w * tl_low_g;
			}
#endif
			final = vec3(0.0);
			mapped_normal = vec3(0.0);
			ao = 0.0;
			weight_sum = 0.0;
		} else {
			int mat = int(findLSB(tl_bits));
			tl_bits &= tl_bits - 1u;
			vec2 ws = textureLod(material_weight, vec3(weight_uv, float(mat)), 0.0).rg;
			w = ws.r;
			if (w <= 0.001) {
				continue;
			}
#ifdef FABLE_LITE_TERRAIN_MIN_WEIGHT
			tl_total += w;
			if (w < FABLE_LITE_TERRAIN_MIN_WEIGHT_VALUE) {
				tl_dropped = true;
				if (w > tl_low_w) {
					tl_low_w = w;
					tl_low_g = ws.g;
					tl_low_i = mat;
				}
				continue;
			}
#endif
			slice = mat;
			layer_slope = slope_w * ws.g;
		}

		vec3 layer_colour;
		vec3 layer_normal;
		float layer_cavity;
		sample_layer(slice, world_xy, v_local, geom_normal, layer_slope, par_dir, quad_cliff, local_dx, local_dy,
			layer_colour, layer_normal, layer_cavity);
		final += layer_colour * w;
#ifndef FABLE_LITE_FLAT_TERRAIN
		if (has_normals) {
			mapped_normal += layer_normal * w;
		}
		if (has_ao) {
			ao += layer_cavity * w;
		}
#endif
		weight_sum += w;
		if (last) {
			break;
		}
	}
#endif

	// The unsampled layers' weight goes back onto the sampled ones: the same total, the same
	// brightness. Nothing dropped, nothing rescaled.
	if (tl_dropped && weight_sum > 0.001) {
		float restore = tl_total / weight_sum;
		final *= restore;
		mapped_normal *= restore;
		ao *= restore;
		weight_sum = tl_total;
	}

	// Above 0.001 here, the fallback's 1.0 included. The colour is not renormalised (the body says why).
	mapped_normal /= weight_sum;
	ao /= weight_sum;
