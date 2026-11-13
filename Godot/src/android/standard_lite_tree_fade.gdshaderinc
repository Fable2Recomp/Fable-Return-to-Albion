// THE PROPS' TREE FADE in a lite build (standard_material_body.gdshaderinc's vertex()): upstream's,
// worked in a local and handed on as fable_lite_varyings says (its own varying, COLOR.g, or none).
    float tree_fade = 1.0;
#ifndef FABLE_SOLID
    tree_fade = xr_fade_alpha;
    if (xr_tree_fade_out.x > 0.0) {
        float d = distance(MAIN_CAM_INV_VIEW_MATRIX[3].xyz, (MODEL_MATRIX * vec4(xr_tree_centre, 1.0)).xyz);
        float fade_in = xr_tree_fade_in.y > 0.0
            ? pow(clamp((d - xr_tree_fade_in.x) * xr_tree_fade_in.y, 0.0, 1.0), xr_tree_fade_in.z)
            : 1.0;
        float fade_out = pow(clamp((xr_tree_fade_out.x - d) * xr_tree_fade_out.y, 0.0, 1.0), xr_tree_fade_out.z);
        tree_fade *= fade_in * fade_out;
    }
#endif
#ifndef FABLE_TREE_FADE_NONE
    v_tree_fade = tree_fade;
#endif
