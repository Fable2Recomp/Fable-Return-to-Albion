shader_type spatial;
// TRANSLUCENT: glass, lamp panes, bottles. The title marks these by their diffuse texture, not the
// material: header flag 0x80 (`sub_82AA04A0`: texture-set +154), which sends the item to the
// back-to-front translucent bucket drawn after the resolve with depth writes off
// (`sub_822251D8`). The texture's alpha is the opacity; no scissor.
render_mode blend_mix, depth_draw_never, cull_disabled;

#define FABLE_TRANSLUCENT

#include "res://src/Engine/material/standard_material_body.gdshaderinc"
