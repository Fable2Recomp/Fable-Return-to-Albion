# refii - ReXGlue Recompiled Project
#
# This file is yours to edit. 'rexglue migrate' will NOT overwrite it.
# SDK boilerplate lives in generated/rexglue.cmake.

cmake_minimum_required(VERSION 3.25)
project(refii LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

#set(REXSDK_VERSION "0.3.0" CACHE STRING "Override SDK version" FORCE)

include(generated/rexglue.cmake)

option(REFII_BUILD_STANDALONE "Build the standalone refii executable" ON)
option(REFII_BUILD_LAUNCHER "Build the launcher executable" ON)
option(REFII_BUILD_NATIVE_LUA "Build native Lua hook sources into refii_generated" ON)
option(REFII_BUILD_SHADER_PIPELINE "Build shader SBK extraction/translation pipeline tooling" ON)

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
    "${CMAKE_SOURCE_DIR}/src/Shader/processed"
    CACHE PATH
    "Config-independent shader artifact mirror under source tree for unified lookup"
)
set(REFII_SHADER_MIRROR_TO_SHARED_OUTPUT_DIR
    ON
    CACHE BOOL
    "When ON, mirror shader pipeline artifacts into REFII_SHADER_SHARED_OUTPUT_DIR"
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

set(REFII_LUA_HOOK_OBJECTS)
set(REFII_LUA_LIBRARIES)

if(REFII_BUILD_NATIVE_LUA)
    enable_language(C)
    add_subdirectory(src/Lua)
endif()

if(REFII_BUILD_SHADER_PIPELINE)
    add_subdirectory(src/Shader)
endif()

set(REFII_CORE_SOURCES
    ${REXGLUE_SHARE_DIR}/rex_app.cpp
)

set(REFII_GENERATED_SOURCES
    ${GENERATED_SOURCES}
    ${REFII_LUA_HOOK_OBJECTS}
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
        rex::core
        rex::system
        rex::kernel
        rex::graphics
        rex::ui
)
set_target_properties(refii_core PROPERTIES
    OUTPUT_NAME refii_core
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
)
set_target_properties(refii_generated PROPERTIES
    OUTPUT_NAME refii_generated
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
            $<TARGET_FILE:rex::kernel>
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

if(REFII_BUILD_SHADER_PIPELINE)
    set(REFII_SHADER_EXTRACT_OUT_DIR "${REFII_SHADER_OUTPUT_DIR}/extracted")
    set(REFII_SHADER_TRANSLATE_OUT_DIR "${REFII_SHADER_OUTPUT_DIR}/translated")
    set(REFII_SHADER_EXTRACT_MANIFEST "${REFII_SHADER_OUTPUT_DIR}/extract_manifest.tsv")
    set(REFII_SHADER_TRANSLATION_REPORT "${REFII_SHADER_OUTPUT_DIR}/translation_report.tsv")
    set(REFII_SHADER_PIPELINE_STAMP "${REFII_SHADER_OUTPUT_DIR}/pipeline.stamp")
    set(REFII_SHADER_SHARED_PIPELINE_STAMP "${REFII_SHADER_SHARED_OUTPUT_DIR}/pipeline_mirror.stamp")
    set(REFII_SHADER_VALIDATION_OUTPUT_DIR "${REFII_SHADER_OUTPUT_DIR}/validation_all_targets")
    set(REFII_SHADER_VALIDATION_EXTRACT_OUT_DIR "${REFII_SHADER_VALIDATION_OUTPUT_DIR}/extracted")
    set(REFII_SHADER_VALIDATION_TRANSLATE_OUT_DIR "${REFII_SHADER_VALIDATION_OUTPUT_DIR}/translated")
    set(REFII_SHADER_VALIDATION_EXTRACT_MANIFEST "${REFII_SHADER_VALIDATION_OUTPUT_DIR}/extract_manifest.tsv")
    set(REFII_SHADER_VALIDATION_TRANSLATION_REPORT "${REFII_SHADER_VALIDATION_OUTPUT_DIR}/translation_report.tsv")
    set(REFII_SHADER_VALIDATION_PIPELINE_STAMP "${REFII_SHADER_VALIDATION_OUTPUT_DIR}/pipeline.stamp")

    set(REFII_SHADER_PIPELINE_TRANSLATOR_DEPENDS)
    set(REFII_SHADER_PIPELINE_TRANSLATOR_ARG)
    if(REFII_SHADER_TRANSLATOR_CMD)
        set(REFII_SHADER_PIPELINE_TRANSLATOR_ARG "${REFII_SHADER_TRANSLATOR_CMD}")
    elseif(REFII_SHADER_USE_XENOS_TRANSLATOR)
        list(APPEND REFII_SHADER_PIPELINE_TRANSLATOR_DEPENDS refii-shader-xenos-translator)
        set(REFII_SHADER_PIPELINE_TRANSLATOR_ARG "$<TARGET_FILE:refii-shader-xenos-translator>")
    endif()

    if(REFII_SHADER_DEDUPE_IDENTICAL_BLOBS)
        set(REFII_SHADER_DEDUPE_IDENTICAL_BLOBS_ARG "on")
    else()
        set(REFII_SHADER_DEDUPE_IDENTICAL_BLOBS_ARG "off")
    endif()

    set(REFII_SHADER_PIPELINE_COMMAND
        $<TARGET_FILE:refii-shader-sbk>
        build
        --input-dir
        "${REFII_SHADER_SBK_INPUT_DIR}"
        --extract-out
        "${REFII_SHADER_EXTRACT_OUT_DIR}"
        --manifest
        "${REFII_SHADER_EXTRACT_MANIFEST}"
        --translate-out
        "${REFII_SHADER_TRANSLATE_OUT_DIR}"
        --report
        "${REFII_SHADER_TRANSLATION_REPORT}"
        --targets
        "${REFII_SHADER_TRANSLATION_TARGETS}"
        --dedupe-identical-blobs
        "${REFII_SHADER_DEDUPE_IDENTICAL_BLOBS_ARG}"
        --translator-timeout-ms
        "${REFII_SHADER_TRANSLATOR_TIMEOUT_MS}"
    )
    if(REFII_SHADER_PIPELINE_TRANSLATOR_ARG)
        list(APPEND REFII_SHADER_PIPELINE_COMMAND
            --translator
            "${REFII_SHADER_PIPELINE_TRANSLATOR_ARG}"
        )
    endif()

    add_custom_command(
        OUTPUT "${REFII_SHADER_PIPELINE_STAMP}"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_SHADER_OUTPUT_DIR}"
        COMMAND ${REFII_SHADER_PIPELINE_COMMAND}
        COMMAND ${CMAKE_COMMAND} -E touch "${REFII_SHADER_PIPELINE_STAMP}"
        DEPENDS
            refii-shader-sbk
            ${REFII_SHADER_PIPELINE_TRANSLATOR_DEPENDS}
            "${REFII_SHADER_SBK_INPUT_DIR}/Shaders.sbk"
            "${REFII_SHADER_SBK_INPUT_DIR}/ShadersRelease.sbk"
        VERBATIM
        COMMAND_EXPAND_LISTS
    )

    add_custom_target(refii_shader_pipeline
        DEPENDS "${REFII_SHADER_PIPELINE_STAMP}"
    )

    if(REFII_SHADER_MIRROR_TO_SHARED_OUTPUT_DIR)
        add_custom_command(
            OUTPUT "${REFII_SHADER_SHARED_PIPELINE_STAMP}"
            COMMAND ${CMAKE_COMMAND} -E remove_directory "${REFII_SHADER_SHARED_OUTPUT_DIR}"
            COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_SHADER_SHARED_OUTPUT_DIR}"
            COMMAND ${CMAKE_COMMAND} -E copy_directory "${REFII_SHADER_OUTPUT_DIR}" "${REFII_SHADER_SHARED_OUTPUT_DIR}"
            COMMAND ${CMAKE_COMMAND} -E touch "${REFII_SHADER_SHARED_PIPELINE_STAMP}"
            DEPENDS "${REFII_SHADER_PIPELINE_STAMP}"
            VERBATIM
        )

        add_custom_target(refii_shader_pipeline_shared_output
            DEPENDS "${REFII_SHADER_SHARED_PIPELINE_STAMP}"
        )
        add_dependencies(refii_shader_pipeline refii_shader_pipeline_shared_output)
    endif()

    set(REFII_SHADER_VALIDATION_COMMAND
        $<TARGET_FILE:refii-shader-sbk>
        build
        --input-dir
        "${REFII_SHADER_SBK_INPUT_DIR}"
        --extract-out
        "${REFII_SHADER_VALIDATION_EXTRACT_OUT_DIR}"
        --manifest
        "${REFII_SHADER_VALIDATION_EXTRACT_MANIFEST}"
        --translate-out
        "${REFII_SHADER_VALIDATION_TRANSLATE_OUT_DIR}"
        --report
        "${REFII_SHADER_VALIDATION_TRANSLATION_REPORT}"
        --targets
        "${REFII_SHADER_VALIDATION_TARGETS}"
        --dedupe-identical-blobs
        "${REFII_SHADER_DEDUPE_IDENTICAL_BLOBS_ARG}"
        --translator-timeout-ms
        "${REFII_SHADER_TRANSLATOR_TIMEOUT_MS}"
    )
    if(REFII_SHADER_PIPELINE_TRANSLATOR_ARG)
        list(APPEND REFII_SHADER_VALIDATION_COMMAND
            --translator
            "${REFII_SHADER_PIPELINE_TRANSLATOR_ARG}"
        )
    endif()

    add_custom_command(
        OUTPUT "${REFII_SHADER_VALIDATION_PIPELINE_STAMP}"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_SHADER_VALIDATION_OUTPUT_DIR}"
        COMMAND ${REFII_SHADER_VALIDATION_COMMAND}
        COMMAND ${CMAKE_COMMAND} -E touch "${REFII_SHADER_VALIDATION_PIPELINE_STAMP}"
        DEPENDS
            refii-shader-sbk
            ${REFII_SHADER_PIPELINE_TRANSLATOR_DEPENDS}
            "${REFII_SHADER_SBK_INPUT_DIR}/Shaders.sbk"
            "${REFII_SHADER_SBK_INPUT_DIR}/ShadersRelease.sbk"
        VERBATIM
        COMMAND_EXPAND_LISTS
    )

    add_custom_target(refii_shader_pipeline_validate_all_targets
        DEPENDS "${REFII_SHADER_VALIDATION_PIPELINE_STAMP}"
    )

    add_dependencies(refii_generated refii_shader_pipeline)
    if(TARGET refii)
        add_dependencies(refii refii_shader_pipeline)
    endif()
endif()

if(REFII_BUILD_LAUNCHER)
    set(REFII_ROOT_DIR ${CMAKE_CURRENT_SOURCE_DIR})
    add_subdirectory(src/launcher)
endif()
