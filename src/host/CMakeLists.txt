# THE ReXGlue APP: what every title's host bridge runs the guest in — the app, its Godot-fed
# drivers, and the build id the crash record names. Shared: it links only shared libraries, and
# the state its drivers keep is refii_runtime's.

set(REFII_HOST_ROOT ${CMAKE_CURRENT_LIST_DIR})

add_library(refii_host STATIC
    ${REFII_HOST_ROOT}/refii_host.cpp
    # Godot-fed controller input. Lives here rather than in the host bridge because
    # refii_app.h installs it through RuntimeConfig::input_factory, and the bridge links
    # refii_host rather than the other way round.
    ${REFII_HOST_ROOT}/host_input.cpp
    # Godot-fed audio output, installed the same way through RuntimeConfig::audio_factory.
    # Its queue and counters live in refii_runtime, which the host bridge also links — the
    # bridge and this driver must see one ring, not one each.
    ${REFII_HOST_ROOT}/host_audio.cpp
)
set_target_properties(refii_host PROPERTIES
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    # Linked into refii_hostbridge, which is SHARED so Godot can P/Invoke it. Without
    # PIC the link fails on an R_X86_64_PC32 relocation a shared object cannot carry.
    POSITION_INDEPENDENT_CODE ON
)
target_include_directories(refii_host
    PUBLIC
        ${CMAKE_SOURCE_DIR}
        ${CMAKE_SOURCE_DIR}/src
        ${REFII_HOST_ROOT}
        ${CMAKE_SOURCE_DIR}/generated
)
if(EXISTS "${REFII_REXGLUE_SHADOW_DIR}/include")
    target_include_directories(refii_host PUBLIC "${REFII_REXGLUE_SHADOW_DIR}/include")
endif()
target_include_directories(refii_host
    PRIVATE
        "${CMAKE_SOURCE_DIR}/src/achievement/include"
)
# THE BUILD THE CRASH RECORD CAME FROM. A record without one has twice been read as the current
# release and triaged as a live fault when it was a beta behind (crash records are append-only
# and survive an in-place upgrade). Stamped at build time, not configure time, so the header
# names the commit the binary was actually built from; rewritten only when it changes.
set(REFII_BUILD_ID_DIR "${CMAKE_BINARY_DIR}/refii_build_id")
add_custom_target(refii_build_id
    COMMAND "${CMAKE_COMMAND}"
        "-DREFII_SOURCE_DIR=${CMAKE_SOURCE_DIR}"
        "-DREFII_OUTPUT=${REFII_BUILD_ID_DIR}/refii_build_id.h"
        -P "${CMAKE_SOURCE_DIR}/cmake/refii_build_id.cmake"
    BYPRODUCTS "${REFII_BUILD_ID_DIR}/refii_build_id.h"
    COMMENT "Stamping the build id for the crash record"
    VERBATIM
)
add_dependencies(refii_host refii_build_id)
target_include_directories(refii_host PRIVATE "${REFII_BUILD_ID_DIR}")
target_compile_definitions(refii_host
    PUBLIC
        REFII_SDL3_WINDOW_ENABLED=$<BOOL:${REFII_SDL3_WINDOW_ENABLED}>
)
# refii_generated is deliberately absent: the payload is built on the player's machine,
# so linking it here would make a release unbuildable on any machine without a game
# image. refii_payload loads it by name instead.
#
# refii_runtime PUBLIC: host_audio.cpp keeps its ring there so the host bridge reads the same
# one, and its include directory carries core/audio_state.h for every TU that reaches
# host_audio.h.
target_link_libraries(refii_host
    PUBLIC
        refii_core
        refii_payload
        refii_runtime
)

# ReXGlue v0.10's rex/ui/imgui_drawer.h includes rex/ui/style.h, which includes imgui.h — so
# every TU that reaches rex_app.h needs imgui's headers. The include path only: imgui is an
# OBJECT library already compiled into rexui, and linking it here would duplicate its objects.
target_include_directories(refii_host PUBLIC
    $<TARGET_PROPERTY:imgui,INTERFACE_INCLUDE_DIRECTORIES>)
