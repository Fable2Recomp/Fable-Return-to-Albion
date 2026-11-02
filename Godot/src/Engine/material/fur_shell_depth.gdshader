// A FADED COAT KEEPS ITS DEPTH (a REMASTER choice, with the shells' fade: Engine_ModernFur). Opaque
// shells hide each other by depth, the nearest surviving texel winning; blended, every surviving
// shell would add over the next and the coat would stay near opaque while the body faded. Each
// shell is drawn once here first (render priority -128, as the body's entity_fade_depth), with
// the colour shells' own vertex stage and cut, writing depth and nothing else; the faded colour
// shells then pass only at the nearest texel.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_always, cull_back, shadows_disabled, fog_disabled;

#define FUR_SHELL_DEPTH
#include "res://src/Engine/material/fur_shell.gdshaderinc"
