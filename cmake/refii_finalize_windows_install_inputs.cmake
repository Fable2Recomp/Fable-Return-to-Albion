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
    # Each title's ReXGlue instance, in its title folder.
    "${STAGING_BIN}/Fable2/refii_hostbridge.dll"
    "${STAGING_BIN}/Fable2/refii_vfs.dll"
    "${STAGING_BIN}/Fable3/refii_hostbridge.dll"
    # The shared natives every instance links.
    "${STAGING_BIN}/refii_core.dll"
    "${STAGING_BIN}/refii_runtime.dll"
    "${STAGING_BIN}/refii_binkdecode.dll"
    "${STAGING_BIN}/refii_xmadecode.dll"
    "${STAGING_BIN}/refii_recompiler.dll"
    # The shader pipeline the launcher loads, with the compiler it links and the tool it runs
    # (src/shader/CMakeLists.txt names them; cmake/refii_stage_godot.cmake stages them).
    "${STAGING_BIN}/refii_shader_sbk.dll"
    "${STAGING_BIN}/refii_shader_xenos_translator.dll"
    "${STAGING_BIN}/XenosRecomp.dll"
    "${STAGING_BIN}/dxcompiler.dll"
    "${STAGING_BIN}/dxil.dll"
    "${STAGING_BIN}/dxc/dxc.exe"
    "${STAGING_BIN}/dxc/shader_common.h"
    "${_recompile}/include_dirs.cmake"
    "${_recompile}/Fable2/hook_sources.cmake"
    "${_recompile}/Fable3/hook_sources.cmake"
    "${_recompile}/payload_core_sources.cmake"
    "${_recompile}/versions/Fable2TU1/refii_manifest.toml"
    "${_recompile}/versions/Fable2TU1/refii_config.toml"
    "${_recompile}/versions/Fable2/refii_manifest.toml"
    "${_recompile}/versions/Fable2/refii_config.toml"
    "${_recompile}/versions/Fable3/refiii_manifest.toml"
    "${_recompile}/versions/Fable3/refiii_config.toml"
    "${_recompile}/versions/Fable3TU3/refiii_manifest.toml"
    "${_recompile}/versions/Fable3TU3/refiii_config.toml"
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

# The install-time link reads these from where the host loads them: the shared ones from bin/,
# a title's own from bin/<Folder>/.
foreach(_library rexruntime refii_core refii_runtime Fable2/refii_vfs)
    get_filename_component(_directory "${STAGING_BIN}/${_library}" DIRECTORY)
    get_filename_component(_name "${_library}" NAME)
    file(GLOB _matches LIST_DIRECTORIES FALSE
        "${_directory}/${_name}.dll"
        "${_directory}/${_name}d.dll"
        "${_directory}/${_name}rd.dll"
        "${_directory}/lib${_name}.dll"
        "${_directory}/lib${_name}d.dll"
        "${_directory}/lib${_name}rd.dll")
    if(NOT _matches)
        message(FATAL_ERROR
            "Windows install link library is missing from ${_directory}: ${_name}")
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
