# Stages everything the install-time payload build needs onto the shipped app.
#
# The player's machine gets no source tree and no package manager: it gets this directory,
# bundled Zig, and their own game image. So whatever the payload needs to compile and link
# has to be here — headers, import libraries, the hook archive, the generator, the manifest.
#
# THERE IS NO BUILD PROJECT. The launcher drives zig directly (see GameCodeBuild), because
# the generated sources are deleted after every successful build and a payload that is
# never rebuilt incrementally has no use for a dependency graph.
#
# Laid out under <app bin>/recompile:
#
#   bin/            the ReXGlue generator
#   zig/            the bundled toolchain: zig + its lib/ (libc, libc++, MinGW CRT)
#   include/NN/     the include directories, staged verbatim, one per number
#   include_dirs.cmake  the list, in the order the compiler saw them
#   lib/            rexruntime, refii_core, refii_vfs, refii_lua, refii_hooks
#   src/            payload_export.cpp
#   payload_exports.def
#   refii_manifest.toml, refii_config.toml
#
# NOTHING derived from a game image is staged here. The generated sources are produced on
# the player's machine, into assets/generated, and deleted once the module is built.

include_guard(GLOBAL)

include(${CMAKE_CURRENT_LIST_DIR}/refii_stage_stdcxx.cmake)
include(${CMAKE_CURRENT_LIST_DIR}/refii_stage_hook_sources.cmake)

# The include set is TAKEN FROM THE TARGET, never listed by hand. The generated code
# includes rex headers that pull in their own thirdparty ones, and a hand-written list
# would be a guess that compiles here and fails on a machine we cannot inspect.
#
# INCLUDE_DIRECTORIES carries the dirs propagated from linked targets too, so this is the
# same set the dev build compiles the payload with.
function(refii_write_payload_include_manifest target output_file)
    file(GENERATE
        OUTPUT "${output_file}"
        CONTENT "$<JOIN:$<REMOVE_DUPLICATES:$<TARGET_PROPERTY:${target},INCLUDE_DIRECTORIES>>,\n>\n"
    )
endfunction()

function(refii_stage_payload_sdk)
    if(NOT REFII_STAGE_GODOT_BUILD)
        return()
    endif()

    if(NOT TARGET refii_generated)
        message(FATAL_ERROR "refii_stage_payload_sdk() must run after refii_generated exists")
    endif()

    set(_sdk_dir "${REFII_GODOT_BUILD_BIN_DIR}/recompile")
    set(_include_manifest "${CMAKE_BINARY_DIR}/payload_include_dirs.txt")

    refii_write_payload_include_manifest(refii_generated "${_include_manifest}")

    # The C++ standard library headers the payload is compiled against. Zig ships libc++,
    # which our own SDK headers do not parse under and whose std:: mangling would not match
    # the libraries this build produces — so libstdc++ is staged from THIS compiler.
    refii_stage_stdcxx("${_sdk_dir}/stdcxx" _staged_stdcxx)

    # The hook sources, compiled at install time rather than shipped as an archive — see
    # refii_stage_hook_sources.cmake for why an archive cannot work on Windows.
    refii_stage_hook_sources(refii_hooks "${_sdk_dir}" "${CMAKE_SOURCE_DIR}")

    # DEV ONLY — see REFII_DEV_SKIP_GAMECODE_REBUILD in the top-level CMakeLists. Written and
    # REMOVED here rather than only written, so turning the option back off takes effect on the
    # next configure instead of leaving a marker nobody remembers dropping.
    set(_dev_marker "${_sdk_dir}/dev-skip-rebuild")
    if(REFII_DEV_SKIP_GAMECODE_REBUILD)
        file(WRITE "${_dev_marker}"
             "Written by REFII_DEV_SKIP_GAMECODE_REBUILD=ON.\n"
             "The launcher will run the CMake-built module instead of recompiling from the\n"
             "installed game image. This must never reach a player: the module here was built\n"
             "from THIS machine's image, not theirs.\n")
        message(WARNING
            "REFII_DEV_SKIP_GAMECODE_REBUILD is ON — the launcher will not rebuild the game "
            "module. Packaging refuses to run while this is set.")
    elseif(EXISTS "${_dev_marker}")
        file(REMOVE "${_dev_marker}")
    endif()

    set(_commands
        # lib/ IS EMPTIED FIRST, because copy_if_different only ever adds. A tree configured
        # RelWithDebInfo and then Debug ends up holding rexruntimerd.dll AND rexruntimed.dll,
        # and the payload link picks by name from whatever it finds: it linked the stale one,
        # so the payload imported one runtime while the host loaded the other. Two copies of
        # the runtime means two copies of its globals, and the guest died reading a field off
        # a null the second copy had never initialised. Same rule the hook staging already
        # follows one call above.
        COMMAND ${CMAKE_COMMAND} -E rm -rf "${_sdk_dir}/lib"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${_sdk_dir}/lib" "${_sdk_dir}/src" "${_sdk_dir}/bin"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${CMAKE_SOURCE_DIR}/src/payload/src/payload_export.cpp"
            "${CMAKE_SOURCE_DIR}/src/payload/src/libcxx_chrono_shim.h"
            "${_sdk_dir}/src"
        # Staged VERBATIM. The launcher writes the effective manifest at install time with
        # absolute paths, because only it knows where the player put the game and where
        # the generated sources are going; a manifest with relative paths baked in here
        # resolves differently in a dev tree than in an exported app.
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${CMAKE_SOURCE_DIR}/refii_config.toml"
            "${CMAKE_SOURCE_DIR}/refii_manifest.toml"
            "${_sdk_dir}"
    )

    # The libraries the payload links. refii_generated itself is NOT among them: it is what
    # gets built, and shipping our copy would be shipping code derived from our game image.
    # TracyClient is here for the GENERATOR, not the payload: rexglue links the runtime,
    # which links Tracy, and the staged tool finds neither without them beside it.
    foreach(_library rexruntime refii_core refii_vfs refii_lua refii_hooks_archive TracyClient)
        if(TARGET ${_library})
            list(APPEND _commands
                COMMAND ${CMAKE_COMMAND} -E copy_if_different
                    "$<TARGET_FILE:${_library}>" "${_sdk_dir}/lib"
            )
        endif()
    endforeach()

    if(TARGET rex::rexglue)
        list(APPEND _commands
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:rex::rexglue>" "${_sdk_dir}/bin"
        )
    endif()

    # Unquoted -D values: under VERBATIM, CMake quotes each argument itself, and quotes
    # written here arrive as part of the value — an EXISTS test on a path with literal
    # quotes around it fails every time.
    list(APPEND _commands
        COMMAND ${CMAKE_COMMAND}
            -DMANIFEST=${_include_manifest}
            -DDESTINATION=${_sdk_dir}
            -DSOURCE_ROOT=${CMAKE_SOURCE_DIR}
            -DGENERATED_ROOT=${CMAKE_SOURCE_DIR}/generated
            -P "${CMAKE_SOURCE_DIR}/cmake/refii_stage_payload_includes.cmake"
    )

    add_custom_target(refii_payload_sdk ALL
        ${_commands}
        DEPENDS "${_include_manifest}"
        COMMENT "Staging the install-time payload build inputs into Godot/build"
        VERBATIM
    )

    foreach(_dependency rexruntime refii_core refii_vfs refii_lua refii_hooks_archive rexglue)
        if(TARGET ${_dependency})
            add_dependencies(refii_payload_sdk ${_dependency})
        endif()
    endforeach()
endfunction()
