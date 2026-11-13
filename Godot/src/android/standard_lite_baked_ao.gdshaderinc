// THE PROPS' BAKED AO in a lite build (standard_material_body.gdshaderinc's fragment()): the world
// position rebuilt here if it was not carried (fable_lite_varyings), then the ground-contact AO per
// vertex, near the floor only, or upstream's per pixel (fable_lite_ground_ao).
    FABLE_PROP_FRAGMENT_WORLD
#ifdef FABLE_VERTEX_GROUND_AO
    float baked_ao = fable_prop_baked_ao_from_vertex(v_prop_ground_ao);
#elif defined(FABLE_NEAR_GROUND_AO)
    float baked_ao = fable_near_prop_baked_ao(COLOR.a, fable_world, v_prop_ground_height);
#else
    float baked_ao = fable_evaluate_prop_baked_ao(COLOR.a, fable_world);
#endif
