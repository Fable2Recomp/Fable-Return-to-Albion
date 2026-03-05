# refii - ReXGlue Recompiled Project
#
# This file is yours to edit. 'rexglue migrate' will NOT overwrite it.
# SDK boilerplate lives in generated/rexglue.cmake.

cmake_minimum_required(VERSION 3.25)
project(refii LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

include(generated/rexglue.cmake)

# Sources
set(REFII_SOURCES
    src/main.cpp
    src/hooks.cpp
)

if(WIN32)
    add_executable(refii WIN32 ${REFII_SOURCES})
else()
    add_executable(refii ${REFII_SOURCES})
endif()

rexglue_setup_target(refii)

