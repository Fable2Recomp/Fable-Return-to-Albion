shader_type spatial;
// A TREE (.mdl byte 77 == 2): the two-sided standard surface moved by the title's tree wind
// (fable_tree_sway.gdshaderinc, the TREEMATERIAL_TRUNK1 stage). Its own shader because a vertex
// function that writes VERTEX refuses Godot's shared depth-only shadow material; the clock is a
// global, not TIME, so a lamp's map holding a tree is not marked dirty every frame.
render_mode blend_mix, depth_draw_opaque, cull_disabled;

#define FABLE_TREE_WIND
#include "res://src/Engine/material/standard_material_body.gdshaderinc"
