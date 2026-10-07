# Host bridge — a C ABI over the guest runtime for the Godot shell.
#
# SHARED and C-linkage on purpose: Godot reaches this through P/Invoke, the same
# arrangement as refii_launcher_ops and refii_bnk_extractor. It carries no game logic;
# everything it exposes is a thin call into the runtime.

set(REFII_HOSTBRIDGE_ROOT ${CMAKE_CURRENT_LIST_DIR})

add_library(refii_hostbridge SHARED
    ${REFII_HOSTBRIDGE_ROOT}/src/host_bridge.cpp
)

# refii_host carries RunRefiiEmbedded and pulls in refii_core and refii_generated —
# the whole recompiled guest. That is what "run refii inside Godot" costs, and it is why
# this library is large.
target_link_libraries(refii_hostbridge
    PRIVATE
        refii_host
        refii_vfs
        # Carries the achievement seam: its one instance lives in rexruntime, and linking the
        # seam here as well gave the bridge a second, empty copy of the state.
        rex::runtime
)

# The seam's header only — see src/achievement/CMakeLists.txt.
target_include_directories(refii_hostbridge PRIVATE
    $<TARGET_PROPERTY:refii_achievement,INTERFACE_INCLUDE_DIRECTORIES>)

# SDL headers: the stepped loop drains the event queue itself rather than handing a
# thread to the guest's blocking SDL_WaitEvent. rex::runtime carries the linkage; this
# is only for the declarations.
if(TARGET SDL3::SDL3)
    target_link_libraries(refii_hostbridge PRIVATE SDL3::SDL3)
endif()

# NO DANGLING SYMBOLS. This library is dlopen'd by .NET with lazy binding, so a reference
# the link could not resolve is not a link error — it is `ld.so` ending the whole process
# with "symbol lookup error" (exit 127) the first time that call executes, with no signal,
# no handler and nothing in any log. That was four "crashes to desktop" in one afternoon.
# Resolve everything at link time instead; a weak reference (the renderer's optional
# ImGui hook) still resolves to null and is allowed.
if(NOT WIN32)
    target_link_options(refii_hostbridge PRIVATE -Wl,--no-undefined)
endif()

# ABI version bump reminder lives in host_bridge.cpp.
set_target_properties(refii_hostbridge PROPERTIES
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    OUTPUT_NAME refii_hostbridge
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
)

# Stage into Godot/build/bin, beside refii_launcher_ops.
#
# Same convention the launcher already uses, and it is what makes the Godot app
# self-contained: without it the app only finds these because NativeLibraries walks
# out/build, which is a DEV-tree accident and does not survive an export.
#
# The whole dependency closure has to come, not just the bridge: it pulls in the
# recompiled guest (refii_generated), the runtime (rexruntime) and the host libraries.
# $<TARGET_RUNTIME_DLLS> is a no-op on ELF platforms — it only resolves DLLs — so each
# one is named explicitly.
if(REFII_STAGE_GODOT_BUILD)
    set(_refii_hostbridge_staged_targets
        refii_hostbridge
        refii_generated
        refii_core
        refii_lua
        refii_vfs
        refii_binkdecode
        refii_xmadecode
        rexruntime
        TracyClient
    )

    set(_refii_hostbridge_stage_commands
        COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_GODOT_BUILD_BIN_DIR}"
    )
    # The SDK's libraries carry a configuration postfix (d, rd) and copy_if_different only
    # ever adds, so a tree built Debug and then RelWithDebInfo staged both runtimes: the
    # host loaded one, the launcher linked the payload against the newest by mtime and
    # logged "more than one rexruntime". The other configurations' copies go first.
    foreach(_staged rexruntime TracyClient)
        if(TARGET ${_staged})
            list(APPEND _refii_hostbridge_stage_commands
                COMMAND ${CMAKE_COMMAND}
                    -DBIN=${REFII_GODOT_BUILD_BIN_DIR}
                    -DSTEM=${_staged}
                    -DKEEP=$<TARGET_FILE_NAME:${_staged}>
                    -P "${CMAKE_SOURCE_DIR}/cmake/refii_prune_stale_configs.cmake"
            )
        endif()
    endforeach()
    foreach(_staged IN LISTS _refii_hostbridge_staged_targets)
        if(TARGET ${_staged})
            list(APPEND _refii_hostbridge_stage_commands
                COMMAND ${CMAKE_COMMAND} -E copy_if_different
                    "$<TARGET_FILE:${_staged}>" "${REFII_GODOT_BUILD_BIN_DIR}"
            )
        endif()
    endforeach()

    # A staging TARGET rather than a POST_BUILD on the bridge alone.
    #
    # Hanging the whole closure off refii_hostbridge's POST_BUILD meant a rebuild that
    # touched only one of the others never restaged it: librefii_vfs.so sat in
    # Godot/build/bin twelve hours stale while the build tree was current, and every run
    # silently loaded the old code — which looked for all the world like a hook that was
    # not firing. add_custom_command(TARGET) also has to live in the directory that
    # created the target, so it cannot reach refii_vfs from here at all.
    #
    # ALL so it runs every build; copy_if_different so that costs nothing when nothing
    # moved.
    add_custom_target(refii_stage_godot ALL
        ${_refii_hostbridge_stage_commands}
        COMMENT "Staging the runtime closure into Godot/build/bin"
        COMMAND_EXPAND_LISTS
    )
    foreach(_staged IN LISTS _refii_hostbridge_staged_targets)
        if(TARGET ${_staged})
            add_dependencies(refii_stage_godot ${_staged})
        endif()
    endforeach()

    add_custom_command(TARGET refii_hostbridge POST_BUILD
        ${_refii_hostbridge_stage_commands}
        COMMENT "Staging the host bridge and the guest runtime into Godot/build/bin"
        COMMAND_EXPAND_LISTS
        VERBATIM
    )
endif()
