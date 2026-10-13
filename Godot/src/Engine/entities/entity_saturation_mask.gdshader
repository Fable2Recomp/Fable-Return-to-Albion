shader_type spatial;
// THE SATURATION MASK (`GraphicAppearance.SetSaturationMask`, comp3+196): the entity's own surface
// drawn once more, adding nothing to the colour, to mark its visible pixels in the stencil. The
// post-process reads the mark (Engine_PostFxSaturationMask) where the title reads
// g_SaturationMaskSampler, and gives those pixels Saturation + SaturationMasked (shader 0505).
// Equal depth passes the default test, so only the pixels the entity itself won are marked.
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;
stencil_mode write, compare_always, 128;

void fragment() {
	ALBEDO = vec3(0.0);
	ALPHA = 0.0;
}
