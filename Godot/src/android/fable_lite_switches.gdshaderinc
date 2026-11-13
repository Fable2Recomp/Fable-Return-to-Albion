// THE PHONE'S LIGHTING SWITCHES: the macros a lite build (FABLE_LITE, Engine_ShaderVariant) gives
// fable_lighting.gdshaderinc's hooks. Included at the top of fable_lighting in every lite build, so
// before either body or the phone's functions (fable_lite_lighting.gdshaderinc, at its end) use
// them; a full build takes upstream's spelling of each hook from fable_lighting's #else instead.
// Macros only: nothing here emits code.

// RELAXED PRECISION, a phone option (`lite_half_precision_lighting`, Engine_ShaderVariant). With
// FABLE_LITE_MEDIUMP the light() maths that is a cosine, a colour or a falloff factor is declared
// mediump — SPIR-V RelaxedPrecision, which a phone GPU may run at half precision. Positions,
// distances, light-table reads and the specular cosine (raised to the material's exponent) stay
// highp. Without it the qualifier is empty.
// LOCALS ONLY, NEVER A PARAMETER: re-spirv (Godot's Vulkan driver, the pass that folds a pipeline's
// specialisation constants) cannot parse RelaxedPrecision on an OpFunctionParameter — "SPIR-V
// Parsing error. Invalid instruction word count" — and the shader then goes without that pass. A
// helper copies its inputs into qualified locals instead (fable_lite_lighting's fable_direct and
// fable_specular); an expression is relaxed only when all of its operands are.
#ifdef FABLE_LITE_MEDIUMP
#define FABLE_LITE_MP mediump
#else
#define FABLE_LITE_MP
#endif

// THE FABLE MODEL'S FIXED GLOBALS AS CONSTANTS (`lite_fold_fable_model`, Engine_ShaderVariant). A
// fable-model lite build is drawn only by a process under the fable model, and there
// Engine_LevelLighting publishes the same values for the life of the process: fable_unified 0, the
// sky tether's weight 0, no interior (0) and its bake lift 1, and no fill floor. As constants every
// mix, max and multiply by them folds at compile time, to the same values. The modern builds keep
// the globals. Without the define each macro is its global.
#if defined(FABLE_LITE) && defined(FABLE_LITE_FOLD_FABLE_MODEL) && !defined(FABLE_LITE_MODERN)
#define FABLE_UNIFIED 0.0
#define FABLE_SKY_TETHER 0.0
#define FABLE_INTERIOR 0.0
#define FABLE_INTERIOR_BAKE 1.0
#define FABLE_FILL_FLOOR vec3(0.0)
#else
// The modern builds' one fixed global (`lite_fold_modern_model`): Engine_LevelLighting publishes
// fable_unified 1 under the modern model, caves included, and these builds already compile out
// every bake it multiplies by zero, so they assume it.
#if defined(FABLE_LITE_MODERN) && defined(FABLE_LITE_FOLD_MODERN_MODEL)
#define FABLE_UNIFIED 1.0
#else
#define FABLE_UNIFIED fable_unified
#endif
#define FABLE_SKY_TETHER fable_sky_ambient.w
#define FABLE_INTERIOR fable_interior
#define FABLE_INTERIOR_BAKE fable_interior_bake
#define FABLE_FILL_FLOOR fable_fill_floor
#endif

// THE MODERN BUILDS' AO DIALS AT THEIR DEFAULTS (`lite_fold_modern_model`, composed only when the
// process starts with baked AO on at strength 1 and light-affect 0.25, Engine_ShaderVariant):
// Engine_LevelAmbientOcclusion publishes exactly those, so the strength's pow and the light-affect
// fold. A change to either, or baked AO turned off, then takes effect at the next start.
#if defined(FABLE_LITE_MODERN) && defined(FABLE_LITE_FOLD_AO_DEFAULTS)
#define FABLE_AO_LIGHT_AFFECT 0.25
#define FABLE_AO_STRENGTH 1.0
#else
#define FABLE_AO_LIGHT_AFFECT fable_ao_light_affect
#define FABLE_AO_STRENGTH fable_ao_strength
#endif

// A phone build with baked AO off compiles the ground-contact AO out (Engine_ShaderVariant): AO off
// at runtime only zeroes the strength, and the fetch and maths would stay in every shader. Finer
// switches for a phone's A/B (refii.toml `lite_extra_defines`, Engine_LiteExtraDefines) each drop one part:
//   _NO_PROP_GROUND_AO    props skip the ground-contact taps and height match (vertex x instance AO stay)
//   _NO_TERRAIN_GROUND_AO the terrain skips its ground AO fetch (its AO is 1)
//   _NO_DIRECT_AO         light() does not weight direct light (or its specular) by the AO
//   _AO_LINEAR            the strength is a mix instead of a pow (the same value at strengths 0 and 1)
//   _VERTEX_GROUND_AO     props take the ground-contact taps in vertex(), not per pixel: the vertex
//                         hands the fragment the walkable floor's AO and its own height above that
//                         floor (FABLE_VERTEX_GROUND_AO below), and only the height fade and the min
//                         stay per pixel. An approximation of the per-pixel match. The terrain keeps
//                         its fetch: its flat ground is decimated to triangles metres wide
//                         (Engine_TerrainLod), wider than a contact shadow.
//   _NEAR_GROUND_AO       props keep the per-pixel taps, taken only where the vertices' height above
//                         the walkable floor puts the pixel within FABLE_NEAR_GROUND_REACH of it:
//                         a tap more than 0.42 m from the pixel's height weighs exactly nothing, so
//                         past the reach the contact AO is exactly 1 (FABLE_NEAR_GROUND_AO)
//   _VERTEX_TERRAIN_AO    the terrain fetches its contact AO per vertex (FABLE_VERTEX_TERRAIN_AO):
//                         a blur, its grid being coarser than the map (AndroidGroundShading)
//   _GROUND_AO_GATHER     the four taps as textureGather (the same picture): the phone's include,
//                         fable_lite_ground_ao
// The functions these replace are fable_lighting's, under FABLE_LITE_OWN_TERRAIN_AO and
// FABLE_LITE_OWN_PROP_AO (and its AO weights under FABLE_LITE); the phone's are in fable_lite_lighting.
#if defined(FABLE_LITE) && defined(FABLE_LITE_VERTEX_GROUND_AO) && !defined(FABLE_LITE_NO_BAKED_AO) && !defined(FABLE_LITE_NO_PROP_GROUND_AO)
#define FABLE_VERTEX_GROUND_AO
#elif defined(FABLE_LITE) && defined(FABLE_LITE_NEAR_GROUND_AO) && !defined(FABLE_LITE_NO_BAKED_AO) && !defined(FABLE_LITE_NO_PROP_GROUND_AO)
#define FABLE_NEAR_GROUND_AO
#define FABLE_NEAR_GROUND_REACH 1.0
#endif
#if defined(FABLE_LITE) && defined(FABLE_LITE_VERTEX_TERRAIN_AO) && !defined(FABLE_LITE_NO_BAKED_AO) && !defined(FABLE_LITE_NO_TERRAIN_GROUND_AO)
#define FABLE_VERTEX_TERRAIN_AO
#endif
#if defined(FABLE_LITE_NO_BAKED_AO) || defined(FABLE_LITE_NO_TERRAIN_GROUND_AO)
#define FABLE_LITE_OWN_TERRAIN_AO
#endif
#if defined(FABLE_LITE_NO_BAKED_AO) || defined(FABLE_LITE_NO_PROP_GROUND_AO)
#define FABLE_LITE_OWN_PROP_AO
#endif

// THE GROUND'S LAYER SAMPLERS (terrain_splat_body's lod_array and lod_detail_array): trilinear under
// FABLE_LITE_TERRAIN_TRILINEAR (`lite_terrain_trilinear`, terrain_lite_layers), else upstream's anisotropic.
#ifdef FABLE_LITE_TERRAIN_TRILINEAR
#define FABLE_TERRAIN_LOD_FILTER filter_linear_mipmap
#else
#define FABLE_TERRAIN_LOD_FILTER filter_linear_mipmap_anisotropic
#endif

// THE LIGHT() GATE, `FABLE_LIGHT_LIVE(c)` in both bodies' light(). A black light adds nothing: the
// stable-light-count fillers and Godot's unfilled first slot of each light type, which every instance
// evaluates. FABLE_LITE_SKIP_BLACK_LIGHTS skips their attenuation lookups (light() may not return early,
// so it is a condition on the gate).
#ifdef FABLE_LITE_SKIP_BLACK_LIGHTS
#define FABLE_LIGHT_LIVE(c) any(greaterThan(LIGHT_COLOR, vec3(0.0))) && c
#else
#define FABLE_LIGHT_LIVE(c) c
#endif
