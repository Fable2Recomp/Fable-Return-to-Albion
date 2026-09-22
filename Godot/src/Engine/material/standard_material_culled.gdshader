shader_type spatial;
// BACK FACES CULLED, as the guest draws every ordinary mesh: its main pass leaves the view's cull
// mode on the device — index 0 of the table at 0x83317064, CCW culled — and no standard material
// changes it (`sub_8221BD40` 0x8221BDE0..0x8221BE50; `sub_82220F48` writes no cull state). The
// models are wound for it (`WindingCensus`), and a garment modelled with both sides draws only the
// side that faces the eye instead of whichever copy won the depth test.
render_mode blend_mix, depth_draw_opaque, cull_back;

// SOLID: the body compiles without its VERTEX write (wind) and without its discard (alpha
// scissor). Neither can apply to a material built with this shader — it is chosen for opaque
// albedo only, and anything that sways or is vegetation is handed back to the two-sided shader
// (`Engine_MdlMaterial.TryCreate`, `ApplyWind`, the vegetation swap) — so the picture is the
// same. What changes is the SHADOW pass: with no vertex write, no discard and back-face culling
// the shader satisfies `uses_shared_shadow_material()` (scene_shader_forward_clustered.h:344),
// so Godot draws these surfaces' shadows with its default depth-only material — one pipeline
// for all of them, no per-draw material set, no albedo fetch in the shadow fragment, and
// consecutive draws of the same mesh merge across materials.
#define FABLE_SOLID

#include "res://src/Engine/material/standard_material_body.gdshaderinc"
