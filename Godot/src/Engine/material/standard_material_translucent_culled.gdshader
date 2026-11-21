shader_type spatial;
// TRANSLUCENT WITH BACK FACES CULLED: second-pass blended draw for translucent surfaces (flag 0x80)
// whose mesh already carries explicit opposite-wound front and back triangle twins, so the back-side
// twin never overwrites the front-side twin under depth_draw_never.
render_mode blend_mix, depth_draw_never, cull_back;

#define FABLE_TRANSLUCENT

#include "res://src/Engine/material/standard_material_body.gdshaderinc"
