# refii - ReXGlue Recompiled Project
#
# This file is yours to edit. 'rexglue migrate' will NOT overwrite it.
# SDK boilerplate lives in generated/rexglue.cmake.

cmake_minimum_required(VERSION 3.25)
project(refii LANGUAGES CXX)

if(POLICY CMP0144)
  cmake_policy(SET CMP0144 NEW)
endif()

if(WIN32)
    add_compile_definitions(NOMINMAX WIN32_LEAN_AND_MEAN)
endif()

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(CMAKE_CONFIGURATION_TYPES)
    set(_REFII_DEFAULT_PACKAGE_OUTPUT_DIR "${CMAKE_BINARY_DIR}/$<CONFIG>")
else()
    set(_REFII_DEFAULT_PACKAGE_OUTPUT_DIR "${CMAKE_BINARY_DIR}")
endif()
set(REFII_PACKAGE_OUTPUT_DIR
    "${_REFII_DEFAULT_PACKAGE_OUTPUT_DIR}"
    CACHE STRING
    "Directory containing sibling bin and lib folders for runnable Refii outputs"
    FORCE
)
set(REFII_RUNTIME_OUTPUT_DIR
    "${REFII_PACKAGE_OUTPUT_DIR}/bin"
    CACHE STRING
    "Directory where runnable refii development executables are emitted"
    FORCE
)
set(REFII_LIBRARY_OUTPUT_DIR
    "${REFII_PACKAGE_OUTPUT_DIR}/lib"
    CACHE STRING
    "Directory where Refii runtime libraries and import/static libraries are emitted"
    FORCE
)
set(REFII_RUNTIME_DATA_DIR "${REFII_RUNTIME_OUTPUT_DIR}/assets")
set(REFII_LONG_PATHS_MANIFEST
    "${CMAKE_CURRENT_SOURCE_DIR}/packaging/windows/refii_long_paths.manifest")
function(refii_embed_long_paths_manifest target)
    if(NOT WIN32 OR MSVC)
        return()
    endif()
    target_link_options(${target} PRIVATE
        "LINKER:/MANIFEST:EMBED"
        "LINKER:/MANIFESTINPUT:${REFII_LONG_PATHS_MANIFEST}")
endfunction()

option(REFII_STAGE_GODOT_BUILD "Stage the runtime and native DLLs into Godot/build" ON)

option(REFII_WINDOWS_INSTALL_INPUTS_ONLY
       "Build the Windows Godot install inputs without game-derived sources" OFF)
if(REFII_WINDOWS_INSTALL_INPUTS_ONLY AND NOT WIN32)
    message(FATAL_ERROR "REFII_WINDOWS_INSTALL_INPUTS_ONLY is Windows-only")
endif()

# OFF by default because it needs a Godot binary AND a real rendering driver — the dummy renderer
# under --headless compiles nothing and passes everything. Turn it on for any build you intend to
# run: a shader that fails to compile is silent apart from the run log, and the meshes using it
# render with the default material.
option(REFII_CHECK_SHADERS "Compile every .gdshader as part of the build" OFF)

# TRACY'S CAPTURE TOOL — the other half of the profiler we already emit into.
#
# The runtime links Tracy's CLIENT and every frame phase opens a zone, but a client with nothing
# connected records nothing; `tracy-capture` is what turns a running game into a .tracy file.
#
# SELF-CONTAINED, so it cannot collide with the client: `capture/CMakeLists.txt` includes
# Tracy's own server and vendor cmake and defines TracyServer and TracyGetOpt itself.
#
# OFF by default — it is a developer instrument, it builds a C++20 server, and it has no place
# in a shipped package.
option(REFII_BUILD_TRACY_CAPTURE "Build tracy-capture for pulling profiles from a run" OFF)
if(REFII_BUILD_TRACY_CAPTURE)
    set(_refii_tracy_capture "${REFII_REXGLUE_SDK_SOURCE_DIR}/thirdparty/tracy/capture")
    if(EXISTS "${_refii_tracy_capture}/CMakeLists.txt")
        add_subdirectory("${_refii_tracy_capture}" "${CMAKE_BINARY_DIR}/tracy-capture" EXCLUDE_FROM_ALL)
        message(STATUS "refii: tracy-capture will build from ${_refii_tracy_capture}")
    else()
        message(WARNING "refii: REFII_BUILD_TRACY_CAPTURE is on but ${_refii_tracy_capture} is missing")
    endif()
endif()

# DEV ONLY. Lets a dev tree run the game code the CMake build just produced instead of making
# the launcher recompile it from the installed image first — a ~5 minute wait between a source
# change and seeing it, paid on every iteration.
#
# It writes a marker beside the staged SDK; the launcher honours it ONLY when a module is
# already present, and says so loudly in its log so a run can never be mistaken for a player's.
# The marker is excluded from a package and the export additionally refuses to zip a tree that
# contains either it or the module, so it cannot escape a dev machine.
#
# OFF by default and it must stay that way: the whole point of install-time codegen is that the
# module is built from the player's OWN game image, and a module built here is built from this
# machine's.
option(REFII_DEV_SKIP_GAMECODE_REBUILD
       "DEV ONLY: let the launcher use the CMake-built game module instead of rebuilding" OFF)

# THE PAYLOAD IS THE LAUNCHER'S. It runs codegen and compiles the module with zig from the
# player's own image on every install, so this build never needs generated/ at all. ON compiles
# whatever generated/ holds into librefii_generated as a dev instrument (it is what
# REFII_DEV_SKIP_GAMECODE_REBUILD hands the launcher); it requires a generated/sources.cmake
# produced by the SAME SDK version as rexglue-net/, or the recomp TUs will not compile.
option(REFII_BUILD_GENERATED_PAYLOAD
       "DEV ONLY: compile generated/ into librefii_generated here instead of leaving it to the launcher" OFF)

# Staging is per-architecture, because Godot exports one build per architecture and each
# needs its OWN native closure. With a single Godot/build the x86_64 and ARM64 builds
# overwrite each other's DLLs, so only whichever ran last could ever be exported — and the
# breakage is silent, because the file names are identical.
#
# Godot's export presets pull from the matching root, and Engine_NativeLibraries prefers
# the subdirectory for the architecture the process is actually running as.
if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64|ARM64)$")
    set(REFII_GODOT_ARCH "arm64")
elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|amd64|AMD64)$")
    set(REFII_GODOT_ARCH "x86_64")
else()
    set(REFII_GODOT_ARCH "${CMAKE_SYSTEM_PROCESSOR}")
endif()
set(REFII_GODOT_BUILD_DIR "${CMAKE_SOURCE_DIR}/Godot/build/${REFII_GODOT_ARCH}")
set(REFII_GODOT_BUILD_BIN_DIR "${REFII_GODOT_BUILD_DIR}/bin")
message(STATUS "Refii: Godot staging root is Godot/build/${REFII_GODOT_ARCH}")
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}")
unset(_REFII_DEFAULT_PACKAGE_OUTPUT_DIR)
set(REFII_PACKAGE_GENERATED_DIR "${CMAKE_BINARY_DIR}/package/generated")
set(REFII_PACKAGE_STAMP_DIR "${CMAKE_BINARY_DIR}/package/stamps")

include(CMakeDependentOption)
set(REFII_SDL3_WINDOW_SUPPORTED OFF)
if((UNIX AND NOT APPLE) OR WIN32)
    set(REFII_SDL3_WINDOW_SUPPORTED ON)
endif()
if(NOT REFII_SDL3_WINDOW_SUPPORTED)
    message(FATAL_ERROR "Refii now requires SDL3 window support on this platform")
endif()
if(DEFINED REFII_RENDERER_PLUME_SDL_VULKAN_ENABLED AND
   NOT DEFINED REFII_SDL3_WINDOW_ENABLED)
    set(REFII_SDL3_WINDOW_ENABLED
        "${REFII_RENDERER_PLUME_SDL_VULKAN_ENABLED}"
        CACHE BOOL
        "Use SDL3 for the ReXGlue app window"
        FORCE
    )
endif()
option(
    REFII_SDL3_WINDOW_ENABLED
    "Use SDL3 for the ReXGlue app window"
    ON
)
if(NOT REFII_SDL3_WINDOW_ENABLED)
    message(FATAL_ERROR "Refii now requires REFII_SDL3_WINDOW_ENABLED=ON")
endif()
set(REFII_RENDERER_PLUME_SDL_VULKAN_ENABLED
    "${REFII_SDL3_WINDOW_ENABLED}"
    CACHE BOOL
    "Deprecated compatibility alias for REFII_SDL3_WINDOW_ENABLED"
    FORCE
)
mark_as_advanced(REFII_RENDERER_PLUME_SDL_VULKAN_ENABLED)
cmake_dependent_option(
    REFII_RENDERER_PLUME_D3D12_AGILITY_SDK_ENABLED
    "Build Plume with the D3D12 Agility SDK"
    OFF
    WIN32
    OFF
)

include(cmake/refii_rexglue.cmake)
include(cmake/refii_vcpkg.cmake)
include(cmake/refii_payload_sdk.cmake)
include(cmake/refii_godot_export.cmake)
include(cmake/refii_optimize_rexglue.cmake)
refii_rexglue_configure_sdk()
if(NOT REFII_WINDOWS_INSTALL_INPUTS_ONLY)
    refii_rexglue_generate_sources_if_missing()
endif()
include(generated/rexglue.cmake)
add_subdirectory(src/achievement)
refii_rexglue_apply_shadow_overrides()

# Immediately after the SDK's targets exist: the guest's runtime stays optimised whatever
# configuration refii is built in, matching the recompiled payload. It is also what gets
# staged for the install-time build and shipped, and a player should never receive a debug
# runtime.
refii_optimize_rexglue_targets("${CMAKE_BINARY_DIR}/rexglue-sdk-shadow")

# Bink VIDEO decoding, for playing the game's .bik files in Godot without the guest.
#
# rexglue builds FFmpeg audio-only (XMA/WMA Pro), so the Bink decoder is present in
# source but compiled out. Enabling it needs three things: the config flag (see
# cmake/ffmpeg-bink/config.h, prepended to the include path so it wins over the SDK's),
# the decoder itself, and the DSP helpers it calls into — blockdsp and hpeldsp are not
# in the audio subset because nothing audio-only needs them.
#
# The decoder is reached directly through `ff_bink_decoder` rather than by registering it
# in the SDK's generated codec_list.c, which would mean editing a pristine generated file
# for no gain: avcodec_open2 takes the AVCodec outright.
if(TARGET libavcodec)
    set(_refii_ffmpeg_dir "${REFII_REXGLUE_SDK_SOURCE_DIR}/thirdparty/FFmpeg")
    target_include_directories(libavcodec BEFORE PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/cmake/ffmpeg-bink"
    )
    target_sources(libavcodec PRIVATE
        "${_refii_ffmpeg_dir}/libavcodec/bink.c"
        "${_refii_ffmpeg_dir}/libavcodec/binkdsp.c"
        "${_refii_ffmpeg_dir}/libavcodec/blockdsp.c"
        "${_refii_ffmpeg_dir}/libavcodec/hpeldsp.c"
    )
    message(STATUS "Refii: enabled the FFmpeg Bink video decoder")
endif()

# Must come AFTER the block above: it links libavcodec, which only carries the Bink
# decoder once those sources have been added to it.
add_subdirectory(src/binkdecode)

# XMA1/XMA2 audio for Godot. No source list of its own: wmaprodec.c is already compiled
# for WMA Pro and defines ff_xma1_decoder/ff_xma2_decoder, which cmake/ffmpeg-bink's
# config override enables.
add_subdirectory(src/xmadecode)
refii_define_rexglue_build_target()
if(NOT REFII_WINDOWS_INSTALL_INPUTS_ONLY)
    refii_define_rexglue_codegen_bootstrap_target()
endif()

# Must stay above the bootstrap return() below: rexcore's memory.cpp uses
# SSSE3 intrinsics unconditionally, so the bootstrap build (which compiles
# the SDK to produce the rexglue codegen tool) needs these flags too.
if(CMAKE_SYSTEM_PROCESSOR MATCHES "x86_64|AMD64|amd64")
    # rexgraphics is the stable-tree target; the netplay SDK names it rexgpu-xenos.
    foreach(_refii_rex_simd_target IN ITEMS rexcore rexaudio rexgraphics rexgpu-xenos)
        if(TARGET "${_refii_rex_simd_target}")
            target_compile_options("${_refii_rex_simd_target}" PRIVATE -mssse3)
        endif()
    endforeach()
    unset(_refii_rex_simd_target)
endif()

function(refii_stage_windows_runtime_dlls target_name)
    if(NOT WIN32 OR NOT TARGET "${target_name}")
        return()
    endif()

    add_custom_command(TARGET "${target_name}" POST_BUILD
        COMMAND "$<$<BOOL:$<TARGET_RUNTIME_DLLS:${target_name}>>:${CMAKE_COMMAND};-E;copy_if_different;$<TARGET_RUNTIME_DLLS:${target_name}>;$<TARGET_FILE_DIR:${target_name}>>"
        COMMAND_EXPAND_LISTS
        VERBATIM
    )

    # STAGED ONE TOOL AT A TIME, because every tool stages into the SAME directory
    # (REFII_TOOL_OUTPUT_DIR) and several of them stage the SAME dll -- both extractor tools
    # link refii_bnk_extractor, so both copy refii_bnk_extractor.dll there. Ninja links tools
    # in parallel and a POST_BUILD command belongs to its target's edge, so two
    # `cmake -E copy_if_different` calls open one destination at once; Windows refuses the
    # second with a sharing violation, which surfaces as "Permission denied" and fails the
    # build. POSIX lets both writes through, so only Windows ever sees this.
    #
    # Chaining each registered tool behind the previous one serialises the copies without
    # serialising anything else. It costs the parallelism of ~5 small tool links.
    get_property(_refii_staged_predecessor GLOBAL PROPERTY REFII_WINDOWS_RUNTIME_DLL_LAST_TARGET)
    if(_refii_staged_predecessor)
        add_dependencies("${target_name}" "${_refii_staged_predecessor}")
    endif()
    set_property(GLOBAL PROPERTY REFII_WINDOWS_RUNTIME_DLL_LAST_TARGET "${target_name}")
endfunction()

function(refii_stage_d3d12_agility_runtime target_name)
    if(NOT WIN32 OR NOT TARGET "${target_name}")
        return()
    endif()

    if(NOT REFII_RENDERER_PLUME_D3D12_AGILITY_SDK_ENABLED)
        return()
    endif()

    if(NOT EXISTS "${REFII_D3D12_AGILITY_CORE_DLL}" OR
       NOT EXISTS "${REFII_D3D12_AGILITY_CORE_DEBUG_DLL}" OR
       NOT EXISTS "${REFII_D3D12_AGILITY_LAYERS_DLL}")
        message(FATAL_ERROR
            "D3D12 Agility SDK runtime DLLs are missing. Expected:\n"
            "  ${REFII_D3D12_AGILITY_CORE_DLL}\n"
            "  ${REFII_D3D12_AGILITY_CORE_DEBUG_DLL}\n"
            "  ${REFII_D3D12_AGILITY_LAYERS_DLL}"
        )
    endif()

    add_custom_command(TARGET "${target_name}" POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory
            "$<TARGET_FILE_DIR:${target_name}>/D3D12"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<IF:$<CONFIG:Debug>,${REFII_D3D12_AGILITY_CORE_DEBUG_DLL},${REFII_D3D12_AGILITY_CORE_DLL}>"
            "$<TARGET_FILE_DIR:${target_name}>/D3D12/D3D12Core.dll"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${REFII_D3D12_AGILITY_LAYERS_DLL}"
            "$<TARGET_FILE_DIR:${target_name}>/D3D12/d3d12SDKLayers.dll"
        VERBATIM
    )
endfunction()

option(REFII_BUILD_NATIVE_LUA "Build the native Lua runtime and Lua tools" ON)
option(REFII_BUILD_SHADER_PIPELINE "Build shader SBK extraction/compilation pipeline tooling" ON)

add_subdirectory(src/extractor)
add_subdirectory(src/modding)
if(REFII_BUILD_NATIVE_LUA)
    enable_language(C)
    add_subdirectory(src/lua)
endif()
if(REFII_BUILD_SHADER_PIPELINE)
    add_subdirectory(src/shader)
endif()

set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
    "${CMAKE_CURRENT_SOURCE_DIR}/generated/sources.cmake"
)
if(REFII_BUILD_GENERATED_PAYLOAD AND
   NOT EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/generated/sources.cmake" AND
   NOT REFII_WINDOWS_INSTALL_INPUTS_ONLY)
    message(STATUS
        "generated/sources.cmake is missing; configuring bootstrap build. "
        "Build once to build ReXGlue and generate sources, then CMake can "
        "regenerate with the full Refii source list."
    )
    return()
endif()
if(REFII_WINDOWS_INSTALL_INPUTS_ONLY)
    message(STATUS
        "Refii: configuring Windows install inputs without game-derived sources")
endif()
if(TARGET TracyClient AND NOT TARGET rex::TracyClient)
    add_library(rex::TracyClient ALIAS TracyClient)
endif()

if(TARGET rexgpu-xenos)
    # The netplay SDK builds the Xenos GPU plugin with hidden visibility,
    # exporting only its plugin interface. Refii's Plume frontend subclasses
    # GraphicsSystem/CommandProcessor directly, so it needs the full surface.
    set_target_properties(rexgpu-xenos PROPERTIES
        CXX_VISIBILITY_PRESET default
        VISIBILITY_INLINES_HIDDEN OFF
    )
endif()

if(TARGET SPIRV-Tools-static AND NOT TARGET rex::SPIRV-Tools-static)
    add_library(rex::SPIRV-Tools-static ALIAS SPIRV-Tools-static)
endif()

function(refii_apply_origin_rpath target_name)
    if(UNIX AND NOT APPLE AND TARGET "${target_name}")
        get_target_property(_refii_rpath_target_type "${target_name}" TYPE)
        if(_refii_rpath_target_type STREQUAL "EXECUTABLE")
            set(_refii_rpath "\$ORIGIN;\$ORIGIN/../lib;\$ORIGIN/lib")
        else()
            set(_refii_rpath "\$ORIGIN;\$ORIGIN/lib")
        endif()
        set_target_properties("${target_name}" PROPERTIES
            BUILD_RPATH "${_refii_rpath}"
            INSTALL_RPATH "${_refii_rpath}"
            BUILD_RPATH_USE_ORIGIN ON
            BUILD_WITH_INSTALL_RPATH ON
            INSTALL_RPATH_USE_LINK_PATH OFF
        )
        unset(_refii_rpath)
        unset(_refii_rpath_target_type)
    endif()
endfunction()

function(refii_append_stage_library_command out_var target_name destination_dir)
    if(NOT TARGET "${target_name}")
        return()
    endif()

    get_target_property(_refii_stage_target_type "${target_name}" TYPE)
    if(_refii_stage_target_type STREQUAL "INTERFACE_LIBRARY")
        return()
    endif()

    get_target_property(_refii_stage_target_imported "${target_name}" IMPORTED)
    set(_refii_stage_library_path "")
    if(_refii_stage_target_imported)
        if(CMAKE_BUILD_TYPE)
            string(TOUPPER "${CMAKE_BUILD_TYPE}" _refii_stage_target_config)
            get_target_property(
                _refii_stage_library_path
                "${target_name}"
                "IMPORTED_LOCATION_${_refii_stage_target_config}"
            )
            unset(_refii_stage_target_config)
        endif()
        if(NOT _refii_stage_library_path)
            get_target_property(_refii_stage_library_path "${target_name}" IMPORTED_LOCATION)
        endif()
    else()
        set(_refii_stage_library_path "$<TARGET_FILE:${target_name}>")
    endif()

    if(_refii_stage_library_path)
        list(APPEND ${out_var}
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${_refii_stage_library_path}"
                "${destination_dir}"
        )
        set(${out_var} "${${out_var}}" PARENT_SCOPE)
    endif()
endfunction()

set(REFII_TARGET_SUPPORTS_SSE41 OFF)
if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|amd64|AMD64|i[3-6]86)$")
    set(REFII_TARGET_SUPPORTS_SSE41 ON)
endif()

function(refii_apply_recompiled_module_compile_options target_name)
    if(NOT TARGET "${target_name}")
        return()
    endif()

    target_compile_options("${target_name}"
        PRIVATE
            $<$<CXX_COMPILER_ID:MSVC>:/EHa>
    )

    if(REFII_TARGET_SUPPORTS_SSE41)
        target_compile_options("${target_name}"
            PRIVATE
                $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-msse4.1>
        )
    endif()
endfunction()

function(refii_apply_generated_payload_compile_options target_name)
    if(NOT TARGET "${target_name}")
        return()
    endif()

    refii_apply_recompiled_module_compile_options("${target_name}")

    if(UNIX AND NOT APPLE)
        target_compile_options("${target_name}" PRIVATE -mcmodel=large)
    endif()

    # The generated code logs too — ppc_trap is the one function in it that does, and it is
    # what pulls spdlog and fmt into the payload. Same bridge as the hooks: see the shadowed
    # rex/logging/macros.h.
    target_compile_definitions("${target_name}" PRIVATE REFII_PAYLOAD_LOG_BRIDGE=1)

    # fmt LINKED IN, not imported. The bridge formats payload-side, so fmt has to resolve
    # inside the module rather than crossing to the host — it is a pure formatter with no
    # shared state, so a private copy is safe in a way a second spdlog registry would not be.
    if(TARGET fmt::fmt)
        target_link_libraries("${target_name}" PRIVATE fmt::fmt)
    endif()

    # Same as the hooks: the profiler is not part of a shipped payload, and TRACY_ENABLE
    # comes in as an interface define that only -U can undo.
    if(NOT MSVC)
        target_compile_options("${target_name}" PRIVATE "-UTRACY_ENABLE" "-UTRACY_MANUAL_LIFETIME" "-UTRACY_DELAYED_INIT" "-UTRACY_ON_DEMAND" "-UTRACY_FIBERS" "-UTRACY_IMPORTS" "-UREXGLUE_ENABLE_PROFILING")
    endif()
endfunction()

set(REFII_PACKAGE_EXTERNAL_LIB_TARGETS
    rex::runtime
    # Netplay-tree GPU plugin; skipped via the TARGET checks on the stable tree.
    rex::gpu-xenos
    rex::TracyClient
    fmt::fmt
    spdlog::spdlog
    SDL3::SDL3-static
    volk::volk
    rex::SPIRV-Tools-static
    # Despite the name, this is also used for Linux Vulkan/SPIR-V shader compilation today.
    Microsoft::DirectXShaderCompiler
    Microsoft::DXIL
)

option(REFII_BUILD_NATIVE_HOOKS "Build native hook sources into refii_generated" ON)
option(REFII_BUILD_VFS_HOOKS "Build the native VFS runtime and hook sources" ON)
# The neural texture upscaler (§8).
#
# OFF HERE BECAUSE IT DOES NOT BELONG HERE. librefii_upscaler links ncnn and libc and nothing
# else — no ReXGlue, no guest, no renderer — so it is a Godot-side texture tool that happens to
# live in this repo. It is built in its own directory (out/build/upscaler) and stages itself
# into Godot/build/<arch>/bin; see src/upscaler/CMakeLists.txt.
#
# Turning it ON here also fails outright: ncnn vendors its own glslang and the ReXGlue SDK
# already defines SPIRV, glslang and OSDependent, so configure dies on duplicate targets. That
# collision is a reason not to merge the two builds, not a problem to solve.
option(REFII_BUILD_UPSCALER "Build the Real-ESRGAN texture upscaler (ncnn + Vulkan)" OFF)

# The C ABI Godot P/Invokes to host the guest in its own process, rather than spawning
# `refii` as a child. Cheap to build and it links only the runtime.
option(REFII_BUILD_HOST_BRIDGE "Build the Godot host bridge over the guest runtime" ON)
option(REFII_BUILD_TOOLS
    "Build optional lightweight test tool executables (bnk extractor, decompiler, shader utilities)"
    ON
)

option(REFII_BUILD_EXTRACTOR
    "Build the XISO extractor shared library (and tool dependencies)"
    ON
)

if(UNIX AND NOT APPLE)
    set(REFII_CAN_BUILD_LINUX_PACKAGES ON)
endif()


set(
    _REFII_PACKAGE_ICON_SOURCE_DEFAULT
    "${CMAKE_SOURCE_DIR}/assets/game/default.xex.icons/067_X_IMAGEID_GAME.png"
)
set(
    _REFII_PACKAGE_ICON_SOURCE_LEGACY_DEFAULT
    "${CMAKE_SOURCE_DIR}/assets/game/default.xex.icons/001_Icon_black_knight.png"
)
set(
    REFII_PACKAGE_ICON_SOURCE
    "${_REFII_PACKAGE_ICON_SOURCE_DEFAULT}"
    CACHE FILEPATH
    "PNG icon used for AppImage and Flatpak packaging"
)
set(
    REFII_DEFAULT_XEX_SOURCE
    "${CMAKE_SOURCE_DIR}/assets/game/default.xex"
    CACHE FILEPATH
    "default.xex used to generate launcher and package icons"
)
if("${REFII_PACKAGE_ICON_SOURCE}" STREQUAL "${_REFII_PACKAGE_ICON_SOURCE_LEGACY_DEFAULT}")
    set(
        REFII_PACKAGE_ICON_SOURCE
        "${_REFII_PACKAGE_ICON_SOURCE_DEFAULT}"
        CACHE FILEPATH
        "PNG icon used for AppImage and Flatpak packaging"
        FORCE
    )
endif()
set(REFII_PACKAGE_ICON_AVAILABLE OFF)
if(EXISTS "${REFII_PACKAGE_ICON_SOURCE}")
    set(REFII_PACKAGE_ICON_AVAILABLE ON)
endif()
set(REFII_PACKAGE_ICON_TARGET)


# The shader pipeline's input/output/workspace/translator cache knobs used to
# live here, from when CMake drove extraction and translation.  It no longer
# does -- the LAUNCHER owns that pipeline end to end (see the note below) -- so
# every one of them had lost its consumer and they are gone.  The build's only
# remaining shader jobs are compiling the tools and seeding the launcher's
# default target list.

set(REFII_TOOL_OUTPUT_DIR
    "${REFII_PACKAGE_OUTPUT_DIR}/tools"
    CACHE STRING
    "Output directory for optional lightweight tool executables"
    FORCE
)
if(APPLE)
    set(REFII_SHADER_DEFAULT_VALIDATION_TARGETS "vulkan,metal")
elseif(WIN32)
    set(REFII_SHADER_DEFAULT_VALIDATION_TARGETS "vulkan,d3d12")
else()
    # BY DESIGN (project owner, 2026-07-26): Linux is Vulkan-only — there is no
    # D3D12 backend to load a DXIL artifact — so the coverage bar must not
    # demand one.  Holding Linux to a d3d12 target would fail the build over a
    # missing artifact this platform is never meant to produce.
    #
    # This mirrors BuildDefaultTranslateTargets() in
    # src/shader/src/shader_sbk_pipeline.cpp; the two must agree, or the
    # producer and the gate disagree about what "complete" means.
    set(REFII_SHADER_DEFAULT_VALIDATION_TARGETS "vulkan")
endif()
set(REFII_SHADER_VALIDATION_TARGETS
    "${REFII_SHADER_DEFAULT_VALIDATION_TARGETS}"
    CACHE STRING
    "Backend targets whose translated shader set must fully cover the extracted blobs (e.g. vulkan, vulkan,d3d12, or vulkan,metal)"
)
unset(REFII_SHADER_DEFAULT_VALIDATION_TARGETS)
# NOTE (project owner, 2026-07-26): there is deliberately no build-time shader
# coverage gate here.  Shader generation is the LAUNCHER's job -- CMake builds
# the tools, it does not produce shader artifacts and must never fail a
# compile over their state.  The equivalent check is native and runs where the
# artifacts are actually owned:
#   * refii::shader::VerifyTranslatedShaderCoverage()
#     (src/shader/src/shader_sbk_pipeline.cpp)
#   * refii-shader-sbk CLI       -> src/shader/src/refii_shader_sbk_cli_main.cpp
# REFII_SHADER_VALIDATION_TARGETS above survives because it seeds a compile-time default
# target list, not because anything in the build validates against it.

set(REFII_VFS_LIBRARIES)
set(REFII_RENDERER_LIBRARIES)
set(REFII_HOOK_OBJECTS)

# OFF: the Fable2AssetBrowser submodule is reference source, not part of the game build.
# Its XMA decoder — the one thing Godot actually needed from it — is now served by
# src/xmadecode via FFmpeg. What remains is the .mdl -> glTF converter, which its own
# shadow/assetbrowser-sdk/abi/README.md still describes as "not yet buildable in-tree".
#
# It also does not currently compile with clang on ANY architecture: wmaprodata.h is
# #included inside `namespace Xma { namespace { ... } }` and pulls in <stddef.h>, whose
# clang version declares `namespace std { ... nullptr_t; }` — so a nested
# Xma::(anonymous namespace)::std swallows every later std::array/std::once_flag (20
# errors, verified identical on x86_64 and ARM64). No compile flag works around it; the
# fix belongs upstream, moving that include out of the namespace.
option(REFII_BUILD_ASSETBROWSER
       "Build the Fable2AssetBrowser native library (reference; see comment above)" OFF)
if(REFII_BUILD_ASSETBROWSER)
    add_subdirectory(src/assetbrowser)
endif()

if(REFII_BUILD_UPSCALER)
    add_subdirectory(src/upscaler)
endif()

if(TARGET refii-default-xex-icon-extractor
        AND EXISTS "${REFII_DEFAULT_XEX_SOURCE}"
        AND "${REFII_PACKAGE_ICON_SOURCE}" STREQUAL "${_REFII_PACKAGE_ICON_SOURCE_DEFAULT}")
    add_custom_command(
        OUTPUT "${REFII_PACKAGE_ICON_SOURCE}"
        COMMAND "$<TARGET_FILE:refii-default-xex-icon-extractor>"
            "${REFII_DEFAULT_XEX_SOURCE}"
        DEPENDS
            refii-default-xex-icon-extractor
            "${REFII_DEFAULT_XEX_SOURCE}"
        BYPRODUCTS
            "${CMAKE_SOURCE_DIR}/assets/game/default.xex.icons/manifest.tsv"
        COMMENT "Extracting default.xex icons"
        VERBATIM
    )
    add_custom_target(refii-extract-default-xex-icons
        DEPENDS "${REFII_PACKAGE_ICON_SOURCE}"
    )
    set(REFII_PACKAGE_ICON_AVAILABLE ON)
    set(REFII_PACKAGE_ICON_TARGET refii-extract-default-xex-icons)
endif()

# Before src/vfs and refii_core: both reach the recompiled guest through this loader
# rather than linking it, so that a release can be built on a machine that has no game
# image and therefore no payload.
add_subdirectory(src/payload)

if(REFII_BUILD_VFS_HOOKS)
    add_subdirectory(src/vfs)
endif()


if(TARGET refii_vfs AND TARGET refii_renderer)
    target_include_directories(refii_vfs
        PRIVATE
            ${REFII_RENDERER_INCLUDE_DIRS}
    )
    target_link_libraries(refii_vfs
        PRIVATE
            refii_renderer
    )
    target_compile_definitions(refii_vfs
        PRIVATE
            REFII_VFS_HAS_RENDERER_RUNTIME=1
    )
endif()

if(REFII_BUILD_NATIVE_HOOKS)
    add_subdirectory(src/hooks)
endif()

refii_rexglue_shadow_source(
    REFII_REX_APP_SOURCE
    "src/ui/rex_app.cpp"
)
set(REFII_CORE_SOURCES
    ${REFII_REX_APP_SOURCE}
)

# refii_core is the RexGlue runtime layer.
add_library(refii_core SHARED ${REFII_CORE_SOURCES})
target_compile_definitions(refii_core
    PRIVATE
        REXGLUE_BUILD_CONFIG="$<CONFIG>"
)
target_include_directories(refii_core
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}
        ${CMAKE_CURRENT_SOURCE_DIR}/src
        ${CMAKE_CURRENT_SOURCE_DIR}/generated
)
if(EXISTS "${REFII_REXGLUE_SHADOW_DIR}/include")
    target_include_directories(refii_core BEFORE PUBLIC "${REFII_REXGLUE_SHADOW_DIR}/include")
endif()
if(REXSDK_DIR AND EXISTS "${REXSDK_DIR}/thirdparty/imgui/imgui.h")
    target_include_directories(refii_core
        PRIVATE
            "${REXSDK_DIR}/thirdparty/imgui"
            "${CMAKE_BINARY_DIR}/rexglue-sdk/include"
    )
endif()
target_link_libraries(refii_core
    PUBLIC
        rex::runtime
)
set_target_properties(refii_core PROPERTIES
    OUTPUT_NAME refii_core
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
)
if(WIN32)
    set_target_properties(refii_core PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

if(REFII_BUILD_GENERATED_PAYLOAD AND NOT REFII_WINDOWS_INSTALL_INPUTS_ONLY)
    set(REFII_GENERATED_SOURCES
        ${GENERATED_SOURCES}
        ${REFII_HOOK_OBJECTS}
        # The host ABI entry point, compiled in here exactly as the install-time project
        # compiles it on the player's machine.
        ${REFII_PAYLOAD_EXPORT_SOURCE}
    )

# refii_generated is the recomp/codegen payload, and it is SHARED on every platform
# because it is built on the PLAYER'S machine from their own game image — a static
# payload cannot be replaced after the app ships.
#
# Windows was static here to avoid exporting tens of thousands of generated PPC symbols
# through an import lib. It no longer has to: nothing links this target. The host reaches
# it through refii_payload, which resolves the ~50 symbols our own code names, so the
# export list is that set plus one entry point rather than the whole image.
set(REFII_GENERATED_LIBRARY_TYPE SHARED)
add_library(refii_generated ${REFII_GENERATED_LIBRARY_TYPE} ${REFII_GENERATED_SOURCES})
target_include_directories(refii_generated
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}
        ${CMAKE_CURRENT_SOURCE_DIR}/src
        ${CMAKE_CURRENT_SOURCE_DIR}/generated
)
if(EXISTS "${REFII_REXGLUE_SHADOW_DIR}/include")
    target_include_directories(refii_generated BEFORE PUBLIC "${REFII_REXGLUE_SHADOW_DIR}/include")
endif()
target_link_libraries(refii_generated
    PUBLIC
        refii_core
    PRIVATE
        ${REFII_LUA_LIBRARIES}
        ${REFII_VFS_LIBRARIES}
        ${REFII_RENDERER_LIBRARIES}
)
set_target_properties(refii_generated PROPERTIES
    OUTPUT_NAME refii_generated
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)
# The recompiled guest payload is the ONE target where optimisation actually
# decides framerate. Measured 2026-07-28: with the observer removed from the hook
# path (per-call cost now microseconds), the present pump body down to 0.04ms and
# the compose on its own thread, the pump spends ~231ms per frame purely BLOCKED
# on the guest -- i.e. the guest thread executing this library is 100% of the
# frame. It is ~150k recompiled PPC functions, and at Debug's -O0 that is
# unoptimised throughout.
#
# This is scoped to refii_generated ALONE and deliberately does NOT change the
# build type: every hand-written target (renderer, host, vfs, lua) stays at -O0
# and fully debuggable, and there is no second build tree, so no extra disk
# space. -g is kept so the generated frames still resolve in backtraces and
# core dumps, which is how this project debugs hangs.
#
# COST: a full rebuild of this target at -O2 is SLOW (150k functions). It only
# pays that when the generated sources change, which is rare. Set
# REFII_OPTIMIZE_GENERATED=OFF to fall back to -O0 if a codegen bug needs
# line-exact stepping through guest code.
option(REFII_OPTIMIZE_GENERATED
       "Compile the recompiled guest payload with optimisation even in Debug builds"
       ON)
if(REFII_OPTIMIZE_GENERATED)
    if(MSVC)
        target_compile_options(refii_generated PRIVATE $<$<CONFIG:Debug>:/O2>)
    else()
        target_compile_options(refii_generated PRIVATE $<$<CONFIG:Debug>:-O2 -g>)
    endif()
    message(STATUS "refii_generated: optimised (-O2) even in Debug; set REFII_OPTIMIZE_GENERATED=OFF to disable")
endif()

# The generated header, precompiled — the single biggest cost in rebuilding this target.
#
# Every recomp TU includes generated/refii_pch.h (config, SDK headers and the function
# macros) plus its own per-file declaration header; refii_init.h is the full surface for
# the init/register TUs and our hooks. Precompiling the pch header takes a TU from 17.7s to
# 10.6s (clang 20, measured on the old single-header layout) with byte-identical objects.
# rexglue_apply_recomp_settings() also pins REXGLUE_RECOMP_OPTIONS on the target so the
# PCH is built in the same EH mode as the TUs that include it.
#
# Deliberately NOT applied to refii_hooks: those are our own sources with a different
# include set, and they are a handful of files rather than 148.
if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/generated/refii_pch.h" AND COMMAND rexglue_apply_recomp_settings)
    rexglue_apply_recomp_settings(refii_generated "${CMAKE_CURRENT_SOURCE_DIR}/generated/refii_pch.h")
    message(STATUS "refii_generated: precompiling generated/refii_pch.h (~40% faster rebuilds)")
endif()

unset(REFII_GENERATED_LIBRARY_TYPE)
else()
    # No payload built here: the launcher's install-time build takes its include list from
    # the hooks target, which carries the same payload-shaped include set.
    if(NOT TARGET refii_hooks)
        message(FATAL_ERROR
            "Payload input staging requires the refii_hooks target")
    endif()
    set(REFII_PAYLOAD_INCLUDE_TARGET refii_hooks)
endif()
add_library(refii_host STATIC
    src/refii_host.cpp
    # Godot-fed controller input. Lives here rather than in the host bridge because
    # refii_app.h installs it through RuntimeConfig::input_factory, and the bridge links
    # refii_host rather than the other way round.
    src/host_input.cpp
    # Godot-fed audio output, installed the same way through RuntimeConfig::audio_factory.
    # Its queue and counters live in refii_vfs, which the host bridge also links — the
    # bridge and this driver must see one ring, not one each.
    src/host_audio.cpp
)
set_target_properties(refii_host PROPERTIES
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    # Linked into refii_hostbridge, which is SHARED so Godot can P/Invoke it. Without
    # PIC the link fails on an R_X86_64_PC32 relocation a shared object cannot carry.
    POSITION_INDEPENDENT_CODE ON
)
target_include_directories(refii_host
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}
        ${CMAKE_CURRENT_SOURCE_DIR}/src
        ${CMAKE_CURRENT_SOURCE_DIR}/generated
        ${REFII_RENDERER_INCLUDE_DIRS}
)
if(EXISTS "${REFII_REXGLUE_SHADOW_DIR}/include")
    target_include_directories(refii_host PUBLIC "${REFII_REXGLUE_SHADOW_DIR}/include")
endif()
target_include_directories(refii_host
    PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/src/achievement/include"
)
if(REFII_RENDERER_LIBRARIES)
    target_compile_definitions(refii_host PUBLIC REFII_HAS_RENDERER_RUNTIME=1)
else()
    target_compile_definitions(refii_host PUBLIC REFII_HAS_RENDERER_RUNTIME=0)
endif()
target_compile_definitions(refii_host
    PUBLIC
        REFII_SDL3_WINDOW_ENABLED=$<BOOL:${REFII_SDL3_WINDOW_ENABLED}>
)
# refii_generated is deliberately absent: the payload is built on the player's machine,
# so linking it here would make a release unbuildable on any machine without a game
# image. refii_payload loads it by name instead.
target_link_libraries(refii_host
    PUBLIC
        refii_core
        refii_payload
        ${REFII_RENDERER_LIBRARIES}
)

# ReXGlue v0.10's rex/ui/imgui_drawer.h includes rex/ui/style.h, which includes imgui.h — so
# every TU that reaches rex_app.h needs imgui's headers. The include path only: imgui is an
# OBJECT library already compiled into rexui, and linking it here would duplicate its objects.
target_include_directories(refii_host PUBLIC
    $<TARGET_PROPERTY:imgui,INTERFACE_INCLUDE_DIRECTORIES>)

# host_audio.cpp keeps its ring in refii_vfs so the host bridge reads the same one. The
# link is PUBLIC because refii_vfs exports its include directories and the standalone
# `refii` executable compiles this translation unit too, so it needs the symbols even
# though only the embedded path ever constructs the driver.
if(TARGET refii_vfs)
    target_link_libraries(refii_host PUBLIC refii_vfs)
endif()

# AFTER refii_host: the bridge stages the whole dependency closure into Godot/build/bin,
# and `if(TARGET ...)` only sees targets already defined. Added earlier, it silently
# staged a partial set — the bridge without the recompiled guest behind it.
if(REFII_BUILD_HOST_BRIDGE)
    add_subdirectory(src/hostbridge)
endif()

# The renderer was the one host-side library that called guest functions by name, so it linked
# the payload directly. It was retired 2026-08-18 (archive/renderer-*.zip) and the guard is kept
# only so a restored src/renderer still links: everything in the shipping configuration reaches
# the payload through refii_payload.
if(TARGET refii_renderer AND TARGET refii_generated)
    target_link_libraries(refii_renderer PUBLIC refii_generated)
endif()

# After every library the payload links: the staging target copies each one, and
# `if(TARGET ...)` only sees targets that already exist.
refii_stage_payload_sdk()


if(UNIX AND NOT APPLE)
    target_link_options(refii_core
        PRIVATE
            -Wl,--whole-archive
            $<TARGET_FILE:rex::runtime>
            -Wl,--no-whole-archive
            -Wl,--no-relax
    )
endif()

refii_apply_recompiled_module_compile_options(refii_core)
if(TARGET refii_generated)
    refii_apply_generated_payload_compile_options(refii_generated)
endif()
refii_apply_generated_payload_compile_options(refii_hooks)


if(TARGET refii-launcher AND TARGET refii-stage-external-libs)
    add_dependencies(refii-launcher refii-stage-external-libs)
endif()

foreach(_refii_origin_rpath_target
    refii
    refii-launcher
    refii_core
    refii_hostbridge
    refii_generated
    refii_bnk_extractor
    refii_lua
    refii_vfs
    refii_renderer
    refii-shader-sbk
    refii-shader-xenos-translator
    glfw
    XenosRecomp
)
    if(TARGET "${_refii_origin_rpath_target}")
        refii_apply_origin_rpath("${_refii_origin_rpath_target}")
    endif()
endforeach()
unset(_refii_origin_rpath_target)

if(UNIX AND NOT APPLE)
    find_program(REFII_LDD_EXECUTABLE
        NAMES ldd
        DOC "Path to ldd."
    )
endif()
file(MAKE_DIRECTORY "${REFII_PACKAGE_GENERATED_DIR}")
set(REFII_PACKAGE_STAGE_RUNTIME_DEPS_SCRIPT "${REFII_PACKAGE_GENERATED_DIR}/refii_stage_runtime_deps.cmake")
set(REFII_PACKAGE_STAGE_RUNTIME_DEPS_SCRIPT_CONTENT [=[
if(POLICY CMP0057)
  cmake_policy(SET CMP0057 NEW)
endif()
if(NOT DEFINED REFII_PACKAGE_EXECUTABLE_DIR)
  message(FATAL_ERROR "REFII_PACKAGE_EXECUTABLE_DIR is required")
endif()
if(NOT DEFINED REFII_PACKAGE_LIBRARY_DIR)
  message(FATAL_ERROR "REFII_PACKAGE_LIBRARY_DIR is required")
endif()
if(NOT EXISTS "@REFII_LDD_EXECUTABLE@")
  message(FATAL_ERROR "Runtime dependency staging requires ldd")
endif()
if(NOT DEFINED REFII_RUNTIME_SEARCH_DIRS)
  set(REFII_RUNTIME_SEARCH_DIRS "")
endif()
if(NOT DEFINED REFII_RUNTIME_EXCLUDE_REGEXES)
  set(REFII_RUNTIME_EXCLUDE_REGEXES "")
endif()
set(REFII_STAGE_ITEMS)
foreach(REFII_STAGE_NAME IN ITEMS refii refii-launcher refii-launcher.bin)
  if(EXISTS "${REFII_PACKAGE_EXECUTABLE_DIR}/${REFII_STAGE_NAME}")
    list(APPEND REFII_STAGE_ITEMS "${REFII_PACKAGE_EXECUTABLE_DIR}/${REFII_STAGE_NAME}")
  endif()
endforeach()
file(GLOB REFII_STAGE_LIBRARIES
  LIST_DIRECTORIES FALSE
  "${REFII_PACKAGE_LIBRARY_DIR}/*.so"
  "${REFII_PACKAGE_LIBRARY_DIR}/*.so.*")
list(APPEND REFII_STAGE_ITEMS ${REFII_STAGE_LIBRARIES})
set(REFII_LD_LIBRARY_PATH "${REFII_PACKAGE_LIBRARY_DIR}")
foreach(REFII_RUNTIME_SEARCH_DIR IN LISTS REFII_RUNTIME_SEARCH_DIRS)
  if(NOT REFII_RUNTIME_SEARCH_DIR STREQUAL "")
    string(APPEND REFII_LD_LIBRARY_PATH ":${REFII_RUNTIME_SEARCH_DIR}")
  endif()
endforeach()
set(REFII_PENDING_ITEMS ${REFII_STAGE_ITEMS})
set(REFII_SEEN_ITEMS)
set(REFII_SEEN_DEPENDENCIES)
while(REFII_PENDING_ITEMS)
  list(POP_FRONT REFII_PENDING_ITEMS REFII_CURRENT_ITEM)
  if(REFII_CURRENT_ITEM IN_LIST REFII_SEEN_ITEMS)
    continue()
  endif()
  list(APPEND REFII_SEEN_ITEMS "${REFII_CURRENT_ITEM}")
  execute_process(
    COMMAND "@CMAKE_COMMAND@" -E env "LD_LIBRARY_PATH=${REFII_LD_LIBRARY_PATH}" "@REFII_LDD_EXECUTABLE@" "${REFII_CURRENT_ITEM}"
    RESULT_VARIABLE REFII_LDD_RESULT
    OUTPUT_VARIABLE REFII_LDD_OUTPUT
    ERROR_VARIABLE REFII_LDD_ERROR
    OUTPUT_STRIP_TRAILING_WHITESPACE)
  if(NOT REFII_LDD_RESULT EQUAL 0)
    if(REFII_LDD_ERROR MATCHES "not a dynamic executable")
      continue()
    endif()
    message(FATAL_ERROR "ldd failed for ${REFII_CURRENT_ITEM}: ${REFII_LDD_ERROR}")
  endif()
  string(REPLACE "\n" ";" REFII_LDD_LINES "${REFII_LDD_OUTPUT}")
  foreach(REFII_LDD_LINE IN LISTS REFII_LDD_LINES)
    string(STRIP "${REFII_LDD_LINE}" REFII_LDD_LINE)
    if(REFII_LDD_LINE STREQUAL "")
      continue()
    endif()
    if(REFII_LDD_LINE MATCHES "^linux-vdso\\.so")
      continue()
    endif()
    if(REFII_LDD_LINE MATCHES "=> not found")
      message(FATAL_ERROR "Unresolved package runtime dependency for ${REFII_CURRENT_ITEM}: ${REFII_LDD_LINE}")
    endif()
    set(REFII_RESOLVED_DEPENDENCY "")
    if(REFII_LDD_LINE MATCHES "=> (/.+) \\(")
      set(REFII_RESOLVED_DEPENDENCY "${CMAKE_MATCH_1}")
    elseif(REFII_LDD_LINE MATCHES "^(/.+) \\(")
      set(REFII_RESOLVED_DEPENDENCY "${CMAKE_MATCH_1}")
    endif()
    if(REFII_RESOLVED_DEPENDENCY STREQUAL "")
      continue()
    endif()
    if(REFII_RESOLVED_DEPENDENCY MATCHES ".*/ld-linux[^/]*\\.so.*$")
      continue()
    endif()
    set(REFII_SKIP_RESOLVED_DEPENDENCY FALSE)
    foreach(REFII_RUNTIME_EXCLUDE_REGEX IN LISTS REFII_RUNTIME_EXCLUDE_REGEXES)
      if(NOT REFII_RUNTIME_EXCLUDE_REGEX STREQUAL "" AND
         REFII_RESOLVED_DEPENDENCY MATCHES "${REFII_RUNTIME_EXCLUDE_REGEX}")
        set(REFII_SKIP_RESOLVED_DEPENDENCY TRUE)
        break()
      endif()
    endforeach()
    if(REFII_SKIP_RESOLVED_DEPENDENCY)
      continue()
    endif()
    if(NOT EXISTS "${REFII_RESOLVED_DEPENDENCY}")
      message(FATAL_ERROR "Resolved runtime dependency does not exist: ${REFII_RESOLVED_DEPENDENCY}")
    endif()
    if(NOT REFII_RESOLVED_DEPENDENCY IN_LIST REFII_SEEN_DEPENDENCIES)
      list(APPEND REFII_SEEN_DEPENDENCIES "${REFII_RESOLVED_DEPENDENCY}")
      execute_process(
        COMMAND "@CMAKE_COMMAND@" -E copy_if_different "${REFII_RESOLVED_DEPENDENCY}" "${REFII_PACKAGE_LIBRARY_DIR}/"
        RESULT_VARIABLE REFII_COPY_RESULT)
      if(NOT REFII_COPY_RESULT EQUAL 0)
        message(FATAL_ERROR "Failed to copy runtime dependency: ${REFII_RESOLVED_DEPENDENCY}")
      endif()
      get_filename_component(REFII_RESOLVED_DEPENDENCY_NAME "${REFII_RESOLVED_DEPENDENCY}" NAME)
      if(REFII_RESOLVED_DEPENDENCY_NAME MATCHES "\\.so(\\..*)?$")
        list(APPEND REFII_PENDING_ITEMS "${REFII_PACKAGE_LIBRARY_DIR}/${REFII_RESOLVED_DEPENDENCY_NAME}")
      endif()
    endif()
  endforeach()
endwhile()
]=])
string(CONFIGURE "${REFII_PACKAGE_STAGE_RUNTIME_DEPS_SCRIPT_CONTENT}" REFII_PACKAGE_STAGE_RUNTIME_DEPS_SCRIPT_CONTENT @ONLY)
file(WRITE "${REFII_PACKAGE_STAGE_RUNTIME_DEPS_SCRIPT}" "${REFII_PACKAGE_STAGE_RUNTIME_DEPS_SCRIPT_CONTENT}")
unset(REFII_PACKAGE_STAGE_RUNTIME_DEPS_SCRIPT_CONTENT)
set(REFII_PACKAGE_RUNTIME_SEARCH_DIRS
    "${REFII_LIBRARY_OUTPUT_DIR}"
    "$<TARGET_FILE_DIR:rex::runtime>"
    "$<TARGET_FILE_DIR:rex::TracyClient>"
    "${CMAKE_SOURCE_DIR}/src/shader/thirdparty/XenosRecomp/thirdparty/dxc-bin/lib/x64"
)


# LAST, deliberately. The packaging target depends on every target that writes into
# Godot/build/<arch>, and `if(TARGET ...)` only sees targets that already exist — declared
# earlier it silently missed refii-stage-external-libs (defined further down) and would have
# packaged a build without them.
refii_define_godot_export_target()

if(REFII_WINDOWS_INSTALL_INPUTS_ONLY)
    foreach(_required_target refii_payload_sdk refii_stage_godot)
        if(NOT TARGET "${_required_target}")
            message(FATAL_ERROR
                "Windows install-input target is unavailable: ${_required_target}")
        endif()
    endforeach()

    add_custom_target(refii_windows_install_inputs
        COMMAND ${CMAKE_COMMAND}
            -DSTAGING_BIN=${REFII_GODOT_BUILD_BIN_DIR}
            -DTARGET_ARCH=${REFII_GODOT_ARCH}
            -DTOOL_CACHE=${REFII_TOOL_CACHE_DIR}
            -DZIG_SCRIPT=${CMAKE_SOURCE_DIR}/cmake/refii_fetch_zig.cmake
            -P "${CMAKE_SOURCE_DIR}/cmake/refii_finalize_windows_install_inputs.cmake"
        COMMENT "Preparing the Windows install-time game-code inputs"
        USES_TERMINAL
        VERBATIM
    )
    foreach(_dependency
            refii_payload_sdk
            refii_stage_godot
            refii_binkdecode
            refii_xmadecode
            refii-stage-external-libs)
        if(TARGET "${_dependency}")
            add_dependencies(refii_windows_install_inputs "${_dependency}")
        endif()
    endforeach()
    unset(_dependency)
    unset(_required_target)
endif()
