cmake_minimum_required(VERSION 3.24)

foreach(_required STAGING_BIN TARGET_ARCH TOOL_CACHE ZIG_SCRIPT)
    if(NOT DEFINED ${_required} OR "${${_required}}" STREQUAL "")
        message(FATAL_ERROR "${_required} must be set")
    endif()
endforeach()

if(NOT EXISTS "${ZIG_SCRIPT}")
    message(FATAL_ERROR "Zig staging script does not exist: ${ZIG_SCRIPT}")
endif()

set(_recompile "${STAGING_BIN}/recompile")
set(_required_inputs
    "${STAGING_BIN}/refii_hostbridge.dll"
    "${STAGING_BIN}/refii_core.dll"
    "${STAGING_BIN}/refii_vfs.dll"
    "${STAGING_BIN}/refii_binkdecode.dll"
    "${STAGING_BIN}/refii_xmadecode.dll"
    "${_recompile}/bin/rexglue.exe"
    "${_recompile}/include_dirs.cmake"
    "${_recompile}/hook_sources.cmake"
    "${_recompile}/refii_manifest.toml"
    "${_recompile}/refii_config.toml"
    "${_recompile}/src/payload_export.cpp"
)
foreach(_input IN LISTS _required_inputs)
    if(NOT EXISTS "${_input}")
        message(FATAL_ERROR "Windows install input was not staged: ${_input}")
    endif()
endforeach()

file(GLOB _runtime_dlls LIST_DIRECTORIES FALSE
    "${STAGING_BIN}/rexruntime*.dll"
    "${STAGING_BIN}/librexruntime*.dll")
if(NOT _runtime_dlls)
    message(FATAL_ERROR "The staged Windows RexGlue runtime DLL is missing from ${STAGING_BIN}")
endif()

foreach(_library rexruntime refii_core refii_vfs)
    file(GLOB _matches LIST_DIRECTORIES FALSE
        "${_recompile}/lib/${_library}.dll"
        "${_recompile}/lib/${_library}d.dll"
        "${_recompile}/lib/${_library}rd.dll"
        "${_recompile}/lib/lib${_library}.dll"
        "${_recompile}/lib/lib${_library}d.dll"
        "${_recompile}/lib/lib${_library}rd.dll"
        "${_recompile}/lib/${_library}.lib"
        "${_recompile}/lib/${_library}d.lib"
        "${_recompile}/lib/${_library}rd.lib")
    if(NOT _matches)
        message(FATAL_ERROR
            "Windows install link library is missing from ${_recompile}/lib: ${_library}")
    endif()
endforeach()

include("${ZIG_SCRIPT}")
_refii_zig_spec("windows" "${TARGET_ARCH}" _zig_file _zig_sha)
set(_zig_stamp "${_recompile}/zig/.refii-zig-install-stamp")
set(_zig_want "${REFII_ZIG_VERSION} windows ${TARGET_ARCH} ${_zig_sha}")
set(_zig_ready FALSE)
if(EXISTS "${_zig_stamp}" AND
   EXISTS "${_recompile}/zig/zig.exe" AND
   IS_DIRECTORY "${_recompile}/zig/lib")
    file(READ "${_zig_stamp}" _zig_have)
    string(STRIP "${_zig_have}" _zig_have)
    if(_zig_have STREQUAL _zig_want)
        set(_zig_ready TRUE)
    endif()
endif()

if(_zig_ready)
    message(STATUS
        "Bundled Zig ${REFII_ZIG_VERSION} for windows-${TARGET_ARCH} is already staged")
else()
    refii_fetch_zig("windows" "${TARGET_ARCH}"
                    "${_recompile}/zig" "${TOOL_CACHE}")
    file(WRITE "${_zig_stamp}" "${_zig_want}\n")
endif()

if(NOT EXISTS "${_recompile}/zig/zig.exe" OR
   NOT IS_DIRECTORY "${_recompile}/zig/lib")
    message(FATAL_ERROR "The bundled Windows Zig toolchain is incomplete")
endif()

message(STATUS "Windows install-time game-code inputs are ready at ${_recompile}")
