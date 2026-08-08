# Emits build-info.json beside the built module.
#
# A separate script rather than configure_file, because these values are only known at
# BUILD time (the compiler actually used, the timestamp) and a configure-time template
# would record what was requested rather than what happened.

if(NOT DEFINED OUT)
    message(FATAL_ERROR "write-build-info.cmake: OUT not set")
endif()

# Escape anything that would break the JSON. Paths and version strings routinely carry
# backslashes on Windows, and one of those turns the file into something the installer
# cannot parse — at which point it rebuilds every launch and nobody knows why.
function(json_escape value out_var)
    string(REPLACE "\\" "\\\\" value "${value}")
    string(REPLACE "\"" "\\\"" value "${value}")
    set(${out_var} "${value}" PARENT_SCOPE)
endfunction()

json_escape("${GAME_HASH}" _game_hash)
json_escape("${CODEGEN_VERSION}" _codegen_version)
json_escape("${RUNTIME_VERSION}" _runtime_version)
json_escape("${COMPILER}" _compiler)
json_escape("${CMAKE_VER}" _cmake)
json_escape("${PLATFORM}" _platform)
json_escape("${BUILT_UTC}" _built)

file(WRITE "${OUT}"
"{
    \"game_hash\": \"${_game_hash}\",
    \"codegen_version\": \"${_codegen_version}\",
    \"runtime_version\": \"${_runtime_version}\",
    \"compiler\": \"${_compiler}\",
    \"cmake\": \"${_cmake}\",
    \"platform\": \"${_platform}\",
    \"built_utc\": \"${_built}\"
}
")

message(STATUS "build-info.json written to ${OUT}")
