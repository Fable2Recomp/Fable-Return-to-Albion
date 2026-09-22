include_guard(GLOBAL)

# The bundled ReXGlue tree is rexglue-net/: the netplay SDK (upstream v0.10 plus the
# RexNet module). It needs a Rust toolchain (corrosion/cargo) and Clang 20+ on Linux;
# see rexglue-net/README.md.
set(_refii_rexglue_tree_root "${CMAKE_CURRENT_SOURCE_DIR}/rexglue-net")

set(REFII_REXGLUE_SDK_SOURCE_DIR
    "${_refii_rexglue_tree_root}/rexglue-sdk"
    CACHE PATH
    "Path to the bundled ReXGlue SDK source tree"
)

set(REFII_REXGLUE_OUTPUT_ROOT
    "${CMAKE_BINARY_DIR}/rexglue"
    CACHE PATH
    "Directory where the bundled ReXGlue SDK emits its compiled binaries"
)

set(REFII_REXGLUE_SHADOW_DIR
    "${_refii_rexglue_tree_root}/shadow/rexglue-sdk"
    CACHE PATH
    "Path to Refii's ReXGlue SDK shadow source tree"
)

set(REFII_REXGLUE_SHADOW_BUILD_DIR
    "${CMAKE_BINARY_DIR}/rexglue-sdk-shadow"
    CACHE PATH
    "Build-local ReXGlue SDK source tree with Refii shadow files overlaid"
)

# A build dir configured against the old two-tree layout still caches paths under
# rexglue/; repoint them and rebuild the shadow overlay from scratch.
if(DEFINED CACHE{REFII_REXGLUE_SELECTED_TREE} AND
   NOT REFII_REXGLUE_SELECTED_TREE STREQUAL _refii_rexglue_tree_root)
    message(STATUS
        "ReXGlue tree moved to ${_refii_rexglue_tree_root}; "
        "repointing SDK/shadow paths and resetting the shadow overlay"
    )
    set(REFII_REXGLUE_SDK_SOURCE_DIR
        "${_refii_rexglue_tree_root}/rexglue-sdk"
        CACHE PATH
        "Path to the bundled ReXGlue SDK source tree"
        FORCE
    )
    set(REFII_REXGLUE_SHADOW_DIR
        "${_refii_rexglue_tree_root}/shadow/rexglue-sdk"
        CACHE PATH
        "Path to Refii's ReXGlue SDK shadow source tree"
        FORCE
    )
    file(REMOVE_RECURSE "${REFII_REXGLUE_SHADOW_BUILD_DIR}")
    unset(REXSDK_DIR CACHE)
endif()
set(REFII_REXGLUE_SELECTED_TREE
    "${_refii_rexglue_tree_root}"
    CACHE INTERNAL
    "ReXGlue tree the build dir was last configured against"
)

# Default the RexNet module on; -DREXGLUE_ENABLE_REXNET=OFF still works for a build
# without the networking module.
set(REXGLUE_ENABLE_REXNET ON CACHE BOOL "Build the RexNet netplay module")
if(NOT WIN32 AND CMAKE_CXX_COMPILER_VERSION VERSION_LESS "20.0")
    message(WARNING
        "The ReXGlue SDK needs Clang 20+ on Linux (std::expected); found "
        "${CMAKE_CXX_COMPILER_VERSION}. Pass "
        "-DCMAKE_C_COMPILER=clang-20 -DCMAKE_CXX_COMPILER=clang++-20."
    )
endif()
unset(_refii_rexglue_tree_root)

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
        # A hardlink keeps the shadow current for free until git rewrites the SDK file: a
        # checkout replaces the inode rather than editing it, leaving the shadow on the old
        # content. Skipping every file that merely EXISTS is what froze this tree on the
        # pre-v0.10 SDK, while Linux -- which wipes and re-copies -- tracked the bump.
        # IS_NEWER_THAN is true on equal timestamps, so a live hardlink still costs nothing.
        if(EXISTS "${_refii_rexglue_sdk_shadow_file}")
            if("${_refii_rexglue_sdk_shadow_file}" IS_NEWER_THAN
               "${_refii_rexglue_sdk_source_file}")
                continue()
            endif()
            file(REMOVE "${_refii_rexglue_sdk_shadow_file}")
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

# The link pass can add and refresh but cannot notice a REMOVAL, and the SDK globs its own
# sources -- so a file upstream deleted keeps being compiled against headers that moved on.
# v0.10 retired sha256, the graphics trace_* set and the legacy_config/migration_scan
# commands, and all of them were still here. This is the missing half of the wipe Linux does.
#
# Two things are spared: "out/", the SDK's own build output, which the link pass also skips;
# and the .git submodule markers written below, which by design are in neither input tree.
function(refii_rexglue_shadow_prune_orphans)
    file(GLOB_RECURSE _refii_rexglue_shadow_files
        LIST_DIRECTORIES FALSE
        RELATIVE "${REFII_REXGLUE_SHADOW_BUILD_DIR}"
        "${REFII_REXGLUE_SHADOW_BUILD_DIR}/*"
    )

    set(_refii_rexglue_pruned 0)
    foreach(_refii_rexglue_shadow_file IN LISTS _refii_rexglue_shadow_files)
        if(_refii_rexglue_shadow_file MATCHES "^out(/|$)")
            continue()
        endif()
        if(_refii_rexglue_shadow_file MATCHES "(^|/)\.git$")
            continue()
        endif()
        if(EXISTS "${REFII_REXGLUE_SDK_SOURCE_DIR}/${_refii_rexglue_shadow_file}")
            continue()
        endif()
        if(EXISTS "${REFII_REXGLUE_SHADOW_DIR}/${_refii_rexglue_shadow_file}")
            continue()
        endif()

        file(REMOVE "${REFII_REXGLUE_SHADOW_BUILD_DIR}/${_refii_rexglue_shadow_file}")
        math(EXPR _refii_rexglue_pruned "${_refii_rexglue_pruned} + 1")
    endforeach()

    if(_refii_rexglue_pruned GREATER 0)
        message(STATUS
            "ReXGlue shadow: pruned ${_refii_rexglue_pruned} file(s) no longer present "
            "in the SDK or the shadow overlay"
        )
    endif()

    unset(_refii_rexglue_pruned)
    unset(_refii_rexglue_shadow_file)
    unset(_refii_rexglue_shadow_files)
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

    # CONFIGURE_DEPENDS ON THE GLOB ITSELF, not only on the files it found. The plain
    # file-list dependency below tracks EDITS to shadow files that existed at configure
    # time, but a file list cannot see a NEW shadow file — it did not exist when the list
    # was captured, so nothing dirtied the configure, the stage never refreshed, and a fix
    # added as a new shadow sat uncompiled while the build button reported nothing to do.
    # The verified glob makes ninja re-evaluate the file SET before every build and
    # reconfigure when it changes.
    file(GLOB_RECURSE _refii_rexglue_shadow_inputs
        CONFIGURE_DEPENDS
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
        refii_rexglue_shadow_prune_orphans()
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


    set(_refii_rexglue_submodule_marker_dirs
        thirdparty/FFmpeg
        thirdparty/catch2
        thirdparty/cli11
        thirdparty/corrosion
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
        thirdparty/stb
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

# rexnet-core is Rust and reaches rexruntime as a STATICLIB. WINDOWS_EXPORT_ALL_SYMBOLS,
# which is how rexruntime exports anything, builds its .def from the target's own objects
# and those of the OBJECT libraries it links -- never from inside a .lib. So the rexnet_*
# entry points are linked into rexruntime.dll and sealed there, and refii_hostbridge, which
# calls them through rexnet_ffi.h, has no import library entry to bind to. ELF has no such
# gap: the Rust symbols are #[no_mangle] pub extern "C" with default visibility and the .so
# exports them unasked, which is why this only broke on Windows.
#
# The list is READ FROM THE HEADER. rexnet_ffi.h is cbindgen output, so it is the same
# surface the caller compiles against; a hand-kept list would omit the next entry point
# someone adds and bring this same link error back. Only declaration lines match -- a line
# beginning with a type, not the " *" of a doc comment.
function(refii_rexglue_export_rexnet_ffi)
    if(NOT WIN32 OR NOT REXGLUE_ENABLE_REXNET OR NOT TARGET rexruntime)
        return()
    endif()

    set(_refii_rexnet_ffi_header
        "${REFII_REXGLUE_SHADOW_BUILD_DIR}/include/rex/net/rexnet_ffi.h"
    )
    if(NOT EXISTS "${_refii_rexnet_ffi_header}")
        set(_refii_rexnet_ffi_header
            "${REFII_REXGLUE_SDK_SOURCE_DIR}/include/rex/net/rexnet_ffi.h"
        )
    endif()
    if(NOT EXISTS "${_refii_rexnet_ffi_header}")
        message(WARNING
            "RexNet is enabled but rexnet_ffi.h was not found; refii_hostbridge will not "
            "link against the rexnet C ABI."
        )
        return()
    endif()
    set_property(DIRECTORY APPEND PROPERTY
        CMAKE_CONFIGURE_DEPENDS "${_refii_rexnet_ffi_header}"
    )

    file(STRINGS "${_refii_rexnet_ffi_header}" _refii_rexnet_ffi_declarations
        REGEX "^[A-Za-z_].*[ *]rexnet_[A-Za-z0-9_]*\\("
    )

    set(_refii_rexnet_ffi_symbols)
    foreach(_refii_rexnet_ffi_declaration IN LISTS _refii_rexnet_ffi_declarations)
        string(REGEX MATCHALL "[ *]rexnet_[A-Za-z0-9_]*\\("
            _refii_rexnet_ffi_matches
            "${_refii_rexnet_ffi_declaration}"
        )
        foreach(_refii_rexnet_ffi_match IN LISTS _refii_rexnet_ffi_matches)
            string(REGEX REPLACE "^[ *]" "" _refii_rexnet_ffi_symbol "${_refii_rexnet_ffi_match}")
            string(REGEX REPLACE "\\($" "" _refii_rexnet_ffi_symbol "${_refii_rexnet_ffi_symbol}")
            list(APPEND _refii_rexnet_ffi_symbols "${_refii_rexnet_ffi_symbol}")
        endforeach()
    endforeach()
    list(REMOVE_DUPLICATES _refii_rexnet_ffi_symbols)

    if(NOT _refii_rexnet_ffi_symbols)
        message(WARNING
            "No rexnet_* entry points were found in ${_refii_rexnet_ffi_header}; "
            "refii_hostbridge will not link against the rexnet C ABI."
        )
        return()
    endif()

    # Undecorated on x86-64, so the C name is the symbol name. /EXPORT adds to the
    # generated .def rather than replacing it, leaving the rest of rexruntime untouched.
    foreach(_refii_rexnet_ffi_symbol IN LISTS _refii_rexnet_ffi_symbols)
        target_link_options(rexruntime PRIVATE "LINKER:/EXPORT:${_refii_rexnet_ffi_symbol}")
    endforeach()

    list(LENGTH _refii_rexnet_ffi_symbols _refii_rexnet_ffi_count)
    message(STATUS
        "RexNet: exporting ${_refii_rexnet_ffi_count} rexnet_* entry point(s) from rexruntime"
    )

    unset(_refii_rexnet_ffi_count)
    unset(_refii_rexnet_ffi_symbol)
    unset(_refii_rexnet_ffi_match)
    unset(_refii_rexnet_ffi_matches)
    unset(_refii_rexnet_ffi_declaration)
    unset(_refii_rexnet_ffi_declarations)
    unset(_refii_rexnet_ffi_symbols)
    unset(_refii_rexnet_ffi_header)
endfunction()

function(refii_rexglue_apply_shadow_overrides)
    if(NOT EXISTS "${REFII_REXGLUE_SHADOW_DIR}")
        return()
    endif()

    if(TARGET rexruntime)
        target_include_directories(rexruntime BEFORE PUBLIC
            "${REFII_REXGLUE_SHADOW_DIR}/include"
        )

        # The seam's OBJECTS, as rexruntime's own — its single home (src/achievement/CMakeLists.txt).
        if(TARGET refii_achievement)
            target_sources(rexruntime PRIVATE $<TARGET_OBJECTS:refii_achievement>)
            target_include_directories(rexruntime PRIVATE
                $<TARGET_PROPERTY:refii_achievement,INTERFACE_INCLUDE_DIRECTORIES>)
            add_dependencies(rexruntime refii_achievement)
        endif()

        refii_rexglue_replace_target_source(rexruntime "src/kernel/xam/xam_user.cpp")

        # XamAlloc's first argument is a FLAGS word, and upstream asserts it is zero. The title
        # passes 0x10000000 for every overlapped system request, which is what a pub game makes as
        # it starts — so sitting down at a Keystone table aborted the process. See the shadow copy.
        refii_rexglue_replace_target_source(rexruntime "src/kernel/xam/xam_info.cpp")

        refii_rexglue_export_rexnet_ffi()
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

    # After codegen produces generated/sources.cmake, the configure that made
    # this bootstrap graph is stale, and ninja will NOT regenerate on its own:
    # CMake drops nonexistent CONFIGURE_DEPENDS files from the regeneration
    # rule, so the file appearing later never dirties it. Chain an explicit
    # reconfigure, then build the full project in the same invocation.
    get_property(_refii_rexglue_multi_config GLOBAL PROPERTY GENERATOR_IS_MULTI_CONFIG)
    set(_refii_rexglue_continue_configure
        "${CMAKE_COMMAND}" -S "${CMAKE_SOURCE_DIR}" -B "${CMAKE_BINARY_DIR}")
    set(_refii_rexglue_continue_build "${CMAKE_COMMAND}" --build "${CMAKE_BINARY_DIR}")
    if(_refii_rexglue_multi_config)
        list(APPEND _refii_rexglue_continue_build --config $<CONFIG>)
    endif()

    if(TARGET refii_codegen)
        add_custom_target(refii_codegen_if_empty ALL
            COMMAND ${_refii_rexglue_continue_configure}
            COMMAND ${_refii_rexglue_continue_build}
            DEPENDS refii_codegen
            COMMENT "Reconfiguring and building Refii with the generated sources"
            USES_TERMINAL
            VERBATIM
        )
    elseif(TARGET rexglue)
        add_custom_target(refii_codegen_if_empty ALL
            COMMAND $<TARGET_FILE:rexglue> --force codegen
                    "${CMAKE_CURRENT_SOURCE_DIR}/refii_manifest.toml"
            COMMAND ${_refii_rexglue_continue_configure}
            COMMAND ${_refii_rexglue_continue_build}
            DEPENDS rexglue
            WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
            COMMENT "Generating Refii sources because generated/sources.cmake is missing"
            USES_TERMINAL
            VERBATIM
        )
    else()
        return()
    endif()

    # The nested build recompiles the full project; make sure every other
    # target in this bootstrap graph is finished first so two build processes
    # never write the same objects concurrently.
    foreach(_refii_rexglue_prior_target IN ITEMS
            refii_rexglue_sdk refii_achievement)
        if(TARGET "${_refii_rexglue_prior_target}")
            add_dependencies(refii_codegen_if_empty "${_refii_rexglue_prior_target}")
        endif()
    endforeach()

    message(STATUS
        "generated/sources.cmake is missing; this build will run ReXGlue "
        "codegen and then automatically reconfigure and build the full project."
    )
endfunction()
