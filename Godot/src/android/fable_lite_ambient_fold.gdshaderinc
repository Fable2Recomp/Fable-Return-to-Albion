// GODOT'S OWN COLOUR AMBIENT, CARRIED BY THE SHADER (a phone's modern lite builds with
// `android_modern_ambient_fold`, AndroidModernAmbientFold): those builds are composed with
// `ambient_light_disabled`, and the body adds back what the Mobile renderer would have added,
// `ambient * AO * ALBEDO * (1 - METALLIC)` (scene_forward_mobile.glsl), from the final values.
// Included by fable_lighting.gdshaderinc in a lite build right after its fable_fill_floor global: Godot
// emits the global uniforms in declaration order, so the one below keeps its place among them.
#ifdef FABLE_LITE_AMBIENT_FOLD
/// The environment's Color-source ambient as the renderer reads it, `srgb_to_linear(colour) *
/// energy`; zero where the environment has none.
global uniform vec3 fable_godot_ambient = vec3(0.0);

vec3 fable_godot_ambient_fold(vec3 albedo, float ao, float metallic) {
	return albedo * fable_godot_ambient * (ao * (1.0 - metallic));
}
#endif
