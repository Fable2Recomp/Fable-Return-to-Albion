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

function(refii_rexglue_prepare_shadow_sdk out_var)
    if(NOT EXISTS "${REFII_REXGLUE_SHADOW_DIR}")
        set(${out_var} "${REFII_REXGLUE_SDK_SOURCE_DIR}" PARENT_SCOPE)
        return()
    endif()

    file(REMOVE_RECURSE "${REFII_REXGLUE_SHADOW_BUILD_DIR}")
    file(MAKE_DIRECTORY "${REFII_REXGLUE_SHADOW_BUILD_DIR}")
    file(COPY "${REFII_REXGLUE_SDK_SOURCE_DIR}/"
         DESTINATION "${REFII_REXGLUE_SHADOW_BUILD_DIR}"
         PATTERN "out" EXCLUDE)
    file(COPY "${REFII_REXGLUE_SHADOW_DIR}/"
         DESTINATION "${REFII_REXGLUE_SHADOW_BUILD_DIR}")

    if(REFII_SDL3_WINDOW_ENABLED)
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
            file(WRITE "${_refii_rexglue_presenter_cpp}" "${_refii_rexglue_presenter_source}")
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
            file(WRITE "${_refii_rexglue_presenter_h}" "${_refii_rexglue_presenter_header}")
            unset(_refii_rexglue_presenter_header)
        endif()
        unset(_refii_rexglue_presenter_h)

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
            file(WRITE
                "${REFII_REXGLUE_SHADOW_BUILD_DIR}/${_refii_rexglue_submodule_marker_dir}/.git"
                "shadow-sdk-submodule-marker\n"
            )
        endif()
    endforeach()
    unset(_refii_rexglue_submodule_marker_dir)
    unset(_refii_rexglue_submodule_marker_dirs)

    set(${out_var} "${REFII_REXGLUE_SHADOW_BUILD_DIR}" PARENT_SCOPE)
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

    if(NOT TARGET rexglue)
        return()
    endif()

    add_custom_target(refii_codegen_if_empty
        COMMAND $<TARGET_FILE:rexglue> codegen
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
