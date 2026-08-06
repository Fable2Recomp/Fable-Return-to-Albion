set(REFII_RENDERER_ROOT ${CMAKE_CURRENT_LIST_DIR})

set(REFII_RENDERER_INCLUDE_DIRS
    ${REFII_RENDERER_ROOT}/include
)

set(REFII_RENDERER_RUNTIME_SOURCES
    ${REFII_RENDERER_ROOT}/src/imgui/plume_imgui_overlay.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/cvars.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/gtl/refii_gtl.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/renderer_hook_policy.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/bik/bink_guest_memory.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/bik/bink_video_plane_setup.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/bik/bink_video_uploads.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/draw/plume_draw_state.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/fable_guest_display.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/direct/fable_direct_command_batch.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/direct/fable_direct_cache_keys.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/direct/fable_direct_command_classification.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/direct/fable_direct_command_readback.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/direct/fable_direct_framebuffer_binding.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/direct/fable_direct_index_data.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/direct/fable_direct_inline_data.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/direct/fable_direct_primitive8.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/direct/fable_direct_render_state.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/direct/fable_direct_scratch_wrapper_state.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/direct/fable_direct_present_state.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/private_rect/fable_direct_private_rect_symbols.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/hooks/direct/fable_observer_callback_factories.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/private_rect/fable_private_rect_copy_actions.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/private_rect/fable_private_rect_geometry.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/private_rect/fable_private_rect_inline_clear.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/private_rect/fable_private_rect_surface_alias.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/private_rect/fable_surface_texture_link_classification.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/resource/fable_direct_resource_readiness.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/resource/fable_texture_fetch.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/resource/fable_upload_data.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/private_rect/fable_direct_synthetic_private_rect.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/packets/fable_frame_actions.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/packets/fable_frame_database_command_packets.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/packets/fable_texture_slot_masks.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/title/fable_title_classification.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/title/fable_title_constants.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/title/fable_title_draw_classification.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/title/fable_title_frame_packet_classification.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/title/fable_title_resource_facts.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/title/fable_title_render_list_observer.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/title/fable_title_root_texture_header.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/title/fable_title_scene_observer.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/title/fable_title_scene_population.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/title/fable_title_scene_table.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/title/fable_title_texture_refresh.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/title/fable_title_world_registry.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/video/fable_bink_plane_readiness.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/video/fable_bink_vertex_transform.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/video/fable_bink_video_uploads.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/video/fable_pix_video_draw_event.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/video/fable_pix_video_geometry.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/video/fable_pix_video_packets.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/video/fable_pix_render_video_frame_observer.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/video/fable_pix_video_readiness.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/video/fable_pix_video_state.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/video/fable_pix_video_uploads.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/video/fable_video_classification.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/video/fable_video_constants.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/packets/fable_refii_packet_analysis.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/packets/fable_refii_packet_builders.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/packets/fable_refii_replay_builders.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/debug/fable_renderdoc_capture.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/debug/fable_renderdoc_draw_targets.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/debug/fable_resource_debug_snapshot.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/packets/fable_render_graph_snapshot.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/direct/fable_direct_renderer.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/direct/fable_direct_state.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/fable/resource/fable_guest_resource_registry.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/general/plume_api_bridge.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/general/plume_backend.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/general/plume_diagnostics.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/general/plume_fable_addressing.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/general/plume_graphics_system.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/general/plume_renderdoc_capture.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/hooks/general/fable_hook_stack.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/hooks/general/fable_scene_boundary_hooks.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/hooks/resource/fable_texture_tile_probe.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/hooks/root/fable_hook_symbols.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/hooks/video/fable_pix_video_decode_observer.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/hooks/video/fable_video_observer_state.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_resource_lifetime.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_resource_state.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_resource_types.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_surface_shape.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_texture_formats.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_texture_lookup.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_texture_metadata.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_texture_upload.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_texture_views.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_vertex_declaration.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/renderdoc_debugger.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/replacement_health.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/hook_observer_removed.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/renderer_runtime.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/shader_library.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/d3d12/plume_d3d12_bridge.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/metal/plume_metal_bridge.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/vulkan/plume_vulkan_bridge.cpp
)

set(REFII_RENDERER_PPC_HOOK_SOURCES
    ${REFII_RENDERER_ROOT}/src/hooks.cpp
    ${REFII_RENDERER_ROOT}/src/hooks/d3d_pass_through.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/bik/bink_audio_cursor_bridge.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/bik/bink_guest_layout.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/bik/bink_movie_clock.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/bik/bink_startup_audio_drain.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/bik/bink_startup_audio_pump.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/bik/bink_startup_ownership.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/bik/bink_startup_trace.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/bik/bink_state_token_bridge.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/bik/bink_video_hooks.cpp
)

set(REFII_RENDERER_HOOK_PARTS
    ${REFII_RENDERER_ROOT}/include/renderer_hook_targets.h
    ${REFII_RENDERER_ROOT}/include/renderer_hooks_private.h
    ${REFII_RENDERER_ROOT}/include/renderer_runtime.h
    ${REFII_RENDERER_ROOT}/include/renderdoc_debugger.h
    ${REFII_RENDERER_ROOT}/include/runtime/cvars.h
    ${REFII_RENDERER_ROOT}/include/runtime/hook_observer.h
    ${REFII_RENDERER_ROOT}/include/runtime/renderer_hook_policy.h
    ${REFII_RENDERER_ROOT}/include/runtime/replacement_health.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/draw/plume_draw_state.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/direct/fable_direct_command_batch.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/direct/fable_direct_cache_keys.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/direct/fable_direct_command_classification.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/direct/fable_direct_command_readback.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/direct/fable_direct_framebuffer_binding.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/direct/fable_direct_index_data.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/direct/fable_direct_inline_data.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/direct/fable_direct_primitive8.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/direct/fable_direct_render_state.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/direct/fable_direct_scratch_wrapper_state.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/direct/fable_direct_present_state.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/private_rect/fable_direct_private_rect_symbols.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_copy_actions.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_geometry.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_inline_clear.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_surface_alias.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/private_rect/fable_surface_texture_link_classification.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/resource/fable_direct_resource_readiness.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/resource/fable_upload_data.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/private_rect/fable_direct_synthetic_private_rect.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/packets/fable_frame_actions.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/packets/fable_render_types.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/packets/fable_texture_slot_masks.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/title/fable_title_classification.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/title/fable_title_constants.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/title/fable_title_draw_classification.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/title/fable_title_frame_packet_classification.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/title/fable_title_resource_facts.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/title/fable_title_render_list_observer.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/title/fable_title_root_texture_header.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/title/fable_title_scene_observer.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/title/fable_title_scene_population.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/title/fable_title_scene_table.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/title/fable_title_texture_refresh.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/title/fable_title_world_registry.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/video/fable_bink_plane_readiness.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/video/fable_bink_vertex_transform.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/video/fable_bink_video_uploads.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/video/fable_pix_video_draw_event.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/video/fable_pix_video_geometry.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/video/fable_pix_video_packets.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/video/fable_pix_render_video_frame_observer.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/video/fable_pix_video_readiness.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/video/fable_pix_video_state.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/video/fable_pix_video_uploads.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/video/fable_video_classification.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/video/fable_video_constants.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/packets/fable_frame_database_command_packets.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/packets/fable_refii_packet_analysis.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/packets/fable_refii_packet_builders.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/packets/fable_refii_replay_builders.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/debug/fable_renderdoc_capture.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/debug/fable_renderdoc_draw_targets.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/debug/fable_resource_debug_snapshot.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/packets/fable_render_graph_snapshot.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/direct/fable_direct_state.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/fable/resource/fable_resource_snapshots.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/hooks/direct/fable_observer_callback_factories.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/hooks/resource/fable_observer_resources.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/hooks/resource/fable_texture_tile_probe.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/direct/fable_synthetic_draws.inl
    ${REFII_RENDERER_ROOT}/include/runtime/plume/general/plume_api_bridge.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/general/plume_api_bridge_private.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/general/plume_backend.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/general/plume_diagnostics.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/general/plume_graphics_system.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/general/plume_imgui_overlay.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/general/plume_fable_addressing.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/plume/general/plume_renderdoc_capture.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/hooks/general/fable_hook_stack.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/hooks/general/fable_scene_boundary_hooks.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/hooks/root/fable_hook_symbols.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/hooks/video/fable_pix_video_decode_observer.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/hooks/video/fable_video_observer_state.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/resource/plume_resource_manager_internal.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/resource/plume_vertex_declaration.h
    ${REFII_RENDERER_ROOT}/include/runtime/shader_library.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/bik/bink_audio_cursor_bridge.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/bik/bink_guest_layout.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/bik/bink_guest_memory.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/bik/bink_movie_clock.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/bik/bink_startup_audio_drain.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/bik/bink_startup_audio_pump.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/bik/bink_startup_ownership.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/bik/bink_startup_trace.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/bik/bink_state_token_bridge.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/bik/bink_video_plane_setup.h
    ${REFII_RENDERER_ROOT}/include/runtime/refii_renderer/bik/bink_video_uploads.h
    ${REFII_RENDERER_ROOT}/include/hooks/ppc_externs.h
)
set_source_files_properties(${REFII_RENDERER_HOOK_PARTS}
    PROPERTIES HEADER_FILE_ONLY TRUE
)

set(REFII_RENDERER_ENABLE_PLUME_SDL_VULKAN OFF)
if(UNIX AND NOT APPLE AND REFII_SDL3_WINDOW_ENABLED)
    set(REFII_RENDERER_ENABLE_PLUME_SDL_VULKAN ON)
endif()
set(PLUME_SDL_VULKAN_ENABLED
    "${REFII_RENDERER_ENABLE_PLUME_SDL_VULKAN}"
    CACHE BOOL
    "Enable Plume's internal SDL Vulkan integration through the Refii SDL3 window"
    FORCE
)
set(REFII_RENDERER_ENABLE_PLUME_D3D12_AGILITY_SDK OFF)
if(WIN32 AND REFII_RENDERER_PLUME_D3D12_AGILITY_SDK_ENABLED)
    set(REFII_RENDERER_ENABLE_PLUME_D3D12_AGILITY_SDK ON)
endif()
set(PLUME_D3D12_AGILITY_SDK_ENABLED
    "${REFII_RENDERER_ENABLE_PLUME_D3D12_AGILITY_SDK}"
    CACHE BOOL
    "Enable D3D12 Agility SDK"
    FORCE
)

set(REFII_RENDERER_PLUME_DEFAULT_ROOT "")
if(EXISTS "${CMAKE_SOURCE_DIR}/thirdparty/plume/CMakeLists.txt")
    set(REFII_RENDERER_PLUME_DEFAULT_ROOT "${CMAKE_SOURCE_DIR}/thirdparty/plume")
elseif(EXISTS "${CMAKE_SOURCE_DIR}/src/renderer/thirdparty/plume/CMakeLists.txt")
    set(REFII_RENDERER_PLUME_DEFAULT_ROOT "${CMAKE_SOURCE_DIR}/src/renderer/thirdparty/plume")
elseif(EXISTS "/home/loreaxe/SonicUnleshed/UnleashedRecomp/thirdparty/plume/CMakeLists.txt")
    set(REFII_RENDERER_PLUME_DEFAULT_ROOT "/home/loreaxe/SonicUnleshed/UnleashedRecomp/thirdparty/plume")
endif()

set(REFII_RENDERER_PLUME_ROOT
    "${REFII_RENDERER_PLUME_DEFAULT_ROOT}"
    CACHE PATH
    "Path to a Plume checkout with CMakeLists.txt for the renderer RHI"
)
if(NOT REFII_RENDERER_PLUME_ROOT AND REFII_RENDERER_PLUME_DEFAULT_ROOT)
    set(REFII_RENDERER_PLUME_ROOT
        "${REFII_RENDERER_PLUME_DEFAULT_ROOT}"
        CACHE PATH
        "Path to a Plume checkout with CMakeLists.txt for the renderer RHI"
        FORCE
    )
endif()
unset(REFII_RENDERER_PLUME_DEFAULT_ROOT)

if(NOT TARGET plume AND REFII_RENDERER_PLUME_ROOT)
    if(EXISTS "${REFII_RENDERER_PLUME_ROOT}/CMakeLists.txt")
        add_subdirectory(
            "${REFII_RENDERER_PLUME_ROOT}"
            "${CMAKE_CURRENT_BINARY_DIR}/thirdparty/plume"
            EXCLUDE_FROM_ALL
        )
    else()
        message(WARNING
            "REFII_RENDERER_PLUME_ROOT is set to ${REFII_RENDERER_PLUME_ROOT}, "
            "but no CMakeLists.txt was found there. Renderer RHI code will "
            "build without linking Plume until the submodule is populated."
        )
    endif()
endif()

set(REFII_RENDERER_RENDERDOC_ROOT
    "${REFII_RENDERER_ROOT}/thirdparty/renderdoc"
    CACHE PATH
    "Path to a RenderDoc checkout for the renderer graphics debugger API header"
)
set(REFII_RENDERER_RENDERDOC_API_HEADER
    "${REFII_RENDERER_RENDERDOC_ROOT}/renderdoc/api/app/renderdoc_app.h"
)
option(REFII_RENDERER_BUILD_RENDERDOC_RUNTIME
    "Build the vendored RenderDoc capture runtime for the Refii renderer"
    OFF
)
set(REFII_RENDERER_RENDERDOC_BUILD_TYPE
    "RelWithDebInfo"
    CACHE STRING
    "Build type used for the standalone vendored RenderDoc capture runtime"
)
set(REFII_RENDERER_RENDERDOC_BUILD_DIR
    "${CMAKE_BINARY_DIR}/thirdparty/renderdoc-runtime"
    CACHE PATH
    "Build directory for the vendored RenderDoc capture runtime"
)
set(REFII_RENDERER_RENDERDOC_RUNTIME_DIR
    "${REFII_LIBRARY_OUTPUT_DIR}"
    CACHE STRING
    "Directory where the vendored RenderDoc runtime is copied for Refii"
    FORCE
)

# Prebuilt RenderDoc runtime staging: release layouts carry every runtime
# library in lib/, and the in-app debugger probes <exe>/../lib for
# librenderdoc, so a prebuilt capture runtime staged there works with no
# per-machine configuration. Point this at an official RenderDoc build's
# librenderdoc (Linux .so / Windows .dll).
set(REFII_RENDERER_RENDERDOC_PREBUILT
    ""
    CACHE FILEPATH
    "Optional prebuilt RenderDoc capture runtime staged into the lib output directory"
)
if(REFII_RENDERER_RENDERDOC_PREBUILT)
    if(EXISTS "${REFII_RENDERER_RENDERDOC_PREBUILT}")
        file(MAKE_DIRECTORY "${REFII_RENDERER_RENDERDOC_RUNTIME_DIR}")
        file(COPY "${REFII_RENDERER_RENDERDOC_PREBUILT}"
             DESTINATION "${REFII_RENDERER_RENDERDOC_RUNTIME_DIR}")
        message(STATUS
            "Refii renderer: staged prebuilt RenderDoc runtime "
            "${REFII_RENDERER_RENDERDOC_PREBUILT} -> "
            "${REFII_RENDERER_RENDERDOC_RUNTIME_DIR}")
    else()
        message(WARNING
            "REFII_RENDERER_RENDERDOC_PREBUILT is set but does not exist: "
            "${REFII_RENDERER_RENDERDOC_PREBUILT}")
    endif()
endif()

if(EXISTS "${REFII_RENDERER_RENDERDOC_API_HEADER}" AND
   REFII_RENDERER_BUILD_RENDERDOC_RUNTIME)
    include(ExternalProject)

    if(WIN32)
        set(REFII_RENDERER_RENDERDOC_WIN32_CONFIGURATION
            "Development"
            CACHE STRING
            "Visual Studio configuration used for the vendored RenderDoc runtime"
        )
        set(REFII_RENDERER_RENDERDOC_WIN32_PLATFORM
            "x64"
            CACHE STRING
            "Visual Studio platform used for the vendored RenderDoc runtime"
        )
        find_program(REFII_RENDERER_MSBUILD_EXECUTABLE
            NAMES MSBuild.exe MSBuild
        )
        if(REFII_RENDERER_MSBUILD_EXECUTABLE)
            set(REFII_RENDERER_RENDERDOC_RUNTIME_BUILD_FILE
                "${REFII_RENDERER_RENDERDOC_ROOT}/${REFII_RENDERER_RENDERDOC_WIN32_PLATFORM}/${REFII_RENDERER_RENDERDOC_WIN32_CONFIGURATION}/renderdoc.dll"
            )
            set(REFII_RENDERER_RENDERDOC_RUNTIME_FILE
                "${REFII_RENDERER_RENDERDOC_RUNTIME_DIR}/renderdoc.dll"
            )
            ExternalProject_Add(refii_renderdoc_runtime_project
                SOURCE_DIR "${REFII_RENDERER_RENDERDOC_ROOT}"
                CONFIGURE_COMMAND ""
                BUILD_COMMAND
                    "${REFII_RENDERER_MSBUILD_EXECUTABLE}"
                    "${REFII_RENDERER_RENDERDOC_ROOT}/renderdoc.sln"
                    /m
                    /t:renderdoc
                    /p:Configuration=${REFII_RENDERER_RENDERDOC_WIN32_CONFIGURATION}
                    /p:Platform=${REFII_RENDERER_RENDERDOC_WIN32_PLATFORM}
                INSTALL_COMMAND ""
                BUILD_IN_SOURCE TRUE
                BUILD_BYPRODUCTS "${REFII_RENDERER_RENDERDOC_RUNTIME_BUILD_FILE}"
            )
        else()
            message(WARNING
                "MSBuild was not found; the vendored RenderDoc runtime DLL will "
                "not be built. Set renderer_renderdoc_runtime_path to an "
                "installed renderdoc.dll if capture integration is needed."
            )
        endif()
    elseif(UNIX AND NOT APPLE)
        find_package(PkgConfig QUIET)
        if(PkgConfig_FOUND)
            pkg_check_modules(REFII_XCB_KEYSYMS xcb-keysyms)
        endif()
        if(REFII_XCB_KEYSYMS_FOUND)
            set(REFII_RENDERER_RENDERDOC_XCB ON)
        else()
            set(REFII_RENDERER_RENDERDOC_XCB OFF)
            message(WARNING
                "xcb-keysyms not found (libxcb-keysyms1-dev): the vendored "
                "RenderDoc runtime builds WITHOUT VK_KHR_xcb_surface support, "
                "so native/xenia captures will fail instance creation when "
                "armed. Install the package, or set "
                "renderer_renderdoc_runtime_path to an official RenderDoc "
                "librenderdoc.so for native captures."
            )
        endif()
        set(REFII_RENDERER_RENDERDOC_CMAKE_ARGS
            -DENABLE_QRENDERDOC=OFF
            -DENABLE_PYRENDERDOC=OFF
            -DENABLE_RENDERDOCCMD=OFF
            -DENABLE_GL=OFF
            -DENABLE_GLES=OFF
            -DENABLE_EGL=OFF
            -DENABLE_VULKAN=ON
            -DENABLE_XLIB=ON
            # XCB (2026-07-19): the netplay SDK's windowing presents via
            # VK_KHR_xcb_surface; a layer built without XCB rejects that
            # extension at vkCreateInstance (armed xenia runs died
            # ErrorExtensionNotPresent, loader policy LLP_LAYER_17).
            # Conditional: renderdoc's XCB build needs xcb-keysyms
            # (libxcb-keysyms1-dev on Mint) — without it the runtime builds
            # xlib-only and native/xenia captures need
            # renderer_renderdoc_runtime_path pointed at an official build.
            -DENABLE_XCB=${REFII_RENDERER_RENDERDOC_XCB}
            -DBUILD_VERSION_DIST_NAME=Refii
            -DBUILD_VERSION_DIST_VER=dev
            -DBUILD_VERSION_DIST_CONTACT=https://github.com
            -DCMAKE_BUILD_TYPE=${REFII_RENDERER_RENDERDOC_BUILD_TYPE}
        )
        set(REFII_RENDERER_RENDERDOC_RUNTIME_BUILD_FILE
            "${REFII_RENDERER_RENDERDOC_BUILD_DIR}/lib/librenderdoc.so"
        )
        set(REFII_RENDERER_RENDERDOC_RUNTIME_FILE
            "${REFII_RENDERER_RENDERDOC_RUNTIME_DIR}/librenderdoc.so"
        )
        ExternalProject_Add(refii_renderdoc_runtime_project
            SOURCE_DIR "${REFII_RENDERER_RENDERDOC_ROOT}"
            BINARY_DIR "${REFII_RENDERER_RENDERDOC_BUILD_DIR}"
            CMAKE_ARGS ${REFII_RENDERER_RENDERDOC_CMAKE_ARGS}
            BUILD_COMMAND
                ${CMAKE_COMMAND} --build <BINARY_DIR> --target renderdoc --config $<CONFIG>
            INSTALL_COMMAND ""
            BUILD_BYPRODUCTS "${REFII_RENDERER_RENDERDOC_RUNTIME_BUILD_FILE}"
        )
    endif()

    if(TARGET refii_renderdoc_runtime_project AND
       REFII_RENDERER_RENDERDOC_RUNTIME_BUILD_FILE)
        add_custom_command(
            OUTPUT "${REFII_RENDERER_RENDERDOC_RUNTIME_FILE}"
            COMMAND ${CMAKE_COMMAND} -E make_directory
                    "${REFII_RENDERER_RENDERDOC_RUNTIME_DIR}"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                    "${REFII_RENDERER_RENDERDOC_RUNTIME_BUILD_FILE}"
                    "${REFII_RENDERER_RENDERDOC_RUNTIME_FILE}"
            DEPENDS refii_renderdoc_runtime_project
            VERBATIM
        )
        add_custom_target(refii_renderdoc_runtime
            DEPENDS "${REFII_RENDERER_RENDERDOC_RUNTIME_FILE}"
        )
    endif()
endif()

set(REFII_RENDERER_DXC_DEFAULT "")
set(REFII_RENDERER_DXC_LIB_DEFAULT "")
if(WIN32 AND EXISTS "${CMAKE_SOURCE_DIR}/src/shader/thirdparty/XenosRecomp/thirdparty/dxc-bin/bin/x64/dxc.exe")
    set(REFII_RENDERER_DXC_DEFAULT
        "${CMAKE_SOURCE_DIR}/src/shader/thirdparty/XenosRecomp/thirdparty/dxc-bin/bin/x64/dxc.exe"
    )
elseif(UNIX AND NOT APPLE AND EXISTS "${CMAKE_SOURCE_DIR}/src/shader/thirdparty/XenosRecomp/thirdparty/dxc-bin/bin/x64/dxc-linux")
    set(REFII_RENDERER_DXC_DEFAULT
        "${CMAKE_SOURCE_DIR}/src/shader/thirdparty/XenosRecomp/thirdparty/dxc-bin/bin/x64/dxc-linux"
    )
    set(REFII_RENDERER_DXC_LIB_DEFAULT
        "${CMAKE_SOURCE_DIR}/src/shader/thirdparty/XenosRecomp/thirdparty/dxc-bin/lib/x64"
    )
endif()
if(WIN32 AND REFII_RENDERER_DXC_EXECUTABLE MATCHES "dxc-linux$")
    unset(REFII_RENDERER_DXC_EXECUTABLE CACHE)
endif()
set(REFII_RENDERER_DXC_EXECUTABLE
    "${REFII_RENDERER_DXC_DEFAULT}"
    CACHE FILEPATH
    "DXC executable used to compile renderer-owned Plume shaders"
)
set(REFII_RENDERER_DXC_LIB_DIR
    "${REFII_RENDERER_DXC_LIB_DEFAULT}"
    CACHE PATH
    "Directory containing DXC runtime libraries for renderer-owned Plume shaders"
)
unset(REFII_RENDERER_DXC_DEFAULT)
unset(REFII_RENDERER_DXC_LIB_DEFAULT)

set(REFII_RENDERER_LIBRARY_TYPE SHARED)
if(WIN32)
    set(REFII_RENDERER_LIBRARY_TYPE STATIC)
endif()

add_library(refii_renderer ${REFII_RENDERER_LIBRARY_TYPE}
    ${REFII_RENDERER_RUNTIME_SOURCES}
)
target_include_directories(refii_renderer
    PUBLIC
        ${REFII_RENDERER_INCLUDE_DIRS}
)
set(REFII_RENDERER_IMGUI_INCLUDE_DIRS "")
set(REFII_RENDERER_HAS_IMGUI 0)
if(TARGET imgui::imgui)
    get_target_property(
        REFII_RENDERER_IMGUI_INCLUDE_DIRS
        imgui::imgui
        INTERFACE_INCLUDE_DIRECTORIES
    )
elseif(REXSDK_DIR AND EXISTS "${REXSDK_DIR}/thirdparty/imgui/imgui.h")
    set(REFII_RENDERER_IMGUI_INCLUDE_DIRS "${REXSDK_DIR}/thirdparty/imgui")
endif()
if(REFII_RENDERER_IMGUI_INCLUDE_DIRS)
    set(REFII_RENDERER_HAS_IMGUI 1)
    target_include_directories(refii_renderer
        PRIVATE
            ${REFII_RENDERER_IMGUI_INCLUDE_DIRS}
    )
endif()
target_link_libraries(refii_renderer
    PUBLIC
        rex::runtime
)
# The netplay SDK splits the Xenos GPU emulation (GraphicsSystem /
# CommandProcessor) out of rexruntime into a separate shared library; the
# stable tree still compiles it into rexruntime as an OBJECT library.
if(TARGET rexgpu-xenos)
    target_link_libraries(refii_renderer
        PUBLIC
            rex::gpu-xenos
    )
endif()
target_compile_definitions(refii_renderer
    PUBLIC
        REFII_RENDERER_HAS_IMGUI=${REFII_RENDERER_HAS_IMGUI}
        REFII_SDL3_WINDOW_ENABLED=$<BOOL:${REFII_SDL3_WINDOW_ENABLED}>
)
if(EXISTS "${REFII_RENDERER_RENDERDOC_API_HEADER}")
    target_include_directories(refii_renderer
        PRIVATE
            "${REFII_RENDERER_RENDERDOC_ROOT}/renderdoc/api/app"
    )
    target_compile_definitions(refii_renderer
        PUBLIC
            REFII_RENDERER_HAS_RENDERDOC=1
        PRIVATE
            REFII_RENDERER_RENDERDOC_ROOT="${REFII_RENDERER_RENDERDOC_ROOT}"
            REFII_RENDERER_RENDERDOC_RUNTIME_DIR="${REFII_RENDERER_RENDERDOC_RUNTIME_DIR}"
    )
    if(TARGET refii_renderdoc_runtime)
        add_dependencies(refii_renderer refii_renderdoc_runtime)
    endif()
else()
    target_compile_definitions(refii_renderer
        PUBLIC
            REFII_RENDERER_HAS_RENDERDOC=0
    )
endif()
if(CMAKE_DL_LIBS)
    target_link_libraries(refii_renderer
        PRIVATE
            ${CMAKE_DL_LIBS}
    )
endif()
if(TARGET plume)
    set_target_properties(plume PROPERTIES
        POSITION_INDEPENDENT_CODE ON
    )
    target_link_libraries(refii_renderer
        PUBLIC
            plume
    )
    target_compile_definitions(refii_renderer
        PUBLIC
            REFII_RENDERER_HAS_PLUME=1
    )
    if(APPLE)
        target_link_libraries(refii_renderer
            PRIVATE
                "-framework Metal"
                "-framework QuartzCore"
                "-framework CoreGraphics"
                "-framework Foundation"
                "-framework IOKit"
        )
    endif()
    if(REFII_RENDERER_DXC_EXECUTABLE)
        set(PLUME_DXC_EXECUTABLE "${REFII_RENDERER_DXC_EXECUTABLE}" CACHE INTERNAL "")
    endif()
    if(REFII_RENDERER_DXC_LIB_DIR)
        set(PLUME_DXC_LIB_DIR "${REFII_RENDERER_DXC_LIB_DIR}" CACHE INTERNAL "")
    endif()
    include("${REFII_RENDERER_PLUME_ROOT}/examples/cmake/PlumeShaders.cmake")
    plume_shaders_init()
    set(REFII_RENDERER_SHADER_OUTPUT_DIR
        "${CMAKE_CURRENT_BINARY_DIR}/generated/plume_shaders"
    )
    plume_compile_vertex_shader(
        refii_renderer
        "${REFII_RENDERER_ROOT}/src/imgui/shaders/plume_imgui_vs.hlsl"
        plume_imgui_vs
        VSMain
        OUTPUT_DIR "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
    plume_compile_pixel_shader(
        refii_renderer
        "${REFII_RENDERER_ROOT}/src/imgui/shaders/plume_imgui_ps.hlsl"
        plume_imgui_ps
        PSMain
        OUTPUT_DIR "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
    plume_compile_vertex_shader(
        refii_renderer
        "${REFII_RENDERER_ROOT}/src/runtime/shaders/plume_debug_triangle_vs.hlsl"
        plume_debug_triangle_vs
        VSMain
        OUTPUT_DIR "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
    plume_compile_pixel_shader(
        refii_renderer
        "${REFII_RENDERER_ROOT}/src/runtime/shaders/plume_debug_triangle_ps.hlsl"
        plume_debug_triangle_ps
        PSMain
        OUTPUT_DIR "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
    plume_compile_vertex_shader(
        refii_renderer
        "${REFII_RENDERER_ROOT}/src/runtime/shaders/plume_present_blit_vs.hlsl"
        plume_present_blit_vs
        VSMain
        OUTPUT_DIR "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
    plume_compile_pixel_shader(
        refii_renderer
        "${REFII_RENDERER_ROOT}/src/runtime/shaders/plume_present_blit_ps.hlsl"
        plume_present_blit_ps
        PSMain
        OUTPUT_DIR "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
    plume_compile_geometry_shader(
        refii_renderer
        "${REFII_RENDERER_ROOT}/src/runtime/shaders/plume_rectangle_list_gs.hlsl"
        plume_rectangle_list_gs
        GSMain
        OUTPUT_DIR "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
    target_include_directories(refii_renderer
        PRIVATE
            "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
else()
    target_compile_definitions(refii_renderer
        PUBLIC
            REFII_RENDERER_HAS_PLUME=0
    )
endif()

if(UNIX AND NOT APPLE)
    find_package(X11 QUIET)
    if(X11_FOUND)
        target_link_libraries(refii_renderer
            PRIVATE
                X11::X11
        )
        target_compile_definitions(refii_renderer
            PRIVATE
                REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE=1
        )
    else()
        target_compile_definitions(refii_renderer
            PRIVATE
                REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE=0
        )
    endif()
else()
    target_compile_definitions(refii_renderer
        PRIVATE
            REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE=0
    )
endif()
set_target_properties(refii_renderer PROPERTIES
    POSITION_INDEPENDENT_CODE ON
    OUTPUT_NAME refii_renderer
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)

if(WIN32 AND NOT REFII_RENDERER_LIBRARY_TYPE STREQUAL "STATIC")
    set_target_properties(refii_renderer PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

set(REFII_RENDERER_HOOK_SOURCES
    ${REFII_RENDERER_PPC_HOOK_SOURCES}
    PARENT_SCOPE
)

set(REFII_RENDERER_INCLUDE_DIRS
    ${REFII_RENDERER_INCLUDE_DIRS}
    PARENT_SCOPE
)

set(REFII_RENDERER_LIBRARIES
    refii_renderer
    PARENT_SCOPE
)
