# Stages the native closure into Godot/build/<arch>/bin, the one directory the Godot resolver
# finds in a dev tree and an export alike: without it the app only finds these because
# NativeLibraries walks out/build, which is a DEV-tree accident and does not survive an export.
#
# The whole dependency closure has to come, not just a bridge: it pulls in the recompiled guest
# (a title's module), the runtime (rexruntime) and the host libraries. $<TARGET_RUNTIME_DLLS> is
# a no-op on ELF platforms — it only resolves DLLs — so each one is named explicitly.
#
# EACH TITLE'S INSTANCE IN ITS OWN FOLDER: a title's host bridge, its own state libraries and its
# module go to bin/<Folder>/ (REFII_TITLE_INSTANCE_TARGETS_<Folder>, REFII_TITLE_INSTANCE_FOLDERS
# in the top-level CMakeLists), the shared natives to bin/. A copy left flat in bin/ by a build
# from before the split is removed, so nothing can load the stale one.

include_guard(GLOBAL)

# The shared natives, staged into bin/ beside each other.
set(REFII_STAGED_SHARED_TARGETS
    # The code generator, loaded by the launcher to build the player's game code. It imports
    # rexruntime from this same directory, which is the whole point of its being a library here
    # rather than an executable under bin/recompile.
    refii_recompiler
    refii_core
    # The shared runtime state every title's instance and payload link.
    refii_runtime
    refii_binkdecode
    refii_xmadecode
    rexruntime
    TracyClient
    # The shader pipeline, loaded by the launcher to compile the player's shader banks: the bank
    # extractor with its C ABI, the translator it links, and XenosRecomp under that. The
    # compiler the translator runs is staged as bin/dxc below.
    refii-shader-sbk
    refii-shader-xenos-translator
    XenosRecomp
)

# Sets `out_commands` to the staging commands for the shared natives and the instances of the
# title folders named after it, and `out_targets` to every staged target that exists. Only
# targets that already exist are staged: call it after every target it should see.
function(refii_stage_godot_commands out_commands out_targets)
    set(_folders ${ARGN})
    set(_commands
        COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_GODOT_BUILD_BIN_DIR}"
    )
    set(_targets "")
    # The SDK's libraries carry a configuration postfix (d, rd) and copy_if_different only
    # ever adds, so a tree built Debug and then RelWithDebInfo staged both runtimes: the
    # host loaded one, the launcher linked the payload against the newest by mtime and
    # logged "more than one rexruntime". The other configurations' copies go first.
    foreach(_staged rexruntime TracyClient)
        if(TARGET ${_staged})
            list(APPEND _commands
                COMMAND ${CMAKE_COMMAND}
                    -DBIN=${REFII_GODOT_BUILD_BIN_DIR}
                    -DSTEM=${_staged}
                    -DKEEP=$<TARGET_FILE_NAME:${_staged}>
                    -P "${CMAKE_SOURCE_DIR}/cmake/refii_prune_stale_configs.cmake"
            )
        endif()
    endforeach()

    foreach(_folder IN LISTS _folders)
        set(_title_bin_dir "${REFII_GODOT_BUILD_BIN_DIR}/${_folder}")
        list(APPEND _commands
            COMMAND ${CMAKE_COMMAND} -E make_directory "${_title_bin_dir}"
        )
        foreach(_staged IN LISTS REFII_TITLE_INSTANCE_TARGETS_${_folder})
            if(TARGET ${_staged})
                list(APPEND _commands
                    COMMAND ${CMAKE_COMMAND} -E rm -f
                        "${REFII_GODOT_BUILD_BIN_DIR}/$<TARGET_FILE_NAME:${_staged}>"
                    COMMAND ${CMAKE_COMMAND} -E copy_if_different
                        "$<TARGET_FILE:${_staged}>" "${_title_bin_dir}"
                )
                list(APPEND _targets ${_staged})
            endif()
        endforeach()
    endforeach()

    foreach(_staged IN LISTS REFII_STAGED_SHARED_TARGETS)
        if(TARGET ${_staged})
            list(APPEND _commands
                COMMAND ${CMAKE_COMMAND} -E copy_if_different
                    "$<TARGET_FILE:${_staged}>" "${REFII_GODOT_BUILD_BIN_DIR}"
            )
            list(APPEND _targets ${_staged})
        endif()
    endforeach()

    # The DirectX Shader Compiler the translator runs: its libraries beside XenosRecomp, which
    # links them, and the tool with the header the generated HLSL includes under bin/dxc
    # (src/shader/CMakeLists.txt names the files; the translator resolves bin/dxc before its
    # compile-time paths).
    if(REFII_SHADER_LIBRARY_FILES)
        list(APPEND _commands
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                ${REFII_SHADER_LIBRARY_FILES} "${REFII_GODOT_BUILD_BIN_DIR}"
        )
    endif()
    if(REFII_SHADER_TOOL_FILES)
        list(APPEND _commands
            COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_GODOT_BUILD_BIN_DIR}/dxc"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                ${REFII_SHADER_TOOL_FILES} "${REFII_GODOT_BUILD_BIN_DIR}/dxc"
        )
    endif()

    set(${out_commands} ${_commands} PARENT_SCOPE)
    set(${out_targets} ${_targets} PARENT_SCOPE)
endfunction()

# A title's host bridge restages, after it links, the shared natives and that title's instance:
# the closure as far as it exists when the title's directory is processed. Called from the
# directory that creates `target` (add_custom_command(TARGET) must be).
function(refii_stage_title_bridge target folder)
    if(NOT REFII_STAGE_GODOT_BUILD)
        return()
    endif()
    refii_stage_godot_commands(_commands _targets ${folder})
    add_custom_command(TARGET ${target} POST_BUILD
        ${_commands}
        COMMENT "Staging the ${folder} host bridge and the guest runtime into Godot/build/bin"
        COMMAND_EXPAND_LISTS
        VERBATIM
    )
endfunction()

# THE STAGING TARGET, for every title's instance and the shared natives. A target rather than a
# POST_BUILD on a bridge alone.
#
# Hanging the whole closure off refii_hostbridge's POST_BUILD meant a rebuild that touched only
# one of the others never restaged it: librefii_vfs.so sat in Godot/build/bin twelve hours stale
# while the build tree was current, and every run silently loaded the old code — which looked for
# all the world like a hook that was not firing. add_custom_command(TARGET) also has to live in
# the directory that created the target, so it cannot reach refii_vfs from a bridge's directory
# at all.
#
# ALL so it runs every build; copy_if_different so that costs nothing when nothing moved. Defined
# after every title's bridge: `if(TARGET ...)` only sees targets that already exist.
function(refii_define_stage_godot)
    if(NOT REFII_STAGE_GODOT_BUILD)
        return()
    endif()
    refii_stage_godot_commands(_commands _targets ${REFII_TITLE_INSTANCE_FOLDERS})
    add_custom_target(refii_stage_godot ALL
        ${_commands}
        COMMENT "Staging the runtime closure into Godot/build/bin"
        COMMAND_EXPAND_LISTS
    )
    foreach(_staged IN LISTS _targets)
        add_dependencies(refii_stage_godot ${_staged})
    endforeach()
endfunction()
