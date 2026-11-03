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
    # Each hero's dog as the game binds it (sub_82399CB8).
    ${CMAKE_CURRENT_LIST_DIR}/src/companion_feed.cpp
    # The HUD the guest states: its element registries (texts, meters) and the screen fade.
    ${CMAKE_CURRENT_LIST_DIR}/src/hud_feed.cpp
    # The world icons, emotion choices and interaction mode the guest's HUD holds, read live.
    ${CMAKE_CURRENT_LIST_DIR}/src/icon_feed.cpp
    # The aim reticules' records as their draw reads them (Fable3/src/hooks/src/reticle_hooks.cpp states the objects).
    ${CMAKE_CURRENT_LIST_DIR}/src/reticle_feed.cpp
    # The options the guest runs with (applied gamma and audio), its languages, and the Options
    # screen's object while it shows.
    ${CMAKE_CURRENT_LIST_DIR}/src/options_feed.cpp
    # The classic renderer's sources: frame constants, theme, lights, shadow spheres, mists,
    # weather, particles and trails.
    ${CMAKE_CURRENT_LIST_DIR}/src/classic_feed.cpp
    # The lighting the guest states: its environment theme stack, layer by layer at its weights.
    ${CMAKE_CURRENT_LIST_DIR}/src/lighting_feed.cpp
    # The clips each rig plays: the animation manager's records and their render trees.
    ${CMAKE_CURRENT_LIST_DIR}/src/anim_feed.cpp
    # The world the guest's entry points state (Fable3/src/hooks/src/world_hooks.cpp): the level's
    # standing, the live effects and their placement, the decals.
    ${CMAKE_CURRENT_LIST_DIR}/src/world_feed.cpp
    # Each character's appearance as its hooks state it (Fable3/src/hooks/src/appearance_hooks.cpp):
    # the parts' morph targets and the textures replaced on them.
    ${CMAKE_CURRENT_LIST_DIR}/src/appearance_feed.cpp
    # Whether the guest poses each rig this tick (sub_82205E00), so the shell holds what it holds.
    ${CMAKE_CURRENT_LIST_DIR}/src/pose_gate_feed.cpp
    # Each entity's cloth pieces as the guest's draw publishes them (Fable3/src/hooks/src/
    # cloth_hooks.cpp), joined to the entity by its render handles.
    ${CMAKE_CURRENT_LIST_DIR}/src/cloth_feed.cpp
    # The map table the guest presents (Fable3/src/hooks/src/map_table_hooks.cpp).
    ${CMAKE_CURRENT_LIST_DIR}/src/map_table_feed.cpp
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
