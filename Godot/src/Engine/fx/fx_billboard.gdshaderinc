// Camera-facing billboard expansion. The CPU emits each quad as six vertices at the particle's
// centre with the corner offset in UV2, already in camera right/up units with the particle's roll
// folded in; this adds the offset along the camera axes, so emitted geometry never depends on
// camera orientation (ribbons still depend on camera position).
//
// The axes are INSTANCE uniforms: one material is shared by every effect, but each effect's mesh
// is in its own node's space, so the host converts the camera basis into that space per node per
// frame. Axis-aligned visuals are emitted pre-expanded with a zero offset.
//
// The defaults keep a node that has not yet been given its axes drawing a correctly sized quad.
instance uniform vec3 fx_cam_right = vec3(1.0, 0.0, 0.0);
instance uniform vec3 fx_cam_up = vec3(0.0, 1.0, 0.0);

void vertex() {
	VERTEX += (fx_cam_right * UV2.x) + (fx_cam_up * UV2.y);
}
