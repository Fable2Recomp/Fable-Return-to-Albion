# Exports, stages and zips one platform/architecture.
#
# A script rather than a chain of COMMANDs so failures can be reported in terms of what the
# user has to fix, and so staging and zipping are one cross-platform path instead of a
# per-platform shell script.

cmake_minimum_required(VERSION 3.24)

# The modding SDK's pins. Defaulted here rather than required so an older caller still works.
if(NOT DEFINED REFII_GODOT_FLOOR OR REFII_GODOT_FLOOR STREQUAL "")
    set(REFII_GODOT_FLOOR "4.7")
endif()
if(NOT DEFINED REFII_GODOT_FLAVOUR OR REFII_GODOT_FLAVOUR STREQUAL "")
    set(REFII_GODOT_FLAVOUR "mono")
endif()
if(NOT DEFINED REFII_DOTNET_MAJOR OR REFII_DOTNET_MAJOR STREQUAL "")
    set(REFII_DOTNET_MAJOR "8")
endif()

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

# THE UNIT TESTS GATE THE PACKAGE, AND THEY RUN HERE, BEFORE THE EXPORT.
#
# The C# project runs them after an ordinary build, but not inside Godot's export build: the
# test project references the launcher, so testing from within the ExportRelease publish
# rebuilds the launcher a second time into the folders the publish is using, and the export
# then finishes with no .NET assemblies in it. Running them as their own step, in the Debug
# configuration, keeps the gate and keeps the two builds out of each other's way.
if(EXISTS "${PROJECT_DIR}/tests/Refii.Tests/Refii.Tests.csproj")
    find_program(_refii_dotnet NAMES dotnet)
    if(NOT _refii_dotnet)
        message(FATAL_ERROR
            "dotnet was not found, so the unit tests that gate a package cannot run.")
    endif()

    message(STATUS "Running the unit tests before the export")
    execute_process(
        COMMAND "${_refii_dotnet}" test "${PROJECT_DIR}/tests/Refii.Tests/Refii.Tests.csproj"
                -c Debug -p:IsTesting=true -p:REFII_NO_TESTS=1
                --logger "console;verbosity=minimal"
        RESULT_VARIABLE _tests_result
    )
    if(NOT _tests_result EQUAL 0)
        message(FATAL_ERROR
            "The unit tests failed (exit ${_tests_result}); nothing was exported. "
            "Run `dotnet test Godot/tests/Refii.Tests` to see them on their own.")
    endif()
endif()

# IMPORT AS ITS OWN RUN FIRST. --export-release reimports anything stale on its way past,
# and on Windows that killed the editor: an access violation partway through the .glsl
# files, reported below as "Godot export failed (exit Access violation)" with nothing to
# say an import had been under way. The same editor imports the project cleanly when asked
# to do only that, then exports cleanly afterwards. A warm cache makes this a quick scan.
# A bad import warns rather than stops: the export still reports any real failure, so this
# leaves the build no worse off than not running it.
execute_process(
    COMMAND "${GODOT}" --headless --path "${PROJECT_DIR}" --import
    RESULT_VARIABLE _import_result
)
if(NOT _import_result EQUAL 0)
    message(WARNING
        "Godot asset import returned ${_import_result}; continuing to the export, which "
        "will reimport what is missing and report anything it cannot.")
endif()

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

# AND THE .NET ASSEMBLIES ARE THE OTHER HALF OF THE ARTEFACT. Godot has also returned 0 with a
# binary that carries none of them -- it then stops at ".NET assemblies not found" on the
# player's machine, and nothing in the export's own output says so. A preset that embeds them
# stores them in the binary under .godot/mono/publish/<arch>/; one that does not writes a
# data_* folder beside it.
file(GLOB_RECURSE _refii_loose_assemblies "${_app}/data_*/refii-launcher.dll")
if(NOT _refii_loose_assemblies)
    file(STRINGS "${_app}/${OUT_BINARY}" _refii_embedded_assemblies
         REGEX "\\.godot/mono/publish/[A-Za-z0-9_]+/refii-launcher\\.dll"
         LIMIT_COUNT 1)
    if(NOT _refii_embedded_assemblies)
        message(FATAL_ERROR
            "The export wrote ${_app}/${OUT_BINARY} without the .NET assemblies: neither a "
            "data_* folder beside it nor refii-launcher.dll embedded in it. The game would "
            "stop at \".NET assemblies not found\". Run the package again; if it repeats, "
            "delete Godot/.godot/mono/temp and export once more.")
    endif()
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
#
# NOR ITS CACHE. cache/ is what the running game converts from the player's own banks —
# textures and the pub-game SWF shapes — and in a dev tree it is converted from OUR game.
# It is derived game data and it is not small: the one that shipped was 2.1 GB, two thirds
# of the zip, with the SWF shapes three times over under three build hashes.
message(STATUS "Staging natives: ${STAGING_ROOT} -> ${_app}")
file(COPY "${STAGING_ROOT}/" DESTINATION "${_app}"
    PATTERN "librefii_generated.so" EXCLUDE
    PATTERN "dev-skip-rebuild" EXCLUDE
    PATTERN "librefii_generated.dylib" EXCLUDE
    PATTERN "refii_generated.dll" EXCLUDE
    PATTERN "build-info.json" EXCLUDE
    PATTERN "refii.toml" EXCLUDE
    PATTERN "logs" EXCLUDE
    PATTERN "assets" EXCLUDE
    PATTERN "cache" EXCLUDE
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
        "${_app}/bin/refii_generated.dll"
        "${_app}/bin/recompile/dev-skip-rebuild")
    if(EXISTS "${_forbidden}")
        message(FATAL_ERROR
            "The recompiled game module reached the package (${_forbidden}). It is derived "
            "from a game image and must never ship.")
    endif()
endforeach()

# Nor may any of the player's game data. An install writes assets/game beside the binary, so
# packaging a directory that has been RUN would otherwise ship the game itself.
foreach(_leaked
        "${_app}/assets/game" "${_app}/assets/generated" "${_app}/assets/unpacked"
        "${_app}/cache")
    if(EXISTS "${_leaked}")
        file(REMOVE_RECURSE "${_leaked}")
        message(STATUS "Removed game-derived data from the package: ${_leaked}")
    endif()
endforeach()

# The per-title metadata DOES ship: assets/metadata/rexnet.toml is ours, not the game's, and
# it is what the guest reads at XNetStartup for the shard tier, the advertised title id and
# the presence mappings. The runtime writes a built-in default when an install has none
# (refii_app.h, RefiiWriteDefaultRexNetConfig), and the shipped copy replaces that default
# once it is newer, so this is the file that carries the reasoning for each value.
set(_metadata "${PROJECT_DIR}/../assets/metadata")
if(IS_DIRECTORY "${_metadata}")
    file(COPY "${_metadata}" DESTINATION "${_app}/assets")
    message(STATUS "Staged per-title metadata: ${_metadata} -> ${_app}/assets/metadata")
endif()

# The mods that ship with the app, as SOURCE: the launcher packs each into assets/mods on first
# start (Engine_ModBundled.cs), switched off. Ours, not the game's — mod Lua patches the shipped
# scripts' tables and never carries a copy of one. Copied whole each time so a mod deleted from
# mods-src does not linger in the package.
set(_bundled_mods "${PROJECT_DIR}/../mods-src")
if(IS_DIRECTORY "${_bundled_mods}")
    file(REMOVE_RECURSE "${_app}/bundled-mods")
    file(COPY "${_bundled_mods}/" DESTINATION "${_app}/bundled-mods")
    # THE LIST IS THE CONTENT. A player unpacks an update over their install, which leaves a
    # file this release deleted lying in bundled-mods/; the launcher packs only what this list
    # names, so a stale file cannot come back as part of the mod.
    file(GLOB_RECURSE _bundled_files RELATIVE "${_app}/bundled-mods" "${_app}/bundled-mods/*")
    list(SORT _bundled_files)
    list(JOIN _bundled_files "\n" _bundled_list)
    file(WRITE "${_app}/bundled-mods/manifest.txt" "${_bundled_list}\n")
    message(STATUS "Staged bundled mods: ${_bundled_mods} -> ${_app}/bundled-mods")
endif()

if(NOT EXISTS "${_app}/bin")
    message(FATAL_ERROR "Staging produced no bin/ in ${_app}; the package would not run.")
endif()

# ONE COPY OF EACH NATIVE, and one directory for them. The install-time link reads the
# runtime and the refii libraries from bin/, where the host loads them from, and the
# generator is a library in that same directory; nothing under bin/recompile is a binary.
# A staging root from before that still carries the old lib/ and bin/ copies -- the runtime
# alone was 444 MB twice -- and they are not ours to ship a second time.
foreach(_stale "${_app}/bin/recompile/lib" "${_app}/bin/recompile/bin")
    if(IS_DIRECTORY "${_stale}")
        file(REMOVE_RECURSE "${_stale}")
        message(STATUS "Removed the pre-library staging directory ${_stale} from the package")
    endif()
endforeach()

# THE MSVC RUNTIME, staged from the redistributables rather than assumed present. Without
# it the package's own binaries have imports no player's machine can resolve, and the
# launcher's first native load fails. See refii_stage_windows_runtime.cmake.
if(TOOL_PLATFORM STREQUAL "windows")
    if(WINDOWS_RUNTIME_SCRIPT AND EXISTS "${WINDOWS_RUNTIME_SCRIPT}")
        set(APP_DIR "${_app}")
        include("${WINDOWS_RUNTIME_SCRIPT}")
        unset(APP_DIR)
    else()
        message(WARNING
            "No Windows runtime staging script at '${WINDOWS_RUNTIME_SCRIPT}'; the package "
            "ships without the MSVC runtime and its natives will not load on a machine "
            "that has no matching Visual C++ runtime installed.")
    endif()
endif()

# DEBUG INFO STAYS HOME. The natives are Debug builds and most of each file is DWARF: the
# runtime is 444 MB of which 81 MB is code. --strip-debug removes only that and keeps
# .symtab and .dynsym, so the install-time link resolves exactly as before and a crash
# still names its frames; only line numbers go, and those are read against THIS tree.
#
# Never the Godot binary: its pck is appended past the last section, and strip drops
# trailing data. Never the staged sysroot either; it ships as the toolchain built it.
if(TOOL_PLATFORM STREQUAL "linux")
    find_program(_strip NAMES llvm-strip strip)
    if(_strip)
        file(GLOB _strippable LIST_DIRECTORIES false
            "${_app}/bin/*.so")
        set(_stripped 0)
        foreach(_binary IN LISTS _strippable)
            if(IS_SYMLINK "${_binary}")
                continue()
            endif()
            execute_process(
                COMMAND "${_strip}" --strip-debug "${_binary}"
                RESULT_VARIABLE _strip_result
                OUTPUT_QUIET ERROR_VARIABLE _strip_error)
            if(_strip_result EQUAL 0)
                math(EXPR _stripped "${_stripped} + 1")
            else()
                message(WARNING "Could not strip ${_binary}: ${_strip_error}")
            endif()
        endforeach()
        message(STATUS "Stripped debug info from ${_stripped} natives with ${_strip}")
    else()
        message(WARNING
            "No strip on this host (llvm-strip or binutils strip): the package ships the "
            "natives with their debug info, roughly four times the size.")
    endif()
endif()

# THE MODDING EDITOR IS THIS PROJECT, so the package carries a copy of it.
#
# The plugins are C# inside the launcher's own assembly and lean on Refii.* throughout: there is
# nothing to extract into a standalone add-on, and no reason to, because the project holds no
# game assets. `sdk/` is that project, at the commit this binary was built from, so
# HostBridge.ExpectedAbiVersion and the staged natives agree by construction.
#
# WITHOUT THE BUILD OUTPUT. `.godot/` is an editor cache, `build/` is the native staging tree
# (per-arch, ~600 MB, and already staged into bin/), and an exported binary left in the project
# would ship twice.
#
# WITHOUT `levels/` EITHER. Those scenes are editor bakes of the player's own game data —
# meshes and images embedded — so they are not redistributable, and the game does not read
# them: regions build straight to a node and a pub game stage rebuilds from the mount when
# its scene is absent. The export presets exclude the same folder from the pck.
message(STATUS "Staging the modding SDK -> ${_app}/sdk")
file(REMOVE_RECURSE "${_app}/sdk")
file(
    COPY "${PROJECT_DIR}/"
    DESTINATION "${_app}/sdk"
    PATTERN ".godot" EXCLUDE
    PATTERN "build" EXCLUDE
    PATTERN "levels" EXCLUDE
    PATTERN "bin" EXCLUDE
    PATTERN "obj" EXCLUDE
    PATTERN "*.x86_64" EXCLUDE
    PATTERN "*.exe" EXCLUDE
    PATTERN "*.pck" EXCLUDE
)

if(NOT EXISTS "${_app}/sdk/project.godot")
    message(FATAL_ERROR
        "The modding SDK staged without a project.godot; the editor button would have nothing "
        "to open. Expected it under ${PROJECT_DIR}.")
endif()

# ONLY A FLOOR IS PINNED. The plugins are plugins and a C# project has a lowest Godot it opens
# in, not a highest, so the launcher fetches the latest stable at or above this. The build keeps
# its own exact pin in refii_fetch_godot.cmake for reproducible exports; these are what a
# player's machine needs.
file(WRITE "${_app}/sdk/pins.json"
"{\n"
"  \"godotFloor\": \"${REFII_GODOT_FLOOR}\",\n"
"  \"flavour\": \"${REFII_GODOT_FLAVOUR}\",\n"
"  \"dotnetMajor\": \"${REFII_DOTNET_MAJOR}\",\n"
"  \"checksumAsset\": \"SHA512-SUMS.txt\"\n"
"}\n"
)

# THE LICENCES TRAVEL WITH THE BINARIES. The BSD, MIT, Apache and LGPL components all require
# their notices in a binary distribution, not only in the source tree. THIRD_PARTY_NOTICES.txt
# is written by tools/licenses/gen_third_party_notices.py.
get_filename_component(_repo_root "${CMAKE_CURRENT_LIST_DIR}/.." ABSOLUTE)
foreach(_notice LICENSE THIRD_PARTY_NOTICES.txt)
    if(NOT EXISTS "${_repo_root}/${_notice}")
        message(FATAL_ERROR
            "${_notice} is missing from ${_repo_root}; a release must carry it. "
            "Run tools/licenses/gen_third_party_notices.py.")
    endif()
    file(COPY "${_repo_root}/${_notice}" DESTINATION "${_app}")
endforeach()

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
message(STATUS "  contents: ${OUT_BINARY}, bin/ (natives + recompile), sdk/ (modding editor)")
message(STATUS "  the player's game, its recompiled code and refii.toml are created on first run")
