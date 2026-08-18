# The neural upscaler: §8's Real-ESRGAN, behind the C ABI in include/refii/upscaler.h.
#
# OFF BY DEFAULT at the top level, for the same reason REFII_BUILD_RENDERER_HOOKS is: ncnn plus
# glslang is a large build, and nobody should pay for it to get a game that renders through Godot.
# Turn it on with -DREFII_BUILD_UPSCALER=ON.

# Everything ncnn builds that we do not use, turned off before the subdirectory is added.
# The tools, examples, benchmarks and tests are most of its build time and none of them ship.
set(NCNN_BUILD_TOOLS OFF CACHE BOOL "" FORCE)
set(NCNN_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(NCNN_BUILD_BENCHMARK OFF CACHE BOOL "" FORCE)
set(NCNN_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(NCNN_PYTHON OFF CACHE BOOL "" FORCE)
set(NCNN_INSTALL_SDK OFF CACHE BOOL "" FORCE)

# STATIC into our shared library, so Godot loads one file. A shared ncnn beside it would be a
# second thing to stage and a second thing to get wrong at load time.
set(NCNN_SHARED_LIB OFF CACHE BOOL "" FORCE)

# Vulkan compute is the entire point — §8 asks for embedded inference that keeps the device alive.
set(NCNN_VULKAN ON CACHE BOOL "" FORCE)

# ncnn's bundled glslang. The system one is not required to be present, and pinning to the
# submodule keeps the shader compiler that ncnn was tested against.
set(NCNN_SYSTEM_GLSLANG OFF CACHE BOOL "" FORCE)

set(REFII_UPSCALER_NCNN_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../thirdparty/ncnn")

if(NOT EXISTS "${REFII_UPSCALER_NCNN_ROOT}/CMakeLists.txt")
    message(FATAL_ERROR
        "src/thirdparty/ncnn is empty. Run:\n"
        "  git submodule update --init src/thirdparty/ncnn\n"
        "  (cd src/thirdparty/ncnn && git submodule update --init glslang)")
endif()

if(NOT EXISTS "${REFII_UPSCALER_NCNN_ROOT}/glslang/CMakeLists.txt")
    message(FATAL_ERROR
        "ncnn's glslang submodule is empty, and NCNN_VULKAN needs it. Run:\n"
        "  (cd src/thirdparty/ncnn && git submodule update --init glslang)")
endif()

add_subdirectory("${REFII_UPSCALER_NCNN_ROOT}" "${CMAKE_CURRENT_BINARY_DIR}/ncnn" EXCLUDE_FROM_ALL)

add_library(refii_upscaler SHARED
    src/upscaler.cpp
)

set_target_properties(refii_upscaler PROPERTIES
    CXX_STANDARD 17
    CXX_STANDARD_REQUIRED ON
    POSITION_INDEPENDENT_CODE ON
)

target_include_directories(refii_upscaler
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
)

target_link_libraries(refii_upscaler PRIVATE ncnn)

# Staged beside the other native libraries Godot loads, exactly as refii_binkdecode is.
#
# The directory is a cache variable so this component can be configured STANDALONE — building it
# on its own avoids running the main project's rexglue codegen, which writes into the source tree
# and must not run concurrently with somebody else's build.
# PER-ARCHITECTURE, matching where every other native is staged and where
# Engine_AppRoot.BinDirectory looks. The old default was the pre-split flat Godot/build/bin;
# once the rest moved to Godot/build/<arch>/bin this kept staging one directory over, so the
# library and its weights sat somewhere nothing loaded from and the neural backend declined
# with the classical resampler taking over silently.
if(NOT REFII_GODOT_ARCH)
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64|ARM64)$")
        set(REFII_GODOT_ARCH "arm64")
    else()
        set(REFII_GODOT_ARCH "x86_64")
    endif()
endif()

set(REFII_UPSCALER_STAGE_DIR "${CMAKE_CURRENT_LIST_DIR}/../../Godot/build/${REFII_GODOT_ARCH}/bin"
    CACHE PATH "Where refii_upscaler is staged for Godot to load")

add_custom_command(TARGET refii_upscaler POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_UPSCALER_STAGE_DIR}"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:refii_upscaler>"
        "${REFII_UPSCALER_STAGE_DIR}/"
    COMMENT "Staging refii_upscaler to ${REFII_UPSCALER_STAGE_DIR}"
)
