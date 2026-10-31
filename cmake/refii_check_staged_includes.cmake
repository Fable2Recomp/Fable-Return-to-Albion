# Checks the staged payload build inputs compile BY INCLUDE, before the export ships them.
#
# The player's machine compiles bin/recompile/<Folder>/hooks and payload_core/ with exactly the
# staged include set (include_dirs.cmake, stdcxx, the generated sources): a header the dev
# build found in the tree but the staging did not ship fails there and nowhere here. So every
# quoted #include of every staged source, and transitively of every staged header it reaches,
# is resolved the way the launcher's compile resolves it — the includer's directory, then the
# staged include directories in order, then stdcxx, then the generator's output (modelled by
# GENERATED_ROOT, the dev tree's generated/) — and a miss fails the build for EITHER title.
# Angle includes are the toolchain's and are not checked.
#
#   cmake -DDESTINATION=<bin/recompile> -DGENERATED_ROOT=<generated> -P refii_check_staged_includes.cmake

if(NOT DESTINATION OR NOT IS_DIRECTORY "${DESTINATION}")
    message(FATAL_ERROR "refii_check_staged_includes: DESTINATION is not a directory: '${DESTINATION}'")
endif()

# The lists are written for the launcher's parser: `set(VAR ${CMAKE_CURRENT_LIST_DIR}/...)`.
function(_refii_read_staged_list file variable out)
    set(${out} "" PARENT_SCOPE)
    if(NOT EXISTS "${file}")
        return()
    endif()
    get_filename_component(_dir "${file}" DIRECTORY)
    set(CMAKE_CURRENT_LIST_DIR "${_dir}")
    include("${file}")
    set(${out} "${${variable}}" PARENT_SCOPE)
endfunction()

_refii_read_staged_list("${DESTINATION}/include_dirs.cmake" REFII_PAYLOAD_INCLUDE_DIRS _include_dirs)
if(NOT _include_dirs)
    message(FATAL_ERROR "refii_check_staged_includes: ${DESTINATION}/include_dirs.cmake lists no directory")
endif()
list(APPEND _include_dirs "${DESTINATION}/stdcxx")

# Every staged source: each title's hooks, the payload core, the export.
set(_roots "")
file(GLOB _hook_lists "${DESTINATION}/*/hook_sources.cmake")
foreach(_list IN LISTS _hook_lists)
    _refii_read_staged_list("${_list}" REFII_HOOK_SOURCES _sources)
    list(APPEND _roots ${_sources})
endforeach()
_refii_read_staged_list("${DESTINATION}/payload_core_sources.cmake" REFII_PAYLOAD_CORE_SOURCES _sources)
list(APPEND _roots ${_sources} "${DESTINATION}/src/payload_export.cpp")

set(_seen "")
set(_missing "")
set(_todo ${_roots})
while(_todo)
    list(POP_BACK _todo _file)
    if(NOT EXISTS "${_file}")
        continue()
    endif()
    list(FIND _seen "${_file}" _at)
    if(NOT _at EQUAL -1)
        continue()
    endif()
    list(APPEND _seen "${_file}")

    get_filename_component(_here "${_file}" DIRECTORY)
    file(STRINGS "${_file}" _lines REGEX "^[ \t]*#[ \t]*include[ \t]+\"[^\"]+\"")
    foreach(_line IN LISTS _lines)
        string(REGEX REPLACE "^[ \t]*#[ \t]*include[ \t]+\"([^\"]+)\".*$" "\\1" _name "${_line}")
        set(_found "")
        foreach(_dir "${_here}" ${_include_dirs})
            if(EXISTS "${_dir}/${_name}")
                set(_found "${_dir}/${_name}")
                break()
            endif()
        endforeach()
        if(NOT _found AND (_name STREQUAL "refii_init.h" OR _name STREQUAL "refii_pch.h" OR (GENERATED_ROOT AND EXISTS "${GENERATED_ROOT}/${_name}")))
            # Written by the generator on the player's machine; present here as the dev tree's.
            continue()
        endif()
        if(NOT _found)
            file(RELATIVE_PATH _rel "${DESTINATION}" "${_file}")
            list(APPEND _missing "  ${_rel}: #include \"${_name}\"")
            continue()
        endif()
        # Only the staged tree is walked: a header under an include directory outside it
        # (the SDK's, a thirdparty's) is a dev-build header the staging copied verbatim.
        get_filename_component(_found "${_found}" ABSOLUTE)
        string(FIND "${_found}" "${DESTINATION}/" _inside)
        if(_inside EQUAL 0)
            list(APPEND _todo "${_found}")
        endif()
    endforeach()
endwhile()

list(LENGTH _seen _count)
if(_missing)
    list(JOIN _missing "\n" _report)
    message(FATAL_ERROR
        "The staged payload sources do not compile by include: the player's build has only what "
        "is staged under ${DESTINATION}, and these includes resolve nowhere in it. A hook's own "
        "header must be listed among its target's sources (cmake/refii_stage_hook_sources.cmake).\n"
        "${_report}")
endif()
message(STATUS "Staged payload sources compile by include: ${_count} files, every quoted include resolved")
