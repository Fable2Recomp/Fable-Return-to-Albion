#!/usr/bin/env bash
# Compile every .gdshader in the Godot project and fail if any of them errors.
#
# Godot reports shader errors by PRINTING them — the compile call returns nothing — so the exit
# status has to come from reading the output. A shader that fails to compile is not a loud
# failure at runtime: every mesh using it silently falls back to the default material and the
# world renders untextured, with a green build behind it.
#
# Needs a real rendering driver. Under `--headless` Godot uses the dummy renderer, which parses
# nothing at all and reports success on a shader that cannot compile.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="${here}/Godot"
godot="${REFII_GODOT_EXECUTABLE:-}"

if [[ -z "${godot}" ]]; then
    for candidate in \
        "${HOME}"/Downloads/Godot_v*_mono_linux_x86_64/Godot_v*_mono_linux.x86_64 \
        "$(command -v godot 2>/dev/null || true)"; do
        if [[ -x "${candidate}" ]]; then godot="${candidate}"; break; fi
    done
fi

if [[ ! -x "${godot}" ]]; then
    echo "check_godot_shaders: no Godot binary; set REFII_GODOT_EXECUTABLE" >&2
    exit 2
fi

output="$("${godot}" --path "${project}" --script res://tools/check_godot_shaders.gd 2>&1)"
status=$?

# Attribute each error to the file the script announced immediately before it.
echo "${output}" | awk '
    /^shader-check: compiling / { current = $3 }
    /SHADER ERROR/             { print "FAIL " current; print "      " $0 }
'

if echo "${output}" | grep -q "SHADER ERROR"; then
    echo "check_godot_shaders: shader compilation failed" >&2
    exit 1
fi

if [[ ${status} -ne 0 ]]; then
    echo "${output}" >&2
    echo "check_godot_shaders: checker exited ${status}" >&2
    exit "${status}"
fi

echo "${output}" | grep -E "^shader-check: parsed"
