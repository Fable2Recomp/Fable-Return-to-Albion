# Fails the build when a payload-side hook imports a C++ symbol from a host library.
#
# THE RULE THIS ENFORCES is the one stated at the top of src/runtime/include/core/payload_bridge.h:
# the payload must import no C++ symbols. refii_generated is compiled on the player's machine
# by Zig; refii_vfs, refii_core and rexruntime are compiled here by us. A C++ symbol crossing
# that line binds both ends to one C++ ABI, and on Windows the two disagree in ways no flag
# fixes -- MSVC mangling against Itanium, MSVC STL against libc++.
#
# WHY IT IS CHECKED RATHER THAN REMEMBERED: on Linux both ends are Itanium and resolve against
# the same libstdc++, so a hook calling straight into refii_vfs links, runs, and looks correct.
# Nothing there can notice. The violation appears only on Windows, and not at the compile that
# introduced it -- it surfaces at the END of the player's install-time build, as undefined
# symbols from a link that ran after ~390 translation units. Seven functions shipped that way.
# This moves the discovery to the compile that introduces it, on either platform.
#
# HOW: every symbol the hook objects leave undefined, minus everything they define between
# them, is what the payload must find elsewhere. Anything in that remainder mangled into
# refii:: or rex:: is a C++ symbol we own, and therefore a violation. The C entries
# (refii_bridge_*), the generated guest's sub_*, and the payload's own std:: are either
# unmangled or not ours, and pass. Both manglings are matched, so it fires on either host.
#
# ONLY STRONG REFERENCES COUNT. A weak undefined (nm "w"/"v") never fails a link: it resolves
# to null when nothing defines it, on ELF and COFF alike. The Itanium ABI produces exactly
# one of these per `extern thread_local` a TU reads -- a weak reference to the variable's
# initialisation routine (_ZTH...), which the wrapper calls only if it exists and which is
# never emitted for a constant-initialised variable. The payload defines those variables
# itself (internal_state_guest.cpp compiles into the hook set), so nothing crosses the
# boundary; MSVC has no such routine, so this appeared only on Linux and read as two imports.

cmake_minimum_required(VERSION 3.24)

foreach(_required OBJECT_LIST NM)
    if(NOT ${_required})
        message(FATAL_ERROR "refii_check_payload_abi: ${_required} not set")
    endif()
endforeach()

if(NOT EXISTS "${OBJECT_LIST}")
    message(FATAL_ERROR "refii_check_payload_abi: no object list at ${OBJECT_LIST}")
endif()

file(STRINGS "${OBJECT_LIST}" _objects)
list(REMOVE_ITEM _objects "")
if(NOT _objects)
    return()
endif()

# Itanium spells the namespace as a length-prefixed component, MSVC as an @-delimited one. A
# symbol is a run of non-whitespace, so the marker is matched inside the whole name.
set(_symbol_pattern "[^ \t\r\n]*(_Z[^ \t\r\n]*(5refii|3rex)|@(refii|rex)@@)[^ \t\r\n]*")

# The undefined pass keys on nm's type column: "U" is a strong reference, "w" and "v" are
# weak and are left out (see the header). The defined pass takes every type.
set(_undefined_pattern "[ \t]U[ \t]+${_symbol_pattern}")
set(_defined_pattern "${_symbol_pattern}")

# ONE REGEX PASS OVER BATCHED OUTPUT. Walking nm's lines in CMake and matching each one costs
# minutes across ~90 objects and their hundred thousand symbols; MATCHALL over the whole blob
# is a single pass in C++ and returns only the handful of names that could possibly be a
# violation. Batched rather than one invocation per object for the process cost, and batched
# rather than all-at-once because Windows stops accepting a command line at 32767 characters --
# the same wall the payload's own link hit.
function(_refii_collect out_var mode pattern)
    set(_found "")
    set(_batch "")
    set(_size 0)
    set(_remaining ${ARGN})

    while(TRUE)
        list(LENGTH _remaining _left)
        if(_left GREATER 0)
            list(POP_FRONT _remaining _object)
            if(EXISTS "${_object}")
                list(APPEND _batch "${_object}")
                math(EXPR _size "${_size} + 1")
            endif()
        endif()

        list(LENGTH _remaining _left)
        if(_batch AND (_size GREATER_EQUAL 24 OR _left EQUAL 0))
            execute_process(
                COMMAND "${NM}" "${mode}" --no-sort ${_batch}
                OUTPUT_VARIABLE _out ERROR_VARIABLE _err RESULT_VARIABLE _result
            )
            if(NOT _result EQUAL 0)
                message(WARNING
                    "refii_check_payload_abi: ${NM} failed (${_result}); the payload ABI was "
                    "not verified.\n${_err}")
                set(${out_var} "SKIPPED" PARENT_SCOPE)
                return()
            endif()
            string(REGEX MATCHALL "${pattern}" _hits "${_out}")
            foreach(_hit IN LISTS _hits)
                # Drop the type column the undefined pattern anchored on.
                string(REGEX REPLACE "^[ \t]U[ \t]+" "" _hit "${_hit}")
                list(APPEND _found "${_hit}")
            endforeach()
            set(_batch "")
            set(_size 0)
        endif()

        if(_left EQUAL 0)
            break()
        endif()
    endwhile()

    if(_found)
        list(REMOVE_DUPLICATES _found)
    endif()
    set(${out_var} "${_found}" PARENT_SCOPE)
endfunction()

_refii_collect(_undefined --undefined-only "${_undefined_pattern}" ${_objects})
if(_undefined STREQUAL "SKIPPED")
    return()
endif()
if(NOT _undefined)
    return()
endif()

_refii_collect(_defined --defined-only "${_defined_pattern}" ${_objects})
if(_defined STREQUAL "SKIPPED")
    return()
endif()
if(_defined)
    list(REMOVE_ITEM _undefined ${_defined})
endif()
if(NOT _undefined)
    return()
endif()

list(SORT _undefined)
set(_report "")
foreach(_symbol IN LISTS _undefined)
    # Two manglings, two demanglers: llvm-cxxfilt reads Itanium and hands an MSVC name back
    # unchanged, llvm-undname reads MSVC. Neither is required -- an undemangled name still
    # carries the function and the namespace in clear, which is enough to find the call.
    set(_pretty "${_symbol}")
    set(_demangler "")
    if(_symbol MATCHES "^\\?" AND UNDNAME AND EXISTS "${UNDNAME}")
        set(_demangler "${UNDNAME}")
    elseif(NOT _symbol MATCHES "^\\?" AND CXXFILT AND EXISTS "${CXXFILT}")
        set(_demangler "${CXXFILT}")
    endif()
    if(_demangler)
        execute_process(COMMAND "${_demangler}" "${_symbol}"
                        OUTPUT_VARIABLE _demangled OUTPUT_STRIP_TRAILING_WHITESPACE)
        if(_demangled)
            set(_pretty "${_demangled}")
        endif()
    endif()
    string(APPEND _report "\n    ${_pretty}")
endforeach()

list(LENGTH _undefined _count)
message(FATAL_ERROR
    "The payload imports ${_count} C++ symbol(s) from a host library:\n"
    "${_report}\n\n"
    "Each is defined in refii_runtime/refii_vfs/refii_core/rexruntime and called from a hook that compiles "
    "into the payload. That link holds on Linux and CANNOT hold on Windows, where the host is "
    "MSVC-built and the payload is not: it fails at the end of the player's install-time build "
    "as an undefined symbol.\n\n"
    "Give each one a C entry in src/runtime/include/core/payload_bridge.h (shared state) or the "
    "title's own (Fable 2: core/payload_bridge_fable2.h), implement it as "
    "extern \"C\" beside the state it touches, and turn the existing declaration into an "
    "inline wrapper so the call sites do not change -- refii_bridge_publish_world_map_region "
    "and refii::vfs::PublishWorldMapRegion are the worked example.\n\n"
    "Leave the state itself in the host library. Moving the definition into the hook set would "
    "also link, and would give the payload its own copy of the globals -- the host reading one "
    "while the guest writes the other.")
