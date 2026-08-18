# Exports, stages and zips one platform/architecture.
#
# A script rather than a chain of COMMANDs so failures can be reported in terms of what the
# user has to fix, and so staging and zipping are one cross-platform path instead of a
# per-platform shell script.

cmake_minimum_required(VERSION 3.24)

foreach(_required PRESET WORK_DIR APP_NAME OUT_BINARY ZIP_PATH PROJECT_DIR STAGING_ROOT)
    if(NOT ${_required})
        message(FATAL_ERROR "refii_run_godot_export: ${_required} not set")
    endif()
endforeach()

# The pinned editor, unless the developer named their own. Fetched HERE and not at configure
# time for the same reason zig is: it is ~200 MB over the network, and only a package needs
# it. Configuring, building and testing must not depend on being able to reach GitHub.
if(NOT GODOT AND GODOT_SCRIPT AND EXISTS "${GODOT_SCRIPT}")
    include("${GODOT_SCRIPT}")
    refii_fetch_godot("${HOST_PLATFORM}" "${HOST_ARCH}"
                      "${TOOL_PLATFORM}" "${TOOL_ARCH}"
                      "${GODOT_DIR}" "${TOOL_CACHE}" GODOT)
endif()

if(NOT GODOT OR NOT EXISTS "${GODOT}")
    message(FATAL_ERROR
        "Godot binary not found at '${GODOT}'.\n"
        "Leave REFII_GODOT_EXECUTABLE unset to fetch the pinned one (see "
        "cmake/refii_fetch_godot.cmake), or set it:\n"
        "  cmake -B <build> -DREFII_GODOT_EXECUTABLE=/path/to/Godot_v4.7.1-stable_mono_linux.x86_64\n"
        "It must be the MONO/.NET build — the standard build cannot export a C# project.")
endif()

if(NOT EXISTS "${STAGING_ROOT}/bin")
    message(FATAL_ERROR
        "No staged natives at ${STAGING_ROOT}/bin.\n"
        "Build refii for this architecture first: the package needs the host bridge, the "
        "guest runtime and the player's build inputs beside the executable.")
endif()

# The app folder inside the work directory. Rebuilt from scratch each time: a stale library
# left behind by an earlier layout is invisible in the zip and fatal on a tester's machine.
set(_app "${WORK_DIR}/${APP_NAME}")
file(REMOVE_RECURSE "${_app}")
file(MAKE_DIRECTORY "${_app}")

message(STATUS "Exporting '${PRESET}' -> ${_app}/${OUT_BINARY}")

execute_process(
    COMMAND "${GODOT}" --headless --path "${PROJECT_DIR}"
            --export-release "${PRESET}" "${_app}/${OUT_BINARY}"
    RESULT_VARIABLE _export_result
)

if(NOT _export_result EQUAL 0)
    message(FATAL_ERROR
        "Godot export failed (exit ${_export_result}).\n"
        "If it reported a missing preset, the names in Godot/export_presets.cfg must match "
        "the ones in cmake/refii_godot_export.cmake — Godot selects a preset BY NAME.")
endif()

# Godot has returned 0 while writing nothing, so the artefact is the test, not the exit code.
if(NOT EXISTS "${_app}/${OUT_BINARY}")
    message(FATAL_ERROR
        "Godot reported success but wrote no binary to ${_app}/${OUT_BINARY}. "
        "Usually the export templates for this Godot version are not installed.")
endif()

# The native closure, in the shape Engine_AppRoot resolves: <app>/bin, <app>/bin/recompile.
#
# WITHOUT THE RECOMPILED PAYLOAD. A dev tree stages librefii_generated.so beside the other
# libraries because the dev build produces one, but it is compiled from a game image on this
# machine: it is derived from the game, it is not ours to distribute, and shipping it would
# defeat the install-time codegen this whole pipeline exists for. The player's copy is built
# on their machine, into this same directory. build-info.json is its build record.
#
# AND WITHOUT THE DEVELOPER'S RUNTIME STATE. The staging root is also the app root a dev
# tree runs from, so launching the editor writes refii.toml, assets/ and logs/ straight into
# what we ship. That config carries ABSOLUTE developer paths —
# game_data_root = "/home/.../Godot/build/x86_64/assets" — and shipping it points every
# tester's install at a directory that exists only on this machine. All three are created on
# first run, so a package must not contain them.
message(STATUS "Staging natives: ${STAGING_ROOT} -> ${_app}")
file(COPY "${STAGING_ROOT}/" DESTINATION "${_app}"
    PATTERN "librefii_generated.so" EXCLUDE
    PATTERN "librefii_generated.dylib" EXCLUDE
    PATTERN "refii_generated.dll" EXCLUDE
    PATTERN "build-info.json" EXCLUDE
    PATTERN "refii.toml" EXCLUDE
    PATTERN "logs" EXCLUDE
    PATTERN "assets" EXCLUDE
)

foreach(_dev_state "${_app}/refii.toml" "${_app}/logs" "${_app}/assets")
    if(EXISTS "${_dev_state}")
        file(REMOVE_RECURSE "${_dev_state}")
        message(STATUS "Removed developer runtime state from the package: ${_dev_state}")
    endif()
endforeach()

# Zig for the TARGET architecture, pinned and hash-verified. This is the player's entire
# C++ toolchain and the thing the launcher drives to build their game code — with it the
# install needs nothing preinstalled. Fetched per target rather than copied from this
# machine, so packaging windows-arm64 from an x86_64 Linux host still ships arm64 tools.
if(BUNDLE_ZIG AND ZIG_SCRIPT AND EXISTS "${ZIG_SCRIPT}")
    include("${ZIG_SCRIPT}")
    refii_fetch_zig("${TOOL_PLATFORM}" "${TOOL_ARCH}"
                    "${_app}/bin/recompile/zig" "${TOOL_CACHE}")
endif()

# NO ABSOLUTE SYMLINKS IN A PACKAGE — but relative ones must be left alone.
#
# An absolute link is the hazard: a dev tree accumulates them (Godot/build/<arch>/bin/assets
# pointed into another checkout) and they are meaningless once the zip leaves this machine,
# while one aimed at game data turns any tool that dereferences links into a redistribution
# leak that a `find -name` check walks straight past.
#
# RELATIVE links are load-bearing. The bundled sysroot is full of them and cannot work
# without them: libstdc++.so -> libstdc++.so.6 -> libstdc++.so.6.0.33 is exactly how the
# linker resolves the C++ runtime, and dozens of headers are relative links too. Deleting
# those breaks the toolchain we just shipped. They also resolve correctly on any machine,
# because they resolve inside the package.
file(GLOB_RECURSE _packaged_entries LIST_DIRECTORIES true "${_app}/*")
foreach(_entry IN LISTS _packaged_entries)
    if(IS_SYMLINK "${_entry}")
        file(READ_SYMLINK "${_entry}" _link_target)
        if(IS_ABSOLUTE "${_link_target}")
            message(STATUS "Removed absolute symlink from the package: ${_entry} -> ${_link_target}")
            file(REMOVE "${_entry}")
        endif()
    endif()
endforeach()

foreach(_forbidden
        "${_app}/bin/librefii_generated.so"
        "${_app}/bin/refii_generated.dll")
    if(EXISTS "${_forbidden}")
        message(FATAL_ERROR
            "The recompiled game module reached the package (${_forbidden}). It is derived "
            "from a game image and must never ship.")
    endif()
endforeach()

# Nor may any of the player's game data. An install writes assets/game beside the binary, so
# packaging a directory that has been RUN would otherwise ship the game itself.
foreach(_leaked "${_app}/assets/game" "${_app}/assets/generated" "${_app}/assets/unpacked")
    if(EXISTS "${_leaked}")
        file(REMOVE_RECURSE "${_leaked}")
        message(STATUS "Removed game-derived data from the package: ${_leaked}")
    endif()
endforeach()

if(NOT EXISTS "${_app}/bin")
    message(FATAL_ERROR "Staging produced no bin/ in ${_app}; the package would not run.")
endif()

# One file to hand over. Zipped from the work directory so the archive contains the app
# FOLDER rather than loose files — unzipping in Downloads should not scatter 1 GB of
# libraries across it.
get_filename_component(_zip_name "${ZIP_PATH}" NAME)
message(STATUS "Zipping -> ${ZIP_PATH}")
file(REMOVE "${ZIP_PATH}")

execute_process(
    COMMAND ${CMAKE_COMMAND} -E tar "cf" "${ZIP_PATH}" --format=zip -- "${APP_NAME}"
    WORKING_DIRECTORY "${WORK_DIR}"
    RESULT_VARIABLE _zip_result
)

if(NOT _zip_result EQUAL 0 OR NOT EXISTS "${ZIP_PATH}")
    message(FATAL_ERROR "Packaging the zip failed (exit ${_zip_result})")
endif()

file(SIZE "${ZIP_PATH}" _zip_size)
math(EXPR _zip_mb "${_zip_size} / 1048576")

message(STATUS "")
message(STATUS "Packaged: ${ZIP_PATH} (${_zip_mb} MB)")
message(STATUS "  folder:  ${_app}")
message(STATUS "  contents: ${OUT_BINARY}, bin/ (natives + recompile)")
message(STATUS "  the player's game, its recompiled code and refii.toml are created on first run")
