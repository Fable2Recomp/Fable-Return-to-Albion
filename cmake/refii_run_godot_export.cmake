# Exports, stages and zips one platform/architecture via Refii.BuildCache.
#
# Preserves every contract of the release packager (unit-test gate, Godot import + release
# export + .NET assembly verification, native closure + Zig + CRT/strip + modding SDK + licence
# staging, orphan pruning, and forbidden-artefact checks) while making each phase incremental,
# content-addressed, and multi-core parallel. Set REFII_NO_BUILD_CACHE=1 in the environment to
# force an uncached full run at any time.

cmake_minimum_required(VERSION 3.24)

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

get_filename_component(_repo_root "${CMAKE_CURRENT_LIST_DIR}/.." ABSOLUTE)

# Fetch the pinned Godot editor on demand if REFII_GODOT_EXECUTABLE was left empty.
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
        "It must be the MONO/.NET build - the standard build cannot export a C# project.")
endif()

if(NOT EXISTS "${STAGING_ROOT}/bin")
    message(FATAL_ERROR
        "No staged natives at ${STAGING_ROOT}/bin.\n"
        "Build refii for this architecture first: the package needs the host bridge, the "
        "guest runtime and the player's build inputs beside the executable.")
endif()

find_program(_refii_dotnet NAMES dotnet)
if(NOT _refii_dotnet)
    message(FATAL_ERROR
        "dotnet was not found, so the unit tests and build-cache packager cannot run.")
endif()

# Ensure the pinned Zig toolchain is downloaded and unpacked in tools/cache/ without copying
# 19,546 files upfront; Refii.BuildCache mirrors or stamp-verifies it into _app/bin/recompile/zig.
set(_zig_unpacked_dir "")
if(BUNDLE_ZIG AND ZIG_SCRIPT AND EXISTS "${ZIG_SCRIPT}")
    include("${ZIG_SCRIPT}")
    refii_ensure_zig_cached("${TOOL_PLATFORM}" "${TOOL_ARCH}" "${TOOL_CACHE}" _zig_unpacked_dir)
endif()

set(_strip_tool "")
if(TOOL_PLATFORM STREQUAL "linux")
    find_program(_strip_tool NAMES llvm-strip strip)
    if(NOT _strip_tool)
        message(WARNING
            "No strip on this host (llvm-strip or binutils strip): the package ships the "
            "natives with their debug info, roughly four times the size.")
    endif()
endif()

# Prevent persistent MSBuild/Roslyn daemons from holding CMake execute_process pipes open.
set(ENV{MSBUILDDISABLENODEREUSE} "1")
set(ENV{DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER} "1")
set(ENV{UseSharedCompilation} "false")

# Build tools/build_cache/Refii.BuildCache.csproj only when missing or when its sources changed.
set(_build_tool_src_dir "${_repo_root}/tools/build_cache")
set(_build_tool_out_dir "${_repo_root}/out/cache/build-tool/bin")
set(_build_tool_dll "${_build_tool_out_dir}/Refii.BuildCache.dll")

set(_need_tool_build FALSE)
if(NOT EXISTS "${_build_tool_dll}")
    set(_need_tool_build TRUE)
else()
    file(GLOB _tool_sources
        "${_build_tool_src_dir}/*.cs"
        "${_build_tool_src_dir}/Refii.BuildCache.csproj")
    foreach(_src IN LISTS _tool_sources)
        if("${_src}" IS_NEWER_THAN "${_build_tool_dll}")
            set(_need_tool_build TRUE)
            break()
        endif()
    endforeach()
endif()

if(_need_tool_build)
    message(STATUS "Building Refii.BuildCache acceleration tool...")
    execute_process(
        COMMAND "${_refii_dotnet}" build "${_build_tool_src_dir}/Refii.BuildCache.csproj"
                -c Release -o "${_build_tool_out_dir}"
                -p:UseSharedCompilation=false -p:NodeReuse=false --disable-build-servers
                --nologo -v q
        RESULT_VARIABLE _tool_build_rc
    )
    if(NOT _tool_build_rc EQUAL 0 OR NOT EXISTS "${_build_tool_dll}")
        message(FATAL_ERROR "Failed to build Refii.BuildCache (exit ${_tool_build_rc}).")
    endif()
endif()

set(_bundle_zig_arg "0")
if(BUNDLE_ZIG)
    set(_bundle_zig_arg "1")
endif()

execute_process(
    COMMAND "${_refii_dotnet}" "${_build_tool_dll}" package-release
            "--repo-root=${_repo_root}"
            "--project-dir=${PROJECT_DIR}"
            "--staging-root=${STAGING_ROOT}"
            "--work-dir=${WORK_DIR}"
            "--app-name=${APP_NAME}"
            "--out-binary=${OUT_BINARY}"
            "--preset=${PRESET}"
            "--zip-path=${ZIP_PATH}"
            "--godot-bin=${GODOT}"
            "--godot-dir=${GODOT_DIR}"
            "--tool-platform=${TOOL_PLATFORM}"
            "--tool-arch=${TOOL_ARCH}"
            "--bundle-zig=${_bundle_zig_arg}"
            "--zig-unpacked-dir=${_zig_unpacked_dir}"
            "--crt-dirs=${CRT_DIRS}"
            "--objdump=${OBJDUMP}"
            "--strip=${_strip_tool}"
            "--godot-floor=${REFII_GODOT_FLOOR}"
            "--godot-flavour=${REFII_GODOT_FLAVOUR}"
            "--dotnet-major=${REFII_DOTNET_MAJOR}"
    RESULT_VARIABLE _pkg_rc
)

if(NOT _pkg_rc EQUAL 0 OR NOT EXISTS "${ZIP_PATH}")
    message(FATAL_ERROR "Packaging failed (exit ${_pkg_rc}).")
endif()
