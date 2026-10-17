set(REFII_SHADER_ROOT ${CMAKE_CURRENT_LIST_DIR})

set(REFII_SHADER_WORKSPACE_SOURCES
    ${REFII_SHADER_ROOT}/src/sha256.cpp
    ${REFII_SHADER_ROOT}/src/shader_sbk_pipeline.cpp
    # The C ABI the launcher P/Invokes (include/shader/refii_shader_build.h).
    ${REFII_SHADER_ROOT}/src/refii_shader_build.cpp
)

set(REFII_SHADER_EXTRACTOR_ZLIB_ROOT "${REFII_SHADER_ROOT}/../extractor/thirdparty/zlib")

if(NOT TARGET ZLIB::ZLIB)
    if(EXISTS "${REFII_SHADER_EXTRACTOR_ZLIB_ROOT}/CMakeLists.txt")
        set(ZLIB_BUILD_SHARED OFF CACHE BOOL "" FORCE)
        set(ZLIB_BUILD_STATIC ON CACHE BOOL "" FORCE)
        set(ZLIB_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
        set(ZLIB_BUILD_TESTING OFF CACHE BOOL "" FORCE)
        add_subdirectory(
            "${REFII_SHADER_EXTRACTOR_ZLIB_ROOT}"
            "${CMAKE_CURRENT_BINARY_DIR}/thirdparty/zlib"
            EXCLUDE_FROM_ALL
        )
        if(TARGET ZLIB::ZLIB)
            # keep as-is
        elseif(TARGET zlibstatic)
            add_library(ZLIB::ZLIB ALIAS zlibstatic)
        elseif(TARGET zlib)
            add_library(ZLIB::ZLIB ALIAS zlib)
        else()
            message(FATAL_ERROR
                "Found vendored zlib at ${REFII_SHADER_EXTRACTOR_ZLIB_ROOT} "
                "but no zlib CMake target was created.")
        endif()
        if(TARGET zlibstatic)
            set_target_properties(zlibstatic PROPERTIES POSITION_INDEPENDENT_CODE ON)
        endif()
        if(TARGET zlib)
            set_target_properties(zlib PROPERTIES POSITION_INDEPENDENT_CODE ON)
        endif()
    else()
        find_package(ZLIB REQUIRED)
    endif()
endif()

if(NOT TARGET ZLIB::ZLIB)
    message(FATAL_ERROR
        "Could not resolve a zlib target for shader module; "
        "expected either vendored zlib at ${REFII_SHADER_EXTRACTOR_ZLIB_ROOT} "
        "or a system zlib install.")
endif()

add_library(refii-shader-sbk SHARED
    ${REFII_SHADER_WORKSPACE_SOURCES}
)

target_include_directories(refii-shader-sbk
    PUBLIC
        ${REFII_SHADER_ROOT}/include
)

target_link_libraries(refii-shader-sbk
    PUBLIC
        ZLIB::ZLIB
)

# UNDERSCORED FILE NAMES, like every other refii library: the Godot side resolves only
# `refii_*` names into bin/ (Engine_NativeLibraries), so a dashed file was never found there.
set_target_properties(refii-shader-sbk PROPERTIES
    OUTPUT_NAME refii_shader_sbk
    POSITION_INDEPENDENT_CODE ON
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
)

if(WIN32)
    set_target_properties(refii-shader-sbk PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

add_library(refii_shader_sbk_workspace ALIAS refii-shader-sbk)

set(REFII_SHADER_THIRDPARTY_ROOT "${REFII_SHADER_ROOT}/thirdparty")
set(REFII_SHADER_XENOSRECOMP_ROOT "${REFII_SHADER_THIRDPARTY_ROOT}/XenosRecomp")

if(NOT EXISTS "${REFII_SHADER_XENOSRECOMP_ROOT}/CMakeLists.txt")
    message(FATAL_ERROR
        "Missing vendored XenosRecomp snapshot at ${REFII_SHADER_XENOSRECOMP_ROOT}"
    )
endif()

# Avoid compiling upstream third-party tests/tools when consumed as vendored code.
set(FMT_DOC OFF CACHE BOOL "" FORCE)
set(FMT_TEST OFF CACHE BOOL "" FORCE)
set(FMT_FUZZ OFF CACHE BOOL "" FORCE)
set(FMT_CUDA_TEST OFF CACHE BOOL "" FORCE)
set(FMT_INSTALL OFF CACHE BOOL "" FORCE)
set(XXHASH_BUILD_XXHSUM OFF CACHE BOOL "" FORCE)
set(ZSTD_BUILD_PROGRAMS OFF CACHE BOOL "" FORCE)
set(ZSTD_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(ZSTD_BUILD_CONTRIB OFF CACHE BOOL "" FORCE)

add_subdirectory(
    "${REFII_SHADER_XENOSRECOMP_ROOT}"
    "${CMAKE_CURRENT_BINARY_DIR}/thirdparty/XenosRecomp"
    EXCLUDE_FROM_ALL
)

set_target_properties(XenosRecomp PROPERTIES
    OUTPUT_NAME XenosRecomp
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)

string(TOLOWER "${CMAKE_SYSTEM_PROCESSOR}" REFII_SHADER_SYSTEM_PROCESSOR)
set(REFII_SHADER_DXC_ARCH "x64")
if(REFII_SHADER_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64)$")
    set(REFII_SHADER_DXC_ARCH "arm64")
endif()

set(REFII_SHADER_DXC_ROOT "${REFII_SHADER_XENOSRECOMP_ROOT}/thirdparty/dxc-bin")
if(WIN32)
    set(REFII_SHADER_DXC_TOOL "${REFII_SHADER_DXC_ROOT}/bin/${REFII_SHADER_DXC_ARCH}/dxc.exe")
    set(REFII_SHADER_DXC_LIB_DIR "${REFII_SHADER_DXC_ROOT}/bin/${REFII_SHADER_DXC_ARCH}")
elseif(APPLE)
    set(REFII_SHADER_DXC_TOOL "${REFII_SHADER_DXC_ROOT}/bin/${REFII_SHADER_DXC_ARCH}/dxc-macos")
    set(REFII_SHADER_DXC_LIB_DIR "${REFII_SHADER_DXC_ROOT}/lib/${REFII_SHADER_DXC_ARCH}")
else()
    if(REFII_SHADER_DXC_ARCH STREQUAL "arm64")
        message(FATAL_ERROR
            "Vendored dxc-bin does not currently provide dxc-linux for arm64 hosts"
        )
    endif()
    set(REFII_SHADER_DXC_TOOL "${REFII_SHADER_DXC_ROOT}/bin/x64/dxc-linux")
    set(REFII_SHADER_DXC_LIB_DIR "${REFII_SHADER_DXC_ROOT}/lib/x64")
endif()

set(REFII_SHADER_DXC_UPSTREAM_URL
    "https://github.com/renderbag/dxc-bin.git"
    CACHE STRING
    "Upstream repository supplying the vendored DXC binaries"
)

if(NOT EXISTS "${REFII_SHADER_DXC_TOOL}")
    set(REFII_SHADER_DXC_UPSTREAM_DIR "${CMAKE_BINARY_DIR}/dxc-bin-upstream")
    if(NOT EXISTS "${REFII_SHADER_DXC_UPSTREAM_DIR}/.git")
        find_package(Git REQUIRED)
        message(STATUS "Fetching vendored DXC binaries from ${REFII_SHADER_DXC_UPSTREAM_URL}")
        file(REMOVE_RECURSE "${REFII_SHADER_DXC_UPSTREAM_DIR}")
        execute_process(
            COMMAND "${GIT_EXECUTABLE}" clone --depth 1
                    "${REFII_SHADER_DXC_UPSTREAM_URL}"
                    "${REFII_SHADER_DXC_UPSTREAM_DIR}"
            RESULT_VARIABLE REFII_SHADER_DXC_CLONE_RESULT
        )
        if(NOT REFII_SHADER_DXC_CLONE_RESULT EQUAL 0)
            message(FATAL_ERROR
                "Failed to clone DXC binaries from ${REFII_SHADER_DXC_UPSTREAM_URL}"
            )
        endif()
    endif()
    file(COPY
        "${REFII_SHADER_DXC_UPSTREAM_DIR}/bin"
        "${REFII_SHADER_DXC_UPSTREAM_DIR}/lib"
        DESTINATION "${REFII_SHADER_DXC_ROOT}"
    )
    if(NOT WIN32 AND EXISTS "${REFII_SHADER_DXC_TOOL}")
        file(CHMOD "${REFII_SHADER_DXC_TOOL}"
            PERMISSIONS
                OWNER_READ OWNER_WRITE OWNER_EXECUTE
                GROUP_READ GROUP_EXECUTE
                WORLD_READ WORLD_EXECUTE
        )
    endif()
endif()

if(NOT EXISTS "${REFII_SHADER_DXC_TOOL}")
    message(FATAL_ERROR "Missing vendored DXC tool at ${REFII_SHADER_DXC_TOOL}")
endif()

# What the translator needs beside itself at runtime, in two places. refii_stage_godot
# (src/hostbridge/CMakeLists.txt) stages them; the translator looks for bin/dxc before the
# compile-time paths above, which name this tree's vendored copies and exist on no player's
# machine.
#
# THE COMPILER'S LIBRARIES GO IN bin/ ITSELF: XenosRecomp links libdxcompiler, so the loader
# has to find it where XenosRecomp is (RPATH $ORIGIN on Linux, the DLL's own directory on
# Windows). The dxc tool and the common header go under bin/dxc, and the tool is launched with
# bin/ on its library path.
set(_refii_shader_tool_files
    "${REFII_SHADER_DXC_TOOL}"
    "${REFII_SHADER_XENOSRECOMP_ROOT}/XenosRecomp/shader_common.h"
)
if(WIN32)
    set(_refii_shader_library_files
        "${REFII_SHADER_DXC_LIB_DIR}/dxcompiler.dll"
        "${REFII_SHADER_DXC_LIB_DIR}/dxil.dll"
    )
elseif(APPLE)
    set(_refii_shader_library_files
        "${REFII_SHADER_DXC_LIB_DIR}/libdxcompiler.dylib"
    )
else()
    set(_refii_shader_library_files
        "${REFII_SHADER_DXC_LIB_DIR}/libdxcompiler.so"
        "${REFII_SHADER_DXC_LIB_DIR}/libdxil.so"
    )
endif()
set(REFII_SHADER_TOOL_FILES "${_refii_shader_tool_files}" PARENT_SCOPE)
set(REFII_SHADER_LIBRARY_FILES "${_refii_shader_library_files}" PARENT_SCOPE)


add_library(refii-shader-xenos-translator SHARED
    ${REFII_SHADER_ROOT}/src/refii_shader_xenos_translator_main.cpp
)
add_dependencies(refii-shader-xenos-translator XenosRecomp)
target_include_directories(refii-shader-xenos-translator
    PUBLIC
        ${REFII_SHADER_ROOT}/include
)
target_link_libraries(refii-shader-xenos-translator
    PRIVATE
        XenosRecomp
)
target_compile_definitions(refii-shader-xenos-translator
    PRIVATE
        XENOSRECOMP_API_IMPORT
        REFII_XENOSRECOMP_SHADER_COMMON_PATH="${REFII_SHADER_XENOSRECOMP_ROOT}/XenosRecomp/shader_common.h"
        REFII_XENOS_DXC_TOOL_PATH="${REFII_SHADER_DXC_TOOL}"
        REFII_XENOS_DXC_LIB_DIR="${REFII_SHADER_DXC_LIB_DIR}"
)
set_target_properties(refii-shader-xenos-translator PROPERTIES
    OUTPUT_NAME refii_shader_xenos_translator
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)
if(WIN32)
    set_target_properties(refii-shader-xenos-translator PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

# And beside XenosRecomp in the build tree as well. Its RPATH is $ORIGIN alone, so without this
# the build tree's copy cannot load — and the Godot resolver takes the NEWEST copy it finds
# across the staging directory and every out/build/*/lib, so an unloadable build-tree copy is
# one it can pick. Hung off the translator, which this directory defines and which links
# XenosRecomp: add_custom_command(TARGET) can only name a target of its own directory.
add_custom_command(TARGET refii-shader-xenos-translator POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        ${_refii_shader_library_files} "$<TARGET_FILE_DIR:XenosRecomp>"
    COMMENT "Placing the DirectX Shader Compiler beside XenosRecomp"
    COMMAND_EXPAND_LISTS
    VERBATIM
)

target_link_libraries(refii-shader-sbk
    PUBLIC
        refii-shader-xenos-translator
)

if(REFII_BUILD_TOOLS)
    add_executable(refii-shader-sbk-tool
        ${REFII_SHADER_ROOT}/src/refii_shader_sbk_cli_main.cpp
    )
    target_link_libraries(refii-shader-sbk-tool
        PRIVATE
            refii-shader-sbk
    )
    set_target_properties(refii-shader-sbk-tool PROPERTIES
        OUTPUT_NAME refii-shader-sbk
        CXX_STANDARD 23
        CXX_STANDARD_REQUIRED ON
        RUNTIME_OUTPUT_DIRECTORY "${REFII_TOOL_OUTPUT_DIR}"
        BUILD_RPATH "$ORIGIN/../lib"
        INSTALL_RPATH "$ORIGIN/../lib"
    )
    refii_stage_windows_runtime_dlls(refii-shader-sbk-tool)

    add_executable(refii-shader-xenos-translator-tool
        ${REFII_SHADER_ROOT}/src/refii_shader_xenos_translator_cli_main.cpp
    )
    target_link_libraries(refii-shader-xenos-translator-tool
        PRIVATE
            refii-shader-xenos-translator
    )
    set_target_properties(refii-shader-xenos-translator-tool PROPERTIES
        OUTPUT_NAME refii-shader-xenos-translator
        CXX_STANDARD 23
        CXX_STANDARD_REQUIRED ON
        RUNTIME_OUTPUT_DIRECTORY "${REFII_TOOL_OUTPUT_DIR}"
        BUILD_RPATH "$ORIGIN/../lib"
        INSTALL_RPATH "$ORIGIN/../lib"
    )
    refii_stage_windows_runtime_dlls(refii-shader-xenos-translator-tool)
endif()
