set(REFII_SHADER_ROOT ${CMAKE_CURRENT_LIST_DIR})
if(NOT DEFINED REFII_SHADER_TOOL_OUTPUT_DIR OR REFII_SHADER_TOOL_OUTPUT_DIR STREQUAL "")
    set(REFII_SHADER_TOOL_OUTPUT_DIR "${CMAKE_BINARY_DIR}/pipeline/shader_private/tools")
endif()

set(REFII_SHADER_WORKSPACE_SOURCES
    ${REFII_SHADER_ROOT}/src/sha256.cpp
    ${REFII_SHADER_ROOT}/src/shader_sbk_pipeline.cpp
)

find_package(ZLIB REQUIRED)

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

set_target_properties(refii-shader-sbk PROPERTIES
    OUTPUT_NAME refii-shader-sbk
    POSITION_INDEPENDENT_CODE ON
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
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
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
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
elif(APPLE)
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

if(NOT EXISTS "${REFII_SHADER_DXC_TOOL}")
    message(FATAL_ERROR "Missing vendored DXC tool at ${REFII_SHADER_DXC_TOOL}")
endif()

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
    OUTPUT_NAME refii-shader-xenos-translator
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)
if(WIN32)
    set_target_properties(refii-shader-xenos-translator PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

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
endif()
