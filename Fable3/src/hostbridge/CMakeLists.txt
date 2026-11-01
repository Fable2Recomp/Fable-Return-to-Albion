# Fable 3's host bridge — the C ABI Godot P/Invokes to host Fable 3's guest.
#
# The shared core (refii_hostcore, src/hostcore) linked whole, plus Fable 3's feeds: the world
# (level_feed.cpp); no part of its own in the core's lifecycle yet. The shell's title
# feeds it does not state are answered as nothing in unstated_feeds.cpp. Target refiii_hostbridge
# (refiii is Fable 3's key, as refii is Fable 2's), file librefii_hostbridge in its own output
# directory so it does not collide with Fable 2's, staged to bin/Fable3/.

add_library(refiii_hostbridge SHARED
    ${CMAKE_CURRENT_LIST_DIR}/src/host_bridge.cpp
    # The shell's title feeds Fable 3 does not state yet, each answered as nothing.
    ${CMAKE_CURRENT_LIST_DIR}/src/unstated_feeds.cpp
    # The world the guest states: its level, level change, scenario, readiness, camera and the
    # build hold.
    ${CMAKE_CURRENT_LIST_DIR}/src/level_feed.cpp
    # The entities the guest holds: the walk, transforms, models, poses and heroes.
    ${CMAKE_CURRENT_LIST_DIR}/src/entity_feed.cpp
    # The HUD the guest states: its element registries (texts, meters) and the screen fade.
    ${CMAKE_CURRENT_LIST_DIR}/src/hud_feed.cpp
    # The classic renderer's sources: frame constants, theme, lights, shadow spheres, mists,
    # weather, particles and trails.
    ${CMAKE_CURRENT_LIST_DIR}/src/classic_feed.cpp
)

# The whole core, --no-undefined and OUTPUT_NAME refii_hostbridge (src/hostcore/CMakeLists.txt).
refii_link_hostcore(refiii_hostbridge)

set_target_properties(refiii_hostbridge PROPERTIES
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}/Fable3"
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}/Fable3"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}/Fable3"
)

# Restaged after every link: the shared natives to bin/, Fable 3's instance
# (REFII_TITLE_INSTANCE_TARGETS_Fable3) to bin/Fable3/.
refii_stage_title_bridge(refiii_hostbridge Fable3)
