// THE PROPS' SIDE FLIP AND NORMAL MAP in a lite build (standard_material_body.gdshaderinc's
// fragment(), whose comments explain both): the phone's normal-map paths, `FABLE_LITE_NM_*`
// (standard_lite_switches), and no tangent frame named at all in a FABLE_SPEC_NO_NORMAL_MAP build,
// since Godot's compiler keeps a dead branch's built-ins as used (TANGENT_USED, NORMAL_MAP_USED: the
// tangent attribute, its varyings and Godot's own normal-map maths).
    float side = 1.0;
    if (!FABLE_FOLIAGE_NORMALS) {
        side = fable_normal_side(NORMAL, VERTEX, VIEW);
        NORMAL *= side;
#if !defined(FABLE_SPEC_NO_NORMAL_MAP) && !defined(FABLE_LITE_NM_DERIV)
        BINORMAL *= side;
#endif
    }
    float ndotn = 1.0;
    vec3 nm = vec3(0.5, 0.5, 1.0);
    vec3 mapped_view = NORMAL;
#ifndef FABLE_SPEC_NO_NORMAL_MAP
    if (FABLE_HAS_NORMAL) {
#ifdef FABLE_LITE_NM_FRAME_ONLY
        // The frame kept live by a tilt far under a texel's worth; no fetch.
        mapped_view = normalize(NORMAL + ((TANGENT + BINORMAL) * 1e-4));
#else
        nm = texture(normal_tex, uv).rgb;
#ifndef FABLE_LITE_NM_DIRECT
        NORMAL_MAP = nm;
#endif
        vec2 nxy = (nm.xy * 2.0) - 1.0;
        ndotn = sqrt(max(1.0 - dot(nxy, nxy), 0.0));
#ifdef FABLE_LITE_NM_DERIV
        // The mesh's own frame rebuilt from screen derivatives of VERTEX (view space, so small
        // numbers) and UV. The game's TANGENT runs along +u and Godot's BINORMAL, cross(N, T) * w,
        // along +v (DirectX), mirrored UV islands included: 99.99% of globals_models.bnk's vertices.
        // So T is the u gradient (Schueler's cotangent t, the sign of D = dot(N, face) divided out)
        // normalised, B is cross(N, T) turned to the v gradient's side — the handedness the file's
        // w carries — and like BINORMAL above it takes the side test's sign.
        vec3 dp1 = dFdx(VERTEX);
        vec3 dp2 = dFdy(VERTEX);
        vec2 duv1 = dFdx(uv);
        vec2 duv2 = dFdy(uv);
        vec3 dp2perp = cross(dp2, NORMAL);
        vec3 dp1perp = cross(NORMAL, dp1);
        vec3 t = (dp2perp * duv1.x) + (dp1perp * duv2.x);
        vec3 b = (dp2perp * duv1.y) + (dp1perp * duv2.y);
#ifdef FABLE_LITE_LEAN_FRAME_SIGN
        // `lite_lean_frame_sign` (AndroidLeanTerms): the triple product below as dp2 . (N x dp1),
        // N x dp1 being dp1perp, one cross product fewer. Exact in real arithmetic; the float
        // roundings can disagree only where it is within rounding of zero, a triangle seen edge-on.
        float frame_sign = dot(dp1perp, dp2) < 0.0 ? -1.0 : 1.0;
#else
        float frame_sign = dot(NORMAL, cross(dp1, dp2)) < 0.0 ? -1.0 : 1.0;
#endif
        t *= frame_sign * inversesqrt(max(dot(t, t), 1e-30));
        vec3 bn = cross(NORMAL, t);
        float hand = (dot(bn, b) * frame_sign) < 0.0 ? -side : side;
        mapped_view = normalize((t * nxy.x) + (bn * (nxy.y * hand)) + (NORMAL * ndotn));
#else
        mapped_view = normalize(TANGENT * nxy.x + BINORMAL * nxy.y + NORMAL * ndotn);
#endif
#endif
    }
#endif
