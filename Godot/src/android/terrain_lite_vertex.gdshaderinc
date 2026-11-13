// THE GROUND'S vertex() in a lite build (terrain_splat_body.gdshaderinc): the varyings it carries
// (terrain_lite_varyings), then what this build takes per vertex: the contact AO, the fog's terms.
#ifdef FABLE_TERRAIN_LOCAL_VARYING
	v_local = VERTEX;
#endif
#ifdef FABLE_TERRAIN_PAGE_RECT_VARYING
	v_page_rect = CUSTOM0;
#endif
#ifdef FABLE_TERRAIN_NORMAL_VARYING
	v_local_normal = NORMAL;
#endif
	vec3 world_at = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
#ifdef FABLE_WORLD_VARYING
	fable_world = world_at;
#endif
#ifdef FABLE_VERTEX_TERRAIN_AO
	// The contact AO per vertex (fable_lighting.gdshaderinc); vertex() reads the map's base level.
	v_terrain_contact_ao = fable_sample_terrain_ao(world_at);
#endif
#ifdef FABLE_LITE_TERRAIN_VERTEX_FOG
	vec3 fog_t = vec3(1.0);
	vec3 fog_c = vec3(0.0);
	fable_atmosphere_terms((VIEW_MATRIX * vec4(world_at, 1.0)).xyz, INV_VIEW_MATRIX, fog_t, fog_c);
	v_fog_t = fog_t;
	v_fog_c = fog_c;
#endif
