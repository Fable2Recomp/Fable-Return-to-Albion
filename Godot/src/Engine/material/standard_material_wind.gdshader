shader_type spatial;
// // THE ONE SHADER THAT READS TIME. Godot calls a material "animated" when its vertex function
// // reads TIME and writes VERTEX (scene_shader_forward_clustered.cpp, `is_animated`), and a light
// // whose shadow pass holds one animated caster is marked dirty again after every render
// // (`_light_instance_update_shadow` returns `animated_material_found`), so that map re-renders
// // every frame for as long as the caster is in reach. The wind is therefore its own shader:
// // only a surface that sways is animated.
render_mode blend_mix, depth_draw_opaque, cull_disabled;

#define FABLE_WIND
#include "res://src/Engine/material/standard_material_body.gdshaderinc"
