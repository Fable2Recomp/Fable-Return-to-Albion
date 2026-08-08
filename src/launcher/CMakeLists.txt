cmake_minimum_required(VERSION 3.25)

project(refii_launcher LANGUAGES C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(LAUNCHER_TARGET refii-launcher)
if(NOT DEFINED REFII_RUNTIME_OUTPUT_DIR OR REFII_RUNTIME_OUTPUT_DIR STREQUAL "")
    set(REFII_RUNTIME_OUTPUT_DIR "${CMAKE_BINARY_DIR}/bin")
endif()
if(NOT DEFINED REFII_RUNTIME_DATA_DIR OR REFII_RUNTIME_DATA_DIR STREQUAL "")
    set(REFII_RUNTIME_DATA_DIR "${REFII_RUNTIME_OUTPUT_DIR}/assets")
endif()
set(LAUNCHER_OUTPUT_DIR ${REFII_RUNTIME_OUTPUT_DIR})

set(GLFW_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(GLFW_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(GLFW_BUILD_DOCS OFF CACHE BOOL "" FORCE)
set(GLFW_INSTALL OFF CACHE BOOL "" FORCE)
set(GLFW_LIBRARY_TYPE STATIC CACHE STRING "" FORCE)
if(UNIX AND NOT APPLE)
    option(REFII_LAUNCHER_ENABLE_WAYLAND "Build native Wayland support for the launcher" ON)
    option(REFII_LAUNCHER_ENABLE_X11 "Build X11 fallback support for the launcher" ON)
    if(NOT REFII_LAUNCHER_ENABLE_WAYLAND AND NOT REFII_LAUNCHER_ENABLE_X11)
        message(FATAL_ERROR "At least one Linux launcher window backend must be enabled.")
    endif()
    set(GLFW_BUILD_WAYLAND ${REFII_LAUNCHER_ENABLE_WAYLAND} CACHE BOOL "" FORCE)
    set(GLFW_BUILD_X11 ${REFII_LAUNCHER_ENABLE_X11} CACHE BOOL "" FORCE)
endif()

add_subdirectory(vendors/glfw EXCLUDE_FROM_ALL)

set(LAUNCHER_SOURCES
    src/installer/installer.cpp
    src/installer/xiso_extractor.cpp
    src/core/background.cpp
    src/core/changelog.cpp
    src/core/credits.cpp
    src/core/launcher.cpp
    src/core/patches.cpp
    src/core/runtime_settings.cpp
    src/core/utils/filedownloader.cpp
    src/core/utils/githubapi.cpp
    src/core/utils/logsystem.cpp
    src/core/utils/stb_image.cpp
    src/core/utils/versionmanager.cpp
    src/core/window.cpp
    generated/glad/src/glad.c
    vendors/imgui/imgui.cpp
    vendors/imgui/imgui_draw.cpp
    vendors/imgui/imgui_tables.cpp
    vendors/imgui/imgui_widgets.cpp
    vendors/imgui/backends/imgui_impl_glfw.cpp
    vendors/imgui/backends/imgui_impl_opengl3.cpp
)

if(WIN32)
    list(APPEND LAUNCHER_SOURCES src/windows/platform.cpp)
    # Opt in to Win32 long paths; the installer writes asset paths up to ~194
    # chars below the install root. MSVC embeds .manifest sources via mt.exe.
    if(MSVC)
        list(APPEND LAUNCHER_SOURCES
            "${CMAKE_SOURCE_DIR}/packaging/windows/refii_long_paths.manifest")
    endif()
    add_executable(${LAUNCHER_TARGET} ${LAUNCHER_SOURCES})
else()
    list(APPEND LAUNCHER_SOURCES src/linux/platform.cpp)
    add_executable(${LAUNCHER_TARGET} ${LAUNCHER_SOURCES})
endif()

set_target_properties(${LAUNCHER_TARGET} PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${LAUNCHER_OUTPUT_DIR}"
)

set(LAUNCHER_OPS_TARGET refii_launcher_ops)
set(LAUNCHER_OPS_SOURCES
    src/abi/launcher_ops_c_api.cpp
    src/installer/installer.cpp
    src/installer/xiso_extractor.cpp
    src/core/runtime_settings.cpp
    src/core/utils/filedownloader.cpp
    src/core/utils/githubapi.cpp
    src/core/utils/versionmanager.cpp
    vendors/imgui/imgui.cpp
    vendors/imgui/imgui_draw.cpp
    vendors/imgui/imgui_tables.cpp
    vendors/imgui/imgui_widgets.cpp
)
if(WIN32)
    list(APPEND LAUNCHER_OPS_SOURCES src/windows/platform.cpp)
else()
    list(APPEND LAUNCHER_OPS_SOURCES src/linux/platform.cpp)
endif()

add_library(${LAUNCHER_OPS_TARGET} SHARED ${LAUNCHER_OPS_SOURCES})

target_include_directories(${LAUNCHER_OPS_TARGET} PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/generated/glad/include
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/include/core
    ${CMAKE_CURRENT_SOURCE_DIR}/include/core/utils
    ${CMAKE_CURRENT_SOURCE_DIR}/vendors/imgui
    ${CMAKE_SOURCE_DIR}/src/abi/include
)

target_link_libraries(${LAUNCHER_OPS_TARGET} PRIVATE
    glfw
    refii_modding
    refii_bnk_extractor
)

if(TARGET refii_lua_decompiler_workspace)
    target_link_libraries(${LAUNCHER_OPS_TARGET} PRIVATE refii_lua_decompiler_workspace)
endif()
if(TARGET refii_shader_sbk_workspace)
    target_link_libraries(${LAUNCHER_OPS_TARGET} PRIVATE refii_shader_sbk_workspace)
endif()

if(WIN32)
    target_link_libraries(${LAUNCHER_OPS_TARGET} PRIVATE
        comdlg32.lib
        dwmapi.lib
        ole32.lib
        shell32.lib
        shlwapi.lib
        winhttp.lib
    )
endif()

set_target_properties(${LAUNCHER_OPS_TARGET} PROPERTIES
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    POSITION_INDEPENDENT_CODE ON
    OUTPUT_NAME refii_launcher_ops
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)

option(REFII_STAGE_GODOT_BIN "Copy launcher ops + runtime DLLs into Godot/build/bin after building" ON)
if(REFII_STAGE_GODOT_BIN)
    set(REFII_GODOT_BIN_DIR "${CMAKE_SOURCE_DIR}/Godot/build/bin")
    add_custom_command(TARGET ${LAUNCHER_OPS_TARGET} POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_GODOT_BIN_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:${LAUNCHER_OPS_TARGET}>" "${REFII_GODOT_BIN_DIR}"
        COMMENT "Staging ${LAUNCHER_OPS_TARGET} into Godot/build/bin"
        VERBATIM
    )
    if(WIN32)
        add_custom_command(TARGET ${LAUNCHER_OPS_TARGET} POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                    "$<TARGET_RUNTIME_DLLS:${LAUNCHER_OPS_TARGET}>" "${REFII_GODOT_BIN_DIR}"
            COMMAND_EXPAND_LISTS
            VERBATIM
        )
    endif()
endif()

target_include_directories(${LAUNCHER_TARGET} PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/generated/glad/include
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/include/core
    ${CMAKE_CURRENT_SOURCE_DIR}/include/core/utils
    ${CMAKE_CURRENT_SOURCE_DIR}/vendors/imgui
    ${CMAKE_CURRENT_SOURCE_DIR}/vendors/imgui/backends
    ${CMAKE_CURRENT_SOURCE_DIR}/vendors/stb
)

target_link_libraries(${LAUNCHER_TARGET} PRIVATE
    glfw
    refii_modding
    refii_bnk_extractor
)

if(UNIX AND NOT APPLE)
    target_compile_definitions(${LAUNCHER_TARGET} PRIVATE
        REFII_LAUNCHER_HAS_WAYLAND=$<BOOL:${REFII_LAUNCHER_ENABLE_WAYLAND}>
        REFII_LAUNCHER_HAS_X11=$<BOOL:${REFII_LAUNCHER_ENABLE_X11}>
        $<$<NOT:$<BOOL:${REFII_LAUNCHER_ENABLE_WAYLAND}>>:IMGUI_IMPL_GLFW_DISABLE_WAYLAND>
        $<$<NOT:$<BOOL:${REFII_LAUNCHER_ENABLE_X11}>>:IMGUI_IMPL_GLFW_DISABLE_X11>
    )
endif()

if(TARGET refii_lua_decompiler_workspace)
    target_link_libraries(${LAUNCHER_TARGET} PRIVATE refii_lua_decompiler_workspace)
endif()
if(TARGET refii_shader_sbk_workspace)
    target_link_libraries(${LAUNCHER_TARGET} PRIVATE refii_shader_sbk_workspace)

    # The launcher's "Compile Shaders" flow must request the same backend set
    # the standalone refii-shader-sbk tool does, otherwise a launcher-driven
    # retranslation silently produces fewer targets.  This is the only
    # consumer of REFII_SHADER_VALIDATION_TARGETS: the build has no coverage
    # gate of its own (shader artifacts are launcher output, not build input),
    # so the value here just seeds the launcher's compile-time default.
    # Empty/undefined falls back to "auto", which the pipeline resolves
    # through BuildDefaultTranslateTargets(); coverage itself is verified
    # natively by refii::shader::VerifyTranslatedShaderCoverage().
    if(DEFINED REFII_SHADER_VALIDATION_TARGETS AND
       NOT REFII_SHADER_VALIDATION_TARGETS STREQUAL "")
        set(REFII_LAUNCHER_SHADER_TARGETS_VALUE "${REFII_SHADER_VALIDATION_TARGETS}")
    else()
        set(REFII_LAUNCHER_SHADER_TARGETS_VALUE "auto")
    endif()
    target_compile_definitions(${LAUNCHER_TARGET} PRIVATE
        REFII_LAUNCHER_SHADER_TARGETS="${REFII_LAUNCHER_SHADER_TARGETS_VALUE}"
    )
    message(STATUS
        "Launcher shader compile targets: ${REFII_LAUNCHER_SHADER_TARGETS_VALUE}")
endif()
if(WIN32)
    target_link_libraries(${LAUNCHER_TARGET} PRIVATE
        opengl32.lib
        comdlg32.lib
        dwmapi.lib
        ole32.lib
        shell32.lib
        shlwapi.lib
        winhttp.lib
    )
else()
    find_package(OpenGL REQUIRED)
    find_package(CURL REQUIRED)
    if(TARGET OpenGL::OpenGL)
        set(REFII_LAUNCHER_OPENGL_TARGET OpenGL::OpenGL)
    else()
        set(REFII_LAUNCHER_OPENGL_TARGET OpenGL::GL)
    endif()

    target_link_libraries(${LAUNCHER_TARGET} PRIVATE
        CURL::libcurl
        ${REFII_LAUNCHER_OPENGL_TARGET}
    )
endif()

set(LAUNCHER_RUNTIME_DATA_DIR "$<TARGET_FILE_DIR:${LAUNCHER_TARGET}>/assets")
set(LAUNCHER_DATA_DIR "${LAUNCHER_RUNTIME_DATA_DIR}/launcher")

add_custom_command(TARGET ${LAUNCHER_TARGET} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory
        "${LAUNCHER_DATA_DIR}"
    COMMAND ${CMAKE_COMMAND} -E make_directory
        "${LAUNCHER_RUNTIME_DATA_DIR}/game"
    COMMAND ${CMAKE_COMMAND} -E make_directory
        "${LAUNCHER_RUNTIME_DATA_DIR}/save"
    COMMAND ${CMAKE_COMMAND} -E make_directory
        "${LAUNCHER_RUNTIME_DATA_DIR}/update"
    COMMAND ${CMAKE_COMMAND} -E copy_directory
        "${CMAKE_CURRENT_SOURCE_DIR}/assets"
        "${LAUNCHER_DATA_DIR}/assets"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_CURRENT_SOURCE_DIR}/config.cfg"
        "${LAUNCHER_DATA_DIR}/config.cfg"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_CURRENT_SOURCE_DIR}/imgui.cfg"
        "${LAUNCHER_DATA_DIR}/imgui.cfg"
    VERBATIM
)

if(REFII_PACKAGE_ICON_AVAILABLE)
    if(REFII_PACKAGE_ICON_TARGET)
        add_dependencies(${LAUNCHER_TARGET} "${REFII_PACKAGE_ICON_TARGET}")
    endif()
    add_custom_command(TARGET ${LAUNCHER_TARGET} POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${REFII_PACKAGE_ICON_SOURCE}"
            "${LAUNCHER_DATA_DIR}/assets/icon.png"
        VERBATIM
    )
endif()

if(WIN32)
    refii_stage_windows_runtime_dlls(${LAUNCHER_TARGET})
    refii_stage_d3d12_agility_runtime(${LAUNCHER_TARGET})
endif()
