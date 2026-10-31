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
#   zig/            the bundled toolchain: zig + its lib/ (libc, libc++, MinGW CRT)
#   include/NN/     the include directories, staged verbatim, one per number
#   include_dirs.cmake  the list, in the order the compiler saw them
#   <Folder>/hooks/ a title's hook sources (<Folder>/hook_sources.cmake), compiled into the payload on
#                   the player's machine
#   payload_core/   the payload's shared sources (payload_core_sources.cmake), compiled into
#                   every title's module
#   src/            payload_export.cpp
#   versions/<name>/  <key>_manifest.toml + <key>_config.toml (refii for Fable 2, refiii for
#                     Fable 3), one folder per supported executable
#
# NO BINARIES. The generator is refii_recompiler, a library in bin/ beside the runtime it
# imports, and the libraries the payload links (rexruntime, refii_core, refii_runtime, and a
# title's refii_vfs) are the ones the host loads. This directory used to carry a second copy of
# each and
# a generator executable with its own CRT, and the generator's dependency on a PATH the
# launcher set is what failed on machines with a restrictive DLL search policy.
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

    set(_payload_include_target refii_generated)
    if(REFII_PAYLOAD_INCLUDE_TARGET)
        set(_payload_include_target "${REFII_PAYLOAD_INCLUDE_TARGET}")
    endif()
    if(NOT TARGET "${_payload_include_target}")
        message(FATAL_ERROR
            "refii_stage_payload_sdk() cannot find its payload include target: "
            "${_payload_include_target}")
    endif()

    set(_sdk_dir "${REFII_GODOT_BUILD_BIN_DIR}/recompile")
    # $<CONFIG> IS PART OF THE NAME. Some of these dirs arrive through config-conditional
    # genexes, and file(GENERATE) refuses one path with two different bodies. Single-config
    # Ninja on Linux evaluates only one configuration and never noticed; a multi-config
    # generator evaluates all three and the generate step fails outright. The staging is
    # per-config anyway -- see the rm -rf of lib/ below.
    set(_include_manifest "${CMAKE_BINARY_DIR}/payload_include_dirs_$<CONFIG>.txt")

    refii_write_payload_include_manifest("${_payload_include_target}" "${_include_manifest}")

    # The C++ standard library headers the payload is compiled against. Zig ships libc++,
    # which our own SDK headers do not parse under and whose std:: mangling would not match
    # the libraries this build produces — so libstdc++ is staged from THIS compiler.
    refii_stage_stdcxx("${_sdk_dir}/stdcxx" _staged_stdcxx)

    # The hook sources, compiled at install time rather than shipped as an archive — see
    # refii_stage_hook_sources.cmake for why an archive cannot work on Windows.
    # EACH TITLE'S OWN, in its own folder: bin/recompile/<Folder>/hooks, laid out relative to
    # <Folder>/src, and bin/recompile/<Folder>/hook_sources.cmake. The flat bin/recompile/hooks of
    # builds from before the split is removed so the launcher cannot read another title's.
    file(REMOVE_RECURSE "${_sdk_dir}/hooks")
    file(REMOVE "${_sdk_dir}/hook_sources.cmake")
    foreach(_folder IN LISTS REFII_TITLE_INSTANCE_FOLDERS)
        if(REFII_TITLE_HOOKS_TARGET_${_folder})
            refii_stage_hook_sources(${REFII_TITLE_HOOKS_TARGET_${_folder}} "${_sdk_dir}/${_folder}"
                                     "${CMAKE_SOURCE_DIR}/${_folder}")
        endif()
    endforeach()
    # The payload's shared sources, which every title's module compiles, hooks or not.
    refii_stage_payload_core_sources("${_sdk_dir}")

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
        # lib/ and bin/ are what this directory held before the generator became a library:
        # copies of the runtime and a rexglue executable. A staging root from that layout
        # still has them, the launcher no longer reads either, and the export must not ship
        # them -- so they are removed on every build rather than left to be found.
        COMMAND ${CMAKE_COMMAND} -E rm -rf "${_sdk_dir}/lib" "${_sdk_dir}/bin"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${_sdk_dir}/src"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${CMAKE_SOURCE_DIR}/src/payload/src/payload_export.cpp"
            "${CMAKE_SOURCE_DIR}/src/payload/src/libcxx_chrono_shim.h"
            "${_sdk_dir}/src"
        # Staged VERBATIM. The launcher writes the effective manifest at install time with
        # absolute paths, because only it knows where the player put the game and where
        # the generated sources are going; a manifest with relative paths baked in here
        # resolves differently in a dev tree than in an exported app.
        # One folder per supported executable; the launcher reads the one matching the
        # player's install. Removed first so a version dropped from the tree does not linger.
        # Every title's version folders, side by side: Fable2/versions and Fable3/versions.
        COMMAND ${CMAKE_COMMAND} -E rm -rf "${_sdk_dir}/versions"
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            "${CMAKE_SOURCE_DIR}/Fable2/versions"
            "${_sdk_dir}/versions"
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            "${CMAKE_SOURCE_DIR}/Fable3/versions"
            "${_sdk_dir}/versions"
    )

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
        # THE GATE: everything staged above compiles by include, for every title, or the build
        # fails here rather than in the launcher on a player's machine.
        COMMAND ${CMAKE_COMMAND}
            -DDESTINATION=${_sdk_dir}
            -DGENERATED_ROOT=${CMAKE_SOURCE_DIR}/generated
            -P "${CMAKE_SOURCE_DIR}/cmake/refii_check_staged_includes.cmake"
    )

    add_custom_target(refii_payload_sdk ALL
        ${_commands}
        DEPENDS "${_include_manifest}"
        COMMENT "Staging the install-time payload build inputs into Godot/build"
        VERBATIM
    )

    unset(_payload_include_target)
endfunction()
