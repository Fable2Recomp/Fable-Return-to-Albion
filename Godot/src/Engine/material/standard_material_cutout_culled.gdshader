shader_type spatial;
// CUTOUT WITH BACK FACES CULLED: for alpha-scissored or translucent-base surfaces whose mesh
// explicitly models both sides as opposite-wound coplanar triangle twins (e.g. ESA_Shop_Price_List,
// double-sided banners, and cutout trim), preventing coplanar front/back Z-fighting while keeping
// depth_prepass_alpha active.
render_mode blend_mix, depth_draw_opaque, cull_back;

#include "res://src/Engine/material/standard_material_body.gdshaderinc"
