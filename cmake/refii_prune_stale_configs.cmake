# Removes the other configurations' copies of a staged library.
#
# Script mode: cmake -DBIN=<dir> -DSTEM=<library stem> -DKEEP=<file name> -P <this file>
#
# The SDK's libraries carry a configuration postfix (rexruntime, rexruntimed, rexruntimerd),
# and the staging into Godot/build/<arch>/bin only ever adds, so a tree built Debug and then
# RelWithDebInfo held both runtimes. The host loaded the one it was linked against while the
# launcher took the newest by mtime for the payload link and logged "more than one rexruntime".
# Two runtimes mean two sets of globals. Every sibling of KEEP that shares the stem goes.

foreach(_required BIN STEM KEEP)
    if(NOT DEFINED ${_required} OR "${${_required}}" STREQUAL "")
        message(FATAL_ERROR "refii_prune_stale_configs: ${_required} is required")
    endif()
endforeach()

if(NOT IS_DIRECTORY "${BIN}")
    return()
endif()

file(GLOB _candidates LIST_DIRECTORIES FALSE
    "${BIN}/lib${STEM}.so" "${BIN}/lib${STEM}d.so" "${BIN}/lib${STEM}rd.so"
    "${BIN}/lib${STEM}.dylib" "${BIN}/lib${STEM}d.dylib" "${BIN}/lib${STEM}rd.dylib"
    "${BIN}/${STEM}.dll" "${BIN}/${STEM}d.dll" "${BIN}/${STEM}rd.dll"
    "${BIN}/lib${STEM}.dll" "${BIN}/lib${STEM}d.dll" "${BIN}/lib${STEM}rd.dll")

foreach(_candidate IN LISTS _candidates)
    get_filename_component(_name "${_candidate}" NAME)
    if(_name STREQUAL KEEP)
        continue()
    endif()
    file(REMOVE "${_candidate}")
    message(STATUS "Removed stale ${_name} from ${BIN} (staging ${KEEP})")
endforeach()
