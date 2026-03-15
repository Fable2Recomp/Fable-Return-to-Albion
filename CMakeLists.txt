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

set(REFII_CORE_SOURCES
    src/refii_host.cpp
    ${GENERATED_SOURCES}
    ${REXGLUE_SHARE_DIR}/rex_app.cpp
    #src/hooks.cpp
)

add_library(refii_core STATIC ${REFII_CORE_SOURCES})
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

if(UNIX AND NOT APPLE)
    find_package(PkgConfig REQUIRED)
    find_package(X11 REQUIRED)
    pkg_check_modules(GTK3 REQUIRED gtk+-3.0)
    target_include_directories(refii_core PUBLIC ${GTK3_INCLUDE_DIRS})
    target_link_libraries(refii_core PUBLIC ${GTK3_LIBRARIES})
    target_link_libraries(refii_core PUBLIC X11::X11)
    target_link_options(refii_core
        INTERFACE
            -Wl,--whole-archive
            $<TARGET_FILE:rex::kernel>
            -Wl,--no-whole-archive
            -Wl,--no-relax
    )
    target_compile_options(refii_core PRIVATE -mcmodel=large)
endif()

if(MSVC)
    target_compile_options(refii_core PRIVATE /EHa)
else()
    target_compile_options(refii_core PRIVATE -msse4.1)
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

    target_link_libraries(refii PRIVATE refii_core)

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
