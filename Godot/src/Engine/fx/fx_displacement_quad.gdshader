// A guest displacement quad (the drunk warp) into the modern displacement layer: its texture's
// offset bytes added as they are, for fx_displacement_apply to read.
shader_type canvas_item;
render_mode blend_add, unshaded;

void fragment() {
	COLOR = texture(TEXTURE, UV);
}
