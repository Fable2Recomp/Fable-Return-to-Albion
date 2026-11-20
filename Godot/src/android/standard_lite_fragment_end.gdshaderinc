// THE END OF THE PROPS' fragment() in a lite build (standard_material_body.gdshaderinc).
#ifdef FABLE_LITE_NM_DIRECT
    // Last: everything above reads the interpolated NORMAL. Without a map it is NORMAL itself.
    NORMAL = mapped_view;
#endif
#ifdef FABLE_LITE_AMBIENT_FOLD
    // Godot's ambient, compiled out of this build, from the values Godot would have read (fable_lite_lighting).
    EMISSION += fable_godot_ambient_fold(ALBEDO, AO, METALLIC);
#endif
#ifdef FABLE_LITE_FLAT_LEAVES
    // FLAT LEAVES (lite_flat_leaves, a tree build drawn `unshaded`, AndroidFlatLeaves): no light() and
    // no normal, so the dense tree meshes stop carrying a normal and tangent per vertex. A leaf takes
    // half the sun's Lambert instead of its own facing (light()'s directional term: fable_direct's
    // colour / PI against Godot's PI-scaled LIGHT_COLOR, no shadow at Low), times the direct AO, plus
    // the bake and the fog's in-scatter in EMISSION, both fogged above; unshaded draws ALBEDO alone.
    ALBEDO = ALBEDO * (fable_sun_colour * (0.5 * fable_direct_ao_for_light(v_combined_ao, true))) + EMISSION;
    EMISSION = vec3(0.0);
#endif
#if defined(FABLE_DEBUG_CUTOUTS_DEPTH_ONLY) && !defined(FABLE_SOLID) && !defined(FABLE_TRANSLUCENT)
    // `debug_cutouts = "depth_only"` (AndroidFlatCutouts): the build is `unshaded` and draws black, so
    // only the vertex stage, the alpha fetch and its discard are left: the work of a depth pre-pass.
    ALBEDO = vec3(0.0);
    EMISSION = vec3(0.0);
#endif
