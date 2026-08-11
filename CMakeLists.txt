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
set(REFII_GODOT_BUILD_DIR "${CMAKE_SOURCE_DIR}/Godot/build")
set(REFII_GODOT_BUILD_BIN_DIR "${REFII_GODOT_BUILD_DIR}/bin")
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
# Visible to every refii target (renderer, host, ...) so sources can adapt to
# the API differences between the stable and netplay SDK trees.
add_compile_definitions(REFII_REXGLUE_NETPLAY=$<BOOL:${REFII_REXGLUE_NETPLAY}>)
refii_rexglue_configure_sdk()
refii_rexglue_generate_sources_if_missing()
include(generated/rexglue.cmake)
add_subdirectory(src/achievement)
refii_rexglue_apply_shadow_overrides()

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
refii_define_rexglue_build_target()
refii_define_rexglue_codegen_bootstrap_target()

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

set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
    "${CMAKE_CURRENT_SOURCE_DIR}/generated/sources.cmake"
)
if(NOT EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/generated/sources.cmake")
    message(STATUS
        "generated/sources.cmake is missing; configuring bootstrap build. "
        "Build once to build ReXGlue and generate sources, then CMake can "
        "regenerate with the full Refii source list."
    )
    return()
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

function(refii_stage_windows_runtime_dlls target_name)
    if(NOT WIN32 OR NOT TARGET "${target_name}")
        return()
    endif()

    add_custom_command(TARGET "${target_name}" POST_BUILD
        COMMAND "$<$<BOOL:$<TARGET_RUNTIME_DLLS:${target_name}>>:${CMAKE_COMMAND};-E;copy_if_different;$<TARGET_RUNTIME_DLLS:${target_name}>;$<TARGET_FILE_DIR:${target_name}>>"
        COMMAND_EXPAND_LISTS
        VERBATIM
    )
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

option(REFII_BUILD_STANDALONE "Build the standalone refii executable" ON)
option(REFII_BUILD_LAUNCHER "Build the launcher executable" ON)
option(REFII_BUILD_NATIVE_LUA "Build the native Lua runtime and Lua tools" ON)
option(REFII_BUILD_NATIVE_HOOKS "Build native hook sources into refii_generated" ON)
option(REFII_BUILD_VFS_HOOKS "Build the native VFS runtime and hook sources" ON)
# OFF by default: Godot renders, so the guest never draws. This target is the
# whole D3D9 replacement — Plume, its D3D12/Vulkan/Metal bridges, and the
# native/"xenia" backend that renders through rexglue's own Vulkan instance.
# It is 86% of the build (1880 targets down to 277 without it) and `refii`
# links with no reference to it.
#
# The renderer is kept, not deleted: it survives as the reference instrument
# that proves a converted asset or a reconstructed transform is right. Turn
# this ON to build it for that.
option(REFII_BUILD_RENDERER_HOOKS "Build the native renderer runtime and hook sources" OFF)
option(REFII_BUILD_SHADER_PIPELINE "Build shader SBK extraction/compilation pipeline tooling" ON)

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

option(REFII_BUILD_PACKAGES_IN_ALL
    "Include Linux package targets in the default all target"
    OFF
)
option(REFII_BUILD_APPIMAGE_IN_ALL
    "Include the Linux AppImage target in the default all target"
    ON
)

set(REFII_CAN_BUILD_LINUX_PACKAGES OFF)
if(UNIX AND NOT APPLE)
    set(REFII_CAN_BUILD_LINUX_PACKAGES ON)
endif()

if(REFII_CAN_BUILD_LINUX_PACKAGES)
    option(REFII_BUILD_APPIMAGE
        "Enable Linux AppImage packaging targets"
        ON
    )

    option(REFII_BUILD_FLATPAK
        "Enable Linux Flatpak packaging targets"
        OFF
    )
else()
    set(
        REFII_BUILD_APPIMAGE
        OFF
        CACHE BOOL
        "Enable Linux AppImage packaging targets"
        FORCE
    )
    set(
        REFII_BUILD_FLATPAK
        OFF
        CACHE BOOL
        "Enable Linux Flatpak packaging targets"
        FORCE
    )
endif()

set(
    REFII_BUILD_APPIMAGE_OUTPUT_ROOT
    "${CMAKE_SOURCE_DIR}/out/appimage"
    CACHE PATH
    "Directory where AppImage artifacts are emitted"
)
set(_REFII_APPIMAGE_LEGACY_OUTPUT_ROOT "${CMAKE_SOURCE_DIR}/appimage")
if(REFII_BUILD_APPIMAGE_OUTPUT_ROOT STREQUAL "${_REFII_APPIMAGE_LEGACY_OUTPUT_ROOT}")
    set(
        REFII_BUILD_APPIMAGE_OUTPUT_ROOT
        "${CMAKE_SOURCE_DIR}/out/appimage"
        CACHE PATH
        "Directory where AppImage artifacts are emitted"
        FORCE
    )
endif()
unset(_REFII_APPIMAGE_LEGACY_OUTPUT_ROOT)

set(
    REFII_BUILD_FLATPAK_OUTPUT_ROOT
    "${CMAKE_SOURCE_DIR}/out/flatpak"
    CACHE PATH
    "Directory where Flatpak artifacts are emitted"
)

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

set(
    REFII_APPIMAGE_ARCH
    "auto"
    CACHE STRING
    "Target architecture for AppImage packaging (auto|x86_64|aarch64|amd64|arm64)"
)
set_property(CACHE REFII_APPIMAGE_ARCH PROPERTY STRINGS auto x86_64 aarch64 amd64 arm64)

set(
    REFII_FLATPAK_RUNTIME
    "org.freedesktop.Platform"
    CACHE STRING
    "Flatpak runtime used in generated fallback manifest"
)

set(
    REFII_FLATPAK_SDK
    "org.freedesktop.Sdk"
    CACHE STRING
    "Flatpak SDK used in generated fallback manifest"
)

set(
    REFII_FLATPAK_RUNTIME_VERSION
    "24.08"
    CACHE STRING
    "Flatpak runtime version used in generated fallback manifest"
)

if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64)$")
    set(_REFII_FLATPAK_ARCH_GUESS "aarch64")
elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|amd64)$")
    set(_REFII_FLATPAK_ARCH_GUESS "x86_64")
else()
    set(_REFII_FLATPAK_ARCH_GUESS "x86_64")
endif()
set(
    REFII_FLATPAK_ARCH
    "${_REFII_FLATPAK_ARCH_GUESS}"
    CACHE STRING
    "Flatpak architecture (x86_64 or aarch64)"
)
set_property(CACHE REFII_FLATPAK_ARCH PROPERTY STRINGS x86_64 aarch64)
unset(_REFII_FLATPAK_ARCH_GUESS)

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
#   * launcher install path      -> src/launcher/src/installer/installer.cpp
#   * refii-shader-sbk CLI       -> src/shader/src/refii_shader_sbk_cli_main.cpp
# REFII_SHADER_VALIDATION_TARGETS above survives because it seeds the
# launcher's compile-time default target list (src/launcher/CMakeLists.txt),
# not because anything in the build validates against it.

set(REFII_LUA_LIBRARIES)
set(REFII_VFS_LIBRARIES)
set(REFII_RENDERER_LIBRARIES)
set(REFII_HOOK_OBJECTS)

add_subdirectory(src/extractor)
add_subdirectory(src/assetbrowser)

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

add_subdirectory(src/modding)

if(REFII_BUILD_NATIVE_LUA)
    enable_language(C)
    add_subdirectory(src/lua)
endif()

if(REFII_BUILD_VFS_HOOKS)
    add_subdirectory(src/vfs)
endif()

if(REFII_BUILD_RENDERER_HOOKS)
    refii_ensure_windows_vcpkg_packages()
    add_subdirectory(src/renderer)
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

if(REFII_BUILD_SHADER_PIPELINE)
    add_subdirectory(src/shader)
endif()

refii_rexglue_shadow_source(
    REFII_REX_APP_SOURCE
    "src/ui/rex_app.cpp"
)
set(REFII_CORE_SOURCES
    ${REFII_REX_APP_SOURCE}
)

set(REFII_GENERATED_SOURCES
    ${GENERATED_SOURCES}
    ${REFII_HOOK_OBJECTS}
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

# refii_generated is the replaceable recomp/codegen payload. Linux keeps it
# shared for the replaceable payload flow; Windows links it statically to avoid
# exporting tens of thousands of generated PPC symbols through an import lib.
set(REFII_GENERATED_LIBRARY_TYPE SHARED)
if(WIN32)
    set(REFII_GENERATED_LIBRARY_TYPE STATIC)
endif()
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

unset(REFII_GENERATED_LIBRARY_TYPE)
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
        REFII_REXGLUE_NETPLAY=$<BOOL:${REFII_REXGLUE_NETPLAY}>
)
target_link_libraries(refii_host
    PUBLIC
        refii_core
        refii_generated
        ${REFII_RENDERER_LIBRARIES}
    PRIVATE
        refii_achievement_ui
)

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
refii_apply_generated_payload_compile_options(refii_generated)
refii_apply_generated_payload_compile_options(refii_hooks)

if(REFII_BUILD_STANDALONE)
    set(REFII_SOURCES src/main.cpp)

    # The netplay SDK replaced the per-platform entry points with a single
    # SDL3 one; the stable tree still uses windowed_app_main_win/posix.
    if(REFII_REXGLUE_NETPLAY)
        refii_rexglue_shadow_source(
            REFII_WINDOWED_APP_MAIN_SOURCE
            "src/ui/windowed_app_main_sdl.cpp"
        )
    elseif(WIN32)
        refii_rexglue_shadow_source(
            REFII_WINDOWED_APP_MAIN_SOURCE
            "src/ui/windowed_app_main_win.cpp"
        )
    else()
        refii_rexglue_shadow_source(
            REFII_WINDOWED_APP_MAIN_SOURCE
            "src/ui/windowed_app_main_posix.cpp"
        )
    endif()
    list(APPEND REFII_SOURCES ${REFII_WINDOWED_APP_MAIN_SOURCE})
    if(WIN32)
        # Opt in to Win32 long paths; extracted asset paths reach ~194 chars
        # below the install root. MSVC embeds .manifest sources via mt.exe;
        # the clang++/lld-link toolchain needs the linker flags below instead.
        if(MSVC)
            list(APPEND REFII_SOURCES
                "${CMAKE_CURRENT_SOURCE_DIR}/packaging/windows/refii_long_paths.manifest")
        endif()
        add_executable(refii WIN32 ${REFII_SOURCES})
        if(NOT MSVC)
            refii_embed_long_paths_manifest(refii)
        endif()
    else()
        add_executable(refii ${REFII_SOURCES})
    endif()

    target_link_libraries(refii PRIVATE refii_host)
    if(EXISTS "${REFII_REXGLUE_SHADOW_DIR}/include")
        target_include_directories(refii PRIVATE "${REFII_REXGLUE_SHADOW_DIR}/include")
    endif()
    target_compile_definitions(refii
        PRIVATE
            REX_UI_HAS_SDL_WINDOW=$<BOOL:${REFII_SDL3_WINDOW_ENABLED}>
            REX_UI_HAS_WAYLAND_WINDOW=$<BOOL:${REFII_SDL3_WINDOW_ENABLED}>
    )
    set_target_properties(refii PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${REFII_RUNTIME_OUTPUT_DIR}"
        BUILD_RPATH "$ORIGIN/../lib"
        INSTALL_RPATH "$ORIGIN/../lib"
    )
    if(REFII_PACKAGE_ICON_AVAILABLE)
        if(REFII_PACKAGE_ICON_TARGET)
            add_dependencies(refii "${REFII_PACKAGE_ICON_TARGET}")
        endif()
        add_custom_command(TARGET refii POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E make_directory
                "$<TARGET_FILE_DIR:refii>/assets/launcher/assets"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${REFII_PACKAGE_ICON_SOURCE}"
                "$<TARGET_FILE_DIR:refii>/assets/launcher/assets/icon.png"
            VERBATIM
        )
    endif()

    # NOTE: the build deliberately stages no shader workspace.  Shader
    # extraction and translation are the LAUNCHER's job, and the launcher writes
    # its workspace directly to <launcher exe dir>/assets/unpacked/shaders
    # (Installer::GetLuaWorkspacePath()) -- the runtime directory.  A build-time
    # copy from the source tree into that same directory could only overwrite the
    # authoritative output, so there is none.

    if(WIN32)
        refii_stage_windows_runtime_dlls(refii)
        refii_stage_d3d12_agility_runtime(refii)
        if(TARGET rex::gpu-xenos)
            # Netplay SDK GPU plugin: the loader dlopens it from the exe folder.
            add_custom_command(TARGET refii POST_BUILD
                COMMAND ${CMAKE_COMMAND} -E copy_if_different
                    "$<TARGET_FILE:rex::gpu-xenos>"
                    "$<TARGET_FILE_DIR:refii>"
                VERBATIM
            )
        endif()

        if(REFII_STAGE_GODOT_BUILD)
            add_custom_command(TARGET refii POST_BUILD
                COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_GODOT_BUILD_BIN_DIR}"
                COMMAND ${CMAKE_COMMAND} -E copy_if_different
                    "$<TARGET_FILE:refii>" "${REFII_GODOT_BUILD_DIR}"
                COMMAND ${CMAKE_COMMAND} -E copy_if_different
                    "$<TARGET_RUNTIME_DLLS:refii>" "${REFII_GODOT_BUILD_DIR}"
                COMMENT "Staging refii runtime into Godot/build"
                COMMAND_EXPAND_LISTS
                VERBATIM
            )
        endif()
    else()
        set(REFII_STAGE_EXTERNAL_LIBS_STAMP "${REFII_PACKAGE_STAMP_DIR}/refii_stage_external_libs.stamp")
        set(_refii_stage_external_lib_commands
            COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_LIBRARY_OUTPUT_DIR}"
            COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_PACKAGE_STAMP_DIR}"
        )
        # The netplay SDK's GPU plugin loader dlopens the plugin from the
        # executable's own folder, so stage it into bin/ as well as lib/.
        refii_append_stage_library_command(
            _refii_stage_external_lib_commands
            rex::gpu-xenos
            "${REFII_RUNTIME_OUTPUT_DIR}/"
        )
        set(_refii_stage_external_lib_dependencies)
        foreach(_refii_external_lib_target IN LISTS REFII_PACKAGE_EXTERNAL_LIB_TARGETS)
            if(TARGET "${_refii_external_lib_target}")
                list(APPEND _refii_stage_external_lib_dependencies "${_refii_external_lib_target}")
            endif()
            refii_append_stage_library_command(
                _refii_stage_external_lib_commands
                "${_refii_external_lib_target}"
                "${REFII_LIBRARY_OUTPUT_DIR}/"
            )
        endforeach()
        list(APPEND _refii_stage_external_lib_commands
            COMMAND ${CMAKE_COMMAND} -E touch "${REFII_STAGE_EXTERNAL_LIBS_STAMP}"
        )
        add_custom_command(
            OUTPUT "${REFII_STAGE_EXTERNAL_LIBS_STAMP}"
            ${_refii_stage_external_lib_commands}
            DEPENDS ${_refii_stage_external_lib_dependencies}
            COMMAND_EXPAND_LISTS
            VERBATIM
        )
        add_custom_target(refii-stage-external-libs DEPENDS "${REFII_STAGE_EXTERNAL_LIBS_STAMP}")
        add_dependencies(refii refii-stage-external-libs)
        unset(_refii_stage_external_lib_commands)
        unset(_refii_stage_external_lib_dependencies)
        unset(_refii_external_lib_target)
    endif()
endif()

if(REFII_BUILD_LAUNCHER)
    set(REFII_ROOT_DIR ${CMAKE_CURRENT_SOURCE_DIR})
    add_subdirectory(src/launcher)
endif()

if(TARGET refii-launcher AND TARGET refii-stage-external-libs)
    add_dependencies(refii-launcher refii-stage-external-libs)
endif()

foreach(_refii_origin_rpath_target
    refii
    refii-launcher
    refii_core
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

set(REFII_PACKAGE_TARGETS)
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
set(REFII_FLATPAK_RUNTIME_EXCLUDE_REGEXES
    ".*/libc\\.so\\..*$"
    ".*/libm\\.so\\..*$"
    ".*/libpthread\\.so\\..*$"
    ".*/librt\\.so\\..*$"
    ".*/libdl\\.so\\..*$"
    ".*/libutil\\.so\\..*$"
    ".*/libresolv\\.so\\..*$"
    ".*/libgcc_s\\.so\\..*$"
    ".*/libstdc\\+\\+\\.so\\..*$"
    ".*/ld-linux[^/]*\\.so.*$"
)
set(REFII_APPIMAGE_RUNTIME_EXCLUDE_REGEXES
    ".*/libc\\.so\\..*$"
    ".*/libm\\.so\\..*$"
    ".*/libpthread\\.so\\..*$"
    ".*/librt\\.so\\..*$"
    ".*/libdl\\.so\\..*$"
    ".*/libutil\\.so\\..*$"
    ".*/libresolv\\.so\\..*$"
    ".*/ld-linux[^/]*\\.so.*$"

    # The display and graphics client libraries must come from the host, never
    # from the build machine. AppRun puts the bundled lib directory first on
    # LD_LIBRARY_PATH, so anything shipped here also shadows the host copy for
    # libraries the host loads later. That breaks Wayland in particular: GLFW
    # dlopens libwayland-cursor/libwayland-egl/libxkbcommon and the GL vendor
    # loads libEGL from the host, and those all bind back to whichever
    # libwayland-client/libGLdispatch is visible. Bundling half of a set that
    # has to match exactly leaves the other half mismatched. X11 tends to
    # survive it because it goes through GLX instead of EGL, which is why this
    # only showed up on a Wayland session.
    ".*/libwayland-[^/]*\\.so\\..*$"
    ".*/libxkbcommon[^/]*\\.so\\..*$"
    ".*/libdecor-[^/]*\\.so\\..*$"
    ".*/libX11[^/]*\\.so\\..*$"
    ".*/libxcb[^/]*\\.so\\..*$"
    ".*/libXau\\.so\\..*$"
    ".*/libXdmcp\\.so\\..*$"
    ".*/libXext\\.so\\..*$"
    ".*/libXi\\.so\\..*$"
    ".*/libXfixes\\.so\\..*$"
    ".*/libXrender\\.so\\..*$"
    ".*/libXrandr\\.so\\..*$"
    ".*/libXcursor\\.so\\..*$"
    ".*/libXinerama\\.so\\..*$"
    ".*/libGL\\.so\\..*$"
    ".*/libGLX[^/]*\\.so\\..*$"
    ".*/libGLdispatch\\.so\\..*$"
    ".*/libOpenGL\\.so\\..*$"
    ".*/libEGL[^/]*\\.so\\..*$"
    ".*/libGLESv[0-9][^/]*\\.so\\..*$"
    ".*/libgbm\\.so\\..*$"
    ".*/libdrm[^/]*\\.so\\..*$"
    ".*/libvulkan\\.so\\..*$"
)

if(REFII_BUILD_APPIMAGE)
    if(NOT UNIX OR APPLE)
        message(FATAL_ERROR "REFII_BUILD_APPIMAGE currently supports Linux builds only.")
    endif()
    if(NOT EXISTS "${REFII_PACKAGE_ICON_SOURCE}" AND NOT TARGET "${REFII_PACKAGE_ICON_TARGET}")
        message(FATAL_ERROR "REFII_BUILD_APPIMAGE requires REFII_PACKAGE_ICON_SOURCE to point to an existing PNG file.")
    endif()
    if(NOT REFII_BUILD_LAUNCHER)
        message(FATAL_ERROR "REFII_BUILD_APPIMAGE requires REFII_BUILD_LAUNCHER=ON.")
    endif()
    if(NOT REFII_BUILD_STANDALONE)
        message(FATAL_ERROR "REFII_BUILD_APPIMAGE requires REFII_BUILD_STANDALONE=ON.")
    endif()
    if(NOT TARGET refii)
        message(FATAL_ERROR "REFII_BUILD_APPIMAGE requires target `refii` to exist.")
    endif()

    if(REFII_APPIMAGE_ARCH STREQUAL "auto")
        if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64)$")
            set(_REFII_APPIMAGE_TOOL_ARCH "aarch64")
        elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|amd64)$")
            set(_REFII_APPIMAGE_TOOL_ARCH "x86_64")
        else()
            set(_REFII_APPIMAGE_TOOL_ARCH "${CMAKE_SYSTEM_PROCESSOR}")
        endif()
    elseif(REFII_APPIMAGE_ARCH STREQUAL "amd64")
        set(_REFII_APPIMAGE_TOOL_ARCH "x86_64")
    elseif(REFII_APPIMAGE_ARCH STREQUAL "arm64")
        set(_REFII_APPIMAGE_TOOL_ARCH "aarch64")
    else()
        set(_REFII_APPIMAGE_TOOL_ARCH "${REFII_APPIMAGE_ARCH}")
    endif()

    if(_REFII_APPIMAGE_TOOL_ARCH STREQUAL "x86_64")
        set(_REFII_APPIMAGE_OUTPUT_ARCH "x86_64")
    elseif(_REFII_APPIMAGE_TOOL_ARCH STREQUAL "aarch64")
        set(_REFII_APPIMAGE_OUTPUT_ARCH "aarch64")
    else()
        set(_REFII_APPIMAGE_OUTPUT_ARCH "${_REFII_APPIMAGE_TOOL_ARCH}")
    endif()

    set(REFII_APPIMAGE_APPRUN_SOURCE "${CMAKE_SOURCE_DIR}/packaging/appimage/AppRun")
    if(NOT EXISTS "${REFII_APPIMAGE_APPRUN_SOURCE}")
        set(REFII_APPIMAGE_APPRUN_SOURCE "${REFII_PACKAGE_GENERATED_DIR}/refii_appimage_AppRun")
        file(
            WRITE "${REFII_APPIMAGE_APPRUN_SOURCE}"
            "#!/usr/bin/env sh\n"
            "SELF=\"$(readlink -f \"$0\")\"\n"
            "APPDIR=\"$(dirname \"$SELF\")\"\n"
            "# Stash the host's own search paths before they get bundled ones\n"
            "# prepended. Helper programs we spawn (zenity, kdialog, xdg-open)\n"
            "# are host binaries and must not load our bundled libraries.\n"
            "export REFII_HOST_ENV_SAVED=1\n"
            "export REFII_HOST_PATH=\"\${PATH-}\"\n"
            "export REFII_HOST_LD_LIBRARY_PATH=\"\${LD_LIBRARY_PATH-}\"\n"
            "export PATH=\"$APPDIR/usr/bin\${PATH:+:$PATH}\"\n"
            "export LD_LIBRARY_PATH=\"$APPDIR/usr/lib\${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}\"\n"
            "LAUNCHER=\"$APPDIR/usr/bin/refii-launcher\"\n"
            "exec \"$LAUNCHER\" \"$@\"\n"
        )
        file(CHMOD "${REFII_APPIMAGE_APPRUN_SOURCE}" PERMISSIONS OWNER_READ OWNER_WRITE OWNER_EXECUTE GROUP_READ GROUP_EXECUTE WORLD_READ WORLD_EXECUTE)
    endif()

    set(REFII_APPIMAGE_DESKTOP_SOURCE "${CMAKE_SOURCE_DIR}/packaging/appimage/refii.desktop")
    if(NOT EXISTS "${REFII_APPIMAGE_DESKTOP_SOURCE}")
        set(REFII_APPIMAGE_DESKTOP_SOURCE "${REFII_PACKAGE_GENERATED_DIR}/refii_appimage.desktop")
        file(
            WRITE "${REFII_APPIMAGE_DESKTOP_SOURCE}"
            "[Desktop Entry]\n"
            "Type=Application\n"
            "Name=Refii\n"
            "Comment=Fable II launcher\n"
            "Exec=refii-launcher\n"
            "Icon=refii\n"
            "Terminal=false\n"
            "Categories=Game;\n"
        )
    endif()

    set(APPIMAGETOOL_LOCAL_PATH "${CMAKE_SOURCE_DIR}/packaging/appimage/appimagetool-${_REFII_APPIMAGE_TOOL_ARCH}.AppImage")
    if(EXISTS "${APPIMAGETOOL_LOCAL_PATH}")
        set(APPIMAGETOOL_EXECUTABLE "${APPIMAGETOOL_LOCAL_PATH}")
    else()
        find_program(APPIMAGETOOL_EXECUTABLE
            NAMES
                appimagetool
                appimagetool-x86_64.AppImage
                appimagetool-aarch64.AppImage
            DOC "Path to appimagetool."
        )
    endif()
    if(NOT APPIMAGETOOL_EXECUTABLE)
        message(FATAL_ERROR "REFII_BUILD_APPIMAGE is ON, but appimagetool was not found. Install it and re-run CMake.")
    endif()
    find_program(DD_EXECUTABLE
        NAMES dd
        DOC "Path to dd."
    )
    if(NOT DD_EXECUTABLE)
        message(FATAL_ERROR "REFII_BUILD_APPIMAGE is ON, but dd was not found. Install it and re-run CMake.")
    endif()
    find_program(CHMOD_EXECUTABLE
        NAMES chmod
        DOC "Path to chmod."
    )
    if(NOT CHMOD_EXECUTABLE)
        message(FATAL_ERROR "REFII_BUILD_APPIMAGE is ON, but chmod was not found. Install it and re-run CMake.")
    endif()
    execute_process(
        COMMAND ${APPIMAGETOOL_EXECUTABLE} --appimage-offset
        OUTPUT_VARIABLE REFII_APPIMAGE_RUNTIME_OFFSET
        OUTPUT_STRIP_TRAILING_WHITESPACE
        ERROR_QUIET
    )
    if(NOT REFII_APPIMAGE_RUNTIME_OFFSET MATCHES "^[0-9]+$")
        message(FATAL_ERROR
            "REFII_BUILD_APPIMAGE could not determine the embedded runtime offset from `${APPIMAGETOOL_EXECUTABLE}`."
        )
    endif()

    set(REFII_APPIMAGE_STAGING_DIR "${CMAKE_BINARY_DIR}/package/appimage")
    set(REFII_APPIMAGE_APPDIR "${REFII_APPIMAGE_STAGING_DIR}/AppDir")
    set(REFII_APPIMAGE_RUNTIME_FILE "${REFII_APPIMAGE_STAGING_DIR}/runtime-${_REFII_APPIMAGE_OUTPUT_ARCH}")
    set(REFII_APPIMAGE_OUTPUT "${REFII_BUILD_APPIMAGE_OUTPUT_ROOT}/refii-${CMAKE_SYSTEM_NAME}-${_REFII_APPIMAGE_OUTPUT_ARCH}.AppImage")

    add_custom_command(
        OUTPUT "${REFII_APPIMAGE_OUTPUT}"
        COMMAND ${CMAKE_COMMAND} -E rm -rf "${REFII_APPIMAGE_APPDIR}"
        COMMAND ${CMAKE_COMMAND} -E remove -f "${REFII_APPIMAGE_OUTPUT}"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_BUILD_APPIMAGE_OUTPUT_ROOT}"
        COMMAND ${CMAKE_COMMAND} -E make_directory
            "${REFII_APPIMAGE_APPDIR}/usr/bin"
            "${REFII_APPIMAGE_APPDIR}/usr/lib"
            "${REFII_APPIMAGE_APPDIR}/usr/share/applications"
            "${REFII_APPIMAGE_APPDIR}/usr/share/icons/hicolor/64x64/apps"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:refii-launcher>"
            "${REFII_APPIMAGE_APPDIR}/usr/bin/"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:refii>"
            "${REFII_APPIMAGE_APPDIR}/usr/bin/"
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            "${REFII_LIBRARY_OUTPUT_DIR}"
            "${REFII_APPIMAGE_APPDIR}/usr/lib"
        COMMAND ${CMAKE_COMMAND}
            "-DREFII_PACKAGE_EXECUTABLE_DIR=${REFII_APPIMAGE_APPDIR}/usr/bin"
            "-DREFII_PACKAGE_LIBRARY_DIR=${REFII_APPIMAGE_APPDIR}/usr/lib"
            "-DREFII_RUNTIME_SEARCH_DIRS=${REFII_PACKAGE_RUNTIME_SEARCH_DIRS}"
            "-DREFII_RUNTIME_EXCLUDE_REGEXES=${REFII_APPIMAGE_RUNTIME_EXCLUDE_REGEXES}"
            -P "${REFII_PACKAGE_STAGE_RUNTIME_DEPS_SCRIPT}"
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            "${REFII_RUNTIME_DATA_DIR}/launcher/assets"
            "${REFII_APPIMAGE_APPDIR}/usr/bin/assets/launcher/assets"
        # Per-title metadata. The seeding in refii_app.h reads
        # <exe>/assets/metadata and returns silently when it is absent, so a
        # package that omits this runs with shard opt-in, advertised_title_id
        # and presence mappings all inactive.
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            "${CMAKE_SOURCE_DIR}/assets/metadata"
            "${REFII_APPIMAGE_APPDIR}/usr/bin/assets/metadata"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${REFII_APPIMAGE_APPRUN_SOURCE}"
            "${REFII_APPIMAGE_APPDIR}/AppRun"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${REFII_APPIMAGE_DESKTOP_SOURCE}"
            "${REFII_APPIMAGE_APPDIR}/usr/share/applications/refii.desktop"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${REFII_APPIMAGE_DESKTOP_SOURCE}"
            "${REFII_APPIMAGE_APPDIR}/refii.desktop"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${REFII_PACKAGE_ICON_SOURCE}"
            "${REFII_APPIMAGE_APPDIR}/refii.png"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${REFII_PACKAGE_ICON_SOURCE}"
            "${REFII_APPIMAGE_APPDIR}/.DirIcon"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${REFII_PACKAGE_ICON_SOURCE}"
            "${REFII_APPIMAGE_APPDIR}/usr/share/icons/hicolor/64x64/apps/refii.png"
        COMMAND ${CHMOD_EXECUTABLE} +x "${REFII_APPIMAGE_APPDIR}/AppRun"
        COMMAND ${CHMOD_EXECUTABLE} +x "${APPIMAGETOOL_EXECUTABLE}"
        COMMAND ${DD_EXECUTABLE}
            if=${APPIMAGETOOL_EXECUTABLE}
            of=${REFII_APPIMAGE_RUNTIME_FILE}
            bs=1
            count=${REFII_APPIMAGE_RUNTIME_OFFSET}
        COMMAND ${CHMOD_EXECUTABLE} +x "${REFII_APPIMAGE_RUNTIME_FILE}"
        COMMAND /usr/bin/env APPIMAGE_EXTRACT_AND_RUN=1 ${APPIMAGETOOL_EXECUTABLE}
            --runtime-file
            "${REFII_APPIMAGE_RUNTIME_FILE}"
            "${REFII_APPIMAGE_APPDIR}"
            "${REFII_APPIMAGE_OUTPUT}"
        DEPENDS
            refii-launcher
            refii
            refii_core
            refii_generated
            ${REFII_PACKAGE_ICON_TARGET}
            "${APPIMAGETOOL_EXECUTABLE}"
            "${REFII_APPIMAGE_APPRUN_SOURCE}"
            "${REFII_APPIMAGE_DESKTOP_SOURCE}"
            "${REFII_PACKAGE_ICON_SOURCE}"
        WORKING_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
        COMMENT "Packaging AppImage with appimagetool"
        VERBATIM
    )

    if(REFII_BUILD_APPIMAGE_IN_ALL OR REFII_BUILD_PACKAGES_IN_ALL)
        add_custom_target(refii-appimage ALL DEPENDS "${REFII_APPIMAGE_OUTPUT}")
    else()
        add_custom_target(refii-appimage DEPENDS "${REFII_APPIMAGE_OUTPUT}")
    endif()
    list(APPEND REFII_PACKAGE_TARGETS refii-appimage)

    unset(_REFII_APPIMAGE_TOOL_ARCH)
    unset(_REFII_APPIMAGE_OUTPUT_ARCH)
    unset(APPIMAGETOOL_LOCAL_PATH)
    unset(CHMOD_EXECUTABLE)
    unset(DD_EXECUTABLE)
    unset(REFII_APPIMAGE_RUNTIME_OFFSET)
endif()

if(REFII_BUILD_FLATPAK)
    if(NOT UNIX OR APPLE)
        message(FATAL_ERROR "REFII_BUILD_FLATPAK currently supports Linux builds only.")
    endif()
    if(NOT EXISTS "${REFII_PACKAGE_ICON_SOURCE}" AND NOT TARGET "${REFII_PACKAGE_ICON_TARGET}")
        message(FATAL_ERROR "REFII_BUILD_FLATPAK requires REFII_PACKAGE_ICON_SOURCE to point to an existing PNG file.")
    endif()
    if(NOT REFII_BUILD_LAUNCHER)
        message(FATAL_ERROR "REFII_BUILD_FLATPAK requires REFII_BUILD_LAUNCHER=ON.")
    endif()
    if(NOT REFII_BUILD_STANDALONE)
        message(FATAL_ERROR "REFII_BUILD_FLATPAK requires REFII_BUILD_STANDALONE=ON.")
    endif()
    if(NOT TARGET refii)
        message(FATAL_ERROR "REFII_BUILD_FLATPAK requires target `refii` to exist.")
    endif()

    find_program(FLATPAK_EXECUTABLE
        NAMES flatpak
        DOC "Path to flatpak."
    )
    if(NOT FLATPAK_EXECUTABLE)
        message(FATAL_ERROR "REFII_BUILD_FLATPAK is ON, but flatpak was not found. Install it and re-run CMake.")
    endif()

    find_program(FLATPAK_BUILDER_EXECUTABLE
        NAMES flatpak-builder
        DOC "Path to flatpak-builder."
    )
    if(NOT FLATPAK_BUILDER_EXECUTABLE)
        message(FATAL_ERROR "REFII_BUILD_FLATPAK is ON, but flatpak-builder was not found. Install it and re-run CMake.")
    endif()
    find_program(OSTREE_EXECUTABLE
        NAMES ostree
        DOC "Path to ostree."
    )
    if(NOT OSTREE_EXECUTABLE)
        message(FATAL_ERROR "REFII_BUILD_FLATPAK is ON, but ostree was not found. Install it and re-run CMake.")
    endif()

    set(REFII_FLATPAK_DESKTOP_SOURCE "${CMAKE_SOURCE_DIR}/packaging/appimage/refii.desktop")
    if(NOT EXISTS "${REFII_FLATPAK_DESKTOP_SOURCE}")
        set(REFII_FLATPAK_DESKTOP_SOURCE "${REFII_PACKAGE_GENERATED_DIR}/refii_flatpak.desktop")
        file(
            WRITE "${REFII_FLATPAK_DESKTOP_SOURCE}"
            "[Desktop Entry]\n"
            "Type=Application\n"
            "Name=Refii\n"
            "Comment=Fable II launcher\n"
            "Exec=refii-launcher\n"
            "Icon=com.refii.refii\n"
            "Terminal=false\n"
            "Categories=Game;\n"
        )
    endif()

    set(REFII_FLATPAK_LAUNCHER_WRAPPER_SOURCE "${REFII_PACKAGE_GENERATED_DIR}/refii_flatpak_launcher")
    file(
        WRITE "${REFII_FLATPAK_LAUNCHER_WRAPPER_SOURCE}"
        "#!/usr/bin/env sh\n"
        "export PATH=\"/app/bin\${PATH:+:$PATH}\"\n"
        "export LD_LIBRARY_PATH=\"/app/lib\${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}\"\n"
        "exec /app/bin/refii-launcher.bin \"$@\"\n"
    )
    file(CHMOD "${REFII_FLATPAK_LAUNCHER_WRAPPER_SOURCE}" PERMISSIONS OWNER_READ OWNER_WRITE OWNER_EXECUTE GROUP_READ GROUP_EXECUTE WORLD_READ WORLD_EXECUTE)

    set(REFII_FLATPAK_MANIFEST_SOURCE "${CMAKE_SOURCE_DIR}/packaging/flatpak/refii.json")
    if(NOT EXISTS "${REFII_FLATPAK_MANIFEST_SOURCE}")
        set(REFII_FLATPAK_MANIFEST_SOURCE "${REFII_PACKAGE_GENERATED_DIR}/refii_flatpak_refii.json")
        file(
            WRITE "${REFII_FLATPAK_MANIFEST_SOURCE}"
            "{\n"
            "  \"app-id\": \"com.refii.refii\",\n"
            "  \"runtime\": \"${REFII_FLATPAK_RUNTIME}\",\n"
            "  \"runtime-version\": \"${REFII_FLATPAK_RUNTIME_VERSION}\",\n"
            "  \"sdk\": \"${REFII_FLATPAK_SDK}\",\n"
            "  \"command\": \"refii-launcher\",\n"
            "  \"finish-args\": [\n"
            "    \"--socket=wayland\",\n"
            "    \"--socket=fallback-x11\",\n"
            "    \"--share=network\",\n"
            "    \"--share=ipc\",\n"
            "    \"--device=dri\"\n"
            "  ],\n"
            "  \"modules\": [\n"
            "    {\n"
            "      \"name\": \"refii\",\n"
            "      \"buildsystem\": \"simple\",\n"
            "      \"sources\": [\n"
            "        {\"type\": \"dir\", \"path\": \"payload\"}\n"
            "      ],\n"
            "      \"build-commands\": [\n"
            "        \"mkdir -p /app/bin /app/lib /app/share/applications /app/share/icons/hicolor/64x64/apps\",\n"
            "        \"cp -a bin/. /app/bin/\",\n"
            "        \"cp -a lib/. /app/lib/\",\n"
            "        \"cp -a share/. /app/share/\"\n"
            "      ]\n"
            "    }\n"
            "  ]\n"
            "}\n"
        )
    endif()

    set(REFII_FLATPAK_STAGING_DIR "${CMAKE_BINARY_DIR}/package/flatpak")
    set(REFII_FLATPAK_PAYLOAD_DIR "${REFII_FLATPAK_STAGING_DIR}/payload")
    set(REFII_FLATPAK_MANIFEST_PATH "${REFII_FLATPAK_STAGING_DIR}/refii.json")
    set(REFII_FLATPAK_BUILD_DIR "${REFII_FLATPAK_STAGING_DIR}/build")
    set(REFII_FLATPAK_REPO_DIR "${REFII_FLATPAK_STAGING_DIR}/repo")
    set(REFII_FLATPAK_OUTPUT "${REFII_BUILD_FLATPAK_OUTPUT_ROOT}/refii.flatpak")

    add_custom_command(
        OUTPUT "${REFII_FLATPAK_OUTPUT}"
        COMMAND ${CMAKE_COMMAND} -E rm -rf "${REFII_FLATPAK_STAGING_DIR}"
        COMMAND ${CMAKE_COMMAND} -E remove -f "${REFII_FLATPAK_OUTPUT}"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_BUILD_FLATPAK_OUTPUT_ROOT}"
        COMMAND ${CMAKE_COMMAND} -E make_directory
            "${REFII_FLATPAK_PAYLOAD_DIR}/bin"
            "${REFII_FLATPAK_PAYLOAD_DIR}/lib"
            "${REFII_FLATPAK_PAYLOAD_DIR}/share/applications"
            "${REFII_FLATPAK_PAYLOAD_DIR}/share/icons/hicolor/64x64/apps"
            "${REFII_FLATPAK_BUILD_DIR}"
            "${REFII_FLATPAK_REPO_DIR}"
        COMMAND ${OSTREE_EXECUTABLE}
            init
            --repo=${REFII_FLATPAK_REPO_DIR}
            --mode=archive-z2
        COMMAND ${OSTREE_EXECUTABLE}
            config
            --repo=${REFII_FLATPAK_REPO_DIR}
            --group=core
            set
            min-free-space-percent
            0
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${REFII_FLATPAK_LAUNCHER_WRAPPER_SOURCE}"
            "${REFII_FLATPAK_PAYLOAD_DIR}/bin/refii-launcher"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:refii-launcher>"
            "${REFII_FLATPAK_PAYLOAD_DIR}/bin/refii-launcher.bin"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:refii>"
            "${REFII_FLATPAK_PAYLOAD_DIR}/bin/refii"
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            "${REFII_LIBRARY_OUTPUT_DIR}"
            "${REFII_FLATPAK_PAYLOAD_DIR}/lib"
        COMMAND ${CMAKE_COMMAND}
            "-DREFII_PACKAGE_EXECUTABLE_DIR=${REFII_FLATPAK_PAYLOAD_DIR}/bin"
            "-DREFII_PACKAGE_LIBRARY_DIR=${REFII_FLATPAK_PAYLOAD_DIR}/lib"
            "-DREFII_RUNTIME_SEARCH_DIRS=${REFII_PACKAGE_RUNTIME_SEARCH_DIRS}"
            "-DREFII_RUNTIME_EXCLUDE_REGEXES=${REFII_FLATPAK_RUNTIME_EXCLUDE_REGEXES}"
            -P "${REFII_PACKAGE_STAGE_RUNTIME_DEPS_SCRIPT}"
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            "${REFII_RUNTIME_DATA_DIR}/launcher/assets"
            "${REFII_FLATPAK_PAYLOAD_DIR}/bin/assets/launcher/assets"
        # Per-title metadata; see the AppImage staging above.
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            "${CMAKE_SOURCE_DIR}/assets/metadata"
            "${REFII_FLATPAK_PAYLOAD_DIR}/bin/assets/metadata"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${REFII_FLATPAK_DESKTOP_SOURCE}"
            "${REFII_FLATPAK_PAYLOAD_DIR}/share/applications/com.refii.refii.desktop"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${REFII_PACKAGE_ICON_SOURCE}"
            "${REFII_FLATPAK_PAYLOAD_DIR}/share/icons/hicolor/64x64/apps/com.refii.refii.png"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${REFII_FLATPAK_MANIFEST_SOURCE}"
            "${REFII_FLATPAK_MANIFEST_PATH}"
        COMMAND ${FLATPAK_BUILDER_EXECUTABLE}
            --state-dir=${REFII_FLATPAK_STAGING_DIR}/state
            --force-clean
            --disable-rofiles-fuse
            --arch=${REFII_FLATPAK_ARCH}
            --repo=${REFII_FLATPAK_REPO_DIR}
            "${REFII_FLATPAK_BUILD_DIR}"
            "${REFII_FLATPAK_MANIFEST_PATH}"
        COMMAND ${FLATPAK_EXECUTABLE}
            build-bundle
            "${REFII_FLATPAK_REPO_DIR}"
            "${REFII_FLATPAK_OUTPUT}"
            "com.refii.refii"
        DEPENDS
            refii-launcher
            refii
            refii_core
            refii_generated
            ${REFII_PACKAGE_ICON_TARGET}
            "${REFII_FLATPAK_LAUNCHER_WRAPPER_SOURCE}"
            "${REFII_FLATPAK_DESKTOP_SOURCE}"
            "${REFII_FLATPAK_MANIFEST_SOURCE}"
            "${REFII_PACKAGE_ICON_SOURCE}"
        WORKING_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
        COMMENT "Packaging Flatpak with flatpak-builder"
        VERBATIM
    )

    add_custom_target(refii-flatpak DEPENDS "${REFII_FLATPAK_OUTPUT}")
    list(APPEND REFII_PACKAGE_TARGETS refii-flatpak)
    unset(OSTREE_EXECUTABLE)
endif()

if(REFII_PACKAGE_TARGETS)
    if(REFII_BUILD_PACKAGES_IN_ALL)
        add_custom_target(refii-packages ALL DEPENDS ${REFII_PACKAGE_TARGETS})
    else()
        add_custom_target(refii-packages DEPENDS ${REFII_PACKAGE_TARGETS})
    endif()
endif()
