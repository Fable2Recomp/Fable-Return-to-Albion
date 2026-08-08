# Generated game module — built ON THE USER'S MACHINE at install time.
#
# This project is copied into <install>/Generated/ next to the code the ReXGlue
# generator produced from the user's own game image, and built there into one shared
# library that the runtime dlopens.
#
# It is deliberately standalone: it does NOT participate in the refii build, does not
# know where the refii source tree is, and needs nothing on the machine beyond the
# toolchain the installer bundles. The only inputs are the generated sources and a
# prebuilt ReXGlue runtime.
#
# WHY NOT part of the main build: the generated C++ is derived from the user's game and
# cannot be redistributed, so it cannot exist on a build machine. Compiling it here is
# what keeps ReXGlue shipping only its own runtime, SDK and generator.

cmake_minimum_required(VERSION 3.24)

project(game_recompiled LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

# --- Inputs -----------------------------------------------------------------------
#
# REXGLUE_RUNTIME_DIR   the shipped runtime: headers + import libraries
# GAME_HASH             hash of the executable this was generated from, for build-info
# CODEGEN_VERSION       generator version, for rebuild detection

set(REXGLUE_RUNTIME_DIR "" CACHE PATH "Shipped ReXGlue runtime (include/ and lib/)")
set(GAME_HASH "" CACHE STRING "Hash of the source game executable")
set(CODEGEN_VERSION "" CACHE STRING "ReXGlue code generator version")
set(RUNTIME_VERSION "" CACHE STRING "ReXGlue runtime version")

if(NOT REXGLUE_RUNTIME_DIR OR NOT EXISTS "${REXGLUE_RUNTIME_DIR}")
    message(FATAL_ERROR
        "REXGLUE_RUNTIME_DIR must point at the shipped ReXGlue runtime. "
        "The installer sets this; it is not discoverable.")
endif()

# The generator emits this next to the sources. It defines GENERATED_SOURCES.
if(NOT EXISTS "${CMAKE_CURRENT_LIST_DIR}/sources.cmake")
    message(FATAL_ERROR
        "sources.cmake not found beside this project. Run the ReXGlue generator first.")
endif()
include("${CMAKE_CURRENT_LIST_DIR}/sources.cmake")

# --- The module -------------------------------------------------------------------

add_library(game_recompiled SHARED ${GENERATED_SOURCES})

target_include_directories(game_recompiled
    PRIVATE
        "${CMAKE_CURRENT_LIST_DIR}"
        "${CMAKE_CURRENT_LIST_DIR}/include"
        "${REXGLUE_RUNTIME_DIR}/include"
)

# SEH: the recompiled code relies on structured exception handling, and MSVC only
# honours it under /EHa. sources.cmake says so in its own header comment; getting this
# wrong produces a module that builds and then unwinds incorrectly at runtime.
target_compile_options(game_recompiled
    PRIVATE
        $<$<CXX_COMPILER_ID:MSVC>:/EHa>
)

# Optimised even in a Debug install: the recompiled guest is hot code and an unoptimised
# build of it is unusably slow. Debug info is kept.
target_compile_options(game_recompiled
    PRIVATE
        $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-O2 -g>
)

find_library(REXGLUE_RUNTIME_LIBRARY
    NAMES rexruntime rexruntimed
    HINTS "${REXGLUE_RUNTIME_DIR}/lib" "${REXGLUE_RUNTIME_DIR}/bin"
    REQUIRED
)
target_link_libraries(game_recompiled PRIVATE "${REXGLUE_RUNTIME_LIBRARY}")

set_target_properties(game_recompiled PROPERTIES
    OUTPUT_NAME game_recompiled
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_LIST_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_LIST_DIR}"
)

# --- Build metadata ---------------------------------------------------------------
#
# Written beside the module so the installer can decide whether a rebuild is needed
# without reading the module itself. Any of these changing invalidates it.

string(TIMESTAMP _built_utc "%Y-%m-%dT%H:%M:%SZ" UTC)
set(_build_info "${CMAKE_CURRENT_LIST_DIR}/build-info.json")

add_custom_command(TARGET game_recompiled POST_BUILD
    COMMAND ${CMAKE_COMMAND}
        -DOUT="${_build_info}"
        -DGAME_HASH="${GAME_HASH}"
        -DCODEGEN_VERSION="${CODEGEN_VERSION}"
        -DRUNTIME_VERSION="${RUNTIME_VERSION}"
        -DCOMPILER="${CMAKE_CXX_COMPILER_ID}-${CMAKE_CXX_COMPILER_VERSION}"
        -DCMAKE_VER="${CMAKE_VERSION}"
        -DPLATFORM="${CMAKE_SYSTEM_NAME}-${CMAKE_SYSTEM_PROCESSOR}"
        -DBUILT_UTC="${_built_utc}"
        -P "${CMAKE_CURRENT_LIST_DIR}/write-build-info.cmake"
    COMMENT "Writing build-info.json"
    VERBATIM
)
