// THE END OF THE PROPS' fragment() in a lite build (standard_material_body.gdshaderinc).
#ifdef FABLE_LITE_NM_DIRECT
    // Last: everything above reads the interpolated NORMAL. Without a map it is NORMAL itself.
    NORMAL = mapped_view;
#endif
#ifdef FABLE_LITE_AMBIENT_FOLD
    // Godot's ambient, compiled out of this build, from the values Godot would have read (fable_lite_lighting).
    EMISSION += fable_godot_ambient_fold(ALBEDO, AO, METALLIC);
#endif
