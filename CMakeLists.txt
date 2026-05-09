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

include(generated/rexglue.cmake)

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
    endif()
endif()

if(REFII_BUILD_LAUNCHER)
    set(REFII_ROOT_DIR ${CMAKE_CURRENT_SOURCE_DIR})
    add_subdirectory(src/launcher)
endif()
