// Camera-facing billboard expansion. The CPU emits each quad as six vertices at the particle's
// centre with the corner offset in UV2, already in camera right/up units with the particle's roll
// folded in; this adds the offset along the camera axes, so emitted geometry never depends on
// camera orientation (ribbons still depend on camera position).
//
// The axes are the VIEW's: the camera basis this draw is rendered through (INV_VIEW_MATRIX), taken
// into the effect node's space (each effect's mesh is in its own node's space) and normalised —
// the same conversion the host made once per frame for the window camera
// (`GlobalTransform.AffineInverse() * camera.GlobalTransform`, basis X/Y normalised). Per view,
// so on split screen each player's billboards face that player's camera. Axis-aligned visuals are
// emitted pre-expanded with a zero offset.
//
// BETWEEN SIMULATION STEPS. The simulation keeps the authored 30 frames a second (15 for a
// half-rate emitter) and geometry is emitted only when it steps, so a particle drawn at its last
// step held still for several display frames. Each vertex carries the last step's displacement in
// CUSTOM0.xyz and its rate class in CUSTOM0.w (0 full, 1 half); the host publishes, per frame,
// how far the effect is between its steps. The particle is drawn on the segment it just moved
// along: centre - motion x (1 - alpha). A mesh without CUSTOM0 reads zero and draws as before.
//
instance uniform float fx_step_alpha = 1.0;
instance uniform float fx_step_alpha_half = 1.0;

void vertex() {
	float alpha = mix(fx_step_alpha, fx_step_alpha_half, CUSTOM0.w);
	VERTEX -= CUSTOM0.xyz * (1.0 - alpha);
	mat3 to_node = inverse(mat3(MODEL_MATRIX));
	vec3 cam_right = normalize(to_node * INV_VIEW_MATRIX[0].xyz);
	vec3 cam_up = normalize(to_node * INV_VIEW_MATRIX[1].xyz);
	VERTEX += (cam_right * UV2.x) + (cam_up * UV2.y);
}
