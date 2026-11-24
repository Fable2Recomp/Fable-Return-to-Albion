// Hardware near-plane oblique water-plane clipper for planar reflection SubViewports (Reversed-Z).
// When rendering from a perspective planar mirror camera (proj[3][3] == 0.0 and camera origin matches
// fable_mirror_clip0/1.xyz), maps clip.z linearly so world.y == waterClipY lands on clip.z == clip.w
// (ndc.z == 1.0), world.y < waterClipY lands at clip.z > clip.w (hardware near-plane clipped with zero
// `discard;`), and world.y >= waterClipY preserves monotonic Reversed-Z depth ordering in [0.05..1.0].
global uniform vec4 fable_mirror_clip0;
global uniform vec4 fable_mirror_clip1;
global uniform vec2 fable_mirror_alpha;

vec4 fable_mirror_clip_position(vec3 world_pos, vec4 view_pos, mat4 proj, vec3 cam_world) {
	vec4 clip = proj * view_pos;
	if (proj[3][3] == 0.0 && proj[2][2] > 0.0) {
		if (fable_mirror_alpha.x > 0.0 && distance(cam_world, fable_mirror_clip0.xyz) < 0.02) {
			clip.z = clip.w - (fable_mirror_alpha.x * (world_pos.y - fable_mirror_clip0.w));
		} else if (fable_mirror_alpha.y > 0.0 && distance(cam_world, fable_mirror_clip1.xyz) < 0.02) {
			clip.z = clip.w - (fable_mirror_alpha.y * (world_pos.y - fable_mirror_clip1.w));
		}
	}
	return clip;
}
