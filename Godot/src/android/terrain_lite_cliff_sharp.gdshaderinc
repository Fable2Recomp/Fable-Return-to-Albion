// THE CLIFF'S TWO PLANES SHARPENED (`lite_terrain_cliff_sharpness`, terrain_lite_layers), in place of
// terrain_splat_body.gdshaderinc's blend inside sample_layer.
		// The two planes blend only in the middle band (`lite_terrain_cliff_sharpness`); a plane at zero is not fetched.
#ifndef FABLE_LITE_TERRAIN_CLIFF_SHARP_VALUE
#define FABLE_LITE_TERRAIN_CLIFF_SHARP_VALUE 0.35
#endif
		pw = clamp((pw - FABLE_LITE_TERRAIN_CLIFF_SHARP_VALUE) / (1.0 - (2.0 * FABLE_LITE_TERRAIN_CLIFF_SHARP_VALUE)), 0.0, 1.0);
		vec3 cliff = vec3(0.0);
		if (pw.x > 0.0) {
			cliff += textureGrad(lod_detail_array, vec3(uv_x, fs), local_dx.zy * cliff_scale, local_dy.zy * cliff_scale).rgb * pw.x;
		}
		if (pw.y > 0.0) {
			cliff += textureGrad(lod_detail_array, vec3(uv_z, fs), local_dx.xy * cliff_scale, local_dy.xy * cliff_scale).rgb * pw.y;
		}
