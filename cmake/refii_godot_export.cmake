# Produces the shippable build: one folder, and a zip of it.
#
# Godot's own export writes exactly one file, the binary. The native closure — host bridge,
# guest runtime, and the build inputs the player compiles their game code with — is not a
# Godot resource and is deliberately kept out of the project by Godot/build/.gdignore. So an
# export on its own is never a runnable build, and every time one is taken from the editor
# it produces a binary with an empty bin/ beside it.
#
# This does both halves and the zip:
#
#   1. godot --headless --export-release "<preset>"   -> the binary, PCK embedded
#   2. stage Godot/build/<arch>/                      -> bin/, bin/recompile
#   3. zip the result                                 -> one file to hand to a tester
#
# PER ARCHITECTURE, and it packages the architecture this build tree was configured for:
# the natives are per-arch and cannot be cross-staged. Building all four means configuring
# for each and running this target in each.
#
# Not wired to ALL: it is slow, needs a Godot binary, and is a release action.

include_guard(GLOBAL)

set(REFII_GODOT_EXECUTABLE "" CACHE FILEPATH
    "Godot 4 editor binary (MONO/.NET build) used to export the game")

# ON makes a plain `cmake --build` produce the release zip, rather than needing a separate
# `--target refii_package`. OFF still defines the target, so it stays available on demand.
option(REFII_PACKAGE "Build the release package (export + stage + zip) as part of ALL" OFF)

# Zig, pinned and fetched for the TARGET architecture. It is the player's whole C++
# toolchain — clang, lld and a redistributable libc — and the build driver, so this is
# what makes an install work on a machine with nothing installed. See refii_fetch_zig.cmake.
option(REFII_BUNDLE_ZIG "Ship pinned Zig inside the package" ON)

# NOT REFII_PACKAGE_OUTPUT_DIR: that name is already the build's own output root (bin/,
# lib/, tools/). Reusing it here inherited the build directory, and had this set() won
# instead it would have redirected every artefact the build produces.
set(REFII_RELEASE_PACKAGE_DIR "${CMAKE_SOURCE_DIR}/out/package" CACHE PATH
    "Directory that packaged releases and their zips are written into")

# The folder a tester unzips. Also the name inside the zip.
set(REFII_PACKAGE_NAME "Fable 2 Return to Albion" CACHE STRING
    "Name of the packaged application folder")

function(refii_define_godot_export_target)
    if(NOT REFII_STAGE_GODOT_BUILD)
        return()
    endif()

    # Preset names are the contract with Godot/export_presets.cfg — Godot selects BY NAME,
    # so a rename there silently breaks this rather than failing loudly.
    if(WIN32)
        set(_platform "windows")
        if(REFII_GODOT_ARCH STREQUAL "arm64")
            set(_preset "Windows ARM64")
        else()
            set(_preset "Windows Desktop")
        endif()
        set(_binary "${REFII_PACKAGE_NAME}.exe")
    else()
        set(_platform "linux")
        if(REFII_GODOT_ARCH STREQUAL "arm64")
            set(_preset "Linux ARM64")
        else()
            set(_preset "Linux")
        endif()
        set(_binary "${REFII_PACKAGE_NAME}.${REFII_GODOT_ARCH}")
    endif()

    set(_work "${REFII_RELEASE_PACKAGE_DIR}/${_platform}-${REFII_GODOT_ARCH}")
    set(_zip "${REFII_RELEASE_PACKAGE_DIR}/refii-${_platform}-${REFII_GODOT_ARCH}.zip")

    # ALL only when asked: an export is slow and needs a Godot binary, so it must not be
    # dragged into every ordinary source build.
    set(_all "")
    if(REFII_PACKAGE)
        set(_all ALL)
    endif()

    add_custom_target(refii_package ${_all}
        COMMAND ${CMAKE_COMMAND}
            -DGODOT=${REFII_GODOT_EXECUTABLE}
            -DPRESET=${_preset}
            -DWORK_DIR=${_work}
            -DAPP_NAME=${REFII_PACKAGE_NAME}
            -DOUT_BINARY=${_binary}
            -DZIP_PATH=${_zip}
            -DPROJECT_DIR=${CMAKE_SOURCE_DIR}/Godot
            -DSTAGING_ROOT=${REFII_GODOT_BUILD_DIR}
            -DBUNDLE_ZIG=$<BOOL:${REFII_BUNDLE_ZIG}>
            -DZIG_SCRIPT=${CMAKE_SOURCE_DIR}/cmake/refii_fetch_zig.cmake
            -DTOOL_PLATFORM=${_platform}
            -DTOOL_ARCH=${REFII_GODOT_ARCH}
            -DTOOL_CACHE=${REFII_RELEASE_PACKAGE_DIR}/toolcache
            -P "${CMAKE_SOURCE_DIR}/cmake/refii_run_godot_export.cmake"
        COMMENT "Packaging ${_platform}-${REFII_GODOT_ARCH} for release"
        USES_TERMINAL
        VERBATIM
    )

    # Everything that WRITES INTO Godot/build/<arch>, not just the libraries themselves.
    #
    # refii_stage_godot is what actually copies the native closure there; depending only on
    # refii_hostbridge builds the library and leaves the staging directory untouched, so a
    # package taken from a clean tree ships a binary with an empty bin/ beside it — the
    # exact failure this target exists to prevent. `--target refii_package` builds only this
    # target's dependency graph, so anything missing from it is simply absent.
    foreach(_dependency
            refii_hostbridge
            refii_payload_sdk
            refii_stage_godot
            refii-stage-external-libs)
        if(TARGET ${_dependency})
            add_dependencies(refii_package ${_dependency})
        endif()
    endforeach()

    # Kept as the name used before the zip step existed.
    add_custom_target(refii_godot_export DEPENDS refii_package)

    # Caught at configure time rather than at the end of a long build.
    if(REFII_PACKAGE AND NOT REFII_GODOT_EXECUTABLE)
        message(FATAL_ERROR
            "REFII_PACKAGE=ON needs REFII_GODOT_EXECUTABLE set to a Godot 4 MONO binary.")
    endif()

    if(REFII_PACKAGE)
        message(STATUS
            "Refii: REFII_PACKAGE=ON - a normal build also packages "
            "${_platform}-${REFII_GODOT_ARCH}")
    else()
        message(STATUS
            "Refii: 'refii_package' builds ${_platform}-${REFII_GODOT_ARCH} "
            "(or set REFII_PACKAGE=ON to fold it into a normal build)")
    endif()
endfunction()
