# Fable 2's host bridge — the C ABI Godot P/Invokes to host Fable 2's guest.
#
# SHARED and C-linkage on purpose: Godot reaches this through P/Invoke, the same
# arrangement as refii_recompiler and refii_shader_sbk. It is the shared core
# (refii_hostcore, src/hostcore: lifecycle, debug channel, audio, input, mods, RexNet)
# linked whole, plus Fable 2's feeds and its part of the core's lifecycle in host_bridge.cpp.

set(REFII_HOSTBRIDGE_ROOT ${CMAKE_CURRENT_LIST_DIR})

add_library(refii_hostbridge SHARED
    ${REFII_HOSTBRIDGE_ROOT}/src/host_bridge.cpp
)

# The whole core, --no-undefined and OUTPUT_NAME refii_hostbridge (src/hostcore/CMakeLists.txt).
refii_link_hostcore(refii_hostbridge)

# Fable 2's own shared state: its movies, effects, appearances, menus and sampled pages.
target_link_libraries(refii_hostbridge
    PRIVATE
        refii_vfs
)

set_target_properties(refii_hostbridge PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
)

# Restaged into Godot/build/<arch>/bin after every link: the shared natives to bin/, Fable 2's
# instance (REFII_TITLE_INSTANCE_TARGETS_Fable2) to bin/Fable2/. The refii_stage_godot target
# (cmake/refii_stage_godot.cmake) stages every title's on every build.
refii_stage_title_bridge(refii_hostbridge Fable2)
