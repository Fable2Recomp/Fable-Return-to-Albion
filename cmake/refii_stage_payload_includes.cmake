# Incrementally syncs the payload's include directories, core sources, and version manifests
# onto the shipped app, and emits a Ninja DEPFILE so unchanged builds skip in 0.00s.
#
# Run as a script (cmake -P) from the refii_payload_sdk custom command:
#   MANIFEST       file listing one include directory per line
#   DESTINATION    the staged recompile/ directory
#   SOURCE_ROOT    repository root
#   GENERATED_ROOT generated/ directory to exclude
#   STAMP          optional stamp path (left-hand target for DEPFILE)
#   DEPFILE        optional Ninja depfile path to write

cmake_minimum_required(VERSION 3.24)

if(NOT MANIFEST OR NOT EXISTS "${MANIFEST}")
    message(FATAL_ERROR "MANIFEST must point at the generated include directory list")
endif()

if(NOT DESTINATION)
    message(FATAL_ERROR "DESTINATION must be set")
endif()

# Remove legacy pre-library directories if left over from an older tree.
foreach(_legacy "${DESTINATION}/lib" "${DESTINATION}/bin")
    if(IS_DIRECTORY "${_legacy}")
        file(REMOVE_RECURSE "${_legacy}")
    endif()
endforeach()

set(_dep_inputs "${MANIFEST}")

# Sync payload_export.cpp and libcxx_chrono_shim.h incrementally.
if(SOURCE_ROOT)
    file(MAKE_DIRECTORY "${DESTINATION}/src")
    foreach(_payload_src
            "${SOURCE_ROOT}/src/payload/src/payload_export.cpp"
            "${SOURCE_ROOT}/src/payload/src/libcxx_chrono_shim.h")
        if(EXISTS "${_payload_src}")
            get_filename_component(_fname "${_payload_src}" NAME)
            file(COPY_FILE "${_payload_src}" "${DESTINATION}/src/${_fname}" ONLY_IF_DIFFERENT)
            list(APPEND _dep_inputs "${_payload_src}")
        endif()
    endforeach()

    # Sync Fable2/versions and Fable3/versions incrementally, pruning removed files.
    set(_versions_dest "${DESTINATION}/versions")
    file(MAKE_DIRECTORY "${_versions_dest}")
    set(_expected_version_files "")
    foreach(_vroot "${SOURCE_ROOT}/Fable2/versions" "${SOURCE_ROOT}/Fable3/versions")
        if(IS_DIRECTORY "${_vroot}")
            file(GLOB_RECURSE _vfiles RELATIVE "${_vroot}" "${_vroot}/*")
            foreach(_vrel IN LISTS _vfiles)
                set(_vsrc "${_vroot}/${_vrel}")
                set(_vdst "${_versions_dest}/${_vrel}")
                get_filename_component(_vparent "${_vdst}" DIRECTORY)
                file(MAKE_DIRECTORY "${_vparent}")
                file(COPY_FILE "${_vsrc}" "${_vdst}" ONLY_IF_DIFFERENT)
                list(APPEND _expected_version_files "${_vrel}")
                list(APPEND _dep_inputs "${_vsrc}")
            endforeach()
        endif()
    endforeach()

    file(GLOB_RECURSE _existing_vfiles RELATIVE "${_versions_dest}" "${_versions_dest}/*")
    foreach(_ev IN LISTS _existing_vfiles)
        list(FIND _expected_version_files "${_ev}" _vfound)
        if(_vfound EQUAL -1)
            file(REMOVE "${_versions_dest}/${_ev}")
        endif()
    endforeach()
endif()

file(STRINGS "${MANIFEST}" _directories)

set(_include_root "${DESTINATION}/include")
file(MAKE_DIRECTORY "${_include_root}")

set(_index 0)
set(_staged "")
set(_active_subdirs "")
set(_copied_headers 0)
set(_total_headers 0)

foreach(_directory IN LISTS _directories)
    if(_directory STREQUAL "" OR NOT IS_DIRECTORY "${_directory}")
        continue()
    endif()

    if(SOURCE_ROOT AND
       (_directory STREQUAL "${SOURCE_ROOT}" OR _directory STREQUAL "${SOURCE_ROOT}/src"))
        continue()
    endif()

    if(GENERATED_ROOT AND _directory STREQUAL "${GENERATED_ROOT}")
        continue()
    endif()

    file(GLOB_RECURSE _headers RELATIVE "${_directory}"
        "${_directory}/*.h" "${_directory}/*.hpp" "${_directory}/*.hh"
        "${_directory}/*.inc" "${_directory}/*.inl" "${_directory}/*.ipp")

    if(NOT _headers)
        continue()
    endif()

    if(_index LESS 10)
        set(_padded "0${_index}")
    else()
        set(_padded "${_index}")
    endif()

    list(APPEND _active_subdirs "${_padded}")
    set(_target_dir "${_include_root}/${_padded}")
    file(MAKE_DIRECTORY "${_target_dir}")

    foreach(_header IN LISTS _headers)
        set(_src_hdr "${_directory}/${_header}")
        set(_dst_hdr "${_target_dir}/${_header}")
        list(APPEND _dep_inputs "${_src_hdr}")
        math(EXPR _total_headers "${_total_headers} + 1")

        get_filename_component(_parent "${_dst_hdr}" DIRECTORY)
        if(NOT IS_DIRECTORY "${_parent}")
            file(MAKE_DIRECTORY "${_parent}")
        endif()

        if(NOT EXISTS "${_dst_hdr}" OR "${_src_hdr}" IS_NEWER_THAN "${_dst_hdr}")
            file(COPY_FILE "${_src_hdr}" "${_dst_hdr}" ONLY_IF_DIFFERENT)
            math(EXPR _copied_headers "${_copied_headers} + 1")
        endif()
    endforeach()

    # Prune any deleted header from this numbered include directory.
    file(GLOB_RECURSE _existing_hdrs RELATIVE "${_target_dir}" "${_target_dir}/*")
    foreach(_ehdr IN LISTS _existing_hdrs)
        list(FIND _headers "${_ehdr}" _hpos)
        if(_hpos EQUAL -1)
            file(REMOVE "${_target_dir}/${_ehdr}")
        endif()
    endforeach()

    list(APPEND _staged "\${CMAKE_CURRENT_LIST_DIR}/include/${_padded}")
    math(EXPR _index "${_index} + 1")
endforeach()

# Prune any stale numbered include/NN directories beyond the current index count.
file(GLOB _existing_num_dirs RELATIVE "${_include_root}" "${_include_root}/*")
foreach(_ndir IN LISTS _existing_num_dirs)
    list(FIND _active_subdirs "${_ndir}" _npos)
    if(_npos EQUAL -1 AND IS_DIRECTORY "${_include_root}/${_ndir}")
        file(REMOVE_RECURSE "${_include_root}/${_ndir}")
    endif()
endforeach()

set(_body "# Generated by refii_stage_payload_includes.cmake - DO NOT EDIT\nset(REFII_PAYLOAD_INCLUDE_DIRS\n")
foreach(_entry IN LISTS _staged)
    string(APPEND _body "    ${_entry}\n")
endforeach()
string(APPEND _body ")\n")

set(_inc_cmake "${DESTINATION}/include_dirs.cmake")
set(_write_inc TRUE)
if(EXISTS "${_inc_cmake}")
    file(READ "${_inc_cmake}" _old_body)
    if(_old_body STREQUAL _body)
        set(_write_inc FALSE)
    endif()
endif()
if(_write_inc)
    file(WRITE "${_inc_cmake}" "${_body}")
endif()

# Include staged hook & payload_core files in the depfile so refii_check_staged_includes re-runs
# whenever any staged hook/core file changes.
file(GLOB_RECURSE _staged_hook_deps
    "${DESTINATION}/*/hooks/*"
    "${DESTINATION}/*/hook_sources.cmake"
    "${DESTINATION}/payload_core/*"
    "${DESTINATION}/payload_core_sources.cmake")
list(APPEND _dep_inputs ${_staged_hook_deps})

if(DEPFILE AND STAMP)
    get_filename_component(_dep_dir "${DEPFILE}" DIRECTORY)
    file(MAKE_DIRECTORY "${_dep_dir}")
    string(REPLACE " " "\\ " _escaped_stamp "${STAMP}")
    set(_dep_text "${_escaped_stamp}:")
    foreach(_d IN LISTS _dep_inputs)
        if(EXISTS "${_d}")
            string(REPLACE " " "\\ " _ed "${_d}")
            string(APPEND _dep_text " \\\n  ${_ed}")
        endif()
    endforeach()
    string(APPEND _dep_text "\n")
    file(WRITE "${DEPFILE}" "${_dep_text}")
endif()

message(STATUS
    "Payload includes: ${_index} directories verified (${_total_headers} headers, ${_copied_headers} updated)")
