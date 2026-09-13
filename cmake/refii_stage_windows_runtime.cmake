# Stages the MSVC runtime the package's own binaries import, beside those binaries.
#
# The Windows counterpart to the ldd closure the Linux package walks. A player has no Visual
# Studio and no guarantee of a Visual C++ redistributable, and our DLLs and rexglue.exe all
# link the CRT dynamically -- so without this the package ships binaries whose imports cannot
# be resolved, and rexglue.exe fails at process start with "the code execution cannot proceed
# because VCRUNTIME140D.dll was not found". Nothing has run at that point, so the launcher
# reports a generator that will not start rather than a missing runtime.
#
# THE LIST IS DERIVED, NOT WRITTEN DOWN. The imports differ by configuration (VCRUNTIME140.dll
# against VCRUNTIME140D.dll) and by what the code happens to use -- MSVCP140_ATOMIC_WAIT.dll
# appears only once something waits on a std::atomic -- so a hand-kept list is a snapshot that
# silently stops matching. The PE imports are read instead.
#
# ONLY THE CRT IS COPIED, AND ONLY OUT OF THE REDISTRIBUTABLE DIRECTORIES. The dependency walk
# names everything, Windows' own DLLs included; an earlier revision of this file copied what
# the walk resolved and put kernel32.dll in the package. Resolution is not trustworthy either:
# msvcp140d.dll resolved out of C:/WINDOWS/system32 on the machine this was written on, which
# is not a file we may ship. So the names come from the walk and the FILES come from CRT_DIRS.
#
# TWO DESTINATIONS. bin/ holds the host natives and is what the launcher puts on the child's
# PATH; bin/recompile/bin holds rexglue.exe alone, and a copy there means it also starts when
# run directly, which is how anyone debugging the install-time build invokes it.

cmake_minimum_required(VERSION 3.24)

if(POLICY CMP0207)
    cmake_policy(SET CMP0207 NEW)
endif()

foreach(_required APP_DIR)
    if(NOT ${_required})
        message(FATAL_ERROR "refii_stage_windows_runtime: ${_required} not set")
    endif()
endforeach()

set(_bin "${APP_DIR}/bin")
set(_generator_bin "${APP_DIR}/bin/recompile/bin")

file(GLOB _libraries LIST_DIRECTORIES FALSE "${_bin}/*.dll")
file(GLOB _executables LIST_DIRECTORIES FALSE "${_bin}/*.exe" "${_generator_bin}/*.exe")
if(NOT _libraries AND NOT _executables)
    message(FATAL_ERROR
        "refii_stage_windows_runtime: no binaries under ${_bin}; the package would not run.")
endif()

if(OBJDUMP AND EXISTS "${OBJDUMP}")
    set(CMAKE_OBJDUMP "${OBJDUMP}")
endif()

file(GET_RUNTIME_DEPENDENCIES
    EXECUTABLES ${_executables}
    LIBRARIES ${_libraries}
    RESOLVED_DEPENDENCIES_VAR _resolved
    UNRESOLVED_DEPENDENCIES_VAR _unresolved
    DIRECTORIES ${CRT_DIRS} "${_bin}" "${_generator_bin}"
    PRE_EXCLUDE_REGEXES "api-ms-win-.*" "ext-ms-.*"
)

# The CRT families a Visual Studio toolchain can make a binary import. vccorlib is here for
# completeness rather than because this tree uses it; it costs nothing to look for and its
# absence is not an error.
set(_crt_pattern "^(msvcp140|vcruntime140|concrt140|vccorlib140|ucrtbase)")

set(_wanted "")
foreach(_dependency IN LISTS _resolved _unresolved)
    get_filename_component(_name "${_dependency}" NAME)
    string(TOLOWER "${_name}" _lower)
    if(_lower MATCHES "${_crt_pattern}")
        list(APPEND _wanted "${_lower}")
    endif()
endforeach()
if(_wanted)
    list(REMOVE_DUPLICATES _wanted)
    list(SORT _wanted)
endif()

set(_staged "")
set(_missing "")
set(_debug_crt FALSE)
foreach(_name IN LISTS _wanted)
    # The release UCRT is part of Windows and is serviced by Windows Update; a copy beside the
    # executable would shadow the machine's own. Its debug twin is not part of Windows and has
    # to travel, which is the only reason a Debug package starts anywhere.
    if(_name STREQUAL "ucrtbase.dll")
        continue()
    endif()

    unset(_found CACHE)
    unset(_found)
    find_file(_found "${_name}" PATHS ${CRT_DIRS} NO_DEFAULT_PATH)
    if(NOT _found)
        list(APPEND _missing "${_name}")
        continue()
    endif()

    foreach(_destination "${_bin}" "${_generator_bin}")
        if(IS_DIRECTORY "${_destination}")
            file(COPY "${_found}" DESTINATION "${_destination}")
        endif()
    endforeach()
    list(APPEND _staged "${_name}")
    if(_name MATCHES "d\.dll$")
        set(_debug_crt TRUE)
    endif()
endforeach()

if(_missing)
    string(REPLACE ";" "\n    " _missing_report "${_missing}")
    string(REPLACE ";" "\n    " _searched "${CRT_DIRS}")
    message(FATAL_ERROR
        "The package needs these MSVC runtime DLLs and none could be found:\n"
        "    ${_missing_report}\n\n"
        "Searched:\n    ${_searched}\n\n"
        "A name ending in d is the DEBUG CRT, which Microsoft does not license for "
        "redistribution and which exists only inside a Visual Studio install. Build "
        "--config RelWithDebInfo for a package that runs on a machine without one.")
endif()

if(_staged)
    list(LENGTH _staged _count)
    string(REPLACE ";" " " _staged_report "${_staged}")
    message(STATUS "Staged ${_count} MSVC runtime DLL(s): ${_staged_report}")
else()
    message(WARNING
        "No MSVC runtime DLL was staged. If the package's binaries import one, it will fail "
        "to start on a machine without a matching Visual C++ runtime installed.")
endif()

# Said once, loudly, at the point the package is built. A Debug package starts on this machine
# because the debug CRT was copied out of the Visual Studio install beside it, and starts
# nowhere else: those DLLs may not be redistributed, and the licence is not the only reason to
# care -- they are absent on every machine that has no Visual Studio.
if(_debug_crt)
    message(WARNING
        "This package carries the DEBUG MSVC runtime and MUST NOT be distributed. It runs on "
        "this machine alone. Build --config RelWithDebInfo for a package to hand to anyone "
        "else.")
endif()
