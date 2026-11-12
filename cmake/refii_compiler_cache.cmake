# Automatically wires sccache or ccache as the C/C++ compiler launcher (and Rust wrapper when
# sccache is present) unless the developer explicitly specified a launcher or turned
# REFII_COMPILER_CACHE=OFF.

include_guard(GLOBAL)

option(REFII_COMPILER_CACHE "Automatically use sccache or ccache when found on PATH" ON)

function(refii_configure_compiler_cache)
    if(NOT REFII_COMPILER_CACHE)
        return()
    endif()

    if(CMAKE_C_COMPILER_LAUNCHER OR CMAKE_CXX_COMPILER_LAUNCHER)
        return()
    endif()

    find_program(REFII_COMPILER_CACHE_PROGRAM NAMES sccache ccache)
    if(NOT REFII_COMPILER_CACHE_PROGRAM)
        return()
    endif()

    set(CMAKE_C_COMPILER_LAUNCHER "${REFII_COMPILER_CACHE_PROGRAM}" CACHE FILEPATH
        "Compiler launcher used to cache C translation units" FORCE)
    set(CMAKE_CXX_COMPILER_LAUNCHER "${REFII_COMPILER_CACHE_PROGRAM}" CACHE FILEPATH
        "Compiler launcher used to cache C++ translation units" FORCE)

    get_filename_component(_cache_name "${REFII_COMPILER_CACHE_PROGRAM}" NAME_WE)
    if(_cache_name STREQUAL "sccache" AND NOT DEFINED ENV{RUSTC_WRAPPER})
        set(ENV{RUSTC_WRAPPER} "${REFII_COMPILER_CACHE_PROGRAM}")
    endif()

    message(STATUS "Refii: enabled compiler cache launcher ${REFII_COMPILER_CACHE_PROGRAM}")
endfunction()
