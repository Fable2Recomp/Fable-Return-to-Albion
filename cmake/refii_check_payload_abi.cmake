# Fails the build when a payload-side hook imports a C++ symbol from a host library.
#
# THE RULE THIS ENFORCES is the one stated at the top of src/runtime/include/core/payload_bridge.h:
# the payload must import no C++ symbols. refii_generated is compiled on the player's machine
# by Zig; refii_vfs, refii_core and rexruntime are compiled here by us. A C++ symbol crossing
# that line binds both ends to one C++ ABI, and on Windows the two disagree in ways no flag
# fixes -- MSVC mangling against Itanium, MSVC STL against libc++.
#
# Supports optional per-object symbol caching via `-DCACHE_DIR=<dir>`: unchanged `.obj` files
# reuse their cached filtered `refii::` / `rex::` symbol sets so single-hook edits run `llvm-nm`
# only on the modified `.obj` rather than re-scanning ~90 objects from scratch.

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

set(_symbol_pattern "[^ \t\r\n]*(_Z[^ \t\r\n]*(5refii|3rex)|@(refii|rex)@@)[^ \t\r\n]*")
set(_undef_line_pattern "[^\r\n]+:[ \t]+U[ \t]+${_symbol_pattern}")
set(_def_line_pattern "[^\r\n]+: [0-9a-fA-F ]*[^ \t\r\n][ \t]+${_symbol_pattern}")

function(_refii_nm_batch mode line_pattern var_prefix)
    set(_remaining ${ARGN})
    set(_batch "")
    set(_size 0)

    while(TRUE)
        list(LENGTH _remaining _left)
        if(_left GREATER 0)
            list(POP_FRONT _remaining _obj)
            list(APPEND _batch "${_obj}")
            math(EXPR _size "${_size} + 1")
        endif()

        list(LENGTH _remaining _left)
        if(_batch AND (_size GREATER_EQUAL 24 OR _left EQUAL 0))
            execute_process(
                COMMAND "${NM}" --print-file-name "${mode}" --no-sort ${_batch}
                OUTPUT_VARIABLE _out ERROR_VARIABLE _err RESULT_VARIABLE _result
            )
            if(NOT _result EQUAL 0)
                message(WARNING
                    "refii_check_payload_abi: ${NM} failed (${_result}); the payload ABI was "
                    "not verified.\n${_err}")
                set(REFII_ABI_SKIPPED TRUE PARENT_SCOPE)
                return()
            endif()

            string(REGEX MATCHALL "${line_pattern}" _hits "${_out}")
            foreach(_hit IN LISTS _hits)
                string(REGEX REPLACE "^(.*): [0-9a-fA-F ]*[^ \t\r\n][ \t]+([^ \t\r\n]+)$" "\\1;\\2" _pair "${_hit}")
                list(LENGTH _pair _plen)
                if(_plen EQUAL 2)
                    list(GET _pair 0 _hit_obj)
                    list(GET _pair 1 _hit_sym)
                    string(SHA256 _okey "${_hit_obj}")
                    list(APPEND _local_${_okey} "${_hit_sym}")
                    list(APPEND _touched_keys "${_okey}")
                endif()
            endforeach()

            set(_batch "")
            set(_size 0)
        endif()

        if(_left EQUAL 0)
            break()
        endif()
    endwhile()

    if(_touched_keys)
        list(REMOVE_DUPLICATES _touched_keys)
        foreach(_k IN LISTS _touched_keys)
            list(REMOVE_DUPLICATES _local_${_k})
            set("${var_prefix}_${_k}" "${_local_${_k}}" PARENT_SCOPE)
        endforeach()
    endif()
endfunction()

set(_undefined "")
set(_defined "")
set(_uncached_objects "")

if(CACHE_DIR)
    file(MAKE_DIRECTORY "${CACHE_DIR}")
endif()

foreach(_obj IN LISTS _objects)
    if(NOT EXISTS "${_obj}")
        continue()
    endif()

    if(CACHE_DIR)
        string(SHA256 _okey "${_obj}")
        file(SIZE "${_obj}" _osz)
        file(TIMESTAMP "${_obj}" _ots "%Y%m%d%H%M%S" UTC)
        set(_want_meta "${_osz};${_ots}")
        set(_cfile "${CACHE_DIR}/${_okey}.cmake")

        set(_cached_ok FALSE)
        if(EXISTS "${_cfile}")
            unset(_abi_meta)
            unset(_abi_undef)
            unset(_abi_def)
            include("${_cfile}")
            if(DEFINED _abi_meta AND _abi_meta STREQUAL _want_meta)
                set(_cached_ok TRUE)
                if(_abi_undef)
                    list(APPEND _undefined ${_abi_undef})
                endif()
                if(_abi_def)
                    list(APPEND _defined ${_abi_def})
                endif()
            endif()
        endif()

        if(NOT _cached_ok)
            list(APPEND _uncached_objects "${_obj}")
            set("_meta_${_okey}" "${_want_meta}")
        endif()
    else()
        list(APPEND _uncached_objects "${_obj}")
    endif()
endforeach()

if(_uncached_objects)
    set(REFII_ABI_SKIPPED FALSE)
    _refii_nm_batch(--undefined-only "${_undef_line_pattern}" "_new_undef" ${_uncached_objects})
    if(REFII_ABI_SKIPPED)
        return()
    endif()
    _refii_nm_batch(--defined-only "${_def_line_pattern}" "_new_def" ${_uncached_objects})
    if(REFII_ABI_SKIPPED)
        return()
    endif()

    foreach(_obj IN LISTS _uncached_objects)
        string(SHA256 _okey "${_obj}")
        set(_u "${_new_undef_${_okey}}")
        set(_d "${_new_def_${_okey}}")
        if(_u)
            list(APPEND _undefined ${_u})
        endif()
        if(_d)
            list(APPEND _defined ${_d})
        endif()

        if(CACHE_DIR)
            set(_cfile "${CACHE_DIR}/${_okey}.cmake")
            set(_u_escaped "")
            foreach(_item IN LISTS _u)
                list(APPEND _u_escaped "\"${_item}\"")
            endforeach()
            list(JOIN _u_escaped " " _u_body)

            set(_d_escaped "")
            foreach(_item IN LISTS _d)
                list(APPEND _d_escaped "\"${_item}\"")
            endforeach()
            list(JOIN _d_escaped " " _d_body)

            file(WRITE "${_cfile}"
                "set(_abi_meta \"${_meta_${_okey}}\")\n"
                "set(_abi_undef ${_u_body})\n"
                "set(_abi_def ${_d_body})\n"
            )
        endif()
    endforeach()
endif()

if(NOT _undefined)
    return()
endif()

list(REMOVE_DUPLICATES _undefined)
if(_defined)
    list(REMOVE_DUPLICATES _defined)
    list(REMOVE_ITEM _undefined ${_defined})
endif()
if(NOT _undefined)
    return()
endif()

list(SORT _undefined)
set(_report "")
foreach(_symbol IN LISTS _undefined)
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
