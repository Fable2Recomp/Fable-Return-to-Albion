set(REFII_RENDERER_ROOT ${CMAKE_CURRENT_LIST_DIR})

set(REFII_RENDERER_INCLUDE_DIRS
    ${REFII_RENDERER_ROOT}/include
)

set(REFII_RENDERER_RUNTIME_SOURCES
    ${REFII_RENDERER_ROOT}/src/imgui/plume_imgui_overlay.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/cvars.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/hook_observer.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/draw/plume_draw_state.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/direct/fable_hook_symbols.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/direct/fable_direct_present_state.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/direct/fable_direct_renderer.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/direct/fable_direct_state.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/direct/fable_guest_resource_registry.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/general/plume_api_bridge.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/general/plume_backend.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/general/plume_diagnostics.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/general/plume_fable_addressing.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/general/plume_graphics_system.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_resource_lifetime.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_resource_state.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_resource_types.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_surface_shape.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_texture_formats.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_texture_lookup.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_texture_metadata.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_texture_upload.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_texture_views.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/plume/resource/plume_vertex_declaration.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/renderdoc_debugger.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/replacement_health.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/renderer_runtime.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/shader_library.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/d3d12/plume_d3d12_bridge.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/metal/plume_metal_bridge.cpp
    ${REFII_RENDERER_ROOT}/src/runtime/vulkan/plume_vulkan_bridge.cpp
)

set(REFII_RENDERER_PPC_HOOK_SOURCES
    ${REFII_RENDERER_ROOT}/src/hooks.cpp
    ${REFII_RENDERER_ROOT}/src/hooks/d3d_pass_through.cpp
)

set(REFII_RENDERER_HOOK_PARTS
    ${REFII_RENDERER_ROOT}/include/renderer_hook_targets.h
    ${REFII_RENDERER_ROOT}/include/renderer_hooks_private.h
    ${REFII_RENDERER_ROOT}/include/renderer_runtime.h
    ${REFII_RENDERER_ROOT}/include/renderdoc_debugger.h
    ${REFII_RENDERER_ROOT}/include/runtime/cvars.h
    ${REFII_RENDERER_ROOT}/include/runtime/hook_observer.h
    ${REFII_RENDERER_ROOT}/include/runtime/replacement_health.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/draw/plume_draw_state.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/direct/fable_direct_present_state.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/direct/fable_direct_renderer.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/direct/fable_direct_state.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/direct/fable_guest_resource_registry.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/direct/fable_hook_symbols.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/direct/fable_observer_resources.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/direct/fable_synthetic_draws.inl
    ${REFII_RENDERER_ROOT}/include/runtime/plume/direct/fable_video_uploads.inl
    ${REFII_RENDERER_ROOT}/include/runtime/plume/general/plume_api_bridge.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/general/plume_api_bridge_private.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/general/plume_backend.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/general/plume_diagnostics.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/general/plume_graphics_system.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/general/plume_imgui_overlay.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/resource/plume_resource_manager_internal.h
    ${REFII_RENDERER_ROOT}/include/runtime/plume/resource/plume_vertex_declaration.h
    ${REFII_RENDERER_ROOT}/include/runtime/shader_library.h
    ${REFII_RENDERER_ROOT}/include/hooks/ppc_externs.h
)
set_source_files_properties(${REFII_RENDERER_HOOK_PARTS}
    PROPERTIES HEADER_FILE_ONLY TRUE
)

set(PLUME_SDL_VULKAN_ENABLED
    "${REFII_SDL3_WINDOW_ENABLED}"
    CACHE BOOL
    "Enable Plume's internal SDL Vulkan integration through the Refii SDL3 window"
    FORCE
)
set(REFII_RENDERER_ENABLE_PLUME_D3D12_AGILITY_SDK OFF)
if(WIN32 AND REFII_RENDERER_PLUME_D3D12_AGILITY_SDK_ENABLED)
    set(REFII_RENDERER_ENABLE_PLUME_D3D12_AGILITY_SDK ON)
endif()
set(PLUME_D3D12_AGILITY_SDK_ENABLED
    "${REFII_RENDERER_ENABLE_PLUME_D3D12_AGILITY_SDK}"
    CACHE BOOL
    "Enable D3D12 Agility SDK"
    FORCE
)

set(REFII_RENDERER_PLUME_DEFAULT_ROOT "")
if(EXISTS "${CMAKE_SOURCE_DIR}/thirdparty/plume/CMakeLists.txt")
    set(REFII_RENDERER_PLUME_DEFAULT_ROOT "${CMAKE_SOURCE_DIR}/thirdparty/plume")
elseif(EXISTS "${CMAKE_SOURCE_DIR}/src/renderer/thirdparty/plume/CMakeLists.txt")
    set(REFII_RENDERER_PLUME_DEFAULT_ROOT "${CMAKE_SOURCE_DIR}/src/renderer/thirdparty/plume")
elseif(EXISTS "/home/loreaxe/SonicUnleshed/UnleashedRecomp/thirdparty/plume/CMakeLists.txt")
    set(REFII_RENDERER_PLUME_DEFAULT_ROOT "/home/loreaxe/SonicUnleshed/UnleashedRecomp/thirdparty/plume")
endif()

set(REFII_RENDERER_PLUME_ROOT
    "${REFII_RENDERER_PLUME_DEFAULT_ROOT}"
    CACHE PATH
    "Path to a Plume checkout with CMakeLists.txt for the renderer RHI"
)
if(NOT REFII_RENDERER_PLUME_ROOT AND REFII_RENDERER_PLUME_DEFAULT_ROOT)
    set(REFII_RENDERER_PLUME_ROOT
        "${REFII_RENDERER_PLUME_DEFAULT_ROOT}"
        CACHE PATH
        "Path to a Plume checkout with CMakeLists.txt for the renderer RHI"
        FORCE
    )
endif()
unset(REFII_RENDERER_PLUME_DEFAULT_ROOT)

if(NOT TARGET plume AND REFII_RENDERER_PLUME_ROOT)
    if(EXISTS "${REFII_RENDERER_PLUME_ROOT}/CMakeLists.txt")
        add_subdirectory(
            "${REFII_RENDERER_PLUME_ROOT}"
            "${CMAKE_CURRENT_BINARY_DIR}/thirdparty/plume"
            EXCLUDE_FROM_ALL
        )
    else()
        message(WARNING
            "REFII_RENDERER_PLUME_ROOT is set to ${REFII_RENDERER_PLUME_ROOT}, "
            "but no CMakeLists.txt was found there. Renderer RHI code will "
            "build without linking Plume until the submodule is populated."
        )
    endif()
endif()

set(REFII_RENDERER_RENDERDOC_ROOT
    "${REFII_RENDERER_ROOT}/thirdparty/renderdoc"
    CACHE PATH
    "Path to a RenderDoc checkout for the renderer graphics debugger API header"
)
set(REFII_RENDERER_RENDERDOC_API_HEADER
    "${REFII_RENDERER_RENDERDOC_ROOT}/renderdoc/api/app/renderdoc_app.h"
)
option(REFII_RENDERER_BUILD_RENDERDOC_RUNTIME
    "Build the vendored RenderDoc capture runtime for the Refii renderer"
    OFF
)
set(REFII_RENDERER_RENDERDOC_BUILD_TYPE
    "RelWithDebInfo"
    CACHE STRING
    "Build type used for the standalone vendored RenderDoc capture runtime"
)
set(REFII_RENDERER_RENDERDOC_BUILD_DIR
    "${CMAKE_BINARY_DIR}/thirdparty/renderdoc-runtime"
    CACHE PATH
    "Build directory for the vendored RenderDoc capture runtime"
)
set(REFII_RENDERER_RENDERDOC_RUNTIME_DIR
    "${CMAKE_BINARY_DIR}/lib"
    CACHE PATH
    "Directory where the vendored RenderDoc runtime is copied for Refii"
)

if(EXISTS "${REFII_RENDERER_RENDERDOC_API_HEADER}" AND
   REFII_RENDERER_BUILD_RENDERDOC_RUNTIME)
    include(ExternalProject)

    if(WIN32)
        set(REFII_RENDERER_RENDERDOC_WIN32_CONFIGURATION
            "Development"
            CACHE STRING
            "Visual Studio configuration used for the vendored RenderDoc runtime"
        )
        set(REFII_RENDERER_RENDERDOC_WIN32_PLATFORM
            "x64"
            CACHE STRING
            "Visual Studio platform used for the vendored RenderDoc runtime"
        )
        find_program(REFII_RENDERER_MSBUILD_EXECUTABLE
            NAMES MSBuild.exe MSBuild
        )
        if(REFII_RENDERER_MSBUILD_EXECUTABLE)
            set(REFII_RENDERER_RENDERDOC_RUNTIME_BUILD_FILE
                "${REFII_RENDERER_RENDERDOC_ROOT}/${REFII_RENDERER_RENDERDOC_WIN32_PLATFORM}/${REFII_RENDERER_RENDERDOC_WIN32_CONFIGURATION}/renderdoc.dll"
            )
            set(REFII_RENDERER_RENDERDOC_RUNTIME_FILE
                "${REFII_RENDERER_RENDERDOC_RUNTIME_DIR}/renderdoc.dll"
            )
            ExternalProject_Add(refii_renderdoc_runtime_project
                SOURCE_DIR "${REFII_RENDERER_RENDERDOC_ROOT}"
                CONFIGURE_COMMAND ""
                BUILD_COMMAND
                    "${REFII_RENDERER_MSBUILD_EXECUTABLE}"
                    "${REFII_RENDERER_RENDERDOC_ROOT}/renderdoc.sln"
                    /m
                    /t:renderdoc
                    /p:Configuration=${REFII_RENDERER_RENDERDOC_WIN32_CONFIGURATION}
                    /p:Platform=${REFII_RENDERER_RENDERDOC_WIN32_PLATFORM}
                INSTALL_COMMAND ""
                BUILD_IN_SOURCE TRUE
                BUILD_BYPRODUCTS "${REFII_RENDERER_RENDERDOC_RUNTIME_BUILD_FILE}"
            )
        else()
            message(WARNING
                "MSBuild was not found; the vendored RenderDoc runtime DLL will "
                "not be built. Set renderer_renderdoc_runtime_path to an "
                "installed renderdoc.dll if capture integration is needed."
            )
        endif()
    elseif(UNIX AND NOT APPLE)
        set(REFII_RENDERER_RENDERDOC_CMAKE_ARGS
            -DENABLE_QRENDERDOC=OFF
            -DENABLE_PYRENDERDOC=OFF
            -DENABLE_RENDERDOCCMD=OFF
            -DENABLE_GL=OFF
            -DENABLE_GLES=OFF
            -DENABLE_EGL=OFF
            -DENABLE_VULKAN=ON
            -DENABLE_XLIB=ON
            -DENABLE_XCB=OFF
            -DBUILD_VERSION_DIST_NAME=Refii
            -DBUILD_VERSION_DIST_VER=dev
            -DBUILD_VERSION_DIST_CONTACT=https://github.com
            -DCMAKE_BUILD_TYPE=${REFII_RENDERER_RENDERDOC_BUILD_TYPE}
        )
        set(REFII_RENDERER_RENDERDOC_RUNTIME_BUILD_FILE
            "${REFII_RENDERER_RENDERDOC_BUILD_DIR}/lib/librenderdoc.so"
        )
        set(REFII_RENDERER_RENDERDOC_RUNTIME_FILE
            "${REFII_RENDERER_RENDERDOC_RUNTIME_DIR}/librenderdoc.so"
        )
        ExternalProject_Add(refii_renderdoc_runtime_project
            SOURCE_DIR "${REFII_RENDERER_RENDERDOC_ROOT}"
            BINARY_DIR "${REFII_RENDERER_RENDERDOC_BUILD_DIR}"
            CMAKE_ARGS ${REFII_RENDERER_RENDERDOC_CMAKE_ARGS}
            BUILD_COMMAND
                ${CMAKE_COMMAND} --build <BINARY_DIR> --target renderdoc --config $<CONFIG>
            INSTALL_COMMAND ""
            BUILD_BYPRODUCTS "${REFII_RENDERER_RENDERDOC_RUNTIME_BUILD_FILE}"
        )
    endif()

    if(TARGET refii_renderdoc_runtime_project AND
       REFII_RENDERER_RENDERDOC_RUNTIME_BUILD_FILE)
        add_custom_command(
            OUTPUT "${REFII_RENDERER_RENDERDOC_RUNTIME_FILE}"
            COMMAND ${CMAKE_COMMAND} -E make_directory
                    "${REFII_RENDERER_RENDERDOC_RUNTIME_DIR}"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                    "${REFII_RENDERER_RENDERDOC_RUNTIME_BUILD_FILE}"
                    "${REFII_RENDERER_RENDERDOC_RUNTIME_FILE}"
            DEPENDS refii_renderdoc_runtime_project
            VERBATIM
        )
        add_custom_target(refii_renderdoc_runtime
            DEPENDS "${REFII_RENDERER_RENDERDOC_RUNTIME_FILE}"
        )
    endif()
endif()

set(REFII_RENDERER_DXC_DEFAULT "")
set(REFII_RENDERER_DXC_LIB_DEFAULT "")
if(EXISTS "${CMAKE_SOURCE_DIR}/src/shader/thirdparty/XenosRecomp/thirdparty/dxc-bin/bin/x64/dxc-linux")
    set(REFII_RENDERER_DXC_DEFAULT
        "${CMAKE_SOURCE_DIR}/src/shader/thirdparty/XenosRecomp/thirdparty/dxc-bin/bin/x64/dxc-linux"
    )
    set(REFII_RENDERER_DXC_LIB_DEFAULT
        "${CMAKE_SOURCE_DIR}/src/shader/thirdparty/XenosRecomp/thirdparty/dxc-bin/lib/x64"
    )
elseif(EXISTS "${CMAKE_SOURCE_DIR}/src/shader/thirdparty/XenosRecomp/thirdparty/dxc-bin/bin/x64/dxc.exe")
    set(REFII_RENDERER_DXC_DEFAULT
        "${CMAKE_SOURCE_DIR}/src/shader/thirdparty/XenosRecomp/thirdparty/dxc-bin/bin/x64/dxc.exe"
    )
endif()
set(REFII_RENDERER_DXC_EXECUTABLE
    "${REFII_RENDERER_DXC_DEFAULT}"
    CACHE FILEPATH
    "DXC executable used to compile renderer-owned Plume shaders"
)
set(REFII_RENDERER_DXC_LIB_DIR
    "${REFII_RENDERER_DXC_LIB_DEFAULT}"
    CACHE PATH
    "Directory containing DXC runtime libraries for renderer-owned Plume shaders"
)
unset(REFII_RENDERER_DXC_DEFAULT)
unset(REFII_RENDERER_DXC_LIB_DEFAULT)

add_library(refii_renderer SHARED
    ${REFII_RENDERER_RUNTIME_SOURCES}
)
target_include_directories(refii_renderer
    PUBLIC
        ${REFII_RENDERER_INCLUDE_DIRS}
)
set(REFII_RENDERER_IMGUI_INCLUDE_DIRS "")
set(REFII_RENDERER_HAS_IMGUI 0)
if(TARGET imgui::imgui)
    get_target_property(
        REFII_RENDERER_IMGUI_INCLUDE_DIRS
        imgui::imgui
        INTERFACE_INCLUDE_DIRECTORIES
    )
elseif(REXSDK_DIR AND EXISTS "${REXSDK_DIR}/thirdparty/imgui/imgui.h")
    set(REFII_RENDERER_IMGUI_INCLUDE_DIRS "${REXSDK_DIR}/thirdparty/imgui")
endif()
if(REFII_RENDERER_IMGUI_INCLUDE_DIRS)
    set(REFII_RENDERER_HAS_IMGUI 1)
    target_include_directories(refii_renderer
        PRIVATE
            ${REFII_RENDERER_IMGUI_INCLUDE_DIRS}
    )
endif()
target_link_libraries(refii_renderer
    PUBLIC
        rex::runtime
)
target_compile_definitions(refii_renderer
    PUBLIC
        REFII_RENDERER_HAS_IMGUI=${REFII_RENDERER_HAS_IMGUI}
        REFII_SDL3_WINDOW_ENABLED=$<BOOL:${REFII_SDL3_WINDOW_ENABLED}>
)
if(EXISTS "${REFII_RENDERER_RENDERDOC_API_HEADER}")
    target_include_directories(refii_renderer
        PRIVATE
            "${REFII_RENDERER_RENDERDOC_ROOT}/renderdoc/api/app"
    )
    target_compile_definitions(refii_renderer
        PUBLIC
            REFII_RENDERER_HAS_RENDERDOC=1
        PRIVATE
            REFII_RENDERER_RENDERDOC_ROOT="${REFII_RENDERER_RENDERDOC_ROOT}"
            REFII_RENDERER_RENDERDOC_RUNTIME_DIR="${REFII_RENDERER_RENDERDOC_RUNTIME_DIR}"
    )
    if(TARGET refii_renderdoc_runtime)
        add_dependencies(refii_renderer refii_renderdoc_runtime)
    endif()
else()
    target_compile_definitions(refii_renderer
        PUBLIC
            REFII_RENDERER_HAS_RENDERDOC=0
    )
endif()
if(CMAKE_DL_LIBS)
    target_link_libraries(refii_renderer
        PRIVATE
            ${CMAKE_DL_LIBS}
    )
endif()
if(TARGET plume)
    set_target_properties(plume PROPERTIES
        POSITION_INDEPENDENT_CODE ON
    )
    target_link_libraries(refii_renderer
        PUBLIC
            plume
    )
    target_compile_definitions(refii_renderer
        PUBLIC
            REFII_RENDERER_HAS_PLUME=1
    )
    if(APPLE)
        target_link_libraries(refii_renderer
            PRIVATE
                "-framework Metal"
                "-framework QuartzCore"
                "-framework CoreGraphics"
                "-framework Foundation"
                "-framework IOKit"
        )
    endif()
    if(REFII_RENDERER_DXC_EXECUTABLE)
        set(PLUME_DXC_EXECUTABLE "${REFII_RENDERER_DXC_EXECUTABLE}" CACHE INTERNAL "")
    endif()
    if(REFII_RENDERER_DXC_LIB_DIR)
        set(PLUME_DXC_LIB_DIR "${REFII_RENDERER_DXC_LIB_DIR}" CACHE INTERNAL "")
    endif()
    include("${REFII_RENDERER_PLUME_ROOT}/examples/cmake/PlumeShaders.cmake")
    plume_shaders_init()
    set(REFII_RENDERER_SHADER_OUTPUT_DIR
        "${CMAKE_CURRENT_BINARY_DIR}/generated/plume_shaders"
    )
    plume_compile_vertex_shader(
        refii_renderer
        "${REFII_RENDERER_ROOT}/src/imgui/shaders/plume_imgui_vs.hlsl"
        plume_imgui_vs
        VSMain
        OUTPUT_DIR "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
    plume_compile_pixel_shader(
        refii_renderer
        "${REFII_RENDERER_ROOT}/src/imgui/shaders/plume_imgui_ps.hlsl"
        plume_imgui_ps
        PSMain
        OUTPUT_DIR "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
    plume_compile_vertex_shader(
        refii_renderer
        "${REFII_RENDERER_ROOT}/src/runtime/shaders/plume_debug_triangle_vs.hlsl"
        plume_debug_triangle_vs
        VSMain
        OUTPUT_DIR "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
    plume_compile_pixel_shader(
        refii_renderer
        "${REFII_RENDERER_ROOT}/src/runtime/shaders/plume_debug_triangle_ps.hlsl"
        plume_debug_triangle_ps
        PSMain
        OUTPUT_DIR "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
    plume_compile_vertex_shader(
        refii_renderer
        "${REFII_RENDERER_ROOT}/src/runtime/shaders/plume_present_blit_vs.hlsl"
        plume_present_blit_vs
        VSMain
        OUTPUT_DIR "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
    plume_compile_pixel_shader(
        refii_renderer
        "${REFII_RENDERER_ROOT}/src/runtime/shaders/plume_present_blit_ps.hlsl"
        plume_present_blit_ps
        PSMain
        OUTPUT_DIR "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
    plume_compile_geometry_shader(
        refii_renderer
        "${REFII_RENDERER_ROOT}/src/runtime/shaders/plume_rectangle_list_gs.hlsl"
        plume_rectangle_list_gs
        GSMain
        OUTPUT_DIR "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
    target_include_directories(refii_renderer
        PRIVATE
            "${REFII_RENDERER_SHADER_OUTPUT_DIR}"
    )
else()
    target_compile_definitions(refii_renderer
        PUBLIC
            REFII_RENDERER_HAS_PLUME=0
    )
endif()

if(UNIX AND NOT APPLE)
    find_package(X11 QUIET)
    if(X11_FOUND)
        target_link_libraries(refii_renderer
            PRIVATE
                X11::X11
        )
        target_compile_definitions(refii_renderer
            PRIVATE
                REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE=1
        )
    else()
        target_compile_definitions(refii_renderer
            PRIVATE
                REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE=0
        )
    endif()
else()
    target_compile_definitions(refii_renderer
        PRIVATE
            REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE=0
    )
endif()
set_target_properties(refii_renderer PROPERTIES
    POSITION_INDEPENDENT_CODE ON
    OUTPUT_NAME refii_renderer
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)

if(WIN32)
    set_target_properties(refii_renderer PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

set(REFII_RENDERER_HOOK_SOURCES
    ${REFII_RENDERER_PPC_HOOK_SOURCES}
    PARENT_SCOPE
)

set(REFII_RENDERER_INCLUDE_DIRS
    ${REFII_RENDERER_INCLUDE_DIRS}
    PARENT_SCOPE
)

set(REFII_RENDERER_LIBRARIES
    refii_renderer
    PARENT_SCOPE
)
