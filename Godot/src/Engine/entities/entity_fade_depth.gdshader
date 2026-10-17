shader_type spatial;
// A FADED ENTITY KEEPS ITS DEPTH. The title draws a partial alpha as a constant blend
// (`sub_822251D8`: BLENDFACTOR / INVBLENDFACTOR) without touching ZWriteEnable, and its depth
// passes skip the alpha, so only the entity's nearest surface blends over what is behind it.
// Godot moves a faded instance (GeometryInstance3D.Transparency) out of the depth pre-pass, so every
// layer of the body blended — skin through the coat, the far leg through the near one. This
// overlay, drawn first in the transparent pass (render priority -128), puts the depth back.
render_mode unshaded, blend_add, depth_draw_always, cull_disabled, shadows_disabled, fog_disabled;

void fragment() {
	ALBEDO = vec3(0.0);
	ALPHA = 0.0;
}
