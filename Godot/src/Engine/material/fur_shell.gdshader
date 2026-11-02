// SHELL FUR, MODERN (Engine_ModernFur): the furred surface drawn again once per shell, pushed out
// along its normal, cut by the fur slices, and lit by Godot's lights through the shared model.
// Learned from the title's shell stages; not its code.
//
// Per shell, h = layer / N (layer 1..N-1). Per vertex, its fur record: COLOR.a = the hair's
// length share, CUSTOM0 = the fur texture's uv at the root (xy) and its drift per unit h (zw).
//
//   reach      r = (h + length - 1) L                        L = the fur length for the distance
//   position   vertex + r normal
//   occlusion  sat(1 - (1 - h)^2 k)                          k = the material's occlusion
//   skin share (1 - h) / length (1 when length < 0.01): the root takes the skin, the tip the fur
//   alpha      slice.a (1 - e) + e, e = 0.3 sat(|eye - p| / 200 L) |N.V| — far fur fills in;
//              zero where the hair ends before this shell (r <= 0)
//   colour     occlusion mix(slice.rgb lit fur, lit skin, share), skin the surface's albedo
//
// THE FUR'S LIGHT (the fur material's stage, which the title bakes into its prelit fur texture):
// T = the view projected into the surface, normalize(V - (N.V) N); R = reflect(-L, T). Per light,
// gated by sat(2 N.L + 0.2): diffuse albedo sat(T.L); a primary lobe (0.5, 0.5, 0.55)
// sat(4 T.L) (R.V)^4 and a secondary (0.3, 0.3, 0.35) (R.V)^7 (the primary is summed with both
// lobe colours). The skin takes plain Lambert, as its own surface does.
//
// The slice is floor(clamp(h, 0, 0.9999) 20) of the material's 20 (Engine_ClassicFur.SlicesFor).
//
// THE CUT AND THE FADE. alpha < 16/255 is discarded (the title's ALPHAREF 16, GREATEREQUAL) and
// no ALPHA is written, so a shell is opaque at rest and takes its instance's Transparency when
// faded. That fade is a REMASTER choice: the title's shell pass never reads the opacity
// (Engine_EntityAppearance, "THE FUR SHELLS FADE WITH THE BODY").
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back;

#include "res://src/Engine/material/fur_shell.gdshaderinc"
