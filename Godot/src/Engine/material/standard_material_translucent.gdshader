shader_type spatial;
// TRANSLUCENT: glass, lamp panes, windows, hair cards. The title marks these by their diffuse texture,
// not the material: header flag 0x80 (`sub_82AA04A0`: texture-set +154). Such a surface draws twice
// (Engine_MdlMaterial): its opaque pass keeps the texels with alpha over 254 and writes depth, and
// this is the second draw — `sub_8221BD40` bucket 3, back to front after the resolve, depth writes
// off, blending semi-transparent pixels over the background. Using blend_mix so lit albedo and
// specular scale by the texture's alpha rather than additively stamping transparent card padding.
// Both sides are drawn (cull_disabled) so single-quad hair cards and ribbons remain visible from
// all angles.
render_mode blend_mix, depth_draw_never, cull_disabled;

#define FABLE_TRANSLUCENT

#include "res://src/Engine/material/standard_material_body.gdshaderinc"
