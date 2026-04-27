set(REFII_SHADER_ROOT ${CMAKE_CURRENT_LIST_DIR})
if(NOT DEFINED REFII_SHADER_TOOL_OUTPUT_DIR OR REFII_SHADER_TOOL_OUTPUT_DIR STREQUAL "")
    set(REFII_SHADER_TOOL_OUTPUT_DIR "${CMAKE_BINARY_DIR}/pipeline/shader_private/tools")
endif()

set(REFII_SHADER_WORKSPACE_SOURCES
    ${REFII_SHADER_ROOT}/src/sha256.cpp
    ${REFII_SHADER_ROOT}/src/shader_sbk_pipeline.cpp
)

find_package(ZLIB REQUIRED)

add_library(refii_shader_sbk_workspace STATIC
    ${REFII_SHADER_WORKSPACE_SOURCES}
)
target_include_directories(refii_shader_sbk_workspace
    PUBLIC
        ${REFII_SHADER_ROOT}/include
)
target_link_libraries(refii_shader_sbk_workspace
    PUBLIC
        ZLIB::ZLIB
)
set_target_properties(refii_shader_sbk_workspace PROPERTIES
    POSITION_INDEPENDENT_CODE ON
)

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
set(ZSTD_BUILD_PROGRAMS OFF CACHE BOOL "" FORCE)
set(ZSTD_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(ZSTD_BUILD_CONTRIB OFF CACHE BOOL "" FORCE)

add_subdirectory(
    "${REFII_SHADER_XENOSRECOMP_ROOT}"
    "${CMAKE_CURRENT_BINARY_DIR}/thirdparty/XenosRecomp"
    EXCLUDE_FROM_ALL
)

set_target_properties(XenosRecomp PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${REFII_SHADER_TOOL_OUTPUT_DIR}"
)

add_executable(refii-shader-sbk EXCLUDE_FROM_ALL
    ${REFII_SHADER_ROOT}/src/refii_shader_sbk_cli_main.cpp
)
target_link_libraries(refii-shader-sbk
    PRIVATE
        refii_shader_sbk_workspace
)
set_target_properties(refii-shader-sbk PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${REFII_SHADER_TOOL_OUTPUT_DIR}"
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

if(NOT EXISTS "${REFII_SHADER_DXC_TOOL}")
    message(FATAL_ERROR "Missing vendored DXC tool at ${REFII_SHADER_DXC_TOOL}")
endif()

add_executable(refii-shader-xenos-translator EXCLUDE_FROM_ALL
    ${REFII_SHADER_ROOT}/src/refii_shader_xenos_translator_main.cpp
)
add_dependencies(refii-shader-xenos-translator XenosRecomp)
target_compile_definitions(refii-shader-xenos-translator
    PRIVATE
        REFII_XENOSRECOMP_TOOL_PATH="$<TARGET_FILE:XenosRecomp>"
        REFII_XENOSRECOMP_SHADER_COMMON_PATH="${REFII_SHADER_XENOSRECOMP_ROOT}/XenosRecomp/shader_common.h"
        REFII_XENOS_DXC_TOOL_PATH="${REFII_SHADER_DXC_TOOL}"
        REFII_XENOS_DXC_LIB_DIR="${REFII_SHADER_DXC_LIB_DIR}"
)
set_target_properties(refii-shader-xenos-translator PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${REFII_SHADER_TOOL_OUTPUT_DIR}"
)
