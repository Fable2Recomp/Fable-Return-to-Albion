include_guard(GLOBAL)

set(REFII_REXGLUE_SDK_SOURCE_DIR
    "${CMAKE_CURRENT_SOURCE_DIR}/rexglue/rexglue-sdk"
    CACHE PATH
    "Path to the bundled ReXGlue SDK source tree"
)

set(REFII_REXGLUE_OUTPUT_ROOT
    "${CMAKE_BINARY_DIR}/rexglue"
    CACHE PATH
    "Directory where the bundled ReXGlue SDK emits its compiled binaries"
)

set(REFII_REXGLUE_SHADOW_DIR
    "${CMAKE_CURRENT_SOURCE_DIR}/rexglue/shadow/rexglue-sdk"
    CACHE PATH
    "Path to Refii's ReXGlue SDK shadow source tree"
)

set(REFII_REXGLUE_SHADOW_BUILD_DIR
    "${CMAKE_BINARY_DIR}/rexglue-sdk-shadow"
    CACHE PATH
    "Build-local ReXGlue SDK source tree with Refii shadow files overlaid"
)

option(
    REFII_REXGLUE_GENERATE_SOURCES_AT_CONFIGURE
    "Generate missing ReXGlue sources during configure"
    ON
)

set(REFII_REXGLUE_CODEGEN_EXECUTABLE
    ""
    CACHE FILEPATH
    "Existing rexglue executable used for configure-time codegen"
)

function(refii_rexglue_read_file out_var file_path)
    if(EXISTS "${file_path}")
        file(READ "${file_path}" _refii_rexglue_file_content)
        set(${out_var} "${_refii_rexglue_file_content}" PARENT_SCOPE)
    else()
        set(${out_var} "" PARENT_SCOPE)
    endif()
    unset(_refii_rexglue_file_content)
endfunction()

function(refii_rexglue_write_file_if_different file_path file_content)
    refii_rexglue_read_file(_refii_rexglue_existing_content "${file_path}")
    if(EXISTS "${file_path}" AND
       _refii_rexglue_existing_content STREQUAL file_content)
        unset(_refii_rexglue_existing_content)
        return()
    endif()

    get_filename_component(_refii_rexglue_file_parent "${file_path}" DIRECTORY)
    file(MAKE_DIRECTORY "${_refii_rexglue_file_parent}")
    file(REMOVE "${file_path}")
    file(WRITE "${file_path}" "${file_content}")

    unset(_refii_rexglue_file_parent)
    unset(_refii_rexglue_existing_content)
endfunction()

function(refii_rexglue_copy_file_if_different source_path destination_path)
    if(NOT EXISTS "${source_path}")
        return()
    endif()

    refii_rexglue_read_file(_refii_rexglue_source_content "${source_path}")
    refii_rexglue_read_file(_refii_rexglue_destination_content "${destination_path}")
    if(EXISTS "${destination_path}" AND
       _refii_rexglue_destination_content STREQUAL _refii_rexglue_source_content)
        unset(_refii_rexglue_destination_content)
        unset(_refii_rexglue_source_content)
        return()
    endif()

    get_filename_component(_refii_rexglue_destination_parent
        "${destination_path}"
        DIRECTORY
    )
    file(MAKE_DIRECTORY "${_refii_rexglue_destination_parent}")
    file(REMOVE "${destination_path}")
    file(COPY_FILE "${source_path}" "${destination_path}")

    unset(_refii_rexglue_destination_parent)
    unset(_refii_rexglue_destination_content)
    unset(_refii_rexglue_source_content)
endfunction()

function(refii_rexglue_copy_tree_if_different source_dir destination_dir)
    file(GLOB_RECURSE _refii_rexglue_tree_files
        LIST_DIRECTORIES FALSE
        RELATIVE "${source_dir}"
        "${source_dir}/*"
    )

    foreach(_refii_rexglue_tree_file IN LISTS _refii_rexglue_tree_files)
        refii_rexglue_copy_file_if_different(
            "${source_dir}/${_refii_rexglue_tree_file}"
            "${destination_dir}/${_refii_rexglue_tree_file}"
        )
    endforeach()

    unset(_refii_rexglue_tree_file)
    unset(_refii_rexglue_tree_files)
endfunction()

function(refii_rexglue_shadow_link_sdk_tree)
    file(GLOB_RECURSE _refii_rexglue_sdk_files
        LIST_DIRECTORIES FALSE
        RELATIVE "${REFII_REXGLUE_SDK_SOURCE_DIR}"
        "${REFII_REXGLUE_SDK_SOURCE_DIR}/*"
    )

    foreach(_refii_rexglue_sdk_file IN LISTS _refii_rexglue_sdk_files)
        if(_refii_rexglue_sdk_file MATCHES "^out(/|$)")
            continue()
        endif()

        set(_refii_rexglue_sdk_source_file
            "${REFII_REXGLUE_SDK_SOURCE_DIR}/${_refii_rexglue_sdk_file}"
        )
        set(_refii_rexglue_sdk_shadow_file
            "${REFII_REXGLUE_SHADOW_BUILD_DIR}/${_refii_rexglue_sdk_file}"
        )
        get_filename_component(_refii_rexglue_sdk_shadow_parent
            "${_refii_rexglue_sdk_shadow_file}"
            DIRECTORY
        )
        file(MAKE_DIRECTORY "${_refii_rexglue_sdk_shadow_parent}")
        if(EXISTS "${_refii_rexglue_sdk_shadow_file}")
            continue()
        endif()
        file(CREATE_LINK
            "${_refii_rexglue_sdk_source_file}"
            "${_refii_rexglue_sdk_shadow_file}"
            COPY_ON_ERROR
        )
    endforeach()

    unset(_refii_rexglue_sdk_shadow_parent)
    unset(_refii_rexglue_sdk_shadow_file)
    unset(_refii_rexglue_sdk_source_file)
    unset(_refii_rexglue_sdk_file)
    unset(_refii_rexglue_sdk_files)
endfunction()

function(refii_rexglue_shadow_copy_mutable_sdk_file relative_path)
    set(_refii_rexglue_mutable_source
        "${REFII_REXGLUE_SDK_SOURCE_DIR}/${relative_path}"
    )
    set(_refii_rexglue_mutable_shadow
        "${REFII_REXGLUE_SHADOW_BUILD_DIR}/${relative_path}"
    )
    if(NOT EXISTS "${_refii_rexglue_mutable_source}")
        return()
    endif()

    get_filename_component(_refii_rexglue_mutable_shadow_parent
        "${_refii_rexglue_mutable_shadow}"
        DIRECTORY
    )
    file(MAKE_DIRECTORY "${_refii_rexglue_mutable_shadow_parent}")
    refii_rexglue_read_file(_refii_rexglue_mutable_source_content
        "${_refii_rexglue_mutable_source}"
    )
    refii_rexglue_read_file(_refii_rexglue_mutable_shadow_content
        "${_refii_rexglue_mutable_shadow}"
    )
    if(NOT EXISTS "${_refii_rexglue_mutable_shadow}" OR
       _refii_rexglue_mutable_shadow_content STREQUAL _refii_rexglue_mutable_source_content)
        file(REMOVE "${_refii_rexglue_mutable_shadow}")
        file(COPY_FILE
            "${_refii_rexglue_mutable_source}"
            "${_refii_rexglue_mutable_shadow}"
        )
    endif()

    unset(_refii_rexglue_mutable_shadow_content)
    unset(_refii_rexglue_mutable_source_content)
    unset(_refii_rexglue_mutable_shadow_parent)
    unset(_refii_rexglue_mutable_shadow)
    unset(_refii_rexglue_mutable_source)
endfunction()

function(refii_rexglue_prepare_shadow_sdk out_var)
    if(NOT EXISTS "${REFII_REXGLUE_SHADOW_DIR}")
        set(${out_var} "${REFII_REXGLUE_SDK_SOURCE_DIR}" PARENT_SCOPE)
        return()
    endif()

    file(GLOB_RECURSE _refii_rexglue_shadow_inputs
        LIST_DIRECTORIES FALSE
        "${REFII_REXGLUE_SHADOW_DIR}/*"
    )
    if(_refii_rexglue_shadow_inputs)
        set_property(DIRECTORY APPEND PROPERTY
            CMAKE_CONFIGURE_DEPENDS ${_refii_rexglue_shadow_inputs}
        )
    endif()

    file(MAKE_DIRECTORY "${REFII_REXGLUE_SHADOW_BUILD_DIR}")
    if(WIN32)
        refii_rexglue_shadow_link_sdk_tree()
    else()
        file(REMOVE_RECURSE "${REFII_REXGLUE_SHADOW_BUILD_DIR}")
        file(MAKE_DIRECTORY "${REFII_REXGLUE_SHADOW_BUILD_DIR}")
        file(COPY "${REFII_REXGLUE_SDK_SOURCE_DIR}/"
            DESTINATION "${REFII_REXGLUE_SHADOW_BUILD_DIR}"
            PATTERN "out" EXCLUDE
        )
    endif()
    refii_rexglue_copy_tree_if_different(
        "${REFII_REXGLUE_SHADOW_DIR}"
        "${REFII_REXGLUE_SHADOW_BUILD_DIR}"
    )

    if(REFII_SDL3_WINDOW_ENABLED)
        refii_rexglue_shadow_copy_mutable_sdk_file("src/ui/presenter.cpp")
        refii_rexglue_shadow_copy_mutable_sdk_file("include/rex/ui/presenter.h")
        refii_rexglue_shadow_copy_mutable_sdk_file("src/input/mnk/mnk_input_driver.cpp")

        set(_refii_rexglue_removed_native_window_files
            include/rex/ui/window_gtk.h
            include/rex/ui/window_win.h
            include/rex/ui/windowed_app_context_gtk.h
            include/rex/ui/windowed_app_context_win.h
            src/ui/window_gtk.cpp
            src/ui/window_win.cpp
            src/ui/windowed_app_context_gtk.cpp
            src/ui/windowed_app_context_win.cpp
        )
        foreach(_refii_rexglue_removed_native_window_file
                IN LISTS _refii_rexglue_removed_native_window_files)
            file(REMOVE
                "${REFII_REXGLUE_SHADOW_BUILD_DIR}/${_refii_rexglue_removed_native_window_file}"
            )
        endforeach()

        set(_refii_rexglue_presenter_cpp
            "${REFII_REXGLUE_SHADOW_BUILD_DIR}/src/ui/presenter.cpp"
        )
        if(EXISTS "${_refii_rexglue_presenter_cpp}")
            file(READ "${_refii_rexglue_presenter_cpp}" _refii_rexglue_presenter_source)
            string(REPLACE
                "#include <rex/ui/window_win.h>"
                "#include <rex/ui/window_sdl.h>"
                _refii_rexglue_presenter_source
                "${_refii_rexglue_presenter_source}"
            )
            string(REPLACE
                "HWND hwnd = static_cast<const Win32Window*>(window_)->hwnd();"
                "HWND hwnd = static_cast<HWND>(static_cast<const SDLWindow*>(window_)->GetNativeWindowHandle());"
                _refii_rexglue_presenter_source
                "${_refii_rexglue_presenter_source}"
            )
            refii_rexglue_write_file_if_different(
                "${_refii_rexglue_presenter_cpp}"
                "${_refii_rexglue_presenter_source}"
            )
            unset(_refii_rexglue_presenter_source)
        endif()
        unset(_refii_rexglue_presenter_cpp)

        set(_refii_rexglue_presenter_h
            "${REFII_REXGLUE_SHADOW_BUILD_DIR}/include/rex/ui/presenter.h"
        )
        if(EXISTS "${_refii_rexglue_presenter_h}")
            file(READ "${_refii_rexglue_presenter_h}" _refii_rexglue_presenter_header)
            string(REPLACE
                "class Win32Window;"
                "class SDLWindow;"
                _refii_rexglue_presenter_header
                "${_refii_rexglue_presenter_header}"
            )
            refii_rexglue_write_file_if_different(
                "${_refii_rexglue_presenter_h}"
                "${_refii_rexglue_presenter_header}"
            )
            unset(_refii_rexglue_presenter_header)
        endif()
        unset(_refii_rexglue_presenter_h)

        set(_refii_rexglue_mnk_input_cpp
            "${REFII_REXGLUE_SHADOW_BUILD_DIR}/src/input/mnk/mnk_input_driver.cpp"
        )
        if(EXISTS "${_refii_rexglue_mnk_input_cpp}")
            file(READ "${_refii_rexglue_mnk_input_cpp}" _refii_rexglue_mnk_input_source)
            string(REPLACE
                "#include <rex/ui/window_win.h>\n#include <Windows.h>"
                "#include <Windows.h>"
                _refii_rexglue_mnk_input_source
                "${_refii_rexglue_mnk_input_source}"
            )
            string(REPLACE
                "  auto* win32_window = dynamic_cast<rex::ui::Win32Window*>(attached_window_);\n  if (win32_window && win32_window->hwnd()) {\n    POINT pt = {static_cast<LONG>(cx), static_cast<LONG>(cy)};\n    ClientToScreen(win32_window->hwnd(), &pt);\n    SetCursorPos(pt.x, pt.y);\n  }"
                "  HWND hwnd = static_cast<HWND>(attached_window_->GetNativeWindowHandle());\n  if (hwnd) {\n    POINT pt = {static_cast<LONG>(cx), static_cast<LONG>(cy)};\n    ClientToScreen(hwnd, &pt);\n    SetCursorPos(pt.x, pt.y);\n  }"
                _refii_rexglue_mnk_input_source
                "${_refii_rexglue_mnk_input_source}"
            )
            refii_rexglue_write_file_if_different(
                "${_refii_rexglue_mnk_input_cpp}"
                "${_refii_rexglue_mnk_input_source}"
            )
            unset(_refii_rexglue_mnk_input_source)
        endif()
        unset(_refii_rexglue_mnk_input_cpp)

        unset(_refii_rexglue_removed_native_window_file)
        unset(_refii_rexglue_removed_native_window_files)
    endif()

    set(_refii_rexglue_submodule_marker_dirs
        thirdparty/FFmpeg
        thirdparty/catch2
        thirdparty/cli11
        thirdparty/fmt
        thirdparty/glslang
        thirdparty/imgui
        thirdparty/inja
        thirdparty/libmspack
        thirdparty/o1heap
        thirdparty/sdl3
        thirdparty/simde
        thirdparty/simde/test/munit
        thirdparty/snappy
        thirdparty/snappy/third_party/benchmark
        thirdparty/snappy/third_party/googletest
        thirdparty/spdlog
        thirdparty/spirv-headers
        thirdparty/spirv-tools
        thirdparty/tomlplusplus
        thirdparty/tracy
        thirdparty/utfcpp
        thirdparty/volk
        thirdparty/vulkan-headers
        thirdparty/vulkan-memory-allocator
        thirdparty/xxHash
    )
    foreach(_refii_rexglue_submodule_marker_dir IN LISTS _refii_rexglue_submodule_marker_dirs)
        if(EXISTS "${REFII_REXGLUE_SDK_SOURCE_DIR}/${_refii_rexglue_submodule_marker_dir}")
            refii_rexglue_write_file_if_different(
                "${REFII_REXGLUE_SHADOW_BUILD_DIR}/${_refii_rexglue_submodule_marker_dir}/.git"
                "shadow-sdk-submodule-marker\n"
            )
        endif()
    endforeach()
    unset(_refii_rexglue_submodule_marker_dir)
    unset(_refii_rexglue_submodule_marker_dirs)

    set(${out_var} "${REFII_REXGLUE_SHADOW_BUILD_DIR}" PARENT_SCOPE)
    unset(_refii_rexglue_shadow_inputs)
endfunction()

function(refii_rexglue_shadow_source out_var relative_path)
    set(_refii_rexglue_shadow_source
        "${REFII_REXGLUE_SHADOW_DIR}/${relative_path}"
    )
    if(EXISTS "${_refii_rexglue_shadow_source}")
        set(${out_var} "${_refii_rexglue_shadow_source}" PARENT_SCOPE)
    else()
        set(${out_var} "${REFII_REXGLUE_SDK_SOURCE_DIR}/${relative_path}" PARENT_SCOPE)
    endif()
endfunction()

function(refii_rexglue_replace_target_source target_name sdk_relative_path)
    if(NOT TARGET "${target_name}")
        return()
    endif()

    set(_refii_rexglue_shadow_source
        "${REFII_REXGLUE_SHADOW_DIR}/${sdk_relative_path}"
    )
    if(NOT EXISTS "${_refii_rexglue_shadow_source}")
        return()
    endif()

    string(REGEX REPLACE "^src/ui/" "" _refii_rexglue_ui_relative
        "${sdk_relative_path}"
    )
    set(_refii_rexglue_sdk_source
        "${REFII_REXGLUE_SDK_SOURCE_DIR}/${sdk_relative_path}"
    )

    get_target_property(_refii_rexglue_sources "${target_name}" SOURCES)
    if(_refii_rexglue_sources)
        set(_refii_rexglue_found_source OFF)
        foreach(_refii_rexglue_candidate IN ITEMS
            "${_refii_rexglue_ui_relative}"
            "${_refii_rexglue_sdk_source}"
        )
            list(FIND _refii_rexglue_sources "${_refii_rexglue_candidate}"
                _refii_rexglue_source_index
            )
            if(NOT _refii_rexglue_source_index EQUAL -1)
                set(_refii_rexglue_found_source ON)
            endif()
        endforeach()
        if(NOT _refii_rexglue_found_source)
            return()
        endif()

        list(REMOVE_ITEM _refii_rexglue_sources
            "${_refii_rexglue_ui_relative}"
            "${_refii_rexglue_sdk_source}"
        )
        set_property(TARGET "${target_name}" PROPERTY SOURCES
            ${_refii_rexglue_sources}
        )
    endif()

    target_sources("${target_name}" PRIVATE "${_refii_rexglue_shadow_source}")
endfunction()

function(refii_rexglue_add_shadow_source target_name sdk_relative_path)
    if(NOT TARGET "${target_name}")
        return()
    endif()

    set(_refii_rexglue_shadow_source
        "${REFII_REXGLUE_SHADOW_DIR}/${sdk_relative_path}"
    )
    if(EXISTS "${_refii_rexglue_shadow_source}")
        target_sources("${target_name}" PRIVATE "${_refii_rexglue_shadow_source}")
    endif()
endfunction()

function(refii_rexglue_apply_shadow_overrides)
    if(NOT EXISTS "${REFII_REXGLUE_SHADOW_DIR}")
        return()
    endif()

    if(TARGET rexruntime)
        target_include_directories(rexruntime BEFORE PUBLIC
            "${REFII_REXGLUE_SHADOW_DIR}/include"
        )

        if(TARGET refii_achievement)
            target_link_libraries(rexruntime PRIVATE refii_achievement)
        endif()

        refii_rexglue_replace_target_source(rexruntime "src/kernel/xam/xam_user.cpp")
    endif()

    if(TARGET rexui)
        target_include_directories(rexui BEFORE PUBLIC
            "${REFII_REXGLUE_SHADOW_DIR}/include"
        )

        refii_rexglue_replace_target_source(rexui "src/ui/surface_gnulinux.cpp")
        refii_rexglue_replace_target_source(rexui "src/ui/vulkan/vulkan_instance.cpp")
        refii_rexglue_replace_target_source(rexui "src/ui/vulkan/vulkan_presenter.cpp")

        if(REFII_SDL3_WINDOW_ENABLED AND TARGET SDL3::SDL3)
            target_link_libraries(rexui PUBLIC SDL3::SDL3)
        endif()
    endif()
endfunction()

function(refii_rexglue_configure_sdk)
    set(_refii_rexglue_legacy_patched_source_dir "${CMAKE_BINARY_DIR}/refii-rexglue-sdk")
    set(_refii_rexglue_build_copy_source_dir "${CMAKE_BINARY_DIR}/rexglue-sdk-patched")
    if(DEFINED REXSDK_DIR AND
       (REXSDK_DIR STREQUAL "${_refii_rexglue_legacy_patched_source_dir}" OR
        REXSDK_DIR STREQUAL "${_refii_rexglue_build_copy_source_dir}"))
        message(STATUS "Ignoring stale copied ReXGlue SDK path: ${REXSDK_DIR}")
        unset(REXSDK_DIR CACHE)
    endif()
    unset(_refii_rexglue_legacy_patched_source_dir)
    unset(_refii_rexglue_build_copy_source_dir)

    set(_refii_rexglue_using_bundled_sdk OFF)
    if((NOT DEFINED REXSDK_DIR OR NOT REXSDK_DIR) AND
       EXISTS "${REFII_REXGLUE_SDK_SOURCE_DIR}/CMakeLists.txt")
        refii_rexglue_prepare_shadow_sdk(_refii_rexglue_sdk_dir)
        set(REXSDK_DIR
            "${_refii_rexglue_sdk_dir}"
            CACHE PATH
            "Path to rexglue-sdk source tree"
            FORCE
        )
        set(_refii_rexglue_using_bundled_sdk ON)
    elseif(DEFINED REXSDK_DIR AND
           (REXSDK_DIR STREQUAL "${REFII_REXGLUE_SDK_SOURCE_DIR}" OR
            REXSDK_DIR STREQUAL "${REFII_REXGLUE_SHADOW_BUILD_DIR}"))
        if(EXISTS "${REFII_REXGLUE_SHADOW_DIR}")
            refii_rexglue_prepare_shadow_sdk(_refii_rexglue_sdk_dir)
            set(REXSDK_DIR
                "${_refii_rexglue_sdk_dir}"
                CACHE PATH
                "Path to rexglue-sdk source tree"
                FORCE
            )
        endif()
        set(_refii_rexglue_using_bundled_sdk ON)
    endif()

    if(_refii_rexglue_using_bundled_sdk)
        set(REXGLUE_OUTPUT_ROOT
            "${REFII_REXGLUE_OUTPUT_ROOT}"
            CACHE PATH
            "Directory where ReXGlue emits compiled binaries"
            FORCE
        )
        set(REXGLUE_VERSION_SOURCE_DIR
            "${REFII_REXGLUE_SDK_SOURCE_DIR}"
            CACHE PATH
            "Git checkout used for ReXGlue version resolution"
            FORCE
        )
        message(STATUS "Using bundled ReXGlue SDK source tree: ${REXSDK_DIR}")
        message(STATUS "ReXGlue compiled output root: ${REXGLUE_OUTPUT_ROOT}")
    endif()
    unset(_refii_rexglue_sdk_dir)
    unset(_refii_rexglue_using_bundled_sdk)
endfunction()

function(refii_rexglue_generate_sources_if_missing)
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/generated/sources.cmake")
        return()
    endif()
    if(NOT REFII_REXGLUE_GENERATE_SOURCES_AT_CONFIGURE)
        return()
    endif()

    set(_refii_rexglue_codegen_tool "${REFII_REXGLUE_CODEGEN_EXECUTABLE}")
    if(NOT _refii_rexglue_codegen_tool)
        find_program(_refii_rexglue_codegen_tool NAMES rexglue)
    endif()
    if(NOT _refii_rexglue_codegen_tool)
        return()
    endif()

    message(STATUS "Generating missing Refii sources with ${_refii_rexglue_codegen_tool}")
    execute_process(
        COMMAND "${_refii_rexglue_codegen_tool}" --force codegen
                "${CMAKE_CURRENT_SOURCE_DIR}/refii_manifest.toml"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        RESULT_VARIABLE _refii_rexglue_codegen_result
    )
    if(NOT _refii_rexglue_codegen_result EQUAL 0)
        message(FATAL_ERROR "ReXGlue codegen failed")
    endif()
    if(NOT EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/generated/sources.cmake")
        message(FATAL_ERROR
            "ReXGlue codegen completed, but generated/sources.cmake was not produced"
        )
    endif()

    unset(_refii_rexglue_codegen_result)
    unset(_refii_rexglue_codegen_tool)
endfunction()

function(refii_define_rexglue_build_target)
    set(_refii_rexglue_targets)
    foreach(_refii_rexglue_target IN ITEMS rexruntime rexglue)
        if(TARGET "${_refii_rexglue_target}")
            list(APPEND _refii_rexglue_targets "${_refii_rexglue_target}")
        endif()
    endforeach()

    if(_refii_rexglue_targets AND NOT TARGET refii_rexglue_sdk)
        add_custom_target(refii_rexglue_sdk ALL
            DEPENDS ${_refii_rexglue_targets}
            COMMENT "Building ReXGlue SDK dependency targets"
        )
    endif()
endfunction()

function(refii_define_rexglue_codegen_bootstrap_target)
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/generated/sources.cmake")
        return()
    endif()

    if(TARGET refii_codegen)
        add_custom_target(refii_codegen_if_empty ALL
            DEPENDS refii_codegen
            COMMENT "Generating Refii sources because generated/sources.cmake is missing"
        )
        message(WARNING
            "generated/sources.cmake is missing. Build target "
            "refii_codegen_if_empty, then re-run CMake."
        )
        return()
    endif()

    if(NOT TARGET rexglue)
        return()
    endif()

    add_custom_target(refii_codegen_if_empty ALL
        COMMAND $<TARGET_FILE:rexglue> --force codegen
                "${CMAKE_CURRENT_SOURCE_DIR}/refii_manifest.toml"
        DEPENDS rexglue
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Generating Refii sources because generated/sources.cmake is missing"
        VERBATIM
    )
    message(WARNING
        "generated/sources.cmake is missing. Build target "
        "refii_codegen_if_empty, then re-run CMake."
    )
endfunction()
