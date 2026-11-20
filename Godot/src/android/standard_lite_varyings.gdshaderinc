// WHAT THE PROPS' vertex() HANDS fragment() in a lite build: standard_material_body.gdshaderinc's
// varyings, declared under fable_lite_varyings' switches (a lite build may carry less, or more:
// the per-vertex fog and ground AO).
#ifdef FABLE_TREE_FADE_VARYING
varying FABLE_TREE_FADE_QUAL float v_tree_fade;
#endif
varying float v_combined_ao;
#ifdef FABLE_LITE_VERTEX_FOG
varying FABLE_VARY_MP vec3 v_fog_t;
varying FABLE_VARY_MP vec3 v_fog_c;
#endif
#ifdef FABLE_GROUND_AO_VARYING
varying FABLE_VARY_MP vec4 v_prop_ground_ao;
#elif defined(FABLE_GROUND_HEIGHT_VARYING)
varying FABLE_VARY_MP float v_prop_ground_height;
#endif
#ifdef FABLE_LITE_FLAT_LEAVES
// Flat leaves' sun (standard_lite_fragment_end), as Engine_LevelBakedLighting publishes it each frame
// for the water too: toward the sun in world space, and its linear colour times energy, the light
// Godot's DirectionalLight3D decodes to.
global uniform vec3 fable_sun_toward;
global uniform vec3 fable_sun_colour;
#endif
