# Stages the native closure into Godot/build/<arch>/bin, the one directory the Godot resolver
# finds in a dev tree and an export alike: without it the app only finds these because
# NativeLibraries walks out/build, which is a DEV-tree accident and does not survive an export.
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

# Stages ONLY a title's own host bridge into Godot/build/<arch>/bin/<folder>/ when that bridge
# links alone (e.g. `ninja refii_hostbridge`), avoiding parallel POST_BUILD write races on shared
# DLLs in bin/ when multiple bridges link concurrently. The full closure is staged once by the
# stamped refii_stage_godot target below.
function(refii_stage_title_bridge target folder)
    if(NOT REFII_STAGE_GODOT_BUILD)
        return()
    endif()
    set(_title_bin_dir "${REFII_GODOT_BUILD_BIN_DIR}/${folder}")
    add_custom_command(TARGET ${target} POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${_title_bin_dir}"
        COMMAND ${CMAKE_COMMAND} -E rm -f "${REFII_GODOT_BUILD_BIN_DIR}/$<TARGET_FILE_NAME:${target}>"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "$<TARGET_FILE:${target}>" "${_title_bin_dir}"
        COMMENT "Staging ${target} into Godot/build/${REFII_GODOT_ARCH}/bin/${folder}"
        VERBATIM
    )
endfunction()

# THE STAGING TARGET, for every title's instance and the shared natives.
#
# Stamped (`stage_godot_$<CONFIG>.stamp`) and driven by a single `cmake -P` script rather than an
# unstamped phony target spawning 28 `cmake -E` processes on every build:
#   - When no staged library or DXC file moved: 0.00s Ninja no-op.
#   - When any staged library re-linked: runs `refii_run_stage_godot.cmake` once and updates only
#     the changed files via ONLY_IF_DIFFERENT.
function(refii_define_stage_godot)
    if(NOT REFII_STAGE_GODOT_BUILD)
        return()
    endif()

    set(_targets "")
    set(_file_deps "")
    set(_prune_lines "")
    set(_title_lines "")
    set(_shared_lines "")

    foreach(_staged rexruntime TracyClient)
        if(TARGET ${_staged})
            list(APPEND _prune_lines "    \"${_staged}=$<TARGET_FILE_NAME:${_staged}>\"")
        endif()
    endforeach()

    foreach(_folder IN LISTS REFII_TITLE_INSTANCE_FOLDERS)
        foreach(_staged IN LISTS REFII_TITLE_INSTANCE_TARGETS_${_folder})
            if(TARGET ${_staged})
                list(APPEND _targets ${_staged})
                list(APPEND _file_deps "$<TARGET_FILE:${_staged}>")
                list(APPEND _title_lines "    \"${_folder}=$<TARGET_FILE:${_staged}>\"")
            endif()
        endforeach()
    endforeach()

    foreach(_staged IN LISTS REFII_STAGED_SHARED_TARGETS)
        if(TARGET ${_staged})
            list(APPEND _targets ${_staged})
            list(APPEND _file_deps "$<TARGET_FILE:${_staged}>")
            list(APPEND _shared_lines "    \"$<TARGET_FILE:${_staged}>\"")
        endif()
    endforeach()

    set(_shader_lib_lines "")
    foreach(_f IN LISTS REFII_SHADER_LIBRARY_FILES)
        list(APPEND _file_deps "${_f}")
        list(APPEND _shader_lib_lines "    \"${_f}\"")
    endforeach()

    set(_shader_tool_lines "")
    foreach(_f IN LISTS REFII_SHADER_TOOL_FILES)
        list(APPEND _file_deps "${_f}")
        list(APPEND _shader_tool_lines "    \"${_f}\"")
    endforeach()

    list(JOIN _prune_lines "\n" _prune_body)
    list(JOIN _title_lines "\n" _title_body)
    list(JOIN _shared_lines "\n" _shared_body)
    list(JOIN _shader_lib_lines "\n" _shader_lib_body)
    list(JOIN _shader_tool_lines "\n" _shader_tool_body)

    set(_manifest "${CMAKE_BINARY_DIR}/stage_godot_manifest_$<CONFIG>.cmake")
    string(CONCAT _manifest_content
        "set(REFII_STAGE_BIN_DIR \"${REFII_GODOT_BUILD_BIN_DIR}\")\n"
        "set(REFII_STAGE_PRUNE_PAIRS\n${_prune_body}\n)\n"
        "set(REFII_STAGE_TITLE_FILES\n${_title_body}\n)\n"
        "set(REFII_STAGE_SHARED_FILES\n${_shared_body}\n)\n"
        "set(REFII_STAGE_SHADER_LIB_FILES\n${_shader_lib_body}\n)\n"
        "set(REFII_STAGE_SHADER_TOOL_FILES\n${_shader_tool_body}\n)\n"
    )
    file(GENERATE
        OUTPUT "${_manifest}"
        CONTENT "${_manifest_content}"
    )

    file(MAKE_DIRECTORY "${REFII_PACKAGE_STAMP_DIR}")
    set(_stamp "${REFII_PACKAGE_STAMP_DIR}/stage_godot_$<CONFIG>.stamp")
    set(_runner "${CMAKE_SOURCE_DIR}/cmake/refii_run_stage_godot.cmake")

    add_custom_command(
        OUTPUT "${_stamp}"
        COMMAND ${CMAKE_COMMAND}
            -DSTAGE_MANIFEST=${_manifest}
            -DSOURCE_ROOT=${CMAKE_SOURCE_DIR}
            -P "${_runner}"
        COMMAND ${CMAKE_COMMAND} -E touch "${_stamp}"
        DEPENDS
            ${_targets}
            ${_file_deps}
            "${_manifest}"
            "${_runner}"
            "${CMAKE_SOURCE_DIR}/cmake/refii_prune_stale_configs.cmake"
        COMMENT "Staging the runtime closure into Godot/build/${REFII_GODOT_ARCH}/bin"
        VERBATIM
    )

    add_custom_target(refii_stage_godot ALL DEPENDS "${_stamp}")
endfunction()
