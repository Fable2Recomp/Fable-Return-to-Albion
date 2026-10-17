// The guest's roofs for the modern rain and snow (Engine_RoofCut, from Engine_ClassicRoofs): a
// drop under a building's roof is not drawn. The title's test (`sub_82A789B0`, the classic
// CutAtRoof): inside the roof's four top corners in the ground plane and below its top. Three
// texels a roof: (c0.xy, c1.xy), (c2.xy, c3.xy), (top, 0, 0, 0), guest axes.
global uniform sampler2D fable_roofs : filter_nearest;
global uniform int fable_roof_count;

bool fable_under_roof(vec3 world) {
	// Godot (x, y, z) is the guest's (-x, z, y).
	vec2 p = vec2(-world.x, world.z);
	for (int i = 0; i < fable_roof_count; i++) {
		vec4 a = texelFetch(fable_roofs, ivec2(i * 3, 0), 0);
		vec4 b = texelFetch(fable_roofs, ivec2(i * 3 + 1, 0), 0);
		float top = texelFetch(fable_roofs, ivec2(i * 3 + 2, 0), 0).x;
		if (world.y >= top) {
			continue;
		}
		vec2 c[4] = vec2[4](a.xy, a.zw, b.xy, b.zw);
		bool inside = true;
		for (int k = 0; k < 4; k++) {
			vec2 s = c[k];
			vec2 n = c[(k + 1) & 3];
			if ((n.y - s.y) * (p.x - s.x) - (n.x - s.x) * (p.y - s.y) > 0.0) {
				inside = false;
				break;
			}
		}
		if (inside) {
			return true;
		}
	}
	return false;
}
