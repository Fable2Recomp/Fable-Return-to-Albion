# Compiles every .gdshader in the Godot project and fails if any of them errors.
#
# WHY CMAKE AND NOT A SHELL SCRIPT: this was tools/check_godot_shaders.sh, invoked as the
# command itself. That works wherever the shebang is honoured and nowhere else -- on Windows
# the build handed a .sh to cmd.exe and got back "unknown error", naming nothing. The .sh
# also looked for Godot only under ~/Downloads and on PATH, so even under a Windows bash it
# would have found nothing to run. Same reasoning as refii_run_godot_export.cmake next door.
#
# Godot reports shader errors by PRINTING them -- the compile call returns nothing -- so the
# exit status has to come from reading the output.
#
# NEEDS A REAL RENDERING DRIVER, which is why --headless is absent here and present in the
# export. Under --headless Godot uses the dummy renderer, which parses nothing at all and
# reports success on a shader that cannot compile.

cmake_minimum_required(VERSION 3.24)

foreach(_required PROJECT_DIR)
    if(NOT ${_required})
        message(FATAL_ERROR "refii_check_godot_shaders: ${_required} not set")
    endif()
endforeach()

# The same editor the packaging step uses, resolved the same way, so the shaders are checked
# by the Godot that will export them rather than by whatever happened to be on PATH.
if(NOT GODOT AND GODOT_SCRIPT AND EXISTS "${GODOT_SCRIPT}")
    include("${GODOT_SCRIPT}")
    refii_fetch_godot("${HOST_PLATFORM}" "${HOST_ARCH}"
                      "${TOOL_PLATFORM}" "${TOOL_ARCH}"
                      "${GODOT_DIR}" "${TOOL_CACHE}" GODOT)
endif()

if(NOT GODOT OR NOT EXISTS "${GODOT}")
    message(FATAL_ERROR
        "check_godot_shaders: no Godot binary at '${GODOT}'.\n"
        "Leave REFII_GODOT_EXECUTABLE unset to fetch the pinned one (see "
        "cmake/refii_fetch_godot.cmake), or set it to a Godot .NET build you have already.")
endif()

execute_process(
    COMMAND "${GODOT}" --path "${PROJECT_DIR}"
            --script res://tools/check_godot_shaders.gd
    RESULT_VARIABLE _check_result
    OUTPUT_VARIABLE _check_output
    ERROR_VARIABLE _check_output
)

# Split without letting the shader text's own semicolons -- every statement ends in one --
# become list separators and tear the lines apart.
string(REPLACE ";" "\\;" _check_lines "${_check_output}")
string(REPLACE "\r\n" "\n" _check_lines "${_check_lines}")
string(REPLACE "\n" ";" _check_lines "${_check_lines}")

# Attribute each error to the file the script announced immediately before it: compiling from
# a string leaves Godot no path to print, so it emits "(:4)" and this marker is the only thing
# that says which shader it meant.
set(_current "")
set(_failed FALSE)
foreach(_line IN LISTS _check_lines)
    if(_line MATCHES "^shader-check: compiling (.+)$")
        set(_current "${CMAKE_MATCH_1}")
    elseif(_line MATCHES "SHADER ERROR")
        message("FAIL ${_current}")
        message("      ${_line}")
        set(_failed TRUE)
    endif()
endforeach()

if(_failed)
    message(FATAL_ERROR "check_godot_shaders: shader compilation failed")
endif()

if(NOT _check_result EQUAL 0)
    message("${_check_output}")
    message(FATAL_ERROR "check_godot_shaders: checker exited ${_check_result}")
endif()

foreach(_line IN LISTS _check_lines)
    if(_line MATCHES "^shader-check: parsed")
        message("${_line}")
    endif()
endforeach()
