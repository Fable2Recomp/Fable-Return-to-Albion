# refii - ReXGlue Recompiled Project
#
# This file is yours to edit. 'rexglue migrate' will NOT overwrite it.
# SDK boilerplate lives in generated/rexglue.cmake.

cmake_minimum_required(VERSION 3.25)
project(refii LANGUAGES CXX)

if(POLICY CMP0144)
  cmake_policy(SET CMP0144 NEW)
endif()

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib")

set(_REFII_REXGLUE_FALLBACK_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/ReXGlue")
if((NOT DEFINED REXSDK_DIR OR NOT REXSDK_DIR) AND EXISTS "${_REFII_REXGLUE_FALLBACK_SOURCE_DIR}/CMakeLists.txt")
    if(DEFINED REXSDK_VERSION AND REXSDK_VERSION)
        find_package(rexglue ${REXSDK_VERSION} EXACT QUIET CONFIG)
    else()
        find_package(rexglue 0.8.0.32 QUIET CONFIG)
    endif()

    if(NOT rexglue_FOUND)
        set(REXSDK_DIR "${_REFII_REXGLUE_FALLBACK_SOURCE_DIR}" CACHE PATH "Path to rexglue-sdk source tree" FORCE)
        message(STATUS "Using ReXGlue fallback submodule source tree: ${REXSDK_DIR}")
    endif()
endif()
unset(_REFII_REXGLUE_FALLBACK_SOURCE_DIR)

include(generated/rexglue.cmake)

if(TARGET TracyClient AND NOT TARGET rex::TracyClient)
    add_library(rex::TracyClient ALIAS TracyClient)
endif()

if(TARGET SPIRV-Tools-static AND NOT TARGET rex::SPIRV-Tools-static)
    add_library(rex::SPIRV-Tools-static ALIAS SPIRV-Tools-static)
endif()

function(refii_apply_origin_rpath target_name)
    if(UNIX AND NOT APPLE AND TARGET "${target_name}")
        set_target_properties("${target_name}" PROPERTIES
            BUILD_RPATH "\$ORIGIN;\$ORIGIN/lib"
            INSTALL_RPATH "\$ORIGIN;\$ORIGIN/lib"
            BUILD_RPATH_USE_ORIGIN ON
            BUILD_WITH_INSTALL_RPATH ON
            INSTALL_RPATH_USE_LINK_PATH OFF
        )
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

set(REFII_PACKAGE_EXTERNAL_LIB_TARGETS
    rex::runtime
    rex::TracyClient
    fmt::fmt
    spdlog::spdlog
    SDL3::SDL3-static
    volk::volk
    rex::SPIRV-Tools-static
    # Despite the name, this is also used for Linux Vulkan/SPIR-V shader translation today.
    Microsoft::DirectXShaderCompiler
    Microsoft::DXIL
)

option(REFII_BUILD_STANDALONE "Build the standalone refii executable" ON)
option(REFII_BUILD_LAUNCHER "Build the launcher executable" ON)
option(REFII_BUILD_NATIVE_LUA "Build the native Lua runtime and Lua tools" ON)
option(REFII_BUILD_NATIVE_HOOKS "Build native hook sources into refii_generated" ON)
option(REFII_BUILD_VFS_HOOKS "Build the native VFS runtime and hook sources" ON)
option(REFII_BUILD_SHADER_PIPELINE "Build shader SBK extraction/translation pipeline tooling" ON)
option(REFII_BUILD_TOOLS
    "Build optional lightweight test tool executables (bnk extractor, decompiler, shader utilities)"
    ON
)

option(REFII_BUILD_EXTRACTOR
    "Build the XISO extractor shared library (and tool dependencies)"
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
        ON
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
    REFII_PACKAGE_ICON_SOURCE
    "${CMAKE_SOURCE_DIR}/assets/game/default.xex.icons/001_Icon_black_knight.png"
    CACHE FILEPATH
    "PNG icon used for AppImage and Flatpak packaging"
)

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

set(REFII_SHADER_SBK_INPUT_DIR
    "${CMAKE_SOURCE_DIR}/assets/game/data/Shaders"
    CACHE PATH
    "Directory containing tracked Shaders.sbk and ShadersRelease.sbk inputs"
)
set(REFII_SHADER_PIPELINE_ROOT_DIR
    "${CMAKE_BINARY_DIR}/pipeline/shader_private"
    CACHE PATH
    "Private shader pipeline workspace root (kept separate from release/runtime outputs)"
)
set(REFII_SHADER_TOOL_OUTPUT_DIR
    "${REFII_SHADER_PIPELINE_ROOT_DIR}/tools"
    CACHE PATH
    "Output directory for shader pipeline helper binaries"
)
set(REFII_SHADER_OUTPUT_DIR
    "${REFII_SHADER_PIPELINE_ROOT_DIR}/artifacts"
    CACHE PATH
    "Private output directory for extracted shader blobs and translation artifacts"
)
set(REFII_SHADER_SHARED_OUTPUT_DIR
    "${CMAKE_SOURCE_DIR}/src/shader/processed"
    CACHE PATH
    "Config-independent shader artifact mirror under source tree for unified lookup"
)
set(REFII_SHADER_MIRROR_TO_SHARED_OUTPUT_DIR
    ON
    CACHE BOOL
    "When ON, mirror shader pipeline artifacts into REFII_SHADER_SHARED_OUTPUT_DIR"
)
set(REFII_TOOL_OUTPUT_DIR
    "${CMAKE_BINARY_DIR}/tools"
    CACHE PATH
    "Output directory for optional lightweight tool executables"
)
set(_REFII_SHADER_LEGACY_TOOL_OUTPUT_DIR "${CMAKE_BINARY_DIR}/tools")
if(REFII_SHADER_TOOL_OUTPUT_DIR STREQUAL "${_REFII_SHADER_LEGACY_TOOL_OUTPUT_DIR}")
    set(REFII_SHADER_TOOL_OUTPUT_DIR
        "${REFII_SHADER_PIPELINE_ROOT_DIR}/tools"
        CACHE PATH
        "Output directory for shader pipeline helper binaries"
        FORCE
    )
endif()
unset(_REFII_SHADER_LEGACY_TOOL_OUTPUT_DIR)

set(_REFII_SHADER_LEGACY_OUTPUT_DIR "${CMAKE_BINARY_DIR}/generated/shaders")
if(REFII_SHADER_OUTPUT_DIR STREQUAL "${_REFII_SHADER_LEGACY_OUTPUT_DIR}")
    set(REFII_SHADER_OUTPUT_DIR
        "${REFII_SHADER_PIPELINE_ROOT_DIR}/artifacts"
        CACHE PATH
        "Private output directory for extracted shader blobs and translation artifacts"
        FORCE
    )
endif()
unset(_REFII_SHADER_LEGACY_OUTPUT_DIR)
set(REFII_SHADER_TRANSLATOR_CMD
    ""
    CACHE FILEPATH
    "Optional translator executable path used by refii-shader-sbk translate/build"
)
set(REFII_SHADER_TRANSLATION_TARGETS
    "auto"
    CACHE STRING
    "Translation targets for shader pipeline: auto, vulkan, d3d12, or vulkan,d3d12"
)
set(REFII_SHADER_USE_XENOS_TRANSLATOR
    ON
    CACHE BOOL
    "When ON and REFII_SHADER_TRANSLATOR_CMD is empty, use the built refii-shader-xenos-translator"
)
set(REFII_SHADER_TRANSLATOR_TIMEOUT_MS
    "60000"
    CACHE STRING
    "Timeout in milliseconds for each translator invocation"
)
set(REFII_SHADER_DEDUPE_IDENTICAL_BLOBS
    ON
    CACHE BOOL
    "When ON, deduplicate translation work across identical shader payload hashes"
)
if(WIN32)
    set(REFII_SHADER_DEFAULT_VALIDATION_TARGETS "vulkan,d3d12")
else()
    set(REFII_SHADER_DEFAULT_VALIDATION_TARGETS "vulkan")
endif()
set(REFII_SHADER_VALIDATION_TARGETS
    "${REFII_SHADER_DEFAULT_VALIDATION_TARGETS}"
    CACHE STRING
    "Targets used by refii_shader_pipeline_validate_all_targets (e.g. vulkan or vulkan,d3d12)"
)
unset(REFII_SHADER_DEFAULT_VALIDATION_TARGETS)

set(REFII_LUA_LIBRARIES)
set(REFII_VFS_LIBRARIES)
set(REFII_HOOK_OBJECTS)

add_subdirectory(src/extractor)

if(REFII_BUILD_NATIVE_LUA)
    enable_language(C)
    add_subdirectory(src/lua)
endif()

if(REFII_BUILD_VFS_HOOKS)
    add_subdirectory(src/vfs)
endif()

if(REFII_BUILD_NATIVE_HOOKS)
    add_subdirectory(src/hooks)
endif()

if(REFII_BUILD_SHADER_PIPELINE)
    add_subdirectory(src/shader)
endif()

set(REFII_CORE_SOURCES
    ${REXGLUE_SHARE_DIR}/rex_app.cpp
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
target_link_libraries(refii_core
    PUBLIC
        rex::runtime
)
set_target_properties(refii_core PROPERTIES
    OUTPUT_NAME refii_core
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
)
if(WIN32)
    set_target_properties(refii_core PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

# refii_generated is the replaceable recomp/codegen payload.
add_library(refii_generated SHARED ${REFII_GENERATED_SOURCES})
target_include_directories(refii_generated
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}
        ${CMAKE_CURRENT_SOURCE_DIR}/src
        ${CMAKE_CURRENT_SOURCE_DIR}/generated
)
target_link_libraries(refii_generated
    PUBLIC
        refii_core
    PRIVATE
        ${REFII_LUA_LIBRARIES}
        ${REFII_VFS_LIBRARIES}
)
set_target_properties(refii_generated PROPERTIES
    OUTPUT_NAME refii_generated
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)
if(WIN32)
    set_target_properties(refii_generated PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

add_library(refii_host STATIC
    src/refii_host.cpp
)
set_target_properties(refii_host PROPERTIES
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
)
target_include_directories(refii_host
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}
        ${CMAKE_CURRENT_SOURCE_DIR}/src
        ${CMAKE_CURRENT_SOURCE_DIR}/generated
)
target_link_libraries(refii_host
    PUBLIC
        refii_core
        refii_generated
)

if(UNIX AND NOT APPLE)
    find_package(PkgConfig REQUIRED)
    find_package(X11 REQUIRED)
    pkg_check_modules(GTK3 REQUIRED gtk+-3.0)
    target_include_directories(refii_host PUBLIC ${GTK3_INCLUDE_DIRS})
    target_link_libraries(refii_host PUBLIC ${GTK3_LIBRARIES})
    target_link_libraries(refii_host PUBLIC X11::X11)
    target_link_options(refii_core
        PRIVATE
            -Wl,--whole-archive
            $<TARGET_FILE:rex::runtime>
            -Wl,--no-whole-archive
            -Wl,--no-relax
    )
    target_compile_options(refii_generated PRIVATE -mcmodel=large)
endif()

if(MSVC)
    target_compile_options(refii_core PRIVATE /EHa)
    target_compile_options(refii_generated PRIVATE /EHa)
else()
    target_compile_options(refii_core PRIVATE -msse4.1)
    target_compile_options(refii_generated PRIVATE -msse4.1)
endif()

if(REFII_BUILD_STANDALONE)
    set(REFII_SOURCES src/main.cpp)

    if(WIN32)
        list(APPEND REFII_SOURCES ${REXGLUE_SHARE_DIR}/windowed_app_main_win.cpp)
        add_executable(refii WIN32 ${REFII_SOURCES})
    else()
        list(APPEND REFII_SOURCES ${REXGLUE_SHARE_DIR}/windowed_app_main_posix.cpp)
        add_executable(refii ${REFII_SOURCES})
    endif()

    target_link_libraries(refii PRIVATE refii_host)
    set_target_properties(refii PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}"
        BUILD_RPATH "$ORIGIN"
        INSTALL_RPATH "$ORIGIN"
    )

    if(WIN32)
        add_custom_command(TARGET refii POST_BUILD
            COMMAND "$<$<BOOL:$<TARGET_RUNTIME_DLLS:refii>>:${CMAKE_COMMAND};-E;copy_if_different;$<TARGET_RUNTIME_DLLS:refii>;$<TARGET_FILE_DIR:refii>>"
            COMMAND_EXPAND_LISTS
        )
    else()
        set(REFII_STAGE_EXTERNAL_LIBS_STAMP "${CMAKE_BINARY_DIR}/.refii_stage_external_libs.stamp")
        set(_refii_stage_external_lib_commands
            COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_BINARY_DIR}/lib"
        )
        set(_refii_stage_external_lib_dependencies)
        foreach(_refii_external_lib_target IN LISTS REFII_PACKAGE_EXTERNAL_LIB_TARGETS)
            if(TARGET "${_refii_external_lib_target}")
                list(APPEND _refii_stage_external_lib_dependencies "${_refii_external_lib_target}")
            endif()
            refii_append_stage_library_command(
                _refii_stage_external_lib_commands
                "${_refii_external_lib_target}"
                "${CMAKE_BINARY_DIR}/lib/"
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
set(REFII_PACKAGE_STAGE_RUNTIME_DEPS_SCRIPT "${CMAKE_CURRENT_BINARY_DIR}/refii_stage_runtime_deps.cmake")
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
    "${CMAKE_BINARY_DIR}/lib"
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
)

if(REFII_BUILD_APPIMAGE)
    if(NOT UNIX OR APPLE)
        message(FATAL_ERROR "REFII_BUILD_APPIMAGE currently supports Linux builds only.")
    endif()
    if(NOT EXISTS "${REFII_PACKAGE_ICON_SOURCE}")
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
        set(REFII_APPIMAGE_APPRUN_SOURCE "${CMAKE_CURRENT_BINARY_DIR}/refii_appimage_AppRun")
        file(
            WRITE "${REFII_APPIMAGE_APPRUN_SOURCE}"
            "#!/usr/bin/env sh\n"
            "SELF=\"$(readlink -f \"$0\")\"\n"
            "APPDIR=\"$(dirname \"$SELF\")\"\n"
            "export PATH=\"$APPDIR/usr/bin\${PATH:+:$PATH}\"\n"
            "export LD_LIBRARY_PATH=\"$APPDIR/usr/lib\${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}\"\n"
            "LAUNCHER=\"$APPDIR/usr/bin/refii-launcher\"\n"
            "exec \"$LAUNCHER\" \"$@\"\n"
        )
        file(CHMOD "${REFII_APPIMAGE_APPRUN_SOURCE}" PERMISSIONS OWNER_READ OWNER_WRITE OWNER_EXECUTE GROUP_READ GROUP_EXECUTE WORLD_READ WORLD_EXECUTE)
    endif()

    set(REFII_APPIMAGE_DESKTOP_SOURCE "${CMAKE_SOURCE_DIR}/packaging/appimage/refii.desktop")
    if(NOT EXISTS "${REFII_APPIMAGE_DESKTOP_SOURCE}")
        set(REFII_APPIMAGE_DESKTOP_SOURCE "${CMAKE_CURRENT_BINARY_DIR}/refii_appimage.desktop")
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
            "${CMAKE_BINARY_DIR}/lib"
            "${REFII_APPIMAGE_APPDIR}/usr/lib"
        COMMAND ${CMAKE_COMMAND}
            "-DREFII_PACKAGE_EXECUTABLE_DIR=${REFII_APPIMAGE_APPDIR}/usr/bin"
            "-DREFII_PACKAGE_LIBRARY_DIR=${REFII_APPIMAGE_APPDIR}/usr/lib"
            "-DREFII_RUNTIME_SEARCH_DIRS=${REFII_PACKAGE_RUNTIME_SEARCH_DIRS}"
            "-DREFII_RUNTIME_EXCLUDE_REGEXES=${REFII_APPIMAGE_RUNTIME_EXCLUDE_REGEXES}"
            -P "${REFII_PACKAGE_STAGE_RUNTIME_DEPS_SCRIPT}"
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            "${CMAKE_BINARY_DIR}/assets/launcher/assets"
            "${REFII_APPIMAGE_APPDIR}/usr/bin/assets/launcher/assets"
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
            "${APPIMAGETOOL_EXECUTABLE}"
            "${REFII_APPIMAGE_APPRUN_SOURCE}"
            "${REFII_APPIMAGE_DESKTOP_SOURCE}"
            "${REFII_PACKAGE_ICON_SOURCE}"
        WORKING_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
        COMMENT "Packaging AppImage with appimagetool"
        VERBATIM
    )

    add_custom_target(refii-appimage DEPENDS "${REFII_APPIMAGE_OUTPUT}")
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
    if(NOT EXISTS "${REFII_PACKAGE_ICON_SOURCE}")
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
        set(REFII_FLATPAK_DESKTOP_SOURCE "${CMAKE_CURRENT_BINARY_DIR}/refii_flatpak.desktop")
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

    set(REFII_FLATPAK_LAUNCHER_WRAPPER_SOURCE "${CMAKE_CURRENT_BINARY_DIR}/refii_flatpak_launcher")
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
        set(REFII_FLATPAK_MANIFEST_SOURCE "${CMAKE_CURRENT_BINARY_DIR}/refii_flatpak_refii.json")
        file(
            WRITE "${REFII_FLATPAK_MANIFEST_SOURCE}"
            "{\n"
            "  \"app-id\": \"com.refii.refii\",\n"
            "  \"runtime\": \"${REFII_FLATPAK_RUNTIME}\",\n"
            "  \"runtime-version\": \"${REFII_FLATPAK_RUNTIME_VERSION}\",\n"
            "  \"sdk\": \"${REFII_FLATPAK_SDK}\",\n"
            "  \"command\": \"refii-launcher\",\n"
            "  \"finish-args\": [\n"
            "    \"--socket=x11\",\n"
            "    \"--socket=wayland\",\n"
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
            "${CMAKE_BINARY_DIR}/lib"
            "${REFII_FLATPAK_PAYLOAD_DIR}/lib"
        COMMAND ${CMAKE_COMMAND}
            "-DREFII_PACKAGE_EXECUTABLE_DIR=${REFII_FLATPAK_PAYLOAD_DIR}/bin"
            "-DREFII_PACKAGE_LIBRARY_DIR=${REFII_FLATPAK_PAYLOAD_DIR}/lib"
            "-DREFII_RUNTIME_SEARCH_DIRS=${REFII_PACKAGE_RUNTIME_SEARCH_DIRS}"
            "-DREFII_RUNTIME_EXCLUDE_REGEXES=${REFII_FLATPAK_RUNTIME_EXCLUDE_REGEXES}"
            -P "${REFII_PACKAGE_STAGE_RUNTIME_DEPS_SCRIPT}"
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            "${CMAKE_BINARY_DIR}/assets/launcher/assets"
            "${REFII_FLATPAK_PAYLOAD_DIR}/bin/assets/launcher/assets"
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
    add_custom_target(refii-packages ALL DEPENDS ${REFII_PACKAGE_TARGETS})
endif()
