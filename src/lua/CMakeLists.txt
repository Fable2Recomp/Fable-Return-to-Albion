# Native Lua runtime and tooling.
#
# `refii_lua` owns host-side runtime helpers. Strong PPC overrides are exposed
# as source files and compiled by the unified `src/hooks` object target.

set(REFII_LUA_ROOT ${CMAKE_CURRENT_LIST_DIR})

set(REFII_LUA_INCLUDE_DIRS
    ${REFII_LUA_ROOT}/include
)

set(REFII_LUA51_INCLUDE_DIRS
    ${REFII_LUA_ROOT}/thirdparty/lua51/src
)

set(REFII_LUA_RUNTIME_PUBLIC_HEADERS
    ${REFII_LUA_ROOT}/include/core/native_lua_bridge.h
    ${REFII_LUA_ROOT}/include/core/native_lua_hooks.h
    ${REFII_LUA_ROOT}/include/lua_script_paths.h
)

set(REFII_LUA_RUNTIME_PRIVATE_HEADERS
    ${REFII_LUA_ROOT}/include/core/native_lua_rexglue_compat.h
)

set(REFII_LUA_RUNTIME_SOURCES
    ${REFII_LUA_ROOT}/src/core/native_lua_bridge.cpp
    ${REFII_LUA_ROOT}/src/core/native_lua_handlers.cpp
    ${REFII_LUA_ROOT}/src/core/native_lua_runtime.cpp
)

set(REFII_LUA_RUNTIME_PARTS
    ${REFII_LUA_RUNTIME_PUBLIC_HEADERS}
    ${REFII_LUA_RUNTIME_PRIVATE_HEADERS}
)
set_source_files_properties(${REFII_LUA_RUNTIME_PARTS} PROPERTIES HEADER_FILE_ONLY TRUE)

set(REFII_LUA_HOOK_SOURCES
    ${REFII_LUA_ROOT}/src/core/native_lua_hooks.cpp
)

set(REFII_LUA_HOOK_PARTS
    ${REFII_LUA_ROOT}/include/core/native_lua_hooks.h
)
set_source_files_properties(${REFII_LUA_HOOK_PARTS} PROPERTIES HEADER_FILE_ONLY TRUE)

set(REFII_LUA51_SOURCES
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lapi.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lauxlib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lbaselib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lcode.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/ldblib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/ldebug.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/ldo.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/ldump.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lfunc.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lgc.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/linit.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/liolib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/llex.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lmathlib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lmem.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/loadlib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lobject.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lopcodes.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/loslib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lparser.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lstate.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lstring.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lstrlib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/ltable.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/ltablib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/ltm.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lundump.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lvm.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lzio.c
)

set(REFII_LUA_DECOMPILER_WORKSPACE_SOURCES
    ${REFII_LUA_ROOT}/src/tools/crypto/sha256.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/branch_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/branch_loop_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/branch_preview_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/branch_structured_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/condition_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/conditional_block_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/control_flow_blocks.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/control_flow_graph.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/decompiler_file_io.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/jump_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/loop_block_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/lua51_analysis.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/lua_script_profile_policy.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/loop_condition_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/loop_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/loop_statement_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/native_first.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/native_first_readability_policy.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/output_placeholder_helpers.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/repair_progress_helpers.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/repair_state_keys.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_artifact_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_literal_seed_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_literal_seed_forward_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_localize_table_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_loop_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_loop_flow_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/exact_bytecode_parity_repair_pipeline.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/exact_instruction_microdiff.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/exact_seed_normalization.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/native_candidate_repair_pipeline.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/native_main_chunk_render_pipeline.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_placeholder_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_readability.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/reported_function_bytecode_repairs.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_receiver_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_cleanup_common_helpers.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_call_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_call_parser.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_call_fallback_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_condition_chain_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_current_state_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_expression_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_function_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_gameface_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_guard_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_nested_guard_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_nested_elseif_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_return_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_postprocess_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/shared_guard_cleanup_repairs.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/shared_cleanup_rules.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/shared_cleanup_rule_table_dispatch.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/shared_cleanup_output_apply.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/shared_cleanup_signal_helpers.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/shared_table_cleanup_repairs.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_structure_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_subclass_setlist_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_table_alias_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_table_builder_cluster_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_table_carry_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_table_literal_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_table_nested_index_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_table_outlier_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_table_placeholder_expand_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_table_qualified_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_table_setlist_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_table_temp_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_table_temp_placeholder_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_temporary_alias_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/top_level_replacement_state.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/validated_cleanup_pipeline.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/operation_build.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/operation_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/preview_fallback_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/preview_join_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/preview_operation_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/preview_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/render_state.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/bytecode_parity_repair_context.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/bytecode_parity_repair_parsing.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_condition_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_cleanup_profiles.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_else_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_local_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_shared_cleanup_orchestration.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_unhandled_if_cleanup.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/rendered_lua_text.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/statement_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/straight_line_render.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/testset_render.cpp
    ${REFII_LUA_ROOT}/src/tools/bytecode/lua_bytecode_oracles.cpp
    ${REFII_LUA_ROOT}/src/tools/bytecode/lua_bytecode_repair_analysis.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_decompile_repair_context.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_decompiled_function_spans.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_function_suffix_and_groupmember_repairs.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_local_diff_exactifier.cpp
    ${REFII_LUA_ROOT}/src/tools/native/lua_native_main_chunk_policy.cpp
    ${REFII_LUA_ROOT}/src/tools/native/lua_native_render_tool_io.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/syntax/lua_ordered_syntax_repair_passes.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_quest_repair_stage_ops.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/syntax/lua_raw_invalid_structural_normalization.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/syntax/lua_raw_invalid_structural_prepass.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_reference_exact_repairs.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_reference_function_repairs.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_rendered_function_helpers.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/syntax/lua_syntax_generator_repairs.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/syntax/lua_syntax_state_loop_repairs.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_boundary_repair.cpp
    ${REFII_LUA_ROOT}/src/tools/bytecode/lua_bytecode_parity_audit.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_owner_repair.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_quest_repair_stages.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_repair_cache.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_repeat_repair.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_repair_stages.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_repair_shared.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_tiny_main_repairs.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_top_level_repair.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_validated_output_repair.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/repair/lua_wait_repair.cpp
    ${REFII_LUA_ROOT}/src/tools/decompiler/syntax/lua_syntax_repair_pipeline.cpp
    ${REFII_LUA_ROOT}/src/tools/workspace/lua_workspace_manager.cpp
    ${REFII_LUA_ROOT}/src/tools/workspace/lua_workspace_decompile_pipeline.cpp
    ${REFII_LUA_ROOT}/src/tools/native/native_lua_chunk.cpp
    ${REFII_LUA_ROOT}/src/tools/native/straight_line_native_render.cpp
    ${REFII_LUA_ROOT}/src/tools/refii_lua_decompiler_main.cpp
)

set(REFII_LUA_DECOMPILER_PUBLIC_HEADERS
    ${REFII_LUA_ROOT}/include/tools/crypto/sha256.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/branch_loop_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/branch_preview_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/branch_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/branch_structured_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/condition_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/conditional_block_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/control_flow_blocks.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/control_flow_graph.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/decompiler_file_io.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/jump_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/loop_block_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/loop_condition_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/loop_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/loop_statement_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/lua51_analysis.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/lua_script_profile_policy.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/native_first.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/native_first_readability_policy.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/operation_build.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/operation_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/preview_fallback_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/preview_join_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/preview_operation_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/preview_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/render_state.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/bytecode_parity_repair_context.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/bytecode_parity_repair_parsing.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/bounded_normalization_pass_loop.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/bounded_reference_repair_loop.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/exact_bytecode_parity_repair_pipeline.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/exact_instruction_microdiff.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/exact_seed_normalization.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/native_candidate_repair_pipeline.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/native_main_chunk_render_pipeline.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/output_placeholder_helpers.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/repair_progress_helpers.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/repair_state_keys.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/rendered_readability.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/reported_function_bytecode_repairs.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/rendered_cleanup_common_helpers.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/rendered_call_parser.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/shared_guard_cleanup_repairs.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/shared_cleanup_rule_table_dispatch.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/shared_cleanup_rules.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/shared_cleanup_signal_helpers.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/shared_table_cleanup_repairs.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/top_level_replacement_state.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/validated_cleanup_pipeline.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/rendered/rendered_lua_text.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/statement_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/straight_line_render.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/testset_render.h
    ${REFII_LUA_ROOT}/include/tools/bytecode/lua_bytecode_oracles.h
    ${REFII_LUA_ROOT}/include/tools/bytecode/lua_bytecode_parity_audit.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/lua_decompiler_internal.h
    ${REFII_LUA_ROOT}/include/tools/workspace/lua_workspace_decompile_pipeline.h
    ${REFII_LUA_ROOT}/include/tools/native/native_lua_chunk.h
    ${REFII_LUA_ROOT}/include/tools/native/native_lua_decompiler.h
    ${REFII_LUA_ROOT}/include/tools/decompiler/repair/lua_tiny_main_repairs.h
    ${REFII_LUA_ROOT}/include/tools/native/straight_line_native_render.h
)

file(GLOB REFII_LUA_DECOMPILER_PRIVATE_PARTS CONFIGURE_DEPENDS
    ${REFII_LUA_ROOT}/src/tools/decompiler/rendered/pipelines/exact/*.h
)
set_source_files_properties(
    ${REFII_LUA_DECOMPILER_PUBLIC_HEADERS}
    ${REFII_LUA_DECOMPILER_PRIVATE_PARTS}
    PROPERTIES HEADER_FILE_ONLY TRUE
)

source_group(TREE ${REFII_LUA_ROOT} FILES
    ${REFII_LUA_RUNTIME_PARTS}
    ${REFII_LUA_RUNTIME_SOURCES}
    ${REFII_LUA_HOOK_PARTS}
    ${REFII_LUA_HOOK_SOURCES}
    ${REFII_LUA51_SOURCES}
    ${REFII_LUA_DECOMPILER_PUBLIC_HEADERS}
    ${REFII_LUA_DECOMPILER_PRIVATE_PARTS}
    ${REFII_LUA_DECOMPILER_WORKSPACE_SOURCES}
)

add_library(refii_lua SHARED
    ${REFII_LUA_RUNTIME_SOURCES}
    ${REFII_LUA_RUNTIME_PARTS}
)
target_include_directories(refii_lua
    PUBLIC
        ${REFII_LUA_INCLUDE_DIRS}
)
target_link_libraries(refii_lua
    PUBLIC
        rex::core
        rex::system
)
set_target_properties(refii_lua PROPERTIES
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    POSITION_INDEPENDENT_CODE ON
    OUTPUT_NAME refii_lua
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)

if(WIN32)
    set_target_properties(refii_lua PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

add_library(refii_lua51 STATIC
    ${REFII_LUA51_SOURCES}
)
target_include_directories(refii_lua51
    PUBLIC
        ${REFII_LUA51_INCLUDE_DIRS}
)
set_target_properties(refii_lua51 PROPERTIES
    C_STANDARD 99
    C_STANDARD_REQUIRED ON
    POSITION_INDEPENDENT_CODE ON
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
)
if(WIN32)
    target_compile_definitions(refii_lua51 PUBLIC LUA_USE_WINDOWS)
elseif(APPLE)
    target_compile_definitions(refii_lua51 PUBLIC LUA_USE_MACOSX)
else()
    target_compile_definitions(refii_lua51 PUBLIC LUA_USE_LINUX)
    target_link_libraries(refii_lua51 PUBLIC ${CMAKE_DL_LIBS} m)
endif()

if(TARGET refii_bnk_extractor)
    add_library(refii_lua_decompiler_workspace STATIC
        ${REFII_LUA_DECOMPILER_WORKSPACE_SOURCES}
        ${REFII_LUA_DECOMPILER_PUBLIC_HEADERS}
        ${REFII_LUA_DECOMPILER_PRIVATE_PARTS}
    )
    target_include_directories(refii_lua_decompiler_workspace
        PUBLIC
            ${REFII_LUA_ROOT}/include
        PRIVATE
            ${REFII_LUA51_INCLUDE_DIRS}
    )
    target_link_libraries(refii_lua_decompiler_workspace
        PUBLIC
            refii_bnk_extractor
            refii_lua51
    )
    set_target_properties(refii_lua_decompiler_workspace PROPERTIES
        CXX_STANDARD 23
        CXX_STANDARD_REQUIRED ON
        ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    )

    if(REFII_BUILD_TOOLS)
        add_executable(refii-lua-decompiler
            ${REFII_LUA_ROOT}/src/tools/refii_lua_decompiler_cli_main.cpp
        )
        target_link_libraries(refii-lua-decompiler
            PRIVATE
                refii_lua_decompiler_workspace
        )
        set_target_properties(refii-lua-decompiler PROPERTIES
            OUTPUT_NAME refii-lua-decompiler
            CXX_STANDARD 23
            CXX_STANDARD_REQUIRED ON
            RUNTIME_OUTPUT_DIRECTORY "${REFII_TOOL_OUTPUT_DIR}"
            BUILD_RPATH "$ORIGIN/../lib"
            INSTALL_RPATH "$ORIGIN/../lib"
        )
    endif()
else()
    message(STATUS
        "Skipping Lua decompiler workspace/tool because refii_bnk_extractor is not available. "
        "Enable REFII_BUILD_EXTRACTOR if you need refii-lua-decompiler."
    )
endif()

set(REFII_LUA_HOOK_SOURCES
    ${REFII_LUA_HOOK_SOURCES}
    PARENT_SCOPE
)

set(REFII_LUA_INCLUDE_DIRS
    ${REFII_LUA_INCLUDE_DIRS}
    PARENT_SCOPE
)

set(REFII_LUA_LIBRARIES
    refii_lua
    PARENT_SCOPE
)
