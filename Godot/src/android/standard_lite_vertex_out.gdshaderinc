// THE END OF THE PROPS' vertex() in a lite build (standard_material_body.gdshaderinc): the world
// position where the vertex is drawn (world space under FABLE_WORLD_VERTEX), carried or not
// (fable_lite_varyings), then what this build takes per vertex: the fog's terms, the ground AO.
#ifdef FABLE_WORLD_VERTEX
    vec3 world_at = VERTEX;
#else
    vec3 world_at = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
#endif
#ifdef FABLE_WORLD_VARYING
    fable_world = world_at;
#endif
#ifdef FABLE_LITE_VERTEX_FOG
    // The fog's terms at the drawn vertex (after wind and sway, as fragment()'s VERTEX would be).
    // A vertex the fog does not reach leaves T = 1 and C = 0, which fable_atmosphere_apply passes through.
    vec3 fog_t = vec3(1.0);
    vec3 fog_c = vec3(0.0);
    fable_atmosphere_terms_for((VIEW_MATRIX * vec4(world_at, 1.0)).xyz, INV_VIEW_MATRIX, !FABLE_FOLIAGE, fog_t, fog_c);
    v_fog_t = fog_t;
    v_fog_c = fog_c;
#endif
#ifdef FABLE_VERTEX_GROUND_AO
    // The ground-contact fetch, per vertex where it is drawn (fable_lite_ground_ao.gdshaderinc).
#ifdef FABLE_GROUND_AO_IN_COLOR
    COLOR.rba = fable_vertex_prop_ground_ao(COLOR.a, world_at).xyz;
#else
    v_prop_ground_ao = fable_vertex_prop_ground_ao(COLOR.a, world_at);
#endif
#elif defined(FABLE_NEAR_GROUND_AO)
#ifdef FABLE_SPEC_GROUND_FROM_ORIGIN
    // A character build (fable_lite_varyings, AndroidCharacterShading): the gate from the model's origin.
    v_prop_ground_height = max(world_at.y - MODEL_MATRIX[3].y, 0.0);
#else
    v_prop_ground_height = fable_vertex_prop_ground_ao(COLOR.a, world_at).y;
#endif
#endif
    FABLE_PROP_VERTEX_OUT
#if defined(FABLE_DEBUG_CUTOUTS_HIDDEN) && !defined(FABLE_SOLID) && !defined(FABLE_TRANSLUCENT)
    // `debug_cutouts = "hidden"` (AndroidFlatCutouts): every vertex of a build that can discard lands
    // outside the clip volume, so its triangles are culled before any pixel; the vertex work stays.
    POSITION = vec4(4.0, 4.0, 0.5, 1.0);
#endif
