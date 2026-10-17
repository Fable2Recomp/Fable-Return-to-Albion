// Draws nothing. A surface that uses the stencil in the opaque list makes Forward+ run its depth
// pre-pass for that render (`used_opaque_stencil`), whatever the project setting says; see
// Engine_DepthPrepassMarker.
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled;
stencil_mode write, compare_always, 0;

void fragment() {
	discard;
}
