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
# WIRED TO ALL. It is slow and it is a release action, which is why it was opt-in, but opt-in
# meant the two ends of a build did not agree: a Linux tree with REFII_PACKAGE=ON in its cache
# packaged on every build while a Windows tree configured from the same presets stopped after
# staging and looked, in the log, exactly like a build that had finished. Nothing in the source
# said which was right. The default is the answer to that; `-DREFII_PACKAGE=OFF` still turns it
# off for a tree doing ordinary code iteration, where the export and the ~600 MB staging copy
# are a real cost per build.
#
# It no longer needs a Godot binary to be installed first — refii_fetch_godot.cmake pins one.

include_guard(GLOBAL)

# Left empty, a pinned Godot is fetched into REFII_GODOT_DIR at package time — see
# refii_fetch_godot.cmake. Set it to use a Godot you already have instead; it must be the
# .NET build of the version that file pins, because the export templates are matched by
# version and a mismatch is only discovered at the end of the export.
set(REFII_GODOT_EXECUTABLE "" CACHE FILEPATH
    "Godot 4 editor binary (MONO/.NET build) used to export the game")

# Outside the build tree on purpose: it is ~200 MB, it does not vary by build configuration,
# and re-configuring or wiping a build directory should not mean fetching it again.
set(REFII_GODOT_DIR "${CMAKE_SOURCE_DIR}/tools/godot" CACHE PATH
    "Directory the pinned Godot editor and its export template are staged into")

# ON makes a plain `cmake --build` produce the release zip, rather than needing a separate
# `--target refii_package`. OFF still defines the target, so it stays available on demand.
# The modding SDK's pins, staged into the package as sdk/pins.json. Only a FLOOR: the editor a
# player opens is the latest stable at or above it, because the plugins are plugins and a C#
# project has a lowest Godot it opens in rather than a highest. The exact pin the build itself
# uses lives in refii_fetch_godot.cmake and is a separate question.
set(REFII_GODOT_FLOOR "4.7" CACHE STRING
    "Lowest Godot the modding SDK opens in; must match project.godot's feature tag")
set(REFII_DOTNET_MAJOR "8" CACHE STRING
    "The .NET major the launcher assembly targets, for the modding editor's SDK check")

option(REFII_PACKAGE "Build the release package (export + stage + zip) as part of ALL" ON)

# Zig, pinned and fetched for the TARGET architecture. It is the player's whole C++
# toolchain — clang, lld and a redistributable libc — and the build driver, so this is
# what makes an install work on a machine with nothing installed. See refii_fetch_zig.cmake.
option(REFII_BUNDLE_ZIG "Ship pinned Zig inside the package" ON)

# NOT REFII_PACKAGE_OUTPUT_DIR: that name is already the build's own output root (bin/,
# lib/, tools/). Reusing it here inherited the build directory, and had this set() won
# instead it would have redirected every artefact the build produces.
set(REFII_RELEASE_PACKAGE_DIR "${CMAKE_SOURCE_DIR}/out/package" CACHE PATH
    "Directory that packaged releases and their zips are written into")

# NOT UNDER out/package. The downloaded toolchains are ~1.4 GB — Zig plus the Godot editor and
# its export template — and out/ is output: it gets cleared to force a clean package, and a
# developer clearing their own build output should not be signing up for a 1.4 GB re-download.
# It lived there and that is exactly what happened. Here it sits beside tools/godot and
# tools/vcpkg, which is where the other fetched toolchains already live and which nobody
# deletes to clean a build.
set(REFII_TOOL_CACHE_DIR "${CMAKE_SOURCE_DIR}/tools/cache" CACHE PATH
    "Directory the pinned toolchain archives are downloaded into and cached")

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

    # The HOST runs the editor; the target above is what gets packaged. They are usually the
    # same machine, but not necessarily — the zig fetch below already exists because
    # windows-arm64 can be packaged from an x86_64 Linux host, and the editor has to be the
    # host's build while the export template has to be the target's.
    if(CMAKE_HOST_WIN32)
        set(_host_platform "windows")
    else()
        set(_host_platform "linux")
    endif()
    if(CMAKE_HOST_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64|ARM64)$")
        set(_host_arch "arm64")
    else()
        set(_host_arch "x86_64")
    endif()

    # ALL only when asked: an export is slow and needs a Godot binary, so it must not be
    # dragged into every ordinary source build.
    set(_all "")
    if(REFII_PACKAGE)
        set(_all ALL)
    endif()

    # THE SHADER CHECK. `dotnet build` does not look at .gdshader files and Godot only compiles
    # one when something loads it, so a syntax error ships with a green build and shows up as
    # every affected mesh falling back to the default material — an untextured world, no error
    # anywhere but the run log. This target compiles all of them up front.
    #
    # It needs a REAL rendering driver: under --headless Godot uses the dummy renderer, which
    # reports success on a shader that cannot compile.
    set(_shader_check_all "")
    if(REFII_CHECK_SHADERS)
        set(_shader_check_all ALL)
    endif()

    # The binary goes through `cmake -E env` rather than a target ENVIRONMENT property, which
    # only applies to tests and would silently do nothing here.
    add_custom_target(refii_shader_check ${_shader_check_all}
        COMMAND ${CMAKE_COMMAND} -E env
            "REFII_GODOT_EXECUTABLE=${REFII_GODOT_EXECUTABLE}"
            "${CMAKE_SOURCE_DIR}/tools/check_godot_shaders.sh"
        WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}"
        COMMENT "Compiling Godot shaders"
        USES_TERMINAL
        VERBATIM
    )

    add_custom_target(refii_package ${_all}
        COMMAND ${CMAKE_COMMAND}
            -DGODOT=${REFII_GODOT_EXECUTABLE}
            -DPRESET=${_preset}
            -DWORK_DIR=${_work}
            -DAPP_NAME=${REFII_PACKAGE_NAME}
            -DOUT_BINARY=${_binary}
            -DREFII_GODOT_FLOOR=${REFII_GODOT_FLOOR}
            -DREFII_GODOT_FLAVOUR=${REFII_GODOT_FLAVOUR}
            -DREFII_DOTNET_MAJOR=${REFII_DOTNET_MAJOR}
            -DZIP_PATH=${_zip}
            -DPROJECT_DIR=${CMAKE_SOURCE_DIR}/Godot
            -DSTAGING_ROOT=${REFII_GODOT_BUILD_DIR}
            -DBUNDLE_ZIG=$<BOOL:${REFII_BUNDLE_ZIG}>
            -DZIG_SCRIPT=${CMAKE_SOURCE_DIR}/cmake/refii_fetch_zig.cmake
            -DGODOT_SCRIPT=${CMAKE_SOURCE_DIR}/cmake/refii_fetch_godot.cmake
            -DGODOT_DIR=${REFII_GODOT_DIR}
            -DHOST_PLATFORM=${_host_platform}
            -DHOST_ARCH=${_host_arch}
            -DTOOL_PLATFORM=${_platform}
            -DTOOL_ARCH=${REFII_GODOT_ARCH}
            -DTOOL_CACHE=${REFII_TOOL_CACHE_DIR}
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

    add_dependencies(refii_package refii_shader_check)

    # Kept as the name used before the zip step existed.
    add_custom_target(refii_godot_export DEPENDS refii_package)

    if(REFII_GODOT_EXECUTABLE)
        message(STATUS "Refii: packaging with the Godot at ${REFII_GODOT_EXECUTABLE}")
    else()
        message(STATUS
            "Refii: packaging will fetch the pinned Godot into ${REFII_GODOT_DIR} "
            "(set REFII_GODOT_EXECUTABLE to use your own)")
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
