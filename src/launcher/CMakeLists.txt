cmake_minimum_required(VERSION 3.25)

project(refii_launcher LANGUAGES C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(LAUNCHER_TARGET refii-launcher)
set(LAUNCHER_OUTPUT_DIR ${CMAKE_BINARY_DIR})
set(LAUNCHER_DATA_DIR ${CMAKE_BINARY_DIR}/assets/launcher)

set(GLFW_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(GLFW_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(GLFW_BUILD_DOCS OFF CACHE BOOL "" FORCE)
set(GLFW_INSTALL OFF CACHE BOOL "" FORCE)
if(UNIX AND NOT APPLE)
    set(GLFW_BUILD_WAYLAND OFF CACHE BOOL "" FORCE)
    set(GLFW_BUILD_X11 ON CACHE BOOL "" FORCE)
endif()

add_subdirectory(vendors/glfw EXCLUDE_FROM_ALL)

set(LAUNCHER_SOURCES
    src/installer/installer.cpp
    src/installer/script_bank_extractor.cpp
    src/installer/xiso_extractor.cpp
    src/core/background.cpp
    src/core/changelog.cpp
    src/core/credits.cpp
    src/core/launcher.cpp
    src/core/patches.cpp
    src/core/utils/crypto/sha256.cpp
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
    add_executable(${LAUNCHER_TARGET} WIN32 ${LAUNCHER_SOURCES})
else()
    list(APPEND LAUNCHER_SOURCES src/linux/platform.cpp)
    add_executable(${LAUNCHER_TARGET} ${LAUNCHER_SOURCES})
endif()

set_target_properties(${LAUNCHER_TARGET} PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${LAUNCHER_OUTPUT_DIR}"
)

if(CMAKE_CONFIGURATION_TYPES)
    foreach(config ${CMAKE_CONFIGURATION_TYPES})
        string(TOUPPER "${config}" config_upper)
        set_target_properties(${LAUNCHER_TARGET} PROPERTIES
            "RUNTIME_OUTPUT_DIRECTORY_${config_upper}" "${LAUNCHER_OUTPUT_DIR}"
        )
    endforeach()
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
)

if(TARGET refii_lua_decompiler_workspace)
    target_link_libraries(${LAUNCHER_TARGET} PRIVATE refii_lua_decompiler_workspace)
endif()
if(WIN32 AND TARGET refii-lua-decompiler-single)
    add_dependencies(${LAUNCHER_TARGET} refii-lua-decompiler-single)
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
    find_package(X11 REQUIRED)
    find_package(ZLIB REQUIRED)

    target_link_libraries(${LAUNCHER_TARGET} PRIVATE
        CURL::libcurl
        OpenGL::GL
        X11::X11
        ZLIB::ZLIB
    )
endif()

if(WIN32)
    find_package(ZLIB REQUIRED)
    target_link_libraries(${LAUNCHER_TARGET} PRIVATE ZLIB::ZLIB)
endif()

add_custom_command(TARGET ${LAUNCHER_TARGET} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory
        ${LAUNCHER_DATA_DIR}
    COMMAND ${CMAKE_COMMAND} -E make_directory
        ${CMAKE_BINARY_DIR}/assets/game
    COMMAND ${CMAKE_COMMAND} -E make_directory
        ${CMAKE_BINARY_DIR}/assets/save
    COMMAND ${CMAKE_COMMAND} -E make_directory
        ${CMAKE_BINARY_DIR}/assets/update
    COMMAND ${CMAKE_COMMAND} -E copy_directory
        ${CMAKE_CURRENT_SOURCE_DIR}/assets
        ${LAUNCHER_DATA_DIR}/assets
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        ${CMAKE_CURRENT_SOURCE_DIR}/config.cfg
        ${LAUNCHER_DATA_DIR}/config.cfg
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        ${CMAKE_CURRENT_SOURCE_DIR}/imgui.cfg
        ${LAUNCHER_DATA_DIR}/imgui.cfg
)
