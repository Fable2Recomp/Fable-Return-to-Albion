// fable_attenuation_weighted's light record in ONE fetch (`lite_one_row_light`, AndroidLightTablePacking.cs
// beside this file). Included in its place by fable_lighting.gdshaderinc, inside that function, only in a
// lite build with FABLE_LITE_ONE_ROW_LIGHT and the lamp-shadow lookup compiled out (FABLE_LITE_NO_STATIC_SHADOW):
// row 2 is then (x, y, z, half2(Godot's range, stated range)). Declares what upstream's two fetches do.
		vec4 packed_record = texelFetch(fable_light_table, ivec2(slot, 2), 0);
		vec2 range_stated = unpackHalf2x16(floatBitsToUint(packed_record.w));
		vec4 record = vec4(packed_record.xyz, range_stated.x);
		float d = distance(world, record.xyz);
		float stated = range_stated.y;
