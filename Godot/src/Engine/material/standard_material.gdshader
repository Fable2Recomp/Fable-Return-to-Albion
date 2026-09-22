shader_type spatial;
// BOTH SIDES: what the guest draws with culling off — a tree's canopy and trunk materials, the one
// material-driven exception in its draw path (`sub_82210DD0`: the first texture-set record has its
// +24 slot filled) — and families whose winding has not been measured.
render_mode blend_mix, depth_draw_opaque, cull_disabled;

#include "res://src/Engine/material/standard_material_body.gdshaderinc"
