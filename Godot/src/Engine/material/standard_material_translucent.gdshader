shader_type spatial;
// TRANSLUCENT: glass, lamp panes, windows, hair. The title marks these by their diffuse texture, not
// the material: header flag 0x80 (`sub_82AA04A0`: texture-set +154). Such a surface draws twice
// (Engine_MdlMaterial): its opaque pass keeps the texels with alpha over 254 and writes depth, and
// this is the second draw — `sub_8221BD40` bucket 3, back to front after the resolve, depth writes
// off, SrcBlend ONE (`sub_822251D8`) over DestBlend INVSRCALPHA: PREMULTIPLIED, the lit colour added
// unscaled and the texture's alpha only attenuating what is behind. One side only: a standard
// material never changes the view's CCW cull (`sub_8221BD40`), so a pane is not drawn again from
// behind, where its inner faces blended in triangle order and the glass changed with the angle.
render_mode blend_premul_alpha, depth_draw_never, cull_back;

#define FABLE_TRANSLUCENT

#include "res://src/Engine/material/standard_material_body.gdshaderinc"
