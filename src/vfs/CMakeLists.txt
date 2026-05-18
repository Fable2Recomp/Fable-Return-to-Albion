# Native VFS runtime for loose asset overrides.
#
# `refii_vfs` owns cvars and host-side path resolution. Strong PPC overrides
# are exposed as source files and compiled by the unified `src/hooks` object
# target.

set(REFII_VFS_ROOT ${CMAKE_CURRENT_LIST_DIR})

set(REFII_VFS_INCLUDE_DIRS
    ${REFII_VFS_ROOT}/include
)

set(REFII_VFS_RUNTIME_SOURCES
    ${REFII_VFS_ROOT}/src/loose_asset_runtime.cpp
    ${REFII_VFS_ROOT}/src/runtime/cvars.cpp
    ${REFII_VFS_ROOT}/src/runtime/path_resolution.cpp
    ${REFII_VFS_ROOT}/src/runtime/replacement_paths.cpp
)

set(REFII_VFS_HOOK_INTERNAL_SOURCES
    ${REFII_VFS_ROOT}/src/core/internal_state_guest.cpp
    ${REFII_VFS_ROOT}/src/streams/host_stream_records.cpp
    ${REFII_VFS_ROOT}/src/streams/host_disk_stream_objects.cpp
    ${REFII_VFS_ROOT}/src/streams/host_async_io_queue.cpp
    ${REFII_VFS_ROOT}/src/resources/resource_wrappers.cpp
    ${REFII_VFS_ROOT}/src/resources/texture_trace_formatting.cpp
    ${REFII_VFS_ROOT}/src/frontend/post_video_trace_state.cpp
    ${REFII_VFS_ROOT}/src/frontend/post_video_handoff_state.cpp
    ${REFII_VFS_ROOT}/src/frontend/post_loading_requests.cpp
    ${REFII_VFS_ROOT}/src/resources/native_texture_parser_trace.cpp
    ${REFII_VFS_ROOT}/src/streams/resource_wrapper_handlers.cpp
    ${REFII_VFS_ROOT}/src/streams/resource_callbacks_and_lua_bridge.cpp
    ${REFII_VFS_ROOT}/src/index/asset_aliases_and_optional_probes.cpp
    ${REFII_VFS_ROOT}/src/banks/manifest_resolution.cpp
    ${REFII_VFS_ROOT}/src/banks/synthetic_bank_runtime.cpp
    ${REFII_VFS_ROOT}/src/banks/synthetic_vfs_hash_index.cpp
    ${REFII_VFS_ROOT}/src/index/runtime_tables.cpp
    ${REFII_VFS_ROOT}/src/index/runtime_alias_cache.cpp
    ${REFII_VFS_ROOT}/src/index/resource_hash_index.cpp
    ${REFII_VFS_ROOT}/src/index/concrete_asset_resolution.cpp
    ${REFII_VFS_ROOT}/src/index/resource_hash_resolution.cpp
    ${REFII_VFS_ROOT}/src/index/ownership_decisions.cpp
    ${REFII_VFS_ROOT}/src/index/open_pipeline.cpp
    ${REFII_VFS_ROOT}/src/frontend/main_menu_trace.cpp
    ${REFII_VFS_ROOT}/src/frontend/loading_sequence_bridge.cpp
    ${REFII_VFS_ROOT}/src/frontend/startup_video_handoff_gates.cpp
    ${REFII_VFS_ROOT}/src/frontend/texture_consumer_trace.cpp
    ${REFII_VFS_ROOT}/src/frontend/texture_commit.cpp
    ${REFII_VFS_ROOT}/src/frontend/synthetic_descriptors.cpp
    ${REFII_VFS_ROOT}/src/frontend/collapsed_handoff.cpp
    ${REFII_VFS_ROOT}/src/frontend/handoff_signals.cpp
    ${REFII_VFS_ROOT}/src/frontend/post_gameface_primary_owner.cpp
    ${REFII_VFS_ROOT}/src/frontend/post_gameface_package_prime.cpp
    ${REFII_VFS_ROOT}/src/banks/hash_bank_runtime.cpp
)

set(REFII_VFS_PPC_HOOK_SOURCES
    ${REFII_VFS_ROOT}/src/hooks/texture_parser.cpp
    ${REFII_VFS_ROOT}/src/hooks/boot_lifecycle.cpp
    ${REFII_VFS_ROOT}/src/hooks/frontend_video_handoff.cpp
    ${REFII_VFS_ROOT}/src/hooks/native_misc.cpp
    ${REFII_VFS_ROOT}/src/hooks/resource_manager_open.cpp
    ${REFII_VFS_ROOT}/src/hooks/loading_resource_hashes.cpp
    ${REFII_VFS_ROOT}/src/hooks/resource_manager_streams.cpp
    ${REFII_VFS_ROOT}/src/hooks/banks.cpp
    ${REFII_VFS_ROOT}/src/hooks/request_provider.cpp
    ${REFII_VFS_ROOT}/src/hooks/streams.cpp
    ${REFII_VFS_ROOT}/src/hooks/file_api.cpp
)

set(REFII_VFS_HOOK_PARTS
    ${REFII_VFS_ROOT}/include/vfs_hooks_private.h
    ${REFII_VFS_ROOT}/include/vfs_hook_constants.h
    ${REFII_VFS_ROOT}/include/vfs_hook_types.h
    ${REFII_VFS_ROOT}/include/banks/types.h
    ${REFII_VFS_ROOT}/include/banks/hash_bank_runtime.h
    ${REFII_VFS_ROOT}/include/banks/manifest_resolution.h
    ${REFII_VFS_ROOT}/include/banks/synthetic_bank_runtime.h
    ${REFII_VFS_ROOT}/include/core/guest_state.h
    ${REFII_VFS_ROOT}/include/frontend/loading_sequence.h
    ${REFII_VFS_ROOT}/include/frontend/post_gameface.h
    ${REFII_VFS_ROOT}/include/frontend/post_loading.h
    ${REFII_VFS_ROOT}/include/frontend/post_video.h
    ${REFII_VFS_ROOT}/include/frontend/texture_handoff.h
    ${REFII_VFS_ROOT}/include/frontend/types.h
    ${REFII_VFS_ROOT}/include/hooks/file_api_types.h
    ${REFII_VFS_ROOT}/include/index/asset_aliases.h
    ${REFII_VFS_ROOT}/include/index/concrete_asset_resolution.h
    ${REFII_VFS_ROOT}/include/index/ownership.h
    ${REFII_VFS_ROOT}/include/index/open_pipeline.h
    ${REFII_VFS_ROOT}/include/index/resource_hash_index.h
    ${REFII_VFS_ROOT}/include/index/resource_hash_resolution.h
    ${REFII_VFS_ROOT}/include/index/runtime_alias_cache.h
    ${REFII_VFS_ROOT}/include/index/runtime_tables.h
    ${REFII_VFS_ROOT}/include/index/types.h
    ${REFII_VFS_ROOT}/include/loose_asset_runtime_stats.h
    ${REFII_VFS_ROOT}/include/runtime/replacement_paths.h
    ${REFII_VFS_ROOT}/include/resources/resource_wrappers.h
    ${REFII_VFS_ROOT}/include/resources/native_texture_parser_trace.h
    ${REFII_VFS_ROOT}/include/runtime/cvars.h
    ${REFII_VFS_ROOT}/include/resources/texture_trace_formatting.h
    ${REFII_VFS_ROOT}/include/resources/texture_tracing.h
    ${REFII_VFS_ROOT}/include/streams/host_streams.h
    ${REFII_VFS_ROOT}/include/streams/host_async_io_queue.h
    ${REFII_VFS_ROOT}/include/streams/host_disk_stream_objects.h
    ${REFII_VFS_ROOT}/include/streams/host_stream_records.h
    ${REFII_VFS_ROOT}/include/streams/resource_callbacks.h
    ${REFII_VFS_ROOT}/include/streams/resource_wrapper_handlers.h
    ${REFII_VFS_ROOT}/include/streams/types.h
    ${REFII_VFS_ROOT}/include/hooks/ppc_externs.h
)
set_source_files_properties(${REFII_VFS_HOOK_PARTS} PROPERTIES HEADER_FILE_ONLY TRUE)

set(REFII_VFS_HOOK_SOURCES
    ${REFII_VFS_ROOT}/src/hooks.cpp
    ${REFII_VFS_HOOK_INTERNAL_SOURCES}
    ${REFII_VFS_PPC_HOOK_SOURCES}
)

add_library(refii_vfs SHARED
    ${REFII_VFS_RUNTIME_SOURCES}
)
target_include_directories(refii_vfs
    PUBLIC
        ${REFII_VFS_INCLUDE_DIRS}
)
target_link_libraries(refii_vfs
    PUBLIC
        rex::runtime
)
set_target_properties(refii_vfs PROPERTIES
    POSITION_INDEPENDENT_CODE ON
    OUTPUT_NAME refii_vfs
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)

if(WIN32)
    set_target_properties(refii_vfs PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

set(REFII_VFS_HOOK_SOURCES
    ${REFII_VFS_HOOK_SOURCES}
    PARENT_SCOPE
)

set(REFII_VFS_INCLUDE_DIRS
    ${REFII_VFS_INCLUDE_DIRS}
    PARENT_SCOPE
)

set(REFII_VFS_LIBRARIES
    refii_vfs
    PARENT_SCOPE
)
