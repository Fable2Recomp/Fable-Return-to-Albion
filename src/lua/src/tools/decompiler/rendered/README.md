# Rendered Lua Cleanup Layout

This folder contains implementation-only include fragments for
`rendered_lua_text.cpp`, `native_first.cpp`, and the decompiler workspace
pipeline. Keep behavior changes parity-gated in the caller; these files are
organized by role, not by script or game content.

Prefer normal `.h`/`.cpp` files for new standalone helpers. `.inc` fragments are
temporary escape hatches for code that still depends on local state inside a
large caller; when a fragment gains a clean API, move it into a regular
translation unit like the rest of the ReXGlue/VFS-style modules.

Current shared modules:
- `include/tools/decompiler/rendered/bytecode_parity_repair_context.h` owns
  exact-parity repair context, bytecode-exact checks, and parity-gated commit
  helpers.
- `include/tools/decompiler/rendered/bytecode_parity_repair_parsing.h` owns
  shared parsing/string helpers used by exact bytecode repair families.
- `include/tools/decompiler/rendered/output_placeholder_helpers.h` owns reusable
  placeholder/readability artifact checks.
- `include/tools/decompiler/rendered/repair_progress_helpers.h` owns syntax
  repair progress predicates shared by validation repair lanes.
- `include/tools/decompiler/rendered/repair_state_keys.h` owns reusable syntax
  repair progress and quality state keys.
- `include/tools/decompiler/rendered/rendered_readability.h` owns temporary-noise
  readability scoring and invalid-nil normalization used by native/readability
  gates.
- `include/tools/decompiler/rendered/rendered_cleanup_common_helpers.h` owns
  common rendered text utilities shared by table/expression cleanup families.
- `src/tools/decompiler/rendered/rendered_function_cleanup.cpp` owns standalone
  function-shape cleanup rules.
- `src/tools/decompiler/rendered/rendered_gameface_cleanup.cpp` owns standalone
  Gameface placeholder cleanup rules.
- `src/tools/decompiler/rendered/rendered_literal_seed_forward_cleanup.cpp` owns
  standalone forward literal seed cleanup rules.
- `src/tools/decompiler/rendered/rendered_literal_seed_cleanup.cpp` owns
  standalone literal seed cleanup rules.
- `src/tools/decompiler/rendered/rendered_localize_table_cleanup.cpp` owns
  standalone table localization and builder target retargeting rules.
- `src/tools/decompiler/rendered/rendered_loop_cleanup.cpp` owns standalone loop
  cleanup rules.
- `src/tools/decompiler/rendered/rendered_placeholder_cleanup.cpp` owns
  standalone function placeholder cleanup helpers/rules.
- `src/tools/decompiler/rendered/rendered_postprocess_cleanup.cpp` owns final
  rendered whitespace and statement emission helpers.
- `src/tools/decompiler/rendered/rendered_structure_cleanup.cpp` owns
  standalone structural cleanup rules that no longer need caller-local state.
- `src/tools/decompiler/rendered/rendered_subclass_setlist_cleanup.cpp` owns
  standalone subclass bootstrap setlist cleanup rules.
- `src/tools/decompiler/rendered/rendered_table_alias_cleanup.cpp` owns
  standalone table/member alias and subclass binding cleanup rules.
- `src/tools/decompiler/rendered/rendered_table_builder_cluster_cleanup.cpp` owns
  standalone clustered table builder cleanup rules.
- `src/tools/decompiler/rendered/rendered_table_carry_cleanup.cpp` owns
  standalone top-level table carry cleanup rules.
- `src/tools/decompiler/rendered/rendered_table_nested_index_cleanup.cpp` owns
  standalone nested table index extraction rules.
- `src/tools/decompiler/rendered/rendered_table_outlier_cleanup.cpp` owns
  standalone outlier table alias/seed cleanup rules.
- `src/tools/decompiler/rendered/rendered_table_placeholder_expand_cleanup.cpp` owns
  standalone qualified table placeholder expansion rules.
- `src/tools/decompiler/rendered/rendered_table_qualified_cleanup.cpp` owns
  standalone qualified table literal/builder cleanup rules.
- `src/tools/decompiler/rendered/rendered_table_setlist_cleanup.cpp` owns
  standalone setlist and top-level multi-table cleanup rules.
- `src/tools/decompiler/rendered/rendered_table_temp_cleanup.cpp` owns
  standalone table temporary cleanup rules.
- `src/tools/decompiler/rendered/rendered_table_temp_placeholder_cleanup.cpp` owns
  standalone temporary-keyed and nested table placeholder cleanup rules.
- `include/tools/decompiler/rendered/rendered_call_parser.h` owns small rendered
  call parsing utilities reused by cleanup families and exact repairs.
- `src/tools/decompiler/rendered/rendered_artifact_cleanup.cpp` owns standalone
  rendered artifact cleanup rules that no longer need caller-local state.
- `include/tools/decompiler/rendered/shared_cleanup_signal_helpers.h` owns
  precheck signals and gated cleanup tails shared by rule pipelines.
- `include/tools/decompiler/rendered/shared_cleanup_rules.h` owns shared cleanup
  rule metadata and profile filtering.
- `include/tools/decompiler/rendered/shared_guard_cleanup_repairs.h` owns guard
  repair rules that no longer depend on local pipeline state.
- `include/tools/decompiler/rendered/shared_table_cleanup_repairs.h` owns table
  repair rules that no longer depend on local pipeline state.
- `include/tools/decompiler/rendered/top_level_replacement_state.h` owns
  lightweight source fingerprints used by top-level syntax replacement guards.

Layout:
- `core/` contains cleanup fragments directly orchestrated by
  `rendered_lua_text.cpp`.
- `families/control/` contains loop, else/elseif, and unhandled-if cleanups.
- `families/table/` contains table constructor, seed, setlist, carry, alias, and
  placeholder-table repairs that still need caller-local state.
- `families/expression/` contains expression and temporary-alias rewrites.
- `families/specialized/` contains Gameface, function, and placeholder cleanups.
- `families/shared/` contains helpers shared by multiple cleanup families.
- `pipelines/native/` contains native-render candidate production, scoring, and
  repair paths.
- `pipelines/syntax/` contains ordered syntax and raw invalid-source repairs.
- `pipelines/exact/` contains bytecode parity and exactification bundles.
- `pipelines/shared/` contains small helpers and cleanup pipelines reused across
  those flows.

When adding a new cleanup, prefer the narrowest existing family or pipeline.
Only add a new include fragment when it prevents a large file from growing a
second unrelated responsibility.
