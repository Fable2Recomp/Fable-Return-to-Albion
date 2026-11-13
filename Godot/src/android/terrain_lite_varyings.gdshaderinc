// WHAT THE GROUND'S vertex() HANDS fragment() in a lite build: terrain_splat_body.gdshaderinc's
// varyings, declared under fable_lite_varyings' FABLE_TERRAIN_* switches (a lite build may carry
// less, and fragment() then makes what it reads of them itself, FABLE_TERRAIN_FRAGMENT_IN; or more:
// the contact AO per vertex, FABLE_VERTEX_TERRAIN_AO, and the fog per vertex).
#ifdef FABLE_TERRAIN_LOCAL_VARYING
varying vec3 v_local;
#endif
#ifdef FABLE_TERRAIN_NORMAL_VARYING
varying FABLE_VARY_MP vec3 v_local_normal;
#endif
#ifdef FABLE_TERRAIN_PAGE_RECT_VARYING
varying flat vec4 v_page_rect;
#endif
varying float v_combined_ao;
#ifdef FABLE_VERTEX_TERRAIN_AO
varying FABLE_VARY_MP float v_terrain_contact_ao;
#endif
#ifdef FABLE_LITE_TERRAIN_VERTEX_FOG
// The fog's two terms per vertex (`lite_terrain_vertex_fog`), as standard_material's: T = 1, C = 0 where none.
varying FABLE_VARY_MP vec3 v_fog_t;
varying FABLE_VARY_MP vec3 v_fog_c;
#endif
