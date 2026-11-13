// THE GROUND'S NORMAL MAP OVER ITS ANALYTIC FRAME (FABLE_LITE_TERRAIN_NM_ANALYTIC), in place of
// terrain_splat_body.gdshaderinc's NORMAL_MAP write.
		// The mesh's frame (Engine_LevelScene) is analytic, so it is rebuilt here instead of carried:
		// T the mesh's +X Gram-Schmidt'd against the normal, B cross(N, T) along the mesh's -Z (the
		// ground's v), times the side test's sign as BINORMAL above; NORMAL written, not NORMAL_MAP.
		vec3 nm_x = mat3(VIEW_MATRIX) * MODEL_MATRIX[0].xyz;
		vec3 nm_t = nm_x - (NORMAL * dot(NORMAL, nm_x));
		nm_t *= inversesqrt(max(dot(nm_t, nm_t), 1e-30));
		vec3 nm_b = cross(NORMAL, nm_t);
		nm_b *= dot(nm_b, mat3(VIEW_MATRIX) * MODEL_MATRIX[2].xyz) > 0.0 ? -side : side;
		NORMAL = normalize((nm_t * mapped_normal.x) + (nm_b * mapped_normal.y) + (NORMAL * mapped_normal.z));
